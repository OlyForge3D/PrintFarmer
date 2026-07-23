using System.Security.Claims;
using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.Exceptions;
using Farm.Infrastructure.Services.Collections;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// Manages model collections and their membership. Collections group models (referenced
/// by id across the model/context boundary) and may be shared. Owners and administrators
/// may mutate a collection; shared collections are readable by any authenticated user.
/// </summary>
[ApiController]
[Route("api/model-collections")]
[Produces("application/json")]
[Authorize]
public class ModelCollectionsController(IModelCollectionService collectionService) : ControllerBase
{
    private readonly IModelCollectionService _collectionService = collectionService ?? throw new ArgumentNullException(nameof(collectionService));

    /// <summary>Lists collections visible to the current user.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<ModelCollectionDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<ModelCollectionDto>>> ListAsync(CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out Guid userId))
        {
            return Unauthorized(new { error = "User ID not found in claims" });
        }

        IReadOnlyList<ModelCollectionDto> collections = await _collectionService.ListCollectionsAsync(userId, IsAdmin(), cancellationToken);
        return Ok(collections);
    }

    /// <summary>Gets a single collection by id.</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(ModelCollectionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ModelCollectionDto>> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out Guid userId))
        {
            return Unauthorized(new { error = "User ID not found in claims" });
        }

        try
        {
            ModelCollectionDto? collection = await _collectionService.GetCollectionAsync(id, userId, IsAdmin(), cancellationToken);
            return collection is null ? NotFound(new { error = $"Collection {id} was not found" }) : Ok(collection);
        }
        catch (CollectionAccessDeniedException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = ex.Message });
        }
    }

    /// <summary>Creates a new collection owned by the current user.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(ModelCollectionDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ModelCollectionDto>> CreateAsync([FromBody] CreateModelCollectionDto dto, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out Guid userId))
        {
            return Unauthorized(new { error = "User ID not found in claims" });
        }

        try
        {
            ModelCollectionDto created = await _collectionService.CreateCollectionAsync(dto, userId, cancellationToken);
            return CreatedAtAction(nameof(GetAsync), new { id = created.Id }, created);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>Updates a collection's metadata.</summary>
    [HttpPut("{id:guid}")]
    [ProducesResponseType(typeof(ModelCollectionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ModelCollectionDto>> UpdateAsync(Guid id, [FromBody] UpdateModelCollectionDto dto, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out Guid userId))
        {
            return Unauthorized(new { error = "User ID not found in claims" });
        }

        try
        {
            ModelCollectionDto updated = await _collectionService.UpdateCollectionAsync(id, dto, userId, IsAdmin(), cancellationToken);
            return Ok(updated);
        }
        catch (CollectionNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (CollectionAccessDeniedException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>Deletes a collection and its memberships.</summary>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out Guid userId))
        {
            return Unauthorized(new { error = "User ID not found in claims" });
        }

        try
        {
            await _collectionService.DeleteCollectionAsync(id, userId, IsAdmin(), cancellationToken);
            return NoContent();
        }
        catch (CollectionNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (CollectionAccessDeniedException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = ex.Message });
        }
    }

    /// <summary>Shares a collection so any authenticated user can read it.</summary>
    [HttpPost("{id:guid}/share")]
    [ProducesResponseType(typeof(ModelCollectionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<ActionResult<ModelCollectionDto>> ShareAsync(Guid id, CancellationToken cancellationToken)
        => SetSharedAsync(id, shared: true, cancellationToken);

    /// <summary>Unshares a collection so only the owner and administrators can read it.</summary>
    [HttpPost("{id:guid}/unshare")]
    [ProducesResponseType(typeof(ModelCollectionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<ActionResult<ModelCollectionDto>> UnshareAsync(Guid id, CancellationToken cancellationToken)
        => SetSharedAsync(id, shared: false, cancellationToken);

    /// <summary>Lists the memberships (model references) of a collection.</summary>
    [HttpGet("{id:guid}/members")]
    [ProducesResponseType(typeof(IReadOnlyList<ModelCollectionMembershipDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<ModelCollectionMembershipDto>>> ListMembersAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out Guid userId))
        {
            return Unauthorized(new { error = "User ID not found in claims" });
        }

        try
        {
            IReadOnlyList<ModelCollectionMembershipDto> members = await _collectionService.ListMembersAsync(id, userId, IsAdmin(), cancellationToken);
            return Ok(members);
        }
        catch (CollectionNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (CollectionAccessDeniedException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = ex.Message });
        }
    }

    /// <summary>Adds a model to a collection.</summary>
    [HttpPost("{id:guid}/members")]
    [ProducesResponseType(typeof(ModelCollectionMembershipDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ModelCollectionMembershipDto>> AddMemberAsync(Guid id, [FromBody] AddModelCollectionMemberDto dto, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out Guid userId))
        {
            return Unauthorized(new { error = "User ID not found in claims" });
        }

        if (dto is null || dto.ModelId == Guid.Empty)
        {
            return BadRequest(new { error = "A valid modelId is required" });
        }

        try
        {
            ModelCollectionMembershipDto membership = await _collectionService.AddMemberAsync(id, dto.ModelId, userId, IsAdmin(), cancellationToken);
            return CreatedAtAction(nameof(ListMembersAsync), new { id }, membership);
        }
        catch (CollectionNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (CollectionAccessDeniedException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = ex.Message });
        }
        catch (CollectionModelValidationException ex)
        {
            return BadRequest(new { error = ex.Message, invalidModelIds = ex.InvalidModelIds });
        }
    }

    /// <summary>Removes a model from a collection.</summary>
    [HttpDelete("{id:guid}/members/{modelId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RemoveMemberAsync(Guid id, Guid modelId, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out Guid userId))
        {
            return Unauthorized(new { error = "User ID not found in claims" });
        }

        try
        {
            await _collectionService.RemoveMemberAsync(id, modelId, userId, IsAdmin(), cancellationToken);
            return NoContent();
        }
        catch (CollectionNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (CollectionAccessDeniedException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = ex.Message });
        }
    }

    /// <summary>Replaces a collection's entire membership set.</summary>
    [HttpPut("{id:guid}/members")]
    [ProducesResponseType(typeof(ModelCollectionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ModelCollectionDto>> ReplaceMembersAsync(Guid id, [FromBody] ReplaceModelCollectionMembersDto dto, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out Guid userId))
        {
            return Unauthorized(new { error = "User ID not found in claims" });
        }

        IEnumerable<Guid> modelIds = dto?.ModelIds ?? [];

        try
        {
            ModelCollectionDto updated = await _collectionService.ReplaceMembersAsync(id, modelIds, userId, IsAdmin(), cancellationToken);
            return Ok(updated);
        }
        catch (CollectionNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (CollectionAccessDeniedException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = ex.Message });
        }
        catch (CollectionModelValidationException ex)
        {
            return BadRequest(new { error = ex.Message, invalidModelIds = ex.InvalidModelIds });
        }
    }

    private async Task<ActionResult<ModelCollectionDto>> SetSharedAsync(Guid id, bool shared, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out Guid userId))
        {
            return Unauthorized(new { error = "User ID not found in claims" });
        }

        try
        {
            ModelCollectionDto updated = await _collectionService.SetSharedAsync(id, shared, userId, IsAdmin(), cancellationToken);
            return Ok(updated);
        }
        catch (CollectionNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (CollectionAccessDeniedException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = ex.Message });
        }
    }

    private bool IsAdmin() => User.IsInRole("farm_admin");

    private bool TryGetUserId(out Guid userId)
    {
        string? userIdString =
            User.FindFirstValue(ClaimTypes.NameIdentifier) ??
            User.FindFirst("sub")?.Value ??
            User.FindFirst("oid")?.Value;

        return Guid.TryParse(userIdString, out userId);
    }
}
