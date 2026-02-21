namespace Farm.Slicer.Module.Services.Configuration;

/// <summary>
/// Configuration options for local artifact storage. Cloud storage is intentionally out of scope.
/// </summary>
public sealed class SlicerArtifactStorageSettings
{
    /// <summary>Configuration section name in appsettings.</summary>
    public const string SectionName = "ArtifactStorage";

    /// <summary>Root path under which artifacts are stored. Default: "artifacts".</summary>
    public string RootPath { get; set; } = "artifacts";

    /// <summary>Maximum allowed uploaded file size in bytes (default 100 MB).</summary>
    public long MaxFileSizeBytes { get; set; } = 100 * 1024 * 1024;

    /// <summary>Comma-separated list of allowed kinds (e.g. "gcode,thumbnail,preview,log").</summary>
    public string AllowedKinds { get; set; } = "gcode,thumbnail,preview,log";

    /// <summary>Enable static file serving for artifacts at /artifacts/* URLs.</summary>
    public bool EnableStaticServing { get; set; }

    /// <summary>Storage warning threshold in bytes (default 5 GB).</summary>
    public long StorageWarningThresholdBytes { get; set; } = 5L * 1024 * 1024 * 1024;

    /// <summary>Storage critical threshold in bytes (default 10 GB).</summary>
    public long StorageCriticalThresholdBytes { get; set; } = 10L * 1024 * 1024 * 1024;

    /// <summary>Enable storage threshold monitoring and alerting.</summary>
    public bool EnableStorageAlerts { get; set; } = true;

    /// <summary>Maximum age in days for artifacts before they become eligible for cleanup.</summary>
    public int? MaxAgeDays { get; set; } = 90;

    /// <summary>Maximum total storage in bytes before oldest artifacts are cleaned up.</summary>
    public long? MaxTotalBytes { get; set; } = 10L * 1024 * 1024 * 1024;

    /// <summary>When true, cleanup service only logs what would be deleted without actually deleting.</summary>
    public bool EnableCleanupDryRun { get; set; } = true;

    /// <summary>Interval in hours between cleanup service scans.</summary>
    public int CleanupIntervalHours { get; set; } = 24;
}
