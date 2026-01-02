using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Normalization;
using Farm.Infrastructure.Repositories.Gcode;
using Farm.Infrastructure.Services.Gcode;
using Farm.Infrastructure.Services.StorageManagement;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Controllers;
using Farm.Web.Api.Services.FileManagement;
using Farm.Web.Api.Services.FolderManagement;
using Microsoft.AspNetCore.Http;

namespace Farm.Web.Api.Services.Gcode
{
    /// <summary>
    /// Service for managing G-code file operations including upload, listing, metadata extraction,
    /// and virtual folder organization. Handles both physical file storage and database tracking
    /// with support for hierarchical browsing and efficient lookups.
    /// </summary>
    /// <remarks>
    /// This service implements virtual folder architecture where:
    /// - Physical files are stored in a flat directory structure with GUID-based names
    /// - Virtual folders exist only in the database for organizational purposes
    /// - Files are tracked in the database with metadata, thumbnails, and folder references
    /// - Move operations update database references without moving physical files
    /// </remarks>
    public class GcodeFilesService : IGcodeFilesService
    {
        private readonly IGcodeRepository _gcodeRepo;
        private readonly IUnifiedLoggingService _logger;
        private readonly IStoragePathService _storagePathService;
        private readonly IGcodeMetadataExtractorService _metadataExtractor;
        private readonly IGcodeThumbnailExtractorService _thumbnailExtractor;
        private readonly IFolderManagementService _folderService;

        public GcodeFilesService(
            IGcodeRepository gcodeRepo,
            IUnifiedLoggingService logger,
            IStoragePathService storagePathService,
            IGcodeMetadataExtractorService metadataExtractor,
            IGcodeThumbnailExtractorService thumbnailExtractor,
            IFolderManagementService folderService)
        {
            _gcodeRepo = gcodeRepo ?? throw new ArgumentNullException(nameof(gcodeRepo));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _storagePathService = storagePathService ?? throw new ArgumentNullException(nameof(storagePathService));
            _metadataExtractor = metadataExtractor ?? throw new ArgumentNullException(nameof(metadataExtractor));
            _thumbnailExtractor = thumbnailExtractor ?? throw new ArgumentNullException(nameof(thumbnailExtractor));
            _folderService = folderService ?? throw new ArgumentNullException(nameof(folderService));
        }

        /// <summary>
        /// Lists G-code files and subdirectories within a specific virtual path with pagination and filtering.
        /// </summary>
        /// <param name="path">Virtual path to browse (e.g., '/', '/subfolder')</param>
        /// <param name="sortBy">Sort field: 'name', 'size', or 'date'</param>
        /// <param name="sortOrder">Sort order: 'asc' or 'desc'</param>
        /// <param name="search">Optional search term to filter by filename</param>
        /// <param name="page">Page number (1-based, default: 1)</param>
        /// <param name="pageSize">Items per page (min: 1, max: 500, default: 100)</param>
        /// <param name="harvestId">Optional harvest operation ID to filter files</param>
        /// <param name="printerId">Optional printer ID for filtering (currently unused)</param>
        /// <param name="ct">Cancellation token</param>
        /// <returns>Paginated list of files and directories with metadata</returns>
        /// <remarks>
        /// This method queries the database for files and subdirectories, applies sorting and filtering,
        /// and returns a paginated response. Directories are always sorted before files.
        /// </remarks>
        public async Task<GcodeFileListResponse> ListAsync(string? path, string? sortBy, string? sortOrder, string? search, int page, int pageSize, Guid? harvestId, Guid? printerId, CancellationToken ct)
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
            List<GcodeFile> dbFiles = await _gcodeRepo.ListValidByDirectoryAsync(requestedDir, ct);
            List<string> subdirectories = await _gcodeRepo.ListSubdirectoriesAsync(requestedDir, ct);

            // Build directory entries
            List<GcodeFileEntryDto> entries = new();

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
                entries.Add(new GcodeFileEntryDto(
                    Path: childVirtual,
                    FileName: subdir,
                    Size: 0,
                    ModifiedAt: DateTime.UtcNow, // Directories don't have modification time in DB
                    IsDirectory: true
                ));
            }

            // Get harvest operations for all printers (once, not per file)
            var printerIds = dbFiles
                .Where(f => f.SourcePrinterId.HasValue)
                .Select(f => f.SourcePrinterId!.Value)
                .Distinct()
                .ToList();

            Dictionary<Guid, Guid?> harvestOpsByPrinter = new();
            if (printerIds.Count > 0)
            {
                try
                {
                    harvestOpsByPrinter = await _gcodeRepo.GetLatestHarvestOperationIdsByPrintersAsync(printerIds, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug($"Non-fatal DB query failure fetching harvest operations: {ex.Message}");
                }
            }

            // Add files from database
            foreach (var file in dbFiles)
            {
                if (!IsMatch(file.FileName, search))
                {
                    continue;
                }

                Guid? harvestOpId = file.SourcePrinterId.HasValue
                    ? harvestOpsByPrinter.GetValueOrDefault(file.SourcePrinterId.Value)
                    : null;

                // Apply harvest filter if specified
                if (harvestId.HasValue && harvestOpId != harvestId)
                {
                    continue;
                }

                string childVirtual = CombineVirtual(virtualPathNormalized, file.FileName);

                // Convert thumbnail path to API URL if available
                string? thumbnailUrl = null;
                if (!string.IsNullOrEmpty(file.ThumbnailFileName))
                {
                    // Construct full path from directory + filename
                    string fullThumbnailPath = Path.Combine(file.FilePath, file.ThumbnailFileName);
                    // Convert full filesystem path to virtual path for API download endpoint
                    // Remove the storage root prefix to get virtual path
                    string gcodeStorageDir = _storagePathService.GetGcodeStorageDirectory();
                    string normalizedStorageDir = Path.GetFullPath(gcodeStorageDir);
                    string normalizedThumbnailPath = Path.GetFullPath(fullThumbnailPath);

                    if (normalizedThumbnailPath.StartsWith(normalizedStorageDir, StringComparison.Ordinal))
                    {
                        // Extract relative path from storage directory
                        string relativePath = normalizedThumbnailPath.Substring(normalizedStorageDir.Length)
                            .TrimStart(Path.DirectorySeparatorChar, '/');
                        // Convert to forward slashes for URL
                        relativePath = relativePath.Replace(Path.DirectorySeparatorChar, '/');
                        thumbnailUrl = $"/api/gcode-files/download?path={Uri.EscapeDataString(relativePath)}";
                    }
                    else
                    {
                        // Fallback: just use the filename
                        thumbnailUrl = $"/api/gcode-files/download?path={Uri.EscapeDataString(file.ThumbnailFileName)}";
                    }
                }

                entries.Add(new GcodeFileEntryDto(
                    Path: childVirtual,
                    FileName: file.FileName,
                    Size: file.FileSizeBytes,
                    ModifiedAt: file.UploadedAt,
                    IsDirectory: false,
                    HarvestOperationId: harvestOpId,
                    ThumbnailPath: thumbnailUrl,
                    GcodeFileId: null,
                    DirectoryId: null,
                    TargetModelName: file.TargetModel?.Name,
                    RequiredMaterial: file.RequiredMaterial,
                    ExtractedSlicerName: file.SlicerName,
                    ExtractedSlicerVersion: file.SlicerVersion,
                    ExtractedPrintTime: file.EstimatedPrintTimeMinutes,
                    ExtractedFilamentLength: file.EstimatedFilamentLengthMm,
                    ExtractedNozzleDiameter: file.RequiredNozzleDiameter,
                    ExtractedMaterial: file.RequiredMaterial,
                    ExtractedPrinterModel: file.TargetModel?.Name,
                    ExtractedHotendTemp: file.PrintTemperature,
                    ExtractedBedTemp: file.BedTemperature
                ));
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
            IReadOnlyList<GcodeFileEntryDto> pagedEntries = skip >= entries.Count ? Array.Empty<GcodeFileEntryDto>() : entries.Skip(skip).Take(pageSize).ToList();
            int totalItems = entries.Count;
            int totalPages = (int)Math.Ceiling(totalItems / (double)pageSize);

            return new GcodeFileListResponse(pagedEntries, totalFiles, totalSize, page, pageSize, totalPages, totalItems);
        }

        /// <summary>
        /// List G-code files with hierarchy support, including directoryId and gcodeFileId for efficient lookups.
        /// This is the hierarchical variant used by ExplorerFileBrowser and other tree-based navigation UIs.
        /// </summary>
        public async Task<GcodeFileListResponse> ListFilesWithHierarchyAsync(
            string? path,
            string? sortBy,
            string? sortOrder,
            string? search,
            int page,
            int pageSize,
            CancellationToken ct)
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

            // Get all files and subdirectories from database for this directory
            List<GcodeFile> dbFiles = await _gcodeRepo.ListValidByDirectoryAsync(requestedDir, ct);
            List<string> subdirectories = await _gcodeRepo.ListSubdirectoriesAsync(requestedDir, ct);

            // Build directory entries with IDs
            List<GcodeFileEntryDto> entries = new();

            // Add directories with directoryId (virtual path)
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
                entries.Add(new GcodeFileEntryDto(
                    Path: childVirtual,
                    FileName: subdir,
                    Size: 0,
                    ModifiedAt: DateTime.UtcNow,
                    IsDirectory: true,
                    HarvestOperationId: null,
                    ThumbnailPath: null,
                    GcodeFileId: null,
                    DirectoryId: childVirtual,  // Virtual path is the directory ID
                    TargetModelName: null,  // Directories don't have printer model
                    RequiredMaterial: null  // Directories don't have material
                ));
            }

            // Add files with gcodeFileId (GUID) and thumbnail URL
            foreach (var file in dbFiles)
            {
                if (!IsMatch(file.FileName, search))
                {
                    continue;
                }

                string childVirtual = CombineVirtual(virtualPathNormalized, file.FileName);

                // Convert thumbnail path to API URL if available
                string? thumbnailUrl = null;
                if (!string.IsNullOrEmpty(file.ThumbnailFileName))
                {
                    string thumbnailFullPath = Path.Combine(file.FilePath, file.ThumbnailFileName);
                    string gcodeStorageDir = _storagePathService.GetGcodeStorageDirectory();
                    string normalizedStorageDir = Path.GetFullPath(gcodeStorageDir);
                    string normalizedThumbnailPath = Path.GetFullPath(thumbnailFullPath);

                    if (normalizedThumbnailPath.StartsWith(normalizedStorageDir, StringComparison.Ordinal))
                    {
                        string relativePath = normalizedThumbnailPath.Substring(normalizedStorageDir.Length)
                            .TrimStart(Path.DirectorySeparatorChar, '/');
                        relativePath = relativePath.Replace(Path.DirectorySeparatorChar, '/');
                        thumbnailUrl = $"/api/gcode-files/download?path={Uri.EscapeDataString(relativePath)}";
                    }
                    else
                    {
                        thumbnailUrl = $"/api/gcode-files/download?path={Uri.EscapeDataString(file.ThumbnailFileName)}";
                    }
                }

                entries.Add(new GcodeFileEntryDto(
                    Path: childVirtual,
                    FileName: file.FileName,
                    Size: file.FileSizeBytes,
                    ModifiedAt: file.UploadedAt,
                    IsDirectory: false,
                    HarvestOperationId: null,
                    ThumbnailPath: thumbnailUrl,
                    GcodeFileId: file.Id.ToString(),  // GUID as string for file ID
                    DirectoryId: null,
                    TargetModelName: file.TargetModel?.Name,  // Include printer model name
                    RequiredMaterial: file.RequiredMaterial,  // Include required filament type
                    ExtractedSlicerName: file.SlicerName,
                    ExtractedSlicerVersion: file.SlicerVersion,
                    ExtractedPrintTime: file.EstimatedPrintTimeMinutes,
                    ExtractedFilamentLength: file.EstimatedFilamentLengthMm,
                    ExtractedNozzleDiameter: file.RequiredNozzleDiameter,
                    ExtractedMaterial: file.RequiredMaterial,
                    ExtractedPrinterModel: file.TargetModel?.Name,
                    ExtractedHotendTemp: file.PrintTemperature,
                    ExtractedBedTemp: file.BedTemperature
                ));
            }

            // Apply sorting
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

            // Apply pagination
            int totalFiles = entries.Count(e => !e.IsDirectory);
            long totalSize = entries.Where(e => !e.IsDirectory).Sum(e => e.Size);
            int skip = (page - 1) * pageSize;
            IReadOnlyList<GcodeFileEntryDto> pagedEntries = skip >= entries.Count
                ? Array.Empty<GcodeFileEntryDto>()
                : entries.Skip(skip).Take(pageSize).ToList();
            int totalItems = entries.Count;
            int totalPages = (int)Math.Ceiling(totalItems / (double)pageSize);

            return new GcodeFileListResponse(pagedEntries, totalFiles, totalSize, page, pageSize, totalPages, totalItems);
        }

        /// <summary>
        /// Uploads a single G-code file to the specified virtual directory with automatic metadata and thumbnail extraction.
        /// </summary>
        /// <param name="path">Virtual directory path where the file should be uploaded</param>
        /// <param name="file">File to upload (IFormFile from multipart/form-data request)</param>
        /// <param name="uploadSettings">Upload settings including allowed extensions</param>
        /// <param name="quotaService">Quota service for tracking upload limits</param>
        /// <param name="ct">Cancellation token</param>
        /// <returns>Metadata about the uploaded file including virtual path and size</returns>
        /// <exception cref="InvalidOperationException">Thrown when file type is not allowed or path is unsafe</exception>
        /// <remarks>
        /// The upload process:
        /// 1. Validates file extension against allowed extensions
        /// 2. Saves file to storage with GUID-based filename
        /// 3. Extracts metadata (print time, material, slicer info)
        /// 4. Extracts and saves thumbnail if present
        /// 5. Creates database record with all metadata
        /// 6. Associates file with virtual folder
        /// </remarks>
        public async Task<GcodeFileEntryDto> UploadFileAsync(string? path, IFormFile file, IGcodeUploadSettings uploadSettings, Farm.Web.Api.Services.IGcodeUploadQuotaService quotaService, CancellationToken ct)
        {
            string ext = Path.GetExtension(file.FileName) ?? string.Empty;
            if (!uploadSettings.AllowedExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Invalid file type '{ext}'");
            }

            // Resolve path using IStoragePathService
            (string storageRoot, string targetDirFullPath, string virtualDir) = ResolveVirtualPath(path, _storagePathService.GetGcodeStorageDirectory());

            if (!Directory.Exists(targetDirFullPath))
            {
                _ = Directory.CreateDirectory(targetDirFullPath);
            }

            string originalName = Path.GetFileName(file.FileName);
            if (string.IsNullOrWhiteSpace(originalName))
            {
                originalName = "upload.gcode";
            }

            string safeName = SanitizeFileName(originalName, ext);

            string destinationPath = Path.Combine(targetDirFullPath, safeName);
            string fullTarget = Path.GetFullPath(destinationPath);
            if (!fullTarget.StartsWith(targetDirFullPath, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Unsafe target path");
            }

            if (File.Exists(fullTarget))
            {
                string baseName = Path.GetFileNameWithoutExtension(safeName);
                int counter = 1;
                do
                {
                    string candidate = baseName + " (" + counter++ + ")" + ext;
                    fullTarget = Path.GetFullPath(Path.Combine(targetDirFullPath, candidate));
                } while (File.Exists(fullTarget));
                safeName = Path.GetFileName(fullTarget);
            }

            await using FileStream fs = new(fullTarget, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            await file.CopyToAsync(fs, ct);
            await fs.FlushAsync(ct);  // Ensure all bytes are written before reading
            fs.Position = 0;  // Reset position

            System.IO.FileInfo info = new(fullTarget);

            // Create database record with metadata and thumbnail extraction
            // This will rename the file to a GUID-based name
            GcodeFile gcodeFile;
            try
            {
                gcodeFile = await CreateGcodeFileRecordAsync(fullTarget, file.FileName, info.Length, ext, virtualDir, ct);
                await _gcodeRepo.AddAsync(gcodeFile, ct);
                await _gcodeRepo.SaveChangesAsync(ct);
                _logger.LogInformation("Created GcodeFile database record for {FileName} with ID {FileId}", file.FileName, gcodeFile.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create GcodeFile database record for {FileName}, but file was uploaded successfully", file.FileName);
                throw;
            }

            // Return the virtual path using the display name (original filename), not the GUID
            string virtualFilePath = CombineVirtual(virtualDir, gcodeFile.FileName);
            return new GcodeFileEntryDto(virtualFilePath, gcodeFile.FileName, gcodeFile.FileSizeBytes, info.LastWriteTimeUtc, false);
        }

        /// <summary>
        /// Finalize a chunked upload by creating a GcodeFile database record with extracted metadata.
        /// This is called after a chunked upload completes to index the file in the database.
        /// </summary>
        public async Task<GcodeFile?> FinalizeChunkedUploadAsync(
            string filePath,
            string? originalFileName,
            string? thumbnailPath,
            string? virtualDirectory,
            IChunkedUploadService chunkedUploadService,
            CancellationToken ct)
        {
            try
            {
                _logger.LogInformation("FinalizeChunkedUploadAsync: Starting for {FileName}, received thumbnailPath={ThumbnailPath}", originalFileName, thumbnailPath ?? "(null)");

                if (!File.Exists(filePath))
                {
                    _logger.LogWarning("Cannot finalize chunked upload: file not found at {FilePath}", filePath);
                    return null;
                }

                // Extract metadata from the uploaded file
                GcodeMetadataExtracted? metadata = await chunkedUploadService.ExtractMetadataFromFileAsync(filePath, ct);
                _logger.LogInformation("FinalizeChunkedUploadAsync: Metadata extracted for {FileName}", originalFileName);

                // Get file info
                FileInfo fileInfo = new(filePath);
                string fullFileName = originalFileName ?? fileInfo.Name;
                string fileExtension = Path.GetExtension(fullFileName) ?? ".gcode";

                // Compute file hash
                string fileHash;
                await using (FileStream fs = System.IO.File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (SHA256 sha256 = SHA256.Create())
                {
                    byte[] hashBytes = await sha256.ComputeHashAsync(fs, ct);
                    fileHash = Convert.ToHexString(hashBytes);
                }

                // Generate GUID for file ID (used for all file names)
                Guid fileId = Guid.NewGuid();

                // Normalize virtual directory path
                string normalizedVirtualDir = NormalizeVirtualPath(virtualDirectory ?? "/");

                // Get or create target folder
                var targetFolder = await _folderService.GetOrCreateFolderAsync(normalizedVirtualDir, "gcode", ct);

                // Create database record
                GcodeFile gcodeFile = new()
                {
                    Id = fileId,
                    FileName = fullFileName,  // Store original filename with extension
                    FilePath = Path.GetDirectoryName(filePath) ?? _storagePathService.GetGcodeStorageDirectory(),
                    FolderId = targetFolder.Id,
                    FileSizeBytes = fileInfo.Length,
                    FileHash = fileHash,
                    UploadedAt = DateTime.UtcNow,
                    Source = GcodeSource.Upload,
                    RequiredNozzleDiameter = metadata?.NozzleDiameter,
                    RequiredMaterial = metadata?.Material,
                    EstimatedPrintTimeMinutes = metadata?.EstimatedPrintTimeMinutes,
                    EstimatedFilamentLengthMm = metadata?.FilamentLengthMm,
                    EstimatedFilamentWeightG = metadata?.FilamentWeightGrams,
                    SlicerName = metadata?.SlicerName,
                    SlicerVersion = metadata?.SlicerVersion,
                    ThumbnailFileName = thumbnailPath != null ? Path.GetFileName(thumbnailPath) : null,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };

                // Move uploaded file to GUID-based name before saving to DB
                string storageDir = Path.GetDirectoryName(filePath) ?? _storagePathService.GetGcodeStorageDirectory();
                string finalFilePath = Path.Combine(storageDir, $"{fileId}{fileExtension}");

                if (filePath != finalFilePath && File.Exists(filePath))
                {
                    File.Move(filePath, finalFilePath, overwrite: true);
                    gcodeFile.FileName = $"{fileId}{fileExtension}";  // Store GUID-based filename
                    _logger.LogInformation("Moved file from {SourcePath} to {FinalPath}", filePath, finalFilePath);
                }

                // Rename thumbnail to match file ID with _thumb.png suffix
                if (!string.IsNullOrEmpty(thumbnailPath) && File.Exists(thumbnailPath))
                {
                    string finalThumbnailPath = Path.Combine(storageDir, $"{fileId}_thumb.png");
                    if (thumbnailPath != finalThumbnailPath)
                    {
                        File.Move(thumbnailPath, finalThumbnailPath, overwrite: true);
                        gcodeFile.ThumbnailFileName = Path.GetFileName(finalThumbnailPath);  // Store just filename, matching standardized pattern
                        _logger.LogInformation("Moved thumbnail from {SourcePath} to {FinalPath}", thumbnailPath, finalThumbnailPath);
                    }
                    else
                    {
                        gcodeFile.ThumbnailFileName = Path.GetFileName(finalThumbnailPath);  // Ensure filename is stored even if no move needed
                    }
                }

                await _gcodeRepo.AddAsync(gcodeFile, ct);
                await _gcodeRepo.SaveChangesAsync(ct);

                _logger.LogInformation("Finalized chunked upload as GcodeFile database record for {FileName} with ID {FileId}", fullFileName, fileId);
                return gcodeFile;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to finalize chunked upload to database at {FilePath}", filePath);
                return null;
            }
        }

        /// <summary>
        /// Uploads multiple G-code files in a single operation with individual error handling per file.
        /// </summary>
        /// <param name="path">Virtual directory path where files should be uploaded</param>
        /// <param name="files">Collection of files to upload</param>
        /// <param name="uploadSettings">Upload settings including allowed extensions</param>
        /// <param name="quotaService">Quota service for tracking upload limits</param>
        /// <param name="ct">Cancellation token</param>
        /// <returns>Response containing lists of successfully uploaded and failed files</returns>
        /// <remarks>
        /// This method processes each file independently, so partial success is possible.
        /// Failed uploads are captured with error messages without stopping the entire operation.
        /// </remarks>
        public async Task<MultiUploadResponse> UploadMultipleFilesAsync(string? path, IFormFileCollection files, IGcodeUploadSettings uploadSettings, Farm.Web.Api.Services.IGcodeUploadQuotaService quotaService, CancellationToken ct)
        {
            List<GcodeFileEntryDto> created = new();
            List<MultiUploadFailure> failed = new();

            // Resolve path using IStoragePathService
            (_, string targetDirFullPath, string virtualDir) = ResolveVirtualPath(path, _storagePathService.GetGcodeStorageDirectory());

            if (!Directory.Exists(targetDirFullPath))
            {
                _ = Directory.CreateDirectory(targetDirFullPath);
            }

            foreach (IFormFile? f in files)
            {
                try
                {
                    if (f == null || f.Length == 0)
                    {
                        failed.Add(new MultiUploadFailure(SafeOriginalName(f?.FileName), "Empty file"));
                        continue;
                    }
                    string ext = Path.GetExtension(f.FileName) ?? string.Empty;
                    if (!uploadSettings.AllowedExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
                    {
                        failed.Add(new MultiUploadFailure(SafeOriginalName(f.FileName), $"Invalid file type '{ext}'"));
                        continue;
                    }

                    (string? fullTarget, string? safeName) = await SaveUploadedFileAsync(f, targetDirFullPath, ct);
                    System.IO.FileInfo info = new(fullTarget);
                    string virtualFilePath = CombineVirtual(virtualDir, safeName);
                    created.Add(new GcodeFileEntryDto(virtualFilePath, safeName, info.Length, info.LastWriteTimeUtc, false));
                }
                catch (Exception exFile)
                {
                    _logger.LogWarning($"Failed to save uploaded file {f?.FileName}: {exFile.Message}");
                    failed.Add(new MultiUploadFailure(SafeOriginalName(f?.FileName), exFile.Message));
                }
            }

            return new MultiUploadResponse(created, failed, created.Count, failed.Count);
        }

        /// <summary>
        /// Creates a new virtual folder in the G-code library for organizational purposes.
        /// </summary>
        /// <param name="path">Parent virtual directory path</param>
        /// <param name="name">Name of the new folder</param>
        /// <param name="ct">Cancellation token</param>
        /// <returns>Metadata about the created folder</returns>
        /// <exception cref="ArgumentException">Thrown when name is empty or contains invalid characters</exception>
        /// <remarks>
        /// This creates a virtual folder that exists only in the database (not on disk).
        /// Virtual folders are used for organizing files without creating physical directories.
        /// Physical files remain in a flat storage structure with GUID-based names.
        /// </remarks>
        public async Task<GcodeFileEntryDto> MakeDirectoryAsync(string? path, string? name, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("name is required");
            }
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains('/') || name.Contains('\n') || name.Contains('\r'))
            {
                throw new ArgumentException("Invalid directory name");
            }

            // Resolve virtual path
            string virtualDir = string.IsNullOrWhiteSpace(path) || path == "/" ? "/" : path.Trim();
            if (!virtualDir.StartsWith('/'))
            {
                virtualDir = "/" + virtualDir;
            }

            // Create virtual folder path
            string folderPath = CombineVirtual(virtualDir, name);

            // Track the folder in the database (virtual organization, not physical directories)
            Folder folder = await _folderService.GetOrCreateFolderAsync(folderPath, "gcode", ct);
            _logger.LogInformation($"[MakeDirectory] Created virtual folder in database: {folderPath}");

            GcodeFileEntryDto dto = new(
                Path: folderPath,
                FileName: name,
                Size: 0,
                ModifiedAt: folder.CreatedAt,
                IsDirectory: true
            );
            return dto;
        }

        /// <summary>
        /// Moves a G-code file to a different virtual folder by updating its database folder reference.
        /// </summary>
        /// <param name="fileId">GUID of the file to move</param>
        /// <param name="targetFolderPath">Virtual path of the destination folder</param>
        /// <param name="ct">Cancellation token</param>
        /// <returns>True if the file was successfully moved; false if file was not found</returns>
        /// <remarks>
        /// This is a virtual move operation that only updates the file's FolderId reference in the database.
        /// The physical file remains in its original location on disk with its GUID-based filename.
        /// Target folder is created automatically if it doesn't exist.
        /// </remarks>
        public async Task<bool> MoveToFolderAsync(Guid fileId, string targetFolderPath, CancellationToken ct)
        {
            try
            {
                // Get the file from database (with includes)
                var gcodeFile = await _gcodeRepo.GetByIdWithIncludesAsync(fileId, ct);
                if (gcodeFile == null)
                {
                    _logger.LogWarning($"[MoveToFolder] File not found: {fileId}");
                    return false;
                }

                // Get or create the target folder
                var targetFolder = await _folderService.GetOrCreateFolderAsync(targetFolderPath, "gcode", ct);

                // Update the folder reference (virtual move - physical file stays in place)
                gcodeFile.FolderId = targetFolder.Id;
                
                // Save changes to database
                await _gcodeRepo.SaveChangesAsync(ct);

                _logger.LogInformation($"[MoveToFolder] Moved file {gcodeFile.FileName} to folder {targetFolderPath}");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError($"[MoveToFolder] Failed to move file {fileId}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Deletes one or more G-code files or directories from the virtual file system.
        /// </summary>
        /// <param name="virtualPaths">Collection of virtual paths to delete</param>
        /// <param name="recursive">If true, recursively deletes directories and their contents</param>
        /// <param name="ct">Cancellation token</param>
        /// <returns>True if at least one file was deleted successfully; false otherwise</returns>
        /// <remarks>
        /// This method resolves virtual paths to physical locations and deletes the actual files.
        /// Failed deletions are logged but don't stop the operation - partial success is possible.
        /// Database records should be cleaned up separately (not handled by this method).
        /// </remarks>
        public Task<bool> DeleteFilesAsync(IEnumerable<string> virtualPaths, bool recursive, CancellationToken ct)
        {
            string storageRoot = _storagePathService.GetGcodeStorageDirectory();
            int deleted = 0;

            foreach (string virtualPath in virtualPaths)
            {
                try
                {
                    // Resolve path using IStoragePathService
                    (_, string fullFilePath, _) = ResolveVirtualPath(virtualPath, storageRoot);

                    if (Directory.Exists(fullFilePath))
                    {
                        if (recursive)
                        {
                            Directory.Delete(fullFilePath, true);
                            deleted++;
                        }
                    }
                    else if (File.Exists(fullFilePath))
                    {
                        File.Delete(fullFilePath);
                        deleted++;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"Failed to delete file {virtualPath}: {ex.Message}");
                }
            }

            return Task.FromResult(deleted > 0);
        }

        /// <summary>
        /// Downloads a G-code file by reading it from disk and returning the bytes and filename.
        /// </summary>
        /// <param name="path">Virtual path to the file</param>
        /// <param name="ct">Cancellation token</param>
        /// <returns>Tuple of file bytes and filename, or null if file not found</returns>
        /// <remarks>
        /// This method resolves the virtual path to the physical file location and reads the entire file into memory.
        /// For large files, consider streaming instead of reading all bytes at once.
        /// </remarks>
        public async Task<(byte[] bytes, string fileName)?> DownloadAsync(string path, CancellationToken ct)
        {
            // Resolve path using IStoragePathService
            (string storageRoot, string fullFilePath, _) = ResolveVirtualPath(path, _storagePathService.GetGcodeStorageDirectory());

            if (!File.Exists(fullFilePath))
            {
                return null;
            }

            byte[] bytes = await File.ReadAllBytesAsync(fullFilePath, ct);
            string fileName = Path.GetFileName(fullFilePath);
            return (bytes, fileName);
        }

        /// <summary>
        /// Moves or renames a file or directory from one virtual path to another (physical file move).
        /// </summary>
        /// <param name="sourcePath">Virtual source path</param>
        /// <param name="destinationPath">Virtual destination path</param>
        /// <param name="overwrite">If true, overwrites existing files at destination</param>
        /// <param name="ct">Cancellation token</param>
        /// <returns>Tuple indicating success, final virtual path, and whether it's a directory</returns>
        /// <exception cref="InvalidOperationException">Thrown when destination exists and overwrite is false, or when trying to overwrite a directory</exception>
        /// <remarks>
        /// This performs a physical file/directory move operation on disk.
        /// For virtual folder moves (database-only), use MoveToFolderAsync instead.
        /// </remarks>
        public Task<(bool ok, string virtualPath, bool isDirectory)> MoveAsync(string sourcePath, string destinationPath, bool overwrite, CancellationToken ct)
        {
            string storageRoot = _storagePathService.GetGcodeStorageDirectory();

            // Resolve source path using IStoragePathService
            (_, string sourceFull, _) = ResolveVirtualPath(sourcePath, storageRoot);

            if (!File.Exists(sourceFull) && !Directory.Exists(sourceFull))
            {
                return Task.FromResult((false, string.Empty, false));
            }

            // Resolve destination path using IStoragePathService
            (_, string destFull, string destVirtual) = ResolveVirtualPath(destinationPath, storageRoot);

            bool isDirectory = Directory.Exists(sourceFull);
            bool destExistsFile = File.Exists(destFull);
            bool destExistsDir = Directory.Exists(destFull);

            if ((destExistsFile || destExistsDir) && !overwrite)
            {
                throw new InvalidOperationException("Destination already exists");
            }

            if (destExistsFile)
            {
                File.Delete(destFull);
            }

            if (destExistsDir && !isDirectory)
            {
                throw new InvalidOperationException("Destination directory exists");
            }

            if (isDirectory)
            {
                if (destExistsDir)
                {
                    throw new InvalidOperationException("Destination directory exists (cannot overwrite)");
                }
                Directory.Move(sourceFull, destFull);
            }
            else
            {
                _ = Directory.CreateDirectory(Path.GetDirectoryName(destFull)!);
                File.Move(sourceFull, destFull, overwrite: overwrite);
            }

            return Task.FromResult((true, destVirtual, isDirectory));
        }

        /// <summary>
        /// Retrieves current upload settings and quota information for a specific user.
        /// </summary>
        /// <param name="userId">User identifier for quota lookup</param>
        /// <param name="uploadSettings">Upload settings service</param>
        /// <param name="quotaService">Quota service for retrieving usage limits</param>
        /// <param name="ct">Cancellation token</param>
        /// <returns>Settings including allowed extensions, daily limit, and current usage</returns>
        /// <remarks>
        /// This method combines configuration settings with per-user quota information
        /// to provide a complete view of upload capabilities and restrictions.
        /// </remarks>
        public Task<GcodeUploadSettingsResponse> GetSettingsAsync(string userId, IGcodeUploadSettings uploadSettings, IGcodeUploadQuotaService quotaService, CancellationToken ct)
        {
            long used = 0;
            long limit = 0;
            _ = quotaService.TryAddUsage(userId, 0, out used, out limit);
            return Task.FromResult(new GcodeUploadSettingsResponse(uploadSettings.AllowedExtensions, limit, used));
        }

        #region Helper Methods

        /// <summary>
        /// Checks if a filename matches the search term (case-insensitive).
        /// </summary>
        /// <param name="name">Filename to check</param>
        /// <param name="search">Search term (null or empty means match all)</param>
        /// <returns>True if name matches search or search is empty; false otherwise</returns>
        private static bool IsMatch(string name, string? search)
            => string.IsNullOrWhiteSpace(search) || name.Contains(search, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Combines a base virtual path with a child name to create a full virtual path.
        /// </summary>
        /// <param name="baseVirtual">Base virtual path (e.g., '/' or '/folder')</param>
        /// <param name="childName">Child filename or folder name</param>
        /// <returns>Combined virtual path with proper separator handling</returns>
        private static string CombineVirtual(string? baseVirtual, string childName)
        {
            if (baseVirtual == "/")
            {
                return "/" + childName;
            }

            return UrlNormalizer.CombineUrl(baseVirtual ?? "/", childName);
        }

        /// <summary>
        /// Normalizes a virtual path by removing leading/trailing slashes and handling the root directory.
        /// </summary>
        /// <param name="path">Virtual path to normalize</param>
        /// <returns>Normalized path (empty string for root, no leading/trailing slashes otherwise)</returns>
        private static string NormalizeVirtualPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || path == "/")
            {
                return string.Empty; // Root directory is stored as empty string
            }

            // Extract path segments, remove leading slashes
            string normalizedPath = path.Trim();
            if (normalizedPath.StartsWith('/'))
            {
                normalizedPath = normalizedPath[1..];
            }
            if (normalizedPath.EndsWith('/'))
            {
                normalizedPath = normalizedPath[..^1];
            }

            return normalizedPath;
        }

        /// <summary>
        /// Safely extracts the original filename from a path, returning a default if invalid.
        /// </summary>
        /// <param name="name">Filename or path</param>
        /// <returns>Safe filename or "(unnamed)" if null/empty</returns>
        private static string SafeOriginalName(string? name)
            => string.IsNullOrWhiteSpace(name) ? "(unnamed)" : Path.GetFileName(name);

        /// <summary>
        /// Sanitizes a filename by replacing invalid characters with underscores and ensuring proper extension.
        /// </summary>
        /// <param name="originalName">Original filename to sanitize</param>
        /// <param name="ext">File extension to ensure (e.g., ".gcode")</param>
        /// <returns>Sanitized filename safe for filesystem use</returns>
        private static string SanitizeFileName(string originalName, string ext)
        {
            string safeName = originalName;
            foreach (char c in Path.GetInvalidFileNameChars())
            {
                safeName = safeName.Replace(c, '_');
            }
            if (!safeName.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            {
                safeName += ext;
            }
            return safeName;
        }

        /// <summary>
        /// Saves an uploaded file to disk with automatic name collision resolution.
        /// </summary>
        /// <param name="file">File to save</param>
        /// <param name="targetDirFullPath">Physical target directory path</param>
        /// <param name="ct">Cancellation token</param>
        /// <returns>Tuple of full file path and safe filename</returns>
        /// <exception cref="InvalidOperationException">Thrown when the resolved path is unsafe (directory traversal attempt)</exception>
        /// <remarks>
        /// If a file with the same name exists, appends " (N)" before the extension.
        /// Performs security checks to prevent directory traversal attacks.
        /// </remarks>
        private static async Task<(string fullTargetPath, string safeName)> SaveUploadedFileAsync(IFormFile file, string targetDirFullPath, CancellationToken ct)
        {
            string ext = Path.GetExtension(file.FileName) ?? string.Empty;
            string originalName = Path.GetFileName(file.FileName);
            if (string.IsNullOrWhiteSpace(originalName))
            {
                originalName = "upload" + ext;
            }

            string safeName = SanitizeFileName(originalName, ext);
            string destinationPath = Path.Combine(targetDirFullPath, safeName);
            string fullTarget = Path.GetFullPath(destinationPath);

            if (!fullTarget.StartsWith(targetDirFullPath, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Unsafe target path");
            }

            if (File.Exists(fullTarget))
            {
                string baseName = Path.GetFileNameWithoutExtension(safeName);
                int counter = 1;
                do
                {
                    string candidate = baseName + " (" + counter++ + ")" + ext;
                    fullTarget = Path.GetFullPath(Path.Combine(targetDirFullPath, candidate));
                } while (File.Exists(fullTarget));
                safeName = Path.GetFileName(fullTarget);
            }

            await using FileStream fs = new(fullTarget, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await file.CopyToAsync(fs, ct);
            return (fullTarget, safeName);
        }

        /// <summary>
        /// Helper method to resolve and validate virtual paths consistently throughout the service.
        /// Centralizes path security logic to prevent directory traversal attacks.
        /// </summary>
        private static (string storageRoot, string resolvedFullPath, string virtualNormalized) ResolveVirtualPath(
            string? virtualPath,
            string storageRoot)
        {
            // Normalize incoming virtual path
            string vPath = string.IsNullOrWhiteSpace(virtualPath) ? "/" : virtualPath.Trim();
            if (!vPath.StartsWith('/'))
            {
                vPath = "/" + vPath;
            }

            // Collapse .. segments and remove . segments
            string[] segments = vPath.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Where(s => s != "." && s != "..")
                .ToArray();

            string safeRel = segments.Length == 0 ? string.Empty : Path.Combine(segments);
            string candidate = Path.GetFullPath(Path.Combine(storageRoot, safeRel));

            // Security check: ensure path doesn't escape the storage root
            if (!candidate.StartsWith(storageRoot, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Path escapes storage root");
            }

            string virtualNormalized = segments.Length == 0 ? "/" : "/" + string.Join('/', segments);

            return (storageRoot, candidate, virtualNormalized);
        }

        /// <summary>
        /// Extract metadata from a G-code file by reading its content.
        /// Handles errors gracefully and returns null if extraction fails.
        /// </summary>
        private async Task<GcodeMetadataExtracted?> ExtractMetadataAsync(string filePath, CancellationToken ct)
        {
            try
            {
                if (!File.Exists(filePath))
                {
                    return null;
                }

                using StreamReader reader = new(filePath, Encoding.UTF8);
                string gcodeContent = await reader.ReadToEndAsync(ct);

                if (string.IsNullOrWhiteSpace(gcodeContent))
                {
                    return null;
                }

                return await _metadataExtractor.ExtractMetadataAsync(gcodeContent);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to extract metadata from gcode file {FilePath}", filePath);
                return null;
            }
        }

        /// <summary>
        /// Extract and save a thumbnail from a G-code file.
        /// Handles errors gracefully and returns null if extraction fails.
        /// </summary>
        private async Task<string?> ExtractThumbnailAsync(string filePath, CancellationToken ct)
        {
            try
            {
                if (!File.Exists(filePath))
                {
                    return null;
                }

                await using FileStream fs = new(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                return await _thumbnailExtractor.ExtractAndSaveThumbnailAsync(fs, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to extract thumbnail from gcode file {FilePath}", filePath);
                return null;
            }
        }

        /// <summary>
        /// Create a GcodeFile database record from an uploaded file with metadata and thumbnail extraction.
        /// </summary>
        private async Task<GcodeFile> CreateGcodeFileRecordAsync(
            string filePath,
            string originalFileName,
            long fileSizeBytes,
            string fileExtension,
            string? virtualDirectory,
            CancellationToken ct)
        {
            _logger.LogInformation("CreateGcodeFileRecordAsync: Starting for {FileName} at {FilePath}", originalFileName, filePath);

            // Compute file hash
            string fileHash;
            await using (FileStream fs = System.IO.File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (SHA256 sha256 = SHA256.Create())
            {
                byte[] hashBytes = await sha256.ComputeHashAsync(fs, ct);
                fileHash = Convert.ToHexString(hashBytes);
            }

            // Generate GUID for file ID
            Guid fileId = Guid.NewGuid();

            // Extract metadata and thumbnail
            GcodeMetadataExtracted? metadata = await ExtractMetadataAsync(filePath, ct);
            _logger.LogInformation("CreateGcodeFileRecordAsync: Metadata extracted for {FileName}", originalFileName);

            string? thumbnailPath = await ExtractThumbnailAsync(filePath, ct);
            _logger.LogInformation("CreateGcodeFileRecordAsync: Thumbnail path for {FileName} is: {ThumbnailPath}", originalFileName, thumbnailPath ?? "(null)");

            // Rename file to GUID-based name
            string storageDir = Path.GetDirectoryName(filePath) ?? _storagePathService.GetGcodeStorageDirectory();
            string finalFilePath = Path.Combine(storageDir, $"{fileId}{fileExtension}");

            if (filePath != finalFilePath && File.Exists(filePath))
            {
                File.Move(filePath, finalFilePath, overwrite: true);
                _logger.LogInformation("Renamed file from {SourcePath} to {FinalPath}", filePath, finalFilePath);
            }

            // Rename thumbnail to match file ID with _thumb.png suffix
            if (!string.IsNullOrEmpty(thumbnailPath) && File.Exists(thumbnailPath))
            {
                string finalThumbnailPath = Path.Combine(storageDir, $"{fileId}_thumb.png");
                if (thumbnailPath != finalThumbnailPath)
                {
                    File.Move(thumbnailPath, finalThumbnailPath, overwrite: true);
                    thumbnailPath = finalThumbnailPath;
                    _logger.LogInformation("Moved thumbnail from {SourcePath} to {FinalPath}", thumbnailPath, finalThumbnailPath);
                }
            }

            // Get or create target folder
            string normalizedVirtualDir = NormalizeVirtualPath(virtualDirectory ?? "/");
            var targetFolder = await _folderService.GetOrCreateFolderAsync(normalizedVirtualDir, "gcode", ct);

            // Create database record
            GcodeFile gcodeFile = new()
            {
                Id = fileId,
                FileName = $"{fileId}{fileExtension}",
                FolderId = targetFolder.Id,
                FilePath = storageDir,
                FileSizeBytes = fileSizeBytes,
                FileHash = fileHash,
                UploadedAt = DateTime.UtcNow,
                Source = GcodeSource.Upload,
                RequiredNozzleDiameter = metadata?.NozzleDiameter,
                RequiredMaterial = metadata?.Material,
                EstimatedPrintTimeMinutes = metadata?.EstimatedPrintTimeMinutes,
                EstimatedFilamentLengthMm = metadata?.FilamentLengthMm,
                EstimatedFilamentWeightG = metadata?.FilamentWeightGrams,
                SlicerName = metadata?.SlicerName,
                SlicerVersion = metadata?.SlicerVersion,
                ThumbnailFileName = thumbnailPath != null ? Path.GetFileName(thumbnailPath) : null,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            return gcodeFile;
        }

        #endregion
    }
}
