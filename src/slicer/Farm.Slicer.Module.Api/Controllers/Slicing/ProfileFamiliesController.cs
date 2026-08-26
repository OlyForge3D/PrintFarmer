using Farm.Infrastructure.Security;
using Farm.Slicer.Module.Api.Filters;
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
            return Created($"/api/slicer/profiles/families/{result.FamilyId:D}", result);
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
}
