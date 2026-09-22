using LoraHotswap.Core;
using LoraHotswap.Core.Ports;
using System.Diagnostics;

namespace LoraHotswap.Api;

/// <summary>
/// The outcome of the startup admission run: either a built, immutable
/// <see cref="AdapterRegistry"/>, or the accumulated <see cref="AdmissionProblem"/>s
/// that must be written to stderr before the process exits non-zero. Producing this
/// outcome is kept free of any process-exit or listener concern so it stays
/// unit-testable; the composition root maps it to an exit code.
/// </summary>
public sealed record AdmissionStartupOutcome
{
    private AdmissionStartupOutcome(
        AdapterRegistry? registry,
        IReadOnlyDictionary<AdapterId, string> adapterDirectories,
        IReadOnlyList<AdmissionProblem> problems)
    {
        Registry = registry;
        AdapterDirectories = adapterDirectories;
        Problems = problems;
    }

    /// <summary>The built registry on success; otherwise null.</summary>
    public AdapterRegistry? Registry { get; }

    /// <summary>
    /// Where each admitted adapter's files live, so verification can read them. A storage
    /// location only -- adapters are still identified by content, never by path (AD-1).
    /// </summary>
    public IReadOnlyDictionary<AdapterId, string> AdapterDirectories { get; }

    /// <summary>Every admission problem (rejected adapters and unresolved tenants); empty on success.</summary>
    public IReadOnlyList<AdmissionProblem> Problems { get; }

    /// <summary>True iff every adapter was admitted and every tenant resolved.</summary>
    public bool Succeeded => Registry is not null && Problems.Count == 0;

    internal static AdmissionStartupOutcome Ok(AdapterRegistry registry, IReadOnlyDictionary<AdapterId, string> directories) =>
        new(registry, directories, []);

    internal static AdmissionStartupOutcome Failed(IReadOnlyList<AdmissionProblem> problems) =>
        new(null, new Dictionary<AdapterId, string>(), problems);
}

/// <summary>
/// Drives startup admission: discover-provided adapter directories are each read
/// through the <see cref="IAdapterArtifactsReader"/> port and judged by the pure
/// <see cref="Admission"/> decision; admitted adapters build the immutable
/// <see cref="AdapterRegistry"/>; every configured tenant is resolved to an admitted
/// adapter. Any rejection or unresolved tenant is accumulated as a problem.
/// </summary>
public static class AdmissionStartup
{
    /// <summary>The span name for one adapter's admission decision.</summary>
    public const string SpanName = "adapter.admission";

    /// <summary>Run admission over the given directories and tenant map.</summary>
    public static async Task<AdmissionStartupOutcome> RunAsync(
        IReadOnlyList<string> adapterDirectories,
        string configuredBaseModel,
        IReadOnlyDictionary<TenantId, AdapterId> tenantMap,
        IAdapterArtifactsReader reader,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(adapterDirectories);
        ArgumentNullException.ThrowIfNull(reader);

        var problems = new List<AdmissionProblem>();
        var admitted = new List<AdmittedAdapter>();
        var directories = new Dictionary<AdapterId, string>();

        foreach (var directory in adapterDirectories)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // One span per adapter (Story 1.8), admitted or rejected, always carrying
            // adapter.id. The directory name is a label only (adapter.name), never identity.
            using Activity? activity = LoraHotswapTelemetry.Source.StartActivity(SpanName, ActivityKind.Internal);
            activity?.SetTag("adapter.name", new DirectoryInfo(directory).Name);

            AdapterArtifacts artifacts;
            AdmissionResult result;
            try
            {
                artifacts = await reader.ReadAsync(directory, cancellationToken);
                result = Admission.Admit(artifacts, configuredBaseModel);
            }
            catch (Exception ex)
            {
                // A read that throws (unreadable file, malformed JSON) still closes its
                // span with a result and an error status before startup fails.
                activity?.SetTag("adapter.id", LoraHotswapTelemetry.UnidentifiedAdapter);
                activity?.SetTag("admission.result", "error");
                activity?.SetTag("admission.reason", ex.GetType().Name);
                // The type name only: exception messages and stack traces from the file
                // system carry paths, and an adapter is never named by path (AD-1).
                activity?.SetStatus(ActivityStatusCode.Error, ex.GetType().Name);
                throw;
            }

            if (result.IsAdmitted)
            {
                AdapterId id = result.AdmittedId!.Value;
                activity?.SetTag("adapter.id", id.Value);
                activity?.SetTag("admission.result", "admitted");
                var snapshot = artifacts.Config;
                var config = snapshot is not null
                    ? new AdapterConfig(snapshot.R, snapshot.Alpha, snapshot.UseRsLora, snapshot.BaseModelName)
                    : new AdapterConfig(0, 0, false, configuredBaseModel);
                admitted.Add(new AdmittedAdapter(id, config, artifacts.AdapterDirectoryName));
                directories.TryAdd(id, directory);
            }
            else
            {
                AdmissionProblem problem = result.Problem!;
                // Identity is the content hash of the source bytes on disk, known even for
                // a rejected adapter whenever those bytes exist.
                activity?.SetTag("adapter.id", artifacts.RecomputedSourceSha256 is { } sha
                    ? "sha256:" + sha
                    : LoraHotswapTelemetry.UnidentifiedAdapter);
                activity?.SetTag("admission.result", "rejected");
                activity?.SetTag("admission.reason", problem.Type);
                activity?.SetStatus(ActivityStatusCode.Error, problem.Title);
                problems.Add(problem);
            }
        }

        RegistryBuildResult build = AdapterRegistry.Build(admitted, tenantMap);
        problems.AddRange(build.Problems);

        if (problems.Count > 0)
        {
            return AdmissionStartupOutcome.Failed(problems);
        }

        return AdmissionStartupOutcome.Ok(build.Registry!, directories);
    }
}
