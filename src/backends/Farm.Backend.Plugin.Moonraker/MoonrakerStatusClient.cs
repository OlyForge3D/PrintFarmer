using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Farm.Backend.Plugin.Core;
using Farm.Infrastructure;
using Farm.Infrastructure.Contracts.Printers.Moonraker;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;
using Microsoft.Extensions.Logging;

namespace Farm.Backend.Plugin.Moonraker;

/// <summary>
/// Printer status client for Moonraker backend (Klipper 3D printer firmware).
/// Implements IPrinterStatusClient for Moonraker-specific status retrieval.
/// Also implements IManagedSpoolProvider as fallback when native Spoolman query returns no data.
/// This status client is provided by the Moonraker backend plugin.
/// </summary>
public class MoonrakerStatusClient : IPrinterStatusClient, IManagedSpoolProvider
{
    private readonly IMoonrakerClient _client;
    private readonly ICircuitBreakerService _circuitBreaker;
    private readonly ManagedSpoolProviderHelper _spoolProvider;
    private readonly ILogger<MoonrakerStatusClient> _logger;

    public PrinterBackend SupportedBackend => PrinterBackend.Moonraker;

    public MoonrakerStatusClient(
        IMoonrakerClient client,
        ICircuitBreakerService circuitBreaker,
        ManagedSpoolProviderHelper spoolProvider,
        ILogger<MoonrakerStatusClient> logger)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(circuitBreaker);
        ArgumentNullException.ThrowIfNull(spoolProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _client = client;
        _circuitBreaker = circuitBreaker;
        _spoolProvider = spoolProvider;
        _logger = logger;
    }

    public async Task<PrinterStatusDto> GetPrinterStatusAsync(Printer printer, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(printer);

        try
        {
            _logger.LogInformation("[Moonraker] GetPrinterStatusAsync for {PrinterName} (ID={PrinterId}): BackendUrl={PrinterBackendUrl}", printer.Name, printer.Id, printer.BackendUrl);

            CircuitBreaker breaker = _circuitBreaker.GetCircuitBreaker($"moonraker-{printer.Id}");

            PrinterCompositeStatus status = await breaker.ExecuteAsync(
                async ct => await _client.GetCompositeStatusAsync(printer.BackendUrl, ct),
                ct);

            _logger.LogInformation("[Moonraker] Status received for {PrinterName}: IsOnline={StatusIsOnline}, State={StatusState}", printer.Name, status.IsOnline, status.State);

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
                BedTarget: status.BedTarget,
                PrintTimeLeftSeconds: status.PrintTimeLeftSeconds);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("[Moonraker] Status timeout for printer {PrinterId}", printer.Id);
            return CreateOfflineStatus(printer.Id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[Moonraker] Error getting status for printer {PrinterId}: {Message}", printer.Id, ex.Message);
            return CreateOfflineStatus(printer.Id);
        }
    }

    public async Task<PrinterDto> GetPrinterDtoAsync(Printer printer, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(printer);

        try
        {
            _logger.LogInformation("[Moonraker] GetPrinterDtoAsync for {PrinterName} (ID={PrinterId}): BackendUrl={PrinterBackendUrl}", printer.Name, printer.Id, printer.BackendUrl);

            CircuitBreaker breaker = _circuitBreaker.GetCircuitBreaker($"moonraker-{printer.Id}");

            PrinterCompositeStatus status = await breaker.ExecuteAsync(
                async ct => await _client.GetCompositeStatusAsync(printer.BackendUrl, ct),
                ct);

            // Get Spoolman integration info — try native Moonraker first, fall back to DB
            PrinterSpoolInfoDto? spoolInfo = await GetSpoolInfoAsync(printer.BackendUrl, ct);
            spoolInfo ??= await GetManagedSpoolInfoAsync(printer, ct);

            _logger.LogInformation("[Moonraker] DTO created for {PrinterName}: IsOnline={StatusIsOnline}, State={StatusState}", printer.Name, status.IsOnline, status.State);

            return await _client.CreatePrinterDtoAsync(printer, status, spoolInfo, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError("[Moonraker] Error getting printer DTO for {PrinterId}: {Message}", printer.Id, ex.Message);
            throw;
        }
    }

    private async Task<PrinterSpoolInfoDto?> GetSpoolInfoAsync(string serverUrl, CancellationToken ct)
    {
        try
        {
            // Check if Spoolman is configured and connected on this printer
            SpoolmanStatus? spoolmanStatus = await _client.GetSpoolmanStatusAsync(serverUrl, ct);
            if (spoolmanStatus == null || !spoolmanStatus.SpoolmanConnected)
            {
                return null; // Spoolman not configured or not connected
            }

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

                // remaining_weight and initial weight are at root level
                double? remainingWeight = root.TryGetProperty("remaining_weight", out JsonElement weightEl) && weightEl.ValueKind == JsonValueKind.Number ? weightEl.GetDouble() : (double?)null;
                double? initialWeight = root.TryGetProperty("initial_weight", out JsonElement initWeightEl) && initWeightEl.ValueKind == JsonValueKind.Number ? initWeightEl.GetDouble() : (double?)null;

                // material, color, vendor, and filament name are nested under .filament
                string? material = null;
                string? colorHex = null;
                string? vendor = null;
                string? filamentName = null;
                if (root.TryGetProperty("filament", out JsonElement filamentEl) && filamentEl.ValueKind == JsonValueKind.Object)
                {
                    material = filamentEl.TryGetProperty("material", out JsonElement matEl) ? matEl.GetString() : null;
                    colorHex = filamentEl.TryGetProperty("color_hex", out JsonElement colorEl) ? colorEl.GetString() : null;
                    filamentName = filamentEl.TryGetProperty("name", out JsonElement fnEl) ? fnEl.GetString() : null;
                    if (filamentEl.TryGetProperty("vendor", out JsonElement vendorEl) && vendorEl.ValueKind == JsonValueKind.Object)
                    {
                        vendor = vendorEl.TryGetProperty("name", out JsonElement vnEl) ? vnEl.GetString() : null;
                    }
                }

                return new PrinterSpoolInfoDto(
                    HasActiveSpool: true,
                    ActiveSpoolId: activeSpoolId,
                    SpoolName: filamentName,
                    Material: material,
                    ColorHex: colorHex != null ? $"#{colorHex}" : null,
                    FilamentName: filamentName,
                    Vendor: vendor,
                    RemainingWeightG: remainingWeight,
                    InitialWeightG: initialWeight);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("[Moonraker] Failed to parse spool details: {Message}", ex.Message);
                return new PrinterSpoolInfoDto(HasActiveSpool: true, ActiveSpoolId: activeSpoolId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug("[Moonraker] Error getting spool info: {Message}", ex.Message);
            return null;
        }
    }

    public async Task<string?> GetCameraStreamUrlAsync(Printer printer, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(printer);

        try
        {
            return await _client.GetCameraStreamUrlAsync(printer.BackendUrl, printer.FrontendPort, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[Moonraker] Error getting camera stream URL for {PrinterId}: {Message}", printer.Id, ex.Message);
            return null;
        }
    }

    public async Task<string?> GetCameraSnapshotUrlAsync(Printer printer, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(printer);

        try
        {
            return await _client.GetCameraSnapshotUrlAsync(printer.BackendUrl, printer.FrontendPort, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[Moonraker] Error getting camera snapshot URL for {PrinterId}: {Message}", printer.Id, ex.Message);
            return null;
        }
    }

    public async Task<bool> IsCameraAvailableAsync(Printer printer, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(printer);

        try
        {
            string moonrakerUrl = printer.FrontendUrl;
            string? streamUrl = await GetCameraStreamUrlAsync(printer, ct);
            return !string.IsNullOrEmpty(streamUrl);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[Moonraker] Error checking camera availability for {PrinterId}: {Message}", printer.Id, ex.Message);
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

    public Task<PrinterSpoolInfoDto?> GetManagedSpoolInfoAsync(Printer printer, CancellationToken ct)
        => _spoolProvider.GetManagedSpoolInfoAsync(printer, ct);
}
