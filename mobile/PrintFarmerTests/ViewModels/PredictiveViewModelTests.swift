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
        XCTAssertEqual(snap.waiterFates[waiterToken.id], .cancelledWhileParked,
                       "cancel from parked must seal fate cancelledWhileParked")
        XCTAssertEqual(snap.observerFates[observerToken.id], .cancelledWhileParked)
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
        XCTAssertEqual(snap.observerFates[token.id], .signaledWhileParked)
        XCTAssertEqual(snap.observerFateCounts[.signaledWhileParked], 1,
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
        XCTAssertEqual(snap.waiterFates[token.id], .openedWhileParked)
        XCTAssertEqual(snap.waiterFateCounts[.openedWhileParked], 1)
        XCTAssertEqual(snap.waiterDuplicateAwaitCount, 1)

        // Post-open, post-resume: another late cancel is a bounded no-op.
        first.cancel()
        let final = await gate.snapshot()
        XCTAssertEqual(final.waiterCancelIgnoredCount, 0,
                       "cancel of an already-completed Task fires no handler")
        XCTAssertEqual(final.waiterFates[token.id], .openedWhileParked,
                       "fate must not be overwritten by late cancel")
        await gate.close()
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
        // Every registration was latched as closed-before-park.
        XCTAssertEqual(snap.waiterFateCounts[.closedBeforePark], 10)
        XCTAssertEqual(snap.observerFateCounts[.closedBeforePark], 10)
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
        async let ack: Void = gate.waitForObserverParked(token2)
        let task2 = Task { await gate.awaitObserver(token2) }
        await ack
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
        XCTAssertEqual(final.observerFates[token3.id], .signaledBeforePark)
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
        XCTAssertEqual(snap.waiterFates[token.id], .openedWhileParked)
        XCTAssertEqual(snap.waiterFateCounts[.openedWhileParked], 1)
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
        XCTAssertEqual(snap.waiterFates[token.id], .cancelledWhileParked)
        XCTAssertEqual(snap.waiterFateCounts[.cancelledWhileParked], 1)
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
        XCTAssertEqual(snap.waiterFates[waiterToken.id], .closedWhileParked)
        XCTAssertEqual(snap.observerFates[parkedObserverToken.id], .closedWhileParked)
        XCTAssertEqual(snap.observerFates[observerToken.id], .signaledBeforePark,
                       "already-latched observer keeps its prior fate through close")
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
        XCTAssertEqual(final.waiterFates[waiterToken.id], .openedWhileParked)
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
        XCTAssertEqual(final.observerFates[observerToken.id], .signaledWhileParked)
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
    }

    // MARK: State
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

    // H3: park-ACK queues — tests can await proof that a specific token
    // has parked, without polling or Task.yield.
    private var observerParkAcks: [UInt64: [CheckedContinuation<Void, Never>]] = [:]
    private var waiterParkAcks: [UInt64: [CheckedContinuation<Void, Never>]] = [:]

    // MARK: Observer (entry-side)

    /// Reserve an observer slot. Synchronous-in-actor.
    func registerObserver() -> ObserverToken {
        let id = nextID; nextID &+= 1
        if closed {
            completedObservers.insert(id)
            sealObserverFate(id: id, reason: .closedBeforePark)
        } else if pendingEntrySignals > 0 {
            pendingEntrySignals -= 1
            completedObservers.insert(id)
            sealObserverFate(id: id, reason: .signaledBeforePark)
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
    func awaitObserver(_ token: ObserverToken) async {
        await withTaskCancellationHandler {
            await withCheckedContinuation { c in
                // (1) Token already has a terminal fate — this is the
                //     normal latched path OR a duplicate await. Consume
                //     the completed latch if present, resume immediately.
                if observerFates[token.id] != nil {
                    if completedObservers.remove(token.id) == nil {
                        // No latch to consume — either already resumed
                        // OR the fate was cancelled/close-drained. This
                        // is duplicate-await territory.
                        observerDuplicateAwaitCount += 1
                    }
                    c.resume()
                    return
                }
                // (2) Same token already has a live parked continuation.
                //     Second concurrent await is a bug; reject the second
                //     without touching the first parked continuation.
                if parkedObservers[token.id] != nil {
                    observerDuplicateAwaitCount += 1
                    c.resume()
                    return
                }
                // (3) Token not in the registered set at all — unknown
                //     token. Bounded no-op.
                if !observerOrder.contains(token.id) {
                    observerUnknownAwaitCount += 1
                    c.resume()
                    return
                }
                // (4) Registered, not yet fated, not yet parked, but the
                //     gate has been closed since registration. Drain
                //     from the registration queue and seal fate.
                if closed {
                    observerOrder.removeAll { $0 == token.id }
                    sealObserverFate(id: token.id, reason: .closedBeforePark)
                    c.resume()
                    return
                }
                // (5) Normal park. Any concurrently-queued cancelObserver
                //     Task will only be dispatched on this actor after
                //     this closure returns and the enclosing `await`
                //     suspends, so `cancelObserver` always finds the
                //     parked continuation.
                parkedObservers[token.id] = c
                flushObserverParkAcks(id: token.id)
            }
        } onCancel: {
            Task { await self.cancelObserver(id: token.id) }
        }
    }

    /// Late-cancel-safe. Mutates state ONLY when a continuation is
    /// currently parked for this id. Every other case is a bounded
    /// no-op that increments `observerCancelIgnoredCount` and never
    /// relatches into `completedObservers` or overwrites a fate.
    private func cancelObserver(id: UInt64) {
        guard let c = parkedObservers.removeValue(forKey: id) else {
            observerCancelIgnoredCount += 1
            return
        }
        observerOrder.removeAll { $0 == id }
        sealObserverFate(id: id, reason: .cancelledWhileParked)
        c.resume()
    }

    /// Convenience: single-shot observer registration + await.
    func waitForEntry() async {
        let token = registerObserver()
        await awaitObserver(token)
    }

    /// H3 park-ACK: suspend until the given observer token has actually
    /// parked (or reached a terminal fate). Lost-wakeup-safe: if the
    /// token is already parked/fated the call returns without
    /// suspending; otherwise the caller is queued and resumed the
    /// moment `awaitObserver` parks or the token gets a fate.
    func waitForObserverParked(_ token: ObserverToken) async {
        if parkedObservers[token.id] != nil || observerFates[token.id] != nil {
            return
        }
        await withCheckedContinuation { c in
            observerParkAcks[token.id, default: []].append(c)
        }
    }

    private func flushObserverParkAcks(id: UInt64) {
        guard let cs = observerParkAcks.removeValue(forKey: id) else { return }
        for c in cs { c.resume() }
    }

    // MARK: Waiter (open-side)

    /// Reserve a waiter slot.
    ///
    /// H2: a closed gate must not emit entry signals. Otherwise
    /// post-close `registerWaiter` would keep incrementing
    /// `pendingEntrySignals` forever with no consumer.
    func registerWaiter() -> WaiterToken {
        if !closed { signalEntryLocked() }
        let id = nextID; nextID &+= 1
        if closed {
            completedWaiters.insert(id)
            sealWaiterFate(id: id, reason: .closedBeforePark)
        } else if opened {
            completedWaiters.insert(id)
            sealWaiterFate(id: id, reason: .openedBeforePark)
        } else {
            waiterOrder.append(id)
        }
        return WaiterToken(id: id)
    }

    /// Await the previously registered waiter. Mirrors `awaitObserver`'s
    /// H1 handling: duplicate await, unknown token, close-since-register,
    /// and normal park are each deterministic and non-hanging.
    func awaitWaiter(_ token: WaiterToken) async {
        await withTaskCancellationHandler {
            await withCheckedContinuation { c in
                if waiterFates[token.id] != nil {
                    if completedWaiters.remove(token.id) == nil {
                        waiterDuplicateAwaitCount += 1
                    }
                    c.resume()
                    return
                }
                if parkedWaiters[token.id] != nil {
                    waiterDuplicateAwaitCount += 1
                    c.resume()
                    return
                }
                if !waiterOrder.contains(token.id) {
                    waiterUnknownAwaitCount += 1
                    c.resume()
                    return
                }
                if closed {
                    waiterOrder.removeAll { $0 == token.id }
                    sealWaiterFate(id: token.id, reason: .closedBeforePark)
                    c.resume()
                    return
                }
                if opened {
                    // Registered before open() ran, but by the time
                    // await got here open() drained the id via the
                    // completedWaiters path — the fate check above
                    // already handles that. This branch handles a race
                    // where the caller registered, then somebody flipped
                    // `opened`, and neither open() nor the fate path
                    // sealed us yet: seal now.
                    waiterOrder.removeAll { $0 == token.id }
                    sealWaiterFate(id: token.id, reason: .openedBeforePark)
                    c.resume()
                    return
                }
                parkedWaiters[token.id] = c
                flushWaiterParkAcks(id: token.id)
            }
        } onCancel: {
            Task { await self.cancelWaiter(id: token.id) }
        }
    }

    /// Late-cancel-safe waiter variant. Mutates state ONLY when a
    /// continuation is currently parked. Bounded no-op otherwise.
    private func cancelWaiter(id: UInt64) {
        guard let c = parkedWaiters.removeValue(forKey: id) else {
            waiterCancelIgnoredCount += 1
            return
        }
        waiterOrder.removeAll { $0 == id }
        sealWaiterFate(id: id, reason: .cancelledWhileParked)
        c.resume()
    }

    /// Convenience: single-shot waiter registration + await.
    func wait() async {
        let token = registerWaiter()
        await awaitWaiter(token)
    }

    /// H3 park-ACK for waiter tokens.
    func waitForWaiterParked(_ token: WaiterToken) async {
        if parkedWaiters[token.id] != nil || waiterFates[token.id] != nil {
            return
        }
        await withCheckedContinuation { c in
            waiterParkAcks[token.id, default: []].append(c)
        }
    }

    private func flushWaiterParkAcks(id: UInt64) {
        guard let cs = waiterParkAcks.removeValue(forKey: id) else { return }
        for c in cs { c.resume() }
    }

    // MARK: Terminal transitions

    /// Open: drain all pending waiters exactly once. Idempotent.
    func open() {
        opened = true
        let ids = waiterOrder
        waiterOrder.removeAll()
        for id in ids {
            if let c = parkedWaiters.removeValue(forKey: id) {
                sealWaiterFate(id: id, reason: .openedWhileParked)
                c.resume()
            } else {
                completedWaiters.insert(id)
                sealWaiterFate(id: id, reason: .openedBeforePark)
            }
        }
    }

    /// Terminal teardown.
    ///
    /// H2: also clears `pendingEntrySignals` and drains any stranded
    /// park-ACK waiters so a helper regression can't hang the test.
    /// H1: seals fates for every drained token so subsequent
    /// register/await against those ids is deterministic.
    func close() {
        closed = true
        pendingEntrySignals = 0
        let wIds = waiterOrder
        waiterOrder.removeAll()
        for id in wIds {
            if let c = parkedWaiters.removeValue(forKey: id) {
                sealWaiterFate(id: id, reason: .closedWhileParked)
                c.resume()
            } else {
                completedWaiters.insert(id)
                sealWaiterFate(id: id, reason: .closedBeforePark)
            }
        }
        let oIds = observerOrder
        observerOrder.removeAll()
        for id in oIds {
            if let c = parkedObservers.removeValue(forKey: id) {
                sealObserverFate(id: id, reason: .closedWhileParked)
                c.resume()
            } else {
                completedObservers.insert(id)
                sealObserverFate(id: id, reason: .closedBeforePark)
            }
        }
        // Drain stranded park-ACK waiters — nothing will ever park now.
        for (_, cs) in observerParkAcks { for c in cs { c.resume() } }
        observerParkAcks.removeAll()
        for (_, cs) in waiterParkAcks { for c in cs { c.resume() } }
        waiterParkAcks.removeAll()
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
            waiterUnknownAwaitCount: waiterUnknownAwaitCount
        )
    }

    // MARK: Private helpers

    private func signalEntryLocked() {
        if let id = observerOrder.first {
            observerOrder.removeFirst()
            if let c = parkedObservers.removeValue(forKey: id) {
                sealObserverFate(id: id, reason: .signaledWhileParked)
                c.resume()
            } else {
                completedObservers.insert(id)
                sealObserverFate(id: id, reason: .signaledBeforePark)
            }
        } else {
            pendingEntrySignals += 1
        }
    }

    private func sealObserverFate(id: UInt64, reason: ResumeReason) {
        guard observerFates[id] == nil else { return }  // exactly-once
        observerFates[id] = reason
        observerFateCounts[reason, default: 0] += 1
        // A fate seals the outcome — flush any park-ACK waiters that
        // were waiting to observe this token park; a fate reaches them
        // as "you'll never see a park" so they can proceed instead of
        // hanging.
        flushObserverParkAcks(id: id)
    }

    private func sealWaiterFate(id: UInt64, reason: ResumeReason) {
        guard waiterFates[id] == nil else { return }
        waiterFates[id] = reason
        waiterFateCounts[reason, default: 0] += 1
        flushWaiterParkAcks(id: id)
    }
}
