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
/// The HTTP surface, in-process: the real route handlers from
/// <see cref="ServingEndpoints.MapServingEndpoints"/>, asserted on status code, the RFC 9457
/// <c>type</c> and the response body -- not on constants read back from a class.
/// </summary>
public sealed class ServingEndpointTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // --- POST /v1/generate (Stories 2.1, 2.3, 2.4) -------------------------------------

    [Theory]
    [MemberData(nameof(TenantServingTests.Strategies), MemberType = typeof(TenantServingTests))]
    public async Task Known_tenant_gets_200_naming_its_adapter_and_the_active_strategy(string strategy)
    {
        await using var host = await Host.StartAsync(strategy);

        using HttpResponseMessage response = await host.Client.PostAsJsonAsync("/v1/generate", new { tenant = "alice", prompt = "hello" }, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal("alice", body.GetProperty("tenant").GetString());
        Assert.Equal(host.World.AliceAdapter.Id.Value, body.GetProperty("adapterId").GetString());
        Assert.Equal([host.World.AliceAdapter.Id.Value], body.GetProperty("appliedAdapters").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(strategy, body.GetProperty("bindingStrategy").GetString());
        Assert.Equal($"fake:{host.World.AliceAdapter.Id.Value}:hello", body.GetProperty("text").GetString());
    }

    [Theory]
    [MemberData(nameof(TenantServingTests.Strategies), MemberType = typeof(TenantServingTests))]
    public async Task Unknown_tenant_is_404_with_the_unknown_tenant_problem_type(string strategy)
    {
        await using var host = await Host.StartAsync(strategy);

        using HttpResponseMessage response = await host.Client.PostAsJsonAsync("/v1/generate", new { tenant = "mallory", prompt = "hello" }, Ct);

        await AssertProblem(response, HttpStatusCode.NotFound, ProblemTypes.UnknownTenant);
    }

    // --- Story 2.6: in-band timings, asserted on the wire --------------------------------

    /// <summary>The four field names a reader with only <c>curl</c> must find in the body (NFR4).</summary>
    private static readonly string[] TimingFields =
        ["adapter_resolution_us", "context_acquisition_us", "adapter_application_us", "generation_us"];

    [Theory]
    [MemberData(nameof(TenantServingTests.Strategies), MemberType = typeof(TenantServingTests))]
    public async Task Successful_body_carries_all_four_us_named_integer_timings(string strategy)
    {
        await using var host = await Host.StartAsync(strategy);

        using HttpResponseMessage response = await host.Client.PostAsJsonAsync("/v1/generate", new { tenant = "alice", prompt = "hello" }, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        JsonElement timings = body.GetProperty("timings");

        // Exactly the four fields, spelled with the _us suffix -- camelCase would not.
        Assert.Equal(TimingFields.Order(StringComparer.Ordinal),
            timings.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.All(TimingFields, name =>
        {
            JsonElement value = timings.GetProperty(name);
            Assert.Equal(JsonValueKind.Number, value.ValueKind);
            Assert.True(value.TryGetInt64(out long microseconds), $"'{name}' must be an integer, not '{value.GetRawText()}'.");
            Assert.True(microseconds >= 0, $"'{name}' reported {microseconds}; microseconds are non-negative.");
        });
    }

    [Fact]
    public async Task Resident_strategy_reports_zero_application_on_the_wire_and_still_measures_acquisition()
    {
        await using var host = await Host.StartAsync(PerTenantResidentStrategy.StrategyName);

        using HttpResponseMessage response = await host.Client.PostAsJsonAsync("/v1/generate", new { tenant = "alice", prompt = "hello" }, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement timings = (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("timings");
        Assert.Equal(0L, timings.GetProperty("adapter_application_us").GetInt64());
    }

    [Fact]
    public async Task Acquisition_time_lands_in_the_acquisition_field_on_the_wire()
    {
        // The remaining two wire fields: with every stage small, resolution and acquisition
        // could be transposed at the mapping and no test would see it. Contention makes
        // acquisition the large one.
        await using var host = await Host.StartAsync(PerRequestApplyStrategy.StrategyName, poolSize: 1,
            leaseTimeout: TimeSpan.FromSeconds(30));
        ContextLease held = await host.World.Strategy.LeaseAsync(TenantServingTests.World.Alice,
            host.World.AliceAdapter, CancellationToken.None);

        Task<HttpResponseMessage> serving = host.Client.PostAsJsonAsync("/v1/generate", new { tenant = "alice", prompt = "hello" }, Ct);
        await Task.Delay(TimeSpan.FromMilliseconds(30), Ct);
        await held.DisposeAsync();

        using HttpResponseMessage response = await serving;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement timings = (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("timings");
        Assert.True(timings.GetProperty("context_acquisition_us").GetInt64() > 20_000,
            "The request waited ~30 ms for the single context, so context_acquisition_us must carry it.");
        Assert.All(new[] { "adapter_resolution_us", "adapter_application_us", "generation_us" },
            name => Assert.True(timings.GetProperty(name).GetInt64() < 10_000,
                $"{name} carries the lease wait, so the wire fields are transposed."));
    }

    [Fact]
    public async Task Each_stage_lands_in_its_own_wire_field()
    {
        // The Core-to-wire copy is positional: with every stage near zero, transposing two
        // fields would serialize happily. A runtime that spends real time in exactly one
        // stage pins each name to the stage it claims to measure.
        var slowGeneration = new TenantServingTests.SlowRuntime { GenerateDelay = TimeSpan.FromMilliseconds(25) };
        await using var host = await Host.StartAsync(PerRequestApplyStrategy.StrategyName, slowGeneration);

        using HttpResponseMessage response = await host.Client.PostAsJsonAsync("/v1/generate", new { tenant = "alice", prompt = "hello" }, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement timings = (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("timings");
        Assert.True(timings.GetProperty("generation_us").GetInt64() > 10_000,
            "The runtime spent 25 ms generating, so generation_us must carry it.");
        Assert.All(new[] { "adapter_resolution_us", "context_acquisition_us", "adapter_application_us" },
            name => Assert.True(timings.GetProperty(name).GetInt64() < 10_000,
                $"{name} carries the generation time, so the wire fields are transposed."));
    }

    [Fact]
    public async Task Application_time_lands_in_the_application_field_on_the_wire()
    {
        var slowApply = new TenantServingTests.SlowRuntime { ApplyDelay = TimeSpan.FromMilliseconds(25) };
        await using var host = await Host.StartAsync(PerRequestApplyStrategy.StrategyName, slowApply);

        using HttpResponseMessage response = await host.Client.PostAsJsonAsync("/v1/generate", new { tenant = "alice", prompt = "hello" }, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement timings = (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("timings");
        Assert.True(timings.GetProperty("adapter_application_us").GetInt64() > 10_000,
            "The runtime spent 25 ms applying, so adapter_application_us must carry it.");
        Assert.All(new[] { "adapter_resolution_us", "context_acquisition_us", "generation_us" },
            name => Assert.True(timings.GetProperty(name).GetInt64() < 10_000,
                $"{name} carries the application time, so the wire fields are transposed."));
    }

    [Theory]
    [MemberData(nameof(TenantServingTests.Strategies), MemberType = typeof(TenantServingTests))]
    public async Task Rejected_requests_carry_no_timings_and_are_otherwise_unchanged(string strategy)
    {
        await using var host = await Host.StartAsync(strategy);

        using HttpResponseMessage unknownTenant = await host.Client.PostAsJsonAsync("/v1/generate", new { tenant = "mallory", prompt = "hello" }, Ct);
        using HttpResponseMessage invalid = await host.Client.PostAsync("/v1/generate",
            new StringContent("{\"prompt\":\"hello\"}", System.Text.Encoding.UTF8, "application/json"), Ct);

        JsonElement notFound = await AssertProblem(unknownTenant, HttpStatusCode.NotFound, ProblemTypes.UnknownTenant);
        JsonElement badRequest = await AssertProblem(invalid, HttpStatusCode.BadRequest, ProblemTypes.InvalidRequest);
        Assert.All([notFound, badRequest], problem =>
        {
            Assert.False(problem.TryGetProperty("timings", out _));
            Assert.All(TimingFields, name => Assert.DoesNotContain(name, problem.GetRawText(), StringComparison.Ordinal));
        });
    }

    [Fact]
    public async Task Timings_live_in_the_body_not_in_a_header()
    {
        await using var host = await Host.StartAsync(PerRequestApplyStrategy.StrategyName);

        using HttpResponseMessage response = await host.Client.PostAsJsonAsync("/v1/generate", new { tenant = "alice", prompt = "hello" }, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string headers = response.Headers + response.Content.Headers.ToString();
        Assert.All(TimingFields, name => Assert.DoesNotContain(name, headers, StringComparison.OrdinalIgnoreCase));
        Assert.True((await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).TryGetProperty("timings", out _));
    }

    [Theory]
    [InlineData("{\"prompt\":\"hello\"}")]
    [InlineData("{\"tenant\":\"  \",\"prompt\":\"hello\"}")]
    [InlineData("{\"tenant\":\"alice\"}")]
    [InlineData("{\"tenant\":null,\"prompt\":null}")]
    public async Task Missing_or_blank_fields_are_400_invalid_request_not_a_crash(string json)
    {
        await using var host = await Host.StartAsync(PerRequestApplyStrategy.StrategyName);

        using HttpResponseMessage response = await host.Client.PostAsync("/v1/generate",
            new StringContent(json, System.Text.Encoding.UTF8, "application/json"), Ct);

        await AssertProblem(response, HttpStatusCode.BadRequest, ProblemTypes.InvalidRequest);
    }

    [Theory]
    [MemberData(nameof(TenantServingTests.Strategies), MemberType = typeof(TenantServingTests))]
    public async Task Unavailable_lease_is_503_with_retry_after(string strategy)
    {
        // PerRequestApply: the one pooled context is held. PerTenantResident: alice's own context is held.
        await using var host = await Host.StartAsync(strategy, poolSize: 1,
            leaseTimeout: TimeSpan.FromMilliseconds(50));
        await using ContextLease held = await host.World.Strategy.LeaseAsync(TenantServingTests.World.Alice,
            host.World.AliceAdapter, CancellationToken.None);

        using HttpResponseMessage response = await host.Client.PostAsJsonAsync("/v1/generate", new { tenant = "alice", prompt = "hello" }, Ct);

        await AssertProblem(response, HttpStatusCode.ServiceUnavailable, ProblemTypes.LeaseUnavailable);
        Assert.Equal(TimeSpan.FromSeconds(ServingEndpoints.LeaseRetryAfterSeconds), response.Headers.RetryAfter?.Delta);
    }

    [Fact]
    public async Task Adapter_application_fault_is_500_adapter_not_applied_without_leaking_internals()
    {
        var runtime = new TenantServingTests.FaultyRuntime { FailApply = true };
        await using var host = await Host.StartAsync(PerRequestApplyStrategy.StrategyName, runtime);

        using HttpResponseMessage response = await host.Client.PostAsJsonAsync("/v1/generate", new { tenant = "alice", prompt = "hello" }, Ct);

        JsonElement problem = await AssertProblem(response, HttpStatusCode.InternalServerError, ProblemTypes.AdapterNotApplied);
        Assert.DoesNotContain(TenantServingTests.FaultyRuntime.SecretMessage, problem.GetRawText());
        Assert.False(problem.TryGetProperty("text", out _));
    }

    [Theory]
    [MemberData(nameof(TenantServingTests.Strategies), MemberType = typeof(TenantServingTests))]
    public async Task Foreign_applied_set_is_500_adapter_not_applied_with_no_output(string strategy)
    {
        var runtime = new TenantServingTests.FaultyRuntime();
        await using var host = await Host.StartAsync(strategy, runtime);
        runtime.ReportInstead = host.World.BobAdapter.Id;

        using HttpResponseMessage response = await host.Client.PostAsJsonAsync("/v1/generate", new { tenant = "alice", prompt = "hello" }, Ct);

        JsonElement problem = await AssertProblem(response, HttpStatusCode.InternalServerError, ProblemTypes.AdapterNotApplied);
        Assert.False(problem.TryGetProperty("text", out _));
    }

    [Theory]
    [InlineData("not json", "application/json")]
    [InlineData("", "application/json")]
    [InlineData("{\"tenant\":5,\"prompt\":\"hello\"}", "application/json")]
    [InlineData("{\"tenant\":\"alice\",\"prompt\":\"hello\"}", "text/plain")]
    public async Task Malformed_body_or_content_type_is_400_invalid_request_not_a_framework_error(string body, string contentType)
    {
        await using var host = await Host.StartAsync(PerRequestApplyStrategy.StrategyName);

        using HttpResponseMessage response = await host.Client.PostAsync("/v1/generate",
            new StringContent(body, System.Text.Encoding.UTF8, contentType), Ct);

        await AssertProblem(response, HttpStatusCode.BadRequest, ProblemTypes.InvalidRequest);
    }

    [Fact]
    public async Task Unexpected_serving_fault_is_500_internal_error_without_leaking_internals()
    {
        var world = await TenantServingTests.World.CreateAsync(PerRequestApplyStrategy.StrategyName);
        var service = new TenantServingService(world.Registry, new DeterministicInferenceRuntime(), new ThrowingStrategy(),
            TestFixtures.ConfiguredBase);
        await using WebApplication app = await StartBareAsync(a => a.MapServingEndpoints(service, world.Registry,
            new SafeTensorsAdapterVerifier(new Dictionary<AdapterId, string>())));
        using HttpClient client = app.GetTestClient();

        using HttpResponseMessage response = await client.PostAsJsonAsync("/v1/generate", new { tenant = "alice", prompt = "hello" }, Ct);

        JsonElement problem = await AssertProblem(response, HttpStatusCode.InternalServerError, ProblemTypes.InternalError);
        Assert.DoesNotContain(ThrowingStrategy.SecretMessage, problem.GetRawText());
    }

    [Fact]
    public void Every_request_time_problem_type_has_an_explicit_status_and_admission_only_types_have_none()
    {
        (string Type, int Status)[] requestTime =
        [
            (ProblemTypes.InvalidRequest, 400), (ProblemTypes.UnknownTenant, 404), (ProblemTypes.UnknownAdapter, 404),
            (ProblemTypes.HashMismatch, 409), (ProblemTypes.MergeReferenceMissing, 422), (ProblemTypes.LeaseUnavailable, 503),
            (ProblemTypes.AdapterNotApplied, 500), (ProblemTypes.GenerationFailed, 500), (ProblemTypes.BaseModelMismatch, 500),
            (ProblemTypes.InternalError, 500),
        ];

        Assert.All(requestTime, t => Assert.Equal(t.Status, ServingEndpoints.StatusCodesByType[t.Type]));
        Assert.False(ServingEndpoints.StatusCodesByType.ContainsKey(ProblemTypes.InertAdapter));
        Assert.False(ServingEndpoints.StatusCodesByType.ContainsKey(ProblemTypes.MissingManifest));
    }

    // --- POST /v1/adapters/{adapterId}/verify (Story 1.9) --------------------------------

    [Fact]
    public async Task Verifying_an_admitted_adapter_from_the_running_instance_reports_match_with_a_span()
    {
        AdmittedAdapter adapter = await TestFixtures.AdmitAsync(TestFixtures.ScalingFour);
        await using var host = await Host.StartAsync(PerRequestApplyStrategy.StrategyName,
            verified: (adapter, TestFixtures.ScalingFour));
        using var spans = new SpanCapture();

        using HttpResponseMessage response = await host.Client.PostAsync($"/v1/adapters/{adapter.Id.Value}/verify", null, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal("match", body.GetProperty("outcome").GetString());
        Assert.Equal(adapter.Id.Value, body.GetProperty("adapterId").GetString());
        Assert.Equal(4, body.GetProperty("modules").GetArrayLength());

        Activity span = Assert.Single(spans.ForAdapterId(adapter.Id.Value), s => s.OperationName == ServingEndpoints.VerificationSpanName);
        Assert.Equal("match", span.GetTagItem("verification.outcome"));
    }

    [Fact]
    public async Task Verification_distinguishes_mismatch_and_inert()
    {
        using TempDirectory mismatchDir = TestFixtures.Copy(TestFixtures.ScalingFour);
        TestFixtures.RewriteF32(Path.Combine(mismatchDir.Path, SafeTensorsAdapterVerifier.ReferenceFileName),
            n => n.EndsWith(".w_merged", StringComparison.Ordinal), v => v * 1.01f);
        using TempDirectory inertDir = TestFixtures.Copy(TestFixtures.ScalingFour);
        TestFixtures.RewriteF32(Path.Combine(inertDir.Path, "adapter_model.safetensors"), n => n.Contains("lora_B"), _ => 0f);
        AdmittedAdapter mismatched = TestFixtures.AdapterAt(mismatchDir.Path);
        AdmittedAdapter inert = TestFixtures.AdapterAt(inertDir.Path);

        await using var host = await Host.StartAsync(PerRequestApplyStrategy.StrategyName,
            verified: (mismatched, mismatchDir.Path), extraVerified: (inert, inertDir.Path));
        using var spans = new SpanCapture();

        JsonElement mismatch = await (await host.Client.PostAsync($"/v1/adapters/{mismatched.Id.Value}/verify", null, Ct))
            .Content.ReadFromJsonAsync<JsonElement>(Ct);
        JsonElement noOp = await (await host.Client.PostAsync($"/v1/adapters/{inert.Id.Value}/verify", null, Ct))
            .Content.ReadFromJsonAsync<JsonElement>(Ct);

        Assert.Equal("mismatch", mismatch.GetProperty("outcome").GetString());
        Assert.Equal("inert", noOp.GetProperty("outcome").GetString());
        Assert.Equal("mismatch", Assert.Single(spans.ForAdapterId(mismatched.Id.Value)).GetTagItem("verification.outcome"));
        Assert.Equal("inert", Assert.Single(spans.ForAdapterId(inert.Id.Value)).GetTagItem("verification.outcome"));
    }

    [Fact]
    public async Task Verifying_a_never_admitted_adapter_is_a_typed_404_with_a_span()
    {
        await using var host = await Host.StartAsync(PerRequestApplyStrategy.StrategyName);
        using var spans = new SpanCapture();
        string unknown = "sha256:" + new string('9', 64);

        using HttpResponseMessage response = await host.Client.PostAsync($"/v1/adapters/{unknown}/verify", null, Ct);

        await AssertProblem(response, HttpStatusCode.NotFound, ProblemTypes.UnknownAdapter);
        Activity span = Assert.Single(spans.ForAdapterId(unknown));
        Assert.Equal("error", span.GetTagItem("verification.outcome"));
        Assert.Equal(ProblemTypes.UnknownAdapter, span.GetTagItem("verification.reason"));
    }

    [Theory]
    [InlineData("not-an-id")]
    [InlineData("sha256:xyz")]
    [InlineData("sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcde")]
    [InlineData("sha256:0123456789ABCDEF0123456789abcdef0123456789abcdef0123456789abcdef")]
    public async Task Malformed_adapter_identity_is_400_invalid_request_with_a_span(string adapterId)
    {
        await using var host = await Host.StartAsync(PerRequestApplyStrategy.StrategyName);
        using var spans = new SpanCapture();

        using HttpResponseMessage response = await host.Client.PostAsync($"/v1/adapters/{adapterId}/verify", null, Ct);

        await AssertProblem(response, HttpStatusCode.BadRequest, ProblemTypes.InvalidRequest);
        Activity span = Assert.Single(spans.ForAdapterId(AdmissionStartup.UnidentifiedAdapter),
            s => s.OperationName == ServingEndpoints.VerificationSpanName);
        Assert.Equal("error", span.GetTagItem("verification.outcome"));
        Assert.Equal(ProblemTypes.InvalidRequest, span.GetTagItem("verification.reason"));
    }

    [Fact]
    public async Task Adapter_changed_since_admission_is_409_hash_mismatch()
    {
        using TempDirectory dir = TestFixtures.Copy(TestFixtures.ScalingFour);
        AdmittedAdapter admitted = TestFixtures.AdapterAt(dir.Path);
        TestFixtures.RewriteF32(Path.Combine(dir.Path, SafeTensorsAdapterVerifier.SourceFileName), n => n.Contains("lora_B"), v => v * 2f);
        await using var host = await Host.StartAsync(PerRequestApplyStrategy.StrategyName, verified: (admitted, dir.Path));

        using HttpResponseMessage response = await host.Client.PostAsync($"/v1/adapters/{admitted.Id.Value}/verify", null, Ct);

        await AssertProblem(response, HttpStatusCode.Conflict, ProblemTypes.HashMismatch);
    }

    [Fact]
    public async Task Unreadable_reference_is_500_internal_error_without_leaking_internals_and_with_a_span()
    {
        using TempDirectory dir = TestFixtures.Copy(TestFixtures.ScalingFour);
        AdmittedAdapter admitted = TestFixtures.AdapterAt(dir.Path);
        string reference = Path.Combine(dir.Path, SafeTensorsAdapterVerifier.ReferenceFileName);
        File.WriteAllBytes(reference, File.ReadAllBytes(reference)[..64]);
        await using var host = await Host.StartAsync(PerRequestApplyStrategy.StrategyName, verified: (admitted, dir.Path));
        using var spans = new SpanCapture();

        using HttpResponseMessage response = await host.Client.PostAsync($"/v1/adapters/{admitted.Id.Value}/verify", null, Ct);

        JsonElement problem = await AssertProblem(response, HttpStatusCode.InternalServerError, ProblemTypes.InternalError);
        Assert.DoesNotContain(dir.Path, problem.GetRawText());
        Assert.Equal("The request failed unexpectedly. No output was returned.", problem.GetProperty("detail").GetString());
        Activity span = Assert.Single(spans.ForAdapterId(admitted.Id.Value));
        Assert.Equal("error", span.GetTagItem("verification.outcome"));
        Assert.Equal(ProblemTypes.InternalError, span.GetTagItem("verification.reason"));
    }

    [Fact]
    public async Task Admitted_adapter_without_a_reference_is_422()
    {
        AdmittedAdapter adapter = new(new AdapterId("sha256:" + new string('3', 64)),
            new AdapterConfig(8, 32, false, TestFixtures.ConfiguredBase), "no-reference");
        using TempDirectory dir = TestFixtures.Copy(TestFixtures.ScalingFour);
        File.Delete(Path.Combine(dir.Path, SafeTensorsAdapterVerifier.ReferenceFileName));
        await using var host = await Host.StartAsync(PerRequestApplyStrategy.StrategyName, verified: (adapter, dir.Path));

        using HttpResponseMessage response = await host.Client.PostAsync($"/v1/adapters/{adapter.Id.Value}/verify", null, Ct);

        await AssertProblem(response, HttpStatusCode.UnprocessableEntity, ProblemTypes.MergeReferenceMissing);
    }

    // --- harness ------------------------------------------------------------------------

    private static async Task<JsonElement> AssertProblem(HttpResponseMessage response, HttpStatusCode status, string type)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        JsonElement problem = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(type, problem.GetProperty("type").GetString());
        Assert.Equal((int)status, problem.GetProperty("status").GetInt32());
        return problem;
    }

    private static async Task<WebApplication> StartBareAsync(Action<WebApplication> map)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddProblemDetails();
        WebApplication app = builder.Build();
        map(app);
        await app.StartAsync();
        return app;
    }

    /// <summary>A strategy that fails with a raw, non-problem exception -- the unexpected-fault path.</summary>
    private sealed class ThrowingStrategy : IContextBindingStrategy
    {
        public const string SecretMessage = "native: /private/contexts/secret exploded";

        public string Name => "Throwing";

        public Task InitializeAsync(AdapterRegistry registry, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<ContextLease> LeaseAsync(TenantId tenant, AdmittedAdapter adapter, CancellationToken cancellationToken) =>
            throw new InvalidOperationException(SecretMessage);
    }

    private sealed class Host : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private Host(WebApplication app, TenantServingTests.World world)
        {
            _app = app;
            World = world;
            Client = app.GetTestClient();
        }

        public TenantServingTests.World World { get; }
        public HttpClient Client { get; }

        public static async Task<Host> StartAsync(
            string strategy,
            TenantServingTests.DeterministicInferenceRuntimeBase? runtime = null,
            int poolSize = 4,
            TimeSpan? leaseTimeout = null,
            (AdmittedAdapter Adapter, string Directory)? verified = null,
            (AdmittedAdapter Adapter, string Directory)? extraVerified = null)
        {
            var world = await TenantServingTests.World.CreateAsync(strategy, runtime, poolSize, leaseTimeout);

            // Verification needs the verified adapters in the registry; serving keeps the world's tenants.
            var admitted = world.Registry.Adapters.ToList();
            var directories = new Dictionary<AdapterId, string>();
            foreach (var entry in new[] { verified, extraVerified })
            {
                if (entry is { } v)
                {
                    admitted.Add(v.Adapter);
                    directories[v.Adapter.Id] = v.Directory;
                }
            }

            AdapterRegistry registry = AdapterRegistry.Build(admitted, world.Registry.Tenants).Registry!;

            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            builder.Services.AddProblemDetails();

            WebApplication app = builder.Build();
            app.MapServingEndpoints(world.Service, registry, new SafeTensorsAdapterVerifier(directories));
            await app.StartAsync();
            return new Host(app, world);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.DisposeAsync();
        }
    }
}
