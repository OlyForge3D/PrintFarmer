using System.Security.Cryptography;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Shared;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Farm.Web.Api.Repositories.Gcode;
using Farm.Web.Api.Repositories.Queue;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// Manages the G-code file library
/// </summary>
[ApiController]
[Route("api/gcode-library")]
[Tags("G-code Library")]
public class GcodeLibraryController(IGcodeRepository gcodeRepo, IQueueRepository queueRepo, IWebHostEnvironment env, IUnifiedLoggingService logger) : ControllerBase
{
    /// <summary>
    /// Get all G-code files in the library
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<GcodeFileDto>), 200)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<IEnumerable<GcodeFileDto>>> GetLibraryAsync(
        [FromQuery] string? search = null,
        [FromQuery] string? material = null,
        [FromQuery] double? nozzleDiameter = null,
        [FromQuery] Guid? targetPrinterId = null)
    {
        try
        {
            List<GcodeFile> files = await gcodeRepo.QueryLibraryAsync(search, material, nozzleDiameter, targetPrinterId, CancellationToken.None);

            return Ok(files.Select(file => new GcodeFileDto(
                Id: file.Id,
                OriginalFileName: file.OriginalFileName,
                DisplayName: file.DisplayName,
                FileSizeBytes: file.FileSizeBytes,
                UploadedAt: file.UploadedAt,
                Source: (GcodeSourceDto)(int)file.Source,
                SourcePrinterId: file.SourcePrinterId,
                SourcePrinterName: file.SourcePrinter?.Name,
                OriginalPrinterPath: file.OriginalPrinterPath,
                LastSeenOnPrinter: file.LastSeenOnPrinter,
                Description: file.Description,
                Tags: file.Tags?.Split(',', StringSplitOptions.RemoveEmptyEntries),
                RequiredNozzleDiameter: file.RequiredNozzleDiameter,
                RequiredMaterial: file.RequiredMaterial,
                CompatibleMaterials: file.CompatibleMaterials,
                EstimatedPrintTimeMinutes: file.EstimatedPrintTimeMinutes,
                EstimatedFilamentLengthMm: file.EstimatedFilamentLengthMm,
                EstimatedFilamentWeightG: file.EstimatedFilamentWeightG,
                RequiredBuildVolumeX: file.RequiredBuildVolumeX,
                RequiredBuildVolumeY: file.RequiredBuildVolumeY,
                RequiredBuildVolumeZ: file.RequiredBuildVolumeZ,
                TargetPrinterId: file.TargetPrinterId,
                TargetPrinterName: file.TargetPrinter?.Name,
                TargetModelId: file.TargetModelId,
                TargetModelName: file.TargetModel?.Name,
                SlicerName: file.SlicerName,
                SlicerVersion: file.SlicerVersion,
                HasThumbnail: !string.IsNullOrEmpty(file.ThumbnailPath)
            )));
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
            GcodeFile? file = await gcodeRepo.GetByIdWithIncludesAsync(id, CancellationToken.None);

            if (file is null)
            {
                return NotFound($"G-code file with ID {id} not found");
            }

            return Ok(new GcodeFileDto(
                Id: file!.Id,
                OriginalFileName: file.OriginalFileName,
                DisplayName: file.DisplayName,
                FileSizeBytes: file.FileSizeBytes,
                UploadedAt: file.UploadedAt,
                Source: (GcodeSourceDto)(int)file.Source,
                SourcePrinterId: file.SourcePrinterId,
                SourcePrinterName: file.SourcePrinter?.Name,
                OriginalPrinterPath: file.OriginalPrinterPath,
                LastSeenOnPrinter: file.LastSeenOnPrinter,
                Description: file.Description,
                Tags: file.Tags?.Split(',', StringSplitOptions.RemoveEmptyEntries),
                RequiredNozzleDiameter: file.RequiredNozzleDiameter,
                RequiredMaterial: file.RequiredMaterial,
                CompatibleMaterials: file.CompatibleMaterials,
                EstimatedPrintTimeMinutes: file.EstimatedPrintTimeMinutes,
                EstimatedFilamentLengthMm: file.EstimatedFilamentLengthMm,
                EstimatedFilamentWeightG: file.EstimatedFilamentWeightG,
                RequiredBuildVolumeX: file.RequiredBuildVolumeX,
                RequiredBuildVolumeY: file.RequiredBuildVolumeY,
                RequiredBuildVolumeZ: file.RequiredBuildVolumeZ,
                TargetPrinterId: file.TargetPrinterId,
                TargetPrinterName: file.TargetPrinter?.Name,
                TargetModelId: file.TargetModelId,
                TargetModelName: file.TargetModel?.Name,
                SlicerName: file.SlicerName,
                SlicerVersion: file.SlicerVersion,
                HasThumbnail: !string.IsNullOrEmpty(file.ThumbnailPath)
            ));
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

            // Calculate hash for deduplication
            string hash;
            using (Stream stream = file.OpenReadStream())
            {
                using SHA256 sha256 = SHA256.Create();
                byte[] hashBytes = await sha256.ComputeHashAsync(stream);
                hash = Convert.ToHexString(hashBytes);
            }

            // Check for duplicate
            GcodeFile? existing = await gcodeRepo.FindByHashAsync(hash, CancellationToken.None);
            if (existing is not null)
            {
                return Conflict($"File already exists in library: {existing.DisplayName}");
            }

            // Ensure directory exists
            string libraryPath = Path.Combine(env.WebRootPath, "gcode-library");
            string libraryRootFull = Path.GetFullPath(libraryPath);
            _ = Directory.CreateDirectory(libraryRootFull);

            // Save file
            string fileName = $"{Guid.NewGuid()}.gcode";
            string filePathFull = Path.GetFullPath(Path.Combine(libraryRootFull, fileName));
            if (!filePathFull.StartsWith(libraryRootFull, StringComparison.Ordinal))
            {
                return BadRequest("Invalid file path");
            }

            using (FileStream stream = System.IO.File.Create(filePathFull))
            {
                await file.CopyToAsync(stream);
            }

            // Create database entry
            GcodeFile gcodeFile = new()
            {
                Id = Guid.NewGuid(),
                OriginalFileName = file.FileName,
                DisplayName = string.IsNullOrEmpty(metadata.DisplayName) ?
                    Path.GetFileNameWithoutExtension(file.FileName) : metadata.DisplayName,
                FilePath = filePathFull,
                FileSizeBytes = file.Length,
                FileHash = hash,
                UploadedAt = DateTime.UtcNow,
                Source = GcodeSource.Upload,
                Description = metadata.Description,
                Tags = metadata.Tags != null ? string.Join(",", metadata.Tags) : null,
                RequiredNozzleDiameter = metadata.RequiredNozzleDiameter,
                RequiredMaterial = metadata.RequiredMaterial,
                CompatibleMaterials = metadata.CompatibleMaterials,
                EstimatedPrintTimeMinutes = metadata.EstimatedPrintTimeMinutes,
                EstimatedFilamentLengthMm = metadata.EstimatedFilamentLengthMm,
                EstimatedFilamentWeightG = metadata.EstimatedFilamentWeightG,
                RequiredBuildVolumeX = metadata.RequiredBuildVolumeX,
                RequiredBuildVolumeY = metadata.RequiredBuildVolumeY,
                RequiredBuildVolumeZ = metadata.RequiredBuildVolumeZ,
                TargetPrinterId = metadata.TargetPrinterId,
                TargetModelId = metadata.TargetModelId,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            await gcodeRepo.AddAsync(gcodeFile, CancellationToken.None);
            await gcodeRepo.SaveChangesAsync(CancellationToken.None);

            // Load related entities via repo
            GcodeFile? saved = await gcodeRepo.GetByIdWithIncludesAsync(gcodeFile.Id, CancellationToken.None);

            return CreatedAtAction(nameof(GetFileAsync), new { id = gcodeFile.Id }, new GcodeFileDto(
                Id: saved!.Id,
                OriginalFileName: saved.OriginalFileName,
                DisplayName: saved.DisplayName,
                FileSizeBytes: saved.FileSizeBytes,
                UploadedAt: saved.UploadedAt,
                Source: (GcodeSourceDto)(int)saved.Source,
                SourcePrinterId: saved.SourcePrinterId,
                SourcePrinterName: saved.SourcePrinter?.Name,
                OriginalPrinterPath: saved.OriginalPrinterPath,
                LastSeenOnPrinter: saved.LastSeenOnPrinter,
                Description: saved.Description,
                Tags: saved.Tags?.Split(',', StringSplitOptions.RemoveEmptyEntries),
                RequiredNozzleDiameter: saved.RequiredNozzleDiameter,
                RequiredMaterial: saved.RequiredMaterial,
                CompatibleMaterials: saved.CompatibleMaterials,
                EstimatedPrintTimeMinutes: saved.EstimatedPrintTimeMinutes,
                EstimatedFilamentLengthMm: saved.EstimatedFilamentLengthMm,
                EstimatedFilamentWeightG: saved.EstimatedFilamentWeightG,
                RequiredBuildVolumeX: saved.RequiredBuildVolumeX,
                RequiredBuildVolumeY: saved.RequiredBuildVolumeY,
                RequiredBuildVolumeZ: saved.RequiredBuildVolumeZ,
                TargetPrinterId: saved.TargetPrinterId,
                TargetPrinterName: saved.TargetPrinter?.Name,
                TargetModelId: saved.TargetModelId,
                TargetModelName: saved.TargetModel?.Name,
                SlicerName: saved.SlicerName,
                SlicerVersion: saved.SlicerVersion,
                HasThumbnail: !string.IsNullOrEmpty(saved.ThumbnailPath)
            ));
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
            GcodeFile? file = await gcodeRepo.GetByIdWithIncludesAsync(id, CancellationToken.None);

            if (file == null)
            {
                return NotFound($"G-code file with ID {id} not found");
            }

            // Update fields
            if (!string.IsNullOrEmpty(request.DisplayName))
            {
                file.DisplayName = request.DisplayName;
            }

            if (request.Description != null)
            {
                file.Description = request.Description;
            }

            if (request.Tags != null)
            {
                file.Tags = string.Join(",", request.Tags);
            }

            if (request.RequiredNozzleDiameter.HasValue)
            {
                file.RequiredNozzleDiameter = request.RequiredNozzleDiameter;
            }

            if (!string.IsNullOrEmpty(request.RequiredMaterial))
            {
                file.RequiredMaterial = request.RequiredMaterial;
            }

            if (request.CompatibleMaterials != null)
            {
                file.CompatibleMaterials = request.CompatibleMaterials;
            }

            if (request.TargetPrinterId.HasValue)
            {
                file.TargetPrinterId = request.TargetPrinterId.Value;
            }

            if (request.TargetModelId.HasValue)
            {
                file.TargetModelId = request.TargetModelId.Value;
            }

            file.UpdatedAt = DateTime.UtcNow;

            await gcodeRepo.SaveChangesAsync(CancellationToken.None);

            // Reload with includes
            GcodeFile? saved = await gcodeRepo.GetByIdWithIncludesAsync(id, CancellationToken.None);

            return Ok(new GcodeFileDto(
                Id: saved!.Id,
                OriginalFileName: saved.OriginalFileName,
                DisplayName: saved.DisplayName,
                FileSizeBytes: saved.FileSizeBytes,
                UploadedAt: saved.UploadedAt,
                Source: (GcodeSourceDto)(int)saved.Source,
                SourcePrinterId: saved.SourcePrinterId,
                SourcePrinterName: saved.SourcePrinter?.Name,
                OriginalPrinterPath: saved.OriginalPrinterPath,
                LastSeenOnPrinter: saved.LastSeenOnPrinter,
                Description: saved.Description,
                Tags: saved.Tags?.Split(',', StringSplitOptions.RemoveEmptyEntries),
                RequiredNozzleDiameter: saved.RequiredNozzleDiameter,
                RequiredMaterial: saved.RequiredMaterial,
                CompatibleMaterials: saved.CompatibleMaterials,
                EstimatedPrintTimeMinutes: saved.EstimatedPrintTimeMinutes,
                EstimatedFilamentLengthMm: saved.EstimatedFilamentLengthMm,
                EstimatedFilamentWeightG: saved.EstimatedFilamentWeightG,
                RequiredBuildVolumeX: saved.RequiredBuildVolumeX,
                RequiredBuildVolumeY: saved.RequiredBuildVolumeY,
                RequiredBuildVolumeZ: saved.RequiredBuildVolumeZ,
                TargetPrinterId: saved.TargetPrinterId,
                TargetPrinterName: saved.TargetPrinter?.Name,
                TargetModelId: saved.TargetModelId,
                TargetModelName: saved.TargetModel?.Name,
                SlicerName: saved.SlicerName,
                SlicerVersion: saved.SlicerVersion,
                HasThumbnail: !string.IsNullOrEmpty(saved.ThumbnailPath)
            ));
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
            GcodeFile? file = await gcodeRepo.GetByIdWithIncludesAsync(id, CancellationToken.None);
            if (file == null)
            {
                return NotFound($"G-code file with ID {id} not found");
            }

            // Check if file is being used in any queued jobs
            int activeJobs = await queueRepo.CountActiveJobsUsingGcodeAsync(id, CancellationToken.None);

            if (activeJobs > 0)
            {
                return BadRequest("Cannot delete file that is being used by active print jobs");
            }

            // Delete physical file (within library root only)
            string libraryPath = Path.Combine(env.WebRootPath, "gcode-library");
            string libraryRootFull = Path.GetFullPath(libraryPath);
            string filePathFull = Path.GetFullPath(file.FilePath);
#pragma warning disable CA3003 // Paths are validated to stay under known library root
            if (filePathFull.StartsWith(libraryRootFull, StringComparison.Ordinal) && System.IO.File.Exists(filePathFull))
            {
                System.IO.File.Delete(filePathFull);
            }

            // Delete thumbnail if exists
            if (!string.IsNullOrEmpty(file.ThumbnailPath))
            {
                string thumbFull = Path.GetFullPath(file.ThumbnailPath);
                if (thumbFull.StartsWith(libraryRootFull, StringComparison.Ordinal) && System.IO.File.Exists(thumbFull))
                {
                    System.IO.File.Delete(thumbFull);
                }
            }
#pragma warning restore CA3003

            await gcodeRepo.RemoveAsync(file, CancellationToken.None);
            await gcodeRepo.SaveChangesAsync(CancellationToken.None);

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
            GcodeFile? file = await gcodeRepo.GetByIdWithIncludesAsync(id, CancellationToken.None);
            if (file == null)
            {
                return NotFound($"G-code file with ID {id} not found");
            }

            string libraryPath = Path.Combine(env.WebRootPath, "gcode-library");
            string libraryRootFull = Path.GetFullPath(libraryPath);
            string filePathFull = Path.GetFullPath(file.FilePath);
#pragma warning disable CA3003 // Paths are validated to stay under known library root
            if (!filePathFull.StartsWith(libraryRootFull, StringComparison.Ordinal) || !System.IO.File.Exists(filePathFull))
            {
                return NotFound("Physical file not found on disk");
            }

            byte[] fileBytes = await System.IO.File.ReadAllBytesAsync(filePathFull);
#pragma warning restore CA3003
            return File(fileBytes, "application/octet-stream", file.OriginalFileName);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, $"Error downloading G-code file {id}");
            return Problem("An error occurred while downloading the file", statusCode: 500);
        }
    }
}
