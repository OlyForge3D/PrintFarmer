import Foundation

// MARK: - Offline Queue Status View Model (F10-Q1, #787)
//
// Drives the minimal operator status surface for the durable offline outbox:
// lists the active namespace's queued writes with their disposition
// (pending / replaying / conflict / expired / paused), triggers a manual replay
// pass, and offers the two explicit dispositions a parked item requires —
// Discard, or Review-and-retry-as-new.
//
// Review-and-retry-as-new is deliberately two-step: `beginReview` FIRST reads
// current server state and stages it for the operator; only an explicit
// `confirmRetryAsNew` mints a NEW idempotency key and enqueues a NEW intent
// (replacing the reviewed one). It is never a silent replay of the old body.

@MainActor @Observable
final class OfflineQueueStatusViewModel {
    private let queue: OfflineWriteQueue
    private let partsInventoryService: any PartsInventoryServiceProtocol

    private(set) var entries: [OfflineWriteQueueEntry] = []
    private(set) var isLoading = false
    var errorMessage: String?

    /// A staged review (current server state already fetched) awaiting explicit
    /// operator confirmation before any new intent is minted. `nil` when no
    /// review is in progress.
    private(set) var review: OfflineWriteReview?

    init(queue: OfflineWriteQueue, partsInventoryService: any PartsInventoryServiceProtocol) {
        self.queue = queue
        self.partsInventoryService = partsInventoryService
    }

    /// Reloads the active-namespace entries from the coordinator.
    func refresh() async {
        entries = await queue.activeEntries()
    }

    /// Manually drives one serialized replay pass (single owner; a no-op if a
    /// pass is already running or the gate is off / unbound), then refreshes.
    func retryReplay() async {
        await queue.replayPending()
        await refresh()
    }

    /// Explicit operator disposition: permanently discard a queued intent.
    func discard(_ entryID: UUID) async {
        _ = await queue.discard(itemID: entryID)
        if review?.entry.id == entryID { review = nil }
        await refresh()
    }

    /// Review-and-retry-as-new — STEP 1. Reads current server state for the
    /// affected SKUs and stages it; does NOT mint a new key or enqueue anything.
    func beginReview(_ entry: OfflineWriteQueueEntry) async {
        isLoading = true
        errorMessage = nil
        defer { isLoading = false }

        let skus = Self.affectedSKUs(entry.item.operation)
        var onHandBySku: [String: Int] = [:]
        do {
            if !skus.isEmpty {
                let parts = try await partsInventoryService.listParts(includeInactive: true)
                let wanted = Set(skus.map { OfflineWriteOperation.normalizeIdentity($0) })
                for part in parts where wanted.contains(OfflineWriteOperation.normalizeIdentity(part.sku)) {
                    onHandBySku[part.sku] = part.onHand
                }
            }
            review = OfflineWriteReview(entry: entry, currentOnHandBySku: onHandBySku)
        } catch {
            errorMessage = error.localizedDescription
        }
    }

    /// Cancels an in-progress review without minting anything.
    func cancelReview() {
        review = nil
    }

    /// Review-and-retry-as-new — STEP 2. Only on explicit confirm: mints a NEW
    /// idempotency key, builds a NEW intent from the reviewed one, and enqueues
    /// it in place of the reviewed item. Never re-sends the old key/body.
    func confirmRetryAsNew() async {
        guard let review else { return }
        let renewed = review.makeRenewedOperation()
        _ = await queue.retryAsNew(replacing: review.entry.id, with: renewed)
        self.review = nil
        await refresh()
    }

    private static func affectedSKUs(_ operation: OfflineWriteOperation) -> [String] {
        switch operation {
        case .partAdjustment(let sku, _):
            return [sku]
        case .harvest(_, let request):
            return request.outputs?.map { $0.sku } ?? []
        case .taskComplete, .toolheadBind:
            // Task-complete and toolhead-bind have no affected parts SKUs; their
            // review refetches canonical task/toolhead state instead.
            return []
        }
    }
}

// MARK: - Review context

/// A staged review of a parked item: the item plus the current server state
/// fetched for its affected SKUs. `makeRenewedOperation` builds a fresh intent
/// with a NEW idempotency key — the only way a reviewed item re-enters the
/// queue.
struct OfflineWriteReview: Equatable, Identifiable {
    let entry: OfflineWriteQueueEntry
    let currentOnHandBySku: [String: Int]

    var id: UUID { entry.id }

    /// Builds a NEW operation identical in shape to the reviewed one but with a
    /// freshly-minted idempotency key, so the server treats it as a distinct
    /// intent (and idempotently dedupes only against this new key).
    func makeRenewedOperation() -> OfflineWriteOperation {
        let newKey = UUID().uuidString
        switch entry.item.operation {
        case .partAdjustment(let sku, let request):
            var renewed = request
            renewed.operationKey = newKey
            return .partAdjustment(sku: sku, request: renewed)
        case .harvest(let jobId, let request):
            var renewed = request
            renewed.operationKey = newKey
            return .harvest(jobId: jobId, request: renewed)
        case .taskComplete(let taskID, _):
            // Idempotency key is a top-level field (header-based); mint a fresh
            // one so the review re-enters as a distinct intent.
            return .taskComplete(taskID: taskID, idempotencyKey: newKey)
        case .toolheadBind(let printerID, let toolheadIndex, _, let request, let expectedPriorSpoolId):
            return .toolheadBind(
                printerID: printerID,
                toolheadIndex: toolheadIndex,
                idempotencyKey: newKey,
                request: request,
                expectedPriorSpoolId: expectedPriorSpoolId
            )
        }
    }
}
