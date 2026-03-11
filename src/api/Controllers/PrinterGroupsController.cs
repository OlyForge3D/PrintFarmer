using Farm.Infrastructure.Services.PrinterGroups;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// Manages printer groups — curated sets of identical printers for dispatch targeting.
/// </summary>
[ApiController]
[Route("api/printer-groups")]
[Authorize]
[Produces("application/json")]
[Tags("Printer Groups")]
public class PrinterGroupsController(
    IPrinterGroupService groupService,
    ILogger<PrinterGroupsController> logger) : ControllerBase
{
    /// <summary>
    /// Lists all printer groups with printer counts.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<PrinterGroupDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<PrinterGroupDto>>> ListGroupsAsync(CancellationToken ct)
    {
        try
        {
            IReadOnlyList<PrinterGroupDto> groups = await groupService.ListAllAsync(ct);
            return Ok(groups);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[PrinterGroupsController] ListGroupsAsync failed");
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Failed to retrieve printer groups" });
        }
    }

    /// <summary>
    /// Gets a printer group with its printers.
    /// </summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(PrinterGroupDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PrinterGroupDetailDto>> GetGroupAsync(Guid id, CancellationToken ct)
    {
        try
        {
            PrinterGroupDetailDto? group = await groupService.GetByIdAsync(id, ct);
            return group is null ? NotFound(new { error = "Printer group not found" }) : Ok(group);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[PrinterGroupsController] GetGroupAsync failed for {Id}", id);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Failed to retrieve printer group" });
        }
    }

    /// <summary>
    /// Creates a new printer group.
    /// </summary>
    [HttpPost]
    [Authorize(Roles = "farm_admin")]
    [ProducesResponseType(typeof(PrinterGroupDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<PrinterGroupDto>> CreateGroupAsync(
        [FromBody] CreatePrinterGroupDto dto,
        CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dto.Name))
            {
                return BadRequest(new { error = "Group name is required" });
            }

            PrinterGroupDto group = await groupService.CreateAsync(dto, ct);
            return CreatedAtAction("GetGroup", new { id = group.Id }, group);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "[PrinterGroupsController] CreateGroupAsync conflict: {Message}", ex.Message);
            return Conflict(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[PrinterGroupsController] CreateGroupAsync failed");
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Failed to create printer group" });
        }
    }

    /// <summary>
    /// Updates a printer group's name and description.
    /// </summary>
    [HttpPut("{id:guid}")]
    [Authorize(Roles = "farm_admin")]
    [ProducesResponseType(typeof(PrinterGroupDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<PrinterGroupDto>> UpdateGroupAsync(
        Guid id,
        [FromBody] UpdatePrinterGroupDto dto,
        CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dto.Name))
            {
                return BadRequest(new { error = "Group name is required" });
            }

            PrinterGroupDto group = await groupService.UpdateAsync(id, dto, ct);
            return Ok(group);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "[PrinterGroupsController] UpdateGroupAsync conflict: {Message}", ex.Message);
            return Conflict(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[PrinterGroupsController] UpdateGroupAsync failed for {Id}", id);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Failed to update printer group" });
        }
    }

    /// <summary>
    /// Deletes a printer group. Printers in the group get their PrinterGroupId set to null.
    /// </summary>
    [HttpDelete("{id:guid}")]
    [Authorize(Roles = "farm_admin")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteGroupAsync(Guid id, CancellationToken ct)
    {
        try
        {
            await groupService.DeleteAsync(id, ct);
            return NoContent();
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[PrinterGroupsController] DeleteGroupAsync failed for {Id}", id);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Failed to delete printer group" });
        }
    }

    /// <summary>
    /// Adds a printer to a group. The printer is removed from its previous group (if any).
    /// </summary>
    [HttpPut("{id:guid}/printers/{printerId:guid}")]
    [Authorize(Roles = "farm_admin")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> AddPrinterToGroupAsync(Guid id, Guid printerId, CancellationToken ct)
    {
        try
        {
            await groupService.AddPrinterAsync(id, printerId, ct);
            return NoContent();
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[PrinterGroupsController] AddPrinterToGroupAsync failed for group {GroupId}, printer {PrinterId}", id, printerId);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Failed to add printer to group" });
        }
    }

    /// <summary>
    /// Removes a printer from a group (sets PrinterGroupId to null).
    /// </summary>
    [HttpDelete("{id:guid}/printers/{printerId:guid}")]
    [Authorize(Roles = "farm_admin")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RemovePrinterFromGroupAsync(Guid id, Guid printerId, CancellationToken ct)
    {
        try
        {
            await groupService.RemovePrinterAsync(id, printerId, ct);
            return NoContent();
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[PrinterGroupsController] RemovePrinterFromGroupAsync failed for group {GroupId}, printer {PrinterId}", id, printerId);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Failed to remove printer from group" });
        }
    }
}
