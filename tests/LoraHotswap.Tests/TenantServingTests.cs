using System.Collections.Concurrent;
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
