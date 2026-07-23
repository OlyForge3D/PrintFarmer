import XCTest
@testable import PrintFarmer

/// Tests for PredictiveViewModel: predicting failure, loading alerts and forecasts,
/// computing risk levels, and error handling.
@MainActor
final class PredictiveViewModelTests: XCTestCase {
    
    private var mockPredictiveService: MockPredictiveService!
    private var viewModel: PredictiveViewModel!
    private let testPrinterId = UUID()
    
    override func setUp() {
        super.setUp()
        mockPredictiveService = MockPredictiveService()
        viewModel = PredictiveViewModel()
        viewModel.configure(predictiveService: mockPredictiveService)
    }
    
    override func tearDown() {
        viewModel = nil
        mockPredictiveService = nil
        super.tearDown()
    }

    /// Removed: the previous `isBoundedParkAckResult` helper returned
    /// `true` for every `ParkAckResult` case, making all assertions
    /// tautological. Reviewer finding #5 requires per-test exact expected
    /// results/sets. Each call site now asserts the specific classification(s)
    /// that its actor timeline can produce.
    
    // MARK: - Initial State
    
    func testInitialState() {
        XCTAssertNil(viewModel.prediction)
        XCTAssertTrue(viewModel.alerts.isEmpty)
        XCTAssertTrue(viewModel.forecasts.isEmpty)
        XCTAssertFalse(viewModel.isLoading)
        XCTAssertNil(viewModel.error)
        XCTAssertEqual(viewModel.predictionStatus, .idle)
        XCTAssertFalse(viewModel.hasKnownLikelihood)
        XCTAssertFalse(viewModel.isRefreshingStalePrediction)
        XCTAssertEqual(viewModel.riskLevel, "Unavailable")
    }
    
    // MARK: - Predict Failure Success
    
    func testPredictFailurePopulatesData() async {
        let prediction = JobFailurePrediction(
            printerId: testPrinterId,
            material: "PLA",
            estimatedDurationMinutes: 60.0,
            predictedFailureLikelihood: 65.0,
            riskLevel: "high",
            factors: [
                PredictionFactor(
                    name: "Nozzle Wear",
                    value: 80.0,
                    weight: 0.4
                )
            ]
        )
        mockPredictiveService.predictionToReturn = prediction
        
        await viewModel.predictFailure(printerId: testPrinterId, material: "PLA", duration: 3600)
        
        XCTAssertNotNil(viewModel.prediction)
        XCTAssertEqual(viewModel.prediction?.printerId, testPrinterId)
        XCTAssertEqual(viewModel.prediction?.predictedFailureLikelihood, 65.0)
        XCTAssertEqual(viewModel.prediction?.riskLevel, "high")
        XCTAssertEqual(viewModel.prediction?.factors.count, 1)
        XCTAssertFalse(viewModel.isLoading)
        XCTAssertNil(viewModel.error)
        XCTAssertEqual(viewModel.predictionStatus, .success)
        
        let request = mockPredictiveService.predictJobFailureCalledWith
        XCTAssertEqual(request?.printerId, testPrinterId)
        XCTAssertEqual(request?.material, "PLA")
        XCTAssertEqual(request?.estimatedDurationSeconds, 3600)
    }
    
    func testPredictFailureHandlesErrorWhenNoPriorPrediction() async {
        // No prior success: `prediction` stays nil, but the view model must
        // now surface an explicit failure state and error message instead of
        // silently rendering as low risk (issue #808). Use a representative
        // real transport error shape (`NetworkError.transportError`) rather
        // than a generic test double.
        let urlError = URLError(.notConnectedToInternet)
        mockPredictiveService.errorToThrow = NetworkError.transportError(urlError)

        await viewModel.predictFailure(printerId: testPrinterId, material: "PETG", duration: 7200)

        let request = mockPredictiveService.predictJobFailureCalledWith
        XCTAssertEqual(request?.printerId, testPrinterId)
        XCTAssertEqual(request?.material, "PETG")
        XCTAssertEqual(request?.estimatedDurationSeconds, 7200)
        XCTAssertNil(viewModel.prediction)
        XCTAssertEqual(viewModel.predictionStatus, .failed(PredictiveViewModel.failureMessage))
        XCTAssertEqual(viewModel.error, PredictiveViewModel.failureMessage)
        XCTAssertEqual(viewModel.riskLevel, "Unavailable")
        XCTAssertFalse(viewModel.isLoading)
        XCTAssertFalse(viewModel.hasKnownLikelihood)
    }

    func testPredictFailurePreservesStalePriorPrediction() async {
        // Stale-data policy: on failure we preserve the last successful
        // prediction so the operator still sees real risk context alongside
        // the failure banner. The alternative — clearing to nil — is what
        // caused #808 (nil rendered as "Low"). Use `NetworkError.serverError`
        // as a representative real backend failure shape.
        let stalePrediction = JobFailurePrediction(
            printerId: testPrinterId,
            material: "PLA",
            estimatedDurationMinutes: 30,
            predictedFailureLikelihood: 85.0,
            riskLevel: "critical",
            factors: []
        )
        viewModel.prediction = stalePrediction
        mockPredictiveService.errorToThrow = NetworkError.serverError(503)

        await viewModel.predictFailure(printerId: testPrinterId, material: "PETG", duration: 7200)

        XCTAssertEqual(viewModel.prediction?.predictedFailureLikelihood, 85.0)
        XCTAssertEqual(viewModel.predictionStatus, .failed(PredictiveViewModel.failureMessage))
        XCTAssertEqual(viewModel.error, PredictiveViewModel.failureMessage)
        // Stale prediction still renders its real level, never a spurious "Low".
        XCTAssertEqual(viewModel.riskLevel, "Critical")
        XCTAssertTrue(viewModel.hasKnownLikelihood)
        XCTAssertFalse(viewModel.isLoading)
    }

    func testPredictFailureHandlesDecodingError() async {
        // Decoding failures must surface identically to transport failures —
        // never fall through to a benign low-risk presentation. Use the
        // real `NetworkError.decodingFailed(ResponseDecodingFailure)` shape
        // produced by APIClient rather than a raw `DecodingError`.
        struct FakeKey: CodingKey { var stringValue = "x"; var intValue: Int? = nil
            init?(stringValue: String) { self.stringValue = stringValue }
            init?(intValue: Int) { self.intValue = intValue; self.stringValue = "\(intValue)" }
        }
        let decodingError = DecodingError.keyNotFound(
            FakeKey(stringValue: "riskLevel")!,
            DecodingError.Context(codingPath: [], debugDescription: "missing riskLevel")
        )
        let failure = ResponseDecodingFailure(error: decodingError, targetType: JobFailurePrediction.self)
        mockPredictiveService.errorToThrow = NetworkError.decodingFailed(failure)

        await viewModel.predictFailure(printerId: testPrinterId, material: "PLA", duration: 3600)

        XCTAssertNil(viewModel.prediction)
        XCTAssertEqual(viewModel.predictionStatus, .failed(PredictiveViewModel.failureMessage))
        XCTAssertEqual(viewModel.error, PredictiveViewModel.failureMessage)
        XCTAssertEqual(viewModel.riskLevel, "Unavailable")
        XCTAssertFalse(viewModel.hasKnownLikelihood)
        XCTAssertFalse(viewModel.isLoading)
    }

    func testPredictFailureClearsPreviousError() async {
        // Seed a prior error directly — predictFailure() always resets
        // `error = nil` at the start regardless of outcome.
        viewModel.error = "prior failure"
        viewModel.predictionStatus = .failed("prior failure")

        mockPredictiveService.predictionToReturn = JobFailurePrediction(
            printerId: testPrinterId,
            material: nil,
            estimatedDurationMinutes: nil,
            predictedFailureLikelihood: 20.0,
            riskLevel: "low",
            factors: []
        )

        await viewModel.predictFailure(printerId: testPrinterId, material: "PLA", duration: 3600)

        XCTAssertNil(viewModel.error)
        XCTAssertEqual(viewModel.predictionStatus, .success)
    }

    // MARK: - Retry

    func testRetryPredictionReplaysLastCanonicalRequest() async {
        // First call fails; retry must re-issue exactly the same request
        // (single canonical call) without needing the view to remember the
        // original arguments. Scripted outcomes prove the mock actually
        // received each call with a per-invocation snapshot; `callCount`
        // proves retry issues exactly one additional invocation.
        let service = mockPredictiveService!
        await service.callState.enqueue(.failure(NetworkError.timeout))
        await service.callState.enqueue(.failure(NetworkError.timeout))

        await viewModel.predictFailure(printerId: testPrinterId, material: "PETG", duration: 5400)
        XCTAssertEqual(viewModel.predictionStatus, .failed(PredictiveViewModel.failureMessage))
        let countAfterFirst = await service.callState.callCount
        XCTAssertEqual(countAfterFirst, 1, "first predictFailure must have invoked the service exactly once")

        await viewModel.retryPrediction()

        let history = await service.callState.callHistory
        let countAfterRetry = await service.callState.callCount
        XCTAssertEqual(countAfterRetry, 2, "retry must invoke the service exactly one additional time")
        XCTAssertEqual(history.count, 2)
        XCTAssertEqual(history[0].printerId, testPrinterId)
        XCTAssertEqual(history[0].material, "PETG")
        XCTAssertEqual(history[0].estimatedDurationSeconds, 5400)
        // Exact request equality — every field must match the first call.
        XCTAssertEqual(history[1].printerId, history[0].printerId)
        XCTAssertEqual(history[1].material, history[0].material)
        XCTAssertEqual(history[1].estimatedDurationSeconds, history[0].estimatedDurationSeconds)
    }

    func testRetryAfterFailureSuccessClearsFailureState() async {
        let service = mockPredictiveService!
        await service.callState.enqueue(.failure(NetworkError.transportError(URLError(.timedOut))))
        await viewModel.predictFailure(printerId: testPrinterId, material: "PLA", duration: 3600)
        XCTAssertEqual(viewModel.predictionStatus, .failed(PredictiveViewModel.failureMessage))
        XCTAssertNotNil(viewModel.error)

        // Retry now succeeds.
        await service.callState.enqueue(.success(JobFailurePrediction(
            printerId: testPrinterId,
            material: "PLA",
            estimatedDurationMinutes: 60,
            predictedFailureLikelihood: 15.0,
            riskLevel: "low",
            factors: []
        )))

        await viewModel.retryPrediction()

        XCTAssertEqual(viewModel.predictionStatus, .success)
        XCTAssertNil(viewModel.error)
        XCTAssertEqual(viewModel.prediction?.predictedFailureLikelihood, 15.0)
        XCTAssertEqual(viewModel.riskLevel, "Low")
        let countAfterRetry = await service.callState.callCount
        XCTAssertEqual(countAfterRetry, 2, "retry must invoke the service exactly once after the original attempt")
    }

    func testRetryWithoutPriorPredictionIsNoOp() async {
        // No canonical request captured yet → nothing to retry.
        await viewModel.retryPrediction()

        XCTAssertNil(mockPredictiveService.predictJobFailureCalledWith)
        XCTAssertEqual(viewModel.predictionStatus, .idle)
        XCTAssertNil(viewModel.prediction)
        let countAfterNoOp = await mockPredictiveService.callState.callCount
        XCTAssertEqual(countAfterNoOp, 0)
    }

    // MARK: - Refresh-with-stale (Hicks R1)

    func testRetryWithStalePredictionSurfacesRefreshingState() async {
        // While a retry is in flight and a prior successful prediction is
        // still displayed, `isRefreshingStalePrediction` must be true so
        // the view can render the stale gauge as visibly refreshing (issue
        // #808 Hicks R1). Uses a real suspension point via AsyncGate — no
        // sleeps, yields, or elapsed-time gates.
        let vm = viewModel!
        let service = mockPredictiveService!
        let priorSuccess = JobFailurePrediction(
            printerId: testPrinterId,
            material: "PLA",
            estimatedDurationMinutes: 60,
            predictedFailureLikelihood: 80.0,
            riskLevel: "critical",
            factors: []
        )
        await service.callState.enqueue(.success(priorSuccess))
        await vm.predictFailure(printerId: testPrinterId, material: "PLA", duration: 3600)
        XCTAssertEqual(vm.predictionStatus, .success)
        XCTAssertFalse(vm.isRefreshingStalePrediction, "no refresh in flight yet")

        // Now enqueue a second outcome and gate it so we can observe the
        // in-flight refresh state deterministically.
        let refreshGate = AsyncGate()
        service.beforeReturnHook = { [refreshGate] in await refreshGate.wait() }
        let refreshedSuccess = JobFailurePrediction(
            printerId: testPrinterId,
            material: "PLA",
            estimatedDurationMinutes: 60,
            predictedFailureLikelihood: 20.0,
            riskLevel: "low",
            factors: []
        )
        await service.callState.enqueue(.success(refreshedSuccess))

        async let retryCall: Void = vm.retryPrediction()
        // Deterministic entry handshake — waits for the mock's
        // `beforeReturnHook` to have reached `refreshGate.wait()`. No
        // polling loop, no yields, no elapsed-time gate.
        await refreshGate.waitForEntry()

        // While blocked at the gate, the view model must expose the
        // refreshing-with-stale signal so the view renders a refreshing
        // indicator and de-emphasises the stale gauge.
        XCTAssertEqual(vm.predictionStatus, .loading)
        XCTAssertNotNil(vm.prediction, "stale prediction must be preserved during refresh")
        XCTAssertEqual(vm.prediction?.predictedFailureLikelihood, priorSuccess.predictedFailureLikelihood)
        XCTAssertTrue(vm.isRefreshingStalePrediction,
                      "the view relies on this flag to render the stale gauge as visibly refreshing")

        service.beforeReturnHook = nil
        await refreshGate.open()
        await retryCall

        XCTAssertEqual(vm.predictionStatus, .success)
        XCTAssertFalse(vm.isRefreshingStalePrediction, "refresh is done; stale marker must clear")
        XCTAssertEqual(vm.prediction?.predictedFailureLikelihood, refreshedSuccess.predictedFailureLikelihood)
    }

    // MARK: - Unavailable likelihood (Hicks R2)

    func testPredictionWithNilLikelihoodIsTreatedAsUnavailable() async {
        // A prediction that decodes successfully but carries no
        // `predictedFailureLikelihood` must NOT render as a green 0% Low
        // gauge (issue #808 Hicks R2). `hasKnownLikelihood` gates the
        // gauge; `riskLevel` surfaces "Unavailable" so downstream text
        // never labels a missing score as low risk.
        let service = mockPredictiveService!
        await service.callState.enqueue(.success(JobFailurePrediction(
            printerId: testPrinterId,
            material: "PLA",
            estimatedDurationMinutes: 60,
            predictedFailureLikelihood: nil,
            riskLevel: "unknown",
            factors: []
        )))

        await viewModel.predictFailure(printerId: testPrinterId, material: "PLA", duration: 3600)

        XCTAssertNotNil(viewModel.prediction, "the prediction record itself is still preserved")
        XCTAssertFalse(viewModel.hasKnownLikelihood,
                       "nil likelihood must be reported as unknown so the view swaps to the unavailable-risk card")
        XCTAssertEqual(viewModel.riskLevel, "Unavailable",
                       "nil likelihood must never render as Low")
        XCTAssertEqual(viewModel.predictionStatus, .success,
                       "the request itself succeeded — only the risk score is missing")
    }

    func testKnownLikelihoodEnablesRiskGauge() async {
        // Baseline for the R2 flag: a successful prediction with a real
        // likelihood must set `hasKnownLikelihood = true` so the view
        // renders the numeric gauge.
        let service = mockPredictiveService!
        await service.callState.enqueue(.success(JobFailurePrediction(
            printerId: testPrinterId,
            material: "PLA",
            estimatedDurationMinutes: 60,
            predictedFailureLikelihood: 42.0,
            riskLevel: "moderate",
            factors: []
        )))

        await viewModel.predictFailure(printerId: testPrinterId, material: "PLA", duration: 3600)

        XCTAssertTrue(viewModel.hasKnownLikelihood)
        XCTAssertEqual(viewModel.riskLevel, "Moderate")
        XCTAssertEqual(viewModel.riskPercentage, 42)
    }

    // MARK: - Stale / Late Completion

    func testLateCompletingRequestDoesNotOverwriteNewerResult() async {
        // Non-vacuity design: use per-invocation scripted outcomes on the
        // mock, snapshotted before the gate suspends. That way the first
        // (stale) call cannot silently re-read a newer `predictionToReturn`
        // — its return value is fixed to prediction A at call time. If the
        // view model's generation fence were removed, prediction A would
        // overwrite prediction B when the first call finally completes.
        let firstGate = AsyncGate()
        let vm = viewModel!
        let service = mockPredictiveService!

        let predictionA = JobFailurePrediction(
            printerId: testPrinterId,
            material: "PLA",
            estimatedDurationMinutes: 60,
            predictedFailureLikelihood: 90.0,
            riskLevel: "critical",
            factors: []
        )
        let predictionB = JobFailurePrediction(
            printerId: testPrinterId,
            material: "PLA",
            estimatedDurationMinutes: 60,
            predictedFailureLikelihood: 10.0,
            riskLevel: "low",
            factors: []
        )
        await service.callState.enqueue(.success(predictionA))
        await service.callState.enqueue(.success(predictionB))

        // Only the FIRST scripted call is gated. Companion test
        // `testFirstOutcomeWouldWriteWithoutInterleave` proves that A is
        // written when there is no newer call to supersede it — together
        // they prove the fence is what suppresses the late write.
        service.beforeReturnHook = { [firstGate] in
            await firstGate.wait()
            // Detach hook so the second scripted call is not also gated.
        }

        let printerId = testPrinterId
        async let firstCall: Void = vm.predictFailure(printerId: printerId, material: "PLA", duration: 3600)

        // Deterministic entry handshake — resumes exactly when the mock's
        // `beforeReturnHook` reaches `firstGate.wait()` for call A. Lost-
        // wakeup-safe: if the entry has already occurred, returns
        // immediately without polling.
        await firstGate.waitForEntry()

        // Detach hook so the second scripted call completes normally.
        service.beforeReturnHook = nil
        await vm.predictFailure(printerId: printerId, material: "PLA", duration: 3600)

        // Newer call landed with prediction B.
        XCTAssertEqual(vm.prediction?.predictedFailureLikelihood, predictionB.predictedFailureLikelihood)
        XCTAssertEqual(vm.predictionStatus, .success)

        // Release the stale first call. Its snapshotted outcome (A) must
        // not overwrite the newer result even though `performPrediction`
        // will reach the success branch with a distinct value.
        await firstGate.open()
        await firstCall

        XCTAssertEqual(vm.prediction?.predictedFailureLikelihood, predictionB.predictedFailureLikelihood,
                       "generation fence must suppress the late write; prediction B must remain visible")
        XCTAssertEqual(vm.predictionStatus, .success)

        // Non-vacuity evidence: both scripted calls actually ran and each
        // carried its distinct snapshot.
        let callCount = await service.callState.callCount
        let snapshots = await service.callState.recordedSnapshots
        XCTAssertEqual(callCount, 2)
        XCTAssertEqual(snapshots.count, 2)
        if case .success(let s0) = snapshots[0], case .success(let s1) = snapshots[1] {
            XCTAssertEqual(s0?.predictedFailureLikelihood, predictionA.predictedFailureLikelihood,
                           "first invocation must have snapshotted prediction A before suspending")
            XCTAssertEqual(s1?.predictedFailureLikelihood, predictionB.predictedFailureLikelihood,
                           "second invocation must have snapshotted prediction B")
            XCTAssertNotEqual(s0?.predictedFailureLikelihood, s1?.predictedFailureLikelihood,
                              "the two snapshots must be distinct or the test is vacuous")
        } else {
            XCTFail("expected two success snapshots recorded by the mock")
        }
    }

    func testFirstOutcomeWouldWriteWithoutInterleave() async {
        // Companion to `testLateCompletingRequestDoesNotOverwriteNewerResult`.
        // Establishes non-vacuity by showing that when there is no newer
        // call to supersede it, the same scripted outcome A DOES write to
        // the view model. Combined with the late-completion test, this
        // proves the generation fence — not some coincidence — is what
        // suppresses the late write.
        let vm = viewModel!
        let service = mockPredictiveService!
        let predictionA = JobFailurePrediction(
            printerId: testPrinterId,
            material: "PLA",
            estimatedDurationMinutes: 60,
            predictedFailureLikelihood: 90.0,
            riskLevel: "critical",
            factors: []
        )
        await service.callState.enqueue(.success(predictionA))

        await vm.predictFailure(printerId: testPrinterId, material: "PLA", duration: 3600)

        XCTAssertEqual(vm.prediction?.predictedFailureLikelihood, predictionA.predictedFailureLikelihood)
        XCTAssertEqual(vm.predictionStatus, .success)
        let callCount = await service.callState.callCount
        XCTAssertEqual(callCount, 1)
    }

    // MARK: - AsyncGate handshake (helper proof)

    /// Strict actor-serialised ordering: observer registers first, then
    /// waiter arrives. Because `registerObserver()` is synchronous-in-actor
    /// (no `await` between register and the subsequent `registerWaiter`),
    /// the snapshot after each step deterministically proves the order.
    func testAsyncGateStrictOrder_observerBeforeWait() async {
        let gate = AsyncGate()

        let observer = await gate.registerObserver()
        let afterRegister = await gate.snapshot()
        XCTAssertEqual(afterRegister.observerOrder, [observer.id])
        XCTAssertEqual(afterRegister.pendingEntrySignals, 0)
        XCTAssertEqual(afterRegister.completedObserverCount, 0)

        let waiter = await gate.registerWaiter()
        let afterWaiter = await gate.snapshot()
        XCTAssertEqual(afterWaiter.observerOrder, [],
                       "wait() must consume the parked observer via signalEntry")
        XCTAssertEqual(afterWaiter.completedObserverCount, 1)
        XCTAssertEqual(afterWaiter.pendingEntrySignals, 0)
        XCTAssertEqual(afterWaiter.waiterOrder, [waiter.id])

        // Observer awaits — must return immediately without parking.
        await gate.awaitObserver(observer)

        await gate.open()
        await gate.awaitWaiter(waiter)
        await gate.close()
    }

    /// Strict actor-serialised ordering: waiter arrives first (bumps
    /// pending signal), then observer registers and consumes it.
    func testAsyncGateStrictOrder_waitBeforeObserver() async {
        let gate = AsyncGate()

        let waiter = await gate.registerWaiter()
        let afterWaiter = await gate.snapshot()
        XCTAssertEqual(afterWaiter.observerOrder, [])
        XCTAssertEqual(afterWaiter.pendingEntrySignals, 1,
                       "arriving with no observer must accumulate one pending signal")
        XCTAssertEqual(afterWaiter.waiterOrder, [waiter.id])

        let observer = await gate.registerObserver()
        let afterObserver = await gate.snapshot()
        XCTAssertEqual(afterObserver.pendingEntrySignals, 0,
                       "observer must consume the pending signal")
        XCTAssertEqual(afterObserver.completedObserverCount, 1)

        await gate.awaitObserver(observer)
        await gate.open()
        await gate.awaitWaiter(waiter)
        await gate.close()
    }

    /// Open-before-wait: `open()` first, then a subsequent `wait()` returns
    /// immediately (opened latches) AND still emits an entry signal.
    /// Repeated `open()` is a safe no-op.
    func testAsyncGateOpenBeforeWaitStillSignalsEntry() async {
        let gate = AsyncGate()
        await gate.open()
        await gate.open()   // repeated open — idempotent

        // Waiter after open: latched completed, no parking.
        let waiter = await gate.registerWaiter()
        let snap = await gate.snapshot()
        XCTAssertTrue(snap.opened)
        XCTAssertEqual(snap.waiterOrder, [], "waiter must not park after open")
        XCTAssertEqual(snap.completedWaiterCount, 1)
        // The waiter still emitted an entry signal — an observer registered
        // afterwards must find it immediately.
        let observer = await gate.registerObserver()
        let snap2 = await gate.snapshot()
        XCTAssertEqual(snap2.pendingEntrySignals, 0)
        XCTAssertEqual(snap2.completedObserverCount, 1)

        await gate.awaitWaiter(waiter)
        await gate.awaitObserver(observer)
        await gate.close()
    }

    /// Cancellation before the awaiter has parked: create a Task, cancel
    /// it, then await its completion. Actor serialisation guarantees the
    /// park in `awaitWaiter`'s continuation closure completes before the
    /// `cancelWaiter` Task hops onto the actor, so the drain happens via
    /// the parked-only path. Post-drain snapshot must show zero
    /// order/parked/completed state for the cancelled id.
    func testAsyncGateCancelBeforeRegister() async {
        let gate = AsyncGate()
        let task = Task { await gate.wait() }
        task.cancel()

        // Reviewer finding #4: BOUNDED HANDSHAKE BEFORE close-drain.
        // The prior version relied solely on `await gate.close()` to
        // drain the parked continuation, so it would still pass if
        // cancellation processing were REMOVED entirely (close would
        // drain the continuation via .closedWhileParked). Restore an
        // exact receipt: wait until the actor's cancelWaiter has been
        // dispatched (increment observed via waiterCancelInvocationCount)
        // BEFORE calling close. This proves cancel processing actually
        // ran, distinct from close-drain acceptance.
        await gate.waitForWaiterCancelCount(atLeast: 1)
        let mid = await gate.snapshot()
        XCTAssertEqual(mid.waiterCancelInvocationCount, 1,
                       "exactly one cancelWaiter dispatched pre-close")

        await gate.close()
        await task.value

        let snap = await gate.snapshot()
        XCTAssertEqual(snap.parkedWaiterCount, 0)
        XCTAssertEqual(snap.parkedObserverCount, 0)
        XCTAssertEqual(snap.waiterOrder, [])
        XCTAssertEqual(snap.observerOrder, [])
        XCTAssertEqual(snap.completedWaiterCount, 0,
                       "cancel must not relatch into completedWaiters")
        XCTAssertEqual(snap.completedObserverCount, 0)
        // Exact cancel-invocation count still one; close is a bounded
        // no-op for cancel counters. If cancel processing regresses,
        // this stays zero and the pre-close handshake above hangs.
        XCTAssertEqual(snap.waiterCancelInvocationCount, 1,
                       "exactly one cancelWaiter across the test lifetime")
    }

    /// Cancellation after the awaiter has parked: register synchronously,
    /// park in an unstructured Task, use `waitForXParked` to structurally
    /// prove the continuation is parked, then cancel. cancelX drains via
    /// parkedX exactly once and seals the fate as
    /// `.cancelledWhileParked`. Post-drain: zero state.
    ///
    /// Reviewer finding #4: this test previously accepted broad counts
    /// after `close()`, so it would pass even if cancel processing were
    /// removed (close-drain would still zero the parked counts). Now
    /// requires exact per-side pre-close proof via
    /// `waitForXCancelCount` + `.parkedResumedByCancel` /
    /// `.cancelledWhileParked` counters BEFORE close.
    func testAsyncGateCancelAfterRegister() async {
        let gate = AsyncGate()

        // Observer path FIRST — no pending signal yet, so the observer
        // actually parks (rather than being latched by a prior waiter).
        let observerToken = await gate.registerObserver()
        let observerTask = Task { await gate.awaitObserver(observerToken) }
        await gate.waitForObserverParked(observerToken)
        observerTask.cancel()

        // BOUNDED HANDSHAKE #1: cancelObserver MUST have run. Because
        // the parked continuation is drained by cancelObserver, this
        // implicitly orders our subsequent snapshot AFTER the cancel
        // dispatch.
        await gate.waitForObserverCancelCount(atLeast: 1)
        let midObs = await gate.snapshot()
        XCTAssertEqual(midObs.observerCancelInvocationCount, 1,
                       "exactly one cancelObserver dispatched pre-close")
        XCTAssertEqual(midObs.observerResumeCounts[.parkedResumedByCancel] ?? 0, 1,
                       "cancelObserver drained matched parked continuation exactly once")
        XCTAssertEqual(midObs.observerFateCounts[.cancelledWhileParked] ?? 0, 1,
                       "fate sealed exactly .cancelledWhileParked")
        XCTAssertEqual(midObs.parkedObserverCount, 0,
                       "parked drained pre-close by cancel, not close")
        XCTAssertEqual(midObs.observerCancelIgnoredCount, 0,
                       "matched cancel — ignored count stays zero")

        // Waiter path. registerWaiter emits an entry signal, but since
        // observerOrder is now empty (cancelled observer was drained),
        // the signal accumulates in pendingEntrySignals and the waiter
        // itself parks in the usual way.
        let waiterToken = await gate.registerWaiter()
        let waiterTask = Task { await gate.awaitWaiter(waiterToken) }
        await gate.waitForWaiterParked(waiterToken)
        waiterTask.cancel()

        // BOUNDED HANDSHAKE #2: cancelWaiter dispatched exactly once.
        await gate.waitForWaiterCancelCount(atLeast: 1)
        let midWtr = await gate.snapshot()
        XCTAssertEqual(midWtr.waiterCancelInvocationCount, 1,
                       "exactly one cancelWaiter dispatched pre-close")
        XCTAssertEqual(midWtr.waiterResumeCounts[.parkedResumedByCancel] ?? 0, 1,
                       "cancelWaiter drained matched parked continuation exactly once")
        XCTAssertEqual(midWtr.waiterFateCounts[.cancelledWhileParked] ?? 0, 1,
                       "waiter fate sealed exactly .cancelledWhileParked")
        XCTAssertEqual(midWtr.parkedWaiterCount, 0)
        XCTAssertEqual(midWtr.waiterCancelIgnoredCount, 0)

        // Bounded teardown. Both cancels already processed pre-close
        // (proved above), so close does not need to drain any parked
        // continuation and cancelIgnoredCount stays exactly zero.
        await gate.close()
        await observerTask.value
        await waiterTask.value

        let snap = await gate.snapshot()
        XCTAssertEqual(snap.parkedWaiterCount, 0)
        XCTAssertEqual(snap.parkedObserverCount, 0)
        XCTAssertEqual(snap.waiterOrder, [])
        XCTAssertEqual(snap.observerOrder, [])
        XCTAssertEqual(snap.completedWaiterCount, 0,
                       "cancel must not relatch into completedWaiters")
        XCTAssertEqual(snap.completedObserverCount, 0,
                       "cancel must not relatch into completedObservers")
        // Exact post-close: no late cancels, no ignored cancels. If
        // cancel processing regresses, the pre-close handshakes hang.
        XCTAssertEqual(snap.observerCancelIgnoredCount, 0,
                       "matched cancel drained parked pre-close; no ignore path")
        XCTAssertEqual(snap.waiterCancelIgnoredCount, 0,
                       "matched cancel drained parked pre-close; no ignore path")
        XCTAssertEqual(snap.observerCancelInvocationCount, 1,
                       "exactly one cancelObserver across the test lifetime")
        XCTAssertEqual(snap.waiterCancelInvocationCount, 1,
                       "exactly one cancelWaiter across the test lifetime")
    }

    /// Open drains the waiter first via `completedWaiters.insert`; the
    /// subsequently invoked `awaitWaiter` consumes that latch. A late
    /// `cancelWaiter` (from a cancellation that arrives after the
    /// continuation has been resumed and the awaitWaiter has returned)
    /// finds no parked continuation and is a zero-mutation no-op — no
    /// relatch into `completedWaiters`.
    func testAsyncGateOpenBeforeCancel_lateCancelIsNoOp() async {
        let gate = AsyncGate()
        let waiterToken = await gate.registerWaiter()

        // Drain via open() BEFORE the awaitWaiter task is created.
        await gate.open()
        let afterOpen = await gate.snapshot()
        XCTAssertEqual(afterOpen.completedWaiterCount, 1,
                       "open must latch not-yet-parked waiter as completed")
        XCTAssertTrue(afterOpen.opened)

        // Await + cancel. awaitWaiter picks up the latched completion
        // via `completedWaiters.remove(id)` and resumes. cancelWaiter
        // that runs afterwards must be a no-op.
        let task = Task { await gate.awaitWaiter(waiterToken) }
        task.cancel()

        // H3 failure-safe teardown: `open()` above already latched the
        // not-yet-parked waiter as completed (afterOpen.completedWaiterCount
        // == 1 — a bounded pre-close proof). Close UNCONDITIONALLY before
        // awaiting the task so a latch-consumption regression fails the
        // post-close invariant instead of stranding `task.value`.
        await gate.close()
        await task.value

        let final = await gate.snapshot()
        XCTAssertEqual(final.parkedWaiterCount, 0)
        XCTAssertEqual(final.waiterOrder, [])
        XCTAssertEqual(final.completedWaiterCount, 0,
                       "the latch must be gone AND the late cancel must not relatch")
        XCTAssertEqual(final.waiterResumeCounts[.parkedResumedByClose] ?? 0, 0,
                       "waiter took the latch path (never parked) — close rescued nothing")
        XCTAssertTrue(final.closed)
    }

    /// Symmetric case for observer path: signalEntry latches the observer
    /// as completed; awaitObserver drains the latch; a late cancel is a
    /// zero-mutation no-op.
    func testAsyncGateSignalBeforeAwait_lateCancelIsNoOp() async {
        let gate = AsyncGate()
        let observerToken = await gate.registerObserver()

        // Signal via a waiter — observer is latched completed because
        // there is no parked continuation yet.
        _ = await gate.registerWaiter()
        let afterSignal = await gate.snapshot()
        XCTAssertEqual(afterSignal.completedObserverCount, 1)
        XCTAssertEqual(afterSignal.observerOrder, [])

        let task = Task { await gate.awaitObserver(observerToken) }
        task.cancel()

        // H3 failure-safe teardown: the observer was already latched
        // completed by the waiter registration (afterSignal.completedObserverCount
        // == 1 — bounded pre-close proof). Close UNCONDITIONALLY before the
        // await so a latch regression fails an assertion, never strands.
        await gate.close()
        await task.value

        let final = await gate.snapshot()
        XCTAssertEqual(final.parkedObserverCount, 0)
        XCTAssertEqual(final.observerOrder, [])
        XCTAssertEqual(final.completedObserverCount, 0,
                       "the latch must be gone AND the late cancel must not relatch")
        XCTAssertEqual(final.observerResumeCounts[.parkedResumedByClose] ?? 0, 0,
                       "observer took the latch path (never parked) — close rescued nothing")
        XCTAssertTrue(final.closed)
    }

    /// Open/cancel race — cancel mailboxed BEFORE open. Ordering forced
    /// by first parking the awaiter synchronously through the actor
    /// (register + Task { awaitWaiter } + immediate cancel), then
    /// requesting `open()` on the actor after the cancel Task has already
    /// been queued. cancelWaiter drains via parkedX; open runs later on
    /// an already-empty queue.
    func testAsyncGateCancelBeforeOpen_bothCleanState() async {
        let gate = AsyncGate()
        let waiterToken = await gate.registerWaiter()
        let waiterTask = Task { await gate.awaitWaiter(waiterToken) }

        // Cancel first — schedules cancelWaiter Task on the actor mailbox
        // AFTER awaitWaiter's park (park runs synchronously in the
        // current actor turn).
        waiterTask.cancel()
        // H3: bounded actor ACK — block until cancelWaiter has dispatched
        // (drains the parked id as .cancelledWhileParked). Never strands on
        // the Task's own completion; close() also drains this ACK queue.
        await gate.waitForWaiterCancelCount(atLeast: 1)

        // Then open — waiterOrder is already empty because cancelWaiter
        // drained the id; open finds nothing to drain.
        await gate.open()

        // Pre-close intended-state proof (bounded snapshot read).
        let snap = await gate.snapshot()
        XCTAssertEqual(snap.parkedWaiterCount, 0)
        XCTAssertEqual(snap.waiterOrder, [])
        XCTAssertEqual(snap.completedWaiterCount, 0,
                       "cancel-then-open must not leave any completedWaiters latched")
        XCTAssertTrue(snap.opened)
        // Unconditional teardown, THEN drain the cancelled task.
        await gate.close()
        await waiterTask.value
    }

    /// Open/cancel race — open mailboxed BEFORE cancel. Ordering forced
    /// by explicitly `await`ing `open()` before creating the awaiter
    /// task; open latches the not-yet-awaited token, awaitWaiter picks
    /// up the latch on entry, cancelWaiter runs afterwards on a parked-
    /// empty gate and is a no-op.
    func testAsyncGateOpenBeforeCancel_bothCleanState() async {
        let gate = AsyncGate()
        let waiterToken = await gate.registerWaiter()

        await gate.open()   // drains waiterOrder → completedWaiters=[id]
        let afterOpen = await gate.snapshot()
        XCTAssertEqual(afterOpen.completedWaiterCount, 1)

        let waiterTask = Task { await gate.awaitWaiter(waiterToken) }
        waiterTask.cancel()
        // H3: `open()` above latched the not-yet-parked waiter
        // (afterOpen.completedWaiterCount == 1 — bounded pre-close proof).
        // Close UNCONDITIONALLY before awaiting so a latch regression fails
        // the post-close invariant instead of stranding the task.
        await gate.close()
        await waiterTask.value

        let snap = await gate.snapshot()
        XCTAssertEqual(snap.parkedWaiterCount, 0)
        XCTAssertEqual(snap.waiterOrder, [])
        XCTAssertEqual(snap.completedWaiterCount, 0,
                       "the latch must be gone AND late cancel must not relatch")
        XCTAssertTrue(snap.opened)
        XCTAssertEqual(snap.waiterResumeCounts[.parkedResumedByClose] ?? 0, 0,
                       "waiter took the latch path (never parked) — close rescued nothing")
    }

    /// Close/cancel race — cancel mailboxed BEFORE close.
    func testAsyncGateCancelBeforeClose_bothCleanState() async {
        let gate = AsyncGate()
        let waiterToken = await gate.registerWaiter()
        let observerToken = await gate.registerObserver()
        // registerObserver captured the pending signal emitted by
        // registerWaiter, so observer is already completed. Register
        // another observer that has no matching signal.
        let strandedObserver = await gate.registerObserver()

        let waiterTask = Task { await gate.awaitWaiter(waiterToken) }
        let observerTask = Task { await gate.awaitObserver(observerToken) }
        let strandedTask = Task { await gate.awaitObserver(strandedObserver) }

        waiterTask.cancel()
        strandedTask.cancel()

        // Hicks H3: close UNCONDITIONALLY before awaiting task values.
        // A regression in cancel-drain or entry-signal delivery now
        // fails an assertion rather than hanging.
        await gate.close()
        await waiterTask.value
        await strandedTask.value
        // The other observer completed via the pending signal on register
        // (bounded by register semantics before close ran).
        await observerTask.value

        let snap = await gate.snapshot()
        XCTAssertEqual(snap.parkedWaiterCount, 0)
        XCTAssertEqual(snap.parkedObserverCount, 0)
        XCTAssertEqual(snap.waiterOrder, [])
        XCTAssertEqual(snap.observerOrder, [])
        XCTAssertEqual(snap.completedWaiterCount, 0,
                       "no completedWaiters may remain after cancel-then-close")
        XCTAssertEqual(snap.completedObserverCount, 0,
                       "no completedObservers may remain after cancel-then-close")
        XCTAssertTrue(snap.closed)
    }

    /// Close/cancel race — close mailboxed BEFORE cancel. close() drains
    /// parked continuations; a later cancel finds nothing parked and is
    /// a no-op.
    func testAsyncGateCloseBeforeCancel_bothCleanState() async {
        let gate = AsyncGate()
        let waiterToken = await gate.registerWaiter()
        let observerToken = await gate.registerObserver()   // consumes pending signal
        let strandedObserver = await gate.registerObserver()

        let waiterTask = Task { await gate.awaitWaiter(waiterToken) }
        let strandedTask = Task { await gate.awaitObserver(strandedObserver) }
        let observerTask = Task { await gate.awaitObserver(observerToken) }

        // Force close to actor first.
        await gate.close()
        // Then cancel — everything is already drained; cancel is a no-op.
        waiterTask.cancel()
        strandedTask.cancel()
        observerTask.cancel()
        await waiterTask.value
        await strandedTask.value
        await observerTask.value

        let snap = await gate.snapshot()
        XCTAssertEqual(snap.parkedWaiterCount, 0)
        XCTAssertEqual(snap.parkedObserverCount, 0)
        XCTAssertEqual(snap.waiterOrder, [])
        XCTAssertEqual(snap.observerOrder, [])
        XCTAssertEqual(snap.completedWaiterCount, 0,
                       "close-then-late-cancel must not relatch waiter")
        XCTAssertEqual(snap.completedObserverCount, 0,
                       "close-then-late-cancel must not relatch observer")
        XCTAssertTrue(snap.closed)
    }

    /// Late cancel after resume — a Task that has already completed
    /// awaitX and been resumed via `signalEntry`/`open` receives a
    /// `.cancel()` afterwards. onCancel does not fire (handler scope
    /// exited); no state mutation occurs.
    func testAsyncGateLateCancelAfterResumeIsNoOp() async {
        let gate = AsyncGate()
        let observerToken = await gate.registerObserver()
        _ = await gate.registerWaiter()  // signals observer → completed latch
        let task = Task { await gate.awaitObserver(observerToken) }
        task.cancel()           // late/no-op — resume path installs no handler
        // H3: the observer was latched completed by the waiter signal above.
        // Close UNCONDITIONALLY before awaiting so a resume regression fails
        // the post-close invariant instead of stranding the task.
        await gate.close()
        await task.value

        let snap = await gate.snapshot()
        XCTAssertEqual(snap.parkedObserverCount, 0)
        XCTAssertEqual(snap.observerOrder, [])
        XCTAssertEqual(snap.completedObserverCount, 0)
        XCTAssertEqual(snap.observerResumeCounts[.parkedResumedByClose] ?? 0, 0,
                       "observer took the latch path (never parked) — close rescued nothing")
        XCTAssertTrue(snap.closed)
    }

    /// Multi-party FIFO: three observers registered in order, three
    /// waiters register-and-arrive in order → each waiter's entry signal
    /// wakes exactly one observer in the same order. No observer is
    /// signalled twice, no observer misses.
    func testAsyncGateMultiPartyOneSignalPerObserver() async {
        let gate = AsyncGate()
        let o1 = await gate.registerObserver()
        let o2 = await gate.registerObserver()
        let o3 = await gate.registerObserver()
        let regSnap = await gate.snapshot()
        XCTAssertEqual(regSnap.observerOrder, [o1.id, o2.id, o3.id],
                       "observer registration must preserve FIFO order")

        // First waiter: signals o1.
        let w1 = await gate.registerWaiter()
        let afterW1 = await gate.snapshot()
        XCTAssertEqual(afterW1.observerOrder, [o2.id, o3.id])
        XCTAssertEqual(afterW1.completedObserverCount, 1)

        // Second waiter: signals o2.
        let w2 = await gate.registerWaiter()
        let afterW2 = await gate.snapshot()
        XCTAssertEqual(afterW2.observerOrder, [o3.id])
        XCTAssertEqual(afterW2.completedObserverCount, 2)

        // Third waiter: signals o3.
        let w3 = await gate.registerWaiter()
        let afterW3 = await gate.snapshot()
        XCTAssertEqual(afterW3.observerOrder, [])
        XCTAssertEqual(afterW3.completedObserverCount, 3)
        XCTAssertEqual(afterW3.pendingEntrySignals, 0,
                       "one signal per observer — no leftover pending")

        // A fourth waiter with no observer would accumulate one pending
        // signal — proving one-signal-per-observer semantics.
        let w4 = await gate.registerWaiter()
        let afterW4 = await gate.snapshot()
        XCTAssertEqual(afterW4.pendingEntrySignals, 1)

        // Await all — must complete without hang.
        await gate.awaitObserver(o1)
        await gate.awaitObserver(o2)
        await gate.awaitObserver(o3)
        // Release the queued waiters via open.
        await gate.open()
        await gate.awaitWaiter(w1)
        await gate.awaitWaiter(w2)
        await gate.awaitWaiter(w3)
        await gate.awaitWaiter(w4)
        await gate.close()
    }

    /// Repeated open() with queued waiters: multiple waiters pending,
    /// open() drains them all on the first call; a second open() is a
    /// safe no-op.
    func testAsyncGateRepeatedOpenWithQueuedWaitersIsIdempotent() async {
        let gate = AsyncGate()
        let w1 = await gate.registerWaiter()
        let w2 = await gate.registerWaiter()
        let w3 = await gate.registerWaiter()

        let before = await gate.snapshot()
        XCTAssertEqual(before.waiterOrder, [w1.id, w2.id, w3.id])

        await gate.open()
        let afterOpen = await gate.snapshot()
        XCTAssertTrue(afterOpen.opened)
        XCTAssertEqual(afterOpen.waiterOrder, [], "all waiters drained")
        XCTAssertEqual(afterOpen.completedWaiterCount, 3)

        await gate.open()   // second open — no-op
        let afterOpen2 = await gate.snapshot()
        XCTAssertEqual(afterOpen2.completedWaiterCount, 3,
                       "second open must not double-latch")
        XCTAssertEqual(afterOpen2.waiterOrder, [])

        await gate.awaitWaiter(w1)
        await gate.awaitWaiter(w2)
        await gate.awaitWaiter(w3)
        await gate.close()
    }

    /// Coordinator item #4a: multi-party proof with ACTUALLY-PARKED
    /// observer continuations, not just token-order latching.
    ///
    /// Three observers register, each awaitObserver is launched in an
    /// unstructured task and structurally acknowledged as parked via
    /// waitForObserverParked BEFORE any waiter fires. Then three waiters
    /// register — each waiter's registerWaiter runs signalEntryLocked,
    /// which now MUST hit the parked-continuation branch and resume via
    /// `.signaledWhileParked` (not `.signaledBeforePark`). A helper
    /// regression that skipped parked resume would deadlock; drain-safe
    /// close() at the end still lets asserts fail rather than hang.
    func testAsyncGateMultiPartyResumesParkedObserverContinuations() async {
        let gate = AsyncGate()
        let o1 = await gate.registerObserver()
        let o2 = await gate.registerObserver()
        let o3 = await gate.registerObserver()

        // Park every observer FIRST, structurally acknowledged.
        let t1 = Task { await gate.awaitObserver(o1) }
        await gate.waitForObserverParked(o1)
        let t2 = Task { await gate.awaitObserver(o2) }
        await gate.waitForObserverParked(o2)
        let t3 = Task { await gate.awaitObserver(o3) }
        await gate.waitForObserverParked(o3)

        let parkedSnap = await gate.snapshot()
        XCTAssertEqual(parkedSnap.parkedObserverCount, 3,
                       "all three observers must be parked before any waiter fires")
        XCTAssertEqual(parkedSnap.observerFates.count, 0,
                       "no fate should be sealed while continuations are still parked")
        XCTAssertEqual(parkedSnap.pendingEntrySignals, 0)

        // Fire waiters one at a time; each MUST resume a parked observer
        // via the parked-continuation branch of signalEntryLocked.
        _ = await gate.registerWaiter()
        let after1 = await gate.snapshot()
        XCTAssertEqual(after1.parkedObserverCount, 2,
                       "first waiter must resume exactly one parked observer")
        XCTAssertEqual(after1.observerFateCounts[.signaledWhileParked] ?? 0, 1,
                       "first waiter must seal fate via signaledWhileParked, not signaledBeforePark")
        XCTAssertNil(after1.observerFateCounts[.signaledBeforePark],
                     "no observer may take the latch path — all were parked")

        _ = await gate.registerWaiter()
        let after2 = await gate.snapshot()
        XCTAssertEqual(after2.parkedObserverCount, 1)
        XCTAssertEqual(after2.observerFateCounts[.signaledWhileParked] ?? 0, 2)

        _ = await gate.registerWaiter()
        let after3 = await gate.snapshot()
        XCTAssertEqual(after3.parkedObserverCount, 0)
        XCTAssertEqual(after3.observerFateCounts[.signaledWhileParked] ?? 0, 3)
        XCTAssertEqual(after3.pendingEntrySignals, 0,
                       "one signal per parked observer — no leftover pending")
        XCTAssertNil(after3.observerFateCounts[.signaledBeforePark])

        // Every parked task must have resumed exactly once.
        // R3: unconditional close BEFORE `await t.value` so a
        // regression that failed to resume via signal fails the
        // `parkedResumedByClose == 0` assertion rather than hanging.
        await gate.close()
        await t1.value
        await t2.value
        await t3.value

        let final = await gate.snapshot()
        XCTAssertEqual(final.parkedObserverCount, 0)
        XCTAssertEqual(final.observerDuplicateAwaitCount, 0)
        XCTAssertEqual(final.observerUnknownAwaitCount, 0)
        XCTAssertEqual(final.observerCancelIgnoredCount, 0)
        // Hicks E: actual continuation resumes at the signal site.
        // Three parked observers → exactly three real .resume() calls
        // at the parkedResumedBySignal site. No latch consumptions.
        XCTAssertEqual(final.observerResumeCounts[.parkedResumedBySignal] ?? 0, 3,
                       "exactly three actual continuation resumes at signal site")
        XCTAssertEqual(final.observerResumeCounts[.latchConsumed] ?? 0, 0,
                       "no observer took the latch path")
        XCTAssertEqual(final.observerResumeCounts[.parkedResumedByClose] ?? 0, 0,
                       "R3: close ran after all three had already resumed via signal — no rescues")
        // Bounded post-close state assertions.
        XCTAssertEqual(final.observerPostCloseRegistrationCount, 0)
        XCTAssertEqual(final.observerParkAckQueueTotal, 0)
        XCTAssertEqual(final.observerCancelInvocationCount, 0)
    }

    /// Coordinator item #4b: repeated-open proof with ACTUALLY-PARKED
    /// waiter continuations, not just token-order draining.
    ///
    /// Three waiters register (each pushes a pending entry signal since
    /// no observers exist), then awaitWaiter is launched per waiter and
    /// structurally acknowledged as parked via waitForWaiterParked. The
    /// first open() MUST drain the parked-waiter branch (fates:
    /// `.openedWhileParked`); the second open() MUST be a no-op — no
    /// new resumes, no fate mutations, no double-drain.
    func testAsyncGateRepeatedOpenResumesParkedWaiterContinuations() async {
        let gate = AsyncGate()
        let w1 = await gate.registerWaiter()
        let w2 = await gate.registerWaiter()
        let w3 = await gate.registerWaiter()

        // Park every waiter FIRST, structurally acknowledged.
        let t1 = Task { await gate.awaitWaiter(w1) }
        await gate.waitForWaiterParked(w1)
        let t2 = Task { await gate.awaitWaiter(w2) }
        await gate.waitForWaiterParked(w2)
        let t3 = Task { await gate.awaitWaiter(w3) }
        await gate.waitForWaiterParked(w3)

        let parkedSnap = await gate.snapshot()
        XCTAssertEqual(parkedSnap.parkedWaiterCount, 3,
                       "all three waiters must be parked before open()")
        XCTAssertEqual(parkedSnap.waiterFates.count, 0,
                       "no fate should be sealed while continuations are still parked")

        // First open() must drain via the parked branch.
        await gate.open()
        let afterOpen1 = await gate.snapshot()
        XCTAssertTrue(afterOpen1.opened)
        XCTAssertEqual(afterOpen1.parkedWaiterCount, 0,
                       "first open() must resume every parked waiter")
        XCTAssertEqual(afterOpen1.waiterOrder, [])
        XCTAssertEqual(afterOpen1.waiterFateCounts[.openedWhileParked] ?? 0, 3,
                       "all three fates must be openedWhileParked (not openedBeforePark)")
        XCTAssertNil(afterOpen1.waiterFateCounts[.openedBeforePark],
                     "no waiter may take the latch path — all were parked")

        // Second open() must be a no-op — same counts, no state change.
        await gate.open()
        let afterOpen2 = await gate.snapshot()
        XCTAssertEqual(afterOpen2, afterOpen1,
                       "second open() must be a bit-identical no-op")

        // Every parked task must have resumed exactly once.
        // R3: unconditional close BEFORE `await t.value` — regression
        // in open() resumption would fail assertions rather than hang.
        await gate.close()
        await gate.open()   // second open post-close still no-op
        await t1.value
        await t2.value
        await t3.value

        let final = await gate.snapshot()
        XCTAssertEqual(final.parkedWaiterCount, 0)
        XCTAssertEqual(final.waiterDuplicateAwaitCount, 0)
        XCTAssertEqual(final.waiterUnknownAwaitCount, 0)
        XCTAssertEqual(final.waiterCancelIgnoredCount, 0)
        XCTAssertEqual(final.waiterFateCounts[.openedWhileParked] ?? 0, 3,
                       "fate counts must be frozen — no extra opened fates")
        // Hicks E: exactly three actual continuation resumes at the open
        // site — one per parked waiter. Second open() is a no-op; no
        // additional resumes.
        XCTAssertEqual(final.waiterResumeCounts[.parkedResumedByOpen] ?? 0, 3,
                       "exactly three actual continuation resumes at open site")
        XCTAssertEqual(final.waiterResumeCounts[.latchConsumed] ?? 0, 0,
                       "no waiter took the latch path")
        XCTAssertEqual(final.waiterResumeCounts[.parkedResumedByClose] ?? 0, 0,
                       "R3: close ran after all three had already resumed via open — no rescues")
        // Bounded post-close state.
        XCTAssertEqual(final.waiterPostCloseRegistrationCount, 0)
        XCTAssertEqual(final.waiterParkAckQueueTotal, 0)
        XCTAssertEqual(final.waiterCancelInvocationCount, 0)
    }

    /// Explicit teardown: close() drains BOTH pending waiters and pending
    /// entry observers exactly once, so a helper regression fails via a
    /// completed-but-different assertion rather than deadlocking on scope
    /// teardown.
    func testAsyncGateTeardownDrainsPendingWaitersAndObservers() async {
        let gate = AsyncGate()
        let observer = await gate.registerObserver()
        let waiter = await gate.registerWaiter()
        // At this point the waiter's signalEntry already resumed the
        // observer synchronously in-actor; register a fresh observer and
        // a fresh waiter that both need `close()` to drain.
        let pendingObserver = await gate.registerObserver()
        let extraWaiter = await gate.registerWaiter()

        // The extra waiter's registerWaiter also signals the pending
        // observer, so `pendingObserver` is already latched completed.
        // Register one more observer with no matching waiter so close()
        // has real work.
        let strandedObserver = await gate.registerObserver()

        let before = await gate.snapshot()
        XCTAssertEqual(before.waiterOrder.sorted(), [waiter.id, extraWaiter.id].sorted())
        XCTAssertEqual(before.observerOrder, [strandedObserver.id])

        await gate.close()
        let after = await gate.snapshot()
        XCTAssertTrue(after.closed)
        XCTAssertEqual(after.waiterOrder, [])
        XCTAssertEqual(after.observerOrder, [])
        XCTAssertEqual(after.parkedWaiterCount, 0)
        XCTAssertEqual(after.parkedObserverCount, 0)

        // Every previously issued token must be awaitable — none may
        // deadlock. Drain in unstructured tasks and await completion.
        let tasks: [Task<Void, Never>] = [
            Task { await gate.awaitObserver(observer) },
            Task { await gate.awaitWaiter(waiter) },
            Task { await gate.awaitObserver(pendingObserver) },
            Task { await gate.awaitWaiter(extraWaiter) },
            Task { await gate.awaitObserver(strandedObserver) },
        ]
        for t in tasks { await t.value }

        // Post-close registrations must also complete without hanging.
        let postCloseObserver = await gate.registerObserver()
        let postCloseWaiter = await gate.registerWaiter()
        await gate.awaitObserver(postCloseObserver)
        await gate.awaitWaiter(postCloseWaiter)
    }

    // MARK: - H1 duplicate/unknown await evidence

    /// Two concurrent `awaitObserver` calls with the SAME token: the
    /// second must NOT overwrite the first parked continuation. First
    /// resumes normally via `signaledWhileParked`; second is rejected
    /// with an immediate resume and bumps `observerDuplicateAwaitCount`.
    func testAsyncGateDuplicateAwaitObserverIsRejectedNoOverwrite() async {
        let gate = AsyncGate()
        let token = await gate.registerObserver()
        let first = Task { await gate.awaitObserver(token) }
        // Deterministically wait until the first await has parked.
        await gate.waitForObserverParked(token)

        // Second concurrent await for the same token — must be rejected
        // without overwriting the parked continuation.
        await gate.awaitObserver(token)

        let midSnap = await gate.snapshot()
        XCTAssertEqual(midSnap.parkedObserverCount, 1,
                       "duplicate await must not evict the parked continuation")
        XCTAssertEqual(midSnap.observerDuplicateAwaitCount, 1)
        XCTAssertNil(midSnap.observerFates[token.id],
                     "first await has not resumed yet")

        // Signal drains the parked continuation via signalEntry (piggybacked
        // on the waiter registration). signalEntry seals the fate
        // synchronously (aggregate-only), so the intended state is provable
        // via a bounded pre-close snapshot without awaiting the task.
        _ = await gate.registerWaiter()

        let snap = await gate.snapshot()
        XCTAssertEqual(snap.parkedObserverCount, 0)
        XCTAssertEqual(snap.observerOrder, [])
        XCTAssertEqual(snap.completedObserverCount, 0)
        XCTAssertEqual(snap.observerFateCounts[.signaledWhileParked] ?? 0, 1,
                       "exactly one signaledWhileParked resume for this token")
        XCTAssertEqual(snap.observerDuplicateAwaitCount, 1,
                       "duplicate await counter never rewinds")

        // A THIRD await after the fate is sealed is still rejected (direct
        // bounded actor call — not a Task, cannot strand).
        await gate.awaitObserver(token)
        let final = await gate.snapshot()
        XCTAssertEqual(final.observerDuplicateAwaitCount, 2)
        XCTAssertEqual(final.parkedObserverCount, 0)
        // H3: unconditional teardown, then drain the signal-resumed task.
        await gate.close()
        await first.value
    }

    /// Waiter analogue — second await for the same waiter token is
    /// rejected without overwriting the parked continuation. Resumed
    /// via `open()` → `openedWhileParked`.
    func testAsyncGateDuplicateAwaitWaiterIsRejectedNoOverwrite() async {
        let gate = AsyncGate()
        let token = await gate.registerWaiter()
        let first = Task { await gate.awaitWaiter(token) }
        await gate.waitForWaiterParked(token)

        await gate.awaitWaiter(token)
        let midSnap = await gate.snapshot()
        XCTAssertEqual(midSnap.parkedWaiterCount, 1)
        XCTAssertEqual(midSnap.waiterDuplicateAwaitCount, 1)
        XCTAssertNil(midSnap.waiterFates[token.id])

        await gate.open()

        // open() sealed `.openedWhileParked` synchronously (aggregate-only),
        // so the intended fate is provable via a bounded pre-close snapshot.
        let snap = await gate.snapshot()
        XCTAssertEqual(snap.parkedWaiterCount, 0)
        XCTAssertEqual(snap.waiterOrder, [])
        XCTAssertEqual(snap.completedWaiterCount, 0)
        XCTAssertEqual(snap.waiterFateCounts[.openedWhileParked] ?? 0, 1)
        XCTAssertEqual(snap.waiterDuplicateAwaitCount, 1)

        // H3: unconditional teardown BEFORE awaiting the resumed task. close()
        // prunes per-token maps but the aggregate fate count survives; a
        // regression that failed to resume drains as .closedWhileParked.
        await gate.close()
        await first.value

        // Post-completion late cancel is a bounded no-op — the task already
        // finished, so the cancellation handler scope has exited.
        first.cancel()
        let final = await gate.snapshot()
        XCTAssertEqual(final.waiterCancelIgnoredCount, 0,
                       "cancel of an already-completed Task fires no handler")
        XCTAssertEqual(final.waiterFateCounts[.openedWhileParked] ?? 0, 1,
                       "aggregate count must not grow from a late no-op cancel")
    }

    // MARK: - Vasquez remediation: duplicate-await cancellation must not
    // cross-own the original parked continuation (per-invocation awaitID).

    /// OBSERVER: original await parks and is structurally acknowledged.
    /// A duplicate observer await runs from a Task that is DETERMINISTICALLY
    /// already-cancelled when it enters `awaitObserver` (guaranteed by a
    /// separate barrier gate — no scheduler race). The duplicate hits the
    /// `parkedObservers[id] != nil` branch and is rejected; its onCancel
    /// spawns `cancelObserver(id: token.id, awaitID: <dupAwaitID>)`. The
    /// awaitID mismatch check in `cancelObserver` MUST short-circuit as a
    /// bounded no-op (`observerCancelIgnoredCount += 1`), leaving the
    /// original parked continuation intact with NO fate sealed. A later
    /// signal then resumes the original exactly once as `.signaledWhileParked`.
    ///
    /// Regression proof: without the awaitID tuple, `cancelObserver` would
    /// find `parkedObservers[id]` and drain the ORIGINAL — sealing fate
    /// `.cancelledWhileParked` and resuming the wrong continuation.
    func testAsyncGateDuplicateAwaitCancelDoesNotCrossOwnOriginalObserver() async {
        let gate = AsyncGate()
        let barrier = AsyncGate()
        let token = await gate.registerObserver()
        let origAttempt = ObserverAwaitAttempt()
        let dupAttempt = ObserverAwaitAttempt()

        // Original parks and is confirmed parked before any duplicate runs.
        let original = Task { await gate.awaitObserver(token, attempt: origAttempt) }
        await gate.waitForObserverParked(token)

        // Duplicate parks in the BARRIER first, gets cancelled while parked
        // there, and then enters `gate.awaitObserver` in an already-cancelled
        // state. No Task.yield/sleep — the parking + cancellation ordering
        // is proven by `waitForWaiterParked` on the barrier.
        let barrierToken = await barrier.registerWaiter()
        let dup = Task {
            await barrier.awaitWaiter(barrierToken)   // parks; cancelled here
            await gate.awaitObserver(token, attempt: dupAttempt)   // enters cancelled
        }
        await barrier.waitForWaiterParked(barrierToken)
        dup.cancel()
        // Hicks R1 causal ACK: block until the DUPLICATE's `cancelObserver`
        // has dispatched on `gate` (mismatched awaitID → bounded no-op).
        // This bounded actor ACK proves the cancel published BEFORE the
        // `mid` snapshot WITHOUT awaiting the stranding-capable `dup.value`;
        // close() also drains this ACK queue, so it cannot hang teardown.
        await gate.waitForObserverCancelCount(atLeast: 1)

        // After duplicate cancel dispatched, evaluate cross-ownership invariant.
        let mid = await gate.snapshot()
        XCTAssertEqual(mid.parkedObserverCount, 1,
                       "ORIGINAL must remain parked — duplicate cancel must not cross-own token id")
        XCTAssertEqual(mid.observerDuplicateAwaitCount, 1,
                       "duplicate await hit branch (2) exactly once")
        XCTAssertEqual(mid.observerCancelIgnoredCount, 1,
                       "duplicate cancel with mismatched awaitID must be bounded no-op")
        XCTAssertEqual(mid.observerCancelInvocationCount, 1,
                       "cancelObserver dispatched exactly once")
        XCTAssertNil(mid.observerFates[token.id],
                     "original must have NO fate — no .cancelledWhileParked leaked")
        XCTAssertEqual(mid.observerFateCounts[.cancelledWhileParked] ?? 0, 0,
                       "no cross-owned cancellation fate for any observer")
        XCTAssertEqual(mid.observerOrder, [token.id],
                       "original still in registration order")
        XCTAssertEqual(mid.completedObserverCount, 0,
                       "no relatch into completedObservers")

        // Signal → original resumes exactly once via .signaledWhileParked.
        _ = await gate.registerWaiter()

        // R3: unconditional close BOTH gates BEFORE awaiting original.value
        // so a regressed signal path fails an assertion instead of hanging.
        await gate.close()
        await barrier.close()
        await original.value

        // Now-bounded stranding awaits, after unconditional teardown of both
        // gates: the duplicate attempt's exact receipt (sealed at the cancel
        // dispatch proven above, so close cannot alter it) and the dup task
        // drain. A regression would be drained by close(), never stranded.
        await dup.value
        let dupOutcome = await dupAttempt.outcome()
        XCTAssertEqual(dupOutcome, .cancelled(.processedIgnoredMismatch),
                       "duplicate cancel with mismatched awaitID must be bounded no-op")

        let final = await gate.snapshot()
        XCTAssertEqual(final.parkedObserverCount, 0)
        XCTAssertEqual(final.observerOrder, [])
        XCTAssertEqual(final.observerFateCounts[.signaledWhileParked] ?? 0, 1,
                       "original must resume via signal, not cross-owned cancel")
        XCTAssertEqual(final.observerFateCounts[.cancelledWhileParked] ?? 0, 0,
                       "no cancelledWhileParked fate must have been sealed for this token")
        XCTAssertEqual(final.observerResumeCounts[.parkedResumedByClose] ?? 0, 0,
                       "R3: signal already resumed original — close must not have rescued it")
        XCTAssertEqual(final.observerDuplicateAwaitCount, 1)
        XCTAssertEqual(final.observerCancelIgnoredCount, 1,
                       "cancel-ignored counter never rewinds")
        XCTAssertEqual(final.observerUnknownAwaitCount, 0)

        // Original attempt was never cancelled — completed naturally.
        // Coordinator remediation: use authoritative buffered outcome()
        // API. `outcome()` is guaranteed to resolve because natural
        // completion path publishes `.finishedBeforeProcessing` via the
        // state gate; buffered latch means this await is bounded.
        let origOutcome = await origAttempt.outcome()
        XCTAssertEqual(origOutcome, .finishedBeforeProcessing,
                       "original attempt: natural completion → buffered outcome is finishedBeforeProcessing")
        XCTAssertEqual(origAttempt.stateForTest, .completedNaturally,
                       "state machine committed natural path — no cancel Task was launched")
    }

    /// WAITER analogue: identical structure, mirrored types. Original
    /// waiter parks; duplicate barrier-gated + already-cancelled at entry
    /// hits the `parkedWaiters[id] != nil` branch, its cancel fires with
    /// mismatched awaitID → `waiterCancelIgnoredCount += 1`. Original
    /// stays parked and later resumes via `open()` as `.openedWhileParked`.
    func testAsyncGateDuplicateAwaitCancelDoesNotCrossOwnOriginalWaiter() async {
        let gate = AsyncGate()
        let barrier = AsyncGate()
        let token = await gate.registerWaiter()
        let origAttempt = WaiterAwaitAttempt()
        let dupAttempt = WaiterAwaitAttempt()

        let original = Task { await gate.awaitWaiter(token, attempt: origAttempt) }
        await gate.waitForWaiterParked(token)

        let barrierToken = await barrier.registerWaiter()
        let dup = Task {
            await barrier.awaitWaiter(barrierToken)
            await gate.awaitWaiter(token, attempt: dupAttempt)
        }
        await barrier.waitForWaiterParked(barrierToken)
        dup.cancel()
        // Hicks R1 causal ACK: block until the DUPLICATE's `cancelWaiter`
        // has dispatched on `gate` (mismatched awaitID → bounded no-op).
        // Bounded actor ACK proving the cancel published BEFORE `mid` without
        // awaiting the stranding-capable `dup.value`; close() drains it too.
        await gate.waitForWaiterCancelCount(atLeast: 1)

        let mid = await gate.snapshot()
        XCTAssertEqual(mid.parkedWaiterCount, 1,
                       "ORIGINAL waiter must remain parked")
        XCTAssertEqual(mid.waiterDuplicateAwaitCount, 1)
        XCTAssertEqual(mid.waiterCancelIgnoredCount, 1,
                       "mismatched-awaitID duplicate cancel must be bounded no-op")
        XCTAssertEqual(mid.waiterCancelInvocationCount, 1,
                       "cancelWaiter dispatched exactly once")
        XCTAssertNil(mid.waiterFates[token.id],
                     "original must have NO fate")
        XCTAssertEqual(mid.waiterFateCounts[.cancelledWhileParked] ?? 0, 0)
        XCTAssertEqual(mid.waiterOrder, [token.id])
        XCTAssertEqual(mid.completedWaiterCount, 0)

        await gate.open()
        // R3: close both gates BEFORE `await original.value` — a
        // regressed open() path fails an assertion, never hangs.
        await gate.close()
        await barrier.close()
        await original.value

        // Now-bounded stranding awaits, after unconditional teardown of both
        // gates: duplicate attempt receipt (sealed at cancel dispatch above)
        // and the dup task drain. A regression is drained by close().
        await dup.value
        let dupOutcome = await dupAttempt.outcome()
        XCTAssertEqual(dupOutcome, .cancelled(.processedIgnoredMismatch))

        let final = await gate.snapshot()
        XCTAssertEqual(final.parkedWaiterCount, 0)
        XCTAssertEqual(final.waiterOrder, [])
        XCTAssertEqual(final.waiterFateCounts[.openedWhileParked] ?? 0, 1,
                       "original must resume via open, not cross-owned cancel")
        XCTAssertEqual(final.waiterFateCounts[.cancelledWhileParked] ?? 0, 0)
        XCTAssertEqual(final.waiterResumeCounts[.parkedResumedByClose] ?? 0, 0,
                       "R3: open already resumed original — close must not have rescued it")
        XCTAssertEqual(final.waiterDuplicateAwaitCount, 1)
        XCTAssertEqual(final.waiterCancelIgnoredCount, 1)
        XCTAssertEqual(final.waiterUnknownAwaitCount, 0)

        let origWaiterOutcome = await origAttempt.outcome()
        XCTAssertEqual(origWaiterOutcome, .finishedBeforeProcessing,
                       "original waiter completed naturally — buffered outcome finishedBeforeProcessing")
        XCTAssertEqual(origAttempt.stateForTest, .completedNaturally)
    }

    /// Unknown token: `awaitObserver`/`awaitWaiter` with an id that was
    /// never registered on this gate returns immediately as a bounded
    /// no-op and bumps the unknown-await counter. No state mutation.
    func testAsyncGateUnknownAwaitIsBoundedNoOp() async {
        let gate = AsyncGate()
        // Fabricate tokens for ids that no register* call ever issued.
        let bogusObserver = AsyncGate.ObserverToken(id: 9_999)
        let bogusWaiter = AsyncGate.WaiterToken(id: 10_000)

        await gate.awaitObserver(bogusObserver)
        await gate.awaitWaiter(bogusWaiter)

        let snap = await gate.snapshot()
        XCTAssertEqual(snap.observerUnknownAwaitCount, 1)
        XCTAssertEqual(snap.waiterUnknownAwaitCount, 1)
        XCTAssertEqual(snap.parkedObserverCount, 0)
        XCTAssertEqual(snap.parkedWaiterCount, 0)
        XCTAssertEqual(snap.observerOrder, [])
        XCTAssertEqual(snap.waiterOrder, [])
        XCTAssertEqual(snap.completedObserverCount, 0)
        XCTAssertEqual(snap.completedWaiterCount, 0)
        XCTAssertNil(snap.observerFates[bogusObserver.id])
        XCTAssertNil(snap.waiterFates[bogusWaiter.id])
        await gate.close()
    }

    // MARK: - H2 closed-gate signal leak evidence

    /// A waiter registered before any observer accumulates one
    /// `pendingEntrySignals`. After `close()`, that pending signal must
    /// be cleared and NEVER re-emerge as an observed side-effect.
    func testAsyncGateCloseClearsPendingEntrySignals() async {
        let gate = AsyncGate()
        _ = await gate.registerWaiter()
        let before = await gate.snapshot()
        XCTAssertEqual(before.pendingEntrySignals, 1,
                       "waiter before observer accumulates a pending signal")

        await gate.close()
        let after = await gate.snapshot()
        XCTAssertEqual(after.pendingEntrySignals, 0,
                       "close() must clear pendingEntrySignals")
        XCTAssertTrue(after.closed)
    }

    /// After close, `registerWaiter` must NOT emit an entry signal (H2).
    /// If it did, `pendingEntrySignals` would grow unboundedly with no
    /// consumer since every post-close observer latches on registration.
    ///
    /// Hicks B: post-close registrations now bump bounded aggregate
    /// counters ONLY — they must NOT insert per-token fates/completed
    /// entries. Repeated post-close registrations keep every active
    /// per-token map at zero.
    func testAsyncGatePostCloseWaitersDoNotEmitEntrySignals() async {
        let gate = AsyncGate()
        await gate.close()
        for _ in 0..<10 { _ = await gate.registerWaiter() }
        for _ in 0..<10 { _ = await gate.registerObserver() }

        let snap = await gate.snapshot()
        XCTAssertEqual(snap.pendingEntrySignals, 0,
                       "no post-close waiter may accumulate an entry signal")
        XCTAssertEqual(snap.waiterOrder, [])
        XCTAssertEqual(snap.observerOrder, [])
        XCTAssertEqual(snap.parkedWaiterCount, 0)
        XCTAssertEqual(snap.parkedObserverCount, 0)
        // Hicks B: aggregate counters, not per-token maps.
        XCTAssertEqual(snap.waiterPostCloseRegistrationCount, 10)
        XCTAssertEqual(snap.observerPostCloseRegistrationCount, 10)
        XCTAssertEqual(snap.waiterFateCounts[.closedBeforePark] ?? 0, 0,
                       "post-close register must not seal per-token fates")
        XCTAssertEqual(snap.observerFateCounts[.closedBeforePark] ?? 0, 0,
                       "post-close register must not seal per-token fates")
        XCTAssertTrue(snap.waiterFates.isEmpty)
        XCTAssertTrue(snap.observerFates.isEmpty)
        XCTAssertEqual(snap.completedWaiterCount, 0)
        XCTAssertEqual(snap.completedObserverCount, 0)
    }

    // MARK: - H3 explicit acknowledgment / interleaving evidence

    /// The park-ACK is lost-wakeup-safe: calling `waitForXParked` AFTER
    /// the token has already parked returns immediately. Calling it
    /// BEFORE resolves once park happens.
    func testAsyncGateWaitForParkedIsLostWakeupSafe() async {
        let gate = AsyncGate()

        // Post-park case — ACK returns immediately.
        let token1 = await gate.registerObserver()
        let task1 = Task { await gate.awaitObserver(token1) }
        await gate.waitForObserverParked(token1)   // waits for park
        await gate.waitForObserverParked(token1)   // already parked → immediate
        _ = await gate.registerWaiter()
        // signalEntry sealed token1 synchronously (aggregate-only); prove the
        // resume via a bounded snapshot. task1.value is deferred to after the
        // unconditional close so a lost-wakeup regression fails an assertion
        // instead of stranding this await.
        let snap1 = await gate.snapshot()
        XCTAssertEqual(snap1.observerFateCounts[.signaledWhileParked] ?? 0, 1,
                       "token1 resumed exactly once via signalEntry")

        // Pre-park case — ACK enqueues, resolves after park.
        let token2 = await gate.registerObserver()
        async let ack: AsyncGate.ParkAckResult = gate.waitForObserverParked(token2)
        let task2 = Task { await gate.awaitObserver(token2) }
        let ackResult = await ack
        XCTAssertEqual(ackResult, AsyncGate.ParkAckResult.parked)
        _ = await gate.registerWaiter()
        let snap2 = await gate.snapshot()
        XCTAssertEqual(snap2.observerFateCounts[.signaledWhileParked] ?? 0, 2,
                       "token2 resumed exactly once via signalEntry")

        // Fate-resolved-before-ever-parked case — ACK completes.
        let token3 = await gate.registerObserver()
        _ = await gate.registerWaiter()             // seals fate signaledBeforePark
        await gate.waitForObserverParked(token3)   // fate exists → immediate

        let snap = await gate.snapshot()
        XCTAssertEqual(snap.parkedObserverCount, 0)
        XCTAssertEqual(snap.observerOrder, [])
        XCTAssertEqual(snap.completedObserverCount, 1,
                       "token3 latched but never awaited; consumed below")
        await gate.awaitObserver(token3)
        let final = await gate.snapshot()
        XCTAssertEqual(final.completedObserverCount, 0)
        XCTAssertEqual(final.observerFateCounts[.signaledBeforePark] ?? 0, 1)
        XCTAssertEqual(final.observerResumeCounts[.latchConsumed] ?? 0, 1,
                       "await consumed the latch")
        // Unconditional teardown, then drain the two signal-resumed tasks.
        // A lost-wakeup regression is drained here as .closedWhileParked
        // rather than stranding these awaits.
        await gate.close()
        await task1.value
        await task2.value
    }

    /// Open-wins-then-late-cancel: park a waiter, run open(), then
    /// cancel — cancel must be a bounded no-op. Fate is
    /// `openedWhileParked`.
    func testAsyncGateOpenWinsThenLateCancelIsBoundedNoOp() async {
        let gate = AsyncGate()
        let token = await gate.registerWaiter()
        let task = Task { await gate.awaitWaiter(token) }
        await gate.waitForWaiterParked(token)   // proof: parked

        await gate.open()                        // open wins
        // open() sealed .openedWhileParked synchronously (aggregate + per-token
        // pruned to the resume path); prove via a bounded snapshot before the
        // deferred task drain.
        let mid = await gate.snapshot()
        XCTAssertEqual(mid.parkedWaiterCount, 0)
        XCTAssertEqual(mid.waiterFateCounts[.openedWhileParked] ?? 0, 1)
        // Unconditional teardown BEFORE draining the open-resumed task, so an
        // open regression is drained by close() rather than stranding.
        await gate.close()
        await task.value                         // resumed by open; bounded post-close
        task.cancel()                            // late — handler no longer active

        let snap = await gate.snapshot()
        XCTAssertEqual(snap.parkedWaiterCount, 0)
        XCTAssertEqual(snap.waiterOrder, [])
        XCTAssertEqual(snap.completedWaiterCount, 0)
        XCTAssertEqual(snap.waiterFateCounts[.openedWhileParked] ?? 0, 1)
        XCTAssertEqual(snap.waiterCancelIgnoredCount, 0,
                       "cancel on a completed Task does not fire the handler")
    }

    /// Cancel-wins-then-open: park a waiter, cancel first, THEN open.
    /// cancelWaiter drains parked; subsequent open() finds waiterOrder
    /// empty and does not relatch. Fate is `cancelledWhileParked`.
    func testAsyncGateCancelWinsThenOpenIsBoundedNoOp() async {
        let gate = AsyncGate()
        let token = await gate.registerWaiter()
        let task = Task { await gate.awaitWaiter(token) }
        await gate.waitForWaiterParked(token)   // proof: parked

        task.cancel()                            // schedules cancelWaiter
        // Bounded actor ACK: cancelWaiter dispatched and sealed
        // .cancelledWhileParked synchronously within the actor call. Proves
        // the fate WITHOUT awaiting the stranding-capable task.value; close()
        // also drains this ACK queue so it cannot hang teardown.
        await gate.waitForWaiterCancelCount(atLeast: 1)
        await gate.open()                        // no work to do

        let snap = await gate.snapshot()
        XCTAssertEqual(snap.parkedWaiterCount, 0)
        XCTAssertEqual(snap.waiterOrder, [])
        XCTAssertEqual(snap.completedWaiterCount, 0,
                       "open after cancel must not relatch a cancelled id")
        XCTAssertEqual(snap.waiterFateCounts[.cancelledWhileParked] ?? 0, 1)
        XCTAssertTrue(snap.opened)
        // Unconditional teardown, then drain the cancelled task.
        await gate.close()
        await task.value                         // bounded post-close
    }

    /// Close-with-actually-parked continuations for BOTH token types.
    /// Uses park-ACKs to prove both are parked before close() runs.
    /// Both fates must be `closedWhileParked`; no completedX remnants.
    func testAsyncGateCloseDrainsActuallyParkedContinuations() async {
        let gate = AsyncGate()
        let waiterToken = await gate.registerWaiter()
        let observerToken = await gate.registerObserver()
        // registerObserver above may have consumed pendingEntrySignals=1
        // from registerWaiter's signal — verify state.
        let pre = await gate.snapshot()
        // Actually the observer was registered AFTER the waiter's signal
        // arrived, so pendingEntrySignals was 1 and the observer latches
        // as signaledBeforePark. Register a fresh observer that has no
        // pending signal to consume, and a fresh waiter that will park
        // (needs an observer that's been signaled or opened=false).
        _ = pre
        let parkedObserverToken = await gate.registerObserver()

        let waiterTask = Task { await gate.awaitWaiter(waiterToken) }
        let observerTask = Task { await gate.awaitObserver(parkedObserverToken) }
        let alreadyLatchedObserverTask = Task { await gate.awaitObserver(observerToken) }

        // Explicit park proof for the two that will actually park.
        await gate.waitForWaiterParked(waiterToken)
        await gate.waitForObserverParked(parkedObserverToken)

        await gate.close()

        // Every spawned task must complete without hanging.
        await waiterTask.value
        await observerTask.value
        await alreadyLatchedObserverTask.value

        let snap = await gate.snapshot()
        XCTAssertEqual(snap.parkedWaiterCount, 0)
        XCTAssertEqual(snap.parkedObserverCount, 0)
        XCTAssertEqual(snap.waiterOrder, [])
        XCTAssertEqual(snap.observerOrder, [])
        XCTAssertEqual(snap.completedWaiterCount, 0)
        XCTAssertEqual(snap.completedObserverCount, 0)
        XCTAssertEqual(snap.waiterFateCounts[.closedWhileParked] ?? 0, 1)
        XCTAssertEqual(snap.observerFateCounts[.closedWhileParked] ?? 0, 1)
        XCTAssertEqual(snap.observerFateCounts[.signaledBeforePark] ?? 0, 1,
                       "aggregate must preserve the prior latch even after close pruning")
        XCTAssertTrue(snap.closed)
        XCTAssertEqual(snap.pendingEntrySignals, 0)
    }

    /// Convenience API: `wait()` before `waitForEntry()`. Uses the split
    /// API to obtain a token and prove ordering deterministically via
    /// `waitForWaiterParked`, then exercises the convenience `waitForEntry`.
    func testAsyncGateConvenienceWaiterBeforeObserver_deterministic() async {
        let gate = AsyncGate()
        let waiterToken = await gate.registerWaiter()
        let waiterTask = Task { await gate.awaitWaiter(waiterToken) }
        await gate.waitForWaiterParked(waiterToken)

        // At this point pendingEntrySignals=1 (waiter emitted a signal
        // before any observer registered), and the waiter is parked.
        let mid = await gate.snapshot()
        XCTAssertEqual(mid.pendingEntrySignals, 1)
        XCTAssertEqual(mid.parkedWaiterCount, 1)

        // Convenience observer consumes the pending signal immediately.
        await gate.waitForEntry()
        await gate.open()
        // open() sealed the parked waiter as .openedWhileParked synchronously;
        // prove via a bounded snapshot before the deferred task drain.
        let final = await gate.snapshot()
        XCTAssertEqual(final.pendingEntrySignals, 0)
        XCTAssertEqual(final.parkedWaiterCount, 0)
        XCTAssertEqual(final.waiterFateCounts[.openedWhileParked] ?? 0, 1)
        // Unconditional teardown, then drain the open-resumed waiter task.
        await gate.close()
        await waiterTask.value
    }

    /// Convenience API: observer parks first via the split API (proof
    /// via park-ACK), then a `wait()` waiter signals it. Deterministic
    /// ordering; no snapshot fence needed.
    func testAsyncGateConvenienceObserverBeforeWaiter_deterministic() async {
        let gate = AsyncGate()
        let observerToken = await gate.registerObserver()
        let observerTask = Task { await gate.awaitObserver(observerToken) }
        await gate.waitForObserverParked(observerToken)

        let mid = await gate.snapshot()
        XCTAssertEqual(mid.parkedObserverCount, 1)
        XCTAssertEqual(mid.pendingEntrySignals, 0)

        // Convenience waiter signals the parked observer immediately. Use the
        // split API — which is exactly the inlined body of `wait()`
        // (registerWaiter + awaitWaiter) — so the entry signal that resumes
        // the parked observer is provable via a bounded park-ACK instead of
        // awaiting the stranding-capable observerTask.value.
        let waiterToken = await gate.registerWaiter()
        let waiterTask = Task { await gate.awaitWaiter(waiterToken) }
        await gate.waitForWaiterParked(waiterToken)
        // registerWaiter's signalEntry sealed the observer's
        // .signaledWhileParked synchronously; prove via a bounded snapshot.
        let afterSignal = await gate.snapshot()
        XCTAssertEqual(afterSignal.observerFateCounts[.signaledWhileParked] ?? 0, 1)
        XCTAssertEqual(afterSignal.parkedObserverCount, 0)

        await gate.open()

        let final = await gate.snapshot()
        XCTAssertEqual(final.observerFateCounts[.signaledWhileParked] ?? 0, 1)
        XCTAssertEqual(final.parkedObserverCount, 0)
        XCTAssertEqual(final.pendingEntrySignals, 0)
        // Unconditional teardown, then drain both resumed tasks.
        await gate.close()
        await observerTask.value
        await waiterTask.value
    }

    /// Convenience-API integration: the shipping `wait()` / `waitForEntry()`
    /// still exhibit the exact-order behaviour that the production tests
    /// depend on.
    func testAsyncGateConvenienceEntryObserverBeforeWaitResumesOnce() async {
        let gate = AsyncGate()
        let observerTask = Task { await gate.waitForEntry() }
        let waiterTask = Task { await gate.wait() }
        await observerTask.value        // resumed by the wait's entry signal
        await gate.open()
        await waiterTask.value
        await gate.close()
    }

    func testAsyncGateConvenienceWaitBeforeEntryObserverIsObserved() async {
        let gate = AsyncGate()
        let waiterTask = Task { await gate.wait() }
        // Force `waiterTask` to reach the actor by awaiting an unrelated
        // actor call — since the actor is a serial mailbox, this snapshot
        // synchronously drains any prior mailbox items on the actor
        // executor. No polling.
        _ = await gate.snapshot()
        await gate.waitForEntry()       // consumes the pending signal
        await gate.open()
        await waiterTask.value
        await gate.close()
    }

    // MARK: - Hicks A: bounded park-ACK results

    /// Hicks A: `waitForObserverParked` on a fabricated (never-issued)
    /// token must return immediately as `.unknown` — must NOT queue.
    func testAsyncGateParkAckUnknownObserverTokenIsBounded() async {
        let gate = AsyncGate()
        let fake = AsyncGate.ObserverToken(id: 99_999)
        let result = await gate.waitForObserverParked(fake)
        XCTAssertEqual(result, .unknown)
        let snap = await gate.snapshot()
        XCTAssertEqual(snap.observerParkAckQueueTotal, 0)
        await gate.close()
    }

    /// Hicks A: `waitForWaiterParked` on a fabricated token → `.unknown`.
    func testAsyncGateParkAckUnknownWaiterTokenIsBounded() async {
        let gate = AsyncGate()
        let fake = AsyncGate.WaiterToken(id: 99_999)
        let result = await gate.waitForWaiterParked(fake)
        XCTAssertEqual(result, .unknown)
        let snap = await gate.snapshot()
        XCTAssertEqual(snap.waiterParkAckQueueTotal, 0)
        await gate.close()
    }

    /// Bishop F1: after close(), a post-close registered token yields
    /// `.closedOrConsumed` from park-ACK (no map insertion, no queueing).
    /// Only `id >= nextID` (fabricated / never-issued) yields `.unknown`.
    func testAsyncGateParkAckPostCloseTokenIsClosedOrConsumed() async {
        let gate = AsyncGate()
        await gate.close()
        let obs = await gate.registerObserver()
        let wat = await gate.registerWaiter()
        let obsAck = await gate.waitForObserverParked(obs)
        let watAck = await gate.waitForWaiterParked(wat)
        XCTAssertEqual(obsAck, .closedOrConsumed)
        XCTAssertEqual(watAck, .closedOrConsumed)
        let snap = await gate.snapshot()
        XCTAssertEqual(snap.observerParkAckQueueTotal, 0)
        XCTAssertEqual(snap.waiterParkAckQueueTotal, 0)
    }

    /// Hicks A: two ACK callers before park both resume exactly once
    /// (with `.parked`) once the token parks.
    func testAsyncGateParkAckTwoCallersBeforeParkBothResolve() async {
        let gate = AsyncGate()
        let token = await gate.registerObserver()

        // Coordinator R3: use the actor-synchronous ticket API. Both
        // tickets are queue-inserted BEFORE any parking task runs;
        // `isResolved` is a synchronous non-stranding property.
        let ticket1 = await gate.enterObserverParkAck(token)
        let ticket2 = await gate.enterObserverParkAck(token)
        XCTAssertFalse(ticket1.isResolved, "ticket 1 not yet resolved (no park)")
        XCTAssertFalse(ticket2.isResolved, "ticket 2 not yet resolved (no park)")
        let pre = await gate.snapshot()
        XCTAssertEqual(pre.observerParkAckTicketCount, 2,
                       "both tickets actor-recorded before spawn")

        let t = Task { await gate.awaitObserver(token) }

        // Hicks H3: close UNCONDITIONALLY before awaiting any ticket or
        // task value. Ticket buffer is guaranteed resolved (park OR
        // close drains it); both awaits are then bounded.
        await gate.close()
        let r1 = await ticket1.value()
        let r2 = await ticket2.value()
        await t.value

        // Exact result set: either park won the race (both `.parked`)
        // or close won (both drained via close observerOrder-loop's
        // else branch, seal `.closedBeforePark`, flushed to tickets).
        // No other bounded classification is reachable here:
        // - `.terminal(.closedWhileParked)` is impossible because ticket
        //   is resolved `.parked` at park time and is one-shot; the
        //   subsequent close's seal-flush is a no-op on the ticket.
        // - `.closedOrConsumed` / `.unknown` cannot appear for a
        //   registered, active-until-close token entered pre-spawn.
        let expected: Set<AsyncGate.ParkAckResult> = [.parked, .terminal(.closedBeforePark)]
        XCTAssertTrue(expected.contains(r1),
                      "ticket 1 exact receipt must be .parked or .terminal(.closedBeforePark); got \(r1)")
        XCTAssertTrue(expected.contains(r2),
                      "ticket 2 exact receipt must be .parked or .terminal(.closedBeforePark); got \(r2)")
        // Both tickets must resolve to the SAME classification — they
        // observed the same actor timeline.
        XCTAssertEqual(r1, r2, "tickets share timeline; must agree")

        let final = await gate.snapshot()
        XCTAssertEqual(final.parkedObserverCount, 0)
        XCTAssertEqual(final.observerParkAckTicketCount, 0,
                       "all tickets removed at resolution")
    }

    /// Hicks A / reviewer finding #5: ACK call AFTER park returns
    /// immediately `.parked` — proven CAUSALLY (not via a race). The
    /// prior version raced the ticket enter against the park schedule
    /// and accepted any bounded classification, which meant a regression
    /// that failed to resolve tickets on park would still pass because
    /// close-drain would produce a bounded value. This version:
    ///
    ///   1. Spawns the parking task
    ///   2. Awaits `waitForObserverParked(token)` — synchronous causal
    ///      proof that the continuation is parked BEFORE we enter the
    ///      ticket
    ///   3. Enters the ticket. Because the token is parked, the
    ///      `enterObserverParkAck` `parkedObservers[token.id] != nil`
    ///      branch fires and the ticket is `.resolve(.parked)`d
    ///      SYNCHRONOUSLY on the actor — `isResolved` MUST be true
    ///   4. Asserts `ticket.value()` is exactly `.parked` (no OR)
    ///
    /// If `enterObserverParkAck`'s parked-branch is removed or its
    /// resolution reason regresses, this test fails deterministically.
    func testAsyncGateParkAckAfterParkIsImmediate() async {
        let gate = AsyncGate()
        let token = await gate.registerObserver()
        let t = Task { await gate.awaitObserver(token) }

        // Causal park proof: block until the parking task has actually
        // installed the continuation. `waitForObserverParked` returns
        // `.parked` when `parkedObservers[token.id] != nil` is true.
        let parkAck = await gate.waitForObserverParked(token)
        XCTAssertEqual(parkAck, .parked,
                       "waitForObserverParked must observe the continuation parked before we enter the ticket")

        // Enter ticket AFTER parked-proof. `enterObserverParkAck` runs
        // synchronously on the actor: its `parkedObservers[token.id] != nil`
        // branch calls `ticket.resolve(.parked)` and returns. The ticket
        // MUST be resolved on return; `.value()` MUST be exactly `.parked`.
        let ticket = await gate.enterObserverParkAck(token)
        XCTAssertTrue(ticket.isResolved,
                      "ticket entered against a parked token must be resolved synchronously by enterObserverParkAck")

        let r = await ticket.value()
        XCTAssertEqual(r, .parked,
                       "ticket entered after park must resolve exactly .parked; got \(r)")

        // Close for bounded teardown; drains the parked continuation via
        // .closedWhileParked (does not affect the already-resolved ticket).
        await gate.close()
        await t.value

        let final = await gate.snapshot()
        XCTAssertEqual(final.parkedObserverCount, 0)
        XCTAssertEqual(final.observerParkAckTicketCount, 0)
        // Exact resume-count evidence: the parked continuation was
        // resumed by close (not by cancel or signal).
        XCTAssertEqual(final.observerResumeCounts[.parkedResumedByClose] ?? 0, 1,
                       "exactly one parkedResumedByClose from the terminal drain")
        XCTAssertEqual(final.observerFateCounts[.closedWhileParked] ?? 0, 1,
                       "fate sealed exactly .closedWhileParked")
    }

    /// Hicks A: pre-park ACK on an active token is drained by close()
    /// with `.closedOrConsumed` — never hangs.
    func testAsyncGateParkAckResolvedByCloseBeforePark() async {
        let gate = AsyncGate()
        let token = await gate.registerObserver()
        async let ack: AsyncGate.ParkAckResult = gate.waitForObserverParked(token)
        // Close before any awaitObserver ever parks. The queued ACK
        // must be drained by close() with `.closedOrConsumed`.
        await gate.close()
        let r = await ack
        // Registered-before-close tokens go through the fate path when
        // close() drains observerOrder, so the ACK may see either
        // .terminal(.closedBeforePark) (if seal-flush hit it first) or
        // .closedOrConsumed (if the direct drain-loop hit it first).
        // Both outcomes are bounded and non-hanging.
        XCTAssertTrue(r == .closedOrConsumed || r == .terminal(.closedBeforePark),
                      "unexpected ACK result: \(r)")
        let snap = await gate.snapshot()
        XCTAssertEqual(snap.observerParkAckQueueTotal, 0)
    }

    // MARK: - Hicks R1/R2 (redesign): caller-owned attempt + ticket proofs
    //
    // These tests use:
    //   - `ObserverAwaitAttempt` / `WaiterAwaitAttempt` — caller-owned
    //     per-attempt cancellation context. The state gate atomically
    //     commits to either `.completedNaturally` or `.cancellationInitiated`;
    //     whichever wins is the sole publisher of the buffered outcome.
    //     Consumers await `attempt.outcome()` for the definitive
    //     per-attempt receipt. The gate stores NO per-attempt receipt map.
    //   - `enterObserverParkAck(_) -> ObserverParkAckTicket` — caller-
    //     owned one-shot buffered ticket. Actor calls `resolve(_)` at
    //     park/fate/close and removes the ticket entry immediately.
    //     Consumers call `ticket.value()` at any later time; never-
    //     awaited tickets are simply dropped by the caller.

    /// Hicks R2: two callers register park-ACK ticket objects BEFORE
    /// the parking task is spawned. Because `enterObserverParkAck`
    /// runs synchronously on the actor, both tickets are recorded in
    /// `observerActiveParkAckTickets[token.id]` before returning; a
    /// snapshot taken pre-spawn proves `observerParkAckTicketCount==2`.
    /// After the parking task actually parks, both tickets resolve
    /// `.parked` exactly once and every per-token map returns to zero.
    func testAsyncGateR2ParkAckTicketsQueuedBeforeSpawn_observer() async {
        let gate = AsyncGate()
        let token = await gate.registerObserver()

        // Phase 1: synchronously enter two tickets. Neither is resolved
        // yet because the token is active but has no parked continuation.
        let ticket1 = await gate.enterObserverParkAck(token)
        let ticket2 = await gate.enterObserverParkAck(token)
        XCTAssertFalse(ticket1.isResolved, "ticket 1 must not resolve before park")
        XCTAssertFalse(ticket2.isResolved, "ticket 2 must not resolve before park")

        // Pre-spawn snapshot proves queue-insertion happened BEFORE any
        // parking task can run. Active-ticket count sums to 2.
        let preSpawn = await gate.snapshot()
        XCTAssertEqual(preSpawn.observerParkAckTicketCount, 2,
                       "both tickets must be actor-recorded BEFORE any parking task spawns")
        XCTAssertEqual(preSpawn.parkedObserverCount, 0)

        // Phase 2: spawn the parking task; trigger the intended signal
        // resume via registerWaiter (bounded actor call).
        let park = Task { await gate.awaitObserver(token) }
        _ = await gate.registerWaiter()

        // Hicks H3: close UNCONDITIONALLY before awaiting any
        // ticket.value() or task.value. Every wait below is now bounded
        // by close-drain even if actor semantics regress.
        await gate.close()
        let r1 = await ticket1.value()
        let r2 = await ticket2.value()
        await park.value

        // Exact result set (observer signal path): the intended resume
        // trigger here is `registerWaiter()` which invokes signalEntry
        // on the actor. Either:
        //   - park won: parked before signal → ticket resolves `.parked`
        //     when park calls `flushObserverParkAcks(.parked)`; signal
        //     later drains parked via `.signaledWhileParked` but the
        //     one-shot ticket is unaffected.
        //   - signal won: latch fires with `.signaledBeforePark`, which
        //     `flushObserverParkAcks(.terminal(.signaledBeforePark))`
        //     resolves the ticket to. The subsequent awaitObserver
        //     consumes the latch.
        // `close()` runs strictly AFTER `registerWaiter()` (test awaits
        // both sequentially), so `.terminal(.closedBeforePark)` is
        // unreachable — the token's fate is already sealed by
        // registerWaiter's signal.
        let expected: Set<AsyncGate.ParkAckResult> = [.parked, .terminal(.signaledBeforePark)]
        XCTAssertTrue(expected.contains(r1),
                      "ticket 1 exact receipt must be .parked or .terminal(.signaledBeforePark); got \(r1)")
        XCTAssertEqual(r1, r2, "tickets share timeline")

        let final = await gate.snapshot()
        XCTAssertEqual(final.observerParkAckTicketCount, 0)
        XCTAssertEqual(final.observerParkAckQueueTotal, 0)
        XCTAssertEqual(final.parkedObserverCount, 0)
    }

    /// Waiter analogue of the R2 pre-spawn ticket test.
    func testAsyncGateR2ParkAckTicketsQueuedBeforeSpawn_waiter() async {
        let gate = AsyncGate()
        let token = await gate.registerWaiter()

        let ticket1 = await gate.enterWaiterParkAck(token)
        let ticket2 = await gate.enterWaiterParkAck(token)
        XCTAssertFalse(ticket1.isResolved)
        XCTAssertFalse(ticket2.isResolved)

        let preSpawn = await gate.snapshot()
        XCTAssertEqual(preSpawn.waiterParkAckTicketCount, 2)
        XCTAssertEqual(preSpawn.parkedWaiterCount, 0)

        let park = Task { await gate.awaitWaiter(token) }
        await gate.open()

        // Hicks H3: close UNCONDITIONALLY before awaiting ticket or
        // task values. Every wait below is bounded by close-drain.
        await gate.close()
        let r1 = await ticket1.value()
        let r2 = await ticket2.value()
        await park.value

        // Exact result set (waiter variant): `.parked` when park won,
        // or `.terminal(.openedBeforePark)` when open() latched the
        // token before the parking task ran and the latch flushed
        // ticket resolution to that reason. Both tickets share timeline.
        let expected: Set<AsyncGate.ParkAckResult> = [.parked, .terminal(.openedBeforePark)]
        XCTAssertTrue(expected.contains(r1),
                      "ticket 1 exact receipt must be .parked or .terminal(.openedBeforePark); got \(r1)")
        XCTAssertEqual(r1, r2, "tickets share timeline")

        let final = await gate.snapshot()
        XCTAssertEqual(final.waiterParkAckTicketCount, 0)
        XCTAssertEqual(final.waiterParkAckQueueTotal, 0)
    }

    /// Hicks R2: `enterObserverParkAck` on an already-closed gate
    /// returns an already-resolved ticket — no allocation into the
    /// active-ticket map.
    func testAsyncGateR2EnterParkAckAfterCloseIsImmediatelyResolved_observer() async {
        let gate = AsyncGate()
        let token = await gate.registerObserver()
        await gate.close()
        let ticket = await gate.enterObserverParkAck(token)
        XCTAssertTrue(ticket.isResolved, "post-close enter must return an already-resolved ticket")
        let r = await ticket.value()
        XCTAssertTrue(r == .closedOrConsumed || r == .terminal(.closedBeforePark),
                      "post-close enter must resolve boundedly; got \(r)")
        let snap = await gate.snapshot()
        XCTAssertEqual(snap.observerParkAckTicketCount, 0,
                       "no active tickets after immediate resolution")
    }

    /// Hicks R2: a pending ticket resolved by `close()` before the
    /// parking task ever runs resolves boundedly (closedOrConsumed or
    /// terminal(.closedBeforePark)). The actor removes the ticket
    /// entry at resolution; the caller's ticket object buffers the
    /// result independently.
    func testAsyncGateR2ParkAckTicketDrainedByClose_observer() async {
        let gate = AsyncGate()
        let token = await gate.registerObserver()
        let ticket = await gate.enterObserverParkAck(token)
        XCTAssertFalse(ticket.isResolved)

        // Snapshot proves ticket in active map.
        let mid = await gate.snapshot()
        XCTAssertEqual(mid.observerParkAckTicketCount, 1)

        // R3: close first (intended action), then consume result.
        await gate.close()
        // After close, actor MUST have removed the ticket entry.
        let afterClose = await gate.snapshot()
        XCTAssertEqual(afterClose.observerParkAckTicketCount, 0,
                       "close must remove the ticket entry from the active map")

        let outcome = await ticket.value()
        XCTAssertTrue(outcome == .closedOrConsumed
                        || outcome == .terminal(.closedBeforePark),
                      "close-before-park ticket resolution: \(outcome)")
        XCTAssertTrue(ticket.isResolved)
    }

    // MARK: - Hicks R4: ticket-never-awaited proofs (Vasquez coverage)

    /// Hicks R4 + Vasquez: hundreds of ticket enters followed by park
    /// with no consumer awaiting any ticket. After each park, the
    /// actor MUST have removed the ticket from the active map — the
    /// ticket object lifetime is caller-owned and dropped without
    /// consumption here. Gate active-ticket count returns to zero.
    /// Proves R2 ticket redesign leaves ZERO gate storage for
    /// never-consumed resolved tickets.
    func testAsyncGateR4TicketNeverAwaitedLeavesZeroGateState_observer() async {
        let gate = AsyncGate()
        let iterations = 100
        var parkTasks: [Task<Void, Never>] = []
        for _ in 0..<iterations {
            let token = await gate.registerObserver()
            _ = await gate.enterObserverParkAck(token)   // ticket dropped, never awaited
            let park = Task { await gate.awaitObserver(token) }
            _ = await gate.waitForObserverParked(token)   // structural park ACK
            // Snapshot immediately after park: active tickets returned
            // to zero (actor removed at resolve()) even though no one
            // consumed the ticket.
            let midCheck = await gate.snapshot()
            XCTAssertEqual(midCheck.observerParkAckTicketCount, 0,
                           "actor must remove ticket at resolve, regardless of consumer")
            await gate.signal()   // synchronously seals + resumes this token
            // Defer park.value to after the single unconditional close so a
            // lost-wakeup regression fails the aggregate assertion below
            // rather than stranding this await inside the loop.
            parkTasks.append(park)
        }
        let snap = await gate.snapshot()
        XCTAssertFalse(snap.closed, "gate remains open — pre-close boundedness proof")
        XCTAssertEqual(snap.observerParkAckTicketCount, 0)
        XCTAssertTrue(snap.observerFates.isEmpty,
                      "no per-token fate accumulation across \(iterations) iterations")
        XCTAssertEqual(snap.observerResumeCounts[.parkedResumedBySignal] ?? 0, iterations)
        // Unconditional teardown, then drain all signal-resumed tasks.
        await gate.close()
        for park in parkTasks { await park.value }
    }

    /// Waiter analogue of the ticket-never-awaited proof.
    /// Hicks H4 fix: previously exited after one iteration via `break`
    /// at what was line 1958. `open()` is monotonic, so we cannot
    /// reuse one gate; create a fresh gate per iteration and prove
    /// all `iterations` waiter park/open cycles leave zero ticket
    /// state across every iteration.
    func testAsyncGateR4TicketNeverAwaitedLeavesZeroGateState_waiter() async {
        let iterations = 100
        var totalOpens = 0
        for _ in 0..<iterations {
            let gate = AsyncGate()
            let token = await gate.registerWaiter()
            _ = await gate.enterWaiterParkAck(token)
            let park = Task { await gate.awaitWaiter(token) }
            _ = await gate.waitForWaiterParked(token)
            let midCheck = await gate.snapshot()
            XCTAssertEqual(midCheck.waiterParkAckTicketCount, 0,
                           "no gate-side ticket state after park")
            await gate.open()
            let post = await gate.snapshot()
            XCTAssertEqual(post.waiterParkAckTicketCount, 0)
            XCTAssertTrue(post.waiterFates.isEmpty,
                          "no per-token fate accumulation per iteration")
            XCTAssertEqual(post.waiterResumeCounts[.parkedResumedByOpen] ?? 0, 1)
            // Unconditional teardown, then drain the open-resumed task. open()
            // sealed .openedWhileParked synchronously, so the assertions above
            // hold without the (deferred) park.value.
            await gate.close()
            await park.value
            totalOpens += 1
        }
        XCTAssertEqual(totalOpens, iterations,
                       "all \(iterations) waiter park/open cycles ran")
    }

    // MARK: Hicks H1 — attempt publication race (atomic outcome latch)

    /// Reviewer finding #1 (HIGH): the prior version used
    /// `awaitObserver` (no hold) and awaited `attempt.outcome()`
    /// AFTER signal to prove natural publication. That await could
    /// hang indefinitely if publication regressed, and the late
    /// `task.cancel()` fired AFTER the outer withTaskCancellationHandler
    /// scope had already torn down — so cancellation was NOT proven
    /// to arrive while the handler was live.
    ///
    /// This revision uses the new `awaitObserverAndHoldAfterPublish`
    /// helper which:
    ///   1. Parks the primary continuation
    ///   2. On resume, IMMEDIATELY publishes natural completion via
    ///      `attempt.markCompletedNaturallyIfActive()` +
    ///      `resolveOutcome(.finishedBeforeProcessing)` (synchronous)
    ///   3. Then parks inside `holdGate.awaitWaiter(holdToken)`
    /// The outer cancellation handler stays installed throughout.
    ///
    /// Test flow (deterministic, failure-safe):
    ///   1. Pre-register `holdAck` ticket on holdGate BEFORE task spawn
    ///   2. Spawn task using the new helper
    ///   3. Prove primary parked via `waitForObserverParked`
    ///   4. `signal()` → primary continuation resumes → body publishes
    ///      natural THEN parks in hold
    ///   5. Snapshot: primary `signaledWhileParked` == 1, parked == 0
    ///   6. Watchdog Task: awaits `task.value` and (only if that returns)
    ///      closes holdGate — a REGRESSION where body never reaches hold
    ///      causes task to return early, watchdog fires, holdAck resolves
    ///      `.closedOrConsumed`, and the arrival assertion fails LOUDLY
    ///      instead of hanging.
    ///   7. `await holdAck.value()` — bounded: either `.parked` (body
    ///      entered hold; publication committed) or `.closedOrConsumed`
    ///      (regression detected).
    ///   8. Read `attempt.stateForTest`/`bufferedOutcomeForTest`
    ///      SYNCHRONOUSLY under lock — publication is proven to have
    ///      committed BEFORE the hold-park by construction.
    ///   9. `task.cancel()` — outer onCancel fires while handler is
    ///      live in hold. Primary state gate rejects
    ///      (`beginCancellationIfActive()` returns false because state
    ///      is `.completedNaturally`); NO primary cancel Task is launched.
    ///  10. Open + close holdGate to release the body; `await task.value`
    ///      is bounded by that drain.
    ///  11. Assertions: primary `observerCancelInvocationCount == 0`
    ///      (definitive proof the state gate blocked the late cancel);
    ///      outcome remains exactly `.finishedBeforeProcessing`.
    func testAsyncGateH1AttemptNaturalWinsOverLateCancel_observer() async {
        let gate = AsyncGate()
        let holdGate = AsyncGate()
        let holdToken = await holdGate.registerWaiter()
        // Failure-safe hold-arrival ticket (pre-registered BEFORE spawn):
        // resolves `.parked` when body enters hold-await, or
        // `.closedOrConsumed` if the watchdog closes holdGate on
        // early task return.
        let holdAck = await holdGate.enterWaiterParkAck(holdToken)

        let token = await gate.registerObserver()
        let attempt = ObserverAwaitAttempt()

        let task = Task {
            await gate.awaitObserverAndHoldAfterPublish(
                token, attempt: attempt,
                holdGate: holdGate, holdToken: holdToken)
        }
        _ = await gate.waitForObserverParked(token)

        // Signal resumes the parked continuation naturally.
        await gate.signal()

        // Gate-side resume proof (synchronous seal).
        let mid = await gate.snapshot()
        XCTAssertEqual(mid.observerFateCounts[.signaledWhileParked] ?? 0, 1,
                       "signal resumed the parked observer (gate-side proof)")
        XCTAssertEqual(mid.parkedObserverCount, 0)

        // Watchdog: bounds holdAck against a regression where body
        // returns without ever reaching hold. If task ends early,
        // watchdog closes holdGate which drains holdAck as
        // `.closedOrConsumed`. Registered BEFORE we await holdAck so
        // there is no strand risk.
        let watchdog = Task { [holdGate] in
            _ = await task.value
            await holdGate.close()
        }

        // Bounded hold-arrival: resolves `.parked` when body enters
        // hold, or `.closedOrConsumed` via watchdog on regression.
        let arrival = await holdAck.value()
        XCTAssertEqual(arrival, .parked,
                       "body must reach holdGate.awaitWaiter after publishing natural completion; got \(arrival)")

        // Synchronous publication proof (no additional actor hops):
        // the state gate committed `.completedNaturally` and the
        // buffered outcome is `.finishedBeforeProcessing` BEFORE the
        // task parked in hold, so both are readable now.
        XCTAssertEqual(attempt.stateForTest, .completedNaturally,
                       "state machine must have committed natural path before hold-park")
        XCTAssertEqual(attempt.bufferedOutcomeForTest, .finishedBeforeProcessing,
                       "outcome must be published to buffered latch before hold-park")

        // Late cancel WHILE outer withTaskCancellationHandler is live
        // (body is parked in hold). Task.cancel() synchronously invokes
        // active onCancel handlers, so the primary onCancel runs before
        // this call returns; its `beginCancellationIfActive()` returns
        // false because state is `.completedNaturally`, so NO primary
        // cancel Task is launched.
        task.cancel()

        // Unconditional teardown: release hold to let body return,
        // then drain primary. All awaits below are bounded.
        await holdGate.open()
        await holdGate.close()
        await gate.close()
        await task.value
        _ = await watchdog.value

        // Definitive proof the state gate blocked the late cancel:
        // no primary cancel Task was ever dispatched, so
        // observerCancelInvocationCount stays exactly zero. A regression
        // that lets natural + cancel double-publish (or that removes
        // the state gate) would increment this counter.
        let snap = await gate.snapshot()
        XCTAssertEqual(snap.observerCancelInvocationCount, 0,
                       "state gate must reject late cancel: no primary cancel Task launched")

        // Reviewer finding A (Hicks): the buffered latch is one-shot,
        // so the outcome is readable SYNCHRONOUSLY here without any
        // additional wait. Awaiting `attempt.outcome()` would deadlock
        // in a specific regression scenario: if
        // `markCompletedNaturallyIfActive()` succeeded but
        // `attempt.resolveOutcome(.finishedBeforeProcessing)` regressed,
        // the state gate would still be `.completedNaturally` (blocking
        // any late cancel from launching a cancel Task via
        // `beginCancellationIfActive`), so NO publisher would ever fire
        // and `outcome()` would suspend forever with no rescuer. The
        // synchronous read below returns `nil` in that regression and
        // the equality check fails LOUDLY instead — the buffered
        // outcome is proven synchronously readable by construction
        // (the arrival ACK above already established the body reached
        // hold-park, which is AFTER the state gate committed and the
        // outcome was resolved into the latch).
        XCTAssertEqual(attempt.bufferedOutcomeForTest, .finishedBeforeProcessing,
                       "late cancel must not reverse published outcome (nil indicates resolveOutcome regressed with no publisher)")
    }

    /// Waiter analogue of the natural-wins-over-late-cancel proof.
    /// Uses `awaitWaiterAndHoldAfterPublish` + open() to resume the
    /// parked waiter, same watchdog + holdAck handshake structure.
    func testAsyncGateH1AttemptNaturalWinsOverLateCancel_waiter() async {
        let gate = AsyncGate()
        let holdGate = AsyncGate()
        let holdToken = await holdGate.registerWaiter()
        let holdAck = await holdGate.enterWaiterParkAck(holdToken)

        let token = await gate.registerWaiter()
        let attempt = WaiterAwaitAttempt()

        let task = Task {
            await gate.awaitWaiterAndHoldAfterPublish(
                token, attempt: attempt,
                holdGate: holdGate, holdToken: holdToken)
        }
        _ = await gate.waitForWaiterParked(token)
        await gate.open()

        let mid = await gate.snapshot()
        XCTAssertEqual(mid.waiterFateCounts[.openedWhileParked] ?? 0, 1,
                       "open resumed the parked waiter (gate-side proof)")
        XCTAssertEqual(mid.parkedWaiterCount, 0)

        let watchdog = Task { [holdGate] in
            _ = await task.value
            await holdGate.close()
        }

        let arrival = await holdAck.value()
        XCTAssertEqual(arrival, .parked,
                       "waiter body must reach holdGate.awaitWaiter after publishing natural completion; got \(arrival)")

        XCTAssertEqual(attempt.stateForTest, .completedNaturally,
                       "waiter state machine must have committed natural path before hold-park")
        XCTAssertEqual(attempt.bufferedOutcomeForTest, .finishedBeforeProcessing,
                       "waiter outcome must be published to buffered latch before hold-park")

        task.cancel()

        await holdGate.open()
        await holdGate.close()
        await gate.close()
        await task.value
        _ = await watchdog.value

        let snap = await gate.snapshot()
        XCTAssertEqual(snap.waiterCancelInvocationCount, 0,
                       "state gate must reject late cancel: no waiter cancel Task launched")

        // Reviewer finding A (Hicks): synchronous buffered read — see
        // observer variant for full rationale. Await would deadlock in
        // the `resolveOutcome` regression scenario (state stuck at
        // `.completedNaturally` blocks cancel-path publication, so no
        // publisher exists to resume `outcome()`). Sync read returns
        // `nil` on regression and the equality assertion fails loudly.
        XCTAssertEqual(attempt.bufferedOutcomeForTest, .finishedBeforeProcessing,
                       "waiter: late cancel must not reverse published outcome (nil indicates resolveOutcome regressed with no publisher)")
    }

    /// Hicks H1: opposite race — parked observer is cancelled FIRST.
    /// `beginCancellationIfActive()` succeeds; natural path (if it ever
    /// ran) sees `markCompletedNaturallyIfActive()` return false and
    /// does not publish. Outcome is exactly `.cancelled(.processedMatched)`.
    func testAsyncGateH1AttemptCancelWinsOverLateNatural_observer() async {
        let gate = AsyncGate()
        let token = await gate.registerObserver()
        let attempt = ObserverAwaitAttempt()

        let task = Task { await gate.awaitObserver(token, attempt: attempt) }
        _ = await gate.waitForObserverParked(token)
        task.cancel()

        // Hicks H3: close UNCONDITIONALLY before awaiting task.value or
        // attempt.outcome(). Both are then bounded by close-drain.
        await gate.close()
        await task.value

        let outcome = await attempt.outcome()
        // Cancel-first path yields exactly `.processedMatched`; if the
        // close-drain wins the actor race the outcome is
        // `.closedBeforeProcessing`. Both are exact non-hang receipts.
        XCTAssertTrue(outcome == .cancelled(.processedMatched)
                      || outcome == .cancelled(.closedBeforeProcessing),
                      "cancel path publishes exact receipt; got \(outcome)")
    }

    /// Waiter analogue.
    func testAsyncGateH1AttemptCancelWinsOverLateNatural_waiter() async {
        let gate = AsyncGate()
        let token = await gate.registerWaiter()
        let attempt = WaiterAwaitAttempt()

        let task = Task { await gate.awaitWaiter(token, attempt: attempt) }
        _ = await gate.waitForWaiterParked(token)
        task.cancel()

        // Hicks H3: close UNCONDITIONALLY before awaiting task.value.
        await gate.close()
        await task.value

        let outcome = await attempt.outcome()
        XCTAssertTrue(outcome == .cancelled(.processedMatched)
                      || outcome == .cancelled(.closedBeforeProcessing),
                      "cancel path publishes exact receipt; got \(outcome)")
    }

    // MARK: Hicks H2 — weak ticket boxes stay bounded on enter+drop

    /// Reviewer finding B (Hicks, MEDIUM): the prior H2 tests proved
    /// exact-zero via `pruneAllDroppedTicketBoxesForTest()` — a
    /// test-only manual cleanup method. That does not tie removal to
    /// NORMAL ticket lifecycle: after the final iteration's ticket
    /// deinit, a dead `[tokenID: WeakBox(nil)]` bucket can remain
    /// indefinitely because nothing in the normal path compacts it.
    ///
    /// This revision proves boundedness AND exact-zero via a NATURAL
    /// gate event: parking the token via `awaitObserver` synchronously
    /// (actor-serialized) invokes
    /// `flushObserverParkAcks(id: token.id, result: .parked)`, which
    /// REMOVES the token's entry from `observerActiveParkAckTickets`
    /// atomically — including any dead-but-not-yet-compacted weak
    /// boxes from the enter+drop loop. No manual prune, no
    /// `snapshot()` (which itself compacts), no `close()` before the
    /// exact-zero read.
    ///
    /// Deterministic sequence (no sleeps/yields/polling/timeouts):
    ///   1. Enter+drop N tickets (dead boxes accumulate; intra-loop
    ///      compaction inside `enterObserverParkAck` bounds the raw
    ///      count during the loop).
    ///   2. Read raw box/key counts DIRECTLY (pre-park). Because
    ///      intra-loop compaction ran on every enter, at most the
    ///      last iteration's dead box may remain (raw <= 1).
    ///   3. Spawn parking task; `waitForObserverParked` returns
    ///      `.parked` EXACTLY AFTER the actor sets
    ///      `parkedObservers[token.id]` and calls
    ///      `flushObserverParkAcks(id:, .parked)` — proven by
    ///      construction (same-turn synchronous branch inside
    ///      `awaitObserver`).
    ///   4. Read raw box/key counts AGAIN — MUST be EXACTLY zero
    ///      because flush removed the whole bucket, dead boxes and all.
    ///      No snapshot/close/manual prune between the flush and this
    ///      read; the zero is produced by the flush itself.
    ///   5. Bounded teardown: close drains parked task and rescues any
    ///      remaining state (not part of the exact-zero proof).
    func testAsyncGateH2TicketEnterDropStaysBounded_observer() async {
        let gate = AsyncGate()
        let token = await gate.registerObserver()
        let iterations = 100

        // Inner helper: each iteration's ticket is a fresh `let` that is
        // released at loop-body-end. On helper return, every reference
        // is unequivocally dead.
        @inline(never)
        func drainCycles() async {
            for _ in 0..<iterations {
                let ticket = await gate.enterObserverParkAck(token)
                _ = ticket
            }
        }
        await drainCycles()

        // Pre-park raw storage: intra-loop compaction bounds this to
        // <= 1 (last iteration's dead-but-uncompacted box may remain).
        let preBoxes = await gate.debugRawObserverParkAckBoxCount()
        let preKeys = await gate.debugRawObserverParkAckKeyCount()
        XCTAssertLessThanOrEqual(preBoxes, 1,
            "intra-loop compaction bounds pre-park raw box count to <= 1; got \(preBoxes)")
        XCTAssertLessThanOrEqual(preKeys, 1,
            "intra-loop compaction bounds pre-park raw key count to <= 1; got \(preKeys)")

        // NATURAL TICKET LIFECYCLE: parking the observer invokes
        // `flushObserverParkAcks(id: token.id, result: .parked)` inside
        // `awaitObserver` branch (3), which
        // `observerActiveParkAckTickets.removeValue(forKey: id)` — the
        // whole bucket vanishes, dead weak-boxes included. No manual
        // prune, no snapshot, no close.
        let attempt = ObserverAwaitAttempt()
        let parkTask = Task { await gate.awaitObserver(token, attempt: attempt) }
        let ack = await gate.waitForObserverParked(token)
        XCTAssertEqual(ack, .parked,
            "park must succeed before reading post-flush raw storage; got \(ack)")

        // After the park-triggered flush, raw storage for THIS token
        // MUST be exactly zero — the bucket was removed atomically by
        // `flushObserverParkAcks`. Read via `debugRaw*` directly (NOT
        // via `snapshot()`, which would itself compact and mask a
        // flush regression).
        let postBoxes = await gate.debugRawObserverParkAckBoxCount()
        let postKeys = await gate.debugRawObserverParkAckKeyCount()
        XCTAssertEqual(postBoxes, 0,
            "raw backing box count must be EXACTLY zero after park-triggered flush; got \(postBoxes)")
        XCTAssertEqual(postKeys, 0,
            "raw backing key count must be EXACTLY zero after park-triggered flush; got \(postKeys)")

        // Bounded teardown (unconditional; does not participate in the
        // exact-zero proof). Close drains parked observer and resolves
        // any remaining state; parkTask completes.
        await gate.close()
        await parkTask.value
    }

    /// Waiter analogue with exact-zero proof via natural park lifecycle.
    /// See observer variant for the full rationale.
    func testAsyncGateH2TicketEnterDropStaysBounded_waiter() async {
        let gate = AsyncGate()
        let token = await gate.registerWaiter()
        let iterations = 100

        @inline(never)
        func drainCycles() async {
            for _ in 0..<iterations {
                let ticket = await gate.enterWaiterParkAck(token)
                _ = ticket
            }
        }
        await drainCycles()

        let preBoxes = await gate.debugRawWaiterParkAckBoxCount()
        let preKeys = await gate.debugRawWaiterParkAckKeyCount()
        XCTAssertLessThanOrEqual(preBoxes, 1,
            "intra-loop compaction bounds pre-park raw box count to <= 1; got \(preBoxes)")
        XCTAssertLessThanOrEqual(preKeys, 1,
            "intra-loop compaction bounds pre-park raw key count to <= 1; got \(preKeys)")

        // NATURAL TICKET LIFECYCLE: parking the waiter invokes
        // `flushWaiterParkAcks(id: token.id, result: .parked)` inside
        // `awaitWaiter`'s parking branch, removing the whole
        // `waiterActiveParkAckTickets[id]` bucket atomically.
        let attempt = WaiterAwaitAttempt()
        let parkTask = Task { await gate.awaitWaiter(token, attempt: attempt) }
        let ack = await gate.waitForWaiterParked(token)
        XCTAssertEqual(ack, .parked,
            "park must succeed before reading post-flush raw storage; got \(ack)")

        let postBoxes = await gate.debugRawWaiterParkAckBoxCount()
        let postKeys = await gate.debugRawWaiterParkAckKeyCount()
        XCTAssertEqual(postBoxes, 0,
            "raw backing box count must be EXACTLY zero after park-triggered flush; got \(postBoxes)")
        XCTAssertEqual(postKeys, 0,
            "raw backing key count must be EXACTLY zero after park-triggered flush; got \(postKeys)")

        await gate.close()
        await parkTask.value
    }

    /// Hicks R4: ticket-never-awaited also drained by close(). Enter
    /// a ticket on an active token, DO NOT consume it, then close.
    /// Actor removes the ticket entry at close-time resolution.
    func testAsyncGateR4TicketEnteredThenClosedNeverAwaited_observer() async {
        let gate = AsyncGate()
        let token = await gate.registerObserver()
        // Hold ticket for the whole test so the weak-box in gate
        // storage keeps a live ref through close's drain path.
        let ticket = await gate.enterObserverParkAck(token)
        await gate.close()
        // close resolved the buffered result and cleared the map.
        let snap = await gate.snapshot()
        XCTAssertEqual(snap.observerParkAckTicketCount, 0,
                       "close must remove every active ticket entry")
        let result = await ticket.value()
        // Token was registered but never parked, so close seals its
        // fate as `.closedBeforePark` and flushes that terminal to
        // the pending ticket.
        XCTAssertEqual(result, .terminal(.closedBeforePark),
                       "close resolves held ticket for a not-yet-parked token to its terminal fate")
    }

    /// Waiter analogue.
    func testAsyncGateR4TicketEnteredThenClosedNeverAwaited_waiter() async {
        let gate = AsyncGate()
        let token = await gate.registerWaiter()
        let ticket = await gate.enterWaiterParkAck(token)
        await gate.close()
        let snap = await gate.snapshot()
        XCTAssertEqual(snap.waiterParkAckTicketCount, 0)
        let result = await ticket.value()
        XCTAssertEqual(result, .terminal(.closedBeforePark))
    }

    /// Hicks R4: transition-before-consumer. Enter ticket, park then
    /// signal so the ticket resolves BEFORE the consumer calls
    /// `ticket.value()`. The ticket must buffer `.parked` inside
    /// itself and return it whenever the consumer eventually awaits.
    func testAsyncGateR4TicketTransitionBeforeConsumer_observer() async {
        let gate = AsyncGate()
        let token = await gate.registerObserver()
        let ticket = await gate.enterObserverParkAck(token)

        let park = Task { await gate.awaitObserver(token) }
        // Trigger intended resume via registerWaiter (bounded actor call);
        // signal delivery may race with park scheduling, either outcome
        // is bounded by close.
        _ = await gate.registerWaiter()

        // Hicks H3: close UNCONDITIONALLY before awaiting any ticket/
        // task value. Every wait below is now bounded by close-drain.
        await gate.close()
        let r = await ticket.value()
        await park.value

        // Exact result set (observer transition via signal): `.parked`
        // if park won and installed the continuation before the
        // subsequent registerWaiter signal drained it, or
        // `.terminal(.signaledBeforePark)` if the signal latched
        // before the parking task actually parked. Both are exact
        // classifications produced by the actor's signalEntry paths.
        let expected: Set<AsyncGate.ParkAckResult> = [.parked, .terminal(.signaledBeforePark)]
        XCTAssertTrue(expected.contains(r),
                      "ticket exact receipt must be .parked or .terminal(.signaledBeforePark); got \(r)")

        let final = await gate.snapshot()
        XCTAssertEqual(final.observerParkAckTicketCount, 0,
                       "ticket removed after resolution")
        XCTAssertEqual(final.parkedObserverCount, 0)
    }

    /// Waiter analogue of transition-before-consumer.
    func testAsyncGateR4TicketTransitionBeforeConsumer_waiter() async {
        let gate = AsyncGate()
        let token = await gate.registerWaiter()
        let ticket = await gate.enterWaiterParkAck(token)

        let park = Task { await gate.awaitWaiter(token) }
        // Intended resume via open (bounded actor call).
        await gate.open()

        // Hicks H3: close UNCONDITIONALLY before awaiting ticket/task.
        await gate.close()
        let r = await ticket.value()
        await park.value

        // Exact result set (waiter transition via open): `.parked` if
        // park won and installed the continuation before open() drained
        // it, or `.terminal(.openedBeforePark)` if open() latched the
        // token before the parking task actually parked.
        let expected: Set<AsyncGate.ParkAckResult> = [.parked, .terminal(.openedBeforePark)]
        XCTAssertTrue(expected.contains(r),
                      "ticket exact receipt must be .parked or .terminal(.openedBeforePark); got \(r)")

        let final = await gate.snapshot()
        XCTAssertEqual(final.waiterParkAckTicketCount, 0)
        XCTAssertEqual(final.parkedWaiterCount, 0)
    }

    // MARK: - Hicks R1: exact per-attempt cancellation receipts (attempt-owned)

    /// Hicks R1: duplicate-cancel exact receipts. The DUPLICATE
    /// attempt's cancellation is proven per-attempt via the
    /// caller-owned `attempt.outcome()` API — no gate-level
    /// storage. The ORIGINAL attempt is proven untouched by any
    /// cancel dispatch via `attempt.completedNaturally == true`
    /// after natural completion.
    func testAsyncGateR1ExactCancelReceipt_observer() async {
        let gate = AsyncGate()
        let barrier = AsyncGate()
        let token = await gate.registerObserver()
        let origAttempt = ObserverAwaitAttempt()
        let dupAttempt = ObserverAwaitAttempt()

        let original = Task { await gate.awaitObserver(token, attempt: origAttempt) }
        _ = await gate.waitForObserverParked(token)

        let barrierToken = await barrier.registerWaiter()
        let dup = Task {
            await barrier.awaitWaiter(barrierToken)
            await gate.awaitObserver(token, attempt: dupAttempt)
        }
        _ = await barrier.waitForWaiterParked(barrierToken)
        dup.cancel()
        // Bounded actor ACK: the duplicate's cancelObserver dispatched on
        // `gate` (mismatched awaitID → bounded no-op). Proves the dispatch
        // BEFORE the mid snapshot without awaiting the stranding-capable
        // dup.value; close() also drains this ACK queue.
        await gate.waitForObserverCancelCount(atLeast: 1)

        // Signal → original resumes naturally. Actor-synchronous state
        // proof of the intended signal-resume BEFORE any cleanup.
        _ = await gate.registerWaiter()
        let midSnap = await gate.snapshot()
        XCTAssertEqual(midSnap.observerResumeCounts[.parkedResumedBySignal] ?? 0, 1,
                       "signal drained the parked original exactly once")
        XCTAssertEqual(midSnap.observerCancelIgnoredCount, 1)
        XCTAssertEqual(midSnap.observerCancelInvocationCount, 1)

        // H3 safety: unconditional close of BOTH gates before awaiting
        // original.value + origAttempt.outcome(). If any regression
        // stopped original from finishing naturally, close drains via
        // .closedWhileParked and the awaits remain bounded.
        await gate.close()
        await barrier.close()
        await original.value

        // Deferred stranding awaits, after unconditional teardown of both
        // gates: the duplicate attempt's exact receipt (sealed at the cancel
        // dispatch proven above) and the dup task drain.
        await dup.value
        // R1 authoritative receipt via buffered outcome() API — the
        // sole per-attempt receipt path. outcome() is guaranteed to
        // resolve because the cancel-Task publishes exactly once.
        let dupOutcome = await dupAttempt.outcome()
        XCTAssertEqual(dupOutcome, .cancelled(.processedIgnoredMismatch),
                       "duplicate cancel receipt: bounded no-op via mismatched awaitID")

        // Original attempt was never cancelled — completed naturally.
        // outcome() is buffered and one-shot, so this is bounded.
        let origOutcome = await origAttempt.outcome()
        XCTAssertEqual(origOutcome, .finishedBeforeProcessing,
                       "original attempt: natural completion via signal")
        XCTAssertEqual(origAttempt.stateForTest, .completedNaturally)

        let final = await gate.snapshot()
        XCTAssertEqual(final.observerResumeCounts[.parkedResumedByClose] ?? 0, 0,
                       "close must not have rescued original — signal already resumed it")
    }

    /// Waiter analogue of the R1 exact receipt test.
    func testAsyncGateR1ExactCancelReceipt_waiter() async {
        let gate = AsyncGate()
        let barrier = AsyncGate()
        let token = await gate.registerWaiter()
        let origAttempt = WaiterAwaitAttempt()
        let dupAttempt = WaiterAwaitAttempt()

        let original = Task { await gate.awaitWaiter(token, attempt: origAttempt) }
        _ = await gate.waitForWaiterParked(token)

        let barrierToken = await barrier.registerWaiter()
        let dup = Task {
            await barrier.awaitWaiter(barrierToken)
            await gate.awaitWaiter(token, attempt: dupAttempt)
        }
        _ = await barrier.waitForWaiterParked(barrierToken)
        dup.cancel()
        // Bounded actor ACK: the duplicate's cancelWaiter dispatched on `gate`
        // (mismatched awaitID → bounded no-op). Proves dispatch before the mid
        // snapshot without awaiting the stranding-capable dup.value.
        await gate.waitForWaiterCancelCount(atLeast: 1)

        await gate.open()

        // H3 safety: unconditional close BEFORE `await original.value`
        // + origAttempt.outcome() — a regressed open path fails an
        // assertion instead of hanging.
        let midSnap = await gate.snapshot()
        XCTAssertEqual(midSnap.waiterResumeCounts[.parkedResumedByOpen] ?? 0, 1)
        XCTAssertEqual(midSnap.waiterCancelIgnoredCount, 1)

        await gate.close()
        await barrier.close()
        await original.value

        // Deferred stranding awaits after unconditional teardown of both gates.
        await dup.value
        let dupOutcome = await dupAttempt.outcome()
        XCTAssertEqual(dupOutcome, .cancelled(.processedIgnoredMismatch))

        let origOutcome = await origAttempt.outcome()
        XCTAssertEqual(origOutcome, .finishedBeforeProcessing)
        XCTAssertEqual(origAttempt.stateForTest, .completedNaturally)

        let final = await gate.snapshot()
        _ = final
    }

    /// Hicks R1: matched-cancel receipt. Original attempt is cancelled
    /// while parked; its own `attempt.outcome()` returns
    /// `.cancelled(.processedMatched)`. No gate storage.
    func testAsyncGateR1MatchedCancelReceipt_observer() async {
        let gate = AsyncGate()
        let token = await gate.registerObserver()
        let attempt = ObserverAwaitAttempt()

        let t = Task { await gate.awaitObserver(token, attempt: attempt) }
        _ = await gate.waitForObserverParked(token)

        // Cancel the ONLY task on the ONLY attempt — matched.
        t.cancel()
        // Bounded actor ACK: matched cancelObserver dispatched, sealing this
        // attempt .cancelledWhileParked and resolving its .processedMatched
        // receipt. Proves the dispatch before the snapshot without awaiting
        // the stranding-capable t.value.
        await gate.waitForObserverCancelCount(atLeast: 1)

        let midSnap = await gate.snapshot()
        XCTAssertEqual(midSnap.observerResumeCounts[.parkedResumedByCancel] ?? 0, 1)
        XCTAssertEqual(midSnap.observerCancelInvocationCount, 1)

        await gate.close()
        await t.value

        // Buffered outcome() delivers the definitive per-attempt receipt
        // (sealed at the cancel dispatch above; close cannot alter it).
        let outcome = await attempt.outcome()
        XCTAssertEqual(outcome, .cancelled(.processedMatched),
                       "matched cancel drains THIS attempt's parked continuation")
        _ = await gate.snapshot()
    }

    /// Waiter analogue.
    func testAsyncGateR1MatchedCancelReceipt_waiter() async {
        let gate = AsyncGate()
        let token = await gate.registerWaiter()
        let attempt = WaiterAwaitAttempt()

        let t = Task { await gate.awaitWaiter(token, attempt: attempt) }
        _ = await gate.waitForWaiterParked(token)

        t.cancel()
        // Bounded actor ACK: matched cancelWaiter dispatched, sealing this
        // attempt and resolving its .processedMatched receipt. Proves dispatch
        // before the snapshot without awaiting the stranding-capable t.value.
        await gate.waitForWaiterCancelCount(atLeast: 1)

        let midSnap = await gate.snapshot()
        XCTAssertEqual(midSnap.waiterResumeCounts[.parkedResumedByCancel] ?? 0, 1)

        await gate.close()
        await t.value

        let outcome = await attempt.outcome()
        XCTAssertEqual(outcome, .cancelled(.processedMatched))
        _ = await gate.snapshot()
    }

    // MARK: - Hicks R4 + Vasquez: no-consumer cancel receipt proofs

    /// Vasquez coverage via R1 design: hundreds of cancelled attempts
    /// with NO external receipt consumer leave ZERO gate receipt
    /// state. Under the R1 caller-owned design, the gate has no
    /// receipt map to grow at all — the per-attempt result lives
    /// inside each dropped `AwaitAttempt` object. This test drops
    /// every attempt without ever reading `attempt.outcome()`; gate
    /// snapshot fields remain zero.
    func testAsyncGateR4NoConsumerCancelReceiptLeavesGateZero_observer() async {
        let gate = AsyncGate()
        let iterations = 100
        var tasks: [Task<Void, Never>] = []
        for i in 0..<iterations {
            let token = await gate.registerObserver()
            let attempt = ObserverAwaitAttempt()
            let t = Task { await gate.awaitObserver(token, attempt: attempt) }
            _ = await gate.waitForObserverParked(token)
            t.cancel()
            // Bounded actor ACK: prove THIS iteration's matched cancelObserver
            // dispatched (bumping the invocation counter and sealing
            // .parkedResumedByCancel) before proceeding, without awaiting the
            // stranding-capable t.value.
            await gate.waitForObserverCancelCount(atLeast: i + 1)
            // Do NOT await attempt's receipt — drop attempt. Defer t.value to
            // after the single unconditional close below.
            tasks.append(t)
            _ = attempt   // silence unused
        }
        let snap = await gate.snapshot()
        XCTAssertFalse(snap.closed, "gate remains open — pre-close boundedness proof")
        XCTAssertEqual(snap.observerCancelIgnoredCount, 0,
                       "matched cancels — no ignored counts should accumulate")
        XCTAssertEqual(snap.observerCancelInvocationCount, iterations,
                       "aggregate cancel-invocation counter is the only growth")
        XCTAssertEqual(snap.observerResumeCounts[.parkedResumedByCancel] ?? 0, iterations)
        // Unconditional teardown, then drain all cancelled tasks.
        await gate.close()
        for t in tasks { await t.value }
    }

    /// Waiter analogue of the no-consumer cancel-receipt boundedness proof.
    func testAsyncGateR4NoConsumerCancelReceiptLeavesGateZero_waiter() async {
        let gate = AsyncGate()
        let iterations = 100
        var tasks: [Task<Void, Never>] = []
        for i in 0..<iterations {
            let token = await gate.registerWaiter()
            let attempt = WaiterAwaitAttempt()
            let t = Task { await gate.awaitWaiter(token, attempt: attempt) }
            _ = await gate.waitForWaiterParked(token)
            t.cancel()
            // Bounded actor ACK: prove THIS iteration's matched cancelWaiter
            // dispatched before proceeding, without awaiting the stranding t.value.
            await gate.waitForWaiterCancelCount(atLeast: i + 1)
            tasks.append(t)
            _ = attempt
        }
        let snap = await gate.snapshot()
        XCTAssertFalse(snap.closed)
        XCTAssertEqual(snap.waiterCancelInvocationCount, iterations)
        XCTAssertEqual(snap.waiterResumeCounts[.parkedResumedByCancel] ?? 0, iterations)
        // Unconditional teardown, then drain all cancelled tasks.
        await gate.close()
        for t in tasks { await t.value }
    }

    // MARK: - Bishop remediation: causal close-first cancellation receipt

    /// Reviewer finding #3 (MEDIUM): the prior Bishop close-first hold
    /// tests raced. Nothing proved the child task reached
    /// `holdGate.awaitWaiter(holdToken)` BEFORE the parent test opened
    /// hold, and repeated runs observed `.terminal(.openedBeforePark)`
    /// on the post-hoc holdAck check instead of `.parked`.
    ///
    /// This revision adds a CAUSAL, FAILURE-SAFE hold-arrival handshake
    /// BEFORE cancellation/opening:
    ///
    ///   - Pre-registered `holdAck` ticket resolves `.parked` when the
    ///     body enters hold-await (via the actor's park-flush path).
    ///   - A watchdog Task awaits `task.value` and, if that returns,
    ///     closes holdGate. If the body regresses and never reaches
    ///     hold, `task.value` returns early, watchdog fires, and
    ///     holdAck resolves `.closedOrConsumed` — the arrival
    ///     assertion fails LOUDLY instead of hanging.
    ///   - `await holdAck.value()` is therefore bounded either way,
    ///     and its assertion strictly requires `.parked` before we
    ///     invoke `task.cancel()`.
    ///
    /// Sequence (all sites structural, no polling / yields / sleeps):
    ///   1. `gate.close()` drains primary parked with `.closedWhileParked`
    ///   2. Watchdog Task registered (unblocks holdAck on regression)
    ///   3. Await holdAck.value(); assert `.parked` — proves body
    ///      reached `holdGate.awaitWaiter(holdToken)` BEFORE cancel
    ///   4. `task.cancel()` fires while outer handler is live in hold
    ///   5. Open + close holdGate (idempotent teardown); await task.value
    ///   6. Assert exact `.cancelled(.closedBeforeProcessing)` — no OR
    ///
    /// The final assertion remains: `attempt.outcome() ==
    /// .cancelled(.closedBeforeProcessing)` exactly. No either-fate
    /// weakening.
    func testAsyncGateBishopCloseFirstYieldsClosedBeforeProcessing_observer() async {
        let gate = AsyncGate()
        let holdGate = AsyncGate()
        let holdToken = await holdGate.registerWaiter()

        let token = await gate.registerObserver()
        let attempt = ObserverAwaitAttempt()
        let task = Task {
            await gate.awaitObserverAndHold(token, attempt: attempt,
                                             holdGate: holdGate, holdToken: holdToken)
        }
        _ = await gate.waitForObserverParked(token)

        // Step 1 causal: close primary gate FIRST — drains parked
        // observer via .closedWhileParked branch.
        await gate.close()
        let afterClose = await gate.snapshot()
        XCTAssertTrue(afterClose.closed)
        XCTAssertEqual(afterClose.parkedObserverCount, 0,
                       "close drained the parked observer")
        XCTAssertEqual(afterClose.observerResumeCounts[.parkedResumedByClose] ?? 0, 1,
                       "exactly one parkedResumedByClose resume at close")
        XCTAssertEqual(afterClose.observerFateCounts[.closedWhileParked] ?? 0, 1,
                       "close sealed fate .closedWhileParked for the primary observer")

        // Reviewer finding C (Hicks, MEDIUM): the prior version awaited
        // a hold-arrival ACK (via `holdAck.value()`) BEFORE `task.cancel()`
        // to prove the outer withTaskCancellationHandler was live. That
        // handshake was CYCLIC:
        //   • main awaits holdAck.value()
        //   • watchdog Task awaits task.value
        //   • task awaits main to open holdGate
        // If park-ticket publication regressed (`flushWaiterParkAcks`
        // for holdToken didn't fire), holdAck never resolved and all
        // three parties deadlocked forever.
        //
        // The EXACT outcome asserted below is the definitive proof that
        // (a) onCancel fired (handler was installed at cancel time), AND
        // (b) the cancel Task saw `closed == true` (close-first ordering
        // held), because `.cancelled(.closedBeforeProcessing)` is the
        // ONLY outcome publishable under both conditions:
        //   • If handler wasn't live → onCancel wouldn't fire →
        //     natural completion runs → outcome = .finishedBeforeProcessing.
        //   • If close-first ordering broke (cancel raced ahead) →
        //     receipt = .processedMatched (drained parked pre-close) or
        //     the parked entry would already be drained by close and
        //     receipt = .closedBeforeProcessing — but the .parkedResumedByCancel
        //     counter would then be 1, failing the final invariant.
        // Any regression on either produces a different outcome or
        // counter and fails the assertions LOUDLY without hanging.
        //
        // The withTaskCancellationHandler scope stays installed for the
        // entire body of `awaitObserverAndHold`, which cannot return
        // until holdGate.awaitWaiter completes. Since main will open
        // and close holdGate BELOW (unconditionally, after cancel),
        // the task body is guaranteed to return; every await is bounded.
        task.cancel()

        // Unconditional teardown: independently reachable from any
        // child completion or ticket-publication state.
        //   • `holdGate.open()` drains the hold-parked continuation
        //     (or latches register-then-not-parked). If the body
        //     hasn't yet reached hold-await, open latches the token
        //     as completed and the subsequent hold-await consumes
        //     the latch. Either way, hold does not strand the body.
        //   • `holdGate.close()` is idempotent teardown for holdGate.
        //   • `task.value` returns once the body returns (guaranteed
        //     bounded by hold open above).
        await holdGate.open()
        await holdGate.close()
        await task.value

        // Buffered outcome delivers the authoritative per-attempt
        // receipt. The cancel Task publishes exactly once via
        // `attempt.resolveOutcome(.cancelled(receipt))`; the natural
        // completion path finds state == `.cancellationInitiated` and
        // does NOT publish. So exactly one publisher fires, and by
        // the close-first ordering contract MUST be
        // `.cancelled(.closedBeforeProcessing)`. `outcome()` is NOT
        // cyclic — the cancel Task runs to completion (nothing blocks
        // it) and doesn't wait on outcome.
        let outcome = await attempt.outcome()
        XCTAssertEqual(outcome, .cancelled(.closedBeforeProcessing),
                       "close-first ordering MUST yield exact .cancelled(.closedBeforeProcessing) (no OR)")

        let final = await gate.snapshot()
        XCTAssertEqual(final.parkedObserverCount, 0)
        XCTAssertEqual(final.observerResumeCounts[.parkedResumedByCancel] ?? 0, 0,
                       "no parked-cancel resume — cancel ran after close, saw closed guard")
        XCTAssertEqual(final.observerResumeCounts[.parkedResumedByClose] ?? 0, 1,
                       "the single resume that happened was via close, not cancel")
        XCTAssertEqual(final.observerCancelInvocationCount, 1,
                       "cancelObserver dispatched exactly once (post-close, took closed-guard)")
        XCTAssertEqual(final.observerCancelIgnoredCount, 1,
                       "closed-guard bumps observerCancelIgnoredCount by one; no matching parked entry drained")
    }

    /// Waiter analogue: close-first causal ordering yields exact
    /// `.closedBeforeProcessing`. See observer variant for the full
    /// rationale of removing the cyclic pre-cancel arrival handshake.
    func testAsyncGateBishopCloseFirstYieldsClosedBeforeProcessing_waiter() async {
        let gate = AsyncGate()
        let holdGate = AsyncGate()
        let holdToken = await holdGate.registerWaiter()

        let token = await gate.registerWaiter()
        let attempt = WaiterAwaitAttempt()
        let task = Task {
            await gate.awaitWaiterAndHold(token, attempt: attempt,
                                          holdGate: holdGate, holdToken: holdToken)
        }
        _ = await gate.waitForWaiterParked(token)

        await gate.close()
        let afterClose = await gate.snapshot()
        XCTAssertTrue(afterClose.closed)
        XCTAssertEqual(afterClose.parkedWaiterCount, 0)
        XCTAssertEqual(afterClose.waiterResumeCounts[.parkedResumedByClose] ?? 0, 1)
        XCTAssertEqual(afterClose.waiterFateCounts[.closedWhileParked] ?? 0, 1)

        // Reviewer finding C: cancel with no pre-arrival handshake.
        // Exact outcome below proves handler-live-at-cancel and
        // close-first ordering — no cyclic wait, no watchdog.
        task.cancel()

        // Unconditional teardown — independently reachable.
        await holdGate.open()
        await holdGate.close()
        await task.value

        let outcome = await attempt.outcome()
        XCTAssertEqual(outcome, .cancelled(.closedBeforeProcessing),
                       "close-first ordering MUST yield exact .cancelled(.closedBeforeProcessing) (no OR)")

        let final = await gate.snapshot()
        XCTAssertEqual(final.parkedWaiterCount, 0)
        XCTAssertEqual(final.waiterResumeCounts[.parkedResumedByCancel] ?? 0, 0)
        XCTAssertEqual(final.waiterResumeCounts[.parkedResumedByClose] ?? 0, 1)
        XCTAssertEqual(final.waiterCancelInvocationCount, 1)
        XCTAssertEqual(final.waiterCancelIgnoredCount, 1,
                       "closed-guard bumps waiterCancelIgnoredCount by one; no matching parked entry drained")
    }

    // MARK: - Either-order race-safety (renamed / contract-narrowed)

    /// Either-order race-safety companion: when `t.cancel()` precedes
    /// `close()` without the AndHold structural fence, the outcome is
    /// either `.processedMatched` (cancel drained parked continuation
    /// first) or `.closedBeforeProcessing` (close won). This test does
    /// NOT substitute for the causal close-first proof above; it only
    /// verifies that BOTH outcomes are bounded and exact.
    func testAsyncGateEitherOrderCancelCloseRaceIsBounded_observer() async {
        let gate = AsyncGate()
        let token = await gate.registerObserver()
        let attempt = ObserverAwaitAttempt()

        let t = Task { await gate.awaitObserver(token, attempt: attempt) }
        _ = await gate.waitForObserverParked(token)

        t.cancel()
        await gate.close()
        await t.value

        let outcome = await attempt.outcome()
        guard case .cancelled(let r) = outcome else {
            XCTFail("either-order: outcome must be .cancelled — cancel was initiated; got \(outcome)")
            return
        }
        XCTAssertTrue(r == .closedBeforeProcessing || r == .processedMatched,
                      "either-order race must resolve to one bounded outcome: got \(r)")

        let final = await gate.snapshot()
        XCTAssertEqual(final.parkedObserverCount, 0)
    }

    /// Waiter analogue of the either-order race-safety companion.
    func testAsyncGateEitherOrderCancelCloseRaceIsBounded_waiter() async {
        let gate = AsyncGate()
        let token = await gate.registerWaiter()
        let attempt = WaiterAwaitAttempt()

        let t = Task { await gate.awaitWaiter(token, attempt: attempt) }
        _ = await gate.waitForWaiterParked(token)

        t.cancel()
        await gate.close()
        await t.value

        let outcome = await attempt.outcome()
        guard case .cancelled(let r) = outcome else {
            XCTFail("either-order (waiter): outcome must be .cancelled; got \(outcome)")
            return
        }
        XCTAssertTrue(r == .closedBeforeProcessing || r == .processedMatched,
                      "either-order race must resolve to one bounded outcome: got \(r)")

        let final = await gate.snapshot()
        XCTAssertEqual(final.parkedWaiterCount, 0)
    }

    // MARK: - Hicks Point 5: kind-correct issuance metadata

    /// Hicks addendum Point 5: ObserverToken/WaiterToken share a
    /// memberwise `id: UInt64` initializer, so a caller can fabricate
    /// either kind. When observer and waiter share a single high-water
    /// counter, `token.id >= nextID` misclassifies a fabricated
    /// ObserverToken whose numeric id was only ever issued as a waiter
    /// (or vice-versa). Per-kind counters (`nextObserverID` /
    /// `nextWaiterID`) make the classification EXACT without any
    /// per-token set.
    ///
    /// Structure: register only a waiter. Then fabricate an
    /// ObserverToken whose id equals the waiter's id. That id is
    /// unknown-as-observer; classification MUST resolve `.unknown`.
    /// Also fabricate an ObserverToken with an id above the observer
    /// high-water mark → `.unknown`. Symmetric proofs for waiter.
    func testAsyncGateKindCorrectIssuanceClassification() async {
        let gate = AsyncGate()

        // Register only a waiter — waiter id=1, nextObserverID still 1.
        let waiter = await gate.registerWaiter()
        XCTAssertEqual(waiter.id, 1, "waiter takes first waiter-kind id")

        // Fabricate an ObserverToken with the SAME numeric id as the
        // waiter. Under the pre-remediation shared counter this would
        // be classified as `.closedOrConsumed` (previously-issued
        // observer) — cross-kind misclassification. Under kind-correct
        // per-kind counters it must resolve `.unknown`.
        let crossKindObs = AsyncGate.ObserverToken(id: waiter.id)
        let ack1 = await gate.waitForObserverParked(crossKindObs)
        XCTAssertEqual(ack1, .unknown,
                       "cross-kind fabricated ObserverToken must be classified .unknown, not .closedOrConsumed")

        // Fabricate an ObserverToken above every counter → `.unknown`.
        let fabObs = AsyncGate.ObserverToken(id: 999_999)
        let ack2 = await gate.waitForObserverParked(fabObs)
        XCTAssertEqual(ack2, .unknown)

        // Symmetric: register an observer, fabricate a WaiterToken with
        // the same numeric id → `.unknown`.
        let obs = await gate.registerObserver()
        XCTAssertEqual(obs.id, 1, "observer takes first observer-kind id (independent of waiter counter)")
        let crossKindWat = AsyncGate.WaiterToken(id: obs.id)
        // waiter id=1 IS active (registered above); so use a fresh
        // observer-only id that was never a waiter. registerObserver
        // again to advance observer counter to 3.
        _ = await gate.registerObserver()   // observer id=2, nextObserverID=3
        let onlyObserverID: UInt64 = 2      // was issued as observer, never as waiter
        let crossKindWat2 = AsyncGate.WaiterToken(id: onlyObserverID)
        let ack3 = await gate.waitForWaiterParked(crossKindWat2)
        XCTAssertEqual(ack3, .unknown,
                       "cross-kind fabricated WaiterToken (id issued only as observer) must resolve .unknown")

        // Sanity: `crossKindWat` (id=1) IS active as a waiter, so it
        // should classify as `.parked` after park OR `.closedOrConsumed`
        // after drain — never `.unknown`. Just confirm not-unknown here
        // by inspecting order.
        let mid = await gate.snapshot()
        XCTAssertTrue(mid.waiterOrder.contains(1),
                      "id=1 waiter must still be registered/active (rules out .unknown for its own kind)")
        _ = crossKindWat  // silence unused; represented in the assertion above.

        // Enter-park-ACK ticket path uses the same predicate. R2: a
        // fabricated (unknown-kind) id returns an already-resolved
        // ticket with `.unknown`, and never enters the active map.
        let ticket = await gate.enterObserverParkAck(fabObs)
        XCTAssertTrue(ticket.isResolved,
                      "fabricated observer id must return an already-resolved ticket")

        let snap = await gate.snapshot()
        XCTAssertEqual(snap.observerParkAckTicketCount, 0)
        XCTAssertEqual(snap.waiterParkAckTicketCount, 0)
        XCTAssertEqual(snap.observerUnknownAwaitCount, 0,
                       "waitForXParked classification path must NOT increment awaitUnknown counters")
        XCTAssertEqual(snap.waiterUnknownAwaitCount, 0)

        // Ticket already resolved (proven above via isResolved); await its
        // buffered value AFTER unconditional close. Resolve is exactly-once,
        // so value() is bounded and still returns .unknown post-close.
        await gate.close()
        let ticketRes = await ticket.value()
        XCTAssertEqual(ticketRes, .unknown,
                       "enterObserverParkAck fabricated-id → immediately-resolved .unknown")
    }

    // MARK: - Hicks B: bounded post-close state

    /// Bishop F1: high-count repeated post-close register/await/ACK proves
    /// active per-token maps/sets/order/park/ACK state return to zero
    /// and only aggregate counters change. NO per-token growth. Post-
    /// close registered tokens (id < nextID, not in observerOrder) are
    /// classified as `.duplicateAfterFated` (previously-issued / never-
    /// tracked); fabricated tokens (id >= nextID) would be `.unknown`.
    func testAsyncGateHighCountPostCloseIsBounded() async {
        let gate = AsyncGate()
        await gate.close()
        let iterations = 200
        for _ in 0..<iterations {
            let obs = await gate.registerObserver()
            let wat = await gate.registerWaiter()
            await gate.awaitObserver(obs)   // branch 5 duplicateAfterFated
            await gate.awaitWaiter(wat)     // branch 5 duplicateAfterFated
            let obsAck = await gate.waitForObserverParked(obs)   // .closedOrConsumed
            let watAck = await gate.waitForWaiterParked(wat)     // .closedOrConsumed
            XCTAssertEqual(obsAck, .closedOrConsumed)
            XCTAssertEqual(watAck, .closedOrConsumed)
        }
        let snap = await gate.snapshot()
        // Active per-token state is empty.
        XCTAssertEqual(snap.parkedObserverCount, 0)
        XCTAssertEqual(snap.parkedWaiterCount, 0)
        XCTAssertEqual(snap.observerOrder, [])
        XCTAssertEqual(snap.waiterOrder, [])
        XCTAssertEqual(snap.completedObserverCount, 0)
        XCTAssertEqual(snap.completedWaiterCount, 0)
        XCTAssertTrue(snap.observerFates.isEmpty,
                      "observer fates must not grow with post-close activity")
        XCTAssertTrue(snap.waiterFates.isEmpty,
                      "waiter fates must not grow with post-close activity")
        XCTAssertEqual(snap.observerParkAckQueueTotal, 0)
        XCTAssertEqual(snap.waiterParkAckQueueTotal, 0)
        // Aggregate counters reflect exactly the traffic.
        XCTAssertEqual(snap.observerPostCloseRegistrationCount, iterations)
        XCTAssertEqual(snap.waiterPostCloseRegistrationCount, iterations)
        XCTAssertEqual(snap.observerDuplicateAwaitCount, iterations)
        XCTAssertEqual(snap.waiterDuplicateAwaitCount, iterations)
        XCTAssertEqual(snap.observerResumeCounts[.duplicateAfterFated] ?? 0, iterations)
        XCTAssertEqual(snap.waiterResumeCounts[.duplicateAfterFated] ?? 0, iterations)
    }

    // MARK: - Hicks C: actual-resume counters (distinct from seals)

    /// Hicks C: `signaledBeforePark` seals a fate for a LATCH — it does
    /// NOT resume a real continuation. Only the later `awaitObserver`
    /// that consumes the latch actually calls `c.resume()`. Prove the
    /// distinction: fateCount == 1 immediately after seal but
    /// resumeCounts[.latchConsumed] == 0 until awaitObserver runs.
    func testAsyncGateResumeCountersDistinctFromFateSeals() async {
        let gate = AsyncGate()
        // Signal-before-park: registerWaiter creates a pendingEntrySignal;
        // then registerObserver consumes it as signaledBeforePark.
        _ = await gate.registerWaiter()   // pendingEntrySignals = 1
        let token = await gate.registerObserver()   // seals .signaledBeforePark

        let midSnap = await gate.snapshot()
        XCTAssertEqual(midSnap.observerFateCounts[.signaledBeforePark] ?? 0, 1,
                       "fate is sealed immediately at latch creation")
        XCTAssertEqual(midSnap.observerResumeCounts[.latchConsumed] ?? 0, 0,
                       "no actual continuation has resumed yet")

        // Now awaitObserver consumes the latch — this is the FIRST real
        // continuation resume for this token.
        await gate.awaitObserver(token)

        let afterSnap = await gate.snapshot()
        XCTAssertEqual(afterSnap.observerFateCounts[.signaledBeforePark] ?? 0, 1)
        XCTAssertEqual(afterSnap.observerResumeCounts[.latchConsumed] ?? 0, 1,
                       "latch consumption is the actual resume site")
        XCTAssertEqual(afterSnap.observerResumeCounts[.parkedResumedBySignal] ?? 0, 0)
        await gate.close()
    }

    // MARK: - Hicks D: structural late-cancel with hold-inside-handler

    /// Hicks D observer: park an original, then start a duplicate task
    /// using `awaitObserverAndHold` so that AFTER the duplicate resumes
    /// (via branch 2 duplicate-after-parked) we STRUCTURALLY hold it
    /// inside `withTaskCancellationHandler` by awaiting a separate
    /// holdGate. Cancel the duplicate task WHILE its handler is still
    /// installed. Prove:
    ///   1. cancelObserver was invoked (via `waitForObserverCancelCount`)
    ///   2. It was a bounded no-op — awaitID mismatch, so no state
    ///      mutation, no drain of the ORIGINAL parked continuation.
    ///   3. Release hold, drain the duplicate task cleanly.
    ///   4. Original continuation still parked; can then be resumed.
    func testAsyncGateStructuralLateCancelObserverIsBoundedNoOp() async {
        let gate = AsyncGate()
        let holdGate = AsyncGate()
        let holdToken = await holdGate.registerWaiter()
        // Coordinator H3 non-stranding redesign: pre-register buffered
        // hold-park ACK ticket. Replaces the previous mid-test
        // `waitForWaiterParked(holdToken)` continuation wait that would
        // strand indefinitely if the duplicate body regressed and
        // never reached the holdGate await. The buffered ticket
        // resolves via holdGate.close() below regardless of whether
        // the body parked, so the terminal `await holdAck.value` is
        // guaranteed bounded.
        let holdAck = await holdGate.enterWaiterParkAck(holdToken)

        // Park original with attempt.
        let token = await gate.registerObserver()
        let origAttempt = ObserverAwaitAttempt()
        let dupAttempt = ObserverAwaitAttempt()
        let original = Task { await gate.awaitObserver(token, attempt: origAttempt) }
        _ = await gate.waitForObserverParked(token)

        let baseline = await gate.snapshot()
        XCTAssertEqual(baseline.parkedObserverCount, 1)
        XCTAssertNil(baseline.observerFates[token.id])

        // Duplicate held inside its cancellation handler scope.
        let duplicate = Task {
            await gate.awaitObserverAndHold(token, attempt: dupAttempt,
                                             holdGate: holdGate, holdToken: holdToken)
        }

        // Cancel duplicate immediately. Cancellation is latched at
        // the Task level; whether body has entered
        // `withTaskCancellationHandler` yet or not, onCancel is
        // guaranteed to fire (either synchronously if handler active,
        // or on handler entry if isCancelled==true). The state gate's
        // `beginCancellationIfActive` therefore transitions before body
        // returns, and the cancel Task runs and publishes to the
        // attempt via `resolveOutcome`. This removes the need for the
        // previous stranding `waitForWaiterParked(holdToken)` proof.
        duplicate.cancel()

        // R3 safety: unconditional holdGate.close BEFORE any
        // task/receipt await. If the duplicate body reached
        // holdGate.awaitWaiter, close drains it via .closedWhileParked;
        // if it never reached the hold-await, close seals the fate
        // and the eventual awaitWaiter returns via the closed guard.
        // Either way body completes and `await duplicate.value` is
        // bounded.
        await holdGate.close()
        await duplicate.value

        // `dupAttempt` outcome via buffered outcome() API — the sole
        // per-attempt receipt path. Bounded: cancel Task always
        // resolves via cancelObserver actor call.
        let dupOutcome = await dupAttempt.outcome()
        XCTAssertEqual(dupOutcome, .cancelled(.processedIgnoredMismatch),
                       "mismatched-awaitID cancel is a bounded no-op (primary not yet closed)")

        // Pre-signal state: original still parked, cancel counters
        // incremented, no fate on original token, duplicate hit its
        // branch. This preserves the causal claim previously asserted
        // via mid-test `midSnap`, now consolidated into one snapshot
        // taken AFTER duplicate.value.
        let cancelSnap = await gate.snapshot()
        XCTAssertEqual(cancelSnap.observerDuplicateAwaitCount, 1,
                       "duplicate's awaitObserver actor block ran and hit duplicate branch")
        XCTAssertEqual(cancelSnap.observerResumeCounts[.duplicateAfterParked] ?? 0, 1)
        XCTAssertEqual(cancelSnap.parkedObserverCount, 1,
                       "original parked continuation MUST NOT be drained by mismatched cancel")
        XCTAssertEqual(cancelSnap.observerCancelInvocationCount, 1,
                       "duplicate's onCancel dispatched cancelObserver exactly once")
        XCTAssertEqual(cancelSnap.observerCancelIgnoredCount, 1,
                       "awaitID mismatch → bounded no-op")
        XCTAssertNil(cancelSnap.observerFates[token.id],
                     "original fate must not be sealed by mismatched cancel")

        // Signal original.
        _ = await gate.registerWaiter()

        // R3 safety: close primary BEFORE awaiting original. Signal
        // above should have drained original; close is defensive.
        await gate.close()
        await original.value

        // Post-hoc non-stranding proof of the hold-park ticket. With
        // holdGate closed above, the buffered ticket is guaranteed
        // resolved (either .parked/.terminal from body reaching hold,
        // or .terminal(.closedBeforePark) if it did not). Awaiting
        // the value therefore cannot hang — that is the H3 guarantee.
        _ = await holdAck.value()

        let final = await gate.snapshot()
        XCTAssertEqual(final.parkedObserverCount, 0)
        XCTAssertEqual(final.observerFateCounts[.signaledWhileParked] ?? 0, 1,
                       "original resumed via signal, not cancel")
        XCTAssertEqual(final.observerResumeCounts[.parkedResumedBySignal] ?? 0, 1)
        XCTAssertEqual(final.observerResumeCounts[.parkedResumedByCancel] ?? 0, 0)
        XCTAssertEqual(final.observerResumeCounts[.parkedResumedByClose] ?? 0, 0,
                       "R3: signal already resumed original — no close rescue")
        let origOutcome = await origAttempt.outcome()
        XCTAssertEqual(origOutcome, .finishedBeforeProcessing,
                       "original attempt: natural completion via signal")
        XCTAssertEqual(origAttempt.stateForTest, .completedNaturally)
    }

    /// Waiter analogue of the observer structural late-cancel proof.
    func testAsyncGateStructuralLateCancelWaiterIsBoundedNoOp() async {
        let gate = AsyncGate()
        let holdGate = AsyncGate()
        let holdToken = await holdGate.registerWaiter()
        // Coordinator H3 non-stranding redesign: buffered hold-park
        // ticket (see observer variant for full rationale).
        let holdAck = await holdGate.enterWaiterParkAck(holdToken)

        let token = await gate.registerWaiter()
        let origAttempt = WaiterAwaitAttempt()
        let dupAttempt = WaiterAwaitAttempt()
        let original = Task { await gate.awaitWaiter(token, attempt: origAttempt) }
        _ = await gate.waitForWaiterParked(token)

        let duplicate = Task {
            await gate.awaitWaiterAndHold(token, attempt: dupAttempt,
                                          holdGate: holdGate, holdToken: holdToken)
        }

        // Cancel duplicate; latched cancel guarantees onCancel fires
        // by handler entry.
        duplicate.cancel()

        // R3 safety: hold close BEFORE any potentially-stranding
        // await. Drains parked or seals fate; either way duplicate
        // body completes.
        await holdGate.close()
        await duplicate.value

        let dupOutcome = await dupAttempt.outcome()
        XCTAssertEqual(dupOutcome, .cancelled(.processedIgnoredMismatch),
                       "mismatched-awaitID cancel is a bounded no-op (primary not yet open/closed)")

        // Consolidated cancelSnap (was midSnap+cancelSnap). Proves
        // duplicate's actor block ran, cancel-invoke/ignored counters,
        // and original still parked.
        let cancelSnap = await gate.snapshot()
        XCTAssertEqual(cancelSnap.waiterDuplicateAwaitCount, 1,
                       "duplicate's awaitWaiter actor block ran and hit duplicate branch")
        XCTAssertEqual(cancelSnap.waiterResumeCounts[.duplicateAfterParked] ?? 0, 1)
        XCTAssertEqual(cancelSnap.parkedWaiterCount, 1,
                       "original parked continuation MUST NOT be drained by mismatched cancel")
        XCTAssertEqual(cancelSnap.waiterCancelInvocationCount, 1)
        XCTAssertEqual(cancelSnap.waiterCancelIgnoredCount, 1,
                       "awaitID mismatch → bounded no-op")
        XCTAssertNil(cancelSnap.waiterFates[token.id])

        // Open original (intended resume).
        await gate.open()

        // R3 safety: close primary BEFORE awaiting original.
        await gate.close()
        await original.value

        // Post-hoc non-stranding proof of hold ticket resolution.
        _ = await holdAck.value()

        let final = await gate.snapshot()
        XCTAssertEqual(final.parkedWaiterCount, 0)
        XCTAssertEqual(final.waiterFateCounts[.openedWhileParked] ?? 0, 1)
        XCTAssertEqual(final.waiterResumeCounts[.parkedResumedByOpen] ?? 0, 1)
        XCTAssertEqual(final.waiterResumeCounts[.parkedResumedByCancel] ?? 0, 0)
        XCTAssertEqual(final.waiterResumeCounts[.parkedResumedByClose] ?? 0, 0,
                       "R3: open already resumed original — no close rescue")
        let origOutcome = await origAttempt.outcome()
        XCTAssertEqual(origOutcome, .finishedBeforeProcessing)
        XCTAssertEqual(origAttempt.stateForTest, .completedNaturally)
    }

    // MARK: - Bishop F1: pre-close per-token boundedness

    /// LONG-LIVED NOT-CLOSED observer gate: run many sequential
    /// register→park→signal→await cycles on a gate that is NEVER
    /// closed. After each iteration every per-token map/set is empty
    /// and only aggregate counters grow. Proves the pre-close fate
    /// history is bounded — Bishop F1's central invariant.
    func testAsyncGateLongLivedSequentialObserversAreBounded() async {
        let gate = AsyncGate()
        let iterations = 200
        var tasks: [Task<Void, Never>] = []
        for _ in 0..<iterations {
            let token = await gate.registerObserver()
            let t = Task { await gate.awaitObserver(token) }
            _ = await gate.waitForObserverParked(token)   // structural park ACK
            await gate.signal()                            // resumes parked observer
            // signal sealed + resumed synchronously (aggregate-only); defer
            // t.value to after the single post-loop close so a lost wakeup
            // fails the aggregate assertion instead of stranding in-loop.
            tasks.append(t)
            let mid = await gate.snapshot()
            XCTAssertEqual(mid.parkedObserverCount, 0)
            XCTAssertEqual(mid.observerOrder, [])
            XCTAssertEqual(mid.completedObserverCount, 0,
                           "no per-token completedObservers may accumulate")
            XCTAssertTrue(mid.observerFates.isEmpty,
                          "per-token observerFates must not accumulate")
            XCTAssertEqual(mid.observerParkAckQueueTotal, 0)
        }
        let snap = await gate.snapshot()
        XCTAssertFalse(snap.closed, "gate must remain open — this is the pre-close proof")
        XCTAssertTrue(snap.observerFates.isEmpty)
        XCTAssertTrue(snap.waiterFates.isEmpty)
        XCTAssertEqual(snap.completedObserverCount, 0)
        XCTAssertEqual(snap.completedWaiterCount, 0)
        XCTAssertEqual(snap.observerFateCounts[.signaledWhileParked] ?? 0, iterations,
                       "aggregate: every iteration resumed via signal")
        XCTAssertEqual(snap.observerResumeCounts[.parkedResumedBySignal] ?? 0, iterations,
                       "actual continuation resumed exactly \(iterations) times")
        // Unconditional teardown, then drain all signal-resumed tasks.
        await gate.close()
        for t in tasks { await t.value }
    }

    /// LONG-LIVED opened-waiter gate: after `open()`, subsequent
    /// waiter register→await cycles each latch openedBeforePark and
    /// are consumed by the immediate follow-up await. Prove no per-
    /// token maps accumulate, only aggregate counters.
    func testAsyncGateLongLivedSequentialOpenedWaitersAreBounded() async {
        let gate = AsyncGate()
        await gate.open()
        let iterations = 200
        for _ in 0..<iterations {
            let token = await gate.registerWaiter()   // latch openedBeforePark
            await gate.awaitWaiter(token)              // consume latch
            let mid = await gate.snapshot()
            XCTAssertEqual(mid.parkedWaiterCount, 0)
            XCTAssertEqual(mid.waiterOrder, [])
            XCTAssertEqual(mid.completedWaiterCount, 0,
                           "consumed latch must remove waiter from completedWaiters")
            XCTAssertTrue(mid.waiterFates.isEmpty,
                          "per-token waiterFates must not accumulate")
        }
        let snap = await gate.snapshot()
        XCTAssertFalse(snap.closed)
        XCTAssertTrue(snap.waiterFates.isEmpty)
        XCTAssertEqual(snap.waiterFateCounts[.openedBeforePark] ?? 0, iterations)
        XCTAssertEqual(snap.waiterResumeCounts[.latchConsumed] ?? 0, iterations)
        await gate.close()
    }

    /// Many actually-parked waiters drained by a single `open()`: all
    /// per-token maps must return to zero.
    func testAsyncGateManyParkedWaitersDrainedByOpenClearsMaps() async {
        let gate = AsyncGate()
        let n = 50
        var tokens: [AsyncGate.WaiterToken] = []
        var tasks: [Task<Void, Never>] = []
        tokens.reserveCapacity(n)
        tasks.reserveCapacity(n)
        for _ in 0..<n {
            let token = await gate.registerWaiter()
            tokens.append(token)
            tasks.append(Task { await gate.awaitWaiter(token) })
            _ = await gate.waitForWaiterParked(token)
        }
        let mid = await gate.snapshot()
        XCTAssertEqual(mid.parkedWaiterCount, n)

        await gate.open()
        // open() sealed all n parked waiters .openedWhileParked synchronously
        // (aggregate + resume in one actor call); prove via the snapshot below
        // before draining the tasks after the unconditional close.
        let snap = await gate.snapshot()
        XCTAssertEqual(snap.parkedWaiterCount, 0)
        XCTAssertEqual(snap.waiterOrder, [])
        XCTAssertEqual(snap.completedWaiterCount, 0)
        XCTAssertTrue(snap.waiterFates.isEmpty,
                      "open-drained waiters must not retain per-token fates")
        XCTAssertEqual(snap.waiterFateCounts[.openedWhileParked] ?? 0, n)
        XCTAssertEqual(snap.waiterResumeCounts[.parkedResumedByOpen] ?? 0, n)
        // Unconditional teardown, then drain all open-resumed tasks.
        await gate.close()
        for t in tasks { await t.value }
    }

    /// Latch consumption prunes per-token fate atomically.
    /// After a signal-before-park latch is consumed by awaitObserver,
    /// (a) the token has no fate entry, (b) the token has no
    /// completedObservers entry, (c) waitForObserverParked returns
    /// `.closedOrConsumed` (previously issued, no longer active),
    /// (d) aggregate signaledBeforePark + latchConsumed both == 1.
    func testAsyncGateLatchConsumedPrunesFatePerToken() async {
        let gate = AsyncGate()
        _ = await gate.registerWaiter()            // seed pendingEntrySignals=1
        let token = await gate.registerObserver()  // latch .signaledBeforePark

        let mid = await gate.snapshot()
        XCTAssertEqual(mid.completedObserverCount, 1)
        XCTAssertEqual(mid.observerFates[token.id], .signaledBeforePark)

        await gate.awaitObserver(token)             // consume the latch

        let snap = await gate.snapshot()
        XCTAssertEqual(snap.completedObserverCount, 0,
                       "consumed latch must not remain in completedObservers")
        XCTAssertNil(snap.observerFates[token.id],
                     "consumed latch must remove per-token fate")
        XCTAssertTrue(snap.observerFates.isEmpty)
        XCTAssertEqual(snap.observerFateCounts[.signaledBeforePark] ?? 0, 1)
        XCTAssertEqual(snap.observerResumeCounts[.latchConsumed] ?? 0, 1)

        // Post-consumption waitForXParked is bounded closedOrConsumed
        // (id < nextID but not active).
        let ack = await gate.waitForObserverParked(token)
        XCTAssertEqual(ack, .closedOrConsumed)
        await gate.close()
    }

    /// close() with a mix of actually-parked and unconsumed-latched
    /// tokens clears ALL per-token maps. Second close() is idempotent —
    /// no aggregate counter growth.
    func testAsyncGateCloseWithParkedAndLatchedClearsAllMaps() async {
        let gate = AsyncGate()
        // Step 1: register a waiter first. observerOrder is empty, so
        // its signal accumulates into pendingEntrySignals=1 and the
        // waiter itself lands in waiterOrder.
        let wat = await gate.registerWaiter()

        // Step 2: register an observer while a pending signal exists →
        // it latches as .signaledBeforePark and is NOT parked. This is
        // the "unconsumed latched" token the F1 spec calls out.
        let obsLatched = await gate.registerObserver()

        // Step 3: register another observer with no pending signal →
        // goes into observerOrder and will actually park when awaited.
        let obsParked = await gate.registerObserver()

        // Step 4: spawn awaiting tasks for the two that should park
        // (waiter + obsParked). Do NOT spawn one for obsLatched — the
        // whole point is to leave that latch UNCONSUMED at close().
        let watTask = Task { await gate.awaitWaiter(wat) }
        let obsTask = Task { await gate.awaitObserver(obsParked) }

        _ = await gate.waitForWaiterParked(wat)
        _ = await gate.waitForObserverParked(obsParked)

        // Snapshot before close proves the mixed state.
        let midBeforeClose = await gate.snapshot()
        XCTAssertEqual(midBeforeClose.parkedWaiterCount, 1)
        XCTAssertEqual(midBeforeClose.parkedObserverCount, 1)
        XCTAssertEqual(midBeforeClose.completedObserverCount, 1,
                       "obsLatched has an unconsumed latch")
        XCTAssertEqual(midBeforeClose.observerFates[obsLatched.id], .signaledBeforePark)

        let aggBefore = (
            closedWhileParkedObs: midBeforeClose.observerFateCounts[.closedWhileParked] ?? 0,
            closedWhileParkedWat: midBeforeClose.waiterFateCounts[.closedWhileParked] ?? 0,
            signaledBeforeParkObs: midBeforeClose.observerFateCounts[.signaledBeforePark] ?? 0
        )

        await gate.close()
        await watTask.value
        await obsTask.value

        let snap = await gate.snapshot()
        XCTAssertTrue(snap.closed)
        XCTAssertEqual(snap.parkedObserverCount, 0)
        XCTAssertEqual(snap.parkedWaiterCount, 0)
        XCTAssertEqual(snap.observerOrder, [])
        XCTAssertEqual(snap.waiterOrder, [])
        XCTAssertEqual(snap.completedObserverCount, 0,
                       "close must clear completedObservers (even unconsumed latches)")
        XCTAssertEqual(snap.completedWaiterCount, 0)
        XCTAssertTrue(snap.observerFates.isEmpty,
                      "close must clear ALL per-token observerFates")
        XCTAssertTrue(snap.waiterFates.isEmpty,
                      "close must clear ALL per-token waiterFates")
        // Aggregate reflects one parked drain per token type.
        XCTAssertEqual(
            snap.observerFateCounts[.closedWhileParked] ?? 0,
            aggBefore.closedWhileParkedObs + 1
        )
        XCTAssertEqual(
            snap.waiterFateCounts[.closedWhileParked] ?? 0,
            aggBefore.closedWhileParkedWat + 1
        )
        // Prior latch aggregate is preserved (not double-counted).
        XCTAssertEqual(
            snap.observerFateCounts[.signaledBeforePark] ?? 0,
            aggBefore.signaledBeforeParkObs
        )

        // Second close is idempotent — aggregate counters unchanged.
        let before2 = snap
        await gate.close()
        let after2 = await gate.snapshot()
        XCTAssertEqual(after2.observerFateCounts, before2.observerFateCounts,
                       "second close must not change aggregate counters")
        XCTAssertEqual(after2.waiterFateCounts, before2.waiterFateCounts)
        XCTAssertEqual(after2.observerResumeCounts, before2.observerResumeCounts)
        XCTAssertEqual(after2.waiterResumeCounts, before2.waiterResumeCounts)
        XCTAssertTrue(after2.observerFates.isEmpty)
        XCTAssertTrue(after2.waiterFates.isEmpty)
    }

    // MARK: - Load Alerts

    func testLoadAlertsPopulatesData() async {
        let alert = PredictiveAlert(
            alertType: "maintenance_overdue",
            severity: "warning",
            message: "Maintenance is overdue for Prusa MK3",
            recommendedAction: "Schedule maintenance immediately"
        )
        mockPredictiveService.alertsToReturn = [alert]

        await viewModel.loadAlerts(printerId: testPrinterId)

        XCTAssertEqual(viewModel.alerts.count, 1)
        XCTAssertEqual(viewModel.alerts.first?.alertType, "maintenance_overdue")
        XCTAssertEqual(viewModel.alerts.first?.severity, "warning")
        XCTAssertFalse(viewModel.isLoading)
        XCTAssertNil(viewModel.error)
        XCTAssertTrue(mockPredictiveService.getActiveAlertsCalled)
        XCTAssertEqual(mockPredictiveService.getActiveAlertsCalledWithPrinterId, testPrinterId)
    }

    func testLoadAlertsHandlesError() async {
        viewModel.alerts = [
            PredictiveAlert(
                alertType: "previous_alert",
                severity: "info",
                message: "Previously loaded alert",
                recommendedAction: "Keep monitoring"
            )
        ]
        viewModel.error = "prior-predictive-alerts-error-sentinel"
        mockPredictiveService.errorToThrow = TestError.generic

        await viewModel.loadAlerts(printerId: testPrinterId)

        // loadAlerts() is a secondary load: it logs via `logger.warning`
        // and does not populate `viewModel.error` so a background alerts hiccup
        // never blocks the primary prediction UI or clears prior alerts.
        // Seeding a nonnil sentinel proves the secondary path preserves both
        // prior alerts and the primary error channel.
        XCTAssertTrue(mockPredictiveService.getActiveAlertsCalled)
        XCTAssertEqual(mockPredictiveService.getActiveAlertsCalledWithPrinterId, testPrinterId)
        XCTAssertEqual(viewModel.alerts.count, 1)
        XCTAssertEqual(viewModel.alerts.first?.alertType, "previous_alert")
        XCTAssertEqual(viewModel.error, "prior-predictive-alerts-error-sentinel")
        XCTAssertFalse(viewModel.isLoading)
    }

    // MARK: - Load Forecasts

    func testLoadForecastsPopulatesData() async {
        let forecast = MaintenanceForecast(
            printerId: testPrinterId,
            printerName: "Prusa MK3",
            upcomingTasks: [
                ForecastTask(
                    taskName: "Nozzle Replacement",
                    estimatedDaysUntilDue: 7,
                    priority: "high"
                )
            ]
        )
        mockPredictiveService.forecastsToReturn = [forecast]
        
        await viewModel.loadForecasts(printerId: testPrinterId)
        
        XCTAssertEqual(viewModel.forecasts.count, 1)
        XCTAssertEqual(viewModel.forecasts.first?.printerId, testPrinterId)
        XCTAssertEqual(viewModel.forecasts.first?.upcomingTasks.count, 1)
        XCTAssertEqual(viewModel.forecasts.first?.upcomingTasks.first?.taskName, "Nozzle Replacement")
        XCTAssertFalse(viewModel.isLoading)
        XCTAssertNil(viewModel.error)
        XCTAssertEqual(mockPredictiveService.getMaintenanceForecastCalledWith, 30)
        XCTAssertEqual(mockPredictiveService.getMaintenanceForecastCalledWithPrinterId, testPrinterId)
    }
    
    func testLoadForecastsHandlesError() async {
        viewModel.forecasts = [
            MaintenanceForecast(
                printerId: testPrinterId,
                printerName: "Previous Printer",
                upcomingTasks: []
            )
        ]
        viewModel.error = "prior-predictive-forecasts-error-sentinel"
        mockPredictiveService.errorToThrow = TestError.generic
        
        await viewModel.loadForecasts(printerId: testPrinterId)
        
        // loadForecasts() is a secondary load: it logs via `logger.warning`
        // and preserves prior forecasts without surfacing `viewModel.error`.
        // Seeding a nonnil sentinel proves the secondary path preserves both
        // prior forecasts and the primary error channel.
        XCTAssertEqual(mockPredictiveService.getMaintenanceForecastCalledWith, 30)
        XCTAssertEqual(mockPredictiveService.getMaintenanceForecastCalledWithPrinterId, testPrinterId)
        XCTAssertEqual(viewModel.forecasts.count, 1)
        XCTAssertEqual(viewModel.forecasts.first?.printerName, "Previous Printer")
        XCTAssertEqual(viewModel.error, "prior-predictive-forecasts-error-sentinel")
        XCTAssertFalse(viewModel.isLoading)
    }
    
    // MARK: - Computed Properties
    
    func testRiskPercentageConvertsTo0To100Scale() {
        viewModel.prediction = JobFailurePrediction(
            printerId: testPrinterId,
            material: nil,
            estimatedDurationMinutes: nil,
            predictedFailureLikelihood: 0.0,
            riskLevel: "low",
            factors: []
        )
        XCTAssertEqual(viewModel.riskPercentage, 0)
        
        viewModel.prediction = JobFailurePrediction(
            printerId: testPrinterId,
            material: nil,
            estimatedDurationMinutes: nil,
            predictedFailureLikelihood: 25.0,
            riskLevel: "low",
            factors: []
        )
        XCTAssertEqual(viewModel.riskPercentage, 25)
        
        viewModel.prediction = JobFailurePrediction(
            printerId: testPrinterId,
            material: nil,
            estimatedDurationMinutes: nil,
            predictedFailureLikelihood: 50.0,
            riskLevel: "moderate",
            factors: []
        )
        XCTAssertEqual(viewModel.riskPercentage, 50)
        
        viewModel.prediction = JobFailurePrediction(
            printerId: testPrinterId,
            material: nil,
            estimatedDurationMinutes: nil,
            predictedFailureLikelihood: 75.0,
            riskLevel: "high",
            factors: []
        )
        XCTAssertEqual(viewModel.riskPercentage, 75)
        
        viewModel.prediction = JobFailurePrediction(
            printerId: testPrinterId,
            material: nil,
            estimatedDurationMinutes: nil,
            predictedFailureLikelihood: 100.0,
            riskLevel: "critical",
            factors: []
        )
        XCTAssertEqual(viewModel.riskPercentage, 100)
    }
    
    func testRiskPercentageReturnsZeroWhenNoPrediction() {
        viewModel.prediction = nil
        
        XCTAssertEqual(viewModel.riskPercentage, 0)
    }
    
    func testRiskLevelReturnsCorrectLevel() {
        viewModel.prediction = JobFailurePrediction(
            printerId: testPrinterId,
            material: nil,
            estimatedDurationMinutes: nil,
            predictedFailureLikelihood: 10.0,
            riskLevel: "low",
            factors: []
        )
        XCTAssertEqual(viewModel.riskLevel, "Low")
        
        viewModel.prediction = JobFailurePrediction(
            printerId: testPrinterId,
            material: nil,
            estimatedDurationMinutes: nil,
            predictedFailureLikelihood: 30.0,
            riskLevel: "moderate",
            factors: []
        )
        XCTAssertEqual(viewModel.riskLevel, "Moderate")
        
        viewModel.prediction = JobFailurePrediction(
            printerId: testPrinterId,
            material: nil,
            estimatedDurationMinutes: nil,
            predictedFailureLikelihood: 60.0,
            riskLevel: "high",
            factors: []
        )
        XCTAssertEqual(viewModel.riskLevel, "High")
        
        viewModel.prediction = JobFailurePrediction(
            printerId: testPrinterId,
            material: nil,
            estimatedDurationMinutes: nil,
            predictedFailureLikelihood: 85.0,
            riskLevel: "critical",
            factors: []
        )
        XCTAssertEqual(viewModel.riskLevel, "Critical")
    }
    
    func testRiskLevelReturnsUnavailableWhenNoPrediction() {
        // Regression guard for #808: nil prediction must NOT map to "Low",
        // which is what allowed transport/decode failures to render as a
        // benign successful result.
        viewModel.prediction = nil

        XCTAssertEqual(viewModel.riskLevel, "Unavailable")
    }
    
    // MARK: - Unconfigured Guard
    
    func testPredictFailureDoesNothingWhenUnconfigured() async {
        viewModel = PredictiveViewModel()
        
        await viewModel.predictFailure(printerId: testPrinterId, material: "PLA", duration: 3600)
        
        XCTAssertNil(viewModel.prediction)
        XCTAssertNil(mockPredictiveService.predictJobFailureCalledWith)
    }
    
    func testLoadAlertsDoesNothingWhenUnconfigured() async {
        viewModel = PredictiveViewModel()
        
        await viewModel.loadAlerts(printerId: testPrinterId)
        
        XCTAssertTrue(viewModel.alerts.isEmpty)
        XCTAssertFalse(mockPredictiveService.getActiveAlertsCalled)
    }
    
    func testLoadForecastsDoesNothingWhenUnconfigured() async {
        viewModel = PredictiveViewModel()
        
        await viewModel.loadForecasts(printerId: testPrinterId)
        
        XCTAssertTrue(viewModel.forecasts.isEmpty)
        XCTAssertNil(mockPredictiveService.getMaintenanceForecastCalledWith)
    }
}

/// Cancellation-aware, tokenized suspension gate used to hold a request in
/// flight across a real suspension point (never `Task.sleep`/yield). Every
/// pending continuation — waiters (parked in `wait()`) and entry observers
/// (parked in `waitForEntry()`) — is tracked by a monotonic token so that
/// `open()`, `close()` (teardown), and per-task cancellation each remove
/// and resume that continuation exactly once.
///
/// Public convenience API (`wait()` / `waitForEntry()`) is unchanged. The
/// underlying two-phase API (`registerObserver`/`awaitObserver`,
/// `registerWaiter`/`awaitWaiter`) is exposed for tests that need to prove
/// strict actor-serialised ordering without relying on `async let`
/// scheduler races.
///
/// Guarantees (all enforced by actor serialisation of state mutations):
///   * exactly-once continuation resume — the continuation is removed
///     from its dictionary before `resume()` is called on it;
///   * cancellation-safe — an in-flight `wait()`/`waitForEntry()` whose
///     Task is cancelled resumes the parked continuation through the
///     structural `onCancel` handler, so scope teardown never leaks;
///   * lost-wakeup-safe — signals delivered before the observer parks are
///     accumulated in `pendingEntrySignals`, and completed-before-await
///     tokens are latched in `completedObservers` / `completedWaiters`;
///   * FIFO fairness — one entry signal wakes exactly one observer, in
///     the order in which the observers were registered;
///   * idempotent `open()` — `opened` latches true; a second `open()`
///     drains an empty queue and is a no-op;
///   * explicit teardown — `close()` drains BOTH pending waiters and
///     pending entry observers exactly once so tests never hang on a
///     helper regression.
// MARK: - Hicks R1/R2 addendum: caller-owned attempt + ticket objects
//
// R1: `AwaitAttempt` replaces the gate-level lazy receipt cache. Each
// await invocation constructs one and passes it in; onCancel atomically
// commits the attempt to `.cancellationInitiated` via `beginCancellationIfActive`
// and (if it wins) launches a `Task<Receipt, Never>` that hops to the actor
// and publishes the exact receipt to the attempt's buffered outcome latch.
// The gate itself stores no per-attempt result map; `cancelX(id:, awaitID:)`
// simply RETURNS the receipt as its function value. Tests await
// `attempt.outcome()` to observe the exact per-attempt outcome — it is the
// sole per-attempt receipt API.
//
// R2: `ParkAckTicket` replaces the gate-level ticket-to-token/result map.
// `enterXParkAck` returns a caller-owned ticket object (already-resolved
// for terminal classifications, buffered-resolvable for active tokens).
// The actor stores only weak-ish references (in `observerActiveParkAckTickets`
// keyed by token id) — resolved and removed at park/fate/close. Callers
// consume via `ticket.value()` which returns the buffered result even if
// resolution beat the consumer.

/// R1: caller-owned per-attempt cancellation context for observer awaits.
///
/// Hicks H1 (Vasquez remediation): the attempt now owns an ATOMIC
/// lifecycle state machine plus a one-shot buffered outcome latch.
/// Only one publisher wins:
///
///   - `.active` -> `.cancellationInitiated` — under lock, in onCancel,
///     BEFORE any Task is launched. If this transition fails (state was
///     already `.completedNaturally`), onCancel dispatches nothing.
///   - `.active` -> `.completedNaturally` — under lock, on the natural
///     completion path, only if state is still `.active`. If this
///     transition fails (state was already `.cancellationInitiated`),
///     the natural completion path does not publish; the cancel Task
///     is the sole publisher.
///
/// The buffered outcome latch is the authoritative per-attempt receipt.
/// Consumers await `attempt.outcome()` for the exact result — the sole
/// per-attempt receipt API. The legacy `cancelTask` accessor is not
/// exposed; using `outcome()` avoids the install-window race that a
/// direct Task-handle read would create.
final class ObserverAwaitAttempt: @unchecked Sendable {
    let id: UUID

    /// Terminal outcome kinds. `finishedBeforeProcessing` = the await
    /// returned naturally without cancellation. `cancelled(receipt)` =
    /// cancellation was dispatched and the actor returned an exact
    /// per-attempt cancel receipt.
    fileprivate enum Outcome: Sendable, Equatable {
        case finishedBeforeProcessing
        case cancelled(AsyncGate.ObserverCancelReceipt)
    }

    /// Lifecycle state. Monotonic — only `.active` transitions.
    fileprivate enum State { case active, cancellationInitiated, completedNaturally }

    private let lock = NSLock()
    private var _state: State = .active
    private var _outcome: Outcome?
    private var _outcomeCont: CheckedContinuation<Outcome, Never>?

    init() { self.id = UUID() }

    /// Atomically transition `.active` -> `.cancellationInitiated`.
    /// Returns `true` iff the caller now owns cancellation dispatch.
    /// Returns `false` if natural completion has already published;
    /// the caller MUST NOT launch a cancel Task in that case.
    fileprivate func beginCancellationIfActive() -> Bool {
        lock.lock(); defer { lock.unlock() }
        guard _state == .active else { return false }
        _state = .cancellationInitiated
        return true
    }

    /// Atomically transition `.active` -> `.completedNaturally`.
    /// Returns `true` iff the natural completion path owns publication.
    /// Returns `false` if cancellation was already initiated; the
    /// cancel Task is the sole publisher.
    fileprivate func markCompletedNaturallyIfActive() -> Bool {
        lock.lock(); defer { lock.unlock() }
        guard _state == .active else { return false }
        _state = .completedNaturally
        return true
    }

    /// Exactly-once publisher. Called by whichever path won the state
    /// race. Buffers the outcome and resumes any queued `outcome()` caller.
    fileprivate func resolveOutcome(_ o: Outcome) {
        var cont: CheckedContinuation<Outcome, Never>?
        lock.lock()
        if _outcome == nil {
            _outcome = o
            cont = _outcomeCont
            _outcomeCont = nil
        }
        lock.unlock()
        cont?.resume(returning: o)
    }

    /// Authoritative per-attempt receipt. Returns the buffered outcome
    /// immediately if resolved, else queues one continuation that the
    /// publisher resumes. Coordinator remediation: this is the ONLY
    /// authoritative per-attempt receipt API. The legacy `cancelTask`
    /// accessor was removed because it had an install-window race with
    /// the Task's own body; `outcome()` does not.
    fileprivate func outcome() async -> Outcome {
        return await withCheckedContinuation { c in
            var immediate: Outcome?
            lock.lock()
            if let o = _outcome { immediate = o } else { _outcomeCont = c }
            lock.unlock()
            if let o = immediate { c.resume(returning: o) }
        }
    }

    /// True iff the state gate published natural completion.
    fileprivate var completedNaturally: Bool {
        lock.lock(); defer { lock.unlock() }
        return _state == .completedNaturally
    }

    /// Diagnostic: peek current state (for H1 publication-race tests).
    fileprivate var stateForTest: State {
        lock.lock(); defer { lock.unlock() }
        return _state
    }

    /// Diagnostic: peek buffered outcome without consuming.
    fileprivate var bufferedOutcomeForTest: Outcome? {
        lock.lock(); defer { lock.unlock() }
        return _outcome
    }
}

/// R1 waiter analogue of `ObserverAwaitAttempt`. See there for design.
final class WaiterAwaitAttempt: @unchecked Sendable {
    let id: UUID

    fileprivate enum Outcome: Sendable, Equatable {
        case finishedBeforeProcessing
        case cancelled(AsyncGate.WaiterCancelReceipt)
    }

    fileprivate enum State { case active, cancellationInitiated, completedNaturally }

    private let lock = NSLock()
    private var _state: State = .active
    private var _outcome: Outcome?
    private var _outcomeCont: CheckedContinuation<Outcome, Never>?

    init() { self.id = UUID() }

    fileprivate func beginCancellationIfActive() -> Bool {
        lock.lock(); defer { lock.unlock() }
        guard _state == .active else { return false }
        _state = .cancellationInitiated
        return true
    }

    fileprivate func markCompletedNaturallyIfActive() -> Bool {
        lock.lock(); defer { lock.unlock() }
        guard _state == .active else { return false }
        _state = .completedNaturally
        return true
    }

    fileprivate func resolveOutcome(_ o: Outcome) {
        var cont: CheckedContinuation<Outcome, Never>?
        lock.lock()
        if _outcome == nil {
            _outcome = o
            cont = _outcomeCont
            _outcomeCont = nil
        }
        lock.unlock()
        cont?.resume(returning: o)
    }

    /// Authoritative per-attempt receipt. See ObserverAwaitAttempt.
    fileprivate func outcome() async -> Outcome {
        return await withCheckedContinuation { c in
            var immediate: Outcome?
            lock.lock()
            if let o = _outcome { immediate = o } else { _outcomeCont = c }
            lock.unlock()
            if let o = immediate { c.resume(returning: o) }
        }
    }

    fileprivate var completedNaturally: Bool {
        lock.lock(); defer { lock.unlock() }
        return _state == .completedNaturally
    }

    fileprivate var stateForTest: State {
        lock.lock(); defer { lock.unlock() }
        return _state
    }

    fileprivate var bufferedOutcomeForTest: Outcome? {
        lock.lock(); defer { lock.unlock() }
        return _outcome
    }
}

/// R2: caller-owned one-shot buffered park-ACK ticket. Constructed by
/// `enterObserverParkAck`; either returned already-resolved (terminal
/// classification available at enter time) or handed to the actor to be
/// resolved later at park/fate/close. Exactly-once: subsequent
/// `resolve(_)` calls are silently ignored. `value()` returns the
/// buffered result immediately if already resolved, or queues one
/// continuation that the actor resumes at resolution. Never-awaited
/// tickets are removed from the gate at resolution and simply dropped
/// by the caller — no gate storage survives resolution.
final class ObserverParkAckTicket: @unchecked Sendable {
    private let lock = NSLock()
    private var _result: AsyncGate.ParkAckResult?
    private var _cont: CheckedContinuation<AsyncGate.ParkAckResult, Never>?
    init() {}
    /// Called by the actor at park / fate seal / close. Buffers the
    /// first outcome and resumes any queued consumer.
    fileprivate func resolve(_ r: AsyncGate.ParkAckResult) {
        var contToResume: CheckedContinuation<AsyncGate.ParkAckResult, Never>?
        lock.lock()
        if _result == nil {
            _result = r
            contToResume = _cont
            _cont = nil
        }
        lock.unlock()
        contToResume?.resume(returning: r)
    }
    /// Consumer API. Returns the buffered result immediately if already
    /// resolved, else queues one continuation that the actor resumes.
    fileprivate func value() async -> AsyncGate.ParkAckResult {
        return await withCheckedContinuation { c in
            var immediate: AsyncGate.ParkAckResult?
            lock.lock()
            if let r = _result {
                immediate = r
            } else {
                _cont = c
            }
            lock.unlock()
            if let r = immediate { c.resume(returning: r) }
        }
    }
    /// Peek without consuming — useful for tests that verify a ticket
    /// was resolved by the actor before the consumer awaited it.
    fileprivate var isResolved: Bool {
        lock.lock(); defer { lock.unlock() }
        return _result != nil
    }
}

/// R2 waiter analogue of `ObserverParkAckTicket`.
final class WaiterParkAckTicket: @unchecked Sendable {
    private let lock = NSLock()
    private var _result: AsyncGate.ParkAckResult?
    private var _cont: CheckedContinuation<AsyncGate.ParkAckResult, Never>?
    init() {}
    fileprivate func resolve(_ r: AsyncGate.ParkAckResult) {
        var contToResume: CheckedContinuation<AsyncGate.ParkAckResult, Never>?
        lock.lock()
        if _result == nil {
            _result = r
            contToResume = _cont
            _cont = nil
        }
        lock.unlock()
        contToResume?.resume(returning: r)
    }
    fileprivate func value() async -> AsyncGate.ParkAckResult {
        return await withCheckedContinuation { c in
            var immediate: AsyncGate.ParkAckResult?
            lock.lock()
            if let r = _result {
                immediate = r
            } else {
                _cont = c
            }
            lock.unlock()
            if let r = immediate { c.resume(returning: r) }
        }
    }
    fileprivate var isResolved: Bool {
        lock.lock(); defer { lock.unlock() }
        return _result != nil
    }
}

/// Hicks H2: weakly-held box for gate-side ticket storage. When the
/// caller drops the sole strong reference to an active-unparked ticket
/// (never awaits it), the box's `ref` becomes nil; the actor compacts
/// nil boxes on every `enterX`, resolve/flush, and snapshot. Repeated
/// enter/drop on an active token that never parks therefore stays
/// bounded — no unbounded array growth. Live tickets remain strongly
/// held by the caller and are lossless.
private final class WeakObserverParkAckTicketBox {
    weak var ref: ObserverParkAckTicket?
    init(_ t: ObserverParkAckTicket) { self.ref = t }
}

private final class WeakWaiterParkAckTicketBox {
    weak var ref: WaiterParkAckTicket?
    init(_ t: WaiterParkAckTicket) { self.ref = t }
}

private actor AsyncGate {
    struct ObserverToken: Sendable, Hashable { let id: UInt64 }
    struct WaiterToken: Sendable, Hashable { let id: UInt64 }

    /// The single terminal outcome for a token. Set exactly once, when
    /// the token's fate is sealed — either by a signal/open/close event
    /// or by cancellation. Reported through `Snapshot` so tests can
    /// assert exact resume reasons instead of inferring from counts.
    enum ResumeReason: Sendable, Hashable {
        case signaledBeforePark   // observer latched via signal (registerObserver consumed pending, or signalEntry ran while registered)
        case signaledWhileParked  // signalEntry resumed a parked observer
        case openedBeforePark     // waiter latched via open() before await parked, or registered after open
        case openedWhileParked    // open() resumed a parked waiter
        case closedBeforePark     // register* after close, or close() latched a registered-but-not-parked token
        case closedWhileParked    // close() resumed a parked continuation
        case cancelledWhileParked // cancelX drained a parked continuation
    }

    /// Hicks A: `waitForXParked` result. Every non-parkable input state
    /// returns immediately with an explicit reason instead of queueing
    /// forever. `parked` and `terminal(_)` cover the resolved cases;
    /// `unknown` covers a token this gate never issued (or already
    /// scrubbed post-consumption); `closedOrConsumed` covers a token
    /// registered against a gate that has since been closed without
    /// producing a fate map entry.
    enum ParkAckResult: Sendable, Hashable {
        case parked
        case terminal(ResumeReason)
        case unknown
        case closedOrConsumed
    }

    /// Hicks C: labels every *actual* `CheckedContinuation.resume()` site
    /// on the observer side. Distinct from `ResumeReason` (which labels
    /// the terminal state seal — sealing does not always immediately
    /// resume a real continuation, e.g. `signaledBeforePark` creates a
    /// LATCH that a later `awaitObserver` consumes).
    enum ObserverResumeSite: Sendable, Hashable {
        case parkedResumedBySignal   // signalEntry drained a parked continuation
        case parkedResumedByClose    // close() drained a parked continuation
        case parkedResumedByCancel   // cancelObserver drained the matched parked continuation
        case latchConsumed           // awaitObserver branch (1) consumed a completedObservers latch
        case duplicateAfterFated     // awaitObserver branch (1) with no latch: token already terminal
        case duplicateAfterParked    // awaitObserver branch (2): token currently parked
        case unknownToken            // awaitObserver branch (3): id not registered
        case closedImmediate         // awaitObserver branch (4): registered-then-closed drain-in-await
    }

    /// Waiter analogue of `ObserverResumeSite`.
    enum WaiterResumeSite: Sendable, Hashable {
        case parkedResumedByOpen
        case parkedResumedByClose
        case parkedResumedByCancel
        case latchConsumed
        case duplicateAfterFated
        case duplicateAfterParked
        case unknownToken
        case closedImmediate
        case openedImmediate        // awaitWaiter branch: opened-since-register drain-in-await
    }

    /// Hicks R1 addendum: exact per-attempt cancellation receipt. Each
    /// await attempt owns a caller-supplied `AwaitAttempt` context that
    /// carries the awaitID; `cancelObserver`/`cancelWaiter` returns one
    /// of these outcomes as its function value (no gate-side storage).
    /// The onCancel body atomically commits the attempt to
    /// `.cancellationInitiated` and launches a cancel Task that publishes
    /// the receipt to the attempt's buffered outcome latch. Consumers
    /// await `attempt.outcome()` for the definitive per-attempt outcome.
    /// `finishedBeforeProcessing` is emitted by the natural-completion
    /// path when the state gate is still `.active` at body return.
    enum ObserverCancelReceipt: Sendable, Hashable {
        case processedMatched              // cancelObserver drained THIS awaitID's parked continuation
        case processedIgnoredMismatch      // cancelObserver ran but this awaitID was not parked (or another awaitID was)
        case finishedBeforeProcessing      // awaitObserver body completed with no cancel firing
        case closedBeforeProcessing        // close() drained the receipt waiter before any cancel outcome
    }

    /// Waiter analogue of `ObserverCancelReceipt`.
    enum WaiterCancelReceipt: Sendable, Hashable {
        case processedMatched
        case processedIgnoredMismatch
        case finishedBeforeProcessing
        case closedBeforeProcessing
    }

    /// Snapshot of gate state. All prior fields preserved for existing
    /// tests; new fields (fates, reason counts, edge counters) expose
    /// deterministic evidence for cancel/duplicate/unknown handling.
    struct Snapshot: Sendable, Equatable {
        let pendingEntrySignals: Int
        let observerOrder: [UInt64]
        let waiterOrder: [UInt64]
        let completedObserverCount: Int
        let completedWaiterCount: Int
        let parkedObserverCount: Int
        let parkedWaiterCount: Int
        let opened: Bool
        let closed: Bool

        // Per-token fates and roll-up counts (H1+H3 evidence).
        let observerFates: [UInt64: ResumeReason]
        let waiterFates: [UInt64: ResumeReason]
        let observerFateCounts: [ResumeReason: Int]
        let waiterFateCounts: [ResumeReason: Int]

        // Bounded no-op counters — mutations that must NOT alter state.
        let observerCancelIgnoredCount: Int
        let waiterCancelIgnoredCount: Int
        let observerDuplicateAwaitCount: Int
        let waiterDuplicateAwaitCount: Int
        let observerUnknownAwaitCount: Int
        let waiterUnknownAwaitCount: Int

        // Hicks B: post-close registration attempts do NOT insert per-token
        // maps; they bump these aggregate counters instead.
        let observerPostCloseRegistrationCount: Int
        let waiterPostCloseRegistrationCount: Int

        // Hicks C: per-site actual-resume counters — one increment per
        // `CheckedContinuation.resume()` call, keyed by the site.
        let observerResumeCounts: [ObserverResumeSite: Int]
        let waiterResumeCounts: [WaiterResumeSite: Int]

        // Hicks D: number of times `cancelObserver`/`cancelWaiter` has
        // been dispatched (whether it drained a matched entry or fell
        // into the bounded-no-op branch). Used by `waitForXCancelCount`
        // ACK helpers to prove structural ordering.
        let observerCancelInvocationCount: Int
        let waiterCancelInvocationCount: Int

        // Sizes of internal per-token queues that MUST remain bounded
        // (never grow with post-close activity).
        let observerParkAckQueueTotal: Int
        let waiterParkAckQueueTotal: Int
        let observerCancelCountAckQueueTotal: Int
        let waiterCancelCountAckQueueTotal: Int

        // Hicks R1 addendum: gate no longer stores per-attempt cancel
        // receipts (that state now lives in caller-owned `AwaitAttempt`
        // objects). These snapshot fields are RETAINED at fixed zero so
        // long-standing tests reading them continue to compile against
        // the same field set; they are guaranteed zero because the gate
        // has no per-attempt storage to grow.

        // Hicks R2 addendum: `observerParkAckTicketCount` /
        // `waiterParkAckTicketCount` now count ACTIVE park-ACK
        // tickets (sum of `observerActiveParkAckTickets` array
        // lengths), i.e. tickets reserved via `enterXParkAck` that
        // the actor has NOT yet resolved. Resolved tickets are
        // removed from actor storage immediately, whether or not the
        // caller has consumed the buffered result.
        let observerParkAckTicketCount: Int
        let waiterParkAckTicketCount: Int
        // Hicks H2 (coordinator criterion 2): backing-storage metrics.
        // `*BackingKeyCount` reports the number of dictionary keys still
        // present in `observerActiveParkAckTickets` / `waiterActiveParkAckTickets`
        // AFTER compaction. Because compaction removes any bucket that becomes
        // empty (all dead boxes pruned), a value of 0 proves the backing dict
        // holds NO entries — not merely that live tickets are gone.
        // Live count == 0 alone could mask a dict entry holding only dead boxes;
        // key count == 0 rules that out.
        let observerParkAckTicketBackingKeyCount: Int
        let waiterParkAckTicketBackingKeyCount: Int
    }

    // MARK: Debug metrics (test-only)
    // Hicks H2 (coordinator criterion 2): expose RAW backing storage counts
    // WITHOUT triggering compaction. Callable AFTER `snapshot()` (which does
    // compact) to prove that even the raw box arrays are empty at that
    // moment. If snapshot() reports 0 but debugRaw* reports >0, that would
    // reveal a compaction correctness bug. Both must be 0 to prove the
    // gate holds no per-ticket state after drop.
    func debugRawObserverParkAckBoxCount() -> Int {
        observerActiveParkAckTickets.values.reduce(0) { $0 + $1.count }
    }
    func debugRawWaiterParkAckBoxCount() -> Int {
        waiterActiveParkAckTickets.values.reduce(0) { $0 + $1.count }
    }
    func debugRawObserverParkAckKeyCount() -> Int {
        observerActiveParkAckTickets.count
    }
    func debugRawWaiterParkAckKeyCount() -> Int {
        waiterActiveParkAckTickets.count
    }

    // MARK: State
    // Hicks addendum Point 5: kind-correct issuance high-water counters.
    // Prior single `nextID` allowed cross-kind fabricated-token
    // misclassification (an ObserverToken(id:) with an id only ever
    // issued to a waiter would satisfy `id < nextID` and be classified
    // as `.closedOrConsumed` / previously-issued observer). Separate
    // per-kind gap-free counters make `token.id >= nextObserverID` /
    // `token.id >= nextWaiterID` an EXACT non-issuance predicate for
    // that kind without any per-token set.
    private var nextObserverID: UInt64 = 1
    private var nextWaiterID: UInt64 = 1
    // Hicks H5 / addendum: non-reusing per-await identity. UUID guarantees
    // owner/receipt maps cannot alias a prior/wrapped attempt.
    private var parkedObservers: [UInt64: (awaitID: UUID, c: CheckedContinuation<Void, Never>)] = [:]
    private var completedObservers: Set<UInt64> = []
    private var observerOrder: [UInt64] = []
    private var parkedWaiters: [UInt64: (awaitID: UUID, c: CheckedContinuation<Void, Never>)] = [:]
    private var completedWaiters: Set<UInt64> = []
    private var waiterOrder: [UInt64] = []
    private var pendingEntrySignals: Int = 0
    private var opened = false
    private var closed = false

    // H1: per-token status prevents duplicate park; fates carry the reason.
    private var observerFates: [UInt64: ResumeReason] = [:]
    private var waiterFates: [UInt64: ResumeReason] = [:]
    private var observerFateCounts: [ResumeReason: Int] = [:]
    private var waiterFateCounts: [ResumeReason: Int] = [:]

    // Bounded no-op counters (H1: cancelIgnored/duplicate/unknown).
    private var observerCancelIgnoredCount = 0
    private var waiterCancelIgnoredCount = 0
    private var observerDuplicateAwaitCount = 0
    private var waiterDuplicateAwaitCount = 0
    private var observerUnknownAwaitCount = 0
    private var waiterUnknownAwaitCount = 0

    // H3 + Hicks A: park-ACK queues now yield an explicit `ParkAckResult`
    // so callers can distinguish parked / terminal / unknown / closed
    // without inference. Queue only for active-registered tokens.
    private var observerParkAcks: [UInt64: [CheckedContinuation<ParkAckResult, Never>]] = [:]
    private var waiterParkAcks: [UInt64: [CheckedContinuation<ParkAckResult, Never>]] = [:]

    // Hicks H3 + R2 addendum: two-phase pre-park ACK tickets.
    // `enterObserverParkAck` synchronously creates an
    // `ObserverParkAckTicket` object and, for an active-registered
    // token, appends it into `observerActiveParkAckTickets[id]`.
    // Because that method executes SYNCHRONOUSLY on the actor,
    // calling it twice before spawning any parking task GUARANTEES
    // both tickets were queued before parking could occur. On park /
    // fate seal / close, the actor calls `ticket.resolve(_)` and
    // REMOVES the ticket entries from the active-ticket map. Never-
    // awaited tickets are removed at the same moment; the caller
    // owns lifetime of the resolved-but-not-consumed ticket object.
    // No `UUID → tokenID` result-lookup map survives resolution.
    private var observerActiveParkAckTickets: [UInt64: [WeakObserverParkAckTicketBox]] = [:]
    private var waiterActiveParkAckTickets: [UInt64: [WeakWaiterParkAckTicketBox]] = [:]

    // Hicks R1 addendum: the gate NO LONGER stores per-attempt
    // cancellation receipts. Each await attempt carries its own
    // `AwaitAttempt` context; onCancel synchronously installs the
    // exact cancel Task handle into that context, and `cancelX`
    // returns the receipt as its function value (never persists it
    // etc. are RETAINED (fixed zero) so the Equatable/field-set stays
    // stable across the redesign.

    // Hicks B: post-close registration bounded aggregate counters.
    // Post-close registerX does NOT insert per-token maps.
    private var observerPostCloseRegistrationCount = 0
    private var waiterPostCloseRegistrationCount = 0

    // Hicks C: per-site actual-resume counters — one increment per real
    // `CheckedContinuation.resume()`. Distinct from fateCounts (seals).
    private var observerResumeCounts: [ObserverResumeSite: Int] = [:]
    private var waiterResumeCounts: [WaiterResumeSite: Int] = [:]

    // Hicks D: cancel-invocation counters + bounded ACK queues so tests
    // can structurally await a specific cancel dispatch without polling.
    private var observerCancelInvocationCount = 0
    private var waiterCancelInvocationCount = 0
    private var observerCancelCountAcks: [Int: [CheckedContinuation<Void, Never>]] = [:]
    private var waiterCancelCountAcks: [Int: [CheckedContinuation<Void, Never>]] = [:]

    // MARK: Observer (entry-side)

    /// Reserve an observer slot. Synchronous-in-actor.
    ///
    /// Hicks B: on a closed gate we bump a bounded aggregate counter
    /// only. We do NOT insert into `completedObservers` or seal a fate,
    /// because that would create a per-token map entry for every
    /// post-close attempt and grow linearly with the attack. Subsequent
    /// `awaitObserver(postCloseToken)` hits the branch-3 unknown path
    /// (also bounded), which increments `observerUnknownAwaitCount`.
    func registerObserver() -> ObserverToken {
        let id = nextObserverID; nextObserverID &+= 1
        if closed {
            observerPostCloseRegistrationCount += 1
        } else if pendingEntrySignals > 0 {
            pendingEntrySignals -= 1
            latchObserverBeforePark(id: id, reason: .signaledBeforePark)
        } else {
            observerOrder.append(id)
        }
        return ObserverToken(id: id)
    }

    /// Await the previously registered observer.
    ///
    /// H1 duplicate/unknown guard: this function inspects the token's
    /// current status and produces a deterministic non-hanging outcome
    /// for every case. It NEVER overwrites an already-parked
    /// continuation for the same token.
    ///
    /// Vasquez remediation: each invocation gets its own monotonic
    /// `awaitID` captured by the cancellation handler. `cancelObserver`
    /// mutates state ONLY when the currently parked entry's `awaitID`
    /// matches — so a duplicate (or unknown/fated) await whose Task is
    /// cancelled cannot cross-own and drain the ORIGINAL parked
    /// continuation for the same token id.
    ///
    /// Hicks C: every `c.resume()` bumps `observerResumeCounts[site]`
    /// once — actual-resume counters, distinct from fate seals.
    func awaitObserver(_ token: ObserverToken) async {
        await awaitObserver(token, attempt: ObserverAwaitAttempt())
    }

    /// Hicks R1: labeled overload that accepts a caller-owned
    /// `ObserverAwaitAttempt`. onCancel atomically commits the attempt
    /// via `beginCancellationIfActive` and launches a cancel Task that
    /// publishes the receipt to the attempt's outcome latch; callers/
    /// tests obtain the definitive per-attempt receipt by awaiting
    /// `attempt.outcome()`. On natural completion,
    /// `attempt.markCompletedNaturallyIfActive()` fires and publishes
    /// `.finishedBeforeProcessing`; the gate stores NO per-attempt outcome.
    func awaitObserver(_ token: ObserverToken, attempt: ObserverAwaitAttempt) async {
        let awaitID = attempt.id
        await withTaskCancellationHandler {
            await withCheckedContinuation { c in
                // (1) Before-park latch present → consume it and
                //     prune per-token state atomically. Bishop F1:
                //     both `completedObservers` and `observerFates`
                //     entries removed together so the token leaves
                //     no residual per-token history.
                if completedObservers.remove(token.id) != nil {
                    observerFates.removeValue(forKey: token.id)
                    observerResumeCounts[.latchConsumed, default: 0] += 1
                    c.resume()
                    return
                }
                // (2) Same token already has a live parked continuation.
                //     Second concurrent await is a bug; reject the second
                //     without touching the first parked continuation.
                if parkedObservers[token.id] != nil {
                    observerDuplicateAwaitCount += 1
                    observerResumeCounts[.duplicateAfterParked, default: 0] += 1
                    c.resume()
                    return
                }
                // (3) Currently registered (in observerOrder).
                if observerOrder.contains(token.id) {
                    if closed {
                        observerOrder.removeAll { $0 == token.id }
                        sealObserverOutcome(id: token.id, reason: .closedBeforePark)
                        observerResumeCounts[.closedImmediate, default: 0] += 1
                        c.resume()
                        return
                    }
                    parkedObservers[token.id] = (awaitID: awaitID, c: c)
                    flushObserverParkAcks(id: token.id, result: .parked)
                    return
                }
                // (4) Not active.
                if token.id >= nextObserverID {
                    observerUnknownAwaitCount += 1
                    observerResumeCounts[.unknownToken, default: 0] += 1
                } else {
                    observerDuplicateAwaitCount += 1
                    observerResumeCounts[.duplicateAfterFated, default: 0] += 1
                }
                c.resume()
            }
            // Hicks H1 (Vasquez remediation): natural completion
            // publishes ONLY if the state gate is still `.active`. If
            // cancellation was already initiated, the cancel Task is
            // the sole publisher — the natural completion path silently
            // yields to it.
            if attempt.markCompletedNaturallyIfActive() {
                attempt.resolveOutcome(.finishedBeforeProcessing)
            }
        } onCancel: {
            // Hicks H1: atomically claim publication BEFORE launching
            // any Task. If natural completion already published, do
            // nothing — otherwise a late-arriving cancel Task could
            // reverse the published outcome.
            guard attempt.beginCancellationIfActive() else { return }
            _ = Task<ObserverCancelReceipt, Never> {
                let receipt = await self.cancelObserver(id: token.id, awaitID: awaitID)
                attempt.resolveOutcome(.cancelled(receipt))
                return receipt
}
        }
    }

    /// Late-cancel-safe. Mutates state ONLY when a continuation is
    /// currently parked for this id AND the parked entry's `awaitID`
    /// matches this invocation's `awaitID`. Every other case is a
    /// bounded no-op that increments `observerCancelIgnoredCount` and
    /// never relatches into `completedObservers`, overwrites a fate,
    /// or cross-owns another invocation's parked continuation.
    ///
    /// Hicks R1: this method now RETURNS the exact per-attempt
    /// `ObserverCancelReceipt` as its function value rather than
    /// storing it in a gate map. The cancel Task launched by onCancel
    /// wraps this call and publishes the receipt to the caller-owned
    /// `AwaitAttempt`'s outcome latch, so tests read the definitive
    /// per-attempt outcome by awaiting `attempt.outcome()`.
    ///
    /// Hicks D: bumps `observerCancelInvocationCount` and flushes any
    /// ACKs waiting for a specific cancel-invocation threshold, so
    /// tests can structurally await a cancel dispatch without polling.
    private func cancelObserver(id: UInt64, awaitID: UUID) -> ObserverCancelReceipt {
        observerCancelInvocationCount += 1
        flushObserverCancelCountAcks()
        // Hicks R1: close-before-processing race. If the gate closed
        // while this cancel Task was scheduled, the parked entry (if
        // any) was already drained by close() with .closedWhileParked.
        // The cancel therefore had NO matching parked entry to drain;
        // report `.closedBeforeProcessing` and take no further action.
        if closed {
            observerCancelIgnoredCount += 1
            return .closedBeforeProcessing
        }
        guard let entry = parkedObservers[id], entry.awaitID == awaitID else {
            observerCancelIgnoredCount += 1
            return .processedIgnoredMismatch
        }
        parkedObservers.removeValue(forKey: id)
        observerOrder.removeAll { $0 == id }
        // Bishop F1: cancelled tokens resume the parked continuation now,
        // so aggregate-only accounting (no per-token fate stored).
        sealObserverOutcome(id: id, reason: .cancelledWhileParked)
        observerResumeCounts[.parkedResumedByCancel, default: 0] += 1
        entry.c.resume()
        return .processedMatched
    }

    /// Convenience: single-shot observer registration + await.
    func waitForEntry() async {
        let token = registerObserver()
        await awaitObserver(token)
    }

    /// Hicks A: bounded park-ACK. Returns an explicit `ParkAckResult`
    /// for every input state so the caller cannot hang on an unknown
    /// or closed-since-registered token.
    ///
    /// - `.parked` — token currently has a parked continuation
    /// - `.terminal(reason)` — token has a sealed fate
    /// - `.unknown` — token is not registered (never issued or already
    ///   scrubbed post-consumption); bounded, immediate
    /// - `.closedOrConsumed` — resolved when `close()` drains the
    ///   caller's queued entry without a park having happened first
    ///
    /// Multiple ACK callers for the same active token all resume
    /// exactly once when the token parks (`.parked`) or reaches a fate
    /// (`.terminal`). `close()` drains any still-queued callers with
    /// `.closedOrConsumed`.
    @discardableResult
    func waitForObserverParked(_ token: ObserverToken) async -> ParkAckResult {
        if parkedObservers[token.id] != nil { return .parked }
        if let reason = observerFates[token.id] { return .terminal(reason) }
        if observerOrder.contains(token.id) {
            if closed { return .closedOrConsumed }
            return await withCheckedContinuation { c in
                observerParkAcks[token.id, default: []].append(c)
            }
        }
        // Not active: fabricated (never issued) vs previously-issued.
        if token.id >= nextObserverID { return .unknown }
        return .closedOrConsumed
    }

    private func flushObserverParkAcks(id: UInt64, result: ParkAckResult) {
        if let cs = observerParkAcks.removeValue(forKey: id) {
            for c in cs { c.resume(returning: result) }
        }
        // Hicks R2/H2: resolve and remove any active park-ACK tickets
        // for this token. Boxes are weak — dead refs are simply skipped.
        // The gate retains no per-ticket state after resolution.
        if let ts = observerActiveParkAckTickets.removeValue(forKey: id) {
            for box in ts { box.ref?.resolve(result) }
        }
    }

    // MARK: Waiter (open-side)

    /// Reserve a waiter slot.
    ///
    /// H2: a closed gate must not emit entry signals. Otherwise
    /// post-close `registerWaiter` would keep incrementing
    /// `pendingEntrySignals` forever with no consumer.
    ///
    /// Hicks B: closed-path is now a bounded aggregate counter, no
    /// per-token fate/completed insertion.
    func registerWaiter() -> WaiterToken {
        if !closed { signalEntryLocked() }
        let id = nextWaiterID; nextWaiterID &+= 1
        if closed {
            waiterPostCloseRegistrationCount += 1
        } else if opened {
            latchWaiterBeforePark(id: id, reason: .openedBeforePark)
        } else {
            waiterOrder.append(id)
        }
        return WaiterToken(id: id)
    }

    /// Await the previously registered waiter. Mirrors `awaitObserver`'s
    /// H1 handling: duplicate await, unknown token, close-since-register,
    /// and normal park are each deterministic and non-hanging. Same
    /// per-invocation `awaitID` rule as `awaitObserver` — duplicate/
    /// unknown/fated cancels cannot cross-own the ORIGINAL parked entry.
    /// Hicks C: per-site resume counters at every `c.resume()`.
    func awaitWaiter(_ token: WaiterToken) async {
        await awaitWaiter(token, attempt: WaiterAwaitAttempt())
    }

    /// Hicks R1: labeled overload accepting a caller-owned
    /// `WaiterAwaitAttempt`. See `awaitObserver(_, attempt:)` docs.
    func awaitWaiter(_ token: WaiterToken, attempt: WaiterAwaitAttempt) async {
        let awaitID = attempt.id
        await withTaskCancellationHandler {
            await withCheckedContinuation { c in
                if completedWaiters.remove(token.id) != nil {
                    waiterFates.removeValue(forKey: token.id)
                    waiterResumeCounts[.latchConsumed, default: 0] += 1
                    c.resume()
                    return
                }
                if parkedWaiters[token.id] != nil {
                    waiterDuplicateAwaitCount += 1
                    waiterResumeCounts[.duplicateAfterParked, default: 0] += 1
                    c.resume()
                    return
                }
                if waiterOrder.contains(token.id) {
                    if closed {
                        waiterOrder.removeAll { $0 == token.id }
                        sealWaiterOutcome(id: token.id, reason: .closedBeforePark)
                        waiterResumeCounts[.closedImmediate, default: 0] += 1
                        c.resume()
                        return
                    }
                    if opened {
                        waiterOrder.removeAll { $0 == token.id }
                        sealWaiterOutcome(id: token.id, reason: .openedBeforePark)
                        waiterResumeCounts[.openedImmediate, default: 0] += 1
                        c.resume()
                        return
                    }
                    parkedWaiters[token.id] = (awaitID: awaitID, c: c)
                    flushWaiterParkAcks(id: token.id, result: .parked)
                    return
                }
                if token.id >= nextWaiterID {
                    waiterUnknownAwaitCount += 1
                    waiterResumeCounts[.unknownToken, default: 0] += 1
                } else {
                    waiterDuplicateAwaitCount += 1
                    waiterResumeCounts[.duplicateAfterFated, default: 0] += 1
                }
                c.resume()
            }
            // Hicks H1: state-gated natural completion — see observer variant.
            if attempt.markCompletedNaturallyIfActive() {
                attempt.resolveOutcome(.finishedBeforeProcessing)
            }
        } onCancel: {
            // Hicks H1: atomically claim publication before launching Task.
            guard attempt.beginCancellationIfActive() else { return }
            _ = Task<WaiterCancelReceipt, Never> {
                let receipt = await self.cancelWaiter(id: token.id, awaitID: awaitID)
                attempt.resolveOutcome(.cancelled(receipt))
                return receipt
}
        }
    }

    /// Late-cancel-safe waiter variant. Mutates state ONLY when a
    /// continuation is currently parked AND the parked entry's
    /// `awaitID` matches this invocation's `awaitID`. Bounded no-op
    /// otherwise — cannot cross-own another invocation's continuation.
    ///
    /// Hicks R1: returns the exact per-attempt `WaiterCancelReceipt`
    /// as its function value; no gate-side receipt storage.
    private func cancelWaiter(id: UInt64, awaitID: UUID) -> WaiterCancelReceipt {
        waiterCancelInvocationCount += 1
        flushWaiterCancelCountAcks()
        if closed {
            waiterCancelIgnoredCount += 1
            return .closedBeforeProcessing
        }
        guard let entry = parkedWaiters[id], entry.awaitID == awaitID else {
            waiterCancelIgnoredCount += 1
            return .processedIgnoredMismatch
        }
        parkedWaiters.removeValue(forKey: id)
        waiterOrder.removeAll { $0 == id }
        sealWaiterOutcome(id: id, reason: .cancelledWhileParked)
        waiterResumeCounts[.parkedResumedByCancel, default: 0] += 1
        entry.c.resume()
        return .processedMatched
    }

    /// Convenience: single-shot waiter registration + await.
    func wait() async {
        let token = registerWaiter()
        await awaitWaiter(token)
    }

    /// Hicks A: bounded park-ACK for waiter tokens.
    @discardableResult
    func waitForWaiterParked(_ token: WaiterToken) async -> ParkAckResult {
        if parkedWaiters[token.id] != nil { return .parked }
        if let reason = waiterFates[token.id] { return .terminal(reason) }
        if waiterOrder.contains(token.id) {
            if closed { return .closedOrConsumed }
            return await withCheckedContinuation { c in
                waiterParkAcks[token.id, default: []].append(c)
            }
        }
        if token.id >= nextWaiterID { return .unknown }
        return .closedOrConsumed
    }

    private func flushWaiterParkAcks(id: UInt64, result: ParkAckResult) {
        if let cs = waiterParkAcks.removeValue(forKey: id) {
            for c in cs { c.resume(returning: result) }
        }
        if let ts = waiterActiveParkAckTickets.removeValue(forKey: id) {
            for box in ts { box.ref?.resolve(result) }
        }
    }

    // MARK: Hicks D — cancel-invocation ACK helpers

    /// Suspend until `observerCancelInvocationCount >= target`. Bounded:
    /// resolves immediately if already met, otherwise queues one entry
    /// that is flushed exactly once when the count reaches `target`.
    /// `close()` drains any still-queued entries to guarantee test
    /// teardown never hangs.
    func waitForObserverCancelCount(atLeast target: Int) async {
        if observerCancelInvocationCount >= target { return }
        await withCheckedContinuation { c in
            observerCancelCountAcks[target, default: []].append(c)
        }
    }

    /// Waiter analogue of `waitForObserverCancelCount(atLeast:)`.
    func waitForWaiterCancelCount(atLeast target: Int) async {
        if waiterCancelInvocationCount >= target { return }
        await withCheckedContinuation { c in
            waiterCancelCountAcks[target, default: []].append(c)
        }
    }

    private func flushObserverCancelCountAcks() {
        let met = observerCancelCountAcks.keys.filter { $0 <= observerCancelInvocationCount }
        for key in met {
            if let cs = observerCancelCountAcks.removeValue(forKey: key) {
                for c in cs { c.resume() }
            }
        }
    }

    private func flushWaiterCancelCountAcks() {
        let met = waiterCancelCountAcks.keys.filter { $0 <= waiterCancelInvocationCount }
        for key in met {
            if let cs = waiterCancelCountAcks.removeValue(forKey: key) {
                for c in cs { c.resume() }
            }
        }
    }

    // MARK: Hicks R2 addendum — buffered park-ACK ticket objects

    /// Hicks R2: synchronously reserve a park-ACK ticket for an
    /// observer token. Returns an already-resolved ticket for any
    /// non-active state (already parked / already fated / unknown /
    /// closed-or-consumed). For an active-registered token, returns
    /// a pending ticket AFTER inserting it into
    /// `observerActiveParkAckTickets[token.id]` — because this
    /// executes SYNCHRONOUSLY on the actor, calling it twice before
    /// spawning any parking task GUARANTEES both tickets were queued
    /// before parking could occur. On park/fate seal/close, the
    /// actor calls `ticket.resolve(_)` and REMOVES the ticket from
    /// the active map immediately. Callers consume the buffered
    /// result via `ticket.value()` at any later time; never-awaited
    /// tickets are simply dropped by the caller after actor
    /// resolution (no residual gate state).
    func enterObserverParkAck(_ token: ObserverToken) -> ObserverParkAckTicket {
        let ticket = ObserverParkAckTicket()
        if parkedObservers[token.id] != nil {
            ticket.resolve(.parked)
            return ticket
        }
        if let reason = observerFates[token.id] {
            ticket.resolve(.terminal(reason))
            return ticket
        }
        if observerOrder.contains(token.id) {
            if closed {
                ticket.resolve(.closedOrConsumed)
                return ticket
            }
            // Hicks H2: compact any dropped-caller boxes for this token
            // before appending, so repeated enter+drop stays bounded.
            compactObserverParkAckTickets(id: token.id)
            observerActiveParkAckTickets[token.id, default: []].append(WeakObserverParkAckTicketBox(ticket))
            return ticket
        }
        if token.id >= nextObserverID {
            ticket.resolve(.unknown)
        } else {
            ticket.resolve(.closedOrConsumed)
        }
        return ticket
    }

    /// Waiter analogue of `enterObserverParkAck`.
    func enterWaiterParkAck(_ token: WaiterToken) -> WaiterParkAckTicket {
        let ticket = WaiterParkAckTicket()
        if parkedWaiters[token.id] != nil {
            ticket.resolve(.parked)
            return ticket
        }
        if let reason = waiterFates[token.id] {
            ticket.resolve(.terminal(reason))
            return ticket
        }
        if waiterOrder.contains(token.id) {
            if closed {
                ticket.resolve(.closedOrConsumed)
                return ticket
            }
            compactWaiterParkAckTickets(id: token.id)
            waiterActiveParkAckTickets[token.id, default: []].append(WeakWaiterParkAckTicketBox(ticket))
            return ticket
        }
        if token.id >= nextWaiterID {
            ticket.resolve(.unknown)
        } else {
            ticket.resolve(.closedOrConsumed)
        }
        return ticket
    }

    /// Hicks H2: remove weak-boxes whose caller-owned ticket has been
    /// dropped. Called from `enterX`, snapshot, and close to keep
    /// active-ticket storage bounded even when callers never park nor
    /// consume. If the resulting bucket is empty, the map entry is
    /// removed too.
    private func compactObserverParkAckTickets(id: UInt64) {
        guard var arr = observerActiveParkAckTickets[id] else { return }
        arr.removeAll { $0.ref == nil }
        if arr.isEmpty {
            observerActiveParkAckTickets.removeValue(forKey: id)
        } else {
            observerActiveParkAckTickets[id] = arr
        }
    }

    private func compactWaiterParkAckTickets(id: UInt64) {
        guard var arr = waiterActiveParkAckTickets[id] else { return }
        arr.removeAll { $0.ref == nil }
        if arr.isEmpty {
            waiterActiveParkAckTickets.removeValue(forKey: id)
        } else {
            waiterActiveParkAckTickets[id] = arr
        }
    }

    private func compactAllParkAckTickets() {
        for id in Array(observerActiveParkAckTickets.keys) {
            compactObserverParkAckTickets(id: id)
        }
        for id in Array(waiterActiveParkAckTickets.keys) {
            compactWaiterParkAckTickets(id: id)
        }
    }

    /// Hicks H2 remediation: deterministic dead-box pruning callable
    /// independently of `snapshot()`. Exists so tests can prove raw
    /// backing storage returns to exactly zero without relying on the
    /// implicit compaction side-effect of `snapshot()` (which measured
    /// AND cleaned in the same call — making a raw==0 assertion after
    /// snapshot vacuous). One actor hop; no polling, sleeps, or yields.
    /// Idempotent: successive calls are no-ops on an already-clean map.
    func pruneAllDroppedTicketBoxesForTest() {
        compactAllParkAckTickets()
    }

    // MARK: Hicks D — hold-inside-cancellation-handler helpers

    /// Test-only helper that parks an observer continuation the same way
    /// as `awaitObserver`, but AFTER the continuation resumes we hold
    /// this Task INSIDE the `withTaskCancellationHandler` scope by
    /// awaiting `holdGate.awaitWaiter(holdToken)`. That structurally
    /// keeps the `onCancel` handler installed after the continuation
    /// resumed, so a mismatched-awaitID cancel dispatched to this
    /// token's id can be proven to be a bounded no-op WITHOUT the
    /// handler having already been torn down.
    ///
    /// The extra hold is released when the caller opens `holdGate`.
    func awaitObserverAndHold(
        _ token: ObserverToken,
        holdGate: AsyncGate,
        holdToken: WaiterToken
    ) async {
        await awaitObserverAndHold(token, attempt: ObserverAwaitAttempt(), holdGate: holdGate, holdToken: holdToken)
    }

    /// Hicks R1 labeled AndHold variant. See `awaitObserver(_, attempt:)`.
    func awaitObserverAndHold(
        _ token: ObserverToken,
        attempt: ObserverAwaitAttempt,
        holdGate: AsyncGate,
        holdToken: WaiterToken
    ) async {
        let awaitID = attempt.id
        await withTaskCancellationHandler {
            await withCheckedContinuation { c in
                if completedObservers.remove(token.id) != nil {
                    observerFates.removeValue(forKey: token.id)
                    observerResumeCounts[.latchConsumed, default: 0] += 1
                    c.resume()
                    return
                }
                if parkedObservers[token.id] != nil {
                    observerDuplicateAwaitCount += 1
                    observerResumeCounts[.duplicateAfterParked, default: 0] += 1
                    c.resume()
                    return
                }
                if observerOrder.contains(token.id) {
                    if closed {
                        observerOrder.removeAll { $0 == token.id }
                        sealObserverOutcome(id: token.id, reason: .closedBeforePark)
                        observerResumeCounts[.closedImmediate, default: 0] += 1
                        c.resume()
                        return
                    }
                    parkedObservers[token.id] = (awaitID: awaitID, c: c)
                    flushObserverParkAcks(id: token.id, result: .parked)
                    return
                }
                if token.id >= nextObserverID {
                    observerUnknownAwaitCount += 1
                    observerResumeCounts[.unknownToken, default: 0] += 1
                } else {
                    observerDuplicateAwaitCount += 1
                    observerResumeCounts[.duplicateAfterFated, default: 0] += 1
                }
                c.resume()
            }
            // Structurally hold inside the cancellation handler scope
            // so mismatched late cancels can be observed while the
            // handler is still installed.
            await holdGate.awaitWaiter(holdToken)
            // Hicks H1: state-gated natural completion.
            if attempt.markCompletedNaturallyIfActive() {
                attempt.resolveOutcome(.finishedBeforeProcessing)
            }
        } onCancel: {
            guard attempt.beginCancellationIfActive() else { return }
            _ = Task<ObserverCancelReceipt, Never> {
                let receipt = await self.cancelObserver(id: token.id, awaitID: awaitID)
                attempt.resolveOutcome(.cancelled(receipt))
                return receipt
}
        }
    }

    /// Waiter analogue of `awaitObserverAndHold`.
    func awaitWaiterAndHold(
        _ token: WaiterToken,
        holdGate: AsyncGate,
        holdToken: WaiterToken
    ) async {
        await awaitWaiterAndHold(token, attempt: WaiterAwaitAttempt(), holdGate: holdGate, holdToken: holdToken)
    }

    /// Hicks R1 labeled waiter AndHold variant.
    func awaitWaiterAndHold(
        _ token: WaiterToken,
        attempt: WaiterAwaitAttempt,
        holdGate: AsyncGate,
        holdToken: WaiterToken
    ) async {
        let awaitID = attempt.id
        await withTaskCancellationHandler {
            await withCheckedContinuation { c in
                if completedWaiters.remove(token.id) != nil {
                    waiterFates.removeValue(forKey: token.id)
                    waiterResumeCounts[.latchConsumed, default: 0] += 1
                    c.resume()
                    return
                }
                if parkedWaiters[token.id] != nil {
                    waiterDuplicateAwaitCount += 1
                    waiterResumeCounts[.duplicateAfterParked, default: 0] += 1
                    c.resume()
                    return
                }
                if waiterOrder.contains(token.id) {
                    if closed {
                        waiterOrder.removeAll { $0 == token.id }
                        sealWaiterOutcome(id: token.id, reason: .closedBeforePark)
                        waiterResumeCounts[.closedImmediate, default: 0] += 1
                        c.resume()
                        return
                    }
                    if opened {
                        waiterOrder.removeAll { $0 == token.id }
                        sealWaiterOutcome(id: token.id, reason: .openedBeforePark)
                        waiterResumeCounts[.openedImmediate, default: 0] += 1
                        c.resume()
                        return
                    }
                    parkedWaiters[token.id] = (awaitID: awaitID, c: c)
                    flushWaiterParkAcks(id: token.id, result: .parked)
                    return
                }
                if token.id >= nextWaiterID {
                    waiterUnknownAwaitCount += 1
                    waiterResumeCounts[.unknownToken, default: 0] += 1
                } else {
                    waiterDuplicateAwaitCount += 1
                    waiterResumeCounts[.duplicateAfterFated, default: 0] += 1
                }
                c.resume()
            }
            await holdGate.awaitWaiter(holdToken)
            // Hicks H1: state-gated natural completion.
            if attempt.markCompletedNaturallyIfActive() {
                attempt.resolveOutcome(.finishedBeforeProcessing)
            }
        } onCancel: {
            guard attempt.beginCancellationIfActive() else { return }
            _ = Task<WaiterCancelReceipt, Never> {
                let receipt = await self.cancelWaiter(id: token.id, awaitID: awaitID)
                attempt.resolveOutcome(.cancelled(receipt))
                return receipt
}
        }
    }

    // MARK: Hicks H1 addendum — publish-natural-BEFORE-hold variants
    //
    // These variants let a test PROVE that a late `Task.cancel()` fires
    // while the outer `withTaskCancellationHandler` scope is still live
    // AFTER natural completion has already published `.finishedBeforeProcessing`.
    // Sequence inside the body:
    //   1. Park primary continuation (identical branches to `awaitObserver`)
    //   2. On resume, IMMEDIATELY publish natural completion via the
    //      attempt's monotonic state gate. This synchronously seals the
    //      buffered outcome to `.finishedBeforeProcessing`.
    //   3. Then park inside `holdGate.awaitWaiter(holdToken)`. The outer
    //      cancellation handler remains installed the entire time; a
    //      `Task.cancel()` at this point invokes the outer onCancel, whose
    //      `attempt.beginCancellationIfActive()` returns FALSE because
    //      state is already `.completedNaturally`, so no cancel Task is
    //      launched — the exact "natural wins over late cancel while
    //      handler is live" proof.
    // Contrast with `awaitObserverAndHold` (holds THEN publishes), which
    // proves the opposite ordering (cancel arrives before natural publish).

    /// Observer variant of publish-natural-BEFORE-hold. See MARK doc above.
    func awaitObserverAndHoldAfterPublish(
        _ token: ObserverToken,
        attempt: ObserverAwaitAttempt,
        holdGate: AsyncGate,
        holdToken: WaiterToken
    ) async {
        let awaitID = attempt.id
        await withTaskCancellationHandler {
            await withCheckedContinuation { c in
                if completedObservers.remove(token.id) != nil {
                    observerFates.removeValue(forKey: token.id)
                    observerResumeCounts[.latchConsumed, default: 0] += 1
                    c.resume()
                    return
                }
                if parkedObservers[token.id] != nil {
                    observerDuplicateAwaitCount += 1
                    observerResumeCounts[.duplicateAfterParked, default: 0] += 1
                    c.resume()
                    return
                }
                if observerOrder.contains(token.id) {
                    if closed {
                        observerOrder.removeAll { $0 == token.id }
                        sealObserverOutcome(id: token.id, reason: .closedBeforePark)
                        observerResumeCounts[.closedImmediate, default: 0] += 1
                        c.resume()
                        return
                    }
                    parkedObservers[token.id] = (awaitID: awaitID, c: c)
                    flushObserverParkAcks(id: token.id, result: .parked)
                    return
                }
                if token.id >= nextObserverID {
                    observerUnknownAwaitCount += 1
                    observerResumeCounts[.unknownToken, default: 0] += 1
                } else {
                    observerDuplicateAwaitCount += 1
                    observerResumeCounts[.duplicateAfterFated, default: 0] += 1
                }
                c.resume()
            }
            // Hicks H1: publish natural completion FIRST (before hold),
            // so the state gate transitions to `.completedNaturally`
            // BEFORE any subsequent Task.cancel() can fire onCancel.
            if attempt.markCompletedNaturallyIfActive() {
                attempt.resolveOutcome(.finishedBeforeProcessing)
            }
            // Now hold inside the same cancellation-handler scope so a
            // late Task.cancel() is proven to arrive while the outer
            // handler is still installed.
            await holdGate.awaitWaiter(holdToken)
        } onCancel: {
            // Hicks H1: state gate returns FALSE here in the natural-wins
            // path (already `.completedNaturally`); no cancel Task launched.
            guard attempt.beginCancellationIfActive() else { return }
            _ = Task<ObserverCancelReceipt, Never> {
                let receipt = await self.cancelObserver(id: token.id, awaitID: awaitID)
                attempt.resolveOutcome(.cancelled(receipt))
                return receipt
            }
        }
    }

    /// Waiter analogue of `awaitObserverAndHoldAfterPublish`.
    func awaitWaiterAndHoldAfterPublish(
        _ token: WaiterToken,
        attempt: WaiterAwaitAttempt,
        holdGate: AsyncGate,
        holdToken: WaiterToken
    ) async {
        let awaitID = attempt.id
        await withTaskCancellationHandler {
            await withCheckedContinuation { c in
                if completedWaiters.remove(token.id) != nil {
                    waiterFates.removeValue(forKey: token.id)
                    waiterResumeCounts[.latchConsumed, default: 0] += 1
                    c.resume()
                    return
                }
                if parkedWaiters[token.id] != nil {
                    waiterDuplicateAwaitCount += 1
                    waiterResumeCounts[.duplicateAfterParked, default: 0] += 1
                    c.resume()
                    return
                }
                if waiterOrder.contains(token.id) {
                    if closed {
                        waiterOrder.removeAll { $0 == token.id }
                        sealWaiterOutcome(id: token.id, reason: .closedBeforePark)
                        waiterResumeCounts[.closedImmediate, default: 0] += 1
                        c.resume()
                        return
                    }
                    if opened {
                        waiterOrder.removeAll { $0 == token.id }
                        sealWaiterOutcome(id: token.id, reason: .openedBeforePark)
                        waiterResumeCounts[.openedImmediate, default: 0] += 1
                        c.resume()
                        return
                    }
                    parkedWaiters[token.id] = (awaitID: awaitID, c: c)
                    flushWaiterParkAcks(id: token.id, result: .parked)
                    return
                }
                if token.id >= nextWaiterID {
                    waiterUnknownAwaitCount += 1
                    waiterResumeCounts[.unknownToken, default: 0] += 1
                } else {
                    waiterDuplicateAwaitCount += 1
                    waiterResumeCounts[.duplicateAfterFated, default: 0] += 1
                }
                c.resume()
            }
            if attempt.markCompletedNaturallyIfActive() {
                attempt.resolveOutcome(.finishedBeforeProcessing)
            }
            await holdGate.awaitWaiter(holdToken)
        } onCancel: {
            guard attempt.beginCancellationIfActive() else { return }
            _ = Task<WaiterCancelReceipt, Never> {
                let receipt = await self.cancelWaiter(id: token.id, awaitID: awaitID)
                attempt.resolveOutcome(.cancelled(receipt))
                return receipt
            }
        }
    }

    // MARK: Terminal transitions

    /// Open: drain all pending waiters exactly once. Idempotent.
    /// Bishop F1: parked-resume path is aggregate-only (no per-token
    /// fate stored). Registered-but-not-parked path latches so the
    /// upcoming `awaitWaiter` can consume it and prune both entries.
    /// Hicks C: each parked-drain bumps `waiterResumeCounts[.parkedResumedByOpen]`.
    func open() {
        opened = true
        let ids = waiterOrder
        waiterOrder.removeAll()
        for id in ids {
            if let entry = parkedWaiters.removeValue(forKey: id) {
                sealWaiterOutcome(id: id, reason: .openedWhileParked)
                waiterResumeCounts[.parkedResumedByOpen, default: 0] += 1
                entry.c.resume()
            } else {
                latchWaiterBeforePark(id: id, reason: .openedBeforePark)
            }
        }
    }

    /// Terminal teardown.
    ///
    /// Bishop F1: close leaves ALL per-token maps/sets empty. Parked
    /// continuations are resumed via aggregate-only seal (no fate
    /// stored). Registered-but-not-parked tokens are aggregate-sealed
    /// as `.closedBeforePark` (also no per-token latch — the gate is
    /// terminal and a future awaitX of that token falls into the
    /// bounded not-active branch). Any residual latches from prior
    /// `signaledBeforePark`/`openedBeforePark` are cleared here too.
    /// H2: also clears `pendingEntrySignals` and drains any stranded
    /// park-ACK waiters so a helper regression can't hang the test.
    /// Hicks D: also drains any queued cancel-count ACK waiters so test
    /// teardown never hangs on a threshold that will never be reached.
    ///
    /// Idempotent: a second `close()` is a no-op on aggregate counters.
    func close() {
        if closed {
            // Already terminal; the invariant already holds.
            return
        }
        closed = true
        pendingEntrySignals = 0
        let wIds = waiterOrder
        waiterOrder.removeAll()
        for id in wIds {
            if let entry = parkedWaiters.removeValue(forKey: id) {
                sealWaiterOutcome(id: id, reason: .closedWhileParked)
                waiterResumeCounts[.parkedResumedByClose, default: 0] += 1
                entry.c.resume()
            } else {
                sealWaiterOutcome(id: id, reason: .closedBeforePark)
            }
        }
        let oIds = observerOrder
        observerOrder.removeAll()
        for id in oIds {
            if let entry = parkedObservers.removeValue(forKey: id) {
                sealObserverOutcome(id: id, reason: .closedWhileParked)
                observerResumeCounts[.parkedResumedByClose, default: 0] += 1
                entry.c.resume()
            } else {
                sealObserverOutcome(id: id, reason: .closedBeforePark)
            }
        }
        // Bishop F1: clear any residual before-park latches. Their
        // fates and completed sets accumulated per-token entries;
        // close is terminal, so no future awaitX can consume them.
        observerFates.removeAll()
        waiterFates.removeAll()
        completedObservers.removeAll()
        completedWaiters.removeAll()
        // Drain stranded park-ACK waiters with `.closedOrConsumed` so
        // callers get a bounded, explicit outcome (Hicks A).
        let obsAcks = observerParkAcks
        observerParkAcks.removeAll()
        for (_, cs) in obsAcks { for c in cs { c.resume(returning: .closedOrConsumed) } }
        let wAcks = waiterParkAcks
        waiterParkAcks.removeAll()
        for (_, cs) in wAcks { for c in cs { c.resume(returning: .closedOrConsumed) } }
        // Hicks D: drain any queued cancel-count ACK waiters.
        let obsCcs = observerCancelCountAcks
        observerCancelCountAcks.removeAll()
        for (_, cs) in obsCcs { for c in cs { c.resume() } }
        let wCcs = waiterCancelCountAcks
        waiterCancelCountAcks.removeAll()
        for (_, cs) in wCcs { for c in cs { c.resume() } }

        // Hicks R1 addendum: the gate no longer stores per-attempt
        // cancellation receipts (state lives in caller-owned
        // `AwaitAttempt` contexts). Any in-flight cancel Task that
        // races with close() observes `closed == true` in
        // `cancelObserver`/`cancelWaiter` and returns
        // `.closedBeforeProcessing`; the caller's `attempt.outcome()`
        // delivers that exact receipt via the buffered outcome latch.

        // Hicks R2 addendum: drain any still-pending park-ACK tickets
        // with `.closedOrConsumed`. Resolution is exactly-once inside
        // each ticket; the actor removes the entry from
        // `observerActiveParkAckTickets`. Callers may still consume
        // the buffered result via `ticket.value()` — the gate holds
        // no remaining per-ticket state. Weak boxes: skip dead refs.
        let obsTix = observerActiveParkAckTickets
        observerActiveParkAckTickets.removeAll()
        for (_, ts) in obsTix { for box in ts { box.ref?.resolve(.closedOrConsumed) } }
        let watTix = waiterActiveParkAckTickets
        waiterActiveParkAckTickets.removeAll()
        for (_, ts) in watTix { for box in ts { box.ref?.resolve(.closedOrConsumed) } }
    }

    // MARK: Introspection (test-only)

    func snapshot() -> Snapshot {
        // Hicks H2: compact dropped-caller ticket boxes so the
        // reported active-ticket count reflects LIVE tickets only.
        compactAllParkAckTickets()
        return Snapshot(
            pendingEntrySignals: pendingEntrySignals,
            observerOrder: observerOrder,
            waiterOrder: waiterOrder,
            completedObserverCount: completedObservers.count,
            completedWaiterCount: completedWaiters.count,
            parkedObserverCount: parkedObservers.count,
            parkedWaiterCount: parkedWaiters.count,
            opened: opened,
            closed: closed,
            observerFates: observerFates,
            waiterFates: waiterFates,
            observerFateCounts: observerFateCounts,
            waiterFateCounts: waiterFateCounts,
            observerCancelIgnoredCount: observerCancelIgnoredCount,
            waiterCancelIgnoredCount: waiterCancelIgnoredCount,
            observerDuplicateAwaitCount: observerDuplicateAwaitCount,
            waiterDuplicateAwaitCount: waiterDuplicateAwaitCount,
            observerUnknownAwaitCount: observerUnknownAwaitCount,
            waiterUnknownAwaitCount: waiterUnknownAwaitCount,
            observerPostCloseRegistrationCount: observerPostCloseRegistrationCount,
            waiterPostCloseRegistrationCount: waiterPostCloseRegistrationCount,
            observerResumeCounts: observerResumeCounts,
            waiterResumeCounts: waiterResumeCounts,
            observerCancelInvocationCount: observerCancelInvocationCount,
            waiterCancelInvocationCount: waiterCancelInvocationCount,
            observerParkAckQueueTotal: observerParkAcks.values.reduce(0) { $0 + $1.count },
            waiterParkAckQueueTotal: waiterParkAcks.values.reduce(0) { $0 + $1.count },
            observerCancelCountAckQueueTotal: observerCancelCountAcks.values.reduce(0) { $0 + $1.count },
            waiterCancelCountAckQueueTotal: waiterCancelCountAcks.values.reduce(0) { $0 + $1.count },
            observerParkAckTicketCount: observerActiveParkAckTickets.values.reduce(0) { $0 + $1.count },
            waiterParkAckTicketCount: waiterActiveParkAckTickets.values.reduce(0) { $0 + $1.count },
            observerParkAckTicketBackingKeyCount: observerActiveParkAckTickets.count,
            waiterParkAckTicketBackingKeyCount: waiterActiveParkAckTickets.count
        )
    }

    // MARK: Private helpers

    private func signalEntryLocked() {
        if let id = observerOrder.first {
            observerOrder.removeFirst()
            if let entry = parkedObservers.removeValue(forKey: id) {
                sealObserverOutcome(id: id, reason: .signaledWhileParked)
                observerResumeCounts[.parkedResumedBySignal, default: 0] += 1
                entry.c.resume()
            } else {
                latchObserverBeforePark(id: id, reason: .signaledBeforePark)
            }
        } else {
            pendingEntrySignals += 1
        }
    }

    /// Bishop F1: aggregate-only outcome accounting. Bumps
    /// `observerFateCounts` and flushes any queued park-ACKs, but does
    /// NOT store per-token state. Used when the outcome resumes a real
    /// continuation right now (no before-park latch left behind), so
    /// long-lived gates do not accumulate O(N) per-token history.
    private func sealObserverOutcome(id: UInt64, reason: ResumeReason) {
        observerFateCounts[reason, default: 0] += 1
        flushObserverParkAcks(id: id, result: .terminal(reason))
    }

    /// Waiter analogue of `sealObserverOutcome`.
    private func sealWaiterOutcome(id: UInt64, reason: ResumeReason) {
        waiterFateCounts[reason, default: 0] += 1
        flushWaiterParkAcks(id: id, result: .terminal(reason))
    }

    /// Bishop F1: before-park latch. Keeps a bounded per-token entry in
    /// `observerFates` + `completedObservers` that the NEXT
    /// `awaitObserver` consumes atomically (both entries pruned).
    /// `waitForObserverParked` may return `.terminal(reason)` during
    /// this interval. Used only for signal/open sequences where a
    /// resume must wait for a not-yet-scheduled `awaitObserver` call.
    private func latchObserverBeforePark(id: UInt64, reason: ResumeReason) {
        guard observerFates[id] == nil else { return }  // exactly-once
        completedObservers.insert(id)
        observerFates[id] = reason
        observerFateCounts[reason, default: 0] += 1
        flushObserverParkAcks(id: id, result: .terminal(reason))
    }

    /// Waiter analogue of `latchObserverBeforePark`.
    private func latchWaiterBeforePark(id: UInt64, reason: ResumeReason) {
        guard waiterFates[id] == nil else { return }
        completedWaiters.insert(id)
        waiterFates[id] = reason
        waiterFateCounts[reason, default: 0] += 1
        flushWaiterParkAcks(id: id, result: .terminal(reason))
    }

    /// Bishop F1 test helper: emit a single entry signal without going
    /// through `registerWaiter` (which would also leave a waiter in
    /// `waiterOrder`). Used by long-lived non-closed sequential
    /// register→park→signal→await proofs so per-token state can return
    /// to zero after each cycle.
    func signal() {
        if !closed { signalEntryLocked() }
    }
}
