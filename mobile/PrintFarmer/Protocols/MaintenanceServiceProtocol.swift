import Foundation

// MARK: - Maintenance Service Protocol

protocol MaintenanceServiceProtocol: Sendable {
    func getAlerts() async throws -> [MaintenanceAlert]
    func getAlerts(printerId: UUID) async throws -> [MaintenanceAlert]
    func acknowledgeAlert(id: UUID, request: AcknowledgeAlertRequest) async throws -> MaintenanceAlert
    func resolveAlert(id: UUID, request: ResolveAlertRequest) async throws -> ResolveAlertResponse
    func dismissAlert(id: UUID, request: DismissAlertRequest) async throws -> MaintenanceAlert
    func getUpcoming(lookaheadDays: Int?, includeOverdue: Bool?, printerId: UUID?) async throws -> [UpcomingMaintenanceTask]
    func getTrends(startDate: Date?, endDate: Date?) async throws -> [MaintenanceTrend]
    func getComponentLifespan() async throws -> [ComponentLifespan]
    func getCost(months: Int?) async throws -> [MaintenanceCost]
    func getUptime() async throws -> [PrinterUptime]
    func getFleetStatistics() async throws -> [FleetPrinterStatistics]

    // MARK: - Printer Detail v2 (issue #712, F7)
    //
    // Per-printer odometer reading and completion logging. `getPrinterStatistics`
    // reads cumulative print hours (`GET /api/maintenance/printers/{id}/statistics`);
    // `createLog` records completion of a due item (`POST /api/maintenance/logs`).
    func getPrinterStatistics(printerId: UUID) async throws -> PrinterMaintenanceStatistics
    func createLog(_ request: CreateMaintenanceLogRequest) async throws -> MaintenanceLog
}

extension MaintenanceServiceProtocol {
    func getUpcoming() async throws -> [UpcomingMaintenanceTask] {
        try await getUpcoming(lookaheadDays: nil, includeOverdue: nil, printerId: nil)
    }

    func getTrends() async throws -> [MaintenanceTrend] {
        try await getTrends(startDate: nil, endDate: nil)
    }

    func getCost() async throws -> [MaintenanceCost] {
        try await getCost(months: nil)
    }
}
