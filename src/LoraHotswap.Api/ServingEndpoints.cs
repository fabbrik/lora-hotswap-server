using System.Diagnostics;
using System.Text.Json;
using LoraHotswap.Core;

namespace LoraHotswap.Api;

/// <summary>The body of <c>POST /v1/generate</c>. Fields are nullable so a missing one is a typed 400, not a crash.</summary>
/// <param name="Tenant">The tenant to serve.</param>
/// <param name="Prompt">The prompt to generate from.</param>
public sealed record GenerateRequest(string? Tenant, string? Prompt);

/// <summary>The wire shape of a served response; identities render as their string tokens.</summary>
/// <param name="Tenant">The tenant that was served.</param>
/// <param name="AdapterId">The adapter the runtime reported applied.</param>
/// <param name="AppliedAdapters">The exact applied adapter set (never empty).</param>
/// <param name="BindingStrategy">The active binding strategy (AD-9).</param>
/// <param name="Text">The generated text.</param>
public sealed record GenerateResponse(
    string Tenant,
    string AdapterId,
    IReadOnlyList<string> AppliedAdapters,
    string BindingStrategy,
    string Text);

/// <summary>The wire shape of one module's verification measurement.</summary>
public sealed record ModuleVerificationResponse(
    string Module,
    double ReferenceError,
    double ChangeFromBase,
    double Tolerance,
    bool MatchesReference,
    bool ChangedFromBase);

/// <summary>The wire shape of a running-instance verification (Story 1.9).</summary>
/// <param name="AdapterId">The verified adapter.</param>
/// <param name="Outcome"><c>match</c>, <c>mismatch</c>, or <c>inert</c>.</param>
/// <param name="Detail">A diagnostic explanation naming the adapter and any failing module.</param>
/// <param name="Modules">Per-module measurements.</param>
public sealed record VerificationResponse(
    string AdapterId,
    string Outcome,
    string Detail,
    IReadOnlyList<ModuleVerificationResponse> Modules);

/// <summary>
/// The HTTP surface. Every failure is an RFC 9457 problem whose <c>type</c> comes from
/// <see cref="ProblemTypes"/>, with one explicit status per type -- never inferred from
/// the URI's spelling. Mapped after admission, so no route exists before the registry does.
/// </summary>
public static class ServingEndpoints
{
    /// <summary>The span name for a running-instance verification.</summary>
    public const string VerificationSpanName = "adapter.verification";

    /// <summary>
    /// The HTTP status for each problem type a request can produce. Anything unlisted is a
    /// 500. Admission-only types (<c>missing-manifest</c>, <c>inert-adapter</c>) never reach
    /// HTTP: they fail startup.
    /// </summary>
    public static IReadOnlyDictionary<string, int> StatusCodesByType { get; } = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        [ProblemTypes.InvalidRequest] = StatusCodes.Status400BadRequest,
        [ProblemTypes.UnknownTenant] = StatusCodes.Status404NotFound,
        [ProblemTypes.UnknownAdapter] = StatusCodes.Status404NotFound,
        [ProblemTypes.HashMismatch] = StatusCodes.Status409Conflict,
        [ProblemTypes.MergeReferenceMissing] = StatusCodes.Status422UnprocessableEntity,
        [ProblemTypes.LeaseUnavailable] = StatusCodes.Status503ServiceUnavailable,
        [ProblemTypes.AdapterNotApplied] = StatusCodes.Status500InternalServerError,
        [ProblemTypes.GenerationFailed] = StatusCodes.Status500InternalServerError,
        [ProblemTypes.BaseModelMismatch] = StatusCodes.Status500InternalServerError,
        [ProblemTypes.InternalError] = StatusCodes.Status500InternalServerError,
    };

    /// <summary>Seconds a client should wait before retrying a <c>lease-unavailable</c> response.</summary>
    public const int LeaseRetryAfterSeconds = 1;

    /// <summary>Map <c>POST /v1/generate</c> and <c>POST /v1/adapters/{adapterId}/verify</c>.</summary>
    public static IEndpointRouteBuilder MapServingEndpoints(
        this IEndpointRouteBuilder routes,
        TenantServingService serving,
        AdapterRegistry registry,
        IAdapterVerifier verifier)
    {
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentNullException.ThrowIfNull(serving);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(verifier);

        ILogger logger = routes.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("LoraHotswap.Serving");

        routes.MapPost("/v1/generate", async (HttpContext http, CancellationToken token) =>
        {
            // Bound by hand, not by the framework: a malformed body must be a typed
            // invalid-request problem, never the framework's untyped 400/415.
            if (!http.Request.HasJsonContentType())
            {
                return Problem(http, RequestProblem.InvalidRequest("The request body must be JSON (Content-Type: application/json)."));
            }

            GenerateRequest? request;
            try
            {
                request = await http.Request.ReadFromJsonAsync<GenerateRequest>(token);
            }
            catch (JsonException)
            {
                return Problem(http, RequestProblem.InvalidRequest("The request body is not a valid generate request."));
            }

            if (request is null || string.IsNullOrWhiteSpace(request.Tenant))
            {
                return Problem(http, RequestProblem.InvalidRequest("The request must name a tenant."));
            }

            if (request.Prompt is null)
            {
                return Problem(http, RequestProblem.InvalidRequest("The request must carry a prompt."));
            }

            var tenant = new TenantId(request.Tenant);
            string adapterForLogs = registry.ResolveTenant(tenant)?.Id.Value ?? "none";
            try
            {
                TenantResponse response = await serving.ServeAsync(new TenantRequest(tenant, request.Prompt), token);
                return Results.Ok(new GenerateResponse(
                    response.Tenant.Value,
                    response.Adapter.Value,
                    response.AppliedAdapters.Select(a => a.Value).Order(StringComparer.Ordinal).ToArray(),
                    response.BindingStrategy,
                    response.Text));
            }
            catch (RequestProblemException ex)
            {
                logger.LogWarning(ex.InnerException,
                    "Request for tenant {TenantId} with adapter {AdapterId} failed closed: {ProblemType}",
                    tenant.Value, adapterForLogs, ex.Problem.Type);
                return Problem(http, ex.Problem);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
            {
                logger.LogError(ex, "Request for tenant {TenantId} with adapter {AdapterId} failed unexpectedly",
                    tenant.Value, adapterForLogs);
                return Problem(http, RequestProblem.InternalError());
            }
        });

        routes.MapPost("/v1/adapters/{adapterId}/verify", async (string adapterId, HttpContext http, CancellationToken token) =>
        {
            // Every verification request gets a span (Story 1.9), a malformed one included;
            // a malformed ID is not an identity, so the span says so rather than echoing it.
            using Activity? activity = LoraHotswapTelemetry.Source.StartActivity(VerificationSpanName, ActivityKind.Internal);
            if (!IsContentIdentity(adapterId))
            {
                activity?.SetTag("adapter.id", AdmissionStartup.UnidentifiedAdapter);
                activity?.SetTag("verification.outcome", "error");
                activity?.SetTag("verification.reason", ProblemTypes.InvalidRequest);
                activity?.SetStatus(ActivityStatusCode.Error, "Invalid request");
                return Problem(http, RequestProblem.InvalidRequest(
                    "The adapter identity must be 'sha256:' followed by 64 lowercase hex characters."));
            }

            var id = new AdapterId(adapterId);
            activity?.SetTag("adapter.id", id.Value);

            try
            {
                AdmittedAdapter adapter = registry.Find(id)
                    ?? throw new RequestProblemException(RequestProblem.UnknownAdapter(id));

                AdapterVerification verification = await verifier.VerifyAsync(adapter, token);
                string outcome = verification.Outcome.ToString().ToLowerInvariant();

                activity?.SetTag("verification.outcome", outcome);
                if (verification.Outcome != VerificationOutcome.Match)
                {
                    activity?.SetStatus(ActivityStatusCode.Error, verification.Detail);
                }

                return Results.Ok(new VerificationResponse(
                    id.Value,
                    outcome,
                    verification.Detail,
                    verification.Modules.Select(m => new ModuleVerificationResponse(
                        m.Module, m.ReferenceError, m.ChangeFromBase, m.Tolerance, m.MatchesReference, m.ChangedFromBase)).ToArray()));
            }
            catch (RequestProblemException ex)
            {
                activity?.SetTag("verification.outcome", "error");
                activity?.SetTag("verification.reason", ex.Problem.Type);
                activity?.SetStatus(ActivityStatusCode.Error, ex.Problem.Title);
                logger.LogWarning(ex.InnerException,
                    "Verification of adapter {AdapterId} failed: {ProblemType}", id.Value, ex.Problem.Type);
                return Problem(http, ex.Problem);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
            {
                activity?.SetTag("verification.outcome", "error");
                activity?.SetTag("verification.reason", ProblemTypes.InternalError);
                activity?.SetStatus(ActivityStatusCode.Error, ex.GetType().Name);
                logger.LogError(ex, "Verification of adapter {AdapterId} failed unexpectedly", id.Value);
                return Problem(http, RequestProblem.InternalError());
            }
        });

        return routes;
    }

    /// <summary>The shape admission produces: <c>sha256:</c> plus 64 lowercase hex characters.</summary>
    private static bool IsContentIdentity(string value) =>
        value.Length == "sha256:".Length + 64
        && value.StartsWith("sha256:", StringComparison.Ordinal)
        && !value.AsSpan("sha256:".Length).ContainsAnyExcept(LowerHex);

    private static readonly System.Buffers.SearchValues<char> LowerHex = System.Buffers.SearchValues.Create("0123456789abcdef");

    private static IResult Problem(HttpContext http, RequestProblem problem)
    {
        int status = StatusCodesByType.GetValueOrDefault(problem.Type, StatusCodes.Status500InternalServerError);
        if (problem.Type == ProblemTypes.LeaseUnavailable)
        {
            http.Response.Headers.RetryAfter = LeaseRetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return Results.Problem(detail: problem.Detail, statusCode: status, title: problem.Title, type: problem.Type);
    }
}
