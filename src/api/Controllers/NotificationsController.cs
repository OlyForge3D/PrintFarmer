using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
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
using Farm.Infrastructure.Services.ServerIdentity;
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
public class NotificationsController(INotificationService notificationService, VapidOptions vapidOptions) : ControllerBase
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
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<IEnumerable<NotificationDto>>> GetNotificationsAsync(
        [FromQuery] int? limit = 50,
        CancellationToken cancellationToken = default)
    {
        // Hicks #4: no broad catch. TryGetUserIdFromClaims maps a missing /
        // malformed claim to a typed 401 without using exceptions for
        // control flow — the previous InvalidOperationException filter
        // could silently swallow unrelated InvalidOperationException from
        // the service and misreport as 401. Every other exception (OCE,
        // provider errors) propagates unchanged so the framework's
        // sanitized problem-details middleware handles them.
        if (!TryGetUserIdFromClaims(out Guid userId))
        {
            return Unauthorized(new { error = "User ID not found in claims" });
        }

        IEnumerable<Notification> notifications = await notificationService.GetUserNotificationsAsync(userId, limit, cancellationToken);
        return Ok(notifications);
    }

    /// <summary>
    /// Get unread notifications for the current user
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of unread notifications</returns>
    [HttpGet("unread")]
    [ProducesResponseType(typeof(IEnumerable<NotificationDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<IEnumerable<NotificationDto>>> GetUnreadNotificationsAsync(
        CancellationToken cancellationToken = default)
    {
        if (!TryGetUserIdFromClaims(out Guid userId))
        {
            return Unauthorized(new { error = "User ID not found in claims" });
        }

        IEnumerable<Notification> notifications = await notificationService.GetUserUnreadNotificationsAsync(userId, cancellationToken);
        return Ok(notifications);
    }

    /// <summary>
    /// Get the count of unread notifications for the current user
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Count of unread notifications</returns>
    [HttpGet("unread/count")]
    [ProducesResponseType(typeof(UnreadCountResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<UnreadCountResponse>> GetUnreadCountAsync(
        CancellationToken cancellationToken = default)
    {
        if (!TryGetUserIdFromClaims(out Guid userId))
        {
            return Unauthorized(new { error = "User ID not found in claims" });
        }

        int count = await notificationService.GetUnreadCountAsync(userId, cancellationToken);
        return Ok(new UnreadCountResponse { UnreadCount = count });
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
        // Hicks #4: no broad catch — routing binder already enforces {guid}
        // shape, and any downstream provider exception surfaces through the
        // standard problem-details middleware with no raw provider message.
        await notificationService.MarkAsReadAsync(notificationId, cancellationToken);
        return NoContent();
    }

    /// <summary>
    /// Mark multiple notifications as read
    /// </summary>
    /// <param name="request">Request containing list of notification IDs</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Success response</returns>
    [HttpPut("mark-read-batch")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> MarkMultipleAsReadAsync(
        [FromBody] MarkMultipleAsReadRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request?.NotificationIds == null || request.NotificationIds.Count == 0)
        {
            return BadRequest(new { error = "NotificationIds list cannot be empty" });
        }

        // Hicks #4: no broad catch — only the shape validation above maps to
        // 400. Provider errors flow to middleware.
        await notificationService.MarkMultipleAsReadAsync(request.NotificationIds, cancellationToken);
        return NoContent();
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
        // Hicks #4: no broad catch. Route binder enforces {guid} and provider
        // errors flow to problem-details middleware without leaking raw
        // messages.
        await notificationService.DeleteAsync(notificationId, cancellationToken);
        return NoContent();
    }

    /// <summary>
    /// Get notification preferences for the current user
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>User notification preferences</returns>
    [HttpGet("preferences")]
    [ProducesResponseType(typeof(NotificationPreferencesDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<NotificationPreferencesDto>> GetPreferencesAsync(
        CancellationToken cancellationToken = default)
    {
        if (!TryGetUserIdFromClaims(out Guid userId))
        {
            return Unauthorized(new { error = "User ID not found in claims" });
        }

        NotificationPreferences? preferences = await notificationService.GetPreferencesAsync(userId, cancellationToken);
        NotificationPreferences effective = preferences ?? CreateDefaultPreferences(userId);
        return Ok(ToDto(effective));
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
    public ActionResult<NotificationPreferencesCapabilitiesDto> GetPreferencesCapabilities()
    {
        // Enum members are converted to the same PascalCase value tokens the
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
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<NotificationPreferencesDto>> UpdatePreferencesAsync(
        [FromBody] UpdateNotificationPreferencesRequest request,
        [FromServices] AppDbContext dbContext,
        CancellationToken cancellationToken = default)
    {
        _ = dbContext;

        if (!TryGetUserIdFromClaims(out Guid userId))
        {
            return Unauthorized(new { error = "User ID not found in claims" });
        }

        if (request == null)
        {
            return BadRequest(new { error = "Request body cannot be empty" });
        }

        // Issue #708 H3-v6 (Vasquez v6 B3): matrix application is now
        // service-owned patch semantics. The controller builds a patch
        // object (nullable scalars + optional row list) and hands it to the
        // service, which applies it over the single tracked persisted
        // row. Omitted rows are preserved.
        //
        // Hicks #4: no broad catch. `ArgumentOutOfRangeException` from
        // MapEventType is the only typed validation we still map to 400 —
        // provider errors, retry-exhaustion, and OCE flow through the
        // sanitized problem-details middleware unchanged.
        NotificationPreferencesUpdate patch;
        try
        {
            patch = BuildPreferencesPatch(request);
        }
        catch (ArgumentOutOfRangeException)
        {
            return BadRequest(new { error = "Unknown notification preference event type." });
        }

        NotificationPreferences persisted = await notificationService.UpdatePreferencesAsync(userId, patch, cancellationToken);
        return Ok(ToDto(persisted));
    }

    /// <summary>
    /// Get the VAPID public key for push subscription enrollment.
    /// </summary>
    [HttpGet("push-subscription/vapid-key")]
    [ProducesResponseType(typeof(VapidKeyResponse), StatusCodes.Status200OK)]
    public ActionResult<VapidKeyResponse> GetVapidKey()
    {
        return Ok(new VapidKeyResponse { PublicKey = vapidOptions.VapidPublicKey ?? string.Empty });
    }

    /// <summary>
    /// Subscribe to web push notifications. Stores the browser push subscription.
    /// </summary>
    /// <param name="request">The push subscription data from the browser.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpPost("push-subscription")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> SubscribePushAsync(
        [FromBody] PushSubscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetUserIdFromClaims(out Guid userId))
        {
            return Unauthorized(new { error = "User ID not found in claims" });
        }

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

    /// <summary>
    /// Unsubscribe from web push notifications for the current device.
    /// </summary>
    /// <param name="request">The push subscription endpoint to remove.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpDelete("push-subscription")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> UnsubscribePushAsync(
        [FromBody] UnsubscribePushRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetUserIdFromClaims(out Guid userId))
        {
            return Unauthorized(new { error = "User ID not found in claims" });
        }

        if (string.IsNullOrWhiteSpace(request?.Endpoint))
        {
            return BadRequest(new { error = "Endpoint is required" });
        }

        await notificationService.DeletePushSubscriptionAsync(userId, request.Endpoint, cancellationToken);
        return NoContent();
    }

    /// <summary>
    /// Registers or updates the native-push device token for the current installation.
    /// Feature-gated by <c>OperatorFeatures.NativePushEnabled</c>; when disabled, returns
    /// 404 <c>ProblemDetails</c> with <c>code=featureDisabled</c> per issue #708 / #725.
    /// See <c>docs/OPERATOR_NATIVE_PUSH.md</c>.
    /// </summary>
    [HttpPost("device-tokens")]
    [ProducesResponseType(typeof(DeviceTokenRegistrationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> RegisterDeviceTokenAsync(
        [FromBody] DeviceTokenRegistrationRequest request,
        [FromServices] IOperatorFeatureGate operatorFeatures,
        [FromServices] IDeviceTokenRepository deviceTokens,
        [FromServices] IServerIdentityService serverIdentity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operatorFeatures);
        ArgumentNullException.ThrowIfNull(deviceTokens);
        ArgumentNullException.ThrowIfNull(serverIdentity);

        if (!await operatorFeatures.IsEnabledAsync(OperatorFeature.NativePush, cancellationToken).ConfigureAwait(false))
        {
            return OperatorFeatureProblemDetails.NotFound(operatorFeatures, OperatorFeature.NativePush);
        }

        if (!TryGetUserIdFromClaims(out Guid userId))
        {
            return Unauthorized(new { error = "User ID not found in claims" });
        }

        if (request is null
            || !NativePushRegistrationContract.IsCanonicalInstallationId(request.InstallationId)
            || !NativePushRegistrationContract.IsCanonicalApnsToken(request.Token)
            || !NativePushRegistrationContract.IsCanonicalPlatform(request.Platform)
            || !NativePushRegistrationContract.IsCanonicalEnvironment(request.Environment)
            || !NativePushRegistrationContract.IsCanonicalAppBundleId(request.AppBundleId))
        {
            return BadRequest(new { error = "Native-push registration values are not in canonical form." });
        }

        _ = await deviceTokens.UpsertAsync(
            userId,
            request.InstallationId,
            request.Token,
            request.Platform,
            request.Environment,
            request.AppBundleId,
            cancellationToken);

        // #1407: the response now returns this server's canonical serverId so the
        // mobile app can bind this APNs registration to its local RegisteredServer
        // entry. Always the persisted, server-generated identity — never derived from
        // the caller-supplied installationId/token.
        Guid serverId = await serverIdentity.GetOrCreateServerIdAsync(cancellationToken).ConfigureAwait(false);
        return Ok(new DeviceTokenRegistrationResponse { ServerId = serverId });
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

        if (!await operatorFeatures.IsEnabledAsync(OperatorFeature.NativePush, cancellationToken).ConfigureAwait(false))
        {
            return OperatorFeatureProblemDetails.NotFound(operatorFeatures, OperatorFeature.NativePush);
        }

        if (!TryGetUserIdFromClaims(out Guid userId))
        {
            return Unauthorized(new { error = "User ID not found in claims" });
        }

        if (request is null
            || !NativePushRegistrationContract.IsCanonicalInstallationId(request.InstallationId))
        {
            return BadRequest(new { error = "installationId must be in canonical form" });
        }

        _ = await deviceTokens.DeleteByInstallationAsync(userId, request.InstallationId, cancellationToken);
        return NoContent();
    }

    /// <summary>
    /// Returns the fixed catalog of native-push attention categories and the actions each
    /// declares. This is the stable contract mobile clients (and #716) consume so that
    /// category registration on the device matches server payloads exactly.
    /// See <c>docs/OPERATOR_NATIVE_PUSH.md</c>.
    /// </summary>
    [HttpGet("attention-categories")]
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
        if (!await operatorFeatures.IsEnabledAsync(OperatorFeature.NativePush, cancellationToken).ConfigureAwait(false))
        {
            return OperatorFeatureProblemDetails.NotFound(operatorFeatures, OperatorFeature.NativePush);
        }

        if (!TryGetUserIdFromClaims(out Guid userId))
        {
            return Unauthorized(new { error = "User ID not found in claims" });
        }

        NotificationPreferences? prefs = await dbContext.NotificationPreferences
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserId == userId, cancellationToken);
        AttentionPushCategoryPreferences catPrefs = AttentionPushCategoryPreferences.FromJson(prefs?.AttentionPushCategoryPreferencesJson);
        return Ok(new AttentionPushPreferencesDto { Categories = catPrefs.Categories });
    }

    /// <summary>
    /// Updates the current user's per-category native-push opt-in map. Absent keys are
    /// left untouched; explicit <c>true</c> / <c>false</c> updates the setting.
    /// </summary>
    [HttpPut("attention-push-preferences")]
    [ProducesResponseType(typeof(AttentionPushPreferencesDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<AttentionPushPreferencesDto>> UpdateAttentionPushPreferencesAsync(
        [FromBody] AttentionPushPreferencesDto request,
        [FromServices] IOperatorFeatureGate operatorFeatures,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operatorFeatures);
        if (!await operatorFeatures.IsEnabledAsync(OperatorFeature.NativePush, cancellationToken).ConfigureAwait(false))
        {
            return OperatorFeatureProblemDetails.NotFound(operatorFeatures, OperatorFeature.NativePush);
        }

        // Hicks #4 + Bishop v6 hardening: attention category keys are
        // attacker-controlled free-form strings persisted verbatim into a
        // JSON column. The controller enforces per-request shape (cardinality
        // + key length) BEFORE delegating to the service, so a malformed
        // request never enters a database transaction.
        //
        // The cumulative caps (total keys persisted and total UTF-8 byte
        // budget) are enforced by the service INSIDE its serializable
        // transaction so a concurrent burst of one-key requests cannot slip
        // past the per-request bound.
        //
        // Legitimate clients are never rejected: any real AttentionKind key
        // is well under 64 characters and the finalized #708 category
        // universe has fewer than 20 kinds.
        if (request?.Categories is { Count: > 0 } incoming)
        {
            if (incoming.Count > MaxAttentionCategoryKeysPerRequest)
            {
                return Problem(
                    detail: $"At most {MaxAttentionCategoryKeysPerRequest} attention category keys may be updated per request.",
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Attention category batch too large");
            }

            int requestBytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(request).Length;
            if (requestBytes > MaxAttentionCategoryJsonBytes)
            {
                return Problem(
                    detail: $"Serialized attention category request must not exceed {MaxAttentionCategoryJsonBytes} UTF-8 bytes.",
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Attention category batch too large");
            }

            foreach (string key in incoming.Keys)
            {
                if (string.IsNullOrEmpty(key))
                {
                    return Problem(
                        detail: "Attention category keys must not be empty.",
                        statusCode: StatusCodes.Status400BadRequest,
                        title: "Invalid attention category key");
                }

                if (key.Length > MaxAttentionCategoryKeyLength)
                {
                    return Problem(
                        detail: $"Attention category keys must be {MaxAttentionCategoryKeyLength} characters or fewer.",
                        statusCode: StatusCodes.Status400BadRequest,
                        title: "Invalid attention category key");
                }
            }
        }

        if (!TryGetUserIdFromClaims(out Guid userId))
        {
            return Unauthorized(new { error = "User ID not found in claims" });
        }

        var updates = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        if (request?.Categories is not null)
        {
            // System.Text.Json preserves object-property order in Dictionary.
            // Assign through the indexer so raw case variants collapse
            // deterministically with ordered last-write-wins rather than the
            // comparer-changing Dictionary constructor throwing a 500.
            foreach (KeyValuePair<string, bool> update in request.Categories)
            {
                updates[update.Key] = update.Value;
            }
        }

        // Hicks #6: the merge/read/save is service-owned so a serializable
        // transaction + retry can guarantee concurrent first-creates converge
        // on a single row and concurrent disjoint-key updates both persist.
        // The controller no longer touches DbContext for this endpoint.
        AttentionCategoryUpdateResult result = await notificationService
            .UpdateAttentionCategoryPreferencesAsync(userId, updates, cancellationToken);

        if (result.Status == AttentionCategoryUpdateStatus.Rejected)
        {
            return result.Rejection switch
            {
                AttentionCategoryUpdateRejection.CumulativeKeyLimitExceeded => Problem(
                    detail: $"At most {MaxAttentionCategoryKeysPersisted} attention category keys may be persisted per user.",
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Attention category storage full"),
                AttentionCategoryUpdateRejection.JsonByteLimitExceeded => Problem(
                    detail: $"Persisted attention category JSON would exceed {MaxAttentionCategoryJsonBytes} bytes.",
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Attention category storage full"),
                _ => Problem(
                    detail: "Attention category update was rejected.",
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Attention category rejected"),
            };
        }

        return Ok(new AttentionPushPreferencesDto
        {
            Categories = result.Categories is null
                ? new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, bool>(result.Categories, StringComparer.OrdinalIgnoreCase),
        });
    }

    /// <summary>
    /// Maximum number of attention-category keys accepted in a single
    /// <c>PUT /api/notifications/attention-push-preferences</c> request. The
    /// bound protects against unbounded attacker-controlled growth of the
    /// persisted JSON column while comfortably exceeding the count of real
    /// AttentionKind categories in the finalized #708 contract (Bishop v6).
    /// </summary>
    private const int MaxAttentionCategoryKeysPerRequest = AttentionPushCategoryPreferences.MaxKeysPerRequest;

    /// <summary>
    /// Maximum length in characters of each attention-category key in a single
    /// update request. Real AttentionKind enum names are well under this bound.
    /// </summary>
    private const int MaxAttentionCategoryKeyLength = AttentionPushCategoryPreferences.MaxKeyLength;

    /// <summary>
    /// Cumulative bound (Hicks #4): the maximum number of category keys that
    /// may be persisted for a single user across the entire lifetime of that
    /// user's preferences row. Enforced against the PROSPECTIVE merged map
    /// so repeated single-key requests cannot slip past the per-request
    /// bound. Exceeding the bound returns 400 and leaves persisted JSON
    /// byte-for-byte unchanged.
    /// </summary>
    private const int MaxAttentionCategoryKeysPersisted = AttentionPushCategoryPreferences.MaxPersistedKeys;

    /// <summary>
    /// Cumulative bound (Hicks #4): the maximum UTF-8 encoded byte size of
    /// the merged category JSON payload persisted to the row. Enforced
    /// against the PROSPECTIVE merged map so a burst of long-valued keys
    /// cannot silently blow past reasonable row storage.
    /// </summary>
    private const int MaxAttentionCategoryJsonBytes = AttentionPushCategoryPreferences.MaxSerializedUtf8Bytes;

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
        // Hicks #3 canonical defaults: the fresh-GET response and the
        // service's new-user persistence MUST produce the same nine-row
        // shape so a first partial modern PUT preserves omitted rows exactly
        // as the client just observed them on GET. Delegates to the shared
        // helper so any future default change lands in one place.
        return Farm.Infrastructure.Services.Notifications.NotificationPreferencesDefaults.Create(userId);
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

    private static NotificationPreferencesUpdate BuildPreferencesPatch(UpdateNotificationPreferencesRequest request)
    {
        List<NotificationEventChannelPreferenceDto>? matrix = request.EventChannelPreferences;
        IReadOnlyList<NotificationPreferencesRowPatch>? rows = null;

        // Hicks #3: preserve a non-null but empty modern matrix as an empty
        // modern patch (rows = []) so the service takes the modern branch and
        // treats every row as "omitted" — omitted rows are preserved. Prior
        // behaviour collapsed an empty matrix to null which forced the legacy
        // branch, deriving job rows from top-level scalars and silently
        // reshaping any modern client that submitted an empty matrix to
        // re-affirm "no per-event opinion".
        if (matrix is not null)
        {
            // Vasquez v6 B3: translate the wire matrix rows into service-layer
            // patch rows without ever synthesizing rows for events the caller
            // did not send. Null entries in the list are ignored — this
            // matches the previous controller behavior and avoids reflecting
            // JSON parser artifacts into the service.
            var buffered = new List<NotificationPreferencesRowPatch>(matrix.Count);
            foreach (NotificationEventChannelPreferenceDto? item in matrix)
            {
                if (item is null)
                {
                    continue;
                }

                buffered.Add(new NotificationPreferencesRowPatch(
                    MapEventType(item.EventType),
                    item.InApp,
                    item.Email,
                    item.Push,
                    item.Telegram));
            }

            rows = buffered;
        }

        return new NotificationPreferencesUpdate(
            request.EnableEmailNotifications,
            request.EnablePushNotifications,
            request.EnableInAppNotifications,
            request.EnableTelegramNotifications,
            request.NotifyOnStart,
            request.NotifyOnCompletion,
            request.NotifyOnFailure,
            request.NotifyOnPause,
            request.Frequency,
            request.RetentionDays,
            rows);
    }

    private static NotificationPreferenceEvent MapEventType(NotificationPreferenceEventType wireEvent)
    {
        // Kept as an explicit switch so the compiler catches any future wire
        // enum addition — an unmapped value must not silently degrade into a
        // default row.
        return wireEvent switch
        {
            NotificationPreferenceEventType.JobStarted => NotificationPreferenceEvent.JobStarted,
            NotificationPreferenceEventType.JobCompleted => NotificationPreferenceEvent.JobCompleted,
            NotificationPreferenceEventType.JobFailed => NotificationPreferenceEvent.JobFailed,
            NotificationPreferenceEventType.JobPaused => NotificationPreferenceEvent.JobPaused,
            NotificationPreferenceEventType.PrinterFailure => NotificationPreferenceEvent.PrinterFailure,
            NotificationPreferenceEventType.FilamentRunout => NotificationPreferenceEvent.FilamentRunout,
            NotificationPreferenceEventType.HarvestReady => NotificationPreferenceEvent.HarvestReady,
            NotificationPreferenceEventType.MaintenanceDue => NotificationPreferenceEvent.MaintenanceDue,
            NotificationPreferenceEventType.PrinterOffline => NotificationPreferenceEvent.PrinterOffline,
            _ => throw new ArgumentOutOfRangeException(nameof(wireEvent), wireEvent, "Unknown notification preference event type."),
        };
    }

    /// <summary>
    /// Hicks #4: non-throwing extraction of the request's user identifier
    /// from the ambient JWT claims. Returns <see langword="false"/> and the
    /// default guid when the request carries no usable identity claim so the
    /// action can respond with a typed 401 without catching
    /// InvalidOperationException from a helper that used exceptions for
    /// control flow. The throwing legacy helper was removed once all call
    /// sites migrated.
    /// </summary>
    private bool TryGetUserIdFromClaims(out Guid userId)
    {
        string? userIdString = User?.FindFirst("sub")?.Value ??
               User?.FindFirst("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier")?.Value ??
               User?.FindFirst("oid")?.Value;

        if (string.IsNullOrEmpty(userIdString) || !Guid.TryParse(userIdString, out userId))
        {
            userId = Guid.Empty;
            return false;
        }

        return true;
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
/// Request model for updating notification preferences.
/// Hicks #5: every scalar field is nullable so a bare <c>{}</c> PUT does not
/// clobber persisted values with binder defaults. <c>null</c> means "omitted;
/// preserve the persisted value"; a value means "apply this value verbatim".
/// The service layer <see cref="NotificationPreferencesUpdate"/> carries the
/// same nullable semantics end-to-end.
/// </summary>
public class UpdateNotificationPreferencesRequest
{
    /// <summary>Nullable legacy master opt-in for the email channel. <c>null</c> = omitted (preserve persisted).</summary>
    public bool? EnableEmailNotifications { get; set; }

    /// <summary>Nullable legacy master opt-in for the push channel. <c>null</c> = omitted.</summary>
    public bool? EnablePushNotifications { get; set; }

    /// <summary>Nullable legacy master opt-in for the in-app channel. <c>null</c> = omitted.</summary>
    public bool? EnableInAppNotifications { get; set; }

    /// <summary>Nullable legacy master opt-in for the telegram channel. <c>null</c> = omitted.</summary>
    public bool? EnableTelegramNotifications { get; set; }

    /// <summary>Nullable legacy per-event toggle for job completion. <c>null</c> = omitted.</summary>
    public bool? NotifyOnCompletion { get; set; }

    /// <summary>Nullable legacy per-event toggle for job failure. <c>null</c> = omitted.</summary>
    public bool? NotifyOnFailure { get; set; }

    /// <summary>Nullable legacy per-event toggle for job start. <c>null</c> = omitted.</summary>
    public bool? NotifyOnStart { get; set; }

    /// <summary>Nullable legacy per-event toggle for job pause. <c>null</c> = omitted.</summary>
    public bool? NotifyOnPause { get; set; }

    /// <summary>Nullable notification frequency. <c>null</c> = omitted.</summary>
    public NotificationFrequency? Frequency { get; set; }

    /// <summary>Nullable retention window in days. <c>null</c> = omitted.</summary>
    public int? RetentionDays { get; set; }

    /// <summary>Per-event by channel notification matrix; <c>null</c> triggers legacy branch.</summary>
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
    // The DTO uses PascalCase JSON casing for enum tokens (the JsonStringEnumConverter
    // in production has no naming policy override), so tokens serialize as:
    // "PrinterFailure", "FilamentRunout", "HarvestReady", "MaintenanceDue",
    // "PrinterOffline". A separate contract test locks the exact tokens.
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
    /// server accepts, using the same PascalCase enum-value tokens the update
    /// endpoint accepts (e.g. <c>"JobStarted"</c>, <c>"PrinterFailure"</c>).
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
    [Required]
    [StringLength(NativePushRegistrationContract.InstallationIdMaxLength)]
    [RegularExpression(
        NativePushRegistrationContract.InstallationIdPattern,
        ErrorMessage = "InstallationId must use canonical ASCII identifier syntax.")]
    public string InstallationId { get; set; } = string.Empty;

    /// <summary>Provider-issued device token (APNs hex).</summary>
    [Required]
    [StringLength(
        NativePushRegistrationContract.TokenMaxLength,
        MinimumLength = NativePushRegistrationContract.TokenMinLength)]
    [RegularExpression(
        NativePushRegistrationContract.ApnsTokenPattern,
        ErrorMessage = "Token must be a byte-aligned lowercase hexadecimal APNs token.")]
    public string Token { get; set; } = string.Empty;

    /// <summary>Client platform: <c>ios</c> today; <c>android</c> reserved.</summary>
    [Required]
    [StringLength(NativePushRegistrationContract.PlatformMaxLength)]
    [RegularExpression(NativePushRegistrationContract.PlatformPattern)]
    public string Platform { get; set; } = "ios";

    /// <summary>APNs environment: <c>development</c> or <c>production</c>.</summary>
    [Required]
    [StringLength(NativePushRegistrationContract.EnvironmentMaxLength)]
    [RegularExpression(NativePushRegistrationContract.EnvironmentPattern)]
    public string Environment { get; set; } = "production";

    /// <summary>App bundle identifier reported by the mobile app (diagnostics only).</summary>
    [StringLength(NativePushRegistrationContract.AppBundleIdMaxLength)]
    [RegularExpression(
        NativePushRegistrationContract.AppBundleIdPattern,
        ErrorMessage = "AppBundleId must use canonical lowercase bundle-id syntax.")]
    public string? AppBundleId { get; set; }
}

/// <summary>
/// Response model for a successful native-push device-token registration. Carries this
/// server's canonical, persisted <c>serverId</c> so the mobile app can bind this APNs
/// registration to its local <c>RegisteredServer</c> entry. See
/// <c>docs/OPERATOR_NATIVE_PUSH.md</c> and issue #1407.
/// </summary>
public class DeviceTokenRegistrationResponse
{
    /// <summary>This server's stable, opaque identity (canonical UUID string).</summary>
    public Guid ServerId { get; set; }
}

/// <summary>Request model for unregistering a native-push device token.</summary>
public class DeviceTokenUnregistrationRequest
{
    /// <summary>Per-server installation identifier to remove.</summary>
    [Required]
    [StringLength(NativePushRegistrationContract.InstallationIdMaxLength)]
    [RegularExpression(
        NativePushRegistrationContract.InstallationIdPattern,
        ErrorMessage = "InstallationId must use canonical ASCII identifier syntax.")]
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
