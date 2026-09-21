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
