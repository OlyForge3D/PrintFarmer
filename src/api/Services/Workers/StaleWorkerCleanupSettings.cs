using Farm.Infrastructure.Settings;

namespace Farm.Web.Api.Services.Workers;

/// <summary>
/// Settings for automatic stale worker cleanup
/// </summary>
[AppSetting(StaleWorkerCleanupSettings.SectionName)]
public class StaleWorkerCleanupSettings : IAppSetting
{
    public const string SectionName = "StaleWorkerCleanup";

    public static string SectionKey => SectionName;

    /// <summary>
    /// Enable automatic cleanup of stale workers
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Interval in seconds between cleanup scans
    /// </summary>
    public int IntervalSeconds { get; set; } = 3600; // 1 hour default

    /// <summary>
    /// Minutes of inactivity before a worker is considered stale
    /// </summary>
    public int StaleAfterMinutes { get; set; } = 1440; // 24 hours default

    /// <summary>
    /// Automatically delete stale workers (if false, just marks them offline)
    /// </summary>
    public bool AutoDelete { get; set; } = false;
}
