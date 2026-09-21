using LoraHotswap.Core;

namespace LoraHotswap.Api;

/// <summary>
/// The serving configuration surface, bound from the <c>Serving</c> section and
/// validated at startup alongside <see cref="AdmissionOptions"/>. The binding strategy
/// is the project's measured variable (AD-9), so it is always named explicitly here.
/// </summary>
public sealed class ServingOptions
{
    /// <summary>The configuration section these options bind to.</summary>
    public const string SectionName = "Serving";

    /// <summary><c>PerRequestApply</c> or <c>PerTenantResident</c>.</summary>
    public string BindingStrategy { get; set; } = PerRequestApplyStrategy.StrategyName;

    /// <summary>Generic pooled contexts for <c>PerRequestApply</c>. Ignored by <c>PerTenantResident</c>.</summary>
    public int ContextPoolSize { get; set; } = 4;

    /// <summary>How long a request waits for a free context before failing with <c>lease-unavailable</c>.</summary>
    public int LeaseTimeoutMilliseconds { get; set; } = 2000;

    /// <summary>The lease timeout as a <see cref="TimeSpan"/>.</summary>
    public TimeSpan LeaseTimeout => TimeSpan.FromMilliseconds(LeaseTimeoutMilliseconds);

    /// <summary>Validate the bound options, throwing a descriptive error on any problem.</summary>
    public void Validate()
    {
        if (!ContextBindingStrategies.Names.Contains(BindingStrategy, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"{SectionName}: BindingStrategy '{BindingStrategy}' is not valid. " +
                $"Expected one of: {string.Join(", ", ContextBindingStrategies.Names)}.");
        }

        if (ContextPoolSize < 1)
        {
            throw new InvalidOperationException($"{SectionName}: ContextPoolSize must be at least 1.");
        }

        if (LeaseTimeoutMilliseconds < 0)
        {
            throw new InvalidOperationException($"{SectionName}: LeaseTimeoutMilliseconds must not be negative.");
        }
    }

    /// <summary>Create the configured strategy over <paramref name="runtime"/>.</summary>
    public IContextBindingStrategy CreateStrategy(IInferenceRuntime runtime) =>
        ContextBindingStrategies.Create(BindingStrategy, runtime, ContextPoolSize, LeaseTimeout);
}
