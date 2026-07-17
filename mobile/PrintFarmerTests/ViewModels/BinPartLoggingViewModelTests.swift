import XCTest
@testable import PrintFarmer

/// Tests for `BinPartLoggingViewModel` (#714 Blocker B remediation):
/// `BinScanResultView.logParts()` previously bypassed the H4 idempotency
/// pattern entirely (fresh key every call, async-only guard). These tests
/// prove the generalized guard/key behavior now matches
/// `PartAdjustmentViewModel`'s proven pattern, plus cancellation safety.
final class BinPartLoggingViewModelTests: XCTestCase {
    @MainActor
    func testBeginSubmitReturnsFalseWhileAlreadySubmitting() {
        let viewModel = BinPartLoggingViewModel(bin: makeBin())
        viewModel.selectedSku = "SKU-A"

        XCTAssertTrue(viewModel.beginSubmit(), "first call should be allowed to proceed")
        XCTAssertFalse(viewModel.beginSubmit(), "a second synchronous call before the first completes must be rejected")
    }

    @MainActor
    func testBeginSubmitReturnsFalseWhenSkuIsEmpty() {
        let viewModel = BinPartLoggingViewModel(bin: makeBin())
        viewModel.selectedSku = ""

        XCTAssertFalse(viewModel.beginSubmit())
    }

    @MainActor
    func testBeginSubmitReturnsFalseWhenQuantityIsZero() {
        let viewModel = BinPartLoggingViewModel(bin: makeBin())
        viewModel.selectedSku = "SKU-A"
        viewModel.quantity = 0

        XCTAssertFalse(viewModel.beginSubmit())
    }

    @MainActor
    func testDoubleInvocationOnlyAppliesOneAdjustment() async {
        // Simulates two rapid taps of "Log Parts to This Bin": the second
        // must be rejected by beginSubmit() before any Task/adjustPart call
        // is made.
        let viewModel = BinPartLoggingViewModel(bin: makeBin())
        viewModel.selectedSku = "SKU-A"
        viewModel.quantity = 5
        let service = MockPartsInventoryService()
        service.adjustmentToReturn = makeAdjustment(resultingBalance: 5)

        XCTAssertTrue(viewModel.beginSubmit())
        XCTAssertFalse(viewModel.beginSubmit(), "rapid second tap must be rejected synchronously")

        _ = await viewModel.submit(partsInventoryService: service)

        XCTAssertEqual(service.adjustPartCalls.count, 1, "only one adjustment must reach the server")
    }

    @MainActor
    func testSubmitUsesStableOperationKeyAcrossRetryOfSameIntent() async {
        let viewModel = BinPartLoggingViewModel(bin: makeBin())
        viewModel.selectedSku = "SKU-A"
        viewModel.quantity = 5
        let service = MockPartsInventoryService()
        service.adjustPartError = NetworkError.serverError(500)

        XCTAssertTrue(viewModel.beginSubmit())
        _ = await viewModel.submit(partsInventoryService: service)
        XCTAssertNotNil(viewModel.errorMessage, "first attempt should have failed")

        // Retry the same intent (SKU/quantity unchanged) — no
        // noteIntentChanged() call, simulating the operator tapping the
        // same "Log Parts to This Bin" button again after a transient
        // failure.
        service.adjustPartError = nil
        service.adjustmentToReturn = makeAdjustment(resultingBalance: 12)
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
        let viewModel = BinPartLoggingViewModel(bin: makeBin())
        viewModel.selectedSku = "SKU-A"
        viewModel.quantity = 5
        let service = MockPartsInventoryService()
        service.adjustPartError = NetworkError.serverError(500)

        XCTAssertTrue(viewModel.beginSubmit())
        _ = await viewModel.submit(partsInventoryService: service)

        // Operator changes their mind: different SKU/quantity is a
        // genuinely new intent.
        viewModel.noteIntentChanged()
        viewModel.selectedSku = "SKU-B"
        viewModel.quantity = 2
        service.adjustPartError = nil
        service.adjustmentToReturn = makeAdjustment(resultingBalance: 2)

        XCTAssertTrue(viewModel.beginSubmit())
        _ = await viewModel.submit(partsInventoryService: service)

        let firstKey = service.adjustPartCalls[0].request.operationKey
        let secondKey = service.adjustPartCalls[1].request.operationKey
        XCTAssertNotEqual(firstKey, secondKey, "a new intent must mint a fresh idempotency key")
    }

    @MainActor
    func testNoteIntentChangedIsNoOpWhileSubmitting() async {
        // Guards against a mid-flight .onChange firing from invalidating
        // the in-flight operation's own retry key.
        let viewModel = BinPartLoggingViewModel(bin: makeBin())
        viewModel.selectedSku = "SKU-A"
        viewModel.quantity = 3
        let service = MockPartsInventoryService()
        service.adjustmentToReturn = makeAdjustment(resultingBalance: 3)

        XCTAssertTrue(viewModel.beginSubmit())
        viewModel.noteIntentChanged() // must be ignored: isSubmitting == true
        _ = await viewModel.submit(partsInventoryService: service)

        XCTAssertEqual(service.adjustPartCalls.first?.request.delta, 3)
    }

    @MainActor
    func testSubmitSendsFixedBinCodeAndManualReason() async {
        let viewModel = BinPartLoggingViewModel(bin: makeBin(code: "BIN-42"))
        viewModel.selectedSku = "SKU-A"
        viewModel.quantity = 7
        let service = MockPartsInventoryService()
        service.adjustmentToReturn = makeAdjustment(resultingBalance: 7)

        XCTAssertTrue(viewModel.beginSubmit())
        _ = await viewModel.submit(partsInventoryService: service)

        let sent = service.adjustPartCalls.first?.request
        XCTAssertEqual(service.adjustPartCalls.first?.sku, "SKU-A")
        XCTAssertEqual(sent?.binCode, "BIN-42")
        XCTAssertEqual(sent?.reason, .manual)
        XCTAssertEqual(sent?.delta, 7)
    }

    @MainActor
    func testSuccessfulSubmitReturnsAdjustmentAndClearsSubmittingState() async {
        let viewModel = BinPartLoggingViewModel(bin: makeBin())
        viewModel.selectedSku = "SKU-A"
        viewModel.quantity = 4
        let service = MockPartsInventoryService()
        service.adjustmentToReturn = makeAdjustment(resultingBalance: 9)

        XCTAssertTrue(viewModel.beginSubmit())
        let result = await viewModel.submit(partsInventoryService: service)

        XCTAssertEqual(result?.resultingBalance, 9)
        XCTAssertFalse(viewModel.isSubmitting)
        XCTAssertNotNil(viewModel.successMessage)
    }

    @MainActor
    func testCancelledSubmitDoesNotWriteErrorMessage() async {
        // Simulates the sheet being dismissed (onDisappear cancels
        // activeTasks) while a submission is in flight. A cancelled task
        // must not write error state into a view that's already gone.
        let viewModel = BinPartLoggingViewModel(bin: makeBin())
        viewModel.selectedSku = "SKU-A"
        viewModel.quantity = 3
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
        let viewModel = BinPartLoggingViewModel(bin: makeBin())
        viewModel.selectedSku = "SKU-A"
        viewModel.quantity = 3
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
    // Same causal proof as PartAdjustmentViewModelTests: cancellation
    // observed before submit() gets a first turn must skip the shielded
    // transport entirely (there is no committed response to protect yet)
    // and must not mint an operationKey for a submission that never
    // touched the server.
    @MainActor
    func testSubmitCancelledPriorToFirstTurnPerformsNoServiceCallAndMintsNoOperationKey() async {
        let viewModel = BinPartLoggingViewModel(bin: makeBin())
        viewModel.selectedSku = "SKU-A"
        viewModel.quantity = 4
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

    // MARK: - Final trio Item 2: post-commit cancellation callback proof
    //
    // Same production callback path as PartAdjustmentViewModel /
    // PartScanResultView (BinScanResultView's button action wraps
    // `submit()` in an identical Task/onAdjusted pattern — see
    // BinScanResultView.swift) — the equivalent proof applies here too.

    @MainActor
    func testCancellationAfterCommitStillDeliversCallbackAndParentRefreshExactlyOnce() async {
        let viewModel = BinPartLoggingViewModel(bin: makeBin())
        viewModel.selectedSku = "SKU-A"
        viewModel.quantity = 5
        let service = MockPartsInventoryService()
        service.adjustmentToReturn = makeAdjustment(resultingBalance: 12)
        let gate = BinLoggingAsyncGate()
        service.adjustPartGate = { await gate.wait() }

        var onAdjustedCallCount = 0
        var parentLoadPartsCallCount = 0
        var childActiveTasks: [Task<Void, Never>] = []
        var parentActiveTasks: [Task<Void, Never>] = []

        // Mirrors BinScanResultView's button action exactly: synchronous
        // guard before Task creation, then a Task wrapping submit() that
        // invokes the onAdjusted callback with the returned adjustment.
        XCTAssertTrue(viewModel.beginSubmit())
        let childTask = Task {
            if await viewModel.submit(partsInventoryService: service) != nil {
                onAdjustedCallCount += 1
                // Mirrors a presenting parent's onAdjusted closure: a
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

        // Simulates BinScanResultView.onDisappear cancelling only its own
        // activeTasks (the sheet is dismissed) — the presenting parent is
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
    // Same rationale as the equivalent proof in
    // `PartAdjustmentViewModelTests.swift`: `MockPartsInventoryService
    // .adjustPart` now performs a real `Task.checkCancellation()` after
    // the commit/gate point, matching how a real `URLSession`-backed
    // transport throws once its own task is cancelled. These two tests
    // prove both halves of the fix for `BinPartLoggingViewModel`: an
    // UNSHIELDED call (the mock invoked directly, on the same task the
    // view cancels) genuinely loses the response to cancellation, while
    // `submit()`'s SHIELDED transport `Task` still delivers the
    // committed result under the exact same cancellation timing.

    @MainActor
    func testUnshieldedCallDirectlyOnTheMockLosesTheResponseToCancellationAfterCommit() async throws {
        let service = MockPartsInventoryService()
        service.adjustmentToReturn = makeAdjustment(resultingBalance: 42)
        let gate = BinLoggingAsyncGate()
        service.adjustPartGate = { await gate.wait() }
        let request = AdjustPartInventoryRequest(
            delta: 1, reason: .manual, jobId: nil, binCode: "BIN-1", notes: nil,
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
        let viewModel = BinPartLoggingViewModel(bin: makeBin())
        viewModel.selectedSku = "SKU-A"
        viewModel.quantity = 5
        let service = MockPartsInventoryService()
        service.adjustmentToReturn = makeAdjustment(resultingBalance: 42)
        let gate = BinLoggingAsyncGate()
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

    private func makeBin(code: String = "BIN-1") -> BinResponse {
        BinResponse(
            id: UUID(), code: code, name: "Shelf A1", location: "Aisle 1",
            notes: nil, isActive: true, createdAt: .now, updatedAt: .now
        )
    }

    private func makeAdjustment(resultingBalance: Int) -> PartAdjustmentResponse {
        PartAdjustmentResponse(
            id: UUID(), partInventoryId: UUID(), sku: "SKU-A", binId: nil, binCode: "BIN-1",
            delta: 1, resultingBalance: resultingBalance, reason: .manual,
            printJobId: nil, operationKey: nil, notes: nil, userId: nil, createdAt: .now
        )
    }
}

/// Deterministic suspension-point gate for the post-commit cancellation
/// causal-proof test above — same shape as `AdjustmentAsyncGate` in
/// `PartAdjustmentViewModelTests.swift`, given a file-local name to avoid
/// any cross-file ambiguity within the test target. Callers `await wait()`
/// inside the code under test; the test `await open()`s it only after
/// observing `hasWaiters` via a real-state busy-poll
/// (`while await !gate.hasWaiters { await Task.yield() }`), never a fixed
/// sleep.
private actor BinLoggingAsyncGate {
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
