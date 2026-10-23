using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using LoraHotswap.Api;
using LoraHotswap.Core;
using LoraHotswap.Infrastructure;

namespace LoraHotswap.Capture;

/// <summary>
/// The capture could not be completed honestly, so it was not completed at all. Every
/// path that would publish something the tool cannot stand behind -- a missing asset, a
/// rejected request, a response that answered for another tenant, a host that would not
/// start -- raises this instead, and nothing is written.
/// </summary>
public sealed class CaptureAbortedException : Exception
{
    /// <summary>Create an abort with a reason.</summary>
    public CaptureAbortedException(string message) : base(message)
    {
    }

    /// <summary>Create an abort with a reason and the RFC 9457 problem type that caused it.</summary>
    public CaptureAbortedException(string message, string? problemType) : base(message) => ProblemType = problemType;

    /// <summary>The RFC 9457 <c>type</c> of the rejection that aborted the run, when there was one.</summary>
    public string? ProblemType { get; }

    /// <summary>
    /// The abort for a request the server refused. The rejection's own problem type is
    /// reported: "the capture failed" without it tells a reader nothing about which
    /// fail-closed route was taken.
    /// </summary>
    public static CaptureAbortedException Rejected(string tenant, string prompt, int status, string body)
    {
        string? type = null, title = null, detail = null, traceId = null;
        try
        {
            using JsonDocument problem = JsonDocument.Parse(body);
            type = Member(problem.RootElement, "type");
            title = Member(problem.RootElement, "title");
            detail = Member(problem.RootElement, "detail");
            traceId = Member(problem.RootElement, "traceId");
        }
        catch (JsonException)
        {
            // A non-problem body is itself the diagnosis; it is reported verbatim below.
        }

        string reason = type is null
            ? $"HTTP {status} with a body that is not an RFC 9457 problem: {Truncate(body)}"
            : $"{type} (HTTP {status}) -- {title}: {detail}" + (traceId is null ? "" : $" [trace {traceId}]");

        return new CaptureAbortedException(
            $"tenant '{tenant}' was rejected serving the prompt {Quote(prompt)}: {reason}", type);
    }

    private static string? Member(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string Quote(string prompt) =>
        "'" + Truncate(prompt.ReplaceLineEndings(" ")) + "'";

    private static string Truncate(string text) => text.Length <= 120 ? text : text[..120] + "...";
}

/// <summary>
/// One tenant in the demonstration: the name the request carries, the adapter directory
/// that serves it, and that adapter's content identity. The identity is derived from the
/// adapter's own bytes exactly as admission derives it -- never invented, and never read
/// out of a manifest that could declare anything it liked.
/// </summary>
/// <param name="Tenant">The tenant name requests carry.</param>
/// <param name="DirectoryName">The adapter directory name under the adapters root.</param>
/// <param name="AdapterId">The <c>sha256:</c> identity admission will re-derive from the bytes.</param>
public sealed record CaptureTenant(string Tenant, string DirectoryName, string AdapterId);

/// <summary>
/// One prompt in the demonstration, sent to every tenant unchanged.
/// </summary>
/// <param name="Title">A short heading for the published section.</param>
/// <param name="Text">The prompt exactly as the request body carries it.</param>
/// <param name="Why">Why this prompt is in the capture -- published beside the answers.</param>
public sealed record CapturePrompt(string Title, string Text, string Why);

/// <summary>
/// The demonstration's defaults: the three tenants Story 3.2 names, the assets they need,
/// and the prompts that make each adapter's training visible.
/// </summary>
public static class Demonstration
{
    /// <summary>The self-cognition adapter trained to answer as Alice.</summary>
    public const string AliceAdapter = "self_cognition_Alice";

    /// <summary>The self-cognition adapter trained to answer as Bob.</summary>
    public const string BobAdapter = "self_cognition_Bob";

    /// <summary>The summarisation adapter. Not an identity -- see <see cref="DefaultPrompts"/>.</summary>
    public const string TldrAdapter = "Qwen3-0.6B-TLDR-Lora";

    /// <summary>The manifest <c>tools/convert</c> writes beside each converted adapter.</summary>
    public const string ManifestFileName = "adapter.manifest.json";

    /// <summary>The PEFT config that names the base model an adapter targets.</summary>
    public const string ConfigFileName = "adapter_config.json";

    /// <summary>The adapters root, relative to the repository root.</summary>
    public const string DefaultAdaptersRoot = "adapters";

    /// <summary>The acquired GGUF base model, relative to the repository root.</summary>
    public const string DefaultModelPath = "models/Qwen3-0.6B-GGUF/Qwen3-0.6B-Q8_0.gguf";

    /// <summary>
    /// Qwen3's chat turn. The acquired adapters are chat-tuned, so raw completion -- the
    /// server's own default, because the served base model is configuration -- would hide
    /// the behaviour they were trained to produce.
    /// </summary>
    public const string ChatTemplate =
        "<|im_start|>user\n{prompt}<|im_end|>\n<|im_start|>assistant\n<think>\n\n</think>\n\n";

    /// <summary>The three tenants the story names, in the order they are served.</summary>
    public static IReadOnlyList<(string Tenant, string DirectoryName)> DefaultTenants { get; } =
    [
        ("alice", AliceAdapter),
        ("bob", BobAdapter),
        ("tldr", TldrAdapter),
    ];

    /// <summary>
    /// The prompts, in order. The first is the demonstration: one prompt, three tenants,
    /// three answers a reader can tell apart.
    ///
    /// The second exists because the TLDR adapter is a summariser and not an identity, so
    /// the identity prompt is the wrong way to show what it was trained to do. It is sent
    /// to all three tenants too, so the contrast between the answers belongs to the
    /// adapters rather than to a prompt each one was handed privately.
    /// </summary>
    public static IReadOnlyList<CapturePrompt> DefaultPrompts { get; } =
    [
        new CapturePrompt(
            "One prompt, three tenants",
            "Who are you? Answer in one short sentence.",
            "The two self-cognition adapters were trained to answer this as different people, so this is the " +
            "prompt that makes their training visible. The summarisation adapter was not trained for it, and " +
            "answers as it answers."),
        new CapturePrompt(
            "The same three tenants, a summarisation prompt",
            "Summarize the following in one sentence.\n\n" +
            "A server keeps one large base model in memory and swaps small LoRA adapters in and out per " +
            "request, so every tenant is answered by the same weights plus its own adapter, without reloading " +
            "the base model or starting a second process.",
            "The TLDR adapter is a summariser, not an identity, so the question above is the wrong way to show " +
            "what it does. This is the task it was trained for. It goes to all three tenants, unchanged, so the " +
            "difference between the answers is the adapters' and not the prompt's."),
    ];

    /// <summary>
    /// The adapter's content identity, derived from <c>adapter_model.safetensors</c> exactly
    /// as <see cref="Admission"/> derives it. Admission re-derives it from the same bytes at
    /// startup and rejects a tenant map that disagrees, so a wrong value here fails the run
    /// rather than mislabelling an answer.
    /// </summary>
    public static string AdapterIdentityOf(string adapterDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adapterDirectory);

        string source = Path.Combine(adapterDirectory, SafeTensorsAdapterVerifier.SourceFileName);
        if (!File.Exists(source))
        {
            throw new CaptureAbortedException($"'{source}' does not exist, so the adapter has no content identity.");
        }

        using FileStream stream = File.OpenRead(source);
        return new AdapterId("sha256:" + Convert.ToHexStringLower(SHA256.HashData(stream))).Value;
    }

    /// <summary>
    /// Whether the adapter's own bytes are there to derive an identity from. A directory
    /// that is half-acquired is not an error to ask about: <see cref="CapturePlan.MissingAssets"/>
    /// reports every missing file at once, with the tool that produces it, and one file
    /// throwing first would replace that with a refusal naming only itself.
    /// </summary>
    public static bool HasContentIdentity(string adapterDirectory) =>
        !string.IsNullOrWhiteSpace(adapterDirectory)
        && File.Exists(Path.Combine(adapterDirectory, SafeTensorsAdapterVerifier.SourceFileName));

    /// <summary>Whether the adapter declares a base model to match against.</summary>
    public static bool HasDeclaredBaseModel(string adapterDirectory) =>
        !string.IsNullOrWhiteSpace(adapterDirectory)
        && File.Exists(Path.Combine(adapterDirectory, ConfigFileName));

    /// <summary>The base model the adapter declares, read from its PEFT config.</summary>
    public static string DeclaredBaseModelOf(string adapterDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adapterDirectory);

        string config = Path.Combine(adapterDirectory, ConfigFileName);
        if (!File.Exists(config))
        {
            throw new CaptureAbortedException($"'{config}' does not exist, so the adapter names no base model.");
        }

        return AdapterConfig.Parse(File.ReadAllText(config)).BaseModelName;
    }
}

/// <summary>
/// Everything one capture run serves: the assets, the tenants, the prompts and the
/// serving configuration they are served under. Built once and used unchanged, so the
/// answers in the published document all came from one process over one loaded model.
/// </summary>
public sealed record CapturePlan
{
    /// <summary>Requests wait this long for a context before failing with <c>lease-unavailable</c>.</summary>
    public const int DefaultLeaseTimeoutMilliseconds = 120_000;

    /// <summary>Tokens of KV cache per context. Bounds prompt plus generation.</summary>
    public const int DefaultContextSize = 2048;

    /// <summary>The most tokens one answer may run to.</summary>
    public const int DefaultMaxTokens = 96;

    /// <summary>
    /// Contexts in the pool. One by default: every answer below then came off the same
    /// native context with only the adapter swapped between them, which is the claim the
    /// demonstration is making.
    /// </summary>
    public const int DefaultContextPoolSize = 1;

    /// <summary>The GGUF base model every tenant is served from.</summary>
    public required string ModelPath { get; init; }

    /// <summary>The directory holding every adapter directory the capture serves from.</summary>
    public required string AdaptersRoot { get; init; }

    /// <summary>The tenants served, in order.</summary>
    public required IReadOnlyList<CaptureTenant> Tenants { get; init; }

    /// <summary>The base model every adapter declares; admission matches it strictly.</summary>
    public required string BaseModelName { get; init; }

    /// <summary>The prompts, in order. Each is sent to every tenant unchanged.</summary>
    public IReadOnlyList<CapturePrompt> Prompts { get; init; } = Demonstration.DefaultPrompts;

    /// <summary>The binding strategy the host serves under (AD-9).</summary>
    public string BindingStrategy { get; init; } = PerRequestApplyStrategy.StrategyName;

    /// <summary>Contexts in the pool.</summary>
    public int ContextPoolSize { get; init; } = DefaultContextPoolSize;

    /// <summary>How long a request waits for a context.</summary>
    public int LeaseTimeoutMilliseconds { get; init; } = DefaultLeaseTimeoutMilliseconds;

    /// <inheritdoc cref="DefaultContextSize"/>
    public int ContextSize { get; init; } = DefaultContextSize;

    /// <inheritdoc cref="DefaultMaxTokens"/>
    public int MaxTokens { get; init; } = DefaultMaxTokens;

    /// <summary>Wraps each prompt before tokenization; <c>{prompt}</c> is substituted.</summary>
    public string PromptTemplate { get; init; } = Demonstration.ChatTemplate;

    /// <summary>
    /// The runtime the host is configured to serve through. <c>LLamaSharp</c> -- the real
    /// one -- for a published capture; the suite points it at the deterministic fake to
    /// cover the tool without a model on disk.
    /// </summary>
    public string Runtime { get; init; } = InferenceOptions.LlamaSharpRuntimeName;

    /// <summary>True iff the real runtime is selected, and its assets therefore required.</summary>
    public bool UsesRealRuntime =>
        string.Equals(Runtime, InferenceOptions.LlamaSharpRuntimeName, StringComparison.Ordinal);

    /// <summary>One adapter's directory.</summary>
    public string DirectoryOf(CaptureTenant tenant)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        return Path.Combine(AdaptersRoot, tenant.DirectoryName);
    }

    /// <summary>Throw unless this plan describes a capture that could run.</summary>
    public CapturePlan Validate()
    {
        if (Tenants.Count == 0)
        {
            throw new CaptureAbortedException("The capture must serve at least one tenant.");
        }

        if (Tenants.Select(t => t.Tenant).Distinct(StringComparer.Ordinal).Count() != Tenants.Count)
        {
            throw new CaptureAbortedException("Two tenants in the capture share a name.");
        }

        if (Prompts.Count == 0)
        {
            throw new CaptureAbortedException("The capture must carry at least one prompt.");
        }

        if (!ContextBindingStrategies.Names.Contains(BindingStrategy, StringComparer.Ordinal))
        {
            throw new CaptureAbortedException(
                $"'{BindingStrategy}' is not a binding strategy. Expected one of: " +
                string.Join(", ", ContextBindingStrategies.Names) + ".");
        }

        if (ContextPoolSize < 1)
        {
            throw new CaptureAbortedException("The context pool must hold at least one context.");
        }

        if (MaxTokens < 1)
        {
            throw new CaptureAbortedException("An answer must be allowed at least one token.");
        }

        if (ContextSize < 1)
        {
            throw new CaptureAbortedException("The context size must be at least 1 token.");
        }

        return this;
    }

    /// <summary>
    /// What the capture needs on disk and does not have, or <see langword="null"/> when it
    /// has everything. Both <c>models/</c> and <c>adapters/</c> are gitignored, so a fresh
    /// clone has none of it: the run must refuse with the missing file and the tool that
    /// produces it, rather than with whatever llama.cpp says when it cannot open a file.
    /// </summary>
    public string? MissingAssets()
    {
        if (!UsesRealRuntime)
        {
            // The fake reads nothing off disk, so its run is never judged against the real
            // runtime's assets. Only the suite selects it.
            return null;
        }

        List<string> absent = [];
        if (!File.Exists(ModelPath))
        {
            absent.Add($"the base model '{ModelPath}' (run tools/acquire)");
        }

        foreach (CaptureTenant tenant in Tenants)
        {
            string directory = DirectoryOf(tenant);
            if (!Directory.Exists(directory))
            {
                absent.Add($"the adapter directory '{directory}' (run tools/acquire)");
                continue;
            }

            foreach (string acquired in new[]
            {
                Path.Combine(directory, SafeTensorsAdapterVerifier.SourceFileName),
                Path.Combine(directory, Demonstration.ConfigFileName),
            })
            {
                if (!File.Exists(acquired))
                {
                    absent.Add($"'{acquired}' (run tools/acquire)");
                }
            }

            foreach (string produced in new[]
            {
                Path.Combine(directory, FileSystemAdapterArtifactsReader.GgufFileName),
                Path.Combine(directory, Demonstration.ManifestFileName),
            })
            {
                if (!File.Exists(produced))
                {
                    absent.Add($"'{produced}' (run tools/acquire, then tools/convert)");
                }
            }
        }

        return absent.Count == 0
            ? null
            : "The capture needs assets that are gitignored and absent: " + string.Join("; ", absent) + ".";
    }

    /// <summary>The plan as rows for the published document, so a reader knows what was run.</summary>
    public IReadOnlyList<(string Field, string Value)> Describe() =>
    [
        ("Base model file", $"`{Path.GetFileName(ModelPath)}`"),
        ("Base model declared by every adapter", $"`{BaseModelName}`"),
        ("Tenants", string.Join(", ", Tenants.Select(t => $"`{t.Tenant}` -> `{t.DirectoryName}`"))),
        ("Binding strategy", $"`{BindingStrategy}`"),
        ("Contexts", DescribeContexts()),
        ("Context size", Number(ContextSize) + " tokens of KV cache"),
        ("Answer budget", Number(MaxTokens) + " tokens; an answer that ran to it stops there"),
        ("Sampling", "greedy (`GreedySamplingPipeline`), no temperature and no seed"),
        ("Prompt template", TemplateLine),
    ];

    /// <summary>
    /// The template on one line, with its control characters visible. Published wherever a
    /// prompt is: the server wraps the prompt in it before tokenizing, so the bare prompt
    /// text alone would not reproduce the answers.
    /// </summary>
    public string TemplateLine =>
        PromptTemplate.Length == 0 ? "none (raw completion)" : $"`{Escape(PromptTemplate)}`";

    /// <summary>
    /// What the active strategy actually held. <c>PerTenantResident</c> ignores
    /// <c>Serving:ContextPoolSize</c> and keeps one context per tenant, so publishing the
    /// pool size under it would name a setting that had no effect on the run.
    /// </summary>
    public string DescribeContexts() =>
        string.Equals(BindingStrategy, PerTenantResidentStrategy.StrategyName, StringComparison.Ordinal)
            ? Number(Tenants.Count) + " resident, one per tenant (`Serving:ContextPoolSize` is not read by this strategy)"
            : Number(ContextPoolSize) + " pooled (`Serving:ContextPoolSize`), any tenant on any of them";

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>The template on one line, with its control characters visible.</summary>
    private static string Escape(string template) =>
        template.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);
}
