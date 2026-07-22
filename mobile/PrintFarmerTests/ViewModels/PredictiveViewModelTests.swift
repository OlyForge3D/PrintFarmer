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

        // `await task.value` proves the task completed. If cancellation
        // regressed this would hang instead of deadlocking silently.
        await task.value

        let snap = await gate.snapshot()
        XCTAssertEqual(snap.parkedWaiterCount, 0)
        XCTAssertEqual(snap.parkedObserverCount, 0)
        XCTAssertEqual(snap.waiterOrder, [])
        XCTAssertEqual(snap.observerOrder, [])
        XCTAssertEqual(snap.completedWaiterCount, 0,
                       "cancel must not relatch into completedWaiters")
        XCTAssertEqual(snap.completedObserverCount, 0)
        await gate.close()
    }

    /// Cancellation after the awaiter has parked: register synchronously,
    /// park in an unstructured Task, cancel. cancelX drains via
    /// parkedX exactly once. Post-drain: zero state.
    func testAsyncGateCancelAfterRegister() async {
        let gate = AsyncGate()

        // Waiter path.
        let waiterToken = await gate.registerWaiter()
        let waiterTask = Task { await gate.awaitWaiter(waiterToken) }
        waiterTask.cancel()
        await waiterTask.value

        // Observer path.
        let observerToken = await gate.registerObserver()
        let observerTask = Task { await gate.awaitObserver(observerToken) }
        observerTask.cancel()
        await observerTask.value

        let snap = await gate.snapshot()
        XCTAssertEqual(snap.parkedWaiterCount, 0)
        XCTAssertEqual(snap.parkedObserverCount, 0)
        XCTAssertEqual(snap.waiterOrder, [])
        XCTAssertEqual(snap.observerOrder, [])
        XCTAssertEqual(snap.completedWaiterCount, 0,
                       "cancel must not relatch into completedWaiters")
        XCTAssertEqual(snap.completedObserverCount, 0,
                       "cancel must not relatch into completedObservers")
        await gate.close()
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
        await task.value
        task.cancel()

        let final = await gate.snapshot()
        XCTAssertEqual(final.parkedWaiterCount, 0)
        XCTAssertEqual(final.waiterOrder, [])
        XCTAssertEqual(final.completedWaiterCount, 0,
                       "the awaitWaiter must have removed the latch AND the late cancel must not relatch")
        await gate.close()
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
        await task.value
        task.cancel()

        let final = await gate.snapshot()
        XCTAssertEqual(final.parkedObserverCount, 0)
        XCTAssertEqual(final.observerOrder, [])
        XCTAssertEqual(final.completedObserverCount, 0,
                       "awaitObserver must consume the latch AND the late cancel must not relatch")
        await gate.close()
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
        await waiterTask.value

        // Then open — waiterOrder is already empty because cancelWaiter
        // drained the id; open finds nothing to drain.
        await gate.open()

        let snap = await gate.snapshot()
        XCTAssertEqual(snap.parkedWaiterCount, 0)
        XCTAssertEqual(snap.waiterOrder, [])
        XCTAssertEqual(snap.completedWaiterCount, 0,
                       "cancel-then-open must not leave any completedWaiters latched")
        XCTAssertTrue(snap.opened)
        await gate.close()
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
        await waiterTask.value

        let snap = await gate.snapshot()
        XCTAssertEqual(snap.parkedWaiterCount, 0)
        XCTAssertEqual(snap.waiterOrder, [])
        XCTAssertEqual(snap.completedWaiterCount, 0,
                       "awaitWaiter must remove the latch AND late cancel must not relatch")
        XCTAssertTrue(snap.opened)
        await gate.close()
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
        await waiterTask.value
        await strandedTask.value
        // The other observer completed via the pending signal on register.
        await observerTask.value

        await gate.close()

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
        await task.value        // task completed, handler scope exited
        task.cancel()           // no-op — no handler installed anymore

        let snap = await gate.snapshot()
        XCTAssertEqual(snap.parkedObserverCount, 0)
        XCTAssertEqual(snap.observerOrder, [])
        XCTAssertEqual(snap.completedObserverCount, 0)
        await gate.close()
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
private actor AsyncGate {
    struct ObserverToken: Sendable, Hashable { let id: UInt64 }
    struct WaiterToken: Sendable, Hashable { let id: UInt64 }

    /// Test-only introspection snapshot. Purely reads state under the
    /// actor so tests can assert precise transitions without polling.
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
    }

    private var nextID: UInt64 = 1
    private var parkedObservers: [UInt64: CheckedContinuation<Void, Never>] = [:]
    private var completedObservers: Set<UInt64> = []
    private var observerOrder: [UInt64] = []
    private var parkedWaiters: [UInt64: CheckedContinuation<Void, Never>] = [:]
    private var completedWaiters: Set<UInt64> = []
    private var waiterOrder: [UInt64] = []
    private var pendingEntrySignals: Int = 0
    private var opened = false
    private var closed = false

    // MARK: Observer (entry-side)

    /// Reserve an observer slot. Synchronous-in-actor: if a pending entry
    /// signal exists it is consumed and the token is latched as
    /// completed. Otherwise the token is appended to the FIFO order.
    func registerObserver() -> ObserverToken {
        let id = nextID; nextID &+= 1
        if closed {
            completedObservers.insert(id)
        } else if pendingEntrySignals > 0 {
            pendingEntrySignals -= 1
            completedObservers.insert(id)
        } else {
            observerOrder.append(id)
        }
        return ObserverToken(id: id)
    }

    /// Await the previously registered observer. Parks unconditionally
    /// unless the token has already terminated (completed via a prior
    /// entry signal, or drained by close). Cancellation is routed
    /// through `cancelObserver` via the structural `onCancel` handler;
    /// the actor's serial executor guarantees the park completes before
    /// `cancelObserver` runs, so the continuation is always drained via
    /// the `parkedObservers` path exactly once.
    func awaitObserver(_ token: ObserverToken) async {
        await withTaskCancellationHandler {
            await withCheckedContinuation { c in
                if completedObservers.remove(token.id) != nil {
                    c.resume()
                    return
                }
                if closed {
                    observerOrder.removeAll { $0 == token.id }
                    c.resume()
                    return
                }
                // No inline Task.isCancelled fast path. If the task was
                // cancelled, `withTaskCancellationHandler` has already
                // (or will imminently) spawn a `cancelObserver` Task that
                // hops onto this actor strictly after this park because
                // the actor turn does not release until this closure
                // returns and the enclosing `await` suspends. Routing
                // cancellation through the parked-only path is what makes
                // late cancellation a bounded no-op.
                parkedObservers[token.id] = c
            }
        } onCancel: {
            Task { await self.cancelObserver(id: token.id) }
        }
    }

    /// Late-cancel-safe. Mutates state ONLY when the continuation is
    /// currently parked. If the id was already terminated by another
    /// path (signalEntry drain, open/close drain, or completed-latch
    /// short-circuit) this is a bounded no-op with zero state mutation
    /// and never relatches into `completedObservers`.
    private func cancelObserver(id: UInt64) {
        guard let c = parkedObservers.removeValue(forKey: id) else {
            return
        }
        observerOrder.removeAll { $0 == id }
        c.resume()
    }

    /// Convenience: single-shot observer registration + await.
    func waitForEntry() async {
        let token = registerObserver()
        await awaitObserver(token)
    }

    // MARK: Waiter (open-side)

    /// Reserve a waiter slot. Synchronous-in-actor: emits an entry signal
    /// (observer-facing) first, then decides whether to park or latch
    /// completion depending on gate state.
    func registerWaiter() -> WaiterToken {
        signalEntryLocked()
        let id = nextID; nextID &+= 1
        if opened || closed {
            completedWaiters.insert(id)
        } else {
            waiterOrder.append(id)
        }
        return WaiterToken(id: id)
    }

    func awaitWaiter(_ token: WaiterToken) async {
        await withTaskCancellationHandler {
            await withCheckedContinuation { c in
                if completedWaiters.remove(token.id) != nil {
                    c.resume()
                    return
                }
                if opened || closed {
                    waiterOrder.removeAll { $0 == token.id }
                    c.resume()
                    return
                }
                // No inline Task.isCancelled fast path — see the note on
                // `awaitObserver` for the actor-serialisation argument.
                parkedWaiters[token.id] = c
            }
        } onCancel: {
            Task { await self.cancelWaiter(id: token.id) }
        }
    }

    /// Late-cancel-safe. Mutates state ONLY when the continuation is
    /// currently parked. Never relatches into `completedWaiters`.
    private func cancelWaiter(id: UInt64) {
        guard let c = parkedWaiters.removeValue(forKey: id) else {
            return
        }
        waiterOrder.removeAll { $0 == id }
        c.resume()
    }

    /// Convenience: single-shot waiter registration + await.
    func wait() async {
        let token = registerWaiter()
        await awaitWaiter(token)
    }

    // MARK: Terminal transitions

    /// Open: drain all pending waiters exactly once. Idempotent.
    func open() {
        opened = true
        let ids = waiterOrder
        waiterOrder.removeAll()
        for id in ids {
            if let c = parkedWaiters.removeValue(forKey: id) {
                c.resume()
            } else {
                completedWaiters.insert(id)
            }
        }
    }

    /// Terminal teardown. Drains both pending waiters and observers
    /// exactly once. After `close()`, further register* calls latch as
    /// completed immediately so tests always converge.
    func close() {
        closed = true
        let wIds = waiterOrder
        waiterOrder.removeAll()
        for id in wIds {
            if let c = parkedWaiters.removeValue(forKey: id) {
                c.resume()
            } else {
                completedWaiters.insert(id)
            }
        }
        let oIds = observerOrder
        observerOrder.removeAll()
        for id in oIds {
            if let c = parkedObservers.removeValue(forKey: id) {
                c.resume()
            } else {
                completedObservers.insert(id)
            }
        }
    }

    // MARK: Introspection (test-only)

    func snapshot() -> Snapshot {
        Snapshot(
            pendingEntrySignals: pendingEntrySignals,
            observerOrder: observerOrder,
            waiterOrder: waiterOrder,
            completedObserverCount: completedObservers.count,
            completedWaiterCount: completedWaiters.count,
            parkedObserverCount: parkedObservers.count,
            parkedWaiterCount: parkedWaiters.count,
            opened: opened,
            closed: closed
        )
    }

    // MARK: Private

    private func signalEntryLocked() {
        if let id = observerOrder.first {
            observerOrder.removeFirst()
            if let c = parkedObservers.removeValue(forKey: id) {
                c.resume()
            } else {
                completedObservers.insert(id)
            }
        } else {
            pendingEntrySignals += 1
        }
    }
}
