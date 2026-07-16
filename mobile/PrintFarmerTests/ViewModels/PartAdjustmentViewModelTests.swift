import XCTest
@testable import PrintFarmer

/// Tests for `PartAdjustmentViewModel` (#714 H4 remediation): rapid
/// duplicate-mutation prevention via a synchronous re-entrancy guard, a
/// stable/retryable idempotency key, and intent-change key invalidation.
final class PartAdjustmentViewModelTests: XCTestCase {
    @MainActor
    func testBeginSubmitReturnsFalseWhileAlreadySubmitting() {
        let viewModel = PartAdjustmentViewModel(part: makePart())
        viewModel.delta = -1

        XCTAssertTrue(viewModel.beginSubmit(), "first call should be allowed to proceed")
        XCTAssertFalse(viewModel.beginSubmit(), "a second synchronous call before the first completes must be rejected")
    }

    @MainActor
    func testBeginSubmitReturnsFalseWhenDeltaIsZero() {
        let viewModel = PartAdjustmentViewModel(part: makePart())
        viewModel.delta = 0

        XCTAssertFalse(viewModel.beginSubmit())
    }

    @MainActor
    func testDoubleInvocationOnlyAppliesOneAdjustment() async {
        // Simulates two rapid taps: the second must be rejected by
        // beginSubmit() before any Task/adjustPart call is made.
        let viewModel = PartAdjustmentViewModel(part: makePart())
        viewModel.delta = -2
        let service = MockPartsInventoryService()
        service.adjustmentToReturn = makeAdjustment(resultingBalance: 3)

        XCTAssertTrue(viewModel.beginSubmit())
        XCTAssertFalse(viewModel.beginSubmit(), "rapid second tap must be rejected synchronously")

        _ = await viewModel.submit(partsInventoryService: service)

        XCTAssertEqual(service.adjustPartCalls.count, 1, "only one adjustment must reach the server")
    }

    @MainActor
    func testSubmitUsesStableOperationKeyAcrossRetryOfSameIntent() async {
        let viewModel = PartAdjustmentViewModel(part: makePart())
        viewModel.delta = -2
        viewModel.reason = .qcReject
        let service = MockPartsInventoryService()
        service.adjustPartError = NetworkError.serverError(500)

        XCTAssertTrue(viewModel.beginSubmit())
        _ = await viewModel.submit(partsInventoryService: service)
        XCTAssertNotNil(viewModel.errorMessage, "first attempt should have failed")

        // Retry the same intent (delta/reason/notes unchanged) — no
        // noteIntentChanged() call, simulating the operator tapping the
        // same "Apply Adjustment" button again after a transient failure.
        service.adjustPartError = nil
        service.adjustmentToReturn = makeAdjustment(resultingBalance: 8)
        XCTAssertTrue(viewModel.beginSubmit())
        let result = await viewModel.submit(partsInventoryService: service)

        XCTAssertNotNil(result)
        XCTAssertEqual(service.adjustPartCalls.count, 2)
        let firstKey = service.adjustPartCalls[0].request.operationKey
        let secondKey = service.adjustPartCalls[1].request.operationKey
        XCTAssertNotNil(firstKey)
        XCTAssertEqual(firstKey, secondKey, "retrying the same intent must reuse the original idempotency key")
    }

    @MainActor
    func testNoteIntentChangedResetsOperationKeyForANewIntent() async {
        let viewModel = PartAdjustmentViewModel(part: makePart())
        viewModel.delta = -2
        let service = MockPartsInventoryService()
        service.adjustPartError = NetworkError.serverError(500)

        XCTAssertTrue(viewModel.beginSubmit())
        _ = await viewModel.submit(partsInventoryService: service)

        // Operator changes their mind and enters a genuinely new adjustment.
        viewModel.noteIntentChanged()
        viewModel.delta = -5
        service.adjustPartError = nil
        service.adjustmentToReturn = makeAdjustment(resultingBalance: 1)

        XCTAssertTrue(viewModel.beginSubmit())
        _ = await viewModel.submit(partsInventoryService: service)

        let firstKey = service.adjustPartCalls[0].request.operationKey
        let secondKey = service.adjustPartCalls[1].request.operationKey
        XCTAssertNotEqual(firstKey, secondKey, "a new intent must mint a fresh idempotency key")
    }

    @MainActor
    func testNoteIntentChangedIsNoOpWhileSubmitting() async {
        // Guards against a mid-flight .onChange firing (e.g. from the
        // view resetting `notes`) from invalidating the in-flight
        // operation's own retry key.
        let viewModel = PartAdjustmentViewModel(part: makePart())
        viewModel.delta = -3
        let service = MockPartsInventoryService()
        service.adjustmentToReturn = makeAdjustment(resultingBalance: 4)

        XCTAssertTrue(viewModel.beginSubmit())
        viewModel.noteIntentChanged() // must be ignored: isSubmitting == true
        _ = await viewModel.submit(partsInventoryService: service)

        XCTAssertEqual(service.adjustPartCalls.first?.request.delta, -3)
    }

    @MainActor
    func testSuccessfulSubmitReturnsAdjustmentAndUpdatesLatestOnHand() async {
        let viewModel = PartAdjustmentViewModel(part: makePart(onHand: 10))
        viewModel.delta = -4
        let service = MockPartsInventoryService()
        service.adjustmentToReturn = makeAdjustment(resultingBalance: 6)

        XCTAssertTrue(viewModel.beginSubmit())
        let result = await viewModel.submit(partsInventoryService: service)

        XCTAssertEqual(result?.resultingBalance, 6)
        XCTAssertEqual(viewModel.latestOnHand, 6)
        XCTAssertFalse(viewModel.isSubmitting)
        XCTAssertNotNil(viewModel.successMessage)
    }

    // MARK: - Final remediation Blocker 4: frozen intent snapshot + cancellation

    @MainActor
    func testRetryAfterFailureReusesFrozenSnapshotDespiteLiveMutationInGap() async {
        // Simulates a "commit-then-response-loss" retry: the first attempt
        // fails (as far as the client can tell), but before the operator
        // retries, the live `delta`/`notes` properties drift in the gap
        // between attempts WITHOUT going through `noteIntentChanged()`
        // (e.g. a stale @Binding write racing the async submit). The retry
        // must still resend the ORIGINAL frozen snapshot under the SAME
        // key — an in-flight UI mutation must never pair the stable key
        // with a changed body.
        let viewModel = PartAdjustmentViewModel(part: makePart())
        viewModel.delta = -2
        viewModel.reason = .qcReject
        viewModel.notes = "first attempt notes"
        let service = MockPartsInventoryService()
        service.adjustPartError = NetworkError.serverError(500)

        XCTAssertTrue(viewModel.beginSubmit())
        _ = await viewModel.submit(partsInventoryService: service)
        XCTAssertNotNil(viewModel.errorMessage, "sanity: first attempt failed")

        // Live properties drift without an explicit intent change.
        viewModel.delta = -99
        viewModel.notes = "mutated after failure, before retry"

        service.adjustPartError = nil
        service.adjustmentToReturn = makeAdjustment(resultingBalance: 1)
        XCTAssertTrue(viewModel.beginSubmit())
        let result = await viewModel.submit(partsInventoryService: service)

        XCTAssertNotNil(result, "truthful resulting balance/callback from the actual server response")
        XCTAssertEqual(result?.resultingBalance, 1)
        XCTAssertEqual(service.adjustPartCalls.count, 2)
        let firstKey = service.adjustPartCalls[0].request.operationKey
        let secondKey = service.adjustPartCalls[1].request.operationKey
        XCTAssertEqual(firstKey, secondKey, "retry key must remain stable (one logical operation)")
        XCTAssertEqual(service.adjustPartCalls[1].request.delta, -2, "retry body must reflect the ORIGINAL frozen intent, not the later live mutation")
        XCTAssertEqual(service.adjustPartCalls[1].request.notes, "first attempt notes")
    }

    @MainActor
    func testCancelledSubmitDoesNotWriteErrorMessage() async {
        // Simulates the sheet being dismissed (onDisappear cancels
        // activeTasks) while a submission is in flight. A cancelled task
        // must not write error state into a view that's already gone.
        let viewModel = PartAdjustmentViewModel(part: makePart())
        viewModel.delta = -3
        let service = MockPartsInventoryService()
        service.adjustPartError = NetworkError.serverError(500)

        XCTAssertTrue(viewModel.beginSubmit())
        let task = Task {
            await viewModel.submit(partsInventoryService: service)
        }
        task.cancel()
        _ = await task.value

        XCTAssertNil(viewModel.errorMessage, "a cancelled submission must not surface an error to a dismissed view")
        XCTAssertFalse(viewModel.isSubmitting)
    }

    @MainActor
    func testCancelledSubmitDoesNotWriteSuccessMessage() async {
        let viewModel = PartAdjustmentViewModel(part: makePart())
        viewModel.delta = -3
        let service = MockPartsInventoryService()
        service.adjustmentToReturn = makeAdjustment(resultingBalance: 3)

        XCTAssertTrue(viewModel.beginSubmit())
        let task = Task {
            await viewModel.submit(partsInventoryService: service)
        }
        task.cancel()
        _ = await task.value

        XCTAssertNil(viewModel.successMessage, "a cancelled submission must not write success state into a dismissed view")
        XCTAssertFalse(viewModel.isSubmitting)
    }

    // MARK: - Replacement remediation Item B: commit-then-response-loss causal proof
    //
    // `testRetryAfterFailureReusesFrozenSnapshotDespiteLiveMutationInGap`
    // above only proves a simple 500-then-fresh-success retry — it never
    // proves the server-side idempotent-dedupe contract a stable
    // `operationKey` exists for: that a commit which the CLIENT never saw
    // the response for (a lost/interrupted response, not a genuine
    // failure) is replayed — not re-applied — on retry. These tests use
    // `MockPartsInventoryService`'s stateful commit ledger
    // (`simulateResponseLossOnFirstCommit` / `appliedMutationCount`) to
    // prove that.

    @MainActor
    func testCommitThenResponseLossRetryReplaysCommittedResultWithoutSecondMutation() async {
        let viewModel = PartAdjustmentViewModel(part: makePart())
        viewModel.delta = -2
        viewModel.reason = .qcReject
        viewModel.notes = "loss test"
        let service = MockPartsInventoryService()
        service.adjustmentToReturn = makeAdjustment(resultingBalance: 7)
        service.simulateResponseLossOnFirstCommit = true

        XCTAssertTrue(viewModel.beginSubmit())
        let firstResult = await viewModel.submit(partsInventoryService: service)

        XCTAssertNil(firstResult, "the client believes the first attempt failed (response lost)")
        XCTAssertNotNil(viewModel.errorMessage)
        XCTAssertEqual(service.adjustPartCalls.count, 1)
        XCTAssertEqual(service.appliedMutationCount, 1, "the server DID commit the mutation exactly once, even though the client saw a failure")
        let frozenRequest = service.adjustPartCalls[0].request

        // Retry the same intent (same key/body) — the mock must replay the
        // already-committed result rather than applying a second mutation.
        XCTAssertTrue(viewModel.beginSubmit(), "the guard must release after the (apparent) failure so the operator can retry")
        let retryResult = await viewModel.submit(partsInventoryService: service)

        XCTAssertNotNil(retryResult, "the retry must succeed by replaying the committed result")
        XCTAssertEqual(retryResult?.resultingBalance, 7, "truthful resulting balance from the actual committed mutation")
        XCTAssertEqual(service.adjustPartCalls.count, 2, "two network attempts reached the server")
        XCTAssertEqual(service.appliedMutationCount, 1, "exactly one mutation was ever applied, despite two network attempts")
        XCTAssertEqual(service.adjustPartCalls[1].request, frozenRequest, "retry key+delta+reason+notes must be byte/value-identical to the frozen first attempt")
        XCTAssertEqual(viewModel.latestOnHand, 7)
        XCTAssertNotNil(viewModel.successMessage, "the retry's replayed success must be surfaced to the operator exactly once")
    }

    @MainActor
    func testCancelDeterministicallyAfterCommitButBeforeResponseDeliveryLeavesNoStaleWriteAndAllowsFreshRetry() async {
        let viewModel = PartAdjustmentViewModel(part: makePart())
        viewModel.delta = -2
        viewModel.reason = .qcReject
        let service = MockPartsInventoryService()
        service.adjustmentToReturn = makeAdjustment(resultingBalance: 9)
        let gate = AdjustmentAsyncGate()
        service.adjustPartGate = { await gate.wait() }

        XCTAssertTrue(viewModel.beginSubmit())
        let task = Task {
            await viewModel.submit(partsInventoryService: service)
        }

        // Deterministically wait until the mock is blocked at the gate —
        // which sits AFTER the mutation is committed but BEFORE the
        // response is delivered — via a real-state busy-poll, not a sleep.
        while await !gate.hasWaiters { await Task.yield() }
        XCTAssertEqual(service.appliedMutationCount, 1, "the commit must already have happened before cancellation")

        task.cancel()
        await gate.open()
        _ = await task.value

        XCTAssertNil(viewModel.errorMessage, "a cancelled submission must not surface an error to a dismissed view")
        XCTAssertNil(viewModel.successMessage, "a cancelled submission must not write success state into a dismissed view")
        XCTAssertFalse(viewModel.isSubmitting)
        XCTAssertEqual(service.appliedMutationCount, 1, "cancellation after commit must never trigger a second mutation")

        // The commit DID succeed server-side before cancellation, so the
        // key/snapshot are cleared (this intent is done) — a further
        // submit mints a genuinely fresh key rather than retrying/
        // replaying the cancelled-but-succeeded one.
        let firstKey = service.adjustPartCalls[0].request.operationKey
        XCTAssertTrue(viewModel.beginSubmit(), "cancellation after a successful commit must not block a fresh submit")
        _ = await viewModel.submit(partsInventoryService: service)

        XCTAssertEqual(service.adjustPartCalls.count, 2)
        let secondKey = service.adjustPartCalls[1].request.operationKey
        XCTAssertNotEqual(firstKey, secondKey, "the cancelled-but-succeeded intent's key must never be reused for a new submission")
        XCTAssertEqual(service.appliedMutationCount, 2, "the fresh key commits as a genuinely new mutation")
    }

    // MARK: - Fixtures

    private func makePart(onHand: Int = 5) -> PartInventoryResponse {
        PartInventoryResponse(
            id: UUID(), sku: "SKU-A", name: "Bracket", description: nil, modelFileRef: nil,
            defaultBinId: nil, defaultBinCode: nil, defaultBinName: nil,
            onHand: onHand, reorderPoint: 2, needsReorder: false, isActive: true,
            createdAt: .now, updatedAt: .now
        )
    }

    private func makeAdjustment(resultingBalance: Int) -> PartAdjustmentResponse {
        PartAdjustmentResponse(
            id: UUID(), partInventoryId: UUID(), sku: "SKU-A", binId: nil, binCode: nil,
            delta: -2, resultingBalance: resultingBalance, reason: .qcReject,
            printJobId: nil, operationKey: nil, notes: nil, userId: nil, createdAt: .now
        )
    }
}

/// Deterministic suspension-point gate for the Blocker B commit-then-loss
/// causal-proof tests — same shape as the identical helper already
/// established in `PrinterControlsViewModelTests.swift`, given a
/// file-local name to avoid any cross-file ambiguity within the test
/// target. Callers `await wait()` inside the code under test; the test
/// `await open()`s it only after observing `hasWaiters` via a real-state
/// busy-poll (`while await !gate.hasWaiters { await Task.yield() }`),
/// never a fixed sleep.
private actor AdjustmentAsyncGate {
    private var waiters: [CheckedContinuation<Void, Never>] = []
    private var opened = false

    var hasWaiters: Bool { !waiters.isEmpty || opened }

    func wait() async {
        if opened { return }
        await withCheckedContinuation { c in waiters.append(c) }
    }

    func open() {
        opened = true
        let toResume = waiters
        waiters.removeAll()
        for c in toResume { c.resume() }
    }
}
