using System.Security.Claims;
using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.Services.Collections;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Farm.Api.Controllers;

/// <summary>
/// REST endpoints for user-owned, shareable model collections and their membership. All mutating
/// operations require the caller to be the collection owner or an administrator; shared collections
/// are readable by any authenticated user.
/// </summary>
[ApiController]
[Route("api/model-collections")]
[Authorize]
[Produces("application/json")]
[Tags("Model Collections")]
public class ModelCollectionsController(
    ILogger<ModelCollectionsController> logger,
    IModelCollectionService collectionService) : ControllerBase
{
    private readonly ILogger<ModelCollectionsController> _logger = logger;
    private readonly IModelCollectionService _collectionService = collectionService;

    /// <summary>Lists the collections visible to the caller.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The collections the caller owns plus any shared collections (all for admins).</returns>
    /// <response code="200">Returns the visible collections.</response>
    /// <response code="401">Unauthorized.</response>
    [HttpGet("")]
    [ProducesResponseType(typeof(IEnumerable<ModelCollectionDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<IEnumerable<ModelCollectionDto>>> ListAsync(CancellationToken ct)
    {
        IReadOnlyList<ModelCollectionDto> collections = await _collectionService.ListAsync(GetCaller(), ct);
        return Ok(collections);
    }

    /// <summary>Gets a single collection the caller may read.</summary>
    /// <param name="id">Collection identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The requested collection.</returns>
    /// <response code="200">Returns the collection.</response>
    /// <response code="403">The caller may not read the collection.</response>
    /// <response code="404">The collection does not exist.</response>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(ModelCollectionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ModelCollectionDto>> GetAsync(Guid id, CancellationToken ct)
    {
        ModelCollectionDto collection = await _collectionService.GetAsync(GetCaller(), id, ct);
        return Ok(collection);
    }

    /// <summary>Creates a new collection owned by the caller.</summary>
    /// <param name="dto">Collection creation payload.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The created collection.</returns>
    /// <response code="201">The collection was created.</response>
    /// <response code="400">The payload is invalid.</response>
    [HttpPost("")]
    [ProducesResponseType(typeof(ModelCollectionDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ModelCollectionDto>> CreateAsync([FromBody] CreateModelCollectionDto dto, CancellationToken ct)
    {
        if (dto is null || string.IsNullOrWhiteSpace(dto.Name))
        {
            return BadRequest(new ProblemDetails { Title = "Invalid request", Detail = "Collection name is required.", Status = StatusCodes.Status400BadRequest });
        }

        ModelCollectionDto created = await _collectionService.CreateAsync(GetCaller(), dto, ct);
        return CreatedAtAction(nameof(GetAsync), new { id = created.Id }, created);
    }

    /// <summary>Updates a collection's name and description.</summary>
    /// <param name="id">Collection identifier.</param>
    /// <param name="dto">Update payload.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The updated collection.</returns>
    /// <response code="200">The collection was updated.</response>
    /// <response code="400">The payload is invalid.</response>
    /// <response code="403">The caller is not the owner or an admin.</response>
    /// <response code="404">The collection does not exist.</response>
    [HttpPut("{id:guid}")]
    [ProducesResponseType(typeof(ModelCollectionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ModelCollectionDto>> UpdateAsync(Guid id, [FromBody] UpdateModelCollectionDto dto, CancellationToken ct)
    {
        if (dto is null || string.IsNullOrWhiteSpace(dto.Name))
        {
            return BadRequest(new ProblemDetails { Title = "Invalid request", Detail = "Collection name is required.", Status = StatusCodes.Status400BadRequest });
        }

        ModelCollectionDto updated = await _collectionService.UpdateAsync(GetCaller(), id, dto, ct);
        return Ok(updated);
    }

    /// <summary>Deletes a collection and its membership.</summary>
    /// <param name="id">Collection identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>No content.</returns>
    /// <response code="204">The collection was deleted.</response>
    /// <response code="403">The caller is not the owner or an admin.</response>
    /// <response code="404">The collection does not exist.</response>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteAsync(Guid id, CancellationToken ct)
    {
        await _collectionService.DeleteAsync(GetCaller(), id, ct);
        return NoContent();
    }

    /// <summary>Marks a collection as shared (readable by any authenticated user).</summary>
    /// <param name="id">Collection identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The updated collection.</returns>
    /// <response code="200">The collection is now shared.</response>
    /// <response code="403">The caller is not the owner or an admin.</response>
    /// <response code="404">The collection does not exist.</response>
    [HttpPost("{id:guid}/share")]
    [ProducesResponseType(typeof(ModelCollectionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ModelCollectionDto>> ShareAsync(Guid id, CancellationToken ct)
    {
        ModelCollectionDto shared = await _collectionService.ShareAsync(GetCaller(), id, ct);
        return Ok(shared);
    }

    /// <summary>Marks a collection as private (readable only by owner and admins).</summary>
    /// <param name="id">Collection identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The updated collection.</returns>
    /// <response code="200">The collection is now private.</response>
    /// <response code="403">The caller is not the owner or an admin.</response>
    /// <response code="404">The collection does not exist.</response>
    [HttpPost("{id:guid}/unshare")]
    [ProducesResponseType(typeof(ModelCollectionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ModelCollectionDto>> UnshareAsync(Guid id, CancellationToken ct)
    {
        ModelCollectionDto updated = await _collectionService.UnshareAsync(GetCaller(), id, ct);
        return Ok(updated);
    }

    /// <summary>Lists the model memberships of a collection.</summary>
    /// <param name="id">Collection identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The memberships of the collection.</returns>
    /// <response code="200">Returns the memberships.</response>
    /// <response code="403">The caller may not read the collection.</response>
    /// <response code="404">The collection does not exist.</response>
    [HttpGet("{id:guid}/members")]
    [ProducesResponseType(typeof(IEnumerable<ModelCollectionMembershipDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IEnumerable<ModelCollectionMembershipDto>>> ListMembersAsync(Guid id, CancellationToken ct)
    {
        IReadOnlyList<ModelCollectionMembershipDto> members = await _collectionService.ListMembersAsync(GetCaller(), id, ct);
        return Ok(members);
    }

    /// <summary>Adds a model to a collection.</summary>
    /// <param name="id">Collection identifier.</param>
    /// <param name="dto">Payload containing the model identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The created or existing membership.</returns>
    /// <response code="200">The model is a member of the collection.</response>
    /// <response code="400">The payload is invalid.</response>
    /// <response code="403">The caller is not the owner or an admin.</response>
    /// <response code="404">The collection or model does not exist.</response>
    [HttpPost("{id:guid}/members")]
    [ProducesResponseType(typeof(ModelCollectionMembershipDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ModelCollectionMembershipDto>> AddMemberAsync(Guid id, [FromBody] AddModelCollectionMemberDto dto, CancellationToken ct)
    {
        if (dto is null || dto.ModelId == Guid.Empty)
        {
            return BadRequest(new ProblemDetails { Title = "Invalid request", Detail = "A valid modelId is required.", Status = StatusCodes.Status400BadRequest });
        }

        ModelCollectionMembershipDto membership = await _collectionService.AddMemberAsync(GetCaller(), id, dto.ModelId, ct);
        return Ok(membership);
    }

    /// <summary>Removes a model from a collection.</summary>
    /// <param name="id">Collection identifier.</param>
    /// <param name="modelId">Model identifier to remove.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>No content.</returns>
    /// <response code="204">The model is no longer a member.</response>
    /// <response code="403">The caller is not the owner or an admin.</response>
    /// <response code="404">The collection does not exist.</response>
    [HttpDelete("{id:guid}/members/{modelId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RemoveMemberAsync(Guid id, Guid modelId, CancellationToken ct)
    {
        await _collectionService.RemoveMemberAsync(GetCaller(), id, modelId, ct);
        return NoContent();
    }

    /// <summary>Replaces the full membership of a collection with the supplied set of models.</summary>
    /// <param name="id">Collection identifier.</param>
    /// <param name="dto">Payload containing the desired model identifiers.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The resulting membership of the collection.</returns>
    /// <response code="200">Returns the resulting memberships.</response>
    /// <response code="400">The payload is invalid.</response>
    /// <response code="403">The caller is not the owner or an admin.</response>
    /// <response code="404">The collection or one of the models does not exist.</response>
    [HttpPut("{id:guid}/members")]
    [ProducesResponseType(typeof(IEnumerable<ModelCollectionMembershipDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IEnumerable<ModelCollectionMembershipDto>>> ReplaceMembersAsync(Guid id, [FromBody] ReplaceModelCollectionMembersDto dto, CancellationToken ct)
    {
        if (dto is null)
        {
            return BadRequest(new ProblemDetails { Title = "Invalid request", Detail = "A request body is required.", Status = StatusCodes.Status400BadRequest });
        }

        IReadOnlyList<ModelCollectionMembershipDto> members = await _collectionService.ReplaceMembersAsync(GetCaller(), id, dto.ModelIds, ct);
        return Ok(members);
    }

    /// <summary>
    /// Builds the <see cref="CollectionCaller"/> from the authenticated principal's claims and roles.
    /// </summary>
    private CollectionCaller GetCaller()
    {
        string? userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(userIdClaim, out Guid userId))
        {
            _logger.LogWarning("Authenticated request without a parseable user id claim");
        }

        bool isAdmin = User.IsInRole("Admin") || User.IsInRole("Administrator");
        return new CollectionCaller(userId, isAdmin);
    }
}
