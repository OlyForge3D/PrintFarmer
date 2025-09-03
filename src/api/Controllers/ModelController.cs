using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Farm.Web.Shared;
using System.ComponentModel.DataAnnotations;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// Controller for managing 3D model files for slicing and printing
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class ModelController : ControllerBase
{
    private readonly ILogger<ModelController> _logger;
    private readonly string _modelsPath;
    
    public ModelController(ILogger<ModelController> logger, IConfiguration configuration)
    {
        _logger = logger;
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
        var fileExtension = Path.GetExtension(modelFile.FileName).ToLowerInvariant();
        
        if (!allowedExtensions.Contains(fileExtension))
        {
            return BadRequest($"Invalid file type. Allowed types: {string.Join(", ", allowedExtensions)}");
        }

        // Generate unique filename
        var modelId = Guid.NewGuid();
        var fileName = $"{modelId}{fileExtension}";
        var filePath = Path.Combine(_modelsPath, fileName);

        try
        {
            // Save file to disk
            using (var stream = new FileStream(filePath, FileMode.Create))
            {
                await modelFile.CopyToAsync(stream);
            }

            // Create result
            var result = new Model3DUploadResultDto
            {
                Id = modelId,
                Name = Path.GetFileNameWithoutExtension(modelFile.FileName),
                FileName = modelFile.FileName,
                FileSize = modelFile.Length,
                FileType = fileExtension.TrimStart('.'),
                UploadedAt = DateTime.UtcNow,
                Url = $"/api/models/{modelId}/file"
            };

            _logger.LogInformation("Model uploaded: {ModelId} ({FileName}, {FileSize} bytes)", 
                modelId, modelFile.FileName, modelFile.Length);

            return CreatedAtAction(nameof(GetModel), new { id = modelId }, result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to upload model file: {FileName}", modelFile.FileName);
            
            // Clean up file if it was partially created
            if (System.IO.File.Exists(filePath))
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
    public IActionResult ListModels()
    {
        try
        {
            var models = new List<Model3DDto>();
            var modelFiles = Directory.GetFiles(_modelsPath);

            foreach (var filePath in modelFiles)
            {
                var fileName = Path.GetFileName(filePath);
                if (Guid.TryParse(Path.GetFileNameWithoutExtension(fileName), out var modelId))
                {
                    var fileInfo = new FileInfo(filePath);
                    var fileExtension = fileInfo.Extension.TrimStart('.');
                    
                    models.Add(new Model3DDto
                    {
                        Id = modelId,
                        Name = $"Model {modelId.ToString()[..8]}",
                        FileName = fileName,
                        FileSize = fileInfo.Length,
                        FileType = fileExtension,
                        UploadedAt = fileInfo.CreationTimeUtc,
                        Url = $"/api/models/{modelId}/file"
                    });
                }
            }

            return Ok(models.OrderByDescending(m => m.UploadedAt));
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
    public IActionResult GetModel(Guid id)
    {
        var possibleFiles = Directory.GetFiles(_modelsPath, $"{id}.*");
        if (possibleFiles.Length == 0)
        {
            return NotFound();
        }

        var filePath = possibleFiles[0];
        var fileInfo = new FileInfo(filePath);
        var fileExtension = fileInfo.Extension.TrimStart('.');

        var model = new Model3DDto
        {
            Id = id,
            Name = $"Model {id.ToString()[..8]}",
            FileName = Path.GetFileName(filePath),
            FileSize = fileInfo.Length,
            FileType = fileExtension,
            UploadedAt = fileInfo.CreationTimeUtc,
            Url = $"/api/models/{id}/file"
        };

        return Ok(model);
    }

    /// <summary>
    /// Download model file
    /// </summary>
    /// <param name="id">Model ID</param>
    /// <returns>Model file</returns>
    [HttpGet("{id:guid}/file")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult GetModelFile(Guid id)
    {
        var possibleFiles = Directory.GetFiles(_modelsPath, $"{id}.*");
        if (possibleFiles.Length == 0)
        {
            return NotFound();
        }

        var filePath = possibleFiles[0];
        var fileName = Path.GetFileName(filePath);
        var fileExtension = Path.GetExtension(filePath);

        var contentType = fileExtension.ToLowerInvariant() switch
        {
            ".stl" => "application/vnd.ms-pki.stl",
            ".3mf" => "model/3mf",
            ".obj" => "text/plain",
            ".ply" => "application/octet-stream",
            _ => "application/octet-stream"
        };

        return PhysicalFile(filePath, contentType, fileName);
    }

    /// <summary>
    /// Delete a model
    /// </summary>
    /// <param name="id">Model ID</param>
    /// <returns>No content if successful</returns>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult DeleteModel(Guid id)
    {
        var possibleFiles = Directory.GetFiles(_modelsPath, $"{id}.*");
        if (possibleFiles.Length == 0)
        {
            return NotFound();
        }

        try
        {
            foreach (var filePath in possibleFiles)
            {
                System.IO.File.Delete(filePath);
            }

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
            Issues = issues.Count > 0 ? issues.ToArray() : null
        };

        return Ok(result);
    }
}