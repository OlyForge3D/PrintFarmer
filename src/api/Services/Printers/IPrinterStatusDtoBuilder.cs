using Farm.Infrastructure.Contracts.Printers;
using Farm.Infrastructure.Domain;

namespace Farm.Web.Api.Services.Printers
{
    /// <summary>
    /// Abstraction for building PrinterDto objects from printer entities and status data.
    /// Centralizes DTO construction logic across all backend implementations.
    /// Separates status translation from backend-specific API communication.
    /// </summary>
    public interface IPrinterStatusDtoBuilder
    {
        /// <summary>
        /// Builds a PrinterDto for a Moonraker/Klipper backend printer.
        /// </summary>
        /// <param name="printer">The printer entity from the database</param>
        /// <param name="status">The composite status from Moonraker API</param>
        /// <param name="cameraStreamUrl">Optional camera stream URL</param>
        /// <param name="cameraSnapshotUrl">Optional camera snapshot URL</param>
        /// <param name="spoolInfo">Optional spool information for Spoolman integration</param>
        /// <param name="ct">Cancellation token</param>
        /// <returns>Fully constructed PrinterDto with all Moonraker-specific details</returns>
        Task<PrinterDto> BuildMoonrakerDtoAsync(
            Printer printer,
            PrinterCompositeStatus status,
            string? cameraStreamUrl,
            string? cameraSnapshotUrl,
            PrinterSpoolInfoDto? spoolInfo,
            CancellationToken ct = default);

        /// <summary>
        /// Builds a PrinterDto for a PrusaLink backend printer.
        /// </summary>
        /// <param name="printer">The printer entity from the database</param>
        /// <param name="status">The composite status from PrusaLink API</param>
        /// <param name="ct">Cancellation token</param>
        /// <returns>Fully constructed PrinterDto with all PrusaLink-specific details</returns>
        Task<PrinterDto> BuildPrusaLinkDtoAsync(
            Printer printer,
            PrusaCompositeStatus status,
            CancellationToken ct = default);

        /// <summary>
        /// Builds a PrinterDto for an SDCP backend printer.
        /// </summary>
        /// <param name="printer">The printer entity from the database</param>
        /// <param name="status">The composite status from SDCP API</param>
        /// <param name="cameraStreamUrl">Optional camera stream URL</param>
        /// <param name="cameraSnapshotUrl">Optional camera snapshot URL</param>
        /// <param name="ct">Cancellation token</param>
        /// <returns>Fully constructed PrinterDto with all SDCP-specific details</returns>
        Task<PrinterDto> BuildSdcpDtoAsync(
            Printer printer,
            PrinterCompositeStatus status,
            string? cameraStreamUrl,
            string? cameraSnapshotUrl,
            CancellationToken ct = default);

        /// <summary>
        /// Builds a PrinterDto for an OctoPrint backend printer.
        /// </summary>
        /// <param name="printer">The printer entity from the database</param>
        /// <param name="printerJson">OctoPrint printer JSON response</param>
        /// <param name="jobJson">OctoPrint job JSON response</param>
        /// <param name="apiKey">OctoPrint API key for authentication</param>
        /// <param name="ct">Cancellation token</param>
        /// <returns>Fully constructed PrinterDto with all OctoPrint-specific details</returns>
        Task<PrinterDto> BuildOctoPrintDtoAsync(
            Printer printer,
            string printerJson,
            string jobJson,
            string apiKey,
            CancellationToken ct = default);

        /// <summary>
        /// Builds a base PrinterDto with common properties from printer entity and status.
        /// Useful for standardized DTO construction across backends.
        /// </summary>
        /// <param name="printer">The printer entity from the database</param>
        /// <param name="status">The composite status object</param>
        /// <param name="backend">The backend type (Moonraker, PrusaLink, SDCP, OctoPrint)</param>
        /// <param name="cameraStreamUrl">Optional camera stream URL</param>
        /// <param name="cameraSnapshotUrl">Optional camera snapshot URL</param>
        /// <param name="spoolInfo">Optional spool information</param>
        /// <returns>Basic PrinterDto with common properties set</returns>
        PrinterDto BuildBasePrinterDto(
            Printer printer,
            PrinterCompositeStatus status,
            PrinterBackend backend,
            string? cameraStreamUrl = null,
            string? cameraSnapshotUrl = null,
            PrinterSpoolInfoDto? spoolInfo = null);

        /// <summary>
        /// Extracts temperature data from a composite status object.
        /// Handles null/missing temperature values gracefully.
        /// </summary>
        /// <param name="status">The composite status object</param>
        /// <returns>Tuple containing (HotendTemp, BedTemp, HotendTarget, BedTarget)</returns>
        (double? HotendTemp, double? BedTemp, double? HotendTarget, double? BedTarget) ExtractTemperatureData(PrinterCompositeStatus status);

        /// <summary>
        /// Extracts position data from a composite status object.
        /// Handles null/missing position values gracefully.
        /// </summary>
        /// <param name="status">The composite status object</param>
        /// <returns>Tuple containing (X, Y, Z) coordinates</returns>
        (double? X, double? Y, double? Z) ExtractPositionData(PrinterCompositeStatus status);

        /// <summary>
        /// Extracts print job data from a composite status object.
        /// Handles null/missing job information gracefully.
        /// </summary>
        /// <param name="status">The composite status object</param>
        /// <returns>Tuple containing (JobName, Progress, State, ThumbnailUrl)</returns>
        (string? JobName, double? Progress, string? State, string? ThumbnailUrl) ExtractJobData(PrinterCompositeStatus status);
    }
}
