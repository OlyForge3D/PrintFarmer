using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Farm.Backend.Plugin.Core;
using Farm.Infrastructure;
using Farm.Infrastructure.Contracts.Printers.Moonraker;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Telemetry;

namespace Farm.Backend.Plugin.Moonraker
{
    /// <summary>
    /// Printer status client for Moonraker backend (Klipper 3D printer firmware).
    /// Implements IPrinterStatusClient for Moonraker-specific status retrieval.
    /// This status client is provided by the Moonraker backend plugin.
    /// </summary>
    public class MoonrakerStatusClient : IPrinterStatusClient
    {
        private readonly IMoonrakerClient _client;
        private readonly ICircuitBreakerService _circuitBreaker;
        private readonly IUnifiedLoggingService _logger;

        public PrinterBackend SupportedBackend => PrinterBackend.Moonraker;

        public MoonrakerStatusClient(
            IMoonrakerClient client,
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
                string moonrakerUrl = BuildMoonrakerUrl(printer.ServerUrl, printer.BackendPort);
                _logger.LogInformation($"[Moonraker] GetPrinterStatusAsync for {printer.Name} (ID={printer.Id}): ServerUrl={printer.ServerUrl}, BackendPort={printer.BackendPort}, FinalUrl={moonrakerUrl}");
                
                CircuitBreaker breaker = _circuitBreaker.GetCircuitBreaker($"moonraker-{printer.Id}");
                
                PrinterCompositeStatus status = await breaker.ExecuteAsync(
                    async ct => await _client.GetCompositeStatusAsync(moonrakerUrl, ct),
                    ct);
                
                _logger.LogInformation($"[Moonraker] Status received for {printer.Name}: IsOnline={status.IsOnline}, State={status.State}");
                
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
                _logger.LogWarning($"[Moonraker] Status timeout for printer {printer.Id}");
                return CreateOfflineStatus(printer.Id);
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"[Moonraker] Error getting status for printer {printer.Id}: {ex.Message}");
                return CreateOfflineStatus(printer.Id);
            }
        }

        public async Task<PrinterDto> GetPrinterDtoAsync(Printer printer, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(printer);

            try
            {
                string moonrakerUrl = BuildMoonrakerUrl(printer.ServerUrl, printer.BackendPort);
                _logger.LogInformation($"[Moonraker] GetPrinterDtoAsync for {printer.Name} (ID={printer.Id}): ServerUrl={printer.ServerUrl}, BackendPort={printer.BackendPort}, FinalUrl={moonrakerUrl}");
                
                CircuitBreaker breaker = _circuitBreaker.GetCircuitBreaker($"moonraker-{printer.Id}");
                
                PrinterCompositeStatus status = await breaker.ExecuteAsync(
                    async ct => await _client.GetCompositeStatusAsync(moonrakerUrl, ct),
                    ct);
                
                // Get Spoolman integration info for Moonraker
                PrinterSpoolInfoDto? spoolInfo = await GetSpoolInfoAsync(moonrakerUrl, ct);
                
                _logger.LogInformation($"[Moonraker] DTO created for {printer.Name}: IsOnline={status.IsOnline}, State={status.State}");
                
                return await _client.CreatePrinterDtoAsync(printer, status, spoolInfo, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError($"[Moonraker] Error getting printer DTO for {printer.Id}: {ex.Message}");
                throw;
            }
        }

        private async Task<PrinterSpoolInfoDto?> GetSpoolInfoAsync(string serverUrl, CancellationToken ct)
        {
            try
            {
                int? activeSpoolId = await _client.GetSpoolmanActiveSpoolAsync(serverUrl, ct);
                if (activeSpoolId == null)
                {
                    return new PrinterSpoolInfoDto(HasActiveSpool: false);
                }

                string? spoolDetailsJson = await _client.GetSpoolmanSpoolByIdAsync(serverUrl, activeSpoolId.Value, ct);
                if (string.IsNullOrWhiteSpace(spoolDetailsJson))
                {
                    return new PrinterSpoolInfoDto(HasActiveSpool: true, ActiveSpoolId: activeSpoolId);
                }

                try
                {
                    using JsonDocument doc = JsonDocument.Parse(spoolDetailsJson);
                    JsonElement root = doc.RootElement;
                    string? spoolName = root.TryGetProperty("name", out JsonElement nameEl) ? nameEl.GetString() : null;
                    string? material = root.TryGetProperty("material", out JsonElement matEl) ? matEl.GetString() : null;
                    string? colorHex = root.TryGetProperty("color_hex", out JsonElement colorEl) ? colorEl.GetString() : null;
                    double? remainingWeight = root.TryGetProperty("remaining_weight", out JsonElement weightEl) && weightEl.ValueKind == JsonValueKind.Number ? weightEl.GetDouble() : (double?)null;

                    return new PrinterSpoolInfoDto(
                        HasActiveSpool: true,
                        ActiveSpoolId: activeSpoolId,
                        SpoolName: spoolName,
                        Material: material,
                        ColorHex: colorHex,
                        RemainingWeightG: remainingWeight);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"[Moonraker] Failed to parse spool details: {ex.Message}");
                    return new PrinterSpoolInfoDto(HasActiveSpool: true, ActiveSpoolId: activeSpoolId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug($"[Moonraker] Error getting spool info: {ex.Message}");
                return null;
            }
        }

        public async Task<string?> GetCameraStreamUrlAsync(Printer printer, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(printer);

            try
            {
                string moonrakerUrl = BuildMoonrakerUrl(printer.ServerUrl, printer.BackendPort);
                return await _client.GetCameraStreamUrlAsync(moonrakerUrl, printer.FrontendPort, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"[Moonraker] Error getting camera stream URL for {printer.Id}: {ex.Message}");
                return null;
            }
        }

        public async Task<string?> GetCameraSnapshotUrlAsync(Printer printer, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(printer);

            try
            {
                string moonrakerUrl = BuildMoonrakerUrl(printer.ServerUrl, printer.BackendPort);
                return await _client.GetCameraSnapshotUrlAsync(moonrakerUrl, printer.FrontendPort, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"[Moonraker] Error getting camera snapshot URL for {printer.Id}: {ex.Message}");
                return null;
            }
        }

        public async Task<bool> IsCameraAvailableAsync(Printer printer, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(printer);

            try
            {
                string moonrakerUrl = BuildMoonrakerUrl(printer.ServerUrl, printer.FrontendPort);
                string? streamUrl = await GetCameraStreamUrlAsync(printer, ct);
                return !string.IsNullOrEmpty(streamUrl);
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"[Moonraker] Error checking camera availability for {printer.Id}: {ex.Message}");
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
                SpoolInfo: null);
        }

        private static string BuildMoonrakerUrl(string serverUrl, int? port)
        {
            return port.HasValue
                ? $"{serverUrl}:{port}"
                : serverUrl;
        }
    }
}
