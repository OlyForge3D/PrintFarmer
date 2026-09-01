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

    /// <summary>
    /// Per-source timeout in milliseconds for reading spool inventory from a spool
    /// source (a printer's native Spoolman proxy, or the central Spoolman server).
    ///
    /// <para>
    /// Coverage is a read-only projection, so it must not inherit the backend's
    /// <see cref="BackendTimeoutSettings.PrintControlTimeout"/> (60s by default). A
    /// printer that is powered down but still holds its address black-holes packets
    /// rather than refusing them, so without this bound a single dark printer stalls
    /// the whole fleet projection — and with it <c>/api/attention</c>, which composes
    /// the same pipeline. A source that exceeds this budget degrades to
    /// <c>spool-source-unavailable</c> (status <c>Unknown</c>), never to a fabricated
    /// coverage verdict.
    /// </para>
    ///
    /// <para>
    /// This is deliberately NOT tight. Degrading a healthy-but-slow source is not free:
    /// <c>FilamentCoverageService</c> drops <c>Unknown</c> slots from runout warnings, so
    /// an over-eager timeout silently suppresses a genuine runout alert, and
    /// <c>SpoolRestockShiftPlanTaskSource</c> rejects any snapshot carrying an error
    /// reason, which drops every restock task from that compile. Real sources on a
    /// reference farm answered in 0.16–2.2s, so this leaves roughly 2x headroom over the
    /// slowest healthy reading. The endpoint's own bound is
    /// <see cref="FleetResolveTimeoutMs"/>, not this value.
    /// </para>
    /// </summary>
    [JsonPropertyName("spoolSourceTimeoutMs")]
    [SettingDisplay(Name = "Spool source timeout", Unit = "ms", Description = "How long the coverage endpoint waits for spool inventory from each spool source before treating that source as unavailable.", InputType = SettingInputType.Number, MinValue = 250, MaxValue = 30000, Order = 6)]
    public int SpoolSourceTimeoutMs { get; set; } = 5000;

    /// <summary>
    /// Overall timeout in milliseconds for resolving spool inventory across the whole
    /// fleet.
    ///
    /// <para>
    /// <see cref="SpoolSourceTimeoutMs"/> bounds a single source; it does not bound the
    /// endpoint. Sources are resolved through a bounded fan-out, so N dark printers
    /// serialise into <c>ceil(N / concurrency)</c> timeout waves and total latency still
    /// grows with fleet size. This deadline makes the endpoint bound hold by
    /// construction at any fleet size: when it expires, every source still in flight
    /// degrades to <c>spool-source-unavailable</c> and the projection returns the
    /// coverage it already has, rather than failing the whole request.
    /// </para>
    ///
    /// <para>
    /// Keep this comfortably below the mobile client's per-probe readiness budget (10s),
    /// or the app reports Attention and Filament Coverage as unavailable at startup.
    /// </para>
    /// </summary>
    [JsonPropertyName("fleetResolveTimeoutMs")]
    [SettingDisplay(Name = "Fleet spool resolve timeout", Unit = "ms", Description = "Overall budget for resolving spool inventory across the whole fleet. Sources still in flight when it expires are reported as unavailable.", InputType = SettingInputType.Number, MinValue = 1000, MaxValue = 60000, Order = 7)]
    public int FleetResolveTimeoutMs { get; set; } = 8000;

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

        if (SpoolSourceTimeoutMs is < 250 or > 30000)
        {
            throw new ValidationException("Spool source timeout must be between 250 and 30000 ms.");
        }

        if (FleetResolveTimeoutMs is < 1000 or > 60000)
        {
            throw new ValidationException("Fleet spool resolve timeout must be between 1000 and 60000 ms.");
        }
    }
}
