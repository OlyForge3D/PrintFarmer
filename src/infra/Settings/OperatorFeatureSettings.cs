using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Settings;

/// <summary>
/// Named, shared operator-feature gate contract for the operator-first mobile redesign (epic #705).
///
/// This is the ONLY sanctioned mechanism for enabling/disabling operator features across the
/// backend, React, and iOS clients. Feature implementations (#707–#715) must consume the
/// resolved <see cref="Farm.Infrastructure.Services.OperatorFeatures.IOperatorFeatureGate"/>
/// rather than introducing per-feature booleans.
///
/// Section key: <c>OperatorFeatures</c>. Effective flags are exposed by
/// <c>GET /api/system/capabilities</c> in camelCase.
///
/// Emergency rollback: an ASP.NET configuration/environment value named
/// <c>OperatorFeatures__&lt;FlagName&gt;=false</c> is a hard-disable override that wins over the
/// database value. See <c>docs/OPERATOR_FEATURE_GATES.md</c>.
/// </summary>
[AppSetting(SectionName)]
[SettingGroup("Operations", DisplayName = "Operations", Description = "Operational settings and cost tracking", Icon = "pf-icon-operations", Order = 3)]
[SettingDisplay(Name = "Operator Features", Description = "Enable or disable operator-first mobile features. Standard ASP.NET configuration values named OperatorFeatures__<FlagName>=false override the database value as an emergency hard-disable.", Icon = "pf-icon-toggle", Group = "Operations", Order = 20)]
public class OperatorFeatureSettings : IAppSetting
{
    public const string SectionName = "OperatorFeatures";

    public static string SectionKey => SectionName;

    [SettingDisplay(
        Name = "Attention feed",
        Description = "Unified attention/exception feed and typed action endpoints (#707).",
        InputType = SettingInputType.Boolean)]
    [JsonPropertyName("attentionEnabled")]
    public bool AttentionEnabled { get; set; } = true;

    [SettingDisplay(
        Name = "Native push notifications",
        Description = "APNs registration and delivery for operator alerts (#708). Disabled by default until a provider/relay is configured.",
        InputType = SettingInputType.Boolean)]
    [JsonPropertyName("nativePushEnabled")]
    public bool NativePushEnabled { get; set; }

    [SettingDisplay(
        Name = "Filament coverage",
        Description = "Coverage/runout calculations exposed to clients (#709).",
        InputType = SettingInputType.Boolean)]
    [JsonPropertyName("filamentCoverageEnabled")]
    public bool FilamentCoverageEnabled { get; set; } = true;

    [SettingDisplay(
        Name = "Guided filament swap",
        Description = "Per-tool requirements, swap validation, and guided swap flow (#710).",
        InputType = SettingInputType.Boolean)]
    [JsonPropertyName("guidedSwapEnabled")]
    public bool GuidedSwapEnabled { get; set; } = true;

    [SettingDisplay(
        Name = "Multi-slot fallback",
        Description = "Fallback groups, per-tool maintenance, and dispatch loadout (#711).",
        InputType = SettingInputType.Boolean)]
    [JsonPropertyName("multiSlotFallbackEnabled")]
    public bool MultiSlotFallbackEnabled { get; set; } = true;

    [SettingDisplay(
        Name = "Shift plan",
        Description = "Shift compiler and Tasks UI feed (#713).",
        InputType = SettingInputType.Boolean)]
    [JsonPropertyName("shiftPlanEnabled")]
    public bool ShiftPlanEnabled { get; set; } = true;

    [SettingDisplay(
        Name = "Printed parts inventory",
        Description = "Printed-part stock, bins, harvest, and scan/inventory API (#714).",
        InputType = SettingInputType.Boolean)]
    [JsonPropertyName("printedPartsInventoryEnabled")]
    public bool PrintedPartsInventoryEnabled { get; set; } = true;

    [SettingDisplay(
        Name = "Offline write replay",
        Description = "Idempotent write queue and offline replay (#715). Disabling switches clients to direct-online mutations while preserving queued entries.",
        InputType = SettingInputType.Boolean)]
    [JsonPropertyName("offlineWriteReplayEnabled")]
    public bool OfflineWriteReplayEnabled { get; set; } = true;
}
