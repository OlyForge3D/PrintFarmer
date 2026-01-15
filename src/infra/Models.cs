using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using Farm.Infrastructure.Annotations;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure;

// This file contains DTOs intended for JSON serialization across client/server.
// URL-like values are represented as strings by design for transport compatibility.
#pragma warning disable CA1056 // URI-like properties should not be strings

// Enum Serialization Policy:
//  - Global Program.cs registers JsonStringEnumConverter (string names in API payloads).
//  - Per-enum [JsonConverter] attributes are ONLY used when:
//      * A custom tolerant converter is required (numeric + string input) OR
//      * The enum is exchanged with external worker processes that may not share the global options.
//  - Simple API-only enums rely on global options (no attribute clutter).
//
// Custom tolerant converter (numeric OR string) for backward compatibility in tests and workers.
[JsonConverter(typeof(Json.PrinterBackendJsonConverter))]
public enum PrinterBackend
{
    Unknown = 0,
    Moonraker = 1,
    PrusaLink = 2,
    SDCP = 3,
    OctoPrint = 4
}

/// <summary>
/// Printer movement mechanism type defining the kinematic configuration.
/// </summary>
public enum MotionType
{
    /// <summary>
    /// Traditional 3-axis Cartesian system with independent XYZ movement.
    /// </summary>
    Cartesian = 0,

    /// <summary>
    /// CoreXY kinematics where X and Y motors work together for diagonal movement.
    /// </summary>
    CoreXY = 1,

    /// <summary>
    /// Delta kinematics with 3 towers and effector for precise movement.
    /// </summary>
    Delta = 2,

    /// <summary>
    /// Unknown or unspecified printer type.
    /// </summary>
    Unknown = 99
}

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


// Basic printer info without live status (for fast loading)
/// <summary>
/// Basic printer information without live status values; optimized for list views / dropdowns.
/// </summary>
public record PrinterBasicDto(
    Guid Id,
    string Name,
    string? Notes,
    string? ManufacturerName = null,
    string? ModelName = null,
    PrinterBackend Backend = PrinterBackend.Moonraker,
    string? ApiKey = null,
    string? OriginalServerUrl = null,
    string? IpAddress = null,
    int BackendPort = 80,  // NOTE: Default 80 is for HTTP. Actual values: 7125 (Moonraker), 80 (PrusaLink/OctoPrint), 8080 (SDCP). See PrinterBackendHelpers.GetDefaultPort()
    int? FrontendPort = null,
    string? BackendUrl = null,
    string? FrontendUrl = null);

// Camera URLs for all printers (static configuration without external API calls)
/// <summary>
/// Lightweight camera URL information for printers without external API overhead.
/// </summary>
public record PrinterCameraUrlsDto(
    Guid Id,
    string Name,
    string? CameraStreamUrl = null,
    string? CameraSnapshotUrl = null);

// Fast printer info optimized for performance - includes camera URLs from database (discovered at registration)
/// <summary>
/// Fast printer information for dashboard loading - includes camera URLs discovered during printer registration.
/// Camera URLs are stored in the database and returned directly without additional API calls.
/// </summary>
public record PrinterFastDto(
    Guid Id,
    string Name,
    string? Notes,
    bool IsOnline,
    string? State,
    string? ManufacturerName = null,
    string? ModelName = null,
    PrinterBackend Backend = PrinterBackend.Moonraker,
    string? ApiKey = null,
    string? OriginalServerUrl = null,
    string? IpAddress = null,
    int BackendPort = 80,
    int? FrontendPort = null,
    bool InMaintenance = false,
    bool IsEnabled = true,
    string? CameraStreamUrl = null,
    string? CameraSnapshotUrl = null,
    string? BackendUrl = null,
    string? FrontendUrl = null);

/// <summary>
/// Complete printer DTO combining static config with live real-time status from SignalR.
/// </summary>
public record CompletePrinterDto(
    // Static configuration from database
    Guid Id,
    string Name,
    string? Notes,
    string? ManufacturerName,
    string? ModelName,
    PrinterBackend Backend,
    string? ApiKey,
    string? OriginalServerUrl,
    string? IpAddress,
    int BackendPort,
    int? FrontendPort,
    bool InMaintenance,
    bool IsEnabled,

    // Live status from SignalR cache (merged at API response time)
    bool IsOnline,
    string? State,
    double? Progress,
    string? JobName,
    string? ThumbnailUrl,
    string? CameraStreamUrl,
    double? X,
    double? Y,
    double? Z,
    double? HotendTemp,
    double? BedTemp,
    double? HotendTarget,
    double? BedTarget,
    string? HomedAxes,
    PrinterSpoolInfoDto? SpoolInfo,
    string? BackendUrl = null,
    string? FrontendUrl = null,
    LocationSummaryDto? Location = null);

// Live status info for a specific printer
/// <summary>
/// Lightweight real-time status snapshot for SignalR / polling scenarios.
/// </summary>
public record PrinterStatusDto(
    Guid Id,
    bool IsOnline,
    string? State,
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
    PrinterSpoolInfoDto? SpoolInfo = null);

/// <summary>
/// File information including G-code file name and thumbnail URL.
/// </summary>
public record PrinterFileDto(
    string FileName,
    string? ThumbnailUrl = null,
    long? Modified = null,
    long? SizeBytes = null);

// Real-time update payload for SignalR
/// <summary>
/// SignalR broadcast payload representing a delta style update for a printer.
/// </summary>
public record PrinterStatusUpdate(
    Guid Id,
    bool IsOnline,
    string? State,
    double? Progress,
    string? JobName,
    string? ThumbnailUrl,
    string? CameraStreamUrl,
    double? X,
    double? Y,
    double? Z,
    double? HotendTemp,
    double? BedTemp,
    double? HotendTarget,
    double? BedTarget,
    string? HomedAxes,
    PrinterSpoolInfoDto? SpoolInfo);

/// <summary>
/// SignalR event for toolhead updates (position, homed_axes)
/// </summary>
public record PrinterToolheadUpdate(
    Guid PrinterId,
    double? X,
    double? Y,
    double? Z,
    string? HomedAxes);

/// <summary>
/// SignalR event for extruder temperature updates
/// </summary>
public record PrinterExtruderUpdate(
    Guid PrinterId,
    double? Temperature,
    double? Target);

/// <summary>
/// SignalR event for heater bed temperature updates
/// </summary>
public record PrinterHeaterBedUpdate(
    Guid PrinterId,
    double? Temperature,
    double? Target);

/// <summary>
/// SignalR event for print state and progress updates
/// </summary>
public record PrinterStateUpdate(
    Guid PrinterId,
    string? State,
    double? Progress,
    string? JobName);

/// <summary>
/// Request payload for creating a new printer entry.
/// </summary>
public class CreatePrinterDto : PrinterInfoDto
{
    /// <summary>
    /// Reference to existing manufacturer in catalog.
    /// If null and NewManufacturerName is provided, a new manufacturer will be created.
    /// </summary>
    public Guid? ManufacturerId { get; set; }

    /// <summary>
    /// Reference to existing model in catalog.
    /// If null and NewModelName is provided, a new model will be created.
    /// </summary>
    public Guid? ModelId { get; set; }

    /// <summary>
    /// Create new manufacturer with this name if ManufacturerId is not provided.
    /// </summary>
    public string? NewManufacturerName { get; set; }

    /// <summary>
    /// Create new model with this name if ModelId is not provided.
    /// </summary>
    public string? NewModelName { get; set; }

    /// <summary>
    /// Location name to assign printer to during import.
    /// Location must already exist or will be skipped.
    /// </summary>
    public string? LocationName { get; set; }

    /// <summary>
    /// Date the printer was acquired (optional metadata).
    /// </summary>
    public DateTime? DateAcquired { get; set; }

    /// <summary>
    /// Whether this printer is visible to normal users.
    /// false = pending admin approval, hidden from normal users
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// Hardware specification fields - populated from exported printer data or discovery
    /// </summary>
    public double? MaxBuildVolumeX { get; set; }
    public double? MaxBuildVolumeY { get; set; }
    public double? MaxBuildVolumeZ { get; set; }
    public bool HasHeatedBed { get; set; } = true;
    public bool HasEnclosure { get; set; } = false;
    public bool MultiMaterial { get; set; } = false;
    public bool SupportsAutoLeveling { get; set; } = false;
    public double? NozzleDiameter { get; set; }
    public string[]? SupportedMaterials { get; set; }
    public int? MaxHotendTemp { get; set; }
    public int? MaxBedTemp { get; set; }
    public string? CurrentMaterial { get; set; }
    public int? CurrentSpoolId { get; set; }

    /// <summary>
    /// Toolhead configurations for multi-toolhead printers.
    /// If provided during import, these will be created instead of the default single toolhead.
    /// If null, a default single toolhead will be created.
    /// </summary>
    public List<CreateToolheadDto>? Toolheads { get; set; }

    /// <summary>
    /// Create from discovered printer info with optional catalog metadata.
    /// </summary>
    public static CreatePrinterDto FromDiscovered(
        DiscoveredPrinterDto discovered,
        Guid? manufacturerId = null,
        Guid? modelId = null,
        string? newManufacturerName = null,
        string? newModelName = null) =>
        new CreatePrinterDto
        {
            Name = discovered.Name,
            ServerUrl = discovered.ServerUrl,
            OriginalServerUrl = discovered.OriginalServerUrl,
            IpAddress = discovered.IpAddress,
            Backend = discovered.Backend,
            BackendPort = discovered.BackendPort,
            FrontendPort = discovered.FrontendPort,
            CameraStreamUrl = discovered.CameraStreamUrl,
            CameraSnapshotUrl = discovered.CameraSnapshotUrl,
            Manufacturer = discovered.Manufacturer,
            Model = discovered.Model,
            Notes = discovered.Notes,
            ApiKey = discovered.ApiKey,
            DiscoveredAt = discovered.DiscoveredAt,
            IsReachable = discovered.IsReachable,
            ManufacturerId = manufacturerId,
            ModelId = modelId,
            NewManufacturerName = newManufacturerName,
            NewModelName = newModelName,
            IsEnabled = true
        };
}

/// <summary>
/// Update payload for modifying core printer attributes or reassigning catalog metadata.
/// </summary>
public record UpdatePrinterDto(
    string? Name = null,
    string? ServerUrl = null,
    string? Notes = null,
    Guid? ManufacturerId = null,
    Guid? ModelId = null,
    string? NewManufacturerName = null,
    string? NewModelName = null,
    DateTime? DateAcquired = null,
    PrinterBackend? Backend = null,
    string? ApiKey = null,
    string? CameraStreamUrl = null,
    string? CameraSnapshotUrl = null,
    string? OriginalServerUrl = null,
    // Printer capabilities
    double? NozzleDiameter = null,
    string[]? SupportedMaterials = null,
    double? MaxBuildVolumeX = null,
    double? MaxBuildVolumeY = null,
    double? MaxBuildVolumeZ = null,
    bool? HasHeatedBed = null,
    bool? HasEnclosure = null,
    bool? MultiMaterial = null,
    int? NumberOfExtruders = null,
    int? MaxHotendTemp = null,
    int? MaxBedTemp = null,
    bool? SupportsAutoLeveling = null,
    int? MaxPrintSpeed = null,
    int? BackendPort = null,
    int? FrontendPort = null,
    // Approval workflow
    bool? IsEnabled = null);

// Local spools removed; Spoolman is the source of truth

/// <summary>
/// Standard command result indicating success or failure with optional message.
/// </summary>
public record CommandResult(bool Success, string? Message = null);

public record TempTargets(double? Hotend, double? Bed);
public record MoveRequest(double? X, double? Y, double? Z, double? F);

// Spoolman integration
/// <summary>
/// Configuration settings for integrating with an external Spoolman instance.
/// </summary>
// Made BaseUrl nullable so that an empty JSON object posted to probe endpoint doesn't trigger automatic 400 from [ApiController].
public record SpoolmanConfigDto(string? BaseUrl)
{
    [JsonIgnore] public Uri? BaseUri => string.IsNullOrWhiteSpace(BaseUrl) ? null : (Uri.TryCreate(BaseUrl, UriKind.Absolute, out Uri? u) ? u : null);
}
/// <summary>
/// Represents a single filament spool entity retrieved from Spoolman.
/// </summary>
public record SpoolmanSpoolDto(
    int Id,
    string Name,
    string Material,
    double? RemainingWeightG,
    string? ColorHex,
    bool InUse,
    string? FilamentName = null,
    string? Vendor = null,
    DateTime? RegisteredAt = null,
    DateTime? FirstUsedAt = null,
    DateTime? LastUsedAt = null,
    // Newly added extended fields (optional to preserve backward compatibility)
    double? InitialWeightG = null,
    double? UsedWeightG = null,
    double? SpoolWeightG = null,
    double? RemainingLengthMm = null,
    double? UsedLengthMm = null,
    string? Location = null,
    string? LotNumber = null,
    bool? Archived = null)
{
    public double? UsedPercent
    {
        get
        {
            if (InitialWeightG.HasValue && InitialWeightG.Value > 0)
            {
                if (UsedWeightG.HasValue)
                {
                    return (UsedWeightG.Value / InitialWeightG.Value) * 100.0;
                }
                if (RemainingWeightG.HasValue)
                {
                    return ((InitialWeightG.Value - RemainingWeightG.Value) / InitialWeightG.Value) * 100.0;
                }
            }
            return null;
        }
    }

    public double? RemainingPercent
        => InitialWeightG.HasValue && InitialWeightG.Value > 0 && RemainingWeightG.HasValue
            ? (RemainingWeightG.Value / InitialWeightG.Value) * 100.0
            : null;
}

/// <summary>
/// Result of scanning a network address for a Spoolman instance.
/// </summary>
public record SpoolmanDiscoveryResult(
    string Url,
    bool IsAvailable,
    string? Error = null,
    string? Version = null,
    TimeSpan? ResponseTime = null);

// Printer spool information for Moonraker printers
/// <summary>
/// Snapshot of active spool information attached to a printer (Moonraker + Spoolman bridge).
/// </summary>
public record PrinterSpoolInfoDto(
    bool HasActiveSpool,
    int? ActiveSpoolId = null,
    string? SpoolName = null,
    string? Material = null,
    string? ColorHex = null,
    string? FilamentName = null,
    string? Vendor = null,
    double? RemainingWeightG = null,
    bool? SpoolInUse = null);

// Catalog (Manufacturers / Models)
/// <summary>
/// Printer manufacturer catalog entry.
/// </summary>
public record ManufacturerDto(Guid Id, string Name);
/// <summary>
/// Printer model catalog entry including optional build volume and defaults.
/// </summary>
public record PrinterModelDto(
    Guid Id,
    string Name,
    Guid ManufacturerId,
    MotionType? MotionType = null,
    double? MaxX = null,
    double? MaxY = null,
    double? MaxZ = null,
    PrinterBackend? DefaultBackend = null,
    string[]? SupportedFilamentTypes = null,
    // Default capabilities that can be inherited by new printers
    double? DefaultNozzleDiameter = null,
    bool HasHeatedBed = true,
    bool HasEnclosure = false,
    bool MultiMaterial = false,
    int NumberOfExtruders = 1,
    bool SupportsAutoLeveling = false,
    // Temperature ranges
    int? MaxHotendTemp = null,
    int? MaxBedTemp = null,
    // Speed capabilities
    int? MaxPrintSpeed = null);

// Filament type management
/// <summary>
/// Filament type with default temperature targets.
/// </summary>
public record FilamentTypeDto(Guid Id, string Name, TempTargets DefaultTemperatures);
/// <summary>
/// Creation payload for a filament type.
/// </summary>
public record CreateFilamentTypeRequest(string Name, TempTargets DefaultTemperatures);
/// <summary>
/// Update payload for a filament type.
/// </summary>
public record UpdateFilamentTypeRequest(string Name, TempTargets DefaultTemperatures);

/// <summary>
/// Result of importing filament types from Spoolman.
/// </summary>
public record SpoolmanFilamentImportResult(
    int ImportedCount,
    int SkippedCount,
    int TotalSpoolmanMaterials,
    string[] ImportedNames
);

/// <summary>
/// Represents a material type definition from Spoolman's /api/v1/material endpoint
/// </summary>
public record SpoolmanMaterialDto(
    int Id,
    string Name,
    double? Density = null,
    string? ColorHex = null
);

/// <summary>
/// Result of probing a Spoolman endpoint (used by the setup flow and health probes).
/// </summary>
public record SpoolmanProbeResult(
    bool Success,
    string? NormalizedUrl = null,
    string? EndpointTried = null,
    int? StatusCode = null,
    string? Version = null,
    string? Message = null,
    string? ErrorCategory = null
);

// Printer details for edit page
/// <summary>
/// Extended printer details used for edit forms and detail pages.
/// </summary>
public record PrinterDetailsDto(
    Guid Id,
    string Name,
    string ServerUrl,
    string? Notes,
    Guid? ManufacturerId,
    string? ManufacturerName,
    Guid? ModelId,
    string? ModelName,
    MotionType? ModelMotionType,
    double? ModelMaxX,
    double? ModelMaxY,
    double? ModelMaxZ,
    DateTime? DateAcquired,
    PrinterBackend Backend = PrinterBackend.Moonraker,
    string? ApiKey = null,
    string? CameraStreamUrl = null,
    string? CameraSnapshotUrl = null,
    string? OriginalServerUrl = null,
    string? IpAddress = null,
    int? BackendPort = null,
    int? FrontendPort = null,
    PrinterCapabilitiesDto? Capabilities = null);

// Filament temperature presets (admin-configurable) - now dynamic
/// <summary>
/// Dynamic filament temperature presets keyed by filament name.
/// </summary>
public record FilamentPresetsDto(Dictionary<string, TempTargets> Presets);

// Resolve hostname/IP utility
/// <summary>
/// Request to normalize and optionally resolve a printer server hostname.
/// </summary>
public record ResolveHostnameRequest(string ServerUrl, PrinterBackend Backend);
/// <summary>
/// Response containing normalized URL and resolved IP (if available).
/// </summary>
public record ResolveHostnameResponse(string NormalizedInputUrl, string? ResolvedIp, string ResolvedBaseUrl);

// Network discovery and printer creation (consolidated pipeline)
/// <summary>
/// Base printer information shared across discovery, registration, and creation flows.
/// This DTO consolidates the discovery → registration → creation pipeline to eliminate data loss.
/// </summary>
public class PrinterInfoDto
{
    /// <summary>Display name for the printer</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Normalized server URL (e.g., http://hostname:7125)</summary>
    public string ServerUrl { get; set; } = string.Empty;

    /// <summary>Original user-supplied URL before normalization (if different)</summary>
    public string? OriginalServerUrl { get; set; }

    /// <summary>IP address of the printer on the network</summary>
    public string IpAddress { get; set; } = string.Empty;

    /// <summary>Backend type (moonraker, prusalink, octoprint, sdcp)</summary>
    public PrinterBackend Backend { get; set; }

    /// <summary>Backend-specific port number (default varies by backend)</summary>
    public int? BackendPort { get; set; }

    /// <summary>Frontend web UI port (if different from backend port)</summary>
    public int? FrontendPort { get; set; }

    /// <summary>Camera stream URL discovered from printer API (optional)</summary>
    public string? CameraStreamUrl { get; set; }

    /// <summary>Camera snapshot URL discovered from printer API (optional)</summary>
    public string? CameraSnapshotUrl { get; set; }

    /// <summary>Printer manufacturer name (from discovery or catalog match)</summary>
    public string? Manufacturer { get; set; }

    /// <summary>Printer model name (from discovery or catalog match)</summary>
    public string? Model { get; set; }

    /// <summary>User notes or description</summary>
    public string? Notes { get; set; }

    /// <summary>API key for backend authentication (if required)</summary>
    public string? ApiKey { get; set; }

    /// <summary>Timestamp when printer was discovered</summary>
    public DateTime DiscoveredAt { get; set; } = DateTime.UtcNow;

    /// <summary>Whether the printer is currently reachable</summary>
    public bool IsReachable { get; set; }
}

/// <summary>
/// Toolhead configuration for import/export.
/// Represents a single hotend/nozzle configuration on a printer.
/// </summary>
public class CreateToolheadDto
{
    /// <summary>
    /// Unique identifier (from exported data or generated on create).
    /// </summary>
    public Guid? Id { get; set; }

    /// <summary>
    /// Friendly name for this toolhead (e.g., "Extruder 1", "Left Tool").
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Zero-based index of this toolhead.
    /// </summary>
    public int Index { get; set; }

    /// <summary>
    /// Nozzle diameter in millimeters.
    /// </summary>
    public double? NozzleDiameter { get; set; }

    /// <summary>
    /// Maximum hotend temperature in °C.
    /// </summary>
    public int? MaxHotendTemp { get; set; }

    /// <summary>
    /// Materials this toolhead is rated for.
    /// </summary>
    public string[]? SupportedMaterials { get; set; }

    /// <summary>
    /// Whether this is the primary/default toolhead.
    /// </summary>
    public bool IsPrimary { get; set; }
}

/// <summary>
/// Printer discovered during network scanning.
/// Now a type alias to PrinterInfoDto for backward compatibility.
/// All discovery operations should use PrinterInfoDto going forward.
/// </summary>
public class DiscoveredPrinterDto : PrinterInfoDto
{
    /// <summary>
    /// Create a DiscoveredPrinterDto from raw discovery data.
    /// </summary>
    public static DiscoveredPrinterDto FromProbe(
        string ipAddress,
        string serverUrl,
        string name,
        PrinterBackend backend,
        int? backendPort = null,
        int? frontendPort = null,
        string? manufacturer = null,
        string? model = null,
        string? cameraStreamUrl = null,
        string? cameraSnapshotUrl = null) =>
        new DiscoveredPrinterDto
        {
            IpAddress = ipAddress,
            ServerUrl = serverUrl,
            Name = name,
            Backend = backend,
            BackendPort = backendPort,
            FrontendPort = frontendPort,
            Manufacturer = manufacturer,
            Model = model,
            CameraStreamUrl = cameraStreamUrl,
            CameraSnapshotUrl = cameraSnapshotUrl,
            DiscoveredAt = DateTime.UtcNow,
            IsReachable = true
        };
}

/// <summary>
/// DEPRECATED: Use PrinterInfoDto directly instead.
/// This bridge DTO existed to pass data from discovery service to API registration.
/// Data loss in this layer has been eliminated by consolidating to PrinterInfoDto.
/// </summary>
public class RegisterDiscoveredPrinterDto
{
    /// <summary>Hostname or local name of the printer</summary>
    public string Hostname { get; set; } = string.Empty;

    /// <summary>IP address of the printer</summary>
    public string IpAddress { get; set; } = string.Empty;

    /// <summary>Port number where printer is accessible</summary>
    public int Port { get; set; } = 80;

    /// <summary>Backend type (moonraker, prusalink, octoprint, sdcp)</summary>
    public string PrinterBackend { get; set; } = string.Empty;

    /// <summary>Friendly display name for the printer</summary>
    public string? FriendlyName { get; set; }

    /// <summary>Timestamp when printer was discovered</summary>
    public DateTime DiscoveredAt { get; set; } = DateTime.UtcNow;

    /// <summary>Convert to PrinterInfoDto (preferred modern format)</summary>
    public PrinterInfoDto ToPrinterInfoDto() =>
        new PrinterInfoDto
        {
            Name = FriendlyName ?? Hostname,
            IpAddress = IpAddress,
            ServerUrl = $"http://{IpAddress}:{Port}",
            OriginalServerUrl = null,
            Backend = Enum.TryParse(PrinterBackend, ignoreCase: true, out PrinterBackend b) ? b : global::Farm.Infrastructure.PrinterBackend.Moonraker,
            BackendPort = Port,
            FrontendPort = null,
            DiscoveredAt = DiscoveredAt,
            IsReachable = true
        };
}

// Discovery progress events for SignalR streaming
/// <summary>
/// Periodic progress update for an active network discovery session.
/// </summary>
public record DiscoveryProgressDto(
    string SessionId,
    string CurrentNetwork,
    string CurrentIp,
    int TotalIps,
    int ScannedIps,
    int PrintersFound,
    int PrintersExcluded,
    double ProgressPercentage,
    DiscoveryStatus Status,
    string? Message = null,
    IReadOnlyList<string>? NetworkRanges = null,
    bool AutoDetectedNetworks = false
);

/// <summary>
/// Event published when a printer is found during discovery.
/// </summary>
public record DiscoveryPrinterFoundDto(
    string SessionId,
    DiscoveredPrinterDto Printer
);

/// <summary>
/// Completion summary for a network discovery session.
/// </summary>
public record DiscoveryCompletedDto(
    string SessionId,
    int TotalPrintersFound,
    int TotalPrintersExcluded,
    TimeSpan Duration,
    bool WasCancelled = false,
    IReadOnlyList<string>? NetworkRanges = null,
    bool AutoDetectedNetworks = false
);

/// <summary>
/// Request to start a network discovery session with optional backend filtering.
/// </summary>
public record StartDiscoveryRequest(
    IReadOnlyList<PrinterBackend>? Backends = null
);

/// <summary>
/// States representing the lifecycle of a discovery session.
/// </summary>
public enum DiscoveryStatus
{
    Starting,
    Scanning,
    Completed,
    Cancelled,
    Error
}

// File operations results (upload/print)
/// <summary>
/// Result of uploading a G-code file directly to a printer backend.
/// </summary>
public record UploadGcodeResultDto(string Message, string Filename);
/// <summary>
/// Result of a start print command issued to a backend.
/// </summary>
public record StartPrintResultDto(string Message, string Filename);

// Network discovery configuration
// Collection types kept as List<T> for JSON binding and Blazor forms compatibility (non-breaking).
// Interface methods return IReadOnlyList<T> to satisfy CA1002 on API surface.
#pragma warning disable CA1002 // Do not expose generic lists in public APIs (kept for JSON binding compatibility)
/// <summary>
/// Configuration for network discovery (ranges, timeouts, ports).
/// </summary>
public record NetworkDiscoverySettingsDto(
    List<string> NetworkRanges,
    int TimeoutMs = 3000,
    int MaxConcurrentScans = 20,
    List<PrinterBackend>? Backends = null)
{
    public NetworkDiscoverySettingsDto() : this(new List<string>(), 3000, 20, null)
    {
    }
}
#pragma warning restore CA1002

// History Models (matching Moonraker structure)
/// <summary>
/// Paginated (or filtered) list response of historical jobs from Moonraker.
/// </summary>
public class HistoryListResponse
{
    public int Count { get; set; }
    public HistoryJob[] Jobs { get; set; } = [];
}

/// <summary>
/// Historical job entry mirroring Moonraker history schema.
/// </summary>
public class HistoryJob
{
    public string JobId { get; set; } = string.Empty;
    public bool Exists { get; set; }
    public double? EndTime { get; set; }
    public double FilamentUsed { get; set; }
    public string Filename { get; set; } = string.Empty;
    [SuppressMessage("Usage", "CA2227:Collection properties should be read only", Justification = "DTO used for JSON serialization; setter required for deserialization")]
    public Dictionary<string, object> Metadata { get; set; } = [];
    public double PrintDuration { get; set; }
    public string Status { get; set; } = string.Empty;
    public double StartTime { get; set; }
    public double TotalDuration { get; set; }
    public string User { get; set; } = string.Empty;
    public AuxiliaryData[]? AuxiliaryData { get; set; }
    public string? ThumbnailUrl { get; set; }
}

/// <summary>
/// Additional provider-specific metadata associated with a history job.
/// </summary>
public class AuxiliaryData
{
    public string Provider { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public object Value { get; set; } = new();
    public string Description { get; set; } = string.Empty;
    public string? Units { get; set; }
}

/// <summary>
/// Aggregate totals across historical jobs, including auxiliary data sums.
/// </summary>
public class HistoryTotals
{
    public JobTotals JobTotals { get; set; } = new();
    public AuxiliaryTotals[]? AuxiliaryTotals { get; set; }
}

/// <summary>
/// Aggregated job statistics (counts, durations, filament usage).
/// </summary>
public class JobTotals
{
    public int TotalJobs { get; set; }
    public double TotalTime { get; set; }
    public double TotalPrintTime { get; set; }
    public double TotalFilamentUsed { get; set; }
    public double LongestJob { get; set; }
    public double LongestPrint { get; set; }
}

/// <summary>
/// Aggregated auxiliary metric totals.
/// </summary>
public class AuxiliaryTotals
{
    public string Provider { get; set; } = string.Empty;
    public string Field { get; set; } = string.Empty;
    public double Maximum { get; set; }
    public double Total { get; set; }
}

// G-code Library & Job Queue DTOs
/// <summary>
/// Origin of a G-code file stored in the library.
/// </summary>
public enum GcodeSourceDto
{
    Upload = 0,
    Harvested = 1,
    Generated = 2
}

/// <summary>
/// Represents a G-code file stored in the library (uploaded, harvested, or generated).
/// </summary>
public record GcodeFileDto(
    Guid Id,
    string FileName,
    long FileSize,
    DateTime UploadedAt,
    string? ThumbnailUrl = null,
    string? Name = null,  // Original filename uploaded by user (for display)
    GcodeSourceDto Source = GcodeSourceDto.Upload,
    Guid? SourcePrinterId = null,
    string? SourcePrinterName = null,
    string? OriginalPrinterPath = null,
    DateTime? LastSeenOnPrinter = null,
    string? Description = null,
    IEnumerable<TagDto>? Tags = null,
    double? RequiredNozzleDiameter = null,
    string? RequiredMaterial = null,
    double? EstimatedPrintTimeMinutes = null,
    double? EstimatedFilamentLengthMm = null,
    double? EstimatedFilamentWeightG = null,
    Guid? PrinterModelId = null,
    string? PrinterModelName = null,
    string? SlicerName = null,
    string? SlicerVersion = null,
    bool HasThumbnail = false);

/// <summary>
/// Multipart metadata section for uploading a new G-code file.
/// </summary>
public class CreateGcodeFileDto
{
    public string FileName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string[]? Tags { get; set; }
    public double? RequiredNozzleDiameter { get; set; }
    public string? RequiredMaterial { get; set; }
    public double? EstimatedPrintTimeMinutes { get; set; }
    public double? EstimatedFilamentLengthMm { get; set; }
    public double? EstimatedFilamentWeightG { get; set; }
    public Guid? PrinterModelId { get; set; }
    public string? SlicerName { get; set; }
    public string? SlicerVersion { get; set; }
}

/// <summary>
/// Update payload for modifying G-code library metadata.
/// </summary>
public record UpdateGcodeFileDto(
    string FileName,
    string? Description = null,
    string[]? Tags = null,
    double? RequiredNozzleDiameter = null,
    string? RequiredMaterial = null,
    double? EstimatedPrintTimeMinutes = null,
    double? EstimatedFilamentLengthMm = null,
    double? EstimatedFilamentWeightG = null,
    Guid? PrinterModelId = null,
    string? SlicerName = null,
    string? SlicerVersion = null,
    string? SlicerSettings = null);

// Print Job DTOs
/// <summary>
/// Lifecycle status of a print job.
/// </summary>
// Custom permissive converter so tests / workers can deserialize numeric or string forms ("Queued", 0, "0").
[JsonConverter(typeof(Json.PrintJobStatusJsonConverter))]
public enum PrintJobStatus
{
    Queued = 0,
    Assigned = 1,
    Starting = 2,
    Printing = 3,
    Paused = 4,
    Completed = 5,
    Failed = 6,
    Cancelled = 7
}

/// <summary>
/// Priority levels influencing scheduling order.
/// </summary>
public enum PrintJobPriority
{
    Low = 0,
    Normal = 1,
    High = 2,
    Urgent = 3
}

/// <summary>
/// Represents a print job (active or historical) with scheduling and tracking data.
/// </summary>
public record PrintJobDto(
    Guid Id,
    string Name,
    int Priority,
    PrintJobStatus Status,
    DateTime QueuedAt,
    DateTime? StartedAt = null,
    DateTime? CompletedAt = null,
    string? ErrorMessage = null,
    Guid GcodeFileId = default,
    string GcodeFileName = "",
    Guid? AssignedPrinterId = null,
    string? AssignedPrinterName = null,
    double? HotendTemperature = null,
    double? BedTemperature = null,
    int? SpoolId = null,
    double? ProgressPercentage = null,
    string? CurrentState = null,
    string[]? RequiredCapabilities = null,
    bool AutoAssign = true,
    Guid[]? PreferredPrinterIds = null,
    Guid[]? ExcludedPrinterIds = null);

/// <summary>
/// Request payload for creating and queueing a new print job.
/// </summary>
public class CreatePrintJobDto
{
    public string Name { get; set; } = string.Empty;
    public int Priority { get; set; }
    public Guid GcodeFileId { get; set; }
    public double? HotendTemperature { get; set; }
    public double? BedTemperature { get; set; }
    public int? SpoolId { get; set; }
    public string[]? RequiredCapabilities { get; set; }
    public bool AutoAssign { get; set; } = true;
    public Guid[]? PreferredPrinterIds { get; set; }
    public Guid[]? ExcludedPrinterIds { get; set; }
}

/// <summary>
/// Update payload for adjusting job metadata or scheduling parameters.
/// </summary>
public record UpdatePrintJobDto(
    string Name,
    int Priority,
    double? HotendTemperature = null,
    double? BedTemperature = null,
    int? SpoolId = null,
    string[]? RequiredCapabilities = null,
    bool AutoAssign = true,
    Guid[]? PreferredPrinterIds = null,
    Guid[]? ExcludedPrinterIds = null);

// Printer Capabilities DTOs
/// <summary>
/// Technical capabilities and current availability snapshot for a printer.
/// </summary>
public record PrinterCapabilitiesDto(
    Guid Id,
    Guid PrinterId,
    string PrinterName,
    double? NozzleDiameter = null,
    string[]? SupportedMaterials = null,
    double? MaxBuildVolumeX = null,
    double? MaxBuildVolumeY = null,
    double? MaxBuildVolumeZ = null,
    bool HasHeatedBed = true,
    bool HasEnclosure = false,
    bool MultiMaterial = false,
    bool SupportsAutoLeveling = false,
    int NumberOfExtruders = 1,
    int? MaxHotendTemp = null,
    int? MaxBedTemp = null,
    [property: ImportExport(ImportExportTargets.Import)] string? CurrentMaterial = null,
    [property: ImportExport(ImportExportTargets.Import)] int? CurrentSpoolId = null,
    [property: ImportExport(ImportExportTargets.Import)] bool IsAvailable = true,
    DateTime LastUpdated = default);

/// <summary>
/// Creation payload for registering printer capabilities.
/// </summary>
public record CreatePrinterCapabilitiesDto(
    Guid PrinterId,
    double? NozzleDiameter = null,
    string[]? SupportedMaterials = null,
    double? MaxBuildVolumeX = null,
    double? MaxBuildVolumeY = null,
    double? MaxBuildVolumeZ = null,
    bool HasHeatedBed = true,
    bool HasEnclosure = false,
    bool MultiMaterial = false,
    int NumberOfExtruders = 1,
    int? MaxHotendTemp = null,
    int? MaxBedTemp = null);

/// <summary>
/// Update payload for modifying an existing printer capabilities record.
/// </summary>
public record UpdatePrinterCapabilitiesDto(
    double? NozzleDiameter = null,
    string[]? SupportedMaterials = null,
    double? MaxBuildVolumeX = null,
    double? MaxBuildVolumeY = null,
    double? MaxBuildVolumeZ = null,
    bool HasHeatedBed = true,
    bool HasEnclosure = false,
    bool MultiMaterial = false,
    int NumberOfExtruders = 1,
    bool SupportsAutoLeveling = false,
    int? MaxHotendTemp = null,
    int? MaxBedTemp = null,
    int? MaxPrintSpeed = null,
    [property: ImportExport(ImportExportTargets.Import)] string? CurrentMaterial = null,
    [property: ImportExport(ImportExportTargets.Import)] int? CurrentSpoolId = null,
    [property: ImportExport(ImportExportTargets.Import)] bool IsAvailable = true);

// Queue Management DTOs
/// <summary>
/// Aggregate queue metrics plus recent jobs for dashboard usage.
/// </summary>
public record QueueStatusDto(
    int TotalJobs,
    int QueuedJobs,
    int ActiveJobs,
    int CompletedJobs,
    int FailedJobs,
    PrintJobDto[] RecentJobs,
    PrinterCapabilitiesDto[] AvailablePrinters);

// G-code Library Search/Filter DTOs
/// <summary>
/// Search and filter parameters for querying the G-code library.
/// </summary>
public class GcodeLibrarySearchDto
{
    public string? SearchTerm { get; set; }
    public string[]? Tags { get; set; }
    public string? RequiredMaterial { get; set; }
    public double? NozzleDiameter { get; set; }
    public Guid? TargetPrinterId { get; set; }
    public Guid? TargetModelId { get; set; }
    public DateTime? UploadedAfter { get; set; }
    public DateTime? UploadedBefore { get; set; }
    public int Skip { get; set; }
    public int Take { get; set; } = 50;
    public string SortBy { get; set; } = "UploadedAt";
    public bool SortDescending { get; set; } = true;
}

/// <summary>
/// Result payload for a library search including available facets.
/// </summary>
public record GcodeLibrarySearchResultDto(
    GcodeFileDto[] Files,
    int TotalCount,
    string[] AvailableTags,
    string[] AvailableMaterials);

// Smart Queue Assignment DTOs
/// <summary>
/// Result of attempting to auto-assign a queued job to a printer.
/// </summary>
public record QueueAssignmentResultDto(
    bool Success,
    string Message,
    Guid? AssignedPrinterId = null,
    string? AssignedPrinterName = null,
    string[]? MissingCapabilities = null,
    string[]? ConflictingRequirements = null);

// G-code Harvesting DTOs
public enum GcodeHarvestStatusDto
{
    Running = 0,
    Completed = 1,
    Failed = 2,
    Cancelled = 3
}

public enum HarvestErrorTypeDto
{
    ConnectionError = 0,
    AuthenticationError = 1,
    FileSystemError = 2,
    ValidationError = 3,
    UnknownError = 4
}

public enum HarvestErrorPhaseDto
{
    Discovery = 0,
    Download = 1,
    Processing = 2,
    Completion = 3
}

/// <summary>
/// Represents a G-code harvesting operation and aggregate progress / results.
/// </summary>
public record GcodeHarvestOperationDto(
    Guid Id,
    Guid PrinterId,
    string PrinterName,
    DateTime StartedAt,
    DateTime? CompletedAt = null,
    GcodeHarvestStatusDto Status = GcodeHarvestStatusDto.Running,
    string? ErrorMessage = null,
    string? ErrorType = null,
    string? ErrorPhase = null,
    string? ErrorDetails = null,
    string? FailedResource = null,
    bool IsRetryable = false,
    DateTime? ErrorOccurredAt = null,
    int FilesFound = 0,
    int FilesProcessed = 0, // Calculated as FilesAdded + FilesSkipped + FilesErrored
    int FilesAdded = 0,
    int FilesSkipped = 0,
    int FilesErrored = 0,
    long TotalBytesProcessed = 0,
    bool IncludeSubdirectories = true,
    long? MaxFileSizeBytes = null,
    DateTime? ModifiedAfter = null);

/// <summary>
/// A file discovered during a harvest operation prior to optional import.
/// </summary>
public record DiscoveredGcodeFileDto(
    Guid Id,
    Guid HarvestOperationId,
    string PrinterPath,
    string FileName,
    long FileSizeBytes,
    DateTime? ModifiedAt = null,
    string? FileHash = null,
    bool IsSelected = false,
    bool AlreadyInLibrary = false,
    Guid? ExistingLibraryFileId = null,
    bool ProcessingFailed = false,
    string? ErrorMessage = null,
    string? ThumbnailUrl = null,
    string? ExtractedSlicerName = null,
    string? ExtractedSlicerVersion = null,
    double? ExtractedPrintTime = null,
    double? ExtractedFilamentLength = null,
    double? ExtractedNozzleDiameter = null,
    string? ExtractedMaterial = null,
    string? ExtractedLayerHeight = null,
    string? ExtractedInfill = null,
    HarvestFileStatus? Status = null);

/// <summary>
/// Generic paged result wrapper
/// </summary>
public record PagedResult<T>(
    IReadOnlyList<T> Items,
    int TotalCount,
    int Page,
    int PageSize,
    int TotalPages);

public class StartGcodeHarvestDto
{
    /// <summary>
    /// Target printer to harvest from.
    /// </summary>
    public Guid PrinterId { get; set; }

    /// <summary>
    /// Include subdirectories below the printer's root G-code storage path (default: true).
    /// </summary>
    public bool IncludeSubdirectories { get; set; } = true;

    /// <summary>
    /// Maximum file size (bytes) to consider. Files larger than this are ignored. Default 100MB.
    /// </summary>
    public long? MaxFileSizeBytes { get; set; } = 100 * 1024 * 1024; // 100MB

    /// <summary>
    /// Harvest only files modified strictly after this timestamp (UTC recommended).
    /// </summary>
    public DateTime? ModifiedAfter { get; set; }

    /// <summary>
    /// Allowlist of file extensions (without the leading dot). Empty/null means all supported extensions.
    /// Example: ["gcode", "gco", "g"]
    /// </summary>
    public string[]? FileExtensions { get; set; }

    /// <summary>
    /// Minimum file size (bytes). Files smaller than this are ignored.
    /// </summary>
    public long? MinFileSizeBytes { get; set; }

    /// <summary>
    /// Behavior when a file already exists in the library: "skip" (default), "overwrite", or "rename".
    /// rename => auto-appends -copy / -copy2 etc. to create a distinct entry.
    /// </summary>
    public string? DuplicateHandling { get; set; }
}

/// <summary>
/// Request payload for importing a subset of discovered harvested files into the library.
/// </summary>
public class ImportSelectedGcodeFilesDto
{
    public Guid HarvestOperationId { get; set; }
    public Guid[] FileIds { get; set; } = [];
    public bool AddToLibraryOnly { get; set; } = true; // If false, also create print jobs
    public bool AutoDetectCapabilities { get; set; } = true;
    public string[]? DefaultTags { get; set; }
}

/// <summary>
/// Result summary returned after importing selected harvested files.
/// </summary>
public class GcodeHarvestResultDto
{
    public Guid OperationId { get; set; }
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public int DiscoveredFiles { get; set; }
    public int ImportedFiles { get; set; }
    public string[]? Errors { get; set; }
    public string[] ImportedFileIds { get; set; } = Array.Empty<string>();
    public string[] SkippedFileIds { get; set; } = Array.Empty<string>();
    public string[] FailedFileIds { get; set; } = Array.Empty<string>();
    public Dictionary<string, string>? ErrorDetails { get; set; }

    // Constructor for backward compatibility
    public GcodeHarvestResultDto() { }

    public GcodeHarvestResultDto(Guid operationId, bool success, string message, int discoveredFiles = 0, int importedFiles = 0, string[]? errors = null)
    {
        OperationId = operationId;
        Success = success;
        Message = message;
        DiscoveredFiles = discoveredFiles;
        ImportedFiles = importedFiles;
        Errors = errors;
    }
}

// G-code Harvest Queue DTOs
/// <summary>
/// Status of a harvest operation in the queue.
/// </summary>
public enum GcodeHarvestQueueItemStatus
{
    Pending = 0,      // Waiting to be processed
    Processing = 1,   // Currently being processed
    Completed = 2,    // Successfully completed
    Failed = 3,       // Failed during processing
    Cancelled = 4     // Cancelled by user
}

/// <summary>
/// DTO representing a queued harvest operation (for API responses).
/// </summary>
public record GcodeHarvestQueueItemDto(
    Guid Id,
    Guid PrinterId,
    string PrinterName,
    DateTime QueuedAt,
    DateTime? ProcessingStartedAt = null,
    DateTime? CompletedAt = null,
    GcodeHarvestQueueItemStatus Status = GcodeHarvestQueueItemStatus.Pending,
    int Priority = 0,
    string? ErrorMessage = null,
    int? FilesFound = null,
    int? FilesAdded = null);

/// <summary>
/// Response when a harvest operation is queued.
/// </summary>
public record QueueHarvestResponseDto(
    Guid QueueItemId,
    string Message,
    GcodeHarvestQueueItemStatus Status);

// G-code Metadata Extraction
/// <summary>
/// Extracted metadata from a parsed G-code file (best-effort heuristics).
/// </summary>
public record GcodeMetadataDto(
    string? SlicerName = null,
    string? SlicerVersion = null,
    double? PrintTimeMinutes = null,
    double? FilamentLengthMm = null,
    double? FilamentWeightG = null,
    double? NozzleDiameter = null,
    string? Material = null,
    double? LayerHeight = null,
    string? InfillPercentage = null,
    double? PrintSpeed = null,
    double? BedTemperature = null,
    double? HotendTemperature = null,
    double? BuildPlateX = null,
    double? BuildPlateY = null,
    double? BuildPlateZ = null,
    string[]? Objects = null,
    Dictionary<string, object>? AdditionalMetadata = null);

// 3D Model Management DTOs
public class Model3DDto
{
    public Guid Id { get; set; }
    public string FileName { get; set; } = string.Empty; // GUID-based filename for internal storage
    public string? Name { get; set; } // Original filename uploaded by user (for display and editing)
    public long FileSize { get; set; }
    public string FileType { get; set; } = string.Empty; // stl, 3mf, obj, ply
    public DateTime UploadedAt { get; set; }
    /// <summary>
    /// URL to download the file. Auto-generated from Id if not explicitly set.
    /// </summary>
    public string Url { get; init; } = string.Empty;
    public string? ThumbnailUrl { get; set; }
    public string? Description { get; set; }
    public double? DimensionX { get; set; } // in mm
    public double? DimensionY { get; set; } // in mm
    public double? DimensionZ { get; set; } // in mm
    public int? TriangleCount { get; set; }
    public bool IsValid { get; set; } = true;
    public string? ValidationErrors { get; set; }
    public TagDto[]? Tags { get; set; }
}

/// <summary>
/// Entry in a hierarchical model file listing (file or directory)
/// </summary>
public record Model3DEntryDto(
    string Path,
    string FileName,
    long FileSize,
    DateTime UploadedAt,
    bool IsDirectory,
    string? ThumbnailUrl = null,
    string? Id = null,  // Include model ID for efficient file lookups
    string? DirectoryId = null,  // Include directory ID for efficient directory lookups
    string? Name = null,  // Original filename for display (not GUID)
    string? FileType = null  // File extension: stl, 3mf, obj, ply
);

/// <summary>
/// Response envelope for hierarchical model file listing
/// </summary>
public record Model3DListResponse(
    IReadOnlyList<Model3DEntryDto> Files,
    int TotalFiles,
    long TotalSize,
    int Page,
    int PageSize,
    int TotalPages,
    int TotalItems);

/// <summary>
/// Request to create a new folder in the models directory
/// </summary>
public record CreateFolderRequest(
    [property: JsonPropertyName("path")] string Path
);

/// <summary>
/// Request to move files to a different folder
/// </summary>
public record MoveFilesRequest(
    [property: JsonPropertyName("filePaths")] IReadOnlyList<string> FilePaths,
    [property: JsonPropertyName("targetPath")] string TargetPath
);

/// <summary>
/// Request to move model files by ID using target directory ID (more efficient than by path)
/// </summary>
public record MoveModelsRequest(
    [property: JsonPropertyName("modelIds")] IReadOnlyList<string> ModelIds,
    [property: JsonPropertyName("targetDirectoryId")] string TargetDirectoryId
);

/// <summary>
/// Response for folder operations
/// </summary>
public record FolderOperationResultDto(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("message")] string Message
);

/// <summary>
/// Tag for organizing and categorizing 3D models
/// </summary>
/// <summary>
/// Tag data transfer object (works for any taggable object type)
/// </summary>
public class TagDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Color { get; set; } // Hex color for UI display
    public string? Description { get; set; }
}

/// <summary>
/// Request to create or update a tag (generic for any object type)
/// </summary>
public class CreateTagDto
{
    public string Name { get; set; } = string.Empty;
    public string? Color { get; set; }
    public string? Description { get; set; }
}

/// <summary>
/// Request to assign tags to an object (generic - works for models, gcode files, etc.)
/// </summary>
public class AssignTagsDto
{
    public Guid[] TagIds { get; set; } = [];
}

/// <summary>
/// Tag suggestion for autocomplete with usage count (Phase 3D)
/// </summary>
public class TagSuggestionDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Color { get; set; }
    public int UsageCount { get; set; } // Number of models using this tag
    public bool IsPopular { get; set; } // True if in top N tags
}

/// <summary>
/// Tag usage statistics for analytics (Phase 3D)
/// </summary>
public class TagAnalyticsDto
{
    public int TotalTags { get; set; }
    public int TagsInUse { get; set; } // Tags with at least one model
    public int UnusedTags { get; set; } // Tags with no models
    public int TotalModelTagAssociations { get; set; }
    public double AverageTagsPerModel { get; set; }
    public IReadOnlyList<TagStatDto>? TopTags { get; set; } // Most used tags
    public IReadOnlyList<TagStatDto>? UnusedTagsList { get; set; } // For cleanup suggestions
}

/// <summary>
/// Individual tag statistics (Phase 3D)
/// </summary>
public class TagStatDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int ModelCount { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
}

/// <summary>
/// Request to filter models by tags (Phase 3D)
/// </summary>
public class FilterModelsByTagsRequestDto
{
    public Guid[]? IncludeTags { get; set; } // Tags to include
    public Guid[]? ExcludeTags { get; set; } // Tags to exclude
    public bool RequireAllTags { get; set; } = false; // If true, ALL include tags required; if false, ANY
}

/// <summary>
/// Response from filtering models by tags (Phase 3D)
/// </summary>
public class FilterModelsResponseDto
{
    public IReadOnlyList<Guid> ModelIds { get; set; } = [];
    public int Count { get; set; }
}

/// <summary>
/// Request to update 3D model properties
/// </summary>
public class UpdateModel3DDto
{
    public string? Name { get; set; }
}

/// <summary>
/// Request to bulk assign tags to multiple models
/// </summary>
public class BulkAssignTagsDto
{
    public Guid[] ModelIds { get; set; } = [];
    public Guid[] TagIds { get; set; } = [];
    public bool ReplaceExisting { get; set; } = false; // If true, replaces all existing tags
}

/// <summary>
/// Result of bulk operation
/// </summary>
public class BulkOperationResultDto
{
    public int SuccessCount { get; set; }
    public int TotalCount { get; set; }
}

/// <summary>
/// Search/filter parameters for 3D models
/// </summary>
public class Model3DSearchRequestDto
{
    public string? Query { get; set; } // Search in name/description
    public Guid[]? TagIds { get; set; } // Filter by tags (AND logic - must have all specified tags)
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;
    public string? SortBy { get; set; } = "uploadedAt"; // uploadedAt, name, size
    public bool Descending { get; set; } = true;
}

/// <summary>
/// Paginated search results for 3D models
/// </summary>
public class Model3DSearchResultDto
{
    public Model3DDto[] Models { get; set; } = [];
    public int TotalCount { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalPages { get; set; }
}

public class Model3DUploadResultDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public string FileType { get; set; } = string.Empty;
    public DateTime UploadedAt { get; set; }
    public string Url { get; set; } = string.Empty;
}

public class Model3DValidationResultDto
{
    public bool Valid { get; set; }
    public string[]? Issues { get; set; }
}

// Slicer Integration DTOs
/// <summary>
/// Composite slicer profile that combines machine, process (quality), and filament profiles.
/// This is the primary profile object passed when slicing a model - it contains all three
/// profile types needed for a complete slicing operation.
/// </summary>
public class SlicerProfileDto
{
    /// <summary>
    /// Machine/printer profile controlling hardware-specific settings (bed size, extruders, etc.)
    /// </summary>
    public MachineProfileDto? MachineProfile { get; set; }

    /// <summary>
    /// Process/quality profile controlling print characteristics (layer height, infill, speed, supports).
    /// </summary>
    public ProcessProfileDto? ProcessProfile { get; set; }

    /// <summary>
    /// Filament/material profile controlling material-specific settings (temperatures, speeds, material type).
    /// </summary>
    public FilamentProfileDto? FilamentProfile { get; set; }
}

/// <summary>
/// Flat slicer profile data sent by the worker service during slicing operations.
/// This represents the profile parameters in a flat structure as understood by the worker.
/// Used internally for worker communication only - not exposed through the public API.
/// </summary>
public class WorkerSlicerProfileDto
{
    public double LayerHeight { get; set; } = 0.2;
    public int InfillPercentage { get; set; } = 20;
    public int PrintSpeed { get; set; } = 50; // mm/s
    public int NozzleTemperature { get; set; } = 210; // °C
    public int BedTemperature { get; set; } = 60; // °C
    public bool Supports { get; set; }
    public string Material { get; set; } = "PLA";
    public string Quality { get; set; } = "standard"; // draft, standard, fine
}

/// <summary>
/// Machine/Printer profile DTO for OrcaSlicer.
/// Contains printer-specific configuration like bed size, extruders, etc.
/// </summary>
public class MachineProfileDto
{
    public string Name { get; set; } = string.Empty;
    public string Manufacturer { get; set; } = string.Empty;
    public string? Description { get; set; }
    public double? NozzleDiameter { get; set; } // e.g., 0.4, 0.6, 0.8 mm
    /// <summary>
    /// Whether this profile can be instantiated (used) in the slicer.
    /// true = user-selectable, false = base/template profile for inheritance only.
    /// </summary>
    [JsonConverter(typeof(Json.StringToBoolJsonConverter))]
    public bool Instantiation { get; set; } = true;
    /// <summary>
    /// Parent profile name to inherit settings from (used during seeding for inheritance resolution).
    /// </summary>
    [JsonPropertyName("inherits")]
    public string? Inherits { get; set; }
    public Dictionary<string, object> Settings { get; set; } = new();
}

/// <summary>
/// Filament/Material profile DTO for OrcaSlicer.
/// Contains material-specific settings like temperature, speed, etc.
/// </summary>
public class FilamentProfileDto
{
    public string Name { get; set; } = string.Empty;
    public string Material { get; set; } = "PLA";
    public string? Manufacturer { get; set; }
    public string? Description { get; set; }
    public int NozzleTemperature { get; set; } = 210;
    public int BedTemperature { get; set; } = 60;
    public int PrintSpeed { get; set; } = 50;
    [JsonPropertyName("compatible_printers")]
    public IList<string> CompatiblePrinters { get; set; } = new List<string>();
    [JsonIgnore]
    public string? CompatiblePrintersCondition { get; set; }
    /// <summary>
    /// Whether this profile can be instantiated (used) in the slicer.
    /// true = user-selectable, false = base/template profile for inheritance only.
    /// </summary>
    [JsonConverter(typeof(Json.StringToBoolJsonConverter))]
    public bool Instantiation { get; set; } = true;
    /// <summary>
    /// Parent profile name to inherit settings from (used during seeding for inheritance resolution).
    /// </summary>
    [JsonPropertyName("inherits")]
    public string? Inherits { get; set; }
    public Dictionary<string, object> Settings { get; set; } = new();
}

/// <summary>
/// Process/Quality profile DTO for OrcaSlicer.
/// Contains quality/speed settings like layer height, infill, supports, etc.
/// </summary>
public class ProcessProfileDto
{
    public string Name { get; set; } = string.Empty;
    public string Quality { get; set; } = "standard"; // draft, standard, fine
    public double LayerHeight { get; set; } = 0.2;
    public int InfillPercentage { get; set; } = 20;
    public int PrintSpeed { get; set; } = 50;
    public bool Supports { get; set; }
    public string? Description { get; set; }
    [JsonPropertyName("compatible_printers")]
    public IList<string> CompatiblePrinters { get; set; } = new List<string>();
    [JsonIgnore]
    public string? CompatiblePrintersCondition { get; set; }
    /// <summary>
    /// Whether this profile can be instantiated (used) in the slicer.
    /// true = user-selectable, false = base/template profile for inheritance only.
    /// </summary>
    [JsonConverter(typeof(Json.StringToBoolJsonConverter))]
    public bool Instantiation { get; set; } = true;
    /// <summary>
    /// Parent profile name to inherit settings from (used during seeding for inheritance resolution).
    /// </summary>
    [JsonPropertyName("inherits")]
    public string? Inherits { get; set; }
    public Dictionary<string, object> Settings { get; set; } = new();
}

/// <summary>
/// Combined response from worker /profiles endpoint containing all three profile types.
/// Profiles are organized by manufacturer bundle to maintain hierarchy.
/// </summary>
public class AllProfilesResponseDto
{
    /// <summary>
    /// Profiles organized by manufacturer and model hierarchy.
    /// Structure: Manufacturer -> Model -> (Machine Profile + Associated Filament/Process Profiles)
    /// </summary>
    public Dictionary<string, ManufacturerProfilesDto> ByHierarchy { get; set; } = new();

    /// <summary>
    /// Legacy flat structure for backward compatibility.
    /// Machine profiles grouped by manufacturer name.
    /// </summary>
    public Dictionary<string, IList<MachineProfileDto>> MachineProfiles { get; set; } = new();

    /// <summary>
    /// Legacy flat structure for backward compatibility.
    /// Filament profiles grouped by manufacturer name.
    /// </summary>
    public Dictionary<string, IList<FilamentProfileDto>> FilamentProfiles { get; set; } = new();

    /// <summary>
    /// Legacy flat structure for backward compatibility.
    /// Process profiles grouped by manufacturer name.
    /// </summary>
    public Dictionary<string, IList<ProcessProfileDto>> ProcessProfiles { get; set; } = new();
}

/// <summary>
/// Represents all profile models for a single manufacturer.
/// Maps model identifiers to their machine profile and associated filament/process profiles.
/// </summary>
public class ManufacturerProfilesDto
{
    /// <summary>
    /// Manufacturer name (e.g., "Prusa", "Creality")
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Models for this manufacturer, keyed by model_id (e.g., "Prusa_CORE_One", "Prusa_MK4S")
    /// Value contains the machine profile and associated filament/process profiles
    /// </summary>
    public Dictionary<string, PrinterModelProfilesDto> Models { get; set; } = new();
}

/// <summary>
/// Represents all profiles for a single printer model.
/// A model has one machine profile and multiple filament/process profiles.
/// </summary>
public class PrinterModelProfilesDto
{
    /// <summary>
    /// Human-readable model name (e.g., "Prusa CORE One", "Creality Ender 3 V2")
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Model identifier from the machine profile (e.g., "Prusa_CORE_One")
    /// </summary>
    public string ModelId { get; set; } = string.Empty;

    /// <summary>
    /// Machine profiles for this model (multiple per model: one per nozzle size variant)
    /// </summary>
    public IList<MachineProfileDto> MachineProfiles { get; set; } = new List<MachineProfileDto>();

    /// <summary>
    /// Filament profiles applicable to this model.
    /// Multiple profiles per model (e.g., PLA, PETG, ABS variants)
    /// </summary>
    public IList<FilamentProfileDto> FilamentProfiles { get; set; } = new List<FilamentProfileDto>();

    /// <summary>
    /// Process/print profiles applicable to this model.
    /// Multiple profiles per model (e.g., draft, normal, quality variants)
    /// </summary>
    public IList<ProcessProfileDto> ProcessProfiles { get; set; } = new List<ProcessProfileDto>();
}

/// <summary>
/// Legacy: Machine profiles grouped by manufacturer name (from bundle file name).
/// Example: { "Prusa": [machine1, machine2, ...], "Creality": [...], ... }
/// </summary>
#pragma warning disable S101 // Naming convention required for backward compatibility
public class AllProfilesResponseDto_Deprecated
#pragma warning restore S101
{
    /// <summary>
    /// Machine profiles grouped by manufacturer name (from bundle file name).
    /// Example: { "Prusa": [machine1, machine2, ...], "Creality": [...], ... }
    /// </summary>
    public Dictionary<string, IList<MachineProfileDto>> MachineProfiles { get; set; } = new();

    /// <summary>
    /// Filament profiles grouped by manufacturer name.
    /// </summary>
    public Dictionary<string, IList<FilamentProfileDto>> FilamentProfiles { get; set; } = new();

    /// <summary>
    /// Process profiles grouped by manufacturer name.
    /// </summary>
    public Dictionary<string, IList<ProcessProfileDto>> ProcessProfiles { get; set; } = new();
}

/// <summary>
/// OrcaSlicer manufacturer bundle entry that points to a profile JSON file.
/// Used in {manufacturer}.json bundle files to reference profiles.
/// </summary>
public class ManufacturerBundleProfileEntry
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("sub_path")]
    public string SubPath { get; set; } = string.Empty; // Relative path like "machine/Prusa MK4S.json"
}

/// <summary>
/// OrcaSlicer manufacturer bundle structure.
/// Contains lists of machine, process, and filament profiles for a manufacturer.
/// </summary>
public class ManufacturerBundleDto
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("machine_model_list")]
    public IList<ManufacturerBundleProfileEntry> MachineModelList { get; set; } = new List<ManufacturerBundleProfileEntry>();

    [JsonPropertyName("machine_list")]
    public IList<ManufacturerBundleProfileEntry> MachineList { get; set; } = new List<ManufacturerBundleProfileEntry>();

    [JsonPropertyName("process_list")]
    public IList<ManufacturerBundleProfileEntry> ProcessList { get; set; } = new List<ManufacturerBundleProfileEntry>();

    [JsonPropertyName("filament_list")]
    public IList<ManufacturerBundleProfileEntry> FilamentList { get; set; } = new List<ManufacturerBundleProfileEntry>();
}

/// <summary>
/// Summary of a slicing job and produced G-code artifact once available.
/// </summary>
public class SliceResultDto
{
    public string JobId { get; set; } = string.Empty;
    public string GcodeUrl { get; set; } = string.Empty;
    public int PrintTime { get; set; } // in seconds
    public double FilamentUsed { get; set; } // in grams
    public int LayerCount { get; set; }
    // Added for contract tests: current status and progress of the job
    public string Status { get; set; } = string.Empty; // Queued, Slicing, Completed, Error, Cancelled
    public int Progress { get; set; } // 0-100
    public SliceMetadataDto Metadata { get; set; } = new();
}

public class SliceMetadataDto
{
    public string SlicerVersion { get; set; } = string.Empty;
    public string ProfileUsed { get; set; } = string.Empty;
    public double EstimatedCost { get; set; }
}

// Slicer Profile Management DTOs
public class CreateProcessProfileDto
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string SlicerType { get; set; } = "PrusaSlicer"; // PrusaSlicer, OrcaSlicer, etc.
    public Guid? PrinterModelId { get; set; }
    public Guid? SpecificPrinterId { get; set; }
    public double LayerHeight { get; set; } = 0.2;
    public int InfillPercentage { get; set; } = 20;
    public double PrintSpeed { get; set; } = 50;
    public int NozzleTemperature { get; set; } = 210;
    public int BedTemperature { get; set; } = 60;
    public bool EnableSupports { get; set; }
    public string Material { get; set; } = "PLA";
    public string Quality { get; set; } = "Standard"; // Draft, Standard, Fine
    public string? AdvancedSettings { get; set; }
    public bool IsDefault { get; set; }
    public bool IsPublic { get; set; } = true;
}

public class ProcessProfileResponseDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string SlicerType { get; set; } = string.Empty;
    public Guid? PrinterModelId { get; set; }
    public string? PrinterModelName { get; set; }
    public Guid? SpecificPrinterId { get; set; }
    public string? SpecificPrinterName { get; set; }
    public double LayerHeight { get; set; }
    public int InfillPercentage { get; set; }
    public int PrintSpeed { get; set; }
    public int NozzleTemperature { get; set; }
    public int BedTemperature { get; set; }
    public bool EnableSupports { get; set; }
    public string Material { get; set; } = string.Empty;
    public string Quality { get; set; } = string.Empty;
    public string? AdvancedSettings { get; set; }
    public bool IsDefault { get; set; }
    public bool IsPublic { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

// Advanced profile import/export DTOs (Phase 6)
public class ImportProcessProfileDto
{
    public string RawJson { get; set; } = string.Empty; // Raw profile JSON from slicer export
    public string? Name { get; set; } // Optional override; if null we derive from profileType + layerHeight
    public string? Description { get; set; }
    public string SlicerType { get; set; } = "PrusaSlicer"; // PrusaSlicer, OrcaSlicer, etc.
    public bool AllowSystemOverride { get; set; } = false; // If true, system profile match by hash can be overridden
    public bool SetDefault { get; set; } = false; // If true, sets profile as default after import (scope: global if user absent)
    public bool IsPublic { get; set; } = true; // Visibility to other users
}

public class ProcessProfileExtendedDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string SlicerType { get; set; } = string.Empty;
    public double LayerHeight { get; set; }
    public int InfillPercentage { get; set; }
    public double PrintSpeed { get; set; }
    public bool EnableSupports { get; set; }
    public string Quality { get; set; } = string.Empty;
    public bool IsDefault { get; set; }
    public bool IsPublic { get; set; }
    public bool IsSystem { get; set; }
    public string Hash { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public Dictionary<string, object?> Metadata { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public class ProcessProfileExportDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string SlicerType { get; set; } = string.Empty;
    public string Hash { get; set; } = string.Empty;
    public string RawJson { get; set; } = string.Empty; // Sanitized raw profile JSON
    public Dictionary<string, object?> Metadata { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Base interface for profile list items
/// </summary>
public interface IProfileListItem
{
    Guid Id { get; }
    string Name { get; }
    string SlicerType { get; }
    bool IsDefault { get; }
    bool IsSystem { get; }
    bool IsPublic { get; }
    string Hash { get; }
    string ProfileType { get; }
}

/// <summary>
/// Process profile list item DTO
/// </summary>
public class ProcessProfileListItemDto : IProfileListItem
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string SlicerType { get; set; } = string.Empty;
    public string Quality { get; set; } = string.Empty;
    public double LayerHeight { get; set; }
    public int InfillPercentage { get; set; }
    public bool IsDefault { get; set; }
    public bool IsSystem { get; set; }
    public bool IsPublic { get; set; }
    public string Hash { get; set; } = string.Empty;
    public string ProfileType => "process";
}

/// <summary>
/// Filament profile list item DTO
/// </summary>
public class FilamentProfileListItemDto : IProfileListItem
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string SlicerType { get; set; } = string.Empty;
    public string Material { get; set; } = string.Empty;
    public int? NozzleTemperature { get; set; }
    public int? BedTemperature { get; set; }
    public int PrintSpeed { get; set; }
    public bool IsDefault { get; set; }
    public bool IsSystem { get; set; }
    public bool IsPublic { get; set; }
    public string Hash { get; set; } = string.Empty;
    public string ProfileType => "filament";
}

/// <summary>
/// Machine profile list item DTO
/// </summary>
public class MachineProfileListItemDto : IProfileListItem
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string SlicerType { get; set; } = string.Empty;
    public string Manufacturer { get; set; } = string.Empty;
    public bool IsDefault { get; set; }
    public bool IsSystem { get; set; }
    public bool IsPublic { get; set; }
    public string Hash { get; set; } = string.Empty;
    public string ProfileType => "machine";
}

// Backwards compatibility alias - use ProcessProfileListItemDto directly
#pragma warning disable S2094 // Empty class alias required for backward compatibility
public class SlicerProfileListItemDto : ProcessProfileListItemDto { }
#pragma warning restore S2094

/// <summary>
/// Response containing all profile types organized separately
/// </summary>
public class ExtendedProfilesResponseDto
{
    public IList<ProcessProfileListItemDto> ProcessProfiles { get; set; } = new List<ProcessProfileListItemDto>();
    public IList<FilamentProfileListItemDto> FilamentProfiles { get; set; } = new List<FilamentProfileListItemDto>();
    public IList<MachineProfileListItemDto> MachineProfiles { get; set; } = new List<MachineProfileListItemDto>();
}

public class BulkProfileImportRequest
{
    public List<Guid>? ProfileIds { get; set; }
    public bool? MakePublic { get; set; }
}

public class BulkProfileImportResultDto
{
    public Guid PrinterId { get; set; }
    public string PrinterName { get; set; } = string.Empty;
    public int TotalRequested { get; set; }
    public int TotalFound { get; set; }
    public int Imported { get; set; }
    public int Duplicated { get; set; }
}

/// <summary>
/// Request to import profiles directly from the OrcaSlicer worker (not from pre-seeded database).
/// Used when profiles haven't been seeded yet and come directly from the worker.
/// </summary>
public class BulkImportFromWorkerRequest
{
    /// <summary>
    /// Profiles to import, as returned from the OrcaSlicer worker (/profiles endpoint)
    /// </summary>
    public List<SlicerProfileDto>? Profiles { get; set; }
    public bool? MakePublic { get; set; }
}

public class BulkImportFromWorkerResultDto
{
    public Guid PrinterId { get; set; }
    public string PrinterName { get; set; } = string.Empty;
    public int Imported { get; set; }
    public int Duplicated { get; set; }
}

// Queue Management DTOs
public class QueueOverviewDto
{
    public Guid PrinterId { get; set; }
    public string PrinterName { get; set; } = string.Empty;
    public string PrinterModel { get; set; } = string.Empty;
    public bool IsAvailable { get; set; }
    public int QueuedJobsCount { get; set; }
    public Guid? CurrentJobId { get; set; }
    public string? CurrentJobName { get; set; }
    public DateTime? EstimatedCompletionTime { get; set; }
}

public class UpdateJobPriorityDto
{
    public int Priority { get; set; }
}

/// <summary>
/// Internal tracking DTO for active / completed slicing jobs.
/// </summary>
// Slicing job status (shared with worker processes) – keep explicit attribute to decouple from Program options.
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SlicingJobStatus
{
    Queued,
    Slicing,
    Completed,
    Error,
    Cancelled
}

public class SlicingJobDto
{
    public string JobId { get; set; } = string.Empty;
    public SlicingJobStatus Status { get; set; } = SlicingJobStatus.Queued;
    public int Progress { get; set; } // 0-100
    public string? Message { get; set; }
    public string SlicerEngine { get; set; } = string.Empty; // prusaslicer, orcaslicer
    public Guid PrinterId { get; set; }
    public string ModelFilePath { get; set; } = string.Empty;
    public string? GcodeFilePath { get; set; }
    public SlicerProfileDto? Profile { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public int? EstimatedPrintTime { get; set; }
    public double? EstimatedFilamentUsed { get; set; }
    public int? LayerCount { get; set; }
}

// Job Queue System DTOs
/// <summary>
/// Queue-focused view of a print job used by management endpoints.
/// </summary>
public class JobQueuePrintJobDto
{
    public Guid Id { get; set; }
    public Guid GcodeFileId { get; set; }
    public string GcodeFileName { get; set; } = string.Empty;
    public Guid? AssignedPrinterId { get; set; }
    public string AssignedPrinterName { get; set; } = string.Empty;
    public PrintJobStatus? Status { get; set; }
    public int Priority { get; set; }
    public int QueuePosition { get; set; }
    public decimal? RequiredNozzleDiameter { get; set; }
    public string? RequiredMaterialType { get; set; }
    public TimeSpan? EstimatedPrintTime { get; set; }
    public double? EstimatedFilamentUsage { get; set; }
    public DateTime? ActualStartTime { get; set; }
    public DateTime? ActualEndTime { get; set; }
    public TimeSpan? ActualPrintTime { get; set; }
    public double? ActualFilamentUsage { get; set; }
    public string? FailureReason { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

// Additional Job Queue DTOs
/// <summary>
/// Request payload to enqueue a new job referencing an existing G-code file.
/// </summary>
public class QueuePrintJobDto
{
    public Guid GcodeFileId { get; set; }
    public Guid? AssignedPrinterId { get; set; } // If null, auto-assign to best available printer
    public PrintJobPriority Priority { get; set; } = PrintJobPriority.Normal;
    public decimal? RequiredNozzleDiameter { get; set; }
    public string? RequiredMaterialType { get; set; }
}

/// <summary>
/// Partial updates for a queued or active job (status, priority, assignment, actual metrics).
/// </summary>
public class UpdatePrintJobStatusDto
{
    public PrintJobStatus? Status { get; set; }
    public PrintJobPriority? Priority { get; set; }
    public Guid? AssignedPrinterId { get; set; }
    public double? ActualFilamentUsage { get; set; }
    public string? FailureReason { get; set; }
}

/// <summary>
/// Batch reordering request specifying new queue positions.
/// </summary>
public class ReorderQueueDto
{
    public JobOrderDto[] JobOrder { get; set; } = [];
}

/// <summary>
/// New ordering metadata for a single job.
/// </summary>
public class JobOrderDto
{
    public Guid JobId { get; set; }
    public int Position { get; set; }
}

// Printer Capabilities DTOs
/// <summary>
/// Legacy / extended capabilities definition supporting multi-nozzle sets and feature flags.
/// </summary>
public class CreateOrUpdatePrinterCapabilitiesDto
{
    public decimal[]? NozzleDiameters { get; set; }
    public string[]? SupportedMaterials { get; set; }
    public decimal MaxPrintVolumeX { get; set; }
    public decimal MaxPrintVolumeY { get; set; }
    public decimal MaxPrintVolumeZ { get; set; }
    public int MaxHotendTemperature { get; set; }
    public int MaxBedTemperature { get; set; }
    public bool HasHeatedBed { get; set; }
    public bool HasEnclosure { get; set; }
    public bool SupportsAutoLeveling { get; set; }
    public int MaxPrintSpeed { get; set; }
}

/// <summary>
/// Lean capabilities export DTO (excludes redundant PrinterId/PrinterName already in parent PrinterWithCapabilitiesDto).
/// Used for export to keep JSON compact and avoid duplication.
/// Null properties are excluded from JSON export to keep payload minimal.
/// </summary>
public class PrinterCapabilitiesExportDto
{
    public Guid Id { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? NozzleDiameter { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string[]? SupportedMaterials { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? MaxBuildVolumeX { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? MaxBuildVolumeY { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? MaxBuildVolumeZ { get; set; }

    public bool HasHeatedBed { get; set; } = true;
    public bool HasEnclosure { get; set; } = false;
    public bool MultiMaterial { get; set; } = false;
    public bool SupportsAutoLeveling { get; set; } = false;
    public int NumberOfExtruders { get; set; } = 1;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MinHotendTemp { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxHotendTemp { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MinBedTemp { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxBedTemp { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CurrentMaterial { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? CurrentSpoolId { get; set; }

    public bool IsAvailable { get; set; } = true;
    public DateTime LastUpdated { get; set; }
}

/// <summary>
/// Combined printer identity with capabilities snapshot.
/// </summary>
public class PrinterWithCapabilitiesDto
{
    public Guid PrinterId { get; set; }
    public string PrinterName { get; set; } = string.Empty;
    public string PrinterModel { get; set; } = string.Empty;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PrinterCapabilitiesExportDto? Capabilities { get; set; }

    // Additional export-friendly fields
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ManufacturerName { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PrinterBackend? Backend { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? IpAddress { get; set; }

    // Import-friendly fields (for re-importing exported printers)
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ServerUrl { get; set; } // Base URL without port (e.g., "http://192.168.1.100")

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? BackendPort { get; set; } // Backend API port (e.g., 7125 for Moonraker)

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? FrontendPort { get; set; } // Frontend port if applicable (e.g., 5000 for PrusaLink)

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ApiKey { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Notes { get; set; }
}

/// <summary>
/// Scored compatibility result when matching a G-code file or job to candidate printers.
/// </summary>
public class CompatiblePrinterDto
{
    public Guid PrinterId { get; set; }
    public string PrinterName { get; set; } = string.Empty;
    public int CompatibilityScore { get; set; } // 0-100
    public string[] CompatibilityReasons { get; set; } = [];
    public int CurrentQueueLength { get; set; }
}

// Authentication and User Management DTOs

/// <summary>
/// Standard authentication outcome with optional JWT token and error information.
/// </summary>
public record AuthenticationResult(
    bool Success,
    string? Token = null,
    DateTime? ExpiresAt = null,
    Contracts.Auth.UserDto? User = null,
    string? Error = null);

/// <summary>
/// Protected resource entity (authorization domain object).
/// </summary>
public record ResourceDto(
    Guid Id,
    string Name,
    string DisplayName,
    string? Description = null,
    string ResourceType = "",
    bool IsActive = true);

/// <summary>
/// Allowed action within a resource scope.
/// </summary>
public record ActionDto(
    Guid Id,
    string Name,
    string DisplayName,
    string? Description = null);

/// <summary>
/// Granted / denied permission relationship linking role, resource and action.
/// </summary>
public record RolePermissionDto(
    Guid Id,
    Guid RoleId,
    Guid ResourceId,
    Guid ActionId,
    string ResourceName = "",
    string ActionName = "",
    bool Granted = true);

/// <summary>
/// Assignment of a role to a user (with optional expiration).
/// </summary>
public record UserRoleDto(
    Guid Id,
    Guid UserId,
    Guid RoleId,
    string RoleName = "",
    DateTime AssignedAt = default,
    DateTime? ExpiresAt = null,
    bool IsActive = true);

/// <summary>
/// Payload for creating a new user and assigning initial roles.
/// </summary>
public class CreateUserRequest
{
    public string Username { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public Guid[] RoleIds { get; set; } = [];
}

/// <summary>
/// Partial update for user profile / activation / role membership.
/// </summary>
public class UpdateUserRequest
{
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public bool? IsActive { get; set; }
    public Guid[]? RoleIds { get; set; }
}

/// <summary>
/// Result of a lightweight availability check for prospective username/email.
/// null indicates the value was not requested / provided.
/// </summary>
public record UserAvailabilityDto(bool? UsernameExists, bool? EmailExists);

/// <summary>
/// Payload for creating a new role and its permission set.
/// </summary>
public class CreateRoleRequest
{
    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public RolePermissionRequestDto[] Permissions { get; set; } = [];
}

/// <summary>
/// Payload for updating a role's display properties and permissions.
/// </summary>
public class UpdateRoleRequest
{
    public string DisplayName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public RolePermissionRequestDto[] Permissions { get; set; } = [];
}

/// <summary>
/// Permission assignment entry within a create/update role request.
/// </summary>
public record RolePermissionRequestDto(
    Guid ResourceId,
    Guid ActionId,
    bool Granted = true);

// Note: ChangePasswordRequest, ResetPasswordRequest, ForgotPasswordRequest are defined 
// in Farm.Infrastructure.Contracts.Auth.AuthDtos

/// <summary>
/// Confirms a user's email address using a verification token.
/// </summary>
public record ConfirmEmailRequest(string Token);

#pragma warning restore CA1056

/// <summary>
/// Location DTO for reading and listing printer locations.
/// Contains all location properties including associated printer count.
/// </summary>
public class LocationDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int PrinterCount { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ModifiedAt { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>
/// DTO for creating a new printer location.
/// Includes required and optional location information.
/// </summary>
public class CreateLocationDto
{
    [Required(ErrorMessage = "Location name is required.")]
    [StringLength(256, MinimumLength = 1, ErrorMessage = "Location name must be between 1 and 256 characters.")]
    public string Name { get; set; } = string.Empty;

    [StringLength(1024, ErrorMessage = "Location description cannot exceed 1024 characters.")]
    public string? Description { get; set; }
}

/// <summary>
/// DTO for updating an existing printer location.
/// Allows updating location name, description, and address.
/// </summary>
public class UpdateLocationDto
{
    [StringLength(256, MinimumLength = 1, ErrorMessage = "Location name must be between 1 and 256 characters.")]
    public string? Name { get; set; }

    [StringLength(1024, ErrorMessage = "Location description cannot exceed 1024 characters.")]
    public string? Description { get; set; }
}

/// <summary>
/// Lightweight location summary DTO for inclusion in printer list responses.
/// Contains essential location information for display purposes.
/// </summary>
public record LocationSummaryDto(
    Guid Id,
    string Name,
    string? Description);

/// <summary>
/// Location details DTO including associated printers.
/// Used when retrieving a location with its full printer list.
/// </summary>
public class LocationDetailsDto : LocationDto
{
    public PrinterInfoDto[] Printers { get; set; } = [];
}

/// <summary>
/// Request DTO for cloning profiles from a template machine profile to a custom printer.
/// </summary>
public class CloneProfilesRequestDto
{
    public Guid SourceMachineProfileId { get; set; } // Machine profile to clone from (e.g., "Prusa CORE One")
    public Guid TargetPrinterId { get; set; } // Printer to clone profiles to (e.g., "Prusa CORE One L custom instance)
}

/// <summary>
/// Response DTO for profile cloning operation results.
/// </summary>
public class CloneProfilesResponseDto
{
    public Guid SourceMachineProfileId { get; set; }
    public string SourceMachineName { get; set; } = string.Empty;
    public Guid TargetPrinterId { get; set; }
    public string TargetPrinterName { get; set; } = string.Empty;
    public int ProcessProfilesCloned { get; set; }
    public int FilamentProfilesCloned { get; set; }
    public int TotalProfilesCloned { get; set; }
}

/// <summary>
/// Utility helpers for printer backend operations.
/// </summary>
public static class PrinterBackendHelpers
{
    /// <summary>
    /// Gets the default backend port for a given printer backend.
    /// Moonraker uses 7125, all other backends use 80.
    /// </summary>
    public static int GetDefaultPort(PrinterBackend backend) =>
        backend == PrinterBackend.Moonraker ? 7125 : 80;
}

/// <summary>
/// DTO for reporting print job status and all available properties.
/// Used by both API controllers and infrastructure services.
/// </summary>
public class PrintJobStatusDto
{
    public string? State { get; set; }
    public double? Progress { get; set; }
    public string? JobName { get; set; }
    public string? ThumbnailUrl { get; set; }
    public string? Error { get; set; }
}
