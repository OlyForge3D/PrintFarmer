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
    public async Task<IActionResult> ListFamiliesAsync(
        [FromQuery] string? renderStatus,
        CancellationToken ct)
    {
        ProfileFamilyRenderStatus? statusFilter = null;
        if (!string.IsNullOrWhiteSpace(renderStatus))
        {
            // Enum binds as a string per the repo's JsonStringEnumConverter convention; parse
            // explicitly so an invalid value returns the {code,detail} envelope, not the default
            // ASP.NET model-state "errors" dictionary.
            if (!Enum.TryParse(renderStatus, ignoreCase: true, out ProfileFamilyRenderStatus parsed)
                || !Enum.IsDefined(parsed))
            {
                return BadRequest(new
                {
                    code = "invalid_render_status",
                    detail = $"'{renderStatus}' is not a valid render status."
                });
            }

            statusFilter = parsed;
        }

        IReadOnlyList<ProfileFamilySummaryDto> families =
            await _profileFamilyService.ListFamiliesAsync(statusFilter, ct);
        return Ok(families);
    }

    /// <summary>
    /// Reads one custom OrcaSlicer profile family by id. Gated on <c>slicing:submit</c>.
    /// </summary>
    [HttpGet("families/{familyId:guid}")]
    [RequirePermission(PrintFarmerPermissions.Slicing.Submit)]
    [ProducesResponseType(typeof(ProfileFamilySummaryDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
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
    }

    /// <summary>
    /// Deletes a custom OrcaSlicer profile family, its variants, its worker bundle, and its alias.
    /// Deletion remains an admin action (<c>slicer_engines:admin</c>). Refuses with 409 when a
    /// printer or a non-terminal slice job still references the family.
    /// </summary>
    [HttpDelete("families/{familyId:guid}")]
    [RequirePermission("slicer_engines:admin")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> DeleteFamilyAsync(Guid familyId, CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(new { code = "invalid_profile_family", detail = "Invalid family id." });
        }

        try
        {
            await _profileFamilyService.DeleteFamilyAsync(familyId, ct);
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
    }
}
