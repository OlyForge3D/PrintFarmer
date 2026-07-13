using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    ILogger<RelayNativePushSender> logger) : INativePushSender
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
    public async Task<NativePushDispatchResult> SendAsync(NativePushEnvelope envelope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        NativePushSettings settings = _optionsMonitor.CurrentValue;
        NativePushRelaySettings relay = settings.Relay;
        if (string.IsNullOrWhiteSpace(relay.Endpoint) || string.IsNullOrWhiteSpace(relay.ApiKey))
        {
            _logger.LogWarning(
                "[NativePush/relay] Skipping send for attentionItemId={AttentionItemId} — relay endpoint or api key is not configured.",
                envelope.AttentionItemId);
            return NativePushDispatchResult.NotConfigured();
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
            ActionIds: envelope.ActionIds);

        HttpClient client = _httpClientFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Post, relay.Endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", relay.ApiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(body, PayloadOptions), Encoding.UTF8, "application/json");

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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "[NativePush/relay] Transient network failure sending envelope for attentionItemId={AttentionItemId}.", envelope.AttentionItemId);
            return NativePushDispatchResult.Transient("network");
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex, "[NativePush/relay] Timeout sending envelope for attentionItemId={AttentionItemId}.", envelope.AttentionItemId);
            return NativePushDispatchResult.Transient("timeout");
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
        IReadOnlyList<string> ActionIds);
}
