using LoraHotswap.Compare;
using Microsoft.AspNetCore.Builder;

namespace LoraHotswap.Capture;

/// <summary>Everything one capture run needs.</summary>
public sealed record CaptureOptions
{
    /// <summary>What is served, and how.</summary>
    public required CapturePlan Plan { get; init; }

    /// <summary>Where the full capture is published. Overwritten on every run.</summary>
    public required string OutputPath { get; init; }

    /// <summary>
    /// The README whose generated region carries the demonstration, or
    /// <see langword="null"/> to publish only the capture document.
    /// </summary>
    public string? ReadmePath { get; init; }

    /// <summary>The hardware line for the environment block, when the tool cannot name the CPU itself.</summary>
    public string? Hardware { get; init; }

    /// <summary>
    /// An extra composition step. The suite uses it to inject a faulting runtime and assert
    /// a rejection publishes nothing; a published capture passes nothing.
    /// </summary>
    public Action<WebApplicationBuilder>? Configure { get; init; }
}

/// <summary>
/// The capture end to end: serve every prompt to every tenant, render both documents,
/// then write them. Nothing is written until every answer is in hand and both documents
/// have rendered, so a run that fails part-way publishes nothing at all rather than a
/// half-finished demonstration a reader would take for a whole one.
/// </summary>
public static class TenantCapture
{
    /// <summary>The process exit code for a run that aborted before publishing.</summary>
    public const int AbortedExitCode = 1;

    /// <summary>
    /// The telemetry line for the published environment block. The capture exports nothing:
    /// a console exporter would interleave spans with the answers it exists to read.
    /// </summary>
    public const string TelemetryDuringTheRun =
        "Console exporter off, no OTLP endpoint, logging providers cleared.";

    /// <summary>Run the capture. Returns <c>0</c> when both documents were written.</summary>
    public static async Task<int> ExecuteAsync(
        CaptureOptions options, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);

        try
        {
            CapturePlan plan = options.Plan.Validate();

            // Read before anything is served: a run that cannot publish into the README is a
            // run that should not have loaded an 800 MB model first.
            string? readme = options.ReadmePath is { } path
                ? await ReadReadmeAsync(path, cancellationToken)
                : null;

            CaptureResult result = await CaptureRunner.RunAsync(plan, options.Configure, stdout, cancellationToken);

            RunEnvironment environment = RunEnvironment.Capture(
                RunEnvironment.DescribeInferenceRuntime([result.InferenceRuntime]),
                options.Hardware,
                TelemetryDuringTheRun);

            // Both documents render before either is written. A render that throws -- no
            // answers, a README with no generated region -- must not leave one of the two
            // published and the other stale.
            string capture = CaptureReport.Render(plan, result, environment);
            string? updated = readme is null
                ? null
                : CaptureReport.ReplaceReadmeSection(
                    readme,
                    CaptureReport.RenderReadmeSection(plan, result, environment, RelativeTo(options.ReadmePath!, options.OutputPath)));

            var documents = new List<(string Path, string Markdown)> { (options.OutputPath, capture) };
            if (updated is not null)
            {
                documents.Add((options.ReadmePath!, updated));
            }

            await PublishAsync(documents, cancellationToken);
            foreach ((string published, string _) in documents)
            {
                await stdout.WriteLineAsync($"wrote {published}");
            }

            return 0;
        }
        catch (CaptureAbortedException ex)
        {
            // Loudly, and with the rejection's own problem type where there was one: "the
            // capture failed" alone tells a reader nothing about which route was taken.
            await stderr.WriteLineAsync($"capture aborted: {ex.Message}");
            await stderr.WriteLineAsync(
                "Nothing was published. A partial demonstration is not written, and the previously published " +
                "capture and README are left as they were.");
            return AbortedExitCode;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The host's own convention: a failure leaves as a reason line, never a bare
            // stack trace. An unwritable output path lands here.
            await stderr.WriteLineAsync($"capture failed: {ex.GetType().Name}: {ex.Message}");
            return AbortedExitCode;
        }
    }

    private static async Task<string> ReadReadmeAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            throw new CaptureAbortedException($"'{path}' does not exist, so there is no generated region to fill.");
        }

        string readme = await File.ReadAllTextAsync(path, cancellationToken);

        // Checked here as well as at replacement time, so a run does not spend minutes
        // generating before discovering it has nowhere to put the result.
        _ = CaptureReport.ReplaceReadmeSection(readme, string.Empty);
        return readme;
    }

    /// <summary>The capture's path as the README should link it: relative, with forward slashes.</summary>
    private static string RelativeTo(string readmePath, string outputPath)
    {
        string from = Path.GetDirectoryName(Path.GetFullPath(readmePath)) ?? ".";
        return Path.GetRelativePath(from, Path.GetFullPath(outputPath)).Replace(Path.DirectorySeparatorChar, '/');
    }

    /// <summary>
    /// Publish every document or none of them. Each is written to a sibling first -- so the
    /// move is a rename within one directory rather than a copy across devices, and no
    /// published file is ever truncated mid-write -- and the renames happen only once every
    /// document has been staged successfully. A capture that could be written beside a
    /// README that could not is exactly the state both documents say cannot happen.
    /// </summary>
    private static async Task PublishAsync(
        IReadOnlyList<(string Path, string Markdown)> documents, CancellationToken cancellationToken)
    {
        var staged = new List<(string Staged, string Final)>(documents.Count);
        try
        {
            foreach ((string path, string markdown) in documents)
            {
                string full = Path.GetFullPath(path);
                string? directory = Path.GetDirectoryName(full);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                string sibling = full + ".tmp";
                await File.WriteAllTextAsync(sibling, markdown, cancellationToken);
                staged.Add((sibling, full));
            }

            // Only now, with every document on disk beside its destination: the renames left
            // are the cheapest operation the filesystem has.
            foreach ((string sibling, string full) in staged)
            {
                File.Move(sibling, full, overwrite: true);
            }
        }
        finally
        {
            foreach ((string sibling, string _) in staged)
            {
                if (File.Exists(sibling))
                {
                    File.Delete(sibling);
                }
            }
        }
    }
}
