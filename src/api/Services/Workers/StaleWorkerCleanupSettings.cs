using System.Text.Json.Serialization;
using Farm.Infrastructure.Settings;

namespace Farm.Web.Api.Services.Workers;

/// <summary>
/// Settings for automatic stale worker cleanup
/// </summary>
[AppSetting(StaleWorkerCleanupSettings.SectionName)]
[SettingDisplay(Name = "Stale Worker Cleanup", Description = "Automatic cleanup of inactive slicer workers.", Icon = "pf-icon-worker", Group = "Slicing", Order = 4)]
public class StaleWorkerCleanupSettings : IAppSetting
{
    public const string SectionName = "StaleWorkerCleanup";

    public static string SectionKey => SectionName;

    /// <summary>
    /// Enable automatic cleanup of stale workers
    /// </summary>
    [JsonPropertyName("enabled")]
    [SettingDisplay(Name = "Enabled", Description = "Enable automatic cleanup of stale workers.", InputType = SettingInputType.Boolean, Order = 1)]
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Interval in seconds between cleanup scans
    /// </summary>
    [JsonPropertyName("intervalSeconds")]
    [SettingDisplay(Name = "Interval (Seconds)", Description = "Time between cleanup scans.", InputType = SettingInputType.Number, MinValue = 300, MaxValue = 86400, Order = 2)]
    public int IntervalSeconds { get; set; } = 3600; // 1 hour default

    /// <summary>
    /// Minutes of inactivity before a worker is considered stale
    /// </summary>
    [JsonPropertyName("staleAfterMinutes")]
    [SettingDisplay(Name = "Stale After (Minutes)", Description = "Minutes of inactivity before a worker is considered stale.", InputType = SettingInputType.Number, MinValue = 10, MaxValue = 10080, Order = 3)]
    public int StaleAfterMinutes { get; set; } = 1440; // 24 hours default

    /// <summary>
    /// Automatically delete stale workers (if false, just marks them offline)
    /// </summary>
    [JsonPropertyName("autoDelete")]
    [SettingDisplay(Name = "Auto Delete", Description = "Automatically delete stale workers (if disabled, just marks them offline).", InputType = SettingInputType.Boolean, Order = 4)]
    public bool AutoDelete { get; set; } = false;
}
