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
    Services.Catalog.ICatalogService catalogService,
    Services.Tags.ITagService tagService) : ControllerBase
{
    private readonly Farm.Infrastructure.Telemetry.IUnifiedLoggingService _unifiedLoggingService = unifiedLoggingService;
    private readonly Services.Catalog.ICatalogService _catalogService = catalogService;
    private readonly Services.Tags.ITagService _tagService = tagService;

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
        if (dto is null)
        {
            return NotFound();
        }
        return Ok(dto);
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
        ManufacturerDto dto = await _catalogService.CreateManufacturerAsync(request.Name, ct);
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
        if (dto is null)
        {
            return NotFound();
        }
        return Ok(dto);
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
            if (updated is null)
            {
                return NotFound();
            }
            return NoContent();
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

    #region Phase 3D: Tag Management Endpoints

    /// <summary>
    /// Gets all available tags with usage counts (Phase 3D).
    /// </summary>
    /// <param name="ct">Cancellation token for the operation</param>
    /// <returns>List of all tags with usage metrics</returns>
    /// <response code="200">Returns the list of tags</response>
    [HttpGet("tags")]
    [ProducesResponseType(typeof(IEnumerable<Model3DTagDto>), 200)]
    public async Task<ActionResult<IEnumerable<Model3DTagDto>>> GetTagsAsync(CancellationToken ct)
    {
        try
        {
            var tags = await _tagService.GetAllTagsAsync(ct);
            return Ok(tags);
        }
        catch (Exception ex)
        {
            _unifiedLoggingService?.LogError(ex, $"[CatalogController] GetTagsAsync failed: {ex.Message}");
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Failed to retrieve tags" });
        }
    }

    /// <summary>
    /// Searches for tags by name (Phase 3D).
    /// </summary>
    /// <param name="q">Search query string</param>
    /// <param name="ct">Cancellation token for the operation</param>
    /// <returns>List of matching tags with usage counts</returns>
    /// <response code="200">Returns matching tags</response>
    [HttpGet("tags/search")]
    [ProducesResponseType(typeof(IEnumerable<TagSuggestionDto>), 200)]
    public async Task<ActionResult<IEnumerable<TagSuggestionDto>>> SearchTagsAsync([FromQuery] string? q, CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(q))
            {
                return Ok(new List<TagSuggestionDto>());
            }

            var results = await _tagService.SearchTagsAsync(q, ct);
            return Ok(results);
        }
        catch (Exception ex)
        {
            _unifiedLoggingService?.LogError(ex, $"[CatalogController] SearchTagsAsync failed: {ex.Message}");
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Failed to search tags" });
        }
    }

    /// <summary>
    /// Gets the most popular tags (Phase 3D).
    /// </summary>
    /// <param name="count">Maximum number of tags to return (default 10)</param>
    /// <param name="ct">Cancellation token for the operation</param>
    /// <returns>List of most-used tags</returns>
    /// <response code="200">Returns popular tags</response>
    [HttpGet("tags/popular")]
    [ProducesResponseType(typeof(IEnumerable<TagSuggestionDto>), 200)]
    public async Task<ActionResult<IEnumerable<TagSuggestionDto>>> GetPopularTagsAsync([FromQuery] int count = 10, CancellationToken ct = default)
    {
        try
        {
            if (count <= 0)
            {
                count = 10;
            }

            var tags = await _tagService.GetPopularTagsAsync(count, ct);
            return Ok(tags);
        }
        catch (Exception ex)
        {
            _unifiedLoggingService?.LogError(ex, $"[CatalogController] GetPopularTagsAsync failed: {ex.Message}");
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Failed to retrieve popular tags" });
        }
    }

    /// <summary>
    /// Gets tag usage analytics (Phase 3D).
    /// </summary>
    /// <param name="ct">Cancellation token for the operation</param>
    /// <returns>Tag statistics and usage analytics</returns>
    /// <response code="200">Returns analytics data</response>
    [HttpGet("tags/analytics")]
    [ProducesResponseType(typeof(TagAnalyticsDto), 200)]
    public async Task<ActionResult<TagAnalyticsDto>> GetTagAnalyticsAsync(CancellationToken ct)
    {
        try
        {
            var analytics = await _tagService.GetAnalyticsAsync(ct);
            return Ok(analytics);
        }
        catch (Exception ex)
        {
            _unifiedLoggingService?.LogError(ex, $"[CatalogController] GetTagAnalyticsAsync failed: {ex.Message}");
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Failed to retrieve tag analytics" });
        }
    }

    /// <summary>
    /// Gets tag suggestions for autocomplete (Phase 3D).
    /// </summary>
    /// <param name="q">Partial tag name for matching</param>
    /// <param name="limit">Maximum number of suggestions (default 10)</param>
    /// <param name="ct">Cancellation token for the operation</param>
    /// <returns>List of tag suggestions</returns>
    /// <response code="200">Returns suggestions</response>
    [HttpGet("tags/suggestions")]
    [ProducesResponseType(typeof(IEnumerable<TagSuggestionDto>), 200)]
    public async Task<ActionResult<IEnumerable<TagSuggestionDto>>> GetTagSuggestionsAsync(
        [FromQuery] string? q,
        [FromQuery] int limit = 10,
        CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(q))
            {
                q = "";
            }

            var suggestions = await _tagService.GetTagSuggestionsAsync(q, limit, ct);
            return Ok(suggestions);
        }
        catch (Exception ex)
        {
            _unifiedLoggingService?.LogError(ex, $"[CatalogController] GetTagSuggestionsAsync failed: {ex.Message}");
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Failed to retrieve tag suggestions" });
        }
    }

    /// <summary>
    /// Filters 3D models by tags with AND/OR logic (Phase 3D).
    /// </summary>
    /// <param name="includeTags">Comma-separated list of tag IDs to include</param>
    /// <param name="excludeTags">Comma-separated list of tag IDs to exclude</param>
    /// <param name="requireAll">If true, require ALL include tags (AND); if false, ANY tag (OR)</param>
    /// <param name="ct">Cancellation token for the operation</param>
    /// <returns>List of matching model IDs and count</returns>
    /// <response code="200">Returns filtered models</response>
    [HttpGet("models/filter")]
    [ProducesResponseType(typeof(FilterModelsResponseDto), 200)]
    [ProducesResponseType(400)]
    public async Task<ActionResult<FilterModelsResponseDto>> FilterModelsByTagsAsync(
        [FromQuery] string? includeTags,
        [FromQuery] string? excludeTags,
        [FromQuery] bool requireAll = false,
        CancellationToken ct = default)
    {
        try
        {
            // Parse comma-separated tag IDs
            var includeTagIds = ParseTagIds(includeTags);
            var excludeTagIds = ParseTagIds(excludeTags);

            // Call filtering service
            var modelIds = await _tagService.FilterModelsByTagsAsync(
                includeTagIds.Any() ? includeTagIds : null,
                excludeTagIds.Any() ? excludeTagIds : null,
                requireAll,
                ct);

            return Ok(new FilterModelsResponseDto
            {
                ModelIds = modelIds,
                Count = modelIds.Count
            });
        }
        catch (FormatException ex)
        {
            _unifiedLoggingService?.LogError(ex, $"[CatalogController] Invalid tag ID format: {ex.Message}");
            return BadRequest(new { error = "Invalid tag ID format" });
        }
        catch (Exception ex)
        {
            _unifiedLoggingService?.LogError(ex, $"[CatalogController] FilterModelsByTagsAsync failed: {ex.Message}");
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Failed to filter models" });
        }
    }

    #endregion

    #region Tag Filtering Endpoints (Phase 3D.2)

    /// <summary>
    /// Get models with all specified tags (AND filter).
    /// </summary>
    /// <param name="tagIds">Comma-separated tag IDs that models must have</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>List of model IDs matching the filter</returns>
    /// <response code="200">Returns list of matching model IDs</response>
    /// <response code="400">Invalid tag ID format</response>
    [HttpGet("models/filter/all-tags")]
    [ProducesResponseType(typeof(IEnumerable<Guid>), 200)]
    [ProducesResponseType(400)]
    public async Task<ActionResult<IEnumerable<Guid>>> GetModelsWithAllTagsAsync([FromQuery(Name = "tags")] string? tagIds, CancellationToken ct)
    {
        try
        {
            var ids = ParseTagIds(tagIds);
            var modelIds = await _tagService.GetModelsWithAllTagsAsync(ids, ct);
            return Ok(modelIds);
        }
        catch (FormatException ex)
        {
            _unifiedLoggingService.LogWarning($"Invalid tag ID format: {ex.Message}");
            return BadRequest(new { error = "Invalid tag ID format", details = ex.Message });
        }
        catch (Exception ex)
        {
            _unifiedLoggingService.LogError($"Error getting models with all tags: {ex.Message}");
            return StatusCode(500, new { error = "Failed to get models", details = ex.Message });
        }
    }

    /// <summary>
    /// Get models with any of the specified tags (OR filter).
    /// </summary>
    /// <param name="tagIds">Comma-separated tag IDs - models matching any will be returned</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>List of model IDs matching the filter</returns>
    /// <response code="200">Returns list of matching model IDs</response>
    /// <response code="400">Invalid tag ID format</response>
    [HttpGet("models/filter/any-tags")]
    [ProducesResponseType(typeof(IEnumerable<Guid>), 200)]
    [ProducesResponseType(400)]
    public async Task<ActionResult<IEnumerable<Guid>>> GetModelsWithAnyTagAsync([FromQuery(Name = "tags")] string? tagIds, CancellationToken ct)
    {
        try
        {
            var ids = ParseTagIds(tagIds);
            var modelIds = await _tagService.GetModelsWithAnyTagAsync(ids, ct);
            return Ok(modelIds);
        }
        catch (FormatException ex)
        {
            _unifiedLoggingService.LogWarning($"Invalid tag ID format: {ex.Message}");
            return BadRequest(new { error = "Invalid tag ID format", details = ex.Message });
        }
        catch (Exception ex)
        {
            _unifiedLoggingService.LogError($"Error getting models with any tags: {ex.Message}");
            return StatusCode(500, new { error = "Failed to get models", details = ex.Message });
        }
    }

    /// <summary>
    /// Get models excluding specific tags (NOT filter).
    /// </summary>
    /// <param name="tagIds">Comma-separated tag IDs to exclude</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>List of model IDs that do NOT have any of the specified tags</returns>
    /// <response code="200">Returns list of matching model IDs</response>
    /// <response code="400">Invalid tag ID format</response>
    [HttpGet("models/filter/exclude-tags")]
    [ProducesResponseType(typeof(IEnumerable<Guid>), 200)]
    [ProducesResponseType(400)]
    public async Task<ActionResult<IEnumerable<Guid>>> GetModelsExcludingTagsAsync([FromQuery(Name = "tags")] string? tagIds, CancellationToken ct)
    {
        try
        {
            var ids = ParseTagIds(tagIds);
            var modelIds = await _tagService.GetModelsExcludingTagsAsync(ids, ct);
            return Ok(modelIds);
        }
        catch (FormatException ex)
        {
            _unifiedLoggingService.LogWarning($"Invalid tag ID format: {ex.Message}");
            return BadRequest(new { error = "Invalid tag ID format", details = ex.Message });
        }
        catch (Exception ex)
        {
            _unifiedLoggingService.LogError($"Error getting models excluding tags: {ex.Message}");
            return StatusCode(500, new { error = "Failed to get models", details = ex.Message });
        }
    }

    /// <summary>
    /// Complex tag filtering with include/exclude rules.
    /// </summary>
    /// <param name="includeAll">Comma-separated tag IDs that models MUST have (AND)</param>
    /// <param name="includeAny">Comma-separated tag IDs that models SHOULD have (OR)</param>
    /// <param name="exclude">Comma-separated tag IDs to exclude</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>List of model IDs matching the complex filter</returns>
    /// <response code="200">Returns list of matching model IDs</response>
    /// <response code="400">Invalid tag ID format</response>
    /// <remarks>
    /// Filter logic (applied in order):
    /// 1. Include ALL: Models must have ALL of these tags (if provided)
    /// 2. Include ANY: Models must have ANY of these tags (if provided and step 1 is empty)
    /// 3. Exclude: Models must NOT have any of these tags
    /// </remarks>
    [HttpGet("models/filter")]
    [ProducesResponseType(typeof(IEnumerable<Guid>), 200)]
    [ProducesResponseType(400)]
    public async Task<ActionResult<IEnumerable<Guid>>> GetModelsWithComplexFilterAsync(
        [FromQuery(Name = "includeAll")] string? includeAll,
        [FromQuery(Name = "includeAny")] string? includeAny,
        [FromQuery(Name = "exclude")] string? exclude,
        CancellationToken ct)
    {
        try
        {
            var includeAllIds = ParseTagIds(includeAll);
            var includeAnyIds = ParseTagIds(includeAny);
            var excludeIds = ParseTagIds(exclude);

            var modelIds = await _tagService.GetModelsWithComplexFilterAsync(includeAllIds, includeAnyIds, excludeIds, ct);
            return Ok(modelIds);
        }
        catch (FormatException ex)
        {
            _unifiedLoggingService.LogWarning($"Invalid tag ID format: {ex.Message}");
            return BadRequest(new { error = "Invalid tag ID format", details = ex.Message });
        }
        catch (Exception ex)
        {
            _unifiedLoggingService.LogError($"Error applying complex tag filter: {ex.Message}");
            return StatusCode(500, new { error = "Failed to filter models", details = ex.Message });
        }
    }

    #endregion

    // ETag computation moved into CatalogCache (IsUniqueConstraint and DB helpers moved to service layer)

    #region Helper Methods (Phase 3D)

    /// <summary>
    /// Parses comma-separated tag IDs from query string.
    /// </summary>
    private static List<Guid> ParseTagIds(string? tagIds)
    {
        if (string.IsNullOrWhiteSpace(tagIds))
        {
            return new List<Guid>();
        }

        var result = new List<Guid>();
        var ids = tagIds.Split(',', StringSplitOptions.RemoveEmptyEntries);

        foreach (var id in ids)
        {
            if (Guid.TryParse(id.Trim(), out var guid))
            {
                result.Add(guid);
            }
            else
            {
                throw new FormatException($"Invalid tag ID format: {id}");
            }
        }

        return result;
    }

    #endregion
}

