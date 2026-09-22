using LoraHotswap.Core;
using Microsoft.AspNetCore.Builder;

namespace LoraHotswap.Compare;

/// <summary>Everything one comparison run needs.</summary>
public sealed record ComparisonOptions
{
    /// <summary>The one workload both arms run.</summary>
    public required ComparisonWorkload Workload { get; init; }

    /// <summary>Where the rendered table is written. Overwritten on every run.</summary>
    public required string OutputPath { get; init; }

    /// <summary>The hardware line for the environment block, when the tool cannot name the CPU itself.</summary>
    public string? Hardware { get; init; }

    /// <summary>The arms, in the order they run. Both strategies, by default.</summary>
    public IReadOnlyList<string> Strategies { get; init; } = ContextBindingStrategies.Names;

    /// <summary>
    /// An extra composition step per arm. The suite uses it to inject a faulting runtime
    /// and assert a rejection aborts the run; a published comparison passes nothing.
    /// </summary>
    public Action<WebApplicationBuilder>? Configure { get; init; }
}

/// <summary>
/// The comparison end to end: run each arm, render one table, write it once. The file is
/// written only after every arm has finished, so a run that fails part-way publishes
/// nothing rather than a partial comparison a reader would take for a whole one.
/// </summary>
public static class Comparison
{
    /// <summary>The process exit code for a run that aborted before publishing.</summary>
    public const int AbortedExitCode = 1;

    /// <summary>Run the comparison. Returns <c>0</c> when the table was written.</summary>
    public static async Task<int> ExecuteAsync(
        ComparisonOptions options, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);

        try
        {
            options.Workload.Validate();

            var arms = new List<ArmResult>(options.Strategies.Count);
            foreach (string strategy in options.Strategies)
            {
                // The arms run one after the other in the same process, never side by side:
                // two arms competing for the same cores would measure the contention.
                await stdout.WriteLineAsync(
                    $"{strategy}: {options.Workload.WarmupRequests} warm-up requests (discarded), " +
                    $"then {options.Workload.Requests} measured at concurrency {options.Workload.Concurrency}...");
                arms.Add(await ComparisonRunner.RunArmAsync(strategy, options.Workload, options.Configure, cancellationToken));
            }

            RunEnvironment environment = RunEnvironment.Capture(
                RunEnvironment.DescribeInferenceRuntime(arms.Select(arm => arm.InferenceRuntime)), options.Hardware);

            await WriteAsync(options.OutputPath, ComparisonReport.Render(arms, options.Workload, environment), cancellationToken);
            await stdout.WriteLineAsync($"wrote {options.OutputPath}");
            return 0;
        }
        catch (ComparisonAbortedException ex)
        {
            // Loudly, and with the rejection's own problem type: "the run failed" without
            // it tells a reader nothing about which fail-closed route was taken.
            await stderr.WriteLineAsync($"comparison aborted: {ex.Message}");
            await stderr.WriteLineAsync(
                "No table was written. A partial comparison is not published, and any previously published table is left as it was.");
            return AbortedExitCode;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The host's own convention: a failure leaves as a reason line, never a bare
            // stack trace. An unwritable output path and a bad exporter setting both land here.
            await stderr.WriteLineAsync($"comparison failed: {ex.GetType().Name}: {ex.Message}");
            return AbortedExitCode;
        }
    }

    /// <summary>
    /// Publish the document, replacing the previous one atomically. Writing in place would
    /// truncate the published table first, so a failure mid-write would leave exactly the
    /// half-published comparison the run is supposed to be incapable of producing.
    /// </summary>
    private static async Task WriteAsync(string outputPath, string markdown, CancellationToken cancellationToken)
    {
        string full = Path.GetFullPath(outputPath);
        string? directory = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // A sibling, so the move is a rename within one directory rather than a copy across
        // devices (which would reintroduce the partial write it exists to avoid).
        string staged = full + ".tmp";
        try
        {
            await File.WriteAllTextAsync(staged, markdown, cancellationToken);
            File.Move(staged, full, overwrite: true);
        }
        finally
        {
            if (File.Exists(staged))
            {
                File.Delete(staged);
            }
        }
    }
}
