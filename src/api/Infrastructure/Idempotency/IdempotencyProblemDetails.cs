using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Web.Api.Infrastructure.Idempotency;

/// <summary>
/// ProblemDetails factories for the persistent <c>Idempotency-Key</c> contract
/// (issue #715). All failure modes return <c>application/problem+json</c> with
/// a stable machine-readable <c>code</c> extension.
/// </summary>
public static class IdempotencyProblemDetails
{
    /// <summary>Well-known type URI so tooling can dedupe/route these errors.</summary>
    public const string TypeUri = "https://printfarmer.io/errors/idempotency";

    /// <summary>Code emitted when the client key fails the format check.</summary>
    public const string CodeMalformedKey = "idempotencyKeyMalformed";

    /// <summary>Code emitted when the same key is reused with different request bytes.</summary>
    public const string CodeHashConflict = "idempotencyKeyConflict";

    /// <summary>Code emitted when a prior request with the same key is still in-flight.</summary>
    public const string CodeInProgress = "idempotencyKeyInProgress";

    /// <summary>Code emitted when the request body is too large to buffer for hashing.</summary>
    public const string CodePayloadTooLarge = "idempotencyRequestTooLarge";

    /// <summary>
    /// Seconds a client should wait before retrying an in-progress key. Surfaced both
    /// as a <c>Retry-After</c> response header and a <c>retryAfterSeconds</c> ProblemDetails
    /// extension. Matches the store's <c>ProcessingStaleness</c> reclaim horizon so a
    /// backed-off retry lands after a wedged Processing row would be reclaimable.
    /// </summary>
    public const int RetryAfterSeconds = 5;

    /// <summary>
    /// <c>400 Bad Request</c>: the client supplied a malformed <c>Idempotency-Key</c>
    /// header (empty, too long, or contained non-printable/whitespace bytes).
    /// </summary>
    public static BadRequestObjectResult MalformedKey()
    {
        ProblemDetails problem = new()
        {
            Status = StatusCodes.Status400BadRequest,
            Title = "Malformed Idempotency-Key",
            Detail = "The Idempotency-Key header must be 1-200 printable ASCII characters with no whitespace.",
            Type = TypeUri,
        };
        problem.Extensions["code"] = CodeMalformedKey;
        return new BadRequestObjectResult(problem);
    }

    /// <summary>
    /// <c>409 Conflict</c>: the same key was previously used with a different
    /// request body. Clients that see this must either generate a fresh key for
    /// the new payload or resend the original bytes.
    /// </summary>
    public static ConflictObjectResult HashConflict()
    {
        ProblemDetails problem = new()
        {
            Status = StatusCodes.Status409Conflict,
            Title = "Idempotency-Key conflict",
            Detail = "This Idempotency-Key was previously used with a different request body. Use a new key or resend the original request.",
            Type = TypeUri,
        };
        problem.Extensions["code"] = CodeHashConflict;
        return new ConflictObjectResult(problem);
    }

    /// <summary>
    /// <c>409 Conflict</c>: a prior request with the same key is still in-flight.
    /// Clients should back off and retry once the original request completes.
    /// </summary>
    public static ConflictObjectResult InProgress()
    {
        ProblemDetails problem = new()
        {
            Status = StatusCodes.Status409Conflict,
            Title = "Idempotent request in progress",
            Detail = "A prior request with the same Idempotency-Key is still in progress. Retry after it completes.",
            Type = TypeUri,
        };
        problem.Extensions["code"] = CodeInProgress;
        problem.Extensions["retryAfterSeconds"] = RetryAfterSeconds;
        return new ConflictObjectResult(problem);
    }

    /// <summary>
    /// <c>413 Payload Too Large</c>: the request body exceeded the size the filter
    /// can buffer for hashing, so the idempotency contract cannot be honoured. The
    /// request is rejected rather than silently bypassing replay protection, which
    /// would let an oversized retry double-apply against the backend.
    /// </summary>
    public static ObjectResult PayloadTooLarge()
    {
        ProblemDetails problem = new()
        {
            Status = StatusCodes.Status413PayloadTooLarge,
            Title = "Request body too large for idempotent replay",
            Detail = $"The request body exceeds the {IdempotencyFilter.MaxBufferedRequestBytes}-byte limit for idempotent endpoints. Reduce the payload or omit the Idempotency-Key header.",
            Type = TypeUri,
        };
        problem.Extensions["code"] = CodePayloadTooLarge;
        return new ObjectResult(problem) { StatusCode = StatusCodes.Status413PayloadTooLarge };
    }
}
