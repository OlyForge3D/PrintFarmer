using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Domain;
using Farm.Web.Api.Controllers;
using Farm.Web.Api.Services.FileManagement;

namespace Farm.Web.Api.Services.Gcode
{
    public interface IGcodeFilesService
    {
        Task<GcodeFileListResponse> ListAsync(string? path, string? sortBy, string? sortOrder, string? search, int page, int pageSize, Guid? harvestId, Guid? printerId, CancellationToken ct);
        Task<GcodeFileEntryDto> UploadAsync(string? path, IFormFile file, IGcodeUploadSettings uploadSettings, IGcodeUploadQuotaService quotaService, CancellationToken ct);
        Task<GcodeFile?> FinalizeChunkedUploadAsync(string filePath, string? originalFileName, string? thumbnailPath, string? virtualDirectory, IChunkedUploadService chunkedUploadService, CancellationToken ct);
        Task<MultiUploadResponse> UploadMultipleAsync(string? path, IFormFileCollection files, IGcodeUploadSettings uploadSettings, IGcodeUploadQuotaService quotaService, CancellationToken ct);
        Task<GcodeFileEntryDto> MakeDirectoryAsync(string? path, string? name, CancellationToken ct);
        Task<bool> DeleteFilesAsync(IEnumerable<string> virtualPaths, bool recursive, CancellationToken ct);
        Task<(byte[] bytes, string fileName)?> DownloadAsync(string path, CancellationToken ct);
        Task<(bool ok, string virtualPath, bool isDirectory)> MoveAsync(string sourcePath, string destinationPath, bool overwrite, CancellationToken ct);
        Task<GcodeUploadSettingsResponse> GetSettingsAsync(string userId, IGcodeUploadSettings uploadSettings, IGcodeUploadQuotaService quotaService, CancellationToken ct);
    }
}
