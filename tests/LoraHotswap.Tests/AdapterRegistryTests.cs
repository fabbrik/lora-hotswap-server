using System.Collections;
using System.Reflection;
using LoraHotswap.Core;

namespace LoraHotswap.Tests;

/// <summary>
/// The immutable registry: proven mutator-free by reflection (AD-4), and its
/// tenant resolution -- including the unknown-adapter rejection -- exercised
/// directly.
/// </summary>
public class AdapterRegistryTests
{
    private static AdmittedAdapter Admitted(string idHex, string dir = "d") =>
        new(new AdapterId("sha256:" + idHex),
            new AdapterConfig(8, 16, false, "Qwen/Qwen3-0.6B"),
            dir);

    [Fact]
    public void Registry_exposes_no_public_mutator()
    {
        var mutating = new[] { "Add", "Remove", "Replace", "Evict", "Set", "Clear", "Insert", "Update", "Put" };

        var offenders = typeof(AdapterRegistry)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName) // exclude property getters
            .Where(m => mutating.Any(v => m.Name.StartsWith(v, StringComparison.Ordinal)))
            .Select(m => m.Name)
            .ToList();

        Assert.True(offenders.Count == 0,
            "AdapterRegistry must expose no mutator; found: " + string.Join(", ", offenders));
    }

    [Fact]
    public void Registry_exposes_no_public_settable_property()
    {
        var settable = typeof(AdapterRegistry)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.SetMethod is { IsPublic: true })
            .Select(p => p.Name)
            .ToList();

        Assert.Empty(settable);
    }

    [Fact]
    public void Registry_does_not_expose_a_mutable_collection()
    {
        // A read-only view must not be an ICollection<T> the caller could Add to.
        foreach (var p in typeof(AdapterRegistry).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            bool isMutableCollection = p.PropertyType.GetInterfaces()
                .Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ICollection<>));
            Assert.False(isMutableCollection,
                $"AdapterRegistry.{p.Name} exposes a mutable ICollection<T>.");
        }
    }

    [Fact]
    public void Exposed_tenants_cannot_be_cast_to_a_mutable_dictionary_and_mutated()
    {
        var a = Admitted("aa");
        var registry = AdapterRegistry.Build(
            [a], new Dictionary<TenantId, AdapterId> { [new TenantId("t1")] = a.Id }).Registry!;

        // A ReadOnlyDictionary's IDictionary surface throws on any mutation, so the
        // AD-4 guarantee is not castable-away.
        Assert.IsNotType<Dictionary<TenantId, AdapterId>>(registry.Tenants);
        var asDictionary = (IDictionary)registry.Tenants;
        Assert.True(asDictionary.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => asDictionary.Add(new TenantId("t2"), a.Id));
    }

    [Fact]
    public void Exposed_adapters_cannot_be_cast_to_a_mutable_collection_and_mutated()
    {
        var a = Admitted("aa");
        var registry = AdapterRegistry.Build([a], new Dictionary<TenantId, AdapterId>()).Registry!;

        // A fresh array is handed out; even if cast to IList it is fixed-size, so a
        // mutation cannot reach back into the registry's backing store.
        var asList = (IList)registry.Adapters;
        Assert.True(asList.IsFixedSize);
        Assert.Throws<NotSupportedException>(() => asList.Add(a));
    }

    [Fact]
    public void Build_skips_a_duplicate_adapter_id_without_throwing()
    {
        // Two directories with identical content share an AdapterId. Build must not
        // throw (ToDictionary would); the duplicate is the same adapter, so it is
        // skipped and a tenant still resolves.
        var first = Admitted("aa", "dir-one");
        var second = Admitted("aa", "dir-two");
        var tenantMap = new Dictionary<TenantId, AdapterId> { [new TenantId("t1")] = first.Id };

        var result = AdapterRegistry.Build([first, second], tenantMap);

        Assert.True(result.Succeeded);
        Assert.Single(result.Registry!.Adapters);
        Assert.NotNull(result.Registry.ResolveTenant(new TenantId("t1")));
    }

    [Fact]
    public void Build_succeeds_and_resolves_every_tenant()
    {
        var a = Admitted("aa", "alpha");
        var tenantMap = new Dictionary<TenantId, AdapterId> { [new TenantId("t1")] = a.Id };

        var result = AdapterRegistry.Build([a], tenantMap);

        Assert.True(result.Succeeded);
        Assert.Empty(result.Problems);
        Assert.Equal(a, result.Registry!.ResolveTenant(new TenantId("t1")));
        Assert.True(result.Registry.Contains(a.Id));
    }

    [Fact]
    public void Build_fails_with_unknown_adapter_for_unresolved_tenant()
    {
        var a = Admitted("aa");
        var missing = new AdapterId("sha256:ffff");
        var tenantMap = new Dictionary<TenantId, AdapterId> { [new TenantId("t1")] = missing };

        var result = AdapterRegistry.Build([a], tenantMap);

        Assert.False(result.Succeeded);
        Assert.Null(result.Registry);
        var problem = Assert.Single(result.Problems);
        Assert.Equal(AdmissionProblem.TypeUriPrefix + "unknown-adapter", problem.Type);
        Assert.Contains("t1", problem.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_tenant_resolves_to_null()
    {
        var result = AdapterRegistry.Build([Admitted("aa")], new Dictionary<TenantId, AdapterId>());

        Assert.Null(result.Registry!.ResolveTenant(new TenantId("nobody")));
    }
}
