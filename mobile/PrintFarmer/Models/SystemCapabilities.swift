import Foundation

/// Operator feature gate flags exposed by `GET /api/system/capabilities`.
///
/// This mirrors the shared `IOperatorFeatureGate` contract defined by
/// issue #725. Every flag is optional so older servers (and servers
/// running before #725 lands) decode successfully; missing values fall
/// back to the documented defaults via ``resolved``.
///
/// The response payload uses camelCase, matching the rest of the
/// PrintFarmer API and the React client. iOS must not introduce a
/// parallel gate system — consult ``SystemCapabilitiesService`` (which
/// caches this response) or read the resolved snapshot through
/// ``AppRouter``.
struct SystemCapabilities: Codable, Sendable, Equatable {
    var attentionEnabled: Bool?
    var nativePushEnabled: Bool?
    var filamentCoverageEnabled: Bool?
    var guidedSwapEnabled: Bool?
    var multiSlotFallbackEnabled: Bool?
    var shiftPlanEnabled: Bool?
    var printedPartsInventoryEnabled: Bool?
    var offlineWriteReplayEnabled: Bool?

    /// Resolved, non-optional snapshot with the defaults documented in #725.
    ///
    /// Defaults:
    /// * `attentionEnabled` — `true`
    /// * `nativePushEnabled` — `false` (until provider/relay configured)
    /// * `filamentCoverageEnabled` — `true`
    /// * `guidedSwapEnabled` — `true`
    /// * `multiSlotFallbackEnabled` — `true`
    /// * `shiftPlanEnabled` — `true`
    /// * `printedPartsInventoryEnabled` — `true`
    /// * `offlineWriteReplayEnabled` — `true`
    var resolved: ResolvedSystemCapabilities {
        ResolvedSystemCapabilities(
            attentionEnabled: attentionEnabled ?? true,
            nativePushEnabled: nativePushEnabled ?? false,
            filamentCoverageEnabled: filamentCoverageEnabled ?? true,
            guidedSwapEnabled: guidedSwapEnabled ?? true,
            multiSlotFallbackEnabled: multiSlotFallbackEnabled ?? true,
            shiftPlanEnabled: shiftPlanEnabled ?? true,
            printedPartsInventoryEnabled: printedPartsInventoryEnabled ?? true,
            offlineWriteReplayEnabled: offlineWriteReplayEnabled ?? true
        )
    }
}

/// Non-optional snapshot of the resolved operator feature gates.
struct ResolvedSystemCapabilities: Sendable, Equatable {
    var attentionEnabled: Bool
    var nativePushEnabled: Bool
    var filamentCoverageEnabled: Bool
    var guidedSwapEnabled: Bool
    var multiSlotFallbackEnabled: Bool
    var shiftPlanEnabled: Bool
    var printedPartsInventoryEnabled: Bool
    var offlineWriteReplayEnabled: Bool

    /// The default snapshot used before `/api/system/capabilities` responds,
    /// after a 404, or when the endpoint is unreachable. Matches #725's
    /// documented defaults so the app boots into a fully-enabled state.
    static let defaults = ResolvedSystemCapabilities(
        attentionEnabled: true,
        nativePushEnabled: false,
        filamentCoverageEnabled: true,
        guidedSwapEnabled: true,
        multiSlotFallbackEnabled: true,
        shiftPlanEnabled: true,
        printedPartsInventoryEnabled: true,
        offlineWriteReplayEnabled: true
    )
}
