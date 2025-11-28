using Farm.Web.Api.Services.Interfaces;
using Farm.Infrastructure;
using Farm.Infrastructure.Contracts.Printers.Moonraker;

namespace Farm.Web.Api.Services;

public partial class MoonrakerClient : IMoonrakerClient
{
    // Status and Job Information
    public Task<PrinterStatus> GetStatusAsync(Uri baseUrl, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return GetStatusAsync(baseUrl.ToString(), ct);
    }

    public Task<MoonrakerPrinterInfo?> GetPrinterInfoAsync(Uri baseUrl, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return GetPrinterInfoAsync(baseUrl.ToString(), ct);
    }

    public Task<PrinterJob?> GetJobAsync(Uri baseUrl, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return GetJobAsync(baseUrl.ToString(), ct);
    }

    public Task<PrinterCompositeStatus> GetCompositeStatusAsync(Uri baseUrl, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return GetCompositeStatusAsync(baseUrl.ToString(), ct);
    }

    // Camera Operations
    public Task<byte[]?> GetCameraSnapshotAsync(Uri baseUrl, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return GetCameraSnapshotAsync(baseUrl.ToString(), ct);
    }

    public Task<(string? StreamUrl, string? SnapshotUrl)> GetConfiguredCameraUrlsAsync(Uri baseUrl, int? frontendPort = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return GetConfiguredCameraUrlsAsync(baseUrl.ToString(), frontendPort, ct);
    }

    // Printer Control Operations
    public Task<bool> SendHomeAsync(Uri baseUrl, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return SendHomeAsync(baseUrl.ToString(), ct);
    }

    public Task<bool> HomeXYAsync(Uri baseUrl, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return HomeXYAsync(baseUrl.ToString(), ct);
    }

    public Task<bool> HomeZAsync(Uri baseUrl, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return HomeZAsync(baseUrl.ToString(), ct);
    }

    public Task<bool> SetTempsAsync(Uri baseUrl, double? hotend = null, double? bed = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return SetTempsAsync(baseUrl.ToString(), hotend, bed, ct);
    }

    public Task<bool> MoveAsync(Uri baseUrl, double? x = null, double? y = null, double? z = null, double? f = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return MoveAsync(baseUrl.ToString(), x, y, z, f, ct);
    }

    public Task<bool> MoveToAsync(Uri baseUrl, double? x = null, double? y = null, double? z = null, double? f = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return MoveToAsync(baseUrl.ToString(), x, y, z, f, ct);
    }

    // Print Job Control
    public Task<bool> PauseAsync(Uri baseUrl, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return PauseAsync(baseUrl.ToString(), ct);
    }

    public Task<bool> ResumeAsync(Uri baseUrl, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return ResumeAsync(baseUrl.ToString(), ct);
    }

    public Task<bool> EmergencyStopAsync(Uri baseUrl, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return EmergencyStopAsync(baseUrl.ToString(), ct);
    }

    public Task<bool> StartPrintAsync(Uri baseUrl, string fileName, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentNullException.ThrowIfNull(fileName);
        return StartPrintAsync(baseUrl.ToString(), fileName, ct);
    }

    // File Operations
    public Task<string[]> GetFileListAsync(Uri baseUrl, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return GetFileListAsync(baseUrl.ToString(), ct);
    }

    public Task<FileRoot[]> GetFileRootsAsync(Uri baseUrl, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return GetFileRootsAsync(baseUrl.ToString(), ct);
    }

    public Task<MoonrakerDirectoryInfo?> GetDirectoryAsync(Uri baseUrl, string path, bool extended = false, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentNullException.ThrowIfNull(path);
        return GetDirectoryAsync(baseUrl.ToString(), path, extended, ct);
    }

    public Task<DirectoryCreateResponse?> CreateDirectoryAsync(Uri baseUrl, string path, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentNullException.ThrowIfNull(path);
        return CreateDirectoryAsync(baseUrl.ToString(), path, ct);
    }

    public Task<bool> DeleteFileOrDirectoryAsync(Uri baseUrl, string path, bool force = false, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentNullException.ThrowIfNull(path);
        return DeleteFileOrDirectoryAsync(baseUrl.ToString(), path, force, ct);
    }

    public Task<bool> MoveFileAsync(Uri baseUrl, string source, string dest, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(dest);
        return MoveFileAsync(baseUrl.ToString(), source, dest, ct);
    }

    public Task<bool> CopyFileAsync(Uri baseUrl, string source, string dest, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(dest);
        return CopyFileAsync(baseUrl.ToString(), source, dest, ct);
    }

    public Task<bool> DeleteFileAsync(Uri baseUrl, string path, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentNullException.ThrowIfNull(path);
        return DeleteFileAsync(baseUrl.ToString(), path, ct);
    }

    public Task<Stream?> GetFileStreamAsync(Uri baseUrl, string filename, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentNullException.ThrowIfNull(filename);
        return GetFileStreamAsync(baseUrl.ToString(), filename, ct);
    }

    // File Metadata and Content
    public Task<GCodeMetadata?> GetFileMetadataAsync(Uri baseUrl, string filename, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentNullException.ThrowIfNull(filename);
        return GetFileMetadataAsync(baseUrl.ToString(), filename, ct);
    }

    public Task<bool> StartMetadataScanAsync(Uri baseUrl, string filename, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentNullException.ThrowIfNull(filename);
        return StartMetadataScanAsync(baseUrl.ToString(), filename, ct);
    }

    public Task<byte[]?> GetFileThumbnailAsync(Uri baseUrl, string filename, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentNullException.ThrowIfNull(filename);
        return GetFileThumbnailAsync(baseUrl.ToString(), filename, ct);
    }

    public Task<byte[]?> DownloadFileAsync(Uri baseUrl, string filename, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentNullException.ThrowIfNull(filename);
        return DownloadFileAsync(baseUrl.ToString(), filename, ct);
    }

    public Task<MoonrakerFileInfo[]> GetDetailedFileListAsync(Uri baseUrl, string root = "gcodes", string? path = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return GetDetailedFileListAsync(baseUrl.ToString(), root, path, ct);
    }

    // File Uploads
    public Task<bool> UploadGcodeAsync(Uri baseUrl, string fileName, Stream fileContent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentNullException.ThrowIfNull(fileName);
        ArgumentNullException.ThrowIfNull(fileContent);
        return UploadGcodeAsync(baseUrl.ToString(), fileName, fileContent, ct);
    }

    public Task<FileUploadResponse?> UploadFileAsync(Uri baseUrl, string root, string filename, Stream content, bool print = false, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(filename);
        ArgumentNullException.ThrowIfNull(content);
        return UploadFileAsync(baseUrl.ToString(), root, filename, content, print, ct);
    }

    public Task<FileUploadResponse?> UploadFileWithPathAsync(Uri baseUrl, string path, Stream content, bool print = false, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(content);
        return UploadFileWithPathAsync(baseUrl.ToString(), path, content, print, ct);
    }

    // History Operations
    public Task<HistoryListResponse?> GetHistoryListAsync(Uri baseUrl, int? limit = null, int? start = null, DateTime? since = null, DateTime? before = null, string? order = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return GetHistoryListAsync(baseUrl.ToString(), limit, start, since, before, order, ct);
    }

    public Task<HistoryJob?> GetHistoryJobAsync(Uri baseUrl, string jobId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentNullException.ThrowIfNull(jobId);
        return GetHistoryJobAsync(baseUrl.ToString(), jobId, ct);
    }

    public Task<bool> DeleteHistoryJobAsync(Uri baseUrl, string jobId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentNullException.ThrowIfNull(jobId);
        return DeleteHistoryJobAsync(baseUrl.ToString(), jobId, ct);
    }

    public Task<HistoryTotals?> GetHistoryTotalsAsync(Uri baseUrl, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return GetHistoryTotalsAsync(baseUrl.ToString(), ct);
    }

    public Task<bool> ResetHistoryTotalsAsync(Uri baseUrl, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return ResetHistoryTotalsAsync(baseUrl.ToString(), ct);
    }

    // Spoolman Integration
    public Task<SpoolmanStatus?> GetSpoolmanStatusAsync(Uri baseUrl, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return GetSpoolmanStatusAsync(baseUrl.ToString(), ct);
    }

    public Task<int?> GetSpoolmanActiveSpoolAsync(Uri baseUrl, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return GetSpoolmanActiveSpoolAsync(baseUrl.ToString(), ct);
    }

    public Task<bool> SetSpoolmanActiveSpoolAsync(Uri baseUrl, int? spoolId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return SetSpoolmanActiveSpoolAsync(baseUrl.ToString(), spoolId, ct);
    }

    public Task<string?> SpoolmanProxyRequestAsync(Uri baseUrl, string method, string path, string? query = null, object? body = null, bool useV2Response = false, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(path);
        return SpoolmanProxyRequestAsync(baseUrl.ToString(), method, path, query, body, useV2Response, ct);
    }

    // Spoolman Spool Operations
    public Task<string?> GetSpoolmanSpoolsAsync(Uri baseUrl, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return GetSpoolmanSpoolsAsync(baseUrl.ToString(), ct);
    }

    public Task<string?> GetSpoolmanSpoolByIdAsync(Uri baseUrl, int spoolId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return GetSpoolmanSpoolByIdAsync(baseUrl.ToString(), spoolId, ct);
    }

    public Task<string?> CreateSpoolmanSpoolAsync(Uri baseUrl, object spoolData, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentNullException.ThrowIfNull(spoolData);
        return CreateSpoolmanSpoolAsync(baseUrl.ToString(), spoolData, ct);
    }

    public Task<string?> UpdateSpoolmanSpoolAsync(Uri baseUrl, int spoolId, object spoolData, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentNullException.ThrowIfNull(spoolData);
        return UpdateSpoolmanSpoolAsync(baseUrl.ToString(), spoolId, spoolData, ct);
    }

    public Task<bool> DeleteSpoolmanSpoolAsync(Uri baseUrl, int spoolId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return DeleteSpoolmanSpoolAsync(baseUrl.ToString(), spoolId, ct);
    }

    // Spoolman Filament Operations
    public Task<string?> GetSpoolmanFilamentsAsync(Uri baseUrl, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return GetSpoolmanFilamentsAsync(baseUrl.ToString(), ct);
    }

    public Task<string?> GetSpoolmanFilamentByIdAsync(Uri baseUrl, int filamentId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return GetSpoolmanFilamentByIdAsync(baseUrl.ToString(), filamentId, ct);
    }

    public Task<string?> CreateSpoolmanFilamentAsync(Uri baseUrl, object filamentData, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentNullException.ThrowIfNull(filamentData);
        return CreateSpoolmanFilamentAsync(baseUrl.ToString(), filamentData, ct);
    }

    public Task<string?> UpdateSpoolmanFilamentAsync(Uri baseUrl, int filamentId, object filamentData, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentNullException.ThrowIfNull(filamentData);
        return UpdateSpoolmanFilamentAsync(baseUrl.ToString(), filamentId, filamentData, ct);
    }

    public Task<bool> DeleteSpoolmanFilamentAsync(Uri baseUrl, int filamentId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return DeleteSpoolmanFilamentAsync(baseUrl.ToString(), filamentId, ct);
    }

    // Spoolman Vendor Operations
    public Task<string?> GetSpoolmanVendorsAsync(Uri baseUrl, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return GetSpoolmanVendorsAsync(baseUrl.ToString(), ct);
    }

    public Task<string?> GetSpoolmanVendorByIdAsync(Uri baseUrl, int vendorId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return GetSpoolmanVendorByIdAsync(baseUrl.ToString(), vendorId, ct);
    }

    public Task<string?> CreateSpoolmanVendorAsync(Uri baseUrl, object vendorData, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentNullException.ThrowIfNull(vendorData);
        return CreateSpoolmanVendorAsync(baseUrl.ToString(), vendorData, ct);
    }

    public Task<string?> UpdateSpoolmanVendorAsync(Uri baseUrl, int vendorId, object vendorData, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentNullException.ThrowIfNull(vendorData);
        return UpdateSpoolmanVendorAsync(baseUrl.ToString(), vendorId, vendorData, ct);
    }

    public Task<bool> DeleteSpoolmanVendorAsync(Uri baseUrl, int vendorId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return DeleteSpoolmanVendorAsync(baseUrl.ToString(), vendorId, ct);
    }

    // Spoolman Utility and Advanced Operations
    public Task<bool> UseSpoolmanFilamentAsync(Uri baseUrl, double length, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return UseSpoolmanFilamentAsync(baseUrl.ToString(), length, ct);
    }

    public Task<string?> GetSpoolmanInfoAsync(Uri baseUrl, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return GetSpoolmanInfoAsync(baseUrl.ToString(), ct);
    }

    public Task<string?> GetSpoolmanHealthAsync(Uri baseUrl, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return GetSpoolmanHealthAsync(baseUrl.ToString(), ct);
    }

    public Task<string?> SearchSpoolmanSpoolsAsync(Uri baseUrl, string? query = null, bool? allowArchived = null, int? limit = null, int? offset = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return SearchSpoolmanSpoolsAsync(baseUrl.ToString(), query, allowArchived, limit, offset, ct);
    }

    public Task<string?> SearchSpoolmanFilamentsAsync(Uri baseUrl, string? query = null, int? limit = null, int? offset = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return SearchSpoolmanFilamentsAsync(baseUrl.ToString(), query, limit, offset, ct);
    }

    public Task<bool> ArchiveSpoolmanSpoolAsync(Uri baseUrl, int spoolId, bool archived = true, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return ArchiveSpoolmanSpoolAsync(baseUrl.ToString(), spoolId, archived, ct);
    }

    public Task<string?> GetSpoolmanStatsAsync(Uri baseUrl, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return GetSpoolmanStatsAsync(baseUrl.ToString(), ct);
    }

    public Task<string?> BackupSpoolmanAsync(Uri baseUrl, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return BackupSpoolmanAsync(baseUrl.ToString(), ct);
    }

    public Task<string?> GetSpoolmanIntegrationsAsync(Uri baseUrl, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return GetSpoolmanIntegrationsAsync(baseUrl.ToString(), ct);
    }
}
