using Farm.Infrastructure.Services.BedTypes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// Manages bed surface types — used for printer matching, filtering, and auto-dispatch compatibility.
/// </summary>
[ApiController]
[Route("api/bed-types")]
[Authorize]
[Produces("application/json")]
[Tags("Bed Types")]
public class BedTypeController(
    IBedTypeService bedTypeService,
    ILogger<BedTypeController> logger) : ControllerBase
{
    /// <summary>
    /// Lists all bed types with printer counts.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<BedTypeDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<BedTypeDto>>> ListBedTypesAsync(CancellationToken ct)
    {
        try
        {
            IReadOnlyList<BedTypeDto> bedTypes = await bedTypeService.ListAllAsync(ct);
            return Ok(bedTypes);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[BedTypeController] ListBedTypesAsync failed");
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Failed to retrieve bed types" });
        }
    }

    /// <summary>
    /// Gets a bed type by ID.
    /// </summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(BedTypeDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<BedTypeDto>> GetBedTypeAsync(Guid id, CancellationToken ct)
    {
        try
        {
            BedTypeDto? bedType = await bedTypeService.GetByIdAsync(id, ct);
            return bedType is null ? NotFound(new { error = "Bed type not found" }) : Ok(bedType);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[BedTypeController] GetBedTypeAsync failed for {Id}", id);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Failed to retrieve bed type" });
        }
    }

    /// <summary>
    /// Creates a new bed type.
    /// </summary>
    [HttpPost]
    [Authorize(Roles = "farm_admin")]
    [ProducesResponseType(typeof(BedTypeDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<BedTypeDto>> CreateBedTypeAsync(
        [FromBody] CreateBedTypeDto dto,
        CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dto.Name))
            {
                return BadRequest(new { error = "Bed type name is required" });
            }

            BedTypeDto bedType = await bedTypeService.CreateAsync(dto, ct);
            return CreatedAtAction("GetBedType", new { id = bedType.Id }, bedType);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "[BedTypeController] CreateBedTypeAsync conflict: {Message}", ex.Message);
            return Conflict(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[BedTypeController] CreateBedTypeAsync failed");
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Failed to create bed type" });
        }
    }

    /// <summary>
    /// Updates a bed type's name, description, and color.
    /// </summary>
    [HttpPut("{id:guid}")]
    [Authorize(Roles = "farm_admin")]
    [ProducesResponseType(typeof(BedTypeDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<BedTypeDto>> UpdateBedTypeAsync(
        Guid id,
        [FromBody] UpdateBedTypeDto dto,
        CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dto.Name))
            {
                return BadRequest(new { error = "Bed type name is required" });
            }

            BedTypeDto bedType = await bedTypeService.UpdateAsync(id, dto, ct);
            return Ok(bedType);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "[BedTypeController] UpdateBedTypeAsync conflict: {Message}", ex.Message);
            return Conflict(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[BedTypeController] UpdateBedTypeAsync failed for {Id}", id);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Failed to update bed type" });
        }
    }

    /// <summary>
    /// Deletes a bed type. System bed types cannot be deleted.
    /// Printers with this bed type get BedTypeId set to null.
    /// </summary>
    [HttpDelete("{id:guid}")]
    [Authorize(Roles = "farm_admin")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> DeleteBedTypeAsync(Guid id, CancellationToken ct)
    {
        try
        {
            await bedTypeService.DeleteAsync(id, ct);
            return NoContent();
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "[BedTypeController] DeleteBedTypeAsync blocked: {Message}", ex.Message);
            return Conflict(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[BedTypeController] DeleteBedTypeAsync failed for {Id}", id);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Failed to delete bed type" });
        }
    }
}
