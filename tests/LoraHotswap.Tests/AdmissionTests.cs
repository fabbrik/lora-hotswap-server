using LoraHotswap.Core;
using LoraHotswap.Core.Ports;

namespace LoraHotswap.Tests;

/// <summary>
/// The pure admission decision over in-memory artifacts -- every I/O-matrix
/// rejection row plus the happy admit, with no filesystem. Ordering is exercised
/// so hash failures surface before base/inert, and the source-vs-gguf naming is
/// asserted explicitly.
/// </summary>
public class AdmissionTests
{
    private const string ConfiguredBase = "Qwen/Qwen3-0.6B";
    private const string SourceHash = "aaaa000000000000000000000000000000000000000000000000000000000000";
    private const string GgufHash = "bbbb000000000000000000000000000000000000000000000000000000000000";
    private static readonly string AdapterIdValue = "sha256:" + SourceHash;

    private static AdapterArtifacts Artifacts(
        AdapterManifest? manifest = null,
        string? sourceHash = SourceHash,
        string? ggufHash = GgufHash,
        string baseModel = ConfiguredBase,
        bool inert = false,
        string name = "adapter-under-test")
    {
        manifest ??= new AdapterManifest(SourceHash, GgufHash, "0".PadLeft(40, '0'), AdapterIdValue);
        var config = new AdapterConfigSnapshot(R: 8, Alpha: 16, UseRsLora: false, BaseModelName: baseModel);
        // A non-inert adapter has a nonzero lora_B magnitude; an inert one is all zero.
        IReadOnlyList<double> magnitudes = inert ? [0.0, 0.0] : [0.0, 0.5];
        return new AdapterArtifacts(name, manifest, sourceHash, ggufHash, config, magnitudes);
    }

    [Fact]
    public void Valid_adapter_is_admitted_with_identity_derived_from_recomputed_source_hash()
    {
        var result = Admission.Admit(Artifacts(), ConfiguredBase);

        Assert.True(result.IsAdmitted);
        // Identity is content-derived from the recomputed source hash, not the
        // manifest field (which happens to agree here).
        Assert.Equal("sha256:" + SourceHash, result.AdmittedId!.Value.Value);
        Assert.Null(result.Problem);
    }

    [Fact]
    public void Admitted_identity_ignores_a_self_declared_manifest_adapter_id()
    {
        // A manifest that passes the source-hash check but carries an arbitrary,
        // untrue adapterId must not have that identity trusted: admission derives
        // identity from the bytes it hashed, not from the self-declared field.
        var manifest = new AdapterManifest(
            SourceHash, GgufHash, "0".PadLeft(40, '0'),
            AdapterId: "sha256:deadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeef");

        var result = Admission.Admit(Artifacts(manifest: manifest), ConfiguredBase);

        Assert.True(result.IsAdmitted);
        Assert.Equal("sha256:" + SourceHash, result.AdmittedId!.Value.Value);
    }

    [Fact]
    public void Missing_manifest_is_rejected_without_inferring_provenance()
    {
        // A directory with valid-looking content but no manifest at all.
        var config = new AdapterConfigSnapshot(8, 16, false, ConfiguredBase);
        var artifacts = new AdapterArtifacts("no-manifest", Manifest: null, SourceHash, GgufHash, config, [0.5]);

        var result = Admission.Admit(artifacts, ConfiguredBase);

        Assert.False(result.IsAdmitted);
        Assert.Equal(AdmissionProblem.TypeUriPrefix + "missing-manifest", result.Problem!.Type);
    }

    [Fact]
    public void Source_hash_divergence_names_the_source_hash()
    {
        var manifest = new AdapterManifest("deadbeef" + SourceHash[8..], GgufHash, "0".PadLeft(40, '0'), AdapterIdValue);

        var result = Admission.Admit(Artifacts(manifest: manifest), ConfiguredBase);

        Assert.False(result.IsAdmitted);
        Assert.Equal(AdmissionProblem.TypeUriPrefix + "hash-mismatch", result.Problem!.Type);
        Assert.Contains("source", result.Problem.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("gguf hash diverged", result.Problem.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Gguf_hash_divergence_names_the_gguf_hash()
    {
        var manifest = new AdapterManifest(SourceHash, "deadbeef" + GgufHash[8..], "0".PadLeft(40, '0'), AdapterIdValue);

        var result = Admission.Admit(Artifacts(manifest: manifest), ConfiguredBase);

        Assert.False(result.IsAdmitted);
        Assert.Equal(AdmissionProblem.TypeUriPrefix + "hash-mismatch", result.Problem!.Type);
        Assert.Contains("gguf", result.Problem.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Source_hash_is_checked_before_gguf_hash()
    {
        // Both diverge; the source failure must win (ordered checks).
        var manifest = new AdapterManifest("f".PadLeft(64, 'f'), "e".PadLeft(64, 'e'), "0".PadLeft(40, '0'), AdapterIdValue);

        var result = Admission.Admit(Artifacts(manifest: manifest), ConfiguredBase);

        Assert.Contains("source", result.Problem!.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Base_model_mismatch_is_rejected_and_this_is_correct_by_design()
    {
        // Some published adapters declare unsloth/Qwen3-0.6B for weights
        // compatible with Qwen/Qwen3-0.6B. Strict string comparison rejects it --
        // asserted here as the intended behavior, not a bug to normalize away.
        var result = Admission.Admit(Artifacts(baseModel: "unsloth/Qwen3-0.6B"), ConfiguredBase);

        Assert.False(result.IsAdmitted);
        Assert.Equal(AdmissionProblem.TypeUriPrefix + "base-model-mismatch", result.Problem!.Type);
        Assert.Contains("unsloth/Qwen3-0.6B", result.Problem.Detail, StringComparison.Ordinal);
        Assert.Contains("Qwen/Qwen3-0.6B", result.Problem.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Inert_adapter_is_rejected_as_its_own_distinct_problem()
    {
        var result = Admission.Admit(Artifacts(inert: true), ConfiguredBase);

        Assert.False(result.IsAdmitted);
        Assert.Equal(AdmissionProblem.TypeUriPrefix + "inert-adapter", result.Problem!.Type);
        // Never conflated with a hash or base failure.
        Assert.DoesNotContain("hash", result.Problem.Type, StringComparison.Ordinal);
    }

    [Fact]
    public void Each_failure_mode_carries_a_distinct_type_uri()
    {
        string[] uris =
        [
            AdmissionProblem.MissingManifest("a").Type,
            AdmissionProblem.HashMismatch("a", "source", "x", "y").Type,
            AdmissionProblem.BaseModelMismatch("a", "x", "y").Type,
            AdmissionProblem.InertAdapter("a", "sha256:x").Type,
            AdmissionProblem.UnknownAdapter(new TenantId("t"), new AdapterId("sha256:x")).Type,
        ];

        Assert.Equal(uris.Length, uris.Distinct().Count());
    }
}
