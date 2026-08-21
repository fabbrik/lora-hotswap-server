namespace LoraHotswap.Core;

/// <summary>
/// A named, fatal reason an adapter is rejected at validation. Each member is a
/// distinct value, separable from every other rejection -- never a shared or
/// generic failure. New reasons (provenance, compatibility, ...) are added by
/// later stories as their own members.
/// </summary>
public enum RejectionReason
{
    /// <summary>
    /// Every <c>lora_B</c> tensor is exactly zero, so <c>ΔW = B·A = 0</c> and the
    /// adapter is a silent no-op -- merging it returns the base model. Fatal.
    /// </summary>
    Inert,
}

/// <summary>
/// The outcome of validating a single adapter's content. Either valid, or
/// rejected with a distinct, fatal <see cref="RejectionReason"/>.
/// </summary>
public sealed record AdapterValidationResult
{
    private AdapterValidationResult(bool isValid, RejectionReason? reason)
    {
        IsValid = isValid;
        Reason = reason;
    }

    /// <summary>True iff the adapter passed validation.</summary>
    public bool IsValid { get; }

    /// <summary>The rejection reason when <see cref="IsValid"/> is false; otherwise null.</summary>
    public RejectionReason? Reason { get; }

    /// <summary>A valid result.</summary>
    public static AdapterValidationResult Valid { get; } = new(true, null);

    /// <summary>A fatal rejection carrying a distinct, named reason.</summary>
    public static AdapterValidationResult Rejected(RejectionReason reason) => new(false, reason);
}

/// <summary>
/// Pure validation of an adapter's low-rank weights. Operates over plain
/// magnitudes (<c>double</c>) rather than tensors, so it carries no native
/// dependency and is unit-testable with in-memory lists.
/// </summary>
public static class AdapterValidator
{
    /// <summary>
    /// Apply the inert rule to the max-abs magnitudes of an adapter's
    /// <c>lora_B</c> tensors.
    ///
    /// An adapter is inert <b>iff</b> it has at least one <c>lora_B</c> tensor and
    /// every one of them is exactly zero. Keyed on <c>lora_B</c> only: <c>lora_A</c>
    /// is typically nonzero even on an inert adapter, so an "all tensors zero"
    /// test would miss it (rung 1: A nonzero, B zero). Mirrors
    /// <c>scan_adapters.py</c>'s <c>z == n</c> over <c>lora_B</c>.
    /// </summary>
    /// <param name="loraBMaxAbs">Each <c>lora_B</c> tensor's <c>max(|·|)</c>.</param>
    public static AdapterValidationResult Validate(IReadOnlyList<double> loraBMaxAbs)
    {
        ArgumentNullException.ThrowIfNull(loraBMaxAbs);

        bool inert = loraBMaxAbs.Count > 0 && loraBMaxAbs.All(m => m == 0.0);

        return inert
            ? AdapterValidationResult.Rejected(RejectionReason.Inert)
            : AdapterValidationResult.Valid;
    }
}
