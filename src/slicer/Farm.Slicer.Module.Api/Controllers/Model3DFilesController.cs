using Farm.Infrastructure.Services;
using Farm.Slicer.Module.Dtos;
using Farm.Slicer.Module.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Farm.Slicer.Module.Api.Controllers;

/// <summary>
/// REST API controller for 3D model file management: upload, download, query, delete, and folder operations.
/// </summary>
[ApiController]
[Route("api/3d-models")]
[Tags("3D Models")]
[Authorize]
public class Model3DFilesController(
    ILogger<Model3DFilesController> logger,
    IModel3DFileService modelService,
    I3MfToStlConversionService threeMfConverter) : ControllerBase
{
    private readonly ILogger<Model3DFilesController> _logger = logger;
    private readonly IModel3DFileService _modelService = modelService;
    private readonly I3MfToStlConversionService _threeMfConverter = threeMfConverter;

    /// <summary>
    /// Uploads a 3D model file (STL, 3MF, OBJ, etc.) with validation and thumbnail generation.
    /// </summary>
    /// <param name="modelFile">The file to upload.</param>
    [HttpPost("upload")]
    [ProducesResponseType(typeof(Model3DUploadResultDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [RequestSizeLimit(500_000_000)] // 500 MB
    public async Task<IActionResult> UploadModelAsync(IFormFile modelFile)
    {
        if (modelFile is null || modelFile.Length == 0)
        {
            return BadRequest("No file uploaded or file is empty.");
        }

        try
        {
            Model3DUploadResultDto result = await _modelService.UploadModelAsync(modelFile, CancellationToken.None);
            return Created($"/api/models/{result.Id}", result);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning("Model upload validation failed: {Message}", ex.Message);
            return BadRequest(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to upload 3D model");
            return StatusCode(StatusCodes.Status500InternalServerError, "Failed to upload model");
        }
    }

    /// <summary>
    /// Lists all 3D models in a flat list.
    /// </summary>
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
            _logger.LogError(ex, "Failed to list 3D models");
            return StatusCode(StatusCodes.Status500InternalServerError, "Failed to list models");
        }
    }

    /// <summary>
    /// Lists all 3D model folders recursively.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("folders")]
    [ProducesResponseType(typeof(List<Model3DEntryDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListAllFoldersAsync(CancellationToken ct)
    {
        try
        {
            List<Model3DEntryDto> folders = await _modelService.ListAllFoldersAsync(ct);
            return Ok(folders);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list model folders");
            return StatusCode(StatusCodes.Status500InternalServerError, "Failed to list folders");
        }
    }

    /// <summary>
    /// Gets a single 3D model by its unique identifier.
    /// </summary>
    /// <param name="id">The model's unique identifier.</param>
    [HttpGet("{id:guid}", Name = "GetModel")]
    [ProducesResponseType(typeof(Model3DDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetModelAsync(Guid id)
    {
        Model3DDto? model = await _modelService.GetModelAsync(id, CancellationToken.None);
        return model is null ? NotFound() : Ok(model);
    }

    /// <summary>
    /// Gets detailed information for a specific model.
    /// </summary>
    /// <param name="id">The model's unique identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("{id:guid}/details")]
    [ProducesResponseType(typeof(Model3DDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetModelDetailsAsync(Guid id, CancellationToken ct)
    {
        Model3DDto? model = await _modelService.GetModelAsync(id, ct);
        return model is null ? NotFound() : Ok(model);
    }

    /// <summary>
    /// Downloads the actual model file by ID. Optionally converts 3MF to STL for viewer compatibility.
    /// </summary>
    /// <param name="id">The model's unique identifier.</param>
    /// <param name="forceStl">If true and the file is a 3MF, convert to STL for the response.</param>
    [HttpGet("file/{id:guid}")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
#pragma warning disable CA3003 // File path from DB lookup by Guid — no injection risk
    public async Task<IActionResult> GetModelFileAsync(Guid id, [FromQuery] bool forceStl = false)
    {
        try
        {
            string? filePath = await _modelService.GetModelFilePathAsync(id, CancellationToken.None);
            if (filePath is null || !System.IO.File.Exists(filePath))
            {
                return NotFound("Model file not found");
            }

            // If conversion requested and file is 3MF, convert to STL
            if (forceStl && filePath.EndsWith(".3mf", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    byte[] threeMfBytes = await System.IO.File.ReadAllBytesAsync(filePath);
                    byte[]? stlBytes = await _threeMfConverter.ConvertToSTLAsync(threeMfBytes, CancellationToken.None);
                    if (stlBytes is not null)
                    {
                        string stlName = Path.GetFileNameWithoutExtension(filePath) + ".stl";
                        return File(stlBytes, "model/stl", stlName);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "3MF to STL conversion failed, returning raw 3MF");
                }
            }

            string contentType = GetContentType(filePath);
            string fileName = Path.GetFileName(filePath);
            FileStream fileStream = new(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return File(fileStream, contentType, fileName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get model file for {Id}", id);
            return StatusCode(StatusCodes.Status500InternalServerError, "Failed to get model file");
        }
    }
#pragma warning restore CA3003

    /// <summary>
    /// Downloads the model's thumbnail image by ID.
    /// </summary>
    /// <param name="id">The model's unique identifier.</param>
    [HttpGet("thumbnail/{id:guid}")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
#pragma warning disable CA3003 // File path from DB lookup by Guid — no injection risk
    public async Task<IActionResult> GetModelThumbnailAsync(Guid id)
    {
        try
        {
            string? thumbnailPath = await _modelService.GetModelThumbnailPathAsync(id, CancellationToken.None);
            if (thumbnailPath is null || !System.IO.File.Exists(thumbnailPath))
            {
                return NotFound("Thumbnail not found");
            }

            FileStream fileStream = new(thumbnailPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return File(fileStream, "image/png", Path.GetFileName(thumbnailPath));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Thumbnail not found for model {Id}", id);
            return NotFound("Thumbnail not found");
        }
    }
#pragma warning restore CA3003

    /// <summary>
    /// Downloads a file by relative path for the 3D model viewer.
    /// Optionally converts 3MF to STL.
    /// </summary>
    /// <param name="path">Relative path to the file.</param>
    /// <param name="forceStl">Convert 3MF to STL if true.</param>
    [HttpGet("download-for-viewer")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DownloadForViewerAsync(
        [FromQuery] string path,
        [FromQuery] bool forceStl = false)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return BadRequest("path query parameter is required");
        }

        try
        {
            (byte[] Bytes, string FileName)? result = await _modelService.DownloadFileAsync(path, CancellationToken.None);
            if (result is null)
            {
                return NotFound("File not found");
            }

            // If conversion requested and file is 3MF, convert to STL
            if (forceStl && result.Value.FileName.EndsWith(".3mf", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    byte[]? stlBytes = await _threeMfConverter.ConvertToSTLAsync(result.Value.Bytes, CancellationToken.None);
                    if (stlBytes is not null)
                    {
                        string stlName = Path.GetFileNameWithoutExtension(result.Value.FileName) + ".stl";
                        return File(stlBytes, "model/stl", stlName);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "3MF to STL conversion failed for viewer download");
                }
            }

            string contentType = GetContentType(result.Value.FileName);
            return File(result.Value.Bytes, contentType, result.Value.FileName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to download file for viewer: {Path}", path);
            return StatusCode(StatusCodes.Status500InternalServerError, "Failed to download file");
        }
    }

    /// <summary>
    /// Deletes a single 3D model and its associated files.
    /// </summary>
    /// <param name="id">The model's unique identifier.</param>
    [HttpDelete("{id:guid}")]
    [Authorize(Roles = "farm_admin")]
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
            _logger.LogError(ex, "Failed to delete 3D model {Id}", id);
            return StatusCode(StatusCodes.Status500InternalServerError, "Failed to delete model");
        }
    }

    /// <summary>
    /// Bulk deletes multiple 3D models by their IDs.
    /// </summary>
    /// <param name="request">Request containing model IDs to delete.</param>
    [HttpDelete]
    [Authorize(Roles = "farm_admin")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult> DeleteModelsAsync([FromBody] DeleteModelsRequest request)
    {
        if (request?.Ids is null || request.Ids.Count == 0)
        {
            return BadRequest("At least one model ID is required");
        }

        int deleted = 0;
        int errors = 0;

        foreach (Guid id in request.Ids)
        {
            try
            {
                await _modelService.DeleteModelAsync(id, CancellationToken.None);
                deleted++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete model {Id} during bulk delete", id);
                errors++;
            }
        }

        return Ok(new { deleted, errors, total = request.Ids.Count });
    }

    /// <summary>
    /// Validates a 3D model file without storing it.
    /// </summary>
    /// <param name="modelFile">The model file to validate.</param>
    [HttpPost("validate")]
    [ProducesResponseType(typeof(Model3DValidationResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public IActionResult ValidateModel(IFormFile modelFile)
    {
        if (modelFile is null || modelFile.Length == 0)
        {
            return BadRequest("No file uploaded or file is empty.");
        }

        Model3DValidationResultDto result = _modelService.ValidateModel(modelFile);
        return Ok(result);
    }

    /// <summary>
    /// Queries 3D models with filtering, sorting, and pagination.
    /// </summary>
    /// <param name="request">Search and filter parameters.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPost("query")]
    [ProducesResponseType(typeof(Model3DListResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> QueryModelsAsync(
        [FromBody] Model3DSearchRequestDto request,
        CancellationToken ct)
    {
        try
        {
            Model3DListResponse result = await _modelService.QueryAsync(
                request.Path,
                request.SortBy,
                request.SortOrder,
                request.Search,
                request.Page,
                request.PageSize,
                request.TagIds,
                ct);

            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to query 3D models");
            return StatusCode(StatusCodes.Status500InternalServerError, "Failed to query models");
        }
    }

    /// <summary>
    /// Creates a virtual folder for organizing 3D model files.
    /// </summary>
    /// <param name="request">Folder creation request.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPost("folder")]
    [ProducesResponseType(typeof(FolderOperationResultDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreateFolderAsync(
        [FromBody] CreateFolderRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Path))
        {
            return BadRequest("path is required");
        }

        try
        {
            Guid folderId = await _modelService.GetOrCreateFolderAsync(request.Path, request.FolderType, ct);
            return Created(string.Empty, new FolderOperationResultDto
            {
                Success = true,
                FolderId = folderId,
                Message = $"Folder '{request.Path}' created or found"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create folder at {Path}", request.Path);
            return StatusCode(StatusCodes.Status500InternalServerError, "Failed to create folder");
        }
    }

    /// <summary>
    /// Moves 3D models to a different folder.
    /// </summary>
    /// <param name="request">Move request with model IDs and target folder.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPost("move")]
    [ProducesResponseType(typeof(FolderOperationResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> MoveFilesAsync(
        [FromBody] MoveModelsRequest request,
        CancellationToken ct)
    {
        if (request?.Ids is null || request.Ids.Count == 0)
        {
            return BadRequest("At least one model ID is required");
        }

        if (string.IsNullOrWhiteSpace(request.TargetFolderPath))
        {
            return BadRequest("targetFolderPath is required");
        }

        int moved = 0;
        int errors = 0;

        foreach (Guid id in request.Ids)
        {
            try
            {
                bool success = await _modelService.MoveToFolderAsync(id, request.TargetFolderPath, ct);
                if (success)
                {
                    moved++;
                }
                else
                {
                    errors++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to move model {Id}", id);
                errors++;
            }
        }

        return Ok(new FolderOperationResultDto
        {
            Success = errors == 0,
            AffectedCount = moved,
            Message = $"Moved {moved} of {request.Ids.Count} models (errors: {errors})"
        });
    }

    /// <summary>
    /// Determines the MIME content type for a model file extension.
    /// </summary>
    private static string GetContentType(string filePath)
    {
        string ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext switch
        {
            ".stl" => "model/stl",
            ".3mf" => "model/3mf",
            ".obj" => "model/obj",
            ".step" or ".stp" => "model/step",
            ".gcode" => "text/x-gcode",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            _ => "application/octet-stream"
        };
    }
}
