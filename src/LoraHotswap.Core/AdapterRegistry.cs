using System.Collections.ObjectModel;

namespace LoraHotswap.Core;

/// <summary>
/// One admitted adapter's durable facts: its content <see cref="AdapterId"/>, its
/// parsed <see cref="AdapterConfig"/>, and its directory name (a label only).
/// </summary>
/// <param name="Id">The content-derived identity.</param>
/// <param name="Config">The parsed adapter config.</param>
/// <param name="DirectoryName">The directory name, for human-facing messages.</param>
public sealed record AdmittedAdapter(AdapterId Id, AdapterConfig Config, string DirectoryName);

/// <summary>
/// The outcome of building an <see cref="AdapterRegistry"/>: either a built
/// registry, or the tenant-resolution problems that prevented it. Exactly one of
/// <see cref="Registry"/> / non-empty <see cref="Problems"/> holds.
/// </summary>
public sealed record RegistryBuildResult
{
    private RegistryBuildResult(AdapterRegistry? registry, IReadOnlyList<AdmissionProblem> problems)
    {
        Registry = registry;
        Problems = problems;
    }

    /// <summary>The built registry when every tenant resolved; otherwise null.</summary>
    public AdapterRegistry? Registry { get; }

    /// <summary>The unresolved-tenant problems; empty on success.</summary>
    public IReadOnlyList<AdmissionProblem> Problems { get; }

    /// <summary>True iff the registry was built (every tenant resolved).</summary>
    public bool Succeeded => Registry is not null;

    internal static RegistryBuildResult Ok(AdapterRegistry registry) => new(registry, []);
    internal static RegistryBuildResult Failed(IReadOnlyList<AdmissionProblem> problems) => new(null, problems);
}

/// <summary>
/// An <b>immutable</b> registry of admitted adapters, constructed once and offering
/// lookup only. There is deliberately no public add/remove/replace/evict/set
/// member: adding an adapter requires a process restart (AD-4). A reflection test
/// proves the type exposes no public mutator.
/// </summary>
public sealed class AdapterRegistry
{
    private readonly ReadOnlyDictionary<AdapterId, AdmittedAdapter> _adapters;
    private readonly ReadOnlyDictionary<TenantId, AdapterId> _tenants;

    private AdapterRegistry(
        ReadOnlyDictionary<AdapterId, AdmittedAdapter> adapters,
        ReadOnlyDictionary<TenantId, AdapterId> tenants)
    {
        _adapters = adapters;
        _tenants = tenants;
    }

    /// <summary>
    /// Every admitted adapter (genuinely immutable snapshot). A fresh array, so the
    /// caller cannot cast it back to a mutable collection and reach into the registry.
    /// </summary>
    public IReadOnlyList<AdmittedAdapter> Adapters => _adapters.Values.ToArray();

    /// <summary>
    /// Every configured tenant that resolved to an admitted adapter. Backed by a
    /// <see cref="ReadOnlyDictionary{TKey,TValue}"/>, so a cast to <c>IDictionary</c>
    /// exposes only a throwing, read-only surface -- the AD-4 guarantee is not
    /// castable-away.
    /// </summary>
    public IReadOnlyDictionary<TenantId, AdapterId> Tenants => _tenants;

    /// <summary>Look an admitted adapter up by its content identity.</summary>
    public AdmittedAdapter? Find(AdapterId id) => _adapters.TryGetValue(id, out var a) ? a : null;

    /// <summary>True iff <paramref name="id"/> names an admitted adapter.</summary>
    public bool Contains(AdapterId id) => _adapters.ContainsKey(id);

    /// <summary>Resolve a tenant to its admitted adapter, or null if the tenant is unknown.</summary>
    public AdmittedAdapter? ResolveTenant(TenantId tenant) =>
        _tenants.TryGetValue(tenant, out var id) ? Find(id) : null;

    /// <summary>
    /// Build the registry from admitted adapters and a configured tenant map,
    /// resolving every tenant to an admitted adapter. A tenant referencing an
    /// unadmitted <see cref="AdapterId"/> yields an <c>unknown-adapter</c> problem
    /// and no registry is produced.
    /// </summary>
    public static RegistryBuildResult Build(
        IEnumerable<AdmittedAdapter> admitted,
        IReadOnlyDictionary<TenantId, AdapterId> tenantMap)
    {
        ArgumentNullException.ThrowIfNull(admitted);
        ArgumentNullException.ThrowIfNull(tenantMap);

        // TryAdd, not ToDictionary: two admitted directories with identical content
        // share an AdapterId. Identical content means the identical adapter, so the
        // duplicate is skipped rather than crashing startup (ToDictionary throws) --
        // Build stays within the fail-closed contract and never raises.
        var adapters = new Dictionary<AdapterId, AdmittedAdapter>();
        foreach (var adapter in admitted)
        {
            adapters.TryAdd(adapter.Id, adapter);
        }

        var problems = new List<AdmissionProblem>();
        foreach (var (tenant, requested) in tenantMap)
        {
            if (!adapters.ContainsKey(requested))
            {
                problems.Add(AdmissionProblem.UnknownAdapter(tenant, requested));
            }
        }

        if (problems.Count > 0)
        {
            return RegistryBuildResult.Failed(problems);
        }

        var tenants = new Dictionary<TenantId, AdapterId>(tenantMap);
        return RegistryBuildResult.Ok(
            new AdapterRegistry(adapters.AsReadOnly(), tenants.AsReadOnly()));
    }
}
