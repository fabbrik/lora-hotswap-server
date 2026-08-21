using System.Text.Json;

namespace LoraHotswap.Core;

/// <summary>
/// The scaling-relevant subset of a PEFT adapter's <c>adapter_config.json</c>.
///
/// Pure config semantics only: no filesystem, no TorchSharp. Parsing is total --
/// an absent <c>use_rslora</c> key defaults to <see langword="false"/> (older
/// adapters omit it) rather than raising, mirroring rung 1's reference parse.
/// </summary>
/// <param name="R">The LoRA rank (<c>r</c>).</param>
/// <param name="Alpha">The LoRA alpha (<c>lora_alpha</c>).</param>
/// <param name="UseRsLora">Whether rSLoRA scaling applies (<c>use_rslora</c>, default false).</param>
/// <param name="BaseModelName">
/// The declared base model (<c>base_model_name_or_path</c>). Compared to the
/// configured base model by <b>strict ordinal string equality</b> at admission --
/// no normalization, case-folding, or vendor-prefix stripping.
/// </param>
public sealed record AdapterConfig(int R, int Alpha, bool UseRsLora, string BaseModelName)
{
    /// <summary>
    /// The merge scaling factor applied as <c>ΔW = Scaling · B · A</c>.
    ///
    /// Standard LoRA divides by <c>r</c>; rSLoRA divides by <c>sqrt(r)</c>.
    /// Matches rung 1's <c>Program.cs:46-47</c> exactly.
    /// </summary>
    public double Scaling => UseRsLora ? Alpha / Math.Sqrt(R) : (double)Alpha / R;

    /// <summary>
    /// Parse the rank, alpha, and rSLoRA flag from an <c>adapter_config.json</c>
    /// document. A missing <c>use_rslora</c> key defaults to false and never raises.
    /// </summary>
    public static AdapterConfig Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        int r = root.GetProperty("r").GetInt32();
        int alpha = root.GetProperty("lora_alpha").GetInt32();

        // GOTCHA (rung 1): older adapters have no "use_rslora" key at all -- it
        // must default to false rather than being read blindly.
        bool useRsLora = root.TryGetProperty("use_rslora", out var rs) && rs.GetBoolean();

        // The declared base model. Kept verbatim -- strict base matching at
        // admission depends on this string being untouched. An absent key
        // yields the empty string, which cannot match any configured base.
        string baseModelName =
            root.TryGetProperty("base_model_name_or_path", out var bm) && bm.ValueKind == JsonValueKind.String
                ? bm.GetString() ?? string.Empty
                : string.Empty;

        return new AdapterConfig(r, alpha, useRsLora, baseModelName);
    }
}
