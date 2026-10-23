using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using LoraHotswap.Infrastructure;

namespace LoraHotswap.Compare;

/// <summary>
/// What produced the figures (NFR3). Published beside every table, because a per-stage
/// microsecond figure without the machine, the runtime and the build it came from is not
/// a number a reader can judge -- and because Phase A's figures are the deterministic
/// fake's, not the cost of real inference, which the block has to say out loud.
/// </summary>
/// <param name="Machine">The hardware: CPU, logical cores, memory.</param>
/// <param name="OperatingSystem">The OS and its architecture.</param>
/// <param name="Runtime">The .NET runtime the arms ran on.</param>
/// <param name="BuildConfiguration">The configuration the tool was built in.</param>
/// <param name="InferenceRuntime">The runtime behind <c>IInferenceRuntime</c> during the run.</param>
/// <param name="Telemetry">What was exporting, if anything, while the workload ran.</param>
/// <param name="GeneratedAt">When the run happened. A wall-clock stamp, not a measurement.</param>
public sealed record RunEnvironment(
    string Machine,
    string OperatingSystem,
    string Runtime,
    string BuildConfiguration,
    string InferenceRuntime,
    string Telemetry,
    DateTimeOffset GeneratedAt)
{
    /// <summary>
    /// The runtime line for the environment block, built from the type names the arms
    /// actually resolved. The Phase A caveat is attached only when the fake is what ran:
    /// once Epic 3 registers a real runtime behind the same port, the block reports that
    /// instead of repeating a claim that has stopped being true.
    /// </summary>
    public static string DescribeInferenceRuntime(IEnumerable<string> observedTypeNames)
    {
        ArgumentNullException.ThrowIfNull(observedTypeNames);

        string[] observed = observedTypeNames.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (observed.Length == 0)
        {
            throw new ComparisonAbortedException("No arm reported which inference runtime served it.");
        }

        string named = string.Join(", ", observed.Select(name => $"`{name}`")) +
            " -- observed in the composed application that served the workload.";

        return observed.Contains(nameof(DeterministicInferenceRuntime), StringComparer.Ordinal)
            ? named + $" `{nameof(DeterministicInferenceRuntime)}` is the Phase A deterministic fake (AD-8): it records " +
                "the adapter set it is asked to apply and generates no text, so `generation_us` is the cost of the fake, " +
                "**not** the cost of real inference."
            : named;
    }

    /// <summary>
    /// Read the environment of the machine this run is happening on.
    /// <paramref name="inferenceRuntime"/> is the runtime line the arms observed;
    /// <paramref name="hardware"/> overrides the detected hardware line, for a machine
    /// whose CPU the tool cannot name; <paramref name="telemetry"/> overrides the
    /// telemetry line, for a run that is not a two-arm comparison.
    /// </summary>
    public static RunEnvironment Capture(
        string inferenceRuntime, string? hardware = null, string? telemetry = null) => new(
        Machine: string.IsNullOrWhiteSpace(hardware) ? DetectMachine() : hardware.Trim(),
        OperatingSystem: $"{RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant()})",
        Runtime: $"{RuntimeInformation.FrameworkDescription} ({RuntimeInformation.RuntimeIdentifier})",
        BuildConfiguration: typeof(RunEnvironment).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration
            ?? "unknown",
        InferenceRuntime: inferenceRuntime,
        Telemetry: string.IsNullOrWhiteSpace(telemetry)
            ? "Console exporter off, no OTLP endpoint, logging providers cleared -- in both arms."
            : telemetry.Trim(),
        GeneratedAt: DateTimeOffset.UtcNow);

    /// <summary>The environment as rows for the published block.</summary>
    public IReadOnlyList<(string Field, string Value)> Describe() =>
    [
        ("Hardware", Machine),
        ("OS", OperatingSystem),
        ("Runtime", Runtime),
        ("Build", BuildConfiguration),
        ("Inference runtime", InferenceRuntime),
        ("Telemetry during the run", Telemetry),
        ("Produced", GeneratedAt.ToString("u", CultureInfo.InvariantCulture)),
    ];

    private static string DetectMachine()
    {
        string cores = $"{Environment.ProcessorCount.ToString(CultureInfo.InvariantCulture)} logical cores";
        string architecture = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();

        string? cpu = Sysctl("machdep.cpu.brand_string");
        string? memory = long.TryParse(Sysctl("hw.memsize"), CultureInfo.InvariantCulture, out long bytes)
            ? $"{(bytes / (1024L * 1024 * 1024)).ToString(CultureInfo.InvariantCulture)} GB"
            : null;

        return string.Join(", ", new[] { cpu, architecture, cores, memory }.Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    /// <summary>
    /// The CPU as macOS names it. Best effort: a machine that cannot answer gets the
    /// architecture and core count instead, and a reader can always pass the hardware in.
    /// </summary>
    private static string? Sysctl(string name)
    {
        // Fully qualified: the record's own OperatingSystem member shadows the BCL type here.
        if (!System.OperatingSystem.IsMacOS())
        {
            return null;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo("/usr/sbin/sysctl", ["-n", name])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });

            if (process is null)
            {
                return null;
            }

            string value = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(5_000);
            return value.Length == 0 ? null : value;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }
}
