using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace LoraHotswap.Api;

/// <summary>
/// In-process serving counters rendered in the Prometheus text exposition format, so a stock
/// Prometheus can scrape <c>/metrics</c> with no extra package. Labels are bounded: tenants
/// are the configured set and outcomes are problem types, never caller-supplied strings.
/// </summary>
public sealed class ServingMetrics
{
    /// <summary>Upper bounds, in seconds, of the generation-latency histogram buckets.</summary>
    public static readonly double[] BucketsSeconds = [0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10, 30];

    private sealed class Series
    {
        public long Count;
        public long SumMicroseconds;
        public readonly long[] Buckets = new long[BucketsSeconds.Length];
    }

    private readonly ConcurrentDictionary<(string Tenant, string Outcome), long> _requests = new();
    private readonly ConcurrentDictionary<string, Series> _latency = new(StringComparer.Ordinal);
    private readonly HashSet<string> _tenants;

    /// <summary>Create metrics for the configured tenants.</summary>
    public ServingMetrics(IEnumerable<string> tenants)
    {
        ArgumentNullException.ThrowIfNull(tenants);
        _tenants = new HashSet<string>(tenants, StringComparer.Ordinal);
    }

    /// <summary>Record a served request and how long generation took.</summary>
    public void RecordServed(string tenant, long generationMicroseconds)
    {
        string label = LabelFor(tenant);
        _requests.AddOrUpdate((label, "ok"), 1, (_, n) => n + 1);
        Series series = _latency.GetOrAdd(label, _ => new Series());
        lock (series)
        {
            series.Count++;
            series.SumMicroseconds += generationMicroseconds;
            double seconds = generationMicroseconds / 1_000_000.0;
            for (int i = 0; i < BucketsSeconds.Length; i++)
            {
                if (seconds <= BucketsSeconds[i])
                {
                    series.Buckets[i]++;
                }
            }
        }
    }

    /// <summary>Record a request that failed, by its problem type.</summary>
    public void RecordFailed(string? tenant, string problemType)
    {
        string outcome = problemType[(problemType.LastIndexOf('/') + 1)..];
        _requests.AddOrUpdate((LabelFor(tenant), outcome), 1, (_, n) => n + 1);
    }

    // An unknown or absent tenant must not mint a new label per caller-chosen string.
    private string LabelFor(string? tenant) => tenant is not null && _tenants.Contains(tenant) ? tenant : "_unknown";

    /// <summary>Render all series in the Prometheus text format.</summary>
    public string Render()
    {
        var sb = new StringBuilder();
        sb.Append("# HELP lorahotswap_requests_total Generate requests by tenant and outcome.\n");
        sb.Append("# TYPE lorahotswap_requests_total counter\n");
        foreach (var ((tenant, outcome), count) in _requests.OrderBy(kv => kv.Key.Tenant, StringComparer.Ordinal).ThenBy(kv => kv.Key.Outcome, StringComparer.Ordinal))
        {
            sb.Append(CultureInfo.InvariantCulture, $"lorahotswap_requests_total{{tenant=\"{tenant}\",outcome=\"{outcome}\"}} {count}\n");
        }

        sb.Append("# HELP lorahotswap_generation_seconds Time spent generating, per tenant.\n");
        sb.Append("# TYPE lorahotswap_generation_seconds histogram\n");
        foreach (var (tenant, series) in _latency.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            lock (series)
            {
                for (int i = 0; i < BucketsSeconds.Length; i++)
                {
                    sb.Append(CultureInfo.InvariantCulture,
                        $"lorahotswap_generation_seconds_bucket{{tenant=\"{tenant}\",le=\"{BucketsSeconds[i]}\"}} {series.Buckets[i]}\n");
                }

                sb.Append(CultureInfo.InvariantCulture, $"lorahotswap_generation_seconds_bucket{{tenant=\"{tenant}\",le=\"+Inf\"}} {series.Count}\n");
                sb.Append(CultureInfo.InvariantCulture, $"lorahotswap_generation_seconds_sum{{tenant=\"{tenant}\"}} {series.SumMicroseconds / 1_000_000.0}\n");
                sb.Append(CultureInfo.InvariantCulture, $"lorahotswap_generation_seconds_count{{tenant=\"{tenant}\"}} {series.Count}\n");
            }
        }

        return sb.ToString();
    }
}
