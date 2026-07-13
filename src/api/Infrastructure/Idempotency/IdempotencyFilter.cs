using System.Security.Claims;
using Farm.Infrastructure.Services.Idempotency;
using Farm.Infrastructure.Services.OperatorFeatures;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Farm.Web.Api.Infrastructure.Idempotency;

/// <summary>
/// Resource filter that implements the persistent <c>Idempotency-Key</c>
/// contract (issue #715) for endpoints decorated with
/// <see cref="IdempotentAttribute"/>.
///
/// <para>
/// Runs as an <see cref="IAsyncResourceFilter"/> — before model binding and
/// before the action executes — so it can (a) buffer the raw request body for
/// deterministic hashing regardless of controller shape, and (b) substitute a
/// short-circuit <see cref="IActionResult"/> when a stored replay applies.
/// </para>
///
/// <para>
/// Feature gate integration: when <c>offlineWriteReplayEnabled</c> (#725) is
/// off, the filter deliberately behaves as a no-op — the header is ignored, no
/// record is persisted, and the request executes directly online. This
/// preserves safe direct-online behavior and avoids destructive queue
/// assumptions. Existing queued rows on the server side are not touched by the
/// disable flip; they simply age out naturally under the retention window.
/// </para>
///
/// <para>
/// Ownership contract: the filter never wraps the response body for
/// unauthenticated requests, requests without the header, or bypassed requests.
/// Response-body substitution is scoped to a narrow try/finally around
/// <c>await next()</c> so a downstream exception cannot leak the swapped
/// <see cref="MemoryStream"/> back to the framework.
/// </para>
/// </summary>
public sealed class IdempotencyFilter : IAsyncResourceFilter
{
    /// <summary>Absolute upper bound on the request body size we will buffer for hashing.</summary>
    /// <remarks>
    /// All four gated routes accept small JSON payloads (well under 64 KB). We cap
    /// at 1 MiB defensively so a malformed or malicious client cannot force the
    /// filter to allocate arbitrary memory before the model binder rejects the body.
    /// Requests over the limit fall through to the pipeline without idempotency
    /// support (the action or model binder will still enforce its own size checks).
    /// </remarks>
    public const int MaxBufferedRequestBytes = 1 * 1024 * 1024;

    /// <summary>Header name written on replayed responses so clients can distinguish a replay from a fresh 200.</summary>
    public const string ReplayHeaderName = "Idempotent-Replay";

    private readonly IIdempotencyStore _store;
    private readonly IOperatorFeatureGate _featureGate;
    private readonly ILogger<IdempotencyFilter> _logger;

    /// <summary>Constructs the filter.</summary>
    public IdempotencyFilter(
        IIdempotencyStore store,
        IOperatorFeatureGate featureGate,
        ILogger<IdempotencyFilter> logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _featureGate = featureGate ?? throw new ArgumentNullException(nameof(featureGate));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        IdempotentAttribute? metadata = context.ActionDescriptor.EndpointMetadata
            .OfType<IdempotentAttribute>()
            .LastOrDefault();
        if (metadata is null)
        {
            // Not a decorated route — the filter should not have been invoked, but
            // defensively execute the pipeline normally if it was.
            _ = await next();
            return;
        }

        HttpContext http = context.HttpContext;
        CancellationToken ct = http.RequestAborted;

        // ---- Feature gate #725: disabled → direct-online, no persistence ----
        if (!_featureGate.IsEnabled(OperatorFeature.OfflineWriteReplay))
        {
            _ = await next();
            return;
        }

        // ---- Header presence & format ----
        if (!http.Request.Headers.TryGetValue(IdempotencyKeyUtilities.HeaderName, out Microsoft.Extensions.Primitives.StringValues headerValues)
            || headerValues.Count == 0
            || string.IsNullOrWhiteSpace(headerValues[0]))
        {
            // No header → not participating in the replay contract. Execute normally.
            _ = await next();
            return;
        }

        if (headerValues.Count > 1)
        {
            // Multi-valued Idempotency-Key is ambiguous; reject rather than pick one.
            context.Result = IdempotencyProblemDetails.MalformedKey();
            return;
        }

        string idempotencyKey = headerValues[0]!;
        if (!IdempotencyKeyUtilities.IsValidKey(idempotencyKey))
        {
            context.Result = IdempotencyProblemDetails.MalformedKey();
            return;
        }

        // ---- Authenticated user identity ----
        string? userId = ResolveUserId(http.User);
        if (string.IsNullOrEmpty(userId))
        {
            // The [Authorize] gate on the wrapped action will reject anonymous
            // callers with 401 anyway; execute the pipeline normally so the
            // framework returns its usual challenge response.
            _ = await next();
            return;
        }

        // ---- Buffer request body and hash it ----
        byte[]? bodyBytes = await BufferRequestBodyAsync(http, ct);
        if (bodyBytes is null)
        {
            // Body was too large to safely buffer; skip the replay contract so
            // the pipeline can return its normal too-large error. Do not persist.
            _logger.LogInformation(
                "Idempotency filter bypassed for route={RouteKey}: request body exceeded {Limit} bytes.",
                metadata.RouteKey,
                MaxBufferedRequestBytes);
            _ = await next();
            return;
        }

        string requestHash = IdempotencyKeyUtilities.ComputeRequestHash(metadata.RouteKey, bodyBytes);

        // ---- Store lookup / insert ----
        IdempotencyLookupResult lookup = await _store.TryBeginAsync(
            userId,
            metadata.RouteKey,
            idempotencyKey,
            requestHash,
            ct);

        switch (lookup.Outcome)
        {
            case IdempotencyLookupOutcome.HashConflict:
                context.Result = IdempotencyProblemDetails.HashConflict();
                return;

            case IdempotencyLookupOutcome.InProgress:
                context.Result = IdempotencyProblemDetails.InProgress();
                return;

            case IdempotencyLookupOutcome.ReplayCompleted when lookup.Record is not null:
                context.Result = new IdempotencyReplayResult(
                    lookup.Record.ResponseStatusCode ?? StatusCodes.Status200OK,
                    lookup.Record.ResponseContentType,
                    lookup.Record.ResponseBody ?? Array.Empty<byte>());
                return;

            case IdempotencyLookupOutcome.Bypassed:
                _ = await next();
                return;

            case IdempotencyLookupOutcome.Inserted when lookup.Record is not null:
                await ExecuteWithCaptureAsync(context, next, lookup.Record.Id, ct);
                return;

            default:
                // Defensive: should not happen; log and execute normally so the
                // request is not silently dropped.
                _logger.LogWarning(
                    "Unhandled idempotency lookup outcome {Outcome} for route={RouteKey}; executing without capture.",
                    lookup.Outcome,
                    metadata.RouteKey);
                _ = await next();
                return;
        }
    }

    /// <summary>
    /// Executes the remaining pipeline while buffering the response body so we
    /// can persist it verbatim for future replays. Uses a narrow try/finally to
    /// guarantee the original response stream is restored even under exception.
    /// </summary>
    private async Task ExecuteWithCaptureAsync(
        ResourceExecutingContext context,
        ResourceExecutionDelegate next,
        Guid recordId,
        CancellationToken ct)
    {
        HttpResponse response = context.HttpContext.Response;
        Stream originalBody = response.Body;

        await using MemoryStream buffer = new();
        response.Body = buffer;

        ResourceExecutedContext executed;
        try
        {
            executed = await next();
        }
        catch
        {
            // Restore the original stream and abandon the record so the client
            // can retry with the same key. The exception is re-thrown so the
            // framework's normal error handling still fires.
            response.Body = originalBody;
            await _store.AbandonProcessingAsync(recordId, CancellationToken.None);
            throw;
        }

        // Restore the original body BEFORE we perform any I/O onto it, so any
        // downstream code that inspects Response.Body sees the real stream.
        response.Body = originalBody;

        int statusCode = response.StatusCode;
        bool isServerError = statusCode >= 500;
        bool hadException = executed.Exception is not null && !executed.ExceptionHandled;

        // Always flush what the action wrote to the real response so the client
        // sees an unchanged first-response body.
        buffer.Position = 0;
        if (buffer.Length > 0)
        {
            await buffer.CopyToAsync(originalBody, ct);
        }

        if (hadException || isServerError)
        {
            // Do not persist a failed mutation as a replayable result — the client
            // must be free to retry the same key against a healed backend.
            await _store.AbandonProcessingAsync(recordId, CancellationToken.None);
            return;
        }

        byte[] captured = buffer.ToArray();
        string? contentType = response.ContentType;
        await _store.CompleteAsync(recordId, statusCode, contentType, captured, CancellationToken.None);
    }

    /// <summary>
    /// Buffers the request body into memory (up to <see cref="MaxBufferedRequestBytes"/>)
    /// and rewinds the stream so downstream model binding sees an untouched view.
    /// Returns <c>null</c> when the body exceeds the limit.
    /// </summary>
    private static async Task<byte[]?> BufferRequestBodyAsync(HttpContext http, CancellationToken ct)
    {
        // Enable buffering: subsequent reads will replay the same bytes.
        http.Request.EnableBuffering(bufferThreshold: 64 * 1024, bufferLimit: MaxBufferedRequestBytes);

        // If Content-Length is present and over the limit, short-circuit.
        long? contentLength = http.Request.ContentLength;
        if (contentLength is > MaxBufferedRequestBytes)
        {
            return null;
        }

        Stream body = http.Request.Body;

        // Some hosts leave the request stream unrewindable when the client sends
        // no body — fall through with an empty payload in that case.
        if (body.CanSeek)
        {
            body.Position = 0;
        }

        await using MemoryStream mem = new();
        byte[] chunk = new byte[8192];
        int total = 0;
        while (true)
        {
            int read = await body.ReadAsync(chunk.AsMemory(0, chunk.Length), ct);
            if (read <= 0)
            {
                break;
            }

            total += read;
            if (total > MaxBufferedRequestBytes)
            {
                return null;
            }

            await mem.WriteAsync(chunk.AsMemory(0, read), ct);
        }

        if (body.CanSeek)
        {
            body.Position = 0;
        }

        return mem.ToArray();
    }

    /// <summary>
    /// Resolves the authenticated user identifier for the current request.
    /// Prefers the standard NameIdentifier claim; falls back to <c>sub</c> and
    /// <c>oid</c> for parity with existing controllers in this codebase.
    /// </summary>
    internal static string? ResolveUserId(ClaimsPrincipal? user)
    {
        if (user is null)
        {
            return null;
        }

        string? id = user.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? user.FindFirstValue("sub")
            ?? user.FindFirstValue("oid");
        return string.IsNullOrWhiteSpace(id) ? null : id;
    }
}

/// <summary>
/// <see cref="IActionResult"/> that writes a stored idempotent replay body to the
/// current response and stamps the well-known <c>Idempotent-Replay</c> header.
/// Kept internal to the filter because callers should never manually construct
/// a replay outside of the store lookup path.
/// </summary>
internal sealed class IdempotencyReplayResult(int statusCode, string? contentType, byte[] body) : IActionResult
{
    /// <inheritdoc />
    public async Task ExecuteResultAsync(ActionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        HttpResponse response = context.HttpContext.Response;
        response.StatusCode = statusCode;
        if (!string.IsNullOrEmpty(contentType))
        {
            response.ContentType = contentType;
        }

        response.Headers[IdempotencyFilter.ReplayHeaderName] = "true";

        // Suppress compressor / chunker size-hint mismatches by advertising the
        // exact captured payload length.
        IHttpResponseFeature? feature = context.HttpContext.Features.Get<IHttpResponseFeature>();
        if (feature is not null && !feature.HasStarted)
        {
            response.ContentLength = body.Length;
        }

        if (body.Length > 0)
        {
            await response.Body.WriteAsync(body, context.HttpContext.RequestAborted);
        }
    }
}
