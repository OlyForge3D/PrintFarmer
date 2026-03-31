#pragma warning disable CS1587 // XML comment is not placed on a valid language element

using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;
using Microsoft.Extensions.Logging;

namespace Farm.Backend.Plugin.OctoPrint;

/// <summary>
/// Printer status client for OctoPrint backend (Klipper/GCODE printer control).
/// Implements IPrinterStatusClient for OctoPrint-specific status retrieval.
/// Implements IManagedSpoolProvider for PrintFarmer-managed spool tracking (no native Spoolman).
/// </summary>
public class OctoPrintStatusClient : IPrinterStatusClient, IManagedSpoolProvider
{
    private readonly IOctoPrintClient _client;
    private readonly ICircuitBreakerService _circuitBreaker;
    private readonly ManagedSpoolProviderHelper _spoolProvider;
    private readonly ILogger<OctoPrintStatusClient> _logger;

    public PrinterBackend SupportedBackend => PrinterBackend.OctoPrint;

    public OctoPrintStatusClient(
        IOctoPrintClient client,
        ICircuitBreakerService circuitBreaker,
        ManagedSpoolProviderHelper spoolProvider,
        ILogger<OctoPrintStatusClient> logger)
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
            CircuitBreaker breaker = _circuitBreaker.GetCircuitBreaker($"octoprint-{printer.Id}");

            // Retrieve both printer state and job status - now returns typed objects
            OctoPrintPrinterState? printerState = await breaker.ExecuteAsync(
                async ct => await _client.GetPrinterStateAsync(printer.BackendUrl, printer.Credential),
                ct);

            OctoPrintJobStatus? jobStatus = await breaker.ExecuteAsync(
                async ct => await _client.GetJobStatusAsync(printer.BackendUrl, printer.Credential),
                ct);

            // Create status DTO from typed objects
            if (printerState != null && jobStatus != null)
            {
                return new PrinterStatusDto(
                    Id: printer.Id,
                    IsOnline: printerState.Operational,
                    State: printerState.State,
                    Progress: jobStatus.Progress ?? 0,
                    JobName: jobStatus.Filename,
                    ThumbnailUrl: null,
                    CameraStreamUrl: null,
                    CameraSnapshotUrl: null);
            }
            else if (printerState != null)
            {
                return new PrinterStatusDto(
                    Id: printer.Id,
                    IsOnline: printerState.Operational,
                    State: printerState.State,
                    Progress: 0,
                    JobName: null,
                    ThumbnailUrl: null,
                    CameraStreamUrl: null,
                    CameraSnapshotUrl: null);
            }
            else
            {
                _logger.LogWarning("[OctoPrint] Failed to retrieve status for printer {PrinterId}", printer.Id);
                return CreateOfflineStatus(printer.Id);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("[OctoPrint] Status timeout for printer {PrinterId}", printer.Id);
            return CreateOfflineStatus(printer.Id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[OctoPrint] Error getting status for printer {PrinterId}: {Message}", printer.Id, ex.Message);
            return CreateOfflineStatus(printer.Id);
        }
    }

    public async Task<PrinterDto> GetPrinterDtoAsync(Printer printer, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(printer);

        try
        {
            CircuitBreaker breaker = _circuitBreaker.GetCircuitBreaker($"octoprint-{printer.Id}");

            OctoPrintPrinterState? printerState = await breaker.ExecuteAsync(
                async ct => await _client.GetPrinterStateAsync(printer.BackendUrl, printer.Credential),
                ct);

            OctoPrintJobStatus? jobStatus = await breaker.ExecuteAsync(
                async ct => await _client.GetJobStatusAsync(printer.BackendUrl, printer.Credential),
                ct);

            PrinterSpoolInfoDto? spoolInfo = await GetManagedSpoolInfoAsync(printer, ct);

            // Build PrinterDto from typed objects
            return printerState != null
                ? new PrinterDto(
                    Id: printer.Id,
                    Name: printer.Name,
                    Notes: printer.Notes,
                    IsOnline: printerState.Operational,
                    State: printerState.State,
                    ManufacturerName: printer.Manufacturer?.Name,
                    ModelName: printer.Model?.Name,
                    Progress: jobStatus?.Progress ?? 0,
                    JobName: jobStatus?.Filename,
                    FileName: PrinterStatusDto.ExtractFileName(jobStatus?.Filename),
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
                    Backend: (PrinterBackend)printer.Backend,
                    ApiKey: printer.ApiKey,
                    Username: printer.Username,
                    Password: printer.Password,
                    OriginalServerUrl: printer.OriginalServerUrl,
                    BackendPort: printer.BackendPort,
                    FrontendPort: printer.FrontendPort,
                    SpoolInfo: spoolInfo,
                    BackendUrl: printer.BackendUrl,
                    FrontendUrl: printer.FrontendUrl)
                : throw new InvalidOperationException($"Failed to retrieve status for printer {printer.Id}");
        }
        catch (Exception ex)
        {
            _logger.LogError("[OctoPrint] Error getting printer DTO for {PrinterId}: {Message}", printer.Id, ex.Message);
            throw;
        }
    }

    public Task<PrinterSpoolInfoDto?> GetManagedSpoolInfoAsync(Printer printer, CancellationToken ct)
        => _spoolProvider.GetManagedSpoolInfoAsync(printer, ct);

    public async Task<string?> GetCameraStreamUrlAsync(Printer printer, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(printer);

        // OctoPrint camera support would be implemented here
        _logger.LogWarning("[OctoPrint] Camera stream URLs not yet implemented for printer {PrinterId}", printer.Id);
        await Task.CompletedTask;
        return null;
    }

    public async Task<string?> GetCameraSnapshotUrlAsync(Printer printer, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(printer);

        // OctoPrint camera support would be implemented here
        _logger.LogWarning("[OctoPrint] Camera snapshot URLs not yet implemented for printer {PrinterId}", printer.Id);
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

    /// <summary>
    /// Parses OctoPrint API responses to extract printer status.
    /// Expects JSON from /api/printer and /api/job endpoints.
    /// </summary>

}
