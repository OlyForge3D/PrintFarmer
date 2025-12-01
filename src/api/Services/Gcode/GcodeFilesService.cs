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
using Farm.Infrastructure.Repositories.Gcode;
using Farm.Infrastructure.Services.Gcode;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Controllers;
using Farm.Web.Api.Services.FileManagement;
using Microsoft.AspNetCore.Http;

namespace Farm.Web.Api.Services.Gcode
{
    public class GcodeFilesService : IGcodeFilesService
    {
        private readonly IGcodeRepository _gcodeRepo;
        private readonly IUnifiedLoggingService _logger;
        private readonly Farm.Web.Api.Services.StorageManagement.IStoragePathService _storagePathService;
        private readonly IGcodeMetadataExtractorService _metadataExtractor;
        private readonly IGcodeThumbnailExtractorService _thumbnailExtractor;

        public GcodeFilesService(
            IGcodeRepository gcodeRepo,
            IUnifiedLoggingService logger,
            Farm.Web.Api.Services.StorageManagement.IStoragePathService storagePathService,
            IGcodeMetadataExtractorService metadataExtractor,
            IGcodeThumbnailExtractorService thumbnailExtractor)
        {
            _gcodeRepo = gcodeRepo ?? throw new ArgumentNullException(nameof(gcodeRepo));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _storagePathService = storagePathService ?? throw new ArgumentNullException(nameof(storagePathService));
            _metadataExtractor = metadataExtractor ?? throw new ArgumentNullException(nameof(metadataExtractor));
            _thumbnailExtractor = thumbnailExtractor ?? throw new ArgumentNullException(nameof(thumbnailExtractor));
        }

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
            string requestedDir = segments.Length == 0 ? string.Empty : Path.Combine(segments);
            string? virtualPathNormalized = segments.Length == 0 ? "/" : "/" + string.Join('/', segments);

            // Get all files and subdirectories from database for this directory (pure DB approach)
            List<GcodeFile> dbFiles = await _gcodeRepo.ListFilesInDirectoryAsync(requestedDir, ct);
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
                    Name: subdir,
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
                if (!IsMatch(file.OriginalFileName, search))
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

                string childVirtual = CombineVirtual(virtualPathNormalized, file.OriginalFileName);
                entries.Add(new GcodeFileEntryDto(
                    Path: childVirtual,
                    Name: file.OriginalFileName,
                    Size: file.FileSizeBytes,
                    ModifiedAt: file.UploadedAt,
                    IsDirectory: false,
                    HarvestOperationId: harvestOpId,
                    ThumbnailPath: file.ThumbnailPath
                ));
            }

            // Sorting
            string normalizedSortBy = string.IsNullOrWhiteSpace(sortBy) ? "name" : sortBy.Trim();
            string normalizedSortOrder = string.IsNullOrWhiteSpace(sortOrder) ? "asc" : sortOrder.Trim();
            bool orderDesc = normalizedSortOrder.Equals("desc", StringComparison.OrdinalIgnoreCase);

            if (normalizedSortBy.Equals("size", StringComparison.OrdinalIgnoreCase))
            {
                entries = orderDesc
                    ? entries.OrderByDescending(e => e.IsDirectory).ThenByDescending(e => e.Size).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList()
                    : entries.OrderByDescending(e => e.IsDirectory).ThenBy(e => e.Size).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
            }
            else if (normalizedSortBy.Equals("date", StringComparison.OrdinalIgnoreCase))
            {
                entries = orderDesc
                    ? entries.OrderByDescending(e => e.IsDirectory).ThenByDescending(e => e.ModifiedAt).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList()
                    : entries.OrderByDescending(e => e.IsDirectory).ThenBy(e => e.ModifiedAt).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
            }
            else
            {
                entries = orderDesc
                    ? entries.OrderByDescending(e => e.IsDirectory).ThenByDescending(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList()
                    : entries.OrderByDescending(e => e.IsDirectory).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
            }

            int totalFiles = entries.Count(e => !e.IsDirectory);
            long totalSize = entries.Where(e => !e.IsDirectory).Sum(e => e.Size);
            int skip = (page - 1) * pageSize;
            IReadOnlyList<GcodeFileEntryDto> pagedEntries = skip >= entries.Count ? Array.Empty<GcodeFileEntryDto>() : entries.Skip(skip).Take(pageSize).ToList();
            int totalItems = entries.Count;
            int totalPages = (int)Math.Ceiling(totalItems / (double)pageSize);

            return new GcodeFileListResponse(pagedEntries, totalFiles, totalSize, page, pageSize, totalPages, totalItems);
        }

        public async Task<GcodeFileEntryDto> UploadAsync(string? path, IFormFile file, IGcodeUploadSettings uploadSettings, Farm.Web.Api.Services.IGcodeUploadQuotaService quotaService, CancellationToken ct)
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
            string virtualFilePath = CombineVirtual(virtualDir, gcodeFile.DisplayName);
            return new GcodeFileEntryDto(virtualFilePath, gcodeFile.DisplayName, gcodeFile.FileSizeBytes, info.LastWriteTimeUtc, false);
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
                string fileName = originalFileName ?? fileInfo.Name;
                string fileExtension = Path.GetExtension(fileName) ?? ".gcode";

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
                
                // Create database record
                GcodeFile gcodeFile = new()
                {
                    Id = fileId,
                    OriginalFileName = fileName,
                    DisplayName = Path.GetFileNameWithoutExtension(fileName),
                    FilePath = Path.GetFullPath(filePath),
                    FileDirectory = normalizedVirtualDir,
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
                    ThumbnailPath = thumbnailPath,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };

                // Move uploaded file to GUID-based name before saving to DB
                string storageDir = Path.GetDirectoryName(filePath) ?? _storagePathService.GetGcodeStorageDirectory();
                string finalFilePath = Path.Combine(storageDir, $"{fileId}{fileExtension}");
                
                if (filePath != finalFilePath && File.Exists(filePath))
                {
                    File.Move(filePath, finalFilePath, overwrite: true);
                    gcodeFile.FilePath = Path.GetFullPath(finalFilePath);
                    _logger.LogInformation("Moved file from {SourcePath} to {FinalPath}", filePath, finalFilePath);
                }

                // Rename thumbnail to match file ID with _thumb.png suffix
                if (!string.IsNullOrEmpty(thumbnailPath) && File.Exists(thumbnailPath))
                {
                    string finalThumbnailPath = Path.Combine(storageDir, $"{fileId}_thumb.png");
                    if (thumbnailPath != finalThumbnailPath)
                    {
                        File.Move(thumbnailPath, finalThumbnailPath, overwrite: true);
                        gcodeFile.ThumbnailPath = Path.GetFullPath(finalThumbnailPath);
                        _logger.LogInformation("Moved thumbnail from {SourcePath} to {FinalPath}", thumbnailPath, finalThumbnailPath);
                    }
                }

                await _gcodeRepo.AddAsync(gcodeFile, ct);
                await _gcodeRepo.SaveChangesAsync(ct);

                _logger.LogInformation("Finalized chunked upload as GcodeFile database record for {FileName} with ID {FileId}", fileName, fileId);
                return gcodeFile;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to finalize chunked upload to database at {FilePath}", filePath);
                return null;
            }
        }

        public async Task<MultiUploadResponse> UploadMultipleAsync(string? path, IFormFileCollection files, IGcodeUploadSettings uploadSettings, Farm.Web.Api.Services.IGcodeUploadQuotaService quotaService, CancellationToken ct)
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

        public Task<GcodeFileEntryDto> MakeDirectoryAsync(string? path, string? name, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("name is required");
            }
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains('/') || name.Contains('\n') || name.Contains('\r'))
            {
                throw new ArgumentException("Invalid directory name");
            }

            // Resolve path using IStoragePathService
            (_, string parentDirFullPath, string virtualDir) = ResolveVirtualPath(path, _storagePathService.GetGcodeStorageDirectory());

            // Create parent directory if needed
            if (!Directory.Exists(parentDirFullPath))
            {
                _ = Directory.CreateDirectory(parentDirFullPath);
            }

            string newDirFullPath = Path.GetFullPath(Path.Combine(parentDirFullPath, name));
            if (!newDirFullPath.StartsWith(parentDirFullPath, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Unsafe directory target");
            }
            if (Directory.Exists(newDirFullPath))
            {
                throw new InvalidOperationException("Directory already exists");
            }

            _ = Directory.CreateDirectory(newDirFullPath);

            GcodeFileEntryDto dto = new(
                Path: CombineVirtual(virtualDir, name),
                Name: name,
                Size: 0,
                ModifiedAt: Directory.GetLastWriteTimeUtc(newDirFullPath),
                IsDirectory: true
            );
            return Task.FromResult(dto);
        }

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

        public Task<GcodeUploadSettingsResponse> GetSettingsAsync(string userId, IGcodeUploadSettings uploadSettings, IGcodeUploadQuotaService quotaService, CancellationToken ct)
        {
            long used = 0;
            long limit = 0;
            _ = quotaService.TryAddUsage(userId, 0, out used, out limit);
            return Task.FromResult(new GcodeUploadSettingsResponse(uploadSettings.AllowedExtensions, limit, used));
        }

        // Helper methods
        private static bool IsMatch(string name, string? search)
            => string.IsNullOrWhiteSpace(search) || name.Contains(search, StringComparison.OrdinalIgnoreCase);

        private static string CombineVirtual(string? baseVirtual, string childName)
        {
            if (baseVirtual == "/")
            {
                return "/" + childName;
            }

            return (baseVirtual ?? "/").TrimEnd('/') + "/" + childName;
        }

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

        private static string SafeOriginalName(string? name)
            => string.IsNullOrWhiteSpace(name) ? "(unnamed)" : Path.GetFileName(name);

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

            // Create database record
            GcodeFile gcodeFile = new()
            {
                Id = fileId,
                OriginalFileName = originalFileName,
                DisplayName = Path.GetFileNameWithoutExtension(originalFileName),
                FileDirectory = NormalizeVirtualPath(virtualDirectory ?? "/"),
                FilePath = Path.GetFullPath(finalFilePath),
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
                ThumbnailPath = thumbnailPath,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            return gcodeFile;
        }
    }
}
