import Foundation
import os

@MainActor @Observable
final class UptimeViewModel {
    var uptimeData: [PrinterUptime] = []
    var fleetStats: [FleetPrinterStatistics] = []
    var isLoading = false
    var error: String?
    var isViewActive = true

    private let logger = Logger(subsystem: "com.printfarmer.ios", category: "Uptime")
    private var maintenanceService: (any MaintenanceServiceProtocol)?

    func configure(maintenanceService: any MaintenanceServiceProtocol) {
        self.maintenanceService = maintenanceService
    }

    func loadData() async {
        guard let maintenanceService, isViewActive else { return }
        isLoading = true
        error = nil

        do {
            async let uptimeTask = maintenanceService.getUptime()
            async let fleetTask = maintenanceService.getFleetStatistics()
            let ut = try await uptimeTask
            let fs = try await fleetTask
            guard isViewActive else { return }
            uptimeData = ut
            fleetStats = fs
        } catch {
            guard isViewActive else { return }
            self.error = error.localizedDescription
        }

        guard isViewActive else { return }
        isLoading = false
    }

    // MARK: - Computed

    var averageUptime: Double {
        guard !uptimeData.isEmpty else { return 0 }
        let total = uptimeData.reduce(0.0) { $0 + $1.uptimePercent }
        return total / Double(uptimeData.count)
    }

    var totalDowntimeMinutes: Int {
        uptimeData.reduce(0) { $0 + $1.totalDowntimeMinutes }
    }

    var totalMaintenanceCount: Int {
        uptimeData.reduce(0) { $0 + $1.maintenanceCount }
    }
}
