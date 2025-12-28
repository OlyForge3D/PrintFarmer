using System;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Contracts.Printers.PrusaLink;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Telemetry;

namespace Farm.Backend.Plugin.PrusaLink
{
    /// <summary>
    /// Printer status client for PrusaLink backend (Prusa 3D printer firmware).
    /// Implements IPrinterStatusClient for PrusaLink-specific status retrieval.
    /// </summary>
    public class PrusaLinkStatusClient : IPrinterStatusClient
    {
        private readonly IPrusaLinkClient _client;
        private readonly ICircuitBreakerService _circuitBreaker;
        private readonly IUnifiedLoggingService _logger;

        public PrinterBackend SupportedBackend => PrinterBackend.PrusaLink;

        public PrusaLinkStatusClient(
            IPrusaLinkClient client,
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
                CircuitBreaker breaker = _circuitBreaker.GetCircuitBreaker($"prusalink-{printer.Id}");

                PrusaCompositeStatus status = await breaker.ExecuteAsync(
                    async ct => await _client.GetCompositeStatusAsync(printer.BackendUrl, printer.ApiKey, ct),
                    ct);

                return new PrinterStatusDto(
                    Id: printer.Id,
                    IsOnline: status.IsOnline,
                    State: status.State,
                    Progress: status.Progress,
                    JobName: status.JobName,
                    ThumbnailUrl: status.ThumbnailUrl,
                    CameraStreamUrl: status.CameraStreamUrl,
                    CameraSnapshotUrl: status.CameraSnapshotUrl);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning($"[PrusaLink] Status timeout for printer {printer.Id}");
                return CreateOfflineStatus(printer.Id);
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"[PrusaLink] Error getting status for printer {printer.Id}: {ex.Message}");
                return CreateOfflineStatus(printer.Id);
            }
        }

        public async Task<PrinterDto> GetPrinterDtoAsync(Printer printer, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(printer);

            try
            {
                CircuitBreaker breaker = _circuitBreaker.GetCircuitBreaker($"prusalink-{printer.Id}");

                PrusaCompositeStatus status = await breaker.ExecuteAsync(
                    async ct => await _client.GetCompositeStatusAsync(printer.BackendUrl, printer.ApiKey, ct),
                    ct);

                if (status == null)
                {
                    throw new InvalidOperationException($"Failed to retrieve status for printer {printer.Id}");
                }

                return await _client.CreatePrinterDtoAsync(printer, status, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError($"[PrusaLink] Error getting printer DTO for {printer.Id}: {ex.Message}");
                throw;
            }
        }

        public async Task<string?> GetCameraStreamUrlAsync(Printer printer, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(printer);

            // PrusaLink camera URLs are not supported due to encoding issues
            _logger.LogWarning($"[PrusaLink] Camera stream URLs are not supported for PrusaLink printer {printer.Id}");
            await Task.CompletedTask;
            return null;
        }

        public async Task<string?> GetCameraSnapshotUrlAsync(Printer printer, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(printer);

            // PrusaLink camera URLs are not supported due to encoding issues
            _logger.LogWarning($"[PrusaLink] Camera snapshot URLs are not supported for PrusaLink printer {printer.Id}");
            await Task.CompletedTask;
            return null;
        }

        public async Task<bool> IsCameraAvailableAsync(Printer printer, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(printer);

            // PrusaLink does not provide camera URLs due to encoding issues
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
