using Farm.Infrastructure.Dtos.Projects;
using Farm.Infrastructure.Services.Projects;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// Manages project templates for creating standardized print projects.
/// </summary>
[ApiController]
[Route("api/project-templates")]
[Tags("Print Project Templates")]
[Authorize]
public class PrintProjectTemplatesController(IPrintProjectTemplateService templateService, ILogger<PrintProjectTemplatesController> logger) : ControllerBase
{
    /// <summary>
    /// Get all project templates with optional filtering.
    /// </summary>
    /// <param name="category">Optional filter by template category.</param>
    /// <param name="search">Optional search term for template name.</param>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<PrintProjectTemplateListDto>), 200)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<IReadOnlyList<PrintProjectTemplateListDto>>> GetTemplatesAsync(
        [FromQuery] string? category = null,
        [FromQuery] string? search = null)
    {
        try
        {
            var templates = await templateService.GetTemplatesAsync(category, search);
            return Ok(templates);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error retrieving project templates");
            return Problem("An error occurred while retrieving templates", statusCode: 500);
        }
    }

    /// <summary>
    /// Get all distinct template categories.
    /// </summary>
    [HttpGet("categories")]
    [ProducesResponseType(typeof(IReadOnlyList<string>), 200)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<IReadOnlyList<string>>> GetCategoriesAsync()
    {
        try
        {
            var categories = await templateService.GetCategoriesAsync();
            return Ok(categories);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error retrieving template categories");
            return Problem("An error occurred while retrieving categories", statusCode: 500);
        }
    }

    /// <summary>
    /// Get a specific project template with all file entries.
    /// </summary>
    /// <param name="id">The template ID.</param>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(PrintProjectTemplateDetailDto), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<PrintProjectTemplateDetailDto>> GetTemplateAsync(Guid id)
    {
        try
        {
            var template = await templateService.GetTemplateAsync(id);
            return template is null ? NotFound($"Template {id} not found") : Ok(template);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error retrieving template {Id}", id);
            return Problem("An error occurred while retrieving the template", statusCode: 500);
        }
    }

    /// <summary>
    /// Create a new project template.
    /// </summary>
    /// <param name="request">The template creation request.</param>
    [HttpPost]
    [ProducesResponseType(typeof(PrintProjectTemplateDetailDto), 201)]
    [ProducesResponseType(400)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<PrintProjectTemplateDetailDto>> CreateTemplateAsync([FromBody] CreatePrintProjectTemplateRequest request)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return BadRequest("Template name is required");
            }

            var template = await templateService.CreateTemplateAsync(request);
            return CreatedAtAction(nameof(GetTemplateAsync), new { id = template.Id }, template);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error creating project template");
            return Problem("An error occurred while creating the template", statusCode: 500);
        }
    }

    /// <summary>
    /// Update an existing project template.
    /// </summary>
    /// <param name="id">The template ID.</param>
    /// <param name="request">The update request.</param>
    [HttpPut("{id:guid}")]
    [ProducesResponseType(typeof(PrintProjectTemplateDetailDto), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<PrintProjectTemplateDetailDto>> UpdateTemplateAsync(Guid id, [FromBody] UpdatePrintProjectTemplateRequest request)
    {
        try
        {
            var template = await templateService.UpdateTemplateAsync(id, request);
            return template is null ? NotFound($"Template {id} not found") : Ok(template);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error updating template {Id}", id);
            return Problem("An error occurred while updating the template", statusCode: 500);
        }
    }

    /// <summary>
    /// Delete a project template.
    /// </summary>
    /// <param name="id">The template ID.</param>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(204)]
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<IActionResult> DeleteTemplateAsync(Guid id)
    {
        try
        {
            var deleted = await templateService.DeleteTemplateAsync(id);
            if (!deleted)
            {
                var template = await templateService.GetTemplateAsync(id);
                if (template is null)
                {
                    return NotFound($"Template {id} not found");
                }

                return BadRequest("Cannot delete system templates");
            }

            return NoContent();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error deleting template {Id}", id);
            return Problem("An error occurred while deleting the template", statusCode: 500);
        }
    }

    /// <summary>
    /// Add a file entry to a template.
    /// </summary>
    /// <param name="id">The template ID.</param>
    /// <param name="request">The file entry to add.</param>
    [HttpPost("{id:guid}/files")]
    [ProducesResponseType(typeof(PrintProjectTemplateFileDto), 201)]
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<PrintProjectTemplateFileDto>> AddFileToTemplateAsync(
        Guid id,
        [FromBody] CreateTemplateFileRequest request)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return BadRequest("File entry name is required");
            }

            var file = await templateService.AddFileToTemplateAsync(id, request);
            return file is null
                ? NotFound($"Template {id} not found")
                : CreatedAtAction(nameof(GetTemplateAsync), new { id }, file);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error adding file to template {Id}", id);
            return Problem("An error occurred while adding the file entry", statusCode: 500);
        }
    }

    /// <summary>
    /// Remove a file entry from a template.
    /// </summary>
    /// <param name="id">The template ID.</param>
    /// <param name="fileId">The file entry ID.</param>
    [HttpDelete("{id:guid}/files/{fileId:guid}")]
    [ProducesResponseType(204)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<IActionResult> RemoveFileFromTemplateAsync(Guid id, Guid fileId)
    {
        try
        {
            var removed = await templateService.RemoveFileFromTemplateAsync(id, fileId);
            return removed ? NoContent() : NotFound($"File entry {fileId} not found in template {id}");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error removing file {FileId} from template {Id}", fileId, id);
            return Problem("An error occurred while removing the file entry", statusCode: 500);
        }
    }

    /// <summary>
    /// Update a file entry within a template.
    /// </summary>
    /// <param name="id">The template ID.</param>
    /// <param name="fileId">The file entry ID.</param>
    /// <param name="request">The update request.</param>
    [HttpPut("{id:guid}/files/{fileId:guid}")]
    [ProducesResponseType(typeof(PrintProjectTemplateFileDto), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<PrintProjectTemplateFileDto>> UpdateTemplateFileAsync(
        Guid id,
        Guid fileId,
        [FromBody] UpdateTemplateFileRequest request)
    {
        try
        {
            var file = await templateService.UpdateTemplateFileAsync(id, fileId, request);
            return file is null ? NotFound($"File entry {fileId} not found in template {id}") : Ok(file);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error updating file {FileId} in template {Id}", fileId, id);
            return Problem("An error occurred while updating the file entry", statusCode: 500);
        }
    }
}
