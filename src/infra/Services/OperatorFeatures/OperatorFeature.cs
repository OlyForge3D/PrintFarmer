namespace Farm.Infrastructure.Services.OperatorFeatures;

/// <summary>
/// Named operator features gated by <see cref="Farm.Infrastructure.Settings.OperatorFeatureSettings"/>.
/// Values map 1:1 to camelCase flag names on the wire and to properties on the settings class.
/// </summary>
public enum OperatorFeature
{
    /// <summary>Unified attention/exception feed and typed action endpoints (#707).</summary>
    Attention,

    /// <summary>APNs registration and delivery for operator alerts (#708).</summary>
    NativePush,

    /// <summary>Coverage/runout calculations exposed to clients (#709).</summary>
    FilamentCoverage,

    /// <summary>Per-tool requirements, swap validation, and guided swap flow (#710).</summary>
    GuidedSwap,

    /// <summary>Fallback groups, per-tool maintenance, and dispatch loadout (#711).</summary>
    MultiSlotFallback,

    /// <summary>Shift compiler and Tasks feed (#713).</summary>
    ShiftPlan,

    /// <summary>Printed-part stock, bins, harvest, and scan/inventory API (#714).</summary>
    PrintedPartsInventory,

    /// <summary>Idempotent write queue and offline replay (#715).</summary>
    OfflineWriteReplay,
}
