using System.Globalization;
using LoraHotswap.Core;
using LoraHotswap.Infrastructure;

namespace LoraHotswap.Capture;

/// <summary>
/// The capture tool (Story 3.2). A tool, not a test: it loads a real base model, generates
/// real text, and publishes the demonstration a reader meets before they build anything.
/// The suite never runs it.
///
/// <code>
///   dotnet run -c Release --project tools/capture
///   dotnet run -c Release --project tools/capture -- --max-tokens 160 --strategy PerTenantResident
/// </code>
/// </summary>
public static class Program
{
    /// <summary>The README whose generated region carries the demonstration.</summary>
    public const string DefaultReadmePath = "README.md";

    /// <summary>The exit code for a command line the tool could not act on.</summary>
    public const int UsageExitCode = 2;

    /// <summary>Parse the command line, run the capture, and report the exit code.</summary>
    public static async Task<int> Main(string[] args)
    {
        if (args.Contains("--help") || args.Contains("-h"))
        {
            Console.Out.WriteLine(Usage);
            return 0;
        }

        CaptureOptions options;
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
        catch (CaptureAbortedException ex)
        {
            // A plan that cannot be built is a command line that cannot be acted on -- an
            // adapter directory with no content identity, an unknown strategy -- rather than
            // a run that failed.
            await Console.Error.WriteLineAsync(ex.Message);
            return UsageExitCode;
        }

        try
        {
            return await TenantCapture.ExecuteAsync(options, Console.Out, Console.Error, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A reason line, never a bare stack trace.
            await Console.Error.WriteLineAsync($"capture failed: {ex.GetType().Name}: {ex.Message}");
            return TenantCapture.AbortedExitCode;
        }
    }

    /// <summary>The usage text.</summary>
    public static string Usage =>
        $"""
        Capture the demonstration: serves one prompt to each configured tenant through the
        real runtime in one process and publishes the answers exactly as they were served.

          --output PATH        where to write the capture (default: {CaptureReport.DefaultOutputPath} under the repository root)
          --readme PATH        the README whose generated region is filled (default: {DefaultReadmePath})
          --no-readme          publish only the capture document, leaving the README alone
          --adapters DIR       the adapters root (default: {Demonstration.DefaultAdaptersRoot} under the repository root)
          --model PATH         the GGUF base model (default: {Demonstration.DefaultModelPath} under the repository root)
          --tenant NAME=DIR    a tenant and its adapter directory, repeatable; replaces the
                               default three ({string.Join(", ", Demonstration.DefaultTenants.Select(t => $"{t.Tenant}={t.DirectoryName}"))})
          --strategy NAME      binding strategy (default: {PerRequestApplyStrategy.StrategyName})
          --contexts N         contexts in the pool (default: {CapturePlan.DefaultContextPoolSize})
          --context-size N     tokens of KV cache per context (default: {CapturePlan.DefaultContextSize})
          --max-tokens N       the token budget for one answer (default: {CapturePlan.DefaultMaxTokens})
          --template TEXT      the prompt template; '{LlamaSharpInferenceRuntime.PromptPlaceholder}' is substituted (default: Qwen3's chat turn)
          --raw-prompt         send the prompt with no template at all
          --hardware TEXT      the hardware line for the environment block, when the CPU cannot be detected
          --help               this text
        """;

    /// <summary>Turn a command line into options, or throw <see cref="ArgumentException"/>.</summary>
    public static CaptureOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        string root = RepositoryRoot();
        string? output = null;
        string? readme = null;
        bool noReadme = false;
        string? adaptersRoot = null;
        string? model = null;
        string? hardware = null;
        string? template = null;
        var tenants = new List<(string Tenant, string DirectoryName)>();
        string strategy = PerRequestApplyStrategy.StrategyName;
        int contexts = CapturePlan.DefaultContextPoolSize;
        int contextSize = CapturePlan.DefaultContextSize;
        int maxTokens = CapturePlan.DefaultMaxTokens;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--output": output = Value(args, ref i); break;
                case "--readme": readme = Value(args, ref i); break;
                case "--no-readme": noReadme = true; break;
                case "--adapters": adaptersRoot = Value(args, ref i); break;
                case "--model": model = Value(args, ref i); break;
                case "--tenant": tenants.Add(Tenant(args, ref i)); break;
                case "--strategy": strategy = Value(args, ref i); break;
                case "--contexts": contexts = Count(args, ref i); break;
                case "--context-size": contextSize = Count(args, ref i); break;
                case "--max-tokens": maxTokens = Count(args, ref i); break;
                case "--template": template = Value(args, ref i); break;
                case "--raw-prompt": template = string.Empty; break;
                case "--hardware": hardware = Value(args, ref i); break;
                default: throw new ArgumentException($"Unknown argument '{args[i]}'.");
            }
        }

        if (noReadme && readme is not null)
        {
            throw new ArgumentException("'--no-readme' and '--readme' contradict each other.");
        }

        string resolvedAdapters = Path.GetFullPath(adaptersRoot ?? Path.Combine(root, Demonstration.DefaultAdaptersRoot));
        IReadOnlyList<(string Tenant, string DirectoryName)> named =
            tenants.Count > 0 ? tenants : Demonstration.DefaultTenants;

        return new CaptureOptions
        {
            OutputPath = Path.GetFullPath(output ?? Path.Combine(root, CaptureReport.DefaultOutputPath)),
            ReadmePath = noReadme ? null : Path.GetFullPath(readme ?? Path.Combine(root, DefaultReadmePath)),
            Hardware = hardware,
            Plan = new CapturePlan
            {
                ModelPath = Path.GetFullPath(model ?? Path.Combine(root, Demonstration.DefaultModelPath)),
                AdaptersRoot = resolvedAdapters,
                Tenants = Describe(resolvedAdapters, named),
                BaseModelName = BaseModelName(resolvedAdapters, named),
                BindingStrategy = strategy,
                ContextPoolSize = contexts,
                ContextSize = contextSize,
                MaxTokens = maxTokens,
                PromptTemplate = template ?? Demonstration.ChatTemplate,
            }.Validate(),
        };
    }

    /// <summary>
    /// The tenants with their adapters' content identities, derived from the adapters' own
    /// bytes. A directory that is absent, or half-acquired, is not an error here: the plan's
    /// asset check reports every missing file at once with the tool that produces it, and
    /// throwing on the first one would replace that with a refusal naming only itself.
    /// </summary>
    private static IReadOnlyList<CaptureTenant> Describe(
        string adaptersRoot, IReadOnlyList<(string Tenant, string DirectoryName)> tenants) =>
        tenants.Select(t =>
        {
            string directory = Path.Combine(adaptersRoot, t.DirectoryName);
            return new CaptureTenant(
                t.Tenant,
                t.DirectoryName,
                Demonstration.HasContentIdentity(directory) ? Demonstration.AdapterIdentityOf(directory) : Unknown);
        }).ToArray();

    /// <summary>
    /// The base model every adapter declares. They must agree: admission matches the
    /// configured base model strictly, so a mixed set has no single configuration that
    /// admits all of it.
    /// </summary>
    private static string BaseModelName(
        string adaptersRoot, IReadOnlyList<(string Tenant, string DirectoryName)> tenants)
    {
        var declared = tenants
            .Select(t => Path.Combine(adaptersRoot, t.DirectoryName))
            .Where(Demonstration.HasDeclaredBaseModel)
            .Select(directory => (Directory: directory, Base: Demonstration.DeclaredBaseModelOf(directory)))
            .GroupBy(pair => pair.Base, StringComparer.Ordinal)
            .ToArray();

        if (declared.Length > 1)
        {
            throw new CaptureAbortedException(
                "The tenants' adapters target different base models, so no single configuration admits them all: " +
                string.Join("; ", declared.Select(g =>
                    $"{g.Key} <- {string.Join(", ", g.Select(p => Path.GetFileName(p.Directory)))}")) + ".");
        }

        // With nothing on disk yet there is nothing to read. Admission never gets the chance
        // to disagree with it: the asset check refuses the run first, naming every absent file.
        return declared.Length == 1 ? declared[0].Key : Unknown;
    }

    /// <summary>
    /// Stands in for a value that could only be read off an absent file. It is never served
    /// with: the plan's asset check refuses the run before the host is composed.
    /// </summary>
    private const string Unknown = "sha256:" + "0000000000000000000000000000000000000000000000000000000000000000";

    private static (string Tenant, string DirectoryName) Tenant(string[] args, ref int i)
    {
        string raw = Value(args, ref i);
        string[] parts = raw.Split('=', 2);
        return parts.Length == 2 && parts[0].Length > 0 && parts[1].Length > 0
            ? (parts[0], parts[1])
            : throw new ArgumentException($"'--tenant' needs NAME=DIRECTORY, got '{raw}'.");
    }

    /// <summary>
    /// The value after a flag. A value that is itself a flag means the value was omitted:
    /// without this, <c>--output --max-tokens 5</c> quietly writes the capture to a file
    /// called <c>--max-tokens</c>.
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
    /// The repository root, found by walking up to the solution file, so the tool's defaults
    /// work from any working directory.
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
                "Pass --adapters, --model and --output explicitly.");
    }
}
