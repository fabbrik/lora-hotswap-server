using LoraHotswap.Core.Ports;

namespace LoraHotswap.Core;

/// <summary>
/// The outcome of admitting one adapter: either an admitted <see cref="AdapterId"/>,
/// or the first <see cref="AdmissionProblem"/> encountered. Exactly one is set.
/// </summary>
public sealed record AdmissionResult
{
    private AdmissionResult(AdapterId? admittedId, AdmissionProblem? problem)
    {
        AdmittedId = admittedId;
        Problem = problem;
    }

    /// <summary>The admitted identity when <see cref="IsAdmitted"/>; otherwise null.</summary>
    public AdapterId? AdmittedId { get; }

    /// <summary>The rejection when not admitted; otherwise null.</summary>
    public AdmissionProblem? Problem { get; }

    /// <summary>True iff the adapter was admitted.</summary>
    public bool IsAdmitted => Problem is null;

    internal static AdmissionResult Admitted(AdapterId id) => new(id, null);
    internal static AdmissionResult Rejected(AdmissionProblem problem) => new(null, problem);
}

/// <summary>
/// The pure admission decision over already-read <see cref="AdapterArtifacts"/>.
///
/// Applies the checks in a deliberate, first-problem-wins order:
/// <list type="number">
///   <item>missing manifest -- without it there are no hashes to check;</item>
///   <item>provenance hashes, source then gguf, naming which diverged;</item>
///   <item>strict ordinal base-model match;</item>
///   <item>inert content (via <see cref="AdapterValidator"/>).</item>
/// </list>
/// No filesystem, no native tensors -- fully unit-testable.
/// </summary>
public static class Admission
{
    /// <summary>Admit <paramref name="artifacts"/> against the <paramref name="configuredBaseModel"/>.</summary>
    public static AdmissionResult Admit(AdapterArtifacts artifacts, string configuredBaseModel)
    {
        ArgumentNullException.ThrowIfNull(artifacts);
        ArgumentNullException.ThrowIfNull(configuredBaseModel);

        string name = artifacts.AdapterDirectoryName;

        // 1. Missing manifest -- provenance is never inferred otherwise.
        if (artifacts.Manifest is not { } manifest)
        {
            return AdmissionResult.Rejected(AdmissionProblem.MissingManifest(name));
        }

        // 2. Provenance hashes: source first, then gguf, naming which diverged.
        if (!HashesEqual(manifest.SourceSha256, artifacts.RecomputedSourceSha256))
        {
            return AdmissionResult.Rejected(
                AdmissionProblem.HashMismatch(name, "source", manifest.SourceSha256, artifacts.RecomputedSourceSha256));
        }

        if (!HashesEqual(manifest.GgufSha256, artifacts.RecomputedGgufSha256))
        {
            return AdmissionResult.Rejected(
                AdmissionProblem.HashMismatch(name, "gguf", manifest.GgufSha256, artifacts.RecomputedGgufSha256));
        }

        // The admitted identity is derived from the *recomputed* source hash, never
        // trusted from the manifest's self-declared `adapterId`. A manifest could
        // pass the source-hash check yet carry an arbitrary adapterId; identity must
        // be authoritative and content-derived, so we build it from the bytes we
        // just hashed. The source hash matched above, so this equals what a correct
        // manifest records.
        var adapterId = new AdapterId("sha256:" + artifacts.RecomputedSourceSha256);

        // Config is required for the remaining semantic checks.
        if (artifacts.Config is not { } config)
        {
            // No config to compare against the configured base model: treat as a
            // base-model mismatch against the empty declared base.
            return AdmissionResult.Rejected(
                AdmissionProblem.BaseModelMismatch(name, string.Empty, configuredBaseModel));
        }

        // 3. Strict ordinal base-model match -- deliberately no normalization.
        if (!string.Equals(config.BaseModelName, configuredBaseModel, StringComparison.Ordinal))
        {
            return AdmissionResult.Rejected(
                AdmissionProblem.BaseModelMismatch(name, config.BaseModelName, configuredBaseModel));
        }

        // 4. Inert content, composed from Story 1.5's rule (not re-implemented).
        var validation = AdapterValidator.Validate(artifacts.LoraBMaxAbs);
        if (!validation.IsValid && validation.Reason == RejectionReason.Inert)
        {
            return AdmissionResult.Rejected(AdmissionProblem.InertAdapter(name, adapterId.Value));
        }

        return AdmissionResult.Admitted(adapterId);
    }

    /// <summary>
    /// Provenance equality. A null on either side (an absent manifest field or an
    /// absent artifact) is never a match. SHA-256 hex is compared case-insensitively.
    /// </summary>
    private static bool HashesEqual(string? manifestHash, string? recomputedHash) =>
        manifestHash is not null
        && recomputedHash is not null
        && string.Equals(manifestHash, recomputedHash, StringComparison.OrdinalIgnoreCase);
}
