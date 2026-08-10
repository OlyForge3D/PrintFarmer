using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Farm.Infrastructure.Domain.Notifications;
using Farm.Infrastructure.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Farm.Infrastructure.Services.Notifications.NativePush;

/// <summary>
/// Direct-mode sender. Signs an ES256 provider JWT locally and posts to
/// <c>api.push.apple.com</c>. Used only when the operator has explicitly signed their own
/// build with a self-issued .p8 key (never OlyForge3D's key). See
/// <c>docs/OPERATOR_NATIVE_PUSH.md</c>.
/// </summary>
public sealed class DirectApnsNativePushSender : INativePushTransportSender, IDisposable
{
    /// <summary>Named HTTP client the direct sender resolves.</summary>
    public const string HttpClientName = "NativePushDirect";

    private const string ProductionHost = "https://api.push.apple.com";
    private const string SandboxHost = "https://api.sandbox.push.apple.com";
    private static readonly EventId JwtSignFailedEvent = new(70801, "NativePushJwtSignFailed");

    // Provider JWTs must be rotated ≤60m per Apple; we rotate at 50m for headroom.
    private static readonly TimeSpan JwtLifetime = TimeSpan.FromMinutes(50);

    private static readonly JsonSerializerOptions ApsOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptionsMonitor<NativePushSettings> _optionsMonitor;
    private readonly ILogger<DirectApnsNativePushSender> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _jwtLock = new(1, 1);

    private string? _cachedJwt;
    private DateTime _cachedJwtExpiresAt = DateTime.MinValue;
    private ECDsa? _cachedSigningKey;
    private string? _cachedKeyId;

    /// <summary>Constructs the sender.</summary>
    /// <param name="httpClientFactory">Named-client factory used to resolve the direct APNs channel.</param>
    /// <param name="optionsMonitor">Runtime settings snapshot for direct mode (team, key, bundle, .p8).</param>
    /// <param name="logger">Sink for non-fatal diagnostics; PEM material and endpoints are never logged.</param>
    /// <param name="timeProvider">Clock abstraction for JWT <c>iat</c> and expiry math. Optional; defaults to <see cref="TimeProvider.System"/>. Tests inject a fake to force deterministic signing timestamps without wall-clock waits.</param>
    public DirectApnsNativePushSender(
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<NativePushSettings> optionsMonitor,
        ILogger<DirectApnsNativePushSender> logger,
        TimeProvider? timeProvider = null)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _optionsMonitor = optionsMonitor ?? throw new ArgumentNullException(nameof(optionsMonitor));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    // -----------------------------------------------------------------------
    // Internal test seams (Hicks #7 deterministic APNs concurrency proofs).
    //
    // These are gated behind `InternalsVisibleTo("Farm.Web.Api.Tests")` and are
    // never touched by production DI. They exist so tests can:
    //   (a) hold the JWT semaphore externally to force a deterministic wait on
    //       InvalidateJwtCacheAsync's WaitAsync (proving cancellation-safety
    //       and no synchronous-Wait / ThreadPool starvation), and
    //   (b) observe the moment just before WaitAsync starts so a bounded
    //       cancellation window can be established without timing-only sleeps
    //       or reflection.
    // No public API is affected.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Test-only view of the JWT critical-section semaphore. Set by tests to
    /// deterministically hold the lock while a concurrent send races into
    /// <see cref="InvalidateJwtCacheAsync"/>'s <see cref="SemaphoreSlim.WaitAsync(CancellationToken)"/>.
    /// </summary>
    internal SemaphoreSlim JwtLockForTests => _jwtLock;

    /// <summary>
    /// Test-only hook fired inside <see cref="InvalidateJwtCacheAsync"/>
    /// immediately before <c>_jwtLock.WaitAsync(cancellationToken)</c>. Tests
    /// use it to signal "about to acquire" so they can drive cancellation
    /// deterministically without wall-clock sleeps. Always <c>null</c> in
    /// production.
    /// </summary>
    internal Func<CancellationToken, Task>? OnBeforeInvalidateWaitAsyncForTests { get; set; }

    /// <summary>
    /// Test-only factory used to prove that a replacement key is disposed when PEM import
    /// fails. Production always uses <see cref="ECDsa.Create()"/>.
    /// </summary>
    internal Func<ECDsa> SigningKeyFactoryForTests { get; set; } = static () => ECDsa.Create();

    /// <inheritdoc />
    public string ModeName => "direct";

    /// <inheritdoc />
    public Task<NativePushDispatchResult> SendAsync(
        NativePushEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        return SendAsync(
            envelope,
            AlwaysPermittedNativePushTransportStart.Instance,
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<NativePushDispatchResult> SendAsync(
        NativePushEnvelope envelope,
        INativePushTransportStart transportStart,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(transportStart);

        NativePushSettings settings = _optionsMonitor.CurrentValue;
        NativePushApnsSettings apns = settings.Apns;
        if (!TryValidateSettings(apns, out string? reason))
        {
            _logger.LogWarning(
                "[NativePush/direct] Skipping send for attentionItemId={AttentionItemId} — configuration incomplete: {Reason}.",
                envelope.AttentionItemId,
                reason);
            return NativePushDispatchResult.NotConfigured();
        }

        // Issue #1407: the origin server identity is routing metadata, never a secret. A
        // missing/non-canonical value must never be silently substituted (e.g. with a
        // different active server's id) — refuse the send entirely so an unattributable
        // payload can never leave this process.
        if (!NativePushRegistrationContract.IsCanonicalOriginServerId(envelope.OriginServerId))
        {
            _logger.LogError(
                "[NativePush/direct] Refusing to send attentionItemId={AttentionItemId} — originServerId is missing or non-canonical.",
                LogSanitizer.Sanitize(envelope.AttentionItemId));
            return NativePushDispatchResult.Terminal("invalid_origin_server_id");
        }

        string bundleId = apns.BundleId!;
        string environment = string.IsNullOrWhiteSpace(envelope.Environment) ? apns.Environment : envelope.Environment;
        string host = string.Equals(environment, "development", StringComparison.OrdinalIgnoreCase) ? SandboxHost : ProductionHost;

        string jwt;
        try
        {
            jwt = await GetOrRefreshJwtAsync(apns, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // File-system and PEM exceptions routinely embed the absolute key path or key
            // material in Message/ToString. Never attach the exception to the log event and
            // never include credential-bearing settings in structured state. A fixed category
            // retains enough operational signal without disclosing the .p8 path, key ids,
            // provider token, or PEM contents.
            _logger.LogError(
                JwtSignFailedEvent,
                "[NativePush/direct] Provider JWT signing failed; category={FailureCategory}.",
                ClassifySigningFailure(ex));
            return NativePushDispatchResult.Terminal("jwt_sign_failed");
        }

        string payload = BuildApsPayload(envelope);
        HttpClient client = _httpClientFactory.CreateClient(HttpClientName);

        // APNs REQUIRES HTTP/2 (RFC 7540). HttpClient defaults to 1.1 with
        // RequestVersionOrLower; both must be set or the request will downgrade.
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{host}/3/device/{envelope.Token}")
        {
            Version = System.Net.HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrHigher,
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("bearer", jwt);
        request.Headers.TryAddWithoutValidation("apns-topic", bundleId);

        // Hicks post-merge #1: a silent Resolved push (no user-visible alert)
        // MUST advertise `apns-push-type: background` — APNs otherwise rejects
        // priority 5 combined with a missing alert dict. Alert pushes retain
        // the "alert" type. We select on Priority so the sender never
        // second-guesses the dispatcher's intent.
        request.Headers.TryAddWithoutValidation(
            "apns-push-type",
            envelope.Priority == NativePushPriority.Background ? "background" : "alert");
        request.Headers.TryAddWithoutValidation(
            "apns-priority",
            envelope.Priority == NativePushPriority.Background ? "5" : "10");
        if (envelope.ExpiresAtUtc is DateTime expires)
        {
            long unix = new DateTimeOffset(expires, TimeSpan.Zero).ToUnixTimeSeconds();
            request.Headers.TryAddWithoutValidation(
                "apns-expiration",
                unix.ToString(CultureInfo.InvariantCulture));
        }

        // JWT acquisition and request construction are pre-transport work. The
        // dispatcher decides whether this still-current lifecycle may cross into
        // APNs only at the final boundary immediately below.
        //
        // Hicks blocker 2: the cached-JWT fast path in GetOrRefreshJwtAsync
        // returns without ever awaiting the JWT lock or observing
        // cancellationToken, so a token cancelled after that return (but
        // before this call) would otherwise reach TryStartAsync() unchecked and
        // could commit dispatcher-owned lifecycle/dedupe/rate state and
        // increment Attempted for an attempt that never reaches APNs. The
        // dispatcher's own TryStartAsync() implementation also guards against
        // this independently (defense in depth) but must never be the ONLY
        // check.
        //
        // The transport-start handshake is now async so the dispatcher can perform
        // its persisted feature-gate re-check outside every in-memory lock (Hicks r2
        // blocker 2). The awaited call is cancellation-aware: a caller cancel that
        // arrives while the gate read is in flight vetoes with rollback rather
        // than blocking a thread-pool worker on the DB round-trip.
        cancellationToken.ThrowIfCancellationRequested();
        NativePushTransportStartDecision decision = await transportStart
            .TryStartAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!decision.IsPermitted)
        {
            return NativePushDispatchResult.TransportStartVetoed();
        }

        // HttpClient.Timeout and caller/shutdown cancellation both surface as
        // OperationCanceledException. The caller token is authoritative: when it
        // is signaled, cancellation propagates unchanged. An OCE with an
        // unsignaled caller token is the named client's internal timeout and is a
        // transient provider failure eligible for dispatcher retry.
        try
        {
            using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);
            int status = (int)response.StatusCode;
            if (status is >= 200 and < 300)
            {
                return NativePushDispatchResult.Delivered();
            }

            // APNs returns 410 with reason "Unregistered" or 400 with reason
            // "BadDeviceToken" for permanent invalidation.
            string? apnsReason = await TryReadApnsReasonAsync(response, cancellationToken);
            if (status == 410
                || string.Equals(apnsReason, "BadDeviceToken", StringComparison.OrdinalIgnoreCase)
                || string.Equals(apnsReason, "Unregistered", StringComparison.OrdinalIgnoreCase))
            {
                return NativePushDispatchResult.Invalidated(apnsReason ?? "http_410");
            }

            if (status is 408 or 429 or >= 500)
            {
                return NativePushDispatchResult.Transient(apnsReason ?? $"http_{status}");
            }

            // 403 InvalidProviderToken means the cached JWT was rejected. Must
            // invalidate the cache so the next attempt re-signs. Without this the
            // sender loops until natural JWT expiry (up to ~55 min).
            if (string.Equals(apnsReason, "InvalidProviderToken", StringComparison.OrdinalIgnoreCase))
            {
                await InvalidateJwtCacheAsync(jwt, cancellationToken);
                return NativePushDispatchResult.Transient("invalid_provider_token");
            }

            if (string.Equals(apnsReason, "ExpiredProviderToken", StringComparison.OrdinalIgnoreCase))
            {
                await InvalidateJwtCacheAsync(jwt, cancellationToken);
                return NativePushDispatchResult.Transient("expired_provider_token");
            }

            return NativePushDispatchResult.Terminal(apnsReason ?? $"http_{status}");
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                ex,
                "[NativePush/direct] HTTP request timed out for attentionItemId={AttentionItemId}.",
                envelope.AttentionItemId);
            return NativePushDispatchResult.Transient("timeout");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "[NativePush/direct] Transient network failure sending envelope for attentionItemId={AttentionItemId}.", envelope.AttentionItemId);
            return NativePushDispatchResult.Transient("network");
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _cachedSigningKey?.Dispose();
        _jwtLock.Dispose();
    }

    private static bool TryValidateSettings(NativePushApnsSettings apns, out string? reason)
    {
        if (string.IsNullOrWhiteSpace(apns.TeamId))
        {
            reason = "teamId";
            return false;
        }

        if (string.IsNullOrWhiteSpace(apns.KeyId))
        {
            reason = "keyId";
            return false;
        }

        if (string.IsNullOrWhiteSpace(apns.BundleId))
        {
            reason = "bundleId";
            return false;
        }

        if (string.IsNullOrWhiteSpace(apns.P8KeyPem) && string.IsNullOrWhiteSpace(apns.P8KeyPath))
        {
            reason = "p8Key";
            return false;
        }

        reason = null;
        return true;
    }

    private static string BuildApsPayload(NativePushEnvelope envelope)
    {
        // Wire shape: standard APS root + typed custom keys the mobile app reads to route
        // the tap / action to the correct in-app destination. Deep link is the fallback for
        // out-of-band launches; category drives which registered action buttons render.
        //
        // Hicks post-merge #1: a Background push (silent Resolved) MUST omit
        // the alert dict entirely — the client SDK on iOS treats presence of
        // `alert` on an apns-push-type=background push as a validation
        // failure. We emit `content-available: 1` only so iOS delivers the
        // payload to the app in the background and the client can silently
        // dismiss its cached copy.
        object aps = envelope.Priority == NativePushPriority.Background
            ? new ApsBackground(ContentAvailable: 1)
            : new ApsAlertRoot(
                new ApsAlert(envelope.Title, envelope.Subtitle, envelope.Body),
                Sound: "default",
                Badge: 1,
                envelope.Category,
                envelope.ThreadId,
                MutableContent: 1);
        var root = new ApsWireRoot(
            aps,
            envelope.AttentionItemId,
            AttentionAliasNames.ForKind(envelope.AttentionKind),
            AttentionAliasNames.ForChangeKind(envelope.ChangeKind),
            envelope.PrinterId,
            envelope.JobId,
            envelope.ToolheadIndex,
            envelope.DeepLink,
            envelope.ActionIds,
            envelope.OriginServerId);
        return JsonSerializer.Serialize(root, ApsOptions);
    }

    private static async Task<string?> TryReadApnsReasonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(body))
            {
                return null;
            }

            using JsonDocument doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("reason", out JsonElement el) ? el.GetString() : null;
        }
        catch (OperationCanceledException)
        {
            // Do NOT swallow cancellation — the caller's cancellationToken has
            // tripped and the dispatch pipeline must observe it (fire-and-forget
            // fan-out cancels when the app is shutting down).
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static string ClassifySigningFailure(Exception exception)
    {
        return exception switch
        {
            FileNotFoundException or DirectoryNotFoundException => "key_file_missing",
            UnauthorizedAccessException or IOException => "key_file_unreadable",
            CryptographicException or ArgumentException or FormatException => "key_material_invalid",
            _ => "signing_failed",
        };
    }

    private async Task<string> GetOrRefreshJwtAsync(NativePushApnsSettings apns, CancellationToken cancellationToken)
    {
        DateTime nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        if (_cachedJwt is not null && nowUtc < _cachedJwtExpiresAt && string.Equals(_cachedKeyId, apns.KeyId, StringComparison.Ordinal))
        {
            return _cachedJwt;
        }

        await _jwtLock.WaitAsync(cancellationToken);
        try
        {
            if (_cachedJwt is not null && nowUtc < _cachedJwtExpiresAt && string.Equals(_cachedKeyId, apns.KeyId, StringComparison.Ordinal))
            {
                return _cachedJwt;
            }

            EnsureSigningKey(apns);

            long iat = new DateTimeOffset(nowUtc, TimeSpan.Zero).ToUnixTimeSeconds();
            string headerJson = $"{{\"alg\":\"ES256\",\"kid\":\"{apns.KeyId}\",\"typ\":\"JWT\"}}";
            string payloadJson = $"{{\"iss\":\"{apns.TeamId}\",\"iat\":{iat.ToString(CultureInfo.InvariantCulture)}}}";
            string header = Base64UrlEncode(Encoding.UTF8.GetBytes(headerJson));
            string payload = Base64UrlEncode(Encoding.UTF8.GetBytes(payloadJson));
            string signingInput = $"{header}.{payload}";
            byte[] sig = _cachedSigningKey!.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256);
            _cachedJwt = $"{signingInput}.{Base64UrlEncode(sig)}";
            _cachedJwtExpiresAt = nowUtc.Add(JwtLifetime);
            _cachedKeyId = apns.KeyId;
            return _cachedJwt;
        }
        finally
        {
            _ = _jwtLock.Release();
        }
    }

    private void EnsureSigningKey(NativePushApnsSettings apns)
    {
        if (_cachedSigningKey is not null && string.Equals(_cachedKeyId, apns.KeyId, StringComparison.Ordinal))
        {
            return;
        }

        string pem = !string.IsNullOrWhiteSpace(apns.P8KeyPem)
            ? apns.P8KeyPem!
            : File.ReadAllText(apns.P8KeyPath!);

        ECDsa? replacement = SigningKeyFactoryForTests();
        try
        {
            replacement.ImportFromPem(pem);

            ECDsa? previous = _cachedSigningKey;
            _cachedSigningKey = replacement;
            replacement = null;
            previous?.Dispose();
        }
        finally
        {
            replacement?.Dispose();
        }
    }

    private async Task InvalidateJwtCacheAsync(string failedJwt, CancellationToken cancellationToken)
    {
        // Vasquez v6 B2: never call SemaphoreSlim.Wait() from an async send
        // path — under a burst of InvalidProviderToken responses that
        // synchronously blocks a ThreadPool thread per concurrent send and can
        // deadlock the runtime under load. WaitAsync respects the caller's
        // cancellation token, so a shutdown signal aborts the wait cleanly
        // without leaving the semaphore leaked (semaphore is not entered on
        // cancellation).

        // Hicks #7 seam: invoke the pre-wait hook if a test has installed one.
        // Fires BEFORE WaitAsync so a test can deterministically observe "about
        // to enter the wait" and drive cancellation. The hook must not itself
        // deadlock; production hook is always null.
        Func<CancellationToken, Task>? hook = OnBeforeInvalidateWaitAsyncForTests;
        if (hook is not null)
        {
            await hook(cancellationToken).ConfigureAwait(false);
        }

        await _jwtLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Compare-and-clear: only invalidate if the still-cached JWT is the one
            // that failed. A concurrent successful refresh must not be clobbered.
            if (string.Equals(_cachedJwt, failedJwt, StringComparison.Ordinal))
            {
                _cachedJwt = null;
                _cachedJwtExpiresAt = DateTime.MinValue;
            }
        }
        finally
        {
            _ = _jwtLock.Release();
        }
    }

    private static string Base64UrlEncode(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed record ApsAlert(
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("subtitle")] string? Subtitle,
        [property: JsonPropertyName("body")] string? Body);

    private sealed record ApsAlertRoot(
        [property: JsonPropertyName("alert")] ApsAlert Alert,
        [property: JsonPropertyName("sound")] string Sound,
        [property: JsonPropertyName("badge")] int Badge,
        [property: JsonPropertyName("category")] string Category,
        [property: JsonPropertyName("thread-id")] string ThreadId,
        [property: JsonPropertyName("mutable-content")] int MutableContent);

    private sealed record ApsBackground(
        [property: JsonPropertyName("content-available")] int ContentAvailable);

    private sealed record ApsWireRoot(
        [property: JsonPropertyName("aps")] object Aps,
        [property: JsonPropertyName("attentionItemId")] string AttentionItemId,
        [property: JsonPropertyName("attentionKind")] string AttentionKind,
        [property: JsonPropertyName("changeKind")] string ChangeKind,
        [property: JsonPropertyName("printerId")] Guid PrinterId,
        [property: JsonPropertyName("jobId")] Guid? JobId,
        [property: JsonPropertyName("toolheadIndex")] int? ToolheadIndex,
        [property: JsonPropertyName("deepLink")] string DeepLink,
        [property: JsonPropertyName("actions")] IReadOnlyList<string> Actions,
        [property: JsonPropertyName("originServerId")] string OriginServerId);
}
