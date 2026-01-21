using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Farm.Infrastructure.Settings;

namespace Farm.Infrastructure.Settings;

[AppSetting("ExternalServicesHealth")]
[SettingDisplay(Name = "External Services Health", Description = "Settings that control health checks against external printer backends.", Icon = "pf-icon-health", Group = "System", Order = 20)]
public class ExternalServicesHealthSettings : IAppSetting, IValidatableSetting
{
    // SectionKey required by the IAppSetting static abstract - kept for reflection/consistency
    public static string SectionKey => "ExternalServicesHealth";

    [JsonPropertyName("percentFailedThreshold")]
    [SettingDisplay(Name = "Percent Failed Threshold", Description = "Percent of failed external services required to mark the system Unhealthy (0-100).", InputType = SettingInputType.Number, MinValue = 0, MaxValue = 100, Order = 10)]
    public int PercentFailedThreshold { get; set; } = 100; // 0-100, default 100 means only Unhealthy when 100% fail

    [JsonPropertyName("printersToCheck")]
    [SettingDisplay(Name = "Printers To Check", Description = "Number of registered printers to probe during external services health check. -1 = all, 0 = none, >0 = number to check.", InputType = SettingInputType.Number, MinValue = -1, MaxValue = 100, Order = 20)]
    public int PrintersToCheck { get; set; } = 0; // 0 = none (default), -1 = all printers, >0 = number to check

    public void Validate()
    {
        if (PercentFailedThreshold is < 0 or > 100)
        {
            ValidationResult vr = new ValidationResult("PercentFailedThreshold must be between 0 and 100", new[] { nameof(PercentFailedThreshold) });
            throw new ValidationException(vr, null, PercentFailedThreshold);
        }

        // Allow -1 for all, 0 for none, or positive numbers; clamp large values
        if (PrintersToCheck < -1)
        {
            ValidationResult vr = new ValidationResult("PrintersToCheck must be -1, 0, or a positive integer", new[] { nameof(PrintersToCheck) });
            throw new ValidationException(vr, null, PrintersToCheck);
        }
    }
}
