using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LoraHotswap.Api;
using LoraHotswap.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Trace;

namespace LoraHotswap.Tests;

/// <summary>
/// The composition root itself (<see cref="LoraHotswapHost.BuildAsync"/>), not a hand-built
/// copy of it: the TracerProvider is the one the real composition registers, admission reads
/// real fixture directories, and every startup failure is observed as an exit code and a
/// reason line. Nothing here binds a socket.
/// </summary>
public sealed class HostCompositionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Admission_spans_reach_the_composed_tracer_provider_before_the_host_starts()
    {
        using TempDirectory adapter = TestFixtures.Copy(TestFixtures.ScalingFour);
        var spans = new CapturingProcessor();

        HostStartup startup = await BuildAsync(Settings(adapter.Path), spans);
        await using WebApplication app = Assert.IsType<WebApplication>(startup.App);

        // Not started: without the early TracerProvider resolution these spans have no listener.
        Activity span = Assert.Single(spans.Ended, s => Equals(s.GetTagItem("adapter.name"), Path.GetFileName(adapter.Path)));
        Assert.Equal(AdmissionStartup.SpanName, span.OperationName);
        Assert.Equal("admitted", span.GetTagItem("admission.result"));
        Assert.Equal(TestFixtures.AdapterAt(adapter.Path).Id.Value, span.GetTagItem("adapter.id"));
    }

    [Fact]
    public async Task The_composed_host_serves_and_verifies_an_admitted_adapter_end_to_end()
    {
        using TempDirectory adapter = TestFixtures.Copy(TestFixtures.ScalingFour);
        AdapterId id = TestFixtures.AdapterAt(adapter.Path).Id;
        var spans = new CapturingProcessor();

        HostStartup startup = await BuildAsync(Settings(adapter.Path, tenant: ("alice", id)), spans);
        await using WebApplication app = Assert.IsType<WebApplication>(startup.App);
        await app.StartAsync(Ct);
        using HttpClient client = app.GetTestClient();

        // The verifier's storage map comes from admission's outcome, not from a test-built dictionary.
        using HttpResponseMessage verify = await client.PostAsync($"/v1/adapters/{id.Value}/verify", null, Ct);
        Assert.Equal(HttpStatusCode.OK, verify.StatusCode);
        Assert.Equal("match", (await verify.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("outcome").GetString());

        // Only LoraHotswap spans are exported, and each carries adapter.id (Story 1.8 AC 4).
        Assert.Contains(spans.Ended, s => s.OperationName == ServingEndpoints.VerificationSpanName && Equals(s.GetTagItem("adapter.id"), id.Value));
        Assert.All(spans.Ended, s => Assert.Equal(LoraHotswapTelemetry.ActivitySourceName, s.Source.Name));

        using HttpResponseMessage generate = await client.PostAsJsonAsync("/v1/generate", new { tenant = "alice", prompt = "hi" }, Ct);
        Assert.Equal(HttpStatusCode.OK, generate.StatusCode);
        Assert.Equal(id.Value, (await generate.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("adapterId").GetString());
    }

    [Fact]
    public async Task Rejected_admission_exits_1_with_a_reason_line_and_its_span_exported()
    {
        using TempDirectory adapter = TestFixtures.Copy(TestFixtures.ScalingFour);
        File.Delete(Path.Combine(adapter.Path, "adapter.manifest.json"));
        var spans = new CapturingProcessor();
        var stderr = new StringWriter();

        HostStartup startup = await BuildAsync(Settings(adapter.Path), spans, stderr);

        Assert.Null(startup.App);
        Assert.Equal(1, startup.ExitCode);
        Assert.Contains(ProblemTypes.MissingManifest, stderr.ToString());
        Activity span = Assert.Single(spans.Ended, s => Equals(s.GetTagItem("adapter.name"), Path.GetFileName(adapter.Path)));
        Assert.Equal("rejected", span.GetTagItem("admission.result"));
    }

    [Theory]
    [InlineData("Serving:BindingStrategy", "Bogus", "admission failed: Serving:")]
    [InlineData("Serving:ContextPoolSize", "0", "admission failed: Serving:")]
    [InlineData("OpenTelemetry:OtlpProtocol", "bogus", "startup failed: OpenTelemetry:")]
    [InlineData("OpenTelemetry:OtlpEndpoint", "collector:4317", "startup failed: OpenTelemetry:")]
    public async Task Invalid_configuration_exits_1_with_a_reason_line_not_a_stack_trace(string key, string value, string reason)
    {
        using TempDirectory adapter = TestFixtures.Copy(TestFixtures.ScalingFour);
        var settings = Settings(adapter.Path);
        settings[key] = value;
        var stderr = new StringWriter();

        HostStartup startup = await BuildAsync(settings, stderr: stderr);

        Assert.Null(startup.App);
        Assert.Equal(1, startup.ExitCode);
        string line = Assert.Single(stderr.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
        Assert.StartsWith(reason, line);
    }

    [Fact]
    public async Task A_resident_context_that_cannot_be_configured_fails_startup_with_exit_1()
    {
        using TempDirectory adapter = TestFixtures.Copy(TestFixtures.ScalingFour);
        var settings = Settings(adapter.Path, tenant: ("alice", TestFixtures.AdapterAt(adapter.Path).Id));
        settings["Serving:BindingStrategy"] = PerTenantResidentStrategy.StrategyName;
        var stderr = new StringWriter();

        HostStartup startup = await BuildAsync(settings, stderr: stderr,
            runtime: new TenantServingTests.FaultyRuntime { FailApply = true });

        Assert.Null(startup.App);
        Assert.Equal(1, startup.ExitCode);
        Assert.StartsWith(ProblemTypes.AdapterNotApplied, stderr.ToString());
        Assert.DoesNotContain(TenantServingTests.FaultyRuntime.SecretMessage, stderr.ToString());
    }

    private static Dictionary<string, string?> Settings(string adapterDirectory, (string Name, AdapterId Id)? tenant = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["OpenTelemetry:ConsoleExporter"] = "false",
            ["Admission:BaseModelName"] = TestFixtures.ConfiguredBase,
            ["Admission:AdaptersRoot"] = Path.GetDirectoryName(adapterDirectory),
            ["Admission:Adapters:0"] = Path.GetFileName(adapterDirectory),
        };
        if (tenant is { } t)
        {
            settings[$"Admission:Tenants:{t.Name}"] = t.Id.Value;
        }

        return settings;
    }

    private static Task<HostStartup> BuildAsync(
        Dictionary<string, string?> settings,
        CapturingProcessor? spans = null,
        StringWriter? stderr = null,
        IInferenceRuntime? runtime = null) =>
        LoraHotswapHost.BuildAsync([], stderr ?? new StringWriter(), builder =>
        {
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            builder.Configuration.AddInMemoryCollection(settings);
            if (spans is not null)
            {
                builder.Services.ConfigureOpenTelemetryTracerProvider(tracing => tracing.AddProcessor(spans));
            }

            if (runtime is not null)
            {
                builder.Services.AddSingleton(runtime);
            }
        });

    private sealed class CapturingProcessor : BaseProcessor<Activity>
    {
        public ConcurrentQueue<Activity> Ended { get; } = new();

        public override void OnEnd(Activity data) => Ended.Enqueue(data);
    }
}
