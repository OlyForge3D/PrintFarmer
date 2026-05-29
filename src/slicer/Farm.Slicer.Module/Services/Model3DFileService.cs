using System;
using System.Collections.Generic;
using System.Linq;
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
        return Path.Combine(_modelsPath, model.FileName);
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
        return model == null ? null : (string.IsNullOrEmpty(model.ThumbnailFileName) ? null : Path.Combine(_modelsPath, model.ThumbnailFileName));
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
            string fullModelPath = Path.Combine(_modelsPath, model.FileName);
            if (_fileManagementService.IsSafePath(fullModelPath, _modelsPath) && System.IO.File.Exists(fullModelPath))
            {
                System.IO.File.Delete(fullModelPath);
            }

            // Thumbnail is stored in same directory
            string? thumbnailFileName = model.ThumbnailFileName;
            if (thumbnailFileName != null)
            {
                string fullThumbnailPath = Path.Combine(_modelsPath, thumbnailFileName);
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

    public async Task<Model3DUploadResultDto> UploadModelAsync(IFormFile modelFile, CancellationToken ct)
    {
        if (modelFile == null || modelFile.Length == 0)
        {
            throw new ArgumentException("Model file is required", nameof(modelFile));
        }

        string originalName = modelFile.FileName ?? string.Empty;
        string fileExtension = Path.GetExtension(originalName);

        // Validate extension using service
        _fileManagementService.ValidateModelExtension(fileExtension);

        Guid modelId = Guid.NewGuid();
        string fileName = $"{modelId}{fileExtension}";
        string finalFilePath = Path.Combine(_modelsPath, fileName);
        if (!_fileManagementService.IsSafePath(finalFilePath, _modelsPath))
        {
            throw new InvalidOperationException("Unsafe file path generated");
        }

        _logger.LogInformation("Starting model upload: {FileName} ({FileSize} bytes), ID: {ModelId}", originalName, modelFile.Length, modelId);

        // Use temp file pattern for safety: write to temp, then move to final location
        string tempFileName = $"{modelId}.tmp{fileExtension}";
        string tempFilePath = Path.Combine(_modelsPath, tempFileName);

        try
        {
            // Step 1: Write to temp file and compute hash
            string fileHash;
            try
            {
                using (Stream stream = _fileSystem.OpenWrite(tempFilePath))
                {
                    using MemoryStream memoryStream = new();
                    await modelFile.CopyToAsync(memoryStream, ct);
                    memoryStream.Position = 0;

                    byte[] hashBytes = await System.Security.Cryptography.SHA256.HashDataAsync(memoryStream, ct);
                    fileHash = _fileManagementService.ToHex(hashBytes);

                    memoryStream.Position = 0;
                    await memoryStream.CopyToAsync(stream, ct);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError("Failed to write model file to temp location: {Message}", ex.Message);

                // Cleanup temp file if write failed
                try
                {
                    if (_fileManagementService.IsSafePath(tempFilePath, _modelsPath) && _fileSystem.FileExists(tempFilePath))
                    {
                        _fileSystem.DeleteFile(tempFilePath);
                    }
                }
                catch
                { /* ignore cleanup errors */
                }

                throw;
            }

            // Virus scan best-effort: if scanner not available skip
            try
            {
                // Resolve a scanner service via DI would be better; for now skip
            }
            catch
            {
            }

            // Step 2: Analyze model metadata (best-effort)
            Farm.Infrastructure.Services.Models.ModelAnalysisResult? analysis = null;
            try
            {
                // analysis is optional; resolve from DI if available via _analysisService
                if (_analysisService != null)
                {
                    _logger.LogDebug("Analyzing model metadata for {ModelId}", modelId);
                    analysis = await _analysisService.AnalyzeModelAsync(tempFilePath, fileExtension, ct);
                    _logger.LogDebug("Model analysis complete for {ModelId}: {DimensionX}x{DimensionY}x{DimensionZ}mm", modelId, analysis?.DimensionX, analysis?.DimensionY, analysis?.DimensionZ);
                }
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
            catch (Exception metadataEx)
            {
                _logger.LogWarning("3MF metadata extraction failed for {ModelId}: {Message}", modelId, metadataEx.Message);
            }

            // Step 3: Check for duplicates
            Model3D? existingModel = await _model3dFiles.GetByHashAsync(fileHash, ct);
            string baseName = Path.GetFileNameWithoutExtension(originalName);
            if (existingModel != null)
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
                    // Cleanup temp file before returning existing
                    if (_fileManagementService.IsSafePath(tempFilePath, _modelsPath) && _fileSystem.FileExists(tempFilePath))
                    {
                        _fileSystem.DeleteFile(tempFilePath);
                    }

                    return new Model3DUploadResultDto
                    {
                        Id = existingModel.Id,
                        FileName = existingModel.FileName,
                        FileSize = existingModel.FileSizeBytes,
                        FileType = _fileManagementService.GetModelFileFormatString(existingModel.FileFormat),
                        UploadedAt = existingModel.UploadedAt,
                        Url = _fileOperations.BuildModel3DFileUrl(existingModel.Id, existingModel.FileFormat)
                    };
                }

                byte[] composite = System.Text.Encoding.UTF8.GetBytes(fileHash + "|" + originalName);
                byte[] newHashBytes = System.Security.Cryptography.SHA256.HashData(composite);
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

            // Step 5: Create folder and database record AFTER file is confirmed on disk
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
                ExtractedMetadataJson = threeMfMetadata != null ? System.Text.Json.JsonSerializer.Serialize(threeMfMetadata) : null
            };

            await _model3dFiles.AddAsync(model, ct);
            await _model3dFiles.SaveChangesAsync(ct);
            _logger.LogInformation("Model record saved to database: {ModelId}", modelId);

            // Step 6: Thumbnail generation (best-effort - don't fail upload if thumbnail fails)
            try
            {
                if (_thumbnailService != null)
                {
                    _logger.LogDebug("Starting thumbnail generation for {ModelId}", modelId);
                    string thumbnailFileName = _fileOperations.GenerateThumbnailFileName(modelId, _thumbnailService.ThumbnailFileExtension);
                    string thumbnailPath = Path.Combine(_modelsPath, thumbnailFileName);

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
                            model.ThumbnailFileName = thumbnailFileName;
                            await _model3dFiles.SaveChangesAsync(ct);

                            _logger.LogInformation("Thumbnail generated successfully for model {ModelId}", modelId);
                        }
                        else
                        {
                            _logger.LogWarning("Thumbnail generation returned false for model {ModelId}", modelId);
                        }
                    }
                }
                else
                {
                    _logger.LogDebug("Thumbnail service not available, skipping thumbnail generation for {ModelId}", modelId);
                }
            }
            catch (Exception thumbnailEx)
            {
                _logger.LogWarning("Failed to generate thumbnail for model {ModelId}: {ThumbnailExMessage}. Continuing without thumbnail.", modelId, thumbnailEx.Message);

                // Don't rethrow - upload should succeed even if thumbnail generation fails
            }

            _logger.LogInformation("Model upload complete: {ModelId} ({FileName}). All post-processing finished.", modelId, fileName);
            return new Model3DUploadResultDto
            {
                Id = modelId,
                FileName = model.FileName,
                FileSize = modelFile.Length,
                FileType = fileExtension.TrimStart('.'),
                UploadedAt = model.UploadedAt,
                Url = _fileOperations.BuildModel3DFileUrl(modelId, model.FileFormat)
            };
        }
        catch
        {
            // Cleanup both temp and final files if something fails
            try
            {
                // Try to clean up temp file
                if (_fileManagementService.IsSafePath(tempFilePath, _modelsPath) && _fileSystem.FileExists(tempFilePath))
                {
                    _fileSystem.DeleteFile(tempFilePath);
                }

                // Try to clean up final file if it was already moved
                if (_fileManagementService.IsSafePath(finalFilePath, _modelsPath) && _fileSystem.FileExists(finalFilePath))
                {
                    _fileSystem.DeleteFile(finalFilePath);
                }
            }
            catch
            { /* ignore cleanup errors */
            }

            throw;
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
    /// Path validation is performed internally to prevent directory traversal attacks.
    /// Paths are normalized and validated to ensure they stay within the model storage directory.
    /// </remarks>
    public async Task<(byte[] Bytes, string FileName)?> DownloadFileAsync(string path, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            // Normalize path and validate it's within storage directory
            string modelsDir = _storagePathService.GetModelUploadDirectory();
            string normalizedPath = Path.GetFullPath(path);
            string normalizedStorageDir = Path.GetFullPath(modelsDir);

            // Construct full path
            string fullPath = Path.Combine(normalizedStorageDir, path);
            string resolvedPath = Path.GetFullPath(fullPath);

            // Security check: ensure resolved path is within storage directory
            if (!resolvedPath.StartsWith(normalizedStorageDir, StringComparison.Ordinal))
            {
                _logger.LogWarning("[Download] Path traversal attempt blocked: {Path}", path);
                return null;
            }

            // Check file exists
            if (!_fileSystem.FileExists(resolvedPath))
            {
                _logger.LogWarning("[Download] File not found: {ResolvedPath}", resolvedPath);
                return null;
            }

            // Read file bytes
            byte[] bytes = await _fileSystem.ReadAllBytesAsync(resolvedPath);
            string fileName = Path.GetFileName(resolvedPath);

            return (bytes, fileName);
        }
        catch (Exception ex)
        {
            _logger.LogError("[Download] Error reading file {Path}: {Message}", path, ex.Message);
            return null;
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
            AutoTags = metadata?.AutoTags?.ToArray()
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
        string finalFilePath = Path.Combine(_modelsPath, fileName);

        if (!_fileManagementService.IsSafePath(finalFilePath, _modelsPath))
        {
            throw new InvalidOperationException("Unsafe file path generated");
        }

        _logger.LogInformation("Geometry upload started: {ModelId} ({FileSize} bytes)", modelId, geometryFile.Length);

        // Write file to a temp path, then move to final location
        string tempFilePath = Path.Combine(_modelsPath, $"{modelId}.tmp.stl");
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
}
