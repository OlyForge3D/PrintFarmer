using System.Text.Json.Serialization;
using Farm.Infrastructure.Settings;

namespace Farm.Web.Api.Services.Maintenance;

/// <summary>
/// Settings for the Print Statistics Sync Service
/// </summary>
[AppSetting(PrintStatsSyncSettings.SectionName)]
[SettingGroup("Maintenance", DisplayName = "Maintenance", Description = "Printer maintenance and lifecycle settings", Icon = "pf-icon-maintenance", Order = 1)]
[SettingDisplay(Name = "Print Statistics Sync", Description = "Synchronizes printer statistics from external prints for maintenance tracking.", Icon = "pf-icon-stats", Group = "Maintenance", Order = 1)]
public class PrintStatsSyncSettings : IAppSetting
{
    public const string SectionName = "PrintStatisticsSync";

    public static string SectionKey => SectionName;

    /// <summary>
    /// Enable automatic synchronization of printer statistics
    /// </summary>
    [JsonPropertyName("enabled")]
    [SettingDisplay(Name = "Enabled", Description = "Enable automatic synchronization of printer statistics.", InputType = SettingInputType.Boolean, Order = 1)]
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Interval in seconds between sync operations (default: 5 minutes = 300 seconds)
    /// </summary>
    [JsonPropertyName("intervalSeconds")]
    [SettingDisplay(Name = "Interval (Seconds)", Description = "Time between sync operations.", InputType = SettingInputType.Number, MinValue = 60, MaxValue = 86400, Order = 2)]
    public int IntervalSeconds { get; set; } = 300;

    /// <summary>
    /// Include statistics from PrintFarmer-managed jobs (in addition to external prints)
    /// </summary>
    [JsonPropertyName("includePrintFarmerJobs")]
    [SettingDisplay(Name = "Include PrintFarmer Jobs", Description = "Include statistics from PrintFarmer-managed jobs (in addition to external prints).", InputType = SettingInputType.Boolean, Order = 3)]
    public bool IncludePrintFarmerJobs { get; set; } = true;

    /// <summary>
    /// Maximum number of printers to sync per iteration (to avoid overload)
    /// </summary>
    [JsonPropertyName("maxPrintersPerIteration")]
    [SettingDisplay(Name = "Max Printers Per Iteration", Description = "Maximum number of printers to sync per iteration.", InputType = SettingInputType.Number, MinValue = 1, MaxValue = 100, Order = 4)]
    public int MaxPrintersPerIteration { get; set; } = 10;

    /// <summary>
    /// Timeout in seconds for each printer API call
    /// </summary>
    [JsonPropertyName("apiTimeoutSeconds")]
    [SettingDisplay(Name = "API Timeout (Seconds)", Description = "Timeout in seconds for each printer API call.", InputType = SettingInputType.Number, MinValue = 1, MaxValue = 120, Order = 5)]
    public int ApiTimeoutSeconds { get; set; } = 10;
}
