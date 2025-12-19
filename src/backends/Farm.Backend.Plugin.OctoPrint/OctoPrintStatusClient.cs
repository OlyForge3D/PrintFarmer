using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Telemetry;
using Farm.Infrastructure.Services.Printers;

namespace Farm.Backend.Plugin.OctoPrint
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
                
                // Retrieve both printer state and job status - now returns typed objects
                OctoPrintPrinterState? printerState = await breaker.ExecuteAsync(
                    async ct => await _client.GetPrinterStateAsync(printer.BackendUrl, printer.ApiKey ?? string.Empty),
                    ct);
                
                OctoPrintJobStatus? jobStatus = await breaker.ExecuteAsync(
                    async ct => await _client.GetJobStatusAsync(printer.BackendUrl, printer.ApiKey ?? string.Empty),
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
                    _logger.LogWarning($"[OctoPrint] Failed to retrieve status for printer {printer.Id}");
                    return CreateOfflineStatus(printer.Id);
                }
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
                
                OctoPrintPrinterState? printerState = await breaker.ExecuteAsync(
                    async ct => await _client.GetPrinterStateAsync(printer.BackendUrl, printer.ApiKey ?? string.Empty),
                    ct);
                
                OctoPrintJobStatus? jobStatus = await breaker.ExecuteAsync(
                    async ct => await _client.GetJobStatusAsync(printer.BackendUrl, printer.ApiKey ?? string.Empty),
                    ct);
                
                // Build PrinterDto from typed objects
                if (printerState != null)
                {
                    return new PrinterDto(
                        Id: printer.Id,
                        Name: printer.Name,
                        Notes: printer.Notes,
                        IsOnline: printerState.Operational,
                        State: printerState.State,
                        ManufacturerName: printer.Manufacturer?.Name,
                        ModelName: printer.Model?.Name,
                        Progress: jobStatus?.Progress ?? 0,
                        JobName: jobStatus?.Filename,
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
                        OriginalServerUrl: printer.OriginalServerUrl,
                        IpAddress: printer.IpAddress,
                        BackendPort: printer.BackendPort,
                        FrontendPort: printer.FrontendPort,
                        SpoolInfo: null,
                        BackendUrl: printer.BackendUrl,
                        FrontendUrl: printer.FrontendUrl);
                }
                else
                {
                    throw new InvalidOperationException($"Failed to retrieve status for printer {printer.Id}");
                }
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

        /// <summary>
        /// Parses OctoPrint API responses to extract printer status.
        /// Expects JSON from /api/printer and /api/job endpoints.
        /// </summary>
        private static OctoPrintStatusData? ParseOctoPrintStatus(string printerStateJson, string jobStatusJson)
        {
            try
            {
                using var printerDoc = JsonDocument.Parse(printerStateJson);
                using var jobDoc = JsonDocument.Parse(jobStatusJson);
                
                var printerRoot = printerDoc.RootElement;
                var jobRoot = jobDoc.RootElement;

                // Get operational state from printer endpoint
                bool operational = false;
                if (printerRoot.TryGetProperty("state", out var stateObj) &&
                    stateObj.TryGetProperty("flags", out var flags) &&
                    flags.TryGetProperty("operational", out var opProp))
                {
                    operational = opProp.GetBoolean();
                }

                // Get printing/paused state
                bool printing = false, paused = false;
                if (printerRoot.TryGetProperty("state", out stateObj) &&
                    stateObj.TryGetProperty("flags", out flags))
                {
                    if (flags.TryGetProperty("printing", out var printProp))
                        printing = printProp.GetBoolean();
                    if (flags.TryGetProperty("paused", out var pauseProp))
                        paused = pauseProp.GetBoolean();
                }

                // Determine state
                string state = !operational ? "Offline" : 
                               printing ? "Printing" :
                               paused ? "Paused" : "Idle";

                // Get progress from job endpoint
                double? progress = null;
                if (jobRoot.TryGetProperty("progress", out var progObj) &&
                    progObj.TryGetProperty("completion", out var completion) &&
                    completion.ValueKind != JsonValueKind.Null)
                {
                    progress = completion.GetDouble() * 100.0;
                }

                // Get job name
                string? jobName = null;
                if (jobRoot.TryGetProperty("job", out var jobObj) &&
                    jobObj.TryGetProperty("file", out var fileObj) &&
                    fileObj.TryGetProperty("name", out var name) &&
                    name.ValueKind != JsonValueKind.Null)
                {
                    jobName = name.GetString();
                }

                // Get Z position
                double? z = null;
                if (printerRoot.TryGetProperty("currentZ", out var zProp) && zProp.ValueKind != JsonValueKind.Null)
                {
                    z = zProp.GetDouble();
                }

                // Get temperatures
                double? hotendTemp = null, bedTemp = null, hotendTarget = null, bedTarget = null;
                if (printerRoot.TryGetProperty("temperature", out var tempObj))
                {
                    if (tempObj.TryGetProperty("tool0", out var tool0) && tool0.ValueKind != JsonValueKind.Null)
                    {
                        if (tool0.TryGetProperty("actual", out var actual))
                            hotendTemp = actual.GetDouble();
                        if (tool0.TryGetProperty("target", out var target))
                            hotendTarget = target.GetDouble();
                    }
                    if (tempObj.TryGetProperty("bed", out var bed) && bed.ValueKind != JsonValueKind.Null)
                    {
                        if (bed.TryGetProperty("actual", out var actual))
                            bedTemp = actual.GetDouble();
                        if (bed.TryGetProperty("target", out var target))
                            bedTarget = target.GetDouble();
                    }
                }

                return new OctoPrintStatusData
                {
                    IsOnline = operational,
                    State = state,
                    Progress = progress,
                    JobName = jobName,
                    Z = z,
                    HotendTemp = hotendTemp,
                    BedTemp = bedTemp,
                    HotendTarget = hotendTarget,
                    BedTarget = bedTarget
                };
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Failed to parse OctoPrint status", ex);
            }
        }
    }
}
