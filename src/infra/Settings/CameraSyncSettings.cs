using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Settings;

/// <summary>
/// Controls background discovery of printer cameras through the backend plugins.
/// </summary>
[AppSetting(SectionName)]
[SettingGroup("Printers", DisplayName = "Printers", Description = "Printer connection and communication settings", Icon = "pf-icon-printer", Order = 1)]
[SettingDisplay(Name = "Camera Sync", Description = "Automatic discovery of cameras reported by printer backends.", Icon = "pf-icon-camera", Group = "Printers", Order = 20)]
public class CameraSyncSettings : IAppSetting
{
    public const string SectionName = "CameraSync";

    public static string SectionKey => SectionName;

    /// <summary>Whether printer cameras are discovered automatically on connect, create, and on an interval.</summary>
    [SettingDisplay(Name = "Automatic camera sync", Description = "Discover printer cameras on connect, on create, and periodically.", InputType = SettingInputType.Boolean)]
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>Minutes between periodic camera sync passes across all printers.</summary>
    [SettingDisplay(Name = "Camera sync interval", Unit = "min", Description = "Minutes between periodic camera discovery passes across all printers.", InputType = SettingInputType.Number, MinValue = 1, MaxValue = 1440)]
    [JsonPropertyName("intervalMinutes")]
    public int IntervalMinutes { get; set; } = 15;
}
