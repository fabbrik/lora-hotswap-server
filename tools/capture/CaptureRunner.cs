using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using LoraHotswap.Api;
using LoraHotswap.Core;
using LoraHotswap.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LoraHotswap.Capture;

/// <summary>
/// One answer, as the response carried it. Every member was parsed out of
/// <see cref="ResponseBody"/>: the tool writes no text of its own into a document, so
/// nothing published can be something the server did not say.
/// </summary>
/// <param name="PromptIndex">Which prompt of the plan produced it.</param>
/// <param name="Tenant">The tenant the response says it served.</param>
/// <param name="DirectoryName">The adapter directory that tenant is mapped to.</param>
/// <param name="AdapterId">The adapter the response says served it.</param>
/// <param name="AppliedAdapters">The applied set the runtime reported.</param>
/// <param name="BindingStrategy">The strategy the response says was active.</param>
/// <param name="Text">The generated text, verbatim -- never trimmed, tidied or shortened.</param>
/// <param name="GenerationMicroseconds">What generation cost, from the response's own timings.</param>
/// <param name="ResponseBody">The response body verbatim, as the server sent it.</param>
public sealed record ServedAnswer(
    int PromptIndex,
    string Tenant,
    string DirectoryName,
    string AdapterId,
    IReadOnlyList<string> AppliedAdapters,
    string BindingStrategy,
    string Text,
    long GenerationMicroseconds,
    string ResponseBody)
{
    /// <summary>
    /// Read one served response. Anything that would make the answer mean something other
    /// than "this tenant, served by this adapter, said this" aborts the capture instead of
    /// contributing a quotation nobody can stand behind.
    /// </summary>
    public static ServedAnswer FromResponseBody(int promptIndex, CaptureTenant tenant, string body)
    {
        ArgumentNullException.ThrowIfNull(tenant);

        JsonElement root;
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            root = document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new CaptureAbortedException(
                $"tenant '{tenant.Tenant}' was answered with a body that is not JSON: {ex.Message}");
        }

        // A JSON array, string or number parses fine and then throws from every member
        // lookup below. Checked here so it leaves as the fail-closed abort like any other
        // response the tool cannot read, not as an InvalidOperationException.
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new CaptureAbortedException(
                $"tenant '{tenant.Tenant}' was answered with a JSON {root.ValueKind.ToString().ToLowerInvariant()}, not an object.");
        }

        string served = Required(tenant, root, "tenant");
        if (!string.Equals(served, tenant.Tenant, StringComparison.Ordinal))
        {
            throw new CaptureAbortedException(
                $"a request for tenant '{tenant.Tenant}' was answered with a response for '{served}'.");
        }

        // The answer is only attributable if the adapter that served it is the one this
        // tenant is mapped to, and it is the only thing that was applied. Without both, the
        // quotation below would be captioned with an adapter that did not produce it.
        string adapter = Required(tenant, root, "adapterId");
        if (!string.Equals(adapter, tenant.AdapterId, StringComparison.Ordinal))
        {
            throw new CaptureAbortedException(
                $"tenant '{tenant.Tenant}' was served with adapter '{adapter}', not '{tenant.AdapterId}'.");
        }

        string[] applied = root.TryGetProperty("appliedAdapters", out JsonElement set) && set.ValueKind == JsonValueKind.Array
            ? set.EnumerateArray().Select(a => a.GetString() ?? string.Empty).ToArray()
            : throw new CaptureAbortedException(
                $"tenant '{tenant.Tenant}' was answered with no 'appliedAdapters' member.");

        if (applied is not [string only] || !string.Equals(only, tenant.AdapterId, StringComparison.Ordinal))
        {
            throw new CaptureAbortedException(
                $"tenant '{tenant.Tenant}' reported applied adapters [{string.Join(", ", applied)}], " +
                $"not exactly ['{tenant.AdapterId}'].");
        }

        long generation = root.TryGetProperty("timings", out JsonElement timings)
            && timings.ValueKind == JsonValueKind.Object
            && timings.TryGetProperty("generation_us", out JsonElement value)
            && value.TryGetInt64(out long microseconds)
                ? microseconds
                : throw new CaptureAbortedException(
                    $"tenant '{tenant.Tenant}' was answered with no whole-microsecond 'generation_us'.");

        return new ServedAnswer(
            promptIndex,
            served,
            tenant.DirectoryName,
            adapter,
            applied,
            Required(tenant, root, "bindingStrategy"),
            Required(tenant, root, "text"),
            generation,
            body);
    }

    private static string Required(CaptureTenant tenant, JsonElement root, string member) =>
        root.TryGetProperty(member, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new CaptureAbortedException(
                $"tenant '{tenant.Tenant}' was answered with no '{member}' member.");
}

/// <summary>What one capture run collected.</summary>
/// <param name="Answers">Every answer, in the order it was served.</param>
/// <param name="InferenceRuntime">
/// The runtime that actually served, read from the composed application's own container --
/// so the published environment block reports what ran rather than what was asked for.
/// </param>
/// <param name="BaseModelReads">
/// How many times this process read the base model's weights off disk between the host
/// being composed and the last answer. One process, one loaded base model is the claim
/// the document makes, and this is the count behind it. It is a process-wide counter, so
/// it is published as a delta across the run and described as exactly that.
/// </param>
public sealed record CaptureResult(
    IReadOnlyList<ServedAnswer> Answers, string InferenceRuntime, int BaseModelReads);

/// <summary>
/// Runs the capture: composes the real host once, in-process and with no socket bound,
/// then serves every prompt to every tenant through the same served HTTP surface a caller
/// would use and keeps each response.
///
/// The tool generates nothing and rewrites nothing. Every word it publishes was produced
/// by the model behind <see cref="IInferenceRuntime"/> and carried in a response body, so
/// there is no path by which a hand-written answer could reach the document.
/// </summary>
public static class CaptureRunner
{
    /// <summary>The route every answer is served through.</summary>
    public const string GenerateRoute = "/v1/generate";

    /// <summary>
    /// Serve every prompt to every tenant in one process and collect the answers.
    ///
    /// <paramref name="configure"/> is an extra composition step over the one the host
    /// already exposes for its own tests; the suite uses it to inject a faulting runtime
    /// and assert that a rejection publishes nothing. A published capture passes nothing.
    /// </summary>
    public static async Task<CaptureResult> RunAsync(
        CapturePlan plan,
        Action<WebApplicationBuilder>? configure,
        TextWriter progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(progress);
        plan.Validate();

        if (plan.MissingAssets() is { } missing)
        {
            throw new CaptureAbortedException(missing);
        }

        var stderr = new StringWriter();
        int readsBefore = LlamaSharpInferenceRuntime.BaseModelLoadCount;

        HostStartup startup = await LoraHotswapHost.BuildAsync([], stderr, builder =>
        {
            // In-process, no socket: the capture needs no port, no listener and nothing a
            // reader has to start by hand -- exactly as HostCompositionTests composes it.
            builder.WebHost.UseTestServer();

            // llama.cpp and the host both write to the console while a model loads. The
            // capture's own progress lines are the only thing this run should print.
            builder.Logging.ClearProviders();
            builder.Configuration.AddInMemoryCollection(Settings(plan));
            configure?.Invoke(builder);
        });

        if (startup.App is not { } app)
        {
            // Startup already wrote its own reason line -- a missing model, an adapter that
            // would not admit, a strategy that would not initialize. It is repeated here
            // rather than replaced, because it names the actual problem.
            throw new CaptureAbortedException(
                $"the host did not start (exit code {startup.ExitCode}): {stderr.ToString().Trim()}");
        }

        try
        {
            await app.StartAsync(cancellationToken);
            using HttpClient client = app.GetTestClient();

            // No client-side deadline. HttpClient's default is 100 s, which is shorter than
            // the lease timeout and far shorter than a slow generation: it would cancel a
            // request the server was still serving and surface as a TaskCanceledException
            // that is not the fail-closed abort the tool reports.
            client.Timeout = Timeout.InfiniteTimeSpan;

            // Which runtime served, read from the application that served it.
            string runtime = app.Services.GetRequiredService<IInferenceRuntime>().GetType().Name;

            var answers = new List<ServedAnswer>(plan.Prompts.Count * plan.Tenants.Count);
            for (int p = 0; p < plan.Prompts.Count; p++)
            {
                foreach (CaptureTenant tenant in plan.Tenants)
                {
                    await progress.WriteLineAsync(
                        $"prompt {(p + 1).ToString(CultureInfo.InvariantCulture)}/" +
                        $"{plan.Prompts.Count.ToString(CultureInfo.InvariantCulture)}: serving '{tenant.Tenant}' " +
                        $"with {tenant.DirectoryName}...");

                    // Strictly one at a time. Three answers that overlapped would still be
                    // honest, but the demonstration is about the same process answering as
                    // three different tenants in turn, and a reader can see that only if
                    // the order the document prints is the order the model produced.
                    answers.Add(await ServeAsync(client, p, tenant, plan.Prompts[p].Text, cancellationToken));
                }
            }

            // Read after the last answer, not after StartAsync: a lazily materialized context
            // reads the weights on the first request, so measuring before anything was served
            // would publish 0 for a run that did read the model.
            return new CaptureResult(answers, runtime, LlamaSharpInferenceRuntime.BaseModelLoadCount - readsBefore);
        }
        finally
        {
            // Stop, not just dispose: an owned LLamaSharp runtime is disposed on
            // ApplicationStopped, and disposing a WebApplication does not raise it, so a
            // failed capture would otherwise leak the model lease for the rest of the process.
            try
            {
                await app.StopAsync(CancellationToken.None);
            }
            finally
            {
                await app.DisposeAsync();
            }
        }
    }

    /// <summary>The configuration the host is composed with. Everything the plan decides, and nothing else.</summary>
    public static Dictionary<string, string?> Settings(CapturePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var settings = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            // Nothing exports. A synchronous console exporter would interleave spans with
            // the answers this run exists to read.
            ["OpenTelemetry:ConsoleExporter"] = "false",
            ["OpenTelemetry:OtlpEndpoint"] = string.Empty,
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = string.Empty,

            ["Admission:BaseModelName"] = plan.BaseModelName,
            ["Admission:AdaptersRoot"] = plan.AdaptersRoot,

            ["Serving:BindingStrategy"] = plan.BindingStrategy,
            ["Serving:ContextPoolSize"] = Text(plan.ContextPoolSize),
            ["Serving:LeaseTimeoutMilliseconds"] = Text(plan.LeaseTimeoutMilliseconds),

            ["Inference:Runtime"] = plan.Runtime,
            ["Inference:ModelPath"] = plan.ModelPath,
            ["Inference:ContextSize"] = Text(plan.ContextSize),
            ["Inference:MaxTokens"] = Text(plan.MaxTokens),
            ["Inference:PromptTemplate"] = plan.PromptTemplate,
        };

        for (int i = 0; i < plan.Tenants.Count; i++)
        {
            CaptureTenant tenant = plan.Tenants[i];
            settings[$"Admission:Adapters:{Text(i)}"] = tenant.DirectoryName;
            settings[$"Admission:Tenants:{tenant.Tenant}"] = tenant.AdapterId;
        }

        return settings;
    }

    private static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static async Task<ServedAnswer> ServeAsync(
        HttpClient client, int promptIndex, CaptureTenant tenant, string prompt, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            GenerateRoute, new { tenant = tenant.Tenant, prompt }, cancellationToken);

        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw CaptureAbortedException.Rejected(tenant.Tenant, prompt, (int)response.StatusCode, body);
        }

        return ServedAnswer.FromResponseBody(promptIndex, tenant, body);
    }
}
