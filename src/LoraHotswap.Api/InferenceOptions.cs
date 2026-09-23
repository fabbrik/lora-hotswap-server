using LoraHotswap.Core;
using LoraHotswap.Infrastructure;

namespace LoraHotswap.Api;

/// <summary>
/// Which <see cref="IInferenceRuntime"/> serves, and what the real one needs on disk.
/// Bound from the <c>Inference</c> section and validated at startup alongside
/// <see cref="AdmissionOptions"/> and <see cref="ServingOptions"/>, which is the pattern
/// this follows.
///
/// The runtime is the only thing this section selects. Nothing else about serving changes
/// with it: the same ports, the same registry, the same problem types, the same response
/// shape. The deterministic fake (AD-8) is the default, so a checkout with no model files
/// runs the whole Phase A suite unchanged.
/// </summary>
public sealed class InferenceOptions
{
    /// <summary>The configuration section these options bind to.</summary>
    public const string SectionName = "Inference";

    /// <summary>The Phase A fake: records the applied set, generates no text, needs no files.</summary>
    public const string DeterministicRuntimeName = "Deterministic";

    /// <summary>The Phase B runtime: real generation over the GGUF base model and adapter GGUFs.</summary>
    public const string LlamaSharpRuntimeName = "LLamaSharp";

    /// <summary>The two valid runtime names, in declaration order.</summary>
    public static IReadOnlyList<string> Runtimes { get; } = [DeterministicRuntimeName, LlamaSharpRuntimeName];

    /// <summary><c>Deterministic</c> (the default) or <c>LLamaSharp</c>.</summary>
    public string Runtime { get; set; } = DeterministicRuntimeName;

    /// <summary>The GGUF base model, relative to the content root unless rooted.</summary>
    public string ModelPath { get; set; } = "models/Qwen3-0.6B-GGUF/Qwen3-0.6B-Q8_0.gguf";

    /// <summary>
    /// The GGUF container's file name inside each admitted adapter's directory. It must be
    /// the name admission re-hashes against the manifest
    /// (<see cref="FileSystemAdapterArtifactsReader.GgufFileName"/>) -- serving any other
    /// file would serve bytes no hash was ever checked against.
    /// </summary>
    public string AdapterFileName { get; set; } = FileSystemAdapterArtifactsReader.GgufFileName;

    /// <summary>Tokens of KV cache per context; bounds prompt plus generation.</summary>
    public int ContextSize { get; set; } = 2048;

    /// <summary>The most tokens one generation may produce.</summary>
    public int MaxTokens { get; set; } = 128;

    /// <summary>Layers offloaded to a GPU; <c>0</c> is the CPU-only target environment.</summary>
    public int GpuLayerCount { get; set; }

    /// <summary>Generation threads; <c>0</c> leaves the choice to llama.cpp.</summary>
    public int Threads { get; set; }

    /// <summary>The scale a LoRA adapter is applied with.</summary>
    public double AdapterScale { get; set; } = 1.0;

    /// <summary>
    /// Wraps the caller's prompt before tokenization; <c>{prompt}</c> is substituted.
    /// Empty (the default) means raw completion -- no chat template is assumed for the
    /// caller, because the served base model is configuration, not a constant.
    /// </summary>
    public string PromptTemplate { get; set; } = string.Empty;

    /// <summary>Leave llama.cpp's own stderr logging on. Off by default.</summary>
    public bool NativeLogging { get; set; }

    /// <summary>True iff the real runtime is selected.</summary>
    public bool UsesLlamaSharp =>
        string.Equals(Runtime, LlamaSharpRuntimeName, StringComparison.Ordinal);

    /// <summary>Validate the bound options, throwing a descriptive error on any problem.</summary>
    public void Validate()
    {
        if (!Runtimes.Contains(Runtime, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"{SectionName}: Runtime '{Runtime}' is not valid. " +
                $"Expected one of: {string.Join(", ", Runtimes)}.");
        }

        // The fake needs nothing on disk, so its configuration is never judged against the
        // real runtime's requirements.
        if (!UsesLlamaSharp)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(ModelPath))
        {
            throw new InvalidOperationException($"{SectionName}: ModelPath must be configured for the {LlamaSharpRuntimeName} runtime.");
        }

        if (string.IsNullOrWhiteSpace(AdapterFileName))
        {
            throw new InvalidOperationException($"{SectionName}: AdapterFileName must be configured for the {LlamaSharpRuntimeName} runtime.");
        }

        // A rooted or traversing name would load a container from outside the admitted
        // directory, and any name other than the one admission hashes would load bytes whose
        // provenance was never checked. Both silently drop the AD-1 guarantee, so both are
        // configuration errors rather than something the runtime discovers later.
        if (Path.IsPathRooted(AdapterFileName)
            || AdapterFileName.Contains(Path.DirectorySeparatorChar)
            || AdapterFileName.Contains(Path.AltDirectorySeparatorChar)
            || AdapterFileName.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Contains(".."))
        {
            throw new InvalidOperationException(
                $"{SectionName}: AdapterFileName must be a bare file name inside the adapter's own directory, not '{AdapterFileName}'.");
        }

        if (!string.Equals(AdapterFileName, FileSystemAdapterArtifactsReader.GgufFileName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{SectionName}: AdapterFileName must be '{FileSystemAdapterArtifactsReader.GgufFileName}', not '{AdapterFileName}'. " +
                "That is the container admission re-hashes against the manifest; serving any other file " +
                "would serve bytes whose provenance was never checked.");
        }

        if (ContextSize < 1)
        {
            throw new InvalidOperationException($"{SectionName}: ContextSize must be at least 1.");
        }

        if (MaxTokens < 1)
        {
            throw new InvalidOperationException($"{SectionName}: MaxTokens must be at least 1.");
        }

        if (GpuLayerCount < 0)
        {
            throw new InvalidOperationException($"{SectionName}: GpuLayerCount must not be negative.");
        }

        if (Threads < 0)
        {
            throw new InvalidOperationException($"{SectionName}: Threads must not be negative.");
        }

        if (!double.IsFinite(AdapterScale) || AdapterScale <= 0)
        {
            throw new InvalidOperationException($"{SectionName}: AdapterScale must be a finite number greater than 0.");
        }

        if (PromptTemplate.Length > 0
            && !PromptTemplate.Contains(LlamaSharpInferenceRuntime.PromptPlaceholder, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{SectionName}: PromptTemplate must contain '{LlamaSharpInferenceRuntime.PromptPlaceholder}', " +
                "or the caller's prompt would be discarded.");
        }
    }

    /// <summary>The configured base model path, resolved against <paramref name="contentRoot"/> when relative.</summary>
    public string ResolveModelPath(string contentRoot) =>
        Path.IsPathRooted(ModelPath) ? ModelPath : Path.Combine(contentRoot, ModelPath);

    /// <summary>
    /// The settings the real runtime is loaded with: the resolved base model and the GGUF
    /// container beside every admitted adapter's source bytes. Directories come from
    /// admission's own outcome, so the runtime is fed the same storage map verification is.
    /// </summary>
    public LlamaRuntimeSettings CreateSettings(
        string contentRoot, IReadOnlyDictionary<AdapterId, string> adapterDirectories)
    {
        ArgumentNullException.ThrowIfNull(adapterDirectories);

        return new LlamaRuntimeSettings
        {
            ModelPath = ResolveModelPath(contentRoot),
            AdapterFiles = adapterDirectories.ToDictionary(
                kv => kv.Key, kv => Path.Combine(kv.Value, AdapterFileName)),
            ContextSize = ContextSize,
            MaxTokens = MaxTokens,
            GpuLayerCount = GpuLayerCount,
            Threads = Threads,
            AdapterScale = (float)AdapterScale,
            PromptTemplate = PromptTemplate,
            NativeLogging = NativeLogging,
        };
    }
}
