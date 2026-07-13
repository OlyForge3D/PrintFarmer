import Foundation

// MARK: - Attention Service Protocol
//
// Wraps the unified attention feed API added in PR #731 (issue #707).
// See `AttentionModels.swift` for wire types.

protocol AttentionServiceProtocol: Sendable {
    /// Fetches one page of the attention feed.
    ///
    /// Callers pass `nil` for the first page and echo `feed.nextCursor` on
    /// subsequent pages. `limit` is validated server-side (1–250, default
    /// 100); out-of-range values return `NetworkError.clientError(400, _)`.
    ///
    /// A gated 404 with `code == "featureDisabled"` (#725/#728) surfaces as
    /// `NetworkError.featureDisabled` so the caller can pick its existing
    /// safe fallback UI without parsing localized error text.
    func getFeed(cursor: String?, limit: Int?) async throws -> AttentionFeed

    /// Snoozes an attention item for the current user until the given UTC
    /// instant. `anchorAtUtc` should be the item's `occurredAt` at snooze
    /// time to enable fresh-occurrence bypass.
    func snooze(
        itemId: String,
        snoozedUntilUtc: Date,
        attentionItemAnchorAtUtc: Date?
    ) async throws -> SnoozeAttentionResponse

    /// Clears a previously-created snooze for the current user.
    func clearSnooze(itemId: String) async throws

    /// Dispatches a typed action against an attention item. The server
    /// routes the request to the appropriate downstream endpoint — clients
    /// must not synthesise action URLs directly.
    func executeAction(
        itemId: String,
        actionKind: AttentionActionKind
    ) async throws -> AttentionActionResult
}

extension AttentionServiceProtocol {
    /// Convenience for the first page with the server default limit.
    func getFeed() async throws -> AttentionFeed {
        try await getFeed(cursor: nil, limit: nil)
    }
}
