using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Exceptions;
using Farm.Web.Api.Controllers.Requests;

using Farm.Web.Api.Infrastructure.Normalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// Provides endpoints for managing printer manufacturer and model catalog data.
/// </summary>
[ApiController]
[Route("api/catalog")]
[Tags("Catalog")]
[Authorize]
public class CatalogController(
    ILogger<CatalogController> unifiedLoggingService,
    Services.Catalog.ICatalogService catalogService) : ControllerBase
{
    private readonly ILogger<CatalogController> _unifiedLoggingService = unifiedLoggingService;
    private readonly Services.Catalog.ICatalogService _catalogService = catalogService;

    /// <summary>
    /// Gets all available printer manufacturers.
    /// </summary>
    /// <param name="ct">Cancellation token for the operation</param>
    /// <returns>List of all printer manufacturers ordered by name</returns>
    /// <response code="200">Returns the list of manufacturers</response>
    [AllowAnonymous]
    [HttpGet("manufacturers")]
    [ProducesResponseType(typeof(IEnumerable<ManufacturerDto>), 200)]
    public async Task<ActionResult<IEnumerable<ManufacturerDto>>> GetManufacturersAsync(CancellationToken ct)
    {
        try
        {
            (IReadOnlyList<ManufacturerDto>? list, string? _) = await _catalogService.GetManufacturersAsync(ct);
            return Ok(list);
        }
        catch (Exception ex)
        {
            // Log the error with as much context as possible via injected unified logging service
            _unifiedLoggingService?.LogError(ex, "[CatalogController] GetManufacturersAsync failed: {Message}", ex.Message);

            // Optionally, include more context in the error response
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Failed to retrieve manufacturers", details = ex.ToString() });
        }
    }

    [AllowAnonymous]
    [HttpGet("manufacturers/{id:guid}", Name = "GetManufacturerById")]
    [ProducesResponseType(typeof(ManufacturerDto), 200)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<ManufacturerDto>> GetManufacturerByIdAsync(Guid id, CancellationToken ct)
    {
        ManufacturerDto? dto = await _catalogService.GetManufacturerByIdAsync(id, ct);
        return dto is null ? NotFound() : Ok(dto);
    }

    /// <summary>
    /// Creates a new printer manufacturer.
    /// </summary>
    /// <param name="request">Payload containing the manufacturer name</param>
    /// <param name="ct">Cancellation token for the operation</param>
    /// <returns>The created manufacturer</returns>
    /// <response code="201">Returns the newly created manufacturer</response>
    /// <response code="400">If the manufacturer name is invalid or empty</response>
    /// <response code="409">If a manufacturer with the same name already exists</response>
    [Authorize(Roles = "farm_admin")]
    [HttpPost("manufacturers")]
    [ProducesResponseType(typeof(ManufacturerDto), 201)]
    [ProducesResponseType(400)]
    [ProducesResponseType(409)]
    public async Task<ActionResult<ManufacturerDto>> CreateManufacturerAsync([FromBody] CreateManufacturerRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest("Name is required");
        }

        // Normalize via shared helper for consistent rule across API & seeding
        ManufacturerDto dto = await _catalogService.CreateManufacturerAsync(request.Name, request.Url, request.Description, ct);

        // The service handles normalization and cache invalidation; include normalized header only if different
        string normalized = dto.Name;
        if (!string.Equals(request.Name, normalized, StringComparison.Ordinal))
        {
            Response.Headers["X-Normalized-Name"] = normalized;
        }

        return CreatedAtRoute("GetManufacturerById", new { id = dto.Id }, dto);
    }

    [AllowAnonymous]
    [HttpGet("printer-models")]
    [ProducesResponseType(typeof(IEnumerable<PrinterModelDto>), 200)]
    public async Task<ActionResult<IEnumerable<PrinterModelDto>>> GetPrinterModelsAsync([FromQuery] Guid? manufacturerId, CancellationToken ct)
    {
        (IReadOnlyList<PrinterModelDto>? list, string? _) = await _catalogService.GetModelsAsync(manufacturerId, ct);
        return Ok(list);
    }

    [AllowAnonymous]
    [HttpGet("printer-models/{id:guid}", Name = "GetPrinterModelById")]
    [ProducesResponseType(typeof(PrinterModelDto), 200)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<PrinterModelDto>> GetPrinterModelByIdAsync(Guid id, CancellationToken ct)
    {
        PrinterModelDto? dto = await _catalogService.GetModelByIdAsync(id, ct);
        return dto is null ? NotFound() : Ok(dto);
    }

    [Authorize(Roles = "farm_admin")]
    [HttpPost("printer-models")]
    [ProducesResponseType(typeof(PrinterModelDto), 201)]
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    [ProducesResponseType(409)]
    public async Task<ActionResult<PrinterModelDto>> CreatePrinterModelAsync([FromBody] CreateModelRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);
        if (req.ManufacturerId == Guid.Empty)
        {
            return BadRequest("ManufacturerId is required");
        }

        if (string.IsNullOrWhiteSpace(req.Name))
        {
            return BadRequest("Name is required");
        }

        try
        {
            PrinterModelDto created = await _catalogService.CreateModelAsync(req, ct);
            if (!string.Equals(req.Name, created.Name, StringComparison.Ordinal))
            {
                Response.Headers["X-Normalized-Name"] = created.Name;
            }

            return CreatedAtRoute("GetPrinterModelById", new { id = created.Id }, created);
        }
        catch (KeyNotFoundException)
        {
            return NotFound("Manufacturer not found");
        }
        catch (DuplicateEntityException dex)
        {
            if (!string.IsNullOrEmpty(dex.NormalizedName))
            {
                Response.Headers["X-Normalized-Name"] = dex.NormalizedName;
            }

            return Conflict(new { error = dex.Message });
        }
    }

    [Authorize(Roles = "farm_admin")]
    [HttpPut("printer-models/{id:guid}")]
    [ProducesResponseType(typeof(PrinterModelDto), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> UpdateModelAsync(Guid id, [FromBody] UpdateModelRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);
        try
        {
            PrinterModelDto? updated = await _catalogService.UpdateModelAsync(id, req, ct);
            return updated is null ? NotFound() : Ok(updated);
        }
        catch (Exception ex)
        {
            _unifiedLoggingService?.LogError(ex, "[CatalogController] UpdateModelAsync failed: {Message}", ex.Message);
            throw;
        }
    }

    [Authorize(Roles = "farm_admin")]
    [HttpDelete("printer-models/{id:guid}")]
    [ProducesResponseType(204)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> DeleteModelAsync(Guid id, CancellationToken ct)
    {
        try
        {
            await _catalogService.DeleteModelAsync(id, ct);
            return NoContent();
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (Exception ex)
        {
            _unifiedLoggingService?.LogError(ex, "[CatalogController] DeleteModelAsync failed: {Message}", ex.Message);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Failed to delete model" });
        }
    }

    /// <summary>
    /// Gets all slicer model name aliases (OrcaSlicer, PrusaSlicer names) for a printer model.
    /// </summary>
    /// <param name="modelId">The printer model ID</param>
    /// <param name="ct">Cancellation token for the operation</param>
    /// <returns>List of slicer name aliases for the model</returns>
    /// <response code="200">Returns the list of aliases</response>
    /// <response code="404">Model not found</response>
    [AllowAnonymous]
    [HttpGet("printer-models/{modelId:guid}/aliases")]
    [ProducesResponseType(typeof(IEnumerable<SlicerModelAliasDto>), 200)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<IEnumerable<SlicerModelAliasDto>>> GetModelAliasesAsync(Guid modelId, CancellationToken ct)
    {
        try
        {
            IEnumerable<SlicerModelAliasDto> aliases = await _catalogService.GetModelAliasesAsync(modelId, ct);
            return Ok(aliases);
        }
        catch (KeyNotFoundException)
        {
            return NotFound(new { error = "Model not found" });
        }
        catch (Exception ex)
        {
            _unifiedLoggingService?.LogError(ex, "[CatalogController] GetModelAliasesAsync failed: {Message}", ex.Message);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Failed to retrieve model aliases" });
        }
    }

    /// <summary>
    /// Updates slicer model name aliases for a printer model.
    /// </summary>
    /// <param name="modelId">The printer model ID</param>
    /// <param name="request">List of slicer aliases to set</param>
    /// <param name="ct">Cancellation token for the operation</param>
    /// <returns>Updated list of aliases</returns>
    /// <response code="200">Returns the updated aliases</response>
    /// <response code="404">Model not found</response>
    [Authorize(Roles = "farm_admin")]
    [HttpPut("printer-models/{modelId:guid}/aliases")]
    [ProducesResponseType(typeof(IEnumerable<SlicerModelAliasDto>), 200)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<IEnumerable<SlicerModelAliasDto>>> UpdateModelAliasesAsync(Guid modelId, [FromBody] UpdateModelAliasesRequest request, CancellationToken ct)
    {
        try
        {
            IEnumerable<SlicerModelAliasDto> aliases = await _catalogService.UpdateModelAliasesAsync(modelId, request.OrcaSlicerNames ?? new List<string>(), request.PrusaSlicerNames ?? new List<string>(), ct);
            return Ok(aliases);
        }
        catch (KeyNotFoundException)
        {
            return NotFound(new { error = "Model not found" });
        }
        catch (Exception ex)
        {
            _unifiedLoggingService?.LogError(ex, "[CatalogController] UpdateModelAliasesAsync failed: {Message}", ex.Message);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Failed to update model aliases" });
        }
    }

    // ============ Component Model Endpoints ============

    /// <summary>
    /// Gets all available hotend models.
    /// </summary>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>List of all hotend model definitions</returns>
    [AllowAnonymous]
    [HttpGet("hotends")]
    [ProducesResponseType(typeof(IEnumerable<HotendModelDto>), 200)]
    public async Task<ActionResult<IEnumerable<HotendModelDto>>> GetHotendsAsync(CancellationToken ct)
    {
        try
        {
            IReadOnlyList<HotendModelDto> hotends = await _catalogService.GetHotendModelsAsync(ct);
            return Ok(hotends);
        }
        catch (Exception ex)
        {
            _unifiedLoggingService?.LogError(ex, "[CatalogController] GetHotendsAsync failed: {Message}", ex.Message);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Failed to retrieve hotend models" });
        }
    }

    /// <summary>
    /// Gets all available extruder models.
    /// </summary>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>List of all extruder model definitions</returns>
    [AllowAnonymous]
    [HttpGet("extruders")]
    [ProducesResponseType(typeof(IEnumerable<ExtruderModelDto>), 200)]
    public async Task<ActionResult<IEnumerable<ExtruderModelDto>>> GetExtrudersAsync(CancellationToken ct)
    {
        try
        {
            IReadOnlyList<ExtruderModelDto> extruders = await _catalogService.GetExtruderModelsAsync(ct);
            return Ok(extruders);
        }
        catch (Exception ex)
        {
            _unifiedLoggingService?.LogError(ex, "[CatalogController] GetExtrudersAsync failed: {Message}", ex.Message);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Failed to retrieve extruder models" });
        }
    }

    /// <summary>
    /// Gets all available toolhead models.
    /// </summary>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>List of all toolhead model definitions</returns>
    [AllowAnonymous]
    [HttpGet("toolheads")]
    [ProducesResponseType(typeof(IEnumerable<ToolheadModelDto>), 200)]
    public async Task<ActionResult<IEnumerable<ToolheadModelDto>>> GetToolheadsAsync(CancellationToken ct)
    {
        try
        {
            IReadOnlyList<ToolheadModelDto> toolheads = await _catalogService.GetToolheadModelsAsync(ct);
            return Ok(toolheads);
        }
        catch (Exception ex)
        {
            _unifiedLoggingService?.LogError(ex, "[CatalogController] GetToolheadsAsync failed: {Message}", ex.Message);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Failed to retrieve toolhead models" });
        }
    }

    /// <summary>
    /// Gets all available nozzle models.
    /// </summary>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>List of all nozzle model definitions</returns>
    [AllowAnonymous]
    [HttpGet("nozzles")]
    [ProducesResponseType(typeof(IEnumerable<NozzleModelDto>), 200)]
    public async Task<ActionResult<IEnumerable<NozzleModelDto>>> GetNozzlesAsync(CancellationToken ct)
    {
        try
        {
            IReadOnlyList<NozzleModelDto> nozzles = await _catalogService.GetNozzleModelsAsync(ct);
            return Ok(nozzles);
        }
        catch (Exception ex)
        {
            _unifiedLoggingService?.LogError(ex, "[CatalogController] GetNozzlesAsync failed: {Message}", ex.Message);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Failed to retrieve nozzle models" });
        }
    }

    // ============ Component Model CRUD Endpoints ============
    #region Hotend Model CRUD

    /// <summary>
    /// Creates a new hotend model definition.
    /// </summary>
    [Authorize(Roles = "farm_admin")]
    [HttpPost("hotends")]
    [ProducesResponseType(typeof(HotendModelDto), 201)]
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<HotendModelDto>> CreateHotendAsync([FromBody] CreateHotendModelDto dto, CancellationToken ct)
    {
        try
        {
            HotendModelDto created = await _catalogService.CreateHotendModelAsync(dto, ct);
            return CreatedAtAction(nameof(GetHotendsAsync), new { }, created);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _unifiedLoggingService?.LogError(ex, "[CatalogController] CreateHotendAsync failed: {Message}", ex.Message);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Failed to create hotend model" });
        }
    }

    /// <summary>
    /// Updates an existing hotend model definition.
    /// </summary>
    [Authorize(Roles = "farm_admin")]
    [HttpPut("hotends/{id:guid}")]
    [ProducesResponseType(typeof(HotendModelDto), 200)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<HotendModelDto>> UpdateHotendAsync(Guid id, [FromBody] UpdateHotendModelDto dto, CancellationToken ct)
    {
        try
        {
            HotendModelDto? updated = await _catalogService.UpdateHotendModelAsync(id, dto, ct);
            return updated is null ? NotFound() : Ok(updated);
        }
        catch (Exception ex)
        {
            _unifiedLoggingService?.LogError(ex, "[CatalogController] UpdateHotendAsync failed: {Message}", ex.Message);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Failed to update hotend model" });
        }
    }

    /// <summary>
    /// Deletes a hotend model definition.
    /// </summary>
    [Authorize(Roles = "farm_admin")]
    [HttpDelete("hotends/{id:guid}")]
    [ProducesResponseType(204)]
    public async Task<ActionResult> DeleteHotendAsync(Guid id, CancellationToken ct)
    {
        try
        {
            await _catalogService.DeleteHotendModelAsync(id, ct);
            return NoContent();
        }
        catch (Exception ex)
        {
            _unifiedLoggingService?.LogError(ex, "[CatalogController] DeleteHotendAsync failed: {Message}", ex.Message);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Failed to delete hotend model" });
        }
    }

    #endregion

    #region Extruder Model CRUD

    /// <summary>
    /// Creates a new extruder model definition.
    /// </summary>
    [Authorize(Roles = "farm_admin")]
    [HttpPost("extruders")]
    [ProducesResponseType(typeof(ExtruderModelDto), 201)]
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<ExtruderModelDto>> CreateExtruderAsync([FromBody] CreateExtruderModelDto dto, CancellationToken ct)
    {
        try
        {
            ExtruderModelDto created = await _catalogService.CreateExtruderModelAsync(dto, ct);
            return CreatedAtAction(nameof(GetExtrudersAsync), new { }, created);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _unifiedLoggingService?.LogError(ex, "[CatalogController] CreateExtruderAsync failed: {Message}", ex.Message);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Failed to create extruder model" });
        }
    }

    /// <summary>
    /// Updates an existing extruder model definition.
    /// </summary>
    [Authorize(Roles = "farm_admin")]
    [HttpPut("extruders/{id:guid}")]
    [ProducesResponseType(typeof(ExtruderModelDto), 200)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<ExtruderModelDto>> UpdateExtruderAsync(Guid id, [FromBody] UpdateExtruderModelDto dto, CancellationToken ct)
    {
        try
        {
            ExtruderModelDto? updated = await _catalogService.UpdateExtruderModelAsync(id, dto, ct);
            return updated is null ? NotFound() : Ok(updated);
        }
        catch (Exception ex)
        {
            _unifiedLoggingService?.LogError(ex, "[CatalogController] UpdateExtruderAsync failed: {Message}", ex.Message);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Failed to update extruder model" });
        }
    }

    /// <summary>
    /// Deletes an extruder model definition.
    /// </summary>
    [Authorize(Roles = "farm_admin")]
    [HttpDelete("extruders/{id:guid}")]
    [ProducesResponseType(204)]
    public async Task<ActionResult> DeleteExtruderAsync(Guid id, CancellationToken ct)
    {
        try
        {
            await _catalogService.DeleteExtruderModelAsync(id, ct);
            return NoContent();
        }
        catch (Exception ex)
        {
            _unifiedLoggingService?.LogError(ex, "[CatalogController] DeleteExtruderAsync failed: {Message}", ex.Message);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Failed to delete extruder model" });
        }
    }

    #endregion

    #region Toolhead Model CRUD

    /// <summary>
    /// Creates a new toolhead model definition.
    /// </summary>
    [Authorize(Roles = "farm_admin")]
    [HttpPost("toolheads")]
    [ProducesResponseType(typeof(ToolheadModelDto), 201)]
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<ToolheadModelDto>> CreateToolheadAsync([FromBody] CreateToolheadModelDto dto, CancellationToken ct)
    {
        try
        {
            ToolheadModelDto created = await _catalogService.CreateToolheadModelAsync(dto, ct);
            return CreatedAtAction(nameof(GetToolheadsAsync), new { }, created);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _unifiedLoggingService?.LogError(ex, "[CatalogController] CreateToolheadAsync failed: {Message}", ex.Message);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Failed to create toolhead model" });
        }
    }

    /// <summary>
    /// Updates an existing toolhead model definition.
    /// </summary>
    [Authorize(Roles = "farm_admin")]
    [HttpPut("toolheads/{id:guid}")]
    [ProducesResponseType(typeof(ToolheadModelDto), 200)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<ToolheadModelDto>> UpdateToolheadAsync(Guid id, [FromBody] UpdateToolheadModelDefDto dto, CancellationToken ct)
    {
        try
        {
            ToolheadModelDto? updated = await _catalogService.UpdateToolheadModelAsync(id, dto, ct);
            return updated is null ? NotFound() : Ok(updated);
        }
        catch (Exception ex)
        {
            _unifiedLoggingService?.LogError(ex, "[CatalogController] UpdateToolheadAsync failed: {Message}", ex.Message);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Failed to update toolhead model" });
        }
    }

    /// <summary>
    /// Deletes a toolhead model definition.
    /// </summary>
    [Authorize(Roles = "farm_admin")]
    [HttpDelete("toolheads/{id:guid}")]
    [ProducesResponseType(204)]
    public async Task<ActionResult> DeleteToolheadAsync(Guid id, CancellationToken ct)
    {
        try
        {
            await _catalogService.DeleteToolheadModelAsync(id, ct);
            return NoContent();
        }
        catch (Exception ex)
        {
            _unifiedLoggingService?.LogError(ex, "[CatalogController] DeleteToolheadAsync failed: {Message}", ex.Message);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Failed to delete toolhead model" });
        }
    }

    #endregion

    #region Nozzle Model CRUD

    /// <summary>
    /// Creates a new nozzle model definition.
    /// </summary>
    [Authorize(Roles = "farm_admin")]
    [HttpPost("nozzles")]
    [ProducesResponseType(typeof(NozzleModelDto), 201)]
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<NozzleModelDto>> CreateNozzleAsync([FromBody] CreateNozzleModelDto dto, CancellationToken ct)
    {
        try
        {
            NozzleModelDto created = await _catalogService.CreateNozzleModelAsync(dto, ct);
            return CreatedAtAction(nameof(GetNozzlesAsync), new { }, created);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _unifiedLoggingService?.LogError(ex, "[CatalogController] CreateNozzleAsync failed: {Message}", ex.Message);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Failed to create nozzle model" });
        }
    }

    /// <summary>
    /// Updates an existing nozzle model definition.
    /// </summary>
    [Authorize(Roles = "farm_admin")]
    [HttpPut("nozzles/{id:guid}")]
    [ProducesResponseType(typeof(NozzleModelDto), 200)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<NozzleModelDto>> UpdateNozzleAsync(Guid id, [FromBody] UpdateNozzleModelDto dto, CancellationToken ct)
    {
        try
        {
            NozzleModelDto? updated = await _catalogService.UpdateNozzleModelAsync(id, dto, ct);
            return updated is null ? NotFound() : Ok(updated);
        }
        catch (Exception ex)
        {
            _unifiedLoggingService?.LogError(ex, "[CatalogController] UpdateNozzleAsync failed: {Message}", ex.Message);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Failed to update nozzle model" });
        }
    }

    /// <summary>
    /// Deletes a nozzle model definition.
    /// </summary>
    [Authorize(Roles = "farm_admin")]
    [HttpDelete("nozzles/{id:guid}")]
    [ProducesResponseType(204)]
    public async Task<ActionResult> DeleteNozzleAsync(Guid id, CancellationToken ct)
    {
        try
        {
            await _catalogService.DeleteNozzleModelAsync(id, ct);
            return NoContent();
        }
        catch (Exception ex)
        {
            _unifiedLoggingService?.LogError(ex, "[CatalogController] DeleteNozzleAsync failed: {Message}", ex.Message);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Failed to delete nozzle model" });
        }
    }

    #endregion

    #region Contextual Manufacturer Query

    /// <summary>
    /// Gets manufacturers grouped by whether they have items in the specified catalog context.
    /// </summary>
    /// <param name="context">The catalog context (Printers, Hotends, Extruders, Toolheads, Nozzles)</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Manufacturers split into with-items and without-items groups</returns>
    [AllowAnonymous]
    [HttpGet("manufacturers/by-context/{context}")]
    [ProducesResponseType(typeof(ManufacturersByContextDto), 200)]
    [ProducesResponseType(400)]
    public async Task<ActionResult<ManufacturersByContextDto>> GetManufacturersByContextAsync(CatalogContext context, CancellationToken ct)
    {
        try
        {
            ManufacturersByContextDto result = await _catalogService.GetManufacturersByContextAsync(context, ct);
            return Ok(result);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return BadRequest(new { error = $"Invalid catalog context: {ex.Message}" });
        }
        catch (Exception ex)
        {
            _unifiedLoggingService?.LogError(ex, "[CatalogController] GetManufacturersByContextAsync failed: {Message}", ex.Message);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Failed to retrieve manufacturers by context" });
        }
    }

    #endregion
}
