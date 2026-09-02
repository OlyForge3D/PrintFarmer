import Foundation
@testable import PrintFarmer

/// Lightweight `AttentionServiceProtocol` double for tests that only need to
/// observe/stub `snooze` (e.g. job-attention notification action handling,
/// issue #1321). `AttentionProofSupport`'s `ScriptedAttentionService` covers
/// the richer feed-scripting scenarios; this mock stays intentionally small.
final class MockAttentionService: AttentionServiceProtocol, @unchecked Sendable {
    private let feedCallLock = NSLock()
    private var recordedFeedCalls: [(cursor: String?, limit: Int?)] = []
    private var recordedFeedHandler:
        (@Sendable (String?, Int?) async throws -> AttentionFeed)?

    var feedToReturn = AttentionFeed(items: [], nextCursor: nil, healthyPrinterCount: 0)
    var errorToThrow: Error?
    var getFeedHandler: (@Sendable (String?, Int?) async throws -> AttentionFeed)? {
        get {
            feedCallLock.lock()
            defer { feedCallLock.unlock() }
            return recordedFeedHandler
        }
        set {
            feedCallLock.lock()
            recordedFeedHandler = newValue
            feedCallLock.unlock()
        }
    }

    var getFeedCalls: [(cursor: String?, limit: Int?)] {
        feedCallLock.lock()
        defer { feedCallLock.unlock() }
        return recordedFeedCalls
    }

    var getFeedCallCount: Int {
        feedCallLock.lock()
        defer { feedCallLock.unlock() }
        return recordedFeedCalls.count
    }

    var snoozeCalledWith: (itemId: String, snoozedUntilUtc: Date)?
    var snoozeResponseToReturn: SnoozeAttentionResponse?
    var clearSnoozeCalledWith: String?
    var executeActionCalledWith: (itemId: String, actionKind: AttentionActionKind)?
    var executeActionResultToReturn: AttentionActionResult?

    /// Records the call synchronously. `NSLock.lock()`/`unlock()` are `noasync`,
    /// so the critical section must live in a sync helper rather than inline in
    /// `getFeed`. Keeping it separate also preserves the load-bearing property
    /// that the lock is released *before* awaiting the handler, which is what
    /// lets concurrent callers accumulate on a barrier in
    /// `testAttentionGatingAndPrefetchRequestsAreConcurrent`.
    private func recordFeedCall(cursor: String?, limit: Int?) {
        feedCallLock.lock()
        defer { feedCallLock.unlock() }
        recordedFeedCalls.append((cursor, limit))
    }

    func getFeed(cursor: String?, limit: Int?) async throws -> AttentionFeed {
        recordFeedCall(cursor: cursor, limit: limit)
        if let getFeedHandler {
            return try await getFeedHandler(cursor, limit)
        }
        if let error = errorToThrow { throw error }
        return feedToReturn
    }

    func snooze(itemId: String, snoozedUntilUtc: Date) async throws -> SnoozeAttentionResponse {
        snoozeCalledWith = (itemId, snoozedUntilUtc)
        if let error = errorToThrow { throw error }
        return snoozeResponseToReturn ?? SnoozeAttentionResponse(
            snoozedUntilUtc: snoozedUntilUtc,
            attentionItemAnchorAtUtc: nil
        )
    }

    func clearSnooze(itemId: String) async throws {
        clearSnoozeCalledWith = itemId
        if let error = errorToThrow { throw error }
    }

    func executeAction(itemId: String, actionKind: AttentionActionKind) async throws -> AttentionActionResult {
        executeActionCalledWith = (itemId, actionKind)
        if let error = errorToThrow { throw error }
        guard let result = executeActionResultToReturn else {
            throw NetworkError.notFound
        }
        return result
    }
}
