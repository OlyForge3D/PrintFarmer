namespace Farm.Backend.Plugin.OctoPrint;

/// <summary>
/// Represents OctoPrint printer status data.
/// </summary>
public sealed class OctoPrintStatusData
{
    public bool IsOnline { get; set; }

    public bool Operational { get; set; }

    public string? State { get; set; }

    public double? Progress { get; set; }

    public string? JobName { get; set; }

    public double? X { get; set; }

    public double? Y { get; set; }

    public double? Z { get; set; }

    public double? HotendTemp { get; set; }

    public double? BedTemp { get; set; }

    public double? HotendTarget { get; set; }

    public double? BedTarget { get; set; }

    public string? ThumbnailUrl { get; set; }

    public string? CameraStreamUrl { get; set; }

    public string? CameraSnapshotUrl { get; set; }
}
