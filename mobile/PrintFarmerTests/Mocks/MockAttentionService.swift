import Foundation
@testable import PrintFarmer

/// Lightweight `AttentionServiceProtocol` double for tests that only need to
/// observe/stub `snooze` (e.g. job-attention notification action handling,
/// issue #1321). `AttentionProofSupport`'s `ScriptedAttentionService` covers
/// the richer feed-scripting scenarios; this mock stays intentionally small.
final class MockAttentionService: AttentionServiceProtocol, @unchecked Sendable {
    var feedToReturn = AttentionFeed(items: [], nextCursor: nil, healthyPrinterCount: 0)
    var errorToThrow: Error?

    var snoozeCalledWith: (itemId: String, snoozedUntilUtc: Date)?
    var snoozeResponseToReturn: SnoozeAttentionResponse?
    var clearSnoozeCalledWith: String?
    var executeActionCalledWith: (itemId: String, actionKind: AttentionActionKind)?
    var executeActionResultToReturn: AttentionActionResult?

    func getFeed(cursor: String?, limit: Int?) async throws -> AttentionFeed {
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
