namespace LoraHotswap.Core.Ports;

/// <summary>
/// The parsed contents of an adapter's <c>adapter.manifest.json</c> sidecar, whose
/// fields mirror the conversion tool exactly. Any field may be absent in a
/// malformed manifest, so all are nullable; admission decides what a missing field
/// means. Provenance rests only on these recorded hashes -- never on filenames,
/// timestamps, directory names, or GGUF metadata.
/// </summary>
/// <param name="SourceSha256">The recorded SHA-256 (hex) of the source safetensors.</param>
/// <param name="GgufSha256">The recorded SHA-256 (hex) of the produced GGUF.</param>
/// <param name="ConverterRevision">The converter revision that produced the GGUF.</param>
/// <param name="AdapterId">The content-derived identity (<c>sha256:</c> + source hash).</param>
public sealed record AdapterManifest(
    string? SourceSha256,
    string? GgufSha256,
    string? ConverterRevision,
    string? AdapterId);

/// <summary>
/// The admission-relevant snapshot of an adapter's <c>adapter_config.json</c>,
/// carried across the port boundary as plain values so <c>Core.Ports</c> stays
/// dependency-free. The infrastructure reader still parses via Story 1.5's
/// <c>AdapterConfig</c> (the reuse mandate) and projects its values here; Core
/// rebuilds a full config from this snapshot for the registry.
/// </summary>
/// <param name="R">The LoRA rank.</param>
/// <param name="Alpha">The LoRA alpha.</param>
/// <param name="UseRsLora">Whether rSLoRA scaling applies.</param>
/// <param name="BaseModelName">The declared base model, verbatim for strict matching.</param>
public sealed record AdapterConfigSnapshot(int R, int Alpha, bool UseRsLora, string BaseModelName);

/// <summary>
/// Everything admission needs about one adapter directory, already read from disk
/// by the <see cref="IAdapterArtifactsReader"/> driven adapter. A pure DTO in the
/// innermost <c>Core.Ports</c> layer: no filesystem handle, no native tensor --
/// only the manifest, the recomputed hashes, the config snapshot, and the reduced
/// <c>lora_B</c> magnitudes.
/// </summary>
/// <param name="AdapterDirectoryName">
/// The adapter directory's name, used only as a human label -- never a trusted path
/// or a provenance signal.
/// </param>
/// <param name="Manifest">The parsed manifest, or <see langword="null"/> when absent.</param>
/// <param name="RecomputedSourceSha256">
/// The SHA-256 (hex) recomputed from <c>adapter_model.safetensors</c> bytes, or
/// <see langword="null"/> when the file is absent.
/// </param>
/// <param name="RecomputedGgufSha256">
/// The SHA-256 (hex) recomputed from <c>adapter.gguf</c> bytes, or
/// <see langword="null"/> when the file is absent.
/// </param>
/// <param name="Config">The config snapshot, or <see langword="null"/> when unreadable.</param>
/// <param name="LoraBMaxAbs">Each <c>lora_B</c> tensor's <c>max(|·|)</c>, for the inert rule.</param>
public sealed record AdapterArtifacts(
    string AdapterDirectoryName,
    AdapterManifest? Manifest,
    string? RecomputedSourceSha256,
    string? RecomputedGgufSha256,
    AdapterConfigSnapshot? Config,
    IReadOnlyList<double> LoraBMaxAbs);
