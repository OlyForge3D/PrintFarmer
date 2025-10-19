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
}
