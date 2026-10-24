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
    [property: JsonIgnore] string? ThumbnailUrl = null,
    [property: JsonIgnore] string? CameraStreamUrl = null,
    [property: JsonIgnore] string? CameraSnapshotUrl = null,
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
    int? SpeedMultiplier = null,
    string? HomedAxes = null,
    PrinterSafetyTelemetryDto? SafetyTelemetry = null,
    [property: JsonIgnore] string? ThumbnailCacheIdentity = null,
    int? CurrentLayer = null,
    int? TotalLayers = null,
    double? FanSpeedPercent = null,
    double? LiveZOffsetMm = null,
    string? ActiveExtruderId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] double? SpeedFactorPercent = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] double? FlowFactorPercent = null)
{
    /// <summary>
    /// Backend-neutral sensors and fans (issue #3406), bounded and deduplicated by
    /// <see cref="PrinterSensorNormalizer"/> on assignment. Empty for backends without sensor telemetry.
    /// </summary>
    public IReadOnlyList<PrinterSensorDto> Sensors
    {
        get => _sensors ?? PrinterSensorList.Empty;
        init => _sensors = PrinterSensorNormalizer.ToList(value);
    }

    private readonly PrinterSensorList? _sensors;

    /// <summary>
    /// Whether <see cref="Sensors"/> was assigned. An assigned list (even empty) is the printer's
    /// complete sensor snapshot; an unassigned list means the producer did not report sensors.
    /// </summary>
    [JsonIgnore]
    public bool SensorsReported => _sensors is not null;

    /// <summary>
    /// Relative authenticated proxy URL for the active job thumbnail.
    /// </summary>
    public string? CurrentJobThumbnailUrl =>
        PrinterThumbnailUrl.Create(Id, State, JobName, ThumbnailUrl, ThumbnailCacheIdentity);

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
