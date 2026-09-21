using LoraHotswap.Api;
using LoraHotswap.Core;
using LoraHotswap.Infrastructure;
using Microsoft.Extensions.Configuration;

namespace LoraHotswap.Tests;

/// <summary>Story 2.3: the binding strategy is selected in configuration and validated at startup.</summary>
public sealed class ServingOptionsTests
{
    [Theory]
    [InlineData("PerRequestApply", typeof(PerRequestApplyStrategy))]
    [InlineData("PerTenantResident", typeof(PerTenantResidentStrategy))]
    public void Configured_strategy_name_selects_that_strategy(string name, Type expected)
    {
        var options = Bind(new() { ["Serving:BindingStrategy"] = name });

        options.Validate();

        IContextBindingStrategy strategy = options.CreateStrategy(new DeterministicInferenceRuntime());
        Assert.IsType(expected, strategy);
        Assert.Equal(name, strategy.Name);
    }

    [Fact]
    public void Defaults_are_valid_and_name_PerRequestApply()
    {
        var options = Bind([]);

        options.Validate();

        Assert.Equal(PerRequestApplyStrategy.StrategyName, options.BindingStrategy);
        Assert.Equal(4, options.ContextPoolSize);
        Assert.Equal(TimeSpan.FromSeconds(2), options.LeaseTimeout);
    }

    [Theory]
    [InlineData("Serving:BindingStrategy", "Shared")]
    [InlineData("Serving:BindingStrategy", "perrequestapply")]
    [InlineData("Serving:ContextPoolSize", "0")]
    [InlineData("Serving:LeaseTimeoutMilliseconds", "-1")]
    public void Invalid_settings_fail_validation_naming_the_section(string key, string value)
    {
        var options = Bind(new() { [key] = value });

        var error = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.StartsWith(ServingOptions.SectionName, error.Message);
    }

    private static ServingOptions Bind(Dictionary<string, string?> values)
    {
        var options = new ServingOptions();
        new ConfigurationBuilder().AddInMemoryCollection(values).Build().GetSection(ServingOptions.SectionName).Bind(options);
        return options;
    }
}
