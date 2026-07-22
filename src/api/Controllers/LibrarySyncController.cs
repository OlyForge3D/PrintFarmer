using System.Security.Claims;
using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.Exceptions;
using Farm.Infrastructure.Services.Sync;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// Bi-directional library sync endpoints (#845). Exposes a cursor-based pull over the append-only
/// sync journal and a transactional batch apply with optimistic concurrency. Pull results are
/// always scoped to the caller's visibility; apply enforces owner-or-administrator authorization,
/// auto-merges genuinely independent membership changes, and returns structured HTTP 409 conflicts
/// (with safe server and submitted versions) when a write is stale or racing.
/// </summary>
[ApiController]
[Route("api/library-sync")]
[Produces("application/json")]
[Authorize]
public class LibrarySyncController(ILibrarySyncService syncService) : ControllerBase
{
    private readonly ILibrarySyncService _syncService = syncService ?? throw new ArgumentNullException(nameof(syncService));

    /// <summary>
    /// Pulls an ordered page of library changes visible to the current user with a revision beyond
    /// the supplied cursor. Deterministic, bounded, and visibility scoped.
    /// </summary>
    /// <param name="cursor">Opaque continuation cursor; omit to start from the beginning.</param>
    /// <param name="limit">Requested page size; clamped to the service maximum.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpGet("changes")]
    [ProducesResponseType(typeof(LibrarySyncPullResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<LibrarySyncPullResultDto>> PullChangesAsync(
        [FromQuery] string? cursor,
        [FromQuery] int? limit,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out Guid userId))
        {
            return Unauthorized(new { error = "User ID not found in claims" });
        }

        try
        {
            LibrarySyncPullResultDto result = await _syncService.PullAsync(cursor, limit, userId, IsAdmin(), cancellationToken);
            return Ok(result);
        }
        catch (InvalidSyncCursorException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Applies a batch of client mutations atomically. Returns 200 with per-operation results on
    /// success, or 409 with the full conflict set when one or more operations are stale or racing.
    /// </summary>
    /// <param name="request">The batch of operations to apply.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpPost("apply")]
    [ProducesResponseType(typeof(ApplySyncResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(SyncConflictResponseDto), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ApplySyncResultDto>> ApplyAsync(
        [FromBody] ApplySyncRequestDto request,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out Guid userId))
        {
            return Unauthorized(new { error = "User ID not found in claims" });
        }

        try
        {
            ApplySyncResultDto result = await _syncService.ApplyAsync(request, userId, IsAdmin(), cancellationToken);
            return Ok(result);
        }
        catch (SyncConflictException ex)
        {
            return Conflict(new SyncConflictResponseDto
            {
                Error = ex.Message,
                Conflicts = ex.Conflicts,
                ServerRevision = ex.ServerRevision
            });
        }
        catch (CollectionAccessDeniedException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = ex.Message });
        }
        catch (CollectionModelValidationException ex)
        {
            return BadRequest(new { error = ex.Message, invalidModelIds = ex.InvalidModelIds });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    private bool IsAdmin() => User.IsInRole("farm_admin");

    private bool TryGetUserId(out Guid userId)
    {
        string? userIdString =
            User.FindFirstValue(ClaimTypes.NameIdentifier) ??
            User.FindFirst("sub")?.Value ??
            User.FindFirst("oid")?.Value;

        return Guid.TryParse(userIdString, out userId);
    }
}
