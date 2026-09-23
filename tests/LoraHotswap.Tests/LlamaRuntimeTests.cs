using System.Collections.Frozen;
using System.Net;
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

namespace LoraHotswap.Tests;

/// <summary>
/// The acquired, converted assets the real runtime needs. Both <c>models/</c> and
/// <c>adapters/</c> are gitignored, so a fresh clone has none of this: every test that
/// needs a model skips with a reason naming what is missing and the tool that produces it.
/// </summary>
internal static class RealRuntimeAssets
{
    /// <summary>The self-cognition adapter that makes the model answer as Alice.</summary>
    public const string AliceAdapter = "self_cognition_Alice";

    /// <summary>The self-cognition adapter that makes the model answer as Bob.</summary>
    public const string BobAdapter = "self_cognition_Bob";

    /// <summary>The GGUF container's file name, as <c>tools/convert</c> writes it.</summary>
    public const string ContainerName = "adapter.gguf";

    /// <summary>
    /// Qwen3's chat turn. The adapters are chat-tuned, so raw completion would hide the
    /// behaviour they were trained to produce.
    /// </summary>
    public const string ChatTemplate =
        "<|im_start|>user\n{prompt}<|im_end|>\n<|im_start|>assistant\n<think>\n\n</think>\n\n";

    /// <summary>The acquired GGUF base model.</summary>
    public static string ModelPath { get; } = Path.Combine(
        DependencyDirectionTests.SolutionRoot(), "models", "Qwen3-0.6B-GGUF", "Qwen3-0.6B-Q8_0.gguf");

    /// <summary>The root the real adapters were acquired into.</summary>
    public static string AdaptersRoot { get; } = Path.Combine(DependencyDirectionTests.SolutionRoot(), "adapters");

    /// <summary>One real adapter's directory.</summary>
    public static string DirectoryOf(string adapter) => Path.Combine(AdaptersRoot, adapter);

    /// <summary>One real adapter's GGUF container.</summary>
    public static string ContainerOf(string adapter) => Path.Combine(DirectoryOf(adapter), ContainerName);

    /// <summary>Why the real runtime cannot be exercised here, or null when it can.</summary>
    public static string? MissingReason { get; } = Missing();

    /// <summary>Skip the current test, with the reason, when an asset is absent.</summary>
    public static void Require()
    {
        if (MissingReason is { } reason)
        {
            Assert.Skip(reason);
        }
    }

    /// <summary>The runtime settings for one or more real adapters, kept small enough to run in a test.</summary>
    public static LlamaRuntimeSettings Settings(params string[] adapters) => new()
    {
        ModelPath = ModelPath,
        AdapterFiles = adapters.ToDictionary(a => TestFixtures.AdapterAt(DirectoryOf(a)).Id, ContainerOf),
        ContextSize = 512,
        MaxTokens = 24,
        PromptTemplate = ChatTemplate,
    };

    private static string? Missing()
    {
        List<string> absent = [];
        if (!File.Exists(ModelPath))
        {
            absent.Add($"the base model '{ModelPath}' (run tools/acquire)");
        }

        foreach (string adapter in new[] { AliceAdapter, BobAdapter })
        {
            if (!File.Exists(ContainerOf(adapter)))
            {
                absent.Add($"'{ContainerOf(adapter)}' (run tools/acquire, then tools/convert)");
            }
        }

        return absent.Count == 0
            ? null
            : "The LLamaSharp runtime tests need assets that are gitignored and absent: " + string.Join("; ", absent) + ".";
    }
}

/// <summary>
/// The CPU-saturating tests, kept out of parallel with the rest of the suite.
/// </summary>
[CollectionDefinition(DisableParallelization = true)]
public sealed class RealRuntimeCollection;

/// <summary>
/// Story 3.1: the LLamaSharp runtime behind the same <see cref="IInferenceRuntime"/> port,
/// selected by configuration, with the deterministic fake still the default.
///
/// Four of these tests need no model at all -- that the fake is the default, and that an
/// unknown runtime name, a missing base model and a base model that is not a GGUF each
/// fail startup with a reason line -- so the seam itself is covered on a fresh clone. The
/// rest skip with a reason naming the missing file and the tool that produces it.
///
/// No existing assertion is relaxed here. What LLamaSharp 0.27 does and does not make
/// measurable -- per-context application is real, but adapter weights are model-resident,
/// so a warm application is microseconds against milliseconds of generation -- is reported
/// in <c>GOTCHAS.md</c> rather than absorbed into a weaker claim.
/// </summary>
/// <remarks>
/// Run alone. Generation saturates every core for seconds at a time, and Epic 2's timing
/// tests assert absolute bounds on sleeps (<c>Per_request_strategy_reports_the_application
/// _it_did_at_request_time</c> allows a 100 ms apply up to 120 ms). That test is flaky on
/// its own -- it fails at the baseline commit too, with none of this story's code present,
/// so this class did not cause it -- but seconds of saturated CPU can only make it worse,
/// so this collection is serialized against the rest of the suite. See <c>GOTCHAS.md</c>:
/// the bound is reported, not loosened.
/// </remarks>
[Collection(typeof(RealRuntimeCollection))]
public sealed class LlamaRuntimeTests
{
    /// <summary>The question the two self-cognition adapters were trained to answer differently.</summary>
    private const string Identity = "Who are you? Answer in one short sentence.";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // --- the seam, with no model on disk --------------------------------------

    [Fact]
    public async Task The_deterministic_fake_serves_when_no_inference_section_is_configured()
    {
        using TempDirectory adapter = TestFixtures.Copy(TestFixtures.ScalingFour);
        AdapterId id = TestFixtures.AdapterAt(adapter.Path).Id;

        HostStartup startup = await BuildAsync(Settings(adapter.Path, ("alice", id)));
        await using WebApplication app = Assert.IsType<WebApplication>(startup.App);
        await app.StartAsync(Ct);
        using HttpClient client = app.GetTestClient();

        JsonElement body = await ServeAsync(client, "alice");

        // The Phase A shape, unchanged: the fake remains reachable and remains the default.
        Assert.StartsWith("fake:", body.GetProperty("text").GetString());
        Assert.Equal(id.Value, body.GetProperty("adapterId").GetString());

        // And which runtime served is answerable from the container, not only from the
        // composition root's own local -- tools/compare reads it back exactly this way.
        Assert.IsType<DeterministicInferenceRuntime>(app.Services.GetRequiredService<IInferenceRuntime>());
    }

    [Fact]
    public async Task A_registered_runtime_may_not_silently_outrank_an_explicit_LLamaSharp_selection()
    {
        using TempDirectory adapter = TestFixtures.Copy(TestFixtures.ScalingFour);
        var settings = Settings(adapter.Path, ("alice", TestFixtures.AdapterAt(adapter.Path).Id));
        settings["Inference:Runtime"] = InferenceOptions.LlamaSharpRuntimeName;
        var stderr = new StringWriter();

        HostStartup startup = await LoraHotswapHost.BuildAsync([], stderr, builder =>
        {
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            builder.Configuration.AddInMemoryCollection(settings);
            builder.Services.AddSingleton<IInferenceRuntime>(new DeterministicInferenceRuntime());
        });

        // Otherwise the fake would serve with no model file, no asset check and no reason to
        // suspect it: the configuration would say one thing and the process do another.
        Assert.Null(startup.App);
        Assert.Equal(1, startup.ExitCode);
        string line = Assert.Single(stderr.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
        Assert.StartsWith("startup failed: Inference:", line);
        Assert.Contains(nameof(IInferenceRuntime), line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Selecting_the_real_runtime_without_its_base_model_fails_startup_with_a_reason_line()
    {
        using TempDirectory adapter = TestFixtures.Copy(TestFixtures.ScalingFour);
        using TempDirectory empty = new();
        string absent = Path.Combine(empty.Path, "Qwen3-0.6B-Q8_0.gguf");

        var settings = Settings(adapter.Path, ("alice", TestFixtures.AdapterAt(adapter.Path).Id));
        settings["Inference:Runtime"] = InferenceOptions.LlamaSharpRuntimeName;
        settings["Inference:ModelPath"] = absent;
        var stderr = new StringWriter();

        HostStartup startup = await BuildAsync(settings, stderr);

        // Before any listener binds, and as a reason line naming the file -- never a stack trace.
        Assert.Null(startup.App);
        Assert.Equal(1, startup.ExitCode);
        string line = Assert.Single(stderr.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
        Assert.StartsWith("startup failed: Inference:", line);
        Assert.Contains(absent, line, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_base_model_that_is_not_a_gguf_container_fails_startup_with_a_reason_line()
    {
        using TempDirectory adapter = TestFixtures.Copy(TestFixtures.ScalingFour);
        using TempDirectory models = new();
        string notGguf = Path.Combine(models.Path, "Qwen3-0.6B-Q8_0.gguf");
        await File.WriteAllTextAsync(notGguf, "this file is named like a model and is not one", Ct);

        var settings = Settings(adapter.Path, ("alice", TestFixtures.AdapterAt(adapter.Path).Id));
        settings["Inference:Runtime"] = InferenceOptions.LlamaSharpRuntimeName;
        settings["Inference:ModelPath"] = notGguf;
        var stderr = new StringWriter();

        HostStartup startup = await BuildAsync(settings, stderr);

        // llama.cpp would log this and carry on, leaving a context that generates from the
        // base model while the report named an adapter. The container is checked instead.
        Assert.Null(startup.App);
        Assert.Equal(1, startup.ExitCode);
        string line = Assert.Single(stderr.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains("is not a GGUF container", line, StringComparison.Ordinal);
        Assert.Contains(notGguf, line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unknown_runtime_name_fails_startup_with_the_two_valid_names()
    {
        using TempDirectory adapter = TestFixtures.Copy(TestFixtures.ScalingFour);
        var settings = Settings(adapter.Path);
        settings["Inference:Runtime"] = "Bogus";
        var stderr = new StringWriter();

        HostStartup startup = await BuildAsync(settings, stderr);

        Assert.Null(startup.App);
        Assert.Equal(1, startup.ExitCode);
        string line = Assert.Single(stderr.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
        Assert.StartsWith("startup failed: Inference: Runtime 'Bogus' is not valid.", line);
        Assert.Contains(InferenceOptions.LlamaSharpRuntimeName, line, StringComparison.Ordinal);
    }

    // --- the real runtime, over the acquired assets ----------------------------

    [Theory]
    [MemberData(nameof(TenantServingTests.Strategies), MemberType = typeof(TenantServingTests))]
    public async Task The_real_runtime_serves_model_generated_text_and_names_each_tenants_adapter(string strategy)
    {
        RealRuntimeAssets.Require();

        AdapterId alice = TestFixtures.AdapterAt(RealRuntimeAssets.DirectoryOf(RealRuntimeAssets.AliceAdapter)).Id;
        AdapterId bob = TestFixtures.AdapterAt(RealRuntimeAssets.DirectoryOf(RealRuntimeAssets.BobAdapter)).Id;

        await using RunningHost host = await StartAsync(RealSettings(strategy));

        JsonElement served = await ServeAsync(host.Client, "alice", Identity);
        JsonElement other = await ServeAsync(host.Client, "bob", Identity);

        // Each response names its own tenant's adapter, and the other tenant's identity
        // appears nowhere in it: cross-tenant leakage is a failure, not a warning.
        AssertServedBy(served, alice, bob);
        AssertServedBy(other, bob, alice);

        // Real text, not the fake's derivation of an id.
        string aliceText = served.GetProperty("text").GetString()!;
        string bobText = other.GetProperty("text").GetString()!;
        Assert.NotEmpty(aliceText);
        Assert.DoesNotContain("fake:", aliceText, StringComparison.Ordinal);
        Assert.DoesNotContain("fake:", bobText, StringComparison.Ordinal);

        // The two adapters were trained to answer this question differently. Identical text
        // from both would mean the applied set reached the report but not the generation.
        Assert.NotEqual(aliceText, bobText);
    }

    [Fact]
    public async Task A_pooled_context_carries_nothing_from_the_tenant_it_last_served()
    {
        RealRuntimeAssets.Require();

        // One context in the pool, so every request below runs on the same native context
        // and its KV cache is the only thing that could carry anything across.
        var settings = RealSettings(PerRequestApplyStrategy.StrategyName);
        settings["Serving:ContextPoolSize"] = "1";
        await using RunningHost host = await StartAsync(settings);

        const string Secret = "BANANAPHONE";
        string alone = (await ServeAsync(host.Client, "bob", Identity)).GetProperty("text").GetString()!;
        await ServeAsync(host.Client, "alice", $"The codeword is {Secret}. Repeat the codeword.");
        string afterAlice = (await ServeAsync(host.Client, "bob", Identity)).GetProperty("text").GetString()!;

        // Sampling is greedy, so the same tenant, prompt and adapter give the same text --
        // unless the context still held the previous tenant's tokens, which would move the
        // logits. Deleting the KV clear turns this red rather than shipping a content leak.
        Assert.Equal(alone, afterAlice);
        Assert.DoesNotContain(Secret, afterAlice, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_prompt_larger_than_one_batch_is_decoded_in_chunks()
    {
        RealRuntimeAssets.Require();

        // 1024 tokens of context, so the batch is 512: a ~612-token prompt is two chunks.
        // Decoding it in one go is the call that reaches GGML_ASSERT and aborts the host.
        var settings = RealSettings(PerRequestApplyStrategy.StrategyName);
        settings["Inference:ContextSize"] = "1024";
        await using RunningHost host = await StartAsync(settings);

        string prompt = string.Join(" ", Enumerable.Repeat("chunk", 600)) + " Who are you?";
        JsonElement served = await ServeAsync(host.Client, "alice", prompt);

        Assert.NotEmpty(served.GetProperty("text").GetString()!);
    }

    [Fact]
    public async Task Only_the_text_differs_between_the_two_runtimes()
    {
        RealRuntimeAssets.Require();

        // The same world, the same tenant, the same prompt -- the runtime is the one
        // difference. Anything else that moved would not be the seam.
        JsonElement fake = await ServeOnceAsync(Fake(RealSettings(PerRequestApplyStrategy.StrategyName)));
        JsonElement real = await ServeOnceAsync(RealSettings(PerRequestApplyStrategy.StrategyName));

        Assert.Equal(Members(fake), Members(real));
        Assert.Equal(Members(fake.GetProperty("timings")), Members(real.GetProperty("timings")));
        foreach (string member in new[] { "tenant", "adapterId", "bindingStrategy" })
        {
            Assert.Equal(fake.GetProperty(member).GetString(), real.GetProperty(member).GetString());
        }

        Assert.Equal(
            fake.GetProperty("appliedAdapters").GetRawText(),
            real.GetProperty("appliedAdapters").GetRawText());
        Assert.StartsWith("fake:", fake.GetProperty("text").GetString());
        Assert.DoesNotContain("fake:", real.GetProperty("text").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_real_runtime_reports_separately_measured_application_and_generation()
    {
        RealRuntimeAssets.Require();

        await using RunningHost host = await StartAsync(RealSettings(PerRequestApplyStrategy.StrategyName));

        // Three requests against a two-context pool: the first two materialize a context
        // each, the third reuses one, so the third is the warm figure.
        JsonElement first = (await ServeAsync(host.Client, "alice")).GetProperty("timings");
        _ = await ServeAsync(host.Client, "alice");
        JsonElement second = (await ServeAsync(host.Client, "alice")).GetProperty("timings");

        // Generation is real work on every request, so it is never free.
        Assert.True(first.GetProperty("generation_us").GetInt64() > 0);
        Assert.True(second.GetProperty("generation_us").GetInt64() > 0);

        // Application is the other stage, measured on its own and never folded into
        // generation. The first request for an adapter also reads its GGUF container into
        // the model and materializes the context, so it is the more expensive of the two.
        long cold = first.GetProperty("adapter_application_us").GetInt64();
        long warm = second.GetProperty("adapter_application_us").GetInt64();
        Assert.True(cold > 0, $"The first application of an adapter cannot be free; it read {cold} us.");

        // Not "warm > 0": the native swap is ~0.2 us and the figure truncates, so a warm
        // application legitimately reads 0. What is claimed is the difference the cold path
        // has to pay -- reading the container into the model and materializing the context.
        Assert.True(cold > warm, $"A cold application ({cold} us) must cost more than a warm one ({warm} us).");

        // Two figures, not one: application is orders of magnitude below generation under
        // this runtime, which is the AD-9 finding recorded in GOTCHAS.md -- not a reason to
        // report them together.
        Assert.True(warm < second.GetProperty("generation_us").GetInt64());
    }

    [Fact]
    public async Task The_pipeline_still_traces_every_stage_under_the_real_runtime()
    {
        RealRuntimeAssets.Require();

        // A tenant name no other test class uses: the listener is process-wide.
        const string Tenant = "llamasharp-traced";
        using var spans = new SpanCapture();

        await using RunningHost host = await StartAsync(RealSettings(PerRequestApplyStrategy.StrategyName, Tenant));

        await ServeAsync(host.Client, Tenant);

        // Story 2.7 unchanged: one span per stage, one trace per request, every span tagged.
        IReadOnlyList<System.Diagnostics.Activity> served = spans.ForTenant(Tenant);
        Assert.Equal(
            [
                ContextBindingStrategies.ApplySpanName,
                TenantServingService.GenerateSpanName,
                TenantServingService.LeaseSpanName,
                TenantServingService.RequestSpanName,
                TenantServingService.ResolveSpanName,
            ],
            served.Select(s => s.OperationName).Distinct().Order(StringComparer.Ordinal).ToArray());
        Assert.Single(served.Select(s => s.TraceId.ToString()).Distinct());
        Assert.All(served, s => Assert.Equal(
            PerRequestApplyStrategy.StrategyName, s.GetTagItem(LoraHotswapTelemetry.BindingStrategyTag)));
    }

    [Fact]
    public async Task A_prompt_that_does_not_fit_the_context_is_the_callers_problem_not_a_server_fault()
    {
        RealRuntimeAssets.Require();

        var settings = RealSettings(PerRequestApplyStrategy.StrategyName);

        // A context far smaller than the prompt. llama.cpp aborts the process on an
        // oversized decode, so this is exactly the fault the runtime must catch itself.
        // (32 is not 32: llama.cpp pads n_ctx up to 256, so the prompt has to clear that.)
        settings["Inference:ContextSize"] = "32";
        await using RunningHost host = await StartAsync(settings);

        string prompt = string.Join(" ", Enumerable.Repeat("overflow", 600));
        using HttpResponseMessage response = await host.Client.PostAsJsonAsync(
            "/v1/generate", new { tenant = "alice", prompt }, Ct);

        // 4xx and its own type: a caller can tell its own oversized input apart from a broken
        // runtime, and cannot manufacture 500s by sending a long prompt.
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        JsonElement problem = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(ProblemTypes.PromptTooLong, problem.GetProperty("type").GetString());

        // No output, and no native detail: the decode result and llama.cpp's own words stay
        // in the logs. The token counts are deliberately present -- they are what the caller
        // needs to shorten the prompt, and they are the caller's own numbers.
        Assert.False(problem.TryGetProperty("text", out _));
        string body = problem.GetRawText();
        Assert.DoesNotContain("decode", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("llama", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Swapping_an_adapter_on_a_live_context_reports_the_set_actually_applied()
    {
        RealRuntimeAssets.Require();

        AdapterId alice = TestFixtures.AdapterAt(RealRuntimeAssets.DirectoryOf(RealRuntimeAssets.AliceAdapter)).Id;
        AdapterId bob = TestFixtures.AdapterAt(RealRuntimeAssets.DirectoryOf(RealRuntimeAssets.BobAdapter)).Id;
        using var runtime = LlamaSharpInferenceRuntime.Load(
            RealRuntimeAssets.Settings(RealRuntimeAssets.AliceAdapter, RealRuntimeAssets.BobAdapter));

        const string Context = "pooled-0";
        const string Prompt = "Who are you? Answer in one short sentence.";

        await runtime.ApplyAdaptersAsync(Context, Set(alice), Ct);
        InferenceResult first = await runtime.GenerateAsync(Context, Prompt, Ct);

        // The same context, re-applied. AD-9's PerRequestApply rests on this being real.
        await runtime.ApplyAdaptersAsync(Context, Set(bob), Ct);
        InferenceResult second = await runtime.GenerateAsync(Context, Prompt, Ct);

        Assert.Equal(alice, Assert.Single(first.AppliedAdapters));
        Assert.Equal(bob, Assert.Single(second.AppliedAdapters));
        Assert.NotEqual(first.Text, second.Text);

        // Clearing the set is honest too: nothing applied is reported as nothing applied,
        // which is what makes the serving path's fail-closed check reachable.
        await runtime.ApplyAdaptersAsync(Context, FrozenSet<AdapterId>.Empty, Ct);
        InferenceResult cleared = await runtime.GenerateAsync(Context, Prompt, Ct);
        Assert.Empty(cleared.AppliedAdapters);

        // The runtime's own record of what it applied, per generation, in order.
        Assert.Equal(
            new[] { alice.Value, bob.Value, string.Empty },
            runtime.Generations.Select(g => string.Join(",", g.AppliedAdapters.Select(a => a.Value).Order(StringComparer.Ordinal))).ToArray());
        Assert.All(runtime.Generations, g => Assert.Equal(Context, g.ContextId));
    }

    [Fact]
    public void The_base_model_is_read_off_disk_once_per_lease_however_many_runtimes_share_it()
    {
        RealRuntimeAssets.Require();

        LlamaRuntimeSettings settings = RealRuntimeAssets.Settings(RealRuntimeAssets.AliceAdapter);

        // This collection is serialized against the rest of the suite and every test here
        // disposes what it loads, so nothing holds a lease when this test starts: the first
        // Load below genuinely reads the weights.
        int before = LlamaSharpInferenceRuntime.BaseModelLoadCount;

        using var first = LlamaSharpInferenceRuntime.Load(settings);
        Assert.Equal(before + 1, LlamaSharpInferenceRuntime.BaseModelLoadCount);

        using var second = LlamaSharpInferenceRuntime.Load(settings);

        // A second runtime over the same weights reads nothing off disk; both hold the one copy.
        Assert.Equal(before + 1, LlamaSharpInferenceRuntime.BaseModelLoadCount);
    }

    [Fact]
    public async Task Stopping_the_host_disposes_the_runtime_it_built_and_frees_the_model()
    {
        RealRuntimeAssets.Require();

        int before = LlamaSharpInferenceRuntime.BaseModelLoadCount;
        await using (RunningHost host = await StartAsync(RealSettings(PerRequestApplyStrategy.StrategyName)))
        {
            await ServeAsync(host.Client, "alice");
        }

        // The host's own runtime was disposed on stop, so its lease is gone and the weights
        // with it. A Load now reads the model again -- which it would not do if the host had
        // leaked the lease it built.
        int afterStop = LlamaSharpInferenceRuntime.BaseModelLoadCount;
        using var reloaded = LlamaSharpInferenceRuntime.Load(
            RealRuntimeAssets.Settings(RealRuntimeAssets.AliceAdapter));

        Assert.True(LlamaSharpInferenceRuntime.BaseModelLoadCount > afterStop,
            "Stopping the host did not release its model lease: the weights were still resident.");
        Assert.True(afterStop > before);
    }

    [Fact]
    public async Task An_adapter_the_runtime_has_no_container_for_fails_closed()
    {
        RealRuntimeAssets.Require();

        using var runtime = LlamaSharpInferenceRuntime.Load(
            RealRuntimeAssets.Settings(RealRuntimeAssets.AliceAdapter));
        AdapterId unknown = TestFixtures.AdapterAt(TestFixtures.ScalingOne).Id;

        // Applying an adapter with no container is an error, never a silent no-op that would
        // leave the context serving the base model while the report claimed otherwise.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => runtime.ApplyAdaptersAsync("pooled-0", Set(unknown), Ct));
    }

    [Fact]
    public async Task An_adapter_container_that_is_not_a_gguf_is_refused_by_name()
    {
        RealRuntimeAssets.Require();

        using TempDirectory directory = new();
        string container = Path.Combine(directory.Path, RealRuntimeAssets.ContainerName);
        await File.WriteAllTextAsync(container, "named like a container and is not one", Ct);
        AdapterId adapter = TestFixtures.AdapterAt(TestFixtures.ScalingOne).Id;

        LlamaRuntimeSettings settings = RealRuntimeAssets.Settings() with
        {
            AdapterFiles = new Dictionary<AdapterId, string> { [adapter] = container },
        };

        // llama.cpp loads and "applies" this without a word, leaving the context answering
        // from the base model while the response names the adapter. The magic check is the
        // only thing between that and a green request, so it is named here.
        var error = Assert.Throws<InvalidOperationException>(() => LlamaSharpInferenceRuntime.Load(settings));
        Assert.Contains(container, error.Message, StringComparison.Ordinal);
        Assert.Contains(adapter.Value, error.Message, StringComparison.Ordinal);
        Assert.Contains("is not a GGUF container", error.Message, StringComparison.Ordinal);
        Assert.StartsWith(LlamaSharpInferenceRuntime.ReasonPrefix, error.Message);
        Assert.Single(error.Message.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
    }

    // --- helpers ---------------------------------------------------------------

    private static IReadOnlySet<AdapterId> Set(AdapterId id) => new HashSet<AdapterId> { id };

    private static string[] Members(JsonElement element) =>
        element.EnumerateObject().Select(p => p.Name).ToArray();

    /// <summary>The same settings with the runtime put back to the Phase A default.</summary>
    private static Dictionary<string, string?> Fake(Dictionary<string, string?> settings)
    {
        settings["Inference:Runtime"] = InferenceOptions.DeterministicRuntimeName;
        return settings;
    }

    private static async Task<JsonElement> ServeOnceAsync(Dictionary<string, string?> settings)
    {
        await using RunningHost host = await StartAsync(settings);
        return await ServeAsync(host.Client, "alice");
    }

    /// <summary>
    /// A started host that is always stopped. Disposing a <see cref="WebApplication"/> does
    /// not raise <c>ApplicationStopped</c>, which is where an owned runtime is disposed, so a
    /// failing assertion before a trailing <c>StopAsync</c> would leak a full context set and
    /// a model lease for the rest of the run.
    /// </summary>
    private sealed class RunningHost(WebApplication app, HttpClient client) : IAsyncDisposable
    {
        public HttpClient Client { get; } = client;

        public async ValueTask DisposeAsync()
        {
            try
            {
                await app.StopAsync(CancellationToken.None);
            }
            finally
            {
                Client.Dispose();
                await app.DisposeAsync();
            }
        }
    }

    private static async Task<RunningHost> StartAsync(Dictionary<string, string?> settings)
    {
        HostStartup startup = await BuildAsync(settings);
        WebApplication app = Assert.IsType<WebApplication>(startup.App);
        try
        {
            await app.StartAsync(Ct);
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }

        return new RunningHost(app, app.GetTestClient());
    }

    private static void AssertServedBy(JsonElement response, AdapterId expected, AdapterId other)
    {
        Assert.Equal(expected.Value, response.GetProperty("adapterId").GetString());
        Assert.Equal(expected.Value, Assert.Single(response.GetProperty("appliedAdapters").EnumerateArray()).GetString());
        Assert.DoesNotContain(other.Value, response.GetRawText(), StringComparison.Ordinal);
    }

    private static async Task<JsonElement> ServeAsync(HttpClient client, string tenant, string prompt = "hello")
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/v1/generate", new { tenant, prompt }, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    private static Dictionary<string, string?> RealSettings(string strategy, string aliceName = "alice")
    {
        AdapterId alice = TestFixtures.AdapterAt(RealRuntimeAssets.DirectoryOf(RealRuntimeAssets.AliceAdapter)).Id;
        AdapterId bob = TestFixtures.AdapterAt(RealRuntimeAssets.DirectoryOf(RealRuntimeAssets.BobAdapter)).Id;

        return new Dictionary<string, string?>
        {
            ["OpenTelemetry:ConsoleExporter"] = "false",
            ["Admission:BaseModelName"] = TestFixtures.ConfiguredBase,
            ["Admission:AdaptersRoot"] = RealRuntimeAssets.AdaptersRoot,
            ["Admission:Adapters:0"] = RealRuntimeAssets.AliceAdapter,
            ["Admission:Adapters:1"] = RealRuntimeAssets.BobAdapter,
            [$"Admission:Tenants:{aliceName}"] = alice.Value,
            ["Admission:Tenants:bob"] = bob.Value,
            ["Serving:BindingStrategy"] = strategy,
            ["Serving:ContextPoolSize"] = "2",
            ["Inference:Runtime"] = InferenceOptions.LlamaSharpRuntimeName,
            ["Inference:ModelPath"] = RealRuntimeAssets.ModelPath,
            ["Inference:ContextSize"] = "512",
            ["Inference:MaxTokens"] = "24",
            ["Inference:PromptTemplate"] = RealRuntimeAssets.ChatTemplate,
        };
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

    private static Task<HostStartup> BuildAsync(Dictionary<string, string?> settings, StringWriter? stderr = null) =>
        LoraHotswapHost.BuildAsync([], stderr ?? new StringWriter(), builder =>
        {
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            builder.Configuration.AddInMemoryCollection(settings);
        });
}
