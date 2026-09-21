using LoraHotswap.Core;
using LoraHotswap.Infrastructure;
using Xunit.Sdk;

namespace LoraHotswap.Tests;

/// <summary>
/// The identity proof failed. A dedicated type so a sabotage test can tell "the proof
/// rejected this world" apart from any other exception that happened to be thrown.
/// </summary>
internal sealed class IdentityProofFailedException(string message) : XunitException(message);

/// <summary>One served request the proof accepted, with the evidence it rested on.</summary>
/// <param name="Tenant">The tenant that was served.</param>
/// <param name="Response">The response the service returned.</param>
/// <param name="Record">The generation the fake runtime recorded for it.</param>
/// <param name="Verification">Artifact verification of the adapter the response named.</param>
internal sealed record ProvenServe(
    TenantId Tenant, TenantResponse Response, GenerationRecord Record, AdapterVerification Verification);

/// <summary>
/// Story 2.5: the single claim that a tenant's request was served with that tenant's
/// adapter and could not have been served with the other's. Every honest path runs this
/// routine and every sabotage is asserted to fail it, so the honest and sabotage paths
/// cannot drift apart -- there is one place to change when the claim changes.
///
/// The proof rests on two independent legs and never on reading merged weights from a
/// live inference context (AD-5):
///
/// <list type="number">
/// <item>routing -- the response's adapter, the applied set the runtime reported, the
/// applied set the fake runtime <i>recorded</i>, and the text it derived from that set;</item>
/// <item>artifact verification (Story 1.7) -- the adapter the response named verifies
/// two-sidedly as <see cref="VerificationOutcome.Match"/> against its Python PEFT reference.</item>
/// </list>
///
/// The reported set alone is never enough: the always-base-model sabotage reports the
/// right set and still fails, on the text leg.
/// </summary>
internal static class IdentityProof
{
    /// <summary>
    /// Prove the identity claim for every tenant in <paramref name="world"/>, or throw
    /// <see cref="IdentityProofFailedException"/> naming the tenant and both adapter ids.
    /// Serves one request at a time: the routing leg reads the generation the runtime
    /// recorded last. Cross-tenant isolation under interleaving is Story 2.2's 300-request
    /// workload; this proof is about which adapter served, not about concurrency.
    /// </summary>
    public static async Task<IReadOnlyList<ProvenServe>> AssertAsync(ProofWorld world, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(world);

        // Identities are content hashes of two different real fixture adapters. If they
        // collided there would be no routing claim left to test.
        if (world.AliceAdapter.Id == world.BobAdapter.Id)
        {
            // Not an IdentityProofFailedException: a degenerate world is a broken test
            // world, and a sabotage test must never read it as a detected sabotage.
            throw new XunitException(
                $"Identity proof world is vacuous: both tenants map to adapter {world.AliceAdapter.Id}.");
        }

        return
        [
            await ProveAsync(world, ProofWorld.Alice, world.AliceAdapter, world.BobAdapter, cancellationToken),
            await ProveAsync(world, ProofWorld.Bob, world.BobAdapter, world.AliceAdapter, cancellationToken),
        ];
    }

    private static async Task<ProvenServe> ProveAsync(
        ProofWorld world, TenantId tenant, AdmittedAdapter expected, AdmittedAdapter other, CancellationToken cancellationToken)
    {
        IdentityProofFailedException Failed(string why) => new(
            $"Identity proof failed for tenant '{tenant}' (its adapter is {expected.Id}, " +
            $"the other tenant's adapter is {other.Id}): {why}.");

        string prompt = $"identity-proof:{tenant}";
        int before = world.Runtime.Generations.Count;

        TenantResponse response;
        try
        {
            response = await world.Service.ServeAsync(new TenantRequest(tenant, prompt), cancellationToken);
        }
        catch (RequestProblemException problem)
        {
            // A tenant that cannot be served is a failed proof, never a skipped tenant.
            throw Failed($"the request failed closed with '{problem.Problem.Type}' ({problem.Problem.Detail}) " +
                         "instead of being served with its adapter");
        }

        // Leg 1a -- the response answers this tenant, names its adapter, and only it.
        if (response.Tenant != tenant)
        {
            throw Failed($"the response names tenant '{response.Tenant}'");
        }

        // Cross-tenant exclusion first, so a leaked identity is reported as the leak it is
        // rather than as a count mismatch by the stricter checks below.
        if (response.AppliedAdapters.Contains(other.Id) || response.Text.Contains(other.Id.Value, StringComparison.Ordinal))
        {
            throw Failed("the response carries the other tenant's adapter");
        }

        if (response.Adapter != expected.Id)
        {
            throw Failed($"the response names adapter {response.Adapter}");
        }

        if (response.AppliedAdapters.Count != 1 || !response.AppliedAdapters.Contains(expected.Id))
        {
            throw Failed($"the reported applied set is {Describe(response.AppliedAdapters)}");
        }

        // Leg 1b -- the generation the runtime recorded applied exactly that adapter. This
        // is the routing evidence the runtime wrote down, not the set it handed back.
        IReadOnlyList<GenerationRecord> recorded = world.Runtime.Generations;
        if (recorded.Count != before + 1)
        {
            throw Failed($"the runtime recorded {recorded.Count - before} generations for one request");
        }

        GenerationRecord record = recorded[^1];
        if (record.AppliedAdapters.Contains(other.Id))
        {
            throw Failed($"the recorded generation on context '{record.ContextId}' applied the other tenant's adapter");
        }

        if (record.AppliedAdapters.Count != 1 || !record.AppliedAdapters.Contains(expected.Id))
        {
            throw Failed($"the recorded generation on context '{record.ContextId}' applied {Describe(record.AppliedAdapters)}");
        }

        // Leg 1c -- the text derives from that adapter. Without this, a runtime that
        // reports the right set while generating from the base model passes unnoticed.
        string derived = $"fake:{expected.Id.Value}:{prompt}";
        if (!string.Equals(response.Text, derived, StringComparison.Ordinal))
        {
            throw Failed($"the generated text is '{response.Text}', not the text that adapter derives ('{derived}')");
        }

        // Leg 2 -- artifact verification of the adapter the response named (Story 1.7),
        // offline against its Python PEFT reference. No live context is inspected.
        AdmittedAdapter served = world.Registry.Find(response.Adapter)
            ?? throw Failed($"the served adapter {response.Adapter} is not an admitted adapter");
        AdapterVerification verification;
        try
        {
            verification = await world.Verifier.VerifyAsync(served, cancellationToken);
        }
        catch (RequestProblemException problem)
        {
            throw Failed($"artifact verification of the served adapter raised '{problem.Problem.Type}' ({problem.Problem.Detail})");
        }

        if (verification.Outcome != VerificationOutcome.Match)
        {
            throw Failed($"artifact verification of the served adapter is '{verification.Outcome}': {verification.Detail}");
        }

        return new ProvenServe(tenant, response, record, verification);
    }

    private static string Describe(IReadOnlySet<AdapterId> adapters) =>
        adapters.Count == 0 ? "(none)" : string.Join(", ", adapters.Select(a => a.Value).Order(StringComparer.Ordinal));
}

/// <summary>
/// A two-tenant world over the real merge fixtures: Alice serves <c>scaling-4</c> and Bob
/// serves <c>scaling-1</c>, so the two identities are genuinely different content hashes
/// and both have a PEFT reference the artifact leg can verify against. Tenant identities
/// and the runtime decorator base are <see cref="TenantServingTests"/>'s.
/// </summary>
internal sealed class ProofWorld
{
    /// <summary>The tenant served by the <c>scaling-4</c> fixture.</summary>
    public static TenantId Alice => TenantServingTests.World.Alice;

    /// <summary>The tenant served by the <c>scaling-1</c> fixture.</summary>
    public static TenantId Bob => TenantServingTests.World.Bob;

    /// <summary>Alice's adapter, identified by the content hash of its source bytes (AD-1).</summary>
    public static AdmittedAdapter AliceFixture { get; } = TestFixtures.AdapterAt(TestFixtures.ScalingFour);

    /// <summary>Bob's adapter, identified by the content hash of its source bytes (AD-1).</summary>
    public static AdmittedAdapter BobFixture { get; } = TestFixtures.AdapterAt(TestFixtures.ScalingOne);

    public required AdmittedAdapter AliceAdapter { get; init; }

    public required AdmittedAdapter BobAdapter { get; init; }

    public required AdapterRegistry Registry { get; init; }

    public required TenantServingService Service { get; init; }

    public required DeterministicInferenceRuntime Runtime { get; init; }

    public required IAdapterVerifier Verifier { get; init; }

    /// <summary>
    /// Compose the world under <paramref name="strategyName"/>. <paramref name="runtime"/>
    /// is the seam a sabotage is injected at -- exactly where a real routing bug would sit.
    /// </summary>
    public static async Task<ProofWorld> CreateAsync(
        string strategyName,
        TenantServingTests.DeterministicInferenceRuntimeBase? runtime = null,
        CancellationToken cancellationToken = default,
        string? aliceDirectory = null)
    {
        runtime ??= new TenantServingTests.DeterministicInferenceRuntimeBase();
        AdmittedAdapter alice = AliceFixture, bob = BobFixture;
        AdapterRegistry registry = AdapterRegistry.Build([alice, bob], new Dictionary<TenantId, AdapterId>
        {
            [Alice] = alice.Id,
            [Bob] = bob.Id,
        }).Registry!;

        IContextBindingStrategy strategy = ContextBindingStrategies.Create(
            strategyName, runtime, poolSize: 4, leaseTimeout: TimeSpan.FromSeconds(5));
        await strategy.InitializeAsync(registry, cancellationToken);

        return new ProofWorld
        {
            AliceAdapter = alice,
            BobAdapter = bob,
            Registry = registry,
            Service = new TenantServingService(registry, runtime, strategy, TestFixtures.ConfiguredBase),
            Runtime = runtime.Inner,
            // Storage locations locate files only; identity stays the content AdapterId.
            Verifier = new SafeTensorsAdapterVerifier(new Dictionary<AdapterId, string>
            {
                [alice.Id] = aliceDirectory ?? TestFixtures.ScalingFour,
                [bob.Id] = TestFixtures.ScalingOne,
            }),
        };
    }
}

/// <summary>
/// Story 2.5 (SM-1): the identity proof runs unchanged under both binding strategies, and
/// the three sabotages -- always-one-adapter, no-adapter, always-base-model -- each turn it
/// red. Every sabotage is planted at the runtime seam and nowhere else; no production
/// behaviour changes in this story and no existing assertion is weakened.
///
/// A sabotage test fails with the sabotage's own name and strategy, so a proof that stops
/// detecting one says which one.
///
/// Which leg each sabotage pins, measured by neutralizing legs one at a time:
/// <list type="bullet">
/// <item><c>leftover-adapter</c> pins the recorded applied set (leg 1b);</item>
/// <item><c>always-base-model</c> pins the text leg (leg 1c);</item>
/// <item><c>drifted-artifact</c> pins artifact verification (leg 2).</item>
/// </list>
///
/// The response's own adapter, reported set and tenant (leg 1a) are deliberately redundant:
/// <see cref="TenantServingService"/> rejects a wrong reported set before the proof ever sees
/// it, and <c>always-one-adapter</c> / <c>no-adapter</c> are caught on that path. They stay as
/// an independent claim that survives if that production check is ever weakened -- verified by
/// weakening it, which leaves every sabotage still caught.
/// </summary>
public sealed class IdentityProofTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [MemberData(nameof(TenantServingTests.Strategies), MemberType = typeof(TenantServingTests))]
    public async Task The_identity_proof_holds_for_every_tenant_under_both_strategies(string strategy)
    {
        ProofWorld world = await ProofWorld.CreateAsync(strategy, cancellationToken: Ct);

        IReadOnlyList<ProvenServe> proven = await IdentityProof.AssertAsync(world, Ct);

        // Both tenants proven, not just the first: a routine that quietly stopped proving
        // Bob would otherwise stay green.
        Assert.Equal(2, proven.Count);
        Assert.Contains(proven, p => p.Tenant == ProofWorld.Bob && p.Response.Adapter == world.BobAdapter.Id);

        // The identities the proof compares are the ones admission derives, not a test-local
        // shortcut: same fixture bytes, same content hash.
        Assert.Equal((await TestFixtures.AdmitAsync(TestFixtures.ScalingFour)).Id, world.AliceAdapter.Id);
        Assert.Equal((await TestFixtures.AdmitAsync(TestFixtures.ScalingOne)).Id, world.BobAdapter.Id);

        ProvenServe alice = Assert.Single(proven, p => p.Tenant == ProofWorld.Alice);
        Assert.Equal([world.AliceAdapter.Id], alice.Response.AppliedAdapters);
        Assert.Equal([world.AliceAdapter.Id], alice.Record.AppliedAdapters);
        Assert.DoesNotContain(world.BobAdapter.Id, alice.Response.AppliedAdapters);
        Assert.DoesNotContain(world.BobAdapter.Id, alice.Record.AppliedAdapters);
        Assert.Equal(strategy, alice.Response.BindingStrategy);
        Assert.All(proven, p => Assert.Equal(VerificationOutcome.Match, p.Verification.Outcome));
    }

    [Theory]
    [MemberData(nameof(TenantServingTests.Strategies), MemberType = typeof(TenantServingTests))]
    public async Task Sabotage_always_one_adapter_is_caught(string strategy)
    {
        // Planted: the runtime applies Alice's adapter whatever it is asked to apply.
        var runtime = new AlwaysOneAdapterRuntime(ProofWorld.AliceFixture.Id);
        ProofWorld world = await ProofWorld.CreateAsync(strategy, runtime, Ct);

        string failure = await AssertProofFailsAsync("always-one-adapter", world, strategy);

        // Both ids appear in every failure prefix, so assert the mechanism instead: Bob's
        // request must be rejected over the applied set, not merely fail for some reason.
        Assert.Contains($"tenant '{ProofWorld.Bob}'", failure, StringComparison.Ordinal);
        Assert.True(
            failure.Contains(ProblemTypes.AdapterNotApplied, StringComparison.Ordinal)
            || failure.Contains("applied set", StringComparison.Ordinal)
            || failure.Contains("applied", StringComparison.Ordinal),
            $"The failure does not name the applied set that gives the sabotage away: {failure}");
    }

    [Theory]
    [MemberData(nameof(TenantServingTests.Strategies), MemberType = typeof(TenantServingTests))]
    public async Task Sabotage_no_adapter_is_caught(string strategy)
    {
        // Planted: the runtime applies nothing, so it records an empty applied set.
        ProofWorld world = await ProofWorld.CreateAsync(strategy, new NoAdapterRuntime(), Ct);

        string failure = await AssertProofFailsAsync("no-adapter", world, strategy);

        // Either mechanism is a pass: the service fails the request closed with
        // adapter-not-applied, or the proof's own routing legs see the empty applied set.
        // Asserting only the first would make this test green if the proof stopped looking.
        Assert.Contains($"tenant '{ProofWorld.Alice}'", failure, StringComparison.Ordinal);
        Assert.True(
            failure.Contains(ProblemTypes.AdapterNotApplied, StringComparison.Ordinal)
            || failure.Contains("(none)", StringComparison.Ordinal),
            $"The failure names neither the fail-closed problem type nor the empty applied set: {failure}");
    }

    [Theory]
    [MemberData(nameof(TenantServingTests.Strategies), MemberType = typeof(TenantServingTests))]
    public async Task Sabotage_always_base_model_is_caught(string strategy)
    {
        // Planted: the runtime reports the right applied set but generates as if nothing
        // were applied. Only the text leg can see this.
        ProofWorld world = await ProofWorld.CreateAsync(strategy, new AlwaysBaseModelRuntime(), Ct);

        string failure = await AssertProofFailsAsync("always-base-model", world, strategy);

        Assert.Contains("the generated text is", failure, StringComparison.Ordinal);
        Assert.Contains($"tenant '{ProofWorld.Alice}'", failure, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(TenantServingTests.Strategies), MemberType = typeof(TenantServingTests))]
    public async Task Sabotage_lying_report_is_caught(string strategy)
    {
        // Planted: the runtime applies the wrong adapter but reports the requested one.
        // Production trusts the reported set, so only the proof's recorded-routing leg
        // can see this -- the bug shape the proof exists to be a backstop for.
        ProofWorld world = await ProofWorld.CreateAsync(strategy, new LyingReportRuntime(ProofWorld.AliceFixture.Id), Ct);

        string failure = await AssertProofFailsAsync("lying-report", world, strategy);

        Assert.Contains("recorded generation", failure, StringComparison.Ordinal);
        Assert.Contains($"tenant '{ProofWorld.Bob}'", failure, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(TenantServingTests.Strategies), MemberType = typeof(TenantServingTests))]
    public async Task Sabotage_leftover_adapter_is_caught(string strategy)
    {
        // Planted: applying leaves a previously applied adapter in place -- the classic
        // context-reuse leak. The report is honest, so production sees nothing wrong.
        ProofWorld world = await ProofWorld.CreateAsync(strategy, new LeftoverAdapterRuntime(), Ct);

        string failure = await AssertProofFailsAsync("leftover-adapter", world, strategy);

        Assert.Contains("recorded generation", failure, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sabotage_drifted_artifact_is_caught()
    {
        // Planted: routing is honest, but the served adapter's reference no longer agrees
        // with its weights. Only the artifact leg can see this.
        using TempDirectory drifted = TestFixtures.Copy(TestFixtures.ScalingFour);
        TestFixtures.RewriteF32(Path.Combine(drifted.Path, SafeTensorsAdapterVerifier.ReferenceFileName),
            n => n.EndsWith(".w_merged", StringComparison.Ordinal), v => v + 1e-3f);
        ProofWorld world = await ProofWorld.CreateAsync(
            PerRequestApplyStrategy.StrategyName, cancellationToken: Ct, aliceDirectory: drifted.Path);

        string failure = await AssertProofFailsAsync("drifted-artifact", world, PerRequestApplyStrategy.StrategyName);

        Assert.Contains("artifact verification", failure, StringComparison.Ordinal);
        Assert.Contains($"tenant '{ProofWorld.Alice}'", failure, StringComparison.Ordinal);
    }

    /// <summary>
    /// Run the proof and return the failure it raised. Passing is the failure condition
    /// here: the message names the sabotage that went undetected.
    /// </summary>
    private static async Task<string> AssertProofFailsAsync(string sabotage, ProofWorld world, string strategy)
    {
        try
        {
            await IdentityProof.AssertAsync(world, Ct);
        }
        catch (IdentityProofFailedException failure)
        {
            // The sabotage's name travels with the message, so a red run says which
            // sabotage and which strategy produced it.
            return $"[{sabotage} | {strategy}] {failure.Message}";
        }

        throw new XunitException(
            $"The identity proof passed against the '{sabotage}' sabotage under {strategy}: " +
            "it no longer detects that sabotage, so SM-1 (PRD 7) is unmet.");
    }

    // --- sabotages, each injected at the runtime seam and nowhere else -------------------

    /// <summary>Applies one fixed adapter whatever it is asked to apply.</summary>
    private sealed class AlwaysOneAdapterRuntime(AdapterId always) : TenantServingTests.DeterministicInferenceRuntimeBase
    {
        public override Task ApplyAdaptersAsync(string contextId, IReadOnlySet<AdapterId> adapters, CancellationToken cancellationToken) =>
            base.ApplyAdaptersAsync(contextId, new HashSet<AdapterId> { always }, cancellationToken);
    }

    /// <summary>
    /// Applies one fixed adapter but reports the adapter that was requested. Production
    /// trusts the reported set, so nothing outside the proof's recorded-routing leg notices.
    /// </summary>
    private sealed class LyingReportRuntime(AdapterId always) : TenantServingTests.DeterministicInferenceRuntimeBase
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, IReadOnlySet<AdapterId>> _requested = new(StringComparer.Ordinal);

        public override Task ApplyAdaptersAsync(string contextId, IReadOnlySet<AdapterId> adapters, CancellationToken cancellationToken)
        {
            _requested[contextId] = adapters;
            return base.ApplyAdaptersAsync(contextId, new HashSet<AdapterId> { always }, cancellationToken);
        }

        public override async Task<InferenceResult> GenerateAsync(string contextId, string prompt, CancellationToken cancellationToken)
        {
            InferenceResult honest = await base.GenerateAsync(contextId, prompt, cancellationToken);
            IReadOnlySet<AdapterId> claimed = _requested.GetValueOrDefault(contextId, honest.AppliedAdapters);
            string text = $"fake:{string.Join(",", claimed.Select(a => a.Value).Order(StringComparer.Ordinal))}:{prompt}";
            return honest with { AppliedAdapters = claimed, Text = text };
        }
    }

    /// <summary>
    /// Leaves a stale adapter applied alongside the requested one, and reports only the
    /// requested one. Production trusts that report, so only the recorded applied set --
    /// which is two adapters wide -- gives the leak away.
    /// </summary>
    private sealed class LeftoverAdapterRuntime : TenantServingTests.DeterministicInferenceRuntimeBase
    {
        private static readonly AdapterId Stale = new("sha256:" + new string('7', 64));

        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, IReadOnlySet<AdapterId>> _requested = new(StringComparer.Ordinal);

        public override Task ApplyAdaptersAsync(string contextId, IReadOnlySet<AdapterId> adapters, CancellationToken cancellationToken)
        {
            _requested[contextId] = adapters;
            return base.ApplyAdaptersAsync(contextId, new HashSet<AdapterId>(adapters) { Stale }, cancellationToken);
        }

        public override async Task<InferenceResult> GenerateAsync(string contextId, string prompt, CancellationToken cancellationToken)
        {
            InferenceResult honest = await base.GenerateAsync(contextId, prompt, cancellationToken);
            return _requested.TryGetValue(contextId, out IReadOnlySet<AdapterId>? requested)
                ? honest with { AppliedAdapters = requested }
                : honest;
        }
    }

    /// <summary>Never applies anything, so every context generates with an empty applied set.</summary>
    private sealed class NoAdapterRuntime : TenantServingTests.DeterministicInferenceRuntimeBase
    {
        public override Task ApplyAdaptersAsync(string contextId, IReadOnlySet<AdapterId> adapters, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    /// <summary>
    /// Applies and reports honestly, but produces the text the fake runtime emits when no
    /// adapter is applied -- the base model answering under the right label.
    /// </summary>
    private sealed class AlwaysBaseModelRuntime : TenantServingTests.DeterministicInferenceRuntimeBase
    {
        private readonly DeterministicInferenceRuntime _baseline = new();

        public override async Task<InferenceResult> GenerateAsync(string contextId, string prompt, CancellationToken cancellationToken)
        {
            InferenceResult honest = await base.GenerateAsync(contextId, prompt, cancellationToken);

            // Text from a throwaway runtime whose context has nothing applied: the fake's
            // real base-model output, not a second copy of its format. Generating it on the
            // world's own runtime would add a record and trip the recorded-routing leg first.
            InferenceResult unapplied = await _baseline.GenerateAsync(contextId, prompt, cancellationToken);
            return honest with { Text = unapplied.Text };
        }
    }
}
