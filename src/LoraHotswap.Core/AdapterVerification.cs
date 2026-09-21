using System.Globalization;

namespace LoraHotswap.Core;

/// <summary>The verdict of a two-sided merge verification.</summary>
public enum VerificationOutcome
{
    /// <summary>Every targeted module matches the PEFT reference and differs from its base.</summary>
    Match,

    /// <summary>At least one module disagrees with the reference, or is unchanged while others change.</summary>
    Mismatch,

    /// <summary>No targeted module differs from its base: merging the adapter is a no-op.</summary>
    Inert,
}

/// <summary>
/// One module's two-sided measurement. The two assertions are independent (spec 1.7):
/// reference equality proves arithmetic and orientation; difference from base proves
/// the adapter does something. Neither alone passes.
/// </summary>
/// <param name="Module">The module path, e.g. <c>model.layers.0.self_attn.q_proj</c>.</param>
/// <param name="ReferenceError"><c>max |W' - W_reference|</c>.</param>
/// <param name="ChangeFromBase"><c>max |W' - W0|</c>.</param>
/// <param name="Tolerance">The absolute floating-point tolerance both sides are judged against.</param>
/// <param name="Fault">Why the module could not be measured at all (e.g. shapes do not compose), or <see langword="null"/>.</param>
public sealed record ModuleVerification(
    string Module, double ReferenceError, double ChangeFromBase, double Tolerance, string? Fault = null)
{
    /// <summary>A module that could not be measured: it fails both assertions and carries the reason.</summary>
    public static ModuleVerification Faulted(string module, double tolerance, string fault) =>
        new(module, double.PositiveInfinity, double.PositiveInfinity, tolerance, fault);

    /// <summary>The computed merge agrees with the reference within tolerance.</summary>
    public bool MatchesReference => ReferenceError <= Tolerance;

    /// <summary>
    /// The computed merge differs from the base by more than the tolerance -- a
    /// measurable change, not floating-point noise.
    /// </summary>
    public bool ChangedFromBase => ChangeFromBase > Tolerance;

    /// <summary>Both assertions hold. Passing one is never sufficient.</summary>
    public bool Passed => MatchesReference && ChangedFromBase;
}

/// <summary>An adapter's verification verdict, per module, with a diagnostic detail.</summary>
/// <param name="Adapter">The verified adapter.</param>
/// <param name="Outcome">The verdict.</param>
/// <param name="Modules">Per-module results, ordered by module name.</param>
/// <param name="Detail">A human-readable explanation naming the adapter, and the failing module when there is one.</param>
public sealed record AdapterVerification(
    AdapterId Adapter,
    VerificationOutcome Outcome,
    IReadOnlyList<ModuleVerification> Modules,
    string Detail)
{
    /// <summary>
    /// Judge <paramref name="modules"/>: <see cref="VerificationOutcome.Inert"/> when no
    /// module changed; <see cref="VerificationOutcome.Mismatch"/> when any module fails
    /// either assertion (or when there are no modules at all -- nothing verified is never
    /// a match); otherwise <see cref="VerificationOutcome.Match"/>.
    /// </summary>
    public static AdapterVerification Judge(AdapterId adapter, IReadOnlyList<ModuleVerification> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);

        var ordered = modules.OrderBy(m => m.Module, StringComparer.Ordinal).ToArray();

        if (ordered.Length == 0)
        {
            return new(adapter, VerificationOutcome.Mismatch, ordered,
                $"Adapter '{adapter}' has no targeted module to verify; nothing verified is never a match.");
        }

        if (ordered.All(m => !m.ChangedFromBase))
        {
            return new(adapter, VerificationOutcome.Inert, ordered,
                $"Adapter '{adapter}' is inert: no targeted module differs from its base weights by more than " +
                $"{Format(ordered[0].Tolerance)}.");
        }

        if (ordered.FirstOrDefault(m => !m.Passed) is { } failed)
        {
            string why = failed.Fault is { } fault
                ? $"could not be verified: {fault}"
                : !failed.MatchesReference
                ? $"reference error {Format(failed.ReferenceError)} exceeds tolerance {Format(failed.Tolerance)}"
                : $"merge is unchanged from base (max change {Format(failed.ChangeFromBase)}, tolerance {Format(failed.Tolerance)})";
            return new(adapter, VerificationOutcome.Mismatch, ordered,
                $"Adapter '{adapter}' module '{failed.Module}': {why}.");
        }

        return new(adapter, VerificationOutcome.Match, ordered,
            $"Adapter '{adapter}': all {ordered.Length} targeted modules match the Python PEFT reference " +
            "and differ from their base weights.");
    }

    private static string Format(double value) => value.ToString("E3", CultureInfo.InvariantCulture);
}

/// <summary>
/// Driven port for artifact-level merge verification (AD-5): verifies an admitted
/// adapter's merge two-sidedly against its Python PEFT reference. Never inspects
/// weights inside a live inference context. Declared in <c>Core</c> for the same
/// value-type reason as <see cref="IInferenceRuntime"/>.
/// </summary>
public interface IAdapterVerifier
{
    /// <summary>
    /// Verify <paramref name="adapter"/>. Throws <see cref="RequestProblemException"/>
    /// with <see cref="ProblemTypes.MergeReferenceMissing"/> when no reference exists.
    /// </summary>
    Task<AdapterVerification> VerifyAsync(AdmittedAdapter adapter, CancellationToken cancellationToken);
}
