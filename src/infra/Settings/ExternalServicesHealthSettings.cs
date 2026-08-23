using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Farm.Infrastructure.Settings;

namespace Farm.Infrastructure.Settings;

[AppSetting("ExternalServicesHealth")]
[SettingGroup("System", DisplayName = "System", Description = "Core system configuration", Icon = "pf-icon-system", Order = 6)]
[SettingDisplay(Name = "External Services Health", Description = "Settings that control health checks against external printer backends.", Icon = "pf-icon-health", Group = "System", Order = 20)]
public class ExternalServicesHealthSettings : IAppSetting, IValidatableSetting
{
    // SectionKey required by the IAppSetting static abstract - kept for reflection/consistency
    public static string SectionKey => "ExternalServicesHealth";

    [JsonPropertyName("percentFailedThreshold")]
    [SettingDisplay(Name = "Percent failed threshold", Description = "Percent of failed external services required to mark the system Unhealthy (0-100).", InputType = SettingInputType.Number, MinValue = 0, MaxValue = 100, Order = 10)]
    public int PercentFailedThreshold { get; set; } = 100; // 0-100, default 100 means only Unhealthy when 100% fail

    public void Validate()
    {
        if (PercentFailedThreshold is < 0 or > 100)
        {
            ValidationResult vr = new ValidationResult("PercentFailedThreshold must be between 0 and 100", new[] { nameof(PercentFailedThreshold) });
            throw new ValidationException(vr, null, PercentFailedThreshold);
        }
    }
}
