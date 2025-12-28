using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Services.Printers
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
        Task<List<Printer>> GetPrintersForExportAsync(Guid[]? ids, CancellationToken ct);
        Task<bool> ExistsByNameOrServerUrlAsync(string name, string serverUrl, CancellationToken ct);
        Task<Printer?> FindByIpAddressAsync(string serverUrl, CancellationToken ct);
        // Higher-level orchestration methods that encapsulate external client calls and status aggregation
        Task<PrinterDto[]> GetAllWithStatusDtosAsync(CancellationToken ct);
        Task<PrinterStatusDto> GetStatusDtoAsync(Guid id, CancellationToken ct);
        Task<PrinterDto> GetPrinterDtoAsync(Guid id, CancellationToken ct);
        Task<PrinterCameraUrlsDto[]> GetCameraUrlsAsync(CancellationToken ct);
        Task<PrinterFastDto[]> GetAllFastDtosAsync(CancellationToken ct);
        // Get complete printer DTOs with live status merged in (replaces GetAllFastDtosAsync for new API)
        Task<CompletePrinterDto[]> GetAllCompleteDtosAsync(CancellationToken ct);
        // Export helpers: return bytes instead of streaming to HTTP response (HTTP handling done in API layer)
        Task<byte[]> BuildExportCsvAsync(Guid[]? ids, CancellationToken ct);
        Task<byte[]> BuildExportJsonAsync(Guid[]? ids, CancellationToken ct);
        // Return ready-to-serialize DTOs combining printer identity and capabilities
        Task<PrinterWithCapabilitiesDto[]> GetPrintersWithCapabilitiesDtosAsync(Guid[]? ids, CancellationToken ct);
        // Create a printer from DTO (resolves manufacturer/model, normalizes host, persists printer and returns DTO)
        Task<PrinterDto> CreatePrinterFromDtoAsync(CreatePrinterDto dto, CancellationToken ct);

        // Resolve hostname: returns normalized base url and resolved IP when available
        Task<ResolveHostnameResponse> ResolveHostnameAsync(string serverUrl, PrinterBackend backend, CancellationToken ct);

        // Extract a thumbnail URL from provider metadata given the base printer server URL
        string? ExtractThumbnailUrl(Dictionary<string, object> metadata, string printerServerUrl);

        // Return normalized server URLs for all printers (for discovery exclusion checks)

        // High-level printer operations that previously lived in controllers
        Task<byte[]?> GetCameraSnapshotAsync(Guid id, CancellationToken ct);
        Task<(string? streamUrl, string? snapshotUrl)> GetCameraUrlsForPrinterAsync(Guid id, CancellationToken ct);
        // History related operations (wrap Moonraker client and return shared DTOs)
        Task<HistoryListResponse> GetHistoryListAsync(Guid printerId, int? limit, int? start, DateTime? since, DateTime? before, string? order, CancellationToken ct);
        Task<HistoryJob> GetHistoryJobAsync(Guid printerId, string jobId, CancellationToken ct);
        Task<HistoryTotals> GetHistoryTotalsAsync(Guid printerId, CancellationToken ct);
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
        Task<bool> DisableMotorsAsync(Guid id, CancellationToken ct);
        Task<bool> StartPrintFromFileAsync(Guid id, string filename, CancellationToken ct);
        Task<bool> DeletePrinterFileAsync(Guid id, string filename, CancellationToken ct);
        Task<bool> UploadGcodeAsync(Guid id, string filename, Stream stream, CancellationToken ct);
        Task<PrinterFileDto[]> GetFileListAsync(Guid id, CancellationToken ct);

        // Get current print job status for a printer
        Task<PrintJobStatusDto?> GetPrintJobStatusAsync(Guid id, CancellationToken ct);

        // Bulk operations - domain logic that works with domain objects, not HTTP uploads
        Task<object> BulkCreatePrintersAsync(CreatePrinterDto[] printers, string duplicateHandling = "skip", CancellationToken ct = default);

        // File-based import - accepts Stream instead of IFormFile (HTTP abstraction removed)
        Task<object> ImportFromStreamAsync(Stream stream, string fileName, string duplicateHandling = "skip", CancellationToken ct = default);

        /// <summary>
        /// Refreshes camera URLs for a printer by querying the backend API.
        /// This updates the stored camera URLs in the database.
        /// </summary>
        /// <param name="id">The printer ID</param>
        /// <param name="ct">Cancellation token</param>
        /// <returns>Updated printer with refreshed camera URLs, or null if printer not found</returns>
        Task<PrinterDto?> RefreshCameraUrlsAsync(Guid id, CancellationToken ct);
    }
}
