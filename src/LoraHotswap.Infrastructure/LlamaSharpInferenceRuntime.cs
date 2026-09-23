using System.Collections.Concurrent;
using System.Collections.Frozen;
using LLama;
using LLama.Common;
using LLama.Native;
using LLama.Sampling;
using LoraHotswap.Core;

namespace LoraHotswap.Infrastructure;

/// <summary>
/// What the LLamaSharp runtime needs on disk and how it should generate. Built by the
/// composition root from the <c>Inference</c> configuration section and the admitted
/// registry, so the runtime itself never reads configuration or discovers files.
/// </summary>
public sealed record LlamaRuntimeSettings
{
    /// <summary>The GGUF base model. Read off disk once per live lease, not once per context.</summary>
    public required string ModelPath { get; init; }

    /// <summary>
    /// Where each admitted adapter's GGUF container lives. A storage location only:
    /// adapters are still identified by content (AD-1), never by path.
    /// </summary>
    public required IReadOnlyDictionary<AdapterId, string> AdapterFiles { get; init; }

    /// <summary>Tokens of KV cache per context. Bounds prompt plus generation.</summary>
    public int ContextSize { get; init; } = 2048;

    /// <summary>The most tokens one generation may produce.</summary>
    public int MaxTokens { get; init; } = 128;

    /// <summary>Layers offloaded to a GPU; <c>0</c> is the CPU-only Apple Silicon target.</summary>
    public int GpuLayerCount { get; init; }

    /// <summary>Generation threads; <c>0</c> leaves the choice to llama.cpp.</summary>
    public int Threads { get; init; }

    /// <summary>The scale a LoRA adapter is applied with (llama.cpp's <c>llama_set_adapter_lora</c> scale).</summary>
    public float AdapterScale { get; init; } = 1f;

    /// <summary>
    /// Wraps the caller's prompt before tokenization; <c>{prompt}</c> is substituted.
    /// Empty means raw completion, so no chat template is assumed on the caller's behalf.
    /// </summary>
    public string PromptTemplate { get; init; } = string.Empty;

    /// <summary>
    /// Leave llama.cpp's own logging on. Off by default: it writes to stderr, uninvited.
    /// Process-wide and decided once -- see <see cref="LlamaSharpInferenceRuntime.NativeLogsSuppressed"/>.
    /// </summary>
    public bool NativeLogging { get; init; }
}

/// <summary>
/// The Phase B <see cref="IInferenceRuntime"/> (AD-8): real generation through LLamaSharp
/// 0.27 over the GGUF base model and the converted adapter GGUFs. It sits beside
/// <see cref="DeterministicInferenceRuntime"/>, never in place of it -- the fake stays the
/// default and its suite never touches a model file.
///
/// The contract is satisfied unchanged. <see cref="ApplyAdaptersAsync"/> applies a set to
/// one live context with <c>SetLoraAdapters</c>, <see cref="GenerateAsync"/> generates on
/// that context, and the reported <see cref="InferenceResult.AppliedAdapters"/> is the set
/// this runtime actually handed the native context -- not the set the caller asked for and
/// not the set a strategy wishes were applied. Nothing is ever read back out of a live
/// context (AD-5); the applied set is the runtime's own record of what it did.
///
/// <para>
/// <b>Locking.</b> Two gates, in this order and never the other way round:
/// <list type="number">
/// <item><see cref="BaseModelLease.Gate"/>, a reader/writer lock over one shared
/// <see cref="LLamaWeights"/>. Anything that <i>reads</i> the model -- creating a context,
/// decoding, applying a set to a context -- takes it for reading, so contexts still decode
/// concurrently. Anything that <i>mutates</i> the model -- reading an adapter container into
/// it, unloading one, disposing the weights -- takes it for writing, so a container load can
/// never run while another context is decoding the same model, and no context can be freed
/// while a decode is inside it.</item>
/// <item><see cref="ContextSlot.Gate"/>, per context, so apply and generate on one context
/// are serialized even if a lease were ever handed out twice.</item>
/// </list>
/// <see cref="ModelRegistryGate"/> is separate and unrelated: it guards only the static
/// registry of leases and the native-log decision, and is never held across native work.
/// </para>
///
/// <para>
/// Facts about llama.cpp that shape this class, all recorded in <c>GOTCHAS.md</c>: adapter
/// weights are <i>model</i>-resident while the applied set is <i>context</i>-resident, so a
/// container is read once per lease and applying it to a context is a pointer operation; and
/// a prompt longer than the context aborts the process inside <c>llama_decode</c> rather
/// than returning an error, so the token count is checked against the live context before
/// any batch is decoded.
/// </para>
/// </summary>
public sealed class LlamaSharpInferenceRuntime : IInferenceRuntime, IDisposable
{
    /// <summary>Records retained, matching the fake so a long-running host stays bounded.</summary>
    public const int MaxRecords = DeterministicInferenceRuntime.MaxRecords;

    /// <summary>The placeholder <see cref="LlamaRuntimeSettings.PromptTemplate"/> substitutes.</summary>
    public const string PromptPlaceholder = "{prompt}";

    /// <summary>Every reason line this runtime raises starts here, so a caller can spot one.</summary>
    public const string ReasonPrefix = "Inference: ";

    private static readonly IReadOnlySet<AdapterId> None = FrozenSet<AdapterId>.Empty;

    /// <summary>
    /// Guards the static lease registry and the one-shot native-log decision. Never held
    /// across native work, and never nested inside a <see cref="BaseModelLease.Gate"/> write
    /// except in <see cref="ReleaseModel"/>, which is the only place that needs both.
    /// </summary>
    private static readonly Lock ModelRegistryGate = new();

    private static readonly Dictionary<string, BaseModelLease> LoadedModels = new(StringComparer.Ordinal);
    private static bool? _nativeLogsSuppressed;
    private static int _baseModelLoadCount;

    private readonly LlamaRuntimeSettings _settings;
    private readonly ModelParams _parameters;
    private readonly BaseModelLease _model;
    private readonly ConcurrentDictionary<string, Lazy<ContextSlot>> _contexts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<AdapterId, Lazy<LoraAdapter>> _adapters = new();
    private readonly ConcurrentQueue<GenerationRecord> _records = new();
    private int _disposed;

    private LlamaSharpInferenceRuntime(LlamaRuntimeSettings settings, ModelParams parameters, BaseModelLease model)
    {
        _settings = settings;
        _parameters = parameters;
        _model = model;
    }

    /// <summary>
    /// How many times this process has read base weights off disk. Every runtime over the
    /// same model shares one copy, so composing the runtime twice must not make this two.
    /// It is a per-lease count, not a per-process one: when the last runtime over a model is
    /// disposed the weights are freed, and a later <see cref="Load"/> reads them again.
    /// </summary>
    public static int BaseModelLoadCount
    {
        get
        {
            lock (ModelRegistryGate)
            {
                return _baseModelLoadCount;
            }
        }
    }

    /// <summary>
    /// The native-log decision this process made, or <see langword="null"/> before any
    /// <see cref="Load"/>. It is a one-shot: llama.cpp freezes its configuration the moment
    /// the native library loads, so the first <see cref="Load"/> decides for the process and
    /// a later <see cref="LlamaRuntimeSettings.NativeLogging"/> cannot change it. Exposed so
    /// that is observable rather than silent.
    /// </summary>
    public static bool? NativeLogsSuppressed
    {
        get
        {
            lock (ModelRegistryGate)
            {
                return _nativeLogsSuppressed;
            }
        }
    }

    /// <summary>The most recent generations, oldest first, with the set each one ran under.</summary>
    public IReadOnlyList<GenerationRecord> Generations => _records.ToArray();

    /// <summary>The GGUF base model this runtime generates over.</summary>
    public string ModelPath => _settings.ModelPath;

    /// <summary>
    /// Take a lease on the base model -- reading it off disk only if no live lease already
    /// holds it -- and return a runtime over it. Every failure is an
    /// <see cref="InvalidOperationException"/> whose message is a single line starting with
    /// <see cref="ReasonPrefix"/>: never a native stack trace and never a multi-line native
    /// message, so the composition root can print it as one reason line and fail startup
    /// before any listener binds.
    /// </summary>
    public static LlamaSharpInferenceRuntime Load(LlamaRuntimeSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        // Reason lines, not argument exceptions: these are configuration, and the caller
        // prints whatever comes out of here as its one startup line.
        if (settings.ContextSize < 1)
        {
            throw Reason($"ContextSize must be at least 1, not {settings.ContextSize}.");
        }

        if (settings.MaxTokens < 1)
        {
            throw Reason($"MaxTokens must be at least 1, not {settings.MaxTokens}.");
        }

        // GOTCHA: this must precede the first touch of any LLamaSharp type -- constructing
        // ModelParams alone already loads the native library, and after that the
        // configuration is frozen.
        ConfigureNativeLogs(settings.NativeLogging);

        RequireGguf(settings.ModelPath, "base model");
        foreach ((AdapterId id, string file) in settings.AdapterFiles.OrderBy(kv => kv.Key.Value, StringComparer.Ordinal))
        {
            RequireGguf(file, $"GGUF container for adapter '{id}'");
        }

        uint batch = (uint)Math.Min(settings.ContextSize, 512);
        var parameters = new ModelParams(settings.ModelPath)
        {
            ContextSize = (uint)settings.ContextSize,
            BatchSize = batch,
            UBatchSize = batch,
            GpuLayerCount = settings.GpuLayerCount,
            Threads = settings.Threads > 0 ? settings.Threads : null,
            BatchThreads = settings.Threads > 0 ? settings.Threads : null,
            SeqMax = 1,
        };

        BaseModelLease model = AcquireModel(settings, parameters);
        return new LlamaSharpInferenceRuntime(settings, parameters, model);
    }

    /// <inheritdoc/>
    public Task ApplyAdaptersAsync(string contextId, IReadOnlySet<AdapterId> adapters, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contextId);
        ArgumentNullException.ThrowIfNull(adapters);
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();

        // Resolved first and outside every other gate: reading a container into the model is
        // a model mutation, and it takes the model gate for writing.
        (LoraAdapter Adapter, float Scale)[] handles = adapters
            .OrderBy(a => a.Value, StringComparer.Ordinal)
            .Select(a => (Handle(a), _settings.AdapterScale))
            .ToArray();

        _model.Gate.EnterReadLock();
        try
        {
            ThrowIfDisposed();
            ContextSlot slot = Slot(contextId);
            lock (slot.Gate)
            {
                // Replaces the context's whole adapter set, so a stale adapter from the previous
                // tenant cannot survive a swap -- an empty set clears the context back to base.
                slot.Context.NativeHandle.SetLoraAdapters(handles);
                slot.Applied = adapters.ToFrozenSet();
            }
        }
        finally
        {
            _model.Gate.ExitReadLock();
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<InferenceResult> GenerateAsync(string contextId, string prompt, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contextId);
        ArgumentNullException.ThrowIfNull(prompt);
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();

        // Held for the whole decode: Dispose takes this gate for writing, so no context can
        // be freed while a decode is running inside it.
        _model.Gate.EnterReadLock();
        try
        {
            ThrowIfDisposed();
            ContextSlot slot = Slot(contextId);
            lock (slot.Gate)
            {
                // Read and recorded before a token is decoded: the routing evidence is what the
                // runtime applied, and it must survive a generation that then fails.
                IReadOnlySet<AdapterId> applied = slot.Applied;
                Record(new GenerationRecord(contextId, applied));

                string text = Generate(slot.Context, prompt, cancellationToken);
                return Task.FromResult(new InferenceResult(applied, text));
            }
        }
        finally
        {
            _model.Gate.ExitReadLock();
        }
    }

    /// <summary>
    /// Dispose every context and adapter this runtime owns and release its lease on the base
    /// model. Runs exactly once however many times it is called, and waits for every in-flight
    /// decode: the model gate is taken for writing, which no reader can hold at the same time.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // Exclusive: every GenerateAsync and ApplyAdaptersAsync holds this for reading, so by
        // the time it is granted no native call is inside any of these contexts.
        _model.Gate.EnterWriteLock();
        try
        {
            // Contexts first: one still holds pointers into the adapters unloaded below. The
            // per-context gate is taken as well -- redundant under the write lock, and cheap
            // insurance if a future caller ever reaches a context by another route.
            foreach (Lazy<ContextSlot> slot in _contexts.Values)
            {
                if (!slot.IsValueCreated)
                {
                    continue;
                }

                ContextSlot value = slot.Value;
                lock (value.Gate)
                {
                    value.Dispose();
                }
            }

            _contexts.Clear();

            foreach (Lazy<LoraAdapter> adapter in _adapters.Values)
            {
                if (adapter.IsValueCreated)
                {
                    adapter.Value.Unload();
                }
            }

            _adapters.Clear();

            // Inside the write lock: the weights may be freed here, and nothing may be
            // decoding over them when they are.
            ReleaseModel(_model);
        }
        finally
        {
            _model.Gate.ExitWriteLock();
        }
    }

    // --- contexts and adapters ------------------------------------------------

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    /// <summary>
    /// The context for <paramref name="contextId"/>, created on first use. A creation that
    /// fails is evicted rather than cached: a faulted <see cref="Lazy{T}"/> rethrows the same
    /// exception forever, which would retire that pooled context id for the process.
    /// </summary>
    private ContextSlot Slot(string contextId)
    {
        Lazy<ContextSlot> lazy = _contexts.GetOrAdd(contextId, _ => new Lazy<ContextSlot>(
            () => new ContextSlot(_model.Weights.CreateContext(_parameters)),
            LazyThreadSafetyMode.ExecutionAndPublication));

        try
        {
            return lazy.Value;
        }
        catch
        {
            _contexts.TryRemove(new KeyValuePair<string, Lazy<ContextSlot>>(contextId, lazy));
            throw;
        }
    }

    /// <summary>
    /// The native handle for <paramref name="id"/>, its container read into the model on
    /// first use. As with <see cref="Slot"/>, a failed load is evicted so one transient
    /// failure does not retire the adapter for the rest of the process.
    /// </summary>
    private LoraAdapter Handle(AdapterId id)
    {
        if (!_settings.AdapterFiles.TryGetValue(id, out string? file))
        {
            // Not a native fault: the caller asked for an adapter this runtime was never
            // given a container for. The strategy maps it to adapter-not-applied.
            throw new InvalidOperationException($"No GGUF container is configured for adapter '{id}'.");
        }

        Lazy<LoraAdapter> lazy = _adapters.GetOrAdd(id, _ => new Lazy<LoraAdapter>(
            () =>
            {
                // Adapter weights are model-resident in llama.cpp, so reading a container
                // mutates the shared model: exclusive against every decode on every context.
                _model.Gate.EnterWriteLock();
                try
                {
                    return _model.Weights.NativeHandle.LoadLoraFromFile(file);
                }
                finally
                {
                    _model.Gate.ExitWriteLock();
                }
            },
            LazyThreadSafetyMode.ExecutionAndPublication));

        try
        {
            return lazy.Value;
        }
        catch
        {
            _adapters.TryRemove(new KeyValuePair<AdapterId, Lazy<LoraAdapter>>(id, lazy));
            throw;
        }
    }

    private void Record(GenerationRecord record)
    {
        _records.Enqueue(record);
        while (_records.Count > MaxRecords && _records.TryDequeue(out _))
        {
        }
    }

    // --- generation -----------------------------------------------------------

    private string Generate(LLamaContext context, string prompt, CancellationToken cancellationToken)
    {
        // A pooled context served some other tenant a moment ago. Its KV cache is cleared so
        // this generation starts from the prompt alone and nothing carries across tenants.
        // The cost is that no prompt prefix is ever reused -- see GOTCHAS.md, which is where
        // the AD-9 conclusion this affects is recorded.
        context.NativeHandle.MemoryClear(true);

        string text = _settings.PromptTemplate.Length == 0
            ? prompt
            : _settings.PromptTemplate.Replace(PromptPlaceholder, prompt, StringComparison.Ordinal);

        // The vocabulary decides whether a BOS belongs here. Hard-coding it would prepend a
        // second one to a chat template that already opens with its own token.
        LLamaToken[] tokens = context.Tokenize(text, addBos: context.Vocab.ShouldAddBOS, special: true);
        int contextSize = (int)context.ContextSize;
        if (tokens.Length == 0)
        {
            throw new InvalidOperationException("The prompt tokenized to no tokens, so there is nothing to generate from.");
        }

        // GOTCHA: llama.cpp aborts the process on an oversized decode rather than failing it,
        // so this is checked before any batch is built -- tokenizing above is native too, but
        // it is bounded and safe; decoding is neither. The bound is the live context's own
        // size, because llama.cpp pads the configured one.
        if (tokens.Length >= contextSize)
        {
            throw new PromptTooLongException(tokens.Length, contextSize);
        }

        var batch = new LLamaBatch();
        int batchSize = Math.Max(1, (int)context.BatchSize);
        for (int offset = 0; offset < tokens.Length; offset += batchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int count = Math.Min(batchSize, tokens.Length - offset);
            bool last = offset + count >= tokens.Length;
            batch.Clear();
            batch.AddRange(tokens.AsSpan(offset, count), offset, LLamaSeqId.Zero, logitsLast: last);
            Decode(context, batch);
        }

        using var sampler = new GreedySamplingPipeline();

        // A streaming decoder, not one detokenized token at a time: a UTF-8 codepoint is
        // routinely split across two tokens, and per-token decoding turns every one of them
        // into replacement characters. The base model is Qwen3, so that is the normal case.
        var decoder = new StreamingTokenDecoder(context);
        int position = tokens.Length;
        int budget = Math.Min(_settings.MaxTokens, contextSize - tokens.Length);
        string finish = "length";
        for (int i = 0; i < budget; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            LLamaToken token = sampler.Sample(context.NativeHandle, -1);
            if (token.IsEndOfGeneration(context.Vocab))
            {
                finish = "stop";
                break;
            }

            decoder.Add(token);

            batch.Clear();
            batch.Add(token, position++, LLamaSeqId.Zero, logits: true);
            Decode(context, batch);
        }

        string generated = decoder.Read();
        if (generated.Length == 0)
        {
            // An empty 200 is indistinguishable from a real completion, and the caller would
            // read "the adapter produced nothing" as an answer. It is a failure, and the
            // finish reason says which kind.
            throw new InvalidOperationException(
                $"Generation produced no text (finish reason: {finish}, budget {budget} tokens).");
        }

        return generated;
    }

    private static void Decode(LLamaContext context, LLamaBatch batch)
    {
        DecodeResult result = context.NativeHandle.Decode(batch);
        if (result != DecodeResult.Ok)
        {
            // The native detail travels as an exception message, which the serving path keeps
            // in the logs: the body only ever says generation-failed.
            throw new InvalidOperationException($"The native runtime failed to decode a batch: {result}.");
        }
    }

    // --- the shared base model -------------------------------------------------

    private static BaseModelLease AcquireModel(LlamaRuntimeSettings settings, ModelParams parameters)
    {
        string key = $"{Path.GetFullPath(settings.ModelPath)}|{settings.GpuLayerCount}";
        lock (ModelRegistryGate)
        {
            if (LoadedModels.TryGetValue(key, out BaseModelLease? existing))
            {
                existing.References++;
                return existing;
            }

            LLamaWeights weights;
            try
            {
                weights = LLamaWeights.LoadFromFile(parameters);
            }
            catch (Exception ex)
            {
                // Only the exception's type, and flattened: a native load failure's own
                // message runs to several lines, and the caller prints this as one line.
                throw Reason($"the GGUF base model '{settings.ModelPath}' could not be loaded ({ex.GetType().Name}).", ex);
            }

            _baseModelLoadCount++;
            var lease = new BaseModelLease(key, weights);
            LoadedModels[key] = lease;
            return lease;
        }
    }

    /// <summary>
    /// Drop one reference, freeing the weights when the last one goes. Idempotent: a lease
    /// already at zero is left alone, so a double dispose cannot free the weights twice.
    /// Called with the lease's gate held for writing.
    /// </summary>
    private static void ReleaseModel(BaseModelLease lease)
    {
        lock (ModelRegistryGate)
        {
            if (lease.References <= 0)
            {
                return;
            }

            if (--lease.References > 0)
            {
                return;
            }

            LoadedModels.Remove(lease.Key);
            lease.Weights.Dispose();
        }
    }

    /// <summary>
    /// Decide, once for the process, whether llama.cpp's own logging is suppressed. llama.cpp
    /// freezes its configuration when the native library loads, so this cannot be revisited;
    /// the decision is recorded either way and published as
    /// <see cref="NativeLogsSuppressed"/> so a later, differing setting is visibly ignored
    /// rather than silently ignored.
    /// </summary>
    private static void ConfigureNativeLogs(bool nativeLogging)
    {
        lock (ModelRegistryGate)
        {
            if (_nativeLogsSuppressed is not null)
            {
                return;
            }

            if (nativeLogging)
            {
                // Recorded, not skipped: the first Load decides, including when it decides to
                // leave llama.cpp's logging alone.
                _nativeLogsSuppressed = false;
                return;
            }

            _nativeLogsSuppressed = true;
            try
            {
                // llama.cpp writes its load-time chatter to stderr uninvited, which would sit in
                // the middle of the startup reason lines this host is judged by.
                NativeLibraryConfig.All.WithLogCallback((_, _) => { });
            }
            catch (InvalidOperationException)
            {
                // Something already loaded the native library in this process, which freezes
                // the configuration. Its logging stays as it is rather than failing startup.
                _nativeLogsSuppressed = false;
            }
        }
    }

    /// <summary>
    /// Every container this runtime will hand llama.cpp, checked before it is handed over.
    /// GOTCHA: <c>llama_adapter_lora_init</c> logs its failure and returns nothing managed
    /// code can test, so a container that is not a GGUF would be "applied" silently and the
    /// request would be answered from the base model while the report named the adapter --
    /// the one failure AD-7 exists to prevent. The magic is checked here instead.
    /// </summary>
    private static void RequireGguf(string path, string what)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw Reason($"no path is configured for the {what}.");
        }

        if (!File.Exists(path))
        {
            throw Reason($"the {what} file '{path}' does not exist.");
        }

        Span<byte> magic = stackalloc byte[4];
        using (FileStream file = File.OpenRead(path))
        {
            if (file.ReadAtLeast(magic, magic.Length, throwOnEndOfStream: false) < magic.Length
                || !magic.SequenceEqual("GGUF"u8))
            {
                throw Reason($"the {what} file '{path}' is not a GGUF container.");
            }
        }
    }

    /// <summary>One prefixed, single-line reason, whatever the underlying message looked like.</summary>
    private static InvalidOperationException Reason(string reason, Exception? inner = null) =>
        new(ReasonPrefix + reason.ReplaceLineEndings(" ").Trim(), inner);

    private sealed class BaseModelLease(string key, LLamaWeights weights)
    {
        public string Key { get; } = key;

        public LLamaWeights Weights { get; } = weights;

        /// <summary>
        /// Readers decode over the model; writers mutate or free it. Shared by every runtime
        /// holding this lease, because they share the weights the lock protects.
        /// </summary>
        public ReaderWriterLockSlim Gate { get; } = new(LockRecursionPolicy.NoRecursion);

        public int References { get; set; } = 1;
    }

    private sealed class ContextSlot(LLamaContext context) : IDisposable
    {
        public LLamaContext Context { get; } = context;

        /// <summary>Serializes apply and generate on one context; the lease already guarantees one holder.</summary>
        public Lock Gate { get; } = new();

        /// <summary>The set this runtime last handed the native context.</summary>
        public IReadOnlySet<AdapterId> Applied { get; set; } = None;

        public void Dispose() => Context.Dispose();
    }
}
