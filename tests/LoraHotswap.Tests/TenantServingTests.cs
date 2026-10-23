using System.Collections.Concurrent;
using System.Diagnostics;
using LoraHotswap.Core;
using LoraHotswap.Infrastructure;

namespace LoraHotswap.Tests;

/// <summary>
/// Stories 2.1–2.4 against the deterministic fake runtime (AD-8). Every behavioural test
/// is a theory over both binding strategies, so each requirement is shown to hold under
/// <c>PerRequestApply</c> and <c>PerTenantResident</c> alike (Story 2.3).
/// </summary>
public sealed class TenantServingTests
{
    public static TheoryData<string> Strategies => new(ContextBindingStrategies.Names);

    // --- 2.1: a tenant is served with its own adapter --------------------------------

    [Theory]
    [MemberData(nameof(Strategies))]
    public async Task Tenant_request_is_served_with_exactly_its_own_adapter_applied(string strategy)
    {
        var world = await World.CreateAsync(strategy);

        TenantResponse response = await world.Service.ServeAsync(new TenantRequest(World.Alice, "hello"), CancellationToken.None);

        Assert.Equal(world.AliceAdapter.Id, response.Adapter);
        Assert.Equal([world.AliceAdapter.Id], response.AppliedAdapters);
        Assert.Equal(strategy, response.BindingStrategy);
        Assert.Equal($"fake:{world.AliceAdapter.Id.Value}:hello", response.Text);

        GenerationRecord record = Assert.Single(world.Runtime.Generations);
        Assert.Equal([world.AliceAdapter.Id], record.AppliedAdapters);
    }

    [Theory]
    [MemberData(nameof(Strategies))]
    public async Task Unknown_tenant_is_a_typed_error_and_no_default_is_substituted(string strategy)
    {
        var world = await World.CreateAsync(strategy);

        var error = await Assert.ThrowsAsync<RequestProblemException>(() =>
            world.Service.ServeAsync(new TenantRequest(new TenantId("mallory"), "hello"), CancellationToken.None));

        Assert.Equal(ProblemTypes.UnknownTenant, error.Problem.Type);
        Assert.Empty(world.Runtime.Generations);
    }

    // --- 2.2: exclusive leases under concurrency --------------------------------------

    [Theory]
    [MemberData(nameof(Strategies))]
    public async Task Sustained_concurrent_workload_never_shares_a_context_or_crosses_tenants(string strategy)
    {
        var runtime = new ExclusivityCheckingRuntime();
        // Pool smaller than the tenant count forces PerRequestApply to re-apply across tenants.
        var world = await World.CreateAsync(strategy, runtime, poolSize: 2, leaseTimeout: TimeSpan.FromSeconds(30));
        var tenants = new[] { (World.Alice, world.AliceAdapter.Id), (World.Bob, world.BobAdapter.Id), (World.Carol, world.CarolAdapter.Id) };

        var responses = await Task.WhenAll(Enumerable.Range(0, 300).Select(i => Task.Run(async () =>
        {
            var (tenant, expected) = tenants[i % tenants.Length];
            TenantResponse response = await world.Service.ServeAsync(new TenantRequest(tenant, $"p{i}"), CancellationToken.None);
            return (Expected: expected, response);
        })));

        Assert.Equal(0, runtime.Violations);
        Assert.True(runtime.MaxConcurrency > 1, "The workload never overlapped, so it proved nothing about exclusivity.");
        Assert.All(responses, r =>
        {
            Assert.Equal(r.Expected, r.response.Adapter);
            Assert.Equal([r.Expected], r.response.AppliedAdapters);
        });
    }

    [Theory]
    [MemberData(nameof(Strategies))]
    public async Task A_lease_that_cannot_be_acquired_fails_the_request_without_generating(string strategy)
    {
        var world = await World.CreateAsync(strategy, poolSize: 1, leaseTimeout: TimeSpan.FromMilliseconds(50));
        await using ContextLease held = await world.Strategy.LeaseAsync(World.Alice, world.AliceAdapter, CancellationToken.None);

        var error = await Assert.ThrowsAsync<RequestProblemException>(() =>
            world.Service.ServeAsync(new TenantRequest(World.Alice, "hello"), CancellationToken.None));

        Assert.Equal(ProblemTypes.LeaseUnavailable, error.Problem.Type);
        Assert.Empty(world.Runtime.Generations);
    }

    // --- 2.4: fail closed ---------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Strategies))]
    public async Task Runtime_reporting_no_applied_adapter_is_an_error_never_output(string strategy)
    {
        var runtime = new FaultyRuntime { ReportEmptySet = true };
        var world = await World.CreateAsync(strategy, runtime);

        var error = await Assert.ThrowsAsync<RequestProblemException>(() =>
            world.Service.ServeAsync(new TenantRequest(World.Alice, "hello"), CancellationToken.None));

        Assert.Equal(ProblemTypes.AdapterNotApplied, error.Problem.Type);
        Assert.Contains(world.AliceAdapter.Id.Value, error.Problem.Detail);
    }

    [Theory]
    [MemberData(nameof(Strategies))]
    public async Task Runtime_reporting_another_tenants_adapter_is_an_error_never_output(string strategy)
    {
        var runtime = new FaultyRuntime();
        var world = await World.CreateAsync(strategy, runtime);
        runtime.ReportInstead = world.BobAdapter.Id;

        var error = await Assert.ThrowsAsync<RequestProblemException>(() =>
            world.Service.ServeAsync(new TenantRequest(World.Alice, "hello"), CancellationToken.None));

        Assert.Equal(ProblemTypes.AdapterNotApplied, error.Problem.Type);
    }

    [Fact]
    public async Task Injected_application_fault_returns_an_error_and_releases_the_lease()
    {
        var runtime = new FaultyRuntime { FailApply = true };
        var world = await World.CreateAsync(PerRequestApplyStrategy.StrategyName, runtime, poolSize: 1,
            leaseTimeout: TimeSpan.FromMilliseconds(50));

        var error = await Assert.ThrowsAsync<RequestProblemException>(() =>
            world.Service.ServeAsync(new TenantRequest(World.Alice, "hello"), CancellationToken.None));

        Assert.Equal(ProblemTypes.AdapterNotApplied, error.Problem.Type);
        Assert.IsType<InvalidOperationException>(error.InnerException);
        Assert.DoesNotContain(FaultyRuntime.SecretMessage, error.Problem.Detail);
        Assert.Equal(0, runtime.GenerateCalls);

        // The failed lease was released: with the fault cleared, the single context serves again.
        runtime.FailApply = false;
        TenantResponse response = await world.Service.ServeAsync(new TenantRequest(World.Alice, "hello"), CancellationToken.None);
        Assert.Equal(world.AliceAdapter.Id, response.Adapter);
    }

    [Fact]
    public async Task Resident_context_that_cannot_be_configured_fails_startup()
    {
        var runtime = new FaultyRuntime { FailApply = true };

        var error = await Assert.ThrowsAsync<RequestProblemException>(() =>
            World.CreateAsync(PerTenantResidentStrategy.StrategyName, runtime));

        Assert.Equal(ProblemTypes.AdapterNotApplied, error.Problem.Type);
    }

    [Theory]
    [MemberData(nameof(Strategies))]
    public async Task Generation_fault_is_a_typed_error_without_internal_detail(string strategy)
    {
        var runtime = new FaultyRuntime { FailGenerate = true };
        var world = await World.CreateAsync(strategy, runtime);

        var error = await Assert.ThrowsAsync<RequestProblemException>(() =>
            world.Service.ServeAsync(new TenantRequest(World.Alice, "hello"), CancellationToken.None));

        Assert.Equal(ProblemTypes.GenerationFailed, error.Problem.Type);
        Assert.DoesNotContain(FaultyRuntime.SecretMessage, error.Problem.Detail);
        Assert.NotNull(error.InnerException);
    }

    [Theory]
    [MemberData(nameof(Strategies))]
    public async Task Caller_cancellation_propagates_as_cancellation_not_a_problem(string strategy)
    {
        var world = await World.CreateAsync(strategy);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            world.Service.ServeAsync(new TenantRequest(World.Alice, "hello"), cancelled.Token));
    }

    [Theory]
    [MemberData(nameof(Strategies))]
    public async Task Adapter_declaring_another_base_is_refused_at_serve_time(string strategy)
    {
        var world = await World.CreateAsync(strategy, servedBase: "unsloth/Qwen3-0.6B");

        var error = await Assert.ThrowsAsync<RequestProblemException>(() =>
            world.Service.ServeAsync(new TenantRequest(World.Alice, "hello"), CancellationToken.None));

        Assert.Equal(ProblemTypes.BaseModelMismatch, error.Problem.Type);
        Assert.Empty(world.Runtime.Generations);
    }

    [Fact]
    public void The_five_fail_closed_modes_have_distinct_type_uris_under_one_prefix()
    {
        string[] modes =
        [
            ProblemTypes.UnknownTenant,
            ProblemTypes.BaseModelMismatch,
            ProblemTypes.InertAdapter,
            ProblemTypes.LeaseUnavailable,
            ProblemTypes.AdapterNotApplied,
        ];

        Assert.Equal(modes.Length, modes.Distinct(StringComparer.Ordinal).Count());
        Assert.All(modes, m => Assert.StartsWith(ProblemTypes.Prefix, m));

        // Admission's inert and base-mismatch rejections use the same catalog entries.
        Assert.Equal(ProblemTypes.InertAdapter, AdmissionProblem.InertAdapter("a", "sha256:x").Type);
        Assert.Equal(ProblemTypes.BaseModelMismatch, AdmissionProblem.BaseModelMismatch("a", "x", "y").Type);
    }

    [Fact]
    public void Strategy_factory_accepts_exactly_the_two_named_strategies()
    {
        var runtime = new DeterministicInferenceRuntime();

        Assert.Equal(["PerRequestApply", "PerTenantResident"], ContextBindingStrategies.Names);
        Assert.IsType<PerRequestApplyStrategy>(ContextBindingStrategies.Create("PerRequestApply", runtime, 1, TimeSpan.Zero));
        Assert.IsType<PerTenantResidentStrategy>(ContextBindingStrategies.Create("PerTenantResident", runtime, 1, TimeSpan.Zero));
        Assert.Throws<ArgumentException>(() => ContextBindingStrategies.Create("perrequestapply", runtime, 1, TimeSpan.Zero));
    }

    [Fact]
    public async Task Resident_strategy_applies_each_tenants_adapter_once_at_startup()
    {
        var runtime = new CountingRuntime();
        var world = await World.CreateAsync(PerTenantResidentStrategy.StrategyName, runtime);

        await world.Service.ServeAsync(new TenantRequest(World.Alice, "1"), CancellationToken.None);
        await world.Service.ServeAsync(new TenantRequest(World.Alice, "2"), CancellationToken.None);

        Assert.Equal(3, runtime.ApplyCalls); // one per configured tenant, none per request
    }

    [Fact]
    public async Task Per_request_strategy_applies_the_adapter_on_every_lease()
    {
        var runtime = new CountingRuntime();
        var world = await World.CreateAsync(PerRequestApplyStrategy.StrategyName, runtime);

        await world.Service.ServeAsync(new TenantRequest(World.Alice, "1"), CancellationToken.None);
        await world.Service.ServeAsync(new TenantRequest(World.Alice, "2"), CancellationToken.None);

        Assert.Equal(2, runtime.ApplyCalls);
    }

    // --- 2.6: in-band timings on every response -----------------------------------------

    [Theory]
    [MemberData(nameof(Strategies))]
    public async Task Every_successful_response_carries_all_four_stage_timings(string strategy)
    {
        var world = await World.CreateAsync(strategy);

        TenantResponse response = await world.Service.ServeAsync(new TenantRequest(World.Alice, "hello"), CancellationToken.None);

        RequestTimings timings = response.Timings;
        long[] stages =
        [
            timings.AdapterResolutionMicroseconds,
            timings.ContextAcquisitionMicroseconds,
            timings.AdapterApplicationMicroseconds,
            timings.GenerationMicroseconds,
        ];

        Assert.All(stages, value => Assert.True(value >= 0,
            $"A stage reported {value} microseconds; timings are non-negative integers."));
    }

    [Fact]
    public async Task Per_request_strategy_reports_the_application_it_did_at_request_time()
    {
        // Generation is the long pole, not application. A Task.Delay under a saturated
        // suite has been observed overshooting by 150 ms or more (a fresh-clone build running beside
        // the suite did exactly that), so the bounds sit at 400 ms: folding 800 ms of generation
        // into application is unmistakable, where folding 25 ms into a 100 ms sleep was not.
        var runtime = new SlowRuntime { ApplyDelay = TimeSpan.FromMilliseconds(25), GenerateDelay = TimeSpan.FromMilliseconds(800) };
        var world = await World.CreateAsync(PerRequestApplyStrategy.StrategyName, runtime);

        TenantResponse response = await world.Service.ServeAsync(new TenantRequest(World.Alice, "hello"), CancellationToken.None);

        Assert.True(response.Timings.AdapterApplicationMicroseconds > 10_000,
            "PerRequestApply applies the adapter inside the lease, so the application figure must reflect that work.");
        Assert.True(response.Timings.GenerationMicroseconds > 400_000,
            "The runtime generated for 800 ms, so generation must carry it.");

        // Each stage measures only itself: resolution is a dictionary lookup and acquisition
        // is an uncontended semaphore, so neither can be anywhere near the 800 ms of work.
        Assert.True(response.Timings.AdapterResolutionMicroseconds < 400_000,
            $"Resolution is a registry lookup, so it cannot take {response.Timings.AdapterResolutionMicroseconds} us " +
            "-- it is folding in the lease, the application or the generation.");
        Assert.True(response.Timings.ContextAcquisitionMicroseconds < 400_000,
            $"An uncontended pool slot cannot take {response.Timings.ContextAcquisitionMicroseconds} us " +
            "-- acquisition is folding in the application it should report separately.");
        // Application sleeps 25 ms. Folding in the 800 ms generation would put it past this
        // bound with room to spare, while ordinary scheduling jitter cannot reach it.
        Assert.True(response.Timings.AdapterApplicationMicroseconds < 400_000,
            $"Application reported {response.Timings.AdapterApplicationMicroseconds} us: it is folding in the generation that follows it.");
    }

    [Fact]
    public async Task Resident_strategy_reports_zero_application_because_it_applied_at_startup()
    {
        var runtime = new SlowRuntime { ApplyDelay = TimeSpan.FromMilliseconds(15), GenerateDelay = TimeSpan.FromMilliseconds(15) };
        var world = await World.CreateAsync(PerTenantResidentStrategy.StrategyName, runtime);

        TenantResponse response = await world.Service.ServeAsync(new TenantRequest(World.Alice, "hello"), CancellationToken.None);

        // The startup application was slow; none of it may leak into a request's figure.
        Assert.Equal(0L, response.Timings.AdapterApplicationMicroseconds);
        Assert.True(response.Timings.GenerationMicroseconds > 0);
    }

    [Theory]
    [MemberData(nameof(Strategies))]
    public async Task Acquisition_is_really_measured_a_contended_context_shows_the_wait(string strategy)
    {
        // The wait is caused by the server, not by this thread: with one context (or one
        // resident context per tenant) and a 50 ms generation, a second request for the same
        // tenant must wait for the first. No delay here races the request reaching the lease.
        var slow = new SlowRuntime { GenerateDelay = TimeSpan.FromMilliseconds(50) };
        var world = await World.CreateAsync(strategy, slow, poolSize: 1, leaseTimeout: TimeSpan.FromSeconds(30));

        Task<TenantResponse> first = world.Service.ServeAsync(new TenantRequest(World.Alice, "one"), CancellationToken.None);
        Task<TenantResponse> second = world.Service.ServeAsync(new TenantRequest(World.Alice, "two"), CancellationToken.None);
        TenantResponse[] responses = await Task.WhenAll(first, second);

        Assert.True(responses.Max(r => r.Timings.ContextAcquisitionMicroseconds) > 20_000,
            "One request waited on the other's 50 ms generation, so acquisition cannot report " +
            $"{string.Join(" and ", responses.Select(r => r.Timings.ContextAcquisitionMicroseconds))} us.");

        // The wait belongs to acquisition alone. If the application clock started before the
        // queue wait instead of after it, this figure would carry those 50 ms too --
        // double-counting the wait in the one stage Story 2.8 compares across strategies.
        Assert.All(responses, r => Assert.True(r.Timings.AdapterApplicationMicroseconds < 20_000,
            $"Application reported {r.Timings.AdapterApplicationMicroseconds} us: it is folding in the lease wait."));
    }

    [Fact]
    public void A_sub_microsecond_stage_reports_zero_and_is_never_rounded_up()
    {
        long start = Stopwatch.GetTimestamp();
        // Ticks in one microsecond, at least 1 so the sub-microsecond case stays meaningful
        // on a platform whose timer runs below 1 MHz.
        long oneMicrosecond = Math.Max(1, Stopwatch.Frequency / 1_000_000);

        Assert.Equal(0L, RequestTimings.Microseconds(start, start));
        Assert.Equal(0L, RequestTimings.Microseconds(start, start + oneMicrosecond - 1));
        Assert.Equal(0L, RequestTimings.Microseconds(start, start - oneMicrosecond));
        // Whole microseconds, not ticks or milliseconds: three milliseconds is 3000.
        // Derived from the platform's own frequency rather than assuming it divides evenly:
        // a 3.579545 MHz timer would make a hard-coded 3000 wrong by a microsecond.
        long threeMilliseconds = (long)(Stopwatch.Frequency * 0.003);
        Assert.InRange(RequestTimings.Microseconds(start, start + threeMilliseconds), 2_999L, 3_000L);
    }

    [Theory]
    [MemberData(nameof(Strategies))]
    public async Task Rejected_requests_carry_no_timings_because_there_is_no_response_to_carry_them(string strategy)
    {
        var world = await World.CreateAsync(strategy);

        var error = await Assert.ThrowsAsync<RequestProblemException>(() =>
            world.Service.ServeAsync(new TenantRequest(new TenantId("mallory"), "hello"), CancellationToken.None));

        Assert.Equal(ProblemTypes.UnknownTenant, error.Problem.Type);
        Assert.DoesNotContain("_us", error.Problem.Detail, StringComparison.Ordinal);
    }

    // --- harness ------------------------------------------------------------------------

    internal sealed class World
    {
        public static readonly TenantId Alice = new("alice");
        public static readonly TenantId Bob = new("bob");
        public static readonly TenantId Carol = new("carol");

        public required AdmittedAdapter AliceAdapter { get; init; }
        public required AdmittedAdapter BobAdapter { get; init; }
        public required AdmittedAdapter CarolAdapter { get; init; }
        public required AdapterRegistry Registry { get; init; }
        public required IContextBindingStrategy Strategy { get; init; }
        public required TenantServingService Service { get; init; }
        public required DeterministicInferenceRuntime Runtime { get; init; }

        public static async Task<World> CreateAsync(
            string strategyName,
            DeterministicInferenceRuntimeBase? runtime = null,
            int poolSize = 4,
            TimeSpan? leaseTimeout = null,
            string servedBase = TestFixtures.ConfiguredBase)
        {
            runtime ??= new DeterministicInferenceRuntimeBase();
            AdmittedAdapter alice = Adapter('a', "alice"), bob = Adapter('b', "bob"), carol = Adapter('c', "carol");
            AdapterRegistry registry = AdapterRegistry.Build([alice, bob, carol], new Dictionary<TenantId, AdapterId>
            {
                [Alice] = alice.Id,
                [Bob] = bob.Id,
                [Carol] = carol.Id,
            }).Registry!;

            IContextBindingStrategy strategy = ContextBindingStrategies.Create(
                strategyName, runtime, poolSize, leaseTimeout ?? TimeSpan.FromSeconds(5));
            await strategy.InitializeAsync(registry, CancellationToken.None);

            return new World
            {
                AliceAdapter = alice,
                BobAdapter = bob,
                CarolAdapter = carol,
                Registry = registry,
                Strategy = strategy,
                Service = new TenantServingService(registry, runtime, strategy, servedBase),
                Runtime = runtime.Inner,
            };
        }

        public static AdmittedAdapter Adapter(char hex, string name) =>
            new(new AdapterId("sha256:" + new string(hex, 64)), new AdapterConfig(8, 32, false, TestFixtures.ConfiguredBase), name);
    }

    /// <summary>Delegates to the real fake runtime; subclasses inject faults or observe calls.</summary>
    internal class DeterministicInferenceRuntimeBase : IInferenceRuntime
    {
        public DeterministicInferenceRuntime Inner { get; } = new();

        public virtual Task ApplyAdaptersAsync(string contextId, IReadOnlySet<AdapterId> adapters, CancellationToken cancellationToken) =>
            Inner.ApplyAdaptersAsync(contextId, adapters, cancellationToken);

        public virtual Task<InferenceResult> GenerateAsync(string contextId, string prompt, CancellationToken cancellationToken) =>
            Inner.GenerateAsync(contextId, prompt, cancellationToken);
    }

    private sealed class CountingRuntime : DeterministicInferenceRuntimeBase
    {
        private int _applyCalls;
        public int ApplyCalls => _applyCalls;

        public override Task ApplyAdaptersAsync(string contextId, IReadOnlySet<AdapterId> adapters, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _applyCalls);
            return base.ApplyAdaptersAsync(contextId, adapters, cancellationToken);
        }
    }

    /// <summary>Makes each stage cost real, measurable time, so a timing of 0 would be a bug.</summary>
    internal sealed class SlowRuntime : DeterministicInferenceRuntimeBase
    {
        public TimeSpan ApplyDelay { get; init; }
        public TimeSpan GenerateDelay { get; init; }

        public override async Task ApplyAdaptersAsync(string contextId, IReadOnlySet<AdapterId> adapters, CancellationToken cancellationToken)
        {
            await Task.Delay(ApplyDelay, cancellationToken);
            await base.ApplyAdaptersAsync(contextId, adapters, cancellationToken);
        }

        public override async Task<InferenceResult> GenerateAsync(string contextId, string prompt, CancellationToken cancellationToken)
        {
            await Task.Delay(GenerateDelay, cancellationToken);
            return await base.GenerateAsync(contextId, prompt, cancellationToken);
        }
    }

    internal sealed class FaultyRuntime : DeterministicInferenceRuntimeBase
    {
        public const string SecretMessage = "native: /private/models/secret.gguf exploded";

        public bool FailApply { get; set; }
        public bool FailGenerate { get; init; }
        public bool ReportEmptySet { get; init; }
        public AdapterId? ReportInstead { get; set; }
        public int GenerateCalls { get; private set; }

        public override Task ApplyAdaptersAsync(string contextId, IReadOnlySet<AdapterId> adapters, CancellationToken cancellationToken) =>
            FailApply ? throw new InvalidOperationException(SecretMessage) : base.ApplyAdaptersAsync(contextId, adapters, cancellationToken);

        public override async Task<InferenceResult> GenerateAsync(string contextId, string prompt, CancellationToken cancellationToken)
        {
            GenerateCalls++;
            if (FailGenerate)
            {
                throw new InvalidOperationException(SecretMessage);
            }

            InferenceResult real = await base.GenerateAsync(contextId, prompt, cancellationToken);
            if (ReportEmptySet)
            {
                return real with { AppliedAdapters = new HashSet<AdapterId>() };
            }

            return ReportInstead is { } other ? real with { AppliedAdapters = new HashSet<AdapterId> { other } } : real;
        }
    }

    /// <summary>Flags any overlap of two calls on the same context, and widens the window so overlaps would happen.</summary>
    private sealed class ExclusivityCheckingRuntime : DeterministicInferenceRuntimeBase
    {
        private readonly ConcurrentDictionary<string, int> _holders = new();
        private int _active;
        private int _maxConcurrency;
        private int _violations;

        public int Violations => _violations;
        public int MaxConcurrency => _maxConcurrency;

        public override async Task ApplyAdaptersAsync(string contextId, IReadOnlySet<AdapterId> adapters, CancellationToken cancellationToken)
        {
            Enter(contextId);
            try
            {
                await Task.Yield();
                await base.ApplyAdaptersAsync(contextId, adapters, cancellationToken);
            }
            finally
            {
                Exit(contextId);
            }
        }

        public override async Task<InferenceResult> GenerateAsync(string contextId, string prompt, CancellationToken cancellationToken)
        {
            Enter(contextId);
            try
            {
                await Task.Delay(1, cancellationToken);
                return await base.GenerateAsync(contextId, prompt, cancellationToken);
            }
            finally
            {
                Exit(contextId);
            }
        }

        private void Enter(string contextId)
        {
            if (_holders.AddOrUpdate(contextId, 1, (_, n) => n + 1) > 1)
            {
                Interlocked.Increment(ref _violations);
            }

            int active = Interlocked.Increment(ref _active);
            int seen;
            while (active > (seen = _maxConcurrency) && Interlocked.CompareExchange(ref _maxConcurrency, active, seen) != seen)
            {
            }
        }

        private void Exit(string contextId)
        {
            _holders.AddOrUpdate(contextId, 0, (_, n) => n - 1);
            Interlocked.Decrement(ref _active);
        }
    }
}
