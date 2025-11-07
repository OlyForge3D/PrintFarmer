using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text; // Needed for Encoding when deriving secondary hash
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Services.FileManagement;
using Farm.Web.Api.Services.Interfaces;
using Farm.Web.Api.Services.Model;
using Farm.Web.Api.Services.Tags;
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
    private readonly ITagService _tagService;

    public ModelController(
        IUnifiedLoggingService logger,
        IModelService modelService,
        IConfiguration configuration,
        IModelAnalysisService analysisService,
        IVirusScanner virusScanner,
        IThumbnailGenerationService thumbnailService,
        Farm.Web.Api.Services.IO.IFileSystem fileSystem,
        IFileManagementService fileManagementService,
        ITagService tagService)
    {
        _logger = logger;
        _modelService = modelService ?? throw new ArgumentNullException(nameof(modelService));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _fileManagementService = fileManagementService ?? throw new ArgumentNullException(nameof(fileManagementService));
        _tagService = tagService ?? throw new ArgumentNullException(nameof(tagService));
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
    /// Get full model details including tags
    /// </summary>
    /// <param name="id">Model ID</param>
    /// <param name="db"></param>
    /// <returns>Detailed model information with tags</returns>
    [HttpGet("{id:guid}/details")]
    [ProducesResponseType(typeof(Model3DDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetModelDetailsAsync(Guid id, [FromServices] AppDbContext db)
    {
        try
        {
            var model = await db.Models3D
                .Where(m => m.Id == id)
                .Include(m => m.TagMappings)
                .ThenInclude(tm => tm.Tag)
                .FirstOrDefaultAsync();

            if (model == null)
            {
                return NotFound();
            }

            var dto = new Model3DDto
            {
                Id = model.Id,
                Name = model.DisplayName,
                FileName = model.OriginalFileName,
                FileSize = model.FileSizeBytes,
                FileType = _fileManagementService.GetModelFileFormatString(model.FileFormat),
                UploadedAt = model.UploadedAt,
                Url = $"/api/3d-models/{model.Id}/file",
                ThumbnailUrl = model.ThumbnailPath != null ? $"/api/3d-models/{model.Id}/thumbnail" : null,
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
    /// Update model name (display name)
    /// </summary>
    /// <param name="id">Model ID</param>
    /// <param name="dto">Update request with new name</param>
    /// <param name="db">Database context</param>
    /// <returns>No content if successful</returns>
    [HttpPut("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateModelAsync(Guid id, [FromBody] UpdateModel3DDto dto, [FromServices] AppDbContext db)
    {
        try
        {
            var model = await db.Models3D.FirstOrDefaultAsync(m => m.Id == id);
            if (model == null)
            {
                return NotFound();
            }

            if (!string.IsNullOrWhiteSpace(dto.Name))
            {
                model.DisplayName = dto.Name.Trim();
            }

            await db.SaveChangesAsync();
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
            var result = _modelService.ValidateModel(modelFile);
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
            var tags = await _tagService.GetAllTagsAsync(ct);
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
            var result = await _tagService.CreateTagAsync(dto, ct);
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
    /// <param name="db"></param>
    /// <returns>Paginated search results</returns>
    [HttpPost("search")]
    [ProducesResponseType(typeof(Model3DSearchResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> SearchModelsAsync(Model3DSearchRequestDto request, [FromServices] AppDbContext db)
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

            var query = db.Models3D.AsQueryable();

            // Text search
            if (!string.IsNullOrWhiteSpace(request.Query))
            {
                var searchTerm = request.Query.ToLower();
                query = query.Where(m => m.DisplayName.Contains(searchTerm, StringComparison.CurrentCultureIgnoreCase) ||
                                        (m.Description != null && m.Description.Contains(searchTerm, StringComparison.CurrentCultureIgnoreCase)));
            }

            // Tag filtering (AND logic - must have all tags)
            if (request.TagIds?.Length > 0)
            {
                foreach (var tagId in request.TagIds)
                {
                    query = query.Where(m => m.TagMappings.Any(tm => tm.TagId == tagId));
                }
            }

            // Sorting
            query = (request.SortBy?.ToLower()) switch
            {
                "name" => request.Descending ? query.OrderByDescending(m => m.DisplayName) : query.OrderBy(m => m.DisplayName),
                "size" => request.Descending ? query.OrderByDescending(m => m.FileSizeBytes) : query.OrderBy(m => m.FileSizeBytes),
                _ => request.Descending ? query.OrderByDescending(m => m.UploadedAt) : query.OrderBy(m => m.UploadedAt) // default: uploadedAt
            };

            var totalCount = await query.CountAsync();
            var models = await query
                .Skip((request.Page - 1) * request.PageSize)
                .Take(request.PageSize)
                .Include(m => m.TagMappings)
                .ThenInclude(tm => tm.Tag)
                .ToListAsync();

            var modelDtos = models.Select(m => new Model3DDto
            {
                Id = m.Id,
                Name = m.DisplayName,
                FileName = m.OriginalFileName,
                FileSize = m.FileSizeBytes,
                FileType = _fileManagementService.GetModelFileFormatString(m.FileFormat),
                UploadedAt = m.UploadedAt,
                Url = $"/api/3d-models/{m.Id}/file",
                ThumbnailUrl = m.ThumbnailPath != null ? $"/api/3d-models/{m.Id}/thumbnail" : null,
                Tags = m.TagMappings.Select(tm => new Model3DTagDto
                {
                    Id = tm.Tag!.Id,
                    Name = tm.Tag!.Name,
                    Color = tm.Tag!.Color,
                    Description = tm.Tag!.Description
                }).ToArray()
            }).ToList();

            var result = new Model3DSearchResultDto
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
}
