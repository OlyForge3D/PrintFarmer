using System.Text.Json.Serialization;
using Farm.Infrastructure.Settings;

namespace Farm.Web.Api.Services.Maintenance;

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
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Interval in seconds between alert evaluations (default: 300 = 5 minutes).
    /// </summary>
    public int IntervalSeconds { get; set; } = 300;

    /// <summary>
    /// Maximum number of printers to evaluate per iteration (to avoid overload).
    /// </summary>
    public int MaxPrintersPerIteration { get; set; } = 20;

    /// <summary>
    /// Threshold percentage to trigger alert before exact interval (default: 90%).
    /// e.g., for 1000 hour interval, alert at 900 hours.
    /// </summary>
    public double ThresholdPercentage { get; set; } = 90.0;

    /// <summary>
    /// Whether to automatically dismiss alerts when printer enters maintenance mode.
    /// </summary>
    public bool AutoDismissOnMaintenance { get; set; } = false;

    /// <summary>
    /// Whether to send SignalR real-time notifications when alerts are created.
    /// </summary>
    public bool EnableSignalRNotifications { get; set; } = true;
}
