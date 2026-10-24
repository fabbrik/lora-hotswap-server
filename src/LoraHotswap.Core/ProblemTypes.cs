namespace LoraHotswap.Core;

/// <summary>
/// The single catalog of RFC 9457 <c>type</c> URIs (NFR5). Every failure mode has
/// exactly one URI, all under one prefix, so a client matching on the prefix sees
/// admission and serving failures alike, and no two modes can be conflated.
///
/// The five AD-7 fail-closed modes are <see cref="UnknownTenant"/>,
/// <see cref="BaseModelMismatch"/>, <see cref="InertAdapter"/>,
/// <see cref="LeaseUnavailable"/>, and <see cref="AdapterNotApplied"/>.
/// </summary>
public static class ProblemTypes
{
    /// <summary>The stable prefix shared by every problem <c>type</c> URI.</summary>
    public const string Prefix = "https://lora-hotswap/problems/";

    // --- admission (startup) -------------------------------------------------

    /// <summary>An adapter directory has no provenance manifest.</summary>
    public const string MissingManifest = Prefix + "missing-manifest";

    /// <summary>
    /// A recomputed artifact hash disagrees with the manifest (admission), or an admitted
    /// adapter's source no longer hashes to its identity (verification).
    /// </summary>
    public const string HashMismatch = Prefix + "hash-mismatch";

    /// <summary>The declared base model does not strictly equal the configured one (AD-7).</summary>
    public const string BaseModelMismatch = Prefix + "base-model-mismatch";

    /// <summary>Every <c>lora_B</c> tensor is zero: the adapter is a silent no-op (AD-7).</summary>
    public const string InertAdapter = Prefix + "inert-adapter";

    /// <summary>An <see cref="AdapterId"/> names no admitted adapter.</summary>
    public const string UnknownAdapter = Prefix + "unknown-adapter";

    // --- serving and verification (request time) ------------------------------

    /// <summary>The request is malformed (missing tenant, prompt, or a malformed identity).</summary>
    public const string InvalidRequest = Prefix + "invalid-request";

    /// <summary>
    /// The request carries no valid API key for what it asks for. The same problem is returned
    /// whether the tenant exists or not, so a caller cannot use it to enumerate tenants.
    /// </summary>
    public const string Unauthorized = Prefix + "unauthorized";

    /// <summary>The request names a tenant that is not configured (AD-7).</summary>
    public const string UnknownTenant = Prefix + "unknown-tenant";

    /// <summary>No context lease could be acquired within the lease timeout (AD-7).</summary>
    public const string LeaseUnavailable = Prefix + "lease-unavailable";

    /// <summary>The tenant's adapter could not be applied, or the runtime did not report it applied (AD-7).</summary>
    public const string AdapterNotApplied = Prefix + "adapter-not-applied";

    /// <summary>Generation failed after the adapter was applied.</summary>
    public const string GenerationFailed = Prefix + "generation-failed";

    /// <summary>
    /// The prompt does not fit the runtime's context. Distinct from
    /// <see cref="GenerationFailed"/> on purpose: it is the caller's input, not a broken
    /// runtime, so it is a 4xx a caller can act on and cannot be used to manufacture 500s.
    /// </summary>
    public const string PromptTooLong = Prefix + "prompt-too-long";

    /// <summary>An admitted adapter has no Python PEFT merge reference to verify against.</summary>
    public const string MergeReferenceMissing = Prefix + "merge-reference-missing";

    /// <summary>An unexpected server fault; the detail never carries internal exception text.</summary>
    public const string InternalError = Prefix + "internal-error";
}
