using LoraHotswap.Core;
using LoraHotswap.Infrastructure;

namespace LoraHotswap.Tests;

/// <summary>
/// The driven reader over the committed offline admission fixtures: it recomputes
/// the source and gguf SHA-256 from raw file bytes, parses the manifest (nullable
/// when absent), and projects the config. Paired with the pure decision it drives
/// the full matrix without network or the gitignored /adapters/ tree.
/// </summary>
public class FileSystemAdapterArtifactsReaderTests
{
    private const string ConfiguredBase = "Qwen/Qwen3-0.6B";

    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "fixtures", "admission", name);

    private static readonly FileSystemAdapterArtifactsReader Reader = new();

    [Fact]
    public async Task Valid_fixture_recomputes_matching_hashes_and_is_admitted()
    {
        var artifacts = await Reader.ReadAsync(Fixture("valid"), TestContext.Current.CancellationToken);

        Assert.NotNull(artifacts.Manifest);
        Assert.Equal(artifacts.Manifest!.SourceSha256, artifacts.RecomputedSourceSha256);
        Assert.Equal(artifacts.Manifest.GgufSha256, artifacts.RecomputedGgufSha256);

        var result = Admission.Admit(artifacts, ConfiguredBase);
        Assert.True(result.IsAdmitted);
        // Identity is derived from the recomputed source hash (the fixture's manifest
        // adapterId agrees, but the derived value is authoritative).
        Assert.Equal("sha256:" + artifacts.RecomputedSourceSha256, result.AdmittedId!.Value.Value);
    }

    [Fact]
    public async Task Tampered_source_fixture_diverges_only_on_source()
    {
        var artifacts = await Reader.ReadAsync(Fixture("tampered-source"), TestContext.Current.CancellationToken);

        Assert.NotEqual(artifacts.Manifest!.SourceSha256, artifacts.RecomputedSourceSha256);
        Assert.Equal(artifacts.Manifest.GgufSha256, artifacts.RecomputedGgufSha256);

        var result = Admission.Admit(artifacts, ConfiguredBase);
        Assert.False(result.IsAdmitted);
        Assert.Equal(AdmissionProblem.TypeUriPrefix + "hash-mismatch", result.Problem!.Type);
        Assert.Contains("source", result.Problem.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tampered_gguf_fixture_diverges_only_on_gguf()
    {
        var artifacts = await Reader.ReadAsync(Fixture("tampered-gguf"), TestContext.Current.CancellationToken);

        Assert.Equal(artifacts.Manifest!.SourceSha256, artifacts.RecomputedSourceSha256);
        Assert.NotEqual(artifacts.Manifest.GgufSha256, artifacts.RecomputedGgufSha256);

        var result = Admission.Admit(artifacts, ConfiguredBase);
        Assert.False(result.IsAdmitted);
        Assert.Equal(AdmissionProblem.TypeUriPrefix + "hash-mismatch", result.Problem!.Type);
        Assert.Contains("gguf", result.Problem.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_manifest_fixture_yields_null_manifest_and_is_rejected()
    {
        var artifacts = await Reader.ReadAsync(Fixture("no-manifest"), TestContext.Current.CancellationToken);

        Assert.Null(artifacts.Manifest);

        var result = Admission.Admit(artifacts, ConfiguredBase);
        Assert.Equal(AdmissionProblem.TypeUriPrefix + "missing-manifest", result.Problem!.Type);
    }

    [Fact]
    public async Task Base_mismatch_fixture_is_rejected_on_strict_base()
    {
        var artifacts = await Reader.ReadAsync(Fixture("base-mismatch"), TestContext.Current.CancellationToken);

        Assert.Equal("unsloth/Qwen3-0.6B", artifacts.Config!.BaseModelName);

        var result = Admission.Admit(artifacts, ConfiguredBase);
        Assert.Equal(AdmissionProblem.TypeUriPrefix + "base-model-mismatch", result.Problem!.Type);
    }

    [Fact]
    public async Task Inert_fixture_is_rejected_as_inert()
    {
        var artifacts = await Reader.ReadAsync(Fixture("inert"), TestContext.Current.CancellationToken);

        var result = Admission.Admit(artifacts, ConfiguredBase);
        Assert.Equal(AdmissionProblem.TypeUriPrefix + "inert-adapter", result.Problem!.Type);
    }
}
