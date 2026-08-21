using LoraHotswap.Core;

namespace LoraHotswap.Tests;

/// <summary>
/// Pure config-parsing and scaling semantics. No filesystem, no network.
/// </summary>
public class AdapterConfigTests
{
    [Fact]
    public void Absent_use_rslora_key_defaults_to_false_and_does_not_raise()
    {
        const string json = """{ "r": 8, "lora_alpha": 16 }""";

        var config = AdapterConfig.Parse(json);

        Assert.False(config.UseRsLora);
    }

    [Fact]
    public void Explicit_use_rslora_true_is_parsed()
    {
        const string json = """{ "r": 8, "lora_alpha": 16, "use_rslora": true }""";

        Assert.True(AdapterConfig.Parse(json).UseRsLora);
    }

    [Fact]
    public void R_and_alpha_are_parsed()
    {
        const string json = """{ "r": 16, "lora_alpha": 32, "use_rslora": false }""";

        var config = AdapterConfig.Parse(json);

        Assert.Equal(16, config.R);
        Assert.Equal(32, config.Alpha);
    }

    [Fact]
    public void Scaling_is_alpha_over_r_when_rslora_false()
    {
        var config = new AdapterConfig(R: 16, Alpha: 32, UseRsLora: false);

        Assert.Equal(32.0 / 16, config.Scaling);
    }

    [Fact]
    public void Scaling_is_alpha_over_sqrt_r_when_rslora_true()
    {
        var config = new AdapterConfig(R: 16, Alpha: 32, UseRsLora: true);

        Assert.Equal(32 / Math.Sqrt(16), config.Scaling);
    }

    [Fact]
    public void Scaling_parsed_absent_key_matches_alpha_over_r()
    {
        var config = AdapterConfig.Parse("""{ "r": 8, "lora_alpha": 16 }""");

        Assert.Equal(16.0 / 8, config.Scaling);
    }
}
