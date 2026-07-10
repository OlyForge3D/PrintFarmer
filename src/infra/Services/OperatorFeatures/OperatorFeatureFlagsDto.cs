using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Services.OperatorFeatures;

/// <summary>
/// Effective operator feature flags after resolving database values and environment overrides.
/// Property names must stay in sync with the camelCase JSON contract exposed by
/// <c>GET /api/system/capabilities</c>.
///
/// Clients (React and iOS) MUST tolerate missing flags in older/newer server responses and
/// fall back to the documented defaults from #725.
/// </summary>
public sealed record OperatorFeatureFlagsDto
{
    /// <summary>Unified attention/exception feed and typed action endpoints (#707). Default true.</summary>
    [JsonPropertyName("attentionEnabled")]
    public bool AttentionEnabled { get; init; } = true;

    /// <summary>APNs registration and delivery for operator alerts (#708). Default false.</summary>
    [JsonPropertyName("nativePushEnabled")]
    public bool NativePushEnabled { get; init; }

    /// <summary>Coverage/runout calculations exposed to clients (#709). Default true.</summary>
    [JsonPropertyName("filamentCoverageEnabled")]
    public bool FilamentCoverageEnabled { get; init; } = true;

    /// <summary>Per-tool requirements, swap validation, and guided swap flow (#710). Default true.</summary>
    [JsonPropertyName("guidedSwapEnabled")]
    public bool GuidedSwapEnabled { get; init; } = true;

    /// <summary>Fallback groups, per-tool maintenance, and dispatch loadout (#711). Default true.</summary>
    [JsonPropertyName("multiSlotFallbackEnabled")]
    public bool MultiSlotFallbackEnabled { get; init; } = true;

    /// <summary>Shift compiler and Tasks feed (#713). Default true.</summary>
    [JsonPropertyName("shiftPlanEnabled")]
    public bool ShiftPlanEnabled { get; init; } = true;

    /// <summary>Printed-part stock, bins, harvest, and scan/inventory API (#714). Default true.</summary>
    [JsonPropertyName("printedPartsInventoryEnabled")]
    public bool PrintedPartsInventoryEnabled { get; init; } = true;

    /// <summary>Idempotent write queue and offline replay (#715). Default true.</summary>
    [JsonPropertyName("offlineWriteReplayEnabled")]
    public bool OfflineWriteReplayEnabled { get; init; } = true;
}
