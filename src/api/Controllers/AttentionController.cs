using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Dtos.Attention;
using Farm.Infrastructure.Services.Attention;
using Farm.Infrastructure.Services.OperatorFeatures;
using Farm.Web.Api.Infrastructure.OperatorFeatures;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// Unified attention feed for the operator home screen. See epic #705 / issue #707.
/// </summary>
/// <remarks>
/// <para>
/// The controller is deliberately thin: composition, snooze application, and typed
/// action dispatch live in <see cref="IAttentionService"/>. SignalR invalidation for
/// mutating endpoints is delegated to <see cref="IAttentionBroadcaster"/>.
/// </para>
/// <para>
/// Authorization uses <c>[Authorize]</c> — any authenticated user may read their feed.
/// The controller resolves <c>User.IsInRole("farm_admin")</c> once per request and
/// passes it to <see cref="IAttentionService"/>, which applies the role filter <b>before</b>
/// composition/pagination/totals so non-admin callers never see maintenance items, ids,
/// details, or counts, and refuses maintenance action dispatch with a 404 that does not
/// disclose existence.
/// </para>
/// <para>
/// <b>Feature gate (#725):</b> every endpoint consults the shared
/// <see cref="Farm.Infrastructure.Services.OperatorFeatures.IOperatorFeatureGate"/> for
/// <see cref="Farm.Infrastructure.Services.OperatorFeatures.OperatorFeature.Attention"/>.
/// When disabled it returns <c>404</c> ProblemDetails with <c>code=featureDisabled</c>
/// before any read, write, or broadcast. Broadcast suppression when disabled is enforced
/// centrally in <see cref="Farm.Infrastructure.Services.Attention.AttentionBroadcaster"/>.
/// </para>
/// </remarks>
[ApiController]
[Route("api/attention")]
[Tags("Attention")]
[Authorize]
public sealed class AttentionController(
    IAttentionService attentionService,
    IAttentionBroadcaster broadcaster,
    IOperatorFeatureGate featureGate,
    ILogger<AttentionController> logger) : ControllerBase
{
    private readonly IAttentionService _service = attentionService ?? throw new ArgumentNullException(nameof(attentionService));
    private readonly IAttentionBroadcaster _broadcaster = broadcaster ?? throw new ArgumentNullException(nameof(broadcaster));
    private readonly IOperatorFeatureGate _featureGate = featureGate ?? throw new ArgumentNullException(nameof(featureGate));
    private readonly ILogger<AttentionController> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    private NotFoundObjectResult? FeatureDisabledResult()
        => _featureGate.IsEnabled(OperatorFeature.Attention)
            ? null
            : OperatorFeatureProblemDetails.NotFound(_featureGate, OperatorFeature.Attention);

    /// <summary>Returns the composed, cursor-paginated attention feed for the current user.</summary>
    /// <param name="cursor">
    /// Opaque cursor from a previous page's <c>nextCursor</c>. Omit for the first page.
    /// A malformed or unsupported cursor returns <c>400</c> (no silent restart from page 1).
    /// </param>
    /// <param name="limit">
    /// Maximum items to return. Defaults to 100; values above 250 or below 1 return a
    /// validation error (the server does not clamp).
    /// </param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <response code="200">Cursor-paginated feed: items, nextCursor, and healthy printer count.</response>
    /// <response code="400">The cursor is malformed/unsupported or the limit is out of range.</response>
    /// <response code="401">User id could not be resolved from the token.</response>
    [HttpGet]
    [ProducesResponseType(typeof(AttentionFeedDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<AttentionFeedDto>> GetFeedAsync(
        [FromQuery] string? cursor = null,
        [FromQuery] int limit = AttentionService.DefaultLimit,
        CancellationToken cancellationToken = default)
    {
        if (FeatureDisabledResult() is NotFoundObjectResult disabled)
        {
            return disabled;
        }

        if (!TryGetUserId(out Guid userId, out ActionResult? error))
        {
            return error!;
        }

        // Validate the limit explicitly (do not clamp) per the R1 contract.
        if (limit < 1 || limit > AttentionService.MaxLimit)
        {
            ModelState.AddModelError(
                nameof(limit),
                $"limit must be between 1 and {AttentionService.MaxLimit}.");
            return ValidationProblem(ModelState);
        }

        bool isFarmAdmin = User?.IsInRole(AttentionService.MaintenanceRoleName) ?? false;
        AttentionFeedResult result = await _service.GetFeedAsync(userId, isFarmAdmin, cursor, limit, cancellationToken);
        if (result.InvalidCursor)
        {
            ModelState.AddModelError(nameof(cursor), "The supplied cursor is malformed or unsupported.");
            return ValidationProblem(ModelState);
        }

        return Ok(result.Feed);
    }

    /// <summary>Snoozes an attention item for the current user until the given UTC instant.</summary>
    /// <response code="200">Snooze accepted.</response>
    /// <response code="400">Request payload is missing or the deadline is in the past.</response>
    /// <response code="401">User id could not be resolved from the token.</response>
    [HttpPost("{attentionItemId}/snooze")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> SnoozeAsync(
        [FromRoute] string attentionItemId,
        [FromBody] SnoozeAttentionRequest request,
        CancellationToken cancellationToken)
    {
        if (FeatureDisabledResult() is NotFoundObjectResult disabled)
        {
            return disabled;
        }

        if (request is null)
        {
            return BadRequest(new { error = "Request body is required." });
        }

        if (!TryGetUserId(out Guid userId, out ActionResult? error))
        {
            return error!;
        }

        SnoozeResult result = await _service.SnoozeAsync(
            userId,
            attentionItemId,
            request.SnoozedUntilUtc,
            request.AttentionItemAnchorAtUtc,
            cancellationToken);
        if (!result.Success)
        {
            return BadRequest(new { error = result.Reason ?? "Snooze rejected." });
        }

        // Snooze is per-user state — target only this user's connections.
        await _broadcaster.NotifyUserChangedAsync(
            userId,
            new AttentionChangedPayload(attentionItemId, AttentionChangeKind.Updated, DateTime.UtcNow),
            cancellationToken);
        return Ok(new
        {
            snoozedUntilUtc = result.Snooze!.SnoozedUntilUtc,
            attentionItemAnchorAtUtc = result.Snooze!.AttentionItemAnchorAtUtc,
        });
    }

    /// <summary>Clears a snooze the current user previously created.</summary>
    /// <response code="204">Snooze cleared.</response>
    /// <response code="404">No active snooze existed for this user/item pair.</response>
    /// <response code="401">User id could not be resolved from the token.</response>
    [HttpDelete("{attentionItemId}/snooze")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ClearSnoozeAsync(
        [FromRoute] string attentionItemId,
        CancellationToken cancellationToken)
    {
        if (FeatureDisabledResult() is NotFoundObjectResult disabled)
        {
            return disabled;
        }

        if (!TryGetUserId(out Guid userId, out ActionResult? error))
        {
            return error!;
        }

        SnoozeResult result = await _service.ClearSnoozeAsync(userId, attentionItemId, cancellationToken);
        if (!result.Success)
        {
            return NotFound(new { error = result.Reason ?? "No active snooze." });
        }

        // Clearing a snooze changes only this user's view — target their connections.
        await _broadcaster.NotifyUserChangedAsync(
            userId,
            new AttentionChangedPayload(attentionItemId, AttentionChangeKind.Updated, DateTime.UtcNow),
            cancellationToken);
        return NoContent();
    }

    /// <summary>
    /// Executes a typed action against an attention item. Clients must not synthesize
    /// downstream URLs — this is the central action seam.
    /// </summary>
    /// <response code="200">Action dispatched successfully.</response>
    /// <response code="400">Action kind is not offered by the item.</response>
    /// <response code="401">User id could not be resolved from the token.</response>
    /// <response code="404">Attention item was not found in the current feed.</response>
    /// <response code="409">Downstream service refused the command (for example printer busy).</response>
    /// <response code="501">Action is defined but not yet implemented server-side.</response>
    [HttpPost("{attentionItemId}/actions/{actionKind}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status501NotImplemented)]
    public async Task<IActionResult> ExecuteActionAsync(
        [FromRoute] string attentionItemId,
        [FromRoute] AttentionActionKind actionKind,
        CancellationToken cancellationToken)
    {
        if (FeatureDisabledResult() is NotFoundObjectResult disabled)
        {
            return disabled;
        }

        if (!TryGetUserId(out Guid userId, out ActionResult? error))
        {
            return error!;
        }

        string userName = User?.Identity?.Name
            ?? User?.FindFirst("preferred_username")?.Value
            ?? userId.ToString("D", CultureInfo.InvariantCulture);

        bool isFarmAdmin = User?.IsInRole(AttentionService.MaintenanceRoleName) ?? false;
        AttentionActionResult result = await _service.ExecuteActionAsync(userId, userName, isFarmAdmin, attentionItemId, actionKind, cancellationToken);
        IActionResult response = result.Outcome switch
        {
            AttentionActionOutcome.Ok => Ok(new { outcome = result.Outcome.ToString() }),
            AttentionActionOutcome.NotFound => NotFound(new { error = result.Reason ?? "Not found." }),
            AttentionActionOutcome.InvalidAction => BadRequest(new { error = result.Reason ?? "Invalid action." }),
            AttentionActionOutcome.Conflict => Conflict(new { error = result.Reason ?? "Conflict." }),
            AttentionActionOutcome.NotImplemented => StatusCode(StatusCodes.Status501NotImplemented, new { error = result.Reason ?? "Not implemented." }),
            _ => StatusCode(StatusCodes.Status502BadGateway, new { error = result.Reason ?? "Action failed." }),
        };

        // Single-owner broadcast topology (issue #707, review R3): the underlying source
        // mutator owns its shared attentionchanged event. The maintenance engine broadcasts
        // after its committed status mutation; the failure dispatch broadcasts one resolved
        // event after the printer mutation + resolution commit. The controller therefore does
        // NOT emit a blanket shared event here (that would double-fire). Per-user snooze/clear
        // events remain controller-owned on their dedicated endpoints.
        return response;
    }

    private bool TryGetUserId(out Guid userId, out ActionResult? error)
    {
        string? userIdString = User?.FindFirst("sub")?.Value
            ?? User?.FindFirst("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier")?.Value
            ?? User?.FindFirst("oid")?.Value;

        if (string.IsNullOrEmpty(userIdString) || !Guid.TryParse(userIdString, out userId))
        {
            _logger.LogWarning("[AttentionController] Missing/invalid user id in token");
            userId = Guid.Empty;
            error = Unauthorized(new { error = "User id not found in claims." });
            return false;
        }

        error = null;
        return true;
    }
}
