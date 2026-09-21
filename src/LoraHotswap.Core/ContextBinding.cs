using System.Collections.Concurrent;
using System.Diagnostics;

namespace LoraHotswap.Core;

/// <summary>
/// An exclusive hold on one runtime context (AD-3). While held, no other request can
/// lease the same context, so only the holder mutates its adapter configuration.
/// Disposing releases it; disposing twice is harmless.
/// </summary>
/// <remarks>
/// A lease also carries what it cost to obtain (Story 2.6). Only the strategy knows
/// which of the two stages it actually performed: <c>PerRequestApply</c> applies the
/// adapter inside the lease, while <c>PerTenantResident</c> applied it at startup and
/// honestly reports <c>0</c> for application.
/// </remarks>
public sealed class ContextLease : IAsyncDisposable
{
    private readonly Action _release;
    private int _released;

    /// <summary>Wrap a held context; <paramref name="release"/> runs exactly once, on dispose.</summary>
    /// <param name="contextId">The leased context.</param>
    /// <param name="release">Runs exactly once, on dispose.</param>
    /// <param name="acquisitionMicroseconds">What acquiring the context cost, in whole microseconds.</param>
    /// <param name="applicationMicroseconds">What applying the adapter cost at request time, in whole microseconds.</param>
    public ContextLease(string contextId, Action release, long acquisitionMicroseconds, long applicationMicroseconds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contextId);
        ArgumentNullException.ThrowIfNull(release);
        ArgumentOutOfRangeException.ThrowIfNegative(acquisitionMicroseconds);
        ArgumentOutOfRangeException.ThrowIfNegative(applicationMicroseconds);
        ContextId = contextId;
        _release = release;
        AcquisitionMicroseconds = acquisitionMicroseconds;
        ApplicationMicroseconds = applicationMicroseconds;
    }

    /// <summary>The leased context.</summary>
    public string ContextId { get; }

    /// <summary>What acquiring this context cost, in whole microseconds.</summary>
    public long AcquisitionMicroseconds { get; }

    /// <summary>
    /// What applying the adapter cost at request time, in whole microseconds. <c>0</c> when
    /// the strategy did no application work at request time -- the true answer, not a gap.
    /// </summary>
    public long ApplicationMicroseconds { get; }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0)
        {
            _release();
        }

        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// How contexts bind to tenants (AD-9) -- the project's measured variable. Exactly two
/// implementations exist, <see cref="PerRequestApplyStrategy"/> and
/// <see cref="PerTenantResidentStrategy"/>, chosen by name in configuration and echoed
/// in every response.
/// </summary>
public interface IContextBindingStrategy
{
    /// <summary>The strategy's configured and echoed name.</summary>
    string Name { get; }

    /// <summary>
    /// Prepare contexts before the host accepts traffic. A failure here is a startup
    /// failure, never a downgrade to serving without the adapter.
    /// </summary>
    Task InitializeAsync(AdapterRegistry registry, CancellationToken cancellationToken);

    /// <summary>
    /// Lease a context that has <paramref name="adapter"/> applied, exclusively.
    /// Throws <see cref="RequestProblemException"/> with <see cref="ProblemTypes.LeaseUnavailable"/>
    /// when no context frees up within the lease timeout, or
    /// <see cref="ProblemTypes.AdapterNotApplied"/> when the adapter cannot be applied.
    /// </summary>
    Task<ContextLease> LeaseAsync(TenantId tenant, AdmittedAdapter adapter, CancellationToken cancellationToken);
}

/// <summary>Resolves a configured strategy name to one of the two implementations.</summary>
public static class ContextBindingStrategies
{
    /// <summary>The two valid strategy names, in declaration order.</summary>
    public static IReadOnlyList<string> Names { get; } =
        [PerRequestApplyStrategy.StrategyName, PerTenantResidentStrategy.StrategyName];

    /// <summary>
    /// Create the strategy named <paramref name="name"/> (ordinal, exact).
    /// <paramref name="poolSize"/> applies to <see cref="PerRequestApplyStrategy"/> only;
    /// the resident strategy holds one context per tenant.
    /// </summary>
    public static IContextBindingStrategy Create(
        string name, IInferenceRuntime runtime, int poolSize, TimeSpan leaseTimeout) => name switch
    {
        PerRequestApplyStrategy.StrategyName => new PerRequestApplyStrategy(runtime, poolSize, leaseTimeout),
        PerTenantResidentStrategy.StrategyName => new PerTenantResidentStrategy(runtime, leaseTimeout),
        _ => throw new ArgumentException(
            $"Unknown binding strategy '{name}'. Expected one of: {string.Join(", ", Names)}.", nameof(name)),
    };
}

/// <summary>
/// <c>PerRequestApply</c>: a pool of generic contexts; the tenant's adapter is applied
/// on every lease, by the lease holder, before the lease is handed out.
/// </summary>
public sealed class PerRequestApplyStrategy : IContextBindingStrategy
{
    /// <summary>The configured name of this strategy.</summary>
    public const string StrategyName = "PerRequestApply";

    private readonly IInferenceRuntime _runtime;
    private readonly TimeSpan _leaseTimeout;
    private readonly SemaphoreSlim _slots;
    private readonly ConcurrentQueue<string> _free = new();

    /// <summary>Create a pool of <paramref name="poolSize"/> generic contexts.</summary>
    public PerRequestApplyStrategy(IInferenceRuntime runtime, int poolSize, TimeSpan leaseTimeout)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentOutOfRangeException.ThrowIfLessThan(poolSize, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(leaseTimeout, TimeSpan.Zero);

        _runtime = runtime;
        _leaseTimeout = leaseTimeout;
        _slots = new SemaphoreSlim(poolSize, poolSize);
        for (int i = 0; i < poolSize; i++)
        {
            _free.Enqueue($"pooled-{i}");
        }
    }

    /// <inheritdoc/>
    public string Name => StrategyName;

    /// <inheritdoc/>
    public Task InitializeAsync(AdapterRegistry registry, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc/>
    public async Task<ContextLease> LeaseAsync(TenantId tenant, AdmittedAdapter adapter, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(adapter);

        long acquisitionStarted = Stopwatch.GetTimestamp();
        if (!await _slots.WaitAsync(_leaseTimeout, cancellationToken))
        {
            throw new RequestProblemException(RequestProblem.LeaseUnavailable(tenant, Name, _leaseTimeout));
        }

        // The semaphore count and the free-queue length move together, so holding a
        // slot guarantees a free context id.
        if (!_free.TryDequeue(out string? contextId))
        {
            _slots.Release();
            throw new InvalidOperationException("Context pool accounting is inconsistent: a slot was held with no free context.");
        }

        long acquisitionMicroseconds = RequestTimings.MicrosecondsSince(acquisitionStarted);

        void Release()
        {
            _free.Enqueue(contextId);
            _slots.Release();
        }

        // Application is timed separately from acquisition: this strategy exists to make
        // exactly that cost visible, so it may never be folded into the wait.
        long applicationStarted = Stopwatch.GetTimestamp();
        try
        {
            await _runtime.ApplyAdaptersAsync(contextId, new HashSet<AdapterId> { adapter.Id }, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Release();
            throw;
        }
        catch (Exception ex)
        {
            Release();
            throw new RequestProblemException(
                RequestProblem.AdapterNotApplied(tenant, adapter.Id, "the runtime failed to apply it on lease"), ex);
        }

        return new ContextLease(contextId, Release, acquisitionMicroseconds,
            RequestTimings.MicrosecondsSince(applicationStarted));
    }
}

/// <summary>
/// <c>PerTenantResident</c>: one resident context per configured tenant, with that
/// tenant's adapter applied once at startup. A lease is exclusive use of the
/// tenant's resident context.
/// </summary>
public sealed class PerTenantResidentStrategy : IContextBindingStrategy
{
    /// <summary>The configured name of this strategy.</summary>
    public const string StrategyName = "PerTenantResident";

    private readonly IInferenceRuntime _runtime;
    private readonly TimeSpan _leaseTimeout;
    private IReadOnlyDictionary<TenantId, Resident>? _residents;

    /// <summary>Create the strategy; contexts are created by <see cref="InitializeAsync"/>.</summary>
    public PerTenantResidentStrategy(IInferenceRuntime runtime, TimeSpan leaseTimeout)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentOutOfRangeException.ThrowIfLessThan(leaseTimeout, TimeSpan.Zero);

        _runtime = runtime;
        _leaseTimeout = leaseTimeout;
    }

    /// <inheritdoc/>
    public string Name => StrategyName;

    /// <inheritdoc/>
    public async Task InitializeAsync(AdapterRegistry registry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(registry);

        var residents = new Dictionary<TenantId, Resident>();
        foreach (var (tenant, adapterId) in registry.Tenants.OrderBy(t => t.Key.Value, StringComparer.Ordinal))
        {
            string contextId = $"resident-{tenant.Value}";
            try
            {
                await _runtime.ApplyAdaptersAsync(contextId, new HashSet<AdapterId> { adapterId }, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new RequestProblemException(
                    RequestProblem.AdapterNotApplied(tenant, adapterId, "the resident context could not be configured at startup"), ex);
            }

            residents[tenant] = new Resident(contextId, adapterId);
        }

        _residents = residents;
    }

    /// <inheritdoc/>
    public async Task<ContextLease> LeaseAsync(TenantId tenant, AdmittedAdapter adapter, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(adapter);

        long acquisitionStarted = Stopwatch.GetTimestamp();
        if (_residents is null || !_residents.TryGetValue(tenant, out Resident? resident))
        {
            throw new RequestProblemException(
                RequestProblem.AdapterNotApplied(tenant, adapter.Id, "no resident context holds it"));
        }

        if (resident.AdapterId != adapter.Id)
        {
            throw new RequestProblemException(
                RequestProblem.AdapterNotApplied(tenant, adapter.Id, $"the resident context holds '{resident.AdapterId}'"));
        }

        if (!await resident.Gate.WaitAsync(_leaseTimeout, cancellationToken))
        {
            throw new RequestProblemException(RequestProblem.LeaseUnavailable(tenant, Name, _leaseTimeout));
        }

        // Application is 0, and honestly so: this strategy applied the adapter at startup,
        // so no application work happens at request time. Acquisition is still measured.
        return new ContextLease(resident.ContextId, () => resident.Gate.Release(),
            RequestTimings.MicrosecondsSince(acquisitionStarted), 0);
    }

    private sealed record Resident(string ContextId, AdapterId AdapterId)
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
    }
}
