using System.Security.Cryptography;
using LoraHotswap.Core;
using static TorchSharp.torch;

namespace LoraHotswap.Infrastructure;

/// <summary>
/// The LoRA merge, ported from rung 1's <c>lora-anatomy-dotnet/src/LoraAnatomy/Program.cs</c>
/// (the reuse mandate): <c>W' = W0 + scaling · (B @ A)</c>, with <c>A</c> of shape
/// <c>(r, k)</c>, <c>B</c> of shape <c>(d, r)</c>, and <c>W0</c> of shape <c>(d, k)</c>.
/// The scaling comes from <see cref="AdapterConfig.Scaling"/>, never recomputed here.
/// </summary>
public static class MergeArithmetic
{
    /// <summary>
    /// Compute the merged weight. Shapes are checked explicitly: tensor broadcasting
    /// would otherwise let a wrong-rank or transposed delta combine silently.
    /// </summary>
    public static Tensor Merge(string module, Tensor baseWeight, Tensor loraA, Tensor loraB, double scaling)
    {
        ArgumentNullException.ThrowIfNull(baseWeight);
        ArgumentNullException.ThrowIfNull(loraA);
        ArgumentNullException.ThrowIfNull(loraB);

        if (baseWeight.ndim != 2 || loraA.ndim != 2 || loraB.ndim != 2
            || loraB.shape[1] != loraA.shape[0]
            || loraB.shape[0] != baseWeight.shape[0]
            || loraA.shape[1] != baseWeight.shape[1])
        {
            throw new ArgumentException(
                $"Module '{module}': shapes do not compose -- W0 {Shape(baseWeight)}, A {Shape(loraA)}, B {Shape(loraB)}; " +
                "expected W0 (d, k), A (r, k), B (d, r).");
        }

        using var scope = NewDisposeScope();
        return (baseWeight + scaling * loraB.matmul(loraA)).MoveToOuterDisposeScope();
    }

    internal static string Shape(Tensor t) => $"[{string.Join(", ", t.shape)}]";
}

/// <summary>
/// Two-sided merge verification over tensors (AD-5, spec 1.7): a candidate merge must
/// match the Python PEFT reference within tolerance <b>and</b> differ from the base.
/// Runs offline on CPU with no live inference context.
/// </summary>
public static class MergeVerifier
{
    /// <summary>The absolute tolerance rung 1 established for float32 PEFT agreement.</summary>
    public const double DefaultTolerance = 1e-5;

    /// <summary>PEFT's wrapper prefix on adapter tensor names (rung 1 GOTCHA: "model" x3).</summary>
    private const string PeftPrefix = "base_model.model.";

    private const string LoraASuffix = ".lora_A.weight";
    private const string LoraBSuffix = ".lora_B.weight";
    private const string W0Suffix = ".w0";
    private const string WMergedSuffix = ".w_merged";

    /// <summary>
    /// Measure a candidate merge against the reference and the base. The candidate is
    /// supplied, not computed, so a deliberately wrong implementation (e.g. one that
    /// omits scaling) can be judged by exactly the same comparison.
    /// </summary>
    public static ModuleVerification CompareModule(
        string module, Tensor baseWeight, Tensor candidateMerged, Tensor referenceMerged, double tolerance = DefaultTolerance)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(module);
        ArgumentNullException.ThrowIfNull(baseWeight);
        ArgumentNullException.ThrowIfNull(candidateMerged);
        ArgumentNullException.ThrowIfNull(referenceMerged);
        if (!(tolerance >= 0))
        {
            throw new ArgumentOutOfRangeException(nameof(tolerance), tolerance, "Tolerance must be a non-negative number.");
        }

        if (!candidateMerged.shape.SequenceEqual(baseWeight.shape) || !referenceMerged.shape.SequenceEqual(baseWeight.shape))
        {
            throw new ArgumentException(
                $"Module '{module}': shapes differ -- W0 {MergeArithmetic.Shape(baseWeight)}, " +
                $"candidate {MergeArithmetic.Shape(candidateMerged)}, reference {MergeArithmetic.Shape(referenceMerged)}.");
        }

        // Intermediates are native allocations; the scope frees them once the two
        // magnitudes are read out. Read as float64 so any float dtype works.
        using var scope = NewDisposeScope();
        double referenceError = MaxAbsDifference(candidateMerged, referenceMerged);
        double changeFromBase = MaxAbsDifference(candidateMerged, baseWeight);
        return new ModuleVerification(module, referenceError, changeFromBase, tolerance);
    }

    /// <summary>
    /// Verify every module the reference covers. A real reference is a sample of the
    /// targeted modules (every module of a 0.6B model would be gigabytes), so coverage is
    /// defined by the reference's <c>&lt;module&gt;.w0</c> / <c>&lt;module&gt;.w_merged</c>
    /// pairs (rung 1's ground-truth layout). Each covered module must be targeted by the
    /// adapter (both <c>lora_A</c> and <c>lora_B</c> present) and must compose; otherwise
    /// it fails verification with a fault naming why. A reference that covers nothing is
    /// never a match.
    /// </summary>
    public static AdapterVerification VerifyAdapter(
        AdapterId adapter,
        IReadOnlyDictionary<string, Tensor> adapterTensors,
        IReadOnlyDictionary<string, Tensor> reference,
        AdapterConfig config,
        double tolerance = DefaultTolerance)
    {
        ArgumentNullException.ThrowIfNull(adapterTensors);
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(config);

        var covered = reference.Keys
            .Where(k => k.EndsWith(W0Suffix, StringComparison.Ordinal))
            .Select(k => k[..^W0Suffix.Length])
            .Order(StringComparer.Ordinal)
            .ToList();
        int targeted = adapterTensors.Keys.Count(k => k.EndsWith(LoraASuffix, StringComparison.Ordinal));

        var results = new List<ModuleVerification>(covered.Count);
        foreach (string module in covered)
        {
            Tensor w0 = reference[module + W0Suffix];
            if (!reference.TryGetValue(module + WMergedSuffix, out Tensor? wReference))
            {
                results.Add(ModuleVerification.Faulted(module, tolerance, "the reference has no w_merged for it"));
                continue;
            }

            if (!adapterTensors.TryGetValue(PeftPrefix + module + LoraASuffix, out Tensor? loraA)
                || !adapterTensors.TryGetValue(PeftPrefix + module + LoraBSuffix, out Tensor? loraB))
            {
                results.Add(ModuleVerification.Faulted(module, tolerance, "the adapter does not target it"));
                continue;
            }

            try
            {
                using Tensor merged = MergeArithmetic.Merge(module, w0, loraA, loraB, config.Scaling);
                results.Add(CompareModule(module, w0, merged, wReference, tolerance));
            }
            catch (ArgumentException ex)
            {
                // A wrong-rank or transposed adapter is a mismatch with a diagnostic, not a server fault.
                results.Add(ModuleVerification.Faulted(module, tolerance, ex.Message));
            }
        }

        AdapterVerification verdict = AdapterVerification.Judge(adapter, results);
        return verdict.Outcome == VerificationOutcome.Match
            ? verdict with
            {
                Detail = $"Adapter '{adapter}': all {results.Count} reference-covered modules (of {targeted} targeted) " +
                         "match the Python PEFT reference and differ from their base weights.",
            }
            : verdict;
    }

    private static double MaxAbsDifference(Tensor left, Tensor right) =>
        (left.to_type(ScalarType.Float64) - right.to_type(ScalarType.Float64)).abs().max().item<double>();
}

/// <summary>
/// The <see cref="IAdapterVerifier"/> driven adapter: loads an admitted adapter's
/// <c>adapter_model.safetensors</c> through the ported rung 1 reader and its
/// <c>merge_reference.safetensors</c> (Python PEFT ground truth, written by
/// <c>tools/reference/make_references.py</c>), then runs <see cref="MergeVerifier.VerifyAdapter"/>.
///
/// Adapter storage locations come from admission. They locate files only; identity is
/// always the content <see cref="AdapterId"/> (AD-1).
/// </summary>
public sealed class SafeTensorsAdapterVerifier : IAdapterVerifier
{
    /// <summary>The reference file expected next to an adapter's source safetensors.</summary>
    public const string ReferenceFileName = "merge_reference.safetensors";

    /// <summary>The adapter's source weights, whose SHA-256 is its identity.</summary>
    public const string SourceFileName = "adapter_model.safetensors";

    private readonly IReadOnlyDictionary<AdapterId, string> _adapterDirectories;
    private readonly double _tolerance;

    /// <summary>Create a verifier over the admitted adapters' storage locations.</summary>
    public SafeTensorsAdapterVerifier(
        IReadOnlyDictionary<AdapterId, string> adapterDirectories, double tolerance = MergeVerifier.DefaultTolerance)
    {
        ArgumentNullException.ThrowIfNull(adapterDirectories);
        _adapterDirectories = adapterDirectories;
        _tolerance = tolerance;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The source bytes are read once, re-hashed against the admitted identity, and the
    /// tensors are parsed from those same bytes: files edited or swapped after admission are
    /// reported as drift, never verified under the admitted <see cref="AdapterId"/> (AD-1).
    /// The scaling is the admitted <see cref="AdmittedAdapter.Config"/>'s, not a re-read.
    /// </remarks>
    public Task<AdapterVerification> VerifyAsync(AdmittedAdapter adapter, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(adapter);

        if (!_adapterDirectories.TryGetValue(adapter.Id, out string? directory))
        {
            throw new RequestProblemException(RequestProblem.UnknownAdapter(adapter.Id));
        }

        string referencePath = Path.Combine(directory, ReferenceFileName);
        if (!File.Exists(referencePath))
        {
            throw new RequestProblemException(RequestProblem.MergeReferenceMissing(adapter.Id));
        }

        // CPU-bound native work: keep it off the request thread.
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            byte[] source = File.ReadAllBytes(Path.Combine(directory, SourceFileName));
            string onDisk = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(source));
            if (onDisk != adapter.Id.Value)
            {
                throw new RequestProblemException(RequestProblem.AdapterDrifted(adapter.Id, onDisk));
            }

            Dictionary<string, Tensor> adapterTensors = SafeTensors.Load(new MemoryStream(source, writable: false));
            Dictionary<string, Tensor>? reference = null;
            try
            {
                reference = SafeTensors.Load(referencePath);
                cancellationToken.ThrowIfCancellationRequested();
                return MergeVerifier.VerifyAdapter(adapter.Id, adapterTensors, reference, adapter.Config, _tolerance);
            }
            finally
            {
                foreach (Tensor t in adapterTensors.Values.Concat(reference?.Values ?? Enumerable.Empty<Tensor>()))
                {
                    t.Dispose();
                }
            }
        }, cancellationToken);
    }
}
