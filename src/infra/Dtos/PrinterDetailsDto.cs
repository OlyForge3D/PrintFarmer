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
    bool? HasMmu = null,
    IReadOnlyList<Farm.Infrastructure.Dtos.FilamentFallbackGroupDto>? FallbackGroups = null,

    // Whether the printer's backend + physical topology can attribute wear to individual
    // toolheads (issue #711, F6 backend). Always emitted as a deterministic bool so #719 UI
    // consumers can gate per-tool odometers without inferring absence. True only when the
    // MultiSlotFallback operator feature is enabled AND the persisted Printer.SupportsPerToolAttribution
    // domain flag is true; false in every other case (feature disabled, non-Moonraker backend,
    // single physical toolhead, etc.).
    bool SupportsPerToolAttribution = false);
