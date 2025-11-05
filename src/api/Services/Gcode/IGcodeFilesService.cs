using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Web.Api.Controllers;
using Farm.Web.Shared;

namespace Farm.Web.Api.Services.Gcode
{
    public interface IGcodeFilesService
    {
        Task<GcodeFileListResponse> ListAsync(string? path, string? sortBy, string? sortOrder, string? search, int page, int pageSize, System.Guid? harvestId, System.Guid? printerId, CancellationToken ct);
        Task<GcodeFileEntryDto> UploadAsync(string? path, Microsoft.AspNetCore.Http.IFormFile file, IGcodeUploadSettings uploadSettings, Farm.Web.Api.Services.IGcodeUploadQuotaService quotaService, CancellationToken ct);
        Task<MultiUploadResponse> UploadMultipleAsync(string? path, Microsoft.AspNetCore.Http.IFormFileCollection files, IGcodeUploadSettings uploadSettings, Farm.Web.Api.Services.IGcodeUploadQuotaService quotaService, CancellationToken ct);
        Task<GcodeFileEntryDto> MakeDirectoryAsync(string? path, string? name, CancellationToken ct);
        Task<bool> DeleteFilesAsync(IEnumerable<string> virtualPaths, bool recursive, CancellationToken ct);
        Task<(byte[] bytes, string fileName)?> DownloadAsync(string path, CancellationToken ct);
        Task<(bool ok, string virtualPath, bool isDirectory)> MoveAsync(string sourcePath, string destinationPath, bool overwrite, CancellationToken ct);
        Task<GcodeUploadSettingsResponse> GetSettingsAsync(string userId, IGcodeUploadSettings uploadSettings, IGcodeUploadQuotaService quotaService, CancellationToken ct);
    }
}
