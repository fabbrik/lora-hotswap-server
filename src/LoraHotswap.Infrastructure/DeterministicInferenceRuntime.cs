using System.Collections.Concurrent;
using System.Collections.Frozen;
using LoraHotswap.Core;

namespace LoraHotswap.Infrastructure;

/// <summary>One generation as the fake runtime saw it: the context and its exact applied adapter set.</summary>
/// <param name="ContextId">The context generated on.</param>
/// <param name="AppliedAdapters">The adapter set applied to that context at generation time.</param>
public sealed record GenerationRecord(string ContextId, IReadOnlySet<AdapterId> AppliedAdapters);

/// <summary>
/// The deterministic fake <see cref="IInferenceRuntime"/> (AD-8). It tracks the adapter
/// set applied to each context, reports that set with every generation, and records
/// it, without generating real text. The Phase A suite runs against it.
/// </summary>
public sealed class DeterministicInferenceRuntime : IInferenceRuntime
{
    /// <summary>Records retained; older ones are dropped so a long-running host stays bounded.</summary>
    public const int MaxRecords = 10_000;

    private static readonly IReadOnlySet<AdapterId> None = FrozenSet<AdapterId>.Empty;

    private readonly ConcurrentDictionary<string, IReadOnlySet<AdapterId>> _applied = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<GenerationRecord> _records = new();

    /// <summary>The most recent generations, oldest first.</summary>
    public IReadOnlyList<GenerationRecord> Generations => _records.ToArray();

    /// <inheritdoc/>
    public Task ApplyAdaptersAsync(string contextId, IReadOnlySet<AdapterId> adapters, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contextId);
        ArgumentNullException.ThrowIfNull(adapters);
        cancellationToken.ThrowIfCancellationRequested();

        _applied[contextId] = adapters.ToFrozenSet();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<InferenceResult> GenerateAsync(string contextId, string prompt, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contextId);
        ArgumentNullException.ThrowIfNull(prompt);
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlySet<AdapterId> applied = _applied.GetValueOrDefault(contextId, None);

        _records.Enqueue(new GenerationRecord(contextId, applied));
        while (_records.Count > MaxRecords && _records.TryDequeue(out _))
        {
        }

        string adapters = string.Join(",", applied.Select(a => a.Value).Order(StringComparer.Ordinal));
        return Task.FromResult(new InferenceResult(applied, $"fake:{adapters}:{prompt}"));
    }
}
