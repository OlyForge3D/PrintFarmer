using Farm.Infrastructure.Contracts.Printers;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Telemetry;

namespace Farm.Web.Api.Services.Printers
{
    /// <summary>
    /// Builds PrinterDto objects from printer entities and backend-specific status data.
    /// Centralizes DTO construction logic and standardizes property mapping across all backends.
    /// </summary>
    public class PrinterStatusDtoBuilder : IPrinterStatusDtoBuilder
    {
        private readonly IUnifiedLoggingService _logger;

        public PrinterStatusDtoBuilder(IUnifiedLoggingService logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<PrinterDto> BuildMoonrakerDtoAsync(
            Printer printer,
            PrinterCompositeStatus status,
            string? cameraStreamUrl,
            string? cameraSnapshotUrl,
            PrinterSpoolInfoDto? spoolInfo,
            CancellationToken ct = default)
        {
            if (printer == null) throw new ArgumentNullException(nameof(printer));
            if (status == null) throw new ArgumentNullException(nameof(status));

            // Extract common data
            var temps = ExtractTemperatureData(status);
            var position = ExtractPositionData(status);
            var job = ExtractJobData(status);

            // Build with Moonraker-specific details
            return new PrinterDto(
                Id: printer.Id,
                Name: printer.Name,
                ServerUrl: printer.ServerUrl,
                Notes: printer.Notes,
                IsOnline: status.IsOnline,
                State: job.State,
                ManufacturerName: printer.Manufacturer?.Name,
                ModelName: printer.Model?.Name,
                Progress: job.Progress,
                JobName: job.JobName,
                ThumbnailUrl: job.ThumbnailUrl,
                CameraStreamUrl: cameraStreamUrl,
                CameraSnapshotUrl: cameraSnapshotUrl,
                X: position.X,
                Y: position.Y,
                Z: position.Z,
                HotendTemp: temps.HotendTemp,
                BedTemp: temps.BedTemp,
                HotendTarget: temps.HotendTarget,
                BedTarget: temps.BedTarget,
                Backend: PrinterBackend.Moonraker,
                ApiKey: printer.ApiKey,
                OriginalServerUrl: printer.OriginalServerUrl,
                IpAddress: printer.IpAddress,
                SpoolInfo: spoolInfo,
                BackendPort: printer.BackendPort,
                FrontendPort: printer.FrontendPort
            );
        }

        public async Task<PrinterDto> BuildPrusaLinkDtoAsync(
            Printer printer,
            PrusaCompositeStatus status,
            CancellationToken ct = default)
        {
            if (printer == null) throw new ArgumentNullException(nameof(printer));
            if (status == null) throw new ArgumentNullException(nameof(status));

            // PrusaLink provides different data structure - map accordingly
            // Note: PrusaLink doesn't provide temperature or position data in CompositeStatus
            return new PrinterDto(
                Id: printer.Id,
                Name: printer.Name,
                ServerUrl: printer.ServerUrl,
                Notes: printer.Notes,
                IsOnline: status.IsOnline,
                State: status.State,
                ManufacturerName: printer.Manufacturer?.Name,
                ModelName: printer.Model?.Name,
                Progress: status.Progress,
                JobName: status.JobName,
                ThumbnailUrl: status.ThumbnailUrl,
                CameraStreamUrl: null, // PrusaLink camera URLs returned separately; not in composite status
                CameraSnapshotUrl: null,
                X: null, // PrusaLink doesn't provide position data
                Y: null,
                Z: null,
                HotendTemp: null, // PrusaLink doesn't provide temperature in composite status
                BedTemp: null,
                HotendTarget: null,
                BedTarget: null,
                Backend: PrinterBackend.PrusaLink,
                ApiKey: printer.ApiKey,
                OriginalServerUrl: printer.OriginalServerUrl,
                IpAddress: printer.IpAddress,
                SpoolInfo: null, // PrusaLink doesn't have integrated spool tracking
                BackendPort: printer.BackendPort,
                FrontendPort: printer.FrontendPort
            );
        }

        public async Task<PrinterDto> BuildSdcpDtoAsync(
            Printer printer,
            PrinterCompositeStatus status,
            string? cameraStreamUrl,
            string? cameraSnapshotUrl,
            CancellationToken ct = default)
        {
            if (printer == null) throw new ArgumentNullException(nameof(printer));
            if (status == null) throw new ArgumentNullException(nameof(status));

            // Extract common data
            var temps = ExtractTemperatureData(status);
            var position = ExtractPositionData(status);
            var job = ExtractJobData(status);

            // Build with SDCP-specific details
            return new PrinterDto(
                Id: printer.Id,
                Name: printer.Name,
                ServerUrl: printer.ServerUrl,
                Notes: printer.Notes,
                IsOnline: status.IsOnline,
                State: job.State,
                ManufacturerName: printer.Manufacturer?.Name,
                ModelName: printer.Model?.Name,
                Progress: job.Progress,
                JobName: job.JobName,
                ThumbnailUrl: job.ThumbnailUrl,
                CameraStreamUrl: cameraStreamUrl,
                CameraSnapshotUrl: cameraSnapshotUrl,
                X: position.X,
                Y: position.Y,
                Z: position.Z,
                HotendTemp: temps.HotendTemp,
                BedTemp: temps.BedTemp,
                HotendTarget: temps.HotendTarget,
                BedTarget: temps.BedTarget,
                Backend: PrinterBackend.SDCP,
                ApiKey: printer.ApiKey,
                OriginalServerUrl: printer.OriginalServerUrl,
                IpAddress: printer.IpAddress,
                SpoolInfo: null, // SDCP doesn't have integrated spool tracking
                BackendPort: printer.BackendPort,
                FrontendPort: printer.FrontendPort
            );
        }

        public async Task<PrinterDto> BuildOctoPrintDtoAsync(
            Printer printer,
            string printerJson,
            string jobJson,
            string apiKey,
            CancellationToken ct = default)
        {
            if (printer == null) throw new ArgumentNullException(nameof(printer));
            if (string.IsNullOrWhiteSpace(printerJson)) throw new ArgumentNullException(nameof(printerJson));
            if (string.IsNullOrWhiteSpace(jobJson)) throw new ArgumentNullException(nameof(jobJson));

            // Note: OctoPrint JSON parsing not yet fully implemented
            // For now, return offline status
            return new PrinterDto(
                Id: printer.Id,
                Name: printer.Name,
                ServerUrl: printer.ServerUrl,
                Notes: printer.Notes,
                IsOnline: false,
                State: "Offline",
                ManufacturerName: printer.Manufacturer?.Name,
                ModelName: printer.Model?.Name,
                Progress: null,
                JobName: null,
                ThumbnailUrl: null,
                CameraStreamUrl: null,
                CameraSnapshotUrl: null,
                X: null,
                Y: null,
                Z: null,
                HotendTemp: null,
                BedTemp: null,
                HotendTarget: null,
                BedTarget: null,
                Backend: PrinterBackend.OctoPrint,
                ApiKey: apiKey,
                OriginalServerUrl: printer.OriginalServerUrl,
                IpAddress: printer.IpAddress,
                SpoolInfo: null,
                BackendPort: printer.BackendPort,
                FrontendPort: printer.FrontendPort
            );
        }

        public PrinterDto BuildBasePrinterDto(
            Printer printer,
            PrinterCompositeStatus status,
            PrinterBackend backend,
            string? cameraStreamUrl = null,
            string? cameraSnapshotUrl = null,
            PrinterSpoolInfoDto? spoolInfo = null)
        {
            if (printer == null) throw new ArgumentNullException(nameof(printer));
            if (status == null) throw new ArgumentNullException(nameof(status));

            // Extract all data
            var temps = ExtractTemperatureData(status);
            var position = ExtractPositionData(status);
            var job = ExtractJobData(status);

            // Build with common properties
            return new PrinterDto(
                Id: printer.Id,
                Name: printer.Name,
                ServerUrl: printer.ServerUrl,
                Notes: printer.Notes,
                IsOnline: status.IsOnline,
                State: job.State,
                ManufacturerName: printer.Manufacturer?.Name,
                ModelName: printer.Model?.Name,
                Progress: job.Progress,
                JobName: job.JobName,
                ThumbnailUrl: job.ThumbnailUrl,
                CameraStreamUrl: cameraStreamUrl,
                CameraSnapshotUrl: cameraSnapshotUrl,
                X: position.X,
                Y: position.Y,
                Z: position.Z,
                HotendTemp: temps.HotendTemp,
                BedTemp: temps.BedTemp,
                HotendTarget: temps.HotendTarget,
                BedTarget: temps.BedTarget,
                Backend: backend,
                ApiKey: printer.ApiKey,
                OriginalServerUrl: printer.OriginalServerUrl,
                IpAddress: printer.IpAddress,
                SpoolInfo: spoolInfo,
                BackendPort: printer.BackendPort,
                FrontendPort: printer.FrontendPort
            );
        }

        public (double? HotendTemp, double? BedTemp, double? HotendTarget, double? BedTarget) ExtractTemperatureData(PrinterCompositeStatus status)
        {
            if (status == null) throw new ArgumentNullException(nameof(status));

            return (
                HotendTemp: status.HotendTemp,
                BedTemp: status.BedTemp,
                HotendTarget: status.HotendTarget,
                BedTarget: status.BedTarget
            );
        }

        public (double? X, double? Y, double? Z) ExtractPositionData(PrinterCompositeStatus status)
        {
            if (status == null) throw new ArgumentNullException(nameof(status));

            return (
                X: status.X,
                Y: status.Y,
                Z: status.Z
            );
        }

        public (string? JobName, double? Progress, string? State, string? ThumbnailUrl) ExtractJobData(PrinterCompositeStatus status)
        {
            if (status == null) throw new ArgumentNullException(nameof(status));

            return (
                JobName: status.JobName,
                Progress: status.Progress,
                State: status.State,
                ThumbnailUrl: status.ThumbnailUrl
            );
        }
    }
}
