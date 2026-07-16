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
    /// Returns `true` if the caller may proceed to create the submit `Task`.
    func beginSubmit() -> Bool {
        guard canSubmit else { return false }
        isSubmitting = true
        return true
    }

    /// Marks that the operator has changed the adjustment's intent
    /// (delta/reason/notes), invalidating any in-flight retry's operation
    /// key so a genuinely new adjustment gets a fresh idempotency key. A
    /// no-op while a submission is in flight, since the key must remain
    /// stable across that submission's own retry path.
    func noteIntentChanged() {
        guard !isSubmitting else { return }
        operationKey = nil
    }

    /// Submits the adjustment. Reuses the existing `operationKey` when one
    /// is already set (i.e. this is a retry of a previously-failed attempt
    /// with the same intent) rather than minting a new one, so the server's
    /// idempotent dedupe recognizes the resubmission as the same logical
    /// operation instead of applying the delta twice.
    ///
    /// Callers must have already called `beginSubmit()` synchronously
    /// before creating the `Task` that invokes this method.
    @discardableResult
    func submit(partsInventoryService: any PartsInventoryServiceProtocol) async -> PartAdjustmentResponse? {
        errorMessage = nil
        successMessage = nil

        let key = operationKey ?? UUID().uuidString
        operationKey = key

        let request = AdjustPartInventoryRequest(
            delta: delta,
            reason: reason,
            jobId: nil,
            binCode: nil,
            notes: notes.isEmpty ? nil : notes,
            operationKey: key
        )

        do {
            let adjustment = try await partsInventoryService.adjustPart(sku: part.sku, request: request)
            latestOnHand = adjustment.resultingBalance
            successMessage = "New balance: \(adjustment.resultingBalance)"
            operationKey = nil
            delta = -1
            notes = ""
            isSubmitting = false
            return adjustment
        } catch {
            logger.warning("Adjustment failed: \(error.localizedDescription)")
            errorMessage = error.localizedDescription
            isSubmitting = false
            return nil
        }
    }
}
