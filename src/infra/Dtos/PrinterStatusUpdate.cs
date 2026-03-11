using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using Farm.Infrastructure.Annotations;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure;

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
    PrinterSpoolInfoDto? SpoolInfo,
    MmuStatusDto? MmuStatus = null,
    string? FileName = null);
