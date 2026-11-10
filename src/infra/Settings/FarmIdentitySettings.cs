using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Settings;

/// <summary>Farm-wide identity, independent of stored schedule timezones and UTC instants.</summary>
[AppSetting(SectionName)]
[SettingGroup("FarmIdentity", DisplayName = "Farm Identity", Description = "Farm name and local time", Icon = "pf-icon-settings", Order = 0)]
[SettingDisplay(Name = "Farm Identity", Description = "Name your farm and choose the timezone for the farm-local clock.", Group = "FarmIdentity")]
public class FarmIdentitySettings : IAppSetting, IValidatableSetting
{
    public const string SectionName = "FarmIdentity";

    public static string SectionKey => SectionName;

    [SettingDisplay(Name = "Farm name", Description = "Shown in the desktop and mobile web app header. Maximum 80 characters.", Required = true, Order = 0)]
    [JsonPropertyName("farmName")]
    public string FarmName { get; set; } = "PrintFarmer";

    [SettingDisplay(Name = "Farm timezone", Description = "IANA timezone for the farm-local clock, e.g. America/Los_Angeles or Europe/London. Defaults to UTC. Does not change existing schedules, timestamps, or planning hours.", Required = true, Order = 1)]
    [JsonPropertyName("timeZone")]
    public string TimeZone { get; set; } = "UTC";

    /// <summary>Rejects blank names, control characters, and unknown or Windows-only timezone IDs.</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(FarmName) || FarmName.Length > 80 || FarmName.Any(char.IsControl))
        {
            throw new ValidationException(
                new ValidationResult("Farm name must contain 1 to 80 characters and no control characters.", [nameof(FarmName)]),
                null, FarmName);
        }

        if (string.IsNullOrWhiteSpace(TimeZone) ||
            (TimeZone != "UTC" && !TimeZoneInfo.TryConvertIanaIdToWindowsId(TimeZone, out _)) ||
            !TimeZoneInfo.TryFindSystemTimeZoneById(TimeZone, out _))
        {
            throw new ValidationException(
                new ValidationResult("Choose a valid IANA timezone, such as America/Los_Angeles, Europe/London, or UTC.", [nameof(TimeZone)]),
                null, TimeZone);
        }
    }
}
