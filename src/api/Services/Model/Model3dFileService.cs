using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Normalization;
using Farm.Infrastructure.Repositories.UnitOfWork;
using Farm.Infrastructure.Services.Models;
using Farm.Infrastructure.Services.StorageManagement;
using Farm.Infrastructure.Services.Thumbnails;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Services.FileManagement;
using Farm.Web.Api.Services.FolderManagement;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Farm.Web.Api.Services.Model
{
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
    public class Model3DFileService : IModel3DFileService
    {
        private readonly IUnitOfWork _unitOfWork;
        private readonly IUnifiedLoggingService _logger;
        private readonly string _modelsPath;
        private readonly IModelAnalysisService? _analysisService;
        private readonly Farm.Web.Api.Services.IO.IFileSystem _fileSystem;
        private readonly IFileManagementService _fileManagementService;
        private readonly IStoredFileOperationsService _fileOperations;
        private readonly IThumbnailGenerationService? _thumbnailService;
        private readonly IFolderManagementService _folderManagementService;
        private readonly IStoragePathService _storagePathService;

        public Model3DFileService(
            IUnitOfWork unitOfWork,
            IUnifiedLoggingService logger,
            IConfiguration configuration,
            Farm.Web.Api.Services.IO.IFileSystem fileSystem,
            IFileManagementService fileManagementService,
            IFolderManagementService folderManagementService,
            IStoragePathService storagePathService,
            IStoredFileOperationsService fileOperations,
            IModelAnalysisService? analysisService = null,
            IThumbnailGenerationService? thumbnailService = null)
        {
            _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _analysisService = analysisService;
            _thumbnailService = thumbnailService;
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
            IReadOnlyList<Model3D> models = await _unitOfWork.Model3dFiles.ListValidAsync(ct);

            return models.Select(m => MapToDto(m)).ToList();
        }

        /// <summary>
        /// Lists 3D models with virtual folder hierarchy, pagination, sorting, and search capabilities.
        /// </summary>
        /// <param name="path">Virtual directory path (e.g., "/MyModels/Characters"). Defaults to root "/"</param>
        /// <param name="sortBy">Field to sort by: "name", "size", "date". Defaults to "name"</param>
        /// <param name="sortOrder">Sort direction: "asc" or "desc". Defaults to "asc"</param>
        /// <param name="search">Optional search term for filtering model names (case-insensitive)</param>
        /// <param name="page">Page number (1-based). Min: 1</param>
        /// <param name="pageSize">Items per page. Min: 1, Max: 500</param>
        /// <param name="ct">Cancellation token for async operation</param>
        /// <returns>Response containing paginated models, folders, and pagination metadata</returns>
        /// <remarks>
        /// Uses virtual folder architecture where folders exist only in database.
        /// Supports breadcrumb navigation with parent path tracking.
        /// Returns both subdirectories and models in the specified path.
        /// </remarks>
        public async Task<Model3DListResponse> ListModelsWithHierarchyAsync(string? path, string? sortBy, string? sortOrder, string? search, int page, int pageSize, CancellationToken ct)
        {
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

            // Parse virtual path to directory
            string? vPath = string.IsNullOrWhiteSpace(path) ? "/" : path.Trim();
            if (!vPath.StartsWith('/'))
            {
                vPath = "/" + vPath;
            }

            string[] segments = vPath.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Where(s => s != "." && s != "..")
                .ToArray();
            string requestedDir = segments.Length == 0 ? "/" : "/" + string.Join('/', segments);
            string? virtualPathNormalized = segments.Length == 0 ? "/" : "/" + string.Join('/', segments);

            // Get all files and subdirectories from database for this directory (pure DB approach)
            List<Model3D> dbFiles = await _unitOfWork.Model3dFiles.ListValidByDirectoryAsync(requestedDir, ct);
            List<string> subdirectories = await _unitOfWork.Model3dFiles.ListSubdirectoriesAsync(requestedDir, ct);

            // Build directory entries
            List<Model3DEntryDto> entries = new();

            foreach (string subdir in subdirectories)
            {
                if (subdir.StartsWith('.'))
                {
                    continue;
                }

                if (!IsMatch(subdir, search))
                {
                    continue;
                }

                string childVirtual = CombineVirtual(virtualPathNormalized, subdir);
                entries.Add(new Model3DEntryDto(
                    Path: childVirtual,
                    FileName: subdir,
                    Size: 0,
                    ModifiedAt: DateTime.UtcNow,
                    IsDirectory: true,
                    DirectoryId: childVirtual  // Directory ID is its own virtual path (FileDirectory value)
                ));
            }

            // Add files from database
            foreach (var file in dbFiles)
            {
                if (!IsMatch(file.FileName, search))
                {
                    continue;
                }

                string childVirtual = CombineVirtual(virtualPathNormalized, file.FileName);
                entries.Add(MapToEntryDto(file, childVirtual));
            }

            // Sorting
            string normalizedSortBy = string.IsNullOrWhiteSpace(sortBy) ? "name" : sortBy.Trim();
            string normalizedSortOrder = string.IsNullOrWhiteSpace(sortOrder) ? "asc" : sortOrder.Trim();
            bool orderDesc = normalizedSortOrder.Equals("desc", StringComparison.OrdinalIgnoreCase);

            if (normalizedSortBy.Equals("size", StringComparison.OrdinalIgnoreCase))
            {
                entries = orderDesc
                    ? entries.OrderByDescending(e => e.IsDirectory).ThenByDescending(e => e.Size).ThenBy(e => e.FileName, StringComparer.OrdinalIgnoreCase).ToList()
                    : entries.OrderByDescending(e => e.IsDirectory).ThenBy(e => e.Size).ThenBy(e => e.FileName, StringComparer.OrdinalIgnoreCase).ToList();
            }
            else if (normalizedSortBy.Equals("date", StringComparison.OrdinalIgnoreCase))
            {
                entries = orderDesc
                    ? entries.OrderByDescending(e => e.IsDirectory).ThenByDescending(e => e.ModifiedAt).ThenBy(e => e.FileName, StringComparer.OrdinalIgnoreCase).ToList()
                    : entries.OrderByDescending(e => e.IsDirectory).ThenBy(e => e.ModifiedAt).ThenBy(e => e.FileName, StringComparer.OrdinalIgnoreCase).ToList();
            }
            else
            {
                entries = orderDesc
                    ? entries.OrderByDescending(e => e.IsDirectory).ThenByDescending(e => e.FileName, StringComparer.OrdinalIgnoreCase).ToList()
                    : entries.OrderByDescending(e => e.IsDirectory).ThenBy(e => e.FileName, StringComparer.OrdinalIgnoreCase).ToList();
            }

            int totalFiles = entries.Count(e => !e.IsDirectory);
            long totalSize = entries.Where(e => !e.IsDirectory).Sum(e => e.Size);
            int skip = (page - 1) * pageSize;
            IReadOnlyList<Model3DEntryDto> pagedEntries = skip >= entries.Count ? Array.Empty<Model3DEntryDto>() : entries.Skip(skip).Take(pageSize).ToList();
            int totalItems = entries.Count;
            int totalPages = (int)Math.Ceiling(totalItems / (double)pageSize);

            return new Model3DListResponse(pagedEntries, totalFiles, totalSize, page, pageSize, totalPages, totalItems);
        }

        #region Helper Methods

        /// <summary>
        /// Combines parent virtual path with child name to create full virtual path.
        /// </summary>
        /// <param name="parentPath">Parent directory path or null for root</param>
        /// <param name="name">Child folder or file name</param>
        /// <returns>Combined virtual path (e.g., "/parent/child")</returns>
        private static string CombineVirtual(string? parentPath, string name)
        {
            if (string.IsNullOrEmpty(parentPath) || parentPath == "/")
            {
                return "/" + name;
            }
            return UrlNormalizer.CombineUrl(parentPath, name);
        }

        /// <summary>
        /// Checks if a name matches the search term (case-insensitive substring match).
        /// </summary>
        /// <param name="name">Name to check against search term</param>
        /// <param name="search">Search term, or null to match all</param>
        /// <returns>True if name matches search criteria, false otherwise</returns>
        private static bool IsMatch(string name, string? search)
        {
            if (string.IsNullOrWhiteSpace(search))
            {
                return true;
            }
            return name.Contains(search, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Retrieves a specific 3D model by its unique identifier.
        /// </summary>
        /// <param name="id">Unique model identifier (GUID)</param>
        /// <param name="ct">Cancellation token for async operation</param>
        /// <returns>Model DTO with file details, or null if not found</returns>
        public async Task<Model3DDto?> GetModelAsync(Guid id, CancellationToken ct)
        {
            Model3D? model = await _unitOfWork.Model3dFiles.GetByIdAsync(id, ct);
            if (model == null)
            {
                return null;
            }

            return MapToDto(model);
        }

        /// <summary>
        /// Gets the physical file path for a 3D model file.
        /// </summary>
        /// <param name="id">Unique model identifier (GUID)</param>
        /// <param name="ct">Cancellation token for async operation</param>
        /// <returns>Full filesystem path to the model file, or null if not found</returns>
        public async Task<string?> GetModelFilePathAsync(Guid id, CancellationToken ct)
        {
            Model3D? model = await _unitOfWork.Model3dFiles.GetByIdAsync(id, ct);
            if (model == null)
            {
                return null;
            }
            // Return relative path by combining FilePath (directory) with FileName (GUID filename)
            // FilePath is the storage directory, FileName is the GUID-based filename
            return Path.Combine(model.FilePath, model.FileName).Replace(_modelsPath, "").TrimStart(Path.DirectorySeparatorChar, '/');
        }

        /// <summary>
        /// Gets the physical file path for a model's thumbnail image.
        /// </summary>
        /// <param name="id">Unique model identifier (GUID)</param>
        /// <param name="ct">Cancellation token for async operation</param>
        /// <returns>Full filesystem path to thumbnail, or null if thumbnail not available</returns>
        public async Task<string?> GetModelThumbnailPathAsync(Guid id, CancellationToken ct)
        {
            Model3D? model = await _unitOfWork.Model3dFiles.GetByIdAsync(id, ct);
            if (model == null)
            {
                return null;
            }
            return _fileOperations.GetFullThumbnailPath(model);
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
            Model3D? model = await _unitOfWork.Model3dFiles.GetByIdAsync(id, ct);
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

                await _unitOfWork.Model3dFiles.RemoveAsync(model, ct);
                await _unitOfWork.SaveChangesAsync(ct);
                _logger.LogInformation($"Model deleted: {id}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to delete model: {id}: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Validates a 3D model file upload before processing.
        /// </summary>
        /// <param name="modelFile">HTTP form file containing the model</param>
        /// <returns>Validation result indicating success/failure and error messages</returns>
        /// <remarks>
        /// Validates:
        /// - File is not null or empty
        /// - File extension is supported (.stl, .obj, .3mf)
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
                    _logger.LogError($"Failed to write model file to temp location: {ex.Message}");
                    // Cleanup temp file if write failed
                    try
                    {
                        if (_fileManagementService.IsSafePath(tempFilePath, _modelsPath) && _fileSystem.FileExists(tempFilePath))
                        {
                            _fileSystem.DeleteFile(tempFilePath);
                        }
                    }
                    catch { /* ignore cleanup errors */ }
                    throw;
                }

                // Virus scan best-effort: if scanner not available skip
                try
                {
                    // Resolve a scanner service via DI would be better; for now skip
                }
                catch { }

                // Step 2: Analyze model metadata (best-effort)
                ModelAnalysisResult? analysis = null;
                try
                {
                    // analysis is optional; resolve from DI if available via _analysisService
                    if (_analysisService != null)
                    {
                        analysis = await _analysisService.AnalyzeModelAsync(tempFilePath, fileExtension, ct);
                    }
                }
                catch { }

                // Step 3: Check for duplicates
                Model3D? existingModel = await _unitOfWork.Model3dFiles.GetByHashAsync(fileHash, ct);
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
                            Url = $"/api/3d-models/{existingModel.Id}/file"
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

                        _logger.LogDebug($"Model file moved from temp to final location: {modelId}");
                    }
                    else
                    {
                        throw new InvalidOperationException("Temp file not found after write operation");
                    }
                }
                catch (Exception moveEx)
                {
                    _logger.LogError($"Failed to move model file from temp to final location: {moveEx.Message}");
                    throw new InvalidOperationException("Failed to finalize model file", moveEx);
                }

                // Step 5: Create folder and database record AFTER file is confirmed on disk
                var rootFolder = await GetOrCreateFolderAsync("/", "models", ct);

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
                    TriangleCount = analysis?.TriangleCount
                };

                await _unitOfWork.Model3dFiles.AddAsync(model, ct);
                await _unitOfWork.SaveChangesAsync(ct);

                // Step 6: Thumbnail generation (best-effort - don't fail upload if thumbnail fails)
                try
                {
                    if (_thumbnailService != null)
                    {
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
                                await _unitOfWork.SaveChangesAsync(ct);

                                _logger.LogInformation($"Thumbnail generated successfully for model {modelId}");
                            }
                        }
                    }
                }
                catch (Exception thumbnailEx)
                {
                    _logger.LogWarning($"Failed to generate thumbnail for model {modelId}: {thumbnailEx.Message}. Continuing without thumbnail.");
                    // Don't rethrow - upload should succeed even if thumbnail generation fails
                }

                return new Model3DUploadResultDto
                {
                    Id = modelId,
                    FileName = model.FileName,
                    FileSize = modelFile.Length,
                    FileType = fileExtension.TrimStart('.'),
                    UploadedAt = model.UploadedAt,
                    Url = $"/api/3d-models/{modelId}/file"
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
                catch { /* ignore cleanup errors */ }

                throw;
            }
        }

        /// <summary>
        /// Gets or creates a Folder entity for the given directory path and type
        /// </summary>
        /// <summary>
        /// Gets an existing folder or creates a new one at the specified virtual path.
        /// </summary>
        /// <param name="directoryPath">Virtual directory path (e.g., "/MyFolder/SubFolder")</param>
        /// <param name="folderType">Folder type identifier (e.g., "model", "gcode")</param>
        /// <param name="ct">Cancellation token for async operation</param>
        /// <returns>Existing or newly created folder entity</returns>
        /// <remarks>
        /// Creates intermediate folders as needed (similar to mkdir -p).
        /// Folders are virtual entities existing only in database.
        /// </remarks>
        public async Task<FolderNode> GetOrCreateFolderAsync(string directoryPath, string folderType, CancellationToken ct)
        {
            // Delegate to shared folder management service
            return await _folderManagementService.GetOrCreateFolderAsync(directoryPath, folderType, ct);
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
        public async Task<(byte[] bytes, string fileName)?> DownloadFileAsync(string path, CancellationToken ct)
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
                    _logger.LogWarning($"[Download] Path traversal attempt blocked: {path}");
                    return null;
                }

                // Check file exists
                if (!_fileSystem.FileExists(resolvedPath))
                {
                    _logger.LogWarning($"[Download] File not found: {resolvedPath}");
                    return null;
                }

                // Read file bytes
                byte[] bytes = await _fileSystem.ReadAllBytesAsync(resolvedPath);
                string fileName = Path.GetFileName(resolvedPath);

                return (bytes, fileName);
            }
            catch (Exception ex)
            {
                _logger.LogError($"[Download] Error reading file {path}: {ex.Message}");
                return null;
            }
        }

        #region Move Operations

        /// <summary>
        /// Moves a 3D model file to a different virtual folder by updating its database folder reference.
        /// </summary>
        /// <param name="modelId">GUID of the model to move</param>
        /// <param name="targetFolderPath">Virtual path of the destination folder</param>
        /// <param name="ct">Cancellation token</param>
        /// <returns>True if the model was successfully moved; false if model was not found</returns>
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
                var model = await _unitOfWork.Model3dFiles.GetByIdAsync(modelId, ct);
                if (model == null)
                {
                    _logger.LogWarning($"[MoveToFolder] Model not found: {modelId}");
                    return false;
                }

                // Get or create the target folder
                var targetFolder = await _folderManagementService.GetOrCreateFolderAsync(targetFolderPath, "models", ct);

                // Update the folder reference (virtual move - physical file stays in place)
                model.FolderId = targetFolder.Id;

                // Save changes to database
                await _unitOfWork.SaveChangesAsync(ct);

                _logger.LogInformation($"[MoveToFolder] Moved model {model.FileName} to folder {targetFolderPath}");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError($"[MoveToFolder] Failed to move model {modelId}: {ex.Message}");
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
        private Model3DDto MapToDto(Model3D model)
        {
            string? thumbnailUrl = _fileOperations.BuildThumbnailUrl(
                model,
                "/api/3d-models/download",
                _storagePathService.GetModelUploadDirectory()
            );

            return new Model3DDto
            {
                Id = model.Id,
                Name = model.Name,
                FileName = model.FileName,
                FileSize = model.FileSizeBytes,
                FileType = _fileManagementService.GetModelFileFormatString(model.FileFormat),
                UploadedAt = model.UploadedAt,
                Url = $"/api/3d-models/{model.Id}/file",
                ThumbnailPath = thumbnailUrl
            };
        }

        /// <summary>
        /// Maps a Model3D domain model to a Model3DEntryDto for folder browser listing with thumbnail URL.
        /// </summary>
        /// <param name="file">The Model3D domain model to convert.</param>
        /// <param name="virtualPath">The virtual path within the folder hierarchy.</param>
        /// <returns>A Model3DEntryDto representing the file in folder browser context.</returns>
        /// <remarks>
        /// This mapping is used for listing files in virtual folder hierarchies. The thumbnail URL uses the same
        /// download endpoint pattern as MapToDto for consistency and efficiency.
        /// </remarks>
        private Model3DEntryDto MapToEntryDto(Model3D file, string virtualPath)
        {
            string? thumbnailUrl = _fileOperations.BuildThumbnailUrl(
                file,
                "/api/3d-models/download",
                _storagePathService.GetModelUploadDirectory()
            );

            return new Model3DEntryDto(
                Path: virtualPath,
                FileName: file.FileName,
                Name: file.Name,  // Include original filename for display
                Size: file.FileSizeBytes,
                ModifiedAt: file.UploadedAt,
                IsDirectory: false,
                ThumbnailPath: thumbnailUrl,
                ModelId: file.Id.ToString()
            );
        }

        #endregion
    }
}
