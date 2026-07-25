using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using Farm.Infrastructure.Annotations;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure;

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
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWriting)]
    string? ApiKey = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWriting)]
    string? OriginalServerUrl = null,
    int BackendPort = 80,
    int? FrontendPort = null,
    bool InMaintenance = false,
    bool IsEnabled = true,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWriting)]
    string? CameraStreamUrl = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWriting)]
    string? CameraSnapshotUrl = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWriting)]
    string? BackendUrl = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWriting)]
    string? FrontendUrl = null);
