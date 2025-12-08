using System;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Services.Interfaces;

namespace Farm.Web.Api.Services.Printers
{
    /// <summary>
    /// Printer status client for OctoPrint backend (Klipper/GCODE printer control).
    /// Implements IPrinterStatusClient for OctoPrint-specific status retrieval.
    /// </summary>
    public class OctoPrintStatusClient : IPrinterStatusClient
    {
        private readonly IOctoPrintClient _client;
        private readonly ICircuitBreakerService _circuitBreaker;
        private readonly IUnifiedLoggingService _logger;

        public PrinterBackend SupportedBackend => PrinterBackend.OctoPrint;

        public OctoPrintStatusClient(
            IOctoPrintClient client,
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
                CircuitBreaker breaker = _circuitBreaker.GetCircuitBreaker($"octoprint-{printer.Id}");
                
                // Retrieve both printer state and job status
                string printerJson = await breaker.ExecuteAsync(
                    async ct => await _client.GetPrinterStateAsync(printer.BackendUrl, printer.ApiKey ?? string.Empty),
                    ct);
                
                string jobJson = await breaker.ExecuteAsync(
                    async ct => await _client.GetJobStatusAsync(printer.BackendUrl, printer.ApiKey ?? string.Empty),
                    ct);
                
                // For now, return basic offline status - OctoPrint status parsing needs to be implemented
                _logger.LogWarning($"[OctoPrint] Status retrieval not fully implemented for printer {printer.Id}");
                return CreateOfflineStatus(printer.Id);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning($"[OctoPrint] Status timeout for printer {printer.Id}");
                return CreateOfflineStatus(printer.Id);
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"[OctoPrint] Error getting status for printer {printer.Id}: {ex.Message}");
                return CreateOfflineStatus(printer.Id);
            }
        }

        public async Task<PrinterDto> GetPrinterDtoAsync(Printer printer, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(printer);

            try
            {
                CircuitBreaker breaker = _circuitBreaker.GetCircuitBreaker($"octoprint-{printer.Id}");
                
                string printerJson = await breaker.ExecuteAsync(
                    async ct => await _client.GetPrinterStateAsync(printer.BackendUrl, printer.ApiKey ?? string.Empty),
                    ct);
                
                string jobJson = await breaker.ExecuteAsync(
                    async ct => await _client.GetJobStatusAsync(printer.BackendUrl, printer.ApiKey ?? string.Empty),
                    ct);
                
                return await _client.CreatePrinterDtoAsync(printer, printerJson, jobJson, printer.ApiKey ?? string.Empty, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError($"[OctoPrint] Error getting printer DTO for {printer.Id}: {ex.Message}");
                throw;
            }
        }

        public async Task<string?> GetCameraStreamUrlAsync(Printer printer, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(printer);

            // OctoPrint camera support would be implemented here
            _logger.LogWarning($"[OctoPrint] Camera stream URLs not yet implemented for printer {printer.Id}");
            await Task.CompletedTask;
            return null;
        }

        public async Task<string?> GetCameraSnapshotUrlAsync(Printer printer, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(printer);

            // OctoPrint camera support would be implemented here
            _logger.LogWarning($"[OctoPrint] Camera snapshot URLs not yet implemented for printer {printer.Id}");
            await Task.CompletedTask;
            return null;
        }

        public async Task<bool> IsCameraAvailableAsync(Printer printer, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(printer);

            // OctoPrint camera support not yet implemented
            await Task.CompletedTask;
            return false;
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
                CameraSnapshotUrl: null);
        }
    }
}
