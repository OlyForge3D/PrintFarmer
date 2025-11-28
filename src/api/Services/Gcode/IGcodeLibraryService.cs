using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Microsoft.AspNetCore.Http;

namespace Farm.Web.Api.Services.Gcode
{
    public interface IGcodeLibraryService
    {
        Task<IReadOnlyList<GcodeFileDto>> QueryLibraryAsync(string? search, string? material, double? nozzleDiameter, Guid? targetPrinterId, CancellationToken ct);
        Task<GcodeFileDto?> GetFileAsync(Guid id, CancellationToken ct);
        Task<GcodeFileDto> UploadFileAsync(IFormFile file, CreateGcodeFileDto metadata, string webRootPath, CancellationToken ct);
        Task<GcodeFileDto> UpdateFileAsync(Guid id, UpdateGcodeFileDto request, CancellationToken ct);
        Task<bool> DeleteFileAsync(Guid id, CancellationToken ct);
        Task<byte[]?> DownloadFileAsync(Guid id, string webRootPath, CancellationToken ct);
    }
}
