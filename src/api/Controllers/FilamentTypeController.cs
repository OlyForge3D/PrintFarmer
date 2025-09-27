using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Web.Api.Services;
using Farm.Web.Api.Services.Startup;
using InfraSettings = Farm.Infrastructure.Settings;
using Shared = Farm.Web.Shared;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// Provides endpoints for managing filament types and their temperature presets.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Tags("Filament Types")]
public class FilamentTypeController(AppDbContext db, StartupStatus startupStatus, SpoolmanService spoolmanService, IAppSettingsService settingsService) : ControllerBase
{
    /// <summary>
    /// Gets all available filament types.
    /// </summary>
    /// <param name="ct">Cancellation token for the operation</param>
    /// <returns>List of all filament types ordered by name</returns>
    /// <response code="200">Returns the list of filament types</response>
    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<Shared.FilamentTypeDto>), 200)]
    [ProducesResponseType(503)]
    public async Task<ActionResult<IEnumerable<Shared.FilamentTypeDto>>> GetFilamentTypesAsync(CancellationToken ct)
    {
        // Ensure initialization is complete to prevent race conditions during startup
        if (!startupStatus.IsReady)
        {
            return StatusCode(503, new { message = "System is still initializing. Please wait a moment and try again." });
        }

        List<Shared.FilamentTypeDto> list = await db.FilamentTypes.AsNoTracking().OrderBy(f => f.Name)
            .Select(f => new Shared.FilamentTypeDto(f.Id, f.Name, new Shared.TempTargets(f.DefaultHotendTemp, f.DefaultBedTemp)))
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
    [ProducesResponseType(typeof(Shared.FilamentPresetsDto), 200)]
    [ProducesResponseType(503)]
    public Task<ActionResult<Shared.FilamentPresetsDto>> GetFilamentPresetsAsync(CancellationToken ct)
    {
        // Ensure initialization is complete to prevent race conditions during startup
        if (!startupStatus.IsReady)
        {
            return Task.FromResult<ActionResult<Shared.FilamentPresetsDto>>(
                StatusCode(503, new { message = "System is still initializing. Please wait a moment and try again." })
            );
        }
        // Return presets from AppSettings
        var presets = settingsService.Current.FilamentPresets;
        Shared.FilamentPresetsDto sharedPresets = presets == null
            ? new Shared.FilamentPresetsDto(new Dictionary<string, Shared.TempTargets>())
            : new Shared.FilamentPresetsDto(
                presets.Presets.ToDictionary(
                    kvp => kvp.Key,
                    kvp => new Shared.TempTargets(kvp.Value.Hotend, kvp.Value.Bed)
                )
            );
        return Task.FromResult<ActionResult<Shared.FilamentPresetsDto>>(Ok(sharedPresets));
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

        db.FilamentTypes.Add(filamentType);
        await db.SaveChangesAsync(ct);

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
        FilamentType? filamentType = await db.FilamentTypes.FindAsync([id], ct);
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
    public async Task<IActionResult> SaveFilamentPresetsAsync([FromBody] Shared.FilamentPresetsDto presets, CancellationToken ct)
    {
        if (presets?.Presets == null)
        {
            return BadRequest("Presets are required");
        }
        // Save presets to AppSettings and persist
        var settings = settingsService.Current;
        // Convert to InfraSettings.TempTargets for storage
        settings.FilamentPresets = new InfraSettings.FilamentPresetsDto(
            presets.Presets.ToDictionary(
                kvp => kvp.Key,
                kvp => new InfraSettings.TempTargets(
                    (int)(kvp.Value.Hotend ?? 0),
                    (int)(kvp.Value.Bed ?? 0)
                )
            )
        );
        await settingsService.SaveAsync(settings, ct);
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
        var config = spoolmanService.GetConfig() as Shared.SpoolmanConfigDto;
        if (config is null || string.IsNullOrWhiteSpace(config.BaseUrl))
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
                    uniqueMaterials.Add(material.Name.Trim());
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

                db.FilamentTypes.Add(newFilamentType);
                importedNames.Add(materialName);
                importedCount++;
            }

            await db.SaveChangesAsync(ct);

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
        string upperMaterial = material.ToUpperInvariant();
        if (upperMaterial.Contains("PLA"))
        { return 205; }
        if (upperMaterial.Contains("ABS"))
        { return 230; }
        if (upperMaterial.Contains("PETG"))
        { return 240; }
        if (upperMaterial.Contains("ASA"))
        { return 245; }
        if (upperMaterial.Contains("PC") || upperMaterial.Contains("POLYCARBONATE"))
        { return 260; }
        if (upperMaterial.Contains("PCTG"))
        { return 235; }
        if (upperMaterial.Contains("TPU") || upperMaterial.Contains("FLEX"))
        { return 220; }
        if (upperMaterial.Contains("WOOD"))
        { return 210; }
        if (upperMaterial.Contains("NYLON"))
        { return 250; }
        if (upperMaterial.Contains("CARBON"))
        { return 260; }
        return 210; // Default for unknown materials
    }

    /// <summary>
    /// Gets reasonable default bed temperature for a material name.
    /// </summary>
    private static int GetDefaultBedTemp(string material)
    {
        string upperMaterial = material.ToUpperInvariant();
        if (upperMaterial.Contains("PLA"))
        { return 60; }
        if (upperMaterial.Contains("ABS"))
        { return 100; }
        if (upperMaterial.Contains("PETG"))
        { return 85; }
        if (upperMaterial.Contains("ASA"))
        { return 100; }
        if (upperMaterial.Contains("PC") || upperMaterial.Contains("POLYCARBONATE"))
        { return 110; }
        if (upperMaterial.Contains("PCTG"))
        { return 80; }
        if (upperMaterial.Contains("TPU") || upperMaterial.Contains("FLEX"))
        { return 60; }
        if (upperMaterial.Contains("WOOD"))
        { return 65; }
        if (upperMaterial.Contains("NYLON"))
        { return 80; }
        if (upperMaterial.Contains("CARBON"))
        { return 100; }
        return 70; // Default for unknown materials
    }
}
