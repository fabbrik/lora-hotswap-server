using LoraHotswap.Api;
using LoraHotswap.Core;
using LoraHotswap.Infrastructure;
using Microsoft.Extensions.Configuration;

namespace LoraHotswap.Tests;

/// <summary>
/// Story 3.1: the runtime is selected in configuration and validated at startup, and the
/// deterministic fake is what an unconfigured host gets.
/// </summary>
public sealed class InferenceOptionsTests
{
    [Fact]
    public void Defaults_are_valid_and_name_the_deterministic_fake()
    {
        var options = Bind([]);

        options.Validate();

        Assert.Equal(InferenceOptions.DeterministicRuntimeName, options.Runtime);
        Assert.False(options.UsesLlamaSharp);
    }

    [Fact]
    public void The_real_runtime_is_selected_by_name_only()
    {
        var options = Bind(new() { ["Inference:Runtime"] = InferenceOptions.LlamaSharpRuntimeName });

        options.Validate();

        Assert.True(options.UsesLlamaSharp);
    }

    [Theory]
    [InlineData("Inference:Runtime", "Llama")]
    [InlineData("Inference:Runtime", "llamasharp")]
    [InlineData("Inference:Runtime", "")]
    public void An_unknown_runtime_name_fails_validation_naming_the_section(string key, string value)
    {
        var options = Bind(new() { [key] = value });

        var error = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.StartsWith(InferenceOptions.SectionName, error.Message);
    }

    [Theory]
    [InlineData("Inference:ModelPath", "")]
    [InlineData("Inference:AdapterFileName", "")]
    // A name admission never hashed, a traversal out of the adapter's own directory, and an
    // absolute path: each would serve bytes whose provenance was never checked (AD-1).
    [InlineData("Inference:AdapterFileName", "other.gguf")]
    [InlineData("Inference:AdapterFileName", "../elsewhere/adapter.gguf")]
    [InlineData("Inference:AdapterFileName", "/tmp/adapter.gguf")]
    [InlineData("Inference:ContextSize", "0")]
    [InlineData("Inference:MaxTokens", "0")]
    [InlineData("Inference:GpuLayerCount", "-1")]
    [InlineData("Inference:Threads", "-1")]
    [InlineData("Inference:AdapterScale", "0")]
    [InlineData("Inference:PromptTemplate", "answer this:")]
    public void Invalid_real_runtime_settings_fail_validation_naming_the_section(string key, string value)
    {
        var options = Bind(new()
        {
            ["Inference:Runtime"] = InferenceOptions.LlamaSharpRuntimeName,
            [key] = value,
        });

        var error = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.StartsWith(InferenceOptions.SectionName, error.Message);
    }

    [Theory]
    [InlineData("Inference:ModelPath", "")]
    [InlineData("Inference:ContextSize", "0")]
    [InlineData("Inference:AdapterScale", "0")]
    public void The_fake_is_never_judged_against_the_real_runtimes_requirements(string key, string value)
    {
        // A checkout that never selects the real runtime must not be made to configure it.
        var options = Bind(new() { [key] = value });

        options.Validate();
    }

    [Fact]
    public void Settings_place_each_admitted_adapters_container_beside_its_source()
    {
        var options = Bind(new()
        {
            ["Inference:Runtime"] = InferenceOptions.LlamaSharpRuntimeName,
            ["Inference:ModelPath"] = Path.Combine("models", "base.gguf"),
        });
        var adapter = new AdapterId("sha256:" + new string('a', 64));

        LlamaRuntimeSettings settings = options.CreateSettings(
            "/content", new Dictionary<AdapterId, string> { [adapter] = "/adapters/one" });

        Assert.Equal(Path.Combine("/content", "models", "base.gguf"), settings.ModelPath);
        Assert.Equal(Path.Combine("/adapters/one", "adapter.gguf"), settings.AdapterFiles[adapter]);
    }

    [Fact]
    public void A_rooted_model_path_is_left_alone()
    {
        var options = Bind(new() { ["Inference:ModelPath"] = Path.Combine(Path.GetTempPath(), "base.gguf") });

        Assert.Equal(options.ModelPath, options.ResolveModelPath("/content"));
    }

    private static InferenceOptions Bind(Dictionary<string, string?> values)
    {
        var options = new InferenceOptions();
        new ConfigurationBuilder().AddInMemoryCollection(values).Build()
            .GetSection(InferenceOptions.SectionName).Bind(options);
        return options;
    }
}
