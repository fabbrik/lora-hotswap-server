# Build:  docker build -t lora-hotswap-server .
# NOTE:   use linux/amd64 (the default on x86 hosts; `--platform linux/amd64` on Apple Silicon).
#         TorchSharp, used for merge verification, ships no linux-arm64 native library, so an
#         arm64 container fails at admission with "The type initializer for TorchSharp.torch threw".
# Run:    see "Running in a container" in the README. Models and adapters are NOT baked into
#         the image: mount them, so the image carries no third-party weights and stays small.
# Build on the builder's own architecture and cross-publish for the target: .NET under
# emulation is unreliable, and publishing with a RID is all the target needs.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src
COPY global.json Directory.Build.props LoraHotswap.slnx ./
COPY src ./src
RUN case "$TARGETARCH" in amd64) RID=linux-x64 ;; arm64) RID=linux-arm64 ;; *) echo "unsupported $TARGETARCH" >&2; exit 1 ;; esac \
 && dotnet publish src/LoraHotswap.Api/LoraHotswap.Api.csproj -c Release -r "$RID" --self-contained false -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
# Non-root; /data is where the models and adapters volume is mounted.
RUN useradd --system --uid 10001 hotswap && mkdir /data && chown hotswap /data
USER hotswap
ENV ASPNETCORE_URLS=http://+:8080 \
    Inference__Runtime=LLamaSharp \
    Inference__ModelPath=/data/models/Qwen3-0.6B-GGUF/Qwen3-0.6B-Q8_0.gguf \
    Admission__AdaptersRoot=/data/adapters
EXPOSE 8080
# Probe GET /healthz from your orchestrator (the runtime image has no curl to do it in-container).
ENTRYPOINT ["dotnet", "LoraHotswap.Api.dll"]
