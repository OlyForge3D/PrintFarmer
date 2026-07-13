using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain.Notifications;
using Farm.Infrastructure.Dtos.Attention;
using Farm.Infrastructure.Repositories.Notifications;
using Farm.Infrastructure.Services.Notifications;
using Farm.Infrastructure.Services.Notifications.NativePush;
using Farm.Infrastructure.Services.OperatorFeatures;
using Farm.Web.Api.Infrastructure.OperatorFeatures;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

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
    /// Cached JSON options used by <see cref="GetPreferencesCapabilities"/>
    /// to round-trip enum values through the SAME converter production
    /// controllers use. Production JSON is configured in
    /// <c>ControllerStartup</c> with <c>new JsonStringEnumConverter()</c> —
    /// no naming policy override — so enum members serialize as their raw
    /// PascalCase names (<c>"JobStarted"</c>, <c>"FilamentRunout"</c>, …).
    /// Hicks v3 blocker 5: this endpoint MUST publish the exact tokens the
    /// preference DTO round-trips, otherwise clients that echo the
    /// capabilities list will submit unrecognised values.
    /// </summary>
    private static readonly System.Text.Json.JsonSerializerOptions CapabilitiesJsonOptions = new()
    {
        Converters =
        {
            new System.Text.Json.Serialization.JsonStringEnumConverter(),
        },
    };

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
    /// Get the notification-preference contract capabilities this server supports.
    /// Issue #708: allows browser/mobile clients (e.g. #716 preference matrix UI)
    /// to enumerate the exact set of <see cref="NotificationPreferenceEventType"/>
    /// tokens the server will accept in a preference update, so old clients on a
    /// new server (or new clients on an old server) can degrade cleanly.
    ///
    /// A client that receives HTTP 404 from this endpoint MUST treat the server as
    /// legacy-only (i.e. only <c>jobStarted / jobCompleted / jobFailed / jobPaused</c>
    /// are supported) and MUST NOT send any extended tokens in a preference update.
    /// </summary>
    [HttpGet("preferences/capabilities")]
    [ProducesResponseType(typeof(NotificationPreferencesCapabilitiesDto), StatusCodes.Status200OK)]
    [AllowAnonymous]
    public ActionResult<NotificationPreferencesCapabilitiesDto> GetPreferencesCapabilities()
    {
        // Enum members are converted to the same camelCase wire tokens the
        // JsonStringEnumConverter emits everywhere else in the DTO, via the
        // pre-built CapabilitiesJsonOptions singleton, so the two paths stay
        // in lock-step even if the naming policy ever changes.
        NotificationPreferenceEventType[] values = Enum
            .GetValues<NotificationPreferenceEventType>();

        List<string> supported = new(values.Length);
        foreach (NotificationPreferenceEventType v in values)
        {
            string token = System.Text.Json.JsonSerializer.Serialize(v, CapabilitiesJsonOptions).Trim('"');
            supported.Add(token);
        }

        return Ok(new NotificationPreferencesCapabilitiesDto
        {
            SupportedEventTypes = supported,
        });
    }

    /// <summary>
    /// Update notification preferences for the current user.
    /// </summary>
    /// <param name="request">The preferences to update.</param>
    /// <param name="dbContext">DB context accepted for backward-compatible parameter binding; the notification service owns the tracked read/write.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated notification preferences.</returns>
    [HttpPut("preferences")]
    [ProducesResponseType(typeof(NotificationPreferencesDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<NotificationPreferencesDto>> UpdatePreferencesAsync(
        [FromBody] UpdateNotificationPreferencesRequest request,
        [FromServices] AppDbContext dbContext,
        CancellationToken cancellationToken = default)
    {
        _ = dbContext;

        try
        {
            Guid userId = GetUserIdFromClaims();

            if (request == null)
            {
                return BadRequest(new { error = "Request body cannot be empty" });
            }

            // Issue #708 H2-v5: attention-row preservation MUST happen inside
            // NotificationService's single tracked read/write unit so a
            // concurrent newer-client attention update cannot be overwritten
            // by a stale pre-read snapshot. The controller no longer touches
            // the persisted row up-front; it only builds the transient
            // request-view of preferences and hands the service a signal for
            // whether the incoming matrix addressed any attention row. The
            // service then either overwrites the 20 attention columns or
            // leaves them untouched.
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
                RetentionDays = request.RetentionDays ?? 30,
            };

            ApplyEventChannelPreferences(preferences, request);
            bool preserveAttentionFields = !RequestMatrixIncludesAttentionRow(request);
            await notificationService.UpdatePreferencesAsync(userId, preferences, preserveAttentionFields, cancellationToken);
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

    /// <summary>
    /// Registers or updates the native-push device token for the current installation.
    /// Feature-gated by <c>OperatorFeatures.NativePushEnabled</c>; when disabled, returns
    /// 404 <c>ProblemDetails</c> with <c>code=featureDisabled</c> per issue #708 / #725.
    /// See <c>docs/OPERATOR_NATIVE_PUSH.md</c>.
    /// </summary>
    [HttpPost("device-tokens")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> RegisterDeviceTokenAsync(
        [FromBody] DeviceTokenRegistrationRequest request,
        [FromServices] IOperatorFeatureGate operatorFeatures,
        [FromServices] IDeviceTokenRepository deviceTokens,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operatorFeatures);
        ArgumentNullException.ThrowIfNull(deviceTokens);

        if (!operatorFeatures.IsEnabled(OperatorFeature.NativePush))
        {
            return OperatorFeatureProblemDetails.NotFound(operatorFeatures, OperatorFeature.NativePush);
        }

        try
        {
            Guid userId = GetUserIdFromClaims();

            if (request is null
                || string.IsNullOrWhiteSpace(request.InstallationId)
                || string.IsNullOrWhiteSpace(request.Token)
                || string.IsNullOrWhiteSpace(request.Platform)
                || string.IsNullOrWhiteSpace(request.Environment))
            {
                return BadRequest(new { error = "installationId, token, platform and environment are required" });
            }

            _ = await deviceTokens.UpsertAsync(
                userId,
                request.InstallationId.Trim(),
                request.Token.Trim(),
                request.Platform.Trim().ToLowerInvariant(),
                request.Environment.Trim().ToLowerInvariant(),
                string.IsNullOrWhiteSpace(request.AppBundleId) ? null : request.AppBundleId.Trim(),
                cancellationToken);
            return NoContent();
        }
        catch (InvalidOperationException)
        {
            return Unauthorized(new { error = "User ID not found in claims" });
        }
    }

    /// <summary>
    /// Unregisters the native-push device token for a specific installation.
    /// Feature-gated by <c>OperatorFeatures.NativePushEnabled</c>.
    /// </summary>
    [HttpDelete("device-tokens")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UnregisterDeviceTokenAsync(
        [FromBody] DeviceTokenUnregistrationRequest request,
        [FromServices] IOperatorFeatureGate operatorFeatures,
        [FromServices] IDeviceTokenRepository deviceTokens,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operatorFeatures);
        ArgumentNullException.ThrowIfNull(deviceTokens);

        if (!operatorFeatures.IsEnabled(OperatorFeature.NativePush))
        {
            return OperatorFeatureProblemDetails.NotFound(operatorFeatures, OperatorFeature.NativePush);
        }

        try
        {
            Guid userId = GetUserIdFromClaims();
            if (request is null || string.IsNullOrWhiteSpace(request.InstallationId))
            {
                return BadRequest(new { error = "installationId is required" });
            }

            _ = await deviceTokens.DeleteByInstallationAsync(userId, request.InstallationId.Trim(), cancellationToken);
            return NoContent();
        }
        catch (InvalidOperationException)
        {
            return Unauthorized(new { error = "User ID not found in claims" });
        }
    }

    /// <summary>
    /// Returns the fixed catalog of native-push attention categories and the actions each
    /// declares. This is the stable contract mobile clients (and #716) consume so that
    /// category registration on the device matches server payloads exactly.
    /// See <c>docs/OPERATOR_NATIVE_PUSH.md</c>.
    /// </summary>
    [HttpGet("attention-categories")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(AttentionCategoryCatalogDto), StatusCodes.Status200OK)]
    public ActionResult<AttentionCategoryCatalogDto> GetAttentionCategories()
    {
        AttentionKind[] kinds =
        {
            AttentionKind.Failure,
            AttentionKind.Offline,
            AttentionKind.Maintenance,
            AttentionKind.Harvest,
            AttentionKind.Runout,
        };
        List<AttentionCategoryDto> items = new(kinds.Length);
        foreach (AttentionKind kind in kinds)
        {
            string? category = AttentionPushCategories.CategoryFor(kind);
            if (category is null)
            {
                continue;
            }

            items.Add(new AttentionCategoryDto
            {
                Kind = kind,
                Category = category,
                Actions = AttentionPushCategories.ActionsFor(kind).ToList(),
                DeepLinkScheme = AttentionDeepLinks.Scheme,
            });
        }

        return Ok(new AttentionCategoryCatalogDto { Categories = items });
    }

    /// <summary>
    /// Returns the current user's per-category native-push opt-in map. Missing keys mean
    /// enabled — new categories light up automatically.
    /// </summary>
    [HttpGet("attention-push-preferences")]
    [ProducesResponseType(typeof(AttentionPushPreferencesDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AttentionPushPreferencesDto>> GetAttentionPushPreferencesAsync(
        [FromServices] IOperatorFeatureGate operatorFeatures,
        [FromServices] AppDbContext dbContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operatorFeatures);
        ArgumentNullException.ThrowIfNull(dbContext);
        if (!operatorFeatures.IsEnabled(OperatorFeature.NativePush))
        {
            return OperatorFeatureProblemDetails.NotFound(operatorFeatures, OperatorFeature.NativePush);
        }

        try
        {
            Guid userId = GetUserIdFromClaims();
            NotificationPreferences? prefs = await dbContext.NotificationPreferences
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.UserId == userId, cancellationToken);
            AttentionPushCategoryPreferences catPrefs = AttentionPushCategoryPreferences.FromJson(prefs?.AttentionPushCategoryPreferencesJson);
            return Ok(new AttentionPushPreferencesDto { Categories = catPrefs.Categories });
        }
        catch (InvalidOperationException)
        {
            return Unauthorized(new { error = "User ID not found in claims" });
        }
    }

    /// <summary>
    /// Updates the current user's per-category native-push opt-in map. Absent keys are
    /// left untouched; explicit <c>true</c> / <c>false</c> updates the setting.
    /// </summary>
    [HttpPut("attention-push-preferences")]
    [ProducesResponseType(typeof(AttentionPushPreferencesDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AttentionPushPreferencesDto>> UpdateAttentionPushPreferencesAsync(
        [FromBody] AttentionPushPreferencesDto request,
        [FromServices] IOperatorFeatureGate operatorFeatures,
        [FromServices] AppDbContext dbContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operatorFeatures);
        ArgumentNullException.ThrowIfNull(dbContext);
        if (!operatorFeatures.IsEnabled(OperatorFeature.NativePush))
        {
            return OperatorFeatureProblemDetails.NotFound(operatorFeatures, OperatorFeature.NativePush);
        }

        try
        {
            Guid userId = GetUserIdFromClaims();
            NotificationPreferences? prefs = await dbContext.NotificationPreferences
                .FirstOrDefaultAsync(p => p.UserId == userId, cancellationToken);
            if (prefs is null)
            {
                prefs = new NotificationPreferences { UserId = userId };
                dbContext.NotificationPreferences.Add(prefs);
            }

            AttentionPushCategoryPreferences catPrefs = AttentionPushCategoryPreferences.FromJson(prefs.AttentionPushCategoryPreferencesJson);
            if (request?.Categories is not null)
            {
                foreach (KeyValuePair<string, bool> kv in request.Categories)
                {
                    catPrefs.Categories[kv.Key] = kv.Value;
                }
            }

            prefs.AttentionPushCategoryPreferencesJson = catPrefs.ToJson();
            prefs.UpdatedAt = DateTime.UtcNow;
            _ = await dbContext.SaveChangesAsync(cancellationToken);
            return Ok(new AttentionPushPreferencesDto { Categories = catPrefs.Categories });
        }
        catch (InvalidOperationException)
        {
            return Unauthorized(new { error = "User ID not found in claims" });
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
            },
            new()
            {
                EventType = NotificationPreferenceEventType.PrinterFailure,
                InApp = preferences.InAppOnPrinterFailure,
                Email = preferences.EmailOnPrinterFailure,
                Push = preferences.PushOnPrinterFailure,
                Telegram = preferences.TelegramOnPrinterFailure
            },
            new()
            {
                EventType = NotificationPreferenceEventType.FilamentRunout,
                InApp = preferences.InAppOnFilamentRunout,
                Email = preferences.EmailOnFilamentRunout,
                Push = preferences.PushOnFilamentRunout,
                Telegram = preferences.TelegramOnFilamentRunout
            },
            new()
            {
                EventType = NotificationPreferenceEventType.HarvestReady,
                InApp = preferences.InAppOnHarvestReady,
                Email = preferences.EmailOnHarvestReady,
                Push = preferences.PushOnHarvestReady,
                Telegram = preferences.TelegramOnHarvestReady
            },
            new()
            {
                EventType = NotificationPreferenceEventType.MaintenanceDue,
                InApp = preferences.InAppOnMaintenanceDue,
                Email = preferences.EmailOnMaintenanceDue,
                Push = preferences.PushOnMaintenanceDue,
                Telegram = preferences.TelegramOnMaintenanceDue
            },
            new()
            {
                EventType = NotificationPreferenceEventType.PrinterOffline,
                InApp = preferences.InAppOnPrinterOffline,
                Email = preferences.EmailOnPrinterOffline,
                Push = preferences.PushOnPrinterOffline,
                Telegram = preferences.TelegramOnPrinterOffline
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

        // Attention-row toggles are only reset when the incoming matrix
        // actually addresses attention rows. A legacy client that knows only
        // the four job rows must NOT clobber attention preferences a newer
        // client saved earlier — this preserves user intent across mixed
        // client versions (Vasquez v3 B1). When the matrix contains any
        // attention token, we reset every attention row to opt-in-safe
        // defaults first so that omitted attention rows land at defaults
        // rather than stale values, then per-row overrides in the loop below
        // apply the sender's actual choices.
        bool matrixIncludesAttentionRow = RequestMatrixIncludesAttentionRow(request);

        if (matrixIncludesAttentionRow)
        {
            preferences.InAppOnPrinterFailure = true;
            preferences.EmailOnPrinterFailure = false;
            preferences.PushOnPrinterFailure = true;
            preferences.TelegramOnPrinterFailure = false;
            preferences.InAppOnFilamentRunout = true;
            preferences.EmailOnFilamentRunout = false;
            preferences.PushOnFilamentRunout = true;
            preferences.TelegramOnFilamentRunout = false;
            preferences.InAppOnHarvestReady = true;
            preferences.EmailOnHarvestReady = false;
            preferences.PushOnHarvestReady = true;
            preferences.TelegramOnHarvestReady = false;
            preferences.InAppOnMaintenanceDue = true;
            preferences.EmailOnMaintenanceDue = false;
            preferences.PushOnMaintenanceDue = true;
            preferences.TelegramOnMaintenanceDue = false;
            preferences.InAppOnPrinterOffline = true;
            preferences.EmailOnPrinterOffline = false;
            preferences.PushOnPrinterOffline = true;
            preferences.TelegramOnPrinterOffline = false;
        }

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
                case NotificationPreferenceEventType.PrinterFailure:
                    preferences.InAppOnPrinterFailure = item.InApp;
                    preferences.EmailOnPrinterFailure = item.Email;
                    preferences.PushOnPrinterFailure = item.Push;
                    preferences.TelegramOnPrinterFailure = item.Telegram;
                    break;
                case NotificationPreferenceEventType.FilamentRunout:
                    preferences.InAppOnFilamentRunout = item.InApp;
                    preferences.EmailOnFilamentRunout = item.Email;
                    preferences.PushOnFilamentRunout = item.Push;
                    preferences.TelegramOnFilamentRunout = item.Telegram;
                    break;
                case NotificationPreferenceEventType.HarvestReady:
                    preferences.InAppOnHarvestReady = item.InApp;
                    preferences.EmailOnHarvestReady = item.Email;
                    preferences.PushOnHarvestReady = item.Push;
                    preferences.TelegramOnHarvestReady = item.Telegram;
                    break;
                case NotificationPreferenceEventType.MaintenanceDue:
                    preferences.InAppOnMaintenanceDue = item.InApp;
                    preferences.EmailOnMaintenanceDue = item.Email;
                    preferences.PushOnMaintenanceDue = item.Push;
                    preferences.TelegramOnMaintenanceDue = item.Telegram;
                    break;
                case NotificationPreferenceEventType.PrinterOffline:
                    preferences.InAppOnPrinterOffline = item.InApp;
                    preferences.EmailOnPrinterOffline = item.Email;
                    preferences.PushOnPrinterOffline = item.Push;
                    preferences.TelegramOnPrinterOffline = item.Telegram;
                    break;
            }
        }

        // Master flags (EnableInAppNotifications, EnableEmailNotifications,
        // EnablePushNotifications, EnableTelegramNotifications) are derived
        // in NotificationService from the OR of all nine event rows on the
        // tracked entity (issue #708 H1-v5). Deriving them here would be
        // wrong for legacy PUTs because the controller does not see the
        // persisted attention rows any more.
        preferences.NotifyOnStart = preferences.InAppOnJobStarted || preferences.EmailOnJobStarted || preferences.PushOnJobStarted || preferences.TelegramOnJobStarted;
        preferences.NotifyOnCompletion = preferences.InAppOnJobCompleted || preferences.EmailOnJobCompleted || preferences.PushOnJobCompleted || preferences.TelegramOnJobCompleted;
        preferences.NotifyOnFailure = true;
        preferences.NotifyOnPause = preferences.InAppOnJobPaused || preferences.EmailOnJobPaused || preferences.PushOnJobPaused || preferences.TelegramOnJobPaused;
    }

    private static bool RequestMatrixIncludesAttentionRow(UpdateNotificationPreferencesRequest request)
    {
        List<NotificationEventChannelPreferenceDto>? matrix = request.EventChannelPreferences;
        if (matrix is null || matrix.Count == 0)
        {
            return false;
        }

        foreach (NotificationEventChannelPreferenceDto item in matrix)
        {
            if (item is null)
            {
                continue;
            }

            if (item.EventType is NotificationPreferenceEventType.PrinterFailure
                or NotificationPreferenceEventType.FilamentRunout
                or NotificationPreferenceEventType.HarvestReady
                or NotificationPreferenceEventType.MaintenanceDue
                or NotificationPreferenceEventType.PrinterOffline)
            {
                return true;
            }
        }

        return false;
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
    JobPaused,

    // Issue #708 shared preference contract — attention-row events extend the
    // existing four job events without changing the DTO shape or JSON casing.
    // Tokens serialize (via JsonStringEnumConverter) as: "printerFailure",
    // "filamentRunout", "harvestReady", "maintenanceDue", "printerOffline".
    PrinterFailure,
    FilamentRunout,
    HarvestReady,
    MaintenanceDue,
    PrinterOffline
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
/// Server capabilities for the notification preference contract (issue #708).
/// Clients read this before rendering the preference matrix so they can adapt
/// to old servers that only support the four legacy job event types.
/// </summary>
public class NotificationPreferencesCapabilitiesDto
{
    /// <summary>
    /// Ordered set of <see cref="NotificationPreferenceEventType"/> tokens the
    /// server accepts, using the same camelCase JSON tokens the update
    /// endpoint accepts (e.g. <c>"jobStarted"</c>, <c>"printerFailure"</c>).
    /// </summary>
    public List<string> SupportedEventTypes { get; set; } = new();
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

/// <summary>
/// Request model for registering / updating a native-push device token (iOS APNs today).
/// See <c>docs/OPERATOR_NATIVE_PUSH.md</c>.
/// </summary>
public class DeviceTokenRegistrationRequest
{
    /// <summary>Per-server installation identifier supplied by the mobile app.</summary>
    public string InstallationId { get; set; } = string.Empty;

    /// <summary>Provider-issued device token (APNs hex).</summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>Client platform: <c>ios</c> today; <c>android</c> reserved.</summary>
    public string Platform { get; set; } = "ios";

    /// <summary>APNs environment: <c>development</c> or <c>production</c>.</summary>
    public string Environment { get; set; } = "production";

    /// <summary>App bundle identifier reported by the mobile app (diagnostics only).</summary>
    public string? AppBundleId { get; set; }
}

/// <summary>Request model for unregistering a native-push device token.</summary>
public class DeviceTokenUnregistrationRequest
{
    /// <summary>Per-server installation identifier to remove.</summary>
    public string InstallationId { get; set; } = string.Empty;
}

/// <summary>
/// Catalog entry describing an attention-push category. Serialized as camelCase; the
/// <see cref="Kind"/> field is emitted using the shared <see cref="AttentionKind"/>
/// converter so the wire value stays in sync with the SignalR contract (issue #707/#716).
/// </summary>
public class AttentionCategoryDto
{
    /// <summary>The <see cref="AttentionKind"/> the category corresponds to.</summary>
    public AttentionKind Kind { get; set; }

    /// <summary>APNs category identifier the mobile app registers at launch.</summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>Ordered action ids the category advertises.</summary>
    public List<string> Actions { get; set; } = new();

    /// <summary>Deep-link scheme the mobile app is expected to handle.</summary>
    public string DeepLinkScheme { get; set; } = "printfarmer";
}

/// <summary>Response body of <c>GET /api/notifications/attention-categories</c>.</summary>
public class AttentionCategoryCatalogDto
{
    /// <summary>Ordered list of category entries.</summary>
    public List<AttentionCategoryDto> Categories { get; set; } = new();
}

/// <summary>
/// Per-user opt-in map for native-push attention categories. Keys are the camelCase
/// <see cref="AttentionKind"/> wire values (<c>failure</c>, <c>offline</c>,
/// <c>maintenance</c>, <c>harvest</c>, <c>runout</c>). Missing keys mean enabled.
/// </summary>
public class AttentionPushPreferencesDto
{
    /// <summary>Category → enabled map. Missing keys default to enabled.</summary>
    public Dictionary<string, bool> Categories { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
