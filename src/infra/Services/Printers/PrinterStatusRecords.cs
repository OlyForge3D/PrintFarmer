namespace Farm.Infrastructure.Services.Printers;

/// <summary>
/// Basic printer status information.
/// </summary>
public record PrinterStatus(bool IsOnline, string? State);

/// <summary>
/// Current print job information.
/// </summary>
public record PrinterJob(string? PrintState, double? Progress, string? JobName, string? ThumbnailUrl);

/// <summary>
/// Temperature reading for a single extruder (Tn index).
/// </summary>
public record ExtruderTemperature(double Current, double Target);

/// <summary>
/// Composite printer status combining state, job progress, position, and temperatures.
/// </summary>
#pragma warning disable CA1056 // URI-like properties should not be strings
public record PrinterCompositeStatus(
    bool IsOnline,
    string? State,
    double? Progress,
    string? JobName,
    string? ThumbnailUrl,
    string? CameraStreamUrl,
    string? CameraSnapshotUrl,
    double? X = null,
    double? Y = null,
    double? Z = null,
    double? HotendTemp = null,
    double? BedTemp = null,
    double? HotendTarget = null,
    double? BedTarget = null,
    IReadOnlyDictionary<int, ExtruderTemperature>? ExtruderTemperatures = null,
    int? DetectedExtruderCount = null);
#pragma warning restore CA1056 // URI-like properties should not be strings
