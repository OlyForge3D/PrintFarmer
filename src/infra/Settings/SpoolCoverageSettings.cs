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
///
/// <para>
/// <b>Rollback note (#725 integration):</b> the runtime enable/disable knob
/// for the whole coverage feature belongs to <c>IOperatorFeatureGate</c> and
/// the <c>filamentCoverageEnabled</c> flag defined in #725, not to a private
/// per-feature boolean. Until #725 lands, <see cref="Enabled"/> here serves as
/// a placeholder that later readers must swap for the shared gate. After
/// rebasing #725, delete <see cref="Enabled"/> and consult
/// <c>IOperatorFeatureGate</c> in <c>FilamentCoverageService</c> and
/// <c>FilamentCoverageController</c> instead.
/// </para>
/// </summary>
[AppSetting(SectionName)]
[SettingGroup("Operations", DisplayName = "Operations", Description = "Operational settings and cost tracking", Icon = "pf-icon-operations", Order = 3)]
[SettingDisplay(Name = "Spool Coverage", Description = "Prediction thresholds and fleet fetch behavior for filament coverage.", Icon = "pf-icon-spool", Group = "Operations", Order = 6)]
public class SpoolCoverageSettings : IAppSetting, IValidatableSetting
{
    public const string SectionName = "SpoolCoverage";

    public static string SectionKey => SectionName;

    /// <summary>
    /// Master switch. When false, endpoints still respond but never emit runout
    /// warnings to the attention feed.
    ///
    /// <para>
    /// Rebase-note (#725): after <c>IOperatorFeatureGate.FilamentCoverageEnabled</c>
    /// lands, this flag becomes redundant. Remove and route callers through the
    /// gate so the endpoint returns 404 + <c>featureDisabled</c> ProblemDetails
    /// when disabled, per #725's contract.
    /// </para>
    /// </summary>
    [JsonPropertyName("enabled")]
    [SettingDisplay(Name = "Enable Coverage Prediction", Description = "When disabled, coverage endpoints still respond but no attention runout warnings are emitted. (Superseded by IOperatorFeatureGate.filamentCoverageEnabled once #725 lands.)", InputType = SettingInputType.Boolean, Order = 1)]
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Lead time in minutes before a predicted active-job runout at which the
    /// attention feed should surface a warning. Acceptance addendum on #709
    /// pins the default to 30 minutes and the valid range to 5–1440 minutes;
    /// anything shorter is too tight to be useful for a swap-response flow.
    /// </summary>
    [JsonPropertyName("runoutWarningLeadMinutes")]
    [SettingDisplay(Name = "Runout Warning Lead Time (minutes)", Description = "Emit an attention warning when a predicted active-job runout is within this many minutes of now (5–1440).", InputType = SettingInputType.Number, MinValue = 5, MaxValue = 1440, Order = 2)]
    public int RunoutWarningLeadMinutes { get; set; } = 30;

    /// <summary>
    /// Whether to emit "insufficient for assigned queue" warnings (i.e. hard
    /// shortages where the current spool cannot cover the queue even without
    /// an ETA). Default <c>true</c>; setting this to <c>false</c> is a way for
    /// operators to opt out of noisy queue projections when the queue is
    /// heavily churned but leaves active-job ETA-driven warnings in place.
    /// </summary>
    [JsonPropertyName("queuedShortageWarningsEnabled")]
    [SettingDisplay(Name = "Queued Shortage Warnings", Description = "Warn when spool remaining cannot cover the assigned queue, even when no active-job runout ETA exists.", InputType = SettingInputType.Boolean, Order = 3)]
    public bool QueuedShortageWarningsEnabled { get; set; } = true;

    /// <summary>
    /// Safety-margin grams held back from the spool remaining weight when
    /// evaluating coverage. Any positive value protects against under-reported
    /// spool weight in Spoolman.
    /// </summary>
    [JsonPropertyName("reserveGrams")]
    [SettingDisplay(Name = "Reserve Grams", Description = "Grams held back from spool remaining weight as a safety margin.", InputType = SettingInputType.Number, MinValue = 0, MaxValue = 1000, Order = 4)]
    public double ReserveGrams { get; set; }

    /// <summary>
    /// Maximum number of printers evaluated concurrently by the fleet endpoint.
    /// Bounds outbound Spoolman / backend load.
    /// </summary>
    [JsonPropertyName("fleetMaxParallelism")]
    [SettingDisplay(Name = "Fleet Max Parallelism", Description = "How many printers the fleet endpoint may probe concurrently.", InputType = SettingInputType.Number, MinValue = 1, MaxValue = 64, Order = 5)]
    public int FleetMaxParallelism { get; set; } = 8;

    /// <summary>
    /// Per-printer timeout in milliseconds for the live progress backend call.
    /// The endpoint degrades gracefully to "unknown progress" when exceeded.
    /// </summary>
    [JsonPropertyName("liveProgressTimeoutMs")]
    [SettingDisplay(Name = "Live Progress Timeout (ms)", Description = "How long the coverage endpoint waits for a live progress reading from each printer.", InputType = SettingInputType.Number, MinValue = 100, MaxValue = 30000, Order = 6)]
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

        if (FleetMaxParallelism is < 1 or > 64)
        {
            throw new ValidationException("Fleet max parallelism must be between 1 and 64.");
        }

        if (LiveProgressTimeoutMs is < 100 or > 30000)
        {
            throw new ValidationException("Live progress timeout must be between 100 and 30000 ms.");
        }
    }
}
