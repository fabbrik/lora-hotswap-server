using System.Text;
using System.Text.Json;
using LoraHotswap.Core;
using LoraHotswap.Infrastructure;

namespace LoraHotswap.Tests;

/// <summary>
/// Shared access to the committed offline fixtures, plus byte-level derivations of them
/// (a zeroed <c>lora_B</c>, a perturbed reference) written to throwaway temp copies so the
/// committed files are never touched.
/// </summary>
internal static class TestFixtures
{
    public const string ConfiguredBase = "Qwen/Qwen3-0.6B";

    /// <summary>The PEFT merge fixture with alpha/r = 4 (<c>make_merge_fixtures.py</c>).</summary>
    public static string ScalingFour => Path.Combine(AppContext.BaseDirectory, "fixtures", "merge", "scaling-4");

    /// <summary>The PEFT merge fixture with alpha/r = 1 (<c>make_merge_fixtures.py</c>).</summary>
    public static string ScalingOne => Path.Combine(AppContext.BaseDirectory, "fixtures", "merge", "scaling-1");

    /// <summary>The admitted identity of a fixture, derived exactly as admission derives it.</summary>
    public static async Task<AdmittedAdapter> AdmitAsync(string directory)
    {
        var artifacts = await new FileSystemAdapterArtifactsReader().ReadAsync(directory, CancellationToken.None);
        AdmissionResult result = Admission.Admit(artifacts, ConfiguredBase);
        Assert.True(result.IsAdmitted, result.Problem?.ToStderrLine());
        var c = artifacts.Config!;
        return new AdmittedAdapter(result.AdmittedId!.Value, new AdapterConfig(c.R, c.Alpha, c.UseRsLora, c.BaseModelName),
            artifacts.AdapterDirectoryName);
    }

    /// <summary>
    /// The adapter in <paramref name="directory"/> as admission would identify it -- its
    /// content identity and parsed config -- without requiring a valid manifest, so derived
    /// copies (a zeroed <c>lora_B</c>) carry their real identity rather than a made-up one.
    /// </summary>
    public static AdmittedAdapter AdapterAt(string directory)
    {
        byte[] source = File.ReadAllBytes(System.IO.Path.Combine(directory, SafeTensorsAdapterVerifier.SourceFileName));
        var id = new AdapterId("sha256:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(source)));
        var config = AdapterConfig.Parse(File.ReadAllText(System.IO.Path.Combine(directory, "adapter_config.json")));
        return new AdmittedAdapter(id, config, System.IO.Path.GetFileName(directory));
    }

    /// <summary>A temp copy of <paramref name="source"/>, deleted when disposed.</summary>
    public static TempDirectory Copy(string source)
    {
        var temp = new TempDirectory();
        foreach (string file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(temp.Path, System.IO.Path.GetFileName(file)));
        }

        return temp;
    }

    /// <summary>Rewrite, in place, the F32 values of every tensor whose name satisfies <paramref name="select"/>.</summary>
    public static void RewriteF32(string safetensorsPath, Func<string, bool> select, Func<float, float> transform)
    {
        byte[] bytes = File.ReadAllBytes(safetensorsPath);
        long headerLength = BitConverter.ToInt64(bytes, 0);
        long dataStart = 8 + headerLength;
        using var header = JsonDocument.Parse(Encoding.UTF8.GetString(bytes, 8, (int)headerLength));

        int rewritten = 0;
        foreach (JsonProperty tensor in header.RootElement.EnumerateObject())
        {
            if (tensor.Name == "__metadata__" || !select(tensor.Name))
            {
                continue;
            }

            Assert.Equal("F32", tensor.Value.GetProperty("dtype").GetString());
            long[] offsets = tensor.Value.GetProperty("data_offsets").EnumerateArray().Select(e => e.GetInt64()).ToArray();
            for (long at = dataStart + offsets[0]; at < dataStart + offsets[1]; at += 4)
            {
                float value = transform(BitConverter.ToSingle(bytes, (int)at));
                BitConverter.TryWriteBytes(bytes.AsSpan((int)at, 4), value);
            }

            rewritten++;
        }

        Assert.True(rewritten > 0, $"No tensor in {safetensorsPath} matched the selector.");
        File.WriteAllBytes(safetensorsPath, bytes);
    }
}

/// <summary>A uniquely named temp directory, deleted on dispose.</summary>
internal sealed class TempDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "lora-hotswap-tests-" + Guid.NewGuid().ToString("N"));

    public TempDirectory() => Directory.CreateDirectory(Path);

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
