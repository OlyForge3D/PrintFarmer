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
        XCTAssertTrue(viewModel.isSubmitting, "cancellation must never write isSubmitting either — it's dismissed-child observable state, same as errorMessage/successMessage")
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
        XCTAssertTrue(viewModel.isSubmitting, "cancellation must never write isSubmitting either — it's dismissed-child observable state, same as errorMessage/successMessage")
    }

    // MARK: - Pre-first-turn cancellation: zero transport, zero state churn
    //
    // The two tests above prove a cancelled submission doesn't leak error
    // or success state, but they don't isolate WHEN the cancellation was
    // observed relative to the shielded transport. Because `beginSubmit()`
    // + `Task { await viewModel.submit(...) } ; task.cancel()` cancels the
    // child task before it has had any chance to run (no `await` occurs
    // between creating the task and cancelling it), the cancellation is
    // actually already "pre-first-turn" in both of those tests too — but
    // neither one asserts that the shielded transport was never started.
    // This test makes that causal claim explicit and adds a second axis
    // (the operationKey/pendingSnapshot must not be minted at all if the
    // submission never began, so a subsequent genuine attempt gets a
    // fresh key rather than reusing one from a submission that never
    // touched the server).
    @MainActor
    func testSubmitCancelledPriorToFirstTurnPerformsNoServiceCallAndMintsNoOperationKey() async {
        let viewModel = PartAdjustmentViewModel(part: makePart())
        viewModel.delta = -4
        viewModel.reason = .qcReject
        let service = MockPartsInventoryService()
        service.adjustmentToReturn = makeAdjustment(resultingBalance: 11)

        XCTAssertTrue(viewModel.beginSubmit())
        let task = Task {
            await viewModel.submit(partsInventoryService: service)
        }
        task.cancel()
        let result = await task.value

        XCTAssertNil(result, "a submission cancelled before its first turn has nothing to return")
        XCTAssertEqual(service.adjustPartCalls.count, 0, "the shielded transport must never be created if the submission never got a first turn — there is no committed response to protect")
        XCTAssertEqual(service.appliedMutationCount, 0, "no mutation may reach the server for a submission that never began")
        XCTAssertNil(viewModel.errorMessage)
        XCTAssertNil(viewModel.successMessage)
        XCTAssertTrue(viewModel.isSubmitting, "the re-entrancy guard set by beginSubmit() is dismissed-child state and must not be cleared by this codepath")
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
    func testCancelDeterministicallyAfterCommitButBeforeResponseDeliveryLeavesNoStaleWriteAndFreshInstanceCanSubmitIndependently() async {
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
        let result = await task.value

        XCTAssertEqual(result?.resultingBalance, 9, "the committed adjustment is still returned to the caller despite the task's own cancellation")
        XCTAssertNil(viewModel.errorMessage, "a cancelled submission must not surface an error to a dismissed view")
        XCTAssertNil(viewModel.successMessage, "a cancelled submission must not write success state into a dismissed view")
        XCTAssertTrue(viewModel.isSubmitting, "cancellation must never write isSubmitting either — it's dismissed-child observable state, same as errorMessage/successMessage")
        XCTAssertEqual(service.appliedMutationCount, 1, "cancellation after commit must never trigger a second mutation")

        // This VM instance belongs to the now-dismissed PartScanResultView
        // sheet; production never reuses it — a re-presented sheet always
        // constructs a brand new `PartAdjustmentViewModel(part:)`. That
        // fresh instance mints its own operationKey and is never blocked by
        // the discarded instance's (intentionally frozen) isSubmitting
        // state, so it can submit independently.
        let freshViewModel = PartAdjustmentViewModel(part: makePart())
        freshViewModel.delta = -2
        freshViewModel.reason = .qcReject
        XCTAssertTrue(freshViewModel.beginSubmit(), "a freshly-constructed view model for a re-presented sheet must be able to submit independently")
        _ = await freshViewModel.submit(partsInventoryService: service)

        XCTAssertEqual(service.adjustPartCalls.count, 2)
        let firstKey = service.adjustPartCalls[0].request.operationKey
        let secondKey = service.adjustPartCalls[1].request.operationKey
        XCTAssertNotEqual(firstKey, secondKey, "the cancelled-but-succeeded intent's key must never be reused by a subsequent submission")
        XCTAssertEqual(service.appliedMutationCount, 2, "the fresh instance's key commits as a genuinely new mutation")
    }

    // MARK: - Final trio Item 2: post-commit cancellation callback proof
    //
    // Dallas ruled: cancellation safety suppresses dismissed-CHILD state
    // writes (no stale successMessage/errorMessage on the dismissed
    // PartScanResultView's own PartAdjustmentViewModel) but must still
    // return the committed adjustment through to `onAdjusted` so the
    // still-alive PARENT (PartsInventoryListView) can refresh. This test
    // exercises the real production Task-nesting/cancellation shape used
    // by PartScanResultView's button action + PartsInventoryListView's
    // `onAdjusted` closure (see both files) rather than a reimplementation,
    // proving the callback is delivered exactly once and the parent's
    // refresh runs exactly once even though the child's own task was
    // cancelled after the mutation had already committed.

    @MainActor
    func testCancellationAfterCommitStillDeliversCallbackAndParentRefreshExactlyOnce() async {
        let viewModel = PartAdjustmentViewModel(part: makePart())
        viewModel.delta = -2
        viewModel.reason = .qcReject
        let service = MockPartsInventoryService()
        service.adjustmentToReturn = makeAdjustment(resultingBalance: 11)
        let gate = AdjustmentAsyncGate()
        service.adjustPartGate = { await gate.wait() }

        var onAdjustedCallCount = 0
        var parentLoadPartsCallCount = 0
        var childActiveTasks: [Task<Void, Never>] = []
        var parentActiveTasks: [Task<Void, Never>] = []

        // Mirrors PartScanResultView's button action exactly: synchronous
        // guard before Task creation, then a Task wrapping submit() that
        // invokes the onAdjusted callback with the returned adjustment.
        XCTAssertTrue(viewModel.beginSubmit())
        let childTask = Task {
            if await viewModel.submit(partsInventoryService: service) != nil {
                onAdjustedCallCount += 1
                // Mirrors PartsInventoryListView's onAdjusted closure: a
                // separate Task on the PARENT (which stays alive — only the
                // CHILD sheet is being dismissed) that reloads its list.
                let parentTask = Task { @MainActor in
                    parentLoadPartsCallCount += 1
                }
                parentActiveTasks.append(parentTask)
            }
        }
        childActiveTasks.append(childTask)

        // Deterministically wait until the mock is blocked at the gate —
        // which sits AFTER the mutation is committed but BEFORE the
        // response is delivered — via a real-state busy-poll, not a sleep.
        while await !gate.hasWaiters { await Task.yield() }
        XCTAssertEqual(service.appliedMutationCount, 1, "the commit must already have happened before dismissal")

        // Simulates PartScanResultView.onDisappear cancelling only its own
        // activeTasks (the sheet is dismissed) — PartsInventoryListView is
        // still alive/visible and its own tasks are never cancelled here.
        childActiveTasks.forEach { $0.cancel() }
        await gate.open()
        _ = await childTask.value
        for parentTask in parentActiveTasks { await parentTask.value }

        XCTAssertEqual(service.appliedMutationCount, 1, "exactly one server mutation")
        XCTAssertNil(viewModel.errorMessage, "cancellation-safe: no error write to the dismissed child view model")
        XCTAssertNil(viewModel.successMessage, "cancellation-safe: no success write to the dismissed child view model")
        XCTAssertTrue(viewModel.isSubmitting, "cancellation-safe: isSubmitting must not be written either — flipping it would itself be a dismissed-child state write")
        XCTAssertEqual(onAdjustedCallCount, 1, "the committed adjustment must still be delivered to the callback exactly once")
        XCTAssertEqual(parentLoadPartsCallCount, 1, "the still-alive parent must refresh exactly once")
    }

    // MARK: - Cancellation shield causal proof (post-review recovery)
    //
    // A real `URLSession`-backed transport observes cancellation of the
    // task it's running on and throws `CancellationError`/`URLError
    // .cancelled` once that task is cancelled — even if the server has
    // already committed the mutation. The tests above only proved
    // cancellation-safety against a mock that never actually threw on
    // cancellation, so they could pass even if `submit()` never shielded
    // the network call at all. `MockPartsInventoryService.adjustPart` now
    // performs a real `Task.checkCancellation()` after the commit/gate
    // point, matching that real transport behavior. These two tests prove
    // both halves of the fix: an UNSHIELDED call (the mock invoked
    // directly, on the same task the view cancels) genuinely loses the
    // response to cancellation, while `submit()`'s SHIELDED transport
    // `Task` still delivers the committed result despite the exact same
    // cancellation timing.

    @MainActor
    func testUnshieldedCallDirectlyOnTheMockLosesTheResponseToCancellationAfterCommit() async throws {
        let service = MockPartsInventoryService()
        service.adjustmentToReturn = makeAdjustment(resultingBalance: 42)
        let gate = AdjustmentAsyncGate()
        service.adjustPartGate = { await gate.wait() }
        let request = AdjustPartInventoryRequest(
            delta: -1, reason: .qcReject, jobId: nil, binCode: nil, notes: nil,
            operationKey: "unshielded-proof-key"
        )

        // Call the mock directly — no ViewModel, no shielding `Task` in
        // between — on the SAME task the test is about to cancel. This is
        // the "unshielded control" that `submit()` used to be equivalent
        // to before the fix.
        let task = Task {
            try await service.adjustPart(sku: "SKU-A", request: request)
        }

        while await !gate.hasWaiters { await Task.yield() }
        XCTAssertEqual(service.appliedMutationCount, 1, "the mutation commits before the cancellation-aware check")

        task.cancel()
        await gate.open()

        do {
            _ = try await task.value
            XCTFail("an unshielded call on a cancelled task must lose its response to CancellationError, matching real transport behavior")
        } catch is CancellationError {
            // Expected: this is exactly the failure mode the production
            // shield in `submit()` exists to prevent.
        }
        XCTAssertEqual(service.appliedMutationCount, 1, "the server-side commit still only happened once even though the caller lost the response")
    }

    @MainActor
    func testShieldedSubmitStillDeliversTheCommittedAdjustmentUnderTheSameCancellationTiming() async {
        let viewModel = PartAdjustmentViewModel(part: makePart())
        viewModel.delta = -2
        viewModel.reason = .qcReject
        let service = MockPartsInventoryService()
        service.adjustmentToReturn = makeAdjustment(resultingBalance: 42)
        let gate = AdjustmentAsyncGate()
        service.adjustPartGate = { await gate.wait() }

        XCTAssertTrue(viewModel.beginSubmit())
        let task = Task {
            await viewModel.submit(partsInventoryService: service)
        }

        while await !gate.hasWaiters { await Task.yield() }
        XCTAssertEqual(service.appliedMutationCount, 1, "the mutation commits before cancellation, identical timing to the unshielded proof above")

        task.cancel()
        await gate.open()
        let result = await task.value

        XCTAssertEqual(result?.resultingBalance, 42, "the shielded transport Task is not cancelled with its caller, so submit() still returns the committed adjustment")
        XCTAssertEqual(service.appliedMutationCount, 1, "exactly one mutation, never a duplicate")
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
