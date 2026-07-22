import Foundation

private enum DemoAttentionServiceError: LocalizedError {
    case forcedActionFailure(String)

    var errorDescription: String? {
        switch self {
        case .forcedActionFailure(let message):
            message
        }
    }
}

private actor DemoAttentionActionGate {
    private var isOpen = false
    private var waiters: [CheckedContinuation<Void, Never>] = []

    func wait() async {
        guard !isOpen else { return }
        await withCheckedContinuation { continuation in
            waiters.append(continuation)
        }
    }

    func open() {
        guard !isOpen else { return }
        isOpen = true
        let pending = waiters
        waiters.removeAll()
        pending.forEach { $0.resume() }
    }
}

// MARK: - Demo Attention Service
//
// No-network stand-in for `AttentionService` used by the demo container and
// deterministic UI-test scenarios.

actor DemoAttentionService: AttentionServiceProtocol {

    private static let emptyFeed = AttentionFeed(
        items: [],
        nextCursor: nil,
        healthyPrinterCount: 0
    )

    private var feed: AttentionFeed
    private let gatedFailureAction: AttentionActionKind?
    private let gateReleaseAction: AttentionActionKind?
    private let actionGate = DemoAttentionActionGate()
    private var didRunGatedFailure = false

    init() {
        self.feed = Self.emptyFeed
        self.gatedFailureAction = nil
        self.gateReleaseAction = nil
    }

    init(
        feed: AttentionFeed,
        gatedFailureAction: AttentionActionKind? = nil,
        gateReleaseAction: AttentionActionKind? = nil
    ) {
        self.feed = feed
        self.gatedFailureAction = gatedFailureAction
        self.gateReleaseAction = gateReleaseAction
    }

    func getFeed(cursor: String?, limit: Int?) async throws -> AttentionFeed {
        feed
    }

    func snooze(
        itemId: String,
        snoozedUntilUtc: Date
    ) async throws -> SnoozeAttentionResponse {
        removeItem(id: itemId)
        return SnoozeAttentionResponse(
            snoozedUntilUtc: snoozedUntilUtc,
            attentionItemAnchorAtUtc: nil
        )
    }

    func clearSnooze(itemId: String) async throws {}

    func executeAction(
        itemId: String,
        actionKind: AttentionActionKind
    ) async throws -> AttentionActionResult {
        if actionKind == gatedFailureAction, !didRunGatedFailure {
            didRunGatedFailure = true
            await actionGate.wait()
            throw DemoAttentionServiceError.forcedActionFailure(
                "The printer refused the first resume request."
            )
        }
        if actionKind == gateReleaseAction {
            await actionGate.open()
        }
        removeItem(id: itemId)
        return AttentionActionResult(outcome: "Ok")
    }

    private func removeItem(id: String) {
        feed = AttentionFeed(
            items: feed.items.filter { $0.id != id },
            nextCursor: feed.nextCursor,
            healthyPrinterCount: feed.healthyPrinterCount
        )
    }
}
