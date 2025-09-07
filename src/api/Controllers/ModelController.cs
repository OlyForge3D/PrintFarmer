using System.Security.Cryptography;
using Farm.Web.Api.Data;
using Farm.Web.Api.Domain;
using Farm.Web.Shared;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// Controller for managing 3D model files for slicing and printing
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class ModelController : ControllerBase
{
    private readonly ILogger<ModelController> _logger;
    private readonly AppDbContext _context;
    private readonly string _modelsPath;

    public ModelController(ILogger<ModelController> logger, AppDbContext context, IConfiguration configuration)
    {
        _logger = logger;
        _context = context;
        ArgumentNullException.ThrowIfNull(configuration);
        _modelsPath = configuration["ModelStorage:Path"] ?? Path.Combine(Directory.GetCurrentDirectory(), "models");

        // Ensure models directory exists
        if (!Directory.Exists(_modelsPath))
        {
            Directory.CreateDirectory(_modelsPath);
        }
    }

    /// <summary>
    /// Upload a 3D model file
    /// </summary>
    /// <param name="modelFile">The model file to upload</param>
    /// <returns>Model upload result with ID and URL</returns>
    [HttpPost]
    [ProducesResponseType(typeof(Model3DUploadResultDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [RequestSizeLimit(100_000_000)] // 100MB limit
    public async Task<IActionResult> UploadModelAsync(IFormFile modelFile)
    {
        if (modelFile == null || modelFile.Length == 0)
        {
            return BadRequest("Model file is required");
        }

        // Validate file extension
        var allowedExtensions = new[] { ".stl", ".3mf", ".obj", ".ply" };
        var originalName = modelFile.FileName ?? string.Empty;
        var fileExtension = Path.GetExtension(originalName).ToLowerInvariant();

        if (!allowedExtensions.Contains(fileExtension))
        {
            return BadRequest($"Invalid file type. Allowed types: {string.Join(", ", allowedExtensions)}");
        }

        // Generate unique filename and calculate hash
        var modelId = Guid.NewGuid();
        var fileName = $"{modelId}{fileExtension}";
        var filePath = Path.Combine(_modelsPath, fileName);
        if (!IsSafePath(filePath, _modelsPath))
        {
            return BadRequest("Unsafe file path generated");
        }

        try
        {
            // Calculate file hash while saving
            string fileHash;
            using (var stream = new FileStream(filePath, FileMode.Create))
            {
                using var memoryStream = new MemoryStream();
                await modelFile.CopyToAsync(memoryStream);
                memoryStream.Position = 0;

                // Calculate hash
                using var sha256 = SHA256.Create();
                var hashBytes = await sha256.ComputeHashAsync(memoryStream);
                fileHash = Convert.ToHexString(hashBytes);

                // Write to file
                memoryStream.Position = 0;
                await memoryStream.CopyToAsync(stream);
            }

            // Check for duplicate by hash
            var existingModel = await _context.Models3D
                .FirstOrDefaultAsync(m => m.FileHash == fileHash);

            if (existingModel != null)
            {
                // Clean up the duplicate file
                if (System.IO.File.Exists(filePath))
                {
                    System.IO.File.Delete(filePath);
                }

                // Return existing model info
                var existingResult = new Model3DUploadResultDto
                {
                    Id = existingModel.Id,
                    Name = existingModel.DisplayName,
                    FileName = existingModel.OriginalFileName,
                    FileSize = existingModel.FileSizeBytes,
                    FileType = GetFileTypeString(existingModel.FileFormat),
                    UploadedAt = existingModel.UploadedAt,
                    Url = $"/api/models/{existingModel.Id}/file"
                };

                return Ok(existingResult); // Return 200 for duplicate instead of 201
            }

            // Create database entity
            var model = new Model3D
            {
                Id = modelId,
                OriginalFileName = originalName,
                DisplayName = Path.GetFileNameWithoutExtension(originalName),
                FilePath = filePath,
                FileSizeBytes = modelFile.Length,
                FileHash = fileHash,
                FileFormat = GetFileFormat(fileExtension),
                UploadedAt = DateTime.UtcNow,
                IsValid = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            _context.Models3D.Add(model);
            await _context.SaveChangesAsync();

            // Create result
            var result = new Model3DUploadResultDto
            {
                Id = modelId,
                Name = model.DisplayName,
                FileName = originalName,
                FileSize = modelFile.Length,
                FileType = fileExtension.TrimStart('.'),
                UploadedAt = DateTime.UtcNow,
                Url = $"/api/models/{modelId}/file"
            };

            _logger.LogInformation("Model uploaded: {ModelId} ({FileName}, {FileSize} bytes)",
                modelId, modelFile.FileName, modelFile.Length);

            return CreatedAtAction(nameof(GetModelAsync), new { id = modelId }, result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to upload model file: {FileName}", modelFile.FileName);

            // Clean up file if it was partially created
            if (IsSafePath(filePath, _modelsPath) && System.IO.File.Exists(filePath))
            {
                System.IO.File.Delete(filePath);
            }

            return StatusCode(StatusCodes.Status500InternalServerError, "Failed to upload model file");
        }
    }

    /// <summary>
    /// List all uploaded models
    /// </summary>
    /// <returns>List of model metadata</returns>
    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<Model3DDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListModelsAsync()
    {
        try
        {
            var models = await _context.Models3D
                .Where(m => m.IsValid)
                .OrderByDescending(m => m.UploadedAt)
                .Select(m => new Model3DDto
                {
                    Id = m.Id,
                    Name = m.DisplayName,
                    FileName = m.OriginalFileName,
                    FileSize = m.FileSizeBytes,
                    FileType = GetFileTypeString(m.FileFormat),
                    UploadedAt = m.UploadedAt,
                    Url = $"/api/models/{m.Id}/file",
                    ThumbnailUrl = m.ThumbnailPath != null ? $"/api/models/{m.Id}/thumbnail" : null
                })
                .ToListAsync();

            return Ok(models);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list models");
            return StatusCode(StatusCodes.Status500InternalServerError, "Failed to list models");
        }
    }

    /// <summary>
    /// Get model metadata by ID
    /// </summary>
    /// <param name="id">Model ID</param>
    /// <returns>Model metadata</returns>
    [HttpGet("{id:guid}", Name = "GetModel")]
    [ProducesResponseType(typeof(Model3DDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetModelAsync(Guid id)
    {
        var model = await _context.Models3D
            .FirstOrDefaultAsync(m => m.Id == id && m.IsValid);

        if (model == null)
        {
            return NotFound();
        }

        var modelDto = new Model3DDto
        {
            Id = model.Id,
            Name = model.DisplayName,
            FileName = model.OriginalFileName,
            FileSize = model.FileSizeBytes,
            FileType = GetFileTypeString(model.FileFormat),
            UploadedAt = model.UploadedAt,
            Url = $"/api/models/{model.Id}/file",
            ThumbnailUrl = model.ThumbnailPath != null ? $"/api/models/{model.Id}/thumbnail" : null
        };

        return Ok(modelDto);
    }

    /// <summary>
    /// Download model file
    /// </summary>
    /// <param name="id">Model ID</param>
    /// <returns>Model file</returns>
    [HttpGet("{id:guid}/file")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetModelFileAsync(Guid id)
    {
        var model = await _context.Models3D
            .FirstOrDefaultAsync(m => m.Id == id && m.IsValid);

        if (model == null)
        {
            return NotFound();
        }

        if (!IsSafePath(model.FilePath, _modelsPath) || !System.IO.File.Exists(model.FilePath))
        {
            return NotFound();
        }

        var fileExtension = Path.GetExtension(model.FilePath);
        var contentType = fileExtension.ToLowerInvariant() switch
        {
            ".stl" => "application/vnd.ms-pki.stl",
            ".3mf" => "model/3mf",
            ".obj" => "text/plain",
            ".ply" => "application/octet-stream",
            _ => "application/octet-stream"
        };

        return PhysicalFile(model.FilePath, contentType, model.OriginalFileName);
    }

    /// <summary>
    /// Delete a model
    /// </summary>
    /// <param name="id">Model ID</param>
    /// <returns>No content if successful</returns>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteModelAsync(Guid id)
    {
        var model = await _context.Models3D
            .FirstOrDefaultAsync(m => m.Id == id);

        if (model == null)
        {
            return NotFound();
        }

        try
        {
            // Delete physical file
            if (IsSafePath(model.FilePath, _modelsPath) && System.IO.File.Exists(model.FilePath))
            {
                System.IO.File.Delete(model.FilePath);
            }

            // Delete thumbnail if exists
            if (model.ThumbnailPath != null && System.IO.File.Exists(model.ThumbnailPath))
            {
                System.IO.File.Delete(model.ThumbnailPath);
            }

            // Remove from database
            _context.Models3D.Remove(model);
            await _context.SaveChangesAsync();

            _logger.LogInformation("Model deleted: {ModelId}", id);
            return NoContent();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete model: {ModelId}", id);
            return StatusCode(StatusCodes.Status500InternalServerError, "Failed to delete model");
        }
    }

    /// <summary>
    /// Validate a model file
    /// </summary>
    /// <param name="modelFile">The model file to validate</param>
    /// <returns>Validation result</returns>
    [HttpPost("validate")]
    [ProducesResponseType(typeof(Model3DValidationResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public IActionResult ValidateModel(IFormFile modelFile)
    {
        if (modelFile == null || modelFile.Length == 0)
        {
            return BadRequest("Model file is required");
        }

        var issues = new List<string>();
        var fileExtension = Path.GetExtension(modelFile.FileName).ToLowerInvariant();

        // Check file extension
        var allowedExtensions = new[] { ".stl", ".3mf", ".obj", ".ply" };
        if (!allowedExtensions.Contains(fileExtension))
        {
            issues.Add($"Invalid file type. Allowed types: {string.Join(", ", allowedExtensions)}");
        }

        // Check file size (max 100MB)
        if (modelFile.Length > 100_000_000)
        {
            issues.Add("File size exceeds 100MB limit");
        }

        // Basic content validation could be added here
        // For now, we'll just check if it's not empty and has correct extension

        var result = new Model3DValidationResultDto
        {
            Valid = issues.Count == 0,
            Issues = issues.Count > 0 ? [.. issues] : null
        };

        return Ok(result);
    }

    private static bool IsSafePath(string candidatePath, string root)
    {
        try
        {
            var fullRoot = Path.GetFullPath(root);
            var fullCandidate = Path.GetFullPath(candidatePath);
            return fullCandidate.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static ModelFileFormat GetFileFormat(string extension)
    {
        return extension.ToLowerInvariant() switch
        {
            ".stl" => ModelFileFormat.STL,
            ".3mf" => ModelFileFormat.TMF,
            ".obj" => ModelFileFormat.OBJ,
            ".ply" => ModelFileFormat.PLY,
            ".step" => ModelFileFormat.STEP,
            _ => ModelFileFormat.STL
        };
    }

    private static string GetFileTypeString(ModelFileFormat format)
    {
        return format switch
        {
            ModelFileFormat.STL => "stl",
            ModelFileFormat.TMF => "3mf",
            ModelFileFormat.OBJ => "obj",
            ModelFileFormat.PLY => "ply",
            ModelFileFormat.STEP => "step",
            _ => "stl"
        };
    }
}
