using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text; // Needed for Encoding when deriving secondary hash
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Services.FileManagement;
using Farm.Web.Api.Services.Interfaces;
using Farm.Web.Api.Services.Model;
using Farm.Web.Shared;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// Controller for managing 3D model files for slicing and printing
/// </summary>
[ApiController]
[Route("api/3d-models")] // Updated route to be more specific and avoid naming conflicts
public class ModelController : ControllerBase
{
    private readonly IUnifiedLoggingService _logger;
    private readonly IModelService _modelService;
    private readonly IModelAnalysisService _analysisService;
    private readonly IVirusScanner _virusScanner;
    private readonly IThumbnailGenerationService _thumbnailService;
    private readonly string _modelsPath;
    private readonly Farm.Web.Api.Services.IO.IFileSystem _fileSystem;
    private readonly IFileManagementService _fileManagementService;

    public ModelController(
        IUnifiedLoggingService logger,
        IModelService modelService,
        IConfiguration configuration,
        IModelAnalysisService analysisService,
        IVirusScanner virusScanner,
        IThumbnailGenerationService thumbnailService,
        Farm.Web.Api.Services.IO.IFileSystem fileSystem,
        IFileManagementService fileManagementService)
    {
        _logger = logger;
        _modelService = modelService ?? throw new ArgumentNullException(nameof(modelService));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _fileManagementService = fileManagementService ?? throw new ArgumentNullException(nameof(fileManagementService));
        ArgumentNullException.ThrowIfNull(configuration);
        _modelsPath = configuration["ModelStorage:Path"] ?? Path.Combine(Directory.GetCurrentDirectory(), "models");
        _analysisService = analysisService ?? throw new ArgumentNullException(nameof(analysisService));
        _virusScanner = virusScanner ?? throw new ArgumentNullException(nameof(virusScanner));
        _thumbnailService = thumbnailService ?? throw new ArgumentNullException(nameof(thumbnailService));

        if (!_fileSystem.DirectoryExists(_modelsPath))
        {
            _fileSystem.CreateDirectory(_modelsPath);
        }
    }

    /// <summary>
    /// Upload a 3D model file
    /// </summary>
    /// <returns>Model upload result with ID and URL</returns>
    [HttpPost]
    [ProducesResponseType(typeof(Model3DUploadResultDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [RequestSizeLimit(100_000_000)] // 100MB limit
    [SuppressMessage("Security", "CA3003", Justification = "File name is GUID-based and path validated via IsSafePath; no user-controlled traversal.")]
    public async Task<IActionResult> UploadModelAsync([FromForm] IFormFile modelFile)
    {
        if (modelFile == null || modelFile.Length == 0)
        {
            return BadRequest("Model file is required");
        }

        // Validate file extension using service
        string fileExtension = Path.GetExtension(modelFile.FileName ?? string.Empty);
        try
        {
            _fileManagementService.ValidateModelExtension(fileExtension);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }

        try
        {
            var result = await _modelService.UploadModelAsync(modelFile, CancellationToken.None);
            return CreatedAtRoute("GetModel", new { id = result.Id }, result);
        }
        catch (Exception ex)
        {
            _logger.LogError($"Failed to upload model file: {modelFile.FileName}: {ex.Message}");
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
            var models = await _modelService.ListModelsAsync(CancellationToken.None);
            return Ok(models);
        }
        catch (Exception ex)
        {
            _logger.LogError($"Failed to list models: {ex.Message}");
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
        var dto = await _modelService.GetModelAsync(id, CancellationToken.None);
        if (dto == null)
        {
            return NotFound();
        }
        return Ok(dto);
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
        var path = await _modelService.GetModelFilePathAsync(id, CancellationToken.None);
        if (string.IsNullOrEmpty(path) || !_fileSystem.FileExists(path) || !_fileManagementService.IsSafePath(path, _modelsPath))
        {
            return NotFound();
        }

        string fileExtension = Path.GetExtension(path);
        string contentType = fileExtension.ToLowerInvariant() switch
        {
            ".stl" => "application/vnd.ms-pki.stl",
            ".3mf" => "model/3mf",
            ".obj" => "text/plain",
            ".ply" => "application/octet-stream",
            _ => "application/octet-stream"
        };

        // Lookup original name to set Content-Disposition filename
        // For performance, fetch model DTO
        var dto = await _modelService.GetModelAsync(id, CancellationToken.None);
        string fileName = dto?.FileName ?? Path.GetFileName(path);
        return PhysicalFile(path, contentType, fileName);
    }

    /// <summary>
    /// Get model thumbnail image
    /// </summary>
    /// <param name="id">Model ID</param>
    /// <returns>Thumbnail image</returns>
    [HttpGet("{id:guid}/thumbnail")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetModelThumbnailAsync(Guid id)
    {
        var thumbPath = await _modelService.GetModelThumbnailPathAsync(id, CancellationToken.None);
        if (string.IsNullOrEmpty(thumbPath) || !_fileSystem.FileExists(thumbPath) || !_fileManagementService.IsSafePath(thumbPath, Path.GetDirectoryName(thumbPath) ?? string.Empty))
        {
            return NotFound("Thumbnail not available");
        }

        string contentType = "image/png";
        return PhysicalFile(thumbPath, contentType);
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
        try
        {
            await _modelService.DeleteModelAsync(id, CancellationToken.None);
            return NoContent();
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (Exception ex)
        {
            _logger.LogError($"Failed to delete model: {id}: {ex.Message}");
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
        try
        {
            var result = _modelService.ValidateModel(modelFile);
            return Ok(result);
        }
        catch (ArgumentException ae)
        {
            return BadRequest(ae.Message);
        }
    }
}
