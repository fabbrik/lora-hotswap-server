using LoraHotswap.Core;
using LoraHotswap.Infrastructure;

namespace LoraHotswap.Tests;

/// <summary>
/// AD-3 lease mechanics of the two binding strategies, independent of the serving path:
/// a context is held by at most one lease, a lease that cannot be acquired in time fails
/// with a typed problem, and releasing makes the context available again.
/// </summary>
public sealed class ContextBindingTests
{
    private static readonly TenantId Alice = new("alice");
    private static readonly AdmittedAdapter AliceAdapter = TenantServingTests.World.Adapter('a', "alice");

    [Fact]
    public async Task Per_request_pool_hands_out_distinct_contexts_up_to_its_size()
    {
        var strategy = new PerRequestApplyStrategy(new DeterministicInferenceRuntime(), poolSize: 3, TimeSpan.Zero);

        var leases = new List<ContextLease>();
        for (int i = 0; i < 3; i++)
        {
            leases.Add(await strategy.LeaseAsync(Alice, AliceAdapter, CancellationToken.None));
        }

        Assert.Equal(3, leases.Select(l => l.ContextId).Distinct().Count());
        var error = await Assert.ThrowsAsync<RequestProblemException>(() =>
            strategy.LeaseAsync(Alice, AliceAdapter, CancellationToken.None));
        Assert.Equal(ProblemTypes.LeaseUnavailable, error.Problem.Type);

        foreach (var lease in leases)
        {
            await lease.DisposeAsync();
        }
    }

    [Fact]
    public async Task Releasing_a_lease_lets_the_next_request_acquire_that_context()
    {
        var strategy = new PerRequestApplyStrategy(new DeterministicInferenceRuntime(), poolSize: 1, TimeSpan.Zero);

        string first;
        await using (ContextLease lease = await strategy.LeaseAsync(Alice, AliceAdapter, CancellationToken.None))
        {
            first = lease.ContextId;
        }

        await using ContextLease second = await strategy.LeaseAsync(Alice, AliceAdapter, CancellationToken.None);
        Assert.Equal(first, second.ContextId);
    }

    [Fact]
    public async Task Disposing_a_lease_twice_releases_it_once()
    {
        var strategy = new PerRequestApplyStrategy(new DeterministicInferenceRuntime(), poolSize: 1, TimeSpan.Zero);

        ContextLease lease = await strategy.LeaseAsync(Alice, AliceAdapter, CancellationToken.None);
        await lease.DisposeAsync();
        await lease.DisposeAsync();

        await using ContextLease again = await strategy.LeaseAsync(Alice, AliceAdapter, CancellationToken.None);
        var error = await Assert.ThrowsAsync<RequestProblemException>(() =>
            strategy.LeaseAsync(Alice, AliceAdapter, CancellationToken.None));
        Assert.Equal(ProblemTypes.LeaseUnavailable, error.Problem.Type);
    }

    [Fact]
    public async Task A_waiting_request_acquires_the_context_when_it_is_released_within_the_timeout()
    {
        var strategy = new PerRequestApplyStrategy(new DeterministicInferenceRuntime(), poolSize: 1, TimeSpan.FromSeconds(10));
        ContextLease held = await strategy.LeaseAsync(Alice, AliceAdapter, CancellationToken.None);

        Task<ContextLease> waiting = strategy.LeaseAsync(Alice, AliceAdapter, CancellationToken.None);
        Assert.False(waiting.IsCompleted);

        await held.DisposeAsync();
        await using ContextLease acquired = await waiting;
        Assert.Equal(held.ContextId, acquired.ContextId);
    }

    [Theory]
    [MemberData(nameof(TenantServingTests.Strategies), MemberType = typeof(TenantServingTests))]
    public async Task Concurrent_acquisition_of_one_context_has_exactly_one_winner(string strategyName)
    {
        // Deterministic by construction, not by timing margin: all attempts line up on a
        // Barrier, and the winner holds its lease until every attempt has resolved.
        const int attemptCount = 8;
        var registry = AdapterRegistry.Build([AliceAdapter], new Dictionary<TenantId, AdapterId> { [Alice] = AliceAdapter.Id }).Registry!;
        IContextBindingStrategy strategy = ContextBindingStrategies.Create(
            strategyName, new DeterministicInferenceRuntime(), poolSize: 1, TimeSpan.Zero);
        await strategy.InitializeAsync(registry, CancellationToken.None);

        using var start = new Barrier(attemptCount);
        using var resolved = new CountdownEvent(attemptCount);
        var release = new TaskCompletionSource();

        var attempts = Enumerable.Range(0, attemptCount).Select(_ => Task.Run(async () =>
        {
            Assert.True(start.SignalAndWait(TimeSpan.FromSeconds(10)), "attempts never lined up");
            ContextLease? lease = null;
            try
            {
                lease = await strategy.LeaseAsync(Alice, AliceAdapter, CancellationToken.None);
            }
            catch (RequestProblemException ex) when (ex.Problem.Type == ProblemTypes.LeaseUnavailable)
            {
            }
            finally
            {
                resolved.Signal();
            }

            if (lease is null)
            {
                return false;
            }

            await release.Task;
            await lease.DisposeAsync();
            return true;
        })).ToArray();

        Assert.True(await Task.Run(() => resolved.Wait(TimeSpan.FromSeconds(10))), "not every attempt resolved in time");
        release.SetResult();

        Assert.Equal(1, (await Task.WhenAll(attempts)).Count(won => won));
    }

    [Fact]
    public async Task Resident_lease_for_a_tenant_without_a_resident_context_is_refused()
    {
        var strategy = new PerTenantResidentStrategy(new DeterministicInferenceRuntime(), TimeSpan.Zero);
        var registry = AdapterRegistry.Build([AliceAdapter], new Dictionary<TenantId, AdapterId>()).Registry!;
        await strategy.InitializeAsync(registry, CancellationToken.None);

        var error = await Assert.ThrowsAsync<RequestProblemException>(() =>
            strategy.LeaseAsync(Alice, AliceAdapter, CancellationToken.None));

        Assert.Equal(ProblemTypes.AdapterNotApplied, error.Problem.Type);
    }
}
