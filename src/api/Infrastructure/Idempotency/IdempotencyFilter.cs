using System.Globalization;
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
/// Response-body substitution is scoped to <c>await next()</c>; both the action
/// call and the entire post-<c>next()</c> region (restore, flush, record) are
/// guarded so a downstream exception restores the original stream and abandons
/// the Processing row rather than leaking the swapped <see cref="MemoryStream"/>
/// or wedging the key as in-progress.
/// </para>
/// </summary>
public sealed class IdempotencyFilter : IAsyncResourceFilter
{
    /// <summary>Absolute upper bound on the request body size we will buffer for hashing.</summary>
    /// <remarks>
    /// All four gated routes accept small JSON payloads (well under 64 KB). We cap
    /// at 1 MiB defensively so a malformed or malicious client cannot force the
    /// filter to allocate arbitrary memory before the model binder rejects the body.
    /// A request over the limit that also carries an <c>Idempotency-Key</c> is
    /// rejected with <c>413 Payload Too Large</c> (see
    /// <see cref="IdempotencyProblemDetails.PayloadTooLarge"/>) rather than silently
    /// bypassing the replay contract — a silent bypass would let an oversized retry
    /// double-apply against the backend.
    /// </remarks>
    public const int MaxBufferedRequestBytes = 1 * 1024 * 1024;

    /// <summary>Header name written on replayed responses so clients can distinguish a replay from a fresh 200.</summary>
    public const string ReplayHeaderName = "Idempotent-Replay";

    /// <summary>
    /// <see cref="HttpContext.Items"/> key under which the filter stashes a synthesized
    /// <c>operationKey</c> for the parts-adjust route (issue #715, Hicks r2 blocker 2).
    /// The controller reads it as a fallback when the client omitted the body
    /// <c>operationKey</c>, guaranteeing the domain's natural idempotency always backstops
    /// the filter's Processing-row retention semantics.
    /// </summary>
    public const string SynthesizedOperationKeyItemKey = "Farm.Web.Api.Idempotency.SynthesizedOperationKey";

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
            // Body exceeded the buffering limit, so we cannot compute a stable request
            // hash. Reject with 413 rather than silently bypassing replay protection
            // (Bishop #NB1): a silent bypass would let an oversized retry slip past the
            // idempotency guard and double-apply. No record is persisted.
            _logger.LogInformation(
                "Idempotency filter rejected route={RouteKey}: request body exceeded {Limit} bytes.",
                metadata.RouteKey,
                MaxBufferedRequestBytes);
            context.Result = IdempotencyProblemDetails.PayloadTooLarge();
            return;
        }

        // Fold the *resolved* request identity into the idempotency identity so that a
        // single client key reused across different {id}/{sku}/{toolheadIndex} values cannot
        // silently replay one resource's response for another (or, for empty-body actions
        // like TaskComplete, silently drop the second mutation). The route-key constant is a
        // static template shared by every id, so on its own it is only a prefix.
        //
        // For parts-adjust specifically (Hicks r2 blocker 1) the discriminator must be the
        // *normalized* SKU rather than the raw path: the domain resolves the target entity by
        // case-insensitive SKU, so /abc/adjust and /ABC/adjust are the same resource and must
        // share one idempotency record — otherwise a same-key retry with different SKU casing
        // double-applies the delta. BuildEffectiveIdentity centralizes that per-route rule.
        string resolvedPath = http.Request.Path.HasValue ? http.Request.Path.Value! : string.Empty;
        string? partsInventorySku = context.RouteData.Values.TryGetValue("sku", out object? skuRouteValue)
            ? skuRouteValue as string
            : null;
        string effectiveRouteKey = IdempotencyRouteKeys.BuildEffectiveIdentity(
            metadata.RouteKey,
            resolvedPath,
            partsInventorySku);

        string requestHash = IdempotencyKeyUtilities.ComputeRequestHash(effectiveRouteKey, bodyBytes);

        // ---- Store lookup / insert ----
        IdempotencyLookupResult lookup = await _store.TryBeginAsync(
            userId,
            effectiveRouteKey,
            idempotencyKey,
            requestHash,
            ct);

        switch (lookup.Outcome)
        {
            case IdempotencyLookupOutcome.HashConflict:
                context.Result = IdempotencyProblemDetails.HashConflict();
                return;

            case IdempotencyLookupOutcome.InProgress:
                // Advise the client to back off before retrying. RetryAfterSeconds is a small,
                // fixed client-friendly backoff hint (see IdempotencyProblemDetails) — it is a
                // politeness signal, deliberately NOT aligned to the multi-minute
                // ProcessingStaleness reclaim horizon. A retry that lands before the wedged row
                // is reclaimable simply gets another 409 InProgress, which is safe.
                http.Response.Headers.RetryAfter = IdempotencyProblemDetails.RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
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
                // Parts-adjust only (Hicks r2 blocker 2): if this first-execution is going to
                // run the mutation, hand the controller a deterministic operationKey derived
                // from the idempotency identity so the domain's (PartInventoryId, OperationKey)
                // uniqueness backstops us. Without this, a client that omits operationKey and
                // suffers a post-mutation flush failure could, after the Processing row is
                // reclaimed, retry the same header key and double-apply the delta — the store's
                // retention alone cannot prevent that once the row is gone. Only set the ambient
                // value; the controller falls back to it only when the body omitted the key.
                if (string.Equals(metadata.RouteKey, IdempotencyRouteKeys.PartsInventoryAdjust, StringComparison.Ordinal))
                {
                    http.Items[SynthesizedOperationKeyItemKey] =
                        IdempotencyKeyUtilities.ComputeSynthesizedOperationKey(userId, effectiveRouteKey, idempotencyKey);
                }

                await ExecuteWithCaptureAsync(context, next, lookup.Record.Id);
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
    /// Executes the remaining pipeline while buffering the response body so we can
    /// persist it verbatim for future replays. Failure handling is deliberately
    /// asymmetric around the mutation boundary (Hicks H-1/H-2):
    /// <list type="bullet">
    /// <item><description><b>Before/at the mutation:</b> if <c>next()</c> throws, or the
    /// action surfaces a 5xx / handled exception, the mutation has not been recorded and
    /// controllers run their own transactions — so we <b>abandon</b> the Processing row and
    /// let the client retry with the same key.</description></item>
    /// <item><description><b>After a successful mutation:</b> if flushing the buffered
    /// response or writing the completion record throws, the mutation has already been
    /// applied. Abandoning here would delete the Processing row and let a retry re-execute
    /// the already-applied write with no replay protection (the double-execution window). We
    /// therefore <b>do not abandon</b>: the Processing row is left in place so retries get
    /// <c>409 InProgress</c> until the staleness reclaim frees it, and the exception is
    /// rethrown so the failure still surfaces.</description></item>
    /// </list>
    /// </summary>
    private async Task ExecuteWithCaptureAsync(
        ResourceExecutingContext context,
        ResourceExecutionDelegate next,
        Guid recordId)
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
            // The mutation pipeline threw before recording an outcome. It is safe (and
            // necessary) to abandon the Processing row so a retry with the same key can
            // re-execute — controllers wrap their own writes in a transaction, so a
            // partially-applied mutation has already rolled back. Restore the real stream
            // and rethrow so the framework's normal error handling still fires.
            response.Body = originalBody;
            await _store.AbandonProcessingAsync(recordId, CancellationToken.None);
            throw;
        }

        // Restore the original body BEFORE any I/O onto it so downstream code that
        // inspects Response.Body sees the real stream.
        response.Body = originalBody;

        int statusCode = response.StatusCode;
        bool isServerError = statusCode >= 500;
        bool hadException = executed.Exception is not null && !executed.ExceptionHandled;

        if (hadException || isServerError)
        {
            // The mutation itself failed (5xx or a handled exception). Do not persist a
            // failed mutation as a replayable result, and abandon so the client can retry
            // the same key against a healed backend. Flush the action's bytes first so the
            // client still sees the original error body; abandon regardless of flush
            // outcome because a failed mutation is always safe to re-execute.
            //
            // Correctness assumption (Bishop r2 NB): a 5xx WITHOUT a surfaced exception is
            // treated as "mutation did not commit," which holds only because every gated
            // controller wraps its write in a single transaction that rolls back before it
            // returns a 5xx. If a future gated route returned 5xx *after* committing without
            // throwing, abandoning here would reopen the double-execution window — such a
            // route must either throw on failure or supply its own operationKey.
            try
            {
                await FlushBufferedBodyAsync(buffer, originalBody);
            }
            finally
            {
                await _store.AbandonProcessingAsync(recordId, CancellationToken.None);
            }

            return;
        }

        // The mutation SUCCEEDED. Any failure from here on is post-mutation: we must NOT
        // abandon (that would reopen the double-execution window, Hicks H-1/H-2). Leave the
        // Processing row so retries return 409 InProgress until the staleness reclaim frees
        // them, and let the exception propagate so the failure surfaces to the caller.
        await FlushBufferedBodyAsync(buffer, originalBody);

        byte[] captured = buffer.ToArray();
        string? contentType = response.ContentType;
        await _store.CompleteAsync(recordId, statusCode, contentType, captured, CancellationToken.None);
    }

    /// <summary>
    /// Flushes the buffered response bytes to the real response stream. Uses
    /// <see cref="CancellationToken.None"/> deliberately: a client disconnect must not
    /// abort the persistence bookkeeping in the caller and strand the Processing row.
    /// </summary>
    private static async Task FlushBufferedBodyAsync(MemoryStream buffer, Stream originalBody)
    {
        buffer.Position = 0;
        if (buffer.Length > 0)
        {
            await buffer.CopyToAsync(originalBody, CancellationToken.None);
        }
    }

    /// <summary>
    /// Buffers the request body into memory (up to <see cref="MaxBufferedRequestBytes"/>)
    /// and rewinds the stream so downstream model binding sees an untouched view.
    /// Returns <c>null</c> when the body exceeds the limit so the caller can emit a
    /// clean 413 instead of silently bypassing the replay contract.
    /// </summary>
    private static async Task<byte[]?> BufferRequestBodyAsync(HttpContext http, CancellationToken ct)
    {
        // Enable buffering with headroom above MaxBufferedRequestBytes so our own size
        // check (below) trips and returns null *before* EnableBuffering's internal spool
        // limit is reached. Without headroom the FileBufferingReadStream throws an
        // IOException at exactly the limit before our manual check can run (Bishop #NB1);
        // we still catch that IOException below as a belt-and-braces fallback.
        http.Request.EnableBuffering(
            bufferThreshold: 64 * 1024,
            bufferLimit: MaxBufferedRequestBytes + (64 * 1024));

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
        try
        {
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
        }
        catch (IOException)
        {
            // The buffering spool exceeded its hard limit before our own size check
            // tripped (e.g. a chunked upload with no Content-Length). Treat it as
            // "too large" so the caller emits a clean 413 rather than a 500.
            return null;
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
/// <remarks>
/// TODO (#715 non-blocking): replay fidelity currently restores status, content
/// type, and body only. Response headers that carried resource identity on the
/// original 201 — notably <c>Location</c> and <c>ETag</c> — are not captured or
/// replayed. Persist and re-emit them for full byte-for-byte replay parity.
/// </remarks>
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
