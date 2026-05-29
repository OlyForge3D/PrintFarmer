using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure;

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
    int? BackendPort = null,
    int? FrontendPort = null,
    PrinterCapabilitiesDto? Capabilities = null,
    ToolheadDto[]? Toolheads = null,
    string? Username = null,
    string? Password = null,
    bool ObicoEnabled = false,
    string? ObicoServerName = null,
    decimal? Wattage = null,
    decimal? MachineHourlyRate = null,
    bool HasCatalogUpdate = false,
    decimal? ZOffsetMm = null,
    DateTime? LastZOffsetCalibrationAt = null,
    bool UseModelDispatchDefaults = true,
    string? BuddyCameraIp = null,
    double? NozzleDiameter = null,
    bool? HasMmu = null);
