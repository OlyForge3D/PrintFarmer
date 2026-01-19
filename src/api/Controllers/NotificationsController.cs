using System;
using System.Collections.Generic;
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
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<NotificationPreferencesDto>> GetPreferencesAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            Guid userId = GetUserIdFromClaims();

            NotificationPreferences? preferences = await notificationService.GetPreferencesAsync(userId, cancellationToken);
            return preferences == null
                ? NotFound(new { error = $"Preferences not found for user {userId}" })
                : Ok(new NotificationPreferencesDto
                {
                    UserId = preferences.UserId,
                    EnableEmailNotifications = preferences.EnableEmailNotifications,
                    EnablePushNotifications = preferences.EnablePushNotifications,
                    EnableInAppNotifications = preferences.EnableInAppNotifications,
                    NotifyOnCompletion = preferences.NotifyOnCompletion,
                    NotifyOnFailure = preferences.NotifyOnFailure,
                    NotifyOnStart = preferences.NotifyOnStart,
                    NotifyOnPause = preferences.NotifyOnPause,
                    Frequency = preferences.Frequency,
                    RetentionDays = preferences.RetentionDays
                });
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
                NotifyOnCompletion = request.NotifyOnCompletion,
                NotifyOnFailure = request.NotifyOnFailure,
                NotifyOnStart = request.NotifyOnStart,
                NotifyOnPause = request.NotifyOnPause,
                Frequency = request.Frequency,
                RetentionDays = request.RetentionDays ?? 30
            };

            await notificationService.UpdatePreferencesAsync(userId, preferences, cancellationToken);

            return Ok(new NotificationPreferencesDto
            {
                UserId = preferences.UserId,
                EnableEmailNotifications = preferences.EnableEmailNotifications,
                EnablePushNotifications = preferences.EnablePushNotifications,
                EnableInAppNotifications = preferences.EnableInAppNotifications,
                NotifyOnCompletion = preferences.NotifyOnCompletion,
                NotifyOnFailure = preferences.NotifyOnFailure,
                NotifyOnStart = preferences.NotifyOnStart,
                NotifyOnPause = preferences.NotifyOnPause,
                Frequency = preferences.Frequency,
                RetentionDays = preferences.RetentionDays
            });
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

    /// <summary>Notify on job completion</summary>
    public bool NotifyOnCompletion { get; set; } = true;

    /// <summary>Notify on job failure</summary>
    public bool NotifyOnFailure { get; set; } = true;

    /// <summary>Notify on job start</summary>
    public bool NotifyOnStart { get; set; } = false;

    /// <summary>Notify on job pause</summary>
    public bool NotifyOnPause { get; set; } = false;

    /// <summary>Notification frequency (RealTime, Hourly, Daily, Weekly, Never)</summary>
    public NotificationFrequency Frequency { get; set; } = NotificationFrequency.RealTime;

    /// <summary>Retention days for notification history</summary>
    public int? RetentionDays { get; set; } = 30;
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

    /// <summary>Notify on job completion</summary>
    public bool NotifyOnCompletion { get; set; }

    /// <summary>Notify on job failure</summary>
    public bool NotifyOnFailure { get; set; }

    /// <summary>Notify on job start</summary>
    public bool NotifyOnStart { get; set; }

    /// <summary>Notify on job pause</summary>
    public bool NotifyOnPause { get; set; }

    /// <summary>Notification frequency</summary>
    public NotificationFrequency Frequency { get; set; }

    /// <summary>Retention days for notification history</summary>
    public int RetentionDays { get; set; }
}
