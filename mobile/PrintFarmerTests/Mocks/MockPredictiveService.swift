import Foundation
@testable import PrintFarmer

final class MockPredictiveService: PredictiveServiceProtocol, @unchecked Sendable {
    var predictionToReturn: JobFailurePrediction?
    var forecastsToReturn: [MaintenanceForecast] = []
    var alertsToReturn: [PredictiveAlert] = []
    var errorToThrow: Error?

    /// Optional async hook awaited immediately before `predictJobFailure`
    /// returns/throws. Lets tests deterministically hold a request in flight
    /// while starting a second request, without sleeps or timing tricks.
    var beforeReturnHook: (@Sendable () async -> Void)?

    // Call tracking
    var predictJobFailureCalledWith: PredictionRequest?
    var getMaintenanceForecastCalledWith: Int?
    var getActiveAlertsCalled = false
    var getActiveAlertsCalledWithPrinterId: UUID?
    var getMaintenanceForecastCalledWithPrinterId: UUID?

    func predictJobFailure(request: PredictionRequest) async throws -> JobFailurePrediction? {
        predictJobFailureCalledWith = request
        if let hook = beforeReturnHook {
            await hook()
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
