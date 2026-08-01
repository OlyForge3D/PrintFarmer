using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Settings;

/// <summary>
/// Runtime-configurable settings for the shift-plan compiler (issue #713).
/// Persisted through the standard AppSettings store; hot-reloaded via
/// <see cref="Microsoft.Extensions.Options.IOptionsMonitor{TOptions}"/>.
/// </summary>
/// <remarks>
/// <para>
/// Filament runout lead time is intentionally NOT duplicated here — the
/// compiler reads it from <see cref="SpoolCoverageSettings.RunoutWarningLeadMinutes"/>
/// so operators configure it in one place. This settings surface only owns
/// lead times and thresholds that are specific to the shift-plan compiler.
/// </para>
/// </remarks>
[AppSetting(SectionName)]
[SettingGroup("Operations", DisplayName = "Operations", Description = "Operational settings and cost tracking", Icon = "pf-icon-operations", Order = 3)]
[SettingDisplay(Name = "Shift Plan", Description = "Time anchors and thresholds used by the shift-plan compiler.", Icon = "pf-icon-shift-plan", Group = "Operations", Order = 7)]
public class ShiftPlanSettings : IAppSetting, IValidatableSetting
{
    /// <summary>Settings section name.</summary>
    public const string SectionName = "ShiftPlan";

    /// <inheritdoc />
    public static string SectionKey => SectionName;

    /// <summary>
    /// Compiler recompute interval in seconds. Lower values give crisper task
    /// visibility but cost query load; 60s is a balanced default.
    /// </summary>
    [JsonPropertyName("compileIntervalSeconds")]
    [SettingDisplay(Name = "Compile Interval", Unit = "seconds", Description = "How often the shift-plan compiler recomputes materialized tasks (15–3600).", InputType = SettingInputType.Number, MinValue = 15, MaxValue = 3600, Order = 1)]
    public int CompileIntervalSeconds { get; set; } = 60;

    /// <summary>
    /// Lead-time buffer in minutes applied to the <em>end</em> of an idle window
    /// for maintenance tasks. The effective window presented to the operator is
    /// <c>[windowStart, windowEnd − maintenanceLeadMinutes)</c>, ensuring they
    /// are not scheduled to work in the last few minutes before the next print
    /// begins. A value of 0 disables the buffer.
    /// </summary>
    [JsonPropertyName("maintenanceLeadMinutes")]
    [SettingDisplay(Name = "Maintenance Lead Time", Unit = "minutes", Description = "How far in advance of an idle window a maintenance task appears (0–1440).", InputType = SettingInputType.Number, MinValue = 0, MaxValue = 1440, Order = 2)]
    public int MaintenanceLeadMinutes { get; set; } = 30;

    /// <summary>
    /// Minimum idle window duration (minutes) the compiler considers "usable"
    /// for maintenance surfacing. Shorter idle windows are ignored so operators
    /// are not pushed into low-value time slices.
    /// </summary>
    [JsonPropertyName("minIdleWindowMinutes")]
    [SettingDisplay(Name = "Minimum Idle Window", Unit = "minutes", Description = "Idle windows shorter than this are ignored (5–1440).", InputType = SettingInputType.Number, MinValue = 5, MaxValue = 1440, Order = 3)]
    public int MinIdleWindowMinutes { get; set; } = 20;

    /// <summary>
    /// Spool reorder threshold in grams used by source-qualified burn-rate projections.
    /// </summary>
    [JsonPropertyName("spoolReorderThresholdGrams")]
    [SettingDisplay(Name = "Spool Reorder Threshold", Unit = "g", Description = "Project when remaining spool weight will cross this value (0–100000).", InputType = SettingInputType.Number, MinValue = 0, MaxValue = 100000, Order = 4)]
    public double SpoolReorderThresholdGrams { get; set; } = 250;

    /// <summary>Completed-job lookback used for authoritative burn-rate samples.</summary>
    [JsonPropertyName("spoolBurnRateLookbackDays")]
    [SettingDisplay(Name = "Spool Burn-Rate Lookback", Unit = "days", Description = "Completed-job history window used for burn-rate projection (1–3650).", InputType = SettingInputType.Number, MinValue = 1, MaxValue = 3650, Order = 5)]
    public int SpoolBurnRateLookbackDays { get; set; } = 30;

    /// <summary>Minimum authoritative usage rows required for a ready projection.</summary>
    [JsonPropertyName("spoolBurnRateMinimumSamples")]
    [SettingDisplay(Name = "Spool Burn-Rate Minimum Samples", Description = "Minimum completed authoritative usage samples required for projection (1–1000).", InputType = SettingInputType.Number, MinValue = 1, MaxValue = 1000, Order = 6)]
    public int SpoolBurnRateMinimumSamples { get; set; } = 3;

    /// <summary>Lead time applied before a projected spool reorder-threshold crossing.</summary>
    [JsonPropertyName("spoolRestockLeadMinutes")]
    [SettingDisplay(Name = "Spool Restock Lead Time", Unit = "minutes", Description = "How far before the projected reorder-threshold crossing a spool-restock task appears (0–1440).", InputType = SettingInputType.Number, MinValue = 0, MaxValue = 1440, Order = 7)]
    public int SpoolRestockLeadMinutes { get; set; }

    /// <summary>
    /// Lead time for harvest tasks. Applied when a harvest is anticipated from
    /// an in-progress job's ETA rather than an already-completed plate.
    /// </summary>
    [JsonPropertyName("harvestLeadMinutes")]
    [SettingDisplay(Name = "Harvest Lead Time", Unit = "minutes", Description = "How far ahead of a projected harvest the task appears (0–1440).", InputType = SettingInputType.Number, MinValue = 0, MaxValue = 1440, Order = 8)]
    public int HarvestLeadMinutes { get; set; }

    /// <inheritdoc />
    public void Validate()
    {
        if (CompileIntervalSeconds is < 15 or > 3600)
        {
            throw new ValidationException("Compile interval must be between 15 and 3600 seconds.");
        }

        if (MaintenanceLeadMinutes is < 0 or > 1440)
        {
            throw new ValidationException("Maintenance lead minutes must be between 0 and 1440.");
        }

        if (MinIdleWindowMinutes is < 5 or > 1440)
        {
            throw new ValidationException("Minimum idle window must be between 5 and 1440 minutes.");
        }

        if (SpoolReorderThresholdGrams is < 0 or > 100000)
        {
            throw new ValidationException("Spool reorder threshold must be between 0 and 100000 grams.");
        }

        if (SpoolBurnRateLookbackDays is < 1 or > 3650)
        {
            throw new ValidationException("Spool burn-rate lookback must be between 1 and 3650 days.");
        }

        if (SpoolBurnRateMinimumSamples is < 1 or > 1000)
        {
            throw new ValidationException("Spool burn-rate minimum samples must be between 1 and 1000.");
        }

        if (SpoolRestockLeadMinutes is < 0 or > 1440)
        {
            throw new ValidationException("Spool restock lead minutes must be between 0 and 1440.");
        }

        if (HarvestLeadMinutes is < 0 or > 1440)
        {
            throw new ValidationException("Harvest lead minutes must be between 0 and 1440.");
        }
    }
}
