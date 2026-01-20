using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure;

/// <summary>
/// Full printer representation including current status, coordinates, temperatures and optional spool information.
/// </summary>
/// <param name="Id">Printer identifier.</param>
/// <param name="Name">Friendly printer name assigned by the user.</param>
/// <param name="Notes">Optional free-form notes.</param>
/// <param name="IsOnline">Whether the backend is currently reachable.</param>
/// <param name="State">Backend reported state (e.g. printing, idle).</param>
/// <param name="ManufacturerName">Resolved manufacturer name if catalogued.</param>
/// <param name="ModelName">Resolved model name if catalogued.</param>
/// <param name="Progress">Active job progress percentage (0-100).</param>
/// <param name="JobName">Current job / file name if printing.</param>
/// <param name="ThumbnailUrl">URL to a job or printer thumbnail (if provided by backend).</param>
/// <param name="CameraStreamUrl">Live camera stream URL.</param>
/// <param name="CameraSnapshotUrl">Snapshot image URL.</param>
/// <param name="X">Current X coordinate (mm).</param>
/// <param name="Y">Current Y coordinate (mm).</param>
/// <param name="Z">Current Z coordinate (mm).</param>
/// <param name="HotendTemp">Current hotend temperature (°C).</param>
/// <param name="BedTemp">Current bed temperature (°C).</param>
/// <param name="HotendTarget">Target hotend temperature (°C) if heating.</param>
/// <param name="BedTarget">Target bed temperature (°C) if heating.</param>
/// <param name="Backend">Printer backend implementation.</param>
/// <param name="ApiKey">API key / token for the backend if required.</param>
/// <param name="OriginalServerUrl">Original user-entered URL prior to normalization.</param>
/// <param name="IpAddress">Resolved IP address when known.</param>
/// <param name="BackendPort">Backend port number.</param>
/// <param name="FrontendPort">Frontend port number.</param>
/// <param name="SpoolInfo">Active spool information (Moonraker + Spoolman integration).</param>
/// <param name="BackendUrl">Calculated backend URL with port (7125 for Moonraker, etc).</param>
/// <param name="FrontendUrl">Calculated frontend URL (typically port 80 for web UI).</param>
/// <param name="Location">Location information (farm location assignment).</param>
public record PrinterDto(
    Guid Id,
    string Name,
    string? Notes,
    bool IsOnline,
    string? State,
    string? ManufacturerName = null,
    string? ModelName = null,
    double? Progress = null,
    string? JobName = null,
    string? ThumbnailUrl = null,
    string? CameraStreamUrl = null,
    string? CameraSnapshotUrl = null,
    double? X = null,
    double? Y = null,
    double? Z = null,
    double? HotendTemp = null,
    double? BedTemp = null,
    double? HotendTarget = null,
    double? BedTarget = null,
    PrinterBackend Backend = PrinterBackend.Moonraker,
    string? ApiKey = null,
    string? OriginalServerUrl = null,
    string? IpAddress = null,
    int BackendPort = 80,  // NOTE: Default 80 is for HTTP. Actual values: 7125 (Moonraker), 80 (PrusaLink/OctoPrint), 8080 (SDCP). See PrinterBackendHelpers.GetDefaultPort()
    int? FrontendPort = null,
    PrinterSpoolInfoDto? SpoolInfo = null,
    string? BackendUrl = null,
    string? FrontendUrl = null,
    LocationSummaryDto? Location = null);
