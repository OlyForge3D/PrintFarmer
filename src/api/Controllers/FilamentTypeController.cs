
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Services;
using Farm.Web.Api.Services.Filament;
using Farm.Web.Api.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// Provides endpoints for managing filament types and their temperature presets.
/// </summary>
[ApiController]
[Route("api/filament-types")]
[Tags("Filament Types")]
public class FilamentTypeController : ControllerBase
{
    private readonly IFilamentTypeService _filamentService;
    private readonly IStartupStatus _startupStatus;
    private readonly IUnifiedLoggingService _logger;

    public FilamentTypeController(IFilamentTypeService filamentService, IStartupStatus startupStatus, IUnifiedLoggingService logger)
    {
        _filamentService = filamentService ?? throw new ArgumentNullException(nameof(filamentService));
        _startupStatus = startupStatus ?? throw new ArgumentNullException(nameof(startupStatus));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Gets all available filament types.
    /// </summary>
    /// <param name="ct">Cancellation token for the operation</param>
    /// <returns>List of all filament types ordered by name</returns>
    /// <response code="200">Returns the list of filament types</response>
    /// <response code="503">If the system is still initializing</response>
    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<FilamentTypeDto>), 200)]
    [ProducesResponseType(503)]
    public async Task<ActionResult<IEnumerable<FilamentTypeDto>>> GetFilamentTypesAsync(CancellationToken ct)
    {
        // Ensure initialization is complete to prevent race conditions during startup
        try
        {
            if (!_startupStatus.IsReady)
            {
                return StatusCode(503, new { message = "System is still initializing. Please wait a moment and try again." });
            }
            IReadOnlyList<FilamentTypeDto> list = await _filamentService.GetFilamentTypesAsync(ct);
            return Ok(list);
        }
        catch (InvalidOperationException)
        {
            return StatusCode(503, new { message = "System is still initializing. Please wait a moment and try again." });
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[FilamentTypeController] Exception in GetFilamentTypesAsync: {Message}", ex.Message);
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
    [ProducesResponseType(typeof(FilamentPresetsDto), 200)]
    [ProducesResponseType(503)]
    public async Task<ActionResult<FilamentPresetsDto>> GetFilamentPresetsAsync(CancellationToken ct)
    {
        try
        {
            if (!_startupStatus.IsReady)
            {
                return StatusCode(503, new { message = "System is still initializing. Please wait a moment and try again." });
            }
            FilamentPresetsDto presets = await _filamentService.GetFilamentPresetsAsync(ct);
            return Ok(presets);
        }
        catch (InvalidOperationException)
        {
            return StatusCode(503, new { message = "System is still initializing. Please wait a moment and try again." });
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error in GetFilamentPresetsAsync: {Message}", ex.Message);
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
    [ProducesResponseType(typeof(FilamentTypeDto), 201)]
    [ProducesResponseType(400)]
    [ProducesResponseType(409)]
    public async Task<ActionResult<FilamentTypeDto>> CreateFilamentTypeAsync([FromBody] CreateFilamentTypeRequest request, CancellationToken ct)
    {
        try
        {
            FilamentTypeDto created = await _filamentService.CreateFilamentTypeAsync(request, ct);
            return CreatedAtAction(nameof(GetFilamentTypesAsync), new { id = created.Id }, created);
        }
        catch (ArgumentException ae)
        {
            return BadRequest(ae.Message);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error in CreateFilamentTypeAsync: {Message}", ex.Message);
            return StatusCode(500, new { error = ex.Message });
        }
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
        try
        {
            await _filamentService.UpdateFilamentTypeAsync(id, request, ct);
            return NoContent();
        }
        catch (ArgumentException ae)
        {
            return BadRequest(ae.Message);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error in UpdateFilamentTypeAsync: {Message}", ex.Message);
            return StatusCode(500, new { error = ex.Message });
        }
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
        try
        {
            await _filamentService.DeleteFilamentTypeAsync(id, ct);
            return NoContent();
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error in DeleteFilamentTypeAsync: {Message}", ex.Message);
            return StatusCode(500, new { error = ex.Message });
        }
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
    public async Task<IActionResult> SaveFilamentPresetsAsync([FromBody] FilamentPresetsDto presets, CancellationToken ct)
    {
        try
        {
            await _filamentService.SaveFilamentPresetsAsync(presets, ct);
            return NoContent();
        }
        catch (ArgumentException ae)
        {
            return BadRequest(ae.Message);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error in SaveFilamentPresetsAsync: {Message}", ex.Message);
            return StatusCode(500, new { error = ex.Message });
        }
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
    [ProducesResponseType(typeof(SpoolmanFilamentImportResult), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(503)]
    public async Task<ActionResult<SpoolmanFilamentImportResult>> ImportFromSpoolmanAsync(CancellationToken ct)
    {
        try
        {
            SpoolmanFilamentImportResult result = await _filamentService.ImportFromSpoolmanAsync(ct);
            return Ok(result);
        }
        catch (InvalidOperationException ie)
        {
            return BadRequest(new { message = ie.Message });
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = $"Failed to import filament types from Spoolman: {ex.Message}" });
        }
    }

    // Default temperature heuristics are implemented in FilamentTypeService; controller-local
    // copies were unused and removed to satisfy analyzer warnings.
}
