using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;

namespace Farm.Web.Api.Services.Printers
{
    public interface IPrintersService
    {
        Task<List<Printer>> GetAllAsync(CancellationToken ct);
        Task<List<Printer>> GetAllWithIncludesAsync(CancellationToken ct);
        Task<Printer?> FindByIdAsync(Guid id, CancellationToken ct);
        Task<Printer?> FindByIdWithIncludesAsync(Guid id, CancellationToken ct);
        Task AddAsync(Printer p, CancellationToken ct);
        Task RemoveAsync(Printer p, CancellationToken ct);
        Task SaveChangesAsync(CancellationToken ct);
        Task SaveCapabilitiesAsync(Farm.Infrastructure.Domain.PrinterCapabilities capabilities, CancellationToken ct);
        Task<List<Printer>> GetPrintersForExportAsync(Guid[]? ids, CancellationToken ct);
        Task<bool> ExistsByNameOrServerUrlAsync(string name, string serverUrl, CancellationToken ct);
        Task<Dictionary<Guid, Farm.Infrastructure.Domain.PrinterCapabilities>> GetCapabilitiesDictionaryAsync(Guid[]? ids, CancellationToken ct);
        Task<List<Farm.Infrastructure.Domain.PrinterCapabilities>> GetCapabilitiesListAsync(Guid[]? ids, CancellationToken ct);
        Task<Farm.Infrastructure.Domain.PrinterCapabilities?> GetCapabilitiesByPrinterIdAsync(Guid id, CancellationToken ct);
        // Higher-level orchestration methods that encapsulate external client calls and status aggregation
        Task<Farm.Web.Shared.PrinterDto[]> GetAllWithStatusDtosAsync(CancellationToken ct);
        Task<Farm.Web.Shared.PrinterStatusDto> GetStatusDtoAsync(Guid id, CancellationToken ct);
        Task<Farm.Web.Shared.PrinterDto> GetPrinterDtoAsync(Guid id, CancellationToken ct);
        Task<Farm.Web.Shared.PrinterCameraUrlsDto[]> GetCameraUrlsAsync(CancellationToken ct);
        Task<Farm.Web.Shared.PrinterFastDto[]> GetAllFastDtosAsync(CancellationToken ct);
        // Export helpers: build CSV bytes or stream export directly
        Task<byte[]> BuildExportCsvAsync(Guid[]? ids, CancellationToken ct);
        Task StreamExportToResponseAsync(Guid[]? ids, string format, HttpResponse response, CancellationToken ct);
        // Return ready-to-serialize DTOs combining printer identity and capabilities
        Task<Farm.Web.Shared.PrinterWithCapabilitiesDto[]> GetPrintersWithCapabilitiesDtosAsync(Guid[]? ids, CancellationToken ct);
        // Create a printer from DTO (resolves manufacturer/model, normalizes host, persists printer and returns DTO)
        Task<Farm.Web.Shared.PrinterDto> CreatePrinterFromDtoAsync(Farm.Web.Shared.CreatePrinterDto dto, CancellationToken ct);

        // Resolve hostname: returns normalized base url and resolved IP when available
        Task<Farm.Web.Shared.ResolveHostnameResponse> ResolveHostnameAsync(string serverUrl, Farm.Web.Shared.PrinterBackend backend, CancellationToken ct);

        // Extract a thumbnail URL from provider metadata given the base printer server URL
        string? ExtractThumbnailUrl(Dictionary<string, object> metadata, string printerServerUrl);

        // Return normalized server URLs for all printers (for discovery exclusion checks)
        Task<HashSet<string>> GetAllNormalizedServerUrlsAsync(int defaultPort, CancellationToken ct);
        // Normalize a server URL (ensures scheme and default port) for stable comparisons
        string NormalizeServerUrl(string? input, int defaultPort);

        // High-level printer operations that previously lived in controllers
        Task<byte[]?> GetCameraSnapshotAsync(Guid id, CancellationToken ct);
        Task<(string? streamUrl, string? snapshotUrl)> GetCameraUrlsForPrinterAsync(Guid id, CancellationToken ct);
        // History related operations (wrap Moonraker client and return shared DTOs)
        Task<Farm.Web.Shared.HistoryListResponse> GetHistoryListAsync(Guid printerId, int? limit, int? start, DateTime? since, DateTime? before, string? order, CancellationToken ct);
        Task<Farm.Web.Shared.HistoryJob> GetHistoryJobAsync(Guid printerId, string jobId, CancellationToken ct);
        Task<Farm.Web.Shared.HistoryTotals> GetHistoryTotalsAsync(Guid printerId, CancellationToken ct);
        Task<bool> DeleteHistoryJobAsync(Guid printerId, string jobId, CancellationToken ct);
        Task<bool> EnableCameraAsync(Guid id, CancellationToken ct);
        Task<bool> DisableCameraAsync(Guid id, CancellationToken ct);
        Task<bool> SendHomeAsync(Guid id, CancellationToken ct);
        Task<bool> HomeXYAsync(Guid id, CancellationToken ct);
        Task<bool> HomeZAsync(Guid id, CancellationToken ct);
        Task<bool> SetTempsAsync(Guid id, double? hotend, double? bed, CancellationToken ct);
        Task<bool> MoveAsync(Guid id, double? x, double? y, double? z, double? f, CancellationToken ct);
        Task<bool> MoveToAsync(Guid id, double? x, double? y, double? z, double? f, CancellationToken ct);
        Task<bool> PauseAsync(Guid id, CancellationToken ct);
        Task<bool> ResumeAsync(Guid id, CancellationToken ct);
        Task<bool> EmergencyStopAsync(Guid id, CancellationToken ct);
        Task<bool> FirmwareRestartAsync(Guid id, CancellationToken ct);
        Task<bool> StartPrintFromFileAsync(Guid id, string filename, CancellationToken ct);
        Task<bool> StartPrintAsync(Guid id, string filename, CancellationToken ct);
        Task<bool> UploadGcodeAsync(Guid id, string filename, System.IO.Stream stream, CancellationToken ct);
        Task<string[]> GetFileListAsync(Guid id, CancellationToken ct);

        // Get current print job status for a printer
        Task<Farm.Web.Shared.PrintJobStatusDto?> GetPrintJobStatusAsync(Guid id, CancellationToken ct);

        // Bulk operations
        Task<object> BulkCreatePrintersAsync(Farm.Web.Shared.CreatePrinterDto[] printers, string duplicateHandling = "skip", CancellationToken ct = default);
    }
}
