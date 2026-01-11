using Farm.Api.DTOs;
using Farm.Web.Api.Services.Tags;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Api.Controllers;

/// <summary>
/// API endpoints for generic tag management and operations (Phase 3D).
/// Provides tag listing, searching, analytics, and suggestions.
/// </summary>
[ApiController]
[Route("api/tags")]
[Authorize]
[Produces("application/json")]
[Tags("Tags")]
public class TagsController(
    Farm.Infrastructure.Telemetry.IUnifiedLoggingService unifiedLoggingService,
    ITagService tagService) : ControllerBase
{
    private readonly Farm.Infrastructure.Telemetry.IUnifiedLoggingService _unifiedLoggingService = unifiedLoggingService;
    private readonly ITagService _tagService = tagService;

    /// <summary>
    /// Gets all available tags with usage counts.
    /// </summary>
    /// <param name="ct">Cancellation token for the operation</param>
    /// <returns>List of all tags with usage metrics</returns>
    /// <response code="200">Returns the list of tags</response>
    /// <response code="401">Unauthorized</response>
    /// <response code="500">Internal server error</response>
    [HttpGet("")]
    [ProducesResponseType(typeof(IEnumerable<Model3DTagDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<IEnumerable<Model3DTagDto>>> GetAllTagsAsync(CancellationToken ct)
    {
        try
        {
            var tags = await _tagService.GetAllTagsAsync(ct);
            return Ok(tags);
        }
        catch (Exception ex)
        {
            _unifiedLoggingService?.LogError(ex, $"[TagsController] GetAllTagsAsync failed: {ex.Message}");
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Failed to retrieve tags" });
        }
    }

    /// <summary>
    /// Searches for tags by name.
    /// </summary>
    /// <param name="q">Search query string</param>
    /// <param name="ct">Cancellation token for the operation</param>
    /// <returns>List of matching tags with usage counts</returns>
    /// <response code="200">Returns matching tags</response>
    /// <response code="401">Unauthorized</response>
    /// <response code="500">Internal server error</response>
    [HttpGet("search")]
    [ProducesResponseType(typeof(IEnumerable<TagSuggestionDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
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
            _unifiedLoggingService?.LogError(ex, $"[TagsController] SearchTagsAsync failed: {ex.Message}");
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Failed to search tags" });
        }
    }

    /// <summary>
    /// Gets the most popular tags.
    /// </summary>
    /// <param name="count">Maximum number of tags to return (default 10)</param>
    /// <param name="ct">Cancellation token for the operation</param>
    /// <returns>List of most-used tags</returns>
    /// <response code="200">Returns popular tags</response>
    /// <response code="401">Unauthorized</response>
    /// <response code="500">Internal server error</response>
    [HttpGet("popular")]
    [ProducesResponseType(typeof(IEnumerable<TagSuggestionDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
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
            _unifiedLoggingService?.LogError(ex, $"[TagsController] GetPopularTagsAsync failed: {ex.Message}");
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Failed to retrieve popular tags" });
        }
    }

    /// <summary>
    /// Gets tag usage analytics.
    /// </summary>
    /// <param name="ct">Cancellation token for the operation</param>
    /// <returns>Tag statistics and usage analytics</returns>
    /// <response code="200">Returns analytics data</response>
    /// <response code="401">Unauthorized</response>
    /// <response code="500">Internal server error</response>
    [HttpGet("analytics")]
    [ProducesResponseType(typeof(TagAnalyticsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<TagAnalyticsDto>> GetTagAnalyticsAsync(CancellationToken ct)
    {
        try
        {
            var analytics = await _tagService.GetAnalyticsAsync(ct);
            return Ok(analytics);
        }
        catch (Exception ex)
        {
            _unifiedLoggingService?.LogError(ex, $"[TagsController] GetTagAnalyticsAsync failed: {ex.Message}");
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Failed to retrieve tag analytics" });
        }
    }

    /// <summary>
    /// Gets tag suggestions for autocomplete.
    /// </summary>
    /// <param name="q">Partial tag name for matching</param>
    /// <param name="limit">Maximum number of suggestions (default 10)</param>
    /// <param name="ct">Cancellation token for the operation</param>
    /// <returns>List of tag suggestions</returns>
    /// <response code="200">Returns suggestions</response>
    /// <response code="401">Unauthorized</response>
    /// <response code="500">Internal server error</response>
    [HttpGet("suggestions")]
    [ProducesResponseType(typeof(IEnumerable<TagSuggestionDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
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
            _unifiedLoggingService?.LogError(ex, $"[TagsController] GetTagSuggestionsAsync failed: {ex.Message}");
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Failed to retrieve tag suggestions" });
        }
    }
}
