using System.Security.Cryptography;
using System.Text;

namespace LoraHotswap.Api;

/// <summary>
/// The <c>Auth</c> configuration section. Keys are stored only as SHA-256 hex digests, so a
/// leaked config file is not a leaked credential. Leaving the section empty leaves the server
/// open, which is the right default for localhost and the wrong one for anything shared.
/// </summary>
public sealed class AuthOptions
{
    /// <summary>The configuration section these options bind to.</summary>
    public const string SectionName = "Auth";

    /// <summary>Tenant id to the SHA-256 (64 hex characters) of that tenant's API key.</summary>
    public Dictionary<string, string> TenantKeySha256 { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// The SHA-256 of the admin key, which guards <c>/metrics</c> and adapter verification.
    /// Required whenever any tenant key is set, so enabling auth never leaves an operator
    /// surface open.
    /// </summary>
    public string AdminKeySha256 { get; set; } = "";

    /// <summary>Whether authentication is on at all.</summary>
    public bool Enabled => TenantKeySha256.Count > 0 || AdminKeySha256.Length > 0;

    /// <summary>Validate against the tenants the server will serve; fails startup on any gap.</summary>
    public void Validate(IEnumerable<string> tenants)
    {
        ArgumentNullException.ThrowIfNull(tenants);
        if (!Enabled)
        {
            return;
        }

        if (AdminKeySha256.Length == 0)
        {
            throw new InvalidOperationException($"{SectionName}: AdminKeySha256 is required when any key is configured.");
        }

        foreach ((string tenant, string digest) in TenantKeySha256)
        {
            RequireDigest($"TenantKeySha256:{tenant}", digest);
        }

        RequireDigest("AdminKeySha256", AdminKeySha256);

        string[] unkeyed = tenants.Where(t => !TenantKeySha256.ContainsKey(t)).Order(StringComparer.Ordinal).ToArray();
        if (unkeyed.Length > 0)
        {
            throw new InvalidOperationException(
                $"{SectionName}: no API key for tenant(s) {string.Join(", ", unkeyed)}. " +
                "With auth on, every tenant needs a key; none is served unauthenticated.");
        }

        string[] unknown = TenantKeySha256.Keys.Where(k => !tenants.Contains(k)).Order(StringComparer.Ordinal).ToArray();
        if (unknown.Length > 0)
        {
            throw new InvalidOperationException(
                $"{SectionName}: API key(s) for tenant(s) {string.Join(", ", unknown)} that are not configured under Admission:Tenants.");
        }
    }

    private static void RequireDigest(string name, string value)
    {
        if (value.Length != 64 || !value.All(Uri.IsHexDigit))
        {
            throw new InvalidOperationException(
                $"{SectionName}: {name} must be the SHA-256 of the key as 64 hex characters " +
                "(printf %s \"$KEY\" | shasum -a 256).");
        }
    }
}

/// <summary>
/// Checks bearer keys against the configured digests in constant time. With no keys
/// configured it allows everything; with any configured it denies everything it cannot match.
/// </summary>
public sealed class EndpointSecurity
{
    /// <summary>Authentication off: every request is allowed.</summary>
    public static EndpointSecurity Open { get; } = new(new AuthOptions());

    private readonly Dictionary<string, byte[]> _tenantKeys;
    private readonly byte[]? _adminKey;

    /// <summary>Build from validated options.</summary>
    public EndpointSecurity(AuthOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Enabled = options.Enabled;
        _tenantKeys = options.TenantKeySha256.ToDictionary(
            kv => kv.Key, kv => Convert.FromHexString(kv.Value), StringComparer.Ordinal);
        _adminKey = options.AdminKeySha256.Length == 0 ? null : Convert.FromHexString(options.AdminKeySha256);
    }

    /// <summary>Whether authentication is on.</summary>
    public bool Enabled { get; }

    /// <summary>The SHA-256 hex digest of a key, as the config expects it.</summary>
    public static string Digest(string key) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    /// <summary>Whether the request may act as <paramref name="tenant"/>. The admin key may not: it is an operator, not a tenant.</summary>
    public bool AuthorizeTenant(HttpRequest request, string? tenant)
    {
        if (!Enabled)
        {
            return true;
        }

        byte[]? presented = Presented(request);
        // Compare against a decoy when the tenant is unknown, so timing does not separate
        // "no such tenant" from "wrong key".
        byte[] expected = tenant is not null && _tenantKeys.TryGetValue(tenant, out byte[]? key) ? key : new byte[32];
        bool known = tenant is not null && _tenantKeys.ContainsKey(tenant);
        return presented is not null && CryptographicOperations.FixedTimeEquals(presented, expected) && known;
    }

    /// <summary>Whether the request carries the admin key.</summary>
    public bool AuthorizeAdmin(HttpRequest request)
    {
        if (!Enabled)
        {
            return true;
        }

        byte[]? presented = Presented(request);
        return presented is not null && _adminKey is not null && CryptographicOperations.FixedTimeEquals(presented, _adminKey);
    }

    private static byte[]? Presented(HttpRequest request)
    {
        string? header = request.Headers.Authorization.ToString();
        const string scheme = "Bearer ";
        if (string.IsNullOrEmpty(header) || !header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string key = header[scheme.Length..].Trim();
        return key.Length == 0 ? null : SHA256.HashData(Encoding.UTF8.GetBytes(key));
    }
}
