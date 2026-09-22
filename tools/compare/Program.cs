using System.Globalization;

namespace LoraHotswap.Compare;

/// <summary>
/// The comparison tool (Story 2.8). A tool, not a test: a reader regenerates Epic 2's
/// headline table without running the suite, and the suite never runs the comparison as
/// part of its normal pass.
///
/// <code>
///   dotnet run -c Release --project tools/compare
///   dotnet run -c Release --project tools/compare -- --requests 500 --concurrency 8
/// </code>
/// </summary>
public static class Program
{
    /// <summary>The published table, relative to the repository root.</summary>
    public const string DefaultOutputPath = "docs/strategy-comparison.md";

    /// <summary>
    /// The adapters the comparison serves from by default: the two committed fixture
    /// adapters the identity proof already uses as two tenants, so the tool runs offline
    /// with no acquisition or conversion step.
    /// </summary>
    public const string DefaultAdaptersRoot = "tests/LoraHotswap.Tests/fixtures/merge";

    /// <summary>The exit code for a command line the tool could not act on.</summary>
    public const int UsageExitCode = 2;

    /// <summary>Parse the command line, run the comparison, and report the exit code.</summary>
    public static async Task<int> Main(string[] args)
    {
        if (args.Contains("--help") || args.Contains("-h"))
        {
            Console.Out.WriteLine(Usage);
            return 0;
        }

        ComparisonOptions options;
        try
        {
            options = Parse(args);
        }
        catch (ArgumentException ex)
        {
            await Console.Error.WriteLineAsync(ex.Message);
            await Console.Error.WriteLineAsync(Usage);
            return UsageExitCode;
        }
        catch (ComparisonAbortedException ex)
        {
            // A workload that cannot be built is a command line that cannot be acted on --
            // an unreadable adapters root, a zero lease timeout -- not a run that failed.
            await Console.Error.WriteLineAsync(ex.Message);
            return UsageExitCode;
        }

        try
        {
            return await Comparison.ExecuteAsync(options, Console.Out, Console.Error, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A reason line, never a bare stack trace.
            await Console.Error.WriteLineAsync($"comparison failed: {ex.GetType().Name}: {ex.Message}");
            return Comparison.AbortedExitCode;
        }
    }

    /// <summary>The usage text.</summary>
    public static string Usage =>
        $"""
        Compare the binding strategies: runs one identical workload against each strategy
        and writes a Markdown table of per-stage costs read from the served responses.

          --output PATH        where to write the table (default: {DefaultOutputPath} under the repository root)
          --adapters DIR       the adapters root to serve from (default: {DefaultAdaptersRoot})
          --adapter NAME       an adapter directory under the root, repeatable; one tenant each
                               (default: every immediate subdirectory)
          --requests N         measured requests per strategy (default: {ComparisonWorkload.DefaultRequests})
          --concurrency N      requests in flight (default: {ComparisonWorkload.DefaultConcurrency})
          --warmup N           requests sent and discarded first (default: {ComparisonWorkload.DefaultWarmupRequests})
          --lease-timeout-ms N lease timeout in both arms (default: {ComparisonWorkload.DefaultLeaseTimeoutMilliseconds})
          --hardware TEXT      the hardware line for the environment block, when the CPU cannot be detected
          --help               this text
        """;

    /// <summary>Turn a command line into options, or throw <see cref="ArgumentException"/>.</summary>
    public static ComparisonOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        string root = RepositoryRoot();
        string? output = null;
        string? adaptersRoot = null;
        var adapters = new List<string>();
        string? hardware = null;
        int requests = ComparisonWorkload.DefaultRequests;
        int concurrency = ComparisonWorkload.DefaultConcurrency;
        int warmup = ComparisonWorkload.DefaultWarmupRequests;
        int leaseTimeout = ComparisonWorkload.DefaultLeaseTimeoutMilliseconds;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--output": output = Value(args, ref i); break;
                case "--adapters": adaptersRoot = Value(args, ref i); break;
                case "--adapter": adapters.Add(Value(args, ref i)); break;
                case "--hardware": hardware = Value(args, ref i); break;
                case "--requests": requests = Count(args, ref i); break;
                case "--concurrency": concurrency = Count(args, ref i); break;
                case "--warmup": warmup = Count(args, ref i); break;
                case "--lease-timeout-ms": leaseTimeout = Count(args, ref i); break;
                default: throw new ArgumentException($"Unknown argument '{args[i]}'.");
            }
        }

        string resolvedAdaptersRoot = Path.GetFullPath(adaptersRoot ?? Path.Combine(root, DefaultAdaptersRoot));
        IReadOnlyList<WorkloadTenant> tenants = AdapterCatalog.Discover(resolvedAdaptersRoot, adapters);

        return new ComparisonOptions
        {
            OutputPath = Path.GetFullPath(output ?? Path.Combine(root, DefaultOutputPath)),
            Hardware = hardware,
            Workload = new ComparisonWorkload
            {
                AdaptersRoot = resolvedAdaptersRoot,
                Tenants = tenants,
                BaseModelName = AdapterCatalog.BaseModelName(resolvedAdaptersRoot, tenants),
                Requests = requests,
                Concurrency = concurrency,
                WarmupRequests = warmup,
                LeaseTimeoutMilliseconds = leaseTimeout,
            }.Validate(),
        };
    }

    /// <summary>
    /// The value after a flag. A value that is itself a flag means the value was omitted:
    /// without this, <c>--output --requests 5</c> quietly writes the table to a file called
    /// <c>--requests</c>.
    /// </summary>
    private static string Value(string[] args, ref int i)
    {
        string flag = args[i];
        if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException($"'{flag}' needs a value.");
        }

        return args[++i];
    }

    private static int Count(string[] args, ref int i)
    {
        string flag = args[i];
        string raw = Value(args, ref i);
        return int.TryParse(raw, CultureInfo.InvariantCulture, out int value)
            ? value
            : throw new ArgumentException($"'{flag}' needs a whole number, got '{raw}'.");
    }

    /// <summary>
    /// The repository root, found by walking up to the solution file, so the tool's
    /// defaults work from any working directory.
    /// </summary>
    public static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "LoraHotswap.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new ArgumentException(
                $"Could not find LoraHotswap.slnx walking up from {AppContext.BaseDirectory}. " +
                "Pass --adapters and --output explicitly.");
    }
}
