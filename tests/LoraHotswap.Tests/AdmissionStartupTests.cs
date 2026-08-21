using LoraHotswap.Api;
using LoraHotswap.Core;
using LoraHotswap.Core.Ports;

namespace LoraHotswap.Tests;

/// <summary>
/// The host-level admission run: it produces a testable outcome (problems or a
/// built registry) that the composition root maps to an exit code and stderr. A
/// fake reader keeps it offline and independent of the filesystem, so the
/// unknown-tenant / rejected-adapter paths are exercised without binding a
/// listener -- the outcome is produced entirely before any process-exit concern.
/// </summary>
public class AdmissionStartupTests
{
    private const string ConfiguredBase = "Qwen/Qwen3-0.6B";
    private const string GoodSource = "aa00000000000000000000000000000000000000000000000000000000000000";
    private const string GoodGguf = "bb00000000000000000000000000000000000000000000000000000000000000";
    private static readonly string GoodId = "sha256:" + GoodSource;

    private sealed class FakeReader(Dictionary<string, AdapterArtifacts> byDirectory) : IAdapterArtifactsReader
    {
        public Task<AdapterArtifacts> ReadAsync(string adapterDirectory, CancellationToken cancellationToken) =>
            Task.FromResult(byDirectory[adapterDirectory]);
    }

    private static AdapterArtifacts Valid(string dir) =>
        new(dir,
            new AdapterManifest(GoodSource, GoodGguf, "0".PadLeft(40, '0'), GoodId),
            GoodSource, GoodGguf,
            new AdapterConfigSnapshot(8, 16, false, ConfiguredBase),
            [0.5]);

    private static AdapterArtifacts BaseMismatch(string dir) =>
        new(dir,
            new AdapterManifest(GoodSource, GoodGguf, "0".PadLeft(40, '0'), GoodId),
            GoodSource, GoodGguf,
            new AdapterConfigSnapshot(8, 16, false, "unsloth/Qwen3-0.6B"),
            [0.5]);

    [Fact]
    public async Task All_valid_and_resolved_builds_the_registry()
    {
        var reader = new FakeReader(new() { ["adapters/a"] = Valid("a") });
        var tenants = new Dictionary<TenantId, AdapterId> { [new TenantId("t1")] = new AdapterId(GoodId) };

        var outcome = await AdmissionStartup.RunAsync(
            ["adapters/a"], ConfiguredBase, tenants, reader, TestContext.Current.CancellationToken);

        Assert.True(outcome.Succeeded);
        Assert.Empty(outcome.Problems);
        Assert.NotNull(outcome.Registry!.ResolveTenant(new TenantId("t1")));
    }

    [Fact]
    public async Task Unknown_tenant_target_fails_with_the_reason()
    {
        var reader = new FakeReader(new() { ["adapters/a"] = Valid("a") });
        var tenants = new Dictionary<TenantId, AdapterId>
        {
            [new TenantId("t1")] = new AdapterId("sha256:notadmitted"),
        };

        var outcome = await AdmissionStartup.RunAsync(
            ["adapters/a"], ConfiguredBase, tenants, reader, TestContext.Current.CancellationToken);

        Assert.False(outcome.Succeeded);
        Assert.Null(outcome.Registry);
        var problem = Assert.Single(outcome.Problems);
        Assert.Equal(AdmissionProblem.TypeUriPrefix + "unknown-adapter", problem.Type);
        // The reason renders to a single stderr line naming the tenant.
        Assert.Contains("t1", problem.ToStderrLine(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rejected_adapter_fails_startup_and_is_reported()
    {
        var reader = new FakeReader(new() { ["adapters/bad"] = BaseMismatch("bad") });

        var outcome = await AdmissionStartup.RunAsync(
            ["adapters/bad"], ConfiguredBase, new Dictionary<TenantId, AdapterId>(),
            reader, TestContext.Current.CancellationToken);

        Assert.False(outcome.Succeeded);
        var problem = Assert.Single(outcome.Problems);
        Assert.Equal(AdmissionProblem.TypeUriPrefix + "base-model-mismatch", problem.Type);
    }

    [Fact]
    public async Task Rejections_and_unresolved_tenants_are_all_accumulated()
    {
        var reader = new FakeReader(new()
        {
            ["adapters/good"] = Valid("good"),
            ["adapters/bad"] = BaseMismatch("bad"),
        });
        var tenants = new Dictionary<TenantId, AdapterId>
        {
            [new TenantId("t1")] = new AdapterId(GoodId),          // resolves
            [new TenantId("t2")] = new AdapterId("sha256:missing"), // unknown
        };

        var outcome = await AdmissionStartup.RunAsync(
            ["adapters/good", "adapters/bad"], ConfiguredBase, tenants,
            reader, TestContext.Current.CancellationToken);

        Assert.False(outcome.Succeeded);
        Assert.Equal(2, outcome.Problems.Count);
        Assert.Contains(outcome.Problems, p => p.Type.EndsWith("base-model-mismatch", StringComparison.Ordinal));
        Assert.Contains(outcome.Problems, p => p.Type.EndsWith("unknown-adapter", StringComparison.Ordinal));
    }
}
