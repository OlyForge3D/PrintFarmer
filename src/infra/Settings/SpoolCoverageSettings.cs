using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Settings;

/// <summary>
/// Runtime-configurable settings for spool coverage and runout prediction
/// (issue #709). Persisted through the standard AppSettings store; hot-reloaded
/// via <see cref="Microsoft.Extensions.Options.IOptionsMonitor{TOptions}"/>.
///
/// <para>
/// Property names, defaults, and ranges are pinned by Dallas's F4 acceptance
/// addendum on #709. Do not rename JSON keys or widen ranges without a
/// coordinated frontend/mobile update — the settings surface is metadata-driven
/// via <see cref="SettingDisplayAttribute"/>.
/// </para>
/// </summary>
[AppSetting(SectionName)]
[SettingGroup("Operations", DisplayName = "Operations", Description = "Operational settings and cost tracking", Icon = "pf-icon-operations", Order = 3)]
[SettingDisplay(Name = "Spool Coverage", Description = "Prediction thresholds and live progress behavior for filament coverage.", Icon = "pf-icon-spool", Group = "Operations", Order = 6)]
public class SpoolCoverageSettings : IAppSetting, IValidatableSetting
{
    public const string SectionName = "SpoolCoverage";

    public static string SectionKey => SectionName;

    /// <summary>
    /// Lead time in minutes before a predicted active-job runout at which the
    /// attention feed should surface a warning. Acceptance addendum on #709
    /// pins the default to 30 minutes and the valid range to 5–1440 minutes;
    /// anything shorter is too tight to be useful for a swap-response flow.
    /// </summary>
    [JsonPropertyName("runoutWarningLeadMinutes")]
    [SettingDisplay(Name = "Runout warning lead time", Unit = "minutes", Description = "Emit an attention warning when a predicted active-job runout is within this many minutes of now (5–1440).", InputType = SettingInputType.Number, MinValue = 5, MaxValue = 1440, Order = 2)]
    public int RunoutWarningLeadMinutes { get; set; } = 30;

    /// <summary>
    /// Whether to emit "insufficient for assigned queue" warnings (i.e. hard
    /// shortages where the current spool cannot cover the queue even without
    /// an ETA). Default <c>true</c>; setting this to <c>false</c> is a way for
    /// operators to opt out of noisy queue projections when the queue is
    /// heavily churned but leaves active-job ETA-driven warnings in place.
    /// </summary>
    [JsonPropertyName("queuedShortageWarningsEnabled")]
    [SettingDisplay(Name = "Queued shortage warnings", Description = "Warn when spool remaining cannot cover the assigned queue, even when no active-job runout ETA exists.", InputType = SettingInputType.Boolean, Order = 3)]
    public bool QueuedShortageWarningsEnabled { get; set; } = true;

    /// <summary>
    /// Safety-margin grams held back from the spool remaining weight when
    /// evaluating coverage. Any positive value protects against under-reported
    /// spool weight in Spoolman.
    /// </summary>
    [JsonPropertyName("reserveGrams")]
    [SettingDisplay(Name = "Reserve grams", Description = "Grams held back from spool remaining weight as a safety margin.", InputType = SettingInputType.Number, MinValue = 0, MaxValue = 1000, Order = 4)]
    public double ReserveGrams { get; set; }

    /// <summary>
    /// Per-printer timeout in milliseconds for the live progress backend call.
    /// The endpoint degrades gracefully to "unknown progress" when exceeded.
    /// </summary>
    [JsonPropertyName("liveProgressTimeoutMs")]
    [SettingDisplay(Name = "Live progress timeout", Unit = "ms", Description = "How long the coverage endpoint waits for a live progress reading from each printer.", InputType = SettingInputType.Number, MinValue = 100, MaxValue = 30000, Order = 5)]
    public int LiveProgressTimeoutMs { get; set; } = 2000;

    public void Validate()
    {
        if (RunoutWarningLeadMinutes is < 5 or > 1440)
        {
            throw new ValidationException("Runout warning lead minutes must be between 5 and 1440.");
        }

        if (ReserveGrams is < 0 or > 1000)
        {
            throw new ValidationException("Reserve grams must be between 0 and 1000.");
        }

        if (LiveProgressTimeoutMs is < 100 or > 30000)
        {
            throw new ValidationException("Live progress timeout must be between 100 and 30000 ms.");
        }
    }
}
