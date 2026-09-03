import Foundation

/// Observed farm counts returned by `GET /api/system/capabilities`.
///
/// Each count is optional so older servers and degraded or partially populated
/// responses remain decodable. ``unknown`` represents a response that supplied
/// no usable farm-shape counts.
struct FarmShape: Codable, Sendable, Equatable {
    var accountCount: Int?
    var locationCount: Int?
    var printerCount: Int?

    static let unknown = FarmShape(
        accountCount: nil,
        locationCount: nil,
        printerCount: nil
    )
}

/// System capability data exposed by `GET /api/system/capabilities`.
///
/// Canonical servers return these flags under `operatorFeatures`. Legacy
/// servers exposed the same fields at the response root. Every flag remains
/// optional so either payload shape decodes successfully; missing values fall
/// back to the documented defaults via ``resolved``. `farmShape` is also
/// optional because older servers, anonymous requests, and degraded count
/// queries omit it.
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
    /// Server-observed farm counts, or `nil` when the shape is unavailable.
    var farmShape: FarmShape?

    private enum CodingKeys: String, CodingKey {
        case operatorFeatures
        case farmShape
        case attentionEnabled
        case nativePushEnabled
        case filamentCoverageEnabled
        case guidedSwapEnabled
        case multiSlotFallbackEnabled
        case shiftPlanEnabled
        case printedPartsInventoryEnabled
        case offlineWriteReplayEnabled
    }

    private struct OperatorFeatures: Codable {
        var attentionEnabled: Bool?
        var nativePushEnabled: Bool?
        var filamentCoverageEnabled: Bool?
        var guidedSwapEnabled: Bool?
        var multiSlotFallbackEnabled: Bool?
        var shiftPlanEnabled: Bool?
        var printedPartsInventoryEnabled: Bool?
        var offlineWriteReplayEnabled: Bool?
    }

    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        let nested = try container.decodeIfPresent(OperatorFeatures.self, forKey: .operatorFeatures)
        let legacy = OperatorFeatures(
            attentionEnabled: try container.decodeIfPresent(Bool.self, forKey: .attentionEnabled),
            nativePushEnabled: try container.decodeIfPresent(Bool.self, forKey: .nativePushEnabled),
            filamentCoverageEnabled: try container.decodeIfPresent(
                Bool.self,
                forKey: .filamentCoverageEnabled
            ),
            guidedSwapEnabled: try container.decodeIfPresent(Bool.self, forKey: .guidedSwapEnabled),
            multiSlotFallbackEnabled: try container.decodeIfPresent(
                Bool.self,
                forKey: .multiSlotFallbackEnabled
            ),
            shiftPlanEnabled: try container.decodeIfPresent(Bool.self, forKey: .shiftPlanEnabled),
            printedPartsInventoryEnabled: try container.decodeIfPresent(
                Bool.self,
                forKey: .printedPartsInventoryEnabled
            ),
            offlineWriteReplayEnabled: try container.decodeIfPresent(
                Bool.self,
                forKey: .offlineWriteReplayEnabled
            )
        )

        attentionEnabled = nested?.attentionEnabled ?? legacy.attentionEnabled
        nativePushEnabled = nested?.nativePushEnabled ?? legacy.nativePushEnabled
        filamentCoverageEnabled =
            nested?.filamentCoverageEnabled ?? legacy.filamentCoverageEnabled
        guidedSwapEnabled = nested?.guidedSwapEnabled ?? legacy.guidedSwapEnabled
        multiSlotFallbackEnabled =
            nested?.multiSlotFallbackEnabled ?? legacy.multiSlotFallbackEnabled
        shiftPlanEnabled = nested?.shiftPlanEnabled ?? legacy.shiftPlanEnabled
        printedPartsInventoryEnabled =
            nested?.printedPartsInventoryEnabled ?? legacy.printedPartsInventoryEnabled
        offlineWriteReplayEnabled =
            nested?.offlineWriteReplayEnabled ?? legacy.offlineWriteReplayEnabled
        farmShape = try container.decodeIfPresent(FarmShape.self, forKey: .farmShape)
    }

    func encode(to encoder: Encoder) throws {
        var container = encoder.container(keyedBy: CodingKeys.self)
        try container.encode(
            OperatorFeatures(
                attentionEnabled: attentionEnabled,
                nativePushEnabled: nativePushEnabled,
                filamentCoverageEnabled: filamentCoverageEnabled,
                guidedSwapEnabled: guidedSwapEnabled,
                multiSlotFallbackEnabled: multiSlotFallbackEnabled,
                shiftPlanEnabled: shiftPlanEnabled,
                printedPartsInventoryEnabled: printedPartsInventoryEnabled,
                offlineWriteReplayEnabled: offlineWriteReplayEnabled
            ),
            forKey: .operatorFeatures
        )
        try container.encodeIfPresent(farmShape, forKey: .farmShape)
    }

    /// Resolved, non-optional snapshot with the defaults documented in #725
    /// and an unknown farm shape when the server did not provide counts.
    ///
    /// Defaults:
    /// * `attentionEnabled` — `true`
    /// * `nativePushEnabled` — `false` (until provider/relay configured)
    /// * `filamentCoverageEnabled` — `true`
    /// * `guidedSwapEnabled` — `true`
    /// * `multiSlotFallbackEnabled` — `true`
    /// * `shiftPlanEnabled` — `true`
    /// * `printedPartsInventoryEnabled` — `false` (until part SKUs/output mappings configured)
    /// * `offlineWriteReplayEnabled` — `true`
    /// * `farmShape` — ``FarmShape/unknown``
    var resolved: ResolvedSystemCapabilities {
        ResolvedSystemCapabilities(
            attentionEnabled: attentionEnabled ?? true,
            nativePushEnabled: nativePushEnabled ?? false,
            filamentCoverageEnabled: filamentCoverageEnabled ?? true,
            guidedSwapEnabled: guidedSwapEnabled ?? true,
            multiSlotFallbackEnabled: multiSlotFallbackEnabled ?? true,
            shiftPlanEnabled: shiftPlanEnabled ?? true,
            printedPartsInventoryEnabled: printedPartsInventoryEnabled ?? false,
            offlineWriteReplayEnabled: offlineWriteReplayEnabled ?? true,
            farmShape: farmShape ?? .unknown
        )
    }
}

/// Non-optional snapshot of the resolved system capabilities.
struct ResolvedSystemCapabilities: Sendable, Equatable {
    var attentionEnabled: Bool
    var nativePushEnabled: Bool
    var filamentCoverageEnabled: Bool
    var guidedSwapEnabled: Bool
    var multiSlotFallbackEnabled: Bool
    var shiftPlanEnabled: Bool
    var printedPartsInventoryEnabled: Bool
    var offlineWriteReplayEnabled: Bool
    var farmShape: FarmShape = .unknown

    /// The default snapshot used before `/api/system/capabilities` responds,
    /// after a 404, or when the endpoint is unreachable. Mirrors the server-side
    /// defaults so the app never advertises an action the backend will reject —
    /// notably harvest, which requires configured part SKUs and output mappings.
    /// Farm shape remains unknown until the server provides observed counts.
    static let defaults = ResolvedSystemCapabilities(
        attentionEnabled: true,
        nativePushEnabled: false,
        filamentCoverageEnabled: true,
        guidedSwapEnabled: true,
        multiSlotFallbackEnabled: true,
        shiftPlanEnabled: true,
        printedPartsInventoryEnabled: false,
        offlineWriteReplayEnabled: true,
        farmShape: .unknown
    )
}
