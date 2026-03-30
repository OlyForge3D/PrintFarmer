using System.Text.Json.Serialization;
using Farm.Infrastructure.Settings;

namespace Farm.Infrastructure.Settings;

/// <summary>
/// Configuration settings for the catalog update detection engine.
/// Controls how often printers are scanned for available model template updates.
/// </summary>
[AppSetting(CatalogUpdateSettings.SectionName)]
[SettingDisplay(Name = "Catalog Update Detection", Description = "Periodically scans printers for available catalog model template updates and notifies users.", Icon = "pf-icon-refresh", Group = "Catalog", Order = 1)]
public class CatalogUpdateSettings : IAppSetting
{
    public const string SectionName = "CatalogUpdates";

    public static string SectionKey => SectionName;

    /// <summary>
    /// Whether catalog update detection is enabled.
    /// </summary>
    [JsonPropertyName("enabled")]
    [SettingDisplay(Name = "Enabled", Description = "Enable or disable catalog update detection", InputType = SettingInputType.Boolean)]
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Interval in seconds between catalog update scans (default: 3600 = 1 hour).
    /// </summary>
    [JsonPropertyName("intervalSeconds")]
    [SettingDisplay(Name = "Interval (seconds)", Description = "How often to scan for catalog model updates", InputType = SettingInputType.Number, MinValue = 300, MaxValue = 86400)]
    public int IntervalSeconds { get; set; } = 3600;

    /// <summary>
    /// When true, automatically applies the latest catalog template to any printer
    /// whose model has been updated, without requiring user action.
    /// Defaults to false — users are notified and must apply manually.
    /// </summary>
    [JsonPropertyName("autoApply")]
    [SettingDisplay(Name = "Auto-apply Updates", Description = "Automatically apply catalog model updates to printers when detected, without user confirmation", InputType = SettingInputType.Boolean)]
    public bool AutoApply { get; set; } = false;
}
