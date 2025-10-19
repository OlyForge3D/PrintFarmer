namespace Farm.Infrastructure.Settings;

/// <summary>
/// Configuration options for local artifact storage. Cloud storage is intentionally out of scope.
/// </summary>
public sealed class ArtifactStorageSettings
{
    public const string SectionName = "ArtifactStorage";

    /// <summary>
    /// Root path (absolute or relative to ContentRoot) under which artifacts are stored.
    /// Default: "artifacts".
    /// </summary>
    public string RootPath { get; set; } = "artifacts";

    /// <summary>
    /// Maximum allowed uploaded file size in bytes (default 100MB).
    /// </summary>
    public long MaxFileSizeBytes { get; set; } = 100 * 1024 * 1024;

    /// <summary>
    /// Comma-separated list of allowed kinds (e.g. "gcode,thumbnail,preview,log").
    /// </summary>
    public string AllowedKinds { get; set; } = "gcode,thumbnail,preview,log";

    /// <summary>
    /// Enable static file serving for artifacts at /artifacts/* URLs.
    /// When enabled, artifacts can be accessed directly without API authentication.
    /// Default: false (use API download endpoint only).
    /// </summary>
    public bool EnableStaticServing { get; set; } = false;

    /// <summary>
    /// Storage warning threshold in bytes (default 5GB).
    /// When total storage exceeds this, a warning event is logged.
    /// </summary>
    public long StorageWarningThresholdBytes { get; set; } = 5L * 1024 * 1024 * 1024;

    /// <summary>
    /// Storage critical threshold in bytes (default 10GB).
    /// When total storage exceeds this, a critical event is logged and metrics flag is set.
    /// </summary>
    public long StorageCriticalThresholdBytes { get; set; } = 10L * 1024 * 1024 * 1024;

    /// <summary>
    /// Enable storage threshold monitoring and alerting.
    /// Default: true.
    /// </summary>
    public bool EnableStorageAlerts { get; set; } = true;
}
