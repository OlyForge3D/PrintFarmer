import Foundation
import os

/// Drives the "Log Parts to This Bin" manual-adjustment form on
/// `BinScanResultView` (#714, Blocker B remediation): generalizes the same
/// rapid-duplicate-mutation guard and idempotency-key stability that
/// `PartAdjustmentViewModel` established for `PartScanResultView` (H4), so
/// both manual-adjustment entry points share one proven pattern instead of
/// `BinScanResultView.logParts()` minting an ad-hoc key on every call.
///
/// Extracted out of `BinScanResultView`'s local `@State` so the guard and
/// key behavior can be unit tested directly, without driving a live SwiftUI
/// view.
@MainActor @Observable
final class BinPartLoggingViewModel {
    let bin: BinResponse

    var selectedSku: String = ""
    var quantity: Int = 1

    private(set) var isSubmitting = false
    var errorMessage: String?
    var successMessage: String?

    /// Stable per-intent idempotency key. Reused across a retry of the same
    /// logical log-parts intent (SKU/quantity/bin unchanged) so the server's
    /// `operationKey`-based dedupe recognizes a resubmission of the same
    /// failed attempt as one operation; reset only when the operator
    /// changes the intent via `noteIntentChanged()` or the submission
    /// succeeds.
    private var operationKey: String?

    private let logger = Logger(subsystem: "com.printfarmer.ios", category: "BinPartLogging")

    init(bin: BinResponse) {
        self.bin = bin
    }

    var canSubmit: Bool {
        !isSubmitting && !selectedSku.isEmpty && quantity > 0
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

    /// Marks that the operator has changed the log-parts intent
    /// (SKU/quantity), invalidating any in-flight retry's operation key so
    /// a genuinely new log-parts request gets a fresh idempotency key. A
    /// no-op while a submission is in flight, since the key must remain
    /// stable across that submission's own retry path.
    func noteIntentChanged() {
        guard !isSubmitting else { return }
        operationKey = nil
    }

    /// Submits the log-parts adjustment. Reuses the existing `operationKey`
    /// when one is already set (i.e. this is a retry of a previously-failed
    /// attempt with the same intent) rather than minting a new one, so the
    /// server's idempotent dedupe recognizes the resubmission as the same
    /// logical operation instead of applying the delta twice.
    ///
    /// Captures `selectedSku`/`quantity` into an immutable local snapshot
    /// before the awaited network call, so the request actually sent for
    /// THIS invocation cannot change even if the caller fails to keep the
    /// controls disabled while submitting.
    ///
    /// Callers must have already called `beginSubmit()` synchronously
    /// before creating the `Task` that invokes this method.
    @discardableResult
    func submit(partsInventoryService: any PartsInventoryServiceProtocol) async -> PartAdjustmentResponse? {
        errorMessage = nil
        successMessage = nil

        let key = operationKey ?? UUID().uuidString
        operationKey = key

        let requestSku = selectedSku
        let requestQuantity = quantity
        let request = AdjustPartInventoryRequest(
            delta: requestQuantity,
            reason: .manual,
            jobId: nil,
            binCode: bin.code,
            notes: "Logged at bin \(bin.code) via scan station",
            operationKey: key
        )

        do {
            let adjustment = try await partsInventoryService.adjustPart(sku: requestSku, request: request)
            isSubmitting = false
            guard !Task.isCancelled else {
                // The presenting view disappeared and cancelled this task
                // while the request was in flight. The adjustment still
                // applied server-side, so clear the key (this intent
                // succeeded) but don't write success state into a view
                // that's already gone.
                operationKey = nil
                return adjustment
            }
            operationKey = nil
            successMessage = "Logged \(requestQuantity) × \(requestSku) — new balance \(adjustment.resultingBalance)"
            return adjustment
        } catch {
            isSubmitting = false
            guard !Task.isCancelled else {
                // Cancellation, not a genuine failure — the key is left
                // intact so a future retry of the same intent still
                // dedupes correctly, and no error is written to a
                // dismissed view.
                return nil
            }
            logger.warning("Log-parts adjustment failed: \(error.localizedDescription)")
            errorMessage = error.localizedDescription
            return nil
        }
    }
}
