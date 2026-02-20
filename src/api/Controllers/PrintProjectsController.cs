using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos.Projects;
using Farm.Infrastructure.Services.Projects;
using Farm.Infrastructure.Telemetry;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// Manages print projects for multi-file job tracking.
/// </summary>
[ApiController]
[Route("api/projects")]
[Tags("Print Projects")]
[Authorize]
public class PrintProjectsController(IPrintProjectService projectService, IUnifiedLoggingService logger) : ControllerBase
{
    /// <summary>
    /// Get all print projects with optional filtering.
    /// </summary>
    /// <param name="status">Optional filter by project status.</param>
    /// <param name="search">Optional search term for project name.</param>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<PrintProjectListDto>), 200)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<IReadOnlyList<PrintProjectListDto>>> GetProjectsAsync(
        [FromQuery] PrintProjectStatus? status = null,
        [FromQuery] string? search = null)
    {
        try
        {
            var projects = await projectService.GetProjectsAsync(status, search);
            return Ok(projects);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error retrieving print projects");
            return Problem("An error occurred while retrieving projects", statusCode: 500);
        }
    }

    /// <summary>
    /// Get a specific print project with all files.
    /// </summary>
    /// <param name="id">The project ID.</param>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(PrintProjectDetailDto), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<PrintProjectDetailDto>> GetProjectAsync(Guid id)
    {
        try
        {
            var project = await projectService.GetProjectAsync(id);
            return project is null ? NotFound($"Project {id} not found") : Ok(project);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, $"Error retrieving project {id}");
            return Problem("An error occurred while retrieving the project", statusCode: 500);
        }
    }

    /// <summary>
    /// Create a new print project.
    /// </summary>
    /// <param name="request">The project creation request.</param>
    [HttpPost]
    [ProducesResponseType(typeof(PrintProjectDetailDto), 201)]
    [ProducesResponseType(400)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<PrintProjectDetailDto>> CreateProjectAsync([FromBody] CreatePrintProjectRequest request)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return BadRequest("Project name is required");
            }

            var project = await projectService.CreateProjectAsync(request);
            return Created($"/api/projects/{project.Id}", project);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error creating print project");
            return Problem("An error occurred while creating the project", statusCode: 500);
        }
    }

    /// <summary>
    /// Update an existing print project.
    /// </summary>
    /// <param name="id">The project ID.</param>
    /// <param name="request">The update request.</param>
    [HttpPut("{id:guid}")]
    [ProducesResponseType(typeof(PrintProjectDetailDto), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<PrintProjectDetailDto>> UpdateProjectAsync(Guid id, [FromBody] UpdatePrintProjectRequest request)
    {
        try
        {
            var project = await projectService.UpdateProjectAsync(id, request);
            return project is null ? NotFound($"Project {id} not found") : Ok(project);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, $"Error updating project {id}");
            return Problem("An error occurred while updating the project", statusCode: 500);
        }
    }

    /// <summary>
    /// Delete a print project.
    /// </summary>
    /// <param name="id">The project ID.</param>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(204)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<IActionResult> DeleteProjectAsync(Guid id)
    {
        try
        {
            var deleted = await projectService.DeleteProjectAsync(id);
            return deleted ? NoContent() : NotFound($"Project {id} not found");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, $"Error deleting project {id}");
            return Problem("An error occurred while deleting the project", statusCode: 500);
        }
    }

    /// <summary>
    /// Add files to a print project.
    /// </summary>
    /// <param name="id">The project ID.</param>
    /// <param name="files">The files to add.</param>
    [HttpPost("{id:guid}/files")]
    [ProducesResponseType(typeof(IReadOnlyList<PrintProjectFileDto>), 201)]
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<IReadOnlyList<PrintProjectFileDto>>> AddFilesToProjectAsync(
        Guid id,
        [FromBody] IReadOnlyList<AddFileToProjectRequest> files)
    {
        try
        {
            if (files.Count == 0)
            {
                return BadRequest("At least one file is required");
            }

            var addedFiles = await projectService.AddFilesToProjectAsync(id, files);
            return Created($"/api/projects/{id}", addedFiles);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, $"Error adding files to project {id}");
            return Problem("An error occurred while adding files to the project", statusCode: 500);
        }
    }

    /// <summary>
    /// Remove a file from a print project.
    /// </summary>
    /// <param name="id">The project ID.</param>
    /// <param name="fileId">The project file ID (not the gcode file ID).</param>
    [HttpDelete("{id:guid}/files/{fileId:guid}")]
    [ProducesResponseType(204)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<IActionResult> RemoveFileFromProjectAsync(Guid id, Guid fileId)
    {
        try
        {
            var removed = await projectService.RemoveFileFromProjectAsync(id, fileId);
            return removed ? NoContent() : NotFound($"File {fileId} not found in project {id}");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, $"Error removing file {fileId} from project {id}");
            return Problem("An error occurred while removing the file", statusCode: 500);
        }
    }

    /// <summary>
    /// Update a file within a print project.
    /// </summary>
    /// <param name="id">The project ID.</param>
    /// <param name="fileId">The project file ID.</param>
    /// <param name="request">The update request.</param>
    [HttpPut("{id:guid}/files/{fileId:guid}")]
    [ProducesResponseType(typeof(PrintProjectFileDto), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<PrintProjectFileDto>> UpdateProjectFileAsync(
        Guid id,
        Guid fileId,
        [FromBody] UpdateProjectFileRequest request)
    {
        try
        {
            var file = await projectService.UpdateProjectFileAsync(id, fileId, request);
            return file is null ? NotFound($"File {fileId} not found in project {id}") : Ok(file);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, $"Error updating file {fileId} in project {id}");
            return Problem("An error occurred while updating the file", statusCode: 500);
        }
    }

    /// <summary>
    /// Mark a file as printed (increments printed count by 1).
    /// </summary>
    /// <param name="id">The project ID.</param>
    /// <param name="fileId">The project file ID.</param>
    /// <param name="printJobId">Optional print job ID that completed this print.</param>
    [HttpPost("{id:guid}/files/{fileId:guid}/mark-printed")]
    [ProducesResponseType(typeof(PrintProjectFileDto), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<PrintProjectFileDto>> MarkFilePrintedAsync(
        Guid id,
        Guid fileId,
        [FromQuery] Guid? printJobId = null)
    {
        try
        {
            var file = await projectService.MarkFilePrintedAsync(id, fileId, printJobId);
            return file is null ? NotFound($"File {fileId} not found in project {id}") : Ok(file);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, $"Error marking file {fileId} as printed in project {id}");
            return Problem("An error occurred while marking the file as printed", statusCode: 500);
        }
    }

    /// <summary>
    /// Get progress summary for a print project.
    /// </summary>
    /// <param name="id">The project ID.</param>
    [HttpGet("{id:guid}/progress")]
    [ProducesResponseType(typeof(PrintProjectProgressDto), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<PrintProjectProgressDto>> GetProjectProgressAsync(Guid id)
    {
        try
        {
            var progress = await projectService.GetProjectProgressAsync(id);
            return progress is null ? NotFound($"Project {id} not found") : Ok(progress);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, $"Error retrieving progress for project {id}");
            return Problem("An error occurred while retrieving project progress", statusCode: 500);
        }
    }

    /// <summary>
    /// Queue all pending files from a project to the print job queue.
    /// Files are automatically ordered by material type and color to minimize filament changes.
    /// </summary>
    /// <param name="id">The project ID.</param>
    /// <param name="request">Queue configuration options.</param>
    [HttpPost("{id:guid}/queue")]
    [ProducesResponseType(typeof(QueueProjectResultDto), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<QueueProjectResultDto>> QueueProjectAsync(Guid id, [FromBody] QueueProjectRequest? request = null)
    {
        try
        {
            var effectiveRequest = request ?? new QueueProjectRequest();
            var result = await projectService.QueueProjectAsync(id, effectiveRequest);

            if (result is null)
            {
                return NotFound($"Project {id} not found");
            }

            if (result.TotalJobsQueued == 0)
            {
                return BadRequest("No pending files to queue in this project");
            }

            return Ok(result);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, $"Error queuing project {id}");
            return Problem("An error occurred while queuing the project", statusCode: 500);
        }
    }
}
