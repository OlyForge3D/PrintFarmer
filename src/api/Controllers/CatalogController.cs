using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Exceptions;
using Farm.Web.Api.Controllers.Requests;
using Farm.Web.Api.Infrastructure.Caching;
using Farm.Web.Api.Infrastructure.Normalization;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// Provides endpoints for managing printer manufacturer and model catalog data.
/// </summary>
[ApiController]
[Route("api/catalog")]
[Tags("Catalog")]
public class CatalogController(
    Farm.Infrastructure.Telemetry.IUnifiedLoggingService unifiedLoggingService,
    Services.Catalog.ICatalogService catalogService) : ControllerBase
{
    private readonly Farm.Infrastructure.Telemetry.IUnifiedLoggingService _unifiedLoggingService = unifiedLoggingService;
    private readonly Services.Catalog.ICatalogService _catalogService = catalogService;

    /// <summary>
    /// Gets all available printer manufacturers.
    /// </summary>
    /// <param name="ifNoneMatch">Optional ETag for conditional GET</param>
    /// <param name="ct">Cancellation token for the operation</param>
    /// <returns>List of all printer manufacturers ordered by name</returns>
    /// <response code="200">Returns the list of manufacturers</response>
    [HttpGet("manufacturers")]
    [ProducesResponseType(typeof(IEnumerable<ManufacturerDto>), 200)]
    [ProducesResponseType(304)]
    public async Task<ActionResult<IEnumerable<ManufacturerDto>>> GetManufacturersAsync([FromHeader(Name = "If-None-Match")] string? ifNoneMatch, CancellationToken ct)
    {
        try
        {
            (IReadOnlyList<ManufacturerDto>? list, string? etag) = await _catalogService.GetManufacturersAsync(ct);
            if (!string.IsNullOrEmpty(ifNoneMatch) && ifNoneMatch.Split(',').Select(s => s.Trim()).Contains(etag, StringComparer.Ordinal))
            {
                Response.Headers["ETag"] = etag;
                return StatusCode(StatusCodes.Status304NotModified);
            }

            Response.Headers["ETag"] = etag;
            return Ok(list);
        }
        catch (Exception ex)
        {
            // Log the error with as much context as possible via injected unified logging service
            _unifiedLoggingService?.LogError(ex, $"[CatalogController] GetManufacturersAsync failed: {ex.Message}");

            // Optionally, include more context in the error response
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Failed to retrieve manufacturers", details = ex.ToString() });
        }
    }

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

    [HttpGet("printer-models")]
    [ProducesResponseType(typeof(IEnumerable<PrinterModelDto>), 200)]
    [ProducesResponseType(304)]
    public async Task<ActionResult<IEnumerable<PrinterModelDto>>> GetPrinterModelsAsync([FromQuery] Guid? manufacturerId, [FromHeader(Name = "If-None-Match")] string? ifNoneMatch, CancellationToken ct)
    {
        (IReadOnlyList<PrinterModelDto>? list, string? etag) = await _catalogService.GetModelsAsync(manufacturerId, ct);
        if (!string.IsNullOrEmpty(ifNoneMatch))
        {
            HashSet<string> clientEtags = ifNoneMatch.Split(',').Select(s => s.Trim()).ToHashSet(StringComparer.Ordinal);
            if (etag is not null && clientEtags.Contains(etag))
            {
                Response.Headers["ETag"] = etag;
                return StatusCode(StatusCodes.Status304NotModified);
            }
        }

        Response.Headers["ETag"] = etag;
        return Ok(list);
    }

    [HttpGet("printer-models/{id:guid}", Name = "GetPrinterModelById")]
    [ProducesResponseType(typeof(PrinterModelDto), 200)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<PrinterModelDto>> GetPrinterModelByIdAsync(Guid id, CancellationToken ct)
    {
        PrinterModelDto? dto = await _catalogService.GetModelByIdAsync(id, ct);
        return dto is null ? NotFound() : Ok(dto);
    }

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

    [HttpPut("printer-models/{id:guid}")]
    [ProducesResponseType(204)]
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> UpdateModelAsync(Guid id, [FromBody] UpdateModelRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);
        try
        {
            PrinterModelDto? updated = await _catalogService.UpdateModelAsync(id, req, ct);
            return updated is null ? NotFound() : NoContent();
        }
        catch (Exception ex)
        {
            _unifiedLoggingService?.LogError(ex, $"[CatalogController] UpdateModelAsync failed: {ex.Message}");
            throw;
        }
    }

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
            _unifiedLoggingService?.LogError(ex, $"[CatalogController] DeleteModelAsync failed: {ex.Message}");
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
            _unifiedLoggingService?.LogError(ex, $"[CatalogController] GetModelAliasesAsync failed: {ex.Message}");
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
            _unifiedLoggingService?.LogError(ex, $"[CatalogController] UpdateModelAliasesAsync failed: {ex.Message}");
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Failed to update model aliases" });
        }
    }

    // ============ Component Model Endpoints ============

    /// <summary>
    /// Gets all available hotend models.
    /// </summary>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>List of all hotend model definitions</returns>
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
            _unifiedLoggingService?.LogError(ex, $"[CatalogController] GetHotendsAsync failed: {ex.Message}");
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Failed to retrieve hotend models" });
        }
    }

    /// <summary>
    /// Gets all available extruder models.
    /// </summary>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>List of all extruder model definitions</returns>
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
            _unifiedLoggingService?.LogError(ex, $"[CatalogController] GetExtrudersAsync failed: {ex.Message}");
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Failed to retrieve extruder models" });
        }
    }

    /// <summary>
    /// Gets all available toolhead models.
    /// </summary>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>List of all toolhead model definitions</returns>
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
            _unifiedLoggingService?.LogError(ex, $"[CatalogController] GetToolheadsAsync failed: {ex.Message}");
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Failed to retrieve toolhead models" });
        }
    }

    /// <summary>
    /// Gets all available nozzle models.
    /// </summary>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>List of all nozzle model definitions</returns>
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
            _unifiedLoggingService?.LogError(ex, $"[CatalogController] GetNozzlesAsync failed: {ex.Message}");
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Failed to retrieve nozzle models" });
        }
    }
}
