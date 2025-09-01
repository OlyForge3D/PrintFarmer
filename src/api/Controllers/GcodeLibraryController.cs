using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Farm.Web.Api.Data;
using Farm.Web.Api.Domain;
using Farm.Web.Shared;
using System.Security.Cryptography;
using System.Text;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// Manages the G-code file library
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class GcodeLibraryController(AppDbContext db, IWebHostEnvironment env, ILogger<GcodeLibraryController> logger) : ControllerBase
{
    /// <summary>
    /// Get all G-code files in the library
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<IEnumerable<GcodeFileDto>>> GetLibraryAsync(
        [FromQuery] string? search = null,
        [FromQuery] string? material = null,
        [FromQuery] double? nozzleDiameter = null,
        [FromQuery] Guid? targetPrinterId = null)
    {
        try
        {
            var query = db.GcodeFiles
                .Include(g => g.SourcePrinter)
                .Include(g => g.TargetPrinter)
                .Include(g => g.TargetModel)
                .AsQueryable();

            // Apply filters
            if (!string.IsNullOrEmpty(search))
            {
                query = query.Where(g => g.OriginalFileName.Contains(search) || 
                                        g.DisplayName.Contains(search) ||
                                        (g.Description != null && g.Description.Contains(search)));
            }

            if (!string.IsNullOrEmpty(material))
            {
                query = query.Where(g => g.RequiredMaterial == material);
            }

            if (nozzleDiameter.HasValue)
            {
                query = query.Where(g => g.RequiredNozzleDiameter == nozzleDiameter.Value);
            }

            if (targetPrinterId.HasValue)
            {
                query = query.Where(g => g.TargetPrinterId == targetPrinterId.Value);
            }

            var files = await query
                .OrderByDescending(g => g.UploadedAt)
                .ToListAsync();

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
    public async Task<ActionResult<GcodeFileDto>> GetFileAsync(Guid id)
    {
        try
        {
            var file = await db.GcodeFiles
                .Include(g => g.SourcePrinter)
                .Include(g => g.TargetPrinter)
                .Include(g => g.TargetModel)
                .FirstOrDefaultAsync(g => g.Id == id);

            if (file == null)
            {
                return NotFound($"G-code file with ID {id} not found");
            }

            return Ok(new GcodeFileDto(
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
            ));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error retrieving G-code file {FileId}", id);
            return Problem("An error occurred while retrieving the file", statusCode: 500);
        }
    }

    /// <summary>
    /// Upload a new G-code file to the library
    /// </summary>
    [HttpPost("upload")]
    public async Task<ActionResult<GcodeFileDto>> UploadFileAsync([FromForm] IFormFile file, [FromForm] CreateGcodeFileDto metadata)
    {
        try
        {
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
            using (var stream = file.OpenReadStream())
            {
                using var sha256 = SHA256.Create();
                var hashBytes = await sha256.ComputeHashAsync(stream);
                hash = Convert.ToHexString(hashBytes);
            }

            // Check for duplicate
            var existing = await db.GcodeFiles.FirstOrDefaultAsync(g => g.FileHash == hash);
            if (existing != null)
            {
                return Conflict($"File already exists in library: {existing.DisplayName}");
            }

            // Ensure directory exists
            var libraryPath = Path.Combine(env.WebRootPath, "gcode-library");
            Directory.CreateDirectory(libraryPath);

            // Save file
            var fileName = $"{Guid.NewGuid()}{Path.GetExtension(file.FileName)}";
            var filePath = Path.Combine(libraryPath, fileName);

            using (var stream = System.IO.File.Create(filePath))
            {
                await file.CopyToAsync(stream);
            }

            // Create database entry
            var gcodeFile = new GcodeFile
            {
                Id = Guid.NewGuid(),
                OriginalFileName = file.FileName,
                DisplayName = string.IsNullOrEmpty(metadata.DisplayName) ? 
                    Path.GetFileNameWithoutExtension(file.FileName) : metadata.DisplayName,
                FilePath = filePath,
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

            db.GcodeFiles.Add(gcodeFile);
            await db.SaveChangesAsync();

            // Load related entities for response
            await db.Entry(gcodeFile)
                .Reference(g => g.TargetPrinter)
                .LoadAsync();
            await db.Entry(gcodeFile)
                .Reference(g => g.TargetModel)
                .LoadAsync();

            return CreatedAtAction(nameof(GetFileAsync), new { id = gcodeFile.Id }, new GcodeFileDto(
                Id: gcodeFile.Id,
                OriginalFileName: gcodeFile.OriginalFileName,
                DisplayName: gcodeFile.DisplayName,
                FileSizeBytes: gcodeFile.FileSizeBytes,
                UploadedAt: gcodeFile.UploadedAt,
                Source: (GcodeSourceDto)(int)gcodeFile.Source,
                SourcePrinterId: gcodeFile.SourcePrinterId,
                SourcePrinterName: gcodeFile.SourcePrinter?.Name,
                OriginalPrinterPath: gcodeFile.OriginalPrinterPath,
                LastSeenOnPrinter: gcodeFile.LastSeenOnPrinter,
                Description: gcodeFile.Description,
                Tags: gcodeFile.Tags?.Split(',', StringSplitOptions.RemoveEmptyEntries),
                RequiredNozzleDiameter: gcodeFile.RequiredNozzleDiameter,
                RequiredMaterial: gcodeFile.RequiredMaterial,
                CompatibleMaterials: gcodeFile.CompatibleMaterials,
                EstimatedPrintTimeMinutes: gcodeFile.EstimatedPrintTimeMinutes,
                EstimatedFilamentLengthMm: gcodeFile.EstimatedFilamentLengthMm,
                EstimatedFilamentWeightG: gcodeFile.EstimatedFilamentWeightG,
                RequiredBuildVolumeX: gcodeFile.RequiredBuildVolumeX,
                RequiredBuildVolumeY: gcodeFile.RequiredBuildVolumeY,
                RequiredBuildVolumeZ: gcodeFile.RequiredBuildVolumeZ,
                TargetPrinterId: gcodeFile.TargetPrinterId,
                TargetPrinterName: gcodeFile.TargetPrinter?.Name,
                TargetModelId: gcodeFile.TargetModelId,
                TargetModelName: gcodeFile.TargetModel?.Name,
                SlicerName: gcodeFile.SlicerName,
                SlicerVersion: gcodeFile.SlicerVersion,
                HasThumbnail: !string.IsNullOrEmpty(gcodeFile.ThumbnailPath)
            ));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error uploading G-code file {FileName}", file?.FileName);
            return Problem("An error occurred while uploading the file", statusCode: 500);
        }
    }

    /// <summary>
    /// Update G-code file metadata
    /// </summary>
    [HttpPut("{id}")]
    public async Task<ActionResult<GcodeFileDto>> UpdateFileAsync(Guid id, [FromBody] UpdateGcodeFileDto request)
    {
        try
        {
            var file = await db.GcodeFiles
                .Include(g => g.SourcePrinter)
                .Include(g => g.TargetPrinter)
                .Include(g => g.TargetModel)
                .FirstOrDefaultAsync(g => g.Id == id);

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
                // Load the printer for response
                await db.Entry(file)
                    .Reference(g => g.TargetPrinter)
                    .LoadAsync();
            }

            if (request.TargetModelId.HasValue)
            {
                file.TargetModelId = request.TargetModelId.Value;
                // Load the model for response
                await db.Entry(file)
                    .Reference(g => g.TargetModel)
                    .LoadAsync();
            }

            file.UpdatedAt = DateTime.UtcNow;

            await db.SaveChangesAsync();

            return Ok(new GcodeFileDto(
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
            ));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error updating G-code file {FileId}", id);
            return Problem("An error occurred while updating the file", statusCode: 500);
        }
    }

    /// <summary>
    /// Delete a G-code file from the library
    /// </summary>
    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteFileAsync(Guid id)
    {
        try
        {
            var file = await db.GcodeFiles.FindAsync(id);
            if (file == null)
            {
                return NotFound($"G-code file with ID {id} not found");
            }

            // Check if file is being used in any queued jobs
            var activeJobs = await db.PrintJobs
                .Where(j => j.GcodeFileId == id && 
                           (j.Status == PrintJobStatus.Queued || 
                            j.Status == PrintJobStatus.Assigned || 
                            j.Status == PrintJobStatus.Starting || 
                            j.Status == PrintJobStatus.Printing))
                .CountAsync();

            if (activeJobs > 0)
            {
                return BadRequest("Cannot delete file that is being used by active print jobs");
            }

            // Delete physical file
            if (System.IO.File.Exists(file.FilePath))
            {
                System.IO.File.Delete(file.FilePath);
            }

            // Delete thumbnail if exists
            if (!string.IsNullOrEmpty(file.ThumbnailPath) && System.IO.File.Exists(file.ThumbnailPath))
            {
                System.IO.File.Delete(file.ThumbnailPath);
            }

            db.GcodeFiles.Remove(file);
            await db.SaveChangesAsync();

            return NoContent();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error deleting G-code file {FileId}", id);
            return Problem("An error occurred while deleting the file", statusCode: 500);
        }
    }

    /// <summary>
    /// Download a G-code file
    /// </summary>
    [HttpGet("{id}/download")]
    public async Task<IActionResult> DownloadFileAsync(Guid id)
    {
        try
        {
            var file = await db.GcodeFiles.FindAsync(id);
            if (file == null)
            {
                return NotFound($"G-code file with ID {id} not found");
            }

            if (!System.IO.File.Exists(file.FilePath))
            {
                return NotFound("Physical file not found on disk");
            }

            var fileBytes = await System.IO.File.ReadAllBytesAsync(file.FilePath);
            return File(fileBytes, "application/octet-stream", file.OriginalFileName);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error downloading G-code file {FileId}", id);
            return Problem("An error occurred while downloading the file", statusCode: 500);
        }
    }
}
