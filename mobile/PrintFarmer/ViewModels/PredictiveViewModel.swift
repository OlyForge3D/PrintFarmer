import Foundation
import os

/// One canonical state for the primary predictive-failure request.
///
/// The view and tests should read this instead of inferring status from
/// `prediction == nil`, which cannot distinguish "backend returned no
/// prediction" from "the request failed".
enum PredictiveLoadState: Equatable {
    case idle
    case loading
    case success
    case failed(String)
}

@MainActor @Observable
final class PredictiveViewModel {
    /// User-facing copy shown when the primary prediction request fails.
    /// Kept as a static constant so tests can assert exactly what the view
    /// presents.
    static let failureMessage = "We couldn't load predictive data. Tap Retry to try again."
    static let farmWideFailureMessage =
        "We couldn't load farm-wide predictive alerts and forecasts. Tap Retry to try again."

    var prediction: JobFailurePrediction?
    var alerts: [PredictiveAlert] = []
    var forecasts: [MaintenanceForecast] = []
    var isLoading = false
    var error: String?
    var isViewActive = true

    /// Status of the most recent primary prediction request.
    var predictionStatus: PredictiveLoadState = .idle
    var farmWideStatus: PredictiveLoadState = .idle

    private let logger = Logger(subsystem: "com.printfarmer.ios", category: "Predictive")
    private var predictiveService: (any PredictiveServiceProtocol)?

    /// Canonical request for `retryPrediction()` — captured on every
    /// `predictFailure` call so retry re-issues the exact same call.
    private var lastPredictionRequest: PredictionRequest?

    /// Monotonic counter guarding against a slow prior request writing
    /// results back after a retry has superseded it.
    private var predictionGeneration: UInt64 = 0
    private var farmWideGeneration: UInt64 = 0

    func configure(predictiveService: any PredictiveServiceProtocol) {
        self.predictiveService = predictiveService
    }

    func predictFailure(printerId: UUID, material: String?, duration: TimeInterval?) async {
        let request = PredictionRequest(
            printerId: printerId,
            material: material,
            estimatedDurationSeconds: duration.map { Int($0) }
        )
        await performPrediction(request: request)
    }

    /// Re-runs the last primary prediction request. Exactly one canonical
    /// request per invocation; no-op if `predictFailure` has never been
    /// called (nothing to retry).
    func retryPrediction() async {
        guard let request = lastPredictionRequest else { return }
        await performPrediction(request: request)
    }

    private func performPrediction(request: PredictionRequest) async {
        guard let predictiveService, isViewActive else { return }

        lastPredictionRequest = request
        predictionGeneration &+= 1
        let generation = predictionGeneration

        isLoading = true
        error = nil
        predictionStatus = .loading
        defer {
            if generation == predictionGeneration {
                isLoading = false
            }
        }

        do {
            let result = try await predictiveService.predictJobFailure(request: request)
            // Drop late completions from superseded requests so a slow prior
            // call cannot overwrite a newer retry's success or failure.
            guard isViewActive, generation == predictionGeneration else { return }
            prediction = result
            predictionStatus = .success
            error = nil
        } catch {
            guard isViewActive, generation == predictionGeneration else { return }
            logger.warning("Failed to predict failure: \(error.localizedDescription)")
            // Deliberate stale-data policy: preserve any previously
            // successful `prediction` so the operator continues to see the
            // last-known risk context alongside the failure banner. If we
            // never had a successful load, `prediction` stays nil and the
            // view shows an explicit "unavailable" state — never a benign
            // low-risk result.
            self.error = Self.failureMessage
            predictionStatus = .failed(Self.failureMessage)
        }

    }

    func loadAlerts(printerId: UUID?) async {
        guard let predictiveService, isViewActive else { return }
        do {
            let result = try await predictiveService.getActiveAlerts(printerId: printerId)
            guard isViewActive else { return }
            alerts = result
        } catch {
            logger.warning("Failed to load predictive alerts: \(error.localizedDescription)")
        }
    }

    func loadForecasts(printerId: UUID?) async {
        guard let predictiveService, isViewActive else { return }
        do {
            let result = try await predictiveService.getMaintenanceForecast(days: 30, printerId: printerId)
            guard isViewActive else { return }
            forecasts = result
        } catch {
            logger.warning("Failed to load forecasts: \(error.localizedDescription)")
        }
    }

    func loadFarmWideInsights() async {
        guard let predictiveService, isViewActive else { return }

        farmWideGeneration &+= 1
        let generation = farmWideGeneration
        farmWideStatus = .loading

        do {
            let loadedAlerts = try await predictiveService.getActiveAlerts(printerId: nil)
            let loadedForecasts = try await predictiveService.getMaintenanceForecast(
                days: 30,
                printerId: nil
            )
            guard isViewActive, generation == farmWideGeneration else { return }
            alerts = loadedAlerts
            forecasts = loadedForecasts
            farmWideStatus = .success
        } catch {
            guard isViewActive, generation == farmWideGeneration else { return }
            logger.warning(
                "Failed to load farm-wide predictive insights: \(error.localizedDescription)"
            )
            farmWideStatus = .failed(Self.farmWideFailureMessage)
        }
    }

    func retryFarmWideInsights() async {
        await loadFarmWideInsights()
    }

    var hasFarmWideData: Bool {
        !alerts.isEmpty || !forecasts.isEmpty
    }

    var isRefreshingFarmWideInsights: Bool {
        farmWideStatus == .loading && hasFarmWideData
    }

    // MARK: - Computed

    /// True when the model has a `prediction` whose
    /// `predictedFailureLikelihood` is a real number. A `prediction` with
    /// nil likelihood previously fell through `Int(nil ?? 0)` = 0 and
    /// rendered as a green "Low" gauge, which is the same #808 hazard the
    /// unavailable-prediction fix addressed. Callers must guard the risk
    /// gauge on this flag rather than on `prediction != nil` alone.
    var hasKnownLikelihood: Bool {
        prediction?.predictedFailureLikelihood != nil
    }

    /// True when a refresh (retry or in-place reload) is in flight while a
    /// prior successful `prediction` is still visible. The view uses this
    /// to render the gauge in a visibly stale/refreshing state so the
    /// operator never mistakes the last-known values for a fresh reading.
    var isRefreshingStalePrediction: Bool {
        predictionStatus == .loading && prediction != nil
    }

    var riskPercentage: Int {
        Int(prediction?.predictedFailureLikelihood ?? 0)
    }

    var riskLevel: String {
        // When there is no `prediction`, OR the prediction is present but
        // carries no `predictedFailureLikelihood`, the previous `switch`
        // fell through to the `0..<25` bucket and returned "Low", which
        // allowed transport, decoding, and partial-response failures to
        // render as a benign low-risk result (issue #808). "Unavailable"
        // makes the missing-data case explicit to any caller and keeps a
        // stale prior success rendering its real level.
        guard hasKnownLikelihood else { return "Unavailable" }
        switch riskPercentage {
        case 0..<25: return "Low"
        case 25..<50: return "Moderate"
        case 50..<75: return "High"
        default: return "Critical"
        }
    }
}
