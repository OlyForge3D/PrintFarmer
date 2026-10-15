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
    [property: JsonIgnore] string? ThumbnailUrl,
    [property: JsonIgnore] string? CameraStreamUrl,
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
    string? FileName = null,
    PrinterSafetyTelemetryDto? SafetyTelemetry = null,
    [property: JsonIgnore] string? ThumbnailCacheIdentity = null,
    int? CurrentLayer = null,
    int? TotalLayers = null,
    double? FanSpeedPercent = null,
    double? LiveZOffsetMm = null)
{
    /// <summary>
    /// Relative authenticated proxy URL for the active job thumbnail.
    /// </summary>
    public string? CurrentJobThumbnailUrl =>
        PrinterThumbnailUrl.Create(Id, State, JobName, ThumbnailUrl, ThumbnailCacheIdentity);

    /// <summary>
    /// Creates the cache snapshot corresponding to this broadcast, preserving all shared fields.
    /// </summary>
    public PrinterStatusDto ToStatusDto(string? cameraSnapshotUrl = null, double? printTimeLeftSeconds = null) =>
        new(
            Id: Id,
            IsOnline: IsOnline,
            State: State,
            Progress: Progress,
            JobName: JobName,
            FileName: FileName,
            ThumbnailUrl: ThumbnailUrl,
            CameraStreamUrl: CameraStreamUrl,
            CameraSnapshotUrl: cameraSnapshotUrl,
            X: X,
            Y: Y,
            Z: Z,
            HotendTemp: HotendTemp,
            BedTemp: BedTemp,
            HotendTarget: HotendTarget,
            BedTarget: BedTarget,
            SpoolInfo: SpoolInfo,
            MmuStatus: MmuStatus,
            PrintTimeLeftSeconds: printTimeLeftSeconds,
            HomedAxes: HomedAxes,
            SafetyTelemetry: SafetyTelemetry,
            ThumbnailCacheIdentity: ThumbnailCacheIdentity,
            CurrentLayer: CurrentLayer,
            TotalLayers: TotalLayers,
            FanSpeedPercent: FanSpeedPercent,
            LiveZOffsetMm: LiveZOffsetMm);
}
