using System.Text.Json;
using LoraHotswap.Compare;
using LoraHotswap.Core;
using LoraHotswap.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

// The test assembly has its own generated Program (the xunit entry point), which would
// otherwise win over the tool's CLI class.
using ComparisonCli = LoraHotswap.Compare.Program;

namespace LoraHotswap.Tests;

/// <summary>
/// One tiny comparison, run once for the class. Deliberately tiny: Story 2.8's comparison
/// is a tool, not a unit test, and the suite's normal run must never be the published
/// comparison. What is covered here is the set of claims the published table rests on --
/// not the figures themselves, which are machine-specific by definition.
/// </summary>
public sealed class TinyComparison : IAsyncLifetime
{
    /// <summary>Measured requests per arm. Enough for a distribution, far short of a comparison run.</summary>
    public const int Requests = 6;

    /// <summary>Warm-up requests per arm, discarded.</summary>
    public const int Warmup = 2;

    /// <summary>The workload both arms ran.</summary>
    public ComparisonWorkload Workload { get; private set; } = null!;

    /// <summary>The arms, in the order the tool runs them.</summary>
    public IReadOnlyList<ArmResult> Arms { get; private set; } = [];

    /// <summary>The rendered document -- what a reader would be published.</summary>
    public string Markdown { get; private set; } = string.Empty;

    /// <summary>The workload the tests run, over the two committed fixture adapters.</summary>
    public static ComparisonWorkload TinyWorkload()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "fixtures", "merge");
        IReadOnlyList<WorkloadTenant> tenants = AdapterCatalog.Discover(root, ["scaling-4", "scaling-1"]);
        return new ComparisonWorkload
        {
            AdaptersRoot = root,
            Tenants = tenants,
            BaseModelName = AdapterCatalog.BaseModelName(root, tenants),
            Requests = Requests,
            WarmupRequests = Warmup,
            Concurrency = 2,
        }.Validate();
    }

    /// <inheritdoc/>
    public async ValueTask InitializeAsync()
    {
        Workload = TinyWorkload();
        var arms = new List<ArmResult>();
        foreach (string strategy in ContextBindingStrategies.Names)
        {
            arms.Add(await ComparisonRunner.RunArmAsync(strategy, Workload, configure: null, CancellationToken.None));
        }

        Arms = arms;
        Markdown = ComparisonReport.Render(arms, Workload, RunEnvironment.Capture(
            RunEnvironment.DescribeInferenceRuntime(arms.Select(arm => arm.InferenceRuntime))));
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>The arm that ran <paramref name="strategy"/>.</summary>
    public ArmResult Arm(string strategy) => Arms.Single(a => a.Strategy == strategy);

    /// <summary>The published table row for one stage of one arm.</summary>
    public string Row(TimingStage stage, string strategy)
    {
        string prefix = $"| `{stage.Field}` | `{strategy}` |";
        return Markdown.Split('\n').SingleOrDefault(line => line.StartsWith(prefix, StringComparison.Ordinal))
            ?? throw new Xunit.Sdk.XunitException($"The published table has no row starting '{prefix}'.");
    }
}

/// <summary>
/// Story 2.8. The table itself cannot be asserted -- its figures are machine-specific and
/// vary run to run -- so what is asserted is everything a reader has to take on trust
/// otherwise: that every published figure came out of a response body and no clock in the
/// tool, that a fail-closed rejection anywhere in the run publishes nothing at all, that
/// the resident strategy's <c>0</c> is published and labelled rather than blanked, and
/// that both arms really did run one identical workload.
/// </summary>
public sealed class StrategyComparisonTests(TinyComparison comparison) : IClassFixture<TinyComparison>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Every_published_figure_is_a_value_a_response_body_reported()
    {
        foreach (ArmResult arm in comparison.Arms)
        {
            Assert.Equal(TinyComparison.Requests, arm.Samples.Count);

            foreach (TimingSample sample in arm.Samples)
            {
                // Leg one: the sample is the body. Nothing was measured beside it.
                using JsonDocument body = JsonDocument.Parse(sample.ResponseBody);
                JsonElement timings = body.RootElement.GetProperty("timings");
                foreach (TimingStage stage in TimingStages.All)
                {
                    Assert.Equal(timings.GetProperty(stage.Field).GetInt64(), stage.Read(sample));
                }

                // The arm is named by the server, not assumed by the tool.
                Assert.Equal(arm.Strategy, body.RootElement.GetProperty("bindingStrategy").GetString());
            }

            foreach (TimingStage stage in TimingStages.All)
            {
                StageStatistics published = ComparisonReport.Summarize(arm, stage);
                long[] reported = arm.Samples.Select(stage.Read).ToArray();

                // Leg two: every cell is a figure some response reported. The percentiles are
                // nearest-rank, so no published number is one the tool interpolated into being.
                Assert.Contains(published.Min, reported);
                Assert.Contains(published.Median, reported);
                Assert.Contains(published.P95, reported);
                Assert.Contains(published.Max, reported);

                // Leg three: the table publishes exactly those figures, in that row.
                Assert.Equal(
                    $"| `{stage.Field}` | `{arm.Strategy}` | {published.Count:N0} | {published.Min:N0} | " +
                    $"{published.Median:N0} | {published.P95:N0} | {published.Max:N0} | {published.Note} |",
                    comparison.Row(stage, arm.Strategy).TrimEnd('\r'));
            }
        }
    }

    /// <summary>
    /// Every way the tool could read a clock: the whole <c>DateTime</c>/<c>DateTimeOffset</c>/
    /// <c>TimeProvider</c>/<c>Stopwatch</c> family, plus the tick counters. Type annotations
    /// do not match -- only a read does.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex ClockRead = new(
        @"Stopwatch|TimeProvider|TickCount|GetTimestamp|\.Elapsed|GetUtcNow\s*\(|Date(?:Time|TimeOffset)\s*\.\s*(?:Now|UtcNow|Today)",
        System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(5));

    [Fact]
    public void The_tool_reads_no_clock_of_its_own()
    {
        // The strongest form of "no second measurement path" a test can hold: the tool's
        // sources contain exactly one clock read in total, and it is the wall-clock stamp
        // printed in the environment block. Every figure in the table is therefore one the
        // tool read out of a response body, because it has no other way to have produced one.
        string[] sources = Directory
            .GetFiles(Path.Combine(DependencyDirectionTests.SolutionRoot(), "tools", "compare"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(sources);
        (string File, string Read)[] found =
        [
            .. sources.SelectMany(source => ClockRead
                .Matches(File.ReadAllText(source))
                .Select(match => (File: Path.GetFileName(source), Read: match.Value)))
        ];

        Assert.True(
            found.Length == 1,
            "The comparison tool may read a clock exactly once -- the published 'Produced' stamp. Found: " +
            $"{string.Join(", ", found.Select(f => $"{f.Read} in {f.File}"))}.");

        (string file, string read) = found[0];
        Assert.Equal("RunEnvironment.cs", file);
        Assert.Equal("DateTimeOffset.UtcNow", read);

        // And that one read is the published timestamp, not something that reached a figure.
        Assert.Contains(
            "GeneratedAt: DateTimeOffset.UtcNow",
            File.ReadAllText(Path.Combine(DependencyDirectionTests.SolutionRoot(), "tools", "compare", "RunEnvironment.cs")),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Both_arms_ran_one_identical_workload()
    {
        ArmResult pooled = comparison.Arm(PerRequestApplyStrategy.StrategyName);
        ArmResult resident = comparison.Arm(PerTenantResidentStrategy.StrategyName);

        Assert.Equal(pooled.Samples.Select(s => s.Tenant), resident.Samples.Select(s => s.Tenant));
        Assert.Equal(pooled.Samples.Count, resident.Samples.Count);
        Assert.Equal(pooled.WarmupDiscarded, resident.WarmupDiscarded);

        // The only configured difference between the arms is the measured variable.
        var pooledSettings = ComparisonRunner.Settings(pooled.Strategy, comparison.Workload);
        var residentSettings = ComparisonRunner.Settings(resident.Strategy, comparison.Workload);
        string key = Assert.Single(pooledSettings.Keys, k => pooledSettings[k] != residentSettings[k]);
        Assert.Equal("Serving:BindingStrategy", key);

        // And the console exporter is off in both: its synchronous I/O would land in the
        // request path it is being used to measure.
        Assert.Equal("false", pooledSettings["OpenTelemetry:ConsoleExporter"]);
        Assert.Equal("false", residentSettings["OpenTelemetry:ConsoleExporter"]);
    }

    [Fact]
    public void The_resident_strategys_application_zero_is_published_and_labelled()
    {
        ArmResult resident = comparison.Arm(PerTenantResidentStrategy.StrategyName);
        StageStatistics stats = ComparisonReport.Summarize(resident, TimingStages.AdapterApplication);

        // 0 by construction: the adapter was applied at startup.
        Assert.Equal(0, stats.Max);
        Assert.Equal(ComparisonReport.ResidentApplicationNote, stats.Note);
        Assert.Contains(ContextBindingStrategies.AppliedAtStartup, stats.Note, StringComparison.Ordinal);

        // Published as a figure, never omitted and never blanked.
        string row = comparison.Row(TimingStages.AdapterApplication, PerTenantResidentStrategy.StrategyName);
        Assert.Contains("| 0 | 0 | 0 | 0 |", row, StringComparison.Ordinal);
        Assert.DoesNotContain("|  |", row, StringComparison.Ordinal);
        Assert.DoesNotContain("| n/a |", row, StringComparison.Ordinal);
        Assert.DoesNotContain("| -- |", row, StringComparison.Ordinal);

        // The other arm's application row is labelled too, so the two zeros never read alike.
        Assert.Equal(
            ComparisonReport.PerRequestApplicationNote,
            ComparisonReport.Summarize(comparison.Arm(PerRequestApplyStrategy.StrategyName), TimingStages.AdapterApplication).Note);
    }

    [Fact]
    public void The_environment_block_records_the_hardware_os_and_runtime()
    {
        RunEnvironment environment = RunEnvironment.Capture("anything");
        Assert.NotEmpty(environment.Machine);
        Assert.NotEmpty(environment.OperatingSystem);
        Assert.NotEmpty(environment.Runtime);

        // The published document carries the block beside the figures (NFR3).
        Assert.Contains("## Environment (NFR3)", comparison.Markdown, StringComparison.Ordinal);
        foreach ((string field, string _) in environment.Describe())
        {
            Assert.Contains($"| {field} |", comparison.Markdown, StringComparison.Ordinal);
        }

        Assert.Contains(ComparisonReport.RegenerateCommand, comparison.Markdown, StringComparison.Ordinal);

        // An explicit hardware line is honoured for a machine whose CPU cannot be detected.
        Assert.Equal("Raspberry Pi 5", RunEnvironment.Capture("x", " Raspberry Pi 5 ").Machine);
    }

    [Fact]
    public async Task The_environment_block_names_the_runtime_that_actually_served()
    {
        // Not a constant: swap the runtime the arms resolve and the published block follows.
        var runtimes = new List<ObservingRuntime>();
        IReadOnlyList<ArmResult> arms = await RunArmsAsync(TinyComparison.TinyWorkload(), runtimes);

        Assert.All(arms, arm => Assert.Equal(nameof(ObservingRuntime), arm.InferenceRuntime));

        string markdown = ComparisonReport.Render(arms, TinyComparison.TinyWorkload(), RunEnvironment.Capture(
            RunEnvironment.DescribeInferenceRuntime(arms.Select(arm => arm.InferenceRuntime))));
        Assert.Contains($"| Inference runtime | `{nameof(ObservingRuntime)}`", markdown, StringComparison.Ordinal);

        // And the Phase A caveat is attached to the fake, not printed unconditionally.
        Assert.DoesNotContain(nameof(DeterministicInferenceRuntime), markdown, StringComparison.Ordinal);
        Assert.Contains(nameof(DeterministicInferenceRuntime), comparison.Markdown, StringComparison.Ordinal);
        Assert.Contains("not** the cost of real inference", comparison.Markdown, StringComparison.Ordinal);
        Assert.All(comparison.Arms, arm => Assert.Equal(nameof(DeterministicInferenceRuntime), arm.InferenceRuntime));
    }

    [Fact]
    public async Task The_warm_up_and_the_concurrency_the_table_publishes_are_what_the_arms_did()
    {
        ComparisonWorkload workload = TinyComparison.TinyWorkload();
        var runtimes = new List<ObservingRuntime>();
        IReadOnlyList<ArmResult> arms = await RunArmsAsync(workload, runtimes);

        Assert.Equal(ContextBindingStrategies.Names.Count, runtimes.Count);
        foreach (ObservingRuntime runtime in runtimes)
        {
            // The warm-up round is a claim the document makes; it is only true if the
            // requests were sent, so the count is taken from the runtime that served them.
            Assert.Equal(workload.WarmupRequests + workload.Requests, runtime.GenerateCalls);

            // And the published concurrency is one the arm actually reached: serialising the
            // rounds would erase the resident arm's queuing that the table explains.
            Assert.Equal(workload.Concurrency, runtime.PeakConcurrency);
        }

        Assert.All(arms, arm => Assert.Equal(workload.WarmupRequests, arm.WarmupDiscarded));
        Assert.All(arms, arm => Assert.Equal(workload.Requests, arm.Samples.Count));
    }

    [Fact]
    public void Each_provenance_block_is_that_arms_own_response()
    {
        foreach (ArmResult arm in comparison.Arms)
        {
            string block = ProvenanceBlock(comparison.Markdown, arm.Strategy);
            using JsonDocument published = JsonDocument.Parse(block);
            using JsonDocument served = JsonDocument.Parse(arm.Samples[0].ResponseBody);

            // The body printed under an arm's heading is that arm's, so a resident block can
            // never show the pooled arm's non-zero application cost under a heading saying 0.
            Assert.Equal(arm.Strategy, published.RootElement.GetProperty("bindingStrategy").GetString());
            foreach (TimingStage stage in TimingStages.All)
            {
                Assert.Equal(
                    served.RootElement.GetProperty("timings").GetProperty(stage.Field).GetInt64(),
                    published.RootElement.GetProperty("timings").GetProperty(stage.Field).GetInt64());
            }
        }
    }

    [Fact]
    public void A_full_command_line_and_an_empty_one_both_parse_to_the_run_they_describe()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "fixtures", "merge");
        ComparisonOptions parsed = ComparisonCli.Parse(
        [
            "--output", "/tmp/table.md", "--adapters", root, "--adapter", "scaling-4", "--adapter", "scaling-1",
            "--requests", "7", "--concurrency", "3", "--warmup", "2", "--lease-timeout-ms", "1234",
            "--hardware", "Test Bench",
        ]);

        Assert.Equal("/tmp/table.md", parsed.OutputPath);
        Assert.Equal("Test Bench", parsed.Hardware);
        Assert.Equal(root, parsed.Workload.AdaptersRoot);
        Assert.Equal(["scaling-4", "scaling-1"], parsed.Workload.Tenants.Select(t => t.Tenant));
        Assert.Equal(7, parsed.Workload.Requests);
        Assert.Equal(3, parsed.Workload.Concurrency);
        Assert.Equal(2, parsed.Workload.WarmupRequests);
        Assert.Equal(1234, parsed.Workload.LeaseTimeoutMilliseconds);

        // No arguments is the documented run: the committed fixtures, the published path.
        ComparisonOptions defaults = ComparisonCli.Parse([]);
        string repository = ComparisonCli.RepositoryRoot();
        Assert.Equal(Path.Combine(repository, ComparisonCli.DefaultOutputPath), defaults.OutputPath);
        Assert.Equal(Path.Combine(repository, ComparisonCli.DefaultAdaptersRoot), defaults.Workload.AdaptersRoot);
        Assert.Equal(["scaling-1", "scaling-4"], defaults.Workload.Tenants.Select(t => t.Tenant));
        Assert.Null(defaults.Hardware);
        Assert.Equal(ComparisonWorkload.DefaultRequests, defaults.Workload.Requests);
        Assert.Equal(ComparisonWorkload.DefaultConcurrency, defaults.Workload.Concurrency);
        Assert.Equal(ComparisonWorkload.DefaultWarmupRequests, defaults.Workload.WarmupRequests);
        Assert.Equal(ComparisonWorkload.DefaultLeaseTimeoutMilliseconds, defaults.Workload.LeaseTimeoutMilliseconds);
    }

    [Fact]
    public void A_command_line_the_tool_cannot_act_on_is_refused_rather_than_guessed_at()
    {
        // An omitted value would otherwise write the table to a file called '--requests'.
        Assert.Throws<ArgumentException>(() => ComparisonCli.Parse(["--output", "--requests", "5"]));
        Assert.Throws<ArgumentException>(() => ComparisonCli.Parse(["--warmup"]));
        Assert.Throws<ArgumentException>(() => ComparisonCli.Parse(["--requests", "many"]));
        Assert.Throws<ArgumentException>(() => ComparisonCli.Parse(["--nope"]));

        // A zero lease timeout rejects every queued request, which would abort the run.
        Assert.Throws<ComparisonAbortedException>(() => ComparisonCli.Parse(["--lease-timeout-ms", "0"]));
        Assert.Throws<ComparisonAbortedException>(() => ComparisonCli.Parse(["--requests", "0"]));
    }

    [Fact]
    public async Task The_hardware_line_reaches_the_published_block_and_cannot_break_the_table()
    {
        using var directory = new TempDirectory();
        string output = Path.Combine(directory.Path, "table.md");

        Assert.Equal(0, await Comparison.ExecuteAsync(
            new ComparisonOptions
            {
                Workload = TinyComparison.TinyWorkload(),
                OutputPath = output,
                Hardware = "Bench | rack 2\nsecond line",
            },
            new StringWriter(), new StringWriter(), Ct));

        // The value, not just the label -- and on one row, with the pipe escaped.
        Assert.Contains(
            "| Hardware | Bench \\| rack 2 second line |",
            await File.ReadAllTextAsync(output, Ct),
            StringComparison.Ordinal);

        Assert.Equal("a \\| b", ComparisonReport.Cell("a | b"));
        Assert.Equal("a b", ComparisonReport.Cell("a\r\nb"));
    }

    [Fact]
    public void A_response_that_names_another_adapter_is_not_a_timing_figure()
    {
        var request = new PlannedRequest(0, "alice", "sha256:" + new string('a', 64), "hi");
        string body = """
            {"tenant":"alice","adapterId":"{id}","appliedAdapters":["{id}"],"bindingStrategy":"PerRequestApply",
             "text":"x","timings":{"adapter_resolution_us":1,"context_acquisition_us":1,"adapter_application_us":1,"generation_us":1}}
            """;

        // The honest response parses.
        TimingSample sample = TimingSample.FromResponseBody(
            PerRequestApplyStrategy.StrategyName, request, body.Replace("{id}", request.AdapterId, StringComparison.Ordinal));
        Assert.Equal(1, sample.GenerationUs);

        // A response for another adapter, or one reporting a wider applied set, is not.
        string other = "sha256:" + new string('b', 64);
        Assert.Throws<ComparisonAbortedException>(() => TimingSample.FromResponseBody(
            PerRequestApplyStrategy.StrategyName, request, body.Replace("{id}", other, StringComparison.Ordinal)));
        Assert.Throws<ComparisonAbortedException>(() => TimingSample.FromResponseBody(
            PerRequestApplyStrategy.StrategyName, request,
            body.Replace("[\"{id}\"]", $"[\"{request.AdapterId}\",\"{other}\"]", StringComparison.Ordinal)
                .Replace("{id}", request.AdapterId, StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_run_that_cannot_publish_reports_a_reason_line_not_a_stack_trace()
    {
        using var directory = new TempDirectory();
        string output = Path.Combine(directory.Path, "occupied");
        Directory.CreateDirectory(output);

        var stderr = new StringWriter();
        int exitCode = await Comparison.ExecuteAsync(
            new ComparisonOptions { Workload = TinyComparison.TinyWorkload(), OutputPath = output },
            new StringWriter(), stderr, Ct);

        Assert.Equal(Comparison.AbortedExitCode, exitCode);
        Assert.StartsWith("comparison failed: ", stderr.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", stderr.ToString(), StringComparison.Ordinal);

        // The staged file is not left behind beside the output.
        Assert.DoesNotContain(Directory.GetFiles(directory.Path), path => path.EndsWith(".tmp", StringComparison.Ordinal));
    }

    /// <summary>The JSON printed under one arm's Provenance heading.</summary>
    private static string ProvenanceBlock(string markdown, string strategy)
    {
        string heading = $"`{strategy}`, `POST {ComparisonRunner.GenerateRoute}`:";
        int start = markdown.IndexOf(heading, StringComparison.Ordinal);
        Assert.True(start >= 0, $"The Provenance section has no block for {strategy}.");
        int open = markdown.IndexOf("```json", start, StringComparison.Ordinal) + "```json".Length;
        int close = markdown.IndexOf("```", open, StringComparison.Ordinal);
        return markdown[open..close];
    }

    /// <summary>Run both arms over one <see cref="ObservingRuntime"/> per arm.</summary>
    private static async Task<IReadOnlyList<ArmResult>> RunArmsAsync(ComparisonWorkload workload, List<ObservingRuntime> runtimes)
    {
        var arms = new List<ArmResult>();
        foreach (string strategy in ContextBindingStrategies.Names)
        {
            arms.Add(await ComparisonRunner.RunArmAsync(strategy, workload, builder =>
            {
                var runtime = new ObservingRuntime(workload.Concurrency);
                runtimes.Add(runtime);
                builder.Services.AddSingleton<IInferenceRuntime>(runtime);
            }, Ct));
        }

        return arms;
    }

    /// <summary>
    /// Records what an arm actually did: how many generations it was asked for, and how many
    /// it was running at once. The first <c>rendezvous</c> callers wait for each other, so
    /// the peak is observed rather than raced for.
    /// </summary>
    private sealed class ObservingRuntime(int rendezvous) : TenantServingTests.DeterministicInferenceRuntimeBase
    {
        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _generateCalls;
        private int _active;
        private int _peak;

        public int GenerateCalls => Volatile.Read(ref _generateCalls);

        public int PeakConcurrency => Volatile.Read(ref _peak);

        public override async Task<InferenceResult> GenerateAsync(string contextId, string prompt, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _generateCalls);
            int active = Interlocked.Increment(ref _active);
            for (int peak = Volatile.Read(ref _peak);
                 active > peak && Interlocked.CompareExchange(ref _peak, active, peak) != peak;
                 peak = Volatile.Read(ref _peak))
            {
            }

            if (active >= rendezvous)
            {
                _reached.TrySetResult();
            }

            try
            {
                // Bounded, so a strategy that cannot reach the rendezvous fails the assertion
                // rather than hanging the suite.
                await _reached.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            }
            catch (TimeoutException)
            {
                _reached.TrySetResult();
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }

            return await base.GenerateAsync(contextId, prompt, cancellationToken);
        }
    }

    [Fact]
    public async Task A_rejected_request_aborts_the_run_and_publishes_nothing()
    {
        using var directory = new TempDirectory();
        string output = Path.Combine(directory.Path, "strategy-comparison.md");
        const string published = "# an earlier table that must survive a failed run\n";
        await File.WriteAllTextAsync(output, published, Ct);

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int exitCode = await Comparison.ExecuteAsync(
            new ComparisonOptions
            {
                Workload = TinyComparison.TinyWorkload(),
                OutputPath = output,
                Configure = builder => builder.Services.AddSingleton<IInferenceRuntime>(
                    new TenantServingTests.FaultyRuntime { FailGenerate = true }),
            },
            stdout, stderr, Ct);

        Assert.Equal(Comparison.AbortedExitCode, exitCode);

        // Loudly, and naming the fail-closed route that was taken.
        Assert.Contains("comparison aborted", stderr.ToString(), StringComparison.Ordinal);
        Assert.Contains(ProblemTypes.GenerationFailed, stderr.ToString(), StringComparison.Ordinal);

        // And with nothing published: not a partial table, not an emptied file.
        Assert.Equal(published, await File.ReadAllTextAsync(output, Ct));
    }

    [Fact]
    public async Task Re_running_regenerates_the_table_and_changes_nothing_else()
    {
        using var directory = new TempDirectory();
        string output = Path.Combine(directory.Path, "strategy-comparison.md");
        string neighbour = Path.Combine(directory.Path, "unrelated.md");
        await File.WriteAllTextAsync(neighbour, "untouched", Ct);

        var options = new ComparisonOptions { Workload = TinyComparison.TinyWorkload(), OutputPath = output };
        Assert.Equal(0, await Comparison.ExecuteAsync(options, new StringWriter(), new StringWriter(), Ct));
        string first = await File.ReadAllTextAsync(output, Ct);

        Assert.Equal(0, await Comparison.ExecuteAsync(options, new StringWriter(), new StringWriter(), Ct));
        string second = await File.ReadAllTextAsync(output, Ct);

        // Regenerated in place: one file, overwritten, and the shape of the comparison holds
        // even though the figures move by ordinary jitter.
        Assert.Equal(["strategy-comparison.md", "unrelated.md"], Directory.GetFiles(directory.Path).Select(Path.GetFileName).Order());
        Assert.Equal("untouched", await File.ReadAllTextAsync(neighbour, Ct));
        foreach (string document in new[] { first, second })
        {
            Assert.StartsWith("# Binding strategy comparison", document, StringComparison.Ordinal);
            foreach (string strategy in ContextBindingStrategies.Names)
            {
                Assert.Contains($"| `{TimingStages.AdapterApplication.Field}` | `{strategy}` |", document, StringComparison.Ordinal);
            }

            Assert.Contains(ComparisonReport.ResidentApplicationNote, document, StringComparison.Ordinal);
        }
    }
}
