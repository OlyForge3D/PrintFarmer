using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Dtos.Attention;
using Farm.Infrastructure.Services.Attention;
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
/// Downstream services enforce their own role checks (for example maintenance
/// resolution requires <c>farm_admin</c> on <see cref="Farm.Infrastructure.Services.Maintenance.IMaintenanceAlertService"/>).
/// </para>
/// <para>
/// <b>Feature-gate integration handoff (#725):</b> when the shared
/// <c>IOperatorFeatureGate</c> lands, insert the following check as the first line of
/// every action here (before <see cref="TryGetUserId"/>):
/// <code>
/// if (!_operatorFeatureGate.IsEnabled("attentionEnabled"))
/// {
///     return Problem(
///         type: "https://printfarmer/errors/feature-disabled",
///         title: "Attention feature is disabled",
///         statusCode: StatusCodes.Status404NotFound,
///         extensions: new Dictionary&lt;string, object?&gt; { ["code"] = "featureDisabled" });
/// }
/// </code>
/// Also gate <see cref="IAttentionBroadcaster.NotifyChangedAsync"/> in
/// <see cref="Farm.Infrastructure.Services.Attention.AttentionBroadcaster"/> and the
/// two invalidation call sites in
/// <see cref="Farm.Web.Api.Services.Maintenance.MaintenanceAlertEngine"/> and
/// <see cref="Farm.Infrastructure.Services.FailureDetection.FailureDetectionIncidentHistoryService"/>
/// so a disabled feature performs no broadcasts, per #725 acceptance criteria.
/// </para>
/// </remarks>
[ApiController]
[Route("api/attention")]
[Tags("Attention")]
[Authorize]
public sealed class AttentionController(
    IAttentionService attentionService,
    IAttentionBroadcaster broadcaster,
    ILogger<AttentionController> logger) : ControllerBase
{
    private readonly IAttentionService _service = attentionService ?? throw new ArgumentNullException(nameof(attentionService));
    private readonly IAttentionBroadcaster _broadcaster = broadcaster ?? throw new ArgumentNullException(nameof(broadcaster));
    private readonly ILogger<AttentionController> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>Returns the composed, paginated attention feed for the current user.</summary>
    /// <param name="page">1-based page index. Values &lt;= 0 are clamped to 1.</param>
    /// <param name="pageSize">
    /// Items per page. Defaults to 50 and is capped at 200 by the service.
    /// </param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <response code="200">Paginated feed of severity-ordered items and healthy printer ids.</response>
    /// <response code="401">User id could not be resolved from the token.</response>
    [HttpGet]
    [ProducesResponseType(typeof(AttentionFeedDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<AttentionFeedDto>> GetFeedAsync(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetUserId(out Guid userId, out ActionResult? error))
        {
            return error!;
        }

        AttentionFeedDto feed = await _service.GetFeedAsync(userId, page, pageSize, cancellationToken);
        return Ok(feed);
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

        await _broadcaster.NotifyChangedAsync(cancellationToken);
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
        if (!TryGetUserId(out Guid userId, out ActionResult? error))
        {
            return error!;
        }

        SnoozeResult result = await _service.ClearSnoozeAsync(userId, attentionItemId, cancellationToken);
        if (!result.Success)
        {
            return NotFound(new { error = result.Reason ?? "No active snooze." });
        }

        await _broadcaster.NotifyChangedAsync(cancellationToken);
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
        if (!TryGetUserId(out Guid userId, out ActionResult? error))
        {
            return error!;
        }

        string userName = User?.Identity?.Name
            ?? User?.FindFirst("preferred_username")?.Value
            ?? userId.ToString("D");

        AttentionActionResult result = await _service.ExecuteActionAsync(userId, userName, attentionItemId, actionKind, cancellationToken);
        IActionResult response = result.Outcome switch
        {
            AttentionActionOutcome.Ok => Ok(new { outcome = result.Outcome.ToString() }),
            AttentionActionOutcome.NotFound => NotFound(new { error = result.Reason ?? "Not found." }),
            AttentionActionOutcome.InvalidAction => BadRequest(new { error = result.Reason ?? "Invalid action." }),
            AttentionActionOutcome.Conflict => Conflict(new { error = result.Reason ?? "Conflict." }),
            AttentionActionOutcome.NotImplemented => StatusCode(StatusCodes.Status501NotImplemented, new { error = result.Reason ?? "Not implemented." }),
            _ => StatusCode(StatusCodes.Status502BadGateway, new { error = result.Reason ?? "Action failed." }),
        };

        if (result.Outcome == AttentionActionOutcome.Ok)
        {
            await _broadcaster.NotifyChangedAsync(cancellationToken);
        }

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
