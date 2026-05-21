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
