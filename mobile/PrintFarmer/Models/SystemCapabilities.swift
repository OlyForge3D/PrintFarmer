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
///
/// The server nests the operator flags under an ``operatorFeatures`` envelope
/// (the shared `PlatformCapabilitiesDto.OperatorFeatures` object exposed by
/// `GET /api/system/capabilities`). Decoding the flags at the top level — as an
/// earlier revision did — silently loses every flag and always resolves to the
/// documented defaults, which defeats server-side kill switches such as
/// `offlineWriteReplayEnabled`. They must be read from the nested object, exactly
/// as the React client reads `operatorFeatures`.
struct SystemCapabilities: Codable, Sendable, Equatable {
    /// The operator feature gate flags, nested under `operatorFeatures` in the
    /// capabilities payload. `nil` when the server predates #725 or the object is
    /// omitted, in which case ``resolved`` falls back to the documented defaults.
    var operatorFeatures: OperatorFeatureFlags?

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
        let flags = operatorFeatures
        return ResolvedSystemCapabilities(
            attentionEnabled: flags?.attentionEnabled ?? true,
            nativePushEnabled: flags?.nativePushEnabled ?? false,
            filamentCoverageEnabled: flags?.filamentCoverageEnabled ?? true,
            guidedSwapEnabled: flags?.guidedSwapEnabled ?? true,
            multiSlotFallbackEnabled: flags?.multiSlotFallbackEnabled ?? true,
            shiftPlanEnabled: flags?.shiftPlanEnabled ?? true,
            printedPartsInventoryEnabled: flags?.printedPartsInventoryEnabled ?? true,
            offlineWriteReplayEnabled: flags?.offlineWriteReplayEnabled ?? true
        )
    }
}

/// Operator feature gate flags carried under the `operatorFeatures` envelope of
/// `GET /api/system/capabilities`. Mirrors the shared `OperatorFeatureFlagsDto`
/// (#725). Every flag is optional so older/newer servers that omit a subset still
/// decode; missing values fall back to the documented defaults via
/// ``SystemCapabilities/resolved``.
struct OperatorFeatureFlags: Codable, Sendable, Equatable {
    var attentionEnabled: Bool?
    var nativePushEnabled: Bool?
    var filamentCoverageEnabled: Bool?
    var guidedSwapEnabled: Bool?
    var multiSlotFallbackEnabled: Bool?
    var shiftPlanEnabled: Bool?
    var printedPartsInventoryEnabled: Bool?
    var offlineWriteReplayEnabled: Bool?
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
