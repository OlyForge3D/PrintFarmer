using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Settings;

[SystemSetting(SystemLogSettings.SectionName)]
[SettingDisplay(Name = "System Log", Description = "System log retention and export settings.", Icon = "pf-icon-systemlog", Group = "System", Order = 4)]
public class SystemLogSettings : ISystemSetting, IValidatableSetting
{
    public const string SectionName = "SystemLog";
    public static string SectionKey => SectionName;

    [SettingDisplay(Name = "Retention Days", MinValue = 1, MaxValue = 365, Description = "Number of days to retain logs.", InputType = SettingInputType.Number)]
    [Range(1, 365)]
    [JsonPropertyName("retentionDays")]
    public int RetentionDays { get; set; } = 30;

    [SettingDisplay(Name = "Enable Export", Description = "Allow exporting logs.", InputType = SettingInputType.Boolean)]
    [JsonPropertyName("enableExport")]
    public bool EnableExport { get; set; } = true;
    public void Validate()
    {
        if (RetentionDays is < 1 or > 365)
        {
            throw new ValidationException("RetentionDays must be between 1 and 365.");
        }
    }
}

// Use TempTargets from AppSettings.cs (sealed class)
