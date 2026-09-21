using System.Text;
using System.Text.Json;
using TorchSharp;
using static TorchSharp.torch;

namespace LoraHotswap.Infrastructure;

/// <summary>
/// Minimal safetensors reader.
///
/// Ported from rung 1's
/// <c>lora-anatomy-dotnet/src/LoraAnatomy/SafeTensors.cs</c> (the reuse mandate); only
/// the namespace changed, plus a <see cref="Load(Stream)"/> overload. Reuses the header/offset parsing and dtype
/// handling unchanged.
///
/// Layout: [8 bytes little-endian u64 = header length][JSON header][raw tensor data]
/// The JSON header maps tensor name -> { dtype, shape, data_offsets: [start, end] },
/// where offsets are relative to the start of the data block. Data is C-contiguous
/// (row-major).
/// </summary>
public static class SafeTensors
{
    public static Dictionary<string, Tensor> Load(string path)
    {
        using var stream = File.OpenRead(path);
        return Load(stream);
    }

    /// <summary>
    /// Load from a seekable stream (added for running-instance verification, which parses
    /// the same bytes it hashed). The stream is left open.
    /// </summary>
    public static Dictionary<string, Tensor> Load(Stream stream)
    {
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

        ulong headerLen = reader.ReadUInt64();
        string headerJson = Encoding.UTF8.GetString(reader.ReadBytes((int)headerLen));

        long dataStart = 8 + (long)headerLen;
        var result = new Dictionary<string, Tensor>();

        using var doc = JsonDocument.Parse(headerJson);
        foreach (var entry in doc.RootElement.EnumerateObject())
        {
            // "__metadata__" is a reserved key holding free-form strings, not a tensor.
            if (entry.Name == "__metadata__") continue;

            string dtype = entry.Value.GetProperty("dtype").GetString()!;
            long[] shape = entry.Value.GetProperty("shape")
                .EnumerateArray().Select(e => e.GetInt64()).ToArray();
            var offsets = entry.Value.GetProperty("data_offsets")
                .EnumerateArray().Select(e => e.GetInt64()).ToArray();

            long byteCount = offsets[1] - offsets[0];
            stream.Seek(dataStart + offsets[0], SeekOrigin.Begin);
            byte[] raw = reader.ReadBytes((int)byteCount);

            result[entry.Name] = ToTensor(raw, dtype, shape);
        }

        return result;
    }

    private static Tensor ToTensor(byte[] raw, string dtype, long[] shape)
    {
        switch (dtype)
        {
            case "F32":
            {
                var values = new float[raw.Length / 4];
                Buffer.BlockCopy(raw, 0, values, 0, raw.Length);
                return tensor(values, shape);
            }
            case "F64":
            {
                var values = new double[raw.Length / 8];
                Buffer.BlockCopy(raw, 0, values, 0, raw.Length);
                return tensor(values, shape);
            }
            case "F16":
            {
                // No managed half-precision path here: read as raw bytes and let
                // TorchSharp reinterpret, then promote to float32 for arithmetic.
                var values = new short[raw.Length / 2];
                Buffer.BlockCopy(raw, 0, values, 0, raw.Length);
                return tensor(values, shape).view_as_real().to(ScalarType.Float32);
            }
            default:
                throw new NotSupportedException(
                    $"dtype '{dtype}' not handled yet. Add it to SafeTensors.ToTensor.");
        }
    }
}
