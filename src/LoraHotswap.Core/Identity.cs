namespace LoraHotswap.Core;

/// <summary>
/// The stable, content-derived identity of an adapter: the <c>sha256:</c>-prefixed
/// SHA-256 of the source safetensors bytes, exactly as the conversion tool records
/// it in the manifest's <c>adapterId</c> field. A value type, never a bare string
/// in a signature, and never a filesystem path -- invariant across renames/moves.
/// </summary>
public readonly record struct AdapterId
{
    /// <summary>The raw <c>sha256:</c>-prefixed identity token.</summary>
    public string Value { get; }

    /// <summary>
    /// Wrap a manifest <c>adapterId</c>. Rejects empty input and any value not
    /// carrying the <c>sha256:</c> content-identity shape.
    /// </summary>
    public AdapterId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (!value.StartsWith("sha256:", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"AdapterId must be a 'sha256:'-prefixed content identity, got '{value}'.",
                nameof(value));
        }

        Value = value;
    }

    /// <inheritdoc/>
    public override string ToString() => Value;
}

/// <summary>
/// The identity of a configured tenant. A value type, never a bare string in a
/// signature. Tenants are declared in application settings and each must resolve
/// to an admitted <see cref="AdapterId"/> at startup.
/// </summary>
public readonly record struct TenantId
{
    /// <summary>The raw tenant name as declared in configuration.</summary>
    public string Value { get; }

    /// <summary>Wrap a configured tenant name. Rejects empty input.</summary>
    public TenantId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
    }

    /// <inheritdoc/>
    public override string ToString() => Value;
}
