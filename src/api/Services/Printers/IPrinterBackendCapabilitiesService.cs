using Farm.Infrastructure;

namespace Farm.Web.Api.Services.Printers;

/// <summary>
/// Service for exposing backend capabilities (what features a printer's backend supports)
/// as opposed to hardware capabilities (nozzle size, build volume, etc.).
/// 
/// Backend capabilities are determined by which interfaces the backend client implements
/// (ISupportsCamera, ISupportsFileUpload, etc.) and are exposed to the UI so it can
/// enable/disable features appropriately.
/// </summary>
public interface IPrinterBackendCapabilitiesService
{
    /// <summary>
    /// Get backend capabilities for a specific printer.
    /// </summary>
    /// <param name="printerId">The printer identifier</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Backend capabilities for the printer, or null if printer not found</returns>
    Task<PrinterBackendCapabilitiesDto?> GetByPrinterIdAsync(Guid printerId, CancellationToken ct);

    /// <summary>
    /// Get backend capabilities for all printers.
    /// </summary>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Backend capabilities for all printers</returns>
    Task<IEnumerable<PrinterBackendCapabilitiesDto>> GetAllAsync(CancellationToken ct);

    /// <summary>
    /// Get backend capabilities for multiple specific printers.
    /// </summary>
    /// <param name="printerIds">Printer identifiers to fetch capabilities for</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Backend capabilities for the specified printers</returns>
    Task<IEnumerable<PrinterBackendCapabilitiesDto>> GetByIdsAsync(Guid[] printerIds, CancellationToken ct);
}
