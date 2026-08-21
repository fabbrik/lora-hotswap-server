using LoraHotswap.Api;
using LoraHotswap.Core;

namespace LoraHotswap.Tests;

/// <summary>
/// The behavioral config logic of <see cref="AdmissionOptions"/>: validation,
/// tenant-map projection, and adapter-directory resolution (explicit list vs
/// discovery, ordinal sort, empty-when-missing, path rooting). Offline and
/// deterministic -- discovery uses a fresh temp directory that is cleaned up.
/// </summary>
public class AdmissionOptionsTests : IDisposable
{
    private readonly string _tempRoot =
        Path.Combine(Path.GetTempPath(), "admission-options-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    private static AdmissionOptions Valid() => new()
    {
        BaseModelName = "Qwen/Qwen3-0.6B",
        AdaptersRoot = "adapters",
    };

    // --- Validate() ----------------------------------------------------------

    [Fact]
    public void Validate_accepts_a_well_formed_configuration()
    {
        var options = Valid();
        options.Tenants["t1"] = "sha256:abc";

        options.Validate(); // does not throw
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_rejects_an_empty_base_model_name(string baseModel)
    {
        var options = Valid();
        options.BaseModelName = baseModel;

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void Validate_rejects_an_empty_adapters_root()
    {
        var options = Valid();
        options.AdaptersRoot = "";

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void Validate_rejects_a_malformed_tenant_adapter_id_target()
    {
        var options = Valid();
        options.Tenants["t1"] = "not-a-sha256-id";

        // The invalid AdapterId shape surfaces as a configuration error.
        Assert.ThrowsAny<ArgumentException>(options.Validate);
    }

    // --- TenantMap() ---------------------------------------------------------

    [Fact]
    public void TenantMap_builds_the_expected_value_type_map()
    {
        var options = Valid();
        options.Tenants["t1"] = "sha256:aaa";
        options.Tenants["t2"] = "sha256:bbb";

        var map = options.TenantMap();

        Assert.Equal(new AdapterId("sha256:aaa"), map[new TenantId("t1")]);
        Assert.Equal(new AdapterId("sha256:bbb"), map[new TenantId("t2")]);
    }

    // --- ResolveAdapterDirectories() -----------------------------------------

    [Fact]
    public void Resolve_uses_the_explicit_list_relative_to_the_base_path()
    {
        var options = Valid();
        options.AdaptersRoot = "adapters";
        options.Adapters = ["b", "a"];

        var resolved = options.ResolveAdapterDirectories(_tempRoot);

        // The explicit list is honored in order (not sorted) and rooted at base/root.
        Assert.Equal(
            [Path.Combine(_tempRoot, "adapters", "b"), Path.Combine(_tempRoot, "adapters", "a")],
            resolved);
    }

    [Fact]
    public void Resolve_discovers_subdirectories_in_ordinal_order_when_list_is_empty()
    {
        string root = Path.Combine(_tempRoot, "adapters");
        Directory.CreateDirectory(Path.Combine(root, "charlie"));
        Directory.CreateDirectory(Path.Combine(root, "alpha"));
        Directory.CreateDirectory(Path.Combine(root, "Bravo"));

        var options = Valid();
        options.AdaptersRoot = root; // rooted absolute path

        var resolved = options.ResolveAdapterDirectories(basePath: "/ignored");

        // Ordinal sort: uppercase 'B' sorts before lowercase 'a'/'c'.
        Assert.Equal(
            [Path.Combine(root, "Bravo"), Path.Combine(root, "alpha"), Path.Combine(root, "charlie")],
            resolved);
    }

    [Fact]
    public void Resolve_returns_empty_when_the_root_does_not_exist()
    {
        var options = Valid();
        options.AdaptersRoot = Path.Combine(_tempRoot, "does-not-exist");

        Assert.Empty(options.ResolveAdapterDirectories(basePath: "/ignored"));
    }

    [Fact]
    public void Resolve_roots_a_relative_adapters_root_at_the_base_path()
    {
        string root = Path.Combine(_tempRoot, "content", "adapters");
        Directory.CreateDirectory(Path.Combine(root, "one"));

        var options = Valid();
        options.AdaptersRoot = "adapters";

        var resolved = options.ResolveAdapterDirectories(Path.Combine(_tempRoot, "content"));

        Assert.Equal([Path.Combine(root, "one")], resolved);
    }
}
