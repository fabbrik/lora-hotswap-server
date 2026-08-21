using LoraHotswap.Core;

namespace LoraHotswap.Api;

/// <summary>
/// The admission configuration surface, bound from the <c>Admission</c> section of
/// application settings and validated at startup. Tenants and adapters are declared
/// here, not discovered implicitly, per the epic's config mandate.
/// </summary>
public sealed class AdmissionOptions
{
    /// <summary>The configuration section these options bind to.</summary>
    public const string SectionName = "Admission";

    /// <summary>The configured base-model name; adapters must declare it exactly (strict match).</summary>
    public string BaseModelName { get; set; } = string.Empty;

    /// <summary>The root directory containing adapter subdirectories.</summary>
    public string AdaptersRoot { get; set; } = "adapters";

    /// <summary>
    /// Explicit adapter directory names under <see cref="AdaptersRoot"/>. When empty,
    /// every immediate subdirectory of the root is discovered.
    /// </summary>
    public List<string> Adapters { get; set; } = [];

    /// <summary>Map of tenant name to the <c>sha256:</c>-prefixed adapter identity it targets.</summary>
    public Dictionary<string, string> Tenants { get; set; } = [];

    /// <summary>Validate the bound options, throwing a descriptive error on any problem.</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(BaseModelName))
        {
            throw new InvalidOperationException(
                $"{SectionName}: BaseModelName must be configured (the strict base-model match target).");
        }

        if (string.IsNullOrWhiteSpace(AdaptersRoot))
        {
            throw new InvalidOperationException($"{SectionName}: AdaptersRoot must be configured.");
        }

        // Surface malformed tenant targets now, as configuration errors, rather than
        // letting an invalid AdapterId throw deep inside admission.
        foreach (var (tenant, adapterId) in Tenants)
        {
            _ = new TenantId(tenant);
            _ = new AdapterId(adapterId);
        }
    }

    /// <summary>The tenant map as value types, ready for registry building.</summary>
    public IReadOnlyDictionary<TenantId, AdapterId> TenantMap() =>
        Tenants.ToDictionary(kv => new TenantId(kv.Key), kv => new AdapterId(kv.Value));

    /// <summary>
    /// Resolve the adapter directories to admit, relative to <paramref name="basePath"/>
    /// when <see cref="AdaptersRoot"/> is not rooted. Uses the explicit
    /// <see cref="Adapters"/> list when present, otherwise discovers every immediate
    /// subdirectory of the root. Returns an empty list when the root does not exist.
    /// </summary>
    public IReadOnlyList<string> ResolveAdapterDirectories(string basePath)
    {
        string root = Path.IsPathRooted(AdaptersRoot)
            ? AdaptersRoot
            : Path.Combine(basePath, AdaptersRoot);

        if (Adapters.Count > 0)
        {
            return Adapters.Select(name => Path.Combine(root, name)).ToList();
        }

        return Directory.Exists(root)
            ? Directory.GetDirectories(root).OrderBy(d => d, StringComparer.Ordinal).ToList()
            : [];
    }
}
