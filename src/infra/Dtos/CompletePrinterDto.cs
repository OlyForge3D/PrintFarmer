using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using Farm.Infrastructure.Annotations;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure;

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
