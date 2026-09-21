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
public sealed record TenantResponse(
    TenantId Tenant,
    AdapterId Adapter,
    IReadOnlySet<AdapterId> AppliedAdapters,
    string BindingStrategy,
    string Text);

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

        AdmittedAdapter adapter = _registry.ResolveTenant(request.Tenant)
            ?? throw new RequestProblemException(RequestProblem.UnknownTenant(request.Tenant));

        // Admission already enforces this; re-checking keeps the serve path fail-closed
        // on its own terms rather than by assumption.
        if (!string.Equals(adapter.Config.BaseModelName, _configuredBaseModel, StringComparison.Ordinal))
        {
            throw new RequestProblemException(
                RequestProblem.BaseModelMismatch(request.Tenant, adapter, _configuredBaseModel));
        }

        await using ContextLease lease = await _strategy.LeaseAsync(request.Tenant, adapter, cancellationToken);

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

        return new TenantResponse(request.Tenant, applied.Single(), applied, _strategy.Name, result.Text);
    }
}
