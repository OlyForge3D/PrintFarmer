import Foundation
@testable import PrintFarmer

/// Deterministic scripted outcome for a single `predictJobFailure` call.
/// The mock snapshots one of these BEFORE awaiting `beforeReturnHook`, so
/// interleaved tests cannot reach the outcome via later mutations of the
/// mock's return field. Used to make the late-completion test non-vacuous.
enum PredictiveScriptedOutcome: @unchecked Sendable {
    case success(JobFailurePrediction?)
    case failure(Error)
}

/// Actor-isolated call state for `MockPredictiveService`. Enforces
/// per-invocation snapshotting of the scripted outcome and records the
/// full request history / call count so tests can assert exact request
/// equality and exactly-once retry semantics.
actor PredictiveMockCallState {
    private var script: [PredictiveScriptedOutcome] = []
    private var history: [PredictionRequest] = []
    private var snapshots: [PredictiveScriptedOutcome] = []

    func enqueue(_ outcome: PredictiveScriptedOutcome) {
        script.append(outcome)
    }

    /// Record the request and return the scripted outcome for THIS
    /// invocation (or nil if the caller should fall back to the legacy
    /// single-outcome fields). The snapshot is taken before any suspension
    /// point so subsequent script mutations cannot alter what this call
    /// eventually returns.
    func consume(request: PredictionRequest) -> PredictiveScriptedOutcome? {
        history.append(request)
        guard !script.isEmpty else { return nil }
        let outcome = script.removeFirst()
        snapshots.append(outcome)
        return outcome
    }

    var callHistory: [PredictionRequest] { history }
    var callCount: Int { history.count }
    var recordedSnapshots: [PredictiveScriptedOutcome] { snapshots }
    var remainingScript: Int { script.count }
}

final class MockPredictiveService: PredictiveServiceProtocol, @unchecked Sendable {
    var predictionToReturn: JobFailurePrediction?
    var forecastsToReturn: [MaintenanceForecast] = []
    var alertsToReturn: [PredictiveAlert] = []
    var errorToThrow: Error?

    /// Optional async hook awaited immediately after the per-invocation
    /// scripted outcome is snapshotted and before the mock returns/throws.
    /// Lets tests deterministically hold a request in flight while a
    /// second request is issued, without sleeps or timing tricks.
    var beforeReturnHook: (@Sendable () async -> Void)?

    /// Actor-isolated scripted call state. When the script is empty the
    /// mock falls back to the legacy `predictionToReturn`/`errorToThrow`
    /// fields so pre-existing tests that don't script outcomes continue
    /// to work unchanged.
    let callState = PredictiveMockCallState()

    // Call tracking (legacy; retained for existing tests)
    var predictJobFailureCalledWith: PredictionRequest?
    var getMaintenanceForecastCalledWith: Int?
    var getActiveAlertsCalled = false
    var getActiveAlertsCalledWithPrinterId: UUID?
    var getMaintenanceForecastCalledWithPrinterId: UUID?

    func predictJobFailure(request: PredictionRequest) async throws -> JobFailurePrediction? {
        predictJobFailureCalledWith = request
        // Snapshot the outcome for THIS call before any suspension point,
        // so later mutations of the script or the legacy fields cannot
        // change what this specific invocation returns.
        let scripted = await callState.consume(request: request)

        if let hook = beforeReturnHook {
            await hook()
        }

        if let scripted {
            switch scripted {
            case .success(let prediction):
                return prediction
            case .failure(let error):
                throw error
            }
        }

        if let error = errorToThrow { throw error }
        return predictionToReturn
    }
    
    func getMaintenanceForecast(days: Int? = nil, printerId: UUID? = nil) async throws -> [MaintenanceForecast] {
        getMaintenanceForecastCalledWith = days
        getMaintenanceForecastCalledWithPrinterId = printerId
        if let error = errorToThrow { throw error }
        return forecastsToReturn
    }
    
    func getActiveAlerts(printerId: UUID? = nil) async throws -> [PredictiveAlert] {
        getActiveAlertsCalled = true
        getActiveAlertsCalledWithPrinterId = printerId
        if let error = errorToThrow { throw error }
        return alertsToReturn
    }
}
