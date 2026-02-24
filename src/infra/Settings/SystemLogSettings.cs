using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Settings;

[AppSetting(SystemLogSettings.SectionName)]
[SettingGroup("System", DisplayName = "System", Description = "System-level configuration", Icon = "pf-icon-system", Order = 10)]
[SettingDisplay(Name = "System Logging", Description = "Database logging configuration, retention, and export settings.", Icon = "pf-icon-systemlog", Group = "System", Order = 4)]
public class SystemLogSettings : IAppSetting, IValidatableSetting
{
    public const string SectionName = "SystemLog";

    public static string SectionKey => SectionName;

    [SettingDisplay(Name = "Enable Database Logging", Description = "Write application logs to the database. Disable to stop all DB log writes.", InputType = SettingInputType.Boolean, Order = 1)]
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [SettingDisplay(Name = "Minimum Log Level", Description = "Only log messages at or above this severity level.", InputType = SettingInputType.Select, AllowedValues = ["Warning", "Error", "Critical"], Order = 2)]
    [JsonPropertyName("minimumLevel")]
    public string MinimumLevel { get; set; } = "Warning";

    [SettingDisplay(Name = "Retention Days", MinValue = 1, MaxValue = 365, Description = "Number of days to retain logs before automatic cleanup.", InputType = SettingInputType.Number, Order = 3)]
    [Range(1, 365)]
    [JsonPropertyName("retentionDays")]
    public int RetentionDays { get; set; } = 30;

    [SettingDisplay(Name = "Enable Export", Description = "Allow exporting logs from the UI.", InputType = SettingInputType.Boolean, Order = 4)]
    [JsonPropertyName("enableExport")]
    public bool EnableExport { get; set; } = true;

    public void Validate()
    {
        if (RetentionDays is < 1 or > 365)
        {
            throw new ValidationException("RetentionDays must be between 1 and 365.");
        }

        string[] validLevels = ["Warning", "Error", "Critical"];
        if (!Array.Exists(validLevels, l => l.Equals(MinimumLevel, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ValidationException("MinimumLevel must be Warning, Error, or Critical.");
        }
    }
}
