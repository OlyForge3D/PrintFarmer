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

    /// Direct proof: wait() before waitForEntry() must be observed without
    /// polling (lost-wakeup safe via pendingEntrySignals).
    func testAsyncGateWaitBeforeEntryObserverIsObserved() async {
        let gate = AsyncGate()
        async let blocker: Void = gate.wait()

        // Give the blocker a chance to enqueue its entry signal by
        // observing the entry synchronously afterwards; this must return
        // deterministically whether the entry happened before or after
        // this call arrives at the actor.
        await gate.waitForEntry()

        await gate.open()
        await blocker
    }

    /// Direct proof: waitForEntry() before wait() must suspend and then
    /// resume exactly once when the first wait() reaches the gate.
    func testAsyncGateEntryObserverBeforeWaitResumesOnce() async {
        let gate = AsyncGate()

        async let entryObserved: Void = gate.waitForEntry()

        // Drive an arrival at the gate. `wait()` will suspend (opened is
        // false) — but the entry observer must have been resumed as part
        // of that arrival, so `entryObserved` completes even though the
        // waiter is still blocked.
        async let blocker: Void = gate.wait()

        await entryObserved

        await gate.open()
        await blocker
    }

    /// Direct proof: open-before-wait still emits an entry signal so a
    /// later `waitForEntry()` observer is not stranded, and repeated
    /// `open()` is a safe no-op.
    func testAsyncGateOpenBeforeWaitStillSignalsEntry() async {
        let gate = AsyncGate()
        await gate.open()
        await gate.open()   // idempotent — must not crash or double-resume.

        // `wait()` returns immediately because the gate is open, but must
        // still emit an entry signal so the observer proceeds.
        await gate.wait()
        await gate.waitForEntry()
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

/// Deterministic suspension gate used to hold a request in flight across a
/// real suspension point (never `Task.sleep`/yield). Extended from the
/// polling helper originally used in `HarvestViewModelTests` with an
/// explicit lost-wakeup-safe entry handshake so tests can synchronise on
/// "the `wait()` caller has reached the gate" without polling loops.
///
/// Ordering guarantees (all handled by actor serialisation):
///   * open-before-wait — `wait()` returns immediately AND still signals
///     an entry, so any observer waiting on entry is not stranded.
///   * wait-before-entry-observer — `wait()` bumps `pendingEntrySignals`;
///     a later `waitForEntry()` consumes exactly one and returns.
///   * entry-observer-before-wait — `waitForEntry()` parks a continuation;
///     the next `wait()` resumes exactly that continuation.
///   * repeated open — `opened` latches true; second `open()` drains an
///     empty waiter list and is a no-op.
///   * teardown/early assertion — no pollers to leak; unresumed observer
///     continuations are released when the actor is deallocated.
///   * exactly-once continuation resume — waiters are drained to a local
///     copy before resume; entry observers are removed from their queue
///     before resume.
private actor AsyncGate {
    private var waiters: [CheckedContinuation<Void, Never>] = []
    private var entryObservers: [CheckedContinuation<Void, Never>] = []
    private var pendingEntrySignals = 0
    private var opened = false

    /// Suspends the caller until `open()` is invoked. Regardless of whether
    /// the gate is already open, an entry signal is emitted so observers
    /// can deterministically synchronise on "wait() reached the gate".
    func wait() async {
        signalEntryLocked()
        if opened { return }
        await withCheckedContinuation { c in
            waiters.append(c)
        }
    }

    /// Lost-wakeup-safe entry handshake. If one or more entries have
    /// already occurred since the last observation, returns immediately by
    /// consuming exactly one pending signal. Otherwise suspends until the
    /// next entry signal. Exactly-once resume is guaranteed because the
    /// installed continuation is removed from `entryObservers` before
    /// `resume()` is called.
    func waitForEntry() async {
        if pendingEntrySignals > 0 {
            pendingEntrySignals -= 1
            return
        }
        await withCheckedContinuation { c in
            entryObservers.append(c)
        }
    }

    func open() {
        opened = true
        let toResume = waiters
        waiters.removeAll()
        for c in toResume { c.resume() }
    }

    private func signalEntryLocked() {
        if entryObservers.isEmpty {
            pendingEntrySignals += 1
        } else {
            let observer = entryObservers.removeFirst()
            observer.resume()
        }
    }
}
