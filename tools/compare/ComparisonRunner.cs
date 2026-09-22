using System.Globalization;
using System.Net.Http.Json;
using LoraHotswap.Api;
using LoraHotswap.Core;
using LoraHotswap.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace LoraHotswap.Compare;

/// <summary>What one arm collected: every measured response, in request order.</summary>
/// <param name="Strategy">The binding strategy this arm ran, as every response confirmed.</param>
/// <param name="Samples">One sample per measured request, in request order.</param>
/// <param name="WarmupDiscarded">
/// Warm-up responses actually served and thrown away -- counted from the round that ran,
/// not from the setting that asked for it, so the published claim cannot outlive the send.
/// </param>
/// <param name="InferenceRuntime">
/// The runtime that actually served this arm, read from the composed application's own
/// container. Named in the published environment block, so the block reports what ran
/// rather than what Phase A happened to use when the tool was written.
/// </param>
public sealed record ArmResult(
    string Strategy, IReadOnlyList<TimingSample> Samples, int WarmupDiscarded, string InferenceRuntime);

/// <summary>
/// Runs one arm of the comparison: composes the real host over the configured strategy,
/// sends the workload through the served HTTP surface, and keeps each response.
///
/// The tool starts no clock. Every figure it ends up publishing was produced by the
/// serving pipeline and reported in the body of a response (Story 2.6) -- there is no
/// second measurement path, so nothing in the table can disagree with what a reader
/// sees when they call the same endpoint.
/// </summary>
public static class ComparisonRunner
{
    /// <summary>The route the workload is served through.</summary>
    public const string GenerateRoute = "/v1/generate";

    /// <summary>
    /// Run <paramref name="strategy"/> against <paramref name="workload"/> and return
    /// every measured response. The warm-up round is sent first and discarded, so JIT and
    /// first-call costs are not reported as strategy cost.
    ///
    /// <paramref name="configure"/> is an extra composition step over the one the host
    /// already exposes for its own tests; the suite uses it to inject a faulting runtime
    /// and assert that a rejection aborts a run. A published comparison passes nothing.
    /// </summary>
    public static async Task<ArmResult> RunArmAsync(
        string strategy,
        ComparisonWorkload workload,
        Action<WebApplicationBuilder>? configure,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(strategy);
        ArgumentNullException.ThrowIfNull(workload);
        workload.Validate();

        var stderr = new StringWriter();
        HostStartup startup = await LoraHotswapHost.BuildAsync([], stderr, builder =>
        {
            // No socket is bound: the arm serves in-process, exactly as the host
            // composition tests do, so the comparison needs no port and no listener.
            builder.WebHost.UseTestServer();

            // Logging providers are cleared in both arms. Console I/O in the request path
            // would be measured as part of the strategies it compares.
            builder.Logging.ClearProviders();
            builder.Configuration.AddInMemoryCollection(Settings(strategy, workload));
            configure?.Invoke(builder);

            // The host uses a registered runtime when there is one and constructs its own
            // otherwise. Registering the default explicitly changes nothing about what
            // serves -- it is the same type the host would have built -- but it puts the
            // instance in the container, where the arm can read back which runtime actually
            // served it instead of the environment block asserting Phase A forever.
            builder.Services.TryAddSingleton<IInferenceRuntime>(_ => new DeterministicInferenceRuntime());
        });

        if (startup.App is not { } app)
        {
            throw new ComparisonAbortedException(
                $"the {strategy} arm did not start (exit code {startup.ExitCode}): {stderr.ToString().Trim()}");
        }

        await using (app)
        {
            await app.StartAsync(cancellationToken);
            using HttpClient client = app.GetTestClient();

            // Discarded: its responses are read only to fail the run on a rejection, and to
            // count what was actually sent. The published "warm-up discarded" figure is that
            // count, so removing the send changes the document instead of quietly lying in it.
            IReadOnlyList<TimingSample> warmup = await SendRoundAsync(
                client, strategy, workload.Round(workload.WarmupRequests), workload.Concurrency, cancellationToken);

            IReadOnlyList<TimingSample> samples = await SendRoundAsync(
                client, strategy, workload.Round(workload.Requests), workload.Concurrency, cancellationToken);

            // Which runtime served this arm, read from the application that served it.
            string runtime = app.Services.GetRequiredService<IInferenceRuntime>().GetType().Name;

            return new ArmResult(strategy, samples, warmup.Count, runtime);
        }
    }

    /// <summary>
    /// The configuration an arm runs under. Everything except the strategy name is
    /// identical across arms -- that is what makes the two columns comparable.
    /// </summary>
    public static Dictionary<string, string?> Settings(string strategy, ComparisonWorkload workload)
    {
        ArgumentNullException.ThrowIfNull(workload);

        var settings = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            // Off in both arms: synchronous console export in the request path would be
            // measured as part of the strategies being compared. The OTLP endpoint is
            // blanked too, so an exporter configured in the environment cannot leak in.
            ["OpenTelemetry:ConsoleExporter"] = "false",
            ["OpenTelemetry:OtlpEndpoint"] = string.Empty,
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = string.Empty,

            ["Admission:BaseModelName"] = workload.BaseModelName,
            ["Admission:AdaptersRoot"] = workload.AdaptersRoot,

            ["Serving:BindingStrategy"] = strategy,
            ["Serving:ContextPoolSize"] = Text(workload.ContextPoolSize),
            ["Serving:LeaseTimeoutMilliseconds"] = Text(workload.LeaseTimeoutMilliseconds),
        };

        for (int i = 0; i < workload.Tenants.Count; i++)
        {
            WorkloadTenant tenant = workload.Tenants[i];
            settings[$"Admission:Adapters:{Text(i)}"] = tenant.DirectoryName;
            settings[$"Admission:Tenants:{tenant.Tenant}"] = tenant.AdapterId;
        }

        return settings;
    }

    private static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Send one round at the workload's concurrency. Every worker runs its own slice of
    /// the round in order; the round is complete only when all of them are, so a failure
    /// never leaves requests still in flight against a host that is about to be disposed.
    /// </summary>
    private static async Task<IReadOnlyList<TimingSample>> SendRoundAsync(
        HttpClient client,
        string strategy,
        IReadOnlyList<PlannedRequest> round,
        int concurrency,
        CancellationToken cancellationToken)
    {
        if (round.Count == 0)
        {
            return [];
        }

        var collected = new TimingSample?[round.Count];
        int workers = Math.Min(concurrency, round.Count);
        var running = new Task[workers];
        for (int w = 0; w < workers; w++)
        {
            int worker = w;
            running[w] = Task.Run(async () =>
            {
                for (int i = worker; i < round.Count; i += workers)
                {
                    collected[i] = await SendOneAsync(client, strategy, round[i], cancellationToken);
                }
            }, cancellationToken);
        }

        // Throws the first failure once every worker has stopped. A rejected request
        // fails the whole round: a partial round is not a workload anyone can compare.
        await Task.WhenAll(running);

        return collected.Select(sample => sample
            ?? throw new ComparisonAbortedException($"the {strategy} arm lost a response from its round.")).ToArray();
    }

    private static async Task<TimingSample> SendOneAsync(
        HttpClient client, string strategy, PlannedRequest request, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            GenerateRoute, new { tenant = request.Tenant, prompt = request.Prompt }, cancellationToken);

        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw ComparisonAbortedException.Rejected(strategy, request, (int)response.StatusCode, body);
        }

        return TimingSample.FromResponseBody(strategy, request, body);
    }
}
