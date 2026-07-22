using Farm.Infrastructure.Domain.Notifications;

namespace Farm.Infrastructure.Services.Notifications;

/// <summary>
/// Pluggable notification channel contract for outbound channels that share notification routing.
/// </summary>
public interface INotificationChannel
{
    /// <summary>The delivery channel handled by this implementation.</summary>
    NotificationDeliveryChannel Channel { get; }

    /// <summary>Sends a routed notification through the channel.</summary>
    Task<NotificationChannelDispatchResult> SendAsync(
        NotificationChannelMessage message,
        CancellationToken cancellationToken);
}

/// <summary>Notification payload shared by pluggable outbound channels.</summary>
public sealed record NotificationChannelMessage(
    NotificationType Type,
    string Subject,
    string Body,
    Guid? JobId = null,
    Guid? PrinterId = null);

/// <summary>Result of sending through a pluggable notification channel.</summary>
public sealed record NotificationChannelDispatchResult(bool Success, string? Error = null)
{
    public static NotificationChannelDispatchResult Succeeded { get; } = new(true);
}
