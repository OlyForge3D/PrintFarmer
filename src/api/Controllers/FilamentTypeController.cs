using Farm.Web.Api.Data;
using Farm.Web.Api.Domain;
using Farm.Web.Shared;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// Provides endpoints for managing filament types and their temperature presets.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Tags("Filament Types")]
public class FilamentTypeController(AppDbContext db) : ControllerBase
{
    /// <summary>
    /// Gets all available filament types.
    /// </summary>
    /// <param name="ct">Cancellation token for the operation</param>
    /// <returns>List of all filament types ordered by name</returns>
    /// <response code="200">Returns the list of filament types</response>
    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<FilamentTypeDto>), 200)]
    public async Task<ActionResult<IEnumerable<FilamentTypeDto>>> GetFilamentTypesAsync(CancellationToken ct)
    {
        var list = await db.FilamentTypes.AsNoTracking().OrderBy(f => f.Name)
            .Select(f => new FilamentTypeDto(f.Id, f.Name, new TempTargets(f.DefaultHotendTemp, f.DefaultBedTemp)))
            .ToListAsync(ct);
        return Ok(list);
    }

    /// <summary>
    /// Gets filament types as a dictionary for presets.
    /// </summary>
    /// <param name="ct">Cancellation token for the operation</param>
    /// <returns>Dictionary of filament type names to temperature targets</returns>
    /// <response code="200">Returns the filament presets dictionary</response>
    [HttpGet("presets")]
    [ProducesResponseType(typeof(FilamentPresetsDto), 200)]
    public async Task<ActionResult<FilamentPresetsDto>> GetFilamentPresetsAsync(CancellationToken ct)
    {
        var filamentTypes = await db.FilamentTypes.AsNoTracking()
            .ToDictionaryAsync(f => f.Name.ToLowerInvariant(), f => new TempTargets(f.DefaultHotendTemp, f.DefaultBedTemp), ct);
        return Ok(new FilamentPresetsDto(filamentTypes));
    }

    /// <summary>
    /// Creates a new filament type.
    /// </summary>
    /// <param name="request">The filament type details to create</param>
    /// <param name="ct">Cancellation token for the operation</param>
    /// <returns>The created filament type</returns>
    /// <response code="201">Returns the newly created filament type</response>
    /// <response code="400">If the filament type data is invalid</response>
    /// <response code="409">If a filament type with the same name already exists</response>
    [HttpPost]
    [ProducesResponseType(typeof(FilamentTypeDto), 201)]
    [ProducesResponseType(400)]
    [ProducesResponseType(409)]
    public async Task<ActionResult<FilamentTypeDto>> CreateFilamentTypeAsync([FromBody] CreateFilamentTypeRequest request, CancellationToken ct)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest("Name is required");
        }

        var trimmed = request.Name.Trim();
        var existing = await db.FilamentTypes.AsNoTracking().FirstOrDefaultAsync(f => f.Name == trimmed, ct);
        if (existing is not null)
        {
            return Conflict(new FilamentTypeDto(existing.Id, existing.Name, new TempTargets(existing.DefaultHotendTemp, existing.DefaultBedTemp)));
        }

        var filamentType = new FilamentType
        {
            Id = Guid.NewGuid(),
            Name = trimmed,
            DefaultHotendTemp = request.DefaultTemperatures.Hotend,
            DefaultBedTemp = request.DefaultTemperatures.Bed,
            CreatedAt = DateTime.UtcNow
        };

        db.FilamentTypes.Add(filamentType);
        await db.SaveChangesAsync(ct);

        return CreatedAtAction(nameof(GetFilamentTypesAsync), new { id = filamentType.Id },
            new FilamentTypeDto(filamentType.Id, filamentType.Name, new TempTargets(filamentType.DefaultHotendTemp, filamentType.DefaultBedTemp)));
    }

    /// <summary>
    /// Updates an existing filament type.
    /// </summary>
    /// <param name="id">The ID of the filament type to update</param>
    /// <param name="request">The updated filament type details</param>
    /// <param name="ct">Cancellation token for the operation</param>
    /// <returns>No content if successful</returns>
    /// <response code="204">If the filament type was updated successfully</response>
    /// <response code="400">If the filament type data is invalid</response>
    /// <response code="404">If the filament type was not found</response>
    [HttpPut("{id:guid}")]
    [ProducesResponseType(204)]
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> UpdateFilamentTypeAsync(Guid id, [FromBody] UpdateFilamentTypeRequest request, CancellationToken ct)
    {
        if (request is null)
        {
            return BadRequest("Request body is required");
        }

        var filamentType = await db.FilamentTypes.FindAsync([id], ct);
        if (filamentType is null)
        {
            return NotFound();
        }

        if (!string.IsNullOrWhiteSpace(request.Name))
        {
            filamentType.Name = request.Name.Trim();
        }

        filamentType.DefaultHotendTemp = request.DefaultTemperatures.Hotend;
        filamentType.DefaultBedTemp = request.DefaultTemperatures.Bed;

        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>
    /// Deletes a filament type.
    /// </summary>
    /// <param name="id">The ID of the filament type to delete</param>
    /// <param name="ct">Cancellation token for the operation</param>
    /// <returns>No content if successful</returns>
    /// <response code="204">If the filament type was deleted successfully</response>
    /// <response code="404">If the filament type was not found</response>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(204)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> DeleteFilamentTypeAsync(Guid id, CancellationToken ct)
    {
        var filamentType = await db.FilamentTypes.FindAsync([id], ct);
        if (filamentType is null)
        {
            return NotFound();
        }

        db.FilamentTypes.Remove(filamentType);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>
    /// Saves filament presets from a dictionary format (for backward compatibility).
    /// </summary>
    /// <param name="presets">The filament presets to save</param>
    /// <param name="ct">Cancellation token for the operation</param>
    /// <returns>No content if successful</returns>
    /// <response code="204">If the presets were saved successfully</response>
    /// <response code="400">If the presets data is invalid</response>
    [HttpPost("presets")]
    [ProducesResponseType(204)]
    [ProducesResponseType(400)]
    public async Task<IActionResult> SaveFilamentPresetsAsync([FromBody] FilamentPresetsDto presets, CancellationToken ct)
    {
        if (presets?.Presets == null)
        {
            return BadRequest("Presets are required");
        }

        foreach (var preset in presets.Presets)
        {
            var name = preset.Key.Trim();
            var existing = await db.FilamentTypes.FirstOrDefaultAsync(f => f.Name == name, ct);

            if (existing != null)
            {
                existing.DefaultHotendTemp = preset.Value.Hotend;
                existing.DefaultBedTemp = preset.Value.Bed;
            }
            else
            {
                var newType = new FilamentType
                {
                    Id = Guid.NewGuid(),
                    Name = name,
                    DefaultHotendTemp = preset.Value.Hotend,
                    DefaultBedTemp = preset.Value.Bed,
                    CreatedAt = DateTime.UtcNow
                };
                db.FilamentTypes.Add(newType);
            }
        }

        await db.SaveChangesAsync(ct);
        return NoContent();
    }
}
