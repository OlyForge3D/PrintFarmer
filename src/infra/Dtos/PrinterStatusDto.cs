using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using Farm.Infrastructure.Annotations;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure;

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
    PrinterSpoolInfoDto? SpoolInfo = null,
    MmuStatusDto? MmuStatus = null);
