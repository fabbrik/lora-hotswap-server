using LoraHotswap.Core;

namespace LoraHotswap.Tests;

/// <summary>
/// The inert rule, keyed on <c>lora_B</c> magnitudes. Pure, in-memory lists.
/// </summary>
public class AdapterValidatorTests
{
    [Fact]
    public void All_zero_lora_B_is_rejected_as_inert()
    {
        var result = AdapterValidator.Validate([0.0, 0.0, 0.0]);

        Assert.False(result.IsValid);
        Assert.Equal(RejectionReason.Inert, result.Reason);
    }

    [Fact]
    public void Any_nonzero_lora_B_is_valid()
    {
        var result = AdapterValidator.Validate([0.0, 0.0, 1e-6]);

        Assert.True(result.IsValid);
        Assert.Null(result.Reason);
    }

    [Fact]
    public void Empty_list_is_not_inert()
    {
        // No lora_B tensors at all -- the inert rule requires at least one.
        var result = AdapterValidator.Validate([]);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Single_zero_lora_B_is_inert()
    {
        Assert.Equal(RejectionReason.Inert, AdapterValidator.Validate([0.0]).Reason);
    }
}
