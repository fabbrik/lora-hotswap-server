namespace LoraHotswap.Core.Ports;

/// <summary>
/// Driven port: reads everything admission needs about one adapter directory --
/// the parsed manifest (nullable when absent), the recomputed SHA-256 of the
/// source safetensors and the GGUF, the config snapshot, and the reduced
/// <c>lora_B</c> magnitudes -- and hands it back as a pure <see cref="AdapterArtifacts"/>.
///
/// The port lives in the innermost <c>Core.Ports</c> layer and references nothing
/// outside it; the filesystem read and the streaming hash live in the
/// infrastructure implementation, keeping Core free of I/O. Async with a
/// <see cref="CancellationToken"/> per NFR6.
/// </summary>
public interface IAdapterArtifactsReader
{
    /// <summary>Read the adapter directory at <paramref name="adapterDirectory"/>.</summary>
    Task<AdapterArtifacts> ReadAsync(string adapterDirectory, CancellationToken cancellationToken);
}
