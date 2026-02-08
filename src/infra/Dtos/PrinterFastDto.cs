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
    string? ApiKey = null,
    string? OriginalServerUrl = null,
    int BackendPort = 80,
    int? FrontendPort = null,
    bool InMaintenance = false,
    bool IsEnabled = true,
    string? CameraStreamUrl = null,
    string? CameraSnapshotUrl = null,
    string? BackendUrl = null,
    string? FrontendUrl = null);
