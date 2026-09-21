namespace LoraHotswap.Core;

/// <summary>
/// What a runtime reports after generating on a context: the exact adapter set it
/// had applied to that context (AD-5 routing evidence) and the produced text.
/// </summary>
/// <param name="AppliedAdapters">The adapter set applied to the context at generation time.</param>
/// <param name="Text">The generated text (a deterministic placeholder on the fake runtime).</param>
public sealed record InferenceResult(IReadOnlySet<AdapterId> AppliedAdapters, string Text);

/// <summary>
/// Driven port for generation (AD-8). Two implementations: a deterministic fake that
/// reports its applied adapter set without generating real text (Phase A), and the
/// LLamaSharp runtime (Epic 3).
///
/// Declared in <c>Core</c> rather than <c>Core.Ports</c> because its signatures use
/// the <see cref="AdapterId"/> value type (NFR8), and <c>Core.Ports</c> may reference
/// nothing. <c>PortConventionTests</c> scans <c>Core</c> interfaces as well, so NFR6
/// still binds it.
/// </summary>
public interface IInferenceRuntime
{
    /// <summary>
    /// Replace the adapter set applied to <paramref name="contextId"/>. Only the current
    /// lease holder calls this (AD-3).
    /// </summary>
    Task ApplyAdaptersAsync(string contextId, IReadOnlySet<AdapterId> adapters, CancellationToken cancellationToken);

    /// <summary>Generate on <paramref name="contextId"/> with whatever adapter set is applied to it.</summary>
    Task<InferenceResult> GenerateAsync(string contextId, string prompt, CancellationToken cancellationToken);
}
