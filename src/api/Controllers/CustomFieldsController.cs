using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.CustomFields;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// Manages custom field definitions and per-entity values for Printers and Users.
/// </summary>
[ApiController]
[Route("api/custom-fields")]
[Authorize]
[Produces("application/json")]
[Tags("Custom Fields")]
public class CustomFieldsController(
    ICustomFieldService customFieldService,
    ILogger<CustomFieldsController> logger) : ControllerBase
{
    /// <summary>Lists custom field definitions for a given entity type.</summary>
    [HttpGet("definitions")]
    [ProducesResponseType(typeof(IEnumerable<CustomFieldDefinitionDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<CustomFieldDefinitionDto>>> ListDefinitionsAsync(
        [FromQuery] CustomFieldEntityType entityType, CancellationToken ct)
    {
        try
        {
            IReadOnlyList<CustomFieldDefinitionDto> definitions =
                await customFieldService.ListDefinitionsAsync(entityType, ct);
            return Ok(definitions);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[CustomFields] ListDefinitionsAsync failed");
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                new { error = "Failed to retrieve custom field definitions" });
        }
    }

    /// <summary>Gets a custom field definition by ID.</summary>
    [HttpGet("definitions/{id:guid}")]
    [ProducesResponseType(typeof(CustomFieldDefinitionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CustomFieldDefinitionDto>> GetDefinitionAsync(
        Guid id, CancellationToken ct)
    {
        try
        {
            CustomFieldDefinitionDto? definition = await customFieldService.GetDefinitionByIdAsync(id, ct);
            return definition is null
                ? NotFound(new { error = "Custom field definition not found" })
                : Ok(definition);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[CustomFields] GetDefinitionAsync failed for {Id}", id);
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                new { error = "Failed to retrieve custom field definition" });
        }
    }

    /// <summary>Creates a new custom field definition.</summary>
    [HttpPost("definitions")]
    [Authorize(Roles = "farm_admin")]
    [ProducesResponseType(typeof(CustomFieldDefinitionDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<CustomFieldDefinitionDto>> CreateDefinitionAsync(
        [FromBody] CreateCustomFieldDefinitionDto dto, CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dto.FieldName))
            {
                return BadRequest(new { error = "Field name is required" });
            }

            if (string.IsNullOrWhiteSpace(dto.FieldKey))
            {
                return BadRequest(new { error = "Field key is required" });
            }

            CustomFieldDefinitionDto definition = await customFieldService.CreateDefinitionAsync(dto, ct);
            return CreatedAtAction("GetDefinition", new { id = definition.Id }, definition);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "[CustomFields] CreateDefinitionAsync conflict: {Message}", ex.Message);
            return Conflict(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[CustomFields] CreateDefinitionAsync failed");
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                new { error = "Failed to create custom field definition" });
        }
    }

    /// <summary>Updates a custom field definition.</summary>
    [HttpPut("definitions/{id:guid}")]
    [Authorize(Roles = "farm_admin")]
    [ProducesResponseType(typeof(CustomFieldDefinitionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<CustomFieldDefinitionDto>> UpdateDefinitionAsync(
        Guid id, [FromBody] UpdateCustomFieldDefinitionDto dto, CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dto.FieldName))
            {
                return BadRequest(new { error = "Field name is required" });
            }

            if (string.IsNullOrWhiteSpace(dto.FieldKey))
            {
                return BadRequest(new { error = "Field key is required" });
            }

            CustomFieldDefinitionDto definition = await customFieldService.UpdateDefinitionAsync(id, dto, ct);
            return Ok(definition);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "[CustomFields] UpdateDefinitionAsync conflict: {Message}", ex.Message);
            return Conflict(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[CustomFields] UpdateDefinitionAsync failed for {Id}", id);
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                new { error = "Failed to update custom field definition" });
        }
    }

    /// <summary>Deletes a custom field definition and all associated values.</summary>
    [HttpDelete("definitions/{id:guid}")]
    [Authorize(Roles = "farm_admin")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteDefinitionAsync(Guid id, CancellationToken ct)
    {
        try
        {
            await customFieldService.DeleteDefinitionAsync(id, ct);
            return NoContent();
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[CustomFields] DeleteDefinitionAsync failed for {Id}", id);
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                new { error = "Failed to delete custom field definition" });
        }
    }

    /// <summary>Gets custom field values for a single entity.</summary>
    /// <remarks>
    /// User-type custom field values require the farm_admin role to prevent
    /// enumeration of other users' metadata.
    /// </remarks>
    [HttpGet("values/{entityType}/{entityId:guid}")]
    [ProducesResponseType(typeof(IEnumerable<CustomFieldValueDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IEnumerable<CustomFieldValueDto>>> GetValuesAsync(
        CustomFieldEntityType entityType, Guid entityId, CancellationToken ct)
    {
        try
        {
            // User custom field values require admin role to prevent enumeration
            if (entityType == CustomFieldEntityType.User && !User.IsInRole("farm_admin"))
            {
                return Forbid();
            }

            IReadOnlyList<CustomFieldValueDto> values =
                await customFieldService.GetValuesForEntityAsync(entityId, entityType, ct);
            return Ok(values);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[CustomFields] GetValuesAsync failed for {EntityType}/{EntityId}",
                entityType, entityId);
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                new { error = "Failed to retrieve custom field values" });
        }
    }

    /// <summary>Sets (upserts) custom field values for a single entity.</summary>
    [HttpPut("values/{entityType}/{entityId:guid}")]
    [Authorize(Roles = "farm_admin")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> SetValuesAsync(
        CustomFieldEntityType entityType,
        Guid entityId,
        [FromBody] SetCustomFieldValuesRequest request,
        CancellationToken ct)
    {
        try
        {
            await customFieldService.SetValuesAsync(entityId, entityType, request.Values, ct);
            return NoContent();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[CustomFields] SetValuesAsync failed for {EntityType}/{EntityId}",
                entityType, entityId);
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                new { error = "Failed to set custom field values" });
        }
    }

    /// <summary>Bulk-gets custom field values for multiple entities (for list views).</summary>
    [HttpPost("values/bulk")]
    [ProducesResponseType(typeof(Dictionary<Guid, IReadOnlyList<CustomFieldValueDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<Dictionary<Guid, IReadOnlyList<CustomFieldValueDto>>>> BulkGetValuesAsync(
        [FromBody] BulkGetCustomFieldValuesRequest request, CancellationToken ct)
    {
        try
        {
            // User custom field values require admin role to prevent enumeration
            if (request.EntityType == CustomFieldEntityType.User && !User.IsInRole("farm_admin"))
            {
                return Forbid();
            }

            Dictionary<Guid, IReadOnlyList<CustomFieldValueDto>> result =
                await customFieldService.BulkGetValuesAsync(request.EntityIds, request.EntityType, ct);
            return Ok(result);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[CustomFields] BulkGetValuesAsync failed");
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                new { error = "Failed to retrieve custom field values" });
        }
    }
}
