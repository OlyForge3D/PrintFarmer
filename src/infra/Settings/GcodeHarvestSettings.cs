namespace Farm.Infrastructure.Settings;

/// <summary>
/// Configuration options for G-code harvest operations.
/// System setting: bootstrap/config-only, not persisted in database.
/// </summary>
[SystemSetting("GcodeHarvest")]
public sealed class GcodeHarvestSettings : ISystemSetting
{
    public static string SectionKey => "GcodeHarvest";

    /// <summary>
    /// Maximum number of files to import concurrently (default: 2).
    /// Set to 1 for sequential imports, higher values for parallel imports (use caution with system resources).
    /// </summary>
    public int MaxConcurrentImports { get; set; } = 2;

    /// <summary>
    /// Timeout in seconds for individual file import operations (default: 300 seconds / 5 minutes).
    /// </summary>
    public int ImportTimeoutSeconds { get; set; } = 300;
}
