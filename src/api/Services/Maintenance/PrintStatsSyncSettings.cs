using System.Text.Json.Serialization;
using Farm.Infrastructure.Settings;

namespace Farm.Web.Api.Services.Maintenance;

/// <summary>
/// Settings for the Print Statistics Sync Service
/// </summary>
[AppSetting(PrintStatsSyncSettings.SectionName)]
[SettingDisplay(Name = "Print Statistics Sync", Description = "Synchronizes printer statistics from external prints for maintenance tracking.", Icon = "pf-icon-stats", Group = "Maintenance", Order = 1)]
public class PrintStatsSyncSettings : IAppSetting
{
    public const string SectionName = "PrintStatisticsSync";

    public static string SectionKey => SectionName;

    /// <summary>
    /// Enable automatic synchronization of printer statistics
    /// </summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Interval in seconds between sync operations (default: 5 minutes = 300 seconds)
    /// </summary>
    [JsonPropertyName("intervalSeconds")]
    public int IntervalSeconds { get; set; } = 300;

    /// <summary>
    /// Include statistics from PrintFarmer-managed jobs (in addition to external prints)
    /// </summary>
    [JsonPropertyName("includePrintFarmerJobs")]
    public bool IncludePrintFarmerJobs { get; set; } = true;

    /// <summary>
    /// Maximum number of printers to sync per iteration (to avoid overload)
    /// </summary>
    [JsonPropertyName("maxPrintersPerIteration")]
    public int MaxPrintersPerIteration { get; set; } = 10;

    /// <summary>
    /// Timeout in seconds for each printer API call
    /// </summary>
    [JsonPropertyName("apiTimeoutSeconds")]
    public int ApiTimeoutSeconds { get; set; } = 10;
}
