import Foundation

// MARK: - Maintenance Service

actor MaintenanceService: MaintenanceServiceProtocol {
    private let apiClient: APIClient

    init(apiClient: APIClient) {
        self.apiClient = apiClient
    }

    func getAlerts() async throws -> [MaintenanceAlert] {
        try await apiClient.get("/api/maintenance/alerts")
    }

    func getAlerts(printerId: UUID) async throws -> [MaintenanceAlert] {
        try await apiClient.get("/api/maintenance/printers/\(printerId)/alerts")
    }

    func acknowledgeAlert(id: UUID, request: AcknowledgeAlertRequest) async throws -> MaintenanceAlert {
        try await apiClient.post("/api/maintenance/alerts/\(id)/acknowledge", body: request)
    }

    func resolveAlert(id: UUID, request: ResolveAlertRequest) async throws -> ResolveAlertResponse {
        try await apiClient.post("/api/maintenance/alerts/\(id)/resolve", body: request)
    }

    func dismissAlert(id: UUID, request: DismissAlertRequest) async throws -> MaintenanceAlert {
        try await apiClient.post("/api/maintenance/alerts/\(id)/dismiss", body: request)
    }

    func getUpcoming(lookaheadDays: Int? = nil, includeOverdue: Bool? = nil, printerId: UUID? = nil) async throws -> [UpcomingMaintenanceTask] {
        var params: [String] = []
        if let days = lookaheadDays { params.append("lookaheadDays=\(days)") }
        if let overdue = includeOverdue { params.append("includeOverdue=\(overdue)") }
        if let pid = printerId { params.append("printerId=\(pid)") }
        let query = params.isEmpty ? "" : "?\(params.joined(separator: "&"))"
        return try await apiClient.get("/api/maintenance/upcoming\(query)")
    }

    func getTrends(startDate: Date? = nil, endDate: Date? = nil) async throws -> [MaintenanceTrend] {
        var params: [String] = []
        if let start = startDate { params.append("startDate=\(Self.iso8601String(start))") }
        if let end = endDate { params.append("endDate=\(Self.iso8601String(end))") }
        let query = params.isEmpty ? "" : "?\(params.joined(separator: "&"))"
        return try await apiClient.get("/api/maintenance/analytics/trends\(query)")
    }

    func getComponentLifespan() async throws -> [ComponentLifespan] {
        try await apiClient.get("/api/maintenance/analytics/component-lifespan")
    }

    func getCost(months: Int? = nil) async throws -> [MaintenanceCost] {
        let query = months.map { "?months=\($0)" } ?? ""
        return try await apiClient.get("/api/maintenance/analytics/cost\(query)")
    }

    func getUptime() async throws -> [PrinterUptime] {
        try await apiClient.get("/api/maintenance/analytics/uptime")
    }

    func getFleetStatistics() async throws -> [FleetPrinterStatistics] {
        try await apiClient.get("/api/maintenance/statistics/fleet")
    }

    private static func iso8601String(_ date: Date) -> String {
        APIClient.iso8601Plain.string(from: date)
    }
}
