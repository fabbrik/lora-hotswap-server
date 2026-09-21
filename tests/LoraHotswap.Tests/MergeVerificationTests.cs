using LoraHotswap.Core;
using LoraHotswap.Infrastructure;
using static TorchSharp.torch;

namespace LoraHotswap.Tests;

/// <summary>
/// Story 1.7: two-sided merge verification against real Python PEFT references.
///
/// The fixtures under <c>fixtures/merge/</c> come from <c>make_merge_fixtures.py</c>, which
/// follows rung 1's PEFT recipe in its environment: the adapter tensors and the merged weights are PEFT's own,
/// so a pass proves tensor naming, orientation and scaling agree with PEFT -- not with a
/// hand-computed expectation.
///
/// Scaling 1 is a trap: an implementation that forgets alpha/r is indistinguishable from a
/// correct one on that fixture. Only the scaling-4 fixture can catch the omission, which
/// <see cref="Omitting_scaling_is_invisible_on_the_scaling_one_fixture_alone"/> documents.
/// </summary>
public sealed class MergeVerificationTests
{
    private const string Module = "model.layers.0.self_attn.q_proj";
    private const string PeftModule = "base_model.model." + Module;

    [Fact]
    public async Task Admitted_scaling_four_adapter_matches_reference_and_changes_every_module()
    {
        AdmittedAdapter adapter = await TestFixtures.AdmitAsync(TestFixtures.ScalingFour);
        Assert.Equal(4.0, adapter.Config.Scaling);

        AdapterVerification result = await Verifier(adapter, TestFixtures.ScalingFour).VerifyAsync(adapter, CancellationToken.None);

        Assert.Equal(VerificationOutcome.Match, result.Outcome);
        Assert.Equal(adapter.Id, result.Adapter);
        Assert.Equal(4, result.Modules.Count);
        Assert.All(result.Modules, m =>
        {
            Assert.True(m.MatchesReference, $"{m.Module}: reference error {m.ReferenceError}");
            Assert.True(m.ChangedFromBase, $"{m.Module}: change {m.ChangeFromBase}");
        });
    }

    [Fact]
    public async Task Admitted_scaling_one_adapter_matches_reference_and_changes_every_module()
    {
        AdmittedAdapter adapter = await TestFixtures.AdmitAsync(TestFixtures.ScalingOne);
        Assert.Equal(1.0, adapter.Config.Scaling);

        AdapterVerification result = await Verifier(adapter, TestFixtures.ScalingOne).VerifyAsync(adapter, CancellationToken.None);

        Assert.Equal(VerificationOutcome.Match, result.Outcome);
        Assert.All(result.Modules, m => Assert.True(m.Passed));
    }

    [Fact]
    public void Omitting_scaling_fails_against_the_scaling_four_reference()
    {
        using var scope = NewDisposeScope();
        var (adapter, reference) = Load(TestFixtures.ScalingFour);

        // A deliberately wrong implementation: W0 + B @ A, scaling forgotten.
        Tensor w0 = reference[$"{Module}.w0"];
        Tensor unscaled = w0 + adapter[$"{PeftModule}.lora_B.weight"].matmul(adapter[$"{PeftModule}.lora_A.weight"]);

        ModuleVerification result = MergeVerifier.CompareModule(Module, w0, unscaled, reference[$"{Module}.w_merged"]);

        Assert.False(result.MatchesReference);
        Assert.True(result.ReferenceError > result.Tolerance);
        Assert.Equal(Module, result.Module);

        var verdict = AdapterVerification.Judge(new AdapterId("sha256:" + new string('4', 64)), [result]);
        Assert.Equal(VerificationOutcome.Mismatch, verdict.Outcome);
        Assert.Contains(Module, verdict.Detail);
        Assert.Contains("reference error", verdict.Detail);
    }

    [Fact]
    public void Omitting_scaling_is_invisible_on_the_scaling_one_fixture_alone()
    {
        // This test passes on purpose. With alpha/r = 1, W0 + B@A equals W0 + 1·B@A, so the
        // same wrong implementation that Omitting_scaling_fails_against_the_scaling_four_reference
        // catches is judged correct here. The scaling-1 fixture cannot detect the omission on
        // its own; the scaling-4 fixture is what makes the suite sensitive to it.
        using var scope = NewDisposeScope();
        var (adapter, reference) = Load(TestFixtures.ScalingOne);

        Tensor w0 = reference[$"{Module}.w0"];
        Tensor unscaled = w0 + adapter[$"{PeftModule}.lora_B.weight"].matmul(adapter[$"{PeftModule}.lora_A.weight"]);

        Assert.True(MergeVerifier.CompareModule(Module, w0, unscaled, reference[$"{Module}.w_merged"]).Passed);
    }

    [Fact]
    public void Candidate_equal_to_base_fails_even_when_it_matches_the_reference()
    {
        using var scope = NewDisposeScope();
        var (_, reference) = Load(TestFixtures.ScalingFour);
        Tensor w0 = reference[$"{Module}.w0"];

        // Reference equality alone never passes: an unchanged merge is a no-op.
        ModuleVerification result = MergeVerifier.CompareModule(Module, w0, w0, w0);

        Assert.True(result.MatchesReference);
        Assert.False(result.ChangedFromBase);
        Assert.False(result.Passed);
        Assert.Equal(VerificationOutcome.Inert, AdapterVerification.Judge(new AdapterId("sha256:" + new string('0', 64)), [result]).Outcome);
    }

    [Fact]
    public async Task Adapter_with_every_lora_B_zeroed_is_reported_inert()
    {
        using TempDirectory copy = TestFixtures.Copy(TestFixtures.ScalingFour);
        TestFixtures.RewriteF32(Path.Combine(copy.Path, "adapter_model.safetensors"), n => n.Contains("lora_B"), _ => 0f);
        AdmittedAdapter adapter = TestFixtures.AdapterAt(copy.Path);

        AdapterVerification result = await Verifier(adapter, copy.Path).VerifyAsync(adapter, CancellationToken.None);

        Assert.Equal(VerificationOutcome.Inert, result.Outcome);
        Assert.All(result.Modules, m => Assert.False(m.ChangedFromBase));
        Assert.Contains(adapter.Id.Value, result.Detail);
    }

    [Fact]
    public async Task Reference_mismatch_beyond_tolerance_fails_deterministically_naming_adapter_and_module()
    {
        using TempDirectory copy = TestFixtures.Copy(TestFixtures.ScalingFour);
        TestFixtures.RewriteF32(Path.Combine(copy.Path, SafeTensorsAdapterVerifier.ReferenceFileName),
            n => n == $"{Module}.w_merged", v => v + 1e-3f);
        AdmittedAdapter adapter = TestFixtures.AdapterAt(copy.Path);
        var verifier = Verifier(adapter, copy.Path);

        AdapterVerification first = await verifier.VerifyAsync(adapter, CancellationToken.None);
        AdapterVerification second = await verifier.VerifyAsync(adapter, CancellationToken.None);

        Assert.Equal(VerificationOutcome.Mismatch, first.Outcome);
        Assert.Equal(first.Detail, second.Detail);
        Assert.Equal(first.Modules, second.Modules);
        Assert.Contains(adapter.Id.Value, first.Detail);
        Assert.Contains(Module, first.Detail);
        var failed = Assert.Single(first.Modules, m => !m.Passed);
        Assert.Equal(Module, failed.Module);
        Assert.True(failed.ReferenceError > failed.Tolerance);
    }

    [Fact]
    public void Verify_adapter_reports_every_module_in_name_order()
    {
        using var scope = NewDisposeScope();
        var (tensors, reference) = Load(TestFixtures.ScalingFour);
        var id = new AdapterId("sha256:" + new string('d', 64));

        AdapterVerification result = MergeVerifier.VerifyAdapter(id, tensors, reference, new AdapterConfig(8, 32, false, "base"));

        Assert.Equal(
            [
                "model.layers.0.self_attn.q_proj",
                "model.layers.0.self_attn.v_proj",
                "model.layers.1.self_attn.q_proj",
                "model.layers.1.self_attn.v_proj",
            ],
            result.Modules.Select(m => m.Module));
    }

    [Fact]
    public void A_reference_sample_verifies_exactly_the_modules_it_covers()
    {
        // Real references are a sample of the targeted modules (make_references.py).
        using var scope = NewDisposeScope();
        var (tensors, reference) = Load(TestFixtures.ScalingFour);
        var sample = reference.Where(kv => !kv.Key.StartsWith("model.layers.1.self_attn.v_proj", StringComparison.Ordinal))
            .ToDictionary(kv => kv.Key, kv => kv.Value);

        AdapterVerification result = MergeVerifier.VerifyAdapter(
            new AdapterId("sha256:" + new string('e', 64)), tensors, sample, new AdapterConfig(8, 32, false, "base"));

        Assert.Equal(VerificationOutcome.Match, result.Outcome);
        Assert.Equal(3, result.Modules.Count);
        Assert.DoesNotContain(result.Modules, m => m.Module == "model.layers.1.self_attn.v_proj");
        Assert.Contains("3 reference-covered modules (of 4 targeted)", result.Detail);
    }

    [Fact]
    public void A_covered_module_the_adapter_does_not_target_fails_naming_it()
    {
        using var scope = NewDisposeScope();
        var (tensors, reference) = Load(TestFixtures.ScalingFour);
        var untargeted = tensors.Where(kv => !kv.Key.Contains("layers.1.self_attn.v_proj", StringComparison.Ordinal))
            .ToDictionary(kv => kv.Key, kv => kv.Value);

        AdapterVerification result = MergeVerifier.VerifyAdapter(
            new AdapterId("sha256:" + new string('e', 64)), untargeted, reference, new AdapterConfig(8, 32, false, "base"));

        Assert.Equal(VerificationOutcome.Mismatch, result.Outcome);
        Assert.Contains("model.layers.1.self_attn.v_proj", result.Detail);
        Assert.Contains("does not target it", result.Detail);
    }

    [Fact]
    public void A_reference_covering_nothing_is_never_a_match()
    {
        using var scope = NewDisposeScope();
        var (tensors, _) = Load(TestFixtures.ScalingFour);

        AdapterVerification result = MergeVerifier.VerifyAdapter(
            new AdapterId("sha256:" + new string('e', 64)), tensors, new Dictionary<string, Tensor>(), new AdapterConfig(8, 32, false, "base"));

        Assert.Equal(VerificationOutcome.Mismatch, result.Outcome);
    }

    [Fact]
    public void Shapes_that_do_not_compose_are_a_mismatch_naming_the_module_not_a_fault()
    {
        using var scope = NewDisposeScope();
        var (tensors, reference) = Load(TestFixtures.ScalingFour);
        var wrongShape = new Dictionary<string, Tensor>(reference)
        {
            [$"{Module}.w0"] = zeros(3, 3),
            [$"{Module}.w_merged"] = zeros(3, 3),
        };

        AdapterVerification result = MergeVerifier.VerifyAdapter(
            new AdapterId("sha256:" + new string('e', 64)), tensors, wrongShape, new AdapterConfig(8, 32, false, "base"));

        Assert.Equal(VerificationOutcome.Mismatch, result.Outcome);
        ModuleVerification faulted = Assert.Single(result.Modules, m => m.Fault is not null);
        Assert.Equal(Module, faulted.Module);
        Assert.Contains("shapes do not compose", result.Detail);
    }

    [Fact]
    public async Task Source_changed_after_admission_is_drift_not_verified_under_the_admitted_identity()
    {
        using TempDirectory copy = TestFixtures.Copy(TestFixtures.ScalingFour);
        AdmittedAdapter admitted = TestFixtures.AdapterAt(copy.Path);
        TestFixtures.RewriteF32(Path.Combine(copy.Path, "adapter_model.safetensors"), n => n.Contains("lora_B"), v => v * 2f);

        var error = await Assert.ThrowsAsync<RequestProblemException>(() =>
            Verifier(admitted, copy.Path).VerifyAsync(admitted, CancellationToken.None));

        Assert.Equal(ProblemTypes.HashMismatch, error.Problem.Type);
        Assert.Contains(admitted.Id.Value, error.Problem.Detail);
    }

    [Fact]
    public async Task The_admitted_scaling_is_used_not_a_re_read_of_the_config_file()
    {
        using TempDirectory copy = TestFixtures.Copy(TestFixtures.ScalingFour);
        AdmittedAdapter admitted = TestFixtures.AdapterAt(copy.Path);
        File.WriteAllText(Path.Combine(copy.Path, "adapter_config.json"),
            File.ReadAllText(Path.Combine(copy.Path, "adapter_config.json")).Replace("\"lora_alpha\": 32", "\"lora_alpha\": 8"));

        AdapterVerification result = await Verifier(admitted, copy.Path).VerifyAsync(admitted, CancellationToken.None);

        Assert.Equal(VerificationOutcome.Match, result.Outcome);
    }

    [Fact]
    public void Shapes_that_only_combine_by_broadcasting_are_rejected()
    {
        using var scope = NewDisposeScope();
        Tensor w0 = zeros(4, 6);
        Tensor a = ones(2, 6);
        Tensor transposedB = ones(2, 4); // (r, d) instead of (d, r)

        var error = Assert.Throws<ArgumentException>(() => MergeArithmetic.Merge(Module, w0, a, transposedB, 4.0));
        Assert.Contains(Module, error.Message);

        Assert.Throws<ArgumentException>(() => MergeVerifier.CompareModule(Module, zeros(1, 2), zeros(1, 1), zeros(1, 2)));
    }

    [Fact]
    public void Non_float32_weights_are_measured_without_error()
    {
        using var scope = NewDisposeScope();
        Tensor w0 = zeros(new long[] { 2, 2 }, dtype: ScalarType.Float64);
        Tensor merged = ones(new long[] { 2, 2 }, dtype: ScalarType.Float64);

        ModuleVerification result = MergeVerifier.CompareModule(Module, w0, merged, merged);

        Assert.Equal(0.0, result.ReferenceError);
        Assert.Equal(1.0, result.ChangeFromBase);
    }

    [Fact]
    public async Task Admitted_adapter_without_a_reference_is_a_typed_problem()
    {
        using TempDirectory copy = TestFixtures.Copy(TestFixtures.ScalingFour);
        File.Delete(Path.Combine(copy.Path, SafeTensorsAdapterVerifier.ReferenceFileName));
        var adapter = new AdmittedAdapter(new AdapterId("sha256:" + new string('f', 64)),
            new AdapterConfig(8, 32, false, TestFixtures.ConfiguredBase), "no-reference");

        var error = await Assert.ThrowsAsync<RequestProblemException>(() =>
            Verifier(adapter, copy.Path).VerifyAsync(adapter, CancellationToken.None));

        Assert.Equal(ProblemTypes.MergeReferenceMissing, error.Problem.Type);
    }

    private static SafeTensorsAdapterVerifier Verifier(AdmittedAdapter adapter, string directory) =>
        new(new Dictionary<AdapterId, string> { [adapter.Id] = directory });

    private static (Dictionary<string, Tensor> Adapter, Dictionary<string, Tensor> Reference) Load(string directory) =>
        (SafeTensors.Load(Path.Combine(directory, "adapter_model.safetensors")),
         SafeTensors.Load(Path.Combine(directory, SafeTensorsAdapterVerifier.ReferenceFileName)));
}
