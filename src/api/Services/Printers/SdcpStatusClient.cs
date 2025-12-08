using System;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Contracts.Printers.Moonraker;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Services.Interfaces;

namespace Farm.Web.Api.Services.Printers
{
    /// <summary>
    /// Printer status client for SDCP backend (Simple Data Communication Protocol).
    /// Implements IPrinterStatusClient for SDCP-specific status retrieval.
    /// </summary>
    public class SdcpStatusClient : IPrinterStatusClient
    {
        private readonly ISdcpClient _client;
        private readonly ICircuitBreakerService _circuitBreaker;
        private readonly IUnifiedLoggingService _logger;

        public PrinterBackend SupportedBackend => PrinterBackend.SDCP;

        public SdcpStatusClient(
            ISdcpClient client,
            ICircuitBreakerService circuitBreaker,
            IUnifiedLoggingService logger)
        {
            ArgumentNullException.ThrowIfNull(client);
            ArgumentNullException.ThrowIfNull(circuitBreaker);
            ArgumentNullException.ThrowIfNull(logger);

            _client = client;
            _circuitBreaker = circuitBreaker;
            _logger = logger;
        }

        public async Task<PrinterStatusDto> GetPrinterStatusAsync(Printer printer, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(printer);

            try
            {
                CircuitBreaker breaker = _circuitBreaker.GetCircuitBreaker($"sdcp-{printer.Id}");
                
                PrinterCompositeStatus status = await breaker.ExecuteAsync(
                    async ct => await _client.GetCompositeStatusAsync(printer.BackendUrl, ct),
                    ct);
                
                return new PrinterStatusDto(
                    Id: printer.Id,
                    IsOnline: status.IsOnline,
                    State: status.State,
                    Progress: status.Progress,
                    JobName: status.JobName,
                    ThumbnailUrl: status.ThumbnailUrl,
                    CameraStreamUrl: status.CameraStreamUrl,
                    CameraSnapshotUrl: status.CameraSnapshotUrl,
                    X: status.X,
                    Y: status.Y,
                    Z: status.Z,
                    HotendTemp: status.HotendTemp,
                    BedTemp: status.BedTemp,
                    HotendTarget: status.HotendTarget,
                    BedTarget: status.BedTarget);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning($"[SDCP] Status timeout for printer {printer.Id}");
                return CreateOfflineStatus(printer.Id);
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"[SDCP] Error getting status for printer {printer.Id}: {ex.Message}");
                return CreateOfflineStatus(printer.Id);
            }
        }

        public async Task<PrinterDto> GetPrinterDtoAsync(Printer printer, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(printer);

            try
            {
                CircuitBreaker breaker = _circuitBreaker.GetCircuitBreaker($"sdcp-{printer.Id}");
                
                PrinterCompositeStatus status = await breaker.ExecuteAsync(
                    async ct => await _client.GetCompositeStatusAsync(printer.BackendUrl, ct),
                    ct);
                
                return await _client.CreatePrinterDtoAsync(printer, status, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError($"[SDCP] Error getting printer DTO for {printer.Id}: {ex.Message}");
                throw;
            }
        }

        public async Task<string?> GetCameraStreamUrlAsync(Printer printer, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(printer);

            try
            {
                return await _client.GetCameraUrlAsync(printer.BackendUrl, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"[SDCP] Error getting camera stream URL for {printer.Id}: {ex.Message}");
                return null;
            }
        }

        public async Task<string?> GetCameraSnapshotUrlAsync(Printer printer, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(printer);

            try
            {
                return await _client.GetCameraSnapshotUrlAsync(printer.BackendUrl, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"[SDCP] Error getting camera snapshot URL for {printer.Id}: {ex.Message}");
                return null;
            }
        }

        public async Task<bool> IsCameraAvailableAsync(Printer printer, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(printer);

            try
            {
                string? streamUrl = await GetCameraStreamUrlAsync(printer, ct);
                return !string.IsNullOrEmpty(streamUrl);
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"[SDCP] Error checking camera availability for {printer.Id}: {ex.Message}");
                return false;
            }
        }

        private static PrinterStatusDto CreateOfflineStatus(Guid printerId)
        {
            return new PrinterStatusDto(
                Id: printerId,
                IsOnline: false,
                State: null,
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
                BedTarget: null);
        }
    }
}
