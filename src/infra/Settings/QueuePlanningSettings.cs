using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Settings;

[AppSetting(SectionName)]
[SettingGroup("Operations", DisplayName = "Operations", Description = "Operational settings and cost tracking", Icon = "pf-icon-operations", Order = 3)]
[SettingDisplay(Name = "Queue Planning", Description = "Assumptions used for queue completion ETA planning.", Icon = "pf-icon-timeline", Group = "Operations", Order = 5)]
public class QueuePlanningSettings : IAppSetting, IValidatableSetting
{
    public const string SectionName = "QueuePlanning";

    public static string SectionKey => SectionName;

    [JsonPropertyName("workdayStartHourUtc")]
    [SettingDisplay(Name = "Workday Start Hour (UTC)", Description = "Hour of day when staffed work begins (0-23 UTC).", InputType = SettingInputType.Number, MinValue = 0, MaxValue = 23, Order = 1)]
    public int WorkdayStartHourUtc { get; set; } = 8;

    [JsonPropertyName("workdayEndHourUtc")]
    [SettingDisplay(Name = "Workday End Hour (UTC)", Description = "Hour of day when staffed work ends (0-23 UTC).", InputType = SettingInputType.Number, MinValue = 0, MaxValue = 23, Order = 2)]
    public int WorkdayEndHourUtc { get; set; } = 17;

    [JsonPropertyName("bedClearMinutes")]
    [SettingDisplay(Name = "Bed Clear Minutes", Description = "Average turnaround time between queued jobs on the same printer.", InputType = SettingInputType.Number, MinValue = 0, MaxValue = 120, Order = 3)]
    public int BedClearMinutes { get; set; } = 10;

    [JsonPropertyName("defaultDeadlineHours")]
    [SettingDisplay(Name = "Default Deadline Offset (Hours)", Description = "Automatically assign a deadline this many hours from now when jobs are queued without a deadline.", InputType = SettingInputType.Number, MinValue = 0, MaxValue = 720, Order = 4)]
    public int? DefaultDeadlineHours { get; set; }

    [JsonPropertyName("requireDeadline")]
    [SettingDisplay(Name = "Require Deadline", Description = "Require a deadline when creating new queue items.", InputType = SettingInputType.Boolean, Order = 5)]
    public bool RequireDeadline { get; set; }

    [JsonPropertyName("minimumLeadHours")]
    [SettingDisplay(Name = "Minimum Lead Time (Hours)", Description = "Minimum number of hours from now that a deadline must be set in the future.", InputType = SettingInputType.Number, MinValue = 0, MaxValue = 720, Order = 6)]
    public int MinimumLeadHours { get; set; }

    public void Validate()
    {
        if (WorkdayStartHourUtc is < 0 or > 23)
        {
            throw new ValidationException("Workday start hour must be between 0 and 23.");
        }

        if (WorkdayEndHourUtc is < 0 or > 23)
        {
            throw new ValidationException("Workday end hour must be between 0 and 23.");
        }

        if (BedClearMinutes is < 0 or > 120)
        {
            throw new ValidationException("Bed clear minutes must be between 0 and 120.");
        }

        if (DefaultDeadlineHours is < 0 or > 720)
        {
            throw new ValidationException("Default deadline hours must be between 0 and 720.");
        }

        if (MinimumLeadHours is < 0 or > 720)
        {
            throw new ValidationException("Minimum lead hours must be between 0 and 720.");
        }
    }
}
