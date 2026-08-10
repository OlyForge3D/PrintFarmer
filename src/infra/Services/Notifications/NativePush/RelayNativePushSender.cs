using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Farm.Infrastructure.Domain.Notifications;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Farm.Infrastructure.Services.Notifications.NativePush;

/// <summary>
/// Relay-mode sender. Forwards typed envelopes to the OlyForge3D-hosted relay over HTTPS
/// using a per-install bearer token. The relay owns the APNs provider key; the local
/// backend never holds it. See <c>docs/OPERATOR_NATIVE_PUSH.md</c>.
/// </summary>
public sealed class RelayNativePushSender(
    IHttpClientFactory httpClientFactory,
    IOptionsMonitor<NativePushSettings> optionsMonitor,
    ILogger<RelayNativePushSender> logger) : INativePushTransportSender
{
    /// <summary>Named HTTP client the relay sender resolves.</summary>
    public const string HttpClientName = "NativePushRelay";

    private static readonly JsonSerializerOptions PayloadOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
    private readonly IOptionsMonitor<NativePushSettings> _optionsMonitor = optionsMonitor ?? throw new ArgumentNullException(nameof(optionsMonitor));
    private readonly ILogger<RelayNativePushSender> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    public string ModeName => "relay";

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
        NativePushRelaySettings relay = settings.Relay;
        if (string.IsNullOrWhiteSpace(relay.Endpoint) || string.IsNullOrWhiteSpace(relay.ApiKey))
        {
            _logger.LogWarning(
                "[NativePush/relay] Skipping send for attentionItemId={AttentionItemId} — relay endpoint or api key is not configured.",
                envelope.AttentionItemId);
            return NativePushDispatchResult.NotConfigured();
        }

        // Issue #1407: the relay request must carry the same origin server identity as the
        // direct path. Never substitute a different/blank value — refuse the send instead.
        if (!NativePushRegistrationContract.IsCanonicalOriginServerId(envelope.OriginServerId))
        {
            _logger.LogError(
                "[NativePush/relay] Refusing to send attentionItemId={AttentionItemId} — originServerId is missing or non-canonical.",
                envelope.AttentionItemId);
            return NativePushDispatchResult.Terminal("invalid_origin_server_id");
        }

        var body = new RelayDispatchRequest(
            InstallationId: relay.InstallationId,
            Token: envelope.Token,
            Platform: envelope.Platform,
            Environment: envelope.Environment,
            BundleId: envelope.AppBundleId,
            Category: envelope.Category,
            ThreadId: envelope.ThreadId,
            Title: envelope.Title,
            Subtitle: envelope.Subtitle,
            Body: envelope.Body,
            AttentionItemId: envelope.AttentionItemId,
            AttentionKind: AttentionAliasNames.ForKind(envelope.AttentionKind),
            ChangeKind: AttentionAliasNames.ForChangeKind(envelope.ChangeKind),
            PrinterId: envelope.PrinterId,
            JobId: envelope.JobId,
            ToolheadIndex: envelope.ToolheadIndex,
            DeepLink: envelope.DeepLink,
            Priority: envelope.Priority == NativePushPriority.Background ? 5 : 10,
            ExpiresAtUtc: envelope.ExpiresAtUtc,
            ActionIds: envelope.ActionIds,
            OriginServerId: envelope.OriginServerId);

        HttpClient client = _httpClientFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Post, relay.Endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", relay.ApiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(body, PayloadOptions), Encoding.UTF8, "application/json");

        // Serialization and request construction are pre-transport work. The
        // dispatcher can still veto this exact lifecycle immediately before the
        // relay HTTP request starts.
        //
        // Hicks blocker 2: check cancellation explicitly, immediately before
        // crossing the transport-start boundary. Without this, a token that
        // was already cancelled after preparation completed (but before this
        // call) would still reach TryStartAsync() and could commit dispatcher-owned
        // lifecycle/dedupe/rate state and increment Attempted for an attempt
        // that will never actually reach the network. The dispatcher's own
        // TryStartAsync() implementation also guards against this independently
        // (defense in depth) but must never be the ONLY check.
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

            if (status == 410)
            {
                return NativePushDispatchResult.Invalidated($"http_{status}");
            }

            // 408 request timeout, 429 rate limit, and 5xx are transient — never
            // treat them as terminal or invalidating: a misconfigured/rate-limited
            // relay must not delete the entire device-token fleet.
            if (status is 408 or 429 or >= 500 and < 600)
            {
                return NativePushDispatchResult.Transient($"http_{status}");
            }

            // All other 4xx (including 404) are terminal for this attempt but
            // must NOT invalidate the device token — a bare 404 typically means
            // the relay path is misrouted, not that the device token is dead.
            if (status is >= 400 and < 500)
            {
                return NativePushDispatchResult.Terminal($"http_{status}");
            }

            return NativePushDispatchResult.Transient($"http_{status}");
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                ex,
                "[NativePush/relay] HTTP request timed out for attentionItemId={AttentionItemId}.",
                envelope.AttentionItemId);
            return NativePushDispatchResult.Transient("timeout");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "[NativePush/relay] Transient network failure sending envelope for attentionItemId={AttentionItemId}.", envelope.AttentionItemId);
            return NativePushDispatchResult.Transient("network");
        }
    }

    private sealed record RelayDispatchRequest(
        string? InstallationId,
        string Token,
        string Platform,
        string Environment,
        string? BundleId,
        string Category,
        string ThreadId,
        string? Title,
        string? Subtitle,
        string? Body,
        string AttentionItemId,
        string AttentionKind,
        string ChangeKind,
        Guid PrinterId,
        Guid? JobId,
        int? ToolheadIndex,
        string DeepLink,
        int Priority,
        DateTime? ExpiresAtUtc,
        IReadOnlyList<string> ActionIds,
        string OriginServerId);
}
