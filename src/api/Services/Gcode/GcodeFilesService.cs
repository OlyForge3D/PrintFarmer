using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Gcode;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Controllers;
using Farm.Web.Shared;
using Microsoft.AspNetCore.Http;

namespace Farm.Web.Api.Services.Gcode
{
    public class GcodeFilesService : IGcodeFilesService
    {
        private readonly IGcodeRepository _gcodeRepo;
        private readonly IUnifiedLoggingService _logger;
        private readonly Farm.Web.Api.Services.StorageManagement.IStoragePathService _storagePathService;

        public GcodeFilesService(IGcodeRepository gcodeRepo, IUnifiedLoggingService logger, Farm.Web.Api.Services.StorageManagement.IStoragePathService storagePathService)
        {
            _gcodeRepo = gcodeRepo ?? throw new ArgumentNullException(nameof(gcodeRepo));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _storagePathService = storagePathService ?? throw new ArgumentNullException(nameof(storagePathService));
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

            // Use StoragePathService to get the correct gcode storage directory
            string storageRoot = _storagePathService.GetGcodeStorageDirectory();
            // Ensure storage root exists
            if (!Directory.Exists(storageRoot))
            {
                _ = Directory.CreateDirectory(storageRoot);
            }

            string? vPath = string.IsNullOrWhiteSpace(path) ? "/" : path.Trim();
            if (!vPath.StartsWith('/'))
            {
                vPath = "/" + vPath;
            }

            // Parse virtual path to relative path
            string[] segments = vPath.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Where(s => s != "." && s != "..")
                .ToArray();
            string safeRel = segments.Length == 0 ? string.Empty : Path.Combine(segments);
            string requestedDirFullPath = Path.GetFullPath(Path.Combine(storageRoot, safeRel));

            // Security check: ensure path doesn't escape the storage root
            if (!requestedDirFullPath.StartsWith(storageRoot, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Path escapes storage root");
            }

            string? virtualPathNormalized = segments.Length == 0 ? "/" : "/" + string.Join('/', segments);
            // Create the requested directory if it doesn't exist (for root or nested paths)
            if (!Directory.Exists(requestedDirFullPath))
            {
                _ = Directory.CreateDirectory(requestedDirFullPath);
            }

            System.IO.DirectoryInfo dirInfo = new(requestedDirFullPath);
            List<GcodeFileEntryDto> entries = new();

            // Directories
            foreach (System.IO.DirectoryInfo dir in dirInfo.EnumerateDirectories())
            {
                if (dir.Name.StartsWith('.'))
                {
                    continue;
                }

                if (!IsMatch(dir.Name, search))
                {
                    continue;
                }

                string childVirtual = CombineVirtual(virtualPathNormalized, dir.Name);
                entries.Add(new GcodeFileEntryDto(
                    Path: childVirtual,
                    Name: dir.Name,
                    Size: 0,
                    ModifiedAt: dir.LastWriteTimeUtc,
                    IsDirectory: true
                ));
            }

            // Files
            foreach (string? pattern in new[] { "*.gcode", "*.bgcode" })
            {
                foreach (System.IO.FileInfo file in dirInfo.EnumerateFiles(pattern))
                {
                    if (!IsMatch(file.Name, search))
                    {
                        continue;
                    }

                    string childVirtual = CombineVirtual(virtualPathNormalized, file.Name);
                    Guid? harvestOpId = null;
                    try
                    {
                        // Repository abstraction: avoid direct DbContext usage in service layer
                        GcodeFile? dbEntry = await _gcodeRepo.GetByFullPathAsync(file.FullName, ct);
                        if (dbEntry?.SourcePrinterId != null)
                        {
                            harvestOpId = await _gcodeRepo.GetLatestHarvestOperationIdForPrinterAsync(dbEntry.SourcePrinterId.Value, ct);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug($"Non-fatal DB correlation failure for file {file.FullName}: {ex.Message}");
                    }

                    if (harvestId.HasValue && harvestOpId != harvestId)
                    {
                        continue;
                    }

                    entries.Add(new GcodeFileEntryDto(
                        Path: childVirtual,
                        Name: file.Name,
                        Size: file.Length,
                        ModifiedAt: file.LastWriteTimeUtc,
                        IsDirectory: false,
                        HarvestOperationId: harvestOpId
                    ));
                }
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

            await using FileStream fs = new(fullTarget, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await file.CopyToAsync(fs, ct);

            System.IO.FileInfo info = new(fullTarget);
            string virtualFilePath = CombineVirtual(virtualDir, safeName);
            return new GcodeFileEntryDto(virtualFilePath, safeName, info.Length, info.LastWriteTimeUtc, false);
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
    }
}
