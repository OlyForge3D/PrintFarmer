namespace Farm.Web.Shared;

public enum PrinterBackend
{
    Moonraker = 0,
    PrusaLink = 1
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
    string? IpAddress = null);

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
    double? BedTarget);

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

// Catalog (Manufacturers / Models)
public record ManufacturerDto(Guid Id, string Name);
public record ModelDto(Guid Id, string Name, Guid ManufacturerId, double? MaxX = null, double? MaxY = null, double? MaxZ = null);

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
