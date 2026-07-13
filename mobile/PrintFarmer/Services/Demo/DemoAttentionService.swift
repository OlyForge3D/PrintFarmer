import Foundation

// MARK: - Demo Attention Service
//
// Static, no-network stand-in for `AttentionService` used by the demo
// ServiceContainer. Returns an empty page so UI code that consumes the
// feed still exercises its empty-state path without any HTTP.

final class DemoAttentionService: AttentionServiceProtocol, @unchecked Sendable {

    private static let sampleFeed = AttentionFeed(
        items: [],
        nextCursor: nil,
        healthyPrinterCount: 0
    )

    func getFeed(cursor: String?, limit: Int?) async throws -> AttentionFeed {
        Self.sampleFeed
    }

    func snooze(
        itemId: String,
        snoozedUntilUtc: Date,
        attentionItemAnchorAtUtc: Date?
    ) async throws -> SnoozeAttentionResponse {
        SnoozeAttentionResponse(
            snoozedUntilUtc: snoozedUntilUtc,
            attentionItemAnchorAtUtc: attentionItemAnchorAtUtc
        )
    }

    func clearSnooze(itemId: String) async throws {}

    func executeAction(
        itemId: String,
        actionKind: AttentionActionKind
    ) async throws -> AttentionActionResult {
        AttentionActionResult(outcome: "Ok")
    }
}
