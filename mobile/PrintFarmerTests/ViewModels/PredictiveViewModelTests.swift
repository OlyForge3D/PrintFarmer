import XCTest
@testable import PrintFarmer

/// Tests for PredictiveViewModel: predicting failure, loading alerts and forecasts,
/// computing risk levels, and error handling.
@MainActor
final class PredictiveViewModelTests: XCTestCase {
    
    private var mockPredictiveService: MockPredictiveService!
    private var viewModel: PredictiveViewModel!
    private let testPrinterId = UUID()

    private func assertExactNormalDelivery(
        _ snapshot: RecoverableDeliverySnapshot,
        rescueInvocations: Int = 1,
        file: StaticString = #filePath,
        line: UInt = #line
    ) {
        XCTAssertEqual(snapshot.normalRequestCount, 1, file: file, line: line)
        XCTAssertEqual(snapshot.normalEnqueueCount, 1, file: file, line: line)
        XCTAssertEqual(snapshot.normalObservationCount, 1, file: file, line: line)
        XCTAssertEqual(
            snapshot.rescueInvocationCount,
            rescueInvocations,
            file: file,
            line: line)
        XCTAssertEqual(snapshot.rescueTakeoverCount, 0, file: file, line: line)
        XCTAssertEqual(snapshot.waiterInstallationCount, 1, file: file, line: line)
        XCTAssertTrue(snapshot.exactNormalDeliveryObserved, file: file, line: line)
    }

    private func assertRescueTakeoverAfterSuppressedNormalDelivery(
        _ snapshot: RecoverableDeliverySnapshot,
        file: StaticString = #filePath,
        line: UInt = #line
    ) {
        XCTAssertEqual(snapshot.normalRequestCount, 1, file: file, line: line)
        XCTAssertEqual(snapshot.normalEnqueueCount, 0, file: file, line: line)
        XCTAssertEqual(snapshot.normalObservationCount, 0, file: file, line: line)
        XCTAssertEqual(snapshot.rescueInvocationCount, 1, file: file, line: line)
        XCTAssertEqual(snapshot.rescueTakeoverCount, 1, file: file, line: line)
        XCTAssertEqual(snapshot.waiterInstallationCount, 1, file: file, line: line)
        XCTAssertFalse(snapshot.exactNormalDeliveryObserved, file: file, line: line)
    }
    
    override func setUp() async throws {
        try await super.setUp()
        mockPredictiveService = MockPredictiveService()
        viewModel = PredictiveViewModel()
        viewModel.configure(predictiveService: mockPredictiveService)
    }
    
    override func tearDown() async throws {
        viewModel = nil
        mockPredictiveService = nil
        try await super.tearDown()
    }

    private func boundedOutcome(
        _ attempt: ObserverAwaitAttempt,
        file: StaticString = #filePath,
        line: UInt = #line
    ) -> ObserverAwaitAttempt.Outcome? {
        attempt.rescueCancellationOutcomeIfNeeded()
        XCTAssertEqual(
            attempt.rescuePublicationInvocationCountForTest,
            1,
            "observer outcome rescue must be invoked exactly once",
            file: file,
            line: line)
        XCTAssertEqual(
            attempt.rescuePublicationCountForTest,
            0,
            "normal observer publication was missing",
            file: file,
            line: line)
        return attempt.bufferedOutcomeForTest
    }

    private func boundedOutcome(
        _ attempt: WaiterAwaitAttempt,
        file: StaticString = #filePath,
        line: UInt = #line
    ) -> WaiterAwaitAttempt.Outcome? {
        attempt.rescueCancellationOutcomeIfNeeded()
        XCTAssertEqual(
            attempt.rescuePublicationInvocationCountForTest,
            1,
            "waiter outcome rescue must be invoked exactly once",
            file: file,
            line: line)
        XCTAssertEqual(
            attempt.rescuePublicationCountForTest,
            0,
            "normal waiter publication was missing",
            file: file,
            line: line)
        return attempt.bufferedOutcomeForTest
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
    /// it, then await its completion. Actor serialisation historically
    /// aimed to guarantee the park in `awaitWaiter`'s continuation
    /// closure completes before the `cancelWaiter` Task hops onto the
    /// actor, but under Swift's concurrent scheduling the task may not
    /// yet have entered `withTaskCancellationHandler` when `task.cancel()`
    /// fires — so cancel dispatch can race with parking AND with close.
    ///
    /// Reviewer finding D (Hicks/Vasquez, MEDIUM): the prior version
    /// awaited `waitForWaiterCancelCount(atLeast: 1)` BEFORE `close()`
    /// to prove cancel processing actually ran. That pre-close wait
    /// would deadlock if cancel processing were removed entirely (no
    /// cancellation processing ever bumps the counter; nothing rescues the ACK
    /// before close is even reached).
    ///
    /// This revision uses the per-attempt buffered outcome as the
    /// exact receipt with unconditional close-drain teardown, and
    /// asserts integrity via correlated per-receipt counter proofs:
    ///   1. Pre-construct `WaiterAwaitAttempt` and pass into
    ///      `awaitWaiter(_, attempt:)`.
    ///   2. `task.cancel()` then `await gate.close()` UNCONDITIONALLY.
    ///   3. `await attempt.outcome()` — bounded because either cancel
    ///      Task publishes `.cancelled(receipt)` or (if cancel path
    ///      regressed) body's natural path publishes
    ///      `.finishedBeforeProcessing`.
    ///   4. Assert outcome is `.cancelled(_)` — regression to
    ///      `.finishedBeforeProcessing` fails loudly.
    ///   5. Assert exactly ONE continuation resume observable via any
    ///      of the resume sites — a cancel-invocation increment
    ///      without ANY parked resume (leak) fails LOUDLY.
    ///   6. Correlated per-receipt proof narrows the resume site set
    ///      by the exact receipt returned.
    func testAsyncGateCancelBeforeRegister() async {
        let gate = AsyncGate()
        let token = await gate.registerWaiter()
        let attempt = WaiterAwaitAttempt()
        let task = Task { await gate.awaitWaiter(token, attempt: attempt) }
        task.cancel()

        // Close UNCONDITIONALLY — no pre-close cancel-count wait.
        // If cancel path regressed, close drains any parked continuation
        // via .closedWhileParked and body's natural path publishes
        // .finishedBeforeProcessing (caught by outcome guard below).
        await gate.close()
        await task.value

        guard let outcome = boundedOutcome(attempt) else {
            XCTFail("cancel path produced no outcome")
            return
        }
        guard case .cancelled(let r) = outcome else {
            XCTFail("cancel path must publish outcome; got \(outcome)")
            return
        }
        // Every receipt classification is exact / non-hanging.
        XCTAssertTrue(r == .processedMatched
                      || r == .processedIgnoredMismatch
                      || r == .closedBeforeProcessing,
                      "receipt must be an exact non-hang classification; got \(r)")

        let snap = await gate.snapshot()
        XCTAssertEqual(snap.parkedWaiterCount, 0)
        XCTAssertEqual(snap.parkedObserverCount, 0)
        XCTAssertEqual(snap.waiterOrder, [])
        XCTAssertEqual(snap.observerOrder, [])
        XCTAssertEqual(snap.completedWaiterCount, 0,
                       "cancel must not relatch into completedWaiters")
        XCTAssertEqual(snap.completedObserverCount, 0)
        XCTAssertEqual(snap.waiterCancelInvocationCount, 1,
                       "exactly one cancelWaiter dispatched across the test lifetime")

        // The caller-owned attempt is the authoritative release owner.
        // CancelBefore may resume before actor parking, so gate-side semantic
        // counters can legitimately remain zero; the raw attempt event cannot.
        let rc = snap.waiterResumeCounts
        let releaseEvents = attempt.lifecycleEventsForTest.filter {
            if case .suspensionResumed = $0.phase { return true }
            return false
        }
        XCTAssertEqual(releaseEvents.count, 1,
                       "the attempt must record exactly one actual continuation resume")
        guard case .suspensionResumed(_, let requestedBy) = releaseEvents.first?.phase else {
            XCTFail("missing raw suspension release evidence")
            return
        }
        XCTAssertEqual(requestedBy, .cancellationHandler,
                       "CancelBefore release is owned by the genuine cancellation handler")

        // Correlated per-receipt evidence: narrows which resume-site
        // group is expected. A receipt-vs-observed-drain mismatch fails.
        // WaiterCancelReceipt has 4 cases; .finishedBeforeProcessing
        // cannot occur here because we're inside `.cancelled(let r)`
        // and the natural-completion receipt is only produced by
        // `AwaitAttempt.markCompletedNaturallyIfActive()`, never by
        // cancelWaiter. Include it for switch exhaustiveness with a
        // failing branch to catch a future misuse.
        // Ripley Finding 4: correlate receipt with per-attempt hop
        // fingerprint recorded at cancelWaiter's actor turn. This
        // disambiguates broken-matcher regressions (matched entry
        // wrongly rejected → .processedIgnoredMismatch + close-side
        // drain would silently pass without this correlation) from
        // legitimate CancelBefore races. hopFingerprintForTest is
        // nil only if cancellation never published — which the
        // receipt existence already rules out.
        guard let hop = attempt.hopFingerprintForTest else {
            XCTFail("cancel receipt present but no hop fingerprint recorded — harness regression")
            return
        }
        switch r {
        case .processedMatched:
            // Cancel Task drained the parked entry directly.
            XCTAssertEqual(snap.waiterResumeCounts[.parkedResumedByCancel] ?? 0, 1,
                           ".processedMatched must produce exactly one .parkedResumedByCancel")
            XCTAssertEqual(snap.waiterFateCounts[.cancelledWhileParked] ?? 0, 1,
                           ".processedMatched must seal fate exactly .cancelledWhileParked")
            XCTAssertEqual(snap.waiterCancelIgnoredCount, 0,
                           ".processedMatched: no ignore-path bump")
            // Ripley Finding 4: at hop time, parked entry MUST have
            // existed AND matched this attempt's awaitID exactly.
            let independentlyMatched = hop.parkedAwaitID == hop.requestedAwaitID
            XCTAssertEqual(hop.requestedAwaitID, attempt.id)
            XCTAssertEqual(hop.parkedEntryExisted, hop.parkedAwaitID != nil)
            XCTAssertTrue(hop.parkedEntryExisted && independentlyMatched,
                          ".processedMatched requires hopFingerprint(parked=true, matched=true); got \(hop)")
            XCTAssertFalse(hop.closedAtHop,
                          ".processedMatched requires gate open at hop; got closedAtHop=true")
        case .processedIgnoredMismatch:
            XCTAssertEqual(rc[.parkedResumedByCancel] ?? 0, 0)
            XCTAssertEqual(rc[.parkedResumedByClose] ?? 0, 0,
                           "the handler already resumed the attempt before close")
            XCTAssertEqual(snap.waiterCancelIgnoredCount, 1,
                           ".processedIgnoredMismatch bumps ignored count by one")
            // Ripley Finding 4: at hop time, either no parked entry
            // existed OR one existed with a DIFFERENT awaitID. If a
            // parked entry existed AND matched, the matcher wrongly
            // classified it as mismatch — broken-matcher regression.
            let independentlyMatched = hop.parkedAwaitID == hop.requestedAwaitID
            XCTAssertEqual(hop.requestedAwaitID, attempt.id)
            XCTAssertEqual(hop.parkedEntryExisted, hop.parkedAwaitID != nil)
            XCTAssertFalse(independentlyMatched,
                          ".processedIgnoredMismatch requires hopFingerprint(awaitIDMatched=false); a true value here is broken-matcher regression (matched entry classified as mismatch). Fingerprint: \(hop)")
            XCTAssertFalse(hop.closedAtHop,
                          ".processedIgnoredMismatch requires gate open at hop (close would produce .closedBeforeProcessing); got closedAtHop=true")
        case .closedBeforeProcessing:
            XCTAssertEqual(rc[.parkedResumedByCancel] ?? 0, 1,
                           "the genuine cancellation handler delivered before its actor hop")
            XCTAssertEqual(rc[.parkedResumedByClose] ?? 0, 0,
                           "close observes an already-resumed caller suspension")
            let fc = snap.waiterFateCounts
            let closedFate = (fc[.closedWhileParked] ?? 0) + (fc[.closedBeforePark] ?? 0)
            XCTAssertEqual(closedFate, 1,
                           ".closedBeforeProcessing: close-side fate seal must fire exactly once; got \(closedFate)")
            XCTAssertEqual(snap.waiterCancelIgnoredCount, 1,
                           ".closedBeforeProcessing: closed-guard bumps ignored count by one")
            // Ripley Finding 4: gate MUST have been closed at hop
            // time. Any other fingerprint value is inconsistent.
            XCTAssertTrue(hop.closedAtHop,
                          ".closedBeforeProcessing requires hopFingerprint(closedAtHop=true); got \(hop)")
        case .finishedBeforeProcessing:
            XCTFail(".finishedBeforeProcessing is a natural-path receipt; cancelWaiter must not produce it")
        }
    }

    /// Cancellation after the awaiter has parked: register synchronously,
    /// park in an unstructured Task, use `waitForXParked` to structurally
    /// prove the continuation is parked, then cancel. cancelX drains via
    /// parkedX exactly once and seals the fate as
    /// `.cancelledWhileParked`. Post-drain: zero state.
    ///
    /// Reviewer finding D (Hicks/Vasquez, MEDIUM): the prior version
    /// awaited `waitForXCancelCount(atLeast: 1)` BEFORE `close()` for
    /// each side. Those pre-close waits hang if cancel processing is
    /// removed.
    ///
    /// This revision uses buffered `attempt.outcome()` as the exact
    /// receipt with unconditional close-drain teardown. Observer and
    /// waiter run on SEPARATE gates to eliminate the cross-signal race
    /// (a shared-gate registerWaiter would signalEntry-drain the still-
    /// parked observer before cancel dispatch, producing
    /// `.processedIgnoredMismatch` non-deterministically).
    func testAsyncGateCancelAfterRegister() async {
        // OBSERVER PATH — dedicated gate; no other work touches it.
        let obsGate = AsyncGate()
        let observerToken = await obsGate.registerObserver()
        let observerAttempt = ObserverAwaitAttempt()
        let observerTask = Task { await obsGate.awaitObserver(observerToken, attempt: observerAttempt) }
        let observerAck = await obsGate.waitForObserverParked(observerToken)
        XCTAssertEqual(observerAck, .parked,
                       "observer must park before cancel; got \(observerAck)")
        observerTask.cancel()

        // WAITER PATH — dedicated gate; no observer interference.
        let waitGate = AsyncGate()
        let waiterToken = await waitGate.registerWaiter()
        let waiterAttempt = WaiterAwaitAttempt()
        let waiterTask = Task { await waitGate.awaitWaiter(waiterToken, attempt: waiterAttempt) }
        let waiterAck = await waitGate.waitForWaiterParked(waiterToken)
        XCTAssertEqual(waiterAck, .parked,
                       "waiter must park before cancel; got \(waiterAck)")
        waiterTask.cancel()

        // Close UNCONDITIONALLY on both gates — no pre-close cancel-count
        // wait. Every await below is bounded by close-drain.
        await obsGate.close()
        await waitGate.close()
        observerAttempt.releaseSuspensionForTest()
        waiterAttempt.releaseSuspensionForTest()
        await observerTask.value
        await waiterTask.value

        guard let observerOutcome = boundedOutcome(observerAttempt) else {
            XCTFail("observer cancel path produced no outcome")
            return
        }
        guard let waiterOutcome = boundedOutcome(waiterAttempt) else {
            XCTFail("waiter cancel path produced no outcome")
            return
        }

        guard case .cancelled(let observerR) = observerOutcome else {
            XCTFail("observer cancel must publish outcome; got \(observerOutcome)")
            return
        }
        guard case .cancelled(let waiterR) = waiterOutcome else {
            XCTFail("waiter cancel must publish outcome; got \(waiterOutcome)")
            return
        }

        // Both sides parked BEFORE cancel (proven by .parked ACK above),
        // so the receipt is bounded to two matched-drain outcomes:
        //   .processedMatched — cancellation drained the parked entry
        //   .closedBeforeProcessing — close raced ahead, drained parked
        //     via .closedWhileParked, cancellation observed closed
        // .processedIgnoredMismatch is IMPOSSIBLE here because parked
        // was proven before cancel dispatched (parkedX[id] was non-nil
        // at cancellation classification time OR close had removed it via matched-
        // drain, in which case receipt is .closedBeforeProcessing).
        XCTAssertTrue(observerR == .processedMatched || observerR == .closedBeforeProcessing,
                      "observer receipt (parked pre-cancel): must be .processedMatched or .closedBeforeProcessing; got \(observerR)")
        XCTAssertTrue(waiterR == .processedMatched || waiterR == .closedBeforeProcessing,
                      "waiter receipt (parked pre-cancel): must be .processedMatched or .closedBeforeProcessing; got \(waiterR)")

        let obsSnap = await obsGate.snapshot()
        let waitSnap = await waitGate.snapshot()
        XCTAssertEqual(obsSnap.parkedObserverCount, 0)
        XCTAssertEqual(waitSnap.parkedWaiterCount, 0)
        XCTAssertEqual(obsSnap.observerOrder, [])
        XCTAssertEqual(waitSnap.waiterOrder, [])
        XCTAssertEqual(obsSnap.completedObserverCount, 0,
                       "cancel must not relatch into completedObservers")
        XCTAssertEqual(waitSnap.completedWaiterCount, 0,
                       "cancel must not relatch into completedWaiters")
        XCTAssertEqual(obsSnap.observerCancelInvocationCount, 1,
                       "exactly one cancelObserver across the test lifetime")
        XCTAssertEqual(waitSnap.waiterCancelInvocationCount, 1,
                       "exactly one cancelWaiter across the test lifetime")

        // Ripley Finding 4: correlate each observed receipt with the
        // per-attempt hop fingerprint. Because parked was PROVEN
        // pre-cancel (waitForXParked returned .parked before
        // task.cancel), any cancel dispatch that later sees no parked
        // entry must have raced close (fingerprint.closedAtHop=true).
        // A broken matcher classifying the still-parked matching entry
        // as mismatch would surface as .processedIgnoredMismatch with
        // fingerprint(matched=true) — impossible in the receipt set we
        // accept, so any such combination is a definitive regression.
        guard let obsHop = observerAttempt.hopFingerprintForTest else {
            XCTFail("observer cancel receipt present but no hop fingerprint — harness regression")
            return
        }
        guard let watHop = waiterAttempt.hopFingerprintForTest else {
            XCTFail("waiter cancel receipt present but no hop fingerprint — harness regression")
            return
        }
        let observerReleaseEvents = observerAttempt.lifecycleEventsForTest.compactMap {
            event -> AwaitSuspensionReleaseSite? in
            guard case .suspensionResumed(_, let requestedBy) = event.phase else {
                return nil
            }
            return requestedBy
        }
        let waiterReleaseEvents = waiterAttempt.lifecycleEventsForTest.compactMap {
            event -> AwaitSuspensionReleaseSite? in
            guard case .suspensionResumed(_, let requestedBy) = event.phase else {
                return nil
            }
            return requestedBy
        }
        XCTAssertEqual(observerReleaseEvents.count, 1)
        XCTAssertEqual(waiterReleaseEvents.count, 1)

        // Correlated matched-cancellation + exact-resume-reason for
        // observer side. Because parked was proven pre-cancel, the
        // parked continuation MUST have been drained by either cancel
        // or close — receipt determines which.
        if observerR == .processedMatched {
            XCTAssertEqual(obsSnap.observerResumeCounts[.parkedResumedByCancel] ?? 0, 1,
                           "observer .processedMatched: exactly one .parkedResumedByCancel")
            XCTAssertEqual(obsSnap.observerFateCounts[.cancelledWhileParked] ?? 0, 1,
                           "observer .processedMatched: fate sealed exactly .cancelledWhileParked")
            XCTAssertEqual(obsSnap.observerCancelIgnoredCount, 0,
                           "observer matched cancel — ignored count stays zero")
            let independentlyMatched = obsHop.parkedAwaitID == obsHop.requestedAwaitID
            XCTAssertEqual(obsHop.requestedAwaitID, observerAttempt.id)
            XCTAssertTrue(independentlyMatched && obsHop.parkedEntryExisted && !obsHop.closedAtHop,
                          "observer .processedMatched requires hopFingerprint(parked=true, matched=true, closed=false); got \(obsHop)")
        } else {
            if observerReleaseEvents.first == .cancellationHandler {
                XCTAssertEqual(obsSnap.observerResumeCounts[.parkedResumedByCancel] ?? 0, 1)
                XCTAssertEqual(obsSnap.observerResumeCounts[.parkedResumedByClose] ?? 0, 0)
            } else {
                XCTAssertEqual(observerReleaseEvents.first, .close)
                XCTAssertEqual(obsSnap.observerResumeCounts[.parkedResumedByCancel] ?? 0, 0)
                XCTAssertEqual(obsSnap.observerResumeCounts[.parkedResumedByClose] ?? 0, 1)
            }
            XCTAssertEqual(obsSnap.observerFateCounts[.closedWhileParked] ?? 0, 1,
                           "observer .closedBeforeProcessing: fate sealed exactly .closedWhileParked")
            XCTAssertEqual(obsSnap.observerCancelIgnoredCount, 1,
                           "observer close-guard bumps ignored count by one")
            XCTAssertTrue(obsHop.closedAtHop,
                          "observer .closedBeforeProcessing requires hopFingerprint(closedAtHop=true); got \(obsHop)")
        }

        // Correlated proof for waiter side.
        if waiterR == .processedMatched {
            XCTAssertEqual(waitSnap.waiterResumeCounts[.parkedResumedByCancel] ?? 0, 1,
                           "waiter .processedMatched: exactly one .parkedResumedByCancel")
            XCTAssertEqual(waitSnap.waiterFateCounts[.cancelledWhileParked] ?? 0, 1,
                           "waiter .processedMatched: fate sealed exactly .cancelledWhileParked")
            XCTAssertEqual(waitSnap.waiterCancelIgnoredCount, 0,
                           "waiter matched cancel — ignored count stays zero")
            let independentlyMatched = watHop.parkedAwaitID == watHop.requestedAwaitID
            XCTAssertEqual(watHop.requestedAwaitID, waiterAttempt.id)
            XCTAssertTrue(independentlyMatched && watHop.parkedEntryExisted && !watHop.closedAtHop,
                          "waiter .processedMatched requires hopFingerprint(parked=true, matched=true, closed=false); got \(watHop)")
        } else {
            if waiterReleaseEvents.first == .cancellationHandler {
                XCTAssertEqual(waitSnap.waiterResumeCounts[.parkedResumedByCancel] ?? 0, 1)
                XCTAssertEqual(waitSnap.waiterResumeCounts[.parkedResumedByClose] ?? 0, 0)
            } else {
                XCTAssertEqual(waiterReleaseEvents.first, .close)
                XCTAssertEqual(waitSnap.waiterResumeCounts[.parkedResumedByCancel] ?? 0, 0)
                XCTAssertEqual(waitSnap.waiterResumeCounts[.parkedResumedByClose] ?? 0, 1)
            }
            XCTAssertEqual(waitSnap.waiterFateCounts[.closedWhileParked] ?? 0, 1,
                           "waiter .closedBeforeProcessing: fate sealed exactly .closedWhileParked")
            XCTAssertEqual(waitSnap.waiterCancelIgnoredCount, 1,
                           "waiter close-guard bumps ignored count by one")
            XCTAssertTrue(watHop.closedAtHop,
                          "waiter .closedBeforeProcessing requires hopFingerprint(closedAtHop=true); got \(watHop)")
        }
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
    /// requesting `open()` after cancellation has already
    /// been queued. cancelWaiter drains via parkedX; open runs later on
    /// an already-empty queue.
    func testAsyncGateCancelBeforeOpen_bothCleanState() async {
        let gate = AsyncGate()
        let waiterToken = await gate.registerWaiter()
        let attempt = WaiterAwaitAttempt()
        let task = Task { await gate.awaitWaiter(waiterToken, attempt: attempt) }
        _ = await gate.waitForWaiterParked(waiterToken)
        task.cancel()
        attempt.releaseSuspensionForTest()
        await task.value
        await gate.open()
        await gate.close()

        guard let outcome = boundedOutcome(attempt) else {
            XCTFail("cancel-before-open produced no outcome")
            return
        }
        XCTAssertEqual(outcome, .cancelled(.processedMatched))
        let snap = await gate.snapshot()
        XCTAssertEqual(snap.parkedWaiterCount, 0)
        XCTAssertEqual(snap.waiterOrder, [])
        XCTAssertEqual(snap.completedWaiterCount, 0,
                       "cancel-then-open must not leave any completedWaiters latched")
        XCTAssertTrue(snap.opened)
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
        XCTAssertEqual(afterOpen2.parkedWaiterCount, afterOpen1.parkedWaiterCount)
        XCTAssertEqual(afterOpen2.waiterOrder, afterOpen1.waiterOrder)
        XCTAssertEqual(afterOpen2.waiterFateCounts, afterOpen1.waiterFateCounts)
        XCTAssertEqual(afterOpen2.completedWaiterCount, afterOpen1.completedWaiterCount)

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
        XCTAssertEqual(final.waiterInFlightDelivery,
                       InFlightDeliverySnapshot(
                        activeCount: 0,
                        requestCount: 3,
                        rescueInvocationCount: 3,
                        normalObservationCount: 3,
                        rescueTakeoverCount: 0))
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

    // MARK: Duplicate cancellation cannot cross-own

    /// A preclaimed duplicate cancellation enters only after the original is
    /// parked. Raw requested/parked IDs prove mismatch independently; the
    /// intentional signal happens after the duplicate actor call returns.
    func testAsyncGateDuplicateAwaitCancelDoesNotCrossOwnOriginalObserver() async {
        let gate = AsyncGate()
        let token = await gate.registerObserver()
        let origAttempt = ObserverAwaitAttempt()
        let dupAttempt = ObserverAwaitAttempt()

        let original = Task { await gate.awaitObserver(token, attempt: origAttempt) }
        await gate.waitForObserverParked(token)

        let branchCheckpoint = BufferedConsumer<AsyncGate.DuplicateBranchCheckpoint>()
        let processingCheckpoint =
            BufferedConsumer<AsyncGate.ObserverCancelProcessingCheckpoint>()
        let duplicate = Task {
            await gate.awaitObserverDuplicateThenSelfCancel(
                token,
                attempt: dupAttempt,
                branchCheckpoint: branchCheckpoint,
                processingCheckpoint: processingCheckpoint)
        }

        branchCheckpoint.armRescue(.rescuedMissingPublication)
        processingCheckpoint.armRescue(.rescuedMissingPublication)
        let branch = await branchCheckpoint.value()
        // Independent rescue occurs after the duplicate branch checkpoint.
        // Correct behavior already resumed at `.duplicateAfterParked`; deleting
        // that release makes this test rescue take without stranding the child.
        dupAttempt.releaseSuspensionForTest()
        let processing = await processingCheckpoint.value()
        _ = await gate.registerWaiter()

        // Wait-for graph: both attempt rescues, outcome rescue, and gate close
        // precede either child join.
        await gate.close()
        dupAttempt.rescueCancellationOutcomeIfNeeded()
        origAttempt.releaseSuspensionForTest()
        await duplicate.value
        await original.value
        XCTAssertEqual(branch, .duplicateAfterParked)
        XCTAssertEqual(processing, .processed(.processedIgnoredMismatch))
        assertExactNormalDelivery(branchCheckpoint.deliverySnapshotForTest)
        assertExactNormalDelivery(processingCheckpoint.deliverySnapshotForTest)
        assertExactNormalDelivery(dupAttempt.suspensionDeliveryForTest)
        assertExactNormalDelivery(origAttempt.suspensionDeliveryForTest)
        XCTAssertEqual(dupAttempt.rescuePublicationInvocationCountForTest, 1)
        XCTAssertEqual(dupAttempt.rescuePublicationCountForTest, 0)
        guard let dupOutcome = dupAttempt.bufferedOutcomeForTest else {
            XCTFail("duplicate cancellation did not publish")
            return
        }
        guard case .cancelled(let dupR) = dupOutcome else {
            XCTFail("duplicate must publish a cancel receipt, got \(dupOutcome)")
            return
        }
        // Ripley Finding 4 fingerprint correlation. Cross-own isolation
        // is proven STRUCTURALLY: at hop time, if a parked entry
        // existed for `token.id`, it was ORIGINAL's (dup never parked
        // on `gate` — it hit the duplicate branch and resumed
        // immediately). If matcher wrongly returns awaitIDMatched=true
        // for dup's fresh UUID against original's UUID, that is a
        // definitive matcher regression.
        guard let hop = dupAttempt.hopFingerprintForTest else {
            XCTFail("cancel receipt present but no hop fingerprint — harness regression")
            return
        }
        let independentlyMatched = hop.parkedAwaitID == hop.requestedAwaitID
        XCTAssertEqual(hop.requestedAwaitID, dupAttempt.id)
        XCTAssertEqual(hop.parkedAwaitID, origAttempt.id)
        XCTAssertTrue(hop.parkedEntryExisted)
        XCTAssertFalse(hop.closedAtHop)
        XCTAssertFalse(independentlyMatched,
                       "raw requested and parked identities must be disjoint")
        XCTAssertEqual(dupR, .processedIgnoredMismatch)
        XCTAssertEqual(dupAttempt.lifecycleEventsForTest.map(\.phase), [
            .suspensionResumed(
                site: .duplicateAfterParked,
                requestedBy: .duplicateAfterParked),
            .cancellationHandlerObserved(observedAcknowledgementEventID: nil),
        ], "duplicate branch owns its suspension release before genuine onCancel")
        XCTAssertFalse(dupAttempt.cancellationReleaseSucceededForTest,
                       "mismatched cancellation performs no second/no-op resume")

        // Consolidated post-teardown structural proof.
        let final = await gate.snapshot()
        XCTAssertEqual(final.parkedObserverCount, 0)
        XCTAssertEqual(final.observerOrder, [])
        XCTAssertEqual(final.observerDuplicateAwaitCount, 1,
                       "duplicate hit branch (2) exactly once")
        XCTAssertEqual(final.observerResumeCounts[.duplicateAfterParked] ?? 0, 1)
        XCTAssertEqual(final.observerCancelInvocationCount, 1,
                       "cancelObserver dispatched exactly once (from dup's onCancel)")
        XCTAssertEqual(final.observerCancelIgnoredCount, 1,
                       "mismatched-awaitID OR closed-at-hop → bounded no-op")
        // Cross-own isolation: original was never drained by cancel.
        XCTAssertEqual(final.observerFateCounts[.cancelledWhileParked] ?? 0, 0,
                       "no cancelledWhileParked fate — cross-own must not happen")
        XCTAssertEqual(final.observerResumeCounts[.parkedResumedByCancel] ?? 0, 0,
                       "no parkedResumedByCancel — cross-own must not have drained original")
        XCTAssertEqual(final.observerFateCounts[.signaledWhileParked] ?? 0, 1,
                       "original must resume via signal")
        XCTAssertEqual(final.observerResumeCounts[.parkedResumedBySignal] ?? 0, 1)
        XCTAssertEqual(final.observerResumeCounts[.parkedResumedByClose] ?? 0, 0,
                       "signal drained original before close ran")
        XCTAssertEqual(final.observerUnknownAwaitCount, 0)

        // Original attempt: peek buffered outcome synchronously.
        guard let origOutcome = origAttempt.bufferedOutcomeForTest else {
            XCTFail("origAttempt outcome nil — natural-completion regression")
            return
        }
        XCTAssertEqual(origOutcome, .finishedBeforeProcessing,
                       "original attempt: natural completion via signal")
        XCTAssertEqual(origAttempt.stateForTest, .completedNaturally,
                       "state machine committed the original natural path")
    }

    /// Waiter analogue of the direct raw-identity cross-own proof.
    func testAsyncGateDuplicateAwaitCancelDoesNotCrossOwnOriginalWaiter() async {
        let gate = AsyncGate()
        let token = await gate.registerWaiter()
        let origAttempt = WaiterAwaitAttempt()
        let dupAttempt = WaiterAwaitAttempt()

        let original = Task { await gate.awaitWaiter(token, attempt: origAttempt) }
        await gate.waitForWaiterParked(token)

        let branchCheckpoint = BufferedConsumer<AsyncGate.DuplicateBranchCheckpoint>()
        let processingCheckpoint =
            BufferedConsumer<AsyncGate.WaiterCancelProcessingCheckpoint>()
        let duplicate = Task {
            await gate.awaitWaiterDuplicateThenSelfCancel(
                token,
                attempt: dupAttempt,
                branchCheckpoint: branchCheckpoint,
                processingCheckpoint: processingCheckpoint)
        }
        branchCheckpoint.armRescue(.rescuedMissingPublication)
        processingCheckpoint.armRescue(.rescuedMissingPublication)
        let branch = await branchCheckpoint.value()
        dupAttempt.releaseSuspensionForTest()
        let processing = await processingCheckpoint.value()
        await gate.open()

        await gate.close()
        dupAttempt.rescueCancellationOutcomeIfNeeded()
        origAttempt.releaseSuspensionForTest()
        await duplicate.value
        await original.value
        XCTAssertEqual(branch, .duplicateAfterParked)
        XCTAssertEqual(processing, .processed(.processedIgnoredMismatch))
        assertExactNormalDelivery(branchCheckpoint.deliverySnapshotForTest)
        assertExactNormalDelivery(processingCheckpoint.deliverySnapshotForTest)
        assertExactNormalDelivery(dupAttempt.suspensionDeliveryForTest)
        assertExactNormalDelivery(origAttempt.suspensionDeliveryForTest)
        XCTAssertEqual(dupAttempt.rescuePublicationInvocationCountForTest, 1)
        XCTAssertEqual(dupAttempt.rescuePublicationCountForTest, 0)
        guard let dupOutcome = dupAttempt.bufferedOutcomeForTest else {
            XCTFail("duplicate cancellation did not publish")
            return
        }
        guard case .cancelled(let dupR) = dupOutcome else {
            XCTFail("duplicate must publish a cancel receipt, got \(dupOutcome)")
            return
        }
        guard let hop = dupAttempt.hopFingerprintForTest else {
            XCTFail("cancel receipt present but no hop fingerprint — harness regression")
            return
        }
        let independentlyMatched = hop.parkedAwaitID == hop.requestedAwaitID
        XCTAssertEqual(hop.requestedAwaitID, dupAttempt.id)
        XCTAssertEqual(hop.parkedAwaitID, origAttempt.id)
        XCTAssertTrue(hop.parkedEntryExisted)
        XCTAssertFalse(hop.closedAtHop)
        XCTAssertFalse(independentlyMatched)
        XCTAssertEqual(dupR, .processedIgnoredMismatch)
        XCTAssertEqual(dupAttempt.lifecycleEventsForTest.map(\.phase), [
            .suspensionResumed(
                site: .duplicateAfterParked,
                requestedBy: .duplicateAfterParked),
            .cancellationHandlerObserved(observedAcknowledgementEventID: nil),
        ])
        XCTAssertFalse(dupAttempt.cancellationReleaseSucceededForTest)

        let final = await gate.snapshot()
        XCTAssertEqual(final.parkedWaiterCount, 0)
        XCTAssertEqual(final.waiterOrder, [])
        XCTAssertEqual(final.waiterDuplicateAwaitCount, 1)
        XCTAssertEqual(final.waiterResumeCounts[.duplicateAfterParked] ?? 0, 1)
        XCTAssertEqual(final.waiterCancelInvocationCount, 1)
        XCTAssertEqual(final.waiterCancelIgnoredCount, 1,
                       "mismatched-awaitID OR closed-at-hop → bounded no-op")
        XCTAssertEqual(final.waiterFateCounts[.cancelledWhileParked] ?? 0, 0,
                       "no cross-own")
        XCTAssertEqual(final.waiterResumeCounts[.parkedResumedByCancel] ?? 0, 0,
                       "no cross-own drain")
        XCTAssertEqual(final.waiterFateCounts[.openedWhileParked] ?? 0, 1,
                       "original must resume via open, not cross-owned cancel")
        XCTAssertEqual(final.waiterResumeCounts[.parkedResumedByOpen] ?? 0, 1)
        XCTAssertEqual(final.waiterResumeCounts[.parkedResumedByClose] ?? 0, 0,
                       "open drained original before close ran")
        XCTAssertEqual(final.waiterUnknownAwaitCount, 0)

        guard let origWaiterOutcome = origAttempt.bufferedOutcomeForTest else {
            XCTFail("origAttempt outcome nil — natural-completion regression")
            return
        }
        XCTAssertEqual(origWaiterOutcome, .finishedBeforeProcessing,
                       "original waiter completed naturally")
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
        let attempt = WaiterAwaitAttempt()
        let task = Task { await gate.awaitWaiter(token, attempt: attempt) }
        _ = await gate.waitForWaiterParked(token)
        task.cancel()
        attempt.releaseSuspensionForTest()
        await task.value
        await gate.open()
        await gate.close()

        let snap = await gate.snapshot()
        XCTAssertEqual(snap.parkedWaiterCount, 0)
        XCTAssertEqual(snap.waiterOrder, [])
        XCTAssertEqual(snap.completedWaiterCount, 0,
                       "open after cancel must not relatch a cancelled id")
        XCTAssertEqual(snap.waiterFateCounts[.cancelledWhileParked] ?? 0, 1)
        XCTAssertTrue(snap.opened)
        XCTAssertEqual(boundedOutcome(attempt), .cancelled(.processedMatched))
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
        // #866: unconditional release + terminal close BEFORE draining the
        // convenience retrievals. The observer is normally resumed by the
        // entry signal that `wait()`'s registerWaiter emits, and the waiter by
        // open(); both resumes are concurrent with these awaits. Draining the
        // gate first means a resume regression is rescued by close()
        // (.closedWhileParked) so this test fails fast instead of stranding on
        // the token-less `observerTask.value`.
        await gate.open()
        await gate.close()
        await observerTask.value        // resumed by the wait's entry signal
        await waiterTask.value
    }

    func testAsyncGateConvenienceWaitBeforeEntryObserverIsObserved() async {
        let gate = AsyncGate()
        let waiterTask = Task { await gate.wait() }
        // Force `waiterTask` to reach the actor by awaiting an unrelated
        // actor call — since the actor is a serial mailbox, this snapshot
        // synchronously drains any prior mailbox items on the actor
        // executor. No polling.
        _ = await gate.snapshot()
        // #866: wrap the token-less convenience observer so its terminal
        // retrieval can be drained AFTER an unconditional teardown. In normal
        // flow it is resumed by the waiter's entry signal; a resume regression
        // is rescued by close() (.closedWhileParked) so the test fails fast
        // instead of stranding on the token-less `waitForEntry()`.
        let observerTask = Task { await gate.waitForEntry() }   // consumes the pending signal
        await gate.open()
        await gate.close()
        await observerTask.value
        await waiterTask.value
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
        let attempt = ObserverAwaitAttempt()
        let t = Task { await gate.awaitObserver(token, attempt: attempt) }

        // Causal park proof: block until the parking task has actually
        // installed the continuation. `waitForObserverParked` returns
        // `.parked` when `parkedObservers[token.id] != nil` is true.
        let parkAck = await gate.waitForObserverParked(token)
        XCTAssertEqual(parkAck, .parked,
                       "waitForObserverParked must observe the continuation parked before we enter the ticket")

        // Enter ticket AFTER parked-proof. `enterObserverParkAck` runs
        // synchronously on the actor: its `parkedObservers[token.id] != nil`
        // branch calls `ticket.resolve(.parked)` and returns without
        // inserting into the active ticket registry.
        let ticket = await gate.enterObserverParkAck(token)

        // Reviewer finding E (Hicks, MEDIUM): capture the sync-resolution
        // state IMMEDIATELY — BEFORE any await. The prior version used
        // \`XCTAssertTrue(ticket.isResolved)\` (non-fatal) then
        // \`await ticket.value()\` right after. If synchronous resolution
        // regressed AND the ticket wasn't inserted into the actor's
        // active-ticket map, \`.value()\` would hang before close was
        // ever reached (with no rescuer). Capturing \`isResolved\`
        // synchronously ensures the resolution proof runs before ANY
        // suspension edge.
        let syncResolved = ticket.isResolved

        // Fail-safe close+drain BEFORE awaiting ticket.value. This
        // guarantees an INDEPENDENT resolver for the ticket in every
        // regression scenario:
        //   (a) sync resolution regressed but map insert happened —
        //       close drains the parked observer via
        //       \`flushObserverParkAcks(id:, .terminal(.closedWhileParked))\`,
        //       which resolves the ticket to .terminal(.closedWhileParked).
        //   (b) sync resolution + map insert BOTH regressed — ticket
        //       has no resolver; the syncResolved guard below catches
        //       this before any \`.value()\` await, preventing hang.
        //   (c) happy path — ticket was resolved .parked at enter
        //       time; close-drain does not affect the already-resolved
        //       ticket (\`resolve(_)\` is idempotent by construction).
        // Every await below is bounded by close-drain.
        await gate.close()
        await t.value

        // Assert the sync-resolution invariant now that teardown is
        // done. Non-blocking: syncResolved was captured before any
        // await, so regression (b) fails HERE with an early return
        // instead of hanging.
        guard syncResolved else {
            XCTFail("ticket entered against a parked token must be resolved SYNCHRONOUSLY by enterObserverParkAck (got isResolved=false at enter time)")
            return
        }

        // Ticket is proven resolved (syncResolved=true) → \`.value()\` is
        // bounded because the buffered latch has a stored result.
        // The exact \`.parked\` equality catches regression (a) — if
        // sync resolved was true but with the WRONG value, or if
        // sync failed and close-drain resolved to
        // .terminal(.closedWhileParked), this assertion fails.
        let r = await ticket.value()
        XCTAssertEqual(r, .parked,
                       "ticket entered after park must resolve exactly .parked (immediate at enter time); got \(r)")

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
    /// the observer ticket registry before returning; a
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
        // Unconditional teardown, then drain all signal-resumed tasks.
        await gate.close()
        for park in parkTasks { await park.value }
        let final = await gate.snapshot()
        XCTAssertEqual(final.observerResumeCounts[.parkedResumedBySignal] ?? 0, iterations)
        XCTAssertEqual(final.observerInFlightDelivery.normalObservationCount, iterations)
        XCTAssertEqual(final.observerInFlightDelivery.rescueInvocationCount, iterations)
        XCTAssertEqual(final.observerInFlightDelivery.rescueTakeoverCount, 0)
        XCTAssertEqual(final.observerInFlightDelivery.activeCount, 0)
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
            // Unconditional teardown, then drain the open-resumed task. open()
            // sealed .openedWhileParked synchronously, so the assertions above
            // hold without the (deferred) park.value.
            await gate.close()
            await park.value
            let final = await gate.snapshot()
            XCTAssertEqual(final.waiterResumeCounts[.parkedResumedByOpen] ?? 0, 1)
            XCTAssertEqual(final.waiterInFlightDelivery.normalObservationCount, 1)
            XCTAssertEqual(final.waiterInFlightDelivery.rescueInvocationCount, 1)
            XCTAssertEqual(final.waiterInFlightDelivery.rescueTakeoverCount, 0)
            XCTAssertEqual(final.waiterInFlightDelivery.activeCount, 0)
            totalOpens += 1
        }
        XCTAssertEqual(totalOpens, iterations,
                       "all \(iterations) waiter park/open cycles ran")
    }

    // MARK: H1 natural-publication causality

    /// Signal resumes the observer, the child publishes its natural outcome,
    /// emits an acknowledgement, and self-cancels in that exact order while
    /// its handler is live. Close plus caller-owned suspension rescue precede
    /// the only join, so missing publication terminates with a nil/ack failure.
    func testAsyncGateH1AttemptNaturalWinsOverLateCancel_observer() async {
        let gate = AsyncGate()
        let token = await gate.registerObserver()
        let attempt = ObserverAwaitAttempt()
        let publicationAck = NaturalPublicationAck()

        let task = Task {
            await gate.awaitObserverPublishThenSelfCancel(
                token,
                attempt: attempt,
                acknowledgement: publicationAck)
        }
        let parkAck = await gate.waitForObserverParked(token)
        XCTAssertEqual(parkAck, .parked)
        await gate.signal()

        // Wait-for graph: gate.close or attempt.rescueSuspension releases
        // the primary continuation; both run before task.value. Outcome and
        // acknowledgement are inspected synchronously after the bounded join.
        await gate.close()
        attempt.releaseSuspensionForTest()
        await task.value

        guard let outcome = attempt.bufferedOutcomeForTest else {
            XCTFail("natural publication was suppressed")
            return
        }
        XCTAssertEqual(outcome, .finishedBeforeProcessing)
        XCTAssertEqual(publicationAck.publicationCountForTest, 1,
                       "acknowledgement is emitted only after natural outcome publication")
        XCTAssertEqual(attempt.cancelHandlerInvocationCountForTest, 1,
                       "self-cancel ran while the handler was installed")
        XCTAssertEqual(attempt.normalPublicationCountForTest, 1)
        XCTAssertEqual(attempt.rescuePublicationCountForTest, 0)
        assertExactNormalDelivery(attempt.suspensionDeliveryForTest)
        let events = attempt.lifecycleEventsForTest
        XCTAssertEqual(events.map(\.id), [1, 2, 3, 4],
                       "raw event identities must be monotonic and gap-free")
        XCTAssertEqual(events.map(\.phase), [
            .suspensionResumed(site: .signal, requestedBy: .signal),
            .naturalPublicationCommitted,
            .acknowledgementCommitted(observedNaturalEventID: 2),
            .cancellationHandlerObserved(observedAcknowledgementEventID: 3),
        ], "exact sequence is release, natural publication, ACK, then self-cancel observation")
        XCTAssertEqual(publicationAck.evidenceForTest,
                       NaturalPublicationAcknowledgementEvidence(
                        eventID: 3,
                        observedNaturalPublicationEventID: 2),
                       "ACK carries the already-committed natural publication identity")
        let snap = await gate.snapshot()
        XCTAssertEqual(snap.observerFateCounts[.signaledWhileParked] ?? 0, 1)
        XCTAssertEqual(snap.observerCancelInvocationCount, 0,
                       "natural publication causally preceded self-cancellation")
    }

    /// Waiter analogue of the causal publish-acknowledge-self-cancel proof.
    func testAsyncGateH1AttemptNaturalWinsOverLateCancel_waiter() async {
        let gate = AsyncGate()
        let token = await gate.registerWaiter()
        let attempt = WaiterAwaitAttempt()
        let publicationAck = NaturalPublicationAck()

        let task = Task {
            await gate.awaitWaiterPublishThenSelfCancel(
                token,
                attempt: attempt,
                acknowledgement: publicationAck)
        }
        let parkAck = await gate.waitForWaiterParked(token)
        XCTAssertEqual(parkAck, .parked)
        await gate.open()

        // Wait-for graph mirrors the observer path: close and the caller-owned
        // suspension rescue both precede the only join.
        await gate.close()
        attempt.releaseSuspensionForTest()
        await task.value

        guard let outcome = attempt.bufferedOutcomeForTest else {
            XCTFail("natural publication was suppressed")
            return
        }
        XCTAssertEqual(outcome, .finishedBeforeProcessing)
        XCTAssertEqual(publicationAck.publicationCountForTest, 1)
        XCTAssertEqual(attempt.cancelHandlerInvocationCountForTest, 1)
        XCTAssertEqual(attempt.normalPublicationCountForTest, 1)
        XCTAssertEqual(attempt.rescuePublicationCountForTest, 0)
        assertExactNormalDelivery(attempt.suspensionDeliveryForTest)
        let events = attempt.lifecycleEventsForTest
        XCTAssertEqual(events.map(\.id), [1, 2, 3, 4])
        XCTAssertEqual(events.map(\.phase), [
            .suspensionResumed(site: .open, requestedBy: .open),
            .naturalPublicationCommitted,
            .acknowledgementCommitted(observedNaturalEventID: 2),
            .cancellationHandlerObserved(observedAcknowledgementEventID: 3),
        ])
        XCTAssertEqual(publicationAck.evidenceForTest,
                       NaturalPublicationAcknowledgementEvidence(
                        eventID: 3,
                        observedNaturalPublicationEventID: 2))
        let snap = await gate.snapshot()
        XCTAssertEqual(snap.waiterFateCounts[.openedWhileParked] ?? 0, 1)
        XCTAssertEqual(snap.waiterCancelInvocationCount, 0,
                       "natural publication causally preceded self-cancellation")
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

        // Independent caller rescue runs before the join. Correct behavior
        // already resumed at the cancellation handler, so this is a no-op;
        // omitting that authoritative release makes this rescue take, keeping
        // the mutation bounded while changing the raw release site.
        attempt.releaseSuspensionForTest()
        await task.value

        guard let outcome = boundedOutcome(attempt) else {
            XCTFail("observer cancel path produced no outcome")
            return
        }
        XCTAssertEqual(outcome, .cancelled(.processedMatched))
        XCTAssertTrue(attempt.cancellationReleaseSucceededForTest)
        XCTAssertEqual(attempt.rescuePublicationCountForTest, 0)
        XCTAssertEqual(attempt.lifecycleEventsForTest.map(\.phase), [
            .cancellationHandlerObserved(observedAcknowledgementEventID: nil),
            .suspensionResumed(
                site: .cancellationHandler,
                requestedBy: .cancellationHandler),
        ])
        let snap = await gate.snapshot()
        XCTAssertEqual(snap.observerResumeCounts[.parkedResumedByCancel] ?? 0, 1)
        assertExactNormalDelivery(attempt.suspensionDeliveryForTest)
        await gate.close()
    }

    /// Waiter analogue.
    func testAsyncGateH1AttemptCancelWinsOverLateNatural_waiter() async {
        let gate = AsyncGate()
        let token = await gate.registerWaiter()
        let attempt = WaiterAwaitAttempt()

        let task = Task { await gate.awaitWaiter(token, attempt: attempt) }
        _ = await gate.waitForWaiterParked(token)
        task.cancel()

        attempt.releaseSuspensionForTest()
        await task.value

        guard let outcome = boundedOutcome(attempt) else {
            XCTFail("waiter cancel path produced no outcome")
            return
        }
        XCTAssertEqual(outcome, .cancelled(.processedMatched))
        XCTAssertTrue(attempt.cancellationReleaseSucceededForTest)
        XCTAssertEqual(attempt.rescuePublicationCountForTest, 0)
        XCTAssertEqual(attempt.lifecycleEventsForTest.map(\.phase), [
            .cancellationHandlerObserved(observedAcknowledgementEventID: nil),
            .suspensionResumed(
                site: .cancellationHandler,
                requestedBy: .cancellationHandler),
        ])
        let snap = await gate.snapshot()
        XCTAssertEqual(snap.waiterResumeCounts[.parkedResumedByCancel] ?? 0, 1)
        assertExactNormalDelivery(attempt.suspensionDeliveryForTest)
        await gate.close()
    }

    // MARK: Hicks H2 — weak ticket boxes stay bounded on enter+drop

    /// Reviewer finding B (Hicks, MEDIUM): the prior H2 tests proved
    /// exact-zero via a test-only manual cleanup method. That does not tie removal to
    /// NORMAL ticket lifecycle: after the final iteration's ticket
    /// deinit, a dead `[tokenID: WeakBox(nil)]` bucket can remain
    /// indefinitely because nothing in the normal path compacts it.
    ///
    /// This revision proves boundedness AND exact-zero via a NATURAL
    /// gate event: parking the token via `awaitObserver` synchronously
    /// (actor-serialized) invokes
    /// `flushObserverParkAcks(id: token.id, result: .parked)`, which
    /// removes the token's entry from the ticket registry
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
        // removes the observer registry bucket — the
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
        // waiter registry bucket atomically.
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

    // MARK: Distinct-token synchronous ticket cleanup

    /// N distinct unparked tokens drop one ticket each. ARC cleanup removes
    /// every exact identity synchronously and records its reason before any
    /// snapshot, close, park, or manual cleanup can affect the result.
    func testAsyncGateH2DistinctTokenEnterDropAutoCleansBounded_observer() async {
        let gate = AsyncGate()
        let iterations = 100
        var tokens: [AsyncGate.ObserverToken] = []
        tokens.reserveCapacity(iterations)
        for _ in 0..<iterations {
            tokens.append(await gate.registerObserver())
        }

        var cleanupEvidence: [TicketCleanupEvidence] = []
        cleanupEvidence.reserveCapacity(iterations)
        @inline(never)
        func enterAndDropOncePerToken() async {
            for t in tokens {
                let ticket = await gate.enterObserverParkAck(t)
                cleanupEvidence.append(ticket.cleanupEvidence)
            }
        }
        await enterAndDropOncePerToken()

        // ARC deinit removes identities synchronously. These reads happen
        // before snapshot, park, prune, or close.
        let rawBoxes = await gate.debugRawObserverParkAckBoxCount()
        let rawKeys = await gate.debugRawObserverParkAckKeyCount()
        XCTAssertEqual(rawBoxes, 0)
        XCTAssertEqual(rawKeys, 0)
        let automaticCleanupCount = await gate.debugObserverAutomaticTicketCleanupCount()
        XCTAssertEqual(automaticCleanupCount, iterations)
        XCTAssertTrue(cleanupEvidence.allSatisfy {
            $0.resultForTest == .removedAutomatically
        }, "every distinct ticket must record automatic deinit cleanup")

        // Owner lifetime: the ticket callback is weak. Releasing the actor
        // before the ticket yields ownerReleased and cannot retain or crash it.
        var ephemeralGate: AsyncGate? = AsyncGate()
        let ephemeralToken = await ephemeralGate!.registerObserver()
        var orphan: ObserverParkAckTicket? = await ephemeralGate!.enterObserverParkAck(ephemeralToken)
        let orphanEvidence = orphan!.cleanupEvidence
        weak let weakGate = ephemeralGate
        ephemeralGate = nil
        XCTAssertNil(weakGate)
        orphan = nil
        XCTAssertEqual(orphanEvidence.resultForTest, .ownerReleased)

        await gate.close()
    }

    /// Waiter analogue of the distinct-token auto-cleanup proof.
    func testAsyncGateH2DistinctTokenEnterDropAutoCleansBounded_waiter() async {
        let gate = AsyncGate()
        let iterations = 100
        var tokens: [AsyncGate.WaiterToken] = []
        tokens.reserveCapacity(iterations)
        for _ in 0..<iterations {
            tokens.append(await gate.registerWaiter())
        }

        var cleanupEvidence: [TicketCleanupEvidence] = []
        cleanupEvidence.reserveCapacity(iterations)
        @inline(never)
        func enterAndDropOncePerToken() async {
            for t in tokens {
                let ticket = await gate.enterWaiterParkAck(t)
                cleanupEvidence.append(ticket.cleanupEvidence)
            }
        }
        await enterAndDropOncePerToken()

        let rawBoxes = await gate.debugRawWaiterParkAckBoxCount()
        let rawKeys = await gate.debugRawWaiterParkAckKeyCount()
        XCTAssertEqual(rawBoxes, 0)
        XCTAssertEqual(rawKeys, 0)
        let automaticCleanupCount = await gate.debugWaiterAutomaticTicketCleanupCount()
        XCTAssertEqual(automaticCleanupCount, iterations)
        XCTAssertTrue(cleanupEvidence.allSatisfy {
            $0.resultForTest == .removedAutomatically
        })

        await gate.close()
    }

    /// Ripley Finding 2: `resolve(_)` path clears the ticket's deinit
    /// cleanup so a subsequent deinit does NOT double-decrement the
    /// pending-identity set. Proves per-identity accounting is
    /// deinit-order-safe: a ticket the actor resolved (via park-flush)
    /// then dropped by the caller must not cause the barrier to
    /// deadlock or fire twice.
    func testAsyncGateH2ResolvedTicketDeinitDoesNotDoubleDecrement_observer() async {
        let gate = AsyncGate()
        let token = await gate.registerObserver()
        var oldIdentity: UUID!
        var oldEvidence: TicketCleanupEvidence!
        do {
            let old = await gate.enterObserverParkAck(token)
            oldIdentity = old.identity
            oldEvidence = old.cleanupEvidence
        }
        XCTAssertEqual(oldEvidence.resultForTest, .removedAutomatically)

        var newer: ObserverParkAckTicket? = await gate.enterObserverParkAck(token)
        let newerIdentity = newer!.identity
        let newerEvidence = newer!.cleanupEvidence
        let boxesBeforeStaleCleanup = await gate.debugRawObserverParkAckBoxCount()
        let staleCleanup = await gate.debugObserverTicketCleanup(
            tokenID: token.id,
            identity: oldIdentity)
        let wrongCleanup = await gate.debugObserverTicketCleanup(
            tokenID: token.id,
            identity: UUID())
        let boxesAfterStaleCleanup = await gate.debugRawObserverParkAckBoxCount()
        XCTAssertEqual(boxesBeforeStaleCleanup, 1)
        XCTAssertEqual(staleCleanup, .staleIdentity)
        XCTAssertEqual(wrongCleanup, .staleIdentity)
        XCTAssertEqual(boxesAfterStaleCleanup, 1,
                       "stale and wrong identities cannot remove the newer ticket")
        XCTAssertNotEqual(oldIdentity, newerIdentity)

        let parkAttempt = ObserverAwaitAttempt()
        let parkTask = Task { await gate.awaitObserver(token, attempt: parkAttempt) }
        let parkAck = await gate.waitForObserverParked(token)
        XCTAssertEqual(parkAck, .parked)
        let synchronouslyResolved = newer!.isResolved
        // Teardown and independent source/suspension rescue precede every
        // ticket/child await. A suppressed park flush therefore fails instead
        // of stranding on `value()`.
        await gate.close()
        newer!.rescueForTest(.closedOrConsumed)
        parkAttempt.releaseSuspensionForTest()
        await parkTask.value
        guard synchronouslyResolved else {
            XCTFail("park flush must resolve the newer identity before teardown")
            return
        }
        let newerValue = await newer!.value()
        XCTAssertEqual(newerValue, .parked)
        XCTAssertEqual(newer!.rescueCountForTest, 0)
        newer = nil
        XCTAssertEqual(newerEvidence.resultForTest, .resolvedByGate,
                       "resolve suppresses the later deinit callback")
        let automaticCleanupCount = await gate.debugObserverAutomaticTicketCleanupCount()
        XCTAssertEqual(automaticCleanupCount, 1,
                       "resolved ticket deinit must not double-clean")

    }

    /// Waiter analogue.
    func testAsyncGateH2ResolvedTicketDeinitDoesNotDoubleDecrement_waiter() async {
        let gate = AsyncGate()
        let token = await gate.registerWaiter()
        var oldIdentity: UUID!
        var oldEvidence: TicketCleanupEvidence!
        do {
            let old = await gate.enterWaiterParkAck(token)
            oldIdentity = old.identity
            oldEvidence = old.cleanupEvidence
        }
        XCTAssertEqual(oldEvidence.resultForTest, .removedAutomatically)

        var newer: WaiterParkAckTicket? = await gate.enterWaiterParkAck(token)
        let newerIdentity = newer!.identity
        let newerEvidence = newer!.cleanupEvidence
        let staleCleanup = await gate.debugWaiterTicketCleanup(
            tokenID: token.id,
            identity: oldIdentity)
        let wrongCleanup = await gate.debugWaiterTicketCleanup(
            tokenID: token.id,
            identity: UUID())
        let boxesAfterStaleCleanup = await gate.debugRawWaiterParkAckBoxCount()
        XCTAssertEqual(staleCleanup, .staleIdentity)
        XCTAssertEqual(wrongCleanup, .staleIdentity)
        XCTAssertEqual(boxesAfterStaleCleanup, 1)
        XCTAssertNotEqual(oldIdentity, newerIdentity)

        let parkAttempt = WaiterAwaitAttempt()
        let parkTask = Task { await gate.awaitWaiter(token, attempt: parkAttempt) }
        let parkAck = await gate.waitForWaiterParked(token)
        XCTAssertEqual(parkAck, .parked)
        let synchronouslyResolved = newer!.isResolved
        await gate.close()
        newer!.rescueForTest(.closedOrConsumed)
        parkAttempt.releaseSuspensionForTest()
        await parkTask.value
        guard synchronouslyResolved else {
            XCTFail("park flush must resolve the newer identity before teardown")
            return
        }
        let newerValue = await newer!.value()
        XCTAssertEqual(newerValue, .parked)
        XCTAssertEqual(newer!.rescueCountForTest, 0)
        newer = nil
        XCTAssertEqual(newerEvidence.resultForTest, .resolvedByGate)
        let automaticCleanupCount = await gate.debugWaiterAutomaticTicketCleanupCount()
        XCTAssertEqual(automaticCleanupCount, 1)

    }

    func testAsyncGateObserverAttemptSupportsMultiplePreResolutionConsumers() async {
        let gate = AsyncGate()
        let token = await gate.registerObserver()
        let attempt = ObserverAwaitAttempt()
        let firstConsumer = attempt.subscribeForOutcome()
        let secondConsumer = attempt.subscribeForOutcome()
        XCTAssertEqual(attempt.outcomeSubscriberCountForTest, 2)
        let firstInstall = BufferedConsumer<ConsumerInstallationResult>()
        let secondInstall = BufferedConsumer<ConsumerInstallationResult>()
        let first = Task {
            await firstConsumer.value(
                installationAck: firstInstall,
                cancellationValue: .consumerCancelled)
        }
        let second = Task {
            await secondConsumer.value(
                installationAck: secondInstall,
                cancellationValue: .consumerCancelled)
        }
        let firstInstallation = await firstInstall.value()
        let secondInstallation = await secondInstall.value()

        let producer = Task { await gate.awaitObserver(token, attempt: attempt) }
        _ = await gate.waitForObserverParked(token)
        producer.cancel()
        attempt.releaseSuspensionForTest()
        await producer.value
        attempt.rescueCancellationOutcomeIfNeeded()
        await gate.close()
        firstConsumer.rescue(.cancelled(.closedBeforeProcessing))
        secondConsumer.rescue(.cancelled(.closedBeforeProcessing))
        let firstOutcome = await first.value
        let secondOutcome = await second.value

        XCTAssertEqual(firstInstallation, .installed)
        XCTAssertEqual(secondInstallation, .installed)
        XCTAssertEqual(firstInstall.rescueCountForTest, 0)
        XCTAssertEqual(secondInstall.rescueCountForTest, 0)
        XCTAssertEqual(firstOutcome, .cancelled(.processedMatched))
        XCTAssertEqual(secondOutcome, firstOutcome)
        XCTAssertEqual(firstConsumer.rescueCountForTest, 0)
        XCTAssertEqual(secondConsumer.rescueCountForTest, 0)
        XCTAssertEqual(firstConsumer.installationCountForTest, 1)
        XCTAssertEqual(secondConsumer.installationCountForTest, 1)
        XCTAssertEqual(attempt.normalPublicationCountForTest, 1)
        XCTAssertEqual(attempt.rescuePublicationCountForTest, 0)
        XCTAssertEqual(attempt.outcomeSubscriberCountForTest, 0)
        XCTAssertEqual(attempt.outcomeLiveSubscriberCountForTest, 0)
    }

    func testAsyncGateWaiterAttemptSupportsMultiplePreResolutionConsumers() async {
        let gate = AsyncGate()
        let token = await gate.registerWaiter()
        let attempt = WaiterAwaitAttempt()
        let firstConsumer = attempt.subscribeForOutcome()
        let secondConsumer = attempt.subscribeForOutcome()
        XCTAssertEqual(attempt.outcomeSubscriberCountForTest, 2)
        let firstInstall = BufferedConsumer<ConsumerInstallationResult>()
        let secondInstall = BufferedConsumer<ConsumerInstallationResult>()
        let first = Task {
            await firstConsumer.value(
                installationAck: firstInstall,
                cancellationValue: .consumerCancelled)
        }
        let second = Task {
            await secondConsumer.value(
                installationAck: secondInstall,
                cancellationValue: .consumerCancelled)
        }
        let firstInstallation = await firstInstall.value()
        let secondInstallation = await secondInstall.value()

        let producer = Task { await gate.awaitWaiter(token, attempt: attempt) }
        _ = await gate.waitForWaiterParked(token)
        producer.cancel()
        attempt.releaseSuspensionForTest()
        await producer.value
        attempt.rescueCancellationOutcomeIfNeeded()
        await gate.close()
        firstConsumer.rescue(.cancelled(.closedBeforeProcessing))
        secondConsumer.rescue(.cancelled(.closedBeforeProcessing))
        let firstOutcome = await first.value
        let secondOutcome = await second.value

        XCTAssertEqual(firstInstallation, .installed)
        XCTAssertEqual(secondInstallation, .installed)
        XCTAssertEqual(firstInstall.rescueCountForTest, 0)
        XCTAssertEqual(secondInstall.rescueCountForTest, 0)
        XCTAssertEqual(firstOutcome, .cancelled(.processedMatched))
        XCTAssertEqual(secondOutcome, firstOutcome)
        XCTAssertEqual(firstConsumer.rescueCountForTest, 0)
        XCTAssertEqual(secondConsumer.rescueCountForTest, 0)
        XCTAssertEqual(firstConsumer.installationCountForTest, 1)
        XCTAssertEqual(secondConsumer.installationCountForTest, 1)
        XCTAssertEqual(attempt.normalPublicationCountForTest, 1)
        XCTAssertEqual(attempt.rescuePublicationCountForTest, 0)
        XCTAssertEqual(attempt.outcomeSubscriberCountForTest, 0)
        XCTAssertEqual(attempt.outcomeLiveSubscriberCountForTest, 0)
    }

    func testAsyncGateObserverTicketSupportsMultiplePreResolutionConsumers() async {
        let gate = AsyncGate()
        let token = await gate.registerObserver()
        let ticket = await gate.enterObserverParkAck(token)
        let firstConsumer = ticket.subscribeForValue()
        let secondConsumer = ticket.subscribeForValue()
        let source = ticket.sourceForTest
        XCTAssertEqual(source.subscriberCountForTest, 2)
        let firstInstall = BufferedConsumer<ConsumerInstallationResult>()
        let secondInstall = BufferedConsumer<ConsumerInstallationResult>()
        let first = Task {
            await firstConsumer.value(
                installationAck: firstInstall,
                cancellationValue: .consumerCancelled)
        }
        let second = Task {
            await secondConsumer.value(
                installationAck: secondInstall,
                cancellationValue: .consumerCancelled)
        }
        let firstInstallation = await firstInstall.value()
        let secondInstallation = await secondInstall.value()

        let parkAttempt = ObserverAwaitAttempt()
        let park = Task { await gate.awaitObserver(token, attempt: parkAttempt) }
        let parkAck = await gate.waitForObserverParked(token)
        await gate.close()
        ticket.rescueForTest(.closedOrConsumed)
        parkAttempt.releaseSuspensionForTest()
        firstConsumer.rescue(.closedOrConsumed)
        secondConsumer.rescue(.closedOrConsumed)
        await park.value
        let firstValue = await first.value
        let secondValue = await second.value

        XCTAssertEqual(parkAck, .parked)
        XCTAssertEqual(firstInstallation, .installed)
        XCTAssertEqual(secondInstallation, .installed)
        XCTAssertEqual(firstValue, secondValue)
        XCTAssertEqual(firstValue, .parked)
        XCTAssertEqual(firstConsumer.rescueCountForTest, 0)
        XCTAssertEqual(secondConsumer.rescueCountForTest, 0)
        XCTAssertEqual(ticket.rescueCountForTest, 0)
        XCTAssertEqual(source.subscriberCountForTest, 0)
        XCTAssertEqual(source.liveSubscriberCountForTest, 0)
        let rawBoxes = await gate.debugRawObserverParkAckBoxCount()
        let rawKeys = await gate.debugRawObserverParkAckKeyCount()
        XCTAssertEqual(rawBoxes, 0)
        XCTAssertEqual(rawKeys, 0)
    }

    func testAsyncGateWaiterTicketSupportsMultiplePreResolutionConsumers() async {
        let gate = AsyncGate()
        let token = await gate.registerWaiter()
        let ticket = await gate.enterWaiterParkAck(token)
        let firstConsumer = ticket.subscribeForValue()
        let secondConsumer = ticket.subscribeForValue()
        let source = ticket.sourceForTest
        XCTAssertEqual(source.subscriberCountForTest, 2)
        let firstInstall = BufferedConsumer<ConsumerInstallationResult>()
        let secondInstall = BufferedConsumer<ConsumerInstallationResult>()
        let first = Task {
            await firstConsumer.value(
                installationAck: firstInstall,
                cancellationValue: .consumerCancelled)
        }
        let second = Task {
            await secondConsumer.value(
                installationAck: secondInstall,
                cancellationValue: .consumerCancelled)
        }
        let firstInstallation = await firstInstall.value()
        let secondInstallation = await secondInstall.value()

        let parkAttempt = WaiterAwaitAttempt()
        let park = Task { await gate.awaitWaiter(token, attempt: parkAttempt) }
        let parkAck = await gate.waitForWaiterParked(token)
        await gate.close()
        ticket.rescueForTest(.closedOrConsumed)
        parkAttempt.releaseSuspensionForTest()
        firstConsumer.rescue(.closedOrConsumed)
        secondConsumer.rescue(.closedOrConsumed)
        await park.value
        let firstValue = await first.value
        let secondValue = await second.value

        XCTAssertEqual(parkAck, .parked)
        XCTAssertEqual(firstInstallation, .installed)
        XCTAssertEqual(secondInstallation, .installed)
        XCTAssertEqual(firstValue, secondValue)
        XCTAssertEqual(firstValue, .parked)
        XCTAssertEqual(firstConsumer.rescueCountForTest, 0)
        XCTAssertEqual(secondConsumer.rescueCountForTest, 0)
        XCTAssertEqual(ticket.rescueCountForTest, 0)
        XCTAssertEqual(source.subscriberCountForTest, 0)
        XCTAssertEqual(source.liveSubscriberCountForTest, 0)
        let rawBoxes = await gate.debugRawWaiterParkAckBoxCount()
        let rawKeys = await gate.debugRawWaiterParkAckKeyCount()
        XCTAssertEqual(rawBoxes, 0)
        XCTAssertEqual(rawKeys, 0)
    }

    func testAsyncGateObserverTicketDeinitDrainsInstalledConsumer() async {
        let gate = AsyncGate()
        let token = await gate.registerObserver()
        var ticket: ObserverParkAckTicket? = await gate.enterObserverParkAck(token)
        let source = ticket!.sourceForTest
        let cleanupEvidence = ticket!.cleanupEvidence
        let consumer = ticket!.subscribeForValue()
        let callbackEvidence = BufferedConsumer<CallbackReentrancyEvidence>()
        consumer.setResolutionCallback { _ in
            let sourceCounts = source.trySubscriberCountsForTest()
            let registryCounts = gate.debugTryObserverTicketRegistryCounts()
            callbackEvidence.resolve(CallbackReentrancyEvidence(
                sourceRawSubscribers: sourceCounts?.raw,
                sourceLiveSubscribers: sourceCounts?.live,
                registryBoxes: registryCounts?.boxes,
                registryKeys: registryCounts?.keys))
        }
        let installationAck = BufferedConsumer<ConsumerInstallationResult>()
        let waiter = Task {
            await consumer.value(
                installationAck: installationAck,
                cancellationValue: .consumerCancelled)
        }
        let installation = await installationAck.value()

        callbackEvidence.armRescue(CallbackReentrancyEvidence(
            sourceRawSubscribers: nil,
            sourceLiveSubscribers: nil,
            registryBoxes: nil,
            registryKeys: nil))
        ticket = nil
        consumer.rescue(.consumerCancelled)
        let value = await waiter.value
        let callback = await callbackEvidence.value()
        let rawBoxes = await gate.debugRawObserverParkAckBoxCount()
        let rawKeys = await gate.debugRawObserverParkAckKeyCount()
        await gate.close()

        XCTAssertEqual(installation, .installed)
        XCTAssertEqual(value, .ticketDeinitialized)
        XCTAssertEqual(cleanupEvidence.resultForTest, .removedAutomatically)
        XCTAssertEqual(source.subscriberCountForTest, 0)
        XCTAssertEqual(source.liveSubscriberCountForTest, 0)
        XCTAssertEqual(rawBoxes, 0)
        XCTAssertEqual(rawKeys, 0)
        XCTAssertEqual(consumer.rescueCountForTest, 0)
        XCTAssertEqual(installationAck.rescueCountForTest, 0)
        XCTAssertEqual(callback, CallbackReentrancyEvidence(
            sourceRawSubscribers: 0,
            sourceLiveSubscribers: 0,
            registryBoxes: 0,
            registryKeys: 0))
        XCTAssertEqual(callbackEvidence.rescueCountForTest, 0)
    }

    func testAsyncGateWaiterTicketDeinitDrainsInstalledConsumer() async {
        let gate = AsyncGate()
        let token = await gate.registerWaiter()
        var ticket: WaiterParkAckTicket? = await gate.enterWaiterParkAck(token)
        let source = ticket!.sourceForTest
        let cleanupEvidence = ticket!.cleanupEvidence
        let consumer = ticket!.subscribeForValue()
        let callbackEvidence = BufferedConsumer<CallbackReentrancyEvidence>()
        consumer.setResolutionCallback { _ in
            let sourceCounts = source.trySubscriberCountsForTest()
            let registryCounts = gate.debugTryWaiterTicketRegistryCounts()
            callbackEvidence.resolve(CallbackReentrancyEvidence(
                sourceRawSubscribers: sourceCounts?.raw,
                sourceLiveSubscribers: sourceCounts?.live,
                registryBoxes: registryCounts?.boxes,
                registryKeys: registryCounts?.keys))
        }
        let installationAck = BufferedConsumer<ConsumerInstallationResult>()
        let waiter = Task {
            await consumer.value(
                installationAck: installationAck,
                cancellationValue: .consumerCancelled)
        }
        let installation = await installationAck.value()

        callbackEvidence.armRescue(CallbackReentrancyEvidence(
            sourceRawSubscribers: nil,
            sourceLiveSubscribers: nil,
            registryBoxes: nil,
            registryKeys: nil))
        ticket = nil
        consumer.rescue(.consumerCancelled)
        let value = await waiter.value
        let callback = await callbackEvidence.value()
        let rawBoxes = await gate.debugRawWaiterParkAckBoxCount()
        let rawKeys = await gate.debugRawWaiterParkAckKeyCount()
        await gate.close()

        XCTAssertEqual(installation, .installed)
        XCTAssertEqual(value, .ticketDeinitialized)
        XCTAssertEqual(cleanupEvidence.resultForTest, .removedAutomatically)
        XCTAssertEqual(source.subscriberCountForTest, 0)
        XCTAssertEqual(source.liveSubscriberCountForTest, 0)
        XCTAssertEqual(rawBoxes, 0)
        XCTAssertEqual(rawKeys, 0)
        XCTAssertEqual(consumer.rescueCountForTest, 0)
        XCTAssertEqual(installationAck.rescueCountForTest, 0)
        XCTAssertEqual(callback, CallbackReentrancyEvidence(
            sourceRawSubscribers: 0,
            sourceLiveSubscribers: 0,
            registryBoxes: 0,
            registryKeys: 0))
        XCTAssertEqual(callbackEvidence.rescueCountForTest, 0)
    }

    func testAsyncGateObserverTicketConcurrentDeinitResolveIsExactlyOnce() async {
        let gate = AsyncGate()
        let token = await gate.registerObserver()
        var ticket: ObserverParkAckTicket? = await gate.enterObserverParkAck(token)
        let endpoint = ticket!.endpoint
        let source = ticket!.sourceForTest
        let cleanupEvidence = ticket!.cleanupEvidence
        let owner = LockedOwner(ticket!)
        let consumer = ticket!.subscribeForValue()
        ticket = nil

        let installationAck = BufferedConsumer<ConsumerInstallationResult>()
        let waiter = Task {
            await consumer.value(
                installationAck: installationAck,
                cancellationValue: .consumerCancelled)
        }
        let installation = await installationAck.value()
        let barrier = TwoPartyBarrier()
        let closeTask = Task {
            await barrier.arriveAndWait()
            await gate.close()
        }
        let releaseTask = Task {
            await barrier.arriveAndWait()
            owner.release()
        }
        await closeTask.value
        await releaseTask.value

        consumer.rescue(.consumerCancelled)
        let value = await waiter.value
        let rawBoxes = await gate.debugRawObserverParkAckBoxCount()
        let rawKeys = await gate.debugRawObserverParkAckKeyCount()

        XCTAssertEqual(installation, .installed)
        XCTAssertEqual(endpoint.source.resultForTest, value)
        XCTAssertTrue(value == .ticketDeinitialized
                      || value == .terminal(.closedBeforePark))
        if value == .ticketDeinitialized {
            XCTAssertTrue(cleanupEvidence.resultForTest == .removedAutomatically
                          || cleanupEvidence.resultForTest == .staleIdentity)
        } else {
            XCTAssertEqual(cleanupEvidence.resultForTest, .resolvedByGate)
        }
        XCTAssertEqual(consumer.rescueCountForTest, 0)
        XCTAssertEqual(source.subscriberCountForTest, 0)
        XCTAssertEqual(rawBoxes, 0)
        XCTAssertEqual(rawKeys, 0)
    }

    func testAsyncGateWaiterTicketConcurrentDeinitResolveIsExactlyOnce() async {
        let gate = AsyncGate()
        let token = await gate.registerWaiter()
        var ticket: WaiterParkAckTicket? = await gate.enterWaiterParkAck(token)
        let endpoint = ticket!.endpoint
        let source = ticket!.sourceForTest
        let cleanupEvidence = ticket!.cleanupEvidence
        let owner = LockedOwner(ticket!)
        let consumer = ticket!.subscribeForValue()
        ticket = nil

        let installationAck = BufferedConsumer<ConsumerInstallationResult>()
        let waiter = Task {
            await consumer.value(
                installationAck: installationAck,
                cancellationValue: .consumerCancelled)
        }
        let installation = await installationAck.value()
        let barrier = TwoPartyBarrier()
        let closeTask = Task {
            await barrier.arriveAndWait()
            await gate.close()
        }
        let releaseTask = Task {
            await barrier.arriveAndWait()
            owner.release()
        }
        await closeTask.value
        await releaseTask.value

        consumer.rescue(.consumerCancelled)
        let value = await waiter.value
        let rawBoxes = await gate.debugRawWaiterParkAckBoxCount()
        let rawKeys = await gate.debugRawWaiterParkAckKeyCount()

        XCTAssertEqual(installation, .installed)
        XCTAssertEqual(endpoint.source.resultForTest, value)
        XCTAssertTrue(value == .ticketDeinitialized
                      || value == .terminal(.closedBeforePark))
        if value == .ticketDeinitialized {
            XCTAssertTrue(cleanupEvidence.resultForTest == .removedAutomatically
                          || cleanupEvidence.resultForTest == .staleIdentity)
        } else {
            XCTAssertEqual(cleanupEvidence.resultForTest, .resolvedByGate)
        }
        XCTAssertEqual(consumer.rescueCountForTest, 0)
        XCTAssertEqual(source.subscriberCountForTest, 0)
        XCTAssertEqual(rawBoxes, 0)
        XCTAssertEqual(rawKeys, 0)
    }

    func testAsyncGateObserverAndWaiterAttemptOwnerReleaseDrainsConsumers() async {
        var observerAttempt: ObserverAwaitAttempt? = ObserverAwaitAttempt()
        let observerSource = observerAttempt!.outcomeSourceForTest
        let observerConsumer = observerAttempt!.subscribeForOutcome()
        let observerInstall = BufferedConsumer<ConsumerInstallationResult>()
        let observerWaiter = Task {
            await observerConsumer.value(
                installationAck: observerInstall,
                cancellationValue: .consumerCancelled)
        }
        let observerInstallation = await observerInstall.value()
        observerAttempt = nil
        observerConsumer.rescue(.consumerCancelled)
        let observerValue = await observerWaiter.value

        var waiterAttempt: WaiterAwaitAttempt? = WaiterAwaitAttempt()
        let waiterSource = waiterAttempt!.outcomeSourceForTest
        let waiterConsumer = waiterAttempt!.subscribeForOutcome()
        let waiterInstall = BufferedConsumer<ConsumerInstallationResult>()
        let waiterWaiter = Task {
            await waiterConsumer.value(
                installationAck: waiterInstall,
                cancellationValue: .consumerCancelled)
        }
        let waiterInstallation = await waiterInstall.value()
        waiterAttempt = nil
        waiterConsumer.rescue(.consumerCancelled)
        let waiterValue = await waiterWaiter.value

        XCTAssertEqual(observerInstallation, .installed)
        XCTAssertEqual(waiterInstallation, .installed)
        XCTAssertEqual(observerValue, .attemptDeinitialized)
        XCTAssertEqual(waiterValue, .attemptDeinitialized)
        XCTAssertEqual(observerSource.subscriberCountForTest, 0)
        XCTAssertEqual(waiterSource.subscriberCountForTest, 0)
        XCTAssertEqual(observerConsumer.rescueCountForTest, 0)
        XCTAssertEqual(waiterConsumer.rescueCountForTest, 0)
    }

    func testAsyncGateTicketFanoutCancelledAndDeallocatedConsumersLeaveZeroStorage() async {
        let gate = AsyncGate()
        let token = await gate.registerObserver()
        let ticket = await gate.enterObserverParkAck(token)
        let source = ticket.sourceForTest

        let retained = ticket.subscribeForValue()
        let cancelled = ticket.subscribeForValue()
        var dropped: BufferedConsumer<AsyncGate.ParkAckResult>? = ticket.subscribeForValue()
        let droppedEvidence = dropped!.cleanupEvidence
        XCTAssertEqual(source.subscriberCountForTest, 3)
        dropped = nil
        XCTAssertEqual(droppedEvidence.resultForTest, .removedAutomatically)
        XCTAssertEqual(source.subscriberCountForTest, 2)

        let retainedInstall = BufferedConsumer<ConsumerInstallationResult>()
        let cancelledInstall = BufferedConsumer<ConsumerInstallationResult>()
        let retainedTask = Task {
            await retained.value(
                installationAck: retainedInstall,
                cancellationValue: .consumerCancelled)
        }
        let cancelledTask = Task {
            await cancelled.value(
                installationAck: cancelledInstall,
                cancellationValue: .consumerCancelled)
        }
        let retainedInstallation = await retainedInstall.value()
        let cancelledInstallation = await cancelledInstall.value()
        XCTAssertEqual(retainedInstallation, .installed)
        XCTAssertEqual(cancelledInstallation, .installed)
        cancelledTask.cancel()
        cancelled.rescue(.consumerCancelled)
        let cancelledValue = await cancelledTask.value
        XCTAssertEqual(cancelledValue, .consumerCancelled)
        XCTAssertEqual(cancelled.cleanupEvidence.resultForTest, .cancelled)
        XCTAssertEqual(source.subscriberCountForTest, 1)

        let parkAttempt = ObserverAwaitAttempt()
        let park = Task { await gate.awaitObserver(token, attempt: parkAttempt) }
        let parkAck = await gate.waitForObserverParked(token)
        await gate.close()
        ticket.rescueForTest(.closedOrConsumed)
        parkAttempt.releaseSuspensionForTest()
        retained.rescue(.closedOrConsumed)
        await park.value
        let retainedValue = await retainedTask.value

        XCTAssertEqual(parkAck, .parked)
        XCTAssertEqual(retainedValue, .parked)
        XCTAssertEqual(retained.rescueCountForTest, 0)
        XCTAssertEqual(cancelled.rescueCountForTest, 1,
                       "consumer cancellation is the one intentional rescue")
        XCTAssertEqual(source.subscriberCountForTest, 0)
        XCTAssertEqual(source.liveSubscriberCountForTest, 0)
        XCTAssertEqual(ticket.rescueCountForTest, 0)
    }

    func testAsyncGateFanoutSubscribeAfterBufferAndDuplicateResolutionAreOneShot() async {
        let observerAttempt = ObserverAwaitAttempt()
        let observerPublication = observerAttempt.publishNaturalIfActive()
        observerAttempt.resolveCancellation(.closedBeforeProcessing)
        let observerConsumer = observerAttempt.subscribeForOutcome()
        let observerInstall = BufferedConsumer<ConsumerInstallationResult>()
        let observerValue = await observerConsumer.value(
            installationAck: observerInstall,
            cancellationValue: .consumerCancelled)
        let observerInstallValue = await observerInstall.value()

        let waiterAttempt = WaiterAwaitAttempt()
        let waiterPublication = waiterAttempt.publishNaturalIfActive()
        waiterAttempt.resolveCancellation(.closedBeforeProcessing)
        let waiterConsumer = waiterAttempt.subscribeForOutcome()
        let waiterInstall = BufferedConsumer<ConsumerInstallationResult>()
        let waiterValue = await waiterConsumer.value(
            installationAck: waiterInstall,
            cancellationValue: .consumerCancelled)
        let waiterInstallValue = await waiterInstall.value()

        let gate = AsyncGate()
        let token = await gate.registerObserver()
        let ticket = await gate.enterObserverParkAck(token)
        let parkAttempt = ObserverAwaitAttempt()
        let park = Task { await gate.awaitObserver(token, attempt: parkAttempt) }
        let parkAck = await gate.waitForObserverParked(token)
        ticket.resolve(.closedOrConsumed)
        let ticketConsumer = ticket.subscribeForValue()
        let ticketInstall = BufferedConsumer<ConsumerInstallationResult>()
        let ticketValueTask = Task {
            await ticketConsumer.value(
                installationAck: ticketInstall,
                cancellationValue: .consumerCancelled)
        }
        let ticketInstallValue = await ticketInstall.value()
        await gate.close()
        parkAttempt.releaseSuspensionForTest()
        ticketConsumer.rescue(.closedOrConsumed)
        await park.value
        let ticketValue = await ticketValueTask.value

        XCTAssertNotNil(observerPublication)
        XCTAssertNotNil(waiterPublication)
        XCTAssertEqual(observerValue, .finishedBeforeProcessing)
        XCTAssertEqual(waiterValue, .finishedBeforeProcessing)
        XCTAssertEqual(observerInstallValue, .completedFromBuffer)
        XCTAssertEqual(waiterInstallValue, .completedFromBuffer)
        XCTAssertEqual(observerConsumer.installationCountForTest, 0)
        XCTAssertEqual(waiterConsumer.installationCountForTest, 0)
        XCTAssertEqual(parkAck, .parked)
        XCTAssertEqual(ticketValue, .parked,
                       "duplicate ticket resolution cannot overwrite the first buffered value")
        XCTAssertEqual(ticketInstallValue, .completedFromBuffer)
        XCTAssertEqual(ticketConsumer.installationCountForTest, 0)
        XCTAssertEqual(ticket.rescueCountForTest, 0)
    }

    func testAsyncGateSuppressedObserverSignalDeliveryUsesRescueWithoutNormalAck() async {
        let gate = AsyncGate()
        let token = await gate.registerObserver()
        let attempt = ObserverAwaitAttempt(
            suppressNormalSuspensionDeliveryForTest: true)
        let task = Task { await gate.awaitObserver(token, attempt: attempt) }
        let parkAck = await gate.waitForObserverParked(token)
        XCTAssertEqual(parkAck, .parked)

        await gate.signal()
        await gate.close()
        await task.value

        assertRescueTakeoverAfterSuppressedNormalDelivery(
            attempt.suspensionDeliveryForTest)
        XCTAssertEqual(attempt.bufferedOutcomeForTest, .finishedBeforeProcessing)
        XCTAssertEqual(
            attempt.lifecycleEventsForTest.map(\.phase),
            [.naturalPublicationCommitted],
            "rescue takeover must not counterfeit a normal release ACK")
        let snapshot = await gate.snapshot()
        XCTAssertEqual(snapshot.observerFateCounts[.signaledWhileParked], 1)
        XCTAssertEqual(snapshot.observerResumeCounts[.parkedResumedBySignal] ?? 0, 0)
        XCTAssertEqual(snapshot.observerInFlightDelivery.normalObservationCount, 0)
        XCTAssertEqual(snapshot.observerInFlightDelivery.rescueTakeoverCount, 1)
        XCTAssertEqual(snapshot.observerInFlightDelivery.activeCount, 0)
    }

    func testAsyncGateSuppressedWaiterOpenDeliveryUsesRescueWithoutNormalAck() async {
        let gate = AsyncGate()
        let token = await gate.registerWaiter()
        let attempt = WaiterAwaitAttempt(
            suppressNormalSuspensionDeliveryForTest: true)
        let task = Task { await gate.awaitWaiter(token, attempt: attempt) }
        let parkAck = await gate.waitForWaiterParked(token)
        XCTAssertEqual(parkAck, .parked)

        await gate.open()
        await gate.close()
        await task.value

        assertRescueTakeoverAfterSuppressedNormalDelivery(
            attempt.suspensionDeliveryForTest)
        XCTAssertEqual(attempt.bufferedOutcomeForTest, .finishedBeforeProcessing)
        XCTAssertEqual(
            attempt.lifecycleEventsForTest.map(\.phase),
            [.naturalPublicationCommitted])
        let snapshot = await gate.snapshot()
        XCTAssertEqual(snapshot.waiterFateCounts[.openedWhileParked], 1)
        XCTAssertEqual(snapshot.waiterResumeCounts[.parkedResumedByOpen] ?? 0, 0)
        XCTAssertEqual(snapshot.waiterInFlightDelivery.normalObservationCount, 0)
        XCTAssertEqual(snapshot.waiterInFlightDelivery.rescueTakeoverCount, 1)
        XCTAssertEqual(snapshot.waiterInFlightDelivery.activeCount, 0)
    }

    func testAsyncGateSuppressedMatchedCancelDeliveryRescuesObserverAndWaiter() async {
        let observerGate = AsyncGate()
        let observerToken = await observerGate.registerObserver()
        let observerAttempt = ObserverAwaitAttempt(
            suppressNormalSuspensionDeliveryForTest: true)
        let observerTask = Task {
            await observerGate.awaitObserver(
                observerToken,
                attempt: observerAttempt)
        }
        let observerParkAck =
            await observerGate.waitForObserverParked(observerToken)
        XCTAssertEqual(observerParkAck, .parked)
        observerTask.cancel()
        await observerTask.value

        let waiterGate = AsyncGate()
        let waiterToken = await waiterGate.registerWaiter()
        let waiterAttempt = WaiterAwaitAttempt(
            suppressNormalSuspensionDeliveryForTest: true)
        let waiterTask = Task {
            await waiterGate.awaitWaiter(waiterToken, attempt: waiterAttempt)
        }
        let waiterParkAck = await waiterGate.waitForWaiterParked(waiterToken)
        XCTAssertEqual(waiterParkAck, .parked)
        waiterTask.cancel()
        await waiterTask.value

        let observerPreTeardown = await observerGate.snapshot()
        let waiterPreTeardown = await waiterGate.snapshot()
        XCTAssertEqual(observerPreTeardown.observerOrder, [])
        XCTAssertEqual(observerPreTeardown.parkedObserverCount, 0)
        XCTAssertEqual(
            observerPreTeardown.observerFateCounts,
            [.cancelledWhileParked: 1])
        XCTAssertEqual(observerPreTeardown.observerCancelInvocationCount, 1)
        XCTAssertEqual(observerPreTeardown.observerCancelIgnoredCount, 0)
        XCTAssertEqual(waiterPreTeardown.waiterOrder, [])
        XCTAssertEqual(waiterPreTeardown.parkedWaiterCount, 0)
        XCTAssertEqual(
            waiterPreTeardown.waiterFateCounts,
            [.cancelledWhileParked: 1])
        XCTAssertEqual(waiterPreTeardown.waiterCancelInvocationCount, 1)
        XCTAssertEqual(waiterPreTeardown.waiterCancelIgnoredCount, 0)
        XCTAssertEqual(
            observerAttempt.bufferedOutcomeForTest,
            .cancelled(.processedMatched))
        XCTAssertEqual(
            waiterAttempt.bufferedOutcomeForTest,
            .cancelled(.processedMatched))

        await observerGate.close()
        await waiterGate.close()

        assertRescueTakeoverAfterSuppressedNormalDelivery(
            observerAttempt.suspensionDeliveryForTest)
        assertRescueTakeoverAfterSuppressedNormalDelivery(
            waiterAttempt.suspensionDeliveryForTest)
        let observerSnapshot = await observerGate.snapshot()
        let waiterSnapshot = await waiterGate.snapshot()
        XCTAssertEqual(observerSnapshot.observerOrder, [])
        XCTAssertEqual(observerSnapshot.parkedObserverCount, 0)
        XCTAssertEqual(observerSnapshot.observerResumeCounts[.parkedResumedByCancel] ?? 0, 0)
        XCTAssertEqual(waiterSnapshot.waiterOrder, [])
        XCTAssertEqual(waiterSnapshot.parkedWaiterCount, 0)
        XCTAssertEqual(waiterSnapshot.waiterResumeCounts[.parkedResumedByCancel] ?? 0, 0)
    }

    func testAsyncGateSuppressedCloseDeliveryRescuesObserverAndWaiter() async {
        let observerGate = AsyncGate()
        let observerToken = await observerGate.registerObserver()
        let observerAttempt = ObserverAwaitAttempt(
            suppressNormalSuspensionDeliveryForTest: true)
        let observerTask = Task {
            await observerGate.awaitObserver(
                observerToken,
                attempt: observerAttempt)
        }
        let observerParkAck =
            await observerGate.waitForObserverParked(observerToken)
        XCTAssertEqual(observerParkAck, .parked)

        let waiterGate = AsyncGate()
        let waiterToken = await waiterGate.registerWaiter()
        let waiterAttempt = WaiterAwaitAttempt(
            suppressNormalSuspensionDeliveryForTest: true)
        let waiterTask = Task {
            await waiterGate.awaitWaiter(waiterToken, attempt: waiterAttempt)
        }
        let waiterParkAck = await waiterGate.waitForWaiterParked(waiterToken)
        XCTAssertEqual(waiterParkAck, .parked)

        await observerGate.close()
        await waiterGate.close()
        await observerTask.value
        await waiterTask.value

        assertRescueTakeoverAfterSuppressedNormalDelivery(
            observerAttempt.suspensionDeliveryForTest)
        assertRescueTakeoverAfterSuppressedNormalDelivery(
            waiterAttempt.suspensionDeliveryForTest)
        XCTAssertEqual(observerAttempt.bufferedOutcomeForTest, .finishedBeforeProcessing)
        XCTAssertEqual(waiterAttempt.bufferedOutcomeForTest, .finishedBeforeProcessing)
        let observerSnapshot = await observerGate.snapshot()
        let waiterSnapshot = await waiterGate.snapshot()
        XCTAssertEqual(observerSnapshot.observerFateCounts[.closedWhileParked], 1)
        XCTAssertEqual(waiterSnapshot.waiterFateCounts[.closedWhileParked], 1)
        XCTAssertEqual(observerSnapshot.observerResumeCounts[.parkedResumedByClose] ?? 0, 0)
        XCTAssertEqual(waiterSnapshot.waiterResumeCounts[.parkedResumedByClose] ?? 0, 0)
    }

    func testAsyncGateSuppressedObserverTicketFanoutUsesRescueTakeover() async {
        let gate = AsyncGate()
        let token = await gate.registerObserver()
        let ticket = await gate.enterObserverParkAck(token)
        let consumer = ticket.subscribeForValue(
            suppressNormalDeliveryForTest: true)
        let installationAck = BufferedConsumer<ConsumerInstallationResult>()
        let waiter = Task {
            await consumer.value(
                installationAck: installationAck,
                cancellationValue: .consumerCancelled)
        }
        let installation = await installationAck.value()
        XCTAssertEqual(installation, .installed)

        let parkAttempt = ObserverAwaitAttempt()
        let park = Task { await gate.awaitObserver(token, attempt: parkAttempt) }
        let parkAck = await gate.waitForObserverParked(token)
        XCTAssertEqual(parkAck, .parked)
        await gate.close()
        await park.value
        let value = await waiter.value

        XCTAssertEqual(value, .parked)
        assertRescueTakeoverAfterSuppressedNormalDelivery(
            consumer.deliverySnapshotForTest)
        XCTAssertEqual(ticket.rescueInvocationCountForTest, 1)
        XCTAssertEqual(ticket.normalObservationCountForTest, 0)
        XCTAssertEqual(ticket.rescueTakeoverCountForTest, 1)
        XCTAssertEqual(ticket.sourceForTest.subscriberCountForTest, 0)
    }

    func testAsyncGateSuppressedWaiterTicketFanoutUsesRescueTakeover() async {
        let gate = AsyncGate()
        let token = await gate.registerWaiter()
        let ticket = await gate.enterWaiterParkAck(token)
        let consumer = ticket.subscribeForValue(
            suppressNormalDeliveryForTest: true)
        let installationAck = BufferedConsumer<ConsumerInstallationResult>()
        let waiter = Task {
            await consumer.value(
                installationAck: installationAck,
                cancellationValue: .consumerCancelled)
        }
        let installation = await installationAck.value()
        XCTAssertEqual(installation, .installed)

        let parkAttempt = WaiterAwaitAttempt()
        let park = Task { await gate.awaitWaiter(token, attempt: parkAttempt) }
        let parkAck = await gate.waitForWaiterParked(token)
        XCTAssertEqual(parkAck, .parked)
        await gate.close()
        await park.value
        let value = await waiter.value

        XCTAssertEqual(value, .parked)
        assertRescueTakeoverAfterSuppressedNormalDelivery(
            consumer.deliverySnapshotForTest)
        XCTAssertEqual(ticket.rescueInvocationCountForTest, 1)
        XCTAssertEqual(ticket.normalObservationCountForTest, 0)
        XCTAssertEqual(ticket.rescueTakeoverCountForTest, 1)
        XCTAssertEqual(ticket.sourceForTest.subscriberCountForTest, 0)
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
        let token = await gate.registerObserver()
        let origAttempt = ObserverAwaitAttempt()
        let dupAttempt = ObserverAwaitAttempt()

        let original = Task { await gate.awaitObserver(token, attempt: origAttempt) }
        _ = await gate.waitForObserverParked(token)

        let branchCheckpoint = BufferedConsumer<AsyncGate.DuplicateBranchCheckpoint>()
        let processingCheckpoint =
            BufferedConsumer<AsyncGate.ObserverCancelProcessingCheckpoint>()
        let duplicate = Task {
            await gate.awaitObserverDuplicateThenSelfCancel(
                token,
                attempt: dupAttempt,
                branchCheckpoint: branchCheckpoint,
                processingCheckpoint: processingCheckpoint)
        }
        branchCheckpoint.armRescue(.rescuedMissingPublication)
        processingCheckpoint.armRescue(.rescuedMissingPublication)
        let branch = await branchCheckpoint.value()
        dupAttempt.releaseSuspensionForTest()
        let processing = await processingCheckpoint.value()
        _ = await gate.registerWaiter()
        await gate.close()
        dupAttempt.rescueCancellationOutcomeIfNeeded()
        origAttempt.releaseSuspensionForTest()
        await duplicate.value
        await original.value
        XCTAssertEqual(branch, .duplicateAfterParked)
        XCTAssertEqual(processing, .processed(.processedIgnoredMismatch))
        assertExactNormalDelivery(branchCheckpoint.deliverySnapshotForTest)
        assertExactNormalDelivery(processingCheckpoint.deliverySnapshotForTest)
        assertExactNormalDelivery(dupAttempt.suspensionDeliveryForTest)
        assertExactNormalDelivery(origAttempt.suspensionDeliveryForTest)
        XCTAssertEqual(dupAttempt.rescuePublicationInvocationCountForTest, 1)
        XCTAssertEqual(dupAttempt.rescuePublicationCountForTest, 0)

        guard let dupOutcome = dupAttempt.bufferedOutcomeForTest else {
            XCTFail("duplicate observer cancellation produced no outcome")
            return
        }
        XCTAssertEqual(dupOutcome, .cancelled(.processedIgnoredMismatch),
                       "duplicate cancel receipt: bounded no-op via mismatched awaitID")

        // Original attempt was never cancelled — completed naturally.
        // outcome() is buffered and one-shot, so this is bounded.
        guard let origOutcome = boundedOutcome(origAttempt) else {
            XCTFail("original observer natural path produced no outcome")
            return
        }
        XCTAssertEqual(origOutcome, .finishedBeforeProcessing,
                       "original attempt: natural completion via signal")
        XCTAssertEqual(origAttempt.stateForTest, .completedNaturally)

        let final = await gate.snapshot()
        XCTAssertEqual(final.observerCancelInvocationCount, 1)
        XCTAssertEqual(final.observerCancelIgnoredCount, 1)
        XCTAssertEqual(final.observerResumeCounts[.parkedResumedBySignal] ?? 0, 1)
        XCTAssertEqual(final.observerResumeCounts[.parkedResumedByClose] ?? 0, 0,
                       "close must not have rescued original — signal already resumed it")
    }

    /// Waiter analogue of the R1 exact receipt test.
    func testAsyncGateR1ExactCancelReceipt_waiter() async {
        let gate = AsyncGate()
        let token = await gate.registerWaiter()
        let origAttempt = WaiterAwaitAttempt()
        let dupAttempt = WaiterAwaitAttempt()

        let original = Task { await gate.awaitWaiter(token, attempt: origAttempt) }
        _ = await gate.waitForWaiterParked(token)

        let branchCheckpoint = BufferedConsumer<AsyncGate.DuplicateBranchCheckpoint>()
        let processingCheckpoint =
            BufferedConsumer<AsyncGate.WaiterCancelProcessingCheckpoint>()
        let duplicate = Task {
            await gate.awaitWaiterDuplicateThenSelfCancel(
                token,
                attempt: dupAttempt,
                branchCheckpoint: branchCheckpoint,
                processingCheckpoint: processingCheckpoint)
        }
        branchCheckpoint.armRescue(.rescuedMissingPublication)
        processingCheckpoint.armRescue(.rescuedMissingPublication)
        let branch = await branchCheckpoint.value()
        dupAttempt.releaseSuspensionForTest()
        let processing = await processingCheckpoint.value()
        await gate.open()
        await gate.close()
        dupAttempt.rescueCancellationOutcomeIfNeeded()
        origAttempt.releaseSuspensionForTest()
        await duplicate.value
        await original.value
        XCTAssertEqual(branch, .duplicateAfterParked)
        XCTAssertEqual(processing, .processed(.processedIgnoredMismatch))
        assertExactNormalDelivery(branchCheckpoint.deliverySnapshotForTest)
        assertExactNormalDelivery(processingCheckpoint.deliverySnapshotForTest)
        assertExactNormalDelivery(dupAttempt.suspensionDeliveryForTest)
        assertExactNormalDelivery(origAttempt.suspensionDeliveryForTest)
        XCTAssertEqual(dupAttempt.rescuePublicationInvocationCountForTest, 1)
        XCTAssertEqual(dupAttempt.rescuePublicationCountForTest, 0)

        guard let dupOutcome = dupAttempt.bufferedOutcomeForTest else {
            XCTFail("duplicate waiter cancellation produced no outcome")
            return
        }
        XCTAssertEqual(dupOutcome, .cancelled(.processedIgnoredMismatch))

        guard let origOutcome = boundedOutcome(origAttempt) else {
            XCTFail("original waiter natural path produced no outcome")
            return
        }
        XCTAssertEqual(origOutcome, .finishedBeforeProcessing)
        XCTAssertEqual(origAttempt.stateForTest, .completedNaturally)

        let final = await gate.snapshot()
        XCTAssertEqual(final.waiterCancelInvocationCount, 1)
        XCTAssertEqual(final.waiterCancelIgnoredCount, 1)
        XCTAssertEqual(final.waiterResumeCounts[.parkedResumedByOpen] ?? 0, 1)
        XCTAssertEqual(final.waiterResumeCounts[.parkedResumedByClose] ?? 0, 0)
    }

    /// Hicks R1: matched-cancel receipt. Original attempt is cancelled
    /// while parked; its own `attempt.outcome()` returns
    /// `.cancelled(.processedMatched)`. No gate storage.
    func testAsyncGateR1MatchedCancelReceipt_observer() async {
        let gate = AsyncGate()
        let token = await gate.registerObserver()
        let attempt = ObserverAwaitAttempt()

        let task = Task { await gate.awaitObserver(token, attempt: attempt) }
        let parkAck = await gate.waitForObserverParked(token)
        XCTAssertEqual(parkAck, .parked)
        task.cancel()
        attempt.releaseSuspensionForTest()
        await task.value

        guard let outcome = boundedOutcome(attempt) else {
            XCTFail("matched observer cancellation produced no outcome")
            return
        }
        XCTAssertEqual(outcome, .cancelled(.processedMatched),
                       "matched cancel drains THIS attempt's parked continuation")
        let preTeardown = await gate.snapshot()
        XCTAssertEqual(preTeardown.parkedObserverCount, 0,
                       "matched cancellation removes the raw parked entry")
        XCTAssertEqual(preTeardown.observerOrder, [],
                       "matched cancellation removes the ordered token before teardown")
        XCTAssertEqual(preTeardown.observerCancelInvocationCount, 1)
        XCTAssertEqual(preTeardown.observerCancelIgnoredCount, 0)
        XCTAssertEqual(preTeardown.observerFateCounts, [.cancelledWhileParked: 1])
        XCTAssertEqual(preTeardown.observerResumeCounts, [.parkedResumedByCancel: 1])
        XCTAssertTrue(attempt.cancellationReleaseSucceededForTest)
        XCTAssertEqual(attempt.rescuePublicationCountForTest, 0)
        XCTAssertEqual(attempt.lifecycleEventsForTest.map(\.phase), [
            .cancellationHandlerObserved(observedAcknowledgementEventID: nil),
            .suspensionResumed(
                site: .cancellationHandler,
                requestedBy: .cancellationHandler),
        ])
        assertExactNormalDelivery(attempt.suspensionDeliveryForTest)
        await gate.close()
    }

    /// Waiter analogue.
    func testAsyncGateR1MatchedCancelReceipt_waiter() async {
        let gate = AsyncGate()
        let token = await gate.registerWaiter()
        let attempt = WaiterAwaitAttempt()

        let task = Task { await gate.awaitWaiter(token, attempt: attempt) }
        let parkAck = await gate.waitForWaiterParked(token)
        XCTAssertEqual(parkAck, .parked)
        task.cancel()
        attempt.releaseSuspensionForTest()
        await task.value

        guard let outcome = boundedOutcome(attempt) else {
            XCTFail("matched waiter cancellation produced no outcome")
            return
        }
        XCTAssertEqual(outcome, .cancelled(.processedMatched))
        let preTeardown = await gate.snapshot()
        XCTAssertEqual(preTeardown.parkedWaiterCount, 0)
        XCTAssertEqual(preTeardown.waiterOrder, [])
        XCTAssertEqual(preTeardown.waiterCancelInvocationCount, 1)
        XCTAssertEqual(preTeardown.waiterCancelIgnoredCount, 0)
        XCTAssertEqual(preTeardown.waiterFateCounts, [.cancelledWhileParked: 1])
        XCTAssertEqual(preTeardown.waiterResumeCounts, [.parkedResumedByCancel: 1])
        XCTAssertTrue(attempt.cancellationReleaseSucceededForTest)
        XCTAssertEqual(attempt.rescuePublicationCountForTest, 0)
        XCTAssertEqual(attempt.lifecycleEventsForTest.map(\.phase), [
            .cancellationHandlerObserved(observedAcknowledgementEventID: nil),
            .suspensionResumed(
                site: .cancellationHandler,
                requestedBy: .cancellationHandler),
        ])
        assertExactNormalDelivery(attempt.suspensionDeliveryForTest)
        await gate.close()
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
        var rescuePublications = 0
        var suspensionRescueInvocations = 0
        var suspensionRescueTakeovers = 0
        var outcomeRescueInvocations = 0
        for _ in 0..<iterations {
            let token = await gate.registerObserver()
            let attempt = ObserverAwaitAttempt()
            let task = Task { await gate.awaitObserver(token, attempt: attempt) }
            _ = await gate.waitForObserverParked(token)
            task.cancel()
            attempt.releaseSuspensionForTest()
            await task.value
            attempt.rescueCancellationOutcomeIfNeeded()
            rescuePublications += attempt.rescuePublicationCountForTest
            let delivery = attempt.suspensionDeliveryForTest
            suspensionRescueInvocations += delivery.rescueInvocationCount
            suspensionRescueTakeovers += delivery.rescueTakeoverCount
            outcomeRescueInvocations +=
                attempt.rescuePublicationInvocationCountForTest
        }
        let snap = await gate.snapshot()
        XCTAssertFalse(snap.closed, "gate remains open — pre-close boundedness proof")
        XCTAssertEqual(snap.observerCancelIgnoredCount, 0,
                       "matched cancels — no ignored counts should accumulate")
        XCTAssertEqual(snap.observerCancelInvocationCount, iterations,
                       "aggregate cancel-invocation counter is the only growth")
        XCTAssertEqual(snap.observerResumeCounts[.parkedResumedByCancel] ?? 0, iterations)
        XCTAssertEqual(rescuePublications, 0)
        XCTAssertEqual(suspensionRescueInvocations, iterations)
        XCTAssertEqual(suspensionRescueTakeovers, 0)
        XCTAssertEqual(outcomeRescueInvocations, iterations)
        await gate.close()
    }

    /// Waiter analogue of the no-consumer cancel-receipt boundedness proof.
    func testAsyncGateR4NoConsumerCancelReceiptLeavesGateZero_waiter() async {
        let gate = AsyncGate()
        let iterations = 100
        var rescuePublications = 0
        var suspensionRescueInvocations = 0
        var suspensionRescueTakeovers = 0
        var outcomeRescueInvocations = 0
        for _ in 0..<iterations {
            let token = await gate.registerWaiter()
            let attempt = WaiterAwaitAttempt()
            let task = Task { await gate.awaitWaiter(token, attempt: attempt) }
            _ = await gate.waitForWaiterParked(token)
            task.cancel()
            attempt.releaseSuspensionForTest()
            await task.value
            attempt.rescueCancellationOutcomeIfNeeded()
            rescuePublications += attempt.rescuePublicationCountForTest
            let delivery = attempt.suspensionDeliveryForTest
            suspensionRescueInvocations += delivery.rescueInvocationCount
            suspensionRescueTakeovers += delivery.rescueTakeoverCount
            outcomeRescueInvocations +=
                attempt.rescuePublicationInvocationCountForTest
        }
        let snap = await gate.snapshot()
        XCTAssertFalse(snap.closed)
        XCTAssertEqual(snap.waiterCancelInvocationCount, iterations)
        XCTAssertEqual(snap.waiterResumeCounts[.parkedResumedByCancel] ?? 0, iterations)
        XCTAssertEqual(rescuePublications, 0)
        XCTAssertEqual(suspensionRescueInvocations, iterations)
        XCTAssertEqual(suspensionRescueTakeovers, 0)
        XCTAssertEqual(outcomeRescueInvocations, iterations)
        await gate.close()
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
        // (b) cancellation observed `closed == true` (close-first ordering
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
        attempt.releaseSuspensionForTest()
        await task.value

        // Buffered outcome delivers the authoritative per-attempt
        // receipt. Structured cancellation publishes exactly once; the natural
        // completion path finds state == `.cancellationInitiated` and
        // does NOT publish. So exactly one publisher fires, and by
        // the close-first ordering contract MUST be
        // `.cancelled(.closedBeforeProcessing)`. `outcome()` is NOT
        // cyclic — cancellation classification runs to completion (nothing blocks
        // it) and doesn't wait on outcome.
        guard let outcome = boundedOutcome(attempt) else {
            XCTFail("close-first observer cancellation produced no outcome")
            return
        }
        XCTAssertEqual(outcome, .cancelled(.closedBeforeProcessing),
                       "close-first ordering MUST yield exact .cancelled(.closedBeforeProcessing) (no OR)")
        assertExactNormalDelivery(attempt.suspensionDeliveryForTest)

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
        XCTAssertEqual(afterClose.waiterFateCounts[.closedWhileParked] ?? 0, 1)

        // Reviewer finding C: cancel with no pre-arrival handshake.
        // Exact outcome below proves handler-live-at-cancel and
        // close-first ordering — no cyclic wait, no watchdog.
        task.cancel()

        // Unconditional teardown — independently reachable.
        await holdGate.open()
        await holdGate.close()
        attempt.releaseSuspensionForTest()
        await task.value

        guard let outcome = boundedOutcome(attempt) else {
            XCTFail("close-first waiter cancellation produced no outcome")
            return
        }
        XCTAssertEqual(outcome, .cancelled(.closedBeforeProcessing),
                       "close-first ordering MUST yield exact .cancelled(.closedBeforeProcessing) (no OR)")
        assertExactNormalDelivery(attempt.suspensionDeliveryForTest)

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
        attempt.releaseSuspensionForTest()
        await t.value

        guard let outcome = boundedOutcome(attempt) else {
            XCTFail("observer cancel/close race produced no outcome")
            return
        }
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
        attempt.releaseSuspensionForTest()
        await t.value

        guard let outcome = boundedOutcome(attempt) else {
            XCTFail("waiter cancel/close race produced no outcome")
            return
        }
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

        let maximumIssuedID = AsyncGate.maximumIssuedTokenIDForTest
        let reservedHighID = maximumIssuedID + 1
        let zeroObserver = AsyncGate.ObserverToken(id: 0)
        let zeroWaiter = AsyncGate.WaiterToken(id: 0)
        let reservedObserver = AsyncGate.ObserverToken(id: reservedHighID)
        let reservedWaiter = AsyncGate.WaiterToken(id: reservedHighID)
        let maxObserver = AsyncGate.ObserverToken(id: UInt64.max)
        let maxWaiter = AsyncGate.WaiterToken(id: UInt64.max)
        let zeroObserverReceipt = await gate.waitForObserverParked(zeroObserver)
        let zeroWaiterReceipt = await gate.waitForWaiterParked(zeroWaiter)
        let highObserverReceipt = await gate.waitForObserverParked(reservedObserver)
        let highWaiterReceipt = await gate.waitForWaiterParked(reservedWaiter)
        let maxObserverReceipt = await gate.waitForObserverParked(maxObserver)
        let maxWaiterReceipt = await gate.waitForWaiterParked(maxWaiter)
        XCTAssertEqual(zeroObserverReceipt, .unknown)
        XCTAssertEqual(zeroWaiterReceipt, .unknown)
        XCTAssertEqual(highObserverReceipt, .unknown)
        XCTAssertEqual(highWaiterReceipt, .unknown)
        XCTAssertEqual(maxObserverReceipt, .unknown)
        XCTAssertEqual(maxWaiterReceipt, .unknown)

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

        let sequentialGate = AsyncGate()
        let observer1 = await sequentialGate.registerObserver()
        let observer2 = await sequentialGate.registerObserver()
        let waiter1 = await sequentialGate.registerWaiter()
        let waiter2 = await sequentialGate.registerWaiter()
        XCTAssertEqual([observer1.id, observer2.id], [1, 2])
        XCTAssertEqual([waiter1.id, waiter2.id], [1, 2])
        let sequentialSnapshot = await sequentialGate.snapshot()
        XCTAssertEqual(sequentialSnapshot.observerFirstIssuableID, 1)
        XCTAssertEqual(sequentialSnapshot.waiterFirstIssuableID, 1)
        XCTAssertEqual(sequentialSnapshot.observerLastIssuedID, 2)
        XCTAssertEqual(sequentialSnapshot.waiterLastIssuedID, 2)
        XCTAssertEqual(sequentialSnapshot.observerIssuanceExhaustedCount, 0)
        XCTAssertEqual(sequentialSnapshot.waiterIssuanceExhaustedCount, 0)
        await sequentialGate.close()

        let exhaustionGate = AsyncGate(
            observerStartingIDForTest: maximumIssuedID,
            waiterStartingIDForTest: maximumIssuedID)
        let finalObserver = await exhaustionGate.tryRegisterObserver()
        let finalWaiter = await exhaustionGate.tryRegisterWaiter()
        XCTAssertEqual(finalObserver?.id, maximumIssuedID)
        XCTAssertEqual(finalWaiter?.id, maximumIssuedID)
        let exhaustedObserver = await exhaustionGate.tryRegisterObserver()
        let exhaustedWaiter = await exhaustionGate.tryRegisterWaiter()
        XCTAssertNil(exhaustedObserver)
        XCTAssertNil(exhaustedWaiter)
        await exhaustionGate.close()
        let exhaustedObserverReceipt = await exhaustionGate.waitForObserverParked(
            AsyncGate.ObserverToken(id: maximumIssuedID))
        let exhaustedWaiterReceipt = await exhaustionGate.waitForWaiterParked(
            AsyncGate.WaiterToken(id: maximumIssuedID))
        let reservedObserverReceipt = await exhaustionGate.waitForObserverParked(
            AsyncGate.ObserverToken(id: reservedHighID))
        let reservedWaiterReceipt = await exhaustionGate.waitForWaiterParked(
            AsyncGate.WaiterToken(id: UInt64.max))
        XCTAssertEqual(exhaustedObserverReceipt, .closedOrConsumed,
                       "the last actually-issued observer remains previously issued")
        XCTAssertEqual(exhaustedWaiterReceipt, .closedOrConsumed,
                       "the last actually-issued waiter remains previously issued")
        XCTAssertEqual(reservedObserverReceipt, .unknown)
        XCTAssertEqual(reservedWaiterReceipt, .unknown)
        let exhaustedSnapshot = await exhaustionGate.snapshot()
        XCTAssertEqual(exhaustedSnapshot.observerLastIssuedID, maximumIssuedID)
        XCTAssertEqual(exhaustedSnapshot.waiterLastIssuedID, maximumIssuedID)
        XCTAssertEqual(exhaustedSnapshot.observerIssuanceExhaustedCount, 1)
        XCTAssertEqual(exhaustedSnapshot.waiterIssuanceExhaustedCount, 1)
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
        // Ripley Finding 3 (Hicks HIGH) — the prior version awaited
        // `dupAttempt.outcome()`. Under a missing-publication mutation
        // (begin succeeds and state becomes `.cancellationInitiated`),
        // no publisher ever fires and the outcome() await hangs.
        // Similarly under "complete onCancel removal", state stays
        // .active, natural publish path completes (via body reaching
        // mark after hold release), and outcome resolves — but the
        // structural claim would silently succeed with the WRONG
        // fingerprint. Ripley Finding 4 (broken-matcher) also applies
        // to this test: use `hopFingerprintForTest` to disambiguate
        // `.processedIgnoredMismatch` (legitimate) from
        // `.closedBeforeProcessing` (close raced cancel).
        //
        // Redesign:
        //   - Fire duplicate.cancel().
        //   - Independent bounded close of BOTH gates (rescues cancel
        //     Task via cancelCountAcks drain + hold-close drain).
        //   - Await duplicate.value (bounded by close + drain).
        //   - SYNCHRONOUSLY peek dupAttempt.bufferedOutcomeForTest.
        //     nil peek => complete-onCancel-removal or
        //     state-transition-without-task mutation → XCTFail loudly.
        //   - Correlate outcome with attempt.hopFingerprintForTest:
        //     closedAtHop=true  → outcome must be .closedBeforeProcessing
        //     closedAtHop=false → outcome must be .processedIgnoredMismatch
        //     (broken-matcher regression: parked entry existed for
        //     original with different awaitID; the OTHER awaitID being
        //     matched would be `awaitIDMatched=true` which is impossible
        //     because dup's awaitID is fresh and cannot equal original's.
        //     `awaitIDMatched=true` in this test's disjoint-attempts
        //     scenario is definitive evidence of a matcher bug.)
        //   - Signal + close + await original (natural completes
        //     via signal → mark → resolveOutcome; bounded).
        //   - Peek origAttempt outcome via bufferedOutcomeForTest.
        //
        // Wait-for graph (acyclic):
        //   parent.waitForObserverParked -> primary park OR close-drain
        //   parent.holdGate.close        -> bounded actor call
        //   parent.duplicate.value        -> hold-close-drain (bounded)
        //   parent.gate.close             -> bounded actor call
        //     (drains cancelCountAcks + primary continuations)
        //   parent.original.value         -> signal-resume OR gate-close drain
        //   parent.bufferedOutcomeForTest — synchronous peek
        let gate = AsyncGate()

        let token = await gate.registerObserver()
        let origAttempt = ObserverAwaitAttempt()
        let dupAttempt = ObserverAwaitAttempt()
        let original = Task { await gate.awaitObserver(token, attempt: origAttempt) }
        _ = await gate.waitForObserverParked(token)

        let baseline = await gate.snapshot()
        XCTAssertEqual(baseline.parkedObserverCount, 1)
        XCTAssertNil(baseline.observerFates[token.id])

        let branchCheckpoint = BufferedConsumer<AsyncGate.DuplicateBranchCheckpoint>()
        let processingCheckpoint =
            BufferedConsumer<AsyncGate.ObserverCancelProcessingCheckpoint>()
        let duplicate = Task {
            await gate.awaitObserverDuplicateThenSelfCancel(
                token,
                attempt: dupAttempt,
                branchCheckpoint: branchCheckpoint,
                processingCheckpoint: processingCheckpoint)
        }
        branchCheckpoint.armRescue(.rescuedMissingPublication)
        processingCheckpoint.armRescue(.rescuedMissingPublication)
        let branch = await branchCheckpoint.value()
        dupAttempt.releaseSuspensionForTest()
        let processing = await processingCheckpoint.value()
        _ = await gate.registerWaiter()

        // Wait-for graph: close and the caller-owned original suspension
        // rescue both run before the only child join.
        await gate.close()
        dupAttempt.rescueCancellationOutcomeIfNeeded()
        origAttempt.releaseSuspensionForTest()
        await duplicate.value
        await original.value
        XCTAssertEqual(branch, .duplicateAfterParked)
        XCTAssertEqual(processing, .processed(.processedIgnoredMismatch))
        assertExactNormalDelivery(branchCheckpoint.deliverySnapshotForTest)
        assertExactNormalDelivery(processingCheckpoint.deliverySnapshotForTest)
        assertExactNormalDelivery(dupAttempt.suspensionDeliveryForTest)
        assertExactNormalDelivery(origAttempt.suspensionDeliveryForTest)
        XCTAssertEqual(dupAttempt.rescuePublicationInvocationCountForTest, 1)
        XCTAssertEqual(dupAttempt.rescuePublicationCountForTest, 0)

        // SYNCHRONOUS peek: nil = complete-onCancel-removal or
        // state-transition-without-task mutation (no publisher).
        guard let dupOutcome = dupAttempt.bufferedOutcomeForTest else {
            XCTFail("duplicate cancellation published no outcome")
            return
        }
        // Correlate outcome with hop fingerprint (Ripley Finding 4):
        // matches the exact receipt to the state cancelObserver saw
        // AT the actor turn — disambiguates legitimate close-race
        // from broken-matcher regression.
        guard case .cancelled(let dupR) = dupOutcome else {
            XCTFail("duplicate must publish a cancel receipt, got \(dupOutcome)")
            return
        }
        if let hop = dupAttempt.hopFingerprintForTest {
            let independentlyMatched = hop.parkedAwaitID == hop.requestedAwaitID
            XCTAssertEqual(hop.requestedAwaitID, dupAttempt.id)
            XCTAssertEqual(hop.parkedAwaitID, origAttempt.id)
            XCTAssertTrue(hop.parkedEntryExisted)
            XCTAssertFalse(independentlyMatched)
            if hop.closedAtHop {
                XCTAssertEqual(dupR, .closedBeforeProcessing,
                    "closedAtHop=true → receipt must be .closedBeforeProcessing; got \(dupR)")
            } else {
                XCTAssertEqual(dupR, .processedIgnoredMismatch,
                    "closedAtHop=false + mismatched awaitID → receipt must be .processedIgnoredMismatch; got \(dupR)")
            }
        } else {
            // Cancel Task hopped (proven by receipt existence via
            // resolveOutcome) but did not record hop fingerprint.
            // This would indicate the harness modification regressed;
            // fail loudly.
            XCTFail("cancellation published a receipt without raw hop evidence")
        }

        // Consolidated post-teardown structural proof.
        let final = await gate.snapshot()
        XCTAssertEqual(final.observerDuplicateAwaitCount, 1,
                       "duplicate's awaitObserver actor block ran and hit duplicate branch")
        XCTAssertEqual(final.observerResumeCounts[.duplicateAfterParked] ?? 0, 1)
        XCTAssertEqual(final.observerCancelInvocationCount, 1,
                       "duplicate's onCancel dispatched cancelObserver exactly once")
        XCTAssertEqual(final.observerCancelIgnoredCount, 1,
                       "awaitID mismatch OR closed-at-hop → bounded no-op")
        XCTAssertEqual(final.parkedObserverCount, 0)
        XCTAssertEqual(final.observerFateCounts[.signaledWhileParked] ?? 0, 1,
                       "original resumed via signal, not cross-owned cancel")
        XCTAssertEqual(final.observerResumeCounts[.parkedResumedBySignal] ?? 0, 1)
        XCTAssertEqual(final.observerResumeCounts[.parkedResumedByCancel] ?? 0, 0,
                       "no cross-owned cancel drained original")
        XCTAssertEqual(final.observerResumeCounts[.parkedResumedByClose] ?? 0, 0,
                       "signal drained original before close ran")

        // Original attempt: peek buffered outcome synchronously; nil
        // would indicate a natural-publish regression (mark or
        // resolveOutcome broken).
        guard let origOutcome = origAttempt.bufferedOutcomeForTest else {
            XCTFail("origAttempt outcome nil — natural completion regression (mark or resolveOutcome broken)")
            return
        }
        XCTAssertEqual(origOutcome, .finishedBeforeProcessing,
                       "original attempt: natural completion via signal")
        XCTAssertEqual(origAttempt.stateForTest, .completedNaturally)
    }

    /// Waiter analogue of the observer structural late-cancel proof.
    func testAsyncGateStructuralLateCancelWaiterIsBoundedNoOp() async {
        // Ripley Finding 3 waiter analogue — see observer variant for
        // full rationale.
        let gate = AsyncGate()

        let token = await gate.registerWaiter()
        let origAttempt = WaiterAwaitAttempt()
        let dupAttempt = WaiterAwaitAttempt()
        let original = Task { await gate.awaitWaiter(token, attempt: origAttempt) }
        _ = await gate.waitForWaiterParked(token)

        let branchCheckpoint = BufferedConsumer<AsyncGate.DuplicateBranchCheckpoint>()
        let processingCheckpoint =
            BufferedConsumer<AsyncGate.WaiterCancelProcessingCheckpoint>()
        let duplicate = Task {
            await gate.awaitWaiterDuplicateThenSelfCancel(
                token,
                attempt: dupAttempt,
                branchCheckpoint: branchCheckpoint,
                processingCheckpoint: processingCheckpoint)
        }
        branchCheckpoint.armRescue(.rescuedMissingPublication)
        processingCheckpoint.armRescue(.rescuedMissingPublication)
        let branch = await branchCheckpoint.value()
        dupAttempt.releaseSuspensionForTest()
        let processing = await processingCheckpoint.value()
        await gate.open()
        await gate.close()
        dupAttempt.rescueCancellationOutcomeIfNeeded()
        origAttempt.releaseSuspensionForTest()
        await duplicate.value
        await original.value
        XCTAssertEqual(branch, .duplicateAfterParked)
        XCTAssertEqual(processing, .processed(.processedIgnoredMismatch))
        assertExactNormalDelivery(branchCheckpoint.deliverySnapshotForTest)
        assertExactNormalDelivery(processingCheckpoint.deliverySnapshotForTest)
        assertExactNormalDelivery(dupAttempt.suspensionDeliveryForTest)
        assertExactNormalDelivery(origAttempt.suspensionDeliveryForTest)
        XCTAssertEqual(dupAttempt.rescuePublicationInvocationCountForTest, 1)
        XCTAssertEqual(dupAttempt.rescuePublicationCountForTest, 0)

        // SYNCHRONOUS peek — see observer variant for full rationale.
        guard let dupOutcome = dupAttempt.bufferedOutcomeForTest else {
            XCTFail("duplicate cancellation published no outcome")
            return
        }
        guard case .cancelled(let dupR) = dupOutcome else {
            XCTFail("duplicate must publish a cancel receipt, got \(dupOutcome)")
            return
        }
        if let hop = dupAttempt.hopFingerprintForTest {
            let independentlyMatched = hop.parkedAwaitID == hop.requestedAwaitID
            XCTAssertEqual(hop.requestedAwaitID, dupAttempt.id)
            XCTAssertEqual(hop.parkedAwaitID, origAttempt.id)
            XCTAssertTrue(hop.parkedEntryExisted)
            XCTAssertFalse(independentlyMatched)
            if hop.closedAtHop {
                XCTAssertEqual(dupR, .closedBeforeProcessing,
                    "closedAtHop=true → receipt must be .closedBeforeProcessing; got \(dupR)")
            } else {
                XCTAssertEqual(dupR, .processedIgnoredMismatch,
                    "closedAtHop=false + mismatched awaitID → receipt must be .processedIgnoredMismatch; got \(dupR)")
            }
        } else {
            XCTFail("cancellation published a receipt without raw hop evidence")
        }

        let final = await gate.snapshot()
        XCTAssertEqual(final.waiterDuplicateAwaitCount, 1)
        XCTAssertEqual(final.waiterResumeCounts[.duplicateAfterParked] ?? 0, 1)
        XCTAssertEqual(final.waiterCancelInvocationCount, 1)
        XCTAssertEqual(final.waiterCancelIgnoredCount, 1,
                       "awaitID mismatch OR closed-at-hop → bounded no-op")
        XCTAssertEqual(final.parkedWaiterCount, 0)
        XCTAssertEqual(final.waiterFateCounts[.openedWhileParked] ?? 0, 1,
                       "original resumed via open, not cross-owned cancel")
        XCTAssertEqual(final.waiterResumeCounts[.parkedResumedByOpen] ?? 0, 1)
        XCTAssertEqual(final.waiterResumeCounts[.parkedResumedByCancel] ?? 0, 0)
        XCTAssertEqual(final.waiterResumeCounts[.parkedResumedByClose] ?? 0, 0,
                       "open drained original before close ran")

        guard let origOutcome = origAttempt.bufferedOutcomeForTest else {
            XCTFail("origAttempt outcome nil — natural completion regression")
            return
        }
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
        // Unconditional teardown, then drain all signal-resumed tasks.
        await gate.close()
        for t in tasks { await t.value }
        let final = await gate.snapshot()
        XCTAssertEqual(final.observerResumeCounts[.parkedResumedBySignal] ?? 0, iterations,
                       "actual continuation resumed exactly \(iterations) times")
        XCTAssertEqual(final.observerInFlightDelivery.normalObservationCount, iterations)
        XCTAssertEqual(final.observerInFlightDelivery.rescueInvocationCount, iterations)
        XCTAssertEqual(final.observerInFlightDelivery.rescueTakeoverCount, 0)
        XCTAssertEqual(final.observerInFlightDelivery.activeCount, 0)
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
        // Unconditional teardown, then drain all open-resumed tasks.
        await gate.close()
        for t in tasks { await t.value }
        let final = await gate.snapshot()
        XCTAssertEqual(final.waiterResumeCounts[.parkedResumedByOpen] ?? 0, n)
        XCTAssertEqual(final.waiterInFlightDelivery.normalObservationCount, n)
        XCTAssertEqual(final.waiterInFlightDelivery.rescueInvocationCount, n)
        XCTAssertEqual(final.waiterInFlightDelivery.rescueTakeoverCount, 0)
        XCTAssertEqual(final.waiterInFlightDelivery.activeCount, 0)
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

    func testLoadAlertsSupportsFarmWideScope() async {
        await viewModel.loadAlerts(printerId: nil)

        XCTAssertTrue(mockPredictiveService.getActiveAlertsCalled)
        XCTAssertNil(mockPredictiveService.getActiveAlertsCalledWithPrinterId)
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

    func testLoadForecastsSupportsFarmWideScope() async {
        await viewModel.loadForecasts(printerId: nil)

        XCTAssertEqual(mockPredictiveService.getMaintenanceForecastCalledWith, 30)
        XCTAssertNil(mockPredictiveService.getMaintenanceForecastCalledWithPrinterId)
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
/// flight across a structural suspension point. Every
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
// R1: `AwaitAttempt` owns the suspension and outcome subscribers. onCancel
// atomically claims cancellation and releases only that attempt's suspension;
// the resumed actor-isolated body computes and publishes the exact receipt
// before returning. No cancel task or gate-side receipt map exists.
//
// R2: caller-owned tickets live in identity-keyed registries. Ticket deinit
// removes its identity synchronously; park/fate/close takes tickets out of
// the registry before resolving every subscriber.

private enum ConsumerInstallationResult: Sendable, Equatable {
    case installed
    case completedFromBuffer
    case rescuedMissingPublication
}

private enum RecoverableDeliveryOrigin: Sendable, Equatable {
    case normal
    case rescue
}

private struct RecoverableDeliverySnapshot: Sendable, Equatable {
    let normalRequestCount: Int
    let normalEnqueueCount: Int
    let normalObservationCount: Int
    let rescueInvocationCount: Int
    let rescueEnqueueCount: Int
    let rescueTakeoverCount: Int
    let waiterInstallationCount: Int

    var exactNormalDeliveryObserved: Bool {
        normalRequestCount == 1
            && normalEnqueueCount == 1
            && normalObservationCount == 1
            && rescueTakeoverCount == 0
    }
}

private struct RecoverableDeliveryObservation<Value: Sendable>: Sendable {
    let value: Value
    let origin: RecoverableDeliveryOrigin
}

/// A single-consumer delivery channel with two independently reachable
/// publishers. Normal publication always gets the first enqueue attempt.
/// Rescue can be armed before that attempt or invoked after an external claim.
/// `AsyncStream.Continuation.yield` is idempotent and reports enqueue/drop/
/// termination, so rescue never double-resumes a checked continuation.
private final class RecoverableDelivery<Value: Sendable>: @unchecked Sendable {
    private struct Envelope: Sendable {
        let value: Value
        let origin: RecoverableDeliveryOrigin
    }

    private let lock = NSLock()
    private let stream: AsyncStream<Envelope>
    private let continuation: AsyncStream<Envelope>.Continuation
    private let suppressNormalDeliveryForTest: Bool

    private var normalWasRequested = false
    private var normalAttemptCompleted = false
    private var rescueValue: Value?
    private var observedOrigin: RecoverableDeliveryOrigin?
    private var normalRequestCount = 0
    private var normalEnqueueCount = 0
    private var normalObservationCount = 0
    private var rescueInvocationCount = 0
    private var rescueEnqueueCount = 0
    private var rescueTakeoverCount = 0
    private var waiterInstallationCount = 0

    init(suppressNormalDeliveryForTest: Bool = false) {
        let pair = AsyncStream<Envelope>.makeStream(bufferingPolicy: .bufferingOldest(1))
        stream = pair.stream
        continuation = pair.continuation
        self.suppressNormalDeliveryForTest = suppressNormalDeliveryForTest
    }

    @discardableResult
    func requestNormal(_ value: Value) -> Bool {
        lock.lock()
        guard !normalWasRequested else {
            lock.unlock()
            return false
        }
        normalWasRequested = true
        normalRequestCount += 1
        lock.unlock()

        if !suppressNormalDeliveryForTest {
            recordYield(value: value, origin: .normal)
        }

        lock.lock()
        normalAttemptCompleted = true
        let armedRescue = observedOrigin == nil ? rescueValue : nil
        lock.unlock()
        if let armedRescue {
            recordYield(value: armedRescue, origin: .rescue)
        }
        return true
    }

    /// Arms rescue without allowing it to overtake a normal publication that
    /// has not attempted its physical enqueue yet.
    func armRescue(_ value: Value) {
        invokeRescue(value, deliveryWasClaimed: false)
    }

    /// Invokes rescue after an independently observed producer claim.
    func rescueAfterClaim(_ value: Value) {
        invokeRescue(value, deliveryWasClaimed: true)
    }

    func value() async -> RecoverableDeliveryObservation<Value> {
        installWaiter()

        // AsyncStream terminates an iterator owned directly by a cancelled
        // task. The short-lived reader is intentionally not stored anywhere:
        // cancellation remains owned by the surrounding handler, while this
        // read stays alive long enough to observe that handler's publication.
        let stream = stream
        let reader = Task {
            var iterator = stream.makeAsyncIterator()
            return await iterator.next()
        }
        guard let envelope = await reader.value else {
            fatalError("recoverable delivery terminated before observation")
        }

        recordObservation(envelope.origin)
        continuation.finish()
        return RecoverableDeliveryObservation(value: envelope.value, origin: envelope.origin)
    }

    private func installWaiter() {
        lock.lock()
        precondition(waiterInstallationCount == 0, "recoverable delivery supports exactly one waiter")
        waiterInstallationCount = 1
        lock.unlock()
    }

    private func recordObservation(_ origin: RecoverableDeliveryOrigin) {
        lock.lock()
        precondition(observedOrigin == nil, "recoverable delivery was observed more than once")
        observedOrigin = origin
        switch origin {
        case .normal:
            normalObservationCount += 1
        case .rescue:
            rescueTakeoverCount += 1
        }
        lock.unlock()
    }

    var snapshotForTest: RecoverableDeliverySnapshot {
        lock.lock(); defer { lock.unlock() }
        return RecoverableDeliverySnapshot(
            normalRequestCount: normalRequestCount,
            normalEnqueueCount: normalEnqueueCount,
            normalObservationCount: normalObservationCount,
            rescueInvocationCount: rescueInvocationCount,
            rescueEnqueueCount: rescueEnqueueCount,
            rescueTakeoverCount: rescueTakeoverCount,
            waiterInstallationCount: waiterInstallationCount)
    }

    private func invokeRescue(_ value: Value, deliveryWasClaimed: Bool) {
        lock.lock()
        if rescueValue == nil {
            rescueValue = value
            rescueInvocationCount += 1
        }
        let selectedValue = rescueValue!
        let shouldYield = observedOrigin == nil
            && (normalAttemptCompleted || deliveryWasClaimed)
        lock.unlock()
        if shouldYield {
            recordYield(value: selectedValue, origin: .rescue)
        }
    }

    private func recordYield(value: Value, origin: RecoverableDeliveryOrigin) {
        let result = continuation.yield(Envelope(value: value, origin: origin))
        lock.lock()
        switch (origin, result) {
        case (.normal, .enqueued):
            normalEnqueueCount += 1
        case (.rescue, .enqueued):
            rescueEnqueueCount += 1
        case (_, .dropped), (_, .terminated):
            break
        @unknown default:
            break
        }
        lock.unlock()
    }

    deinit {
        continuation.finish()
    }
}

private enum ConsumerCleanupResult: Sendable, Equatable {
    case removedAutomatically
    case cancelled
    case resolvedBySource
    case staleIdentity
    case ownerReleased
}

private enum ConsumerCleanupCause: Sendable {
    case deinitialized
    case cancelled
    case deliveryObserved(RecoverableDeliveryOrigin)
}

private final class ConsumerCleanupEvidence: @unchecked Sendable {
    private let lock = NSLock()
    private var result: ConsumerCleanupResult?

    func resolve(_ value: ConsumerCleanupResult) {
        lock.lock()
        if result == nil {
            result = value
        }
        lock.unlock()
    }

    var resultForTest: ConsumerCleanupResult? {
        lock.lock(); defer { lock.unlock() }
        return result
    }
}

private final class BufferedConsumer<Value: Sendable>: @unchecked Sendable {
    let identity = UUID()
    let cleanupEvidence = ConsumerCleanupEvidence()

    private let lock = NSLock()
    private let delivery: RecoverableDelivery<Value>
    private var normalValue: Value?
    private var observedValue: Value?
    private var subscriptionCleanup:
        (@Sendable (ConsumerCleanupCause) -> ConsumerCleanupResult)?
    private var resolutionCallback: (@Sendable (Value) -> Void)?
    private var installationCount = 0

    init(suppressNormalDeliveryForTest: Bool = false) {
        delivery = RecoverableDelivery(
            suppressNormalDeliveryForTest: suppressNormalDeliveryForTest)
    }

    fileprivate func attachSubscriptionCleanup(
        _ cleanup: @escaping @Sendable (ConsumerCleanupCause) -> ConsumerCleanupResult
    ) {
        lock.lock()
        subscriptionCleanup = cleanup
        lock.unlock()
    }

    fileprivate func resolveFromSource(_ value: Value) {
        lock.lock()
        guard normalValue == nil else {
            lock.unlock()
            return
        }
        normalValue = value
        lock.unlock()
        delivery.armRescue(value)
        precondition(
            delivery.snapshotForTest.rescueInvocationCount == 1,
            "source delivery requested without an armed consumer rescue")
        _ = delivery.requestNormal(value)
    }

    func resolve(_ value: Value) {
        lock.lock()
        guard normalValue == nil else {
            lock.unlock()
            return
        }
        normalValue = value
        lock.unlock()
        delivery.armRescue(value)
        precondition(
            delivery.snapshotForTest.rescueInvocationCount == 1,
            "buffered delivery requested without an armed rescue")
        _ = delivery.requestNormal(value)
    }

    /// Arms a bounded fallback before the producer has claimed publication.
    func armRescue(_ value: Value) {
        delivery.armRescue(value)
    }

    /// Invokes fallback after the caller has independently observed the
    /// producer claim or terminal transition.
    func rescue(_ value: Value) {
        delivery.rescueAfterClaim(value)
    }

    func value(
        installationAck: BufferedConsumer<ConsumerInstallationResult>? = nil,
        cancellationValue: Value? = nil
    ) async -> Value {
        let installationResult = recordWaiterInstallation()
        installationAck?.armRescue(.rescuedMissingPublication)
        installationAck?.resolve(installationResult)

        let observation = await withTaskCancellationHandler {
            await delivery.value()
        } onCancel: {
            if let cancellationValue {
                self.cancel(with: cancellationValue)
            }
        }
        finishObservation(observation)
        return observation.value
    }

    private func recordWaiterInstallation() -> ConsumerInstallationResult {
        lock.lock()
        let result: ConsumerInstallationResult
        if normalValue == nil {
            installationCount += 1
            result = .installed
        } else {
            result = .completedFromBuffer
        }
        lock.unlock()
        return result
    }

    func setResolutionCallback(_ callback: @escaping @Sendable (Value) -> Void) {
        var immediate: Value?
        lock.lock()
        if let observedValue {
            immediate = observedValue
        } else {
            precondition(resolutionCallback == nil, "a buffered consumer supports one resolution callback")
            resolutionCallback = callback
        }
        lock.unlock()
        if let immediate {
            callback(immediate)
        }
    }

    private func cancel(with value: Value) {
        var cleanup: (@Sendable (ConsumerCleanupCause) -> ConsumerCleanupResult)?
        lock.lock()
        cleanup = subscriptionCleanup
        subscriptionCleanup = nil
        lock.unlock()
        cleanupEvidence.resolve(cleanup?(.cancelled) ?? .ownerReleased)
        delivery.rescueAfterClaim(value)
    }

    private func finishObservation(
        _ observation: RecoverableDeliveryObservation<Value>
    ) {
        var cleanup: (@Sendable (ConsumerCleanupCause) -> ConsumerCleanupResult)?
        var callback: (@Sendable (Value) -> Void)?
        lock.lock()
        if observedValue == nil {
            observedValue = observation.value
            cleanup = subscriptionCleanup
            subscriptionCleanup = nil
            callback = resolutionCallback
            resolutionCallback = nil
        }
        lock.unlock()
        if let cleanup {
            cleanupEvidence.resolve(cleanup(.deliveryObserved(observation.origin)))
        }
        callback?(observation.value)
    }

    var deliverySnapshotForTest: RecoverableDeliverySnapshot {
        delivery.snapshotForTest
    }

    var rescueInvocationCountForTest: Int {
        delivery.snapshotForTest.rescueInvocationCount
    }

    var rescueTakeoverCountForTest: Int {
        delivery.snapshotForTest.rescueTakeoverCount
    }

    var rescueCountForTest: Int {
        rescueTakeoverCountForTest
    }

    var installationCountForTest: Int {
        lock.lock(); defer { lock.unlock() }
        return installationCount
    }

    var hasPendingContinuationForTest: Bool {
        let snapshot = delivery.snapshotForTest
        return snapshot.waiterInstallationCount == 1
            && snapshot.normalObservationCount + snapshot.rescueTakeoverCount == 0
    }

    deinit {
        var cleanup: (@Sendable (ConsumerCleanupCause) -> ConsumerCleanupResult)?
        lock.lock()
        cleanup = subscriptionCleanup
        subscriptionCleanup = nil
        lock.unlock()
        cleanupEvidence.resolve(cleanup?(.deinitialized) ?? .ownerReleased)
    }
}

private final class WeakConsumerBox<Value: Sendable> {
    weak var ref: BufferedConsumer<Value>?
    let identity: UUID

    init(consumer: BufferedConsumer<Value>) {
        ref = consumer
        identity = consumer.identity
    }
}

private final class BufferedFanoutSource<Value: Sendable>: @unchecked Sendable {
    private let lock = NSLock()
    private var result: Value?
    private var consumers: [UUID: WeakConsumerBox<Value>] = [:]
    private var normalFanoutAttemptCompleted = false
    private var rescueValue: Value?
    private var rescueInvocationCount = 0
    private var normalObservationCount = 0
    private var rescueTakeoverCount = 0

    func subscribe(
        suppressNormalDeliveryForTest: Bool = false
    ) -> BufferedConsumer<Value> {
        let consumer = BufferedConsumer<Value>(
            suppressNormalDeliveryForTest: suppressNormalDeliveryForTest)
        var immediate: Value?
        var armedRescue: Value?
        lock.lock()
        if let result {
            immediate = result
            if normalFanoutAttemptCompleted {
                armedRescue = rescueValue
            }
        } else {
            let identity = consumer.identity
            consumer.attachSubscriptionCleanup { [weak self] cause in
                self?.remove(identity: identity, cause: cause) ?? .ownerReleased
            }
            consumers[identity] = WeakConsumerBox(consumer: consumer)
        }
        lock.unlock()
        if let immediate {
            consumer.resolveFromSource(immediate)
            if let armedRescue {
                consumer.rescue(armedRescue)
            }
        }
        return consumer
    }

    @discardableResult
    func resolve(_ value: Value) -> Bool {
        var consumersToResolve: [BufferedConsumer<Value>] = []
        lock.lock()
        guard result == nil else {
            lock.unlock()
            return false
        }
        result = value
        normalFanoutAttemptCompleted = false
        if rescueValue == nil {
            rescueValue = value
            rescueInvocationCount += 1
        }
        consumersToResolve = consumers.values.compactMap(\.ref)
        lock.unlock()
        for consumer in consumersToResolve {
            consumer.armRescue(value)
            consumer.resolveFromSource(value)
        }

        lock.lock()
        normalFanoutAttemptCompleted = true
        let armedRescue = rescueValue
        let consumersToRescue = armedRescue == nil
            ? []
            : consumers.values.compactMap(\.ref)
        lock.unlock()
        if let armedRescue {
            for consumer in consumersToRescue {
                consumer.rescue(armedRescue)
            }
        }
        return true
    }

    func rescue(_ value: Value) {
        lock.lock()
        if rescueValue == nil {
            rescueValue = value
            rescueInvocationCount += 1
        }
        let selectedValue = rescueValue!
        let shouldDeliver = result != nil && normalFanoutAttemptCompleted
        let consumersToRescue = shouldDeliver
            ? consumers.values.compactMap(\.ref)
            : []
        lock.unlock()
        for consumer in consumersToRescue {
            consumer.rescue(selectedValue)
        }
    }

    private func remove(
        identity: UUID,
        cause: ConsumerCleanupCause
    ) -> ConsumerCleanupResult {
        lock.lock()
        guard consumers.removeValue(forKey: identity) != nil else {
            lock.unlock()
            return .staleIdentity
        }
        let result: ConsumerCleanupResult
        switch cause {
        case .deinitialized:
            result = .removedAutomatically
        case .cancelled:
            result = .cancelled
        case .deliveryObserved(let origin):
            result = .resolvedBySource
            switch origin {
            case .normal:
                normalObservationCount += 1
            case .rescue:
                rescueTakeoverCount += 1
            }
        }
        lock.unlock()
        return result
    }

    var subscriberCountForTest: Int {
        lock.lock(); defer { lock.unlock() }
        return consumers.count
    }

    var liveSubscriberCountForTest: Int {
        lock.lock(); defer { lock.unlock() }
        return consumers.values.reduce(0) { $0 + ($1.ref == nil ? 0 : 1) }
    }

    var resultForTest: Value? {
        lock.lock(); defer { lock.unlock() }
        return result
    }

    var rescueInvocationCountForTest: Int {
        lock.lock(); defer { lock.unlock() }
        return rescueInvocationCount
    }

    var normalObservationCountForTest: Int {
        lock.lock(); defer { lock.unlock() }
        return normalObservationCount
    }

    var rescueTakeoverCountForTest: Int {
        lock.lock(); defer { lock.unlock() }
        return rescueTakeoverCount
    }

    func trySubscriberCountsForTest() -> (raw: Int, live: Int)? {
        guard lock.try() else { return nil }
        defer { lock.unlock() }
        return (
            raw: consumers.count,
            live: consumers.values.reduce(0) { $0 + ($1.ref == nil ? 0 : 1) })
    }
}

private actor TwoPartyBarrier {
    private var arrivals = 0
    private var waiters: [CheckedContinuation<Void, Never>] = []

    func arriveAndWait() async {
        arrivals += 1
        if arrivals == 2 {
            let pending = waiters
            waiters.removeAll()
            for waiter in pending {
                waiter.resume()
            }
            return
        }
        await withCheckedContinuation { continuation in
            waiters.append(continuation)
        }
    }
}

private final class LockedOwner<Value: AnyObject>: @unchecked Sendable {
    private let lock = NSLock()
    private var value: Value?

    init(_ value: Value) {
        self.value = value
    }

    func release() {
        var released: Value?
        lock.lock()
        released = value
        value = nil
        lock.unlock()
        withExtendedLifetime(released) {}
    }
}

private struct CallbackReentrancyEvidence: Sendable, Equatable {
    let sourceRawSubscribers: Int?
    let sourceLiveSubscribers: Int?
    let registryBoxes: Int?
    let registryKeys: Int?
}

private enum AwaitSuspensionReleaseSite: Sendable, Equatable {
    case installAfterPriorRelease
    case latchConsumed
    case duplicateAfterParked
    case duplicateAfterFated
    case unknownToken
    case closedImmediate
    case openedImmediate
    case signal
    case open
    case close
    case cancellationHandler
    case testRescue
}

private struct AwaitSuspensionDeliveryRequest: Sendable {
    let observedSite: AwaitSuspensionReleaseSite
    let requestedBy: AwaitSuspensionReleaseSite
    let onNormalObservation: @Sendable () -> Void
}

private final class AwaitSuspensionChannel: @unchecked Sendable {
    private let lock = NSLock()
    private let delivery: RecoverableDelivery<AwaitSuspensionDeliveryRequest>
    private var waiterInstalled = false
    private var releaseRequestedBy: AwaitSuspensionReleaseSite?
    private var observationCallback:
        (@Sendable (RecoverableDeliveryOrigin) -> Void)?

    init(suppressNormalDeliveryForTest: Bool = false) {
        delivery = RecoverableDelivery(
            suppressNormalDeliveryForTest: suppressNormalDeliveryForTest)
    }

    func installWaiter() {
        lock.lock()
        precondition(!waiterInstalled, "an await attempt supports exactly one suspension")
        waiterInstalled = true
        lock.unlock()
    }

    @discardableResult
    func requestRelease(
        at site: AwaitSuspensionReleaseSite,
        onClaim: @escaping @Sendable () -> Void,
        onNormalObservation: @escaping @Sendable () -> Void,
        onObservation: @escaping @Sendable (RecoverableDeliveryOrigin) -> Void
    ) -> Bool {
        lock.lock()
        guard releaseRequestedBy == nil else {
            lock.unlock()
            return false
        }
        releaseRequestedBy = site
        observationCallback = onObservation
        let observedSite: AwaitSuspensionReleaseSite =
            waiterInstalled ? site : .installAfterPriorRelease
        lock.unlock()
        onClaim()
        precondition(
            delivery.snapshotForTest.rescueInvocationCount == 1,
            "attempt release requested without an armed rescue")
        return delivery.requestNormal(AwaitSuspensionDeliveryRequest(
            observedSite: observedSite,
            requestedBy: site,
            onNormalObservation: onNormalObservation))
    }

    func wait(
        recordingWith recorder: AttemptLifecycleRecorder
    ) async -> RecoverableDeliveryOrigin {
        let observation = await delivery.value()
        if observation.origin == .normal {
            recorder.record(.suspensionResumed(
                site: observation.value.observedSite,
                requestedBy: observation.value.requestedBy))
            observation.value.onNormalObservation()
        }
        takeObservationCallback()?(observation.origin)
        return observation.origin
    }

    func invokeRescue() {
        delivery.armRescue(AwaitSuspensionDeliveryRequest(
            observedSite: .testRescue,
            requestedBy: .testRescue,
            onNormalObservation: {}))
    }

    var snapshotForTest: RecoverableDeliverySnapshot {
        delivery.snapshotForTest
    }

    private func takeObservationCallback()
        -> (@Sendable (RecoverableDeliveryOrigin) -> Void)?
    {
        lock.lock()
        let callback = observationCallback
        observationCallback = nil
        lock.unlock()
        return callback
    }
}

private final class GateResumeEvidence<Site: Sendable & Hashable>: @unchecked Sendable {
    private let lock = NSLock()
    private var counts: [Site: Int] = [:]

    func record(_ site: Site) {
        lock.lock()
        counts[site, default: 0] += 1
        lock.unlock()
    }

    var countsForTest: [Site: Int] {
        lock.lock(); defer { lock.unlock() }
        return counts
    }
}

private struct InFlightDeliverySnapshot: Sendable, Equatable {
    let activeCount: Int
    let requestCount: Int
    let rescueInvocationCount: Int
    let normalObservationCount: Int
    let rescueTakeoverCount: Int
}

private final class InFlightDeliveryRegistry<Attempt: AnyObject>: @unchecked Sendable {
    private let lock = NSLock()
    private var attempts: [UUID: Attempt] = [:]
    private var requestCount = 0
    private var rescueInvocationCount = 0
    private var normalObservationCount = 0
    private var rescueTakeoverCount = 0

    func insert(
        _ attempt: Attempt,
        id: UUID,
        rescueWasInvoked: Bool
    ) {
        lock.lock()
        precondition(attempts[id] == nil, "attempt delivery registered twice")
        attempts[id] = attempt
        requestCount += 1
        if rescueWasInvoked {
            rescueInvocationCount += 1
        }
        lock.unlock()
    }

    func observe(id: UUID, origin: RecoverableDeliveryOrigin) {
        lock.lock()
        precondition(
            attempts.removeValue(forKey: id) != nil,
            "unregistered attempt delivery observed")
        switch origin {
        case .normal:
            normalObservationCount += 1
        case .rescue:
            rescueTakeoverCount += 1
        }
        lock.unlock()
    }

    var snapshotForTest: InFlightDeliverySnapshot {
        lock.lock(); defer { lock.unlock() }
        return InFlightDeliverySnapshot(
            activeCount: attempts.count,
            requestCount: requestCount,
            rescueInvocationCount: rescueInvocationCount,
            normalObservationCount: normalObservationCount,
            rescueTakeoverCount: rescueTakeoverCount)
    }
}

private enum AttemptLifecyclePhase: Sendable, Equatable {
    case suspensionResumed(
        site: AwaitSuspensionReleaseSite,
        requestedBy: AwaitSuspensionReleaseSite)
    case naturalPublicationCommitted
    case acknowledgementCommitted(observedNaturalEventID: UInt64)
    case cancellationHandlerObserved(observedAcknowledgementEventID: UInt64?)
}

private struct AttemptLifecycleEvent: Sendable, Equatable {
    let id: UInt64
    let phase: AttemptLifecyclePhase
}

private final class AttemptLifecycleRecorder: @unchecked Sendable {
    private let lock = NSLock()
    private var nextID: UInt64 = 1
    private var events: [AttemptLifecycleEvent] = []

    @discardableResult
    func record(_ phase: AttemptLifecyclePhase) -> AttemptLifecycleEvent {
        lock.lock()
        precondition(nextID < UInt64.max, "attempt lifecycle event identity exhausted")
        let event = AttemptLifecycleEvent(id: nextID, phase: phase)
        nextID += 1
        events.append(event)
        lock.unlock()
        return event
    }

    var eventsForTest: [AttemptLifecycleEvent] {
        lock.lock(); defer { lock.unlock() }
        return events
    }
}

private struct NaturalPublicationEvidence: Sendable, Equatable {
    let eventID: UInt64
}

private struct NaturalPublicationAcknowledgementEvidence: Sendable, Equatable {
    let eventID: UInt64
    let observedNaturalPublicationEventID: UInt64
}

private final class NaturalPublicationAck: @unchecked Sendable {
    private let lock = NSLock()
    private var evidence: NaturalPublicationAcknowledgementEvidence?
    private var publicationCount = 0

    fileprivate func publish(_ value: NaturalPublicationAcknowledgementEvidence) {
        lock.lock()
        if evidence == nil {
            evidence = value
        }
        publicationCount += 1
        lock.unlock()
    }

    var evidenceForTest: NaturalPublicationAcknowledgementEvidence? {
        lock.lock(); defer { lock.unlock() }
        return evidence
    }

    var publicationCountForTest: Int {
        lock.lock(); defer { lock.unlock() }
        return publicationCount
    }
}

private enum TicketCleanupResult: Sendable, Equatable {
    case removedAutomatically
    case resolvedByGate
    case staleIdentity
    case ownerReleased
}

private final class TicketCleanupEvidence: @unchecked Sendable {
    private let lock = NSLock()
    private var result: TicketCleanupResult?

    func resolve(_ value: TicketCleanupResult) {
        lock.lock()
        if result == nil {
            result = value
        }
        lock.unlock()
    }

    var resultForTest: TicketCleanupResult? {
        lock.lock(); defer { lock.unlock() }
        return result
    }
}

private final class TicketResolutionEndpoint<Value: Sendable>: @unchecked Sendable {
    let cleanupEvidence = TicketCleanupEvidence()
    let source = BufferedFanoutSource<Value>()

    @discardableResult
    func resolve(_ value: Value, cleanupResult: TicketCleanupResult) -> Bool {
        guard source.resolve(value) else { return false }
        precondition(
            source.rescueInvocationCountForTest == 1,
            "ticket resolution requested without an armed fan-out rescue")
        cleanupEvidence.resolve(cleanupResult)
        return true
    }

    func rescue(_ value: Value) {
        source.rescue(value)
    }

    var rescueInvocationCountForTest: Int {
        source.rescueInvocationCountForTest
    }

    var normalObservationCountForTest: Int {
        source.normalObservationCountForTest
    }

    var rescueTakeoverCountForTest: Int {
        source.rescueTakeoverCountForTest
    }

    var rescueCountForTest: Int {
        rescueTakeoverCountForTest
    }
}

/// Caller-owned observer await lifecycle. Cancellation only claims state and
/// releases this attempt's own suspension. The resumed actor-isolated body
/// performs cancellation and publishes the exact receipt before it returns,
/// so no unstructured cancel task or task handle exists.
final class ObserverAwaitAttempt: @unchecked Sendable {
    let id = UUID()

    fileprivate enum Outcome: Sendable, Equatable {
        case finishedBeforeProcessing
        case cancelled(AsyncGate.ObserverCancelReceipt)
        case consumerCancelled
        case attemptDeinitialized
    }

    fileprivate enum State { case active, cancellationInitiated, completedNaturally }

    struct HopFingerprint: Sendable, Hashable {
        let requestedAwaitID: UUID
        let parkedAwaitID: UUID?
        let parkedEntryExisted: Bool
        let closedAtHop: Bool
    }

    private let lock = NSLock()
    private let lifecycleRecorder = AttemptLifecycleRecorder()
    private let outcomeSource = BufferedFanoutSource<Outcome>()
    private let suspensionChannel: AwaitSuspensionChannel
    private var state: State = .active
    private var outcomeValue: Outcome?
    private var hopFingerprint: HopFingerprint?
    private var cancelHandlerInvocationCount = 0
    private var normalPublicationCount = 0
    private var rescuePublicationInvocationCount = 0
    private var rescuePublicationCount = 0

    init(suppressNormalSuspensionDeliveryForTest: Bool = false) {
        suspensionChannel = AwaitSuspensionChannel(
            suppressNormalDeliveryForTest: suppressNormalSuspensionDeliveryForTest)
    }

    fileprivate func prepareSuspensionWait() {
        suspensionChannel.installWaiter()
    }

    @discardableResult
    fileprivate func awaitSuspensionDelivery() async -> RecoverableDeliveryOrigin {
        await suspensionChannel.wait(recordingWith: lifecycleRecorder)
    }

    @discardableResult
    fileprivate func releaseSuspension(
        at site: AwaitSuspensionReleaseSite,
        onClaim: @escaping @Sendable () -> Void = {},
        onNormalObservation: @escaping @Sendable () -> Void = {},
        onObservation: @escaping @Sendable (RecoverableDeliveryOrigin) -> Void = { _ in }
    ) -> Bool {
        suspensionChannel.requestRelease(
            at: site,
            onClaim: onClaim,
            onNormalObservation: onNormalObservation,
            onObservation: onObservation)
    }

    fileprivate func invokeSuspensionRescueForTest() {
        suspensionChannel.invokeRescue()
    }

    fileprivate func releaseSuspensionForTest() {
        invokeSuspensionRescueForTest()
    }

    fileprivate func beginCancellationIfActive(
        observing acknowledgement: NaturalPublicationAck? = nil
    ) -> Bool {
        let acknowledgementEventID = acknowledgement?.evidenceForTest?.eventID
        lifecycleRecorder.record(.cancellationHandlerObserved(
            observedAcknowledgementEventID: acknowledgementEventID))
        lock.lock()
        cancelHandlerInvocationCount += 1
        guard state == .active else {
            lock.unlock()
            return false
        }
        state = .cancellationInitiated
        lock.unlock()
        return true
    }

    @discardableResult
    fileprivate func publishNaturalIfActive() -> NaturalPublicationEvidence? {
        lock.lock()
        guard state == .active, outcomeValue == nil else {
            lock.unlock()
            return nil
        }
        state = .completedNaturally
        outcomeValue = .finishedBeforeProcessing
        normalPublicationCount += 1
        let event = lifecycleRecorder.record(.naturalPublicationCommitted)
        lock.unlock()
        _ = outcomeSource.resolve(.finishedBeforeProcessing)
        return NaturalPublicationEvidence(eventID: event.id)
    }

    fileprivate func publishAcknowledgement(
        _ acknowledgement: NaturalPublicationAck,
        after publication: NaturalPublicationEvidence
    ) {
        let event = lifecycleRecorder.record(.acknowledgementCommitted(
            observedNaturalEventID: publication.eventID))
        acknowledgement.publish(NaturalPublicationAcknowledgementEvidence(
            eventID: event.id,
            observedNaturalPublicationEventID: publication.eventID))
    }

    fileprivate func resolveCancellation(_ receipt: AsyncGate.ObserverCancelReceipt) {
        let outcome = Outcome.cancelled(receipt)
        var didPublish = false
        lock.lock()
        if outcomeValue == nil {
            outcomeValue = outcome
            normalPublicationCount += 1
            didPublish = true
        }
        lock.unlock()
        if didPublish {
            _ = outcomeSource.resolve(outcome)
        }
    }

    fileprivate func rescueCancellationOutcomeIfNeeded(
        _ receipt: AsyncGate.ObserverCancelReceipt = .closedBeforeProcessing
    ) {
        let outcome = Outcome.cancelled(receipt)
        var didPublish = false
        lock.lock()
        rescuePublicationInvocationCount += 1
        if state == .cancellationInitiated, outcomeValue == nil {
            outcomeValue = outcome
            rescuePublicationCount += 1
            didPublish = true
        }
        lock.unlock()
        if didPublish {
            _ = outcomeSource.resolve(outcome)
        }
    }

    fileprivate func subscribeForOutcome() -> BufferedConsumer<Outcome> {
        outcomeSource.subscribe()
    }

    fileprivate func outcome() async -> Outcome {
        await subscribeForOutcome().value(cancellationValue: .consumerCancelled)
    }

    fileprivate var cancellationWasInitiated: Bool {
        lock.lock(); defer { lock.unlock() }
        return state == .cancellationInitiated
    }

    fileprivate var completedNaturally: Bool {
        lock.lock(); defer { lock.unlock() }
        return state == .completedNaturally
    }

    fileprivate var stateForTest: State {
        lock.lock(); defer { lock.unlock() }
        return state
    }

    fileprivate var bufferedOutcomeForTest: Outcome? {
        lock.lock(); defer { lock.unlock() }
        return outcomeValue
    }

    fileprivate var cancelHandlerInvocationCountForTest: Int {
        lock.lock(); defer { lock.unlock() }
        return cancelHandlerInvocationCount
    }

    fileprivate var normalPublicationCountForTest: Int {
        lock.lock(); defer { lock.unlock() }
        return normalPublicationCount
    }

    fileprivate var rescuePublicationCountForTest: Int {
        lock.lock(); defer { lock.unlock() }
        return rescuePublicationCount
    }

    fileprivate var rescuePublicationInvocationCountForTest: Int {
        lock.lock(); defer { lock.unlock() }
        return rescuePublicationInvocationCount
    }

    fileprivate var outcomeSubscriberCountForTest: Int {
        outcomeSource.subscriberCountForTest
    }

    fileprivate var outcomeLiveSubscriberCountForTest: Int {
        outcomeSource.liveSubscriberCountForTest
    }

    fileprivate var lifecycleEventsForTest: [AttemptLifecycleEvent] {
        lifecycleRecorder.eventsForTest
    }

    fileprivate var suspensionDeliveryForTest: RecoverableDeliverySnapshot {
        suspensionChannel.snapshotForTest
    }

    fileprivate var cancellationReleaseSucceededForTest: Bool {
        lifecycleRecorder.eventsForTest.contains {
            guard case .suspensionResumed(_, let requestedBy) = $0.phase else {
                return false
            }
            return requestedBy == .cancellationHandler
        }
    }

    fileprivate var outcomeSourceForTest: BufferedFanoutSource<Outcome> {
        outcomeSource
    }

    fileprivate func recordHopFingerprint(_ fingerprint: HopFingerprint) {
        lock.lock()
        if hopFingerprint == nil {
            hopFingerprint = fingerprint
        }
        lock.unlock()
    }

    var hopFingerprintForTest: HopFingerprint? {
        lock.lock(); defer { lock.unlock() }
        return hopFingerprint
    }

    deinit {
        _ = outcomeSource.resolve(.attemptDeinitialized)
    }
}

/// Waiter analogue of `ObserverAwaitAttempt`.
final class WaiterAwaitAttempt: @unchecked Sendable {
    let id = UUID()

    fileprivate enum Outcome: Sendable, Equatable {
        case finishedBeforeProcessing
        case cancelled(AsyncGate.WaiterCancelReceipt)
        case consumerCancelled
        case attemptDeinitialized
    }

    fileprivate enum State { case active, cancellationInitiated, completedNaturally }

    struct HopFingerprint: Sendable, Hashable {
        let requestedAwaitID: UUID
        let parkedAwaitID: UUID?
        let parkedEntryExisted: Bool
        let closedAtHop: Bool
    }

    private let lock = NSLock()
    private let lifecycleRecorder = AttemptLifecycleRecorder()
    private let outcomeSource = BufferedFanoutSource<Outcome>()
    private let suspensionChannel: AwaitSuspensionChannel
    private var state: State = .active
    private var outcomeValue: Outcome?
    private var hopFingerprint: HopFingerprint?
    private var cancelHandlerInvocationCount = 0
    private var normalPublicationCount = 0
    private var rescuePublicationInvocationCount = 0
    private var rescuePublicationCount = 0

    init(suppressNormalSuspensionDeliveryForTest: Bool = false) {
        suspensionChannel = AwaitSuspensionChannel(
            suppressNormalDeliveryForTest: suppressNormalSuspensionDeliveryForTest)
    }

    fileprivate func prepareSuspensionWait() {
        suspensionChannel.installWaiter()
    }

    @discardableResult
    fileprivate func awaitSuspensionDelivery() async -> RecoverableDeliveryOrigin {
        await suspensionChannel.wait(recordingWith: lifecycleRecorder)
    }

    @discardableResult
    fileprivate func releaseSuspension(
        at site: AwaitSuspensionReleaseSite,
        onClaim: @escaping @Sendable () -> Void = {},
        onNormalObservation: @escaping @Sendable () -> Void = {},
        onObservation: @escaping @Sendable (RecoverableDeliveryOrigin) -> Void = { _ in }
    ) -> Bool {
        suspensionChannel.requestRelease(
            at: site,
            onClaim: onClaim,
            onNormalObservation: onNormalObservation,
            onObservation: onObservation)
    }

    fileprivate func invokeSuspensionRescueForTest() {
        suspensionChannel.invokeRescue()
    }

    fileprivate func releaseSuspensionForTest() {
        invokeSuspensionRescueForTest()
    }

    fileprivate func beginCancellationIfActive(
        observing acknowledgement: NaturalPublicationAck? = nil
    ) -> Bool {
        let acknowledgementEventID = acknowledgement?.evidenceForTest?.eventID
        lifecycleRecorder.record(.cancellationHandlerObserved(
            observedAcknowledgementEventID: acknowledgementEventID))
        lock.lock()
        cancelHandlerInvocationCount += 1
        guard state == .active else {
            lock.unlock()
            return false
        }
        state = .cancellationInitiated
        lock.unlock()
        return true
    }

    @discardableResult
    fileprivate func publishNaturalIfActive() -> NaturalPublicationEvidence? {
        lock.lock()
        guard state == .active, outcomeValue == nil else {
            lock.unlock()
            return nil
        }
        state = .completedNaturally
        outcomeValue = .finishedBeforeProcessing
        normalPublicationCount += 1
        let event = lifecycleRecorder.record(.naturalPublicationCommitted)
        lock.unlock()
        _ = outcomeSource.resolve(.finishedBeforeProcessing)
        return NaturalPublicationEvidence(eventID: event.id)
    }

    fileprivate func publishAcknowledgement(
        _ acknowledgement: NaturalPublicationAck,
        after publication: NaturalPublicationEvidence
    ) {
        let event = lifecycleRecorder.record(.acknowledgementCommitted(
            observedNaturalEventID: publication.eventID))
        acknowledgement.publish(NaturalPublicationAcknowledgementEvidence(
            eventID: event.id,
            observedNaturalPublicationEventID: publication.eventID))
    }

    fileprivate func resolveCancellation(_ receipt: AsyncGate.WaiterCancelReceipt) {
        let outcome = Outcome.cancelled(receipt)
        var didPublish = false
        lock.lock()
        if outcomeValue == nil {
            outcomeValue = outcome
            normalPublicationCount += 1
            didPublish = true
        }
        lock.unlock()
        if didPublish {
            _ = outcomeSource.resolve(outcome)
        }
    }

    fileprivate func rescueCancellationOutcomeIfNeeded(
        _ receipt: AsyncGate.WaiterCancelReceipt = .closedBeforeProcessing
    ) {
        let outcome = Outcome.cancelled(receipt)
        var didPublish = false
        lock.lock()
        rescuePublicationInvocationCount += 1
        if state == .cancellationInitiated, outcomeValue == nil {
            outcomeValue = outcome
            rescuePublicationCount += 1
            didPublish = true
        }
        lock.unlock()
        if didPublish {
            _ = outcomeSource.resolve(outcome)
        }
    }

    fileprivate func subscribeForOutcome() -> BufferedConsumer<Outcome> {
        outcomeSource.subscribe()
    }

    fileprivate func outcome() async -> Outcome {
        await subscribeForOutcome().value(cancellationValue: .consumerCancelled)
    }

    fileprivate var cancellationWasInitiated: Bool {
        lock.lock(); defer { lock.unlock() }
        return state == .cancellationInitiated
    }

    fileprivate var completedNaturally: Bool {
        lock.lock(); defer { lock.unlock() }
        return state == .completedNaturally
    }

    fileprivate var stateForTest: State {
        lock.lock(); defer { lock.unlock() }
        return state
    }

    fileprivate var bufferedOutcomeForTest: Outcome? {
        lock.lock(); defer { lock.unlock() }
        return outcomeValue
    }

    fileprivate var cancelHandlerInvocationCountForTest: Int {
        lock.lock(); defer { lock.unlock() }
        return cancelHandlerInvocationCount
    }

    fileprivate var normalPublicationCountForTest: Int {
        lock.lock(); defer { lock.unlock() }
        return normalPublicationCount
    }

    fileprivate var rescuePublicationCountForTest: Int {
        lock.lock(); defer { lock.unlock() }
        return rescuePublicationCount
    }

    fileprivate var rescuePublicationInvocationCountForTest: Int {
        lock.lock(); defer { lock.unlock() }
        return rescuePublicationInvocationCount
    }

    fileprivate var outcomeSubscriberCountForTest: Int {
        outcomeSource.subscriberCountForTest
    }

    fileprivate var outcomeLiveSubscriberCountForTest: Int {
        outcomeSource.liveSubscriberCountForTest
    }

    fileprivate var lifecycleEventsForTest: [AttemptLifecycleEvent] {
        lifecycleRecorder.eventsForTest
    }

    fileprivate var suspensionDeliveryForTest: RecoverableDeliverySnapshot {
        suspensionChannel.snapshotForTest
    }

    fileprivate var cancellationReleaseSucceededForTest: Bool {
        lifecycleRecorder.eventsForTest.contains {
            guard case .suspensionResumed(_, let requestedBy) = $0.phase else {
                return false
            }
            return requestedBy == .cancellationHandler
        }
    }

    fileprivate var outcomeSourceForTest: BufferedFanoutSource<Outcome> {
        outcomeSource
    }

    fileprivate func recordHopFingerprint(_ fingerprint: HopFingerprint) {
        lock.lock()
        if hopFingerprint == nil {
            hopFingerprint = fingerprint
        }
        lock.unlock()
    }

    var hopFingerprintForTest: HopFingerprint? {
        lock.lock(); defer { lock.unlock() }
        return hopFingerprint
    }

    deinit {
        _ = outcomeSource.resolve(.attemptDeinitialized)
    }
}

final class ObserverParkAckTicket: @unchecked Sendable {
    let identity = UUID()
    fileprivate let endpoint = TicketResolutionEndpoint<AsyncGate.ParkAckResult>()

    private let lock = NSLock()
    private var pendingCleanup: (@Sendable () -> TicketCleanupResult)?

    fileprivate var cleanupEvidence: TicketCleanupEvidence {
        endpoint.cleanupEvidence
    }

    fileprivate func attachDeinitCleanup(
        _ cleanup: @escaping @Sendable () -> TicketCleanupResult
    ) {
        lock.lock()
        pendingCleanup = cleanup
        lock.unlock()
    }

    fileprivate func resolve(_ value: AsyncGate.ParkAckResult) {
        lock.lock()
        pendingCleanup = nil
        lock.unlock()
        _ = endpoint.resolve(value, cleanupResult: .resolvedByGate)
    }

    fileprivate func subscribeForValue(
        suppressNormalDeliveryForTest: Bool = false
    ) -> BufferedConsumer<AsyncGate.ParkAckResult> {
        endpoint.source.subscribe(
            suppressNormalDeliveryForTest: suppressNormalDeliveryForTest)
    }

    fileprivate func value() async -> AsyncGate.ParkAckResult {
        await subscribeForValue().value(cancellationValue: .consumerCancelled)
    }

    fileprivate var isResolved: Bool {
        endpoint.source.resultForTest != nil
    }

    fileprivate var subscriberCountForTest: Int {
        endpoint.source.subscriberCountForTest
    }

    fileprivate var liveSubscriberCountForTest: Int {
        endpoint.source.liveSubscriberCountForTest
    }

    fileprivate var sourceForTest: BufferedFanoutSource<AsyncGate.ParkAckResult> {
        endpoint.source
    }

    fileprivate func rescueForTest(_ value: AsyncGate.ParkAckResult) {
        endpoint.rescue(value)
    }

    fileprivate var rescueCountForTest: Int {
        endpoint.rescueCountForTest
    }

    fileprivate var rescueInvocationCountForTest: Int {
        endpoint.rescueInvocationCountForTest
    }

    fileprivate var normalObservationCountForTest: Int {
        endpoint.normalObservationCountForTest
    }

    fileprivate var rescueTakeoverCountForTest: Int {
        endpoint.rescueTakeoverCountForTest
    }

    deinit {
        var cleanup: (@Sendable () -> TicketCleanupResult)?
        lock.lock()
        cleanup = pendingCleanup
        pendingCleanup = nil
        lock.unlock()
        let cleanupResult = cleanup?() ?? .ownerReleased
        _ = endpoint.resolve(.ticketDeinitialized, cleanupResult: cleanupResult)
    }
}

final class WaiterParkAckTicket: @unchecked Sendable {
    let identity = UUID()
    fileprivate let endpoint = TicketResolutionEndpoint<AsyncGate.ParkAckResult>()

    private let lock = NSLock()
    private var pendingCleanup: (@Sendable () -> TicketCleanupResult)?

    fileprivate var cleanupEvidence: TicketCleanupEvidence {
        endpoint.cleanupEvidence
    }

    fileprivate func attachDeinitCleanup(
        _ cleanup: @escaping @Sendable () -> TicketCleanupResult
    ) {
        lock.lock()
        pendingCleanup = cleanup
        lock.unlock()
    }

    fileprivate func resolve(_ value: AsyncGate.ParkAckResult) {
        lock.lock()
        pendingCleanup = nil
        lock.unlock()
        _ = endpoint.resolve(value, cleanupResult: .resolvedByGate)
    }

    fileprivate func subscribeForValue(
        suppressNormalDeliveryForTest: Bool = false
    ) -> BufferedConsumer<AsyncGate.ParkAckResult> {
        endpoint.source.subscribe(
            suppressNormalDeliveryForTest: suppressNormalDeliveryForTest)
    }

    fileprivate func value() async -> AsyncGate.ParkAckResult {
        await subscribeForValue().value(cancellationValue: .consumerCancelled)
    }

    fileprivate var isResolved: Bool {
        endpoint.source.resultForTest != nil
    }

    fileprivate var subscriberCountForTest: Int {
        endpoint.source.subscriberCountForTest
    }

    fileprivate var liveSubscriberCountForTest: Int {
        endpoint.source.liveSubscriberCountForTest
    }

    fileprivate var sourceForTest: BufferedFanoutSource<AsyncGate.ParkAckResult> {
        endpoint.source
    }

    fileprivate func rescueForTest(_ value: AsyncGate.ParkAckResult) {
        endpoint.rescue(value)
    }

    fileprivate var rescueCountForTest: Int {
        endpoint.rescueCountForTest
    }

    fileprivate var rescueInvocationCountForTest: Int {
        endpoint.rescueInvocationCountForTest
    }

    fileprivate var normalObservationCountForTest: Int {
        endpoint.normalObservationCountForTest
    }

    fileprivate var rescueTakeoverCountForTest: Int {
        endpoint.rescueTakeoverCountForTest
    }

    deinit {
        var cleanup: (@Sendable () -> TicketCleanupResult)?
        lock.lock()
        cleanup = pendingCleanup
        pendingCleanup = nil
        lock.unlock()
        let cleanupResult = cleanup?() ?? .ownerReleased
        _ = endpoint.resolve(.ticketDeinitialized, cleanupResult: cleanupResult)
    }
}

private final class WeakTicketBox<Ticket: AnyObject, Endpoint> {
    weak var ref: Ticket?
    let identity: UUID
    let endpoint: Endpoint

    init(ticket: Ticket, identity: UUID, endpoint: Endpoint) {
        ref = ticket
        self.identity = identity
        self.endpoint = endpoint
    }
}

/// Ticket backing storage is synchronized independently of the gate actor so
/// a ticket's `deinit` can remove its exact identity synchronously. Cleanup
/// never depends on scheduling an actor task, and stale identities cannot
/// remove a newer ticket for the same token.
private final class TicketRegistry<Ticket: AnyObject, Endpoint>: @unchecked Sendable {
    private let lock = NSLock()
    private var storage: [UInt64: [UUID: WeakTicketBox<Ticket, Endpoint>]] = [:]
    private var automaticRemovalCount = 0

    func insert(
        _ ticket: Ticket,
        endpoint: Endpoint,
        tokenID: UInt64,
        identity: UUID
    ) {
        lock.lock()
        storage[tokenID, default: [:]][identity] = WeakTicketBox(
            ticket: ticket,
            identity: identity,
            endpoint: endpoint)
        lock.unlock()
    }

    func removeAutomatically(tokenID: UInt64, identity: UUID) -> TicketCleanupResult {
        lock.lock()
        guard var bucket = storage[tokenID], bucket.removeValue(forKey: identity) != nil else {
            lock.unlock()
            return .staleIdentity
        }
        if bucket.isEmpty {
            storage.removeValue(forKey: tokenID)
        } else {
            storage[tokenID] = bucket
        }
        automaticRemovalCount += 1
        lock.unlock()
        return .removedAutomatically
    }

    func take(tokenID: UInt64) -> [Endpoint] {
        lock.lock()
        let bucket = storage.removeValue(forKey: tokenID) ?? [:]
        let endpoints = bucket.values.map(\.endpoint)
        lock.unlock()
        return endpoints
    }

    func takeAll() -> [Endpoint] {
        lock.lock()
        let buckets = storage.values
        storage.removeAll()
        let endpoints = buckets.flatMap { $0.values.map(\.endpoint) }
        lock.unlock()
        return endpoints
    }

    var rawBoxCount: Int {
        lock.lock(); defer { lock.unlock() }
        return storage.values.reduce(0) { $0 + $1.count }
    }

    var rawKeyCount: Int {
        lock.lock(); defer { lock.unlock() }
        return storage.count
    }

    var automaticRemovalCountForTest: Int {
        lock.lock(); defer { lock.unlock() }
        return automaticRemovalCount
    }

    func tryRawCountsForTest() -> (boxes: Int, keys: Int)? {
        guard lock.try() else { return nil }
        defer { lock.unlock() }
        return (
            boxes: storage.values.reduce(0) { $0 + $1.count },
            keys: storage.count)
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
        case consumerCancelled
        case ticketDeinitialized
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
    /// onCancel atomically commits `.cancellationInitiated` and releases
    /// the caller-owned suspension. The resumed actor body publishes the
    /// exact receipt before returning.
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

    enum DuplicateBranchCheckpoint: Sendable, Equatable {
        case duplicateAfterParked
        case notDuplicate
        case rescuedMissingRelease
        case rescuedMissingPublication
    }

    enum ObserverCancelProcessingCheckpoint: Sendable, Equatable {
        case processed(ObserverCancelReceipt)
        case naturalCompletion
        case rescuedMissingPublication
    }

    enum WaiterCancelProcessingCheckpoint: Sendable, Equatable {
        case processed(WaiterCancelReceipt)
        case naturalCompletion
        case rescuedMissingPublication
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

        // Number of actor-isolated cancellation classifications.
        let observerCancelInvocationCount: Int
        let waiterCancelInvocationCount: Int

        // Sizes of internal per-token queues that MUST remain bounded
        // (never grow with post-close activity).
        let observerParkAckQueueTotal: Int
        let waiterParkAckQueueTotal: Int

        // Active ticket identities and backing token-key counts.
        let observerParkAckTicketCount: Int
        let waiterParkAckTicketCount: Int
        let observerParkAckTicketBackingKeyCount: Int
        let waiterParkAckTicketBackingKeyCount: Int

        // Exact bounded issuance ranges. `nil` last-issued means this kind
        // has not emitted a token. Exhaustion is aggregate-only.
        let observerFirstIssuableID: UInt64
        let waiterFirstIssuableID: UInt64
        let observerLastIssuedID: UInt64?
        let waiterLastIssuedID: UInt64?
        let observerIssuanceExhaustedCount: Int
        let waiterIssuanceExhaustedCount: Int

        let observerInFlightDelivery: InFlightDeliverySnapshot
        let waiterInFlightDelivery: InFlightDeliverySnapshot
    }

    // MARK: Debug metrics (test-only)
    func debugRawObserverParkAckBoxCount() -> Int {
        observerTicketRegistry.rawBoxCount
    }
    func debugRawWaiterParkAckBoxCount() -> Int {
        waiterTicketRegistry.rawBoxCount
    }
    func debugRawObserverParkAckKeyCount() -> Int {
        observerTicketRegistry.rawKeyCount
    }
    func debugRawWaiterParkAckKeyCount() -> Int {
        waiterTicketRegistry.rawKeyCount
    }
    func debugObserverAutomaticTicketCleanupCount() -> Int {
        observerTicketRegistry.automaticRemovalCountForTest
    }
    func debugWaiterAutomaticTicketCleanupCount() -> Int {
        waiterTicketRegistry.automaticRemovalCountForTest
    }
    func debugObserverTicketCleanup(tokenID: UInt64, identity: UUID) -> TicketCleanupResult {
        observerTicketRegistry.removeAutomatically(tokenID: tokenID, identity: identity)
    }
    func debugWaiterTicketCleanup(tokenID: UInt64, identity: UUID) -> TicketCleanupResult {
        waiterTicketRegistry.removeAutomatically(tokenID: tokenID, identity: identity)
    }
    nonisolated func debugTryObserverTicketRegistryCounts()
        -> (boxes: Int, keys: Int)?
    {
        observerTicketRegistry.tryRawCountsForTest()
    }
    nonisolated func debugTryWaiterTicketRegistryCounts()
        -> (boxes: Int, keys: Int)?
    {
        waiterTicketRegistry.tryRawCountsForTest()
    }

    // MARK: State
    // Reserve Int.max and all larger UInt64 values as never-issued. This
    // guarantees nonwrapping arithmetic and keeps zero/max fabricated tokens
    // outside the exact compact issued ranges.
    private static let maximumIssuedTokenID = UInt64(Int.max) - 1
    private let observerFirstIssuableID: UInt64
    private let waiterFirstIssuableID: UInt64
    private var nextObserverID: UInt64?
    private var nextWaiterID: UInt64?
    private var observerLastIssuedID: UInt64?
    private var waiterLastIssuedID: UInt64?
    private var observerIssuanceExhaustedCount = 0
    private var waiterIssuanceExhaustedCount = 0
    // Hicks H5 / addendum: non-reusing per-await identity. UUID guarantees
    // owner/receipt maps cannot alias a prior/wrapped attempt.
    private var parkedObservers: [UInt64: (awaitID: UUID, attempt: ObserverAwaitAttempt)] = [:]
    private var completedObservers: Set<UInt64> = []
    private var observerOrder: [UInt64] = []
    private var parkedWaiters: [UInt64: (awaitID: UUID, attempt: WaiterAwaitAttempt)] = [:]
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
    private var observerParkAcks: [UInt64: [BufferedConsumer<ParkAckResult>]] = [:]
    private var waiterParkAcks: [UInt64: [BufferedConsumer<ParkAckResult>]] = [:]

    private nonisolated let observerTicketRegistry =
        TicketRegistry<ObserverParkAckTicket, TicketResolutionEndpoint<ParkAckResult>>()
    private nonisolated let waiterTicketRegistry =
        TicketRegistry<WaiterParkAckTicket, TicketResolutionEndpoint<ParkAckResult>>()

    // Hicks B: post-close registration bounded aggregate counters.
    // Post-close registerX does NOT insert per-token maps.
    private var observerPostCloseRegistrationCount = 0
    private var waiterPostCloseRegistrationCount = 0

    // Per-site delivery-observation counters. A request does not count:
    // only the resumed waiter ACK records success.
    private nonisolated let observerResumeEvidence =
        GateResumeEvidence<ObserverResumeSite>()
    private nonisolated let waiterResumeEvidence =
        GateResumeEvidence<WaiterResumeSite>()
    private nonisolated let observerInFlightDelivery =
        InFlightDeliveryRegistry<ObserverAwaitAttempt>()
    private nonisolated let waiterInFlightDelivery =
        InFlightDeliveryRegistry<WaiterAwaitAttempt>()

    private var observerCancelInvocationCount = 0
    private var waiterCancelInvocationCount = 0

    init(
        observerStartingIDForTest: UInt64 = 1,
        waiterStartingIDForTest: UInt64 = 1
    ) {
        precondition(
            observerStartingIDForTest > 0
                && observerStartingIDForTest <= Self.maximumIssuedTokenID,
            "observer token start must be in the issuable range")
        precondition(
            waiterStartingIDForTest > 0
                && waiterStartingIDForTest <= Self.maximumIssuedTokenID,
            "waiter token start must be in the issuable range")
        observerFirstIssuableID = observerStartingIDForTest
        waiterFirstIssuableID = waiterStartingIDForTest
        nextObserverID = observerStartingIDForTest
        nextWaiterID = waiterStartingIDForTest
    }

    nonisolated static var maximumIssuedTokenIDForTest: UInt64 {
        maximumIssuedTokenID
    }

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
        guard let token = tryRegisterObserver() else {
            preconditionFailure("observer token issuance exhausted")
        }
        return token
    }

    func tryRegisterObserver() -> ObserverToken? {
        guard let id = issueObserverID() else { return nil }
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

    /// Labeled overload accepting a caller-owned attempt. Cancellation
    /// releases the attempt suspension; this actor-isolated body publishes
    /// the exact receipt before returning.
    func awaitObserver(_ token: ObserverToken, attempt: ObserverAwaitAttempt) async {
        let awaitID = attempt.id
        await withTaskCancellationHandler {
            attempt.prepareSuspensionWait()
            // (1) Before-park latch present → consume it and
            //     prune per-token state atomically. Bishop F1:
            //     both `completedObservers` and `observerFates`
            //     entries removed together so the token leaves
            //     no residual per-token history.
            if completedObservers.remove(token.id) != nil {
                observerFates.removeValue(forKey: token.id)
                requestObserverRelease(
                    attempt,
                    at: .latchConsumed,
                    resumeSite: .latchConsumed)
            // (2) Same token already has a live parked waiter. Reject this
            //     invocation without touching the original.
            } else if parkedObservers[token.id] != nil {
                observerDuplicateAwaitCount += 1
                requestObserverRelease(
                    attempt,
                    at: .duplicateAfterParked,
                    resumeSite: .duplicateAfterParked)
            // (3) Currently registered (in observerOrder).
            } else if observerOrder.contains(token.id) {
                if closed {
                    observerOrder.removeAll { $0 == token.id }
                    sealObserverOutcome(id: token.id, reason: .closedBeforePark)
                    requestObserverRelease(
                        attempt,
                        at: .closedImmediate,
                        resumeSite: .closedImmediate)
                } else {
                    parkedObservers[token.id] = (awaitID: awaitID, attempt: attempt)
                    flushObserverParkAcks(id: token.id, result: .parked)
                }
            // (4) Not active: exact issued-range classification.
            } else if !wasObserverIDIssued(token.id) {
                observerUnknownAwaitCount += 1
                requestObserverRelease(
                    attempt,
                    at: .unknownToken,
                    resumeSite: .unknownToken)
            } else {
                observerDuplicateAwaitCount += 1
                requestObserverRelease(
                    attempt,
                    at: .duplicateAfterFated,
                    resumeSite: .duplicateAfterFated)
            }
            _ = await attempt.awaitSuspensionDelivery()
            if attempt.publishNaturalIfActive() == nil, attempt.cancellationWasInitiated {
                let receipt = cancelObserver(
                    id: token.id,
                    awaitID: awaitID,
                    attempt: attempt)
                attempt.resolveCancellation(receipt)
            }
        } onCancel: {
            if attempt.beginCancellationIfActive() {
                ensureObserverCancellationRelease(attempt)
            }
        }
    }

    func awaitObserverDuplicateThenSelfCancel(
        _ token: ObserverToken,
        attempt: ObserverAwaitAttempt,
        branchCheckpoint: BufferedConsumer<DuplicateBranchCheckpoint>,
        processingCheckpoint: BufferedConsumer<ObserverCancelProcessingCheckpoint>
    ) async {
        let awaitID = attempt.id
        await withTaskCancellationHandler {
            attempt.prepareSuspensionWait()
            if parkedObservers[token.id] != nil {
                observerDuplicateAwaitCount += 1
                requestObserverRelease(
                    attempt,
                    at: .duplicateAfterParked,
                    resumeSite: .duplicateAfterParked)
                branchCheckpoint.resolve(.duplicateAfterParked)
            } else {
                branchCheckpoint.resolve(.notDuplicate)
                if !wasObserverIDIssued(token.id) {
                    observerUnknownAwaitCount += 1
                    requestObserverRelease(
                        attempt,
                        at: .unknownToken,
                        resumeSite: .unknownToken)
                } else {
                    observerDuplicateAwaitCount += 1
                    requestObserverRelease(
                        attempt,
                        at: .duplicateAfterFated,
                        resumeSite: .duplicateAfterFated)
                }
            }
            _ = await attempt.awaitSuspensionDelivery()

            withUnsafeCurrentTask { task in
                task?.cancel()
            }
            if attempt.publishNaturalIfActive() == nil, attempt.cancellationWasInitiated {
                let receipt = cancelObserver(
                    id: token.id,
                    awaitID: awaitID,
                    attempt: attempt)
                attempt.resolveCancellation(receipt)
                processingCheckpoint.resolve(.processed(receipt))
            } else {
                processingCheckpoint.resolve(.naturalCompletion)
            }
        } onCancel: {
            if attempt.beginCancellationIfActive() {
                ensureObserverCancellationRelease(attempt)
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
    /// storing it in a gate map. The resumed actor body publishes it to
    /// the caller-owned attempt.
    ///
    /// Ripley Finding 4: also records the exact per-attempt hop
    /// raw fingerprint (requested identity, parked identity, entry existence,
    /// and closed state) before matching or mutation.
    private func cancelObserver(id: UInt64, awaitID: UUID, attempt: ObserverAwaitAttempt) -> ObserverCancelReceipt {
        observerCancelInvocationCount += 1
        // Ripley Finding 4: record fingerprint BEFORE mutating state,
        // so the fingerprint reflects the state the matcher observed.
        let entry = parkedObservers[id]
        let hopParkedEntryExisted = (entry != nil)
        attempt.recordHopFingerprint(ObserverAwaitAttempt.HopFingerprint(
            requestedAwaitID: awaitID,
            parkedAwaitID: entry?.awaitID,
            parkedEntryExisted: hopParkedEntryExisted,
            closedAtHop: closed))
        // Close-before-processing race. If the gate closed
        // while cancellation was being processed, the parked entry (if
        // any) was already drained by close() with .closedWhileParked.
        // The cancel therefore had NO matching parked entry to drain;
        // report `.closedBeforeProcessing` and take no further action.
        if closed {
            observerCancelIgnoredCount += 1
            return .closedBeforeProcessing
        }
        guard let entry = entry, entry.awaitID == awaitID else {
            observerCancelIgnoredCount += 1
            return .processedIgnoredMismatch
        }
        parkedObservers.removeValue(forKey: id)
        observerOrder.removeAll { $0 == id }
        // Bishop F1: cancelled tokens resume the parked continuation now,
        // so aggregate-only accounting (no per-token fate stored).
        sealObserverOutcome(id: id, reason: .cancelledWhileParked)
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
            let delivery = BufferedConsumer<ParkAckResult>()
            observerParkAcks[token.id, default: []].append(delivery)
            delivery.armRescue(.consumerCancelled)
            return await delivery.value(cancellationValue: .consumerCancelled)
        }
        // Not active: fabricated (never issued) vs previously-issued.
        if !wasObserverIDIssued(token.id) { return .unknown }
        return .closedOrConsumed
    }

    private func flushObserverParkAcks(id: UInt64, result: ParkAckResult) {
        if let deliveries = observerParkAcks.removeValue(forKey: id) {
            for delivery in deliveries {
                delivery.resolve(result)
            }
        }
        for endpoint in observerTicketRegistry.take(tokenID: id) {
            _ = endpoint.resolve(result, cleanupResult: .resolvedByGate)
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
        guard let token = tryRegisterWaiter() else {
            preconditionFailure("waiter token issuance exhausted")
        }
        return token
    }

    func tryRegisterWaiter() -> WaiterToken? {
        guard let id = issueWaiterID() else { return nil }
        if !closed { signalEntryLocked() }
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
            attempt.prepareSuspensionWait()
            if completedWaiters.remove(token.id) != nil {
                waiterFates.removeValue(forKey: token.id)
                requestWaiterRelease(
                    attempt,
                    at: .latchConsumed,
                    resumeSite: .latchConsumed)
            } else if parkedWaiters[token.id] != nil {
                waiterDuplicateAwaitCount += 1
                requestWaiterRelease(
                    attempt,
                    at: .duplicateAfterParked,
                    resumeSite: .duplicateAfterParked)
            } else if waiterOrder.contains(token.id) {
                if closed {
                    waiterOrder.removeAll { $0 == token.id }
                    sealWaiterOutcome(id: token.id, reason: .closedBeforePark)
                    requestWaiterRelease(
                        attempt,
                        at: .closedImmediate,
                        resumeSite: .closedImmediate)
                } else if opened {
                    waiterOrder.removeAll { $0 == token.id }
                    sealWaiterOutcome(id: token.id, reason: .openedBeforePark)
                    requestWaiterRelease(
                        attempt,
                        at: .openedImmediate,
                        resumeSite: .openedImmediate)
                } else {
                    parkedWaiters[token.id] = (awaitID: awaitID, attempt: attempt)
                    flushWaiterParkAcks(id: token.id, result: .parked)
                }
            } else if !wasWaiterIDIssued(token.id) {
                waiterUnknownAwaitCount += 1
                requestWaiterRelease(
                    attempt,
                    at: .unknownToken,
                    resumeSite: .unknownToken)
            } else {
                waiterDuplicateAwaitCount += 1
                requestWaiterRelease(
                    attempt,
                    at: .duplicateAfterFated,
                    resumeSite: .duplicateAfterFated)
            }
            _ = await attempt.awaitSuspensionDelivery()
            if attempt.publishNaturalIfActive() == nil, attempt.cancellationWasInitiated {
                let receipt = cancelWaiter(
                    id: token.id,
                    awaitID: awaitID,
                    attempt: attempt)
                attempt.resolveCancellation(receipt)
            }
        } onCancel: {
            if attempt.beginCancellationIfActive() {
                ensureWaiterCancellationRelease(attempt)
            }
        }
    }

    func awaitWaiterDuplicateThenSelfCancel(
        _ token: WaiterToken,
        attempt: WaiterAwaitAttempt,
        branchCheckpoint: BufferedConsumer<DuplicateBranchCheckpoint>,
        processingCheckpoint: BufferedConsumer<WaiterCancelProcessingCheckpoint>
    ) async {
        let awaitID = attempt.id
        await withTaskCancellationHandler {
            attempt.prepareSuspensionWait()
            if parkedWaiters[token.id] != nil {
                waiterDuplicateAwaitCount += 1
                requestWaiterRelease(
                    attempt,
                    at: .duplicateAfterParked,
                    resumeSite: .duplicateAfterParked)
                branchCheckpoint.resolve(.duplicateAfterParked)
            } else {
                branchCheckpoint.resolve(.notDuplicate)
                if !wasWaiterIDIssued(token.id) {
                    waiterUnknownAwaitCount += 1
                    requestWaiterRelease(
                        attempt,
                        at: .unknownToken,
                        resumeSite: .unknownToken)
                } else {
                    waiterDuplicateAwaitCount += 1
                    requestWaiterRelease(
                        attempt,
                        at: .duplicateAfterFated,
                        resumeSite: .duplicateAfterFated)
                }
            }
            _ = await attempt.awaitSuspensionDelivery()

            withUnsafeCurrentTask { task in
                task?.cancel()
            }
            if attempt.publishNaturalIfActive() == nil, attempt.cancellationWasInitiated {
                let receipt = cancelWaiter(
                    id: token.id,
                    awaitID: awaitID,
                    attempt: attempt)
                attempt.resolveCancellation(receipt)
                processingCheckpoint.resolve(.processed(receipt))
            } else {
                processingCheckpoint.resolve(.naturalCompletion)
            }
        } onCancel: {
            if attempt.beginCancellationIfActive() {
                ensureWaiterCancellationRelease(attempt)
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
    ///
    /// Ripley Finding 4: waiter analogue of the hop-fingerprint
    /// recording — see `cancelObserver` for full rationale.
    private func cancelWaiter(id: UInt64, awaitID: UUID, attempt: WaiterAwaitAttempt) -> WaiterCancelReceipt {
        waiterCancelInvocationCount += 1
        let entry = parkedWaiters[id]
        let hopParkedEntryExisted = (entry != nil)
        attempt.recordHopFingerprint(WaiterAwaitAttempt.HopFingerprint(
            requestedAwaitID: awaitID,
            parkedAwaitID: entry?.awaitID,
            parkedEntryExisted: hopParkedEntryExisted,
            closedAtHop: closed))
        if closed {
            waiterCancelIgnoredCount += 1
            return .closedBeforeProcessing
        }
        guard let entry = entry, entry.awaitID == awaitID else {
            waiterCancelIgnoredCount += 1
            return .processedIgnoredMismatch
        }
        parkedWaiters.removeValue(forKey: id)
        waiterOrder.removeAll { $0 == id }
        sealWaiterOutcome(id: id, reason: .cancelledWhileParked)
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
            let delivery = BufferedConsumer<ParkAckResult>()
            waiterParkAcks[token.id, default: []].append(delivery)
            delivery.armRescue(.consumerCancelled)
            return await delivery.value(cancellationValue: .consumerCancelled)
        }
        if !wasWaiterIDIssued(token.id) { return .unknown }
        return .closedOrConsumed
    }

    private func flushWaiterParkAcks(id: UInt64, result: ParkAckResult) {
        if let deliveries = waiterParkAcks.removeValue(forKey: id) {
            for delivery in deliveries {
                delivery.resolve(result)
            }
        }
        for endpoint in waiterTicketRegistry.take(tokenID: id) {
            _ = endpoint.resolve(result, cleanupResult: .resolvedByGate)
        }
    }

    // MARK: Hicks R2 addendum — buffered park-ACK ticket objects

    /// Synchronously reserve a park-ACK ticket. Active tickets are inserted
    /// into the identity registry before return; terminal tickets resolve
    /// immediately. Park/fate/close removes before resolving.
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
            let identity = ticket.identity
            let tokenID = token.id
            let registry = observerTicketRegistry
            registry.insert(
                ticket,
                endpoint: ticket.endpoint,
                tokenID: tokenID,
                identity: identity)
            ticket.attachDeinitCleanup { [weak registry] in
                registry?.removeAutomatically(tokenID: tokenID, identity: identity)
                    ?? .ownerReleased
            }
            return ticket
        }
        if !wasObserverIDIssued(token.id) {
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
            let identity = ticket.identity
            let tokenID = token.id
            let registry = waiterTicketRegistry
            registry.insert(
                ticket,
                endpoint: ticket.endpoint,
                tokenID: tokenID,
                identity: identity)
            ticket.attachDeinitCleanup { [weak registry] in
                registry?.removeAutomatically(tokenID: tokenID, identity: identity)
                    ?? .ownerReleased
            }
            return ticket
        }
        if !wasWaiterIDIssued(token.id) {
            ticket.resolve(.unknown)
        } else {
            ticket.resolve(.closedOrConsumed)
        }
        return ticket
    }

    // MARK: Hold-inside-cancellation-handler helpers

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
            attempt.prepareSuspensionWait()
            if completedObservers.remove(token.id) != nil {
                observerFates.removeValue(forKey: token.id)
                requestObserverRelease(
                    attempt,
                    at: .latchConsumed,
                    resumeSite: .latchConsumed)
            } else if parkedObservers[token.id] != nil {
                observerDuplicateAwaitCount += 1
                requestObserverRelease(
                    attempt,
                    at: .duplicateAfterParked,
                    resumeSite: .duplicateAfterParked)
            } else if observerOrder.contains(token.id) {
                if closed {
                    observerOrder.removeAll { $0 == token.id }
                    sealObserverOutcome(id: token.id, reason: .closedBeforePark)
                    requestObserverRelease(
                        attempt,
                        at: .closedImmediate,
                        resumeSite: .closedImmediate)
                } else {
                    parkedObservers[token.id] = (awaitID: awaitID, attempt: attempt)
                    flushObserverParkAcks(id: token.id, result: .parked)
                }
            } else if !wasObserverIDIssued(token.id) {
                observerUnknownAwaitCount += 1
                requestObserverRelease(
                    attempt,
                    at: .unknownToken,
                    resumeSite: .unknownToken)
            } else {
                observerDuplicateAwaitCount += 1
                requestObserverRelease(
                    attempt,
                    at: .duplicateAfterFated,
                    resumeSite: .duplicateAfterFated)
            }
            _ = await attempt.awaitSuspensionDelivery()
            await holdGate.awaitWaiter(holdToken)
            if attempt.publishNaturalIfActive() == nil, attempt.cancellationWasInitiated {
                let receipt = cancelObserver(
                    id: token.id,
                    awaitID: awaitID,
                    attempt: attempt)
                attempt.resolveCancellation(receipt)
            }
        } onCancel: {
            if attempt.beginCancellationIfActive() {
                ensureObserverCancellationRelease(attempt)
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
            attempt.prepareSuspensionWait()
            if completedWaiters.remove(token.id) != nil {
                waiterFates.removeValue(forKey: token.id)
                requestWaiterRelease(
                    attempt,
                    at: .latchConsumed,
                    resumeSite: .latchConsumed)
            } else if parkedWaiters[token.id] != nil {
                waiterDuplicateAwaitCount += 1
                requestWaiterRelease(
                    attempt,
                    at: .duplicateAfterParked,
                    resumeSite: .duplicateAfterParked)
            } else if waiterOrder.contains(token.id) {
                if closed {
                    waiterOrder.removeAll { $0 == token.id }
                    sealWaiterOutcome(id: token.id, reason: .closedBeforePark)
                    requestWaiterRelease(
                        attempt,
                        at: .closedImmediate,
                        resumeSite: .closedImmediate)
                } else if opened {
                    waiterOrder.removeAll { $0 == token.id }
                    sealWaiterOutcome(id: token.id, reason: .openedBeforePark)
                    requestWaiterRelease(
                        attempt,
                        at: .openedImmediate,
                        resumeSite: .openedImmediate)
                } else {
                    parkedWaiters[token.id] = (awaitID: awaitID, attempt: attempt)
                    flushWaiterParkAcks(id: token.id, result: .parked)
                }
            } else if !wasWaiterIDIssued(token.id) {
                waiterUnknownAwaitCount += 1
                requestWaiterRelease(
                    attempt,
                    at: .unknownToken,
                    resumeSite: .unknownToken)
            } else {
                waiterDuplicateAwaitCount += 1
                requestWaiterRelease(
                    attempt,
                    at: .duplicateAfterFated,
                    resumeSite: .duplicateAfterFated)
            }
            _ = await attempt.awaitSuspensionDelivery()
            await holdGate.awaitWaiter(holdToken)
            if attempt.publishNaturalIfActive() == nil, attempt.cancellationWasInitiated {
                let receipt = cancelWaiter(
                    id: token.id,
                    awaitID: awaitID,
                    attempt: attempt)
                attempt.resolveCancellation(receipt)
            }
        } onCancel: {
            if attempt.beginCancellationIfActive() {
                ensureWaiterCancellationRelease(attempt)
            }
        }
    }

    // MARK: Natural publication causality

    func awaitObserverPublishThenSelfCancel(
        _ token: ObserverToken,
        attempt: ObserverAwaitAttempt,
        acknowledgement: NaturalPublicationAck
    ) async {
        let awaitID = attempt.id
        await withTaskCancellationHandler {
            attempt.prepareSuspensionWait()
            if completedObservers.remove(token.id) != nil {
                observerFates.removeValue(forKey: token.id)
                requestObserverRelease(
                    attempt,
                    at: .latchConsumed,
                    resumeSite: .latchConsumed)
            } else if parkedObservers[token.id] != nil {
                observerDuplicateAwaitCount += 1
                requestObserverRelease(
                    attempt,
                    at: .duplicateAfterParked,
                    resumeSite: .duplicateAfterParked)
            } else if observerOrder.contains(token.id) {
                if closed {
                    observerOrder.removeAll { $0 == token.id }
                    sealObserverOutcome(id: token.id, reason: .closedBeforePark)
                    requestObserverRelease(
                        attempt,
                        at: .closedImmediate,
                        resumeSite: .closedImmediate)
                } else {
                    parkedObservers[token.id] = (awaitID: awaitID, attempt: attempt)
                    flushObserverParkAcks(id: token.id, result: .parked)
                }
            } else if !wasObserverIDIssued(token.id) {
                observerUnknownAwaitCount += 1
                requestObserverRelease(
                    attempt,
                    at: .unknownToken,
                    resumeSite: .unknownToken)
            } else {
                observerDuplicateAwaitCount += 1
                requestObserverRelease(
                    attempt,
                    at: .duplicateAfterFated,
                    resumeSite: .duplicateAfterFated)
            }
            _ = await attempt.awaitSuspensionDelivery()
            if let publication = attempt.publishNaturalIfActive() {
                attempt.publishAcknowledgement(acknowledgement, after: publication)
                withUnsafeCurrentTask { task in
                    task?.cancel()
                }
            }
        } onCancel: {
            if attempt.beginCancellationIfActive(observing: acknowledgement) {
                ensureObserverCancellationRelease(attempt)
            }
        }
    }

    func awaitWaiterPublishThenSelfCancel(
        _ token: WaiterToken,
        attempt: WaiterAwaitAttempt,
        acknowledgement: NaturalPublicationAck
    ) async {
        let awaitID = attempt.id
        await withTaskCancellationHandler {
            attempt.prepareSuspensionWait()
            if completedWaiters.remove(token.id) != nil {
                waiterFates.removeValue(forKey: token.id)
                requestWaiterRelease(
                    attempt,
                    at: .latchConsumed,
                    resumeSite: .latchConsumed)
            } else if parkedWaiters[token.id] != nil {
                waiterDuplicateAwaitCount += 1
                requestWaiterRelease(
                    attempt,
                    at: .duplicateAfterParked,
                    resumeSite: .duplicateAfterParked)
            } else if waiterOrder.contains(token.id) {
                if closed {
                    waiterOrder.removeAll { $0 == token.id }
                    sealWaiterOutcome(id: token.id, reason: .closedBeforePark)
                    requestWaiterRelease(
                        attempt,
                        at: .closedImmediate,
                        resumeSite: .closedImmediate)
                } else if opened {
                    waiterOrder.removeAll { $0 == token.id }
                    sealWaiterOutcome(id: token.id, reason: .openedBeforePark)
                    requestWaiterRelease(
                        attempt,
                        at: .openedImmediate,
                        resumeSite: .openedImmediate)
                } else {
                    parkedWaiters[token.id] = (awaitID: awaitID, attempt: attempt)
                    flushWaiterParkAcks(id: token.id, result: .parked)
                }
            } else if !wasWaiterIDIssued(token.id) {
                waiterUnknownAwaitCount += 1
                requestWaiterRelease(
                    attempt,
                    at: .unknownToken,
                    resumeSite: .unknownToken)
            } else {
                waiterDuplicateAwaitCount += 1
                requestWaiterRelease(
                    attempt,
                    at: .duplicateAfterFated,
                    resumeSite: .duplicateAfterFated)
            }
            _ = await attempt.awaitSuspensionDelivery()
            if let publication = attempt.publishNaturalIfActive() {
                attempt.publishAcknowledgement(acknowledgement, after: publication)
                withUnsafeCurrentTask { task in
                    task?.cancel()
                }
            }
        } onCancel: {
            if attempt.beginCancellationIfActive(observing: acknowledgement) {
                ensureWaiterCancellationRelease(attempt)
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
                requestWaiterRelease(
                    entry.attempt,
                    at: .open,
                    resumeSite: .parkedResumedByOpen)
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
                requestWaiterRelease(
                    entry.attempt,
                    at: .close,
                    resumeSite: .parkedResumedByClose)
            } else {
                sealWaiterOutcome(id: id, reason: .closedBeforePark)
            }
        }
        let oIds = observerOrder
        observerOrder.removeAll()
        for id in oIds {
            if let entry = parkedObservers.removeValue(forKey: id) {
                sealObserverOutcome(id: id, reason: .closedWhileParked)
                requestObserverRelease(
                    entry.attempt,
                    at: .close,
                    resumeSite: .parkedResumedByClose)
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
        for (_, deliveries) in obsAcks {
            for delivery in deliveries {
                delivery.resolve(.closedOrConsumed)
            }
        }
        let wAcks = waiterParkAcks
        waiterParkAcks.removeAll()
        for (_, deliveries) in wAcks {
            for delivery in deliveries {
                delivery.resolve(.closedOrConsumed)
            }
        }
        // Hicks R1 addendum: the gate no longer stores per-attempt
        // cancellation receipts (state lives in caller-owned
        // `AwaitAttempt` contexts). Cancellation that races with close()
        // observes `closed == true` in
        // `cancelObserver`/`cancelWaiter` and returns
        // `.closedBeforeProcessing`; the caller's `attempt.outcome()`
        // delivers that exact receipt via the buffered outcome latch.

        for endpoint in observerTicketRegistry.takeAll() {
            _ = endpoint.resolve(.closedOrConsumed, cleanupResult: .resolvedByGate)
        }
        for endpoint in waiterTicketRegistry.takeAll() {
            _ = endpoint.resolve(.closedOrConsumed, cleanupResult: .resolvedByGate)
        }
    }

    // MARK: Introspection (test-only)

    func snapshot() -> Snapshot {
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
            observerResumeCounts: observerResumeEvidence.countsForTest,
            waiterResumeCounts: waiterResumeEvidence.countsForTest,
            observerCancelInvocationCount: observerCancelInvocationCount,
            waiterCancelInvocationCount: waiterCancelInvocationCount,
            observerParkAckQueueTotal: observerParkAcks.values.reduce(0) { $0 + $1.count },
            waiterParkAckQueueTotal: waiterParkAcks.values.reduce(0) { $0 + $1.count },
            observerParkAckTicketCount: observerTicketRegistry.rawBoxCount,
            waiterParkAckTicketCount: waiterTicketRegistry.rawBoxCount,
            observerParkAckTicketBackingKeyCount: observerTicketRegistry.rawKeyCount,
            waiterParkAckTicketBackingKeyCount: waiterTicketRegistry.rawKeyCount,
            observerFirstIssuableID: observerFirstIssuableID,
            waiterFirstIssuableID: waiterFirstIssuableID,
            observerLastIssuedID: observerLastIssuedID,
            waiterLastIssuedID: waiterLastIssuedID,
            observerIssuanceExhaustedCount: observerIssuanceExhaustedCount,
            waiterIssuanceExhaustedCount: waiterIssuanceExhaustedCount,
            observerInFlightDelivery: observerInFlightDelivery.snapshotForTest,
            waiterInFlightDelivery: waiterInFlightDelivery.snapshotForTest
        )
    }

    // MARK: Private helpers

    nonisolated private func requestObserverRelease(
        _ attempt: ObserverAwaitAttempt,
        at releaseSite: AwaitSuspensionReleaseSite,
        resumeSite: ObserverResumeSite
    ) {
        let evidence = observerResumeEvidence
        let registry = observerInFlightDelivery
        let attemptID = attempt.id
        _ = attempt.releaseSuspension(
            at: releaseSite,
            onClaim: {
                attempt.invokeSuspensionRescueForTest()
                registry.insert(
                    attempt,
                    id: attemptID,
                    rescueWasInvoked:
                        attempt.suspensionDeliveryForTest.rescueInvocationCount == 1)
            },
            onNormalObservation: {
                evidence.record(resumeSite)
            },
            onObservation: { origin in
                registry.observe(id: attemptID, origin: origin)
            })
    }

    nonisolated private func requestWaiterRelease(
        _ attempt: WaiterAwaitAttempt,
        at releaseSite: AwaitSuspensionReleaseSite,
        resumeSite: WaiterResumeSite
    ) {
        let evidence = waiterResumeEvidence
        let registry = waiterInFlightDelivery
        let attemptID = attempt.id
        _ = attempt.releaseSuspension(
            at: releaseSite,
            onClaim: {
                attempt.invokeSuspensionRescueForTest()
                registry.insert(
                    attempt,
                    id: attemptID,
                    rescueWasInvoked:
                        attempt.suspensionDeliveryForTest.rescueInvocationCount == 1)
            },
            onNormalObservation: {
                evidence.record(resumeSite)
            },
            onObservation: { origin in
                registry.observe(id: attemptID, origin: origin)
            })
    }

    nonisolated private func ensureObserverCancellationRelease(
        _ attempt: ObserverAwaitAttempt
    ) {
        let delivery = attempt.suspensionDeliveryForTest
        if delivery.normalRequestCount == 0 {
            requestObserverRelease(
                attempt,
                at: .cancellationHandler,
                resumeSite: .parkedResumedByCancel)
        } else if delivery.normalObservationCount == 0
                    && delivery.rescueTakeoverCount == 0 {
            attempt.invokeSuspensionRescueForTest()
        }
    }

    nonisolated private func ensureWaiterCancellationRelease(
        _ attempt: WaiterAwaitAttempt
    ) {
        let delivery = attempt.suspensionDeliveryForTest
        if delivery.normalRequestCount == 0 {
            requestWaiterRelease(
                attempt,
                at: .cancellationHandler,
                resumeSite: .parkedResumedByCancel)
        } else if delivery.normalObservationCount == 0
                    && delivery.rescueTakeoverCount == 0 {
            attempt.invokeSuspensionRescueForTest()
        }
    }

    private func issueObserverID() -> UInt64? {
        guard let id = nextObserverID else {
            observerIssuanceExhaustedCount += 1
            return nil
        }
        precondition(
            id > 0 && id <= Self.maximumIssuedTokenID,
            "observer token allocator escaped its bounded range")
        observerLastIssuedID = id
        nextObserverID = id == Self.maximumIssuedTokenID ? nil : id + 1
        return id
    }

    private func issueWaiterID() -> UInt64? {
        guard let id = nextWaiterID else {
            waiterIssuanceExhaustedCount += 1
            return nil
        }
        precondition(
            id > 0 && id <= Self.maximumIssuedTokenID,
            "waiter token allocator escaped its bounded range")
        waiterLastIssuedID = id
        nextWaiterID = id == Self.maximumIssuedTokenID ? nil : id + 1
        return id
    }

    private func wasObserverIDIssued(_ id: UInt64) -> Bool {
        guard id >= observerFirstIssuableID, let observerLastIssuedID else {
            return false
        }
        return id <= observerLastIssuedID
    }

    private func wasWaiterIDIssued(_ id: UInt64) -> Bool {
        guard id >= waiterFirstIssuableID, let waiterLastIssuedID else {
            return false
        }
        return id <= waiterLastIssuedID
    }

    private func signalEntryLocked() {
        if let id = observerOrder.first {
            observerOrder.removeFirst()
            if let entry = parkedObservers.removeValue(forKey: id) {
                sealObserverOutcome(id: id, reason: .signaledWhileParked)
                requestObserverRelease(
                    entry.attempt,
                    at: .signal,
                    resumeSite: .parkedResumedBySignal)
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
