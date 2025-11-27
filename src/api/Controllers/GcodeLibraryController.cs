using System.Security.Cryptography;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Gcode;
using Farm.Infrastructure.Repositories.Queue;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Shared;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// Manages the G-code file library
/// </summary>
[ApiController]
[Route("api/gcode-library")]
[Tags("G-code Library")]
public class GcodeLibraryController(Services.Gcode.IGcodeLibraryService gcodeService, IWebHostEnvironment env, IUnifiedLoggingService logger) : ControllerBase
{
    /// <summary>
    /// Get all G-code files in the library
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<GcodeFileDto>), 200)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<IEnumerable<GcodeFileDto>>> GetLibraryAsync([
        FromQuery] string? search = null,
        [FromQuery] string? material = null,
        [FromQuery] double? nozzleDiameter = null,
        [FromQuery] Guid? targetPrinterId = null)
    {
        try
        {
            IReadOnlyList<GcodeFileDto> result = await gcodeService.QueryLibraryAsync(search, material, nozzleDiameter, targetPrinterId, CancellationToken.None);
            return Ok(result);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error retrieving G-code library");
            return Problem("An error occurred while retrieving the library", statusCode: 500);
        }
    }

    /// <summary>
    /// Get a specific G-code file
    /// </summary>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(GcodeFileDto), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<GcodeFileDto>> GetFileAsync(Guid id)
    {
        try
        {
            GcodeFileDto? dto = await gcodeService.GetFileAsync(id, CancellationToken.None);
            if (dto is null)
            {
                return NotFound($"G-code file with ID {id} not found");
            }

            return Ok(dto);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, $"Error retrieving G-code file {id}");
            return Problem("An error occurred while retrieving the file", statusCode: 500);
        }
    }

    /// <summary>
    /// Upload a new G-code file to the library
    /// </summary>
    [HttpPost("upload")]
    [ProducesResponseType(typeof(GcodeFileDto), 201)]
    [ProducesResponseType(400)]
    [ProducesResponseType(409)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<GcodeFileDto>> UploadFileAsync([FromForm] IFormFile file, [FromForm] CreateGcodeFileDto metadata)
    {
        try
        {
            if (metadata is null)
            {
                return BadRequest("Metadata is required");
            }
            if (file == null || file.Length == 0)
            {
                return BadRequest("No file provided");
            }
            if (!file.FileName.EndsWith(".gcode", StringComparison.OrdinalIgnoreCase))
            {
                return BadRequest("File must be a .gcode file");
            }

            GcodeFileDto created = await gcodeService.UploadFileAsync(file, metadata, env.WebRootPath ?? env.ContentRootPath, CancellationToken.None);
            return CreatedAtAction(nameof(GetFileAsync), new { id = created.Id }, created);
        }
        catch (InvalidOperationException inv) when (string.Equals(inv.Message, "duplicate", StringComparison.OrdinalIgnoreCase))
        {
            return Conflict("File already exists in library");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, $"Error uploading G-code file {file?.FileName}");
            return Problem("An error occurred while uploading the file", statusCode: 500);
        }
    }

    /// <summary>
    /// Update G-code file metadata
    /// </summary>
    [HttpPut("{id}")]
    [ProducesResponseType(typeof(GcodeFileDto), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<GcodeFileDto>> UpdateFileAsync(Guid id, [FromBody] UpdateGcodeFileDto request)
    {
        try
        {
            if (request is null)
            {
                return BadRequest("Request body is required");
            }
            // Delegate update entirely to the service
            GcodeFileDto updated = await gcodeService.UpdateFileAsync(id, request, CancellationToken.None);
            if (updated == null)
            {
                return NotFound($"G-code file with ID {id} not found");
            }

            return Ok(updated);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, $"Error updating G-code file {id}");
            return Problem("An error occurred while updating the file", statusCode: 500);
        }
    }

    /// <summary>
    /// Delete a G-code file from the library
    /// </summary>
    [HttpDelete("{id}")]
    [ProducesResponseType(204)]
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<IActionResult> DeleteFileAsync(Guid id)
    {
        try
        {
            // Let service decide if file exists or is deletable
            bool ok = await gcodeService.DeleteFileAsync(id, CancellationToken.None);
            if (!ok)
            {
                return BadRequest("Cannot delete file (may be used by active jobs or missing)");
            }

            return NoContent();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, $"Error deleting G-code file {id}");
            return Problem("An error occurred while deleting the file", statusCode: 500);
        }
    }

    /// <summary>
    /// Download a G-code file
    /// </summary>
    [HttpGet("{id}/download")]
    [ProducesResponseType(typeof(FileContentResult), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<IActionResult> DownloadFileAsync(Guid id)
    {
        try
        {
            GcodeFileDto? dto = await gcodeService.GetFileAsync(id, CancellationToken.None);
            if (dto == null)
            {
                return NotFound($"G-code file with ID {id} not found");
            }

            byte[]? bytes = await gcodeService.DownloadFileAsync(id, env.WebRootPath ?? env.ContentRootPath, CancellationToken.None);
            if (bytes == null)
            {
                return NotFound("Physical file not found on disk");
            }

            return File(bytes, "application/octet-stream", dto.OriginalFileName);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, $"Error downloading G-code file {id}");
            return Problem("An error occurred while downloading the file", statusCode: 500);
        }
    }
}
