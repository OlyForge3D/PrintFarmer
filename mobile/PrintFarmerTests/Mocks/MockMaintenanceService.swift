import Foundation
@testable import PrintFarmer

final class MockMaintenanceService: MaintenanceServiceProtocol, @unchecked Sendable {
    var alertsToReturn: [MaintenanceAlert] = []
    var alertToReturn: MaintenanceAlert?
    var resolveAlertResponse: ResolveAlertResponse?
    var upcomingTasksToReturn: [UpcomingMaintenanceTask] = []
    var trendsToReturn: [MaintenanceTrend] = []
    var lifespansToReturn: [ComponentLifespan] = []
    var costsToReturn: [MaintenanceCost] = []
    var uptimeToReturn: [PrinterUptime] = []
    var fleetStatsToReturn: [FleetPrinterStatistics] = []
    var errorToThrow: Error?
    
    // Call tracking
    var getAlertsCalled = false
    var getAlertsForPrinterCalledWith: UUID?
    var acknowledgeAlertCalledWith: (id: UUID, request: AcknowledgeAlertRequest)?
    var resolveAlertCalledWith: (id: UUID, request: ResolveAlertRequest)?
    var dismissAlertCalledWith: (id: UUID, request: DismissAlertRequest)?
    var getUpcomingCalledWith: (lookaheadDays: Int?, includeOverdue: Bool?, printerId: UUID?)?
    var getTrendsCalled = false
    var getComponentLifespanCalled = false
    var getCostCalledWith: Int?
    var getUptimeCalled = false
    var getFleetStatisticsCalled = false
    
    func getAlerts() async throws -> [MaintenanceAlert] {
        getAlertsCalled = true
        if let error = errorToThrow { throw error }
        return alertsToReturn
    }
    
    func getAlerts(printerId: UUID) async throws -> [MaintenanceAlert] {
        getAlertsForPrinterCalledWith = printerId
        if let error = errorToThrow { throw error }
        return alertsToReturn
    }
    
    func acknowledgeAlert(id: UUID, request: AcknowledgeAlertRequest) async throws -> MaintenanceAlert {
        acknowledgeAlertCalledWith = (id, request)
        if let error = errorToThrow { throw error }
        return alertToReturn ?? alertsToReturn.first!
    }
    
    func resolveAlert(id: UUID, request: ResolveAlertRequest) async throws -> ResolveAlertResponse {
        resolveAlertCalledWith = (id, request)
        if let error = errorToThrow { throw error }
        return resolveAlertResponse ?? ResolveAlertResponse(
            alert: alertToReturn ?? alertsToReturn.first!,
            maintenanceLog: nil
        )
    }
    
    func dismissAlert(id: UUID, request: DismissAlertRequest) async throws -> MaintenanceAlert {
        dismissAlertCalledWith = (id, request)
        if let error = errorToThrow { throw error }
        return alertToReturn ?? alertsToReturn.first!
    }
    
    func getUpcoming(lookaheadDays: Int? = nil, includeOverdue: Bool? = nil, printerId: UUID? = nil) async throws -> [UpcomingMaintenanceTask] {
        getUpcomingCalledWith = (lookaheadDays, includeOverdue, printerId)
        if let error = errorToThrow { throw error }
        return upcomingTasksToReturn
    }
    
    func getTrends(startDate: Date?, endDate: Date?) async throws -> [MaintenanceTrend] {
        getTrendsCalled = true
        if let error = errorToThrow { throw error }
        return trendsToReturn
    }
    
    func getComponentLifespan() async throws -> [ComponentLifespan] {
        getComponentLifespanCalled = true
        if let error = errorToThrow { throw error }
        return lifespansToReturn
    }
    
    func getCost(months: Int? = nil) async throws -> [MaintenanceCost] {
        getCostCalledWith = months
        if let error = errorToThrow { throw error }
        return costsToReturn
    }
    
    func getUptime() async throws -> [PrinterUptime] {
        getUptimeCalled = true
        if let error = errorToThrow { throw error }
        return uptimeToReturn
    }
    
    func getFleetStatistics() async throws -> [FleetPrinterStatistics] {
        getFleetStatisticsCalled = true
        if let error = errorToThrow { throw error }
        return fleetStatsToReturn
    }
}
