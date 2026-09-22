using System.Text.Json;

namespace LoraHotswap.Compare;

/// <summary>
/// The comparison could not be completed honestly, so it was not completed at all. Every
/// path that would publish a figure the tool cannot stand behind -- a rejected request, a
/// response with no <c>timings</c>, an arm that would not start -- raises this instead,
/// and no table is written.
/// </summary>
public sealed class ComparisonAbortedException : Exception
{
    /// <summary>Create an abort with a reason.</summary>
    public ComparisonAbortedException(string message) : base(message)
    {
    }

    /// <summary>Create an abort with a reason and the RFC 9457 problem type that caused it.</summary>
    public ComparisonAbortedException(string message, string? problemType) : base(message) => ProblemType = problemType;

    /// <summary>The RFC 9457 <c>type</c> of the rejection that aborted the run, when there was one.</summary>
    public string? ProblemType { get; }

    /// <summary>
    /// The abort for a request the server refused. The rejection's own problem type is
    /// reported, because "the run failed" without it tells a reader nothing about why.
    /// </summary>
    public static ComparisonAbortedException Rejected(string strategy, PlannedRequest request, int status, string body)
    {
        ArgumentNullException.ThrowIfNull(request);

        string? type = null, title = null, detail = null, traceId = null;
        try
        {
            using JsonDocument problem = JsonDocument.Parse(body);
            type = Member(problem.RootElement, "type");
            title = Member(problem.RootElement, "title");
            detail = Member(problem.RootElement, "detail");
            traceId = Member(problem.RootElement, "traceId");
        }
        catch (JsonException)
        {
            // A non-problem body is itself the diagnosis; it is reported verbatim below.
        }

        string reason = type is null
            ? $"HTTP {status} with a body that is not an RFC 9457 problem: {Truncate(body)}"
            : $"{type} (HTTP {status}) -- {title}: {detail}" + (traceId is null ? "" : $" [trace {traceId}]");

        return new ComparisonAbortedException(
            $"the {strategy} arm was rejected serving tenant '{request.Tenant}' (request #{request.Index}): {reason}",
            type);
    }

    private static string? Member(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string Truncate(string body) => body.Length <= 300 ? body : body[..300] + "...";
}

/// <summary>
/// One stage of the served pipeline as the response body names it. The field name is the
/// wire name, so a reader can match a column in the published table to a member of a
/// response they fetch themselves.
/// </summary>
/// <param name="Field">The <c>_us</c>-suffixed member of the response's <c>timings</c> object.</param>
/// <param name="Description">What the stage covers.</param>
/// <param name="Read">The member's value on a collected sample.</param>
public sealed record TimingStage(string Field, string Description, Func<TimingSample, long> Read);

/// <summary>The four stages every successful response reports (Story 2.6), in pipeline order.</summary>
public static class TimingStages
{
    /// <summary>Resolving the tenant to an admitted adapter.</summary>
    public static TimingStage AdapterResolution { get; } =
        new("adapter_resolution_us", "Resolving the tenant to an admitted adapter", s => s.AdapterResolutionUs);

    /// <summary>Acquiring an exclusive context lease.</summary>
    public static TimingStage ContextAcquisition { get; } =
        new("context_acquisition_us", "Acquiring an exclusive context lease", s => s.ContextAcquisitionUs);

    /// <summary>Applying the adapter at request time.</summary>
    public static TimingStage AdapterApplication { get; } =
        new("adapter_application_us", "Applying the adapter at request time", s => s.AdapterApplicationUs);

    /// <summary>Generating through the runtime.</summary>
    public static TimingStage Generation { get; } =
        new("generation_us", "Generating through the runtime", s => s.GenerationUs);

    /// <summary>Every stage, in pipeline order.</summary>
    public static IReadOnlyList<TimingStage> All { get; } =
        [AdapterResolution, ContextAcquisition, AdapterApplication, Generation];
}

/// <summary>
/// One served response, read for its figures. Every member here was parsed out of
/// <see cref="ResponseBody"/>: the tool reads no clock of its own, so there is nothing in
/// the published table that a reader could not recover by fetching the same response.
/// The body is kept so a published figure can be pointed back at the response that
/// reported it.
/// </summary>
/// <param name="Index">Position in the measured round.</param>
/// <param name="Tenant">The tenant the response says it served.</param>
/// <param name="BindingStrategy">The strategy the response says was active -- the arm, named by the server.</param>
/// <param name="AdapterResolutionUs">The response's <c>adapter_resolution_us</c>.</param>
/// <param name="ContextAcquisitionUs">The response's <c>context_acquisition_us</c>.</param>
/// <param name="AdapterApplicationUs">The response's <c>adapter_application_us</c>.</param>
/// <param name="GenerationUs">The response's <c>generation_us</c>.</param>
/// <param name="ResponseBody">The response body verbatim, as the server sent it.</param>
public sealed record TimingSample(
    int Index,
    string Tenant,
    string BindingStrategy,
    long AdapterResolutionUs,
    long ContextAcquisitionUs,
    long AdapterApplicationUs,
    long GenerationUs,
    string ResponseBody)
{
    /// <summary>
    /// Read one response body. Everything the sample carries comes from here, and any
    /// response that does not carry a full <c>timings</c> object, or that reports a tenant
    /// or a strategy other than the one asked for, aborts the run rather than contributing
    /// a figure nobody can stand behind.
    /// </summary>
    public static TimingSample FromResponseBody(string strategy, PlannedRequest request, string body)
    {
        ArgumentNullException.ThrowIfNull(request);

        JsonElement root;
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            root = document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new ComparisonAbortedException(
                $"the {strategy} arm returned a body that is not JSON for tenant '{request.Tenant}' (request #{request.Index}): {ex.Message}");
        }

        string tenant = RequiredString(strategy, request, root, "tenant");
        string reported = RequiredString(strategy, request, root, "bindingStrategy");

        if (!string.Equals(tenant, request.Tenant, StringComparison.Ordinal))
        {
            throw new ComparisonAbortedException(
                $"the {strategy} arm answered a request for tenant '{request.Tenant}' with a response for '{tenant}' (request #{request.Index}).");
        }

        // The arm is named by the server, not assumed by the tool: a figure is only
        // attributed to a strategy the response itself says was active.
        if (!string.Equals(reported, strategy, StringComparison.Ordinal))
        {
            throw new ComparisonAbortedException(
                $"the {strategy} arm served request #{request.Index} under binding strategy '{reported}'.");
        }

        // A figure is only worth publishing if it came from a request that was served
        // correctly. The response must name the adapter the workload maps this tenant to,
        // and report exactly that one applied -- otherwise the timing describes something
        // other than the comparison being made, and the run aborts rather than publishing it.
        string served = RequiredString(strategy, request, root, "adapterId");
        if (!string.Equals(served, request.AdapterId, StringComparison.Ordinal))
        {
            throw new ComparisonAbortedException(
                $"the {strategy} arm served tenant '{request.Tenant}' with adapter '{served}', not '{request.AdapterId}' (request #{request.Index}).");
        }

        string[] applied = root.TryGetProperty("appliedAdapters", out JsonElement set) && set.ValueKind == JsonValueKind.Array
            ? set.EnumerateArray().Select(a => a.GetString() ?? string.Empty).ToArray()
            : throw new ComparisonAbortedException(
                $"the {strategy} arm returned a response with no 'appliedAdapters' member (request #{request.Index}).");

        if (applied is not [string only] || !string.Equals(only, request.AdapterId, StringComparison.Ordinal))
        {
            throw new ComparisonAbortedException(
                $"the {strategy} arm reported applied adapters [{string.Join(", ", applied)}] for tenant " +
                $"'{request.Tenant}', not exactly ['{request.AdapterId}'] (request #{request.Index}).");
        }

        if (!root.TryGetProperty("timings", out JsonElement timings) || timings.ValueKind != JsonValueKind.Object)
        {
            throw new ComparisonAbortedException(
                $"the {strategy} arm returned a response with no 'timings' object for tenant '{request.Tenant}' (request #{request.Index}); " +
                "there is no other place a figure could come from.");
        }

        return new TimingSample(
            request.Index,
            tenant,
            reported,
            RequiredMicroseconds(strategy, request, timings, TimingStages.AdapterResolution.Field),
            RequiredMicroseconds(strategy, request, timings, TimingStages.ContextAcquisition.Field),
            RequiredMicroseconds(strategy, request, timings, TimingStages.AdapterApplication.Field),
            RequiredMicroseconds(strategy, request, timings, TimingStages.Generation.Field),
            body);
    }

    private static string RequiredString(string strategy, PlannedRequest request, JsonElement root, string member) =>
        root.TryGetProperty(member, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new ComparisonAbortedException(
                $"the {strategy} arm returned a response with no '{member}' member (request #{request.Index}).");

    private static long RequiredMicroseconds(string strategy, PlannedRequest request, JsonElement timings, string member)
    {
        if (!timings.TryGetProperty(member, out JsonElement value) || !value.TryGetInt64(out long microseconds))
        {
            throw new ComparisonAbortedException(
                $"the {strategy} arm returned timings with no whole-microsecond '{member}' (request #{request.Index}); " +
                "a stage the response did not report is not a stage the table can report.");
        }

        if (microseconds < 0)
        {
            throw new ComparisonAbortedException(
                $"the {strategy} arm reported a negative '{member}' ({microseconds}) on request #{request.Index}.");
        }

        return microseconds;
    }
}
