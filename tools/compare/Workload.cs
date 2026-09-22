using System.Globalization;
using System.Text.Json;
using LoraHotswap.Core;

namespace LoraHotswap.Compare;

/// <summary>
/// One tenant in the comparison workload: the tenant name the request carries, the
/// adapter directory under the workload's root that serves it, and that adapter's
/// content identity as its manifest records it. The identity is read, never invented
/// -- admission re-derives it from the bytes and would reject a wrong one.
/// </summary>
/// <param name="Tenant">The tenant name requests carry.</param>
/// <param name="DirectoryName">The adapter directory name under the adapters root.</param>
/// <param name="AdapterId">The <c>sha256:</c> identity the adapter's manifest records.</param>
public sealed record WorkloadTenant(string Tenant, string DirectoryName, string AdapterId);

/// <summary>
/// Reads the adapter directories a comparison serves from. Nothing here measures
/// anything: it only turns a directory of adapters into the tenant map both arms are
/// configured with, so the two arms cannot differ in what they serve.
/// </summary>
public static class AdapterCatalog
{
    /// <summary>The manifest that records an adapter's content identity.</summary>
    public const string ManifestFileName = "adapter.manifest.json";

    /// <summary>The PEFT config that names the base model an adapter targets.</summary>
    public const string ConfigFileName = "adapter_config.json";

    /// <summary>The manifest member carrying the content identity.</summary>
    public const string ManifestIdMember = "adapterId";

    /// <summary>
    /// The adapters under <paramref name="adaptersRoot"/> as tenants, one tenant per
    /// directory, named after the directory so a figure in the table can be traced back
    /// to the adapter that produced it. With <paramref name="directoryNames"/> empty,
    /// every immediate subdirectory is taken, in ordinal order.
    /// </summary>
    public static IReadOnlyList<WorkloadTenant> Discover(string adaptersRoot, IReadOnlyList<string>? directoryNames = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adaptersRoot);
        if (!Directory.Exists(adaptersRoot))
        {
            throw new ComparisonAbortedException($"The adapters root '{adaptersRoot}' does not exist.");
        }

        IReadOnlyList<string> names = directoryNames is { Count: > 0 }
            ? directoryNames
            : Directory.GetDirectories(adaptersRoot)
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrEmpty(name))
                .Select(name => name!)
                .Order(StringComparer.Ordinal)
                .ToArray();

        if (names.Count == 0)
        {
            throw new ComparisonAbortedException($"No adapter directories were found under '{adaptersRoot}'.");
        }

        return names.Select(name => new WorkloadTenant(name, name, ReadAdapterId(Path.Combine(adaptersRoot, name)))).ToArray();
    }

    /// <summary>
    /// The base model every adapter in the workload targets. They must agree: admission
    /// matches the configured base model strictly, so a mixed set has no single
    /// configuration that admits all of it.
    /// </summary>
    public static string BaseModelName(string adaptersRoot, IReadOnlyList<WorkloadTenant> tenants)
    {
        ArgumentNullException.ThrowIfNull(tenants);

        var byBase = tenants
            .Select(t => (t.DirectoryName, Base: ReadBaseModelName(Path.Combine(adaptersRoot, t.DirectoryName))))
            .GroupBy(pair => pair.Base, StringComparer.Ordinal)
            .ToArray();

        if (byBase.Length != 1)
        {
            throw new ComparisonAbortedException(
                "The workload's adapters target different base models, so no single configuration admits them all: " +
                string.Join("; ", byBase.Select(g => $"{g.Key} <- {string.Join(", ", g.Select(p => p.DirectoryName))}")) + ".");
        }

        return byBase[0].Key;
    }

    private static string ReadAdapterId(string adapterDirectory)
    {
        string manifest = Path.Combine(adapterDirectory, ManifestFileName);
        if (!File.Exists(manifest))
        {
            throw new ComparisonAbortedException(
                $"Adapter directory '{adapterDirectory}' has no {ManifestFileName}, so it carries no content identity to map a tenant to.");
        }

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifest));
        if (!document.RootElement.TryGetProperty(ManifestIdMember, out JsonElement id) || id.GetString() is not { Length: > 0 } value)
        {
            throw new ComparisonAbortedException($"'{manifest}' has no '{ManifestIdMember}' member.");
        }

        // Rejects anything that is not a content identity before it reaches configuration.
        return new AdapterId(value).Value;
    }

    private static string ReadBaseModelName(string adapterDirectory)
    {
        string config = Path.Combine(adapterDirectory, ConfigFileName);
        if (!File.Exists(config))
        {
            throw new ComparisonAbortedException($"Adapter directory '{adapterDirectory}' has no {ConfigFileName}.");
        }

        return AdapterConfig.Parse(File.ReadAllText(config)).BaseModelName;
    }
}

/// <summary>One planned request: which tenant, which prompt, and its position in the round.</summary>
/// <param name="Index">Position in the round, so samples can be reported in request order.</param>
/// <param name="Tenant">The tenant the request names.</param>
/// <param name="AdapterId">The adapter that tenant must be served with, from the workload's own map.</param>
/// <param name="Prompt">The prompt the request carries.</param>
public sealed record PlannedRequest(int Index, string Tenant, string AdapterId, string Prompt);

/// <summary>
/// The one workload both arms run. It is built once and handed to every arm unchanged:
/// same tenants, same prompts, same count, same concurrency, same lease timeout. The
/// only thing that differs between arms is the binding strategy -- the measured variable.
/// </summary>
public sealed record ComparisonWorkload
{
    /// <summary>The prompts the workload cycles through. Fixed text, so both arms send identical bytes.</summary>
    public static IReadOnlyList<string> DefaultPrompts { get; } =
    [
        "summarise the release notes",
        "answer in one sentence",
        "translate this paragraph",
        "explain the tradeoff",
    ];

    /// <summary>The default measured requests per arm.</summary>
    public const int DefaultRequests = 200;

    /// <summary>The default number of requests in flight at once.</summary>
    public const int DefaultConcurrency = 4;

    /// <summary>The default warm-up round size; it is discarded, so JIT is not reported as strategy cost.</summary>
    public const int DefaultWarmupRequests = 20;

    /// <summary>
    /// The lease timeout both arms are configured with. Deliberately generous: a queued
    /// request must be measured as acquisition time, not turned into a rejection that
    /// aborts the run. Identical in both arms, so it cannot favour either.
    /// </summary>
    public const int DefaultLeaseTimeoutMilliseconds = 30_000;

    /// <summary>The directory holding every adapter directory the workload serves from.</summary>
    public required string AdaptersRoot { get; init; }

    /// <summary>The tenants served, in the order requests cycle through them.</summary>
    public required IReadOnlyList<WorkloadTenant> Tenants { get; init; }

    /// <summary>The base model every adapter declares; admission matches it strictly.</summary>
    public required string BaseModelName { get; init; }

    /// <summary>Measured requests per arm.</summary>
    public int Requests { get; init; } = DefaultRequests;

    /// <summary>Requests in flight at once, in both arms.</summary>
    public int Concurrency { get; init; } = DefaultConcurrency;

    /// <summary>Requests sent and discarded before measuring.</summary>
    public int WarmupRequests { get; init; } = DefaultWarmupRequests;

    /// <inheritdoc cref="DefaultLeaseTimeoutMilliseconds"/>
    public int LeaseTimeoutMilliseconds { get; init; } = DefaultLeaseTimeoutMilliseconds;

    /// <summary>The prompts the workload cycles through.</summary>
    public IReadOnlyList<string> Prompts { get; init; } = DefaultPrompts;

    /// <summary>
    /// The contexts each arm is given. Both arms get one context per tenant: the resident
    /// strategy holds exactly that many by construction, so giving the pooled arm the same
    /// budget is what makes the two comparable rather than one of them simply larger.
    /// </summary>
    public int ContextPoolSize => Tenants.Count;

    /// <summary>Throw if the workload could not run identically in both arms.</summary>
    public ComparisonWorkload Validate()
    {
        if (Tenants.Count == 0)
        {
            throw new ComparisonAbortedException("The workload must serve at least one tenant.");
        }

        if (Tenants.Select(t => t.Tenant).Distinct(StringComparer.Ordinal).Count() != Tenants.Count)
        {
            throw new ComparisonAbortedException("Two tenants in the workload share a name.");
        }

        if (Requests < 1)
        {
            throw new ComparisonAbortedException("The workload must send at least one measured request per arm.");
        }

        if (Concurrency < 1)
        {
            throw new ComparisonAbortedException("Concurrency must be at least 1.");
        }

        if (WarmupRequests < 0)
        {
            throw new ComparisonAbortedException("The warm-up round cannot be negative.");
        }

        if (Prompts.Count == 0)
        {
            throw new ComparisonAbortedException("The workload must carry at least one prompt.");
        }

        // A zero timeout turns every queued request into a lease-unavailable rejection,
        // which aborts the run: the one setting that silently makes a comparison impossible.
        if (LeaseTimeoutMilliseconds < 1)
        {
            throw new ComparisonAbortedException(
                "The lease timeout must be at least 1 ms: at 0 a request that has to wait for a context is rejected " +
                "instead of measured, and the run aborts.");
        }

        return this;
    }

    /// <summary>
    /// The round of <paramref name="count"/> requests, tenants and prompts assigned by
    /// position. A pure function of the workload, so the warm-up round and the measured
    /// round of one arm are byte-for-byte the rounds the other arm runs.
    /// </summary>
    public IReadOnlyList<PlannedRequest> Round(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        return Enumerable.Range(0, count)
            .Select(i => new PlannedRequest(
                i, Tenants[i % Tenants.Count].Tenant, Tenants[i % Tenants.Count].AdapterId, Prompts[i % Prompts.Count]))
            .ToArray();
    }

    /// <summary>The workload as rows for the published table, so a reader knows what was run.</summary>
    public IReadOnlyList<(string Field, string Value)> Describe() =>
    [
        ("Tenants", $"{Tenants.Count} ({string.Join(", ", Tenants.Select(t => $"`{t.Tenant}` -> `{t.AdapterId}`"))})"),
        ("Base model", $"`{BaseModelName}`"),
        ("Requests per strategy", Requests.ToString(CultureInfo.InvariantCulture) + " (measured)"),
        ("Warm-up requests", WarmupRequests.ToString(CultureInfo.InvariantCulture) + " (sent first, then discarded)"),
        ("Concurrency", Concurrency.ToString(CultureInfo.InvariantCulture) + " requests in flight"),
        ("Prompts", $"{Prompts.Count}, cycled by request position (identical text in both arms)"),
        ("Contexts per arm", ContextPoolSize.ToString(CultureInfo.InvariantCulture) + " (`Serving:ContextPoolSize`, one per tenant, same in both arms)"),
        ("Lease timeout", LeaseTimeoutMilliseconds.ToString(CultureInfo.InvariantCulture) + " ms (same in both arms)"),
    ];
}
