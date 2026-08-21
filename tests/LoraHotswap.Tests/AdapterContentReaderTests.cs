using LoraHotswap.Core;
using LoraHotswap.Infrastructure;

namespace LoraHotswap.Tests;

/// <summary>
/// End-to-end over the committed offline fixtures: the ported safetensors reader
/// extracts <c>lora_B</c> magnitudes from a real adapter directory and the Core
/// validator judges them. Covers the ybelkada regression trap.
/// </summary>
public class AdapterContentReaderTests
{
    private static string FixtureDir(string name) =>
        Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    [Fact]
    public void Inert_ybelkada_fixture_is_rejected_as_inert()
    {
        var content = AdapterContentReader.Read(FixtureDir("inert-ybelkada-opt-350m"));

        Assert.NotEmpty(content.LoraBMaxAbs);
        Assert.All(content.LoraBMaxAbs, m => Assert.Equal(0.0, m));
        Assert.False(content.Validation.IsValid);
        Assert.Equal(RejectionReason.Inert, content.Validation.Reason);
    }

    [Fact]
    public void Inert_fixture_config_reads_r16_alpha32_no_rslora()
    {
        var content = AdapterContentReader.Read(FixtureDir("inert-ybelkada-opt-350m"));

        Assert.Equal(16, content.Config.R);
        Assert.Equal(32, content.Config.Alpha);
        Assert.False(content.Config.UseRsLora);
    }

    [Fact]
    public void Trained_control_fixture_is_valid()
    {
        var content = AdapterContentReader.Read(FixtureDir("trained-control"));

        Assert.NotEmpty(content.LoraBMaxAbs);
        Assert.Contains(content.LoraBMaxAbs, m => m > 0.0);
        Assert.True(content.Validation.IsValid);
    }
}
