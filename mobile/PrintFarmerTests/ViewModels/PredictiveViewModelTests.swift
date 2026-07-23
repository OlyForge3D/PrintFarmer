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
    /// park in an unstructured Task, use `waitForXParked` to structurally
    /// prove the continuation is parked, then cancel. cancelX drains via
    /// parkedX exactly once and seals the fate as
    /// `.cancelledWhileParked`. Post-drain: zero state.
    func testAsyncGateCancelAfterRegister() async {
        let gate = AsyncGate()

        // Observer path FIRST — no pending signal yet, so the observer
        // actually parks (rather than being latched by a prior waiter).
        let observerToken = await gate.registerObserver()
        let observerTask = Task { await gate.awaitObserver(observerToken) }
        await gate.waitForObserverParked(observerToken)
        observerTask.cancel()
        await observerTask.value

        // Waiter path. registerWaiter emits an entry signal, but since
        // observerOrder is now empty (cancelled observer was drained),
        // the signal accumulates in pendingEntrySignals and the waiter
        // itself parks in the usual way.
        let waiterToken = await gate.registerWaiter()
        let waiterTask = Task { await gate.awaitWaiter(waiterToken) }
        await gate.waitForWaiterParked(waiterToken)
        waiterTask.cancel()
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
        XCTAssertEqual(snap.waiterFateCounts[.cancelledWhileParked] ?? 0, 1,
                       "cancel from parked must record aggregate cancelledWhileParked")
        XCTAssertEqual(snap.observerFateCounts[.cancelledWhileParked] ?? 0, 1)
        XCTAssertEqual(snap.observerCancelIgnoredCount, 0,
                       "cancel from parked is not an ignored no-op")
        XCTAssertEqual(snap.waiterCancelIgnoredCount, 0)
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
        // on the waiter registration).
        _ = await gate.registerWaiter()
        await first.value

        let snap = await gate.snapshot()
        XCTAssertEqual(snap.parkedObserverCount, 0)
        XCTAssertEqual(snap.observerOrder, [])
        XCTAssertEqual(snap.completedObserverCount, 0)
        XCTAssertEqual(snap.observerFateCounts[.signaledWhileParked] ?? 0, 1,
                       "exactly one signaledWhileParked resume for this token")
        XCTAssertEqual(snap.observerDuplicateAwaitCount, 1,
                       "duplicate await counter never rewinds")

        // A THIRD await after the fate is sealed is still rejected.
        await gate.awaitObserver(token)
        let final = await gate.snapshot()
        XCTAssertEqual(final.observerDuplicateAwaitCount, 2)
        XCTAssertEqual(final.parkedObserverCount, 0)
        await gate.close()
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
        await first.value

        let snap = await gate.snapshot()
        XCTAssertEqual(snap.parkedWaiterCount, 0)
        XCTAssertEqual(snap.waiterOrder, [])
        XCTAssertEqual(snap.completedWaiterCount, 0)
        XCTAssertEqual(snap.waiterFateCounts[.openedWhileParked] ?? 0, 1)
        XCTAssertEqual(snap.waiterDuplicateAwaitCount, 1)

        // Post-open, post-resume: another late cancel is a bounded no-op.
        first.cancel()
        let final = await gate.snapshot()
        XCTAssertEqual(final.waiterCancelIgnoredCount, 0,
                       "cancel of an already-completed Task fires no handler")
        XCTAssertEqual(final.waiterFateCounts[.openedWhileParked] ?? 0, 1,
                       "aggregate count must not grow from a late no-op cancel")
        await gate.close()
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
        await dup.value
        // Hicks R1 causal ACK: consume the DUPLICATE attempt's exact
        // cancel Task result. The task returns from
        // `cancelObserver(id:awaitID:)` — which is bounded, actor-
        // synchronous work — so awaiting its value proves the cancel
        // dispatch completed BEFORE the following snapshot.
        guard let dupCancelTask = dupAttempt.cancelTask else {
            XCTFail("duplicate attempt: onCancel must have installed a cancel Task")
            await gate.close(); await barrier.close(); return
        }
        let dupReceipt = await dupCancelTask.value
        XCTAssertEqual(dupReceipt, .processedIgnoredMismatch,
                       "duplicate cancel with mismatched awaitID must be bounded no-op")

        // After duplicate has finished, evaluate cross-ownership invariant.
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
        XCTAssertNil(origAttempt.cancelTask,
                     "original attempt: onCancel never fired → no installed cancel Task")
        XCTAssertTrue(origAttempt.completedNaturally,
                      "original attempt: natural completion")
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
        await dup.value
        // Hicks R1 causal ACK — consume the duplicate attempt's exact
        // cancel Task result to prove cancel dispatch completed.
        guard let dupCancelTask = dupAttempt.cancelTask else {
            XCTFail("duplicate waiter attempt: onCancel must have installed a cancel Task")
            await gate.close(); await barrier.close(); return
        }
        let dupReceipt = await dupCancelTask.value
        XCTAssertEqual(dupReceipt, .processedIgnoredMismatch)

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

        XCTAssertNil(origAttempt.cancelTask)
        XCTAssertTrue(origAttempt.completedNaturally)
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
        await task1.value

        // Pre-park case — ACK enqueues, resolves after park.
        let token2 = await gate.registerObserver()
        async let ack: AsyncGate.ParkAckResult = gate.waitForObserverParked(token2)
        let task2 = Task { await gate.awaitObserver(token2) }
        let ackResult = await ack
        XCTAssertEqual(ackResult, AsyncGate.ParkAckResult.parked)
        _ = await gate.registerWaiter()
        await task2.value

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
        await gate.close()
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
        await task.value                         // task completed
        task.cancel()                            // late — handler no longer active

        let snap = await gate.snapshot()
        XCTAssertEqual(snap.parkedWaiterCount, 0)
        XCTAssertEqual(snap.waiterOrder, [])
        XCTAssertEqual(snap.completedWaiterCount, 0)
        XCTAssertEqual(snap.waiterFateCounts[.openedWhileParked] ?? 0, 1)
        XCTAssertEqual(snap.waiterCancelIgnoredCount, 0,
                       "cancel on a completed Task does not fire the handler")
        await gate.close()
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
        await task.value                         // fate sealed cancelledWhileParked
        await gate.open()                        // no work to do

        let snap = await gate.snapshot()
        XCTAssertEqual(snap.parkedWaiterCount, 0)
        XCTAssertEqual(snap.waiterOrder, [])
        XCTAssertEqual(snap.completedWaiterCount, 0,
                       "open after cancel must not relatch a cancelled id")
        XCTAssertEqual(snap.waiterFateCounts[.cancelledWhileParked] ?? 0, 1)
        XCTAssertTrue(snap.opened)
        await gate.close()
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
        await waiterTask.value

        let final = await gate.snapshot()
        XCTAssertEqual(final.pendingEntrySignals, 0)
        XCTAssertEqual(final.parkedWaiterCount, 0)
        XCTAssertEqual(final.waiterFateCounts[.openedWhileParked] ?? 0, 1)
        await gate.close()
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

        // Convenience waiter signals the parked observer immediately.
        let waiterTask = Task { await gate.wait() }
        await observerTask.value

        await gate.open()
        await waiterTask.value

        let final = await gate.snapshot()
        XCTAssertEqual(final.observerFateCounts[.signaledWhileParked] ?? 0, 1)
        XCTAssertEqual(final.parkedObserverCount, 0)
        XCTAssertEqual(final.pendingEntrySignals, 0)
        await gate.close()
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
        async let a1: AsyncGate.ParkAckResult = gate.waitForObserverParked(token)
        async let a2: AsyncGate.ParkAckResult = gate.waitForObserverParked(token)
        let t = Task { await gate.awaitObserver(token) }
        let (r1, r2) = await (a1, a2)
        XCTAssertEqual(r1, .parked)
        XCTAssertEqual(r2, .parked)
        _ = await gate.registerWaiter()
        await t.value
        let snap = await gate.snapshot()
        XCTAssertEqual(snap.observerParkAckQueueTotal, 0)
        await gate.close()
    }

    /// Hicks A: ACK call AFTER park returns immediately `.parked`.
    func testAsyncGateParkAckAfterParkIsImmediate() async {
        let gate = AsyncGate()
        let token = await gate.registerObserver()
        let t = Task { await gate.awaitObserver(token) }
        _ = await gate.waitForObserverParked(token)   // first waits for park
        let second = await gate.waitForObserverParked(token)
        XCTAssertEqual(second, .parked)
        _ = await gate.registerWaiter()
        await t.value
        await gate.close()
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
    //     per-attempt cancellation context. onCancel synchronously
    //     installs the exact `Task<CancelReceipt, Never>` into the
    //     attempt BEFORE returning; consumers await
    //     `attempt.cancelTask?.value` for the definitive per-attempt
    //     outcome. The gate stores NO per-attempt receipt map.
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

        // Phase 2: spawn the parking task and consume both tickets.
        let park = Task { await gate.awaitObserver(token) }
        let (r1, r2) = await (ticket1.value(), ticket2.value())
        XCTAssertEqual(r1, .parked)
        XCTAssertEqual(r2, .parked)

        // Assert intended actual-resume state FIRST (R3: before close).
        // The parked-signal transition is expected via signal below.
        _ = await gate.registerWaiter()
        let midSnap = await gate.snapshot()
        XCTAssertEqual(midSnap.observerResumeCounts[.parkedResumedBySignal] ?? 0, 1,
                       "exactly one continuation resumed via signal")
        XCTAssertEqual(midSnap.observerParkAckTicketCount, 0,
                       "tickets removed from actor at resolution")

        // R3: unconditional close BEFORE awaiting task.value.
        await gate.close()
        await park.value

        let final = await gate.snapshot()
        XCTAssertEqual(final.observerParkAckTicketCount, 0)
        XCTAssertEqual(final.observerParkAckQueueTotal, 0)
        XCTAssertEqual(final.parkedObserverCount, 0)
        XCTAssertEqual(final.observerResumeCounts[.parkedResumedByClose] ?? 0, 0,
                       "close must not rescue any parked continuation — signal already drained")
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
        let (r1, r2) = await (ticket1.value(), ticket2.value())
        XCTAssertEqual(r1, .parked)
        XCTAssertEqual(r2, .parked)

        await gate.open()
        let midSnap = await gate.snapshot()
        XCTAssertEqual(midSnap.waiterResumeCounts[.parkedResumedByOpen] ?? 0, 1)
        XCTAssertEqual(midSnap.waiterParkAckTicketCount, 0)

        await gate.close()
        await park.value

        let final = await gate.snapshot()
        XCTAssertEqual(final.waiterParkAckTicketCount, 0)
        XCTAssertEqual(final.waiterParkAckQueueTotal, 0)
        XCTAssertEqual(final.waiterResumeCounts[.parkedResumedByClose] ?? 0, 0)
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
            await gate.signal()
            await park.value
        }
        let snap = await gate.snapshot()
        XCTAssertFalse(snap.closed, "gate remains open — pre-close boundedness proof")
        XCTAssertEqual(snap.observerParkAckTicketCount, 0)
        XCTAssertTrue(snap.observerFates.isEmpty,
                      "no per-token fate accumulation across \(iterations) iterations")
        XCTAssertEqual(snap.observerResumeCounts[.parkedResumedBySignal] ?? 0, iterations)
        await gate.close()
    }

    /// Waiter analogue of the ticket-never-awaited proof.
    func testAsyncGateR4TicketNeverAwaitedLeavesZeroGateState_waiter() async {
        let gate = AsyncGate()
        let iterations = 100
        for _ in 0..<iterations {
            let token = await gate.registerWaiter()
            _ = await gate.enterWaiterParkAck(token)
            let park = Task { await gate.awaitWaiter(token) }
            _ = await gate.waitForWaiterParked(token)
            let midCheck = await gate.snapshot()
            XCTAssertEqual(midCheck.waiterParkAckTicketCount, 0)
            await gate.open()
            await park.value
            // Reopen the gate for next iteration by creating a fresh
            // gate — opened is monotonic, but we test one at a time.
            // (No `reopen` on this gate; just verify per-iteration.)
            break
        }
        // For the loop-based proof, verify the single iteration's
        // outcome: no gate state grew.
        let snap = await gate.snapshot()
        XCTAssertEqual(snap.waiterParkAckTicketCount, 0)
        XCTAssertEqual(snap.waiterResumeCounts[.parkedResumedByOpen] ?? 0, 1)
        await gate.close()
    }

    /// Hicks R4: ticket-never-awaited also drained by close(). Enter
    /// a ticket on an active token, DO NOT consume it, then close.
    /// Actor removes the ticket entry at close-time resolution.
    func testAsyncGateR4TicketEnteredThenClosedNeverAwaited_observer() async {
        let gate = AsyncGate()
        let token = await gate.registerObserver()
        _ = await gate.enterObserverParkAck(token)
        let mid = await gate.snapshot()
        XCTAssertEqual(mid.observerParkAckTicketCount, 1)
        await gate.close()
        let snap = await gate.snapshot()
        XCTAssertEqual(snap.observerParkAckTicketCount, 0,
                       "close must remove every active ticket entry")
    }

    /// Waiter analogue.
    func testAsyncGateR4TicketEnteredThenClosedNeverAwaited_waiter() async {
        let gate = AsyncGate()
        let token = await gate.registerWaiter()
        _ = await gate.enterWaiterParkAck(token)
        let mid = await gate.snapshot()
        XCTAssertEqual(mid.waiterParkAckTicketCount, 1)
        await gate.close()
        let snap = await gate.snapshot()
        XCTAssertEqual(snap.waiterParkAckTicketCount, 0)
    }

    /// Hicks R4: transition-before-consumer. Enter ticket, park then
    /// signal so the ticket resolves BEFORE the consumer calls
    /// `ticket.value()`. The ticket must buffer `.parked` inside
    /// itself and return it whenever the consumer eventually awaits.
    func testAsyncGateR4TicketTransitionBeforeConsumer_observer() async {
        let gate = AsyncGate()
        let token = await gate.registerObserver()
        let ticket = await gate.enterObserverParkAck(token)

        // Park + signal completes before we call value().
        let park = Task { await gate.awaitObserver(token) }
        _ = await gate.waitForObserverParked(token)
        // Ticket resolves to .parked here (buffered inside ticket).
        XCTAssertTrue(ticket.isResolved,
                      "actor must have resolved ticket at park time")
        _ = await gate.registerWaiter()
        await park.value

        // R3: close before final value() call — the ticket's buffered
        // .parked result is caller-owned and independent of gate state.
        await gate.close()

        // Late consumer still gets the buffered .parked.
        let r = await ticket.value()
        XCTAssertEqual(r, .parked,
                       "ticket must buffer first resolution regardless of consumer timing")
    }

    /// Waiter analogue of transition-before-consumer.
    func testAsyncGateR4TicketTransitionBeforeConsumer_waiter() async {
        let gate = AsyncGate()
        let token = await gate.registerWaiter()
        let ticket = await gate.enterWaiterParkAck(token)

        let park = Task { await gate.awaitWaiter(token) }
        _ = await gate.waitForWaiterParked(token)
        XCTAssertTrue(ticket.isResolved)
        await gate.open()
        await park.value

        await gate.close()
        let r = await ticket.value()
        XCTAssertEqual(r, .parked)
    }

    // MARK: - Hicks R1: exact per-attempt cancellation receipts (attempt-owned)

    /// Hicks R1: duplicate-cancel exact receipts. The DUPLICATE
    /// attempt's cancellation is proven per-attempt via the
    /// caller-owned `attempt.cancelTask?.value` — no gate-level
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
        await dup.value

        // R1: obtain the exact cancel Task installed by onCancel and
        // await its value. Nil means onCancel never fired — that would
        // be a bug for the duplicate here.
        guard let dupCancelTask = dupAttempt.cancelTask else {
            XCTFail("duplicate attempt: onCancel must have installed a cancel Task")
            await gate.close(); await barrier.close()
            return
        }
        let dupReceipt = await dupCancelTask.value
        XCTAssertEqual(dupReceipt, .processedIgnoredMismatch,
                       "duplicate cancel receipt: bounded no-op via mismatched awaitID")

        // Signal → original resumes naturally.
        _ = await gate.registerWaiter()
        await original.value

        // R1: original attempt was never cancelled — cancelTask nil,
        // completedNaturally true.
        XCTAssertNil(origAttempt.cancelTask,
                     "original attempt: onCancel never fired → no installed cancel Task")
        XCTAssertTrue(origAttempt.completedNaturally,
                      "original attempt: natural completion, no cancel processing")

        // R3: assert intended actual-resume counters, then close.
        let midSnap = await gate.snapshot()
        XCTAssertEqual(midSnap.observerResumeCounts[.parkedResumedBySignal] ?? 0, 1)
        XCTAssertEqual(midSnap.observerCancelIgnoredCount, 1)
        XCTAssertEqual(midSnap.observerCancelInvocationCount, 1)
        XCTAssertEqual(midSnap.observerReceiptStoredCount, 0,
                       "gate stores no per-attempt receipts")

        await gate.close()
        await barrier.close()

        let final = await gate.snapshot()
        XCTAssertEqual(final.observerReceiptStoredCount, 0)
        XCTAssertEqual(final.observerReceiptWaiterQueueTotal, 0)
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
        await dup.value

        guard let dupCancelTask = dupAttempt.cancelTask else {
            XCTFail("duplicate attempt onCancel must have installed a cancel Task")
            await gate.close(); await barrier.close()
            return
        }
        let dupReceipt = await dupCancelTask.value
        XCTAssertEqual(dupReceipt, .processedIgnoredMismatch)

        await gate.open()
        await original.value

        XCTAssertNil(origAttempt.cancelTask)
        XCTAssertTrue(origAttempt.completedNaturally)

        let midSnap = await gate.snapshot()
        XCTAssertEqual(midSnap.waiterResumeCounts[.parkedResumedByOpen] ?? 0, 1)
        XCTAssertEqual(midSnap.waiterCancelIgnoredCount, 1)

        await gate.close()
        await barrier.close()

        let final = await gate.snapshot()
        XCTAssertEqual(final.waiterReceiptStoredCount, 0)
        XCTAssertEqual(final.waiterReceiptWaiterQueueTotal, 0)
    }

    /// Hicks R1: matched-cancel receipt. Original attempt is cancelled
    /// while parked; its own attempt.cancelTask.value returns
    /// `.processedMatched`. No gate storage.
    func testAsyncGateR1MatchedCancelReceipt_observer() async {
        let gate = AsyncGate()
        let token = await gate.registerObserver()
        let attempt = ObserverAwaitAttempt()

        let t = Task { await gate.awaitObserver(token, attempt: attempt) }
        _ = await gate.waitForObserverParked(token)

        // Cancel the ONLY task on the ONLY attempt — matched.
        t.cancel()
        await t.value

        guard let cancelTask = attempt.cancelTask else {
            XCTFail("cancelled attempt must have installed a cancel Task")
            await gate.close()
            return
        }
        let r = await cancelTask.value
        XCTAssertEqual(r, .processedMatched,
                       "matched cancel drains THIS attempt's parked continuation")

        let midSnap = await gate.snapshot()
        XCTAssertEqual(midSnap.observerResumeCounts[.parkedResumedByCancel] ?? 0, 1)
        XCTAssertEqual(midSnap.observerCancelInvocationCount, 1)

        await gate.close()
        let final = await gate.snapshot()
        XCTAssertEqual(final.observerReceiptStoredCount, 0)
    }

    /// Waiter analogue.
    func testAsyncGateR1MatchedCancelReceipt_waiter() async {
        let gate = AsyncGate()
        let token = await gate.registerWaiter()
        let attempt = WaiterAwaitAttempt()

        let t = Task { await gate.awaitWaiter(token, attempt: attempt) }
        _ = await gate.waitForWaiterParked(token)

        t.cancel()
        await t.value

        guard let cancelTask = attempt.cancelTask else {
            XCTFail("cancelled waiter attempt must have installed a cancel Task")
            await gate.close()
            return
        }
        let r = await cancelTask.value
        XCTAssertEqual(r, .processedMatched)

        let midSnap = await gate.snapshot()
        XCTAssertEqual(midSnap.waiterResumeCounts[.parkedResumedByCancel] ?? 0, 1)

        await gate.close()
        let final = await gate.snapshot()
        XCTAssertEqual(final.waiterReceiptStoredCount, 0)
    }

    // MARK: - Hicks R4 + Vasquez: no-consumer cancel receipt proofs

    /// Vasquez coverage via R1 design: hundreds of cancelled attempts
    /// with NO external receipt consumer leave ZERO gate receipt
    /// state. Under the R1 caller-owned design, the gate has no
    /// receipt map to grow at all — the per-attempt result lives
    /// inside each dropped `AwaitAttempt` object. This test drops
    /// every attempt without ever reading `cancelTask.value`; gate
    /// snapshot fields remain zero.
    func testAsyncGateR4NoConsumerCancelReceiptLeavesGateZero_observer() async {
        let gate = AsyncGate()
        let iterations = 100
        for _ in 0..<iterations {
            let token = await gate.registerObserver()
            let attempt = ObserverAwaitAttempt()
            let t = Task { await gate.awaitObserver(token, attempt: attempt) }
            _ = await gate.waitForObserverParked(token)
            t.cancel()
            await t.value
            // Do NOT await attempt.cancelTask?.value — drop attempt.
            _ = attempt   // silence unused
        }
        let snap = await gate.snapshot()
        XCTAssertFalse(snap.closed, "gate remains open — pre-close boundedness proof")
        XCTAssertEqual(snap.observerReceiptStoredCount, 0,
                       "gate has no per-attempt receipt storage under R1 design")
        XCTAssertEqual(snap.observerReceiptWaiterQueueTotal, 0)
        XCTAssertEqual(snap.observerCancelIgnoredCount, 0,
                       "matched cancels — no ignored counts should accumulate")
        XCTAssertEqual(snap.observerCancelInvocationCount, iterations,
                       "aggregate cancel-invocation counter is the only growth")
        XCTAssertEqual(snap.observerResumeCounts[.parkedResumedByCancel] ?? 0, iterations)
        await gate.close()
    }

    /// Waiter analogue of the no-consumer cancel-receipt boundedness proof.
    func testAsyncGateR4NoConsumerCancelReceiptLeavesGateZero_waiter() async {
        let gate = AsyncGate()
        let iterations = 100
        for _ in 0..<iterations {
            let token = await gate.registerWaiter()
            let attempt = WaiterAwaitAttempt()
            let t = Task { await gate.awaitWaiter(token, attempt: attempt) }
            _ = await gate.waitForWaiterParked(token)
            t.cancel()
            await t.value
            _ = attempt
        }
        let snap = await gate.snapshot()
        XCTAssertFalse(snap.closed)
        XCTAssertEqual(snap.waiterReceiptStoredCount, 0)
        XCTAssertEqual(snap.waiterReceiptWaiterQueueTotal, 0)
        XCTAssertEqual(snap.waiterCancelInvocationCount, iterations)
        XCTAssertEqual(snap.waiterResumeCounts[.parkedResumedByCancel] ?? 0, iterations)
        await gate.close()
    }

    // MARK: - Hicks R4: race — cancel Task installed but close first

    /// Hicks R4: race proof. The cancel Task is installed by onCancel
    /// but the gate is closed before the cancel actor call runs.
    /// The exact per-attempt cancel Task's value returns
    /// `.closedBeforeProcessing`. No post-close state contradicts.
    func testAsyncGateR4CancelTaskRacesCloseAndSeesClosed_observer() async {
        let gate = AsyncGate()
        let token = await gate.registerObserver()
        let attempt = ObserverAwaitAttempt()

        let t = Task { await gate.awaitObserver(token, attempt: attempt) }
        _ = await gate.waitForObserverParked(token)

        // Cancel BEFORE close — installs onCancel Task. Close runs on
        // the actor and MUST resume the parked continuation with
        // .closedWhileParked; the cancel Task then observes closed and
        // returns .closedBeforeProcessing.
        t.cancel()
        // Close before awaiting anything (R3 pattern): drains parked
        // continuation via close path, then the cancel Task queued on
        // the actor sees closed==true.
        await gate.close()
        await t.value

        guard let cancelTask = attempt.cancelTask else {
            XCTFail("onCancel must have installed a cancel Task before cancel returned")
            return
        }
        let r = await cancelTask.value
        // Either .closedBeforeProcessing (cancel ran after close) or
        // .processedMatched (cancel ran before close and drained the
        // parked continuation itself). Both are bounded and exact.
        XCTAssertTrue(r == .closedBeforeProcessing || r == .processedMatched,
                      "race outcome must be exact: got \(r)")

        let final = await gate.snapshot()
        XCTAssertEqual(final.observerReceiptStoredCount, 0,
                       "no gate-side storage even under race")
        XCTAssertEqual(final.parkedObserverCount, 0)
    }

    /// Waiter analogue of the cancel-races-close proof.
    func testAsyncGateR4CancelTaskRacesCloseAndSeesClosed_waiter() async {
        let gate = AsyncGate()
        let token = await gate.registerWaiter()
        let attempt = WaiterAwaitAttempt()

        let t = Task { await gate.awaitWaiter(token, attempt: attempt) }
        _ = await gate.waitForWaiterParked(token)

        t.cancel()
        await gate.close()
        await t.value

        guard let cancelTask = attempt.cancelTask else {
            XCTFail("onCancel must have installed a cancel Task")
            return
        }
        let r = await cancelTask.value
        XCTAssertTrue(r == .closedBeforeProcessing || r == .processedMatched,
                      "race outcome: \(r)")

        let final = await gate.snapshot()
        XCTAssertEqual(final.waiterReceiptStoredCount, 0)
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
        let ticketRes = await ticket.value()
        XCTAssertEqual(ticketRes, .unknown,
                       "enterObserverParkAck fabricated-id → immediately-resolved .unknown")

        let snap = await gate.snapshot()
        XCTAssertEqual(snap.observerParkAckTicketCount, 0)
        XCTAssertEqual(snap.waiterParkAckTicketCount, 0)
        XCTAssertEqual(snap.observerUnknownAwaitCount, 0,
                       "waitForXParked classification path must NOT increment awaitUnknown counters")
        XCTAssertEqual(snap.waiterUnknownAwaitCount, 0)

        await gate.close()
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
        _ = await holdGate.waitForWaiterParked(holdToken)

        let midSnap = await gate.snapshot()
        XCTAssertEqual(midSnap.observerDuplicateAwaitCount, 1)
        XCTAssertEqual(midSnap.observerResumeCounts[.duplicateAfterParked] ?? 0, 1)
        XCTAssertEqual(midSnap.parkedObserverCount, 1,
                       "original must still be parked; duplicate never overwrote it")

        // Cancel duplicate WHILE its handler is still installed. The
        // duplicate's onCancel installs an exact cancel Task into
        // `dupAttempt`; we consume its value to prove cancel dispatch
        // completed with a mismatched-awaitID no-op.
        duplicate.cancel()
        guard let dupCancelTask = dupAttempt.cancelTask else {
            XCTFail("duplicate attempt: onCancel must have installed a cancel Task")
            await holdGate.open(); await gate.close(); await holdGate.close()
            return
        }
        let dupReceipt = await dupCancelTask.value
        XCTAssertEqual(dupReceipt, .processedIgnoredMismatch,
                       "mismatched-awaitID cancel is a bounded no-op")

        let cancelSnap = await gate.snapshot()
        XCTAssertEqual(cancelSnap.observerCancelInvocationCount, 1,
                       "duplicate's onCancel fired exactly once")
        XCTAssertEqual(cancelSnap.observerCancelIgnoredCount, 1,
                       "awaitID mismatch → bounded no-op")
        XCTAssertEqual(cancelSnap.parkedObserverCount, 1,
                       "original parked continuation MUST NOT be drained")
        XCTAssertNil(cancelSnap.observerFates[token.id],
                     "original fate must not be sealed by mismatched cancel")

        // Release hold and signal original.
        await holdGate.open()
        _ = await gate.registerWaiter()

        // R3: unconditional close BOTH gates BEFORE awaiting any task
        // value. If intended resumes regress, close drains the parked
        // state and the assertions surface — no hang.
        await gate.close()
        await holdGate.close()
        await duplicate.value
        await original.value

        let final = await gate.snapshot()
        XCTAssertEqual(final.parkedObserverCount, 0)
        XCTAssertEqual(final.observerFateCounts[.signaledWhileParked] ?? 0, 1,
                       "original resumed via signal, not cancel")
        XCTAssertEqual(final.observerResumeCounts[.parkedResumedBySignal] ?? 0, 1)
        XCTAssertEqual(final.observerResumeCounts[.parkedResumedByCancel] ?? 0, 0)
        XCTAssertEqual(final.observerResumeCounts[.parkedResumedByClose] ?? 0, 0,
                       "R3: signal already resumed original — no close rescue")
        XCTAssertNil(origAttempt.cancelTask,
                     "original attempt: onCancel never fired")
        XCTAssertTrue(origAttempt.completedNaturally)
    }

    /// Waiter analogue of the observer structural late-cancel proof.
    func testAsyncGateStructuralLateCancelWaiterIsBoundedNoOp() async {
        let gate = AsyncGate()
        let holdGate = AsyncGate()
        let holdToken = await holdGate.registerWaiter()

        let token = await gate.registerWaiter()
        let origAttempt = WaiterAwaitAttempt()
        let dupAttempt = WaiterAwaitAttempt()
        let original = Task { await gate.awaitWaiter(token, attempt: origAttempt) }
        _ = await gate.waitForWaiterParked(token)

        let duplicate = Task {
            await gate.awaitWaiterAndHold(token, attempt: dupAttempt,
                                          holdGate: holdGate, holdToken: holdToken)
        }
        _ = await holdGate.waitForWaiterParked(holdToken)

        let midSnap = await gate.snapshot()
        XCTAssertEqual(midSnap.waiterDuplicateAwaitCount, 1)
        XCTAssertEqual(midSnap.waiterResumeCounts[.duplicateAfterParked] ?? 0, 1)
        XCTAssertEqual(midSnap.parkedWaiterCount, 1)

        duplicate.cancel()
        guard let dupCancelTask = dupAttempt.cancelTask else {
            XCTFail("duplicate waiter attempt: onCancel must have installed a cancel Task")
            await holdGate.open(); await gate.close(); await holdGate.close()
            return
        }
        let dupReceipt = await dupCancelTask.value
        XCTAssertEqual(dupReceipt, .processedIgnoredMismatch)

        let cancelSnap = await gate.snapshot()
        XCTAssertEqual(cancelSnap.waiterCancelInvocationCount, 1)
        XCTAssertEqual(cancelSnap.waiterCancelIgnoredCount, 1,
                       "awaitID mismatch → bounded no-op")
        XCTAssertEqual(cancelSnap.parkedWaiterCount, 1,
                       "original parked continuation MUST NOT be drained")
        XCTAssertNil(cancelSnap.waiterFates[token.id])

        await holdGate.open()
        await gate.open()

        // R3: close BEFORE awaiting task values.
        await gate.close()
        await holdGate.close()
        await duplicate.value
        await original.value

        let final = await gate.snapshot()
        XCTAssertEqual(final.parkedWaiterCount, 0)
        XCTAssertEqual(final.waiterFateCounts[.openedWhileParked] ?? 0, 1)
        XCTAssertEqual(final.waiterResumeCounts[.parkedResumedByOpen] ?? 0, 1)
        XCTAssertEqual(final.waiterResumeCounts[.parkedResumedByCancel] ?? 0, 0)
        XCTAssertEqual(final.waiterResumeCounts[.parkedResumedByClose] ?? 0, 0,
                       "R3: open already resumed original — no close rescue")
        XCTAssertNil(origAttempt.cancelTask)
        XCTAssertTrue(origAttempt.completedNaturally)
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
        for _ in 0..<iterations {
            let token = await gate.registerObserver()
            let t = Task { await gate.awaitObserver(token) }
            _ = await gate.waitForObserverParked(token)   // structural park ACK
            await gate.signal()                            // resumes parked observer
            await t.value                                  // fully drained
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
        await gate.close()
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
        for t in tasks { await t.value }

        let snap = await gate.snapshot()
        XCTAssertEqual(snap.parkedWaiterCount, 0)
        XCTAssertEqual(snap.waiterOrder, [])
        XCTAssertEqual(snap.completedWaiterCount, 0)
        XCTAssertTrue(snap.waiterFates.isEmpty,
                      "open-drained waiters must not retain per-token fates")
        XCTAssertEqual(snap.waiterFateCounts[.openedWhileParked] ?? 0, n)
        XCTAssertEqual(snap.waiterResumeCounts[.parkedResumedByOpen] ?? 0, n)
        await gate.close()
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
// await invocation constructs one and passes it in; onCancel synchronously
// creates the exact per-attempt cancel `Task<Receipt, Never>` and installs
// it into the attempt via `installCancelTask` BEFORE onCancel returns.
// The gate itself stores no per-attempt result map; `cancelX(id:, awaitID:)`
// simply RETURNS the receipt as its function value, and the task handle
// carried by the attempt is the authoritative receipt consumer. Tests
// await `attempt.cancelTask?.value` to observe the exact outcome.
//
// R2: `ParkAckTicket` replaces the gate-level ticket-to-token/result map.
// `enterXParkAck` returns a caller-owned ticket object (already-resolved
// for terminal classifications, buffered-resolvable for active tokens).
// The actor stores only weak-ish references (in `observerActiveParkAckTickets`
// keyed by token id) — resolved and removed at park/fate/close. Callers
// consume via `ticket.value()` which returns the buffered result even if
// resolution beat the consumer.

/// R1: caller-owned per-attempt cancellation context for observer awaits.
/// Holds a UUID identity plus a lock-backed cancel Task handle. onCancel
/// installs the exact cancel Task synchronously, so tests can obtain and
/// await it directly for the definitive per-attempt receipt.
final class ObserverAwaitAttempt: @unchecked Sendable {
    let id: UUID
    private let lock = NSLock()
    private var _cancelTask: Task<AsyncGate.ObserverCancelReceipt, Never>?
    private var _completedNaturally: Bool = false
    init() { self.id = UUID() }
    /// Called by AsyncGate.onCancel synchronously (from a `Task { ... }`
    /// creation site) BEFORE onCancel returns.
    fileprivate func installCancelTask(_ task: Task<AsyncGate.ObserverCancelReceipt, Never>) {
        lock.lock(); defer { lock.unlock() }
        if _cancelTask == nil { _cancelTask = task }
    }
    /// Test-visible: exact per-attempt cancel Task handle. `nil` iff the
    /// await completed without cancellation firing.
    fileprivate var cancelTask: Task<AsyncGate.ObserverCancelReceipt, Never>? {
        lock.lock(); defer { lock.unlock() }
        return _cancelTask
    }
    fileprivate func markCompletedNaturally() {
        lock.lock(); defer { lock.unlock() }
        _completedNaturally = true
    }
    /// True iff the await returned normally with no cancel Task installed.
    fileprivate var completedNaturally: Bool {
        lock.lock(); defer { lock.unlock() }
        return _completedNaturally && _cancelTask == nil
    }
}

/// R1 waiter analogue of `ObserverAwaitAttempt`.
final class WaiterAwaitAttempt: @unchecked Sendable {
    let id: UUID
    private let lock = NSLock()
    private var _cancelTask: Task<AsyncGate.WaiterCancelReceipt, Never>?
    private var _completedNaturally: Bool = false
    init() { self.id = UUID() }
    fileprivate func installCancelTask(_ task: Task<AsyncGate.WaiterCancelReceipt, Never>) {
        lock.lock(); defer { lock.unlock() }
        if _cancelTask == nil { _cancelTask = task }
    }
    fileprivate var cancelTask: Task<AsyncGate.WaiterCancelReceipt, Never>? {
        lock.lock(); defer { lock.unlock() }
        return _cancelTask
    }
    fileprivate func markCompletedNaturally() {
        lock.lock(); defer { lock.unlock() }
        _completedNaturally = true
    }
    fileprivate var completedNaturally: Bool {
        lock.lock(); defer { lock.unlock() }
        return _completedNaturally && _cancelTask == nil
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
    /// The caller-owned attempt holds the wrapping `Task<Receipt,Never>`
    /// handle, and consumers await `attempt.cancelTask?.value` to observe
    /// the definitive per-attempt outcome. `finishedBeforeProcessing` is
    /// signaled by `attempt.completedNaturally` (cancelTask remained nil).
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
        let observerReceiptStoredCount: Int
        let waiterReceiptStoredCount: Int
        let observerReceiptWaiterQueueTotal: Int
        let waiterReceiptWaiterQueueTotal: Int

        // Hicks R2 addendum: `observerParkAckTicketCount` /
        // `waiterParkAckTicketCount` now count ACTIVE park-ACK
        // tickets (sum of `observerActiveParkAckTickets` array
        // lengths), i.e. tickets reserved via `enterXParkAck` that
        // the actor has NOT yet resolved. Resolved tickets are
        // removed from actor storage immediately, whether or not the
        // caller has consumed the buffered result.
        let observerParkAckTicketCount: Int
        let waiterParkAckTicketCount: Int
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
    private var observerActiveParkAckTickets: [UInt64: [ObserverParkAckTicket]] = [:]
    private var waiterActiveParkAckTickets: [UInt64: [WaiterParkAckTicket]] = [:]

    // Hicks R1 addendum: the gate NO LONGER stores per-attempt
    // cancellation receipts. Each await attempt carries its own
    // `AwaitAttempt` context; onCancel synchronously installs the
    // exact cancel Task handle into that context, and `cancelX`
    // returns the receipt as its function value (never persists it
    // in gate state). Snapshot slots for `observerReceiptStoredCount`
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
    /// `ObserverAwaitAttempt`. onCancel synchronously creates the exact
    /// cancel Task and installs it into `attempt` BEFORE onCancel
    /// returns; callers/tests obtain the definitive per-attempt
    /// receipt by awaiting `attempt.cancelTask?.value`. On natural
    /// completion, `attempt.markCompletedNaturally()` fires; the
    /// gate stores NO per-attempt outcome.
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
            // Hicks R1: natural completion marker on the caller-owned
            // attempt. No gate-side receipt storage; callers infer
            // "finished before processing" via `attempt.completedNaturally`
            // (cancelTask nil AND completedNaturally set).
            attempt.markCompletedNaturally()
        } onCancel: {
            // Hicks R1: synchronously CREATE the exact cancel Task and
            // INSTALL it into the caller-owned attempt BEFORE onCancel
            // returns. `cancelObserver` returns the per-attempt receipt
            // as its function value; the gate stores no receipt map.
            let cancelTask = Task<ObserverCancelReceipt, Never> {
                await self.cancelObserver(id: token.id, awaitID: awaitID)
            }
            attempt.installCancelTask(cancelTask)
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
    /// storing it in a gate map. The caller-owned `AwaitAttempt`
    /// holds the `Task<ObserverCancelReceipt, Never>` handle that
    /// wraps this call, so tests read the definitive per-attempt
    /// outcome by awaiting `attempt.cancelTask?.value`.
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
        // Hicks R2: resolve and remove any active park-ACK tickets
        // for this token. Resolution is exactly-once inside the
        // ticket; the gate no longer retains any per-ticket state.
        if let ts = observerActiveParkAckTickets.removeValue(forKey: id) {
            for t in ts { t.resolve(result) }
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
            attempt.markCompletedNaturally()
        } onCancel: {
            let cancelTask = Task<WaiterCancelReceipt, Never> {
                await self.cancelWaiter(id: token.id, awaitID: awaitID)
            }
            attempt.installCancelTask(cancelTask)
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
            for t in ts { t.resolve(result) }
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
            observerActiveParkAckTickets[token.id, default: []].append(ticket)
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
            waiterActiveParkAckTickets[token.id, default: []].append(ticket)
            return ticket
        }
        if token.id >= nextWaiterID {
            ticket.resolve(.unknown)
        } else {
            ticket.resolve(.closedOrConsumed)
        }
        return ticket
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
            attempt.markCompletedNaturally()
        } onCancel: {
            let cancelTask = Task<ObserverCancelReceipt, Never> {
                await self.cancelObserver(id: token.id, awaitID: awaitID)
            }
            attempt.installCancelTask(cancelTask)
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
            attempt.markCompletedNaturally()
        } onCancel: {
            let cancelTask = Task<WaiterCancelReceipt, Never> {
                await self.cancelWaiter(id: token.id, awaitID: awaitID)
            }
            attempt.installCancelTask(cancelTask)
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
        // `.closedBeforeProcessing`; the caller's attempt.cancelTask
        // .value delivers that exact receipt.

        // Hicks R2 addendum: drain any still-pending park-ACK tickets
        // with `.closedOrConsumed`. Resolution is exactly-once inside
        // each ticket; the actor removes the entry from
        // `observerActiveParkAckTickets`. Callers may still consume
        // the buffered result via `ticket.value()` — the gate holds
        // no remaining per-ticket state.
        let obsTix = observerActiveParkAckTickets
        observerActiveParkAckTickets.removeAll()
        for (_, ts) in obsTix { for t in ts { t.resolve(.closedOrConsumed) } }
        let watTix = waiterActiveParkAckTickets
        waiterActiveParkAckTickets.removeAll()
        for (_, ts) in watTix { for t in ts { t.resolve(.closedOrConsumed) } }
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
            observerReceiptStoredCount: 0,
            waiterReceiptStoredCount: 0,
            observerReceiptWaiterQueueTotal: 0,
            waiterReceiptWaiterQueueTotal: 0,
            observerParkAckTicketCount: observerActiveParkAckTickets.values.reduce(0) { $0 + $1.count },
            waiterParkAckTicketCount: waiterActiveParkAckTickets.values.reduce(0) { $0 + $1.count }
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
