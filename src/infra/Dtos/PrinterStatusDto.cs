using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using Farm.Infrastructure.Annotations;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;

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
    string? FileName = null,
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
    MmuStatusDto? MmuStatus = null,
    IReadOnlyDictionary<int, ExtruderTemperature>? ExtruderTemperatures = null,
    int? DetectedExtruderCount = null,
    double? PrintTimeLeftSeconds = null,
    int? SpeedMultiplier = null)
{
    /// <summary>
    /// Returns a copy with FileName derived from JobName (path stripped) and JobName preserved as-is.
    /// </summary>
    public PrinterStatusDto WithNormalizedFileName() =>
        string.IsNullOrEmpty(JobName)
            ? (FileName != null ? this with { FileName = null } : this)
            : this with { FileName = Path.GetFileName(JobName) };

    /// <summary>
    /// Extract just the file name from a job name path (e.g. ".cache/file.gcode" → "file.gcode").
    /// </summary>
    public static string? ExtractFileName(string? jobName) =>
        string.IsNullOrEmpty(jobName) ? null : Path.GetFileName(jobName);
}
