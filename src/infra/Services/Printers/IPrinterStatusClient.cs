using System;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Contracts.Printers.Moonraker;
using Farm.Infrastructure.Contracts.Printers.PrusaLink;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Services.Printers
{
    /// <summary>
    /// Abstraction for retrieving printer status from different backend systems (Moonraker, PrusaLink, SDCP, OctoPrint).
    /// This interface normalizes status retrieval across different printer firmware/control systems.
    /// </summary>
    public interface IPrinterStatusClient
    {
        /// <summary>
        /// Retrieves the composite status for a printer from its backend system.
        /// </summary>
        /// <param name="printer">The printer entity containing connection details</param>
        /// <param name="ct">Cancellation token</param>
        /// <returns>Normalized composite status from the backend</returns>
        Task<PrinterStatusDto> GetPrinterStatusAsync(Printer printer, CancellationToken ct);

        /// <summary>
        /// Retrieves the full printer DTO with all details from the backend system.
        /// </summary>
        /// <param name="printer">The printer entity containing connection details</param>
        /// <param name="ct">Cancellation token</param>
        /// <returns>Full printer DTO with status and capabilities</returns>
        Task<PrinterDto> GetPrinterDtoAsync(Printer printer, CancellationToken ct);

        /// <summary>
        /// Gets a stream URL for the printer's camera (if available).
        /// </summary>
        /// <param name="printer">The printer entity containing connection details</param>
        /// <param name="ct">Cancellation token</param>
        /// <returns>Camera stream URL or null if not available</returns>
        Task<string?> GetCameraStreamUrlAsync(Printer printer, CancellationToken ct);

        /// <summary>
        /// Gets a snapshot URL for the printer's camera (if available).
        /// </summary>
        /// <param name="printer">The printer entity containing connection details</param>
        /// <param name="ct">Cancellation token</param>
        /// <returns>Camera snapshot URL or null if not available</returns>
        Task<string?> GetCameraSnapshotUrlAsync(Printer printer, CancellationToken ct);

        /// <summary>
        /// Checks if the printer's camera is available.
        /// </summary>
        /// <param name="printer">The printer entity containing connection details</param>
        /// <param name="ct">Cancellation token</param>
        /// <returns>True if camera is available, false otherwise</returns>
        Task<bool> IsCameraAvailableAsync(Printer printer, CancellationToken ct);

        /// <summary>
        /// Gets the printer backend type this client handles.
        /// </summary>
        PrinterBackend SupportedBackend { get; }
    }
}
