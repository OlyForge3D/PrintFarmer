namespace Farm.Web.Shared;

public enum PrinterBackend
{
    Moonraker = 0,
    PrusaLink = 1,
    SDCP = 2
}

public record PrinterDto(
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

// Basic printer info without live status (for fast loading)
public record PrinterBasicDto(
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

// Live status info for a specific printer
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

// Real-time update payload for SignalR
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
    PrinterSpoolInfoDto? SpoolInfo);

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

public record CommandResult(bool Success, string? Message = null);

public record TempTargets(double? Hotend, double? Bed);
public record MoveRequest(double? X, double? Y, double? Z, double? F);

// Spoolman integration
public record SpoolmanConfigDto(string BaseUrl);
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
    DateTime? LastUsedAt = null);

// Printer spool information for Moonraker printers
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
public record ManufacturerDto(Guid Id, string Name);
public record ModelDto(Guid Id, string Name, Guid ManufacturerId, double? MaxX = null, double? MaxY = null, double? MaxZ = null, PrinterBackend? DefaultBackend = null);

// Printer details for edit page
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

// Filament temperature presets (admin-configurable)
public record FilamentPresetsDto(
    TempTargets Abs,
    TempTargets Asa,
    TempTargets Pla,
    TempTargets Pc,
    TempTargets Pctg,
    TempTargets Petg);

// Resolve hostname/IP utility
public record ResolveHostnameRequest(string ServerUrl, PrinterBackend Backend);
public record ResolveHostnameResponse(string NormalizedInputUrl, string? ResolvedIp, string ResolvedBaseUrl);

// Network discovery
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

// Network discovery configuration
public record NetworkDiscoverySettingsDto(
    List<string> NetworkRanges,
    int TimeoutMs = 3000,
    int MaxConcurrentScans = 20,
    List<int> Ports = null!)
{
    public NetworkDiscoverySettingsDto() : this(new List<string>(), 3000, 20, new List<int> { 80, 7125 })
    {
    }
}

// History Models (matching Moonraker structure)
public class HistoryListResponse
{
    public int Count { get; set; }
    public HistoryJob[] Jobs { get; set; } = Array.Empty<HistoryJob>();
}

public class HistoryJob
{
    public string JobId { get; set; } = string.Empty;
    public bool Exists { get; set; }
    public double? EndTime { get; set; }
    public double FilamentUsed { get; set; }
    public string Filename { get; set; } = string.Empty;
    public Dictionary<string, object> Metadata { get; set; } = new();
    public double PrintDuration { get; set; }
    public string Status { get; set; } = string.Empty;
    public double StartTime { get; set; }
    public double TotalDuration { get; set; }
    public string User { get; set; } = string.Empty;
    public AuxiliaryData[]? AuxiliaryData { get; set; }
    public string? ThumbnailUrl { get; set; }
}

public class AuxiliaryData
{
    public string Provider { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public object Value { get; set; } = new();
    public string Description { get; set; } = string.Empty;
    public string? Units { get; set; }
}

public class HistoryTotals
{
    public JobTotals JobTotals { get; set; } = new();
    public AuxiliaryTotals[]? AuxiliaryTotals { get; set; }
}

public class JobTotals
{
    public int TotalJobs { get; set; }
    public double TotalTime { get; set; }
    public double TotalPrintTime { get; set; }
    public double TotalFilamentUsed { get; set; }
    public double LongestJob { get; set; }
    public double LongestPrint { get; set; }
}

public class AuxiliaryTotals
{
    public string Provider { get; set; } = string.Empty;
    public string Field { get; set; } = string.Empty;
    public double Maximum { get; set; }
    public double Total { get; set; }
}
