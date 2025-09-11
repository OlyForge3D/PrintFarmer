using System.Diagnostics.CodeAnalysis;
// using System.Text.Json.Serialization; // already imported earlier in file

namespace Farm.Web.Shared;

// This file contains DTOs intended for JSON serialization across client/server.
// URL-like values are represented as strings by design for transport compatibility.
#pragma warning disable CA1056 // URI-like properties should not be strings

using System.Text.Json.Serialization;

// Preserve original enum (converter handled globally); attribute removed to avoid redundancy.
public enum PrinterBackend
{
    Moonraker = 0,
    PrusaLink = 1,
    SDCP = 2
}

/// <summary>
/// Full printer representation including current status, coordinates, temperatures and optional spool information.
/// </summary>
/// <param name="Id">Printer identifier.</param>
/// <param name="Name">Friendly printer name assigned by the user.</param>
/// <param name="ServerUrl">Normalized base URL of the printer backend (e.g. Moonraker / PrusaLink).</param>
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
/// <param name="SpoolInfo">Active spool information (Moonraker + Spoolman integration).</param>
public partial record PrinterDto(
    Guid Id,
    string Name,
    string ServerUrl,
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
    PrinterSpoolInfoDto? SpoolInfo = null);

// Non-breaking typed accessors for URL-like fields (ignored in JSON)
public partial record PrinterDto
{
    [JsonIgnore] public Uri? ServerUri => Uri.TryCreate(ServerUrl, UriKind.Absolute, out var u) ? u : null;
    [JsonIgnore] public Uri? ThumbnailUri => string.IsNullOrWhiteSpace(ThumbnailUrl) ? null : (Uri.TryCreate(ThumbnailUrl, UriKind.Absolute, out var u) ? u : null);
    [JsonIgnore] public Uri? CameraStreamUri => string.IsNullOrWhiteSpace(CameraStreamUrl) ? null : (Uri.TryCreate(CameraStreamUrl, UriKind.Absolute, out var u) ? u : null);
    [JsonIgnore] public Uri? CameraSnapshotUri => string.IsNullOrWhiteSpace(CameraSnapshotUrl) ? null : (Uri.TryCreate(CameraSnapshotUrl, UriKind.Absolute, out var u) ? u : null);
}

// Basic printer info without live status (for fast loading)
/// <summary>
/// Basic printer information without live status values; optimized for list views / dropdowns.
/// </summary>
public partial record PrinterBasicDto(
    Guid Id,
    string Name,
    string ServerUrl,
    string? Notes,
    string? ManufacturerName = null,
    string? ModelName = null,
    PrinterBackend Backend = PrinterBackend.Moonraker,
    string? ApiKey = null,
    string? OriginalServerUrl = null,
    string? IpAddress = null);

public partial record PrinterBasicDto
{
    [JsonIgnore] public Uri? ServerUri => Uri.TryCreate(ServerUrl, UriKind.Absolute, out var u) ? u : null;
}

// Live status info for a specific printer
/// <summary>
/// Lightweight real-time status snapshot for SignalR / polling scenarios.
/// </summary>
public partial record PrinterStatusDto(
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

public partial record PrinterStatusDto
{
    [JsonIgnore] public Uri? ThumbnailUri => string.IsNullOrWhiteSpace(ThumbnailUrl) ? null : (Uri.TryCreate(ThumbnailUrl, UriKind.Absolute, out var u) ? u : null);
    [JsonIgnore] public Uri? CameraStreamUri => string.IsNullOrWhiteSpace(CameraStreamUrl) ? null : (Uri.TryCreate(CameraStreamUrl, UriKind.Absolute, out var u) ? u : null);
    [JsonIgnore] public Uri? CameraSnapshotUri => string.IsNullOrWhiteSpace(CameraSnapshotUrl) ? null : (Uri.TryCreate(CameraSnapshotUrl, UriKind.Absolute, out var u) ? u : null);
}

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
/// Request payload for creating a new printer entry.
/// </summary>
public class CreatePrinterDto
{
    public string Name { get; set; } = string.Empty;
    public string ServerUrl { get; set; } = string.Empty;
    public string? OriginalServerUrl { get; set; }
    public string? Notes { get; set; }
    public Guid? ManufacturerId { get; set; }
    public Guid? ModelId { get; set; }
    // Optional: create new manufacturer/model
    public string? NewManufacturerName { get; set; }
    public string? NewModelName { get; set; }
    public DateTime? DateAcquired { get; set; }
    public PrinterBackend Backend { get; set; } = PrinterBackend.Moonraker;
    public string? ApiKey { get; set; }
}

/// <summary>
/// Update payload for modifying core printer attributes or reassigning catalog metadata.
/// </summary>
public record UpdatePrinterDto(
    string Name,
    string ServerUrl,
    string? Notes,
    Guid? ManufacturerId,
    Guid? ModelId,
    string? NewManufacturerName,
    string? NewModelName,
    DateTime? DateAcquired,
    PrinterBackend? Backend = null,
    string? ApiKey = null,
    string? OriginalServerUrl = null);

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
public partial record SpoolmanConfigDto(string BaseUrl);
public partial record SpoolmanConfigDto
{
    [JsonIgnore] public Uri? BaseUri => string.IsNullOrWhiteSpace(BaseUrl) ? null : (Uri.TryCreate(BaseUrl, UriKind.Absolute, out var u) ? u : null);
}
/// <summary>
/// Represents a single filament spool entity retrieved from Spoolman.
/// </summary>
public partial record SpoolmanSpoolDto(
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
    bool? Archived = null);

public partial record SpoolmanSpoolDto
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
public record ModelDto(Guid Id, string Name, Guid ManufacturerId, double? MaxX = null, double? MaxY = null, double? MaxZ = null, PrinterBackend? DefaultBackend = null, string[]? SupportedFilamentTypes = null);

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
    double? ModelMaxX,
    double? ModelMaxY,
    double? ModelMaxZ,
    DateTime? DateAcquired,
    PrinterBackend Backend = PrinterBackend.Moonraker,
    string? ApiKey = null,
    string? OriginalServerUrl = null,
    string? IpAddress = null);

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

// Network discovery
/// <summary>
/// Printer discovered during network scanning.
/// </summary>
public class DiscoveredPrinterDto
{
    public string IpAddress { get; set; } = string.Empty;
    public int Port { get; set; }
    public string ServerUrl { get; set; } = string.Empty;
    public PrinterBackend Backend { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Manufacturer { get; set; }
    public string? Model { get; set; }
    public string? Firmware { get; set; }
    public string? Version { get; set; }
    public bool IsReachable { get; set; }
    public DateTime DiscoveredAt { get; set; }
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
    List<int> Ports = null!)
{
    public NetworkDiscoverySettingsDto() : this([], 3000, 20, [7125, 80])
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
    string OriginalFileName,
    string DisplayName,
    long FileSizeBytes,
    DateTime UploadedAt,
    GcodeSourceDto Source = GcodeSourceDto.Upload,
    Guid? SourcePrinterId = null,
    string? SourcePrinterName = null,
    string? OriginalPrinterPath = null,
    DateTime? LastSeenOnPrinter = null,
    string? Description = null,
    string[]? Tags = null,
    double? RequiredNozzleDiameter = null,
    string? RequiredMaterial = null,
    string[]? CompatibleMaterials = null,
    double? EstimatedPrintTimeMinutes = null,
    double? EstimatedFilamentLengthMm = null,
    double? EstimatedFilamentWeightG = null,
    double? RequiredBuildVolumeX = null,
    double? RequiredBuildVolumeY = null,
    double? RequiredBuildVolumeZ = null,
    Guid? TargetPrinterId = null,
    string? TargetPrinterName = null,
    Guid? TargetModelId = null,
    string? TargetModelName = null,
    string? SlicerName = null,
    string? SlicerVersion = null,
    bool HasThumbnail = false);

/// <summary>
/// Multipart metadata section for uploading a new G-code file.
/// </summary>
public class CreateGcodeFileDto
{
    public string DisplayName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string[]? Tags { get; set; }
    public double? RequiredNozzleDiameter { get; set; }
    public string? RequiredMaterial { get; set; }
    public string[]? CompatibleMaterials { get; set; }
    public double? EstimatedPrintTimeMinutes { get; set; }
    public double? EstimatedFilamentLengthMm { get; set; }
    public double? EstimatedFilamentWeightG { get; set; }
    public double? RequiredBuildVolumeX { get; set; }
    public double? RequiredBuildVolumeY { get; set; }
    public double? RequiredBuildVolumeZ { get; set; }
    public Guid? TargetPrinterId { get; set; }
    public Guid? TargetModelId { get; set; }
    public string? SlicerName { get; set; }
    public string? SlicerVersion { get; set; }
    public string? SlicerSettings { get; set; }
}

/// <summary>
/// Update payload for modifying G-code library metadata.
/// </summary>
public record UpdateGcodeFileDto(
    string DisplayName,
    string? Description = null,
    string[]? Tags = null,
    double? RequiredNozzleDiameter = null,
    string? RequiredMaterial = null,
    string[]? CompatibleMaterials = null,
    double? EstimatedPrintTimeMinutes = null,
    double? EstimatedFilamentLengthMm = null,
    double? EstimatedFilamentWeightG = null,
    double? RequiredBuildVolumeX = null,
    double? RequiredBuildVolumeY = null,
    double? RequiredBuildVolumeZ = null,
    Guid? TargetPrinterId = null,
    Guid? TargetModelId = null,
    string? SlicerName = null,
    string? SlicerVersion = null,
    string? SlicerSettings = null);

// Print Job DTOs
/// <summary>
/// Lifecycle status of a print job.
/// </summary>
// Converter handled via System.Text.Json options in Program.cs
public enum PrintJobStatusDto
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
    PrintJobStatusDto Status,
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
    int NumberOfExtruders = 1,
    int? MinHotendTemp = null,
    int? MaxHotendTemp = null,
    int? MinBedTemp = null,
    int? MaxBedTemp = null,
    string? CurrentMaterial = null,
    int? CurrentSpoolId = null,
    bool IsAvailable = true,
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
    int? MinHotendTemp = null,
    int? MaxHotendTemp = null,
    int? MinBedTemp = null,
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
    int? MinHotendTemp = null,
    int? MaxHotendTemp = null,
    int? MinBedTemp = null,
    int? MaxBedTemp = null,
    string? CurrentMaterial = null,
    int? CurrentSpoolId = null,
    bool IsAvailable = true);

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
    int FilesFound = 0,
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
    string? ExtractedSlicerName = null,
    string? ExtractedSlicerVersion = null,
    double? ExtractedPrintTime = null,
    double? ExtractedFilamentLength = null,
    double? ExtractedNozzleDiameter = null,
    string? ExtractedMaterial = null,
    string? ExtractedLayerHeight = null,
    string? ExtractedInfill = null);

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
    public Guid[] SelectedFileIds { get; set; } = [];
    public bool AddToLibraryOnly { get; set; } = true; // If false, also create print jobs
    public bool AutoDetectCapabilities { get; set; } = true;
    public string[]? DefaultTags { get; set; }
}

/// <summary>
/// Result summary returned after importing selected harvested files.
/// </summary>
public record GcodeHarvestResultDto(
    Guid OperationId,
    bool Success,
    string Message,
    int DiscoveredFiles = 0,
    int ImportedFiles = 0,
    string[]? Errors = null);

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
    public string Name { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public string FileType { get; set; } = string.Empty; // stl, 3mf, obj, ply
    public DateTime UploadedAt { get; set; }
    public string Url { get; set; } = string.Empty;
    public string? ThumbnailUrl { get; set; }
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
/// Slicer profile parameters controlling core print characteristics.
/// </summary>
public class SlicerProfileDto
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
public class CreateSlicerProfileDto
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
    public bool EnableSupports { get; set; } = false;
    public string Material { get; set; } = "PLA";
    public string Quality { get; set; } = "Standard"; // Draft, Standard, Fine
    public string? AdvancedSettings { get; set; }
    public bool IsDefault { get; set; } = false;
    public bool IsPublic { get; set; } = true;
}

public class SlicerProfileResponseDto
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
    public PrintJobStatusDto Status { get; set; }
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
    public PrintJobStatusDto? Status { get; set; }
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
/// Combined printer identity with capabilities snapshot.
/// </summary>
public class PrinterWithCapabilitiesDto
{
    public Guid PrinterId { get; set; }
    public string PrinterName { get; set; } = string.Empty;
    public string PrinterModel { get; set; } = string.Empty;
    public PrinterCapabilitiesDto? Capabilities { get; set; }
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
/// Credentials for authenticating a user.
/// </summary>
public record LoginRequest(
    string Username,
    string Password);

/// <summary>
/// Registration details for creating a new user account.
/// </summary>
public record RegisterRequest(
    string Username,
    string Email,
    string Password,
    string? FirstName = null,
    string? LastName = null);

/// <summary>
/// Standard authentication outcome with optional JWT token and error information.
/// </summary>
public record AuthenticationResult(
    bool Success,
    string? Token = null,
    DateTime? ExpiresAt = null,
    UserDto? User = null,
    string? Error = null);

/// <summary>
/// User profile with role and permission membership.
/// </summary>
public record UserDto(
    Guid Id,
    string Username,
    string Email,
    string? FirstName = null,
    string? LastName = null,
    bool IsActive = true,
    bool EmailConfirmed = false,
    DateTime? LastLogin = null,
    DateTime CreatedAt = default,
    string[] Roles = null!,
    string[] Permissions = null!);

/// <summary>
/// Role definition aggregating permissions.
/// </summary>
public record RoleDto(
    Guid Id,
    string Name,
    string DisplayName,
    string? Description = null,
    bool IsSystemRole = false,
    bool IsActive = true,
    DateTime CreatedAt = default,
    RolePermissionDto[] Permissions = null!);

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

/// <summary>
/// Request to change the current authenticated user's password.
/// </summary>
public class ChangePasswordRequest
{
    public string CurrentPassword { get; set; } = string.Empty;
    public string NewPassword { get; set; } = string.Empty;
}

/// <summary>
/// Request to reset password using a previously issued token.
/// </summary>
public class ResetPasswordRequest
{
    public string Token { get; set; } = string.Empty;
    public string NewPassword { get; set; } = string.Empty;
}

/// <summary>
/// Initiates a password reset flow by email.
/// </summary>
public record ForgotPasswordRequest(string Email);
/// <summary>
/// Confirms a user's email address using a verification token.
/// </summary>
public record ConfirmEmailRequest(string Token);

#pragma warning restore CA1056
