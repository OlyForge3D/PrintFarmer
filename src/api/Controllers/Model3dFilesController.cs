using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text; // Needed for Encoding when deriving secondary hash
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.UnitOfWork;
using Farm.Infrastructure.Security;
using Farm.Infrastructure.Services.Models;
using Farm.Infrastructure.Services.StorageManagement;
using Farm.Infrastructure.Services.Thumbnails;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Services.FileManagement;
using Farm.Web.Api.Services.FolderManagement;
using Farm.Web.Api.Services.Model;
using Farm.Web.Api.Services.Tags;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// Controller for managing 3D model files for slicing and printing
/// </summary>
[ApiController]
[Route("api/3d-models")] // Updated route to be more specific and avoid naming conflicts
public class Model3DFilesController : ControllerBase
{
    private readonly IUnifiedLoggingService _logger;
    private readonly IModel3DFileService _modelService;
    private readonly string _modelsPath;
    private readonly Services.IO.IFileSystem _fileSystem;
    private readonly IFileManagementService _fileManagementService;
    private readonly ITagService _tagService;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IFolderManagementService _folderService;
    private readonly IStoredFileOperationsService _fileOperations;
    private readonly IStoragePathService _storagePathService;
    private readonly I3MfToStlConversionService _threeMfConverter;

    public Model3DFilesController(
        IUnifiedLoggingService logger,
        IModel3DFileService modelService,
        IConfiguration configuration,
        Services.IO.IFileSystem fileSystem,
        IFileManagementService fileManagementService,
        ITagService tagService,
        IUnitOfWork unitOfWork,
        IFolderManagementService folderService,
        IStoredFileOperationsService fileOperations,
        IStoragePathService storagePathService,
        I3MfToStlConversionService threeMfConverter)
    {
        _logger = logger;
        _modelService = modelService ?? throw new ArgumentNullException(nameof(modelService));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _fileManagementService = fileManagementService ?? throw new ArgumentNullException(nameof(fileManagementService));
        _tagService = tagService ?? throw new ArgumentNullException(nameof(tagService));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
        _folderService = folderService ?? throw new ArgumentNullException(nameof(folderService));
        _fileOperations = fileOperations ?? throw new ArgumentNullException(nameof(fileOperations));
        _storagePathService = storagePathService ?? throw new ArgumentNullException(nameof(storagePathService));
        _threeMfConverter = threeMfConverter ?? throw new ArgumentNullException(nameof(threeMfConverter));
        ArgumentNullException.ThrowIfNull(configuration);
        string configPath = configuration["ModelStorage:Path"] ?? "models";
        // Ensure path is absolute - if relative, combine with current directory first
        _modelsPath = Path.IsPathRooted(configPath)
            ? configPath
            : Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), configPath));

        if (!_fileSystem.DirectoryExists(_modelsPath))
        {
            _fileSystem.CreateDirectory(_modelsPath);
        }
    }

    /// <summary>
    /// Resolves a model file path to an absolute path.
    /// Database stores relative paths (e.g., "uuid.stl") from the models root.
    /// For backward compatibility with older data, also handles "models/uuid.stl" format by stripping the prefix.
    /// </summary>
    private string ResolvePath(string? relativePath)
    {
        if (string.IsNullOrEmpty(relativePath))
        {
            return string.Empty;
        }

        // Normalize virtual paths - strip leading slashes to handle virtual path format
        string normalizedPath = relativePath.TrimStart('/').Trim();
        if (string.IsNullOrEmpty(normalizedPath))
        {
            return _modelsPath;
        }

        // If the normalized path is already absolute, return as-is (Windows C:\ or Unix /mnt/etc)
        if (Path.IsPathRooted(normalizedPath))
        {
            return normalizedPath;
        }

        // Combine relative path with models directory (guaranteed to be absolute)
        return Path.Combine(_modelsPath, normalizedPath);
    }

    /// <summary>
    /// Generates the appropriate viewer URL for a model based on its file type.
    /// For 3MF files, returns the file endpoint with forceStl=true parameter.
    /// For other formats, returns the direct file download URL.
    /// </summary>
    private string GenerateViewerUrl(Model3D model)
    {
        if (model.FileFormat == ModelFileFormat.TMF) // 3MF format
        {
            // Return the file endpoint with forceStl=true to trigger conversion
            return $"/api/3d-models/{model.Id}/file?forceStl=true";
        }

        // For STL, PLY, OBJ, STEP - return direct file URL
        return $"/api/3d-models/{model.Id}/file";
    }

    /// <summary>
    /// Upload a 3D model file
    /// </summary>
    /// <returns>Model upload result with ID and URL</returns>
    [HttpPost("upload")]
    [ProducesResponseType(typeof(Model3DUploadResultDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [RequestSizeLimit(500_000_000)] // 500MB limit for individual 3D model files
    [SuppressMessage("Security", "CA3003", Justification = "File name is GUID-based and path validated via IsSafePath; no user-controlled traversal.")]
    public async Task<IActionResult> UploadModelAsync([FromForm] IFormFile modelFile)
    {
        _logger.LogInformation($"[Upload] Received upload request for file: {modelFile?.FileName ?? "NULL"}, Size: {modelFile?.Length ?? 0} bytes");

        if (modelFile == null || modelFile.Length == 0)
        {
            _logger.LogWarning("[Upload] Model file is null or empty");
            return BadRequest("Model file is required");
        }

        // Validate file extension using service
        string fileExtension = Path.GetExtension(modelFile.FileName ?? string.Empty);
        _logger.LogInformation($"[Upload] File extension: {fileExtension}");

        try
        {
            _fileManagementService.ValidateModelExtension(fileExtension);
            _logger.LogInformation($"[Upload] File extension validation passed");
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning($"[Upload] File extension validation failed: {ex.Message}");
            return BadRequest(ex.Message);
        }

        try
        {
            _logger.LogInformation($"[Upload] Starting model upload service for {modelFile.FileName}");
            Model3DUploadResultDto result = await _modelService.UploadModelAsync(modelFile, CancellationToken.None);
            _logger.LogInformation($"[Upload] Upload completed successfully. Model ID: {result.Id}, File size: {result.FileSize} bytes");
            return CreatedAtRoute("GetModel", new { id = result.Id }, result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"[Upload] Failed to upload model file: {modelFile.FileName}: {ex.Message}");
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
            IReadOnlyList<Model3DDto> models = await _modelService.ListModelsAsync(CancellationToken.None);
            return Ok(models);
        }
        catch (Exception ex)
        {
            _logger.LogError($"Failed to list models: {ex.Message}");
            return StatusCode(StatusCodes.Status500InternalServerError, "Failed to list models");
        }
    }

    /// <summary>
    /// List models and subdirectories within a specific path (hierarchical browsing)
    /// </summary>
    /// <param name="path">Virtual path to browse (e.g., '/', '/subfolder')</param>
    /// <param name="sortBy">Sort field: name, size, or date</param>
    /// <param name="sortOrder">asc or desc</param>
    /// <param name="search">Optional search term to filter by filename</param>
    /// <param name="page">Page number (1-based)</param>
    /// <param name="pageSize">Items per page</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Hierarchical listing with files and directories</returns>
    [HttpGet("hierarchy")]
    [ProducesResponseType(typeof(Model3DListResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ListModelsHierarchicalAsync(
        [FromQuery] string? path = "/",
        [FromQuery] string? sortBy = "name",
        [FromQuery] string? sortOrder = "asc",
        [FromQuery] string? search = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        try
        {
            var response = await _modelService.ListModelsWithHierarchyAsync(
                path ?? "/",
                sortBy ?? "name",
                sortOrder ?? "asc",
                search,
                page,
                pageSize,
                ct);
            return Ok(response);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError($"Failed to list models hierarchically: {ex.Message}");
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
        Model3DDto? dto = await _modelService.GetModelAsync(id, CancellationToken.None);
        if (dto == null)
        {
            return NotFound();
        }
        return Ok(dto);
    }

    /// <summary>
    /// Get full model details including tags
    /// </summary>
    /// <param name="id">Model ID</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Detailed model information with tags</returns>
    [HttpGet("{id:guid}/details")]
    [ProducesResponseType(typeof(Model3DDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetModelDetailsAsync(Guid id, CancellationToken ct)
    {
        try
        {
            Model3D? model = await _unitOfWork.Model3dFiles.GetByIdWithTagsAsync(id, ct);
            if (model == null)
            {
                return NotFound();
            }

            Model3DDto dto = new Model3DDto
            {
                Id = model.Id,
                FileName = model.FileName,
                FileSize = model.FileSizeBytes,
                FileType = _fileManagementService.GetModelFileFormatString(model.FileFormat),
                UploadedAt = model.UploadedAt,
                Url = GenerateViewerUrl(model),
                ThumbnailPath = _fileOperations.BuildThumbnailUrl(model, "/api/3d-models/download", _storagePathService.GetModelUploadDirectory()),
                Description = model.Description,
                DimensionX = model.DimensionX,
                DimensionY = model.DimensionY,
                DimensionZ = model.DimensionZ,
                TriangleCount = model.TriangleCount,
                IsValid = model.IsValid,
                ValidationErrors = model.ValidationErrors,
                Tags = model.TagMappings.Select(tm => new Model3DTagDto
                {
                    Id = tm.Tag!.Id,
                    Name = tm.Tag!.Name,
                    Color = tm.Tag!.Color,
                    Description = tm.Tag!.Description
                }).ToArray()
            };

            return Ok(dto);
        }
        catch (Exception ex)
        {
            _logger.LogError($"Failed to get model details {id}: {ex.Message}");
            return StatusCode(StatusCodes.Status500InternalServerError, "Failed to get model details");
        }
    }

    /// <summary>
    /// Download model file
    /// </summary>
    /// <param name="id">Model ID</param>
    /// <param name="forceStl">Force conversion of 3MF files to STL format</param>
    /// <returns>Model file</returns>
    [HttpGet("{id:guid}/file")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetModelFileAsync(Guid id, [FromQuery] bool forceStl = false)
    {
        string? path = await _modelService.GetModelFilePathAsync(id, CancellationToken.None);
        if (string.IsNullOrEmpty(path))
        {
            _logger.LogWarning($"Model {id} has no file path in database");
            return NotFound();
        }

        string fullPath = ResolvePath(path);

        if (!_fileManagementService.IsSafePath(fullPath, _modelsPath) || !_fileSystem.FileExists(fullPath))
        {
            _logger.LogWarning($"Model {id} file path is unsafe or does not exist: {fullPath}");
            return NotFound();
        }

        string fileExtension = Path.GetExtension(fullPath);

        // Check if this is a 3MF file that needs conversion to STL
        byte[]? fileData = null;
        string? fileName = null;
        if (forceStl && fileExtension.Equals(".3mf", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation($"Converting 3MF file {id} to STL for viewer. File path: {fullPath}");
            try
            {
                byte[] inputBytes = await _fileSystem.ReadAllBytesAsync(fullPath);
                _logger.LogInformation($"Successfully read 3MF file {id}, size: {inputBytes.Length} bytes");

                byte[]? stlBytes = await _threeMfConverter.ConvertToSTLAsync(inputBytes, CancellationToken.None);
                _logger.LogInformation($"Conversion result for {id}: {(stlBytes != null ? $"{stlBytes.Length} bytes" : "null")}");

                if (stlBytes != null)
                {
                    // File was successfully converted - set up to return STL
                    Model3DDto? dto = await _modelService.GetModelAsync(id, CancellationToken.None);
                    fileName = dto?.FileName ?? Path.GetFileName(fullPath);
                    fileName = Path.ChangeExtension(fileName, ".stl");
                    fileExtension = ".stl";
                    fileData = stlBytes;
                    _logger.LogInformation($"Converted 3MF file {id} to STL successfully. Output filename: {fileName}");
                }
                else
                {
                    _logger.LogError($"3MF conversion service returned null for file {id}. The 3MF file may be malformed or missing required data.");
                    return StatusCode(StatusCodes.Status500InternalServerError, "Failed to convert 3MF file to STL: conversion returned null");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Exception converting 3MF file {id}: {ex.GetType().Name}: {ex.Message}");
                return StatusCode(StatusCodes.Status500InternalServerError, $"Error converting 3MF file: {ex.GetType().Name}");
            }
        }

        // If not converted, fetch original filename
        if (fileName == null)
        {
            Model3DDto? modelDto = await _modelService.GetModelAsync(id, CancellationToken.None);
            fileName = modelDto?.FileName ?? Path.GetFileName(fullPath);
        }

        string contentType = fileExtension.ToLowerInvariant() switch
        {
            ".stl" => "application/vnd.ms-pki.stl",
            ".3mf" => "model/3mf",
            ".obj" => "text/plain",
            ".ply" => "application/octet-stream",
            _ => "application/octet-stream"
        };

        // Return converted data or original file from disk
        if (fileData != null)
        {
            return File(fileData, contentType, fileName);
        }

        return PhysicalFile(fullPath, contentType, fileName);
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
        try
        {
            _logger.LogInformation($"[Thumbnail] Retrieving thumbnail for model {id}");

            string? thumbPath = await _modelService.GetModelThumbnailPathAsync(id, CancellationToken.None);
            _logger.LogInformation($"[Thumbnail] Service returned path: {thumbPath ?? "NULL"}");

            if (string.IsNullOrEmpty(thumbPath))
            {
                _logger.LogWarning($"[Thumbnail] No thumbnail path in database for model {id}");
                return NotFound("Thumbnail not available");
            }

            // Use common path resolution helper
            string absolutePath = ResolvePath(thumbPath);
            _logger.LogInformation($"[Thumbnail] Resolved absolute path: {absolutePath}");

            bool fileExists = _fileSystem.FileExists(absolutePath);
            _logger.LogInformation($"[Thumbnail] File exists at '{absolutePath}': {fileExists}");

            if (!fileExists)
            {
                _logger.LogWarning($"[Thumbnail] Thumbnail file not found on disk at {absolutePath}");
                return NotFound("Thumbnail not available");
            }

            string contentType = "image/png";
            var fileInfo = _fileSystem.GetFileInfo(absolutePath);
            _logger.LogInformation($"[Thumbnail] File size: {fileInfo.Length} bytes");
            _logger.LogInformation($"[Thumbnail] Serving thumbnail from {absolutePath}");
            return PhysicalFile(absolutePath, contentType);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"[Thumbnail] Exception retrieving thumbnail for model {id}: {ex.Message}");
            return StatusCode(StatusCodes.Status500InternalServerError, "Error retrieving thumbnail");
        }
    }

    /// <summary>
    /// Download model file or thumbnail using path query parameter (generic download endpoint).
    /// Unified with Gcode download endpoint pattern for consistency.
    /// </summary>
    /// <param name="path">Relative file path within model storage directory</param>
    /// <returns>File content with appropriate media type</returns>
    /// <remarks>
    /// This endpoint serves both model files and thumbnails using a path-based query parameter approach,
    /// consistent with the Gcode files download endpoint. This allows efficient thumbnail serving without
    /// database lookups on each request.
    /// </remarks>
    [HttpGet("download")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DownloadAsync([FromQuery] string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return BadRequest("path is required");
        }

        try
        {
            // Get file bytes (validates path internally)
            (byte[] bytes, string fileName)? result = await _modelService.DownloadFileAsync(path, CancellationToken.None);
            if (result == null)
            {
                return NotFound();
            }

            var (fileBytes, safeFileName) = result.Value;
            string contentType = GetContentType(safeFileName);
            return File(fileBytes, contentType, safeFileName);
        }
        catch (Exception ex)
        {
            _logger.LogError($"Failed to download file {path}: {ex.Message}");
            return StatusCode(StatusCodes.Status500InternalServerError, "Failed to download file");
        }
    }

    /// <summary>
    /// Downloads a 3D model file, converting 3MF files to STL format for viewing
    /// </summary>
    /// <param name="path">Path to the model file</param>
    /// <param name="forceStl">If true, converts 3MF files to STL for viewer compatibility</param>
    /// <returns>The model file, optionally converted to STL format</returns>
    [HttpGet("download-for-viewer")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DownloadForViewerAsync([FromQuery] string path, [FromQuery] bool forceStl = true)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return BadRequest("path is required");
        }

        try
        {
            // Get file bytes (validates path internally)
            (byte[] bytes, string fileName)? result = await _modelService.DownloadFileAsync(path, CancellationToken.None);
            if (result == null)
            {
                return NotFound();
            }

            var (fileBytes, safeFileName) = result.Value;

            // Check if this is a 3MF file that needs conversion
            if (forceStl && safeFileName.EndsWith(".3mf", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation($"Converting 3MF file {safeFileName} to STL for viewer");

                var stlBytes = await _threeMfConverter.ConvertToSTLAsync(fileBytes, CancellationToken.None);
                if (stlBytes != null)
                {
                    // Return as STL file
                    var stlFileName = Path.ChangeExtension(safeFileName, ".stl");
                    return File(stlBytes, "application/octet-stream", stlFileName);
                }
                else
                {
                    _logger.LogWarning($"Failed to convert 3MF file {safeFileName} to STL");
                    return StatusCode(StatusCodes.Status500InternalServerError, "Failed to convert 3MF file to STL");
                }
            }

            // Return original file
            string contentType = GetContentType(safeFileName);
            return File(fileBytes, contentType, safeFileName);
        }
        catch (Exception ex)
        {
            _logger.LogError($"Failed to download file {path}: {ex.Message}");
            return StatusCode(StatusCodes.Status500InternalServerError, "Failed to download file");
        }
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
    /// Delete models by file paths (for hierarchical browser)
    /// </summary>
    /// <param name="request">Request with list of model paths to delete</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>No content if successful</returns>
    [HttpDelete]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> DeleteModelsAsync([FromBody] DeleteModelsRequest request, CancellationToken ct = default)
    {
        try
        {
            if (request?.ModelPaths == null || request.ModelPaths.Count == 0)
            {
                return BadRequest("At least one model path is required");
            }

            // Find models by file paths and delete them
            int deleted = 0;
            foreach (var path in request.ModelPaths)
            {
                try
                {
                    // Find model by path and delete
                    var model = await _unitOfWork.Model3dFiles.ListValidAsync(ct);
                    var matchingModel = model.FirstOrDefault(m => m.FilePath == path);
                    if (matchingModel != null)
                    {
                        await _modelService.DeleteModelAsync(matchingModel.Id, ct);
                        deleted++;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"Failed to delete model at path {path}: {ex.Message}");
                }
            }

            return NoContent();
        }
        catch (Exception ex)
        {
            _logger.LogError($"Failed to delete models: {ex.Message}");
            return StatusCode(StatusCodes.Status500InternalServerError, "Failed to delete models");
        }
    }

    /// <summary>
    /// Update model name (display name)
    /// </summary>
    /// <param name="id">Model ID</param>
    /// <param name="dto">Update request with new name</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>No content if successful</returns>
    [HttpPut("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateModelAsync(Guid id, [FromBody] UpdateModel3DDto dto, CancellationToken ct)
    {
        try
        {
            Model3D? model = await _unitOfWork.Model3dFiles.GetByIdAsync(id, ct);
            if (model == null)
            {
                return NotFound();
            }

            if (!string.IsNullOrWhiteSpace(dto.Name))
            {
                model.FileName = dto.Name.Trim();
            }

            await _unitOfWork.Model3dFiles.UpdateAsync(model, ct);
            await _unitOfWork.SaveChangesAsync(ct);
            return NoContent();
        }
        catch (Exception ex)
        {
            _logger.LogError($"Failed to update model {id}: {ex.Message}");
            return StatusCode(StatusCodes.Status500InternalServerError, "Failed to update model");
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
            Model3DValidationResultDto result = _modelService.ValidateModel(modelFile);
            return Ok(result);
        }
        catch (ArgumentException ae)
        {
            return BadRequest(ae.Message);
        }
    }

    /// <summary>
    /// Get all available tags
    /// </summary>
    /// <returns>List of all tags</returns>
    [HttpGet("tags")]
    [ProducesResponseType(typeof(Model3DTagDto[]), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetTagsAsync(CancellationToken ct = default)
    {
        try
        {
            IReadOnlyList<Model3DTagDto> tags = await _tagService.GetAllTagsAsync(ct);
            return Ok(tags);
        }
        catch (Exception ex)
        {
            _logger.LogError($"Failed to get tags: {ex.Message}");
            return StatusCode(StatusCodes.Status500InternalServerError, "Failed to get tags");
        }
    }

    /// <summary>
    /// Create a new tag
    /// </summary>
    /// <param name="dto">Tag creation parameters</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Created tag</returns>
    [HttpPost("tags")]
    [ProducesResponseType(typeof(Model3DTagDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreateTagAsync([FromBody] CreateModel3DTagDto dto, CancellationToken ct = default)
    {
        try
        {
            Model3DTagDto result = await _tagService.CreateTagAsync(dto, ct);
            // Return 201 Created with the location of the created resource
            return Created($"/api/3d-models/tags/{result.Id}", result);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CreateTagAsync] EXCEPTION: {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine($"[CreateTagAsync] InnerException: {ex.InnerException?.GetType().Name}: {ex.InnerException?.Message}");
            Console.WriteLine($"[CreateTagAsync] StackTrace: {ex.StackTrace}");
            _logger.LogError($"Failed to create tag: {ex.GetType().Name} - {ex.Message}\nInnerException: {ex.InnerException?.Message}\n{ex.StackTrace}");
            return StatusCode(StatusCodes.Status500InternalServerError, new
            {
                error = "An unexpected error occurred",
                message = ex.Message,
                innerMessage = ex.InnerException?.Message,
                details = ex.StackTrace
            });
        }
    }

    /// <summary>
    /// Delete a tag
    /// </summary>
    /// <param name="tagId">Tag ID</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>No content if successful</returns>
    [HttpDelete("tags/{tagId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteTagAsync(Guid tagId, CancellationToken ct = default)
    {
        try
        {
            await _tagService.DeleteTagAsync(tagId, ct);
            return NoContent();
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (Exception ex)
        {
            _logger.LogError($"Failed to delete tag {tagId}: {ex.Message}");
            return StatusCode(StatusCodes.Status500InternalServerError, "Failed to delete tag");
        }
    }

    /// <summary>
    /// Assign tags to a model
    /// </summary>
    /// <param name="modelId">Model ID</param>
    /// <param name="dto">Tag IDs to assign</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>No content if successful</returns>
    [HttpPost("{modelId:guid}/tags")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> AssignTagsAsync(Guid modelId, AssignTagsToModelDto dto, CancellationToken ct = default)
    {
        try
        {
            _logger.LogInformation($"AssignTagsAsync called: modelId={modelId}, tagCount={dto?.TagIds?.Length ?? 0}");
            await _tagService.AssignTagsToModelAsync(modelId, dto?.TagIds ?? [], ct);
            _logger.LogInformation($"Successfully assigned tags to model {modelId}");
            return NoContent();
        }
        catch (KeyNotFoundException ex)
        {
            _logger.LogError($"Model not found: {ex.Message}");
            return NotFound();
        }
        catch (Exception ex)
        {
            _logger.LogError($"Failed to assign tags to model {modelId}: {ex.GetType().Name} - {ex.Message}");
            _logger.LogError($"Stack trace: {ex.StackTrace}");
            return StatusCode(StatusCodes.Status500InternalServerError, "Failed to assign tags");
        }
    }

    /// <summary>
    /// Remove a tag from a model
    /// </summary>
    /// <param name="modelId">Model ID</param>
    /// <param name="tagId">Tag ID</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>No content if successful</returns>
    [HttpDelete("{modelId:guid}/tags/{tagId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RemoveTagAsync(Guid modelId, Guid tagId, CancellationToken ct = default)
    {
        try
        {
            await _tagService.RemoveTagFromModelAsync(modelId, tagId, ct);
            return NoContent();
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (Exception ex)
        {
            _logger.LogError($"Failed to remove tag {tagId} from model {modelId}: {ex.Message}");
            return StatusCode(StatusCodes.Status500InternalServerError, "Failed to remove tag");
        }
    }

    /// <summary>
    /// Bulk assign tags to multiple models
    /// </summary>
    /// <param name="bulkRequest">Model IDs and tag IDs</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Number of models updated</returns>
    [HttpPost("bulk/assign-tags")]
    [ProducesResponseType(typeof(BulkOperationResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> BulkAssignTagsAsync(BulkAssignTagsDto bulkRequest, CancellationToken ct = default)
    {
        try
        {
            if (bulkRequest.ModelIds == null || bulkRequest.ModelIds.Length == 0)
            {
                return BadRequest("No models specified");
            }

            if (bulkRequest.TagIds == null || bulkRequest.TagIds.Length == 0)
            {
                return BadRequest("No tags specified");
            }

            await _tagService.BulkAssignTagsAsync(bulkRequest.ModelIds, bulkRequest.TagIds, ct);

            return Ok(new BulkOperationResultDto { SuccessCount = bulkRequest.ModelIds.Length, TotalCount = bulkRequest.ModelIds.Length });
        }
        catch (Exception ex)
        {
            _logger.LogError($"Failed to bulk assign tags: {ex.Message}");
            return StatusCode(StatusCodes.Status500InternalServerError, "Failed to bulk assign tags");
        }
    }

    /// <summary>
    /// Search and filter models with pagination
    /// </summary>
    /// <param name="request">Search parameters</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Paginated search results</returns>
    [HttpPost("search")]
    [ProducesResponseType(typeof(Model3DSearchResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> SearchModelsAsync(Model3DSearchRequestDto request, CancellationToken ct)
    {
        try
        {
            if (request.Page < 1)
            {
                request.Page = 1;
            }

            if (request.PageSize < 1 || request.PageSize > 100)
            {
                request.PageSize = 20;
            }

            int skip = (request.Page - 1) * request.PageSize;
            int totalCount = await _unitOfWork.Model3dFiles.CountValidAsync(ct);

            IReadOnlyList<Model3D> models = await _unitOfWork.Model3dFiles.SearchAsync(
                request.Query,
                request.TagIds,
                request.SortBy ?? "uploadedAt",
                request.Descending,
                skip,
                request.PageSize,
                ct);

            List<Model3DDto> modelDtos = models.Select(m => new Model3DDto
            {
                Id = m.Id,
                FileName = m.FileName,
                Name = m.Name,
                FileSize = m.FileSizeBytes,
                FileType = _fileManagementService.GetModelFileFormatString(m.FileFormat),
                UploadedAt = m.UploadedAt,
                Url = GenerateViewerUrl(m),
                ThumbnailPath = _fileOperations.BuildThumbnailUrl(m, "/api/3d-models/download", _storagePathService.GetModelUploadDirectory()),
                Tags = m.TagMappings.Select(tm => new Model3DTagDto
                {
                    Id = tm.Tag!.Id,
                    Name = tm.Tag!.Name,
                    Color = tm.Tag!.Color,
                    Description = tm.Tag!.Description
                }).ToArray()
            }).ToList();

            Model3DSearchResultDto result = new Model3DSearchResultDto
            {
                Models = modelDtos.ToArray(),
                TotalCount = totalCount,
                Page = request.Page,
                PageSize = request.PageSize,
                TotalPages = (int)Math.Ceiling((double)totalCount / request.PageSize)
            };

            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError($"Failed to search models: {ex.Message}");
            return StatusCode(StatusCodes.Status500InternalServerError, "Failed to search models");
        }
    }

    /// <summary>
    /// Create a new folder in the models directory
    /// </summary>
    /// <param name="request">Create folder request with folder path</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Operation result</returns>
    [HttpPost("folder")]
    [ProducesResponseType(typeof(FolderOperationResultDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CreateFolderAsync([FromBody] CreateFolderRequest request, CancellationToken ct)
    {
        try
        {
            if (request == null || string.IsNullOrWhiteSpace(request.Path))
            {
                return BadRequest(new FolderOperationResultDto(false, "Folder path is required"));
            }

            // Normalize path - strip leading/trailing slashes for relative path
            string relativePath = request.Path.Trim('/').Trim();

            _logger.LogDebug($"[CreateFolder] Input path: '{request.Path}' -> Relative: '{relativePath}', Models path: '{_modelsPath}'");

            // Validate path for security - prevent directory traversal
            string[] pathParts = string.IsNullOrEmpty(relativePath) ? Array.Empty<string>() : relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (pathParts.Length == 0)
            {
                return BadRequest(new FolderOperationResultDto(false, "Folder path cannot be empty. Please provide a folder name."));
            }

            foreach (var part in pathParts)
            {
                if (string.IsNullOrWhiteSpace(part))
                {
                    return BadRequest(new FolderOperationResultDto(false, $"Folder path contains empty segments. Path: '{relativePath}'"));
                }
                if (part.Contains("..") || part.Contains("\\") || part == ".")
                {
                    return BadRequest(new FolderOperationResultDto(false, $"Invalid folder name: '{part}' in path '{relativePath}'. Cannot use '.', '..', or backslashes."));
                }
            }

            // Resolve full folder path - use relative path that doesn't start with /
            string fullPath = ResolvePath(relativePath);
            _logger.LogDebug($"[CreateFolder] Full path: '{fullPath}'");

            // Check if folder already exists
            if (_fileSystem.DirectoryExists(fullPath))
            {
                return Conflict(new FolderOperationResultDto(false, $"Folder already exists at: '{relativePath}'"));
            }

            // Verify parent directory exists
            string? parentPath = Path.GetDirectoryName(fullPath);
            _logger.LogDebug($"[CreateFolder] Parent path: '{parentPath}', Full path: '{fullPath}', Models base: '{_modelsPath}'");

            // Also check if models directory exists
            bool modelsPathExists = _fileSystem.DirectoryExists(_modelsPath);
            _logger.LogDebug($"[CreateFolder] Models path '{_modelsPath}' exists: {modelsPathExists}");

            if (string.IsNullOrEmpty(parentPath) || !_fileSystem.DirectoryExists(parentPath))
            {
                string diagnostic = $"Requested path: '{relativePath}', Resolved full path: '{fullPath}', Parent: '{parentPath}', Models base exists: {modelsPathExists}";
                _logger.LogWarning($"[CreateFolder] Parent directory does not exist. {diagnostic}");
                return BadRequest(new FolderOperationResultDto(false, $"Cannot create folder. Parent directory '{parentPath}' does not exist or models directory is not accessible. Details: {diagnostic}"));
            }

            // Create the folder
            _fileSystem.CreateDirectory(fullPath);

            // Track the folder in the database for proper hierarchy management
            try
            {
                string folderPath = "/" + relativePath.Trim('/');
                FolderNode folder = await _folderService.GetOrCreateFolderAsync(folderPath, "models", ct);
                _logger.LogInformation($"[CreateFolder] Recorded folder in database: {folder.Path}");
            }
            catch (Exception dbEx)
            {
                _logger.LogError($"[CreateFolder] Failed to record folder in database: {dbEx.Message}. Physical folder was created but not tracked.");
                // Don't fail the request - the physical folder exists, just not tracked yet
            }

            _logger.LogInformation($"[CreateFolder] Successfully created folder: {relativePath} at {fullPath}");
            return StatusCode(StatusCodes.Status201Created, new FolderOperationResultDto(true, "Folder created successfully"));
        }
        catch (ArgumentException ex)
        {
            _logger.LogError($"[CreateFolder] Invalid argument: {ex.Message}");
            return BadRequest(new FolderOperationResultDto(false, $"Invalid path: {ex.Message}"));
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogError($"[CreateFolder] Access denied: {ex.Message}");
            return StatusCode(StatusCodes.Status403Forbidden, new FolderOperationResultDto(false, "Access denied: insufficient permissions"));
        }
        catch (IOException ex)
        {
            _logger.LogError($"[CreateFolder] IO error: {ex.Message}");
            return StatusCode(StatusCodes.Status500InternalServerError, new FolderOperationResultDto(false, $"I/O error: {ex.Message}"));
        }
        catch (Exception ex)
        {
            _logger.LogError($"[CreateFolder] Unexpected error: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            return StatusCode(StatusCodes.Status500InternalServerError, new FolderOperationResultDto(false, $"Failed to create folder: {ex.GetType().Name}"));
        }
    }

    /// <summary>
    /// Move model files to a different virtual folder by model IDs
    /// </summary>
    /// <param name="request">Move request with model IDs and target folder</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Operation result</returns>
    [HttpPost("move")]
    [ProducesResponseType(typeof(FolderOperationResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> MoveFilesAsync([FromBody] MoveModelsRequest request, CancellationToken ct)
    {
        try
        {
            if (request == null || request.ModelIds == null || request.ModelIds.Count == 0)
            {
                return BadRequest(new FolderOperationResultDto(false, "At least one model ID is required"));
            }

            if (string.IsNullOrWhiteSpace(request.TargetDirectoryId))
            {
                return BadRequest(new FolderOperationResultDto(false, "Target directory ID is required"));
            }

            // Use the directory ID (virtual path) exactly as provided by the frontend
            // Frontend is responsible for constructing valid directory IDs
            string targetDirectoryPath = request.TargetDirectoryId;

            _logger.LogDebug($"[MoveModels] Moving {request.ModelIds.Count} model(s) to virtual directory: '{targetDirectoryPath}'");

            int movedCount = 0;
            int failedCount = 0;
            var failedFiles = new List<(string id, string reason)>();

            // Move each file - this is a virtual move (just update database)
            foreach (var modelIdStr in request.ModelIds)
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(modelIdStr))
                    {
                        continue;
                    }

                    // Parse the model ID (GUID)
                    if (!Guid.TryParse(modelIdStr, out Guid modelId))
                    {
                        _logger.LogWarning($"[MoveModels] Invalid model ID format: '{modelIdStr}'. Expected GUID from ModelId field, not Path.");
                        failedFiles.Add((modelIdStr, "Invalid model ID format - use ModelId, not Path"));
                        failedCount++;
                        continue;
                    }

                    // This will update the model's FolderId in the database
                    bool moved = await _modelService.MoveToFolderAsync(modelId, targetDirectoryPath, ct);

                    if (moved)
                    {
                        movedCount++;
                        _logger.LogDebug($"[MoveModels] Successfully moved model: {modelId}");
                    }
                    else
                    {
                        _logger.LogWarning($"[MoveModels] Model not found: {modelId}");
                        failedFiles.Add((modelIdStr, "Model not found"));
                        failedCount++;
                    }
                }
                catch (ArgumentException ex)
                {
                    _logger.LogWarning($"[MoveModels] Invalid argument for model {modelIdStr}: {ex.Message}");
                    failedFiles.Add((modelIdStr, $"Invalid argument: {ex.Message}"));
                    failedCount++;
                }
                catch (UnauthorizedAccessException ex)
                {
                    _logger.LogWarning($"[MoveModels] Access denied for model {modelIdStr}: {ex.Message}");
                    failedFiles.Add((modelIdStr, "Access denied"));
                    failedCount++;
                }
                catch (IOException ex)
                {
                    _logger.LogWarning($"[MoveModels] IO error for model {modelIdStr}: {ex.Message}");
                    failedFiles.Add((modelIdStr, $"IO error: {ex.Message}"));
                    failedCount++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"[MoveModels] Unexpected error for model {modelIdStr}: {ex.GetType().Name}: {ex.Message}");
                    failedFiles.Add((modelIdStr, $"{ex.GetType().Name}: {ex.Message}"));
                    failedCount++;
                }
            }

            string message = failedCount == 0
                ? $"Successfully moved {movedCount} model(s)"
                : $"Moved {movedCount} model(s), failed to move {failedCount} model(s)";

            if (failedFiles.Count > 0)
            {
                var failureDetails = failedFiles.Take(3).Select(f => $"{f.id} ({f.reason})").ToList();
                message += $" - Failed: {string.Join(", ", failureDetails)}";
                if (failedFiles.Count > 3)
                {
                    message += $" and {failedFiles.Count - 3} more";
                }
            }

            return Ok(new FolderOperationResultDto(failedCount == 0, message));
        }
        catch (ArgumentException ex)
        {
            _logger.LogError($"[MoveModels] Invalid argument: {ex.Message}");
            return BadRequest(new FolderOperationResultDto(false, $"Invalid request: {ex.Message}"));
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogError($"[MoveModels] Access denied: {ex.Message}");
            return StatusCode(StatusCodes.Status403Forbidden, new FolderOperationResultDto(false, "Access denied: insufficient permissions"));
        }
        catch (IOException ex)
        {
            _logger.LogError($"[MoveModels] IO error: {ex.Message}");
            return StatusCode(StatusCodes.Status500InternalServerError, new FolderOperationResultDto(false, $"I/O error: {ex.Message}"));
        }
        catch (Exception ex)
        {
            _logger.LogError($"[MoveModels] Unexpected error: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            return StatusCode(StatusCodes.Status500InternalServerError, new FolderOperationResultDto(false, $"Failed to move files: {ex.GetType().Name}"));
        }
    }

    /// <summary>
    /// Helper method to determine content type from file extension.
    /// </summary>
    private string GetContentType(string fileName)
    {
        string extension = Path.GetExtension(fileName).ToLowerInvariant();
        return extension switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".stl" => "model/stl",
            ".obj" => "model/obj",
            ".3mf" => "model/3mf",
            ".gcode" => "text/plain",
            ".bgcode" => "application/octet-stream",
            _ => "application/octet-stream"
        };
    }
}

/// <summary>
/// Request DTO for creating a new folder
/// </summary>
public record CreateFolderRequest(string Path);
