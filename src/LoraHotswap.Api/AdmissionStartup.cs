using LoraHotswap.Core;
using LoraHotswap.Core.Ports;

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
    private AdmissionStartupOutcome(AdapterRegistry? registry, IReadOnlyList<AdmissionProblem> problems)
    {
        Registry = registry;
        Problems = problems;
    }

    /// <summary>The built registry on success; otherwise null.</summary>
    public AdapterRegistry? Registry { get; }

    /// <summary>Every admission problem (rejected adapters and unresolved tenants); empty on success.</summary>
    public IReadOnlyList<AdmissionProblem> Problems { get; }

    /// <summary>True iff every adapter was admitted and every tenant resolved.</summary>
    public bool Succeeded => Registry is not null && Problems.Count == 0;

    internal static AdmissionStartupOutcome Ok(AdapterRegistry registry) => new(registry, []);
    internal static AdmissionStartupOutcome Failed(IReadOnlyList<AdmissionProblem> problems) => new(null, problems);
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

        foreach (var directory in adapterDirectories)
        {
            cancellationToken.ThrowIfCancellationRequested();

            AdapterArtifacts artifacts = await reader.ReadAsync(directory, cancellationToken);
            AdmissionResult result = Admission.Admit(artifacts, configuredBaseModel);

            if (result.IsAdmitted)
            {
                var snapshot = artifacts.Config;
                var config = snapshot is not null
                    ? new AdapterConfig(snapshot.R, snapshot.Alpha, snapshot.UseRsLora, snapshot.BaseModelName)
                    : new AdapterConfig(0, 0, false, configuredBaseModel);
                admitted.Add(new AdmittedAdapter(result.AdmittedId!.Value, config, artifacts.AdapterDirectoryName));
            }
            else
            {
                problems.Add(result.Problem!);
            }
        }

        RegistryBuildResult build = AdapterRegistry.Build(admitted, tenantMap);
        problems.AddRange(build.Problems);

        if (problems.Count > 0)
        {
            return AdmissionStartupOutcome.Failed(problems);
        }

        return AdmissionStartupOutcome.Ok(build.Registry!);
    }
}
