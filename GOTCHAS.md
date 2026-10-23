# GOTCHAS

Raw log, appended while bleeding. `[HIT]` bit for real, `[TRAP]` was anticipated
and dodged but would have bitten someone.

---

### [HIT] `dotnet new xunit3` does not exist — the template ships xunit **2.9.3**

The test project was specified to use xunit.v3 3.2.2, assuming a matching template. SDK 10.0.302
offers only `xunit`, and it generates:

```xml
<PackageReference Include="xunit" Version="2.9.3" />
```

That is the v2 line. A new project on .NET 10 that follows the template silently
lands two major versions behind.

Migration is manual: swap `xunit` for `xunit.v3`, keep
`xunit.runner.visualstudio` (3.1.4 handles both v2 and v3), and add
`<OutputType>Exe</OutputType>` — xunit.v3 test projects are executables, unlike v2.

### [HIT] .NET 10 `dotnet new sln` emits `.slnx`, not `.sln`

The new XML solution format is the default. Any tooling, script, or test that
walks up looking for `*.sln` finds nothing and fails with something unhelpful:

```
Assert.NotNull() Failure: Value is null
```

The root-finding walk now accepts both.

### [HIT] MSBuild catches *some* layering violations before any test runs — but only by accident

Adding `Core → Infrastructure` produced:

```
error MSB4006: There is a circular dependency in the target dependency graph
```

not an architecture-test failure. `Infrastructure → Core` already existed, so the
forbidden edge closed a cycle and MSBuild rejected it during restore.

That is a stronger guard than the test — but it is **incidental**. It only fires
because the reverse edge happens to exist. A forbidden reference with no reverse
edge compiles cleanly, and then the architecture test is the only thing standing
there.

Consequence for verification: sabotaging `Core → Infrastructure` does **not**
exercise the test. To prove the test works, the reverse edge must be removed
first. Done, confirmed failing with the right message, then both restored:

```
LoraHotswap.Core must not reference LoraHotswap.Infrastructure.
Current references: LoraHotswap.Core.Ports, LoraHotswap.Infrastructure.
```

### [HIT] `dotnet remove reference` can fail silently

`dotnet remove <proj> reference <target>` returned without error and left the
`ProjectReference` in place. With output redirected to `/dev/null` this looked
like success, and the subsequent sabotage run kept reporting the circular-
dependency error instead of the expected test failure — costing a full debug
cycle chasing the wrong thing.

Verify the `.csproj` after mutating references. Do not trust the exit code.

### [TRAP] An assembly-reference check would have passed for the wrong reason

The obvious implementation of the dependency test is:

```csharp
typeof(X).Assembly.GetReferencedAssemblies()
```

The compiler elides assembly references that no code uses. With empty projects —
the state early on, before any real code existed — `Core` emits no reference to
anything, so the assertion passes while proving nothing.

The test reads the `.csproj` `<ProjectReference>` graph instead. Cannot be
optimised away, and true from the first commit.

### [NOTE] No architecture-testing library was added, deliberately

- **NetArchTest** — last commit **2024-07-29** (checked 2026-08-10). Stale.
- **ArchUnitNET** (`TngTech.ArchUnitNET` 0.13.3) — last push **2026-08-09**.
  Actively maintained and the right choice *if* richer rules are ever needed.

One rule does not justify a dependency. ~40 lines of XML parsing covers it.

---

### [HIT] `Directory.Build.props` is a decoy if the csproj templates re-declare the same properties

`dotnet new classlib` and `dotnet new web` both emit `<Nullable>` and
`<ImplicitUsings>` into the project file. Project-level properties win over
`Directory.Build.props`, so setting them centrally does **nothing** — the file
looks authoritative and is inert for those two settings.

Only `TreatWarningsAsErrors` and `LangVersion` — the properties the template does
not emit — actually took effect. Found by review, not by anything failing.

Fix: strip `TargetFramework`, `Nullable`, and `ImplicitUsings` from every
generated csproj and let the props file own them.

### [HIT] `TreatWarningsAsErrors` turns a published CVE into a broken build

NuGet audit reports NU1901–NU1904 as warnings. Promoted to errors, a CVE
disclosed against any transitive dependency breaks the build with no commit and
no code change.

```xml
<WarningsNotAsErrors>NU1901;NU1902;NU1903;NU1904</WarningsNotAsErrors>
```

### [TRAP] `EnumerateFiles("Name.sln?")` also matches `.slnf`

The single-character wildcard matches solution *filter* files as well as `.slnx`.
A stray `.slnf` in an ancestor directory stops the root walk in the wrong place,
and the failure surfaces as a confusing missing-project assertion. Both filenames
are now checked explicitly.

### [TRAP] Parsing csproj XML without namespace handling can silently find zero references

`Descendants("ProjectReference")` uses a no-namespace name. A csproj carrying the
legacy `xmlns="http://schemas.microsoft.com/developer/msbuild/2003"` root
attribute returns **no matches**, so every "must not reference" rule passes
while the forbidden reference sits in the file.

Match on `Name.LocalName`, case-insensitively — MSBuild itself is case-insensitive
— and split `Include` on `;`, which MSBuild accepts.

---

### [NOTE] `ggml-org/Qwen3-0.6B-GGUF` ships four quantisations — none pinned by spec

The repo publishes `BF16`, `f16` (~1.5 GB each), `Q8_0` (~805 MB, near-lossless)
and `Q4_0` (~429 MB), with no single one obviously "the" pinned quant. Resolved
as **Q8_0**: CPU-only Apple Silicon serving doesn't benefit from BF16/f16's
extra precision, and Q8_0 stays close enough to full precision that it
shouldn't confound later merge-verification tolerance checks the way a 4-bit
quant might. `sources.toml` fetches only that one file — never a
`snapshot_download` of the whole repo. Revisit if those tolerance checks ever
disagree with this choice.

### [NOTE] Unauthenticated Hub requests print a rate-limit warning, not an error

Every `hf_hub_download`/`model_info` call without `HF_TOKEN` set prints
`Warning: You are sending unauthenticated requests to the HF Hub...` to
stderr. It is noise, not failure — all five artifacts acquired here (the base
model plus four adapters) are public and downloaded fine anonymously — but a
script that treats any stderr output as a failure signal would misreport a
clean run.

### [NOTE] `convert_lora_to_gguf.py` needs only the base model's *config*, not its weights — and it fetches it itself

The open question was whether converting these Qwen3 adapters would drag in the
full HF base-model safetensors repo — a much larger download than intended.
It does not. When neither `--base` nor `--base-model-id` is passed, the converter
reads each adapter's own `base_model_name_or_path` and loads *only* that base's
`config.json` via `transformers.AutoConfig.from_pretrained` (cached under
`~/.cache/huggingface`). That's a small config file, not the model weights — no
large artifact fetched. So the tool deliberately passes neither flag: letting
the converter use each adapter's declared base string also preserves the
`unsloth/Qwen3-0.6B` vs `Qwen/Qwen3-0.6B` divergence between adapters without
normalising it away.

### [NOTE] Qwen3 LoRA adapters convert cleanly at the pinned llama.cpp revision

All four acquired adapters (`Qwen3-0.6B-nlp-to-sql`, `Qwen3-0.6B-alpaca-lora`,
`Qwen3-0.6B-TLDR-Lora`, `Qwen3-0.6B-clinical-lora`; an earlier set was replaced by
Apache-2.0 ones, and converted the same way) convert with no error at converter
revision `749f688f…` — the architecture (`Qwen3ForCausalLM`) is supported. This
was the main unverified external dependency in the conversion step; it is now
confirmed to work.

### [NOTE] The vendored converter imports `conversion` and `gguf` as siblings, so vendor more than one file

"Obtain the converter" is not "download one script". `convert_lora_to_gguf.py`
does `from conversion import ...` (the per-architecture model definitions, a
whole package) and `import gguf` (from the bundled `gguf-py`). `convert.py`
vendors all three paths (`convert_lora_to_gguf.py`, `conversion/`, `gguf-py/`) at
the pinned commit and runs the script with cwd set to the vendored root so both
imports resolve. Pinning just the one filename would break at import.

### [NOTE] Converter chatter goes to stderr on success — do not treat it as failure

Same trap as acquisition's Hub warning. The converter logs INFO lines and (when
unauthenticated) the Hub rate-limit warning to stderr even on a clean run.
`convert.py` keys off the subprocess *exit code*, and only surfaces stderr
verbatim when that code is non-zero. GGUF output is also not guaranteed
byte-identical across machines/library versions, which is exactly why the
`ggufSha256` and the manifests are computed per-local-conversion and never
committed — a committed hash would fail a fresh clone's warm-path check.

### [NOTE] `adapterId` is machine-stable; `ggufSha256` is not — don't conflate them

The manifest carries two content hashes with different stability. `adapterId`
(`sha256:` + the SHA-256 of the *source* `adapter_model.safetensors`) is
deterministic across machines — the source bytes are identical everywhere — so it
is a safe, path-independent identity to name an adapter by in logs, errors, and
responses. `ggufSha256`, by contrast, is the *derived* GGUF's hash, which is not
byte-stable across machines/library versions (previous NOTE); never treat it as
an identity. A pre-1.4 manifest predates `adapterId` and has only three fields;
the warm path reports such an adapter `modified` (missing `adapterId`) and leaves
it untouched — reconvert to regenerate the four-field manifest, never hand-edit
the field in.

### [NOTE] Vendoring fetches the converter by commit SHA, which the remote must allow

`convert.py` obtains the converter with `git fetch --depth 1 origin <sha>` then
`git checkout <sha>`. Fetching a bare commit SHA works only when the server
enables `uploadpack.allowReachableSHA1InWant` (or `allowAnySHA1InWant`). GitHub
(the pinned `ggml-org/llama.cpp` remote) currently allows it, so this is fine
today — but a self-hosted mirror or proxy that disables it would fail the fetch
with an opaque git error. There is no branch/tag fallback: the lock pins a SHA
by design (reproducibility), and `load_converter_lock` now rejects a non-40-hex
`revision` outright. If the remote ever stops allowing SHA fetches, the fix is to
mirror the converter, not to loosen the pin. The offline-on-rerun guarantee rests
on the vendored `.revision` marker short-circuit, not on the fetch itself.

### [HIT] The circular-dependency wall hides the fact that a layering test is untested

Second time this bit. Adding `Infrastructure → Api` fails at MSBuild restore with
a *circular dependency* error, because `Api → Infrastructure` already exists —
so the sabotage never reaches the test, and an empty grep looks like "no
findings" rather than "never ran".

Every inward layering violation in a well-formed hexagon is circular, so **no
forbidden edge can be sabotage-tested without first removing the legitimate
reverse edge**. Verified properly:

```
LoraHotswap.Infrastructure must not reference LoraHotswap.Api.
Current references: LoraHotswap.Api, LoraHotswap.Core, LoraHotswap.Core.Ports.
```

### [HIT] A vacuous test and a broken test are indistinguishable

`PortConventionTests` scanned an assembly with no types and asserted "no
offenders found". Green, and completely without evidence — a typo in the scanner
would have looked identical, and would have stayed hidden until the first real
port arrived.

The scanner is now proven against deliberately non-conforming fixtures declared
in the test assembly: a synchronous method, a method with no `CancellationToken`,
an `internal` interface, and a member inherited from a base interface. Each must
be detected; a conforming port must not be.

The original also used `GetExportedTypes()` (misses `internal` interfaces) and
`Type.GetMethods()` (misses base-interface members) — both invisible while the
assembly was empty.

### [HIT] The inert trap is keyed on `lora_B` only — an "all zeros" check misses it

Story 1.5 ports rung 1's finding into the server. An adapter is inert when
`ΔW = B·A = 0`, which happens iff every `lora_B` tensor is zero — but `lora_A` is
typically *nonzero* even on an inert adapter (that is exactly the shape
`ybelkada/opt-350m-lora` ships). A naive "is the safetensors file all zeros"
check would pass an inert adapter as trained. `AdapterValidator.Validate` keys on
`lora_B` magnitudes only, mirroring `scan_adapters.py`'s `z == n` over `lora_B`.

The rejection is a distinct, fatal `RejectionReason.Inert` — never a generic
failure or a warning — so later stories can add their own reasons without
colliding.

### [HIT] The real inert adapter is a gated ~700MB download — reproduce, don't fetch

`ybelkada/opt-350m-lora` is the canonical inert adapter, but pulling it from the
Hub is a gated multi-hundred-MB download and would make the suite network-bound.
`tests/LoraHotswap.Tests/fixtures/make_inert_fixture.py` reproduces its *defining*
structure (r=16, alpha=32, q_proj/v_proj on facebook/opt-350m, all `lora_B` zero,
`lora_A` seeded-nonzero) in a compact committed `.safetensors` (~513 KiB) using
only stdlib `struct`/`json` — no numpy, torch, or network. It is deterministic:
re-running rewrites byte-identical files. Keep the generator and its output in
sync; regenerate with `python3 make_inert_fixture.py` from the fixtures dir.

### [TRAP] The artifacts DTO cannot live in `Core` — `Core.Ports` references *nothing*

Story 1.6's driven port `IAdapterArtifactsReader` returns an artifacts DTO. The
natural instinct (and the story's Code Map note) is to put that DTO in `Core`. But
the dependency direction is `Core -> Core.Ports`: `Core.Ports` is the *innermost*
layer and `DependencyDirectionTests` asserts it references nothing. A port
returning a `Core` type would force `Core.Ports -> Core` and break that test.

Resolution: the DTO (`AdapterArtifacts`, `AdapterManifest`, `AdapterConfigSnapshot`)
lives in `Core.Ports`, carrying the config as a plain-value **snapshot** rather than
the `Core` `AdapterConfig` type. Admission (in `Core`) consumes it fine
(`Core -> Core.Ports`), and the host rebuilds a full `AdapterConfig` from the
snapshot for the registry. The reuse mandate still holds: the infrastructure reader
parses via Story 1.5's `AdapterConfig`, then projects it into the snapshot.

### [TRAP] Registering a singleton *after* `builder.Build()` is impossible

Admission must run **between** `builder.Build()` and `app.Run()` (the
fail-before-listen guarantee), yet its product — the `AdapterRegistry` — must be a
DI singleton. The service collection is frozen at `Build()`, so you cannot add the
registry once admission has produced it.

Resolution: register a singleton *factory* over a captured `AdapterRegistry?`
closure variable **before** `Build()`. Admission (after `Build()`) assigns the
variable; the factory throws if resolved before that. Later stories resolve it only
while serving — strictly after admission has set it, or after the process has
already exited non-zero. Nothing binds a listener in between.

### [HIT] Strict base matching is a *feature*, not a bug to normalize away

Some published adapters (one was used earlier in development) declare `unsloth/Qwen3-0.6B` for weights
compatible with `Qwen/Qwen3-0.6B`. Admission compares `base_model_name_or_path` to
the configured base by **ordinal string equality** and rejects the mismatch. A
normalization table (case-folding, vendor-prefix stripping) would paper over a
genuinely different provenance claim, so the rejection is asserted as
correct-by-design in `AdmissionTests`. Don't "fix" it.

### [TRAP] Admission fixtures need a real `.gguf` — but its *content* is irrelevant

Admission hashes the raw bytes of `adapter.gguf`, so the admission fixtures ship a
tiny non-GGUF stand-in blob (`b"GGUF" + <deterministic bytes>`). Only determinism
matters for the hash comparison, not GGUF validity — no llama.cpp, no multi-MB
container. `make_admission_fixtures.py` (stdlib `struct`/`json`/`hashlib` only)
regenerates all six directories byte-identically under
`tests/LoraHotswap.Tests/fixtures/admission/`, each well under 1 MB.

### [HIT] The OpenTelemetry `TracerProvider` does not exist until the host starts — after admission

`AddOpenTelemetry().WithTracing(...)` builds the `TracerProvider` in a hosted
service, which runs on `app.Run()`. Admission runs before that, between `Build()`
and `Run()`, so every `adapter.admission` span was started with no listener and
silently dropped. The unit tests missed it because they attach their own
`ActivityListener`.

Resolution: the composition root (`LoraHotswapHost.BuildAsync`, called by
`Program.cs`) resolves `TracerProvider` from `app.Services` right after `Build()`,
and force-flushes it (bounded to 5 s) after admission so a failing startup still
exports its spans before exiting. `HostCompositionTests` runs the real composition
and fails if the early resolution is removed.

### [HIT] ASP.NET Core's unsampled request activity drops every child span

With logging on, ASP.NET Core wraps each request in an activity. If nothing records
that activity, it is unsampled. The default parent-based sampler then drops every
child, so `adapter.verification` never reached the console exporter, even though a
raw `ActivityListener` still saw it. The endpoint tests hid this because their test
host cleared the logging providers, and with no enabled diagnostics there is no
parent activity at all.

Since Story 2.7 the dominant case is the five spans a served request emits, not
just `adapter.verification`.

Resolution: an `AlwaysOnSampler`, so every `LoraHotswap` span is recorded whatever
its parent. The first fix, `AddAspNetCoreInstrumentation()`, also worked, but it
exported an HTTP server span per request with no `adapter.id`, breaking Story 1.8's
"every span carries `adapter.id`". The regression test (`AdmissionConsoleExportTests`)
keeps default logging on and asserts against the real console exporter.

### [TRAP] An OTLP endpoint set in code is used verbatim — `http/protobuf` gets no `/v1/traces`

The exporter appends the signal path only to an endpoint that comes from
`OTEL_EXPORTER_OTLP_ENDPOINT`. `OpenTelemetry:OtlpEndpoint` is assigned in code, so
`http://collector:4318` with `http/protobuf` posted to the collector root and every
export was dropped. `AddLoraHotswapTracing` now appends `v1/traces` to a base
`http/protobuf` endpoint. It also sets the protocol only when `OtlpProtocol` is
configured, because always setting it silently overrode `OTEL_EXPORTER_OTLP_PROTOCOL`.

### [NOTE] Real merge references are a sample, and the base comes from `unsloth/Qwen3-0.6B`

A reference holding `w0` and `w_merged` for every targeted module of Qwen3-0.6B
would be gigabytes per adapter. `tools/reference/make_references.py` writes 6
modules spread across the layers (75–117 MB per adapter), and the verifier checks
exactly the modules the reference covers. Only the config of `Qwen/Qwen3-0.6B` was
in the Hugging Face cache; the weights were cached as `unsloth/Qwen3-0.6B`, the mirror
rung 1 used, so that is the tool's default `BASE_ID`. All four real adapters verify
with a reference error of exactly 0 (bit-identical to PEFT's fp32 merge).

### [HIT] PEFT writes `target_modules` in set order, which follows `PYTHONHASHSEED`

`make_merge_fixtures.py` produced byte-identical tensors on every run, but
`adapter_config.json` changed between runs: `["q_proj","v_proj"]` versus
`["v_proj","q_proj"]`. That changes the file's hash, which fails provenance checks
and breaks "re-running rewrites byte-identical files". The generator now sorts
`target_modules` before writing.

### [NOTE] The `LoraHotswap` telemetry vocabulary lives in Core, and the two `Telemetry.cs` files differ

Story 2.7 needed the binding strategies (Core) to emit spans, and Core may not
reference Api. So `Core/Telemetry.cs` owns `LoraHotswapTelemetry`: the single
`ActivitySource`, the tag names, the `unidentified` constant (moved off
`AdmissionStartup`) and the span helpers. `Api/Telemetry.cs` keeps only the
exporter wiring and was renamed `LoraHotswapTracing` so the two names cannot be
confused. `AddProblemDetails()` is still registered but no longer shapes serving or
verification problem bodies, which are written directly; a future
`CustomizeProblemDetails` would apply to framework problems only.

### [TRAP] `_us` field names cannot survive the default JSON policy

NFR4 requires `adapter_resolution_us` and friends on the wire, but minimal APIs
serialize with the camelCase policy, which turns `AdapterResolutionMicroseconds`
into `adapterResolutionUs` — no underscores, and the unit suffix silently mangled.
Every property on the timings DTO therefore carries an explicit
`[property: JsonPropertyName]`, and the endpoint tests assert the names on the raw
JSON rather than on the C# record, so a dropped attribute fails rather than
quietly renaming a documented field.

### [TRAP] Serving ports need `AdapterId`, but `Core.Ports` may reference nothing

`IInferenceRuntime`, `IContextBindingStrategy` and `IAdapterVerifier` take
`AdapterId`/`TenantId` value types (NFR8), which live in `Core`. Putting the ports
in `Core.Ports` would force `Core.Ports -> Core`, and moving the value types would
break the "interfaces only" rule. They are declared in `Core` instead, and
`PortConventionTests` now scans both assemblies. A guard test fails if the scan
stops finding them.


### [HIT] `Results.Problem` overwrites a `traceId` extension with the W3C traceparent

Story 2.7 requires every error body to carry the id of the trace that produced it.
Passing `extensions: { ["traceId"] = Activity.Current.TraceId }` to `Results.Problem`
looked right and came back as `00-<traceId>-<spanId>-01`: ASP.NET Core's default
ProblemDetails writer sets `traceId` to `Activity.Current.Id` unconditionally, after
the caller's extensions are copied in. Problems are now written with `Results.Json`
over a `ProblemDetails` whose `Extensions` carry the value, which emits the identical
shape (`type`, `title`, `status`, `detail`, plus `traceId`) with the trace id a reader
can actually look up. A test pins both that member list and that the value equals
the `serve.request` span's trace id, so neither the shape nor the value can silently
change the wire shape.

### [TRAP] A process-wide `ActivityListener` sees every test class's spans at once

`SpanCapture` listens to the whole `LoraHotswap` source, and serving spans carry
`adapter.id` just as verification spans do. Once the serving path emitted spans, a
verification test asserting `Assert.Single(spans.ForAdapterId(id))` could catch
another class's serving span for the same fixture adapter, in parallel. The filters
are now keyed on the operation name as well (`SpanCapture.Verifications`), and the
serving tracing tests give every tenant a unique name and filter on `tenant.id`.

### [HIT] A record property named `OperatingSystem` shadows the BCL type of the same name

`RunEnvironment` (the comparison tool's NFR3 block) has an `OperatingSystem`
member, and the same file calls `OperatingSystem.IsMacOS()` to decide whether it
can ask `sysctl` for the CPU name. Inside the record, the property wins and the
build fails with `CS0120: An object reference is required` — pointing at the
static call, not at the name collision that caused it. `System.OperatingSystem`
is now spelled out at the call site. Any record member named after a BCL static
class (`Environment`, `Path`, `Console`) has the same trap.

### [TRAP] `dotnet test` walks every project in the solution, tool projects included

Story 2.8's comparison is a tool, not a test — the spec forbids making it part of
the suite's normal run — but adding `tools/compare` to `LoraHotswap.slnx` puts it
in front of `dotnet test`, which looks for a test framework in whatever it is
handed. The project declares `IsTestProject=false` and
`IsTestingPlatformApplication=false` so the runner walks past it. The tool is
still covered: the test project references it and exercises its claims against
deliberately tiny workloads, never the published 200-request comparison.

### [NOTE] Giving both binding strategies the same context budget is a choice, and it is visible in the table

`PerTenantResident` holds exactly one context per tenant, by construction.
`PerRequestApply`'s pool size is configuration, so the comparison sets
`Serving:ContextPoolSize` to the tenant count — otherwise the arms differ in how
much hardware they were given as well as in how they bind, and the table compares
two things at once. The consequence is worth reading in the published figures:
pooled contexts serve any tenant, resident ones cannot, so above one request per
tenant in flight the resident arm queues and that shows up in
`context_acquisition_us`. That is the strategy's own property, not a handicap the
tool gave it, and the generated table says so beside the numbers.

### [HIT] LLamaSharp 0.27 **does** apply LoRA adapters to a live context — and that shrinks what AD-9 can measure

The Epic 3 risk was concentrated in one question: can a LoRA adapter be applied and
swapped on a context that already exists, or only bound when the model is loaded?
It can. The API is two calls:

```csharp
LoraAdapter adapter = weights.NativeHandle.LoadLoraFromFile(path);   // model-scoped
context.NativeHandle.SetLoraAdapters([(adapter, 1.0f)]);             // context-scoped
```

`SetLoraAdapters` replaces the context's whole adapter set — passing an empty span
clears it back to base — and the same context answers as adapter A, then as adapter B, then as
the base model, in that order, without reloading anything. `PerRequestApply` is
therefore honest: it really does apply the adapter on every lease.

The consequence for AD-9 is that the two strategies differ by much less than the
"orders of magnitude" the decision anticipated, because the split is not where it
looks. **Adapter weights are model-resident; only the applied set is context-resident.**
`LoadLoraFromFile` reads the container into the model once per process (~60 ms for a
20 MB adapter); `SetLoraAdapters` after that is a pointer swap — measured at
**0.2 µs median over 40 swaps**, below the resolution `adapter_application_us` reports
in. The LoRA arithmetic itself is not done at application time at all: llama.cpp
builds the delta into the compute graph, so the adapter's cost is paid inside
`generation_us`.

What the in-band timings then show, from a real run (`PerRequestApply`, Qwen3-0.6B
Q8_0, Apple M3 Pro, CPU-only):

| Request | `adapter_application_us` | `generation_us` |
| --- | --- | --- |
| first for an adapter *and* context | 18090 | 170519 |
| first for a fresh pooled context | 17390 | 184015 |
| warm context, warm adapter | 8120 | 181109 |
| steady state (10th request) | 36 | 224841 |

Two costs hide inside a cold `adapter_application_us`: reading the adapter container
into the model, and materialising the pooled context itself (its KV cache) on first
use, because the runtime is handed context *ids* by the strategy and cannot create a
context before one is named. In steady state the figure is ~40 µs against ~200 ms of
generation.

So AD-9's measured variable survives, but it is a **startup-cost and memory**
difference, not a steady-state per-request latency difference:
`PerTenantResident` pays container load and context creation at startup and holds one
context per tenant; `PerRequestApply` pays them on the first lease of each context and
then ~40 µs per request. `adapter_application_us` stays exactly what it measures rather
than being widened to make the gap look bigger.

Two caveats belong next to that conclusion, and neither is resolved:

**It is a prediction, not a measurement.** Story 2.8's table has never been run against
the real runtime — every published figure is Phase A's fake. `tools/compare` no longer
hard-registers that fake (it reads whichever runtime the host selected from
`Inference:*`), so the run is now *possible*; until someone does it, "the two arms
converge after warm-up" is an expectation derived from the table above, not a result.

**Part of the convergence is this implementation's own doing.** `Generate` calls
`MemoryClear(true)` before every generation, because a pooled context must carry nothing
from the tenant it last served — a requirement, and one the suite now pins. But it also
discards prompt-prefix reuse, which is exactly the advantage `PerTenantResident` would
otherwise have: its context is the *same* tenant's every time, so its KV cache could be
kept warm across requests, while a pooled context's genuinely cannot. As written, both
arms re-decode the whole prompt every request, so the comparison is between two
strategies that have both been denied that optimisation. Giving the resident arm prefix
reuse is a real design question, not a bug — and until it is answered, the measured gap
is a floor, not the strategy's true profile.

### [HIT] A prompt larger than the context does not fail — it aborts the process

Handing `llama_decode` more tokens than the batch holds does not return an error code
that LLamaSharp can turn into an exception. It trips a `GGML_ASSERT` and calls
`abort()`:

```
/Users/runner/work/LLamaSharp/LLamaSharp/src/llama-context.cpp:1599:
GGML_ASSERT(n_tokens_all <= cparams.n_batch) failed
WARNING: Using native backtrace. Set GGML_BACKTRACE_LLDB for more info.
0   libggml-base.dylib   ggml_print_backtrace + 276
1   libggml-base.dylib   ggml_abort + 156
2   libllama.dylib       llama_context::decode + 5464
```

The whole host dies — no typed `generation-failed`, no 500, no log line from the
server. There is no `try`/`catch` that helps: the process is gone.

`LlamaSharpInferenceRuntime` therefore checks the prompt's token count against the live
context **before any batch is decoded** — tokenizing and clearing the KV cache are native
calls too, but they are bounded and safe; `llama_decode` is neither — and decodes the
prompt in `n_batch`-sized chunks so the batch bound cannot be exceeded either. The
over-long prompt is then an ordinary exception, and it gets its **own** problem type:
`prompt-too-long` with a **413**, not `generation-failed` with a 500. It is the caller's
input, so a caller must be able to tell it apart from a broken runtime, and must not be
able to manufacture 500s by sending a long prompt. The opt-in test asserts both the
status and that no native detail reaches the body.

### [HIT] `Inference:ContextSize` is not the context size you get — llama.cpp pads `n_ctx` up to 256

Asking for a 32-token context and reading it back:

```
ContextSize=256 BatchSize=32
```

The first attempt at the generation-failure test sent a 212-token prompt at
`ContextSize=32`, expected a rejection, and got a 200 with real text: the prompt fit
the padded context. Any bound the runtime enforces has to be read off the live context
(`LLamaContext.ContextSize`), never off the configured number.

### [HIT] Constructing `ModelParams` already loads the native library, so `NativeLibraryConfig` must come first

`NativeLibraryConfig.All.WithLogCallback(...)` is how llama.cpp's load-time chatter is
kept off stderr — and it is a one-shot that has to run before the native library is
loaded. It does not survive being called "early in the runtime's `Load`":

```
InvalidOperationException: The library has already loaded, you can't change the
configurations. Please finish the configuration setting before any call to LLamaSharp
native APIs.
```

The call that had already loaded it was `new ModelParams(path)` — a managed options
object, two lines earlier. Until the callback is registered, ~200 lines of
`ggml_metal_device_init` / `llama_model_loader` / `repack` output land on stderr, in
the middle of the startup reason lines this host is judged by. `ConfigureNativeLogs`
is now the first statement of `Load`, before any LLamaSharp type is touched, and the
`InvalidOperationException` is still caught so a process that loaded the library
elsewhere degrades to noisy logs rather than a failed startup.

### [HIT] A corrupt adapter container loads and applies **successfully** — llama.cpp only logs it

Pointing `LoadLoraFromFile` at a file containing the text `definitely not a gguf`:

```
llama_adapter_lora_init_impl: loading lora adapter from '/tmp/junk2.gguf' ...
llama_adapter_lora_init: failed to apply lora adapter: failed to load lora adapter file
LoadLoraFromFile: NO THROW
SetLoraAdapters: NO THROW
```

No exception, no return value managed code can test (`LoraAdapter.Pointer` is
internal). The context is then perfectly willing to generate — from the base model,
while the applied-set report names the adapter. That is precisely the AD-7 failure the
project exists to prevent, and neither the port nor the strategy could catch it,
because both were told the application succeeded.

Two things close it. Admission (Story 1.6) already re-hashes `adapter.gguf` against
its manifest, so a modified container never reaches the registry. On top of that the
runtime checks the `GGUF` magic of every container — base model included — before
handing it to llama.cpp, and a failure is a startup reason line. Neither covers a
*well-formed* GGUF that is not this model's adapter; for that, the manifest's
`ggufSha256` is the only guarantee, which is one more reason provenance is never
inferred from the file.

### [HIT] `Per_request_strategy_reports_the_application_it_did_at_request_time` is flaky, and it is **not** Story 3.1's doing

The test sleeps 100 ms inside the apply stage and asserts the reported figure is under
120 ms — the assertion that catches a stage folding in its neighbour's work. It went red
during Story 3.1 with `Application reported 152846 us`, and the obvious story was that the
new LLamaSharp tests pin every core for seconds and starve the sleeping thread.

That story was wrong, and checking it mattered. The failure reproduces with the
real-runtime class excluded from the run entirely, and it reproduces **at the baseline
commit `f888ae8`, in a worktree with none of Story 3.1's code present** — twice in six
three-second runs, at 123 ms and 193 ms. It is a pre-existing flake: xunit runs test
classes in parallel, and a `Task.Delay(100)` simply is not guaranteed to be rescheduled
inside 20 ms on a loaded machine.

So it is reported here rather than fixed, and the 120 ms bound is **not** widened — it is
the assertion that makes the in-band timings trustworthy, and loosening it to make a run
green would blunt exactly that. A real fix means measuring the property without a
wall-clock sleep (a runtime that blocks on a signal the test releases, say), which is
Epic 2's test to change, not this story's.

What Story 3.1 did do is keep its own tests out of the way: `LlamaRuntimeTests` is its own
collection with `[CollectionDefinition(DisableParallelization = true)]`, so seconds of
saturated CPU never overlap the timing suite. That reduces the pressure; it does not
remove the flake, and nothing here should be read as having fixed it.

### [HIT] Detokenizing one token at a time mangles every multi-byte character

The first cut appended `Vocab.LLamaTokenToString(token, false)` per sampled token. That
is wrong for any UTF-8 codepoint a tokenizer splits across two tokens — routine for
CJK, emoji and accented text, and the base model here is Qwen3, so it is the normal
case rather than an edge one. Each half decodes to replacement characters and the
character is gone, silently, in a `200`.

`StreamingTokenDecoder` exists for exactly this: it buffers the bytes and only hands
back completed characters. The runtime now feeds it every token and reads once at the
end. Related, and equally silent: the per-token call is nullable, and a `?? string.Empty`
turned a failed decode into a shorter answer instead of an error — the streaming decoder
removes that swallow along with the splitting.

### [HIT] `addBos: true` is not "the safe default" — the vocabulary already knows

`context.Tokenize(text, addBos: true, ...)` looked harmless. Qwen3 reports
`Vocab.ShouldAddBOS == false`, and the chat template the README recommends already opens
with `<|im_start|>`, so every prompt was being given a beginning-of-sequence token the
model was not trained to see there. It is read from the vocabulary now. Nothing crashed;
the output was just quietly conditioned on a token that should not have been there.

### [HIT] A faulted `Lazy<T>` is cached forever, so one transient failure retires the key

Contexts and adapter handles were memoized as `Lazy<T>` in a `ConcurrentDictionary`.
`Lazy<T>` with `ExecutionAndPublication` caches the *exception* as well as the value: a
single failed `CreateContext` or `LoadLoraFromFile` — a momentary allocation failure, a
file briefly unreadable — rethrows the same exception for every later request, for the
life of the process. Under `PerRequestApply` that permanently retires one pooled context
id; for an adapter it retires that tenant. Both accessors now evict the entry on failure
(`TryRemove` on the exact `Lazy` instance, so a concurrent replacement is not dropped)
and the next request retries.

### [TRAP] Sharing one `LLamaWeights` across contexts needs a reader/writer lock, not a mutex

Adapter weights are model-resident, so `LoadLoraFromFile` **mutates the shared model**
while `llama_decode` on any other context is reading it — and with the default
`ContextPoolSize` of 4 that overlap is the normal case, not a rare one. The first cut
guarded the load with the same short lock that protects the static lease registry, which
serialized loads against each other and against nothing else.

The lease now carries a `ReaderWriterLockSlim`: decoding, applying a set to a context and
creating a context take it for **reading** (so contexts still decode concurrently, which
is verified to be safe); reading a container into the model, unloading one and freeing the
weights take it for **writing**. `Dispose` taking the write lock is also what makes
disposal safe — it cannot be granted while any decode is inside a context, so no context
is freed under a running `llama_decode`. The plain `bool _disposed` became an
`Interlocked` flag for the same reason: two `Dispose` calls could otherwise both pass the
check, dispose the contexts twice and double-decrement the lease's reference count, freeing
the weights while another runtime still held them.
