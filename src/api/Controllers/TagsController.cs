using Farm.Infrastructure.Services.Tags;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

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
    ILogger<TagsController> unifiedLoggingService,
    ITagService tagService) : ControllerBase
{
    private readonly ILogger<TagsController> _unifiedLoggingService = unifiedLoggingService;
    private readonly ITagService _tagService = tagService;

    /// <summary>
    /// Gets all available tags with usage counts.
    /// </summary>
    /// <param name="ct">Cancellation token for the operation</param>
    /// <returns>List of all tags with usage metrics</returns>
    /// <response code="200">Returns the list of tags</response>
    /// <response code="401">Unauthorized</response>
    /// <response code="500">Internal server error</response>
    [AllowAnonymous]
    [HttpGet("")]
    [ProducesResponseType(typeof(IEnumerable<TagDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<IEnumerable<TagDto>>> GetAllTagsAsync(CancellationToken ct)
    {
        try
        {
            IReadOnlyList<TagDto> tags = await _tagService.GetAllTagsAsync(ct);
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
    [AllowAnonymous]
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

            IReadOnlyList<TagSuggestionDto> results = await _tagService.SearchTagsAsync(q, ct);
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
    [AllowAnonymous]
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

            IReadOnlyList<TagSuggestionDto> tags = await _tagService.GetPopularTagsAsync(count, ct);
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
    [AllowAnonymous]
    [HttpGet("analytics")]
    [ProducesResponseType(typeof(TagAnalyticsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<TagAnalyticsDto>> GetTagAnalyticsAsync(CancellationToken ct)
    {
        try
        {
            TagAnalyticsDto analytics = await _tagService.GetAnalyticsAsync(ct);
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
    [AllowAnonymous]
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
                q = string.Empty;
            }

            IReadOnlyList<TagSuggestionDto> suggestions = await _tagService.GetTagSuggestionsAsync(q, limit, ct);
            return Ok(suggestions);
        }
        catch (Exception ex)
        {
            _unifiedLoggingService?.LogError(ex, $"[TagsController] GetTagSuggestionsAsync failed: {ex.Message}");
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Failed to retrieve tag suggestions" });
        }
    }

    /// <summary>
    /// Creates a new tag.
    /// </summary>
    /// <param name="createTagDto">Tag creation request with name and optional color/description</param>
    /// <param name="ct">Cancellation token for the operation</param>
    /// <returns>Created tag with assigned ID</returns>
    /// <response code="201">Tag created successfully</response>
    /// <response code="400">Invalid tag data</response>
    /// <response code="401">Unauthorized</response>
    /// <response code="409">Tag with this name already exists</response>
    /// <response code="500">Internal server error</response>
    [Authorize(Roles = "farm_admin")]
    [HttpPost("")]
    [ProducesResponseType(typeof(TagDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<TagDto>> CreateTagAsync(
        [FromBody] CreateTagDto createTagDto,
        CancellationToken ct)
    {
        try
        {
            if (createTagDto == null || string.IsNullOrWhiteSpace(createTagDto.Name))
            {
                return BadRequest(new { error = "Tag name is required" });
            }

            TagDto tag = await _tagService.CreateTagAsync(createTagDto, ct);
            return StatusCode(StatusCodes.Status201Created, tag);
        }
        catch (InvalidOperationException ex)
        {
            _unifiedLoggingService?.LogWarning(ex, $"[TagsController] CreateTagAsync - Tag already exists: {ex.Message}");
            return Conflict(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _unifiedLoggingService?.LogError(ex, $"[TagsController] CreateTagAsync failed: {ex.Message}");
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Failed to create tag" });
        }
    }

    /// <summary>
    /// Gets a specific tag by ID.
    /// </summary>
    /// <param name="tagId">Unique tag identifier</param>
    /// <param name="ct">Cancellation token for the operation</param>
    /// <returns>Tag details</returns>
    /// <response code="200">Tag found</response>
    /// <response code="401">Unauthorized</response>
    /// <response code="404">Tag not found</response>
    /// <response code="500">Internal server error</response>
    [AllowAnonymous]
    [HttpGet("{tagId:guid}")]
    [ProducesResponseType(typeof(TagDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<TagDto>> GetTagByIdAsync(
        [FromRoute] Guid tagId,
        CancellationToken ct)
    {
        try
        {
            TagDto? tag = await _tagService.GetTagByIdAsync(tagId, ct);
            return tag == null ? NotFound(new { error = "Tag not found" }) : Ok(tag);
        }
        catch (Exception ex)
        {
            _unifiedLoggingService?.LogError(ex, $"[TagsController] GetTagByIdAsync failed: {ex.Message}");
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Failed to retrieve tag" });
        }
    }

    /// <summary>
    /// Deletes a tag and all its associations.
    /// </summary>
    /// <param name="tagId">Unique tag identifier</param>
    /// <param name="ct">Cancellation token for the operation</param>
    /// <returns>No content on success</returns>
    /// <response code="204">Tag deleted successfully</response>
    /// <response code="401">Unauthorized</response>
    /// <response code="404">Tag not found</response>
    /// <response code="500">Internal server error</response>
    [Authorize(Roles = "farm_admin")]
    [HttpDelete("{tagId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> DeleteTagAsync(
        [FromRoute] Guid tagId,
        CancellationToken ct)
    {
        try
        {
            await _tagService.DeleteTagAsync(tagId, ct);
            return NoContent();
        }
        catch (KeyNotFoundException ex)
        {
            _unifiedLoggingService?.LogWarning(ex, $"[TagsController] DeleteTagAsync - Tag not found: {ex.Message}");
            return NotFound(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _unifiedLoggingService?.LogError(ex, $"[TagsController] DeleteTagAsync failed: {ex.Message}");
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Failed to delete tag" });
        }
    }

    /// <summary>
    /// Assigns a tag to an object (Model3D, GcodeFile, etc.).
    /// </summary>
    /// <param name="objectId">Unique object identifier</param>
    /// <param name="tagId">Unique tag identifier</param>
    /// <param name="objectType">Type of object: Model3D or GcodeFile</param>
    /// <param name="ct">Cancellation token for the operation</param>
    /// <returns>No content on success</returns>
    /// <response code="200">Tag successfully assigned</response>
    /// <response code="400">Invalid parameters</response>
    /// <response code="401">Unauthorized</response>
    /// <response code="404">Object or tag not found</response>
    /// <response code="500">Internal server error</response>
    /// Keep [Authorize] from class level - users can tag their own items
    [HttpPost("{objectId:guid}/{tagId:guid}/assign")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> AssignTagToObjectAsync(
        [FromRoute] Guid objectId,
        [FromRoute] Guid tagId,
        [FromQuery] string? objectType,
        CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(objectType) || (objectType != "Model3D" && objectType != "GcodeFile"))
            {
                return BadRequest(new { error = "objectType query parameter is required and must be 'Model3D' or 'GcodeFile'" });
            }

            await _tagService.AssignTagAsync(objectId, tagId, objectType, ct);

            // Fetch and return the assigned tag
            TagDto? tag = await _tagService.GetTagByIdAsync(tagId, ct);
            return Ok(tag);
        }
        catch (KeyNotFoundException ex)
        {
            _unifiedLoggingService?.LogWarning(ex, $"[TagsController] AssignTagToObjectAsync - Object or tag not found: {ex.Message}");
            return NotFound(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _unifiedLoggingService?.LogError(ex, $"[TagsController] AssignTagToObjectAsync failed: {ex.Message}");
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Failed to assign tag" });
        }
    }

    /// <summary>
    /// Unassigns a tag from an object (Model3D, GcodeFile, etc.).
    /// </summary>
    /// <param name="objectId">Unique object identifier</param>
    /// <param name="tagId">Unique tag identifier</param>
    /// <param name="objectType">Type of object: Model3D or GcodeFile</param>
    /// <param name="ct">Cancellation token for the operation</param>
    /// <returns>No content on success</returns>
    /// <response code="204">Tag unassigned successfully</response>
    /// <response code="400">Invalid parameters</response>
    /// <response code="401">Unauthorized</response>
    /// <response code="404">Mapping not found</response>
    /// <response code="500">Internal server error</response>
    /// Keep [Authorize] from class level - users can remove tags from their own items
    [HttpDelete("{objectId:guid}/{tagId:guid}/remove")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> RemoveTagFromObjectAsync(
        [FromRoute] Guid objectId,
        [FromRoute] Guid tagId,
        [FromQuery] string? objectType,
        CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(objectType) || (objectType != "Model3D" && objectType != "GcodeFile"))
            {
                return BadRequest(new { error = "objectType query parameter is required and must be 'Model3D' or 'GcodeFile'" });
            }

            await _tagService.RemoveTagAsync(objectId, tagId, objectType, ct);
            return NoContent();
        }
        catch (KeyNotFoundException ex)
        {
            _unifiedLoggingService?.LogWarning(ex, $"[TagsController] RemoveTagFromObjectAsync - Mapping not found: {ex.Message}");
            return NotFound(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _unifiedLoggingService?.LogError(ex, $"[TagsController] RemoveTagFromObjectAsync failed: {ex.Message}");
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Failed to remove tag" });
        }
    }

    /// <summary>
    /// Gets all tags assigned to a specific object.
    /// </summary>
    /// <param name="objectId">Unique object identifier</param>
    /// <param name="objectType">Type of object (e.g., "Model3D", "GcodeFile")</param>
    /// <param name="ct">Cancellation token for the operation</param>
    /// <returns>List of tags assigned to the object</returns>
    /// <response code="200">Returns the list of tags</response>
    /// <response code="400">Invalid parameters</response>
    /// <response code="401">Unauthorized</response>
    /// <response code="500">Internal server error</response>
    /// Keep [Authorize] from class level - authenticated users can view item tags
    [HttpGet("object/{objectId:guid}")]
    [ProducesResponseType(typeof(IEnumerable<TagDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<IEnumerable<TagDto>>> GetObjectTagsAsync(
        [FromRoute] Guid objectId,
        [FromQuery] string? objectType,
        CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(objectType))
            {
                return BadRequest(new { error = "objectType query parameter is required" });
            }

            IReadOnlyList<TagDto> tags = await _tagService.GetObjectTagsAsync(objectId, objectType, ct);
            return Ok(tags);
        }
        catch (Exception ex)
        {
            _unifiedLoggingService?.LogError(ex, $"[TagsController] GetObjectTagsAsync failed: {ex.Message}");
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Failed to retrieve object tags" });
        }
    }
}
