using Farm.Infrastructure.Security;
using Farm.Slicer.Module.Api.Filters;
using Farm.Slicer.Module.Domain;
using Farm.Slicer.Module.Dtos;
using Farm.Slicer.Module.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Farm.Slicer.Module.Api.Controllers.Slicing;

/// <summary>
/// Creates farm-wide custom OrcaSlicer machine profile families.
/// </summary>
[Route("api/slicer/profiles")]
[Tags("Slicer Profiles")]
[Authorize]
public sealed class ProfileFamiliesController(
    IProfileFamilyService profileFamilyService,
    ILogger<ProfileFamiliesController> logger) : ControllerBase
{
    private readonly IProfileFamilyService _profileFamilyService =
        profileFamilyService ?? throw new ArgumentNullException(nameof(profileFamilyService));

    private readonly ILogger<ProfileFamiliesController> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// Clones a stock machine model into a farm-wide custom family with selected nozzle variants.
    /// </summary>
    [HttpPost("clone-family")]
    [RequirePermission("slicer_engines:admin")]
    [ProducesResponseType(typeof(CloneProfileFamilyResponseDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> CloneFamilyAsync(
        [FromBody] CloneProfileFamilyRequestDto? request,
        CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(new
            {
                code = "invalid_profile_family",
                detail = "One or more profile family fields are invalid."
            });
        }

        if (request is null)
        {
            return BadRequest(new { code = "invalid_profile_family", detail = "Request body is required." });
        }

        if (!PrintFarmerPermissions.TryGetUserId(User, out Guid userId))
        {
            return Forbid();
        }

        try
        {
            CloneProfileFamilyResponseDto result =
                await _profileFamilyService.CloneFamilyAsync(request, userId, ct);
            return StatusCode(StatusCodes.Status201Created, result);
        }
        catch (ProfileFamilyConflictException ex)
        {
            return Conflict(new { code = "profile_family_name_conflict", detail = ex.Message });
        }
        catch (ProfileFamilyHashConflictException ex)
        {
            return Conflict(new { code = "profile_family_hash_conflict", detail = ex.Message });
        }
        catch (ProfileFamilySourceException ex)
        {
            return UnprocessableEntity(new { code = "source_preset_unavailable", detail = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { code = "invalid_profile_family", detail = ex.Message });
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "OrcaSlicer worker unavailable during profile-family creation");
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new
                {
                    code = "profile_family_worker_unavailable",
                    detail = "OrcaSlicer worker unavailable."
                });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Profile-family creation failed");
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                new { code = "profile_family_creation_failed", detail = "Profile family creation failed." });
        }
    }

    /// <summary>
    /// Lists custom OrcaSlicer profile families, optionally filtered by render status.
    /// Reading families is an ordinary slicing action, gated on <c>slicing:submit</c> rather than
    /// the admin gate that guards family creation.
    /// </summary>
    [HttpGet("families")]
    [RequirePermission(PrintFarmerPermissions.Slicing.Submit)]
    [ProducesResponseType(typeof(IReadOnlyList<ProfileFamilySummaryDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> ListFamiliesAsync(
        [FromQuery] string? renderStatus,
        CancellationToken ct)
    {
        ProfileFamilyRenderStatus? statusFilter = null;
        if (!string.IsNullOrWhiteSpace(renderStatus))
        {
            // Enum binds as a string per the repo's JsonStringEnumConverter convention. Match against
            // the enum NAMES explicitly: Enum.TryParse also accepts the underlying numeric value
            // (e.g. ?renderStatus=2), which would violate the string-enum-only wire contract, and
            // Enum.IsDefined then rubber-stamps it. Comparing names rejects numeric input and returns
            // the {code,detail} envelope instead of the default ASP.NET model-state "errors" dictionary.
            string trimmed = renderStatus.Trim();
            string? matchedName = Enum.GetNames<ProfileFamilyRenderStatus>()
                .FirstOrDefault(name => string.Equals(name, trimmed, StringComparison.OrdinalIgnoreCase));
            if (matchedName is null)
            {
                return BadRequest(new
                {
                    code = "invalid_render_status",
                    detail = $"'{renderStatus}' is not a valid render status."
                });
            }

            statusFilter = Enum.Parse<ProfileFamilyRenderStatus>(matchedName);
        }

        try
        {
            IReadOnlyList<ProfileFamilySummaryDto> families =
                await _profileFamilyService.ListFamiliesAsync(statusFilter, ct);
            return Ok(families);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Listing profile families failed");
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                new { code = "profile_family_list_failed", detail = "Listing profile families failed." });
        }
    }

    /// <summary>
    /// Reads one custom OrcaSlicer profile family by id. Gated on <c>slicing:submit</c>.
    /// </summary>
    [HttpGet("families/{familyId:guid}")]
    [RequirePermission(PrintFarmerPermissions.Slicing.Submit)]
    [ProducesResponseType(typeof(ProfileFamilySummaryDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetFamilyAsync(Guid familyId, CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(new { code = "invalid_profile_family", detail = "Invalid family id." });
        }

        try
        {
            ProfileFamilySummaryDto family = await _profileFamilyService.GetFamilyAsync(familyId, ct);
            return Ok(family);
        }
        catch (ProfileFamilyNotFoundException ex)
        {
            return NotFound(new { code = "profile_family_not_found", detail = ex.Message });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Reading profile family {FamilyId} failed", familyId);
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                new { code = "profile_family_read_failed", detail = "Reading the profile family failed." });
        }
    }

    /// <summary>
    /// Deletes a custom OrcaSlicer profile family, its variants, its worker bundle, and its alias.
    /// Deletion remains an admin action (<c>slicer_engines:admin</c>). Refuses with 409 when a
    /// printer or a non-terminal slice job directly references the family (<c>profile_family_in_use</c>),
    /// or when removing the family's alias would strip a model's last OrcaSlicer coverage while a printer
    /// uses that model (<c>profile_family_last_coverage</c>). Pass <c>?force=true</c> to bypass ONLY the
    /// coverage refusal — never the direct-reference refusal.
    /// </summary>
    [HttpDelete("families/{familyId:guid}")]
    [RequirePermission("slicer_engines:admin")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> DeleteFamilyAsync(
        Guid familyId,
        [FromQuery] bool force,
        CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(new { code = "invalid_profile_family", detail = "Invalid family id." });
        }

        if (!PrintFarmerPermissions.TryGetUserId(User, out Guid userId))
        {
            return Forbid();
        }

        try
        {
            _logger.LogInformation(
                "User {UserId} is deleting profile family {FamilyId} (force: {Force})",
                userId,
                familyId,
                force);
            await _profileFamilyService.DeleteFamilyAsync(familyId, force, ct);
            return NoContent();
        }
        catch (ProfileFamilyNotFoundException ex)
        {
            return NotFound(new { code = "profile_family_not_found", detail = ex.Message });
        }
        catch (ProfileFamilyInUseException ex)
        {
            return Conflict(new { code = "profile_family_in_use", detail = ex.Message });
        }
        catch (ProfileFamilyLastCoverageException ex)
        {
            // Distinct code from profile_family_in_use: the remediation differs (re-point the printer or
            // retry with ?force=true), so a client must be able to tell the two refusals apart.
            return Conflict(new { code = "profile_family_last_coverage", detail = ex.Message });
        }
        catch (ProfileFamilyConcurrencyException ex)
        {
            return Conflict(new { code = "profile_family_concurrent_modification", detail = ex.Message });
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "OrcaSlicer worker unavailable during profile-family deletion");
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new
                {
                    code = "profile_family_worker_unavailable",
                    detail = "OrcaSlicer worker unavailable."
                });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A late alias/DB failure after the worker bundle was removed: the service has marked the
            // family Failed (C3) so it is re-deletable; surface the {code,detail} envelope, never a raw
            // 500 (S3).
            _logger.LogError(ex, "Profile-family {FamilyId} deletion failed after worker delete", familyId);
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                new { code = "profile_family_deletion_failed", detail = "Profile family deletion failed." });
        }
    }

    /// <summary>
    /// Edits one custom OrcaSlicer profile family in place (rename, family-shared overrides, nozzle
    /// variant set, and/or source re-bind) and re-renders it. Admin action
    /// (<c>slicer_engines:admin</c>). Returns the updated family in the same shape as
    /// <c>GET families/{familyId}</c>.
    /// </summary>
    [HttpPatch("families/{familyId:guid}")]
    [RequirePermission("slicer_engines:admin")]
    [ProducesResponseType(typeof(ProfileFamilySummaryDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> EditFamilyAsync(
        Guid familyId,
        [FromBody] EditProfileFamilyRequestDto? request,
        CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(new
            {
                code = "invalid_profile_family",
                detail = "One or more profile family fields are invalid."
            });
        }

        if (request is null)
        {
            return BadRequest(new { code = "invalid_profile_family", detail = "Request body is required." });
        }

        if (!PrintFarmerPermissions.TryGetUserId(User, out Guid userId))
        {
            return Forbid();
        }

        try
        {
            _logger.LogInformation(
                "User {UserId} is editing profile family {FamilyId}", userId, familyId);
            ProfileFamilySummaryDto family = await _profileFamilyService.EditFamilyAsync(familyId, request, ct);
            return Ok(family);
        }
        catch (ProfileFamilyNotFoundException ex)
        {
            return NotFound(new { code = "profile_family_not_found", detail = ex.Message });
        }
        catch (ProfileFamilyConflictException ex)
        {
            return Conflict(new { code = "profile_family_name_conflict", detail = ex.Message });
        }
        catch (ProfileFamilyHashConflictException ex)
        {
            return Conflict(new { code = "profile_family_hash_conflict", detail = ex.Message });
        }
        catch (ProfileFamilyInUseException ex)
        {
            return Conflict(new { code = "profile_family_in_use", detail = ex.Message });
        }
        catch (ProfileFamilyConcurrentlyDeletedException ex)
        {
            // The family was deleted mid-edit; the service rolled back the partially installed bundle.
            return NotFound(new { code = "profile_family_deleted_concurrently", detail = ex.Message });
        }
        catch (ProfileFamilyConcurrencyException ex)
        {
            return Conflict(new { code = "profile_family_concurrent_modification", detail = ex.Message });
        }
        catch (ProfileFamilySourceException ex)
        {
            return UnprocessableEntity(new { code = "source_preset_unavailable", detail = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { code = "invalid_profile_family", detail = ex.Message });
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "OrcaSlicer worker unavailable during profile-family edit");
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new
                {
                    code = "profile_family_worker_unavailable",
                    detail = "OrcaSlicer worker unavailable."
                });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A late alias/DB failure during the edit's render/install escapes every specific handler
            // above; supply the {code,detail} envelope rather than a raw 500 (S3).
            _logger.LogError(ex, "Profile-family {FamilyId} edit failed", familyId);
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                new { code = "profile_family_edit_failed", detail = "Profile family edit failed." });
        }
    }

    /// <summary>
    /// Re-renders one custom OrcaSlicer profile family against the live worker (recovers a
    /// <c>Stale</c>/<c>Failed</c> family, or forces a re-render of a <c>Healthy</c> one). Idempotent.
    /// Admin action (<c>slicer_engines:admin</c>). Returns the updated family.
    /// </summary>
    [HttpPost("families/{familyId:guid}/render")]
    [RequirePermission("slicer_engines:admin")]
    [ProducesResponseType(typeof(ProfileFamilySummaryDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> RenderFamilyAsync(Guid familyId, CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(new { code = "invalid_profile_family", detail = "Invalid family id." });
        }

        if (!PrintFarmerPermissions.TryGetUserId(User, out Guid userId))
        {
            return Forbid();
        }

        try
        {
            _logger.LogInformation(
                "User {UserId} is re-rendering profile family {FamilyId}", userId, familyId);
            ProfileFamilySummaryDto family = await _profileFamilyService.RenderFamilyAsync(familyId, ct);
            return Ok(family);
        }
        catch (ProfileFamilyNotFoundException ex)
        {
            return NotFound(new { code = "profile_family_not_found", detail = ex.Message });
        }
        catch (ProfileFamilyConflictException ex)
        {
            return Conflict(new { code = "profile_family_name_conflict", detail = ex.Message });
        }
        catch (ProfileFamilyHashConflictException ex)
        {
            return Conflict(new { code = "profile_family_hash_conflict", detail = ex.Message });
        }
        catch (ProfileFamilyInUseException ex)
        {
            // A re-render that would drop a still-referenced variant is refused (S6), same as an edit.
            return Conflict(new { code = "profile_family_in_use", detail = ex.Message });
        }
        catch (ProfileFamilyConcurrentlyDeletedException ex)
        {
            // The family was deleted mid-render; the service rolled back the partially installed bundle
            // so nothing is stranded on the worker.
            return NotFound(new { code = "profile_family_deleted_concurrently", detail = ex.Message });
        }
        catch (ProfileFamilyConcurrencyException ex)
        {
            return Conflict(new { code = "profile_family_concurrent_modification", detail = ex.Message });
        }
        catch (ProfileFamilySourceException ex)
        {
            return UnprocessableEntity(new { code = "source_preset_unavailable", detail = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { code = "invalid_profile_family", detail = ex.Message });
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "OrcaSlicer worker unavailable during profile-family re-render");
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new
                {
                    code = "profile_family_worker_unavailable",
                    detail = "OrcaSlicer worker unavailable."
                });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A late alias/DB failure during render/install escapes every specific handler above;
            // supply the {code,detail} envelope rather than a raw 500 (S3).
            _logger.LogError(ex, "Profile-family {FamilyId} re-render failed", familyId);
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                new { code = "profile_family_render_failed", detail = "Profile family re-render failed." });
        }
    }

    /// <summary>
    /// Re-renders a bounded batch of <c>Stale</c> or <c>Failed</c> custom families, returning one result
    /// per family so a single failure never hides the others plus a count of families left unprocessed
    /// (so the caller can drain the queue across calls). Admin action (<c>slicer_engines:admin</c>).
    /// </summary>
    [HttpPost("families/render-stale")]
    [RequirePermission("slicer_engines:admin")]
    [ProducesResponseType(typeof(RenderStaleFamiliesResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> RenderStaleFamiliesAsync(CancellationToken ct)
    {
        if (!PrintFarmerPermissions.TryGetUserId(User, out Guid userId))
        {
            return Forbid();
        }

        try
        {
            _logger.LogInformation("User {UserId} is re-rendering stale profile families", userId);
            RenderStaleFamiliesResponseDto response =
                await _profileFamilyService.RenderStaleFamiliesAsync(ct);
            return Ok(response);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Per-family failures are captured in the response; this handles a failure of the batch
            // itself (e.g. the initial DB query), keeping the {code,detail} envelope (S3).
            _logger.LogError(ex, "Bulk re-render of stale profile families failed");
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                new
                {
                    code = "profile_family_render_stale_failed",
                    detail = "Bulk re-render of stale profile families failed."
                });
        }
    }
}
