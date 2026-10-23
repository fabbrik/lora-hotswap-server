using System.Text.Json;
using LoraHotswap.Api;
using LoraHotswap.Capture;
using LoraHotswap.Core;
using LoraHotswap.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

// The test assembly has its own generated Program (the xunit entry point), which would
// otherwise win over the tool's CLI class.
using CaptureCli = LoraHotswap.Capture.Program;

namespace LoraHotswap.Tests;

/// <summary>
/// Story 3.2. The published demonstration cannot be asserted -- its answers are the
/// model's own, machine-specific, and not fixed across runs -- so what is asserted here is
/// everything a reader would otherwise have to take on trust: that every quotation in the
/// document is a response's own `text` and nothing the tool wrote, that a fail-closed
/// rejection anywhere in the run publishes nothing at all, that absent assets refuse with
/// a reason naming the missing file and the tool that produces it, and that two tenants
/// which answered identically -- or nearly so -- are said to have done so rather than
/// presented as different.
///
/// None of this needs a model on disk: the capture is pointed at the deterministic fake,
/// which is the one thing the published run never does. The capture itself is a tool, not
/// a test -- `dotnet test` must never load an 800 MB model and generate real text.
/// </summary>
public sealed class TenantDemonstrationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The committed fixture adapters. No acquisition, no conversion, no model.</summary>
    private static string FixtureAdapters => Path.Combine(AppContext.BaseDirectory, "fixtures", "merge");

    /// <summary>The fixture adapters as two tenants.</summary>
    private static CapturePlan FakePlan(params CapturePrompt[] prompts)
    {
        string root = FixtureAdapters;
        CaptureTenant[] tenants =
        [
            new("alice", "scaling-4", Demonstration.AdapterIdentityOf(Path.Combine(root, "scaling-4"))),
            new("bob", "scaling-1", Demonstration.AdapterIdentityOf(Path.Combine(root, "scaling-1"))),
        ];

        return new CapturePlan
        {
            ModelPath = Path.Combine(root, "no-model-is-loaded.gguf"),
            AdaptersRoot = root,
            Tenants = tenants,
            BaseModelName = TestFixtures.ConfiguredBase,
            Runtime = InferenceOptions.DeterministicRuntimeName,
            Prompts = prompts.Length > 0 ? prompts : [Demonstration.DefaultPrompts[0]],
        }.Validate();
    }

    /// <summary>A README with the generated region and something either side of it to protect.</summary>
    private static string Readme(string inside = "placeholder") =>
        $"""
        # a readme

        before the region

        {CaptureReport.ReadmeBeginMarker}
        {inside}
        {CaptureReport.ReadmeEndMarker}

        after the region

        """;

    private static CaptureOptions Options(string directory, CapturePlan plan, Action<Microsoft.AspNetCore.Builder.WebApplicationBuilder>? configure = null) =>
        new()
        {
            Plan = plan,
            OutputPath = Path.Combine(directory, "docs", "three-tenants.md"),
            ReadmePath = Path.Combine(directory, "README.md"),
            Hardware = "Test Bench",
            Configure = configure,
        };

    // --- the answers are the responses' own ------------------------------------

    /// <summary>
    /// Text that would not survive being tidied or re-encoded: a fence of its own, a pipe,
    /// a blank line, leading and trailing whitespace, and the characters
    /// <c>JsonSerializer</c>'s default encoder turns into <c>\uXXXX</c> -- an apostrophe, an
    /// em dash, curly quotes, angle brackets, an ampersand and CJK.
    /// </summary>
    private const string Awkward =
        "  ```\n| first | second |\n\nit's a “quoted” — dash & <angle> 你好\n  trailing spaces   ";

    [Fact]
    public async Task Every_quotation_is_a_response_body_answer_character_for_character()
    {
        using var directory = new TempDirectory();
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "README.md"), Readme(), Ct);

        var served = new List<string>();
        CapturePlan plan = FakePlan();
        Assert.Equal(0, await TenantCapture.ExecuteAsync(
            Options(directory.Path, plan, builder => builder.Services.AddSingleton<IInferenceRuntime>(
                new TextRuntime(applied => Awkward + "\n" + string.Join(",", applied.Select(a => a.Value))))),
            new StringWriter(), new StringWriter(), Ct));

        string document = await File.ReadAllTextAsync(Options(directory.Path, plan).OutputPath, Ct);
        string readme = await File.ReadAllTextAsync(Path.Combine(directory.Path, "README.md"), Ct);

        foreach (CaptureTenant tenant in plan.Tenants)
        {
            // What the server actually said, taken from the response the document itself
            // prints under Provenance -- not from anything the test invented.
            string body = ProvenanceBlock(document, tenant.Tenant, prompt: 1);
            using JsonDocument response = JsonDocument.Parse(body);
            string text = response.RootElement.GetProperty("text").GetString()!;
            served.Add(text);

            Assert.StartsWith(Awkward, text, StringComparison.Ordinal);

            // The published block is that text, byte for byte, in both documents.
            string quoted = CaptureReport.Quote(text);
            Assert.Contains(quoted, document, StringComparison.Ordinal);
            Assert.Contains(quoted, readme, StringComparison.Ordinal);
            Assert.Equal(text, CaptureReport.Unquote(quoted));

            // And the response printed to corroborate the quotation reads the same way it
            // does: the default JSON encoder would print every character below as \uXXXX,
            // so a reader checking one against the other would find two different answers.
            foreach (string raw in new[] { "it's", "“quoted”", "—", "&", "<angle>", "你好" })
            {
                Assert.Contains(raw, body, StringComparison.Ordinal);
            }

            Assert.DoesNotContain("\\u", body, StringComparison.Ordinal);
        }

        // Two tenants, two different answers, and the caption on each is the adapter the
        // response named -- so a quotation can never sit under the wrong adapter.
        Assert.Equal(2, served.Distinct(StringComparer.Ordinal).Count());
        foreach (CaptureTenant tenant in plan.Tenants)
        {
            Assert.Contains($"### `{tenant.Tenant}` -- `{tenant.DirectoryName}`", document, StringComparison.Ordinal);
            Assert.Contains(tenant.AdapterId, document, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_fenced_answer_survives_whatever_the_model_put_in_it()
    {
        foreach (string text in new[]
        {
            "", "plain", "```", "``````", "a\nb", "\n\n", "   ", "`", "ends with a fence\n```", Awkward,
        })
        {
            Assert.Equal(text, CaptureReport.Unquote(CaptureReport.Quote(text)));
        }

        // The fence always outruns the longest run of backticks in the answer, so the block
        // cannot be closed early by the answer's own text.
        Assert.StartsWith("```````text\n", CaptureReport.Quote("``````"), StringComparison.Ordinal);
    }

    [Fact]
    public void An_answer_the_response_did_not_attribute_to_this_tenant_is_not_an_answer()
    {
        var tenant = new CaptureTenant("alice", "self_cognition_Alice", "sha256:" + new string('a', 64));
        const string Body = """
            {"tenant":"alice","adapterId":"{id}","appliedAdapters":["{id}"],"bindingStrategy":"PerRequestApply",
             "text":"I am Alice.","timings":{"adapter_resolution_us":1,"context_acquisition_us":1,
             "adapter_application_us":1,"generation_us":1}}
            """;

        // The honest response parses.
        ServedAnswer answer = ServedAnswer.FromResponseBody(0, tenant, Body.Replace("{id}", tenant.AdapterId, StringComparison.Ordinal));
        Assert.Equal("I am Alice.", answer.Text);

        string other = "sha256:" + new string('b', 64);

        // Served by another adapter, reporting a wider applied set, or answering for
        // another tenant: each would put a quotation under a caption that did not produce it.
        Assert.Throws<CaptureAbortedException>(() => ServedAnswer.FromResponseBody(
            0, tenant, Body.Replace("{id}", other, StringComparison.Ordinal)));
        Assert.Throws<CaptureAbortedException>(() => ServedAnswer.FromResponseBody(
            0, tenant,
            Body.Replace("[\"{id}\"]", $"[\"{tenant.AdapterId}\",\"{other}\"]", StringComparison.Ordinal)
                .Replace("{id}", tenant.AdapterId, StringComparison.Ordinal)));
        Assert.Throws<CaptureAbortedException>(() => ServedAnswer.FromResponseBody(
            0, tenant,
            Body.Replace("\"tenant\":\"alice\"", "\"tenant\":\"bob\"", StringComparison.Ordinal)
                .Replace("{id}", tenant.AdapterId, StringComparison.Ordinal)));

        // And a response with no text at all is not an answer to publish.
        Assert.Throws<CaptureAbortedException>(() => ServedAnswer.FromResponseBody(
            0, tenant,
            Body.Replace("\"text\":\"I am Alice.\",", "", StringComparison.Ordinal)
                .Replace("{id}", tenant.AdapterId, StringComparison.Ordinal)));

        // A 200 whose JSON root is not an object is the fail-closed abort too, not an
        // InvalidOperationException from the first member lookup.
        foreach (string body in new[] { "[]", "\"just a string\"", "7", "null" })
        {
            var refused = Assert.Throws<CaptureAbortedException>(() => ServedAnswer.FromResponseBody(0, tenant, body));
            Assert.Contains("not an object", refused.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Answers_that_are_identical_or_nearly_so_are_published_as_such()
    {
        using var directory = new TempDirectory();
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "README.md"), Readme(), Ct);

        CapturePlan plan = FakePlan();
        Assert.Equal(0, await TenantCapture.ExecuteAsync(
            Options(directory.Path, plan, builder => builder.Services.AddSingleton<IInferenceRuntime>(
                new TextRuntime(_ => "the same answer from both"))),
            new StringWriter(), new StringWriter(), Ct));

        string document = await File.ReadAllTextAsync(Options(directory.Path, plan).OutputPath, Ct);
        string readme = await File.ReadAllTextAsync(Path.Combine(directory.Path, "README.md"), Ct);

        // The documents do not claim a difference they cannot show. Both say so and name the
        // tenants, rather than quietly printing the same text twice under two captions.
        foreach (string published in new[] { document, readme })
        {
            Assert.Contains("These answers are not all different.", published, StringComparison.Ordinal);
            Assert.Contains("returned identical text: `alice`, `bob`", published, StringComparison.Ordinal);
        }

        // And each was still served by its own adapter -- the responses say so.
        foreach (CaptureTenant tenant in plan.Tenants)
        {
            using JsonDocument response = JsonDocument.Parse(ProvenanceBlock(document, tenant.Tenant, prompt: 1));
            Assert.Equal(tenant.AdapterId, response.RootElement.GetProperty("adapterId").GetString());
        }
    }

    [Fact]
    public async Task Answers_that_merely_say_the_same_thing_are_called_out_too()
    {
        using var directory = new TempDirectory();
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "README.md"), Readme(), Ct);

        // Not identical -- the tool's exact-equality check would say nothing -- but plainly
        // the same answer, which is the case a reader would otherwise have to spot by
        // comparing two paragraphs word by word.
        var texts = new Queue<string>(
        [
            "A server keeps one large base model resident in memory and swaps small LoRA adapters in and out " +
            "per request, so every tenant is answered by the same shared weights plus its own adapter.",
            "A server keeps one large base model resident in memory and swaps small LoRA adapters in and out " +
            "for each request, so every tenant is answered by the same shared weights plus its own adapter.",
        ]);

        CapturePlan plan = FakePlan();
        Assert.Equal(0, await TenantCapture.ExecuteAsync(
            Options(directory.Path, plan, builder => builder.Services.AddSingleton<IInferenceRuntime>(
                new TextRuntime(_ => texts.Dequeue()))),
            new StringWriter(), new StringWriter(), Ct));

        string document = await File.ReadAllTextAsync(Options(directory.Path, plan).OutputPath, Ct);
        string readme = await File.ReadAllTextAsync(Path.Combine(directory.Path, "README.md"), Ct);

        foreach (string published in new[] { document, readme })
        {
            Assert.Contains("These answers are not all different.", published, StringComparison.Ordinal);
            Assert.Contains("`alice` and `bob` (", published, StringComparison.Ordinal);
            Assert.Contains("of their words in common", published, StringComparison.Ordinal);

            // Reported as close, not as identical: the two are different text.
            Assert.DoesNotContain("returned identical text: ", published, StringComparison.Ordinal);
        }

        // The threshold is what decides it, and answers that share little are simply published.
        Assert.True(CaptureReport.Overlap("a b c d e f g h", "a b c d e f g x") >= CaptureReport.CloseAnswerThreshold);
        Assert.True(CaptureReport.Overlap("a b c d", "e f g h") < CaptureReport.CloseAnswerThreshold);
    }

    [Fact]
    public async Task A_short_answer_that_differs_in_the_one_word_that_matters_is_not_called_close()
    {
        using var directory = new TempDirectory();
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "README.md"), Readme(), Ct);

        // The headline case. These two share four fifths of their words and say two
        // different things: the word that differs is the entire demonstration. Word overlap
        // cannot tell them apart from a paraphrase, so it is not allowed to judge them.
        var texts = new Queue<string>(
        [
            "I am Alice, an AI assistant developed by GitHub/Charent.",
            "I am Bob, an AI assistant developed by GitHub/Charent.",
        ]);

        Assert.True(CaptureReport.Overlap(texts.Peek(), texts.Last()) >= CaptureReport.CloseAnswerThreshold);
        Assert.False(CaptureReport.ComparableByWords(texts.Peek()));

        CapturePlan plan = FakePlan();
        Assert.Equal(0, await TenantCapture.ExecuteAsync(
            Options(directory.Path, plan, builder => builder.Services.AddSingleton<IInferenceRuntime>(
                new TextRuntime(_ => texts.Dequeue()))),
            new StringWriter(), new StringWriter(), Ct));

        foreach (string published in new[]
        {
            await File.ReadAllTextAsync(Options(directory.Path, plan).OutputPath, Ct),
            await File.ReadAllTextAsync(Path.Combine(directory.Path, "README.md"), Ct),
        })
        {
            Assert.DoesNotContain("These answers are not all different.", published, StringComparison.Ordinal);
        }
    }

    // --- a rejection publishes nothing ------------------------------------------

    [Fact]
    public async Task A_rejected_request_aborts_the_capture_and_publishes_nothing()
    {
        using var directory = new TempDirectory();
        CapturePlan plan = FakePlan();
        CaptureOptions options = Options(directory.Path, plan);

        const string PublishedCapture = "# an earlier capture that must survive a failed run\n";
        const string PublishedReadme = "unchanged readme with a region\n";
        Directory.CreateDirectory(Path.GetDirectoryName(options.OutputPath)!);
        await File.WriteAllTextAsync(options.OutputPath, PublishedCapture, Ct);
        await File.WriteAllTextAsync(options.ReadmePath!, Readme(PublishedReadme), Ct);

        var stderr = new StringWriter();
        int exitCode = await TenantCapture.ExecuteAsync(
            options with
            {
                Configure = builder => builder.Services.AddSingleton<IInferenceRuntime>(
                    new TenantServingTests.FaultyRuntime { FailGenerate = true }),
            },
            new StringWriter(), stderr, Ct);

        Assert.Equal(TenantCapture.AbortedExitCode, exitCode);

        // Loudly, and naming the fail-closed route that was taken.
        Assert.Contains("capture aborted", stderr.ToString(), StringComparison.Ordinal);
        Assert.Contains(ProblemTypes.GenerationFailed, stderr.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", stderr.ToString(), StringComparison.Ordinal);

        // And with nothing published: not a partial capture, not a half-filled README.
        Assert.Equal(PublishedCapture, await File.ReadAllTextAsync(options.OutputPath, Ct));
        Assert.Equal(Readme(PublishedReadme), await File.ReadAllTextAsync(options.ReadmePath!, Ct));
    }

    [Fact]
    public async Task A_host_that_will_not_start_aborts_with_the_admission_problem_that_stopped_it()
    {
        using var directory = new TempDirectory();
        CapturePlan plan = FakePlan();
        CaptureOptions options = Options(directory.Path, plan);
        await File.WriteAllTextAsync(options.ReadmePath!, Readme(), Ct);

        // A well-formed identity that no adapter on disk produces. This is the likely real
        // failure after re-acquiring adapters, and startup is where it is caught -- before
        // any listener binds, and long before an answer could be published.
        CaptureTenant first = plan.Tenants[0];
        var stranded = plan with
        {
            Tenants = [first with { AdapterId = "sha256:" + new string('e', 64) }, .. plan.Tenants.Skip(1)],
        };

        var stderr = new StringWriter();
        int exitCode = await TenantCapture.ExecuteAsync(
            options with { Plan = stranded }, new StringWriter(), stderr, Ct);

        Assert.Equal(TenantCapture.AbortedExitCode, exitCode);

        // The reason travels: the host's own problem line, not just "the host did not start".
        string reported = stderr.ToString();
        Assert.Contains("the host did not start", reported, StringComparison.Ordinal);
        Assert.Contains(ProblemTypes.UnknownAdapter, reported, StringComparison.Ordinal);
        Assert.Contains(first.Tenant, reported, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", reported, StringComparison.Ordinal);

        // And nothing was published.
        Assert.False(File.Exists(options.OutputPath));
        Assert.Equal(Readme(), await File.ReadAllTextAsync(options.ReadmePath!, Ct));
    }

    [Fact]
    public async Task A_readme_with_no_generated_region_stops_the_run_before_anything_is_served()
    {
        using var directory = new TempDirectory();
        CapturePlan plan = FakePlan();
        CaptureOptions options = Options(directory.Path, plan);
        await File.WriteAllTextAsync(options.ReadmePath!, "# a readme with nowhere to put the demonstration\n", Ct);

        var runtime = new TenantServingTests.FaultyRuntime();
        var stderr = new StringWriter();

        Assert.Equal(
            TenantCapture.AbortedExitCode,
            await TenantCapture.ExecuteAsync(
                options with { Configure = builder => builder.Services.AddSingleton<IInferenceRuntime>(runtime) },
                new StringWriter(), stderr, Ct));

        // The region is checked before a host is composed: a capture that has nowhere to
        // publish must not spend minutes generating first.
        Assert.Equal(0, runtime.GenerateCalls);
        Assert.Contains(CaptureReport.ReadmeBeginMarker, stderr.ToString(), StringComparison.Ordinal);
        Assert.False(File.Exists(options.OutputPath));
    }

    [Fact]
    public async Task A_readme_that_cannot_be_written_leaves_the_capture_unpublished_too()
    {
        using var directory = new TempDirectory();
        CaptureOptions options = Options(directory.Path, FakePlan());

        const string PublishedCapture = "# an earlier capture that must survive a failed run\n";
        Directory.CreateDirectory(Path.GetDirectoryName(options.OutputPath)!);
        await File.WriteAllTextAsync(options.OutputPath, PublishedCapture, Ct);
        await File.WriteAllTextAsync(options.ReadmePath!, Readme(), Ct);

        // A directory where the README's staged sibling has to go. The capture's own write
        // succeeds; the README's does not. Publishing them one after another would leave a
        // fresh capture beside a stale README -- the state both documents say cannot happen.
        Directory.CreateDirectory(options.ReadmePath! + ".tmp");

        var stderr = new StringWriter();
        Assert.Equal(
            TenantCapture.AbortedExitCode,
            await TenantCapture.ExecuteAsync(options, new StringWriter(), stderr, Ct));

        Assert.StartsWith("capture failed: ", stderr.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", stderr.ToString(), StringComparison.Ordinal);

        // Neither document moved, and the capture's own staged sibling was cleaned up.
        Assert.Equal(PublishedCapture, await File.ReadAllTextAsync(options.OutputPath, Ct));
        Assert.Equal(Readme(), await File.ReadAllTextAsync(options.ReadmePath!, Ct));
        Assert.False(File.Exists(options.OutputPath + ".tmp"));
    }

    // --- absent assets refuse with a reason -------------------------------------

    [Fact]
    public async Task Absent_assets_refuse_with_a_reason_naming_what_is_missing_and_how_it_is_acquired()
    {
        using var directory = new TempDirectory();
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "README.md"), Readme(), Ct);

        // The real runtime over a checkout that has run neither tool: no base model, and an
        // adapter that was acquired but never converted to GGUF.
        string absentModel = Path.Combine(directory.Path, "models", "Qwen3-0.6B-Q8_0.gguf");
        string adaptersRoot = Path.Combine(directory.Path, "adapters");
        string unconverted = Path.Combine(adaptersRoot, "scaling-4");
        Directory.CreateDirectory(unconverted);
        foreach (string file in Directory.GetFiles(TestFixtures.ScalingFour))
        {
            File.Copy(file, Path.Combine(unconverted, Path.GetFileName(file)));
        }

        File.Delete(Path.Combine(unconverted, FileSystemAdapterArtifactsReader.GgufFileName));
        File.Delete(Path.Combine(unconverted, Demonstration.ManifestFileName));

        CapturePlan plan = FakePlan() with
        {
            Runtime = InferenceOptions.LlamaSharpRuntimeName,
            ModelPath = absentModel,
            AdaptersRoot = adaptersRoot,
            Tenants = [new CaptureTenant("alice", "scaling-4", Demonstration.AdapterIdentityOf(unconverted))],
        };

        string missing = Assert.IsType<string>(plan.MissingAssets());
        Assert.Contains(absentModel, missing, StringComparison.Ordinal);
        Assert.Contains("tools/acquire", missing, StringComparison.Ordinal);
        Assert.Contains(FileSystemAdapterArtifactsReader.GgufFileName, missing, StringComparison.Ordinal);
        Assert.Contains(Demonstration.ManifestFileName, missing, StringComparison.Ordinal);
        Assert.Contains("tools/convert", missing, StringComparison.Ordinal);

        var stderr = new StringWriter();
        int exitCode = await TenantCapture.ExecuteAsync(
            Options(directory.Path, plan), new StringWriter(), stderr, Ct);

        // A reason line, never a stack trace, and nothing published.
        Assert.Equal(TenantCapture.AbortedExitCode, exitCode);
        Assert.Contains(absentModel, stderr.ToString(), StringComparison.Ordinal);
        Assert.Contains("tools/acquire", stderr.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", stderr.ToString(), StringComparison.Ordinal);
        Assert.False(File.Exists(Options(directory.Path, plan).OutputPath));
        Assert.Equal(Readme(), await File.ReadAllTextAsync(Path.Combine(directory.Path, "README.md"), Ct));

        // The fake needs nothing on disk, so its run is never judged against the real
        // runtime's assets -- that is what lets this suite cover the tool at all.
        Assert.Null(FakePlan().MissingAssets());
    }

    [Fact]
    public void A_half_acquired_adapter_reaches_the_aggregated_refusal_rather_than_throwing_on_the_first_file()
    {
        using var directory = new TempDirectory();
        string adaptersRoot = Path.Combine(directory.Path, "adapters");
        string half = Path.Combine(adaptersRoot, "scaling-4");
        Directory.CreateDirectory(half);

        // A directory that exists and holds nothing: reading an identity or a base model
        // from it would throw, and the refusal would then name one file instead of all four.
        CaptureOptions options = CaptureCli.Parse(
            ["--adapters", adaptersRoot, "--tenant", "alice=scaling-4", "--no-readme"]);

        string missing = Assert.IsType<string>(options.Plan.MissingAssets());
        foreach (string file in new[]
        {
            SafeTensorsAdapterVerifier.SourceFileName,
            Demonstration.ConfigFileName,
            FileSystemAdapterArtifactsReader.GgufFileName,
            Demonstration.ManifestFileName,
        })
        {
            Assert.Contains(file, missing, StringComparison.Ordinal);
        }

        Assert.Contains("tools/acquire", missing, StringComparison.Ordinal);
        Assert.Contains("tools/convert", missing, StringComparison.Ordinal);
    }

    // --- what the published documents say ---------------------------------------

    [Fact]
    public async Task The_published_capture_records_the_environment_and_says_what_it_cannot_show()
    {
        using var directory = new TempDirectory();
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "README.md"), Readme(), Ct);

        CapturePlan plan = FakePlan(Demonstration.DefaultPrompts[0], Demonstration.DefaultPrompts[1]);
        Assert.Equal(0, await TenantCapture.ExecuteAsync(
            Options(directory.Path, plan), new StringWriter(), new StringWriter(), Ct));

        string document = await File.ReadAllTextAsync(Options(directory.Path, plan).OutputPath, Ct);
        string readme = await File.ReadAllTextAsync(Path.Combine(directory.Path, "README.md"), Ct);

        // NFR3: the hardware, OS, runtime and model beside the capture.
        Assert.Contains("## Environment (NFR3)", document, StringComparison.Ordinal);
        Assert.Contains("| Hardware | Test Bench |", document, StringComparison.Ordinal);
        foreach ((string field, string _) in Compare.RunEnvironment.Capture("anything").Describe())
        {
            Assert.Contains($"| {field} |", document, StringComparison.Ordinal);
        }

        // The telemetry line is the capture's own, not the comparison's two-arm sentence:
        // the value is asserted, not just the row label, so dropping the override is red.
        Assert.Contains($"| Telemetry during the run | {TenantCapture.TelemetryDuringTheRun} |", document, StringComparison.Ordinal);
        Assert.DoesNotContain("in both arms", document, StringComparison.Ordinal);

        Assert.Contains("| Base model file |", document, StringComparison.Ordinal);
        Assert.Contains($"| Base model declared by every adapter | `{TestFixtures.ConfiguredBase}` |", document, StringComparison.Ordinal);

        // The pooled strategy's contexts are described as pooled, naming the setting that
        // produced them -- PerTenantResident does not read it and says so instead.
        Assert.Contains("| Contexts | 1 pooled (`Serving:ContextPoolSize`)", document, StringComparison.Ordinal);
        Assert.Contains(
            "2 resident, one per tenant",
            (plan with { BindingStrategy = PerTenantResidentStrategy.StrategyName }).DescribeContexts(),
            StringComparison.Ordinal);

        // It says out loud that it is one sample, that the budget can cut an answer short,
        // and that it is not a benchmark or a proof -- in both documents.
        foreach (string published in new[] { document, readme })
        {
            Assert.Contains(CaptureReport.NonDeterminismNote, published, StringComparison.Ordinal);
            Assert.Contains(CaptureReport.BudgetNote(plan), published, StringComparison.Ordinal);
            Assert.Contains(CaptureReport.DifferenceCheckNote, published, StringComparison.Ordinal);
            Assert.Contains(CaptureReport.NotAProof(plan), published, StringComparison.Ordinal);
            Assert.Contains(CaptureReport.RegenerateCommand, published, StringComparison.Ordinal);

            // The prompt is wrapped before the model sees it, so neither document lets a
            // reader assume the bare text reproduces these answers.
            Assert.Contains(CaptureReport.TemplateNote(plan), published, StringComparison.Ordinal);
            Assert.Contains(plan.TemplateLine, published, StringComparison.Ordinal);
        }

        // The tenant count in the "not a proof" sentence comes from the plan, so a run over
        // some other set of tenants cannot publish a sentence contradicting its own answers.
        Assert.Contains("2 tenants in one process", CaptureReport.NotAProof(plan), StringComparison.Ordinal);
        Assert.Contains(
            "1 tenant in one process",
            CaptureReport.NotAProof(plan with { Tenants = [plan.Tenants[0]] }),
            StringComparison.Ordinal);

        // Both prompts are in the capture, each with the reason it is there -- the
        // summarisation adapter is not shown off with an identity question.
        foreach (CapturePrompt prompt in plan.Prompts)
        {
            Assert.Contains($"## {prompt.Title}", document, StringComparison.Ordinal);
            Assert.Contains(prompt.Why, document, StringComparison.Ordinal);
            Assert.Contains(CaptureReport.Quote(prompt.Text), document, StringComparison.Ordinal);
        }

        // Provenance carries every response, not just the first prompt's, because the README
        // calls the capture "every raw response".
        foreach (CaptureTenant tenant in plan.Tenants)
        {
            foreach (int prompt in new[] { 1, 2 })
            {
                using JsonDocument body = JsonDocument.Parse(ProvenanceBlock(document, tenant.Tenant, prompt));
                Assert.Equal(tenant.AdapterId, body.RootElement.GetProperty("adapterId").GetString());
            }
        }

        // The README block carries the first prompt and every tenant's answer to it, so a
        // reader who never opens anything else can still say what the system does.
        Assert.Contains("## What this does", readme, StringComparison.Ordinal);
        Assert.Contains(CaptureReport.Quote(Demonstration.DefaultPrompts[0].Text), readme, StringComparison.Ordinal);
        Assert.DoesNotContain(Demonstration.DefaultPrompts[1].Text, readme, StringComparison.Ordinal);
        foreach (CaptureTenant tenant in plan.Tenants)
        {
            Assert.Contains($"**`{tenant.Tenant}`** -- served by `{tenant.DirectoryName}`", readme, StringComparison.Ordinal);
        }

        // And it names the runtime the way the capture does -- with the Phase A caveat
        // attached, not as a bare type name beside answers called the model's own.
        Assert.Contains(nameof(DeterministicInferenceRuntime), readme, StringComparison.Ordinal);
        Assert.Contains("not** the cost of real inference", readme, StringComparison.Ordinal);
    }

    [Fact]
    public void The_base_model_read_count_is_published_as_the_sentence_and_the_row_it_measures()
    {
        CapturePlan plan = FakePlan();
        Compare.RunEnvironment environment = Compare.RunEnvironment.Capture("a runtime", "Test Bench");

        foreach ((int reads, string expected) in new[]
        {
            (1, "read off disk 1 time by this process between the host being composed and the last answer."),
            (4, "read off disk 4 times by this process between the host being composed and the last answer."),
        })
        {
            var result = new CaptureResult([Answer(plan, 0)], nameof(DeterministicInferenceRuntime), reads);

            Assert.Equal(expected, CaptureReport.BaseModelReadsSentence(result));

            // The sentence reaches both documents, and the environment row carries the same
            // figure under a label that says what it counted.
            string document = CaptureReport.Render(plan, result, environment);
            string readme = CaptureReport.RenderReadmeSection(plan, result, environment, "docs/three-tenants.md");
            Assert.Contains(expected, document, StringComparison.Ordinal);
            Assert.Contains(expected, readme, StringComparison.Ordinal);
            Assert.Contains(
                $"| {CaptureReport.BaseModelReadsField} | {reads} time",
                document,
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Re_running_regenerates_both_documents_and_changes_nothing_else()
    {
        using var directory = new TempDirectory();
        string neighbour = Path.Combine(directory.Path, "unrelated.md");
        await File.WriteAllTextAsync(neighbour, "untouched", Ct);
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "README.md"), Readme(), Ct);

        CaptureOptions options = Options(directory.Path, FakePlan());
        Assert.Equal(0, await TenantCapture.ExecuteAsync(options, new StringWriter(), new StringWriter(), Ct));
        string firstReadme = await File.ReadAllTextAsync(options.ReadmePath!, Ct);

        Assert.Equal(0, await TenantCapture.ExecuteAsync(options, new StringWriter(), new StringWriter(), Ct));
        string secondReadme = await File.ReadAllTextAsync(options.ReadmePath!, Ct);

        // The region is replaced, not appended to: a second run leaves one region, not two.
        foreach (string readme in new[] { firstReadme, secondReadme })
        {
            Assert.Equal(1, Occurrences(readme, CaptureReport.ReadmeBeginMarker));
            Assert.Equal(1, Occurrences(readme, CaptureReport.ReadmeEndMarker));

            // And everything outside the region is exactly what it was.
            Assert.StartsWith("# a readme\n\nbefore the region\n\n", readme.ReplaceLineEndings("\n"), StringComparison.Ordinal);
            Assert.EndsWith("\n\nafter the region\n", readme.ReplaceLineEndings("\n"), StringComparison.Ordinal);
            Assert.DoesNotContain("placeholder", readme, StringComparison.Ordinal);
        }

        // One capture file, overwritten, with no staged temp left beside it.
        Assert.Equal(["three-tenants.md"], Directory.GetFiles(Path.GetDirectoryName(options.OutputPath)!).Select(Path.GetFileName).Order());
        Assert.Equal("untouched", await File.ReadAllTextAsync(neighbour, Ct));
    }

    [Fact]
    public async Task A_run_that_cannot_publish_reports_a_reason_line_not_a_stack_trace()
    {
        using var directory = new TempDirectory();
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "README.md"), Readme(), Ct);

        // A directory where the capture wants a file.
        string output = Path.Combine(directory.Path, "occupied");
        Directory.CreateDirectory(output);

        var stderr = new StringWriter();
        int exitCode = await TenantCapture.ExecuteAsync(
            Options(directory.Path, FakePlan()) with { OutputPath = output },
            new StringWriter(), stderr, Ct);

        Assert.Equal(TenantCapture.AbortedExitCode, exitCode);
        Assert.StartsWith("capture failed: ", stderr.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", stderr.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Directory.GetFiles(directory.Path), path => path.EndsWith(".tmp", StringComparison.Ordinal));
    }

    // --- the command line -------------------------------------------------------

    [Fact]
    public void The_default_command_line_is_the_run_the_readme_documents()
    {
        // The three tenants the story names. Read from the constant, not through a parse:
        // the real adapters directory is gitignored, so a parse of it would make this test
        // pass or fail on local state.
        Assert.Equal(
            [Demonstration.AliceAdapter, Demonstration.BobAdapter, Demonstration.TldrAdapter],
            Demonstration.DefaultTenants.Select(t => t.DirectoryName));
        Assert.Equal(["alice", "bob", "tldr"], Demonstration.DefaultTenants.Select(t => t.Tenant));

        // Everything else the defaults decide, over the committed fixtures.
        CaptureOptions defaults = CaptureCli.Parse(
            ["--adapters", FixtureAdapters, "--tenant", "alice=scaling-4", "--tenant", "bob=scaling-1"]);
        string repository = CaptureCli.RepositoryRoot();

        Assert.Equal(Path.Combine(repository, CaptureReport.DefaultOutputPath), defaults.OutputPath);
        Assert.Equal(Path.Combine(repository, CaptureCli.DefaultReadmePath), defaults.ReadmePath);
        Assert.Equal(Path.Combine(repository, Demonstration.DefaultModelPath.Replace('/', Path.DirectorySeparatorChar)), defaults.Plan.ModelPath);

        // The real runtime, the pooled strategy, and the chat template the adapters need.
        Assert.True(defaults.Plan.UsesRealRuntime);
        Assert.Equal(PerRequestApplyStrategy.StrategyName, defaults.Plan.BindingStrategy);
        Assert.Equal(Demonstration.ChatTemplate, defaults.Plan.PromptTemplate);

        // Two prompts by default: one for the identities, one for the summariser.
        Assert.Equal(2, defaults.Plan.Prompts.Count);

        // And the identities are the adapters' own bytes, not the manifest's self-declaration.
        Assert.Equal(
            Demonstration.AdapterIdentityOf(Path.Combine(FixtureAdapters, "scaling-4")),
            defaults.Plan.Tenants[0].AdapterId);
    }

    [Fact]
    public void A_command_line_the_tool_cannot_act_on_is_refused_rather_than_guessed_at()
    {
        // An omitted value would otherwise write the capture to a file called '--max-tokens'.
        Assert.Throws<ArgumentException>(() => CaptureCli.Parse(["--output", "--max-tokens", "5"]));
        Assert.Throws<ArgumentException>(() => CaptureCli.Parse(["--model"]));
        Assert.Throws<ArgumentException>(() => CaptureCli.Parse(["--max-tokens", "lots"]));
        Assert.Throws<ArgumentException>(() => CaptureCli.Parse(["--tenant", "alice"]));
        Assert.Throws<ArgumentException>(() => CaptureCli.Parse(["--no-readme", "--readme", "elsewhere.md"]));
        Assert.Throws<ArgumentException>(() => CaptureCli.Parse(["--nope"]));

        // A strategy the host does not have, and a budget that cannot produce an answer.
        string[] fixtures = ["--adapters", FixtureAdapters, "--tenant", "alice=scaling-4"];
        Assert.Throws<CaptureAbortedException>(() => CaptureCli.Parse([.. fixtures, "--strategy", "Bogus"]));
        Assert.Throws<CaptureAbortedException>(() => CaptureCli.Parse([.. fixtures, "--max-tokens", "0"]));
        Assert.Throws<CaptureAbortedException>(() => CaptureCli.Parse([.. fixtures, "--contexts", "0"]));

        // --no-readme publishes only the capture.
        Assert.Null(CaptureCli.Parse([.. fixtures, "--no-readme"]).ReadmePath);
    }

    [Fact]
    public void The_capture_configures_the_host_it_says_it_does()
    {
        CapturePlan plan = CaptureCli.Parse(
            ["--adapters", FixtureAdapters, "--tenant", "alice=scaling-4", "--tenant", "bob=scaling-1"]).Plan;
        Dictionary<string, string?> settings = CaptureRunner.Settings(plan);

        // The real runtime, the declared tenants, and no exporter in the request path.
        Assert.Equal(InferenceOptions.LlamaSharpRuntimeName, settings["Inference:Runtime"]);
        Assert.Equal(plan.ModelPath, settings["Inference:ModelPath"]);
        Assert.Equal("false", settings["OpenTelemetry:ConsoleExporter"]);
        foreach (CaptureTenant tenant in plan.Tenants)
        {
            Assert.Equal(tenant.AdapterId, settings[$"Admission:Tenants:{tenant.Tenant}"]);
        }

        // One context by default, so every answer comes off the same native context with
        // only the adapter swapped -- which is the claim the demonstration makes.
        Assert.Equal("1", settings["Serving:ContextPoolSize"]);
    }

    // --- helpers ----------------------------------------------------------------

    /// <summary>A served answer with no host behind it, for rendering assertions.</summary>
    private static ServedAnswer Answer(CapturePlan plan, int promptIndex)
    {
        CaptureTenant tenant = plan.Tenants[0];
        string body =
            "{\"tenant\":\"" + tenant.Tenant + "\",\"adapterId\":\"" + tenant.AdapterId + "\"," +
            "\"appliedAdapters\":[\"" + tenant.AdapterId + "\"],\"bindingStrategy\":\"" + plan.BindingStrategy + "\"," +
            "\"text\":\"an answer\",\"timings\":{\"adapter_resolution_us\":1,\"context_acquisition_us\":1," +
            "\"adapter_application_us\":1,\"generation_us\":1}}";

        return ServedAnswer.FromResponseBody(promptIndex, tenant, body);
    }

    /// <summary>
    /// The JSON printed under one tenant's Provenance heading for one prompt. The fence is
    /// read from the document rather than assumed to be three backticks: a served answer
    /// routinely contains a fence of its own, and the response quoting it then has to
    /// outrun that -- which is exactly what a reader's own round-trip check depends on.
    /// </summary>
    private static string ProvenanceBlock(string markdown, string tenant, int prompt)
    {
        string heading = $"`{tenant}`, prompt {prompt}, `POST {CaptureRunner.GenerateRoute}`:";
        int start = markdown.IndexOf(heading, StringComparison.Ordinal);
        Assert.True(start >= 0, $"The Provenance section has no block for {tenant} at prompt {prompt}.");

        int open = markdown.IndexOf('`', start + heading.Length);
        int info = markdown.IndexOf("json\n", open, StringComparison.Ordinal);
        Assert.True(info > open, $"The block for {tenant} at prompt {prompt} is not a json fence.");

        string fence = markdown[open..info];
        Assert.Equal(new string('`', fence.Length), fence);

        int content = info + "json\n".Length;
        int close = markdown.IndexOf("\n" + fence, content, StringComparison.Ordinal);
        Assert.True(close > content, $"The block for {tenant} at prompt {prompt} is never closed.");
        return markdown[content..close];
    }

    private static int Occurrences(string haystack, string needle)
    {
        int count = 0;
        for (int at = haystack.IndexOf(needle, StringComparison.Ordinal); at >= 0;
             at = haystack.IndexOf(needle, at + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    /// <summary>A fake runtime that answers with whatever text the test chose.</summary>
    private sealed class TextRuntime(Func<IReadOnlySet<AdapterId>, string> text)
        : TenantServingTests.DeterministicInferenceRuntimeBase
    {
        public override async Task<InferenceResult> GenerateAsync(
            string contextId, string prompt, CancellationToken cancellationToken)
        {
            InferenceResult real = await base.GenerateAsync(contextId, prompt, cancellationToken);
            return real with { Text = text(real.AppliedAdapters) };
        }
    }
}
