namespace LoraHotswap.Core;

/// <summary>
/// An RFC 9457 problem raised while handling a request -- serving or running-instance
/// verification. The admission-time counterpart is <see cref="AdmissionProblem"/>;
/// both draw their <see cref="Type"/> from <see cref="ProblemTypes"/>.
///
/// Details name tenants and adapters by identity and never carry internal exception
/// text: the underlying fault travels as the <see cref="RequestProblemException"/>'s
/// inner exception, for logs only.
/// </summary>
/// <param name="Type">The RFC 9457 <c>type</c> URI -- distinct per failure mode.</param>
/// <param name="Title">A short, human-readable summary of the failure mode.</param>
/// <param name="Detail">A specific explanation naming the tenant and/or adapter.</param>
public sealed record RequestProblem(string Type, string Title, string Detail)
{
    /// <summary>The request is malformed.</summary>
    public static RequestProblem InvalidRequest(string reason) => new(
        ProblemTypes.InvalidRequest,
        "Invalid request",
        reason);

    /// <summary>The request names a tenant that is not configured. No default is substituted.</summary>
    public static RequestProblem UnknownTenant(TenantId tenant) => new(
        ProblemTypes.UnknownTenant,
        "Unknown tenant",
        $"Tenant '{tenant}' is not configured; no default tenant is substituted and no request was served.");

    /// <summary>The <see cref="AdapterId"/> names no admitted adapter.</summary>
    public static RequestProblem UnknownAdapter(AdapterId adapter) => new(
        ProblemTypes.UnknownAdapter,
        "Unknown adapter",
        $"Adapter '{adapter}' was never admitted by this instance.");

    /// <summary>The tenant's adapter declares a base model other than the one being served.</summary>
    public static RequestProblem BaseModelMismatch(TenantId tenant, AdmittedAdapter adapter, string configuredBase) => new(
        ProblemTypes.BaseModelMismatch,
        "Adapter base-model mismatch",
        $"Tenant '{tenant}' resolves to adapter '{adapter.Id}', which declares base model " +
        $"'{adapter.Config.BaseModelName}' but the served base model is '{configuredBase}'.");

    /// <summary>No context lease was acquired within the timeout; the request was not served.</summary>
    public static RequestProblem LeaseUnavailable(TenantId tenant, string strategy, TimeSpan timeout) => new(
        ProblemTypes.LeaseUnavailable,
        "Context lease unavailable",
        $"No {strategy} context could be leased for tenant '{tenant}' within {timeout.TotalMilliseconds:0} ms; " +
        "the request was not served with an unconfigured context.");

    /// <summary>The adapter was not applied, or the runtime reported a different applied set.</summary>
    public static RequestProblem AdapterNotApplied(TenantId tenant, AdapterId adapter, string reason) => new(
        ProblemTypes.AdapterNotApplied,
        "Adapter not applied",
        $"Adapter '{adapter}' was not applied for tenant '{tenant}': {reason}. No output was returned.");

    /// <summary>Generation failed after the adapter was applied.</summary>
    public static RequestProblem GenerationFailed(TenantId tenant, AdapterId adapter) => new(
        ProblemTypes.GenerationFailed,
        "Generation failed",
        $"Generation failed for tenant '{tenant}' with adapter '{adapter}'. No output was returned.");

    /// <summary>
    /// The prompt does not fit the context. The caller's own input, so the detail says what
    /// would fit -- a caller must be able to tell this apart from a broken runtime.
    /// </summary>
    public static RequestProblem PromptTooLong(TenantId tenant, int promptTokens, int contextTokens) => new(
        ProblemTypes.PromptTooLong,
        "Prompt too long",
        $"The prompt from tenant '{tenant}' is {promptTokens} tokens and the context holds {contextTokens}; " +
        "no output was returned. Send a shorter prompt or configure a larger context.");

    /// <summary>An admitted adapter has no merge reference to verify against.</summary>
    public static RequestProblem MergeReferenceMissing(AdapterId adapter) => new(
        ProblemTypes.MergeReferenceMissing,
        "Merge reference missing",
        $"Adapter '{adapter}' is admitted but has no Python PEFT merge reference, so its merge cannot be verified.");

    /// <summary>
    /// The adapter's source bytes on disk no longer hash to its admitted identity, so they
    /// are not the adapter that was admitted and are not verified under its identity.
    /// </summary>
    public static RequestProblem AdapterDrifted(AdapterId adapter, string onDisk) => new(
        ProblemTypes.HashMismatch,
        "Adapter changed since admission",
        $"Adapter '{adapter}' was admitted by content hash, but its source file on disk now hashes to " +
        $"'{onDisk}'. Restart to re-admit it.");

    /// <summary>
    /// A missing or wrong API key. Deliberately identical for every cause (no key, wrong key,
    /// unknown tenant) so the response reveals nothing about which tenants exist.
    /// </summary>
    public static RequestProblem Unauthorized() => new(
        ProblemTypes.Unauthorized,
        "Unauthorized",
        "A valid API key is required: send 'Authorization: Bearer <key>'.");

    /// <summary>An unexpected fault. Deliberately generic: internals stay in the logs.</summary>
    public static RequestProblem InternalError() => new(
        ProblemTypes.InternalError,
        "Internal error",
        "The request failed unexpectedly. No output was returned.");
}

/// <summary>Carries a <see cref="RequestProblem"/> out of the domain to the HTTP boundary.</summary>
public sealed class RequestProblemException : Exception
{
    /// <summary>Raise <paramref name="problem"/>, optionally preserving the fault that caused it.</summary>
    public RequestProblemException(RequestProblem problem, Exception? innerException = null)
        : base(problem.Detail, innerException)
    {
        Problem = problem;
    }

    /// <summary>The problem to render as RFC 9457 problem details.</summary>
    public RequestProblem Problem { get; }

    /// <summary>
    /// The stage span that already recorded this rejection, if any. An enclosing stage
    /// checks it so a single rejection is attributed to one stage, not to every stage it
    /// unwinds through (SM-5: one rejection, one check).
    /// </summary>
    public string? RecordedOnStage { get; private set; }

    /// <summary>The trace the rejection was recorded on, so the response can name it even with no ambient activity.</summary>
    public string? TraceId { get; private set; }

    /// <summary>Record that <paramref name="stage"/> attributed this rejection; the first stage wins.</summary>
    public void MarkRecordedOn(string stage, string? traceId)
    {
        RecordedOnStage ??= stage;
        TraceId ??= traceId;
    }
}
