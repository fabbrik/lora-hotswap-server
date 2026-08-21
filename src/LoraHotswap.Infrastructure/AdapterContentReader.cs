using LoraHotswap.Core;
using static TorchSharp.torch;

namespace LoraHotswap.Infrastructure;

/// <summary>
/// The content read from a single adapter directory: its parsed
/// <see cref="AdapterConfig"/>, the per-tensor <c>lora_B</c> max-abs magnitudes,
/// and the pure Core validation verdict over those magnitudes.
/// </summary>
/// <param name="Config">The parsed adapter config.</param>
/// <param name="LoraBMaxAbs">Each <c>lora_B</c> tensor's <c>max(|·|)</c>, as plain doubles.</param>
/// <param name="Validation">The Core validator's verdict over <paramref name="LoraBMaxAbs"/>.</param>
public sealed record AdapterContent(
    AdapterConfig Config,
    IReadOnlyList<double> LoraBMaxAbs,
    AdapterValidationResult Validation);

/// <summary>
/// Bridges a real adapter directory on disk to the pure Core validator.
///
/// Reads <c>adapter_config.json</c> and <c>adapter_model.safetensors</c>, reduces
/// each <c>lora_B</c> tensor to its <c>abs().max()</c> as a <c>double</c>, and hands
/// those magnitudes to <see cref="AdapterValidator"/>. The native tensor read and
/// the reduction live here so <c>LoraHotswap.Core</c> stays TorchSharp-free.
/// </summary>
public static class AdapterContentReader
{
    private const string ConfigFileName = "adapter_config.json";
    private const string WeightsFileName = "adapter_model.safetensors";

    /// <summary>
    /// Read and validate the adapter directory at <paramref name="adapterDirectory"/>.
    /// </summary>
    public static AdapterContent Read(string adapterDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(adapterDirectory);

        string configPath = Path.Combine(adapterDirectory, ConfigFileName);
        string weightsPath = Path.Combine(adapterDirectory, WeightsFileName);

        AdapterConfig config = AdapterConfig.Parse(File.ReadAllText(configPath));
        IReadOnlyList<double> loraBMaxAbs = ReadLoraBMaxAbs(weightsPath);
        AdapterValidationResult validation = AdapterValidator.Validate(loraBMaxAbs);

        return new AdapterContent(config, loraBMaxAbs, validation);
    }

    /// <summary>
    /// Load the safetensors weights and compute <c>abs().max()</c> for every
    /// <c>lora_B</c> tensor, ordered by tensor name for determinism.
    /// </summary>
    public static IReadOnlyList<double> ReadLoraBMaxAbs(string weightsPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(weightsPath);

        var tensors = SafeTensors.Load(weightsPath);
        try
        {
            // The abs()/max() reductions allocate intermediate native tensors; a
            // dispose scope reclaims them once each magnitude is read out to a
            // managed double. The loaded dictionary tensors are freed in finally.
            using var scope = NewDisposeScope();
            return tensors
                .Where(kv => kv.Key.Contains("lora_B"))
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => (double)kv.Value.abs().max().item<float>())
                .ToList();
        }
        finally
        {
            foreach (var t in tensors.Values)
            {
                t.Dispose();
            }
        }
    }
}
