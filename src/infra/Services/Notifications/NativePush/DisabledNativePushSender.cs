using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.Notifications.NativePush;

/// <summary>
/// Default no-op sender used when <see cref="NativePushSettings.Mode"/> is
/// <see cref="NativePushMode.Disabled"/> (or when the chosen mode's configuration is
/// incomplete). Returns <see cref="NativePushDispatchResult.NotConfigured"/> so the
/// delivery service can account for the skip.
/// </summary>
public sealed class DisabledNativePushSender(ILogger<DisabledNativePushSender> logger) : INativePushTransportSender
{
    private readonly ILogger<DisabledNativePushSender> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    public string ModeName => "disabled";

    /// <inheritdoc />
    public Task<NativePushDispatchResult> SendAsync(NativePushEnvelope envelope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        _logger.LogDebug(
            "[NativePush/disabled] Skipping send for attentionItemId={AttentionItemId} deviceTokenId={DeviceTokenId} — sender is disabled.",
            envelope.AttentionItemId,
            envelope.DeviceTokenId);
        return Task.FromResult(NativePushDispatchResult.NotConfigured());
    }

    /// <inheritdoc />
    public Task<NativePushDispatchResult> SendAsync(
        NativePushEnvelope envelope,
        INativePushTransportStart transportStart,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transportStart);
        return SendAsync(envelope, cancellationToken);
    }
}
