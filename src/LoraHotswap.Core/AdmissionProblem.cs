namespace LoraHotswap.Core;

/// <summary>
/// An RFC 9457 problem describing why an adapter (or a tenant reference) failed
/// admission. Each failure mode has its own <see cref="Type"/> URI so later HTTP
/// surfacing (Story 2.1) and log correlation stay unambiguous -- a hash failure is
/// never conflated with a base-model or inert failure.
///
/// Immutable: constructed only through the per-mode factory methods below. The
/// offending adapter is named by its content <see cref="AdapterId"/> when known,
/// otherwise by its directory name -- never implied to be a trusted path.
/// </summary>
public sealed record AdmissionProblem
{
    /// <summary>The stable prefix shared by every admission problem <see cref="Type"/> URI.</summary>
    public const string TypeUriPrefix = "https://lora-hotswap/problems/";

    private AdmissionProblem(
        string type,
        string title,
        string detail,
        string adapterName,
        string? adapterId)
    {
        Type = type;
        Title = title;
        Detail = detail;
        AdapterName = adapterName;
        AdapterId = adapterId;
    }

    /// <summary>The RFC 9457 <c>type</c> URI -- distinct per failure mode.</summary>
    public string Type { get; }

    /// <summary>A short, human-readable summary of the failure mode.</summary>
    public string Title { get; }

    /// <summary>A specific, human-readable explanation naming the offending adapter.</summary>
    public string Detail { get; }

    /// <summary>The offending adapter's directory name (a label, not a trusted path).</summary>
    public string AdapterName { get; }

    /// <summary>The offending adapter's content identity, when it is known at failure time.</summary>
    public string? AdapterId { get; }

    // --- factories: one distinct type URI per failure mode -------------------

    /// <summary>An adapter directory with no <c>adapter.manifest.json</c>.</summary>
    public static AdmissionProblem MissingManifest(string adapterName) => new(
        TypeUriPrefix + "missing-manifest",
        "Adapter manifest is missing",
        $"Adapter '{adapterName}' has no adapter.manifest.json; provenance is never inferred " +
        "from filenames, timestamps, directory names, or GGUF metadata.",
        adapterName,
        adapterId: null);

    /// <summary>
    /// A recomputed hash disagrees with the manifest. <paramref name="which"/> names
    /// the diverged artifact -- <c>"source"</c> or <c>"gguf"</c>.
    /// </summary>
    public static AdmissionProblem HashMismatch(
        string adapterName, string which, string? manifestHash, string? recomputedHash) => new(
        TypeUriPrefix + "hash-mismatch",
        "Adapter provenance hash mismatch",
        $"Adapter '{adapterName}' {which} hash diverged: manifest recorded '{manifestHash ?? "(absent)"}' " +
        $"but the {which} file on disk hashes to '{recomputedHash ?? "(absent)"}'.",
        adapterName,
        adapterId: null);

    /// <summary>The declared base model does not strictly equal the configured base model.</summary>
    public static AdmissionProblem BaseModelMismatch(
        string adapterName, string declaredBase, string configuredBase) => new(
        TypeUriPrefix + "base-model-mismatch",
        "Adapter base-model mismatch",
        $"Adapter '{adapterName}' declares base model '{declaredBase}' but the configured base model is " +
        $"'{configuredBase}'. Base matching is a deliberate strict string comparison -- no normalization.",
        adapterName,
        adapterId: null);

    /// <summary>Every <c>lora_B</c> tensor is zero: merging the adapter is a silent no-op.</summary>
    public static AdmissionProblem InertAdapter(string adapterName, string adapterId) => new(
        TypeUriPrefix + "inert-adapter",
        "Adapter is inert",
        $"Adapter '{adapterName}' ({adapterId}) has every lora_B tensor exactly zero, so merging it is a " +
        "silent no-op. Rejected as inert, distinct from any hash or base-model failure.",
        adapterName,
        adapterId);

    /// <summary>A configured tenant references an <see cref="AdapterId"/> that was not admitted.</summary>
    public static AdmissionProblem UnknownAdapter(TenantId tenant, AdapterId requested) => new(
        TypeUriPrefix + "unknown-adapter",
        "Tenant references an unknown adapter",
        $"Tenant '{tenant}' references adapter '{requested}', which is not among the admitted adapters. " +
        "Every configured tenant must resolve to an admitted adapter.",
        adapterName: requested.Value,
        adapterId: requested.Value);

    /// <summary>A single-line RFC 9457 rendering for stderr at the composition root.</summary>
    public string ToStderrLine() => $"{Type} | {Title}: {Detail}";
}
