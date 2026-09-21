using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using LoraHotswap.Api;
using LoraHotswap.Core;
using LoraHotswap.Core.Ports;
using LoraHotswap.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Trace;

namespace LoraHotswap.Tests;

/// <summary>
/// Story 1.8: admission emits one span per adapter on the <c>LoraHotswap</c> source,
/// recording admitted / rejected with the reason and always carrying <c>adapter.id</c>.
///
/// The listener is process-wide and other test classes run admission in parallel, so
/// every test names its adapters uniquely and filters on that name, and spans are
/// collected into a concurrent queue.
/// </summary>
public sealed class AdmissionTracingTests
{
    private const string Source = "aa00000000000000000000000000000000000000000000000000000000000000";
    private const string Gguf = "bb00000000000000000000000000000000000000000000000000000000000000";

    [Fact]
    public void The_system_has_one_activity_source_named_LoraHotswap()
    {
        Assert.Equal("LoraHotswap", LoraHotswapTelemetry.ActivitySourceName);
        Assert.Equal("LoraHotswap", LoraHotswapTelemetry.Source.Name);
    }

    [Fact]
    public async Task Admitted_adapter_emits_one_span_with_result_and_identity()
    {
        string name = Unique("admitted");
        using var capture = new SpanCapture();

        await AdmissionStartup.RunAsync([name], TestFixtures.ConfiguredBase, new Dictionary<TenantId, AdapterId>(),
            new Reader(_ => Valid(name)), CancellationToken.None);

        Activity span = Assert.Single(capture.For(name));
        Assert.Equal(AdmissionStartup.SpanName, span.OperationName);
        Assert.Equal("admitted", span.GetTagItem("admission.result"));
        Assert.Equal("sha256:" + Source, span.GetTagItem("adapter.id"));
    }

    [Fact]
    public async Task Rejected_adapter_span_carries_identity_reason_and_error_status()
    {
        string name = Unique("rejected");
        using var capture = new SpanCapture();

        await AdmissionStartup.RunAsync([name], TestFixtures.ConfiguredBase, new Dictionary<TenantId, AdapterId>(),
            new Reader(_ => Valid(name) with { Manifest = null }), CancellationToken.None);

        Activity span = Assert.Single(capture.For(name));
        Assert.Equal("rejected", span.GetTagItem("admission.result"));
        Assert.Equal(ProblemTypes.MissingManifest, span.GetTagItem("admission.reason"));
        Assert.Equal("sha256:" + Source, span.GetTagItem("adapter.id"));
        Assert.Equal(ActivityStatusCode.Error, span.Status);
    }

    [Fact]
    public async Task Adapter_without_source_bytes_is_tagged_unidentified_not_by_path()
    {
        string name = Unique("no-source");
        using var capture = new SpanCapture();

        await AdmissionStartup.RunAsync([Path.Combine("some", "dir", name)], TestFixtures.ConfiguredBase,
            new Dictionary<TenantId, AdapterId>(),
            new Reader(_ => Valid(name) with { Manifest = null, RecomputedSourceSha256 = null }), CancellationToken.None);

        Activity span = Assert.Single(capture.For(name));
        Assert.Equal(AdmissionStartup.UnidentifiedAdapter, span.GetTagItem("adapter.id"));
        Assert.DoesNotContain(span.Tags, t => t.Value?.Contains("some") == true);
    }

    [Fact]
    public async Task Every_span_carries_adapter_id_across_a_mixed_run()
    {
        string good = Unique("mixed-good"), bad = Unique("mixed-bad");
        using var capture = new SpanCapture();

        await AdmissionStartup.RunAsync([good, bad], TestFixtures.ConfiguredBase, new Dictionary<TenantId, AdapterId>(),
            new Reader(dir => dir == good ? Valid(good) : Valid(bad) with { LoraBMaxAbs = [0.0] }), CancellationToken.None);

        var spans = capture.For(good).Concat(capture.For(bad)).ToList();
        Assert.Equal(2, spans.Count);
        Assert.All(spans, s => Assert.NotNull(s.GetTagItem("adapter.id")));
    }

    [Fact]
    public async Task A_read_that_throws_still_closes_its_span_with_an_error()
    {
        string name = Unique("throws");
        using var capture = new SpanCapture();

        await Assert.ThrowsAsync<IOException>(() => AdmissionStartup.RunAsync([name], TestFixtures.ConfiguredBase,
            new Dictionary<TenantId, AdapterId>(), new Reader(_ => throw new IOException("disk gone")), CancellationToken.None));

        Activity span = Assert.Single(capture.For(name));
        Assert.Equal("error", span.GetTagItem("admission.result"));
        Assert.Equal(AdmissionStartup.UnidentifiedAdapter, span.GetTagItem("adapter.id"));
        Assert.Equal(ActivityStatusCode.Error, span.Status);
    }

    [Fact]
    public async Task Spans_are_exported_over_OTLP_when_an_endpoint_is_configured()
    {
        string name = Unique("otlp");
        using var collector = new FakeOtlpCollector();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OpenTelemetry:ConsoleExporter"] = "false",
            ["OpenTelemetry:OtlpEndpoint"] = collector.Endpoint + "v1/traces",
            ["OpenTelemetry:OtlpProtocol"] = "http/protobuf",
        }).Build();

        await using ServiceProvider services = new ServiceCollection().AddLoraHotswapTracing(configuration).BuildServiceProvider();
        TracerProvider provider = services.GetRequiredService<TracerProvider>();

        await AdmissionStartup.RunAsync([name], TestFixtures.ConfiguredBase, new Dictionary<TenantId, AdapterId>(),
            new Reader(_ => Valid(name)), CancellationToken.None);
        Assert.True(provider.ForceFlush(10_000));

        byte[] body = await collector.WaitForBodyContainingAsync(name, TimeSpan.FromSeconds(10));
        Assert.Contains(AdmissionStartup.SpanName, Encoding.UTF8.GetString(body));
    }

    [Fact]
    public async Task A_base_http_protobuf_endpoint_gets_the_traces_path_appended()
    {
        string name = Unique("otlp-base");
        using var collector = new FakeOtlpCollector();
        await ExportAndAwaitAsync(collector, name, new()
        {
            ["OpenTelemetry:OtlpEndpoint"] = collector.Endpoint,
            ["OpenTelemetry:OtlpProtocol"] = "http/protobuf",
        });

        Assert.Contains("/v1/traces", collector.Paths);
    }

    [Fact]
    public async Task The_standard_environment_variables_alone_enable_OTLP_and_choose_the_protocol()
    {
        string name = Unique("otlp-env");
        using var collector = new FakeOtlpCollector();
        await ExportAndAwaitAsync(collector, name, new()
        {
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = collector.Endpoint,
            ["OTEL_EXPORTER_OTLP_PROTOCOL"] = "http/protobuf",
        });
    }

    [Theory]
    [InlineData("OpenTelemetry:OtlpProtocol", "bogus")]
    [InlineData("OpenTelemetry:OtlpEndpoint", "collector:4317")]
    [InlineData("OpenTelemetry:OtlpEndpoint", "not a url")]
    public void Unsupported_exporter_settings_are_rejected_at_registration(string key, string value)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [key] = value }).Build();

        var error = Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddLoraHotswapTracing(configuration));
        Assert.StartsWith(LoraHotswapTelemetry.SectionName + ":", error.Message);
    }

    private static async Task ExportAndAwaitAsync(FakeOtlpCollector collector, string name, Dictionary<string, string?> settings)
    {
        settings["OpenTelemetry:ConsoleExporter"] = "false";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        // The exporter reads the OTEL_* variables through the container's IConfiguration.
        await using ServiceProvider services = new ServiceCollection()
            .AddSingleton<IConfiguration>(configuration)
            .AddLoraHotswapTracing(configuration)
            .BuildServiceProvider();
        TracerProvider provider = services.GetRequiredService<TracerProvider>();

        await AdmissionStartup.RunAsync([name], TestFixtures.ConfiguredBase, new Dictionary<TenantId, AdapterId>(),
            new Reader(_ => Valid(name)), CancellationToken.None);
        Assert.True(provider.ForceFlush(10_000));

        await collector.WaitForBodyContainingAsync(name, TimeSpan.FromSeconds(10));
    }

    internal static string Unique(string label) => $"{label}-{Guid.NewGuid():N}";

    private static AdapterArtifacts Valid(string name) =>
        new(name,
            new AdapterManifest(Source, Gguf, new string('0', 40), "sha256:" + Source),
            Source, Gguf,
            new AdapterConfigSnapshot(8, 16, false, TestFixtures.ConfiguredBase),
            [0.5]);

    private sealed class Reader(Func<string, AdapterArtifacts> read) : IAdapterArtifactsReader
    {
        public Task<AdapterArtifacts> ReadAsync(string adapterDirectory, CancellationToken cancellationToken) =>
            Task.FromResult(read(adapterDirectory));
    }

    /// <summary>A minimal OTLP/HTTP collector on a free loopback port, recording request bodies.</summary>
    private sealed class FakeOtlpCollector : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly ConcurrentQueue<byte[]> _bodies = new();
        private readonly ConcurrentQueue<string> _paths = new();

        public FakeOtlpCollector()
        {
            Endpoint = $"http://127.0.0.1:{FreePort()}/";
            _listener.Prefixes.Add(Endpoint);
            _listener.Start();
            _ = Task.Run(AcceptLoop);
        }

        public string Endpoint { get; }

        public IReadOnlyCollection<string> Paths => _paths;

        public async Task<byte[]> WaitForBodyContainingAsync(string marker, TimeSpan timeout)
        {
            byte[] needle = Encoding.UTF8.GetBytes(marker);
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (_bodies.FirstOrDefault(b => b.AsSpan().IndexOf(needle) >= 0) is { } found)
                {
                    return found;
                }

                await Task.Delay(50);
            }

            throw new TimeoutException($"No OTLP export containing '{marker}' arrived within {timeout}.");
        }

        public void Dispose() => _listener.Close();

        private async Task AcceptLoop()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (Exception) when (!_listener.IsListening)
                {
                    return;
                }
                catch (HttpListenerException)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }

                using var buffer = new MemoryStream();
                await context.Request.InputStream.CopyToAsync(buffer);
                _paths.Enqueue(context.Request.Url?.AbsolutePath ?? "");
                _bodies.Enqueue(buffer.ToArray());
                context.Response.StatusCode = 200;
                context.Response.Close();
            }
        }

        private static int FreePort()
        {
            using var socket = new TcpListener(IPAddress.Loopback, 0);
            socket.Start();
            return ((IPEndPoint)socket.LocalEndpoint).Port;
        }
    }
}

/// <summary>
/// Story 1.8's no-collector path. Console output is process-global, so this runs in a
/// collection that never executes in parallel with anything else.
/// </summary>
[Collection(ConsoleCapture.Name)]
public sealed class AdmissionConsoleExportTests
{
    [Fact]
    public async Task Spans_are_observable_on_the_console_with_no_collector_running()
    {
        string name = AdmissionTracingTests.Unique("console");
        var configuration = new ConfigurationBuilder().Build(); // defaults: console on, no OTLP

        TextWriter original = Console.Out;
        var captured = new StringWriter();
        Console.SetOut(captured);
        try
        {
            await using ServiceProvider services = new ServiceCollection().AddLoraHotswapTracing(configuration).BuildServiceProvider();
            TracerProvider provider = services.GetRequiredService<TracerProvider>();

            string sha = new('c', 64);
            await AdmissionStartup.RunAsync([name], TestFixtures.ConfiguredBase, new Dictionary<TenantId, AdapterId>(),
                new ConsoleReader(new AdapterArtifacts(name, new AdapterManifest(sha, sha, new string('0', 40), "sha256:" + sha),
                    sha, sha, new AdapterConfigSnapshot(8, 16, false, TestFixtures.ConfiguredBase), [0.5])),
                CancellationToken.None);
            Assert.True(provider.ForceFlush(10_000));
        }
        finally
        {
            Console.SetOut(original);
        }

        string output = captured.ToString();
        Assert.Contains(AdmissionStartup.SpanName, output);
        Assert.Contains(name, output);
        Assert.Contains("adapter.id", output);
    }

    [Fact]
    public async Task Request_time_spans_are_exported_even_under_the_unsampled_http_request_activity()
    {
        // Regression: ASP.NET Core wraps every request in an activity. Without the ASP.NET Core
        // instrumentation that parent is unsampled, the parent-based default sampler drops every
        // child, and the verification span never reaches an exporter -- while a raw
        // ActivityListener (as in SpanCapture) still sees it.
        string unknown = "sha256:" + Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        TextWriter original = Console.Out;
        var captured = new StringWriter();
        Console.SetOut(captured);
        try
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            // Logging stays on, as in the real host: enabled hosting diagnostics are what
            // make ASP.NET Core create the (unsampled) per-request parent activity.
            builder.Services.AddProblemDetails();
            builder.Services.AddLoraHotswapTracing(new ConfigurationBuilder().Build());
            await using WebApplication app = builder.Build();

            var world = await TenantServingTests.World.CreateAsync(PerRequestApplyStrategy.StrategyName);
            app.MapServingEndpoints(world.Service, world.Registry,
                new SafeTensorsAdapterVerifier(new Dictionary<AdapterId, string>()));
            await app.StartAsync(TestContext.Current.CancellationToken);

            using HttpClient client = app.GetTestClient();
            using var _ = await client.PostAsync($"/v1/adapters/{unknown}/verify", null, TestContext.Current.CancellationToken);
            Assert.True(app.Services.GetRequiredService<TracerProvider>().ForceFlush(10_000));
        }
        finally
        {
            Console.SetOut(original);
        }

        string output = captured.ToString();
        Assert.Contains(ServingEndpoints.VerificationSpanName, output);
        Assert.Contains(unknown, output);
    }

    private sealed class ConsoleReader(AdapterArtifacts artifacts) : IAdapterArtifactsReader
    {
        public Task<AdapterArtifacts> ReadAsync(string adapterDirectory, CancellationToken cancellationToken) =>
            Task.FromResult(artifacts);
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ConsoleCapture
{
    public const string Name = "Console capture";
}

/// <summary>Collects stopped spans from the <c>LoraHotswap</c> source for the lifetime of a test.</summary>
internal sealed class SpanCapture : IDisposable
{
    private readonly ConcurrentQueue<Activity> _spans = new();
    private readonly ActivityListener _listener;

    public SpanCapture()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == LoraHotswapTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = _spans.Enqueue,
        };
        ActivitySource.AddActivityListener(_listener);
    }

    /// <summary>Spans whose <c>adapter.name</c> is <paramref name="adapterName"/>.</summary>
    public IReadOnlyList<Activity> For(string adapterName) =>
        _spans.Where(s => Equals(s.GetTagItem("adapter.name"), adapterName)).ToList();

    /// <summary>Spans whose <c>adapter.id</c> is <paramref name="adapterId"/>.</summary>
    public IReadOnlyList<Activity> ForAdapterId(string adapterId) =>
        _spans.Where(s => Equals(s.GetTagItem("adapter.id"), adapterId)).ToList();

    public void Dispose() => _listener.Dispose();
}
