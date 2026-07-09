using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain.Notifications;
using Farm.Infrastructure.Services.Notifications;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// Controller for user notifications management (Phase 4.3)
/// Provides endpoints for retrieving, managing, and configuring notifications
/// </summary>
[ApiController]
[Route("api/notifications")]
[Produces("application/json")]
[Authorize]
public class NotificationsController(INotificationService notificationService) : ControllerBase
{
    /// <summary>
    /// Get all notifications for the current user with optional filtering and pagination
    /// </summary>
    /// <param name="limit">Maximum number of notifications to return (default: 50)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of user notifications</returns>
    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<NotificationDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<NotificationDto>>> GetNotificationsAsync(
        [FromQuery] int? limit = 50,
        CancellationToken cancellationToken = default)
    {
        try
        {
            Guid userId = GetUserIdFromClaims();
            IEnumerable<Notification> notifications = await notificationService.GetUserNotificationsAsync(userId, limit, cancellationToken);
            return Ok(notifications);
        }
        catch (InvalidOperationException)
        {
            return Unauthorized(new { error = "User ID not found in claims" });
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Get unread notifications for the current user
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of unread notifications</returns>
    [HttpGet("unread")]
    [ProducesResponseType(typeof(IEnumerable<NotificationDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<NotificationDto>>> GetUnreadNotificationsAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            Guid userId = GetUserIdFromClaims();
            IEnumerable<Notification> notifications = await notificationService.GetUserUnreadNotificationsAsync(userId, cancellationToken);
            return Ok(notifications);
        }
        catch (InvalidOperationException)
        {
            return Unauthorized(new { error = "User ID not found in claims" });
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Get the count of unread notifications for the current user
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Count of unread notifications</returns>
    [HttpGet("unread/count")]
    [ProducesResponseType(typeof(UnreadCountResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<UnreadCountResponse>> GetUnreadCountAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            Guid userId = GetUserIdFromClaims();
            int count = await notificationService.GetUnreadCountAsync(userId, cancellationToken);
            return Ok(new UnreadCountResponse { UnreadCount = count });
        }
        catch (InvalidOperationException)
        {
            return Unauthorized(new { error = "User ID not found in claims" });
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Mark a notification as read
    /// </summary>
    /// <param name="notificationId">The notification ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Success response</returns>
    [HttpPut("{notificationId:guid}/mark-read")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> MarkAsReadAsync(
        string notificationId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await notificationService.MarkAsReadAsync(notificationId, cancellationToken);
            return NoContent();
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Mark multiple notifications as read
    /// </summary>
    /// <param name="request">Request containing list of notification IDs</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Success response</returns>
    [HttpPut("mark-read-batch")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> MarkMultipleAsReadAsync(
        [FromBody] MarkMultipleAsReadRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (request?.NotificationIds == null || request.NotificationIds.Count == 0)
            {
                return BadRequest(new { error = "NotificationIds list cannot be empty" });
            }

            await notificationService.MarkMultipleAsReadAsync(request.NotificationIds, cancellationToken);
            return NoContent();
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Delete a notification
    /// </summary>
    /// <param name="notificationId">The notification ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Success response</returns>
    [HttpDelete("{notificationId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteNotificationAsync(
        string notificationId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await notificationService.DeleteAsync(notificationId, cancellationToken);
            return NoContent();
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Get notification preferences for the current user
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>User notification preferences</returns>
    [HttpGet("preferences")]
    [ProducesResponseType(typeof(NotificationPreferencesDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<NotificationPreferencesDto>> GetPreferencesAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            Guid userId = GetUserIdFromClaims();

            NotificationPreferences? preferences = await notificationService.GetPreferencesAsync(userId, cancellationToken);
            NotificationPreferences effective = preferences ?? CreateDefaultPreferences(userId);
            return Ok(ToDto(effective));
        }
        catch (InvalidOperationException)
        {
            return Unauthorized(new { error = "User ID not found in claims" });
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Update notification preferences for the current user.
    /// </summary>
    /// <param name="request">The preferences to update.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated notification preferences.</returns>
    [HttpPut("preferences")]
    [ProducesResponseType(typeof(NotificationPreferencesDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<NotificationPreferencesDto>> UpdatePreferencesAsync(
        [FromBody] UpdateNotificationPreferencesRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            Guid userId = GetUserIdFromClaims();

            if (request == null)
            {
                return BadRequest(new { error = "Request body cannot be empty" });
            }

            var preferences = new NotificationPreferences
            {
                UserId = userId,
                EnableEmailNotifications = request.EnableEmailNotifications,
                EnablePushNotifications = request.EnablePushNotifications,
                EnableInAppNotifications = request.EnableInAppNotifications,
                EnableTelegramNotifications = request.EnableTelegramNotifications,
                NotifyOnCompletion = request.NotifyOnCompletion,
                NotifyOnFailure = request.NotifyOnFailure,
                NotifyOnStart = request.NotifyOnStart,
                NotifyOnPause = request.NotifyOnPause,
                Frequency = request.Frequency,
                RetentionDays = request.RetentionDays ?? 30
            };

            ApplyEventChannelPreferences(preferences, request);
            await notificationService.UpdatePreferencesAsync(userId, preferences, cancellationToken);
            return Ok(ToDto(preferences));
        }
        catch (InvalidOperationException)
        {
            return Unauthorized(new { error = "User ID not found in claims" });
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Get the VAPID public key for push subscription enrollment.
    /// </summary>
    [HttpGet("push-subscription/vapid-key")]
    [ProducesResponseType(typeof(VapidKeyResponse), StatusCodes.Status200OK)]
    public ActionResult<VapidKeyResponse> GetVapidKey()
    {
        // TODO: Move to configuration once VAPID keys are generated for deployment
        string? publicKey = Environment.GetEnvironmentVariable("VAPID_PUBLIC_KEY");
        if (string.IsNullOrEmpty(publicKey))
        {
            return Ok(new VapidKeyResponse { PublicKey = string.Empty });
        }

        return Ok(new VapidKeyResponse { PublicKey = publicKey });
    }

    /// <summary>
    /// Subscribe to web push notifications. Stores the browser push subscription.
    /// </summary>
    /// <param name="request">The push subscription data from the browser.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpPost("push-subscription")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> SubscribePushAsync(
        [FromBody] PushSubscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            Guid userId = GetUserIdFromClaims();

            if (string.IsNullOrWhiteSpace(request?.Endpoint))
            {
                return BadRequest(new { error = "Endpoint is required" });
            }

            if (string.IsNullOrWhiteSpace(request.Keys?.P256dh) || string.IsNullOrWhiteSpace(request.Keys?.Auth))
            {
                return BadRequest(new { error = "Subscription keys p256dh and auth are required" });
            }

            await notificationService.SavePushSubscriptionAsync(userId, request.Endpoint, request.Keys.P256dh, request.Keys.Auth, cancellationToken);
            return NoContent();
        }
        catch (InvalidOperationException)
        {
            return Unauthorized(new { error = "User ID not found in claims" });
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Unsubscribe from web push notifications for the current device.
    /// </summary>
    /// <param name="request">The push subscription endpoint to remove.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpDelete("push-subscription")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UnsubscribePushAsync(
        [FromBody] UnsubscribePushRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            Guid userId = GetUserIdFromClaims();

            if (string.IsNullOrWhiteSpace(request?.Endpoint))
            {
                return BadRequest(new { error = "Endpoint is required" });
            }

            await notificationService.DeletePushSubscriptionAsync(userId, request.Endpoint, cancellationToken);
            return NoContent();
        }
        catch (InvalidOperationException)
        {
            return Unauthorized(new { error = "User ID not found in claims" });
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    private static NotificationPreferencesDto ToDto(NotificationPreferences preferences)
    {
        return new NotificationPreferencesDto
        {
            UserId = preferences.UserId,
            EnableEmailNotifications = preferences.EnableEmailNotifications,
            EnablePushNotifications = preferences.EnablePushNotifications,
            EnableInAppNotifications = preferences.EnableInAppNotifications,
            EnableTelegramNotifications = preferences.EnableTelegramNotifications,
            NotifyOnCompletion = preferences.NotifyOnCompletion,
            NotifyOnFailure = preferences.NotifyOnFailure,
            NotifyOnStart = preferences.NotifyOnStart,
            NotifyOnPause = preferences.NotifyOnPause,
            EventChannelPreferences = BuildEventChannelPreferences(preferences),
            Frequency = preferences.Frequency,
            RetentionDays = preferences.RetentionDays
        };
    }

    private static NotificationPreferences CreateDefaultPreferences(Guid userId)
    {
        var defaults = new NotificationPreferences
        {
            UserId = userId
        };
        ApplyEventChannelPreferences(defaults, new UpdateNotificationPreferencesRequest());
        return defaults;
    }

    private static List<NotificationEventChannelPreferenceDto> BuildEventChannelPreferences(NotificationPreferences preferences)
    {
        return new List<NotificationEventChannelPreferenceDto>
        {
            new()
            {
                EventType = NotificationPreferenceEventType.JobStarted,
                InApp = preferences.InAppOnJobStarted,
                Email = preferences.EmailOnJobStarted,
                Push = preferences.PushOnJobStarted,
                Telegram = preferences.TelegramOnJobStarted
            },
            new()
            {
                EventType = NotificationPreferenceEventType.JobCompleted,
                InApp = preferences.InAppOnJobCompleted,
                Email = preferences.EmailOnJobCompleted,
                Push = preferences.PushOnJobCompleted,
                Telegram = preferences.TelegramOnJobCompleted
            },
            new()
            {
                EventType = NotificationPreferenceEventType.JobFailed,
                InApp = true,
                Email = preferences.EmailOnJobFailed,
                Push = preferences.PushOnJobFailed,
                Telegram = preferences.TelegramOnJobFailed
            },
            new()
            {
                EventType = NotificationPreferenceEventType.JobPaused,
                InApp = preferences.InAppOnJobPaused,
                Email = preferences.EmailOnJobPaused,
                Push = preferences.PushOnJobPaused,
                Telegram = preferences.TelegramOnJobPaused
            }
        };
    }

    private static void ApplyEventChannelPreferences(NotificationPreferences preferences, UpdateNotificationPreferencesRequest request)
    {
        List<NotificationEventChannelPreferenceDto>? matrix = request.EventChannelPreferences;
        if (matrix is null || matrix.Count == 0)
        {
            preferences.InAppOnJobStarted = request.EnableInAppNotifications && request.NotifyOnStart;
            preferences.InAppOnJobCompleted = request.EnableInAppNotifications && request.NotifyOnCompletion;
            preferences.InAppOnJobFailed = true;
            preferences.InAppOnJobPaused = request.EnableInAppNotifications && request.NotifyOnPause;
            preferences.EmailOnJobStarted = request.EnableEmailNotifications && request.NotifyOnStart;
            preferences.EmailOnJobCompleted = request.EnableEmailNotifications && request.NotifyOnCompletion;
            preferences.EmailOnJobFailed = request.EnableEmailNotifications && request.NotifyOnFailure;
            preferences.EmailOnJobPaused = request.EnableEmailNotifications && request.NotifyOnPause;
            preferences.PushOnJobStarted = request.EnablePushNotifications && request.NotifyOnStart;
            preferences.PushOnJobCompleted = request.EnablePushNotifications && request.NotifyOnCompletion;
            preferences.PushOnJobFailed = request.EnablePushNotifications && request.NotifyOnFailure;
            preferences.PushOnJobPaused = request.EnablePushNotifications && request.NotifyOnPause;
            preferences.TelegramOnJobStarted = request.EnableTelegramNotifications && request.NotifyOnStart;
            preferences.TelegramOnJobCompleted = request.EnableTelegramNotifications && request.NotifyOnCompletion;
            preferences.TelegramOnJobFailed = request.EnableTelegramNotifications && request.NotifyOnFailure;
            preferences.TelegramOnJobPaused = request.EnableTelegramNotifications && request.NotifyOnPause;
            return;
        }

        preferences.InAppOnJobStarted = false;
        preferences.InAppOnJobCompleted = true;
        preferences.InAppOnJobFailed = true;
        preferences.InAppOnJobPaused = true;
        preferences.EmailOnJobStarted = false;
        preferences.EmailOnJobCompleted = true;
        preferences.EmailOnJobFailed = true;
        preferences.EmailOnJobPaused = true;
        preferences.PushOnJobStarted = false;
        preferences.PushOnJobCompleted = true;
        preferences.PushOnJobFailed = true;
        preferences.PushOnJobPaused = true;
        preferences.TelegramOnJobStarted = false;
        preferences.TelegramOnJobCompleted = false;
        preferences.TelegramOnJobFailed = false;
        preferences.TelegramOnJobPaused = false;

        foreach (NotificationEventChannelPreferenceDto item in matrix)
        {
            if (item is null)
            {
                continue;
            }

            switch (item.EventType)
            {
                case NotificationPreferenceEventType.JobStarted:
                    preferences.InAppOnJobStarted = item.InApp;
                    preferences.EmailOnJobStarted = item.Email;
                    preferences.PushOnJobStarted = item.Push;
                    preferences.TelegramOnJobStarted = item.Telegram;
                    break;
                case NotificationPreferenceEventType.JobCompleted:
                    preferences.InAppOnJobCompleted = item.InApp;
                    preferences.EmailOnJobCompleted = item.Email;
                    preferences.PushOnJobCompleted = item.Push;
                    preferences.TelegramOnJobCompleted = item.Telegram;
                    break;
                case NotificationPreferenceEventType.JobFailed:
                    preferences.InAppOnJobFailed = true;
                    preferences.EmailOnJobFailed = item.Email;
                    preferences.PushOnJobFailed = item.Push;
                    preferences.TelegramOnJobFailed = item.Telegram;
                    break;
                case NotificationPreferenceEventType.JobPaused:
                    preferences.InAppOnJobPaused = item.InApp;
                    preferences.EmailOnJobPaused = item.Email;
                    preferences.PushOnJobPaused = item.Push;
                    preferences.TelegramOnJobPaused = item.Telegram;
                    break;
            }
        }

        preferences.EnableInAppNotifications =
            preferences.InAppOnJobStarted
            || preferences.InAppOnJobCompleted
            || preferences.InAppOnJobFailed
            || preferences.InAppOnJobPaused;
        preferences.EnableEmailNotifications =
            preferences.EmailOnJobStarted
            || preferences.EmailOnJobCompleted
            || preferences.EmailOnJobFailed
            || preferences.EmailOnJobPaused;
        preferences.EnablePushNotifications =
            preferences.PushOnJobStarted
            || preferences.PushOnJobCompleted
            || preferences.PushOnJobFailed
            || preferences.PushOnJobPaused;
        preferences.EnableTelegramNotifications =
            preferences.TelegramOnJobStarted
            || preferences.TelegramOnJobCompleted
            || preferences.TelegramOnJobFailed
            || preferences.TelegramOnJobPaused;

        preferences.NotifyOnStart = preferences.InAppOnJobStarted || preferences.EmailOnJobStarted || preferences.PushOnJobStarted || preferences.TelegramOnJobStarted;
        preferences.NotifyOnCompletion = preferences.InAppOnJobCompleted || preferences.EmailOnJobCompleted || preferences.PushOnJobCompleted || preferences.TelegramOnJobCompleted;
        preferences.NotifyOnFailure = true;
        preferences.NotifyOnPause = preferences.InAppOnJobPaused || preferences.EmailOnJobPaused || preferences.PushOnJobPaused || preferences.TelegramOnJobPaused;
    }

    /// <summary>
    /// Helper: Extract user ID from JWT claims
    /// </summary>
    private Guid GetUserIdFromClaims()
    {
        string? userIdString = User?.FindFirst("sub")?.Value ??
               User?.FindFirst("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier")?.Value ??
               User?.FindFirst("oid")?.Value;

        return string.IsNullOrEmpty(userIdString) || !Guid.TryParse(userIdString, out Guid userId)
            ? throw new InvalidOperationException("User ID not found or invalid in claims")
            : userId;
    }
}

/// <summary>
/// Request model for marking multiple notifications as read
/// </summary>
public class MarkMultipleAsReadRequest
{
    /// <summary>List of notification IDs to mark as read</summary>
    public List<string> NotificationIds { get; set; } = new();
}

/// <summary>
/// Request model for updating notification preferences
/// </summary>
public class UpdateNotificationPreferencesRequest
{
    /// <summary>Enable email notifications</summary>
    public bool EnableEmailNotifications { get; set; } = true;

    /// <summary>Enable push notifications</summary>
    public bool EnablePushNotifications { get; set; } = true;

    /// <summary>Enable in-app notifications</summary>
    public bool EnableInAppNotifications { get; set; } = true;

    /// <summary>Enable Telegram notifications</summary>
    public bool EnableTelegramNotifications { get; set; } = false;

    /// <summary>Notify on job completion</summary>
    public bool NotifyOnCompletion { get; set; } = true;

    /// <summary>Notify on job failure</summary>
    public bool NotifyOnFailure { get; set; } = true;

    /// <summary>Notify on job start</summary>
    public bool NotifyOnStart { get; set; } = false;

    /// <summary>Notify on job pause</summary>
    public bool NotifyOnPause { get; set; } = true;

    /// <summary>Notification frequency (RealTime, Hourly, Daily, Weekly, Never)</summary>
    public NotificationFrequency Frequency { get; set; } = NotificationFrequency.RealTime;

    /// <summary>Retention days for notification history</summary>
    public int? RetentionDays { get; set; } = 30;

    /// <summary>Per-event by channel notification matrix.</summary>
    public List<NotificationEventChannelPreferenceDto>? EventChannelPreferences { get; set; }
}

/// <summary>
/// Response model for unread notification count
/// </summary>
public class UnreadCountResponse
{
    /// <summary>Number of unread notifications</summary>
    public int UnreadCount { get; set; }
}

/// <summary>
/// DTO for notification response
/// </summary>
public class NotificationDto
{
    /// <summary>Unique notification ID</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>User ID this notification belongs to</summary>
    public Guid UserId { get; set; }

    /// <summary>Associated job ID (optional)</summary>
    public Guid? JobId { get; set; }

    /// <summary>Notification type</summary>
    public NotificationType Type { get; set; }

    /// <summary>Notification subject/title</summary>
    public string Subject { get; set; } = string.Empty;

    /// <summary>Notification body/message</summary>
    public string Body { get; set; } = string.Empty;

    /// <summary>Whether the notification has been read</summary>
    public bool IsRead { get; set; }

    /// <summary>When the notification was created</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>When the notification was read (if applicable)</summary>
    public DateTime? ReadAt { get; set; }

    /// <summary>When the notification expires</summary>
    public DateTime? ExpiresAt { get; set; }
}

/// <summary>
/// DTO for notification preferences response
/// </summary>
public class NotificationPreferencesDto
{
    /// <summary>User ID</summary>
    public Guid UserId { get; set; }

    /// <summary>Enable email notifications</summary>
    public bool EnableEmailNotifications { get; set; }

    /// <summary>Enable push notifications</summary>
    public bool EnablePushNotifications { get; set; }

    /// <summary>Enable in-app notifications</summary>
    public bool EnableInAppNotifications { get; set; }

    /// <summary>Enable Telegram notifications</summary>
    public bool EnableTelegramNotifications { get; set; }

    /// <summary>Notify on job completion</summary>
    public bool NotifyOnCompletion { get; set; }

    /// <summary>Notify on job failure</summary>
    public bool NotifyOnFailure { get; set; }

    /// <summary>Notify on job start</summary>
    public bool NotifyOnStart { get; set; }

    /// <summary>Notify on job pause</summary>
    public bool NotifyOnPause { get; set; }

    /// <summary>Per-event by channel notification matrix.</summary>
    public List<NotificationEventChannelPreferenceDto> EventChannelPreferences { get; set; } = new();

    /// <summary>Notification frequency</summary>
    public NotificationFrequency Frequency { get; set; }

    /// <summary>Retention days for notification history</summary>
    public int RetentionDays { get; set; }
}

public enum NotificationPreferenceEventType
{
    JobStarted,
    JobCompleted,
    JobFailed,
    JobPaused
}

public class NotificationEventChannelPreferenceDto
{
    public NotificationPreferenceEventType EventType { get; set; }

    public bool InApp { get; set; }

    public bool Email { get; set; }

    public bool Push { get; set; }

    public bool Telegram { get; set; }
}

/// <summary>
/// VAPID public key response for push subscription enrollment
/// </summary>
public class VapidKeyResponse
{
    /// <summary>The VAPID public key (Base64url encoded)</summary>
    public string PublicKey { get; set; } = string.Empty;
}

/// <summary>
/// Request model for creating a push subscription
/// </summary>
public class PushSubscriptionRequest
{
    /// <summary>The push subscription endpoint URL</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>Subscription keys</summary>
    public PushSubscriptionKeys? Keys { get; set; }
}

/// <summary>
/// Push subscription cryptographic keys
/// </summary>
public class PushSubscriptionKeys
{
    /// <summary>The p256dh key (Base64url encoded)</summary>
    public string P256dh { get; set; } = string.Empty;

    /// <summary>The auth secret (Base64url encoded)</summary>
    public string Auth { get; set; } = string.Empty;
}

/// <summary>
/// Request model for unsubscribing a specific push subscription (device endpoint)
/// </summary>
public class UnsubscribePushRequest
{
    /// <summary>The push subscription endpoint URL to remove</summary>
    public string Endpoint { get; set; } = string.Empty;
}
