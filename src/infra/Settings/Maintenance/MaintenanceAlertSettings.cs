using System.Text.Json.Serialization;
using Farm.Infrastructure.Settings;

namespace Farm.Infrastructure.Settings;

/// <summary>
/// Configuration settings for the maintenance alert engine.
/// Controls how and when maintenance alerts are evaluated and generated.
/// </summary>
[AppSetting(MaintenanceAlertSettings.SectionName)]
[SettingDisplay(Name = "Maintenance Alerts", Description = "Evaluates maintenance schedules and generates alerts when maintenance is due.", Icon = "pf-icon-alert", Group = "Maintenance", Order = 2)]
public class MaintenanceAlertSettings : IAppSetting
{
    public const string SectionName = "MaintenanceAlerts";

    public static string SectionKey => SectionName;

    /// <summary>
    /// Whether the maintenance alert engine is enabled.
    /// </summary>
    [JsonPropertyName("enabled")]
    [SettingDisplay(Name = "Enabled", Description = "Enable or disable the maintenance alert engine", InputType = SettingInputType.Boolean)]
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Interval in seconds between alert evaluations (default: 300 = 5 minutes).
    /// </summary>
    [JsonPropertyName("intervalSeconds")]
    [SettingDisplay(Name = "Interval (seconds)", Description = "How often to check for maintenance alerts", InputType = SettingInputType.Number, MinValue = 60, MaxValue = 3600)]
    public int IntervalSeconds { get; set; } = 300;

    /// <summary>
    /// Maximum number of printers to evaluate per iteration (to avoid overload).
    /// </summary>
    [JsonPropertyName("maxPrintersPerIteration")]
    [SettingDisplay(Name = "Max Printers Per Iteration", Description = "Limit printers processed each cycle to avoid overload", InputType = SettingInputType.Number, MinValue = 1, MaxValue = 100)]
    public int MaxPrintersPerIteration { get; set; } = 20;

    /// <summary>
    /// Threshold percentage to trigger alert before exact interval (default: 90%).
    /// e.g., for 1000 hour interval, alert at 900 hours.
    /// </summary>
    [JsonPropertyName("thresholdPercentage")]
    [SettingDisplay(Name = "Threshold Percentage", Description = "Percentage of maintenance interval at which to trigger alert (e.g., 90 = alert at 90% of interval)", InputType = SettingInputType.Number, MinValue = 50, MaxValue = 100)]
    public double ThresholdPercentage { get; set; } = 90.0;

    /// <summary>
    /// Whether to automatically dismiss alerts when printer enters maintenance mode.
    /// </summary>
    [JsonPropertyName("autoDismissOnMaintenance")]
    [SettingDisplay(Name = "Auto-dismiss on Maintenance", Description = "Automatically dismiss alerts when printer enters maintenance mode", InputType = SettingInputType.Boolean)]
    public bool AutoDismissOnMaintenance { get; set; } = false;

    /// <summary>
    /// Whether to send SignalR real-time notifications when alerts are created.
    /// </summary>
    [JsonPropertyName("enableSignalRNotifications")]
    [SettingDisplay(Name = "Enable SignalR Notifications", Description = "Send real-time notifications when maintenance alerts are created", InputType = SettingInputType.Boolean)]
    public bool EnableSignalRNotifications { get; set; } = true;

    /// <summary>
    /// Whether to show alerts when printers go offline in the dashboard.
    /// When disabled, offline printers will not be shown in the Alerts panel.
    /// </summary>
    [JsonPropertyName("showOfflinePrinterAlerts")]
    [SettingDisplay(Name = "Show Offline Printer Alerts", Description = "Display alerts in the dashboard when printers are offline", InputType = SettingInputType.Boolean)]
    public bool ShowOfflinePrinterAlerts { get; set; } = true;
}
