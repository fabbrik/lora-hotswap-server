using System.Diagnostics;

namespace LoraHotswap.Core;

/// <summary>
/// The system's single <see cref="ActivitySource"/> (AD-10), and the tag vocabulary every
/// span started from it shares.
///
/// It lives in Core because the serving pipeline and the binding strategies are the code
/// that knows where a stage begins and ends, and Core may not reference the Api project.
/// Only the exporter wiring -- which sampler, which exporters, which endpoint -- stays in
/// Api (<c>LoraHotswapTracing</c>), where the host configuration is.
/// </summary>
public static class LoraHotswapTelemetry
{
    /// <summary>The one ActivitySource name for the whole system (Spine: Tracing convention).</summary>
    public const string ActivitySourceName = "LoraHotswap";

    /// <summary>
    /// The <see cref="AdapterTag"/> value when no adapter identity is known yet -- a rejected
    /// admission with no source bytes, a malformed identity, or a request whose tenant has not
    /// been resolved. Never a path (AD-1) and never absent.
    /// </summary>
    public const string UnidentifiedAdapter = "unidentified";

    /// <summary>The tenant a span belongs to.</summary>
    public const string TenantTag = "tenant.id";

    /// <summary>The adapter a span belongs to, or <see cref="UnidentifiedAdapter"/>.</summary>
    public const string AdapterTag = "adapter.id";

    /// <summary>The active binding strategy (AD-9) -- the project's measured variable.</summary>
    public const string BindingStrategyTag = "binding.strategy";

    /// <summary>The RFC 9457 <c>type</c> of the problem that rejected a request.</summary>
    public const string ProblemTypeTag = "problem.type";

    /// <summary>The shared source every span is started from.</summary>
    public static ActivitySource Source { get; } = new(ActivitySourceName);

    /// <summary>
    /// Start a serving span already carrying the three tags every serving span must have
    /// (SM-5): the tenant, the adapter identity, and the binding strategy. Returns
    /// <see langword="null"/> when nothing is listening, which is the no-tracing path --
    /// every caller uses <c>?.</c> from there on, so serving behaves identically.
    /// </summary>
    /// <param name="name">The span name.</param>
    /// <param name="tenant">The tenant being served.</param>
    /// <param name="adapterId">The adapter identity, or <see cref="UnidentifiedAdapter"/>.</param>
    /// <param name="bindingStrategy">The active binding strategy's name.</param>
    public static Activity? StartServingSpan(string name, TenantId tenant, string adapterId, string bindingStrategy)
    {
        Activity? span = Source.StartActivity(name, ActivityKind.Internal);
        if (span is null)
        {
            return null;
        }

        span.SetTag(TenantTag, tenant.Value);
        span.SetTag(AdapterTag, adapterId);
        span.SetTag(BindingStrategyTag, bindingStrategy);
        return span;
    }

    /// <summary>
    /// Record a fail-closed rejection on the stage span that raised it: the problem type and
    /// an error status. The title, never the detail or an exception message -- a span carries
    /// no filesystem path and no internal exception text.
    /// </summary>
    public static void RecordRejection(Activity? span, string problemType, string title)
    {
        span?.SetTag(ProblemTypeTag, problemType);
        span?.SetStatus(ActivityStatusCode.Error, title);
    }
}
