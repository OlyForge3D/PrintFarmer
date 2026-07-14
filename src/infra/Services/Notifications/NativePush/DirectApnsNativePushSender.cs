using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Farm.Infrastructure.Services.Notifications.NativePush;

/// <summary>
/// Direct-mode sender. Signs an ES256 provider JWT locally and posts to
/// <c>api.push.apple.com</c>. Used only when the operator has explicitly signed their own
/// build with a self-issued .p8 key (never OlyForge3D's key). See
/// <c>docs/OPERATOR_NATIVE_PUSH.md</c>.
/// </summary>
public sealed class DirectApnsNativePushSender : INativePushSender, IDisposable
{
    /// <summary>Named HTTP client the direct sender resolves.</summary>
    public const string HttpClientName = "NativePushDirect";

    private const string ProductionHost = "https://api.push.apple.com";
    private const string SandboxHost = "https://api.sandbox.push.apple.com";

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

    /// <inheritdoc />
    public string ModeName => "direct";

    /// <inheritdoc />
    public async Task<NativePushDispatchResult> SendAsync(NativePushEnvelope envelope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);

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
            _logger.LogError(ex, "[NativePush/direct] Failed to sign provider JWT for attentionItemId={AttentionItemId}.", envelope.AttentionItemId);
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
        request.Headers.TryAddWithoutValidation("apns-push-type", "alert");
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "[NativePush/direct] Transient network failure sending envelope for attentionItemId={AttentionItemId}.", envelope.AttentionItemId);
            return NativePushDispatchResult.Transient("network");
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex, "[NativePush/direct] Timeout sending envelope for attentionItemId={AttentionItemId}.", envelope.AttentionItemId);
            return NativePushDispatchResult.Transient("timeout");
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
        var aps = new ApsRoot(
            new ApsAlert(envelope.Title, envelope.Subtitle, envelope.Body),
            envelope.Category,
            envelope.ThreadId,
            MutableContent: 1,
            envelope.Priority == NativePushPriority.Background ? 1 : (int?)null);
        var root = new ApsWireRoot(
            aps,
            envelope.AttentionItemId,
            AttentionAliasNames.ForKind(envelope.AttentionKind),
            AttentionAliasNames.ForChangeKind(envelope.ChangeKind),
            envelope.PrinterId,
            envelope.JobId,
            envelope.ToolheadIndex,
            envelope.DeepLink,
            envelope.ActionIds);
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

        ECDsa ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(pem);

        _cachedSigningKey?.Dispose();
        _cachedSigningKey = ecdsa;
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

    private sealed record ApsRoot(
        [property: JsonPropertyName("alert")] ApsAlert Alert,
        [property: JsonPropertyName("category")] string Category,
        [property: JsonPropertyName("thread-id")] string ThreadId,
        [property: JsonPropertyName("mutable-content")] int MutableContent,
        [property: JsonPropertyName("content-available")] int? ContentAvailable);

    private sealed record ApsWireRoot(
        [property: JsonPropertyName("aps")] ApsRoot Aps,
        [property: JsonPropertyName("attentionItemId")] string AttentionItemId,
        [property: JsonPropertyName("attentionKind")] string AttentionKind,
        [property: JsonPropertyName("changeKind")] string ChangeKind,
        [property: JsonPropertyName("printerId")] Guid PrinterId,
        [property: JsonPropertyName("jobId")] Guid? JobId,
        [property: JsonPropertyName("toolheadIndex")] int? ToolheadIndex,
        [property: JsonPropertyName("deepLink")] string DeepLink,
        [property: JsonPropertyName("actions")] IReadOnlyList<string> Actions);
}
