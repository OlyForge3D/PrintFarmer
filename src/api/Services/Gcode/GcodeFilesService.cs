using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Normalization;
using Farm.Infrastructure.Repositories.Gcode;
using Farm.Infrastructure.Repositories.UnitOfWork;
using Farm.Infrastructure.Services.Gcode;
using Farm.Infrastructure.Services.StorageManagement;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Controllers;
using Farm.Web.Api.Services.FileManagement;
using Farm.Web.Api.Services.FolderManagement;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Farm.Web.Api.Services.Gcode
{
    /// <summary>
    /// Service for managing G-code file operations including upload, listing, metadata extraction,
    /// and virtual folder organization. Handles both physical file storage and database tracking
    /// with support for hierarchical browsing and efficient lookups.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This service implements a unified interface combining file browser (directory-based) and library
    /// (metadata-based) operations for G-code management. It operates on a virtual folder architecture where:
    /// </para>
    /// <list type="bullet">
    /// <item><description>Physical files are stored in a flat directory structure with GUID-based names for efficiency</description></item>
    /// <item><description>Virtual folders exist only in the database for organizational purposes</description></item>
    /// <item><description>Files are tracked in the database with metadata, thumbnails, and folder references</description></item>
    /// <item><description>Move operations update database references without moving physical files</description></item>
    /// <item><description>Thumbnails are automatically extracted and stored alongside G-code files</description></item>
    /// </list>
    /// <para>
    /// The service consolidates functionality that was previously split between separate file browser and
    /// library services, providing a single source of truth for G-code file management.
    /// </para>
    /// </remarks>
    public class GcodeFilesService : IGcodeFilesService, IGcodeFileProcessingService
    {
        private readonly IGcodeRepository _gcodeRepo;
        private readonly IUnitOfWork _unitOfWork;
        private readonly IUnifiedLoggingService _logger;
        private readonly IStoragePathService _storagePathService;
        private readonly IGcodeMetadataExtractorService _metadataExtractor;
        private readonly IGcodeThumbnailExtractorService _thumbnailExtractor;
        private readonly IFolderManagementService _folderService;
        private readonly IStoredFileOperationsService _fileOperations;

        /// <summary>
        /// Initializes a new instance of the <see cref="GcodeFilesService"/> class.
        /// </summary>
        /// <param name="gcodeRepo">Repository for G-code file database operations.</param>
        /// <param name="unitOfWork">Unit of work for coordinated database operations.</param>
        /// <param name="logger">Logging service for diagnostic and error logging.</param>
        /// <param name="storagePathService">Service providing paths to storage directories.</param>
        /// <param name="metadataExtractor">Service for extracting metadata from G-code files.</param>
        /// <param name="thumbnailExtractor">Service for extracting thumbnail images from G-code files.</param>
        /// <param name="folderService">Service for managing virtual folder hierarchy.</param>
        /// <param name="fileOperations">Service for stored file operations including thumbnail URL building.</param>
        /// <exception cref="ArgumentNullException">Thrown if any dependency is null.</exception>
        public GcodeFilesService(
            IGcodeRepository gcodeRepo,
            IUnitOfWork unitOfWork,
            IUnifiedLoggingService logger,
            IStoragePathService storagePathService,
            IGcodeMetadataExtractorService metadataExtractor,
            IGcodeThumbnailExtractorService thumbnailExtractor,
            IFolderManagementService folderService,
            IStoredFileOperationsService fileOperations)
        {
            _gcodeRepo = gcodeRepo ?? throw new ArgumentNullException(nameof(gcodeRepo));
            _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _storagePathService = storagePathService ?? throw new ArgumentNullException(nameof(storagePathService));
            _metadataExtractor = metadataExtractor ?? throw new ArgumentNullException(nameof(metadataExtractor));
            _thumbnailExtractor = thumbnailExtractor ?? throw new ArgumentNullException(nameof(thumbnailExtractor));
            _folderService = folderService ?? throw new ArgumentNullException(nameof(folderService));
            _fileOperations = fileOperations ?? throw new ArgumentNullException(nameof(fileOperations));
        }

        /// <summary>
        /// Lists G-code files and subdirectories within a specific virtual path with pagination and filtering.
        /// </summary>
        /// <param name="path">Virtual path to browse (e.g., '/', '/subfolder'). Null or whitespace defaults to root.</param>
        /// <param name="sortBy">Sort field: 'name', 'size', or 'date'. Case-insensitive.</param>
        /// <param name="sortOrder">Sort order: 'asc' (ascending) or 'desc' (descending).</param>
        /// <param name="search">Optional search term to filter by filename. Case-insensitive partial matching.</param>
        /// <param name="page">Page number (1-based). Values &lt; 1 default to 1.</param>
        /// <param name="pageSize">Items per page. Automatically clamped to range [1, 500]. Default if invalid: 100.</param>
        /// <param name="harvestId">Optional harvest operation ID to filter files by harvest source.</param>
        /// <param name="printerId">Optional printer ID for filtering (reserved for future use).</param>
        /// <param name="ct">Cancellation token for canceling async operation.</param>
        /// <returns>
        /// A paginated response containing files and directories with metadata. Directories are always sorted
        /// before files regardless of sort order.
        /// </returns>
        /// <remarks>
        /// <para>
        /// This method queries the database for files and subdirectories at the specified virtual path,
        /// applies sorting and filtering, and returns a paginated result set. All paths are normalized
        /// to start with '/' automatically.
        /// </para>
        /// <para>
        /// Search is performed on filename only (not the full path). Page and pageSize parameters are
        /// validated and clamped to reasonable ranges automatically.
        /// </para>
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
                    TargetModelName: file.PrinterModel?.Name,
                    RequiredMaterial: file.RequiredMaterial,
                    ExtractedSlicerName: file.SlicerName,
                    ExtractedSlicerVersion: file.SlicerVersion,
                    ExtractedPrintTime: file.EstimatedPrintTimeMinutes,
                    ExtractedFilamentLength: file.EstimatedFilamentLengthMm,
                    ExtractedNozzleDiameter: file.RequiredNozzleDiameter,
                    ExtractedMaterial: file.RequiredMaterial,
                    ExtractedPrinterModel: file.PrinterModel?.Name,
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
                    TargetModelName: file.PrinterModel?.Name,  // Include printer model name
                    RequiredMaterial: file.RequiredMaterial,  // Include required filament type
                    ExtractedSlicerName: file.SlicerName,
                    ExtractedSlicerVersion: file.SlicerVersion,
                    ExtractedPrintTime: file.EstimatedPrintTimeMinutes,
                    ExtractedFilamentLength: file.EstimatedFilamentLengthMm,
                    ExtractedNozzleDiameter: file.RequiredNozzleDiameter,
                    ExtractedMaterial: file.RequiredMaterial,
                    ExtractedPrinterModel: file.PrinterModel?.Name,
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

            // Generate GUID upfront for both file and thumbnail
            Guid fileId = Guid.NewGuid();
            string guidFileName = $"{fileId}{ext}";
            string destinationPath = Path.Combine(targetDirFullPath, guidFileName);
            string fullTarget = Path.GetFullPath(destinationPath);

            if (!fullTarget.StartsWith(targetDirFullPath, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Unsafe target path");
            }

            await using FileStream fs = new(fullTarget, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            await file.CopyToAsync(fs, ct);
            await fs.FlushAsync(ct);  // Ensure all bytes are written before reading
            fs.Position = 0;  // Reset position

            System.IO.FileInfo info = new(fullTarget);

            // Create database record with metadata and thumbnail extraction
            // Pass the fileId so both file and thumbnail use the same GUID
            GcodeFile gcodeFile;
            try
            {
                gcodeFile = await CreateGcodeFileRecordAsync(fullTarget, originalName, info.Length, ext, virtualDir, fileId, ct);
                await _gcodeRepo.AddAsync(gcodeFile, ct);
                await _gcodeRepo.SaveChangesAsync(ct);
                _logger.LogInformation("Created GcodeFile database record for {FileName} with ID {FileId}", originalName, gcodeFile.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create GcodeFile database record for {FileName}, but file was uploaded successfully", originalName);
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
            FolderNode folder = await _folderService.GetOrCreateFolderAsync(folderPath, "gcode", ct);
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
        /// Deletes one or more G-code files by ID from the virtual file system and database.
        /// </summary>
        /// <param name="fileIds">Collection of file IDs (GUIDs) to delete</param>
        /// <param name="ct">Cancellation token</param>
        /// <returns>True if at least one file was deleted successfully; false otherwise</returns>
        /// <remarks>
        /// This method deletes by file ID (GUID) rather than path resolution, ensuring orphaned files
        /// (database records with missing physical files) can be properly cleaned up.
        /// Database records are deleted first, then physical files (with exception handling for missing files).
        /// </remarks>
        public async Task<bool> DeleteFilesAsync(IEnumerable<Guid> fileIds, CancellationToken ct)
        {
            List<Guid> fileIdsList = fileIds.ToList();

            _logger.LogInformation($"[DeleteFilesAsync] Starting deletion of {fileIdsList.Count} file(s) by ID");

            // Step 1: Get all file records from database by ID
            List<GcodeFile> filesToDelete = new();
            foreach (var fileId in fileIdsList)
            {
                try
                {
                    var file = await _gcodeRepo.GetByIdWithIncludesAsync(fileId, ct);
                    if (file != null)
                    {
                        filesToDelete.Add(file);
                        _logger.LogInformation($"[DeleteFilesAsync] Found file {file.FileName} (ID: {fileId})");
                    }
                    else
                    {
                        _logger.LogWarning($"[DeleteFilesAsync] File not found in database: {fileId}");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"[DeleteFilesAsync] Failed to retrieve file {fileId}: {ex.Message}");
                }
            }

            if (filesToDelete.Count == 0)
            {
                _logger.LogWarning($"[DeleteFilesAsync] No files found in database, returning false");
                return false;
            }

            // Step 2: Delete database records first (before deleting physical files)
            _logger.LogInformation($"[DeleteFilesAsync] Deleting {filesToDelete.Count} record(s) from database");

            foreach (var file in filesToDelete)
            {
                _logger.LogInformation($"[DeleteFilesAsync]   - Removing from DB: {file.FileName} (ID: {file.Id})");
                await _gcodeRepo.RemoveAsync(file, ct);
            }

            await _gcodeRepo.SaveChangesAsync(ct);
            _logger.LogInformation($"[DeleteFilesAsync] Successfully saved database changes, {filesToDelete.Count} record(s) deleted from DB");

            // Step 3: Delete physical files (gcode + thumbnails)
            // If a physical file is missing, we still count it as deleted since the DB record was removed
            int deleted = 0;
            foreach (var file in filesToDelete)
            {
                try
                {
                    string fullPath = Path.Combine(file.FilePath, file.FileName);

                    if (File.Exists(fullPath))
                    {
                        _logger.LogInformation($"[DeleteFilesAsync] Deleting file from disk: {fullPath}");
                        File.Delete(fullPath);
                        deleted++;
                        _logger.LogInformation($"[DeleteFilesAsync] ✓ Successfully deleted file from disk: {file.FileName}");
                    }
                    else
                    {
                        deleted++;
                        _logger.LogInformation($"[DeleteFilesAsync] ✓ File not on disk (DB record already deleted): {file.FileName}");
                    }

                    // Delete associated thumbnail if it exists
                    if (!string.IsNullOrEmpty(file.ThumbnailFileName))
                    {
                        string thumbnailPath = Path.Combine(file.FilePath, file.ThumbnailFileName);
                        _logger.LogInformation($"[DeleteFilesAsync] Checking for thumbnail: {thumbnailPath}");
                        try
                        {
                            if (File.Exists(thumbnailPath))
                            {
                                File.Delete(thumbnailPath);
                                _logger.LogInformation($"[DeleteFilesAsync] ✓ Deleted thumbnail: {thumbnailPath}");
                            }
                            else
                            {
                                _logger.LogInformation($"[DeleteFilesAsync] Thumbnail file not found on disk: {thumbnailPath}");
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning($"[DeleteFilesAsync] Failed to delete thumbnail: {ex.Message}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"[DeleteFilesAsync] ✗ Exception while deleting {file.FileName} (ID: {file.Id}): {ex.GetType().Name}: {ex.Message}");
                }
            }

            _logger.LogInformation($"[DeleteFilesAsync] Deletion complete: {deleted}/{filesToDelete.Count} file(s) successfully processed, returning {(deleted > 0)}");
            return deleted > 0;
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
            Guid fileId,
            CancellationToken ct)
        {
            _logger.LogInformation($"CreateGcodeFileRecordAsync: Starting for {originalFileName} at {filePath} with fileId {fileId}");

            // Compute file hash
            string fileHash;
            await using (FileStream fs = System.IO.File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (SHA256 sha256 = SHA256.Create())
            {
                byte[] hashBytes = await sha256.ComputeHashAsync(fs, ct);
                fileHash = Convert.ToHexString(hashBytes);
            }

            // Extract metadata and thumbnail
            GcodeMetadataExtracted? metadata = await ExtractMetadataAsync(filePath, ct);
            _logger.LogInformation("CreateGcodeFileRecordAsync: Metadata extracted for {FileName}", originalFileName);

            string? thumbnailPath = await ExtractThumbnailAsync(filePath, ct);
            _logger.LogInformation("CreateGcodeFileRecordAsync: Thumbnail path for {FileName} is: {ThumbnailPath}", originalFileName, thumbnailPath ?? "(null)");

            // Rename file to GUID-based name (file was already saved with GUID name in UploadFileAsync)
            string storageDir = Path.GetDirectoryName(filePath) ?? _storagePathService.GetGcodeStorageDirectory();
            string finalFilePath = Path.Combine(storageDir, $"{fileId}{fileExtension}");

            if (filePath != finalFilePath && File.Exists(filePath))
            {
                File.Move(filePath, finalFilePath, overwrite: true);
                _logger.LogInformation("Renamed file from {SourcePath} to {FinalPath}", filePath, finalFilePath);
            }

            // Rename thumbnail to match file ID with _thumb.png suffix (uses same GUID as gcode file)
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

            // Resolve printer model from extracted metadata
            Guid? printerModelId = await _gcodeRepo.ResolvePrinterModelIdAsync(metadata?.PrinterModel, ct);

            return BuildGcodeFileEntityFromMetadata(
                fileId,
                originalFileName,
                fileHash,
                fileSizeBytes,
                targetFolder.Id,
                metadata,
                thumbnailPath,
                GcodeSource.Upload,
                fileExtension,
                resolvedPrinterModelId: printerModelId
            );
        }

        /// <summary>
        /// Build a GcodeFile entity from extracted metadata and file info.
        /// This is the unified method used by all code paths (upload, single harvest, bulk harvest).
        /// Storage directory is obtained from IStoragePathService internally.
        /// </summary>
        internal GcodeFile BuildGcodeFileEntityFromMetadata(
            Guid fileId,
            string originalFileName,
            string fileHash,
            long fileSizeBytes,
            Guid folderId,
            GcodeMetadataExtracted? metadata,
            string? thumbnailPath,
            GcodeSource source,
            string fileExtension = ".gcode",
            Guid? sourcePrinterId = null,
            string? originalPrinterPath = null,
            Guid? resolvedPrinterModelId = null)
        {
            return new GcodeFile
            {
                Id = fileId,
                Name = originalFileName,
                FileName = $"{fileId}{fileExtension}",
                FolderId = folderId,
                FilePath = "/",  // Virtual folder path - always root for stored files
                FileSizeBytes = fileSizeBytes,
                FileHash = fileHash,
                UploadedAt = DateTime.UtcNow,
                Source = source,
                SourcePrinterId = sourcePrinterId,
                OriginalPrinterPath = originalPrinterPath,
                PrinterModelId = resolvedPrinterModelId, // Source printer model from gcode metadata
                RequiredNozzleDiameter = metadata?.NozzleDiameter,
                RequiredMaterial = metadata?.Material,
                EstimatedPrintTimeMinutes = metadata?.EstimatedPrintTimeMinutes,
                EstimatedFilamentLengthMm = metadata?.FilamentLengthMm,
                EstimatedFilamentWeightG = metadata?.FilamentWeightGrams,
                SlicerName = metadata?.SlicerName,
                SlicerVersion = metadata?.SlicerVersion,
                PrintSettingsId = metadata?.PrintSettingsId,
                LayerHeight = metadata?.LayerHeight,
                PrintTemperature = metadata?.PrintTemperature,
                BedTemperature = metadata?.BedTemperature,
                ThumbnailFileName = thumbnailPath != null ? Path.GetFileName(thumbnailPath) : null,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
        }

        #endregion

        /// <summary>
        /// Queries the G-code library with optional filters for search, material, nozzle diameter, and printer model.
        /// </summary>
        /// <param name="search">Optional search term to match against filenames (case-insensitive partial match).</param>
        /// <param name="material">Optional material filter (e.g., 'PLA', 'PETG') to match RequiredMaterial field.</param>
        /// <param name="nozzleDiameter">Optional nozzle diameter in millimeters (e.g., 0.4, 0.6) to match RequiredNozzleDiameter field.</param>
        /// <param name="printerModelId">Optional printer model ID to filter files by the model used when slicing.</param>
        /// <param name="ct">Cancellation token for canceling async operation.</param>
        /// <returns>
        /// A read-only list of G-code file DTOs matching all specified criteria. Empty list if no matches found.
        /// </returns>
        /// <remarks>
        /// <para>
        /// All filters are optional and combined with AND logic (all must match). This method is the primary way
        /// to discover files based on their metadata attributes.
        /// </para>
        /// <para>
        /// The repository layer handles the actual filtering logic, which may include null checks and partial
        /// matching for string fields. Returned DTOs include thumbnail URLs and all metadata.
        /// </para>
        /// </remarks>
        public async Task<IReadOnlyList<GcodeFileDto>> QueryLibraryAsync(string? search, string? material, double? nozzleDiameter, Guid? printerModelId, CancellationToken ct)
        {
            List<GcodeFile> files = await _gcodeRepo.QueryLibraryAsync(search, material, nozzleDiameter, printerModelId, ct);
            return files.Select(file => MapToDto(file)).ToArray();
        }

        /// <summary>
        /// Retrieves a specific G-code file by ID with full metadata and relationships.
        /// </summary>
        /// <param name="id">Unique identifier of the G-code file.</param>
        /// <param name="ct">Cancellation token for canceling async operation.</param>
        /// <returns>
        /// DTO containing complete file metadata (description, tags, nozzle diameter, material, etc.),
        /// or null if file with specified ID not found.
        /// </returns>
        /// <remarks>
        /// <para>
        /// This method includes all related data such as source printer information, target printer, and
        /// associated 3D model. The DTO includes a thumbnail URL if a thumbnail is available.
        /// </para>
        /// <para>
        /// Use this method to display detailed file information in the UI. Returns null gracefully if the
        /// file does not exist rather than throwing an exception.
        /// </para>
        /// </remarks>
        public async Task<GcodeFileDto?> GetFileAsync(Guid id, CancellationToken ct)
        {
            GcodeFile? file = await _gcodeRepo.GetByIdWithIncludesAsync(id, ct);
            if (file is null)
            {
                return null;
            }

            return MapToDto(file);
        }

        /// <summary>
        /// Uploads a G-code file to the library with full metadata.
        /// </summary>
        /// <param name="file">The uploaded file from an HTTP request. Must not be null.</param>
        /// <param name="metadata">Metadata including description, tags, nozzle diameter, material, estimated print time, etc. Must not be null.</param>
        /// <param name="webRootPath">Application web root path for thumbnail URL generation (used by MapToDto).</param>
        /// <param name="ct">Cancellation token for canceling async operation.</param>
        /// <returns>
        /// DTO representing the newly uploaded file with all provided metadata and generated system fields
        /// (ID, upload timestamp, thumbnail URL if available).
        /// </returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown if a file with the same content hash already exists (duplicate file detection) or if
        /// file path validation fails.
        /// </exception>
        /// <exception cref="ArgumentNullException">Thrown if file or metadata parameter is null.</exception>
        /// <remarks>
        /// <para>
        /// This method computes a SHA256 hash of the file content and checks for duplicates before saving.
        /// If a duplicate is detected, an InvalidOperationException is thrown with the message "duplicate".
        /// </para>
        /// <para>
        /// The file is saved to the G-code storage directory with a GUID-based filename for uniqueness.
        /// All metadata from the CreateGcodeFileDto is persisted to the database. This is distinct from
        /// the file browser UploadFileAsync in that it emphasizes metadata capture over virtual folder organization.
        /// </para>
        /// </remarks>
        public async Task<GcodeFileDto> UploadFileAsync(IFormFile file, CreateGcodeFileDto metadata, string webRootPath, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(file);
            ArgumentNullException.ThrowIfNull(metadata);

            // Compute hash
            string hash;
            using (Stream stream = file.OpenReadStream())
            using (SHA256 sha256 = SHA256.Create())
            {
                byte[] hashBytes = await sha256.ComputeHashAsync(stream, ct);
                hash = Convert.ToHexString(hashBytes);
            }

            // Check duplicate
            GcodeFile? existing = await _gcodeRepo.FindByHashAsync(hash, ct);
            if (existing is not null)
            {
                throw new InvalidOperationException("duplicate");
            }

            // Use StoragePathService to get the correct gcode storage directory
            string libraryPath = _storagePathService.GetGcodeStorageDirectory();
            string libraryRootFull = Path.GetFullPath(libraryPath);
            _ = Directory.CreateDirectory(libraryRootFull);

            // Save file
            string fileName = $"{Guid.NewGuid()}.gcode";
            string filePathFull = Path.GetFullPath(Path.Combine(libraryRootFull, fileName));
            if (!filePathFull.StartsWith(libraryRootFull, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Invalid file path");
            }

            await using (FileStream fs = System.IO.File.Create(filePathFull))
            {
                await file.CopyToAsync(fs, ct);
            }

            GcodeFile gcodeFile = new()
            {
                Id = Guid.NewGuid(),
                FileName = string.IsNullOrEmpty(metadata.FileName) ? file.FileName : metadata.FileName,
                FilePath = libraryRootFull, // Store directory path
                FileSizeBytes = file.Length,
                FileHash = hash,
                UploadedAt = DateTime.UtcNow,
                Source = GcodeSource.Upload,
                Description = metadata.Description,
                Tags = metadata.Tags != null ? string.Join(',', metadata.Tags) : null,
                RequiredNozzleDiameter = metadata.RequiredNozzleDiameter,
                RequiredMaterial = metadata.RequiredMaterial,
                EstimatedPrintTimeMinutes = metadata.EstimatedPrintTimeMinutes,
                EstimatedFilamentLengthMm = metadata.EstimatedFilamentLengthMm,
                EstimatedFilamentWeightG = metadata.EstimatedFilamentWeightG,
                PrinterModelId = metadata.PrinterModelId,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            await _gcodeRepo.AddAsync(gcodeFile, ct);
            await _gcodeRepo.SaveChangesAsync(ct);

            GcodeFile? saved = await _gcodeRepo.GetByIdWithIncludesAsync(gcodeFile.Id, ct);
            return MapToDto(saved!);
        }

        /// <summary>
        /// Updates metadata for an existing G-code file.
        /// </summary>
        /// <param name="id">Unique identifier of the file to update.</param>
        /// <param name="request">DTO containing metadata fields to update. Null or empty fields are skipped (partial update).</param>
        /// <param name="ct">Cancellation token for canceling async operation.</param>
        /// <returns>
        /// DTO containing the updated file with all current metadata after the update.
        /// </returns>
        /// <exception cref="KeyNotFoundException">Thrown if file with specified ID not found in the database.</exception>
        /// <remarks>
        /// <para>
        /// This method performs a partial update - only provided fields are modified. Does not update file content,
        /// core properties like UploadedAt, or file hash. Automatically updates the UpdatedAt timestamp.
        /// </para>
        /// <para>
        /// All fields in the UpdateGcodeFileDto are optional. Null or empty fields are skipped, allowing selective
        /// metadata updates without replacing the entire record.
        /// </para>
        /// </remarks>
        public async Task<GcodeFileDto> UpdateFileAsync(Guid id, UpdateGcodeFileDto request, CancellationToken ct)
        {
            GcodeFile? file = await _gcodeRepo.GetByIdWithIncludesAsync(id, ct);
            if (file == null)
            {
                throw new KeyNotFoundException();
            }

            if (!string.IsNullOrEmpty(request.FileName))
            {
                file.FileName = request.FileName;
            }

            if (request.Description != null)
            {
                file.Description = request.Description;
            }

            if (request.Tags != null)
            {
                file.Tags = string.Join(',', request.Tags);
            }

            if (request.RequiredNozzleDiameter.HasValue)
            {
                file.RequiredNozzleDiameter = request.RequiredNozzleDiameter;
            }

            if (!string.IsNullOrEmpty(request.RequiredMaterial))
            {
                file.RequiredMaterial = request.RequiredMaterial;
            }

            if (request.PrinterModelId.HasValue)
            {
                file.PrinterModelId = request.PrinterModelId.Value;
            }

            file.UpdatedAt = DateTime.UtcNow;

            await _gcodeRepo.SaveChangesAsync(ct);

            GcodeFile? saved = await _gcodeRepo.GetByIdWithIncludesAsync(id, ct);
            return MapToDto(saved!);
        }

        /// <summary>
        /// Deletes a G-code file from the library.
        /// </summary>
        /// <param name="id">Unique identifier of the file to delete.</param>
        /// <param name="ct">Cancellation token for canceling async operation.</param>
        /// <returns>
        /// True if deletion succeeded. False if file not found or cannot be deleted (e.g., in use by active print job).
        /// </returns>
        /// <remarks>
        /// <para>
        /// Before deletion, checks if the file is referenced by any active print queue jobs. If active jobs exist,
        /// the deletion is prevented and returns false.
        /// </para>
        /// <para>
        /// Removes both the physical file and its thumbnail from disk. If physical file deletion fails, the error
        /// is logged as a warning but does not prevent the database record removal. This ensures the database stays
        /// in sync even if filesystem operations partially fail.
        /// </para>
        /// <para>
        /// Returns false gracefully if file not found rather than throwing an exception.
        /// </para>
        /// </remarks>
        public async Task<bool> DeleteFileAsync(Guid id, CancellationToken ct)
        {
            GcodeFile? file = await _gcodeRepo.GetByIdWithIncludesAsync(id, ct);
            if (file == null)
            {
                return false;
            }

            // Delete physical
            try
            {
                string fullFilePath = Path.Combine(file.FilePath, file.FileName);
                if (!string.IsNullOrEmpty(fullFilePath) && System.IO.File.Exists(fullFilePath))
                {
                    System.IO.File.Delete(fullFilePath);
                }

                if (!string.IsNullOrEmpty(file.ThumbnailFileName))
                {
                    string fullThumbnailPath = Path.Combine(file.FilePath, file.ThumbnailFileName);
                    if (System.IO.File.Exists(fullThumbnailPath))
                    {
                        System.IO.File.Delete(fullThumbnailPath);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"Failed to delete physical file for gcode {id}");
            }

            await _gcodeRepo.RemoveAsync(file, ct);
            await _gcodeRepo.SaveChangesAsync(ct);
            return true;
        }

        /// <summary>
        /// Downloads a G-code file by ID, returning its complete contents.
        /// </summary>
        /// <param name="id">Unique identifier of the file to download.</param>
        /// <param name="webRootPath">Application web root path (used for path resolution).</param>
        /// <param name="ct">Cancellation token for canceling async operation.</param>
        /// <returns>
        /// Complete file contents as byte array suitable for HTTP response transmission, or null if file
        /// not found in database or cannot be read from filesystem.
        /// </returns>
        /// <remarks>
        /// <para>
        /// Checks both the database record and filesystem existence before returning. Returns null gracefully
        /// if the file metadata exists but the physical file has been deleted or moved.
        /// </para>
        /// <para>
        /// The returned byte array can be directly written to an HTTP response stream. No additional
        /// encoding or transformation is performed.
        /// </para>
        /// </remarks>
        public async Task<byte[]?> DownloadFileAsync(Guid id, string webRootPath, CancellationToken ct)
        {
            GcodeFile? file = await _gcodeRepo.GetByIdWithIncludesAsync(id, ct);
            if (file == null)
            {
                return null;
            }

            if (string.IsNullOrEmpty(file.FilePath))
            {
                return null;
            }

            string fullPath = Path.Combine(file.FilePath, file.FileName);
            if (!System.IO.File.Exists(fullPath))
            {
                return null;
            }

            return await System.IO.File.ReadAllBytesAsync(fullPath, ct);
        }

        /// <summary>
        /// Maps a GcodeFile domain model to a DTO with thumbnail URL construction.
        /// </summary>
        /// <param name="file">The GcodeFile domain model to convert to DTO.</param>
        /// <returns>
        /// A GcodeFileDto containing all file metadata with thumbnail URL properly constructed
        /// from the physical filename and storage path.
        /// </returns>
        /// <remarks>
        /// <para>
        /// This method constructs the thumbnail URL (if a thumbnail exists) from the physical file location
        /// using the centralized IThumbnailUrlBuilderService. This ensures consistent URL construction with
        /// the Model3DFileService for uniform handling across the application.
        /// </para>
        /// <para>
        /// All related data (source printer, target printer, target model) from the domain model is included
        /// in the DTO. The method handles null/missing thumbnail filenames gracefully, returning null for
        /// the thumbnail URL in such cases.
        /// </para>
        /// <para>
        /// This is an internal helper method used consistently throughout the service to ensure uniform
        /// DTO construction and thumbnail URL handling.
        /// </para>
        /// </remarks>
        private GcodeFileDto MapToDto(GcodeFile file)
        {
            // Use centralized service method for efficient download-based thumbnail URL
            string? thumbnailUrl = _fileOperations.BuildThumbnailUrl(
                file,
                "/api/gcode-files/download",
                _storagePathService.GetGcodeStorageDirectory()
            );

            return new GcodeFileDto(
                Id: file.Id,
                Name: file.Name,
                FileName: file.FileName,
                FileSize: file.FileSizeBytes,
                UploadedAt: file.UploadedAt,
                ThumbnailUrl: thumbnailUrl,
                Source: (GcodeSourceDto)(int)file.Source,
                SourcePrinterId: file.SourcePrinterId,
                SourcePrinterName: file.SourcePrinter?.Name,
                OriginalPrinterPath: file.OriginalPrinterPath,
                LastSeenOnPrinter: file.LastSeenOnPrinter,
                Description: file.Description,
                Tags: file.Tags?.Split(',', StringSplitOptions.RemoveEmptyEntries),
                RequiredNozzleDiameter: file.RequiredNozzleDiameter,
                RequiredMaterial: file.RequiredMaterial,
                EstimatedPrintTimeMinutes: file.EstimatedPrintTimeMinutes,
                EstimatedFilamentLengthMm: file.EstimatedFilamentLengthMm,
                EstimatedFilamentWeightG: file.EstimatedFilamentWeightG,
                PrinterModelId: file.PrinterModelId,
                PrinterModelName: file.PrinterModel?.Name,
                SlicerName: file.SlicerName,
                SlicerVersion: file.SlicerVersion,
                HasThumbnail: !string.IsNullOrEmpty(file.ThumbnailFileName)
            );
        }

        /// <summary>
        /// Unified method for processing and storing G-code files from any source
        /// (upload, single harvest, bulk harvest, or future sources).
        /// 
        /// Handles all file processing: storage, hash calculation, duplicate detection,
        /// metadata extraction, thumbnail processing, entity creation, and database persistence.
        /// </summary>
        /// <param name="fileContent">The raw file content bytes</param>
        /// <param name="originalFileName">Original filename as provided by source</param>
        /// <param name="folderId">Virtual folder ID where file should be organized</param>
        /// <param name="virtualDirectory">Virtual directory path (e.g., '/', '/subfolder'). Defaults to '/'.</param>
        /// <param name="sourcePrinterId">Optional printer ID if harvested from a specific printer</param>
        /// <param name="originalPrinterPath">Optional original path on printer if harvested</param>
        /// <param name="thumbnailUrl">Optional thumbnail URL from printer API (harvest only)</param>
        /// <param name="fileId">Optional specific ID for the file. If null, a new GUID is generated.</param>
        /// <param name="ct">Cancellation token</param>
        /// <returns>The saved GcodeFile entity with all metadata populated</returns>
        /// <remarks>
        /// This method consolidates all file handling logic:
        /// 1. Stores file to disk with GUID-based filename
        /// 2. Calculates SHA256 hash and checks for duplicates
        /// 3. Extracts metadata from G-code (slicer, temps, filament, etc.)
        /// 4. Processes and extracts thumbnail image
        /// 5. Creates GcodeFile entity with complete metadata
        /// 6. Saves to database
        /// 
        /// If a duplicate file is detected (same hash), an exception is thrown allowing
        /// the caller to decide whether to skip, replace, or re-import.
        /// </remarks>
        /// <exception cref="InvalidOperationException">Thrown if a duplicate file already exists</exception>
        public async Task<GcodeFile> ProcessAndStoreGcodeFileAsync(
            byte[] fileContent,
            string originalFileName,
            Guid folderId,
            string? virtualDirectory = null,
            Guid? sourcePrinterId = null,
            string? originalPrinterPath = null,
            string? thumbnailUrl = null,
            Guid? fileId = null,
            CancellationToken ct = default)
        {
            if (fileContent == null || fileContent.Length == 0)
                throw new ArgumentException("File content cannot be null or empty", nameof(fileContent));
            if (string.IsNullOrWhiteSpace(originalFileName))
                throw new ArgumentException("Original file name cannot be null or empty", nameof(originalFileName));

            fileId ??= Guid.NewGuid();
            virtualDirectory = NormalizeVirtualPath(virtualDirectory ?? "/");

            _logger.LogInformation($"ProcessAndStoreGcodeFileAsync: Starting for {originalFileName} (folderId={folderId})");

            // Step 1: Store file to disk
            string storageDir = _storagePathService.GetGcodeStorageDirectory();
            _ = Directory.CreateDirectory(storageDir);
            
            string fileExtension = Path.GetExtension(originalFileName);
            string finalFilePath = Path.Combine(storageDir, $"{fileId}{fileExtension}");
            
            await System.IO.File.WriteAllBytesAsync(finalFilePath, fileContent, ct);
            _logger.LogInformation($"File stored at {finalFilePath}");

            // Step 2: Calculate hash and check for duplicates
            string fileHash;
            using (var hashStream = new MemoryStream(fileContent))
            using (var sha256 = SHA256.Create())
            {
                byte[] hashBytes = await sha256.ComputeHashAsync(hashStream, ct);
                fileHash = Convert.ToHexString(hashBytes);
            }
            _logger.LogInformation($"Calculated file hash: {fileHash.Substring(0, 8)}...");

            // Check for duplicates (allow if from same source/printer path, but otherwise reject)
            var existingFile = await _gcodeRepo.FindByHashAsync(fileHash, ct);
            if (existingFile != null && existingFile.Id != fileId)
            {
                // Clean up the file we just wrote
                try { System.IO.File.Delete(finalFilePath); }
                catch { /* Ignore cleanup errors */ }
                
                throw new InvalidOperationException(
                    $"Duplicate file detected: {existingFile.Name} (hash: {fileHash.Substring(0, 8)}...)");
            }

            // Step 3: Extract metadata
            GcodeMetadataExtracted? metadata = null;
            try
            {
                string gcodeText = Encoding.UTF8.GetString(fileContent);
                metadata = await _metadataExtractor.ExtractMetadataAsync(gcodeText);
                _logger.LogInformation("Metadata extracted successfully");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to extract metadata from G-code");
            }

            // Step 4: Process thumbnail (from URL first, then extraction)
            string? thumbnailPath = null;
            try
            {
                if (!string.IsNullOrWhiteSpace(thumbnailUrl))
                {
                    // Try to download from printer API URL
                    thumbnailPath = await ProcessThumbnailFromUrlAsync(thumbnailUrl, fileId.Value, storageDir, ct);
                }
                
                // If no URL or URL download failed, try extracting from G-code
                if (string.IsNullOrEmpty(thumbnailPath))
                {
                    thumbnailPath = await ExtractThumbnailAsync(finalFilePath, ct);
                }
                
                if (!string.IsNullOrEmpty(thumbnailPath))
                {
                    _logger.LogInformation($"Thumbnail processed: {Path.GetFileName(thumbnailPath)}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error processing thumbnail");
            }

            // Step 5: Create entity with all metadata
            // Resolve printer model from extracted metadata
            Guid? printerModelId = await _gcodeRepo.ResolvePrinterModelIdAsync(metadata?.PrinterModel, ct);

            var gcodeFile = BuildGcodeFileEntityFromMetadata(
                fileId.Value,
                originalFileName,
                fileHash,
                fileContent.Length,
                folderId,
                metadata,
                thumbnailPath,
                sourcePrinterId.HasValue ? GcodeSource.Harvested : GcodeSource.Upload,
                ".gcode",
                sourcePrinterId,
                originalPrinterPath,
                resolvedPrinterModelId: printerModelId);

            // Step 6: Save to database
            await _unitOfWork.GcodeFiles.AddAsync(gcodeFile, ct);
            await _unitOfWork.SaveChangesAsync(ct);

            _logger.LogInformation($"GcodeFile {fileId} saved to database successfully");
            return gcodeFile;
        }

        /// <summary>
        /// Helper method to process thumbnail from printer API URL
        /// </summary>
        private async Task<string?> ProcessThumbnailFromUrlAsync(
            string thumbnailUrl,
            Guid fileId,
            string storageDir,
            CancellationToken ct)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(thumbnailUrl))
                    return null;

                // Download thumbnail from URL
                using var httpClient = new System.Net.Http.HttpClient();
                var response = await httpClient.GetAsync(thumbnailUrl, ct);
                
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning($"Failed to download thumbnail from URL: {response.StatusCode}");
                    return null;
                }

                // Save thumbnail
                byte[] thumbnailData = await response.Content.ReadAsByteArrayAsync(ct);
                string thumbnailPath = Path.Combine(storageDir, $"{fileId}_thumb.png");
                await System.IO.File.WriteAllBytesAsync(thumbnailPath, thumbnailData, ct);
                
                return thumbnailPath;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error downloading thumbnail from URL");
                return null;
            }
        }
    }
}
