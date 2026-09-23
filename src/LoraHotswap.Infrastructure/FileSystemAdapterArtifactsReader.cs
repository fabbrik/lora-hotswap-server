using System.Security.Cryptography;
using System.Text.Json;
using LoraHotswap.Core;
using LoraHotswap.Core.Ports;

namespace LoraHotswap.Infrastructure;

/// <summary>
/// The driven adapter for <see cref="IAdapterArtifactsReader"/>: reads one adapter
/// directory off disk into a pure <see cref="AdapterArtifacts"/>.
///
/// Provenance is recomputed, not trusted: the source safetensors and the GGUF are
/// hashed with a streaming SHA-256 over their raw file bytes (the .NET analogue of
/// <c>convert.py</c>'s <c>sha256_of</c>), never a re-hash of parsed tensors. The
/// manifest is parsed structurally (nullable when the file is absent), and the
/// config plus <c>lora_B</c> magnitudes come from Story 1.5's
/// <see cref="AdapterContentReader"/> -- reused, not re-implemented.
/// </summary>
public sealed class FileSystemAdapterArtifactsReader : IAdapterArtifactsReader
{
    /// <summary>
    /// The GGUF container admission re-hashes against the manifest's <c>ggufSha256</c>.
    /// Public because the serving runtime must load <i>this</i> file and no other: any other
    /// name is a container admission never hashed, which drops the provenance guarantee.
    /// </summary>
    public const string GgufFileName = "adapter.gguf";

    private const string ManifestFileName = "adapter.manifest.json";
    private const string SourceFileName = "adapter_model.safetensors";
    private const string ConfigFileName = "adapter_config.json";

    /// <inheritdoc/>
    public async Task<AdapterArtifacts> ReadAsync(string adapterDirectory, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(adapterDirectory);

        string name = new DirectoryInfo(adapterDirectory).Name;

        string manifestPath = Path.Combine(adapterDirectory, ManifestFileName);
        string sourcePath = Path.Combine(adapterDirectory, SourceFileName);
        string ggufPath = Path.Combine(adapterDirectory, GgufFileName);
        string configPath = Path.Combine(adapterDirectory, ConfigFileName);

        AdapterManifest? manifest = File.Exists(manifestPath)
            ? ParseManifest(await File.ReadAllTextAsync(manifestPath, cancellationToken))
            : null;

        string? sourceHash = File.Exists(sourcePath)
            ? await Sha256HexAsync(sourcePath, cancellationToken)
            : null;
        string? ggufHash = File.Exists(ggufPath)
            ? await Sha256HexAsync(ggufPath, cancellationToken)
            : null;

        AdapterConfigSnapshot? config = null;
        IReadOnlyList<double> loraBMaxAbs = [];

        // Reuse Story 1.5's reader for the config + lora_B magnitudes. It needs both
        // the config and the weights present; a directory missing either yields a
        // null snapshot, which admission surfaces as a base-model failure (after the
        // manifest and hash checks have already had their say).
        if (File.Exists(configPath) && File.Exists(sourcePath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            AdapterContent content = AdapterContentReader.Read(adapterDirectory);
            AdapterConfig parsed = content.Config;
            config = new AdapterConfigSnapshot(parsed.R, parsed.Alpha, parsed.UseRsLora, parsed.BaseModelName);
            loraBMaxAbs = content.LoraBMaxAbs;
        }

        return new AdapterArtifacts(name, manifest, sourceHash, ggufHash, config, loraBMaxAbs);
    }

    private static AdapterManifest ParseManifest(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        return new AdapterManifest(
            SourceSha256: ReadString(root, "sourceSha256"),
            GgufSha256: ReadString(root, "ggufSha256"),
            ConverterRevision: ReadString(root, "converterRevision"),
            AdapterId: ReadString(root, "adapterId"));
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    /// <summary>
    /// Streaming SHA-256 over a file's raw bytes, rendered as lowercase hex to match
    /// the conversion tool's <c>hexdigest()</c> exactly.
    /// </summary>
    private static async Task<string> Sha256HexAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1024 * 1024, useAsync: true);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexStringLower(hash);
    }
}
