using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.Normalization;
using Farm.Infrastructure.Repositories.Tags;
using Farm.Infrastructure.Services.FileManagement;
using Farm.Infrastructure.Services.FolderManagement;
using Farm.Infrastructure.Services.Models;
using Farm.Infrastructure.Services.StorageManagement;
using Farm.Infrastructure.Services.Thumbnails;
using Farm.Slicer.Module.Data.Repositories;
using Farm.Slicer.Module.Domain;
using Farm.Slicer.Module.Dtos;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;

namespace Farm.Slicer.Module.Services;

/// <summary>
/// Service for managing 3D model files with support for virtual folder organization and metadata extraction.
/// </summary>
/// <remarks>
/// This service provides comprehensive 3D model file management capabilities including:
/// - Upload and storage of STL, OBJ, and 3MF model files
/// - Virtual folder hierarchy for organizing models (folders exist only in database)
/// - Automatic thumbnail generation from model files
/// - Model analysis for dimensions and file format validation
/// - Pagination and search capabilities for model listings
/// - Tag-based organization through Model3DTag relationships
/// Physical files are stored with GUID-based names in a flat directory structure.
/// </remarks>
public class Model3DFileService : Farm.Slicer.Module.Services.IModel3DFileService
{
    private const int StreamBufferSize = 81_920;
    private const long MaxClientThumbnailBytes = 10 * 1024 * 1024;
    private const int MaxClientThumbnailDimension = 4_096;
    private const long MaxClientThumbnailPixels = 16_000_000;
    private const int MaxSymbolicLinkDepth = 64;

    private static readonly byte[] PngSignature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    private readonly IModel3DFileRepository _model3dFiles;
    private readonly ITagRepository _tagRepository;
    private readonly ILogger<Model3DFileService> _logger;
    private readonly string _modelsPath;
    private readonly IModelAnalysisService? _analysisService;
    private readonly Farm.Infrastructure.IO.IFileSystem _fileSystem;
    private readonly IFileManagementService _fileManagementService;
    private readonly IStoredFileOperationsService _fileOperations;
    private readonly IThumbnailGenerationService? _thumbnailService;
    private readonly IFolderManagementService _folderManagementService;
    private readonly IStoragePathService _storagePathService;
    private readonly IThreeMfMetadataService? _threeMfMetadataService;

    public Model3DFileService(
        IModel3DFileRepository model3dFiles,
        ITagRepository tagRepository,
        ILogger<Model3DFileService> logger,
        IConfiguration configuration,
        Farm.Infrastructure.IO.IFileSystem fileSystem,
        IFileManagementService fileManagementService,
        IFolderManagementService folderManagementService,
        IStoragePathService storagePathService,
        IStoredFileOperationsService fileOperations,
        IModelAnalysisService? analysisService = null,
        IThumbnailGenerationService? thumbnailService = null,
        IThreeMfMetadataService? threeMfMetadataService = null)
    {
        _model3dFiles = model3dFiles ?? throw new ArgumentNullException(nameof(model3dFiles));
        _tagRepository = tagRepository ?? throw new ArgumentNullException(nameof(tagRepository));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _analysisService = analysisService;
        _thumbnailService = thumbnailService;
        _threeMfMetadataService = threeMfMetadataService;
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _fileManagementService = fileManagementService ?? throw new ArgumentNullException(nameof(fileManagementService));
        _folderManagementService = folderManagementService ?? throw new ArgumentNullException(nameof(folderManagementService));
        _fileOperations = fileOperations ?? throw new ArgumentNullException(nameof(fileOperations));
        _storagePathService = storagePathService ?? throw new ArgumentNullException(nameof(storagePathService));
        ArgumentNullException.ThrowIfNull(configuration);

        // Use storage path service for consistent path handling (like GcodeFilesService)
        _modelsPath = storagePathService.GetModelUploadDirectory();
        if (!_fileSystem.DirectoryExists(_modelsPath))
        {
            _fileSystem.CreateDirectory(_modelsPath);
        }
    }

    /// <summary>
    /// Lists all valid 3D model files in the system.
    /// </summary>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <returns>Read-only list of model DTOs with basic file information</returns>
    /// <remarks>
    /// Returns only valid models (non-deleted) with file size, format, upload date, and thumbnail URLs.
    /// </remarks>
    public async Task<IReadOnlyList<Model3DDto>> ListModelsAsync(CancellationToken ct)
    {
        IReadOnlyList<Model3D> models = await _model3dFiles.ListValidAsync(ct);

        List<Model3DDto> result = new List<Model3DDto>();
        foreach (Model3D m in models)
        {
            result.Add(await MapToDtoAsync(m));
        }

        return result;
    }

    /// <summary>
    /// Lists all 3D model folders recursively for building a folder tree structure.
    /// </summary>
    /// <param name="ct">Cancellation token for async operation.</param>
    /// <returns>List of folder entries for building a folder tree.</returns>
    public async Task<List<Model3DEntryDto>> ListAllFoldersAsync(CancellationToken ct)
    {
        // Get all folders from the folder management service
        List<string> allFolderPaths = await _folderManagementService.GetAllFolderPathsRecursiveAsync("models", "/", ct);

        List<Model3DEntryDto> folderEntries = [];

        // Always include root folder
        folderEntries.Add(new Model3DEntryDto(
            Path: "/",
            FileName: "/",
            FileSize: 0,
            UploadedAt: DateTime.UtcNow,
            IsDirectory: true,
            DirectoryId: "/"));

        // Add all subfolders
        foreach (string? folderPath in allFolderPaths.OrderBy(p => p))
        {
            string folderName = folderPath.Split('/').LastOrDefault(s => !string.IsNullOrEmpty(s)) ?? folderPath;
            folderEntries.Add(new Model3DEntryDto(
                Path: folderPath,
                FileName: folderName,
                FileSize: 0,
                UploadedAt: DateTime.UtcNow,
                IsDirectory: true,
                DirectoryId: folderPath));
        }

        return folderEntries;
    }

    /// <summary>
    /// Queries models with comprehensive filtering, sorting, and pagination.
    /// All operations are performed at the database level for maximum efficiency.
    /// </summary>
    /// <param name="path">Virtual folder path to filter by.</param>
    /// <param name="sortBy">Field to sort by (e.g., "name", "date", "size").</param>
    /// <param name="sortOrder">Sort direction ("asc" or "desc").</param>
    /// <param name="search">Search term for filtering by name.</param>
    /// <param name="page">Page number for pagination (1-based).</param>
    /// <param name="pageSize">Number of items per page.</param>
    /// <param name="tagIds">Optional array of tag IDs to filter by.</param>
    /// <param name="ct">Cancellation token for async operation.</param>
    /// <returns>Paginated response containing model entries and metadata.</returns>
    public async Task<Model3DListResponse> QueryAsync(
        string? path,
        string? sortBy,
        string? sortOrder,
        string? search,
        int page,
        int pageSize,
        Guid[]? tagIds,
        CancellationToken ct)
    {
        // Validate and clamp pagination parameters
        if (page < 1)
        {
            page = 1;
        }

        if (pageSize < 1)
        {
            pageSize = 1;
        }

        if (pageSize > 500)
        {
            pageSize = 500;
        }

        // Resolve path to folder IDs for the module repository
        Guid[]? folderIds = null;
        if (!string.IsNullOrWhiteSpace(path))
        {
            try
            {
                FolderNode folder = await _folderManagementService.GetOrCreateFolderAsync(path, "models", ct);
                folderIds = [folder.Id];
            }
            catch
            {
                // Path doesn't exist — return empty results
                return new Model3DListResponse(Models: [], TotalCount: 0, TotalSize: 0, Page: page, PageSize: pageSize, TotalPages: 0, TotalItems: 0);
            }
        }

        // Call the efficient repository method that does everything at the database level
        (List<Model3D> models, int totalCount) = await _model3dFiles.QueryModelsAsync(
            folderIds,
            search,
            sortBy,
            sortOrder,
            page,
            pageSize,
            ct);

        // Build model entries using existing MapToEntryDto
        List<Model3DEntryDto> entries = [];
        long totalSize = 0;

        foreach (Model3D model in models)
        {
            // Use existing MapToEntryDto helper for consistency
            entries.Add(MapToEntryDto(model, model.FileName));
            totalSize += model.FileSizeBytes;
        }

        // Calculate total pages
        int totalPages = totalCount > 0 ? (int)Math.Ceiling((double)totalCount / pageSize) : 0;

        return new Model3DListResponse(
            Models: entries,
            TotalCount: totalCount,
            TotalSize: totalSize,
            Page: page,
            PageSize: pageSize,
            TotalPages: totalPages,
            TotalItems: totalCount);
    }

    #region Helper Methods

    /// <summary>
    /// Retrieves a specific 3D model by its unique identifier.
    /// </summary>
    /// <param name="id">Unique model identifier (GUID)</param>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <returns>Model DTO with file details, or null if not found</returns>
    public async Task<Model3DDto?> GetModelAsync(Guid id, CancellationToken ct)
    {
        Model3D? model = await _model3dFiles.GetByIdAsync(id, ct);
        return model == null ? null : await MapToDtoAsync(model);
    }

    /// <summary>
    /// Gets the physical file path for a 3D model file.
    /// </summary>
    /// <param name="id">Unique model identifier (GUID)</param>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <returns>Full filesystem path to the model file, or null if not found</returns>
    public async Task<string?> GetModelFilePathAsync(Guid id, CancellationToken ct)
    {
        // Use unfiltered query for file operations - files should be accessible regardless of validation status
        Model3D? model = await _model3dFiles.GetByIdUnfilteredAsync(id, ct);
        if (model == null)
        {
            return null;
        }

        // Return absolute path using the configured storage directory
        return Path.Join(_modelsPath, model.FileName);
    }

    /// <summary>
    /// Gets the physical file path for a model's thumbnail image.
    /// </summary>
    /// <param name="id">Unique model identifier (GUID)</param>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <returns>Full filesystem path to thumbnail, or null if thumbnail not available</returns>
    public async Task<string?> GetModelThumbnailPathAsync(Guid id, CancellationToken ct)
    {
        // Use unfiltered query for file operations - thumbnails should be accessible regardless of validation status
        Model3D? model = await _model3dFiles.GetByIdUnfilteredAsync(id, ct);
        return model == null ? null : (string.IsNullOrEmpty(model.ThumbnailFileName) ? null : Path.Join(_modelsPath, model.ThumbnailFileName));
    }

    /// <summary>
    /// Deletes a 3D model and its associated files (model file and thumbnail).
    /// </summary>
    /// <param name="id">Unique model identifier (GUID)</param>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <exception cref="KeyNotFoundException">Thrown when model with specified ID does not exist</exception>
    /// <remarks>
    /// Deletes both database record and physical files.
    /// Removes model file, thumbnail (if exists), and all tag associations.
    /// Uses Entity Framework change tracking to cascade delete relationships.
    /// </remarks>
    public async Task DeleteModelAsync(Guid id, CancellationToken ct)
    {
        Model3D? model = await _model3dFiles.GetByIdAsync(id, ct);
        if (model == null)
        {
            throw new KeyNotFoundException("Model not found");
        }

        try
        {
            // Construct full physical path: all models are stored in _modelsPath regardless of virtual path
            string fullModelPath = Path.Join(_modelsPath, model.FileName);
            if (_fileManagementService.IsSafePath(fullModelPath, _modelsPath) && System.IO.File.Exists(fullModelPath))
            {
                System.IO.File.Delete(fullModelPath);
            }

            // Thumbnail is stored in same directory
            string? thumbnailFileName = model.ThumbnailFileName;
            if (thumbnailFileName != null)
            {
                string fullThumbnailPath = Path.Join(_modelsPath, thumbnailFileName);
                if (_fileManagementService.IsSafePath(fullThumbnailPath, _modelsPath) && System.IO.File.Exists(fullThumbnailPath))
                {
                    System.IO.File.Delete(fullThumbnailPath);
                }
            }

            await _model3dFiles.RemoveAsync(model, ct);
            await _model3dFiles.SaveChangesAsync(ct);
            _logger.LogInformation("Model deleted: {Id}", id);
        }
        catch (Exception ex)
        {
            _logger.LogError("Failed to delete model: {Id}: {Message}", id, ex.Message);
            throw;
        }
    }

    /// <summary>
    /// Validates a 3D model file upload before processing.
    /// </summary>
    /// <param name="modelFile">HTTP form file containing the model.</param>
    /// <returns>Validation result indicating success/failure and error messages.</returns>
    /// <remarks>
    /// Validates:
    /// - File is not null or empty
    /// - File extension is supported (.stl, .obj, .3mf, .step, .stp)
    /// - File size is within limits (max 100 MB)
    /// </remarks>
    public Model3DValidationResultDto ValidateModel(IFormFile modelFile)
    {
        if (modelFile == null || modelFile.Length == 0)
        {
            throw new ArgumentException("Model file is required");
        }

        List<string> issues = new();
        string fileExtension = Path.GetExtension(modelFile.FileName);

        // Validate extension
        try
        {
            _fileManagementService.ValidateModelExtension(fileExtension);
        }
        catch (ArgumentException ex)
        {
            issues.Add(ex.Message);
        }

        if (modelFile.Length > 100_000_000)
        {
            issues.Add("File size exceeds 100MB limit");
        }

        return new Model3DValidationResultDto
        {
            Valid = issues.Count == 0,
            Issues = issues.Count > 0 ? issues.ToArray() : null
        };
    }

    /// <inheritdoc />
    public Task<Model3DUploadResultDto> UploadModelAsync(IFormFile modelFile, CancellationToken ct)
        => UploadModelCoreAsync(modelFile, thumbnailFile: null, userId: null, clientUploadId: null, ct);

    /// <inheritdoc />
    public Task<Model3DUploadResultDto> UploadModelAsync(
        IFormFile modelFile,
        IFormFile? thumbnailFile,
        CancellationToken ct)
        => UploadModelCoreAsync(modelFile, thumbnailFile, userId: null, clientUploadId: null, ct);

    /// <inheritdoc />
    public Task<Model3DUploadResultDto> UploadModelAsync(
        IFormFile modelFile,
        IFormFile? thumbnailFile,
        Guid userId,
        Guid? clientUploadId,
        CancellationToken ct)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("Upload owner is required", nameof(userId));
        }

        if (clientUploadId == Guid.Empty)
        {
            throw new ArgumentException("clientUploadId must be a non-empty GUID", nameof(clientUploadId));
        }

        return UploadModelCoreAsync(modelFile, thumbnailFile, userId, clientUploadId, ct);
    }

    /// <inheritdoc />
    public async Task<Model3DThumbnailUpdateResultDto> ReplaceThumbnailAsync(
        Guid modelId,
        IFormFile thumbnailFile,
        Guid? userId,
        bool isAdmin,
        string? ifMatch,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(thumbnailFile);

        Model3D model = await _model3dFiles.GetByIdAsync(modelId, ct)
            ?? throw new KeyNotFoundException("Model not found");

        if (!isAdmin && (!userId.HasValue || model.UploadedByUserId != userId))
        {
            throw new UnauthorizedAccessException("Only the model owner or an administrator can replace its thumbnail");
        }

        string currentETag = CreateETag(model);
        if (!MatchesIfMatch(ifMatch, currentETag))
        {
            throw new DbUpdateConcurrencyException("The model was modified after the supplied ETag was issued");
        }

        string thumbnailBaseName = _fileOperations.GenerateThumbnailFileName(modelId, ".png");
        string thumbnailFileName = $"{Path.GetFileNameWithoutExtension(thumbnailBaseName)}_{Guid.NewGuid():N}.png";
        string thumbnailFinalPath = Path.Join(_modelsPath, thumbnailFileName);
        string thumbnailTempPath = $"{thumbnailFinalPath}.tmp";
        string? previousThumbnailFileName = model.ThumbnailFileName;
        string? previousThumbnailPath = previousThumbnailFileName is null
            ? null
            : Path.Join(_modelsPath, previousThumbnailFileName);
        DateTime previousUpdatedAt = model.UpdatedAt;

        if (!_fileManagementService.IsSafePath(thumbnailFinalPath, _modelsPath)
            || !_fileManagementService.IsSafePath(thumbnailTempPath, _modelsPath)
            || (previousThumbnailPath is not null
                && !_fileManagementService.IsSafePath(previousThumbnailPath, _modelsPath)))
        {
            throw new InvalidOperationException("Unsafe thumbnail storage path generated");
        }

        try
        {
            await StageAndValidateClientThumbnailAsync(thumbnailFile, thumbnailTempPath, ct);
            MoveStagedFile(thumbnailTempPath, thumbnailFinalPath, "thumbnail");
            model.ThumbnailFileName = thumbnailFileName;
            DateTime now = DateTime.UtcNow;
            model.UpdatedAt = now > previousUpdatedAt ? now : previousUpdatedAt.AddMilliseconds(1);
            await _model3dFiles.UpdateAsync(model, ct);
            await _model3dFiles.SaveChangesAsync(ct);

            if (previousThumbnailPath is not null
                && !string.Equals(previousThumbnailPath, thumbnailFinalPath, StringComparison.OrdinalIgnoreCase))
            {
                DeleteUploadArtifact(previousThumbnailPath);
            }

            return new Model3DThumbnailUpdateResultDto
            {
                Id = model.Id,
                ThumbnailUrl = _fileOperations.BuildModel3DThumbnailUrl(model.Id),
                ETag = CreateETag(model)
            };
        }
        catch
        {
            model.ThumbnailFileName = previousThumbnailFileName;
            model.UpdatedAt = previousUpdatedAt;
            DeleteUploadArtifact(thumbnailFinalPath);

            throw;
        }
        finally
        {
            DeleteUploadArtifact(thumbnailTempPath);
        }
    }

    private async Task<Model3DUploadResultDto> UploadModelCoreAsync(
        IFormFile modelFile,
        IFormFile? thumbnailFile,
        Guid? userId,
        Guid? clientUploadId,
        CancellationToken ct)
    {
        if (modelFile == null || modelFile.Length == 0)
        {
            throw new ArgumentException("Model file is required", nameof(modelFile));
        }

        if (clientUploadId.HasValue && !userId.HasValue)
        {
            throw new ArgumentException("An upload owner is required when clientUploadId is provided", nameof(userId));
        }

        string originalName = modelFile.FileName ?? string.Empty;
        string fileExtension = Path.GetExtension(originalName);

        // Validate extension using service
        _fileManagementService.ValidateModelExtension(fileExtension);

        Guid modelId = Guid.NewGuid();
        string fileName = $"{modelId}{fileExtension}";
        string finalFilePath = Path.Join(_modelsPath, fileName);
        if (!_fileManagementService.IsSafePath(finalFilePath, _modelsPath))
        {
            throw new InvalidOperationException("Unsafe file path generated");
        }

        _logger.LogInformation("Starting model upload: {FileName} ({FileSize} bytes), ID: {ModelId}", originalName, modelFile.Length, modelId);

        // Use temp file pattern for safety: write to temp, then move to final location
        string tempFileName = $"{modelId}.tmp{fileExtension}";
        string tempFilePath = Path.Join(_modelsPath, tempFileName);
        if (!_fileManagementService.IsSafePath(tempFilePath, _modelsPath))
        {
            throw new InvalidOperationException("Unsafe temporary file path generated");
        }

        string? thumbnailFileName = null;
        string? thumbnailTempPath = null;
        string? thumbnailFinalPath = null;

        try
        {
            string fileHash = await StreamModelToTempAndHashAsync(modelFile, tempFilePath, ct);
            string clientUploadHash = fileHash;

            if (userId.HasValue && clientUploadId.HasValue)
            {
                Model3D? existingUpload = await _model3dFiles.GetByClientUploadIdAsync(
                    userId.Value,
                    clientUploadId.Value,
                    ct);
                if (existingUpload is not null)
                {
                    DeleteUploadArtifact(tempFilePath);
                    return CreateIdempotentRetryResult(existingUpload, clientUploadHash);
                }
            }

            if (thumbnailFile is not null)
            {
                thumbnailFileName = _fileOperations.GenerateThumbnailFileName(modelId, ".png");
                thumbnailFinalPath = Path.Join(_modelsPath, thumbnailFileName);
                thumbnailTempPath = Path.Join(_modelsPath, $"{modelId}_thumb.{Guid.NewGuid():N}.tmp");
                if (!_fileManagementService.IsSafePath(thumbnailFinalPath, _modelsPath)
                    || !_fileManagementService.IsSafePath(thumbnailTempPath, _modelsPath))
                {
                    throw new InvalidOperationException("Unsafe thumbnail storage path generated");
                }

                await StageAndValidateClientThumbnailAsync(thumbnailFile, thumbnailTempPath, ct);
            }

            Farm.Infrastructure.Services.Models.ModelAnalysisResult? analysis = null;
            try
            {
                if (_analysisService != null)
                {
                    _logger.LogDebug("Analyzing model metadata for {ModelId}", modelId);
                    analysis = await _analysisService.AnalyzeModelAsync(tempFilePath, fileExtension, ct);
                    _logger.LogDebug("Model analysis complete for {ModelId}: {DimensionX}x{DimensionY}x{DimensionZ}mm", modelId, analysis?.DimensionX, analysis?.DimensionY, analysis?.DimensionZ);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception analysisEx)
            {
                _logger.LogWarning("Model analysis failed for {ModelId}: {Message}", modelId, analysisEx.Message);
            }

            // Step 2b: Extract 3MF metadata (best-effort)
            ThreeMfMetadataDto? threeMfMetadata = null;
            try
            {
                if (_threeMfMetadataService != null &&
                    fileExtension.Equals(".3mf", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogDebug("Extracting 3MF metadata for {ModelId}", modelId);
                    threeMfMetadata = await _threeMfMetadataService.ExtractMetadataAsync(tempFilePath, ct);
                    if (threeMfMetadata != null)
                    {
                        _logger.LogDebug(
                            "3MF metadata extracted for {ModelId}: Title={Title}, Designer={Designer}, AutoTags={TagCount}",
                            modelId, threeMfMetadata.Title, threeMfMetadata.Designer, threeMfMetadata.AutoTags.Count);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception metadataEx)
            {
                _logger.LogWarning("3MF metadata extraction failed for {ModelId}: {Message}", modelId, metadataEx.Message);
            }

            // Step 3: Check for duplicates
            Model3D? existingModel = await _model3dFiles.GetByHashAsync(fileHash, ct);
            string baseName = Path.GetFileNameWithoutExtension(originalName);
            if (existingModel != null && !clientUploadId.HasValue)
            {
                string existingBaseName = Path.GetFileNameWithoutExtension(existingModel.FileName);
                string existingExt = Path.GetExtension(existingModel.FileName);
                bool isSameExtension = string.Equals(existingExt, fileExtension, StringComparison.OrdinalIgnoreCase);
                bool bothDuplicatePrefix = existingBaseName.StartsWith("duplicate", StringComparison.OrdinalIgnoreCase)
                    && baseName.StartsWith("duplicate", StringComparison.OrdinalIgnoreCase);
                bool baseNamesMatch = string.Equals(existingBaseName, baseName, StringComparison.OrdinalIgnoreCase);
                bool treatAsDuplicate = isSameExtension && (baseNamesMatch || bothDuplicatePrefix);

                if (treatAsDuplicate)
                {
                    DeleteUploadArtifact(tempFilePath);
                    DeleteUploadArtifact(thumbnailTempPath);

                    return CreateUploadResult(existingModel, wasExisting: true);
                }

                byte[] composite = System.Text.Encoding.UTF8.GetBytes(fileHash + "|" + originalName);
                byte[] newHashBytes = SHA256.HashData(composite);
                fileHash = _fileManagementService.ToHex(newHashBytes);
            }
            else if (existingModel is not null)
            {
                byte[] composite = System.Text.Encoding.UTF8.GetBytes(
                    $"{fileHash}|{userId:D}|{clientUploadId:D}");
                byte[] newHashBytes = SHA256.HashData(composite);
                fileHash = _fileManagementService.ToHex(newHashBytes);
            }

            // Step 4: Move temp file to final location and verify before creating database record
            // This ensures the database never references a file that doesn't exist on disk
            try
            {
                if (_fileSystem.FileExists(tempFilePath))
                {
                    // Delete any existing file at final location first (shouldn't happen but be safe)
                    if (_fileSystem.FileExists(finalFilePath))
                    {
                        _fileSystem.DeleteFile(finalFilePath);
                    }

                    // Move temp to final location
                    _fileSystem.MoveFile(tempFilePath, finalFilePath, overwrite: true);

                    // Verify file exists at final location before proceeding
                    if (!_fileSystem.FileExists(finalFilePath))
                    {
                        throw new InvalidOperationException("File move succeeded but verification failed - file not found at final location");
                    }

                    _logger.LogDebug("Model file moved from temp to final location: {ModelId}", modelId);
                }
                else
                {
                    throw new InvalidOperationException("Temp file not found after write operation");
                }
            }
            catch (Exception moveEx)
            {
                _logger.LogError("Failed to move model file from temp to final location: {MoveExMessage}", moveEx.Message);
                throw new InvalidOperationException("Failed to finalize model file", moveEx);
            }

            if (thumbnailTempPath is not null && thumbnailFinalPath is not null)
            {
                MoveStagedFile(thumbnailTempPath, thumbnailFinalPath, "thumbnail");
            }

            FolderNode rootFolder = await _folderManagementService.GetOrCreateFolderAsync("/", "models", ct);

            Model3D model = new()
            {
                Id = modelId,
                Name = originalName,  // Store user-provided filename for display
                FileName = fileName,  // Store GUID-based filename (e.g., "abc123.stl")
                FolderId = rootFolder.Id,  // Root folder for uploaded files
                FilePath = "/",  // Store virtual root path (matching GcodeFile pattern for uploaded files)
                FileSizeBytes = modelFile.Length,
                FileHash = fileHash,
                FileFormat = _fileManagementService.GetModelFileFormat(fileExtension),
                UploadedAt = DateTime.UtcNow,
                IsValid = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                DimensionX = analysis?.DimensionX,
                DimensionY = analysis?.DimensionY,
                DimensionZ = analysis?.DimensionZ,
                TriangleCount = analysis?.TriangleCount,
                ThumbnailFileName = thumbnailFileName,
                UploadedByUserId = userId,
                ClientUploadId = clientUploadId,
                ClientUploadHash = clientUploadId.HasValue ? clientUploadHash : null,
                ExtractedMetadataJson = threeMfMetadata != null ? System.Text.Json.JsonSerializer.Serialize(threeMfMetadata) : null
            };

            await _model3dFiles.AddAsync(model, ct);
            try
            {
                await _model3dFiles.SaveChangesAsync(ct);
            }
            catch (DbUpdateException) when (userId.HasValue && clientUploadId.HasValue)
            {
                await _model3dFiles.RemoveAsync(model, CancellationToken.None);
                Model3D? winningUpload = await _model3dFiles.GetByClientUploadIdAsync(
                    userId.Value,
                    clientUploadId.Value,
                    ct);
                if (winningUpload is null)
                {
                    throw;
                }

                Model3DUploadResultDto retryResult = CreateIdempotentRetryResult(
                    winningUpload,
                    clientUploadHash);
                DeleteUploadArtifact(tempFilePath);
                DeleteUploadArtifact(finalFilePath);
                DeleteUploadArtifact(thumbnailTempPath);
                DeleteUploadArtifact(thumbnailFinalPath);
                return retryResult;
            }

            _logger.LogInformation("Model record saved to database: {ModelId}", modelId);

            // Step 6: Thumbnail generation (best-effort - don't fail upload if thumbnail fails)
            try
            {
                if (thumbnailFile is null && _thumbnailService != null)
                {
                    _logger.LogDebug("Starting thumbnail generation for {ModelId}", modelId);
                    string generatedThumbnailFileName = _fileOperations.GenerateThumbnailFileName(modelId, _thumbnailService.ThumbnailFileExtension);
                    string thumbnailPath = Path.Join(_modelsPath, generatedThumbnailFileName);
                    thumbnailFinalPath = thumbnailPath;

                    if (_fileManagementService.IsSafePath(thumbnailPath, _modelsPath))
                    {
                        // Use final file path for thumbnail generation
                        bool thumbSuccess = await _thumbnailService.GenerateThumbnailAsync(
                            finalFilePath,
                            model.FileFormat,
                            thumbnailPath,
                            ct: ct);

                        if (thumbSuccess)
                        {
                            // Update model with thumbnail filename
                            model.ThumbnailFileName = generatedThumbnailFileName;
                            await _model3dFiles.SaveChangesAsync(ct);

                            _logger.LogInformation("Thumbnail generated successfully for model {ModelId}", modelId);
                        }
                        else
                        {
                            _logger.LogWarning("Thumbnail generation returned false for model {ModelId}", modelId);
                            DeleteUploadArtifact(thumbnailFinalPath);
                            thumbnailFinalPath = null;
                        }
                    }
                }
                else if (thumbnailFile is null)
                {
                    _logger.LogDebug("Thumbnail service not available, skipping thumbnail generation for {ModelId}", modelId);
                }
            }
            catch (Exception thumbnailEx)
            {
                DeleteUploadArtifact(thumbnailFinalPath);
                thumbnailFinalPath = null;
                model.ThumbnailFileName = null;
                _logger.LogWarning("Failed to generate thumbnail for model {ModelId}: {ThumbnailExMessage}. Continuing without thumbnail.", modelId, thumbnailEx.Message);

                // Don't rethrow - upload should succeed even if thumbnail generation fails
            }

            _logger.LogInformation("Model upload complete: {ModelId} ({FileName}). All post-processing finished.", modelId, fileName);
            return CreateUploadResult(model, wasExisting: false);
        }
        catch
        {
            DeleteUploadArtifact(tempFilePath);
            DeleteUploadArtifact(finalFilePath);
            DeleteUploadArtifact(thumbnailTempPath);
            DeleteUploadArtifact(thumbnailFinalPath);

            throw;
        }
    }

    private Model3DUploadResultDto CreateIdempotentRetryResult(Model3D existingModel, string clientUploadHash)
    {
        if (!string.Equals(existingModel.ClientUploadHash, clientUploadHash, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "clientUploadId has already been used for a different model payload.",
                nameof(clientUploadHash));
        }

        return CreateUploadResult(existingModel, wasExisting: true);
    }

    private Model3DUploadResultDto CreateUploadResult(Model3D model, bool wasExisting)
    {
        return new Model3DUploadResultDto
        {
            Id = model.Id,
            Name = model.Name ?? model.FileName,
            FileName = model.FileName,
            FileSize = model.FileSizeBytes,
            FileType = _fileManagementService.GetModelFileFormatString(model.FileFormat)
                ?? Path.GetExtension(model.FileName).TrimStart('.'),
            UploadedAt = model.UploadedAt,
            Url = _fileOperations.BuildModel3DFileUrl(model.Id, model.FileFormat),
            ThumbnailUrl = model.ThumbnailFileName is null
                ? null
                : _fileOperations.BuildModel3DThumbnailUrl(model.Id),
            WasExisting = wasExisting,
            ClientUploadId = model.ClientUploadId,
            ETag = CreateETag(model)
        };
    }

    private static string CreateETag(Model3D model)
        => RevisionETag.EncodeQuoted(model.Revision);

    private static bool MatchesIfMatch(string? ifMatch, string currentETag)
    {
        if (string.IsNullOrWhiteSpace(ifMatch))
        {
            return true;
        }

        return ifMatch
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(candidate => candidate == "*" || string.Equals(candidate, currentETag, StringComparison.Ordinal));
    }

    private async Task<string> StreamModelToTempAndHashAsync(
        IFormFile modelFile,
        string tempFilePath,
        CancellationToken ct)
    {
        await using Stream source = modelFile.OpenReadStream();
        await using Stream destination = _fileSystem.OpenWrite(tempFilePath);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        byte[] buffer = ArrayPool<byte>.Shared.Rent(StreamBufferSize);
        try
        {
            int bytesRead;
            while ((bytesRead = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
            {
                hash.AppendData(buffer, 0, bytesRead);
                await destination.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
            }

            await destination.FlushAsync(ct);
            return _fileManagementService.ToHex(hash.GetHashAndReset());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task StageAndValidateClientThumbnailAsync(
        IFormFile thumbnailFile,
        string thumbnailTempPath,
        CancellationToken ct)
    {
        if (thumbnailFile.Length <= 0)
        {
            throw new ArgumentException("Client thumbnail is empty", nameof(thumbnailFile));
        }

        if (thumbnailFile.Length > MaxClientThumbnailBytes)
        {
            throw new ArgumentException("Client thumbnail exceeds the 10 MB size limit", nameof(thumbnailFile));
        }

        await using (Stream source = thumbnailFile.OpenReadStream())
        await using (Stream destination = _fileSystem.OpenWrite(thumbnailTempPath))
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(StreamBufferSize);
            try
            {
                long totalBytes = 0;
                int bytesRead;
                while ((bytesRead = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
                {
                    totalBytes += bytesRead;
                    if (totalBytes > MaxClientThumbnailBytes)
                    {
                        throw new ArgumentException("Client thumbnail exceeds the 10 MB size limit", nameof(thumbnailFile));
                    }

                    await destination.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                }

                await destination.FlushAsync(ct);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        await ValidateClientPngAsync(thumbnailTempPath, ct);
    }

    private async Task ValidateClientPngAsync(string thumbnailTempPath, CancellationToken ct)
    {
        await using Stream stream = _fileSystem.OpenRead(thumbnailTempPath);
        byte[] signature = new byte[PngSignature.Length];
        int signatureBytesRead = 0;
        while (signatureBytesRead < signature.Length)
        {
            int bytesRead = await stream.ReadAsync(signature.AsMemory(signatureBytesRead), ct);
            if (bytesRead == 0)
            {
                break;
            }

            signatureBytesRead += bytesRead;
        }

        if (signatureBytesRead != signature.Length || !signature.AsSpan().SequenceEqual(PngSignature))
        {
            throw new ArgumentException("Client thumbnail does not have a valid PNG signature");
        }

        stream.Position = 0;

        try
        {
            ImageInfo? imageInfo = await Image.IdentifyAsync(stream, ct);
            if (imageInfo is null
                || !string.Equals(imageInfo.Metadata.DecodedImageFormat?.Name, "PNG", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Client thumbnail is not a decodable PNG");
            }

            if (imageInfo.Width > MaxClientThumbnailDimension || imageInfo.Height > MaxClientThumbnailDimension)
            {
                throw new ArgumentException("Client thumbnail dimensions exceed 4096 x 4096 pixels");
            }

            long pixelCount = (long)imageInfo.Width * imageInfo.Height;
            if (pixelCount > MaxClientThumbnailPixels)
            {
                throw new ArgumentException("Client thumbnail exceeds the 16,000,000 pixel limit");
            }

            stream.Position = 0;
            using Image image = await Image.LoadAsync(stream, ct);
            if (!string.Equals(image.Metadata.DecodedImageFormat?.Name, "PNG", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Client thumbnail is not a decodable PNG");
            }
        }
        catch (UnknownImageFormatException ex)
        {
            throw new ArgumentException("Client thumbnail is not a decodable PNG", ex);
        }
        catch (InvalidImageContentException ex)
        {
            throw new ArgumentException("Client thumbnail is not a decodable PNG", ex);
        }
    }

    private void MoveStagedFile(string tempPath, string finalPath, string artifactName)
    {
        if (!_fileSystem.FileExists(tempPath))
        {
            throw new InvalidOperationException($"Staged {artifactName} file was not found");
        }

        _fileSystem.MoveFile(tempPath, finalPath, overwrite: false);
        if (!_fileSystem.FileExists(finalPath))
        {
            throw new InvalidOperationException($"{artifactName} file move could not be verified");
        }
    }

    private void DeleteUploadArtifact(string? path)
    {
        if (path is null || !_fileManagementService.IsSafePath(path, _modelsPath))
        {
            return;
        }

        try
        {
            if (_fileSystem.FileExists(path))
            {
                _fileSystem.DeleteFile(path);
            }
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Failed to clean upload artifact {Path}", path);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Failed to clean upload artifact {Path}", path);
        }
    }

    /// <summary>
    /// Gets an existing folder or creates a new one at the specified virtual path.
    /// </summary>
    /// <param name="directoryPath">Virtual directory path (e.g., "/MyFolder/SubFolder").</param>
    /// <param name="folderType">Folder type identifier (e.g., "model", "gcode").</param>
    /// <param name="ct">Cancellation token for async operation.</param>
    /// <returns>Existing or newly created folder entity.</returns>
    /// <remarks>
    /// Creates intermediate folders as needed (similar to mkdir -p).
    /// Folders are virtual entities existing only in database.
    /// </remarks>
    public async Task<Guid> GetOrCreateFolderAsync(string directoryPath, string folderType, CancellationToken ct)
    {
        // Delegate to shared folder management service, return folder ID per module interface contract
        FolderNode folder = await _folderManagementService.GetOrCreateFolderAsync(directoryPath, folderType, ct);
        return folder.Id;
    }

    #endregion

    /// <summary>
    /// Downloads a file from the model storage directory by relative path.
    /// Unified with Gcode download endpoint for consistent thumbnail serving.
    /// </summary>
    /// <param name="path">Relative path to the file within model storage directory</param>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <returns>Tuple of (file bytes, safe filename) if found, otherwise null</returns>
    /// <remarks>
    /// This method serves both model files and thumbnails using path-based lookups.
    /// Lexical and physical path validation ensure traversal, absolute paths, and filesystem links
    /// cannot escape the configured model storage directory.
    /// </remarks>
    public async Task<(byte[] Bytes, string FileName)?> DownloadFileAsync(string path, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        if (Path.IsPathRooted(path))
        {
            throw new ArgumentException("The model path must be relative to the configured storage root.", nameof(path));
        }

        string storageRoot = GetFullPathForDownload(_storagePathService.GetModelUploadDirectory(), nameof(path));
        string requestedPath = GetFullPathForDownload(Path.Join(storageRoot, path), nameof(path));
        if (!IsWithinStorageRoot(storageRoot, requestedPath))
        {
            _logger.LogWarning("[Download] Path traversal attempt blocked");
            throw new ArgumentException("The model path escapes the configured storage root.", nameof(path));
        }

        string physicalStorageRoot = ResolvePhysicalPath(storageRoot);
        string physicalRequestedPath = ResolvePhysicalPath(requestedPath);
        if (!IsWithinStorageRoot(physicalStorageRoot, physicalRequestedPath))
        {
            _logger.LogWarning("[Download] Filesystem link escape attempt blocked");
            throw new UnauthorizedAccessException("The resolved model path is outside the configured storage root.");
        }

        if (!_fileSystem.FileExists(physicalRequestedPath))
        {
            _logger.LogWarning("[Download] File not found: {ResolvedPath}", physicalRequestedPath);
            return null;
        }

        try
        {
            byte[] bytes = await _fileSystem.ReadAllBytesAsync(physicalRequestedPath);
            string fileName = Path.GetFileName(requestedPath);

            return (bytes, fileName);
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "[Download] Error reading model file");
            return null;
        }
    }

    private static string GetFullPathForDownload(string path, string parameterName)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (ArgumentException ex)
        {
            throw new ArgumentException("The model path is invalid.", parameterName, ex);
        }
        catch (NotSupportedException ex)
        {
            throw new ArgumentException("The model path is invalid.", parameterName, ex);
        }
        catch (PathTooLongException ex)
        {
            throw new ArgumentException("The model path is invalid.", parameterName, ex);
        }
    }

    private static bool IsWithinStorageRoot(string storageRoot, string candidatePath)
    {
        string relativePath = Path.GetRelativePath(storageRoot, candidatePath);
        return !relativePath.Equals("..", StringComparison.Ordinal)
            && !relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            && !relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal)
            && !Path.IsPathFullyQualified(relativePath);
    }

    private static string ResolvePhysicalPath(string path)
    {
        string pendingPath = Path.GetFullPath(path);
        StringComparer pathComparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        HashSet<string> visitedLinks = new(pathComparer);
        int linkDepth = 0;

        while (true)
        {
            string rootPath = Path.GetPathRoot(pendingPath)
                ?? throw new ArgumentException(
                    "The model path does not have a filesystem root.",
                    nameof(path));
            string currentPath = rootPath;
            string[] segments = pendingPath[rootPath.Length..]
                .Split(
                    [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                    StringSplitOptions.RemoveEmptyEntries);
            bool linkResolved = false;

            for (int index = 0; index < segments.Length; index++)
            {
                currentPath = Path.Join(currentPath, segments[index]);
                FileSystemInfo entry = Directory.Exists(currentPath)
                    ? new DirectoryInfo(currentPath)
                    : new FileInfo(currentPath);
                FileSystemInfo? linkTarget;
                try
                {
                    linkTarget = entry.ResolveLinkTarget(returnFinalTarget: false);
                }
                catch (FileNotFoundException)
                {
                    continue;
                }
                catch (DirectoryNotFoundException)
                {
                    continue;
                }

                if (linkTarget is null)
                {
                    continue;
                }

                string linkPath = Path.GetFullPath(currentPath);
                if (!visitedLinks.Add(linkPath) || ++linkDepth > MaxSymbolicLinkDepth)
                {
                    throw new UnauthorizedAccessException(
                        "The model path contains a filesystem link cycle or exceeds the link depth limit.");
                }

                string targetPath = Path.GetFullPath(linkTarget.FullName);
                if (index + 1 < segments.Length)
                {
                    targetPath = Path.GetFullPath(
                        Path.Join(targetPath, Path.Join(segments[(index + 1)..])));
                }

                pendingPath = targetPath;
                linkResolved = true;
                break;
            }

            if (!linkResolved)
            {
                return Path.GetFullPath(currentPath);
            }
        }
    }

    #region Move Operations

    /// <summary>
    /// Moves a 3D model file to a different virtual folder by updating its database folder reference.
    /// </summary>
    /// <param name="modelId">GUID of the model to move.</param>
    /// <param name="targetFolderPath">Virtual path of the destination folder.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the model was successfully moved; false if model was not found.</returns>
    /// <remarks>
    /// This is a virtual move operation that only updates the model's FolderId reference in the database.
    /// The physical file remains in its original location on disk with its GUID-based filename.
    /// Target folder is created automatically if it doesn't exist.
    /// </remarks>
    public async Task<bool> MoveToFolderAsync(Guid modelId, string targetFolderPath, CancellationToken ct)
    {
        try
        {
            // Get the model from database
            Model3D? model = await _model3dFiles.GetByIdAsync(modelId, ct);
            if (model == null)
            {
                _logger.LogWarning("[MoveToFolder] Model not found: {ModelId}", modelId);
                return false;
            }

            // Get or create the target folder
            FolderNode targetFolder = await _folderManagementService.GetOrCreateFolderAsync(targetFolderPath, "models", ct);

            // Update the folder reference (virtual move - physical file stays in place)
            model.FolderId = targetFolder.Id;

            // Save changes to database
            await _model3dFiles.UpdateAsync(model, ct);
            await _model3dFiles.SaveChangesAsync(ct);

            _logger.LogInformation("[MoveToFolder] Moved model {ModelFileName} to folder {TargetFolderPath}", model.FileName, targetFolderPath);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError("[MoveToFolder] Failed to move model {ModelId}: {Message}", modelId, ex.Message);
            return false;
        }
    }

    #endregion

    #region DTO Mapping Helpers

    /// <summary>
    /// Maps a Model3D domain model to a Model3DDto with thumbnail URL construction using download endpoint pattern.
    /// </summary>
    /// <param name="model">The Model3D domain model to convert.</param>
    /// <returns>A Model3DDto with all file metadata and properly constructed thumbnail URL.</returns>
    /// <remarks>
    /// <para>
    /// This method uses the same pattern as GcodeFilesService for thumbnail URL construction - a path-based
    /// query parameter approach rather than a dedicated {id}/thumbnail endpoint. This provides efficient
    /// thumbnail serving through the generic download endpoint without requiring database lookups.
    /// </para>
    /// <para>
    /// The thumbnail URL is computed from the physical file location by calculating the relative path from
    /// the model storage directory and encoding it for safe transmission in HTTP URLs.
    /// </para>
    /// </remarks>
    private async Task<Model3DDto> MapToDtoAsync(Model3D model)
    {
        string? thumbnailUrl = _fileOperations.BuildModel3DThumbnailUrl(model.Id);

        // Map tags from the tag repository
        IReadOnlyList<Tag> tagEntities = await _tagRepository.GetTagsByObjectAsync(model.Id, CancellationToken.None);
        TagDto[] tags = tagEntities
            .Select(t => new TagDto { Id = t.Id, Name = t.Name, Color = t.Color })
            .ToArray();

        ThreeMfMetadataDto? metadata = DeserializeMetadata(model.ExtractedMetadataJson);

        return new Model3DDto
        {
            Id = model.Id,
            Name = model.Name,
            FileName = model.FileName,
            FileSize = model.FileSizeBytes,
            FileType = _fileManagementService.GetModelFileFormatString(model.FileFormat),
            UploadedAt = model.UploadedAt,
            Url = _fileOperations.BuildModel3DFileUrl(model.Id, model.FileFormat),
            ThumbnailUrl = thumbnailUrl,
            Tags = tags,
            ExtractedMetadata = metadata,
            AutoTags = metadata?.AutoTags?.ToArray(),
            SourceUrl = model.SourceUrl,
            SourceLicense = model.SourceLicense,
            SourceCreator = model.SourceCreator,
            ImportedAt = model.ImportedAt,
            ETag = CreateETag(model)
        };
    }

    private static ThreeMfMetadataDto? DeserializeMetadata(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<ThreeMfMetadataDto>(json);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Maps a Model3D domain model to a Model3DEntryDto for folder browser listing with thumbnail URL.
    /// </summary>
    /// <param name="file">The Model3D domain model to convert.</param>
    /// <param name="virtualPath">The virtual path within the folder hierarchy.</param>
    /// <returns>A Model3DEntryDto representing the file in folder browser context.</returns>
    /// <remarks>
    /// This mapping is used for listing files in virtual folder hierarchies. The thumbnail URL uses the same
    /// download endpoint pattern as MapToDtoAsync for consistency and efficiency.
    /// </remarks>
    private Model3DEntryDto MapToEntryDto(Model3D file, string virtualPath)
    {
        string? thumbnailUrl = _fileOperations.BuildModel3DThumbnailUrl(file.Id);

        return new Model3DEntryDto(
            Path: virtualPath,
            FileName: file.FileName,
            Name: file.Name,  // Include original filename for display
            FileSize: file.FileSizeBytes,
            UploadedAt: file.UploadedAt,
            IsDirectory: false,
            ThumbnailUrl: thumbnailUrl,
            Id: file.Id.ToString(),
            FileType: _fileManagementService.GetModelFileFormatString(file.FileFormat));
    }

    #endregion

    /// <inheritdoc />
    public async Task<GeometryUploadResultDto> UploadGeometryAsync(IFormFile geometryFile, CancellationToken ct)
    {
        if (geometryFile is null || geometryFile.Length == 0)
        {
            throw new ArgumentException("Geometry file is required", nameof(geometryFile));
        }

        const long maxFileSize = 200_000_000; // 200 MB
        if (geometryFile.Length > maxFileSize)
        {
            throw new ArgumentException($"File exceeds maximum allowed size of {maxFileSize / 1_000_000} MB", nameof(geometryFile));
        }

        Guid modelId = Guid.NewGuid();
        string fileName = $"{modelId}.stl";
        string finalFilePath = Path.Join(_modelsPath, fileName);

        if (!_fileManagementService.IsSafePath(finalFilePath, _modelsPath))
        {
            throw new InvalidOperationException("Unsafe file path generated");
        }

        _logger.LogInformation("Geometry upload started: {ModelId} ({FileSize} bytes)", modelId, geometryFile.Length);

        // Write file to a temp path, then move to final location
        string tempFilePath = Path.Join(_modelsPath, $"{modelId}.tmp.stl");
        try
        {
            using (Stream dest = _fileSystem.OpenWrite(tempFilePath))
            {
                await geometryFile.CopyToAsync(dest, ct);
            }

            if (_fileSystem.FileExists(finalFilePath))
            {
                _fileSystem.DeleteFile(finalFilePath);
            }

            _fileSystem.MoveFile(tempFilePath, finalFilePath, overwrite: true);

            if (!_fileSystem.FileExists(finalFilePath))
            {
                throw new InvalidOperationException("File move succeeded but verification failed");
            }
        }
        catch
        {
            // Cleanup on failure
            foreach (string path in new[] { tempFilePath, finalFilePath })
            {
                try
                {
                    if (_fileManagementService.IsSafePath(path, _modelsPath) && _fileSystem.FileExists(path))
                    {
                        _fileSystem.DeleteFile(path);
                    }
                }
                catch
                {
                    // ignore cleanup errors
                }
            }

            throw;
        }

        // Create minimal DB entry so the existing download endpoint can serve the file
        FolderNode rootFolder = await _folderManagementService.GetOrCreateFolderAsync("/", "models", ct);

        Model3D model = new()
        {
            Id = modelId,
            Name = geometryFile.FileName ?? $"cut-geometry-{modelId:N}.stl",
            FileName = fileName,
            FolderId = rootFolder.Id,
            FilePath = "/",
            FileSizeBytes = geometryFile.Length,
            FileHash = modelId.ToString("N"), // Use model ID as hash — no dedup for generated geometry
            FileFormat = ModelFileFormat.STL,
            UploadedAt = DateTime.UtcNow,
            IsValid = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        try
        {
            await _model3dFiles.AddAsync(model, ct);
            await _model3dFiles.SaveChangesAsync(ct);
        }
        catch
        {
            // DB write failed — clean up the orphaned file on disk
            try
            {
                if (_fileManagementService.IsSafePath(finalFilePath, _modelsPath) && _fileSystem.FileExists(finalFilePath))
                {
                    _fileSystem.DeleteFile(finalFilePath);
                }
            }
            catch
            {
                // ignore cleanup errors
            }

            throw;
        }

        string fileUrl = _fileOperations.BuildModel3DFileUrl(modelId, ModelFileFormat.STL);
        _logger.LogInformation("Geometry upload complete: {ModelId}, URL: {FileUrl}", modelId, fileUrl);

        return new GeometryUploadResultDto
        {
            Id = modelId,
            FileName = fileName,
            FileSize = geometryFile.Length,
            FileUrl = fileUrl
        };
    }

    /// <inheritdoc />
    public async Task SetAttributionAsync(Guid modelId, string? sourceUrl, string? sourceCreator, string? sourceLicense, DateTime? importedAt, CancellationToken ct)
    {
        if (sourceUrl is not null && sourceUrl.Length > 2048)
        {
            throw new ArgumentException("SourceUrl must not exceed 2048 characters.", nameof(sourceUrl));
        }

        if (sourceCreator is not null && sourceCreator.Length > 256)
        {
            throw new ArgumentException("SourceCreator must not exceed 256 characters.", nameof(sourceCreator));
        }

        if (sourceLicense is not null && sourceLicense.Length > 128)
        {
            throw new ArgumentException("SourceLicense must not exceed 128 characters.", nameof(sourceLicense));
        }

        Model3D? model = await _model3dFiles.GetByIdUnfilteredAsync(modelId, ct);
        if (model == null)
        {
            throw new InvalidOperationException($"Model {modelId} not found.");
        }

        model.SourceUrl = sourceUrl;
        model.SourceCreator = sourceCreator;
        model.SourceLicense = sourceLicense;
        model.ImportedAt = importedAt;
        model.UpdatedAt = DateTime.UtcNow;

        await _model3dFiles.UpdateAsync(model, ct);
        await _model3dFiles.SaveChangesAsync(ct);
    }
}
