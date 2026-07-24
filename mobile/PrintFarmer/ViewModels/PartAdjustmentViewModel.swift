import Foundation
import os

/// Drives the manual stock-adjustment form on `PartScanResultView` (#714,
/// H4 remediation): QC reject / manual correction deltas. Harvest deltas are
/// applied only through the atomic harvest flow (`HarvestViewModel`), never
/// here.
///
/// Extracted out of `PartScanResultView`'s local `@State` so the
/// rapid-duplicate-mutation guard and idempotency-key stability can be unit
/// tested directly, without driving a live SwiftUI view.
@MainActor @Observable
final class PartAdjustmentViewModel {
    let part: PartInventoryResponse

    var delta: Int = -1
    var reason: PartAdjustmentReason = .qcReject
    var notes: String = ""

    private(set) var isSubmitting = false
    var errorMessage: String?
    var successMessage: String?
    private(set) var latestOnHand: Int

    /// Stable per-intent idempotency key. Reused across a retry of the same
    /// logical adjustment (so the server's `operationKey`-based dedupe
    /// recognizes a resubmission of the same failed attempt as one
    /// operation); reset only when the operator changes the intent
    /// (delta/reason/notes) via `noteIntentChanged()`.
    private var operationKey: String?

    /// Immutable snapshot of `{delta, reason, notes}` captured the first
    /// time a given `operationKey` is used (in `beginSubmit()`). A retry of
    /// the SAME key always resends this exact body — even if the live
    /// `delta`/`reason`/`notes` properties were mutated in the gap between
    /// the synchronous `beginSubmit()` guard and this `submit()` call
    /// actually starting to run inside its `Task` — so an in-flight UI
    /// mutation can never pair the stable key with a changed body. Cleared
    /// alongside `operationKey` whenever the key itself is cleared (new
    /// intent or a completed success).
    private var pendingSnapshot: (delta: Int, reason: PartAdjustmentReason, notes: String)?

    private let logger = Logger(subsystem: "com.printfarmer.ios", category: "PartAdjustment")

    init(part: PartInventoryResponse) {
        self.part = part
        self.latestOnHand = part.onHand
    }

    var canSubmit: Bool {
        !isSubmitting && delta != 0
    }

    /// Synchronous, atomic check-and-set re-entrancy guard. Must be called
    /// directly from the button's action closure — BEFORE any `Task {}` is
    /// created — so the check and the `isSubmitting = true` set happen on
    /// the same run-loop turn as the tap, with no intervening suspension
    /// point. A guard placed inside the async `submit()` body instead would
    /// leave a race window between two rapid taps, since Swift concurrency
    /// does not guarantee strict ordering of two independently-scheduled
    /// `Task`s hopping onto the `@MainActor`.
    ///
    /// Also captures `pendingSnapshot` here (only if not already set for
    /// the current key/intent), rather than lazily inside `submit()` —
    /// this is the last synchronous point before the request body could
    /// possibly drift from what the operator saw when they tapped.
    ///
    /// Returns `true` if the caller may proceed to create the submit `Task`.
    func beginSubmit() -> Bool {
        guard canSubmit else { return false }
        isSubmitting = true
        if pendingSnapshot == nil {
            pendingSnapshot = (delta: delta, reason: reason, notes: notes)
        }
        return true
    }

    /// Marks that the operator has changed the adjustment's intent
    /// (delta/reason/notes), invalidating any in-flight retry's operation
    /// key AND its frozen body snapshot so a genuinely new adjustment gets
    /// both a fresh idempotency key and a fresh request body. A no-op while
    /// a submission is in flight, since the key/snapshot must remain stable
    /// across that submission's own retry path.
    func noteIntentChanged() {
        guard !isSubmitting else { return }
        operationKey = nil
        pendingSnapshot = nil
    }

    /// Submits the adjustment. Reuses the existing `operationKey` when one
    /// is already set (i.e. this is a retry of a previously-failed attempt
    /// with the same intent) rather than minting a new one, so the server's
    /// idempotent dedupe recognizes the resubmission as the same logical
    /// operation instead of applying the delta twice. Uses `pendingSnapshot`
    /// (frozen at `beginSubmit()` time) for the request body rather than
    /// live properties, so a same-key retry always resends the exact
    /// original body.
    ///
    /// Callers must have already called `beginSubmit()` synchronously
    /// before creating the `Task` that invokes this method. Falls back to
    /// live values only if `submit()` is ever invoked directly without
    /// `beginSubmit()` (some unit tests exercise it that way).
    @discardableResult
    func submit(
        partsInventoryService: any PartsInventoryServiceProtocol,
        offlineQueue: OfflineWriteEnqueuing? = nil
    ) async -> PartAdjustmentResponse? {
        // If the caller's own Task was already cancelled before this
        // method got its first turn on the executor (e.g. the presenting
        // sheet was dismissed in the same runloop tick as the submit
        // Task was created), bail out before touching any state or
        // minting a key/snapshot. Nothing has been submitted yet, so
        // there is no committed response to shield or return — unlike
        // the post-commit cancellation path below, a pre-start
        // cancellation must not start the shielded transport at all,
        // and must not reset errorMessage/successMessage either, since
        // that would itself be a dismissed-child observable write.
        guard !Task.isCancelled else { return nil }

        errorMessage = nil
        successMessage = nil

        let key = operationKey ?? UUID().uuidString
        operationKey = key
        let snapshot = pendingSnapshot ?? (delta: delta, reason: reason, notes: notes)
        pendingSnapshot = snapshot

        let request = AdjustPartInventoryRequest(
            delta: snapshot.delta,
            reason: snapshot.reason,
            jobId: nil,
            binCode: nil,
            notes: snapshot.notes.isEmpty ? nil : snapshot.notes,
            operationKey: key
        )

        do {
            // A real network transport (URLSession) watches for
            // cancellation of the task it runs on and throws once that
            // task is cancelled — so calling `adjustPart` directly here
            // would risk losing an already-committed server response if
            // the presenting view is dismissed (and this `submit()` task
            // cancelled) while the request is in flight. Running the call
            // in its own unstructured `Task` shields it from that
            // cancellation: an unstructured `Task` does not automatically
            // inherit cancellation from the task that created it, so it
            // keeps running to completion and still delivers the
            // committed result below even after `submit()`'s own task has
            // been cancelled.
            let transport = Task {
                try await partsInventoryService.adjustPart(sku: part.sku, request: request)
            }
            let adjustment = try await transport.value

            guard !Task.isCancelled else {
                // The presenting view disappeared and cancelled this task
                // while the request was in flight. The adjustment still
                // applied server-side (the shielded transport above
                // completed regardless), so clear the key/snapshot (this
                // intent succeeded) — but don't touch `isSubmitting` or
                // write success state into a view that's already gone.
                operationKey = nil
                pendingSnapshot = nil
                return adjustment
            }
            isSubmitting = false
            latestOnHand = adjustment.resultingBalance
            successMessage = "New balance: \(adjustment.resultingBalance)"
            operationKey = nil
            pendingSnapshot = nil
            delta = -1
            notes = ""
            return adjustment
        } catch {
            guard !Task.isCancelled else {
                // Cancellation, not a genuine failure — the key and
                // snapshot are left intact so a future retry of the same
                // intent still dedupes correctly with the identical body.
                // Don't touch `isSubmitting` and don't write an error into
                // a dismissed view.
                return nil
            }
            isSubmitting = false
            logger.warning("Adjustment failed: \(error.localizedDescription)")

            // Offline-class failure + durable outbox available: hand the frozen
            // intent (identical operationKey + body) to the queue for later
            // idempotent replay, rather than dropping it. Not a second
            // idempotency owner — the queue re-sends this exact key/body. A
            // terminal failure (conflict/validation/auth) is NOT queued; it
            // surfaces immediately below.
            if let offlineQueue, OfflineWriteReplayClassifier.isEnqueueableOfflineFailure(error) {
                let operation = OfflineWriteOperation.partAdjustment(sku: part.sku, request: request)
                let outcome = await offlineQueue.enqueue(operation)
                if case .enqueued = outcome {
                    // The intent is now durably owned by the queue; release this
                    // view model's key/snapshot and reset the form as on success.
                    operationKey = nil
                    pendingSnapshot = nil
                    delta = -1
                    notes = ""
                    successMessage = "Saved offline — will sync when reconnected."
                    return nil
                }
            }

            errorMessage = error.localizedDescription
            return nil
        }
    }
}
