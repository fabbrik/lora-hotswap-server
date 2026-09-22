using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LoraHotswap.Api;
using LoraHotswap.Core;
using LoraHotswap.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LoraHotswap.Tests;

/// <summary>
/// Story 2.7: the serving pipeline is a trace. One <c>serve.request</c> span per request
/// with one span per stage under it, every span carrying <c>tenant.id</c>,
/// <c>adapter.id</c> and <c>binding.strategy</c>, the stage that rejected a request
/// carrying the error and the problem type, and every error body carrying the trace id.
///
/// The <see cref="SpanCapture"/> listener is process-wide and other classes serve in
/// parallel, so every test here serves a <b>uniquely named tenant</b> and filters on it.
/// </summary>
public sealed class ServingTracingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The four stage spans, in pipeline order.</summary>
    private static readonly string[] StageSpans =
    [
        TenantServingService.ResolveSpanName,
        TenantServingService.LeaseSpanName,
        ContextBindingStrategies.ApplySpanName,
        TenantServingService.GenerateSpanName,
    ];

    // --- a served request is one trace with four stage spans ----------------------------

    [Theory]
    [MemberData(nameof(TenantServingTests.Strategies), MemberType = typeof(TenantServingTests))]
    public async Task Served_request_is_one_trace_of_a_request_span_and_four_stage_spans(string strategy)
    {
        var world = await TracedWorld.CreateAsync(strategy);
        using var capture = new SpanCapture();

        await world.Service.ServeAsync(new TenantRequest(world.Tenant, "hello"), Ct);

        IReadOnlyList<Activity> spans = capture.ForTenant(world.Tenant.Value);
        Assert.Equal(
            new[] { TenantServingService.RequestSpanName }.Concat(StageSpans).Order(StringComparer.Ordinal),
            spans.Select(s => s.OperationName).Order(StringComparer.Ordinal));

        Activity request = Single(spans, TenantServingService.RequestSpanName);
        Assert.All(spans, s => Assert.Equal(request.TraceId, s.TraceId));

        // The stages the pipeline owns hang directly off the request; the application stage
        // is emitted from inside the lease, where the work is, so it hangs off the lease --
        // one trace either way, with no stage outside it.
        Assert.All(new[] { TenantServingService.ResolveSpanName, TenantServingService.LeaseSpanName, TenantServingService.GenerateSpanName },
            name => Assert.Equal(request.SpanId, Single(spans, name).ParentSpanId));
        Assert.Equal(Single(spans, TenantServingService.LeaseSpanName).SpanId,
            Single(spans, ContextBindingStrategies.ApplySpanName).ParentSpanId);
        Assert.All(spans, s => Assert.Same(request, Root(s, spans)));
    }

    [Theory]
    [MemberData(nameof(TenantServingTests.Strategies), MemberType = typeof(TenantServingTests))]
    public async Task Every_span_carries_the_tenant_the_adapter_and_the_binding_strategy(string strategy)
    {
        var world = await TracedWorld.CreateAsync(strategy);
        using var capture = new SpanCapture();

        await world.Service.ServeAsync(new TenantRequest(world.Tenant, "hello"), Ct);

        IReadOnlyList<Activity> spans = capture.ForTenant(world.Tenant.Value);
        Assert.Equal(5, spans.Count);
        Assert.All(spans, s =>
        {
            Assert.Equal(world.Tenant.Value, s.GetTagItem(LoraHotswapTelemetry.TenantTag));
            // The identity is known by the time every span ends -- never a path, never absent.
            Assert.Equal(world.Adapter.Id.Value, s.GetTagItem(LoraHotswapTelemetry.AdapterTag));
            Assert.Equal(strategy, s.GetTagItem(LoraHotswapTelemetry.BindingStrategyTag));
            Assert.Equal(ActivityStatusCode.Unset, s.Status);
        });
    }

    [Theory]
    [InlineData(PerRequestApplyStrategy.StrategyName, ContextBindingStrategies.AppliedAtRequest)]
    [InlineData(PerTenantResidentStrategy.StrategyName, ContextBindingStrategies.AppliedAtStartup)]
    public async Task The_application_stage_has_a_span_under_both_strategies_saying_when_the_work_happened(
        string strategy, string appliedAt)
    {
        var world = await TracedWorld.CreateAsync(strategy);
        using var capture = new SpanCapture();

        await world.Service.ServeAsync(new TenantRequest(world.Tenant, "hello"), Ct);

        // PerTenantResident does no application work at request time; the span exists anyway
        // and says so, rather than leaving a hole where a stage should be.
        Activity apply = Single(capture.ForTenant(world.Tenant.Value), ContextBindingStrategies.ApplySpanName);
        Assert.Equal(appliedAt, apply.GetTagItem(ContextBindingStrategies.AppliedAtTag));
    }

    // --- every fail-closed rejection names the stage that rejected it --------------------

    [Theory]
    [MemberData(nameof(TenantServingTests.Strategies), MemberType = typeof(TenantServingTests))]
    public async Task Unknown_tenant_is_recorded_on_the_resolution_span_and_no_later_stage_starts(string strategy)
    {
        var world = await TracedWorld.CreateAsync(strategy);
        var unknown = new TenantId(TracedWorld.Unique("mallory"));
        using var capture = new SpanCapture();

        await Assert.ThrowsAsync<RequestProblemException>(() =>
            world.Service.ServeAsync(new TenantRequest(unknown, "hello"), Ct));

        IReadOnlyList<Activity> spans = capture.ForTenant(unknown.Value);
        Assert.Equal([TenantServingService.RequestSpanName, TenantServingService.ResolveSpanName],
            spans.Select(s => s.OperationName).Order(StringComparer.Ordinal));

        Activity resolve = Single(spans, TenantServingService.ResolveSpanName);
        AssertRejected(resolve, ProblemTypes.UnknownTenant);
        // No tenant means no adapter: the tag is present and says so, rather than being absent.
        Assert.Equal(LoraHotswapTelemetry.UnidentifiedAdapter, resolve.GetTagItem(LoraHotswapTelemetry.AdapterTag));
        Assert.Equal(strategy, resolve.GetTagItem(LoraHotswapTelemetry.BindingStrategyTag));
        AssertRejected(Single(spans, TenantServingService.RequestSpanName), ProblemTypes.UnknownTenant);
    }

    [Theory]
    [MemberData(nameof(TenantServingTests.Strategies), MemberType = typeof(TenantServingTests))]
    public async Task Base_model_mismatch_is_recorded_on_the_resolution_span(string strategy)
    {
        var world = await TracedWorld.CreateAsync(strategy, servedBase: "unsloth/Qwen3-0.6B");
        using var capture = new SpanCapture();

        await Assert.ThrowsAsync<RequestProblemException>(() =>
            world.Service.ServeAsync(new TenantRequest(world.Tenant, "hello"), Ct));

        IReadOnlyList<Activity> spans = capture.ForTenant(world.Tenant.Value);
        Activity resolve = Single(spans, TenantServingService.ResolveSpanName);
        AssertRejected(resolve, ProblemTypes.BaseModelMismatch);
        // Resolution got far enough to know the identity, so the span stopped saying unidentified.
        Assert.Equal(world.Adapter.Id.Value, resolve.GetTagItem(LoraHotswapTelemetry.AdapterTag));
        Assert.DoesNotContain(spans, s => s.OperationName == TenantServingService.GenerateSpanName);
    }

    [Theory]
    [MemberData(nameof(TenantServingTests.Strategies), MemberType = typeof(TenantServingTests))]
    public async Task Unavailable_lease_is_recorded_on_the_lease_span_and_generation_never_starts(string strategy)
    {
        var world = await TracedWorld.CreateAsync(strategy, poolSize: 1, leaseTimeout: TimeSpan.FromMilliseconds(50));
        await using ContextLease held = await world.Strategy.LeaseAsync(world.Tenant, world.Adapter, Ct);
        using var capture = new SpanCapture();

        await Assert.ThrowsAsync<RequestProblemException>(() =>
            world.Service.ServeAsync(new TenantRequest(world.Tenant, "hello"), Ct));

        IReadOnlyList<Activity> spans = capture.ForTenant(world.Tenant.Value);
        // Resolution succeeded, the lease did not, and no stage after it ever started --
        // no application span and, above all, no generation.
        Assert.Equal(
            [TenantServingService.LeaseSpanName, TenantServingService.RequestSpanName, TenantServingService.ResolveSpanName],
            spans.Select(s => s.OperationName).Order(StringComparer.Ordinal));
        AssertRejected(Single(spans, TenantServingService.LeaseSpanName), ProblemTypes.LeaseUnavailable);
        AssertRejected(Single(spans, TenantServingService.RequestSpanName), ProblemTypes.LeaseUnavailable);
    }

    [Theory]
    [MemberData(nameof(TenantServingTests.Strategies), MemberType = typeof(TenantServingTests))]
    public async Task A_foreign_applied_set_is_recorded_on_the_generation_stage_and_the_request_span(string strategy)
    {
        var runtime = new TenantServingTests.FaultyRuntime();
        var world = await TracedWorld.CreateAsync(strategy, runtime);
        runtime.ReportInstead = new AdapterId(TracedWorld.UniqueAdapterId());
        using var capture = new SpanCapture();

        await Assert.ThrowsAsync<RequestProblemException>(() =>
            world.Service.ServeAsync(new TenantRequest(world.Tenant, "hello"), Ct));

        IReadOnlyList<Activity> spans = capture.ForTenant(world.Tenant.Value);
        AssertRejected(Single(spans, TenantServingService.GenerateSpanName), ProblemTypes.AdapterNotApplied);
        AssertRejected(Single(spans, TenantServingService.RequestSpanName), ProblemTypes.AdapterNotApplied);
    }

    [Theory]
    [MemberData(nameof(TenantServingTests.Strategies), MemberType = typeof(TenantServingTests))]
    public async Task An_empty_applied_set_is_recorded_on_the_generation_stage(string strategy)
    {
        var world = await TracedWorld.CreateAsync(strategy, new TenantServingTests.FaultyRuntime { ReportEmptySet = true });
        using var capture = new SpanCapture();

        await Assert.ThrowsAsync<RequestProblemException>(() =>
            world.Service.ServeAsync(new TenantRequest(world.Tenant, "hello"), Ct));

        AssertRejected(Single(capture.ForTenant(world.Tenant.Value), TenantServingService.GenerateSpanName),
            ProblemTypes.AdapterNotApplied);
    }

    [Theory]
    [MemberData(nameof(TenantServingTests.Strategies), MemberType = typeof(TenantServingTests))]
    public async Task A_generation_fault_is_recorded_on_the_generation_span(string strategy)
    {
        var world = await TracedWorld.CreateAsync(strategy, new TenantServingTests.FaultyRuntime { FailGenerate = true });
        using var capture = new SpanCapture();

        await Assert.ThrowsAsync<RequestProblemException>(() =>
            world.Service.ServeAsync(new TenantRequest(world.Tenant, "hello"), Ct));

        AssertRejected(Single(capture.ForTenant(world.Tenant.Value), TenantServingService.GenerateSpanName),
            ProblemTypes.GenerationFailed);
    }

    [Fact]
    public async Task An_application_fault_is_recorded_on_the_application_span_without_leaking_internals()
    {
        var world = await TracedWorld.CreateAsync(PerRequestApplyStrategy.StrategyName,
            new TenantServingTests.FaultyRuntime { FailApply = true });
        using var capture = new SpanCapture();

        await Assert.ThrowsAsync<RequestProblemException>(() =>
            world.Service.ServeAsync(new TenantRequest(world.Tenant, "hello"), Ct));

        IReadOnlyList<Activity> spans = capture.ForTenant(world.Tenant.Value);
        AssertRejected(Single(spans, ContextBindingStrategies.ApplySpanName), ProblemTypes.AdapterNotApplied);
        Assert.DoesNotContain(spans, s => s.OperationName == TenantServingService.GenerateSpanName);

        // One rejection, one rejecting stage (SM-5): the enclosing lease span must not
        // re-record a problem its child already owns, or the trace names two rejecting stages.
        Activity lease = Single(spans, TenantServingService.LeaseSpanName);
        Assert.Null(lease.GetTagItem(LoraHotswapTelemetry.ProblemTypeTag));

        // The root still says the request failed, and with which problem.
        AssertRejected(Single(spans, TenantServingService.RequestSpanName), ProblemTypes.AdapterNotApplied);

        // Never a filesystem path and never internal exception text -- on a tag or a status.
        Assert.All(spans, s =>
        {
            Assert.DoesNotContain(TenantServingTests.FaultyRuntime.SecretMessage, s.StatusDescription ?? "", StringComparison.Ordinal);
            Assert.All(s.Tags, t => Assert.DoesNotContain(
                TenantServingTests.FaultyRuntime.SecretMessage, t.Value ?? "", StringComparison.Ordinal));
        });
    }

    [Fact]
    public async Task An_unexpected_strategy_fault_is_recorded_on_the_lease_span_as_internal_error()
    {
        var world = await TracedWorld.CreateAsync(PerRequestApplyStrategy.StrategyName);
        var service = new TenantServingService(world.Registry, new DeterministicInferenceRuntime(),
            new ThrowingStrategy(), TestFixtures.ConfiguredBase);
        using var capture = new SpanCapture();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ServeAsync(new TenantRequest(world.Tenant, "hello"), Ct));

        IReadOnlyList<Activity> spans = capture.ForTenant(world.Tenant.Value);
        Activity lease = Single(spans, TenantServingService.LeaseSpanName);
        AssertRejected(lease, ProblemTypes.InternalError);
        Assert.All(spans, s => Assert.DoesNotContain(
            ThrowingStrategy.SecretMessage, s.StatusDescription ?? "", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_cancelled_request_is_not_a_trace_that_reads_as_successful()
    {
        var world = await TracedWorld.CreateAsync(PerRequestApplyStrategy.StrategyName,
            new TenantServingTests.SlowRuntime { GenerateDelay = TimeSpan.FromSeconds(5) });
        using var capture = new SpanCapture();
        using var caller = new CancellationTokenSource();

        Task serving = world.Service.ServeAsync(new TenantRequest(world.Tenant, "hello"), caller.Token);
        await Task.Delay(TimeSpan.FromMilliseconds(50), Ct);
        await caller.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => serving);

        // Unset status on every span would make an aborted request indistinguishable from a
        // served one; the stage that was running says it was cancelled, and so does the root.
        IReadOnlyList<Activity> spans = capture.ForTenant(world.Tenant.Value);
        Activity generate = Single(spans, TenantServingService.GenerateSpanName);
        Assert.Equal(ActivityStatusCode.Error, generate.Status);
        Assert.Equal(TenantServingService.CancelledStatus, generate.StatusDescription);
        Activity request = Single(spans, TenantServingService.RequestSpanName);
        Assert.Equal(ActivityStatusCode.Error, request.Status);
        Assert.Equal(TenantServingService.CancelledStatus, request.StatusDescription);

        // A cancellation is not a typed rejection: no problem type is invented for it.
        Assert.Null(request.GetTagItem(LoraHotswapTelemetry.ProblemTypeTag));
    }

    [Theory]
    [MemberData(nameof(TenantServingTests.Strategies), MemberType = typeof(TenantServingTests))]
    public async Task The_emitted_span_vocabulary_is_exactly_what_the_README_documents(string strategy)
    {
        // The span names and tag names live on three types (LoraHotswapTelemetry,
        // TenantServingService, ContextBindingStrategies) and are documented in one README
        // table. This is the guard that keeps the two from drifting apart.
        var world = await TracedWorld.CreateAsync(strategy);
        using var capture = new SpanCapture();

        await world.Service.ServeAsync(new TenantRequest(world.Tenant, "hello"), Ct);

        IReadOnlyList<Activity> spans = capture.ForTenant(world.Tenant.Value);
        Assert.Equal(
            [
                ContextBindingStrategies.ApplySpanName,
                TenantServingService.GenerateSpanName,
                TenantServingService.LeaseSpanName,
                TenantServingService.RequestSpanName,
                TenantServingService.ResolveSpanName,
            ],
            spans.Select(s => s.OperationName).Order(StringComparer.Ordinal).ToArray());

        Assert.All(spans, span =>
        {
            Assert.Equal(world.Tenant.Value, span.GetTagItem(LoraHotswapTelemetry.TenantTag));
            Assert.Equal(world.Adapter.Id.Value, span.GetTagItem(LoraHotswapTelemetry.AdapterTag));
            Assert.Equal(strategy, span.GetTagItem(LoraHotswapTelemetry.BindingStrategyTag));
        });

        Activity apply = Single(spans, ContextBindingStrategies.ApplySpanName);
        Assert.Equal(
            strategy == PerRequestApplyStrategy.StrategyName
                ? ContextBindingStrategies.AppliedAtRequest
                : ContextBindingStrategies.AppliedAtStartup,
            apply.GetTagItem(ContextBindingStrategies.AppliedAtTag));
    }

    // --- timings are unaffected by tracing -----------------------------------------------

    [Theory]
    [MemberData(nameof(TenantServingTests.Strategies), MemberType = typeof(TenantServingTests))]
    public async Task Timings_are_still_measured_by_the_pipeline_while_the_spans_are_being_recorded(string strategy)
    {
        var runtime = new TenantServingTests.SlowRuntime
        {
            ApplyDelay = TimeSpan.FromMilliseconds(15),
            GenerateDelay = TimeSpan.FromMilliseconds(15),
        };
        var world = await TracedWorld.CreateAsync(strategy, runtime);
        using var capture = new SpanCapture();

        TenantResponse response = await world.Service.ServeAsync(new TenantRequest(world.Tenant, "hello"), Ct);

        // The spans exist, and the stages are still measured by the pipeline: the generation
        // figure carries the runtime's 15 ms, and the stages that did no waiting stay small.
        Assert.Equal(5, capture.ForTenant(world.Tenant.Value).Count);
        Assert.True(response.Timings.GenerationMicroseconds > 10_000,
            $"Generation reported {response.Timings.GenerationMicroseconds} us for a 15 ms runtime call.");
        Assert.True(response.Timings.AdapterResolutionMicroseconds < 25_000,
            $"Resolution reported {response.Timings.AdapterResolutionMicroseconds} us: starting a span is inside a stage's clock.");
        Assert.True(response.Timings.ContextAcquisitionMicroseconds < 25_000,
            $"Acquisition reported {response.Timings.ContextAcquisitionMicroseconds} us: starting a span is inside a stage's clock.");
        Assert.Equal(strategy == PerRequestApplyStrategy.StrategyName,
            response.Timings.AdapterApplicationMicroseconds > 10_000);
    }

    // --- harness ---------------------------------------------------------------------------

    private static Activity Single(IEnumerable<Activity> spans, string operationName) =>
        Assert.Single(spans, s => s.OperationName == operationName);

    /// <summary>The span no other captured span parents -- the trace's root within this request.</summary>
    private static Activity Root(Activity span, IReadOnlyList<Activity> spans)
    {
        while (spans.FirstOrDefault(s => s.SpanId == span.ParentSpanId) is { } parent)
        {
            span = parent;
        }

        return span;
    }

    private static void AssertRejected(Activity span, string problemType)
    {
        Assert.Equal(problemType, span.GetTagItem(LoraHotswapTelemetry.ProblemTypeTag));
        Assert.Equal(ActivityStatusCode.Error, span.Status);
    }

    /// <summary>A strategy that fails with a raw, non-problem exception -- the unexpected-fault path.</summary>
    internal sealed class ThrowingStrategy : IContextBindingStrategy
    {
        public const string SecretMessage = "native: /private/contexts/secret exploded";

        public string Name => PerRequestApplyStrategy.StrategyName;

        public Task InitializeAsync(AdapterRegistry registry, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<ContextLease> LeaseAsync(TenantId tenant, AdmittedAdapter adapter, CancellationToken cancellationToken) =>
            throw new InvalidOperationException(SecretMessage);
    }
}

/// <summary>
/// Story 2.7 on the wire: every error body is a handle back to its own trace, and the
/// serving logs name the same trace alongside the tenant and adapter they already carried.
///
/// The host keeps a logging provider registered, as the real host does: enabled hosting
/// diagnostics are what make ASP.NET Core create the per-request activity the serving spans
/// run under, so the trace id in the body is the trace id of <c>serve.request</c>.
/// </summary>
public sealed class ServingTraceIdTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [MemberData(nameof(TenantServingTests.Strategies), MemberType = typeof(TenantServingTests))]
    public async Task An_unknown_tenant_body_carries_the_trace_id_of_the_request_that_produced_it(string strategy)
    {
        var unknown = TracedWorld.Unique("mallory");
        await using var host = await TracedHost.StartAsync(strategy);
        using var capture = new SpanCapture();

        using HttpResponseMessage response = await host.Client.PostAsJsonAsync(
            "/v1/generate", new { tenant = unknown, prompt = "hello" }, Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertTraceIdMatchesTheRequestSpan(response, capture, unknown);
    }

    [Theory]
    [MemberData(nameof(TenantServingTests.Strategies), MemberType = typeof(TenantServingTests))]
    public async Task An_unavailable_lease_body_carries_the_trace_id_of_the_request_that_produced_it(string strategy)
    {
        await using var host = await TracedHost.StartAsync(strategy, poolSize: 1, leaseTimeout: TimeSpan.FromMilliseconds(50));
        await using ContextLease held = await host.World.Strategy.LeaseAsync(host.World.Tenant, host.World.Adapter, Ct);
        using var capture = new SpanCapture();

        using HttpResponseMessage response = await host.Client.PostAsJsonAsync(
            "/v1/generate", new { tenant = host.World.Tenant.Value, prompt = "hello" }, Ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        await AssertTraceIdMatchesTheRequestSpan(response, capture, host.World.Tenant.Value);
    }

    [Theory]
    [MemberData(nameof(TenantServingTests.Strategies), MemberType = typeof(TenantServingTests))]
    public async Task An_unapplied_adapter_body_carries_the_trace_id_of_the_request_that_produced_it(string strategy)
    {
        await using var host = await TracedHost.StartAsync(strategy,
            new TenantServingTests.FaultyRuntime { ReportEmptySet = true });
        using var capture = new SpanCapture();

        using HttpResponseMessage response = await host.Client.PostAsJsonAsync(
            "/v1/generate", new { tenant = host.World.Tenant.Value, prompt = "hello" }, Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        await AssertTraceIdMatchesTheRequestSpan(response, capture, host.World.Tenant.Value);
    }

    [Fact]
    public async Task A_malformed_request_body_still_carries_a_trace_id_although_no_stage_ran()
    {
        await using var host = await TracedHost.StartAsync(PerRequestApplyStrategy.StrategyName);

        using HttpResponseMessage response = await host.Client.PostAsync("/v1/generate",
            new StringContent("{\"prompt\":\"hello\"}", System.Text.Encoding.UTF8, "application/json"), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        JsonElement problem = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(ProblemTypes.InvalidRequest, problem.GetProperty("type").GetString());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty(ServingEndpoints.TraceIdMember).GetString()));
    }

    [Fact]
    public async Task A_problem_body_is_the_RFC_9457_shape_it_always_was_plus_the_trace_id()
    {
        // Problems are written directly rather than through Results.Problem, because the
        // default writer overwrites traceId. This pins the shape that switch must preserve.
        await using var host = await TracedHost.StartAsync(PerRequestApplyStrategy.StrategyName);

        using HttpResponseMessage response = await host.Client.PostAsJsonAsync(
            "/v1/generate", new { tenant = TracedWorld.Unique("mallory"), prompt = "hello" }, Ct);

        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        JsonElement problem = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(["type", "title", "status", "detail", ServingEndpoints.TraceIdMember],
            problem.EnumerateObject().Select(p => p.Name));
        Assert.Equal(ProblemTypes.UnknownTenant, problem.GetProperty("type").GetString());
        Assert.Equal("Unknown tenant", problem.GetProperty("title").GetString());
        Assert.Equal((int)HttpStatusCode.NotFound, problem.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task A_verification_problem_body_carries_a_trace_id_too()
    {
        await using var host = await TracedHost.StartAsync(PerRequestApplyStrategy.StrategyName);
        string unknown = "sha256:" + new string('9', 64);

        using HttpResponseMessage response = await host.Client.PostAsync($"/v1/adapters/{unknown}/verify", null, Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        JsonElement problem = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty(ServingEndpoints.TraceIdMember).GetString()));
    }

    [Theory]
    [MemberData(nameof(TenantServingTests.Strategies), MemberType = typeof(TenantServingTests))]
    public async Task A_successful_response_still_carries_its_four_timings_with_tracing_fully_wired(string strategy)
    {
        await using var host = await TracedHost.StartAsync(strategy);
        using var capture = new SpanCapture();

        using HttpResponseMessage response = await host.Client.PostAsJsonAsync(
            "/v1/generate", new { tenant = host.World.Tenant.Value, prompt = "hello" }, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        JsonElement timings = body.GetProperty("timings");
        Assert.Equal(
            new[] { "adapter_application_us", "adapter_resolution_us", "context_acquisition_us", "generation_us" },
            timings.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.All(timings.EnumerateObject(), p => Assert.True(p.Value.GetInt64() >= 0));

        // The spans are being recorded on this very request, and the body still carries the
        // timings: a span never substitutes for an in-band figure.
        Assert.Equal(5, capture.ForTenant(host.World.Tenant.Value).Count);
        Assert.False(body.TryGetProperty(ServingEndpoints.TraceIdMember, out _),
            "traceId belongs on the error bodies; the success shape is unchanged.");
    }

    [Fact]
    public async Task An_unresolvable_tenant_logs_the_span_vocabularys_unidentified_adapter()
    {
        await using var host = await TracedHost.StartAsync(PerRequestApplyStrategy.StrategyName);

        using HttpResponseMessage response = await host.Client.PostAsJsonAsync(
            "/v1/generate", new { tenant = "mallory", prompt = "hello" }, Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        LoggedLine line = Assert.Single(host.Logs, l => l.Category == "LoraHotswap.Serving");
        // The log and the span vocabulary agree: no resolved adapter reads "unidentified".
        Assert.Equal(LoraHotswapTelemetry.UnidentifiedAdapter, line.State["AdapterId"]);
        Assert.Equal(ProblemTypes.UnknownTenant, line.State["ProblemType"]);
        Assert.False(string.IsNullOrWhiteSpace(line.State["TraceId"]), "The rejection log carries no trace id.");
    }

    [Fact]
    public async Task An_unexpected_fault_logs_at_error_with_the_trace_id()
    {
        await using var host = await TracedHost.StartAsync(PerRequestApplyStrategy.StrategyName,
            strategyOverride: new ServingTracingTests.ThrowingStrategy());

        using HttpResponseMessage response = await host.Client.PostAsJsonAsync(
            "/v1/generate", new { tenant = host.World.Tenant.Value, prompt = "hello" }, Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        LoggedLine line = Assert.Single(host.Logs, l => l.Category == "LoraHotswap.Serving");
        Assert.Equal(LogLevel.Error, line.Level);
        Assert.False(string.IsNullOrWhiteSpace(line.State["TraceId"]),
            "The unexpected-fault log carries no trace id -- the case where correlating log to trace matters most.");
    }

    [Fact]
    public async Task Serving_logs_name_the_tenant_the_adapter_and_the_trace()
    {
        await using var host = await TracedHost.StartAsync(PerRequestApplyStrategy.StrategyName,
            new TenantServingTests.FaultyRuntime { ReportEmptySet = true });
        using var capture = new SpanCapture();

        using HttpResponseMessage response = await host.Client.PostAsJsonAsync(
            "/v1/generate", new { tenant = host.World.Tenant.Value, prompt = "hello" }, Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Activity request = Assert.Single(capture.ForTenant(host.World.Tenant.Value),
            s => s.OperationName == TenantServingService.RequestSpanName);

        LoggedLine line = Assert.Single(host.Logs, l => l.Category == "LoraHotswap.Serving");
        Assert.Equal(LogLevel.Warning, line.Level);
        Assert.Equal(host.World.Tenant.Value, line.State["TenantId"]);
        Assert.Equal(host.World.Adapter.Id.Value, line.State["AdapterId"]);
        Assert.Equal(ProblemTypes.AdapterNotApplied, line.State["ProblemType"]);
        Assert.Equal(request.TraceId.ToString(), line.State["TraceId"]);
    }

    private static async Task AssertTraceIdMatchesTheRequestSpan(
        HttpResponseMessage response, SpanCapture capture, string tenant)
    {
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        JsonElement problem = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Activity request = Assert.Single(capture.ForTenant(tenant),
            s => s.OperationName == TenantServingService.RequestSpanName);

        Assert.Equal(request.TraceId.ToString(), problem.GetProperty(ServingEndpoints.TraceIdMember).GetString());
    }
}

/// <summary>
/// The no-listener path from the matrix: with nothing listening, serving behaves identically
/// and the timings are unaffected. The listener is process-wide, so this runs in the
/// collection that never executes in parallel with anything else -- otherwise another test's
/// <see cref="SpanCapture"/> would be listening and the assertion would prove nothing.
/// </summary>
[Collection(ConsoleCapture.Name)]
public sealed class ServingWithoutTracingTests
{
    [Theory]
    [MemberData(nameof(TenantServingTests.Strategies), MemberType = typeof(TenantServingTests))]
    public async Task Serving_is_unchanged_when_no_listener_is_registered(string strategy)
    {
        var world = await TracedWorld.CreateAsync(strategy);
        using var capture = new SpanCapture();
        capture.Dispose(); // no listener of ours; nothing may be recorded for this tenant

        TenantResponse response = await world.Service.ServeAsync(
            new TenantRequest(world.Tenant, "hello"), TestContext.Current.CancellationToken);

        Assert.Empty(capture.ForTenant(world.Tenant.Value));

        Assert.Equal(world.Adapter.Id, response.Adapter);
        Assert.Equal([world.Adapter.Id], response.AppliedAdapters);
        Assert.Equal(strategy, response.BindingStrategy);
        Assert.Equal($"fake:{world.Adapter.Id.Value}:hello", response.Text);
        Assert.All(
            new[]
            {
                response.Timings.AdapterResolutionMicroseconds,
                response.Timings.ContextAcquisitionMicroseconds,
                response.Timings.AdapterApplicationMicroseconds,
                response.Timings.GenerationMicroseconds,
            },
            value => Assert.True(value >= 0));
    }
}

/// <summary>
/// A serving world whose tenant and adapter identities are unique per test, so a test can
/// filter the process-wide span listener down to its own request.
/// </summary>
internal sealed class TracedWorld
{
    public required TenantId Tenant { get; init; }
    public required AdmittedAdapter Adapter { get; init; }
    public required AdapterRegistry Registry { get; init; }
    public required IContextBindingStrategy Strategy { get; init; }
    public required TenantServingService Service { get; init; }

    public static async Task<TracedWorld> CreateAsync(
        string strategyName,
        IInferenceRuntime? runtime = null,
        int poolSize = 4,
        TimeSpan? leaseTimeout = null,
        string servedBase = TestFixtures.ConfiguredBase,
        IContextBindingStrategy? strategyOverride = null)
    {
        runtime ??= new DeterministicInferenceRuntime();
        var tenant = new TenantId(Unique("tenant"));
        AdmittedAdapter adapter = new(new AdapterId(UniqueAdapterId()),
            new AdapterConfig(8, 32, false, TestFixtures.ConfiguredBase), "traced");
        AdapterRegistry registry = AdapterRegistry.Build([adapter],
            new Dictionary<TenantId, AdapterId> { [tenant] = adapter.Id }).Registry!;

        IContextBindingStrategy strategy = strategyOverride ?? ContextBindingStrategies.Create(
            strategyName, runtime, poolSize, leaseTimeout ?? TimeSpan.FromSeconds(5));
        await strategy.InitializeAsync(registry, CancellationToken.None);

        return new TracedWorld
        {
            Tenant = tenant,
            Adapter = adapter,
            Registry = registry,
            Strategy = strategy,
            Service = new TenantServingService(registry, runtime, strategy, servedBase),
        };
    }

    public static string Unique(string label) => $"{label}-{Guid.NewGuid():N}";

    /// <summary>An identity of admission's shape -- <c>sha256:</c> plus 64 lowercase hex -- and unique.</summary>
    public static string UniqueAdapterId() => $"sha256:{Guid.NewGuid():N}{Guid.NewGuid():N}";
}

/// <summary>One captured log line, with its structured state.</summary>
internal sealed record LoggedLine(string Category, LogLevel Level, IReadOnlyDictionary<string, string?> State);

/// <summary>
/// The real route handlers over a <see cref="TracedWorld"/>, with logging kept on so
/// ASP.NET Core creates the per-request activity the serving spans run under.
/// </summary>
internal sealed class TracedHost : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly CapturingLoggerProvider _logs;

    private TracedHost(WebApplication app, TracedWorld world, CapturingLoggerProvider logs)
    {
        _app = app;
        _logs = logs;
        World = world;
        Client = app.GetTestClient();
    }

    public TracedWorld World { get; }

    public HttpClient Client { get; }

    public IReadOnlyCollection<LoggedLine> Logs => _logs.Lines;

    public static async Task<TracedHost> StartAsync(
        string strategy,
        IInferenceRuntime? runtime = null,
        int poolSize = 4,
        TimeSpan? leaseTimeout = null,
        IContextBindingStrategy? strategyOverride = null)
    {
        var world = await TracedWorld.CreateAsync(strategy, runtime, poolSize, leaseTimeout,
            strategyOverride: strategyOverride);
        var logs = new CapturingLoggerProvider();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(logs);
        builder.Services.AddProblemDetails();

        WebApplication app = builder.Build();
        app.MapServingEndpoints(world.Service, world.Registry,
            new SafeTensorsAdapterVerifier(new Dictionary<AdapterId, string>()));
        await app.StartAsync();
        return new TracedHost(app, world, logs);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.DisposeAsync();
        _logs.Dispose();
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<LoggedLine> Lines { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Capturing(categoryName, Lines);

        public void Dispose()
        {
        }

        private sealed class Capturing(string category, ConcurrentQueue<LoggedLine> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                var values = new Dictionary<string, string?>(StringComparer.Ordinal);
                if (state is IReadOnlyList<KeyValuePair<string, object?>> structured)
                {
                    foreach (var (key, value) in structured)
                    {
                        values[key] = value?.ToString();
                    }
                }

                lines.Enqueue(new LoggedLine(category, logLevel, values));
            }
        }
    }
}
