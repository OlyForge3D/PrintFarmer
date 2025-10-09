
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Services;
using Farm.Web.Api.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Shared = Farm.Web.Shared;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// Provides endpoints for managing filament types and their temperature presets.
/// </summary>
[ApiController]
[Route("api/filament-types")]
[Tags("Filament Types")]
public class FilamentTypeController(AppDbContext db, Farm.Web.Api.Services.Interfaces.IStartupStatus startupStatus, ISpoolmanService spoolmanService, IUnifiedLoggingService logger) : ControllerBase
{
    private readonly AppDbContext db = db;
    private readonly Farm.Web.Api.Services.Interfaces.IStartupStatus startupStatus = startupStatus;
    private readonly ISpoolmanService spoolmanService = spoolmanService;
    private readonly IUnifiedLoggingService logger = logger;

    /// <summary>
    /// Gets all available filament types.
    /// </summary>
    /// <param name="ct">Cancellation token for the operation</param>
    /// <returns>List of all filament types ordered by name</returns>
    /// <response code="200">Returns the list of filament types</response>
    /// <response code="503">If the system is still initializing</response>
    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<Shared.FilamentTypeDto>), 200)]
    [ProducesResponseType(503)]
    public async Task<ActionResult<IEnumerable<Shared.FilamentTypeDto>>> GetFilamentTypesAsync(CancellationToken ct)
    {
        // Ensure initialization is complete to prevent race conditions during startup
        try
        {
            if (!startupStatus.IsReady)
            {
                return StatusCode(503, new { message = "System is still initializing. Please wait a moment and try again." });
            }

            // Uncomment the next line to force a test exception for log verification
            //throw new InvalidOperationException("[FilamentTypeController] Forced test exception for error logging verification.");

            List<Shared.FilamentTypeDto> list = await db.FilamentTypes.AsNoTracking().OrderBy(f => f.Name)
                .Select(f => new Shared.FilamentTypeDto(f.Id, f.Name, new Shared.TempTargets(f.DefaultHotendTemp, f.DefaultBedTemp)))
                .ToListAsync(ct);
            return Ok(list);
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "[FilamentTypeController] TEST ERROR LOG: Exception captured in GetFilamentTypesAsync: {Message}", ex.Message);
            return StatusCode(500, new { error = ex.Message, detail = ex.ToString() });
        }
    }

    /// <summary>
    /// Gets filament types as a dictionary for presets (from the database).
    /// </summary>
    /// <param name="ct">Cancellation token for the operation</param>
    /// <returns>Dictionary of filament type names to temperature targets</returns>
    /// <response code="200">Returns the filament presets dictionary</response>
    /// <response code="503">If the system is still initializing</response>
    [HttpGet("presets")]
    [ProducesResponseType(typeof(Shared.FilamentPresetsDto), 200)]
    [ProducesResponseType(503)]
    public async Task<ActionResult<Shared.FilamentPresetsDto>> GetFilamentPresetsAsync(CancellationToken ct)
    {
        try
        {
            if (!startupStatus.IsReady)
            {
                return StatusCode(503, new { message = "System is still initializing. Please wait a moment and try again." });
            }
            Dictionary<string, Shared.TempTargets> presets = await db.FilamentTypes
                .AsNoTracking()
                .OrderBy(f => f.Name)
                .ToDictionaryAsync(
                    f => f.Name,
                    f => new Shared.TempTargets(f.DefaultHotendTemp, f.DefaultBedTemp), ct);
            return Ok(new Shared.FilamentPresetsDto(presets));
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Error in GetFilamentPresetsAsync: {Message}", ex.Message);
            return StatusCode(500, new { error = ex.Message, detail = ex.ToString() });
        }
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
    [ProducesResponseType(typeof(Shared.FilamentTypeDto), 201)]
    [ProducesResponseType(400)]
    [ProducesResponseType(409)]
    public async Task<ActionResult<Shared.FilamentTypeDto>> CreateFilamentTypeAsync([FromBody] Shared.CreateFilamentTypeRequest request, CancellationToken ct)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest("Name is required");
        }

        string trimmed = request.Name.Trim();
        FilamentType? existing = await db.FilamentTypes.AsNoTracking().FirstOrDefaultAsync(f => f.Name == trimmed, ct);
        if (existing is not null)
        {
            return Conflict(new Shared.FilamentTypeDto(existing.Id, existing.Name, new Shared.TempTargets(existing.DefaultHotendTemp, existing.DefaultBedTemp)));
        }

        FilamentType filamentType = new()
        {
            Id = Guid.NewGuid(),
            Name = trimmed,
            DefaultHotendTemp = request.DefaultTemperatures.Hotend,
            DefaultBedTemp = request.DefaultTemperatures.Bed,
            CreatedAt = DateTime.UtcNow
        };

        _ = db.FilamentTypes.Add(filamentType);
        _ = await db.SaveChangesAsync(ct);

        return CreatedAtAction(nameof(GetFilamentTypesAsync), new { id = filamentType.Id },
            new Shared.FilamentTypeDto(filamentType.Id, filamentType.Name, new Shared.TempTargets(filamentType.DefaultHotendTemp, filamentType.DefaultBedTemp)));
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
    public async Task<IActionResult> UpdateFilamentTypeAsync(Guid id, [FromBody] Shared.UpdateFilamentTypeRequest request, CancellationToken ct)
    {
        if (request is null)
        {
            return BadRequest("Request body is required");
        }

        FilamentType? filamentType = await db.FilamentTypes.FindAsync([id], ct);
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

        _ = await db.SaveChangesAsync(ct);
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
        FilamentType? filamentType = await db.FilamentTypes.FindAsync([id], ct);
        if (filamentType is null)
        {
            return NotFound();
        }

        _ = db.FilamentTypes.Remove(filamentType);
        _ = await db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>
    /// Saves filament presets from a dictionary format (updates the database).
    /// </summary>
    /// <param name="presets">The filament presets to save</param>
    /// <param name="ct">Cancellation token for the operation</param>
    /// <returns>No content if successful</returns>
    /// <response code="204">If the presets were saved successfully</response>
    /// <response code="400">If the presets data is invalid</response>
    [HttpPost("presets")]
    [ProducesResponseType(204)]
    [ProducesResponseType(400)]
    public async Task<IActionResult> SaveFilamentPresetsAsync([FromBody] Shared.FilamentPresetsDto presets, CancellationToken ct)
    {
        if (presets?.Presets == null)
        {
            return BadRequest("Presets are required");
        }
        foreach (KeyValuePair<string, Shared.TempTargets> kvp in presets.Presets)
        {
            string name = kvp.Key.Trim();
            Shared.TempTargets tempTargets = kvp.Value;
            FilamentType? filamentType = await db.FilamentTypes.FirstOrDefaultAsync(f => f.Name == name, ct);
            if (filamentType == null)
            {
                filamentType = new FilamentType
                {
                    Id = Guid.NewGuid(),
                    Name = name,
                    DefaultHotendTemp = tempTargets.Hotend,
                    DefaultBedTemp = tempTargets.Bed,
                    CreatedAt = DateTime.UtcNow
                };
                _ = db.FilamentTypes.Add(filamentType);
            }
            else
            {
                filamentType.DefaultHotendTemp = tempTargets.Hotend;
                filamentType.DefaultBedTemp = tempTargets.Bed;
            }
        }
        _ = await db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>
    /// Imports unique filament types from Spoolman's /api/v1/material endpoint to maintain parity between applications.
    /// </summary>
    /// <param name="ct">Cancellation token for the operation</param>
    /// <returns>Import result with counts of imported and skipped types</returns>
    /// <response code="200">Returns the import results</response>
    /// <response code="400">If Spoolman is not configured</response>
    /// <response code="503">If system is still initializing</response>
    [HttpPost("import-from-spoolman")]
    [ProducesResponseType(typeof(Shared.SpoolmanFilamentImportResult), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(503)]
    public async Task<ActionResult<Shared.SpoolmanFilamentImportResult>> ImportFromSpoolmanAsync(CancellationToken ct)
    {
        // Ensure initialization is complete to prevent race conditions during startup
        if (!startupStatus.IsReady)
        {
            return StatusCode(503, new { message = "System is still initializing. Please wait a moment and try again." });
        }

        // Check if Spoolman is configured
        if (spoolmanService.GetConfig() is not Shared.SpoolmanConfigDto config || string.IsNullOrWhiteSpace(config.BaseUrl))
        {
            return BadRequest(new { message = "Spoolman is not configured. Please configure Spoolman integration first." });
        }

        try
        {
            // Get all materials from Spoolman's material endpoint (more direct and efficient)
            IReadOnlyList<Shared.SpoolmanMaterialDto> materials = await spoolmanService.ListMaterialsAsync(ct);

            // Extract unique material names (filament types)
            HashSet<string> uniqueMaterials = new(StringComparer.OrdinalIgnoreCase);
            foreach (Shared.SpoolmanMaterialDto material in materials)
            {
                if (!string.IsNullOrWhiteSpace(material.Name))
                {
                    _ = uniqueMaterials.Add(material.Name.Trim());
                }
            }

            // Get existing filament types from our database
            List<string> existingTypes = await db.FilamentTypes
                .Select(ft => ft.Name)
                .ToListAsync(ct);

            HashSet<string> existingTypesSet = new(existingTypes, StringComparer.OrdinalIgnoreCase);

            // Import new filament types
            int importedCount = 0;
            int skippedCount = 0;
            List<string> importedNames = new();

            foreach (string materialName in uniqueMaterials.OrderBy(m => m))
            {
                if (existingTypesSet.Contains(materialName))
                {
                    skippedCount++;
                    continue;
                }

                // Create new filament type with reasonable defaults
                // Note: We use reasonable temperature defaults since Spoolman materials may not include temperature info
                FilamentType newFilamentType = new()
                {
                    Id = Guid.NewGuid(),
                    Name = materialName,
                    DefaultHotendTemp = GetDefaultHotendTemp(materialName),
                    DefaultBedTemp = GetDefaultBedTemp(materialName),
                    CreatedAt = DateTime.UtcNow
                };

                _ = db.FilamentTypes.Add(newFilamentType);
                importedNames.Add(materialName);
                importedCount++;
            }

            _ = await db.SaveChangesAsync(ct);

            return Ok(new Shared.SpoolmanFilamentImportResult(
                ImportedCount: importedCount,
                SkippedCount: skippedCount,
                TotalSpoolmanMaterials: uniqueMaterials.Count,
                ImportedNames: importedNames.ToArray()
            ));
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = $"Failed to import filament types from Spoolman: {ex.Message}" });
        }
    }

    /// <summary>
    /// Gets reasonable default hotend temperature for a material name.
    /// </summary>
    private static int GetDefaultHotendTemp(string material)
    {
        // Use OrdinalIgnoreCase comparisons directly without allocating an upper-case copy
        if (material.Contains("PLA", StringComparison.OrdinalIgnoreCase))
        { return 205; }
        if (material.Contains("ABS", StringComparison.OrdinalIgnoreCase))
        { return 230; }
        if (material.Contains("PETG", StringComparison.OrdinalIgnoreCase))
        { return 240; }
        if (material.Contains("ASA", StringComparison.OrdinalIgnoreCase))
        { return 245; }
        if (material.Contains("PC", StringComparison.OrdinalIgnoreCase) || material.Contains("POLYCARBONATE", StringComparison.OrdinalIgnoreCase))
        { return 260; }
        if (material.Contains("PCTG", StringComparison.OrdinalIgnoreCase))
        { return 235; }
        if (material.Contains("TPU", StringComparison.OrdinalIgnoreCase) || material.Contains("FLEX", StringComparison.OrdinalIgnoreCase))
        { return 220; }
        if (material.Contains("WOOD", StringComparison.OrdinalIgnoreCase))
        { return 210; }
        if (material.Contains("NYLON", StringComparison.OrdinalIgnoreCase))
        { return 250; }
        if (material.Contains("CARBON", StringComparison.OrdinalIgnoreCase))
        { return 260; }
        return 210; // Default for unknown materials
    }

    /// <summary>
    /// Gets reasonable default bed temperature for a material name.
    /// </summary>
    private static int GetDefaultBedTemp(string material)
    {
        // Prefer direct OrdinalIgnoreCase checks instead of material.ToUpperInvariant()
        if (material.Contains("PLA", StringComparison.OrdinalIgnoreCase))
        { return 60; }
        if (material.Contains("ABS", StringComparison.OrdinalIgnoreCase))
        { return 100; }
        if (material.Contains("PETG", StringComparison.OrdinalIgnoreCase))
        { return 85; }
        if (material.Contains("ASA", StringComparison.OrdinalIgnoreCase))
        { return 100; }
        if (material.Contains("PC", StringComparison.OrdinalIgnoreCase) || material.Contains("POLYCARBONATE", StringComparison.OrdinalIgnoreCase))
        { return 110; }
        if (material.Contains("PCTG", StringComparison.OrdinalIgnoreCase))
        { return 80; }
        if (material.Contains("TPU", StringComparison.OrdinalIgnoreCase) || material.Contains("FLEX", StringComparison.OrdinalIgnoreCase))
        { return 60; }
        if (material.Contains("WOOD", StringComparison.OrdinalIgnoreCase))
        { return 65; }
        if (material.Contains("NYLON", StringComparison.OrdinalIgnoreCase))
        { return 80; }
        if (material.Contains("CARBON", StringComparison.OrdinalIgnoreCase))
        { return 100; }
        return 70; // Default for unknown materials
    }
}
