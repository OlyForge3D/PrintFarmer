using System.Text.Json.Serialization;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure;

// Printer details for edit page
/// <summary>
/// Extended printer details used for edit forms and detail pages.
/// </summary>
public record PrinterDetailsDto(
    Guid Id,
    string Name,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWriting)]
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
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWriting)]
    string? ApiKey = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWriting)]
    string? CameraStreamUrl = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWriting)]
    string? CameraSnapshotUrl = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWriting)]
    string? OriginalServerUrl = null,
    int? BackendPort = null,
    int? FrontendPort = null,
    PrinterCapabilitiesDto? Capabilities = null,
    ToolheadDto[]? Toolheads = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWriting)]
    string? Username = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWriting)]
    string? Password = null,
    bool ObicoEnabled = false,
    string? ObicoServerName = null,
    decimal? Wattage = null,
    decimal? MachineHourlyRate = null,
    bool HasCatalogUpdate = false,
    bool ServerConfigured = false,
    bool ApiKeyConfigured = false,
    bool UsernameConfigured = false,
    bool PasswordConfigured = false,
    bool CameraStreamConfigured = false,
    bool CameraSnapshotConfigured = false);
