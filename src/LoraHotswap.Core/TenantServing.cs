using System.Diagnostics;

namespace LoraHotswap.Core;

/// <summary>A tenant-scoped generation request.</summary>
/// <param name="Tenant">The tenant the caller names.</param>
/// <param name="Prompt">The prompt to generate from.</param>
public sealed record TenantRequest(TenantId Tenant, string Prompt);

/// <summary>
/// A response produced with the tenant's adapter applied. <see cref="Adapter"/> is
/// the adapter the runtime reported applied -- never merely the one the registry
/// resolved -- and <see cref="AppliedAdapters"/> is never empty.
/// </summary>
/// <param name="Tenant">The tenant that was served.</param>
/// <param name="Adapter">The adapter that was applied.</param>
/// <param name="AppliedAdapters">The exact applied set the runtime reported.</param>
/// <param name="BindingStrategy">The active binding strategy (AD-9).</param>
/// <param name="Text">The generated text.</param>
/// <param name="Timings">What each pipeline stage cost (Story 2.6).</param>
public sealed record TenantResponse(
    TenantId Tenant,
    AdapterId Adapter,
    IReadOnlySet<AdapterId> AppliedAdapters,
    string BindingStrategy,
    string Text,
    RequestTimings Timings);

/// <summary>
/// What each pipeline stage cost, in whole microseconds (NFR4). This is the project's
/// only measurement path: the serving pipeline measures the stages it owns and the
/// binding strategy reports acquisition and application separately, because the two
/// strategies differ in which of them happens at request time. Every figure is
/// present on every successful response -- never omitted, sampled, or zero-suppressed.
/// A stage that genuinely does no work at request time reports <c>0</c>, which is the
/// true answer rather than a missing measurement.
/// </summary>
/// <param name="AdapterResolutionMicroseconds">Resolving the tenant to an admitted adapter.</param>
/// <param name="ContextAcquisitionMicroseconds">Acquiring an exclusive context lease.</param>
/// <param name="AdapterApplicationMicroseconds">Applying the adapter at request time; <c>0</c> when it was applied at startup.</param>
/// <param name="GenerationMicroseconds">Generating through the runtime.</param>
public sealed record RequestTimings(
    long AdapterResolutionMicroseconds,
    long ContextAcquisitionMicroseconds,
    long AdapterApplicationMicroseconds,
    long GenerationMicroseconds)
{
    /// <summary>Every figure is a non-negative whole microsecond (NFR4), enforced where it is built.</summary>
    public long AdapterResolutionMicroseconds { get; } = NonNegative(AdapterResolutionMicroseconds);

    /// <inheritdoc cref="AdapterResolutionMicroseconds"/>
    public long ContextAcquisitionMicroseconds { get; } = NonNegative(ContextAcquisitionMicroseconds);

    /// <inheritdoc cref="AdapterResolutionMicroseconds"/>
    public long AdapterApplicationMicroseconds { get; } = NonNegative(AdapterApplicationMicroseconds);

    /// <inheritdoc cref="AdapterResolutionMicroseconds"/>
    public long GenerationMicroseconds { get; } = NonNegative(GenerationMicroseconds);

    private static long NonNegative(long microseconds) =>
        microseconds >= 0 ? microseconds : throw new ArgumentOutOfRangeException(
            nameof(microseconds), microseconds, "A stage cannot take a negative number of microseconds.");

    /// <summary>
    /// Whole microseconds between two <see cref="Stopwatch.GetTimestamp"/> readings.
    /// Truncated, never rounded up: a stage faster than a microsecond reports <c>0</c>
    /// rather than inventing precision it does not have.
    /// </summary>
    public static long Microseconds(long startTimestamp, long endTimestamp)
    {
        if (endTimestamp <= startTimestamp)
        {
            return 0;
        }

        double microseconds = Stopwatch.GetElapsedTime(startTimestamp, endTimestamp).TotalMicroseconds;
        return microseconds <= 0 ? 0 : (long)microseconds;
    }

    /// <summary>Whole microseconds elapsed since <paramref name="startTimestamp"/>.</summary>
    public static long MicrosecondsSince(long startTimestamp) =>
        Microseconds(startTimestamp, Stopwatch.GetTimestamp());
}

/// <summary>
/// Serves a tenant-scoped request, failing closed (AD-7): unknown tenant, unmatched
/// base, unavailable lease, and unapplied adapter each raise their own typed problem,
/// and there is no path that returns output without the tenant's adapter applied.
/// </summary>
public sealed class TenantServingService
{
    private readonly AdapterRegistry _registry;
    private readonly IInferenceRuntime _runtime;
    private readonly IContextBindingStrategy _strategy;
    private readonly string _configuredBaseModel;

    /// <summary>Compose the service over an admitted registry and an initialized strategy.</summary>
    public TenantServingService(
        AdapterRegistry registry,
        IInferenceRuntime runtime,
        IContextBindingStrategy strategy,
        string configuredBaseModel)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(strategy);
        ArgumentException.ThrowIfNullOrWhiteSpace(configuredBaseModel);

        _registry = registry;
        _runtime = runtime;
        _strategy = strategy;
        _configuredBaseModel = configuredBaseModel;
    }

    /// <summary>The active binding strategy's name.</summary>
    public string BindingStrategy => _strategy.Name;

    /// <summary>Serve <paramref name="request"/> or throw <see cref="RequestProblemException"/>.</summary>
    public async Task<TenantResponse> ServeAsync(TenantRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Prompt);

        long resolutionStarted = Stopwatch.GetTimestamp();
        AdmittedAdapter adapter = _registry.ResolveTenant(request.Tenant)
            ?? throw new RequestProblemException(RequestProblem.UnknownTenant(request.Tenant));

        // Admission already enforces this; re-checking keeps the serve path fail-closed
        // on its own terms rather than by assumption.
        if (!string.Equals(adapter.Config.BaseModelName, _configuredBaseModel, StringComparison.Ordinal))
        {
            throw new RequestProblemException(
                RequestProblem.BaseModelMismatch(request.Tenant, adapter, _configuredBaseModel));
        }

        // Resolution ends here: everything after this point is the lease's and the
        // runtime's time, and folding it in would make this field whole-request latency.
        long resolutionMicroseconds = RequestTimings.MicrosecondsSince(resolutionStarted);

        await using ContextLease lease = await _strategy.LeaseAsync(request.Tenant, adapter, cancellationToken);

        long generationStarted = Stopwatch.GetTimestamp();
        InferenceResult result;
        try
        {
            result = await _runtime.GenerateAsync(lease.ContextId, request.Prompt, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new RequestProblemException(RequestProblem.GenerationFailed(request.Tenant, adapter.Id), ex);
        }

        long generationMicroseconds = RequestTimings.MicrosecondsSince(generationStarted);

        // The routing claim (AD-5): the runtime must report exactly this tenant's adapter
        // applied. An empty or foreign set means the output was not produced with it.
        if (result.AppliedAdapters is not { Count: 1 } applied || !applied.Contains(adapter.Id))
        {
            string reported = result.AppliedAdapters is { Count: > 0 } set
                ? string.Join(", ", set.Select(a => a.Value).Order(StringComparer.Ordinal))
                : "(none)";
            throw new RequestProblemException(RequestProblem.AdapterNotApplied(
                request.Tenant, adapter.Id, $"the runtime reported applied adapters {reported}"));
        }

        // Every figure comes from this pipeline and the lease it held: one measurement
        // path, so Story 2.8 has nothing else it could prefer.
        var timings = new RequestTimings(
            resolutionMicroseconds,
            lease.AcquisitionMicroseconds,
            lease.ApplicationMicroseconds,
            generationMicroseconds);

        return new TenantResponse(request.Tenant, applied.Single(), applied, _strategy.Name, result.Text, timings);
    }
}
