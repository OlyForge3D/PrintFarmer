import Foundation
import os

@MainActor @Observable
final class MaintenanceViewModel {
    var alerts: [MaintenanceAlert] = []
    var upcomingTasks: [UpcomingMaintenanceTask] = []
    var uptimeData: [PrinterUptime] = []
    var costData: [MaintenanceCost] = []
    var isLoading = false
    var error: String?
    var isViewActive = true

    private let logger = Logger(subsystem: "com.printfarmer.ios", category: "Maintenance")
    private var maintenanceService: (any MaintenanceServiceProtocol)?

    func configure(maintenanceService: any MaintenanceServiceProtocol) {
        self.maintenanceService = maintenanceService
    }

    func loadData() async {
        guard let maintenanceService, isViewActive else { return }
        isLoading = true
        error = nil

        do {
            async let alertsTask = maintenanceService.getAlerts()
            async let upcomingTask = maintenanceService.getUpcoming(
                lookaheadDays: 14,
                includeOverdue: true,
                printerId: nil
            )

            let a = try await alertsTask
            let u = try await upcomingTask
            guard isViewActive else { return }
            alerts = a
            upcomingTasks = u

            // Load analytics data non-critically
            do {
                async let uptimeTask = maintenanceService.getUptime()
                async let costTask = maintenanceService.getCost(months: nil)
                let ut = try await uptimeTask
                let ct = try await costTask
                guard isViewActive else { return }
                uptimeData = ut
                costData = ct
            } catch {
                logger.warning("Failed to load maintenance analytics: \(error.localizedDescription)")
            }
        } catch {
            guard isViewActive else { return }
            self.error = error.localizedDescription
        }

        guard isViewActive else { return }
        isLoading = false
    }

    func acknowledgeAlert(_ alert: MaintenanceAlert) async {
        guard let maintenanceService, isViewActive else { return }
        do {
            let request = AcknowledgeAlertRequest(acknowledgedBy: "iOS User")
            let updated = try await maintenanceService.acknowledgeAlert(
                id: alert.id,
                request: request
            )
            guard isViewActive else { return }
            if let index = alerts.firstIndex(where: { $0.id == alert.id }) {
                alerts[index] = updated
            }
        } catch {
            guard isViewActive else { return }
            self.error = error.localizedDescription
        }
    }

    func dismissAlert(_ alert: MaintenanceAlert) async {
        guard let maintenanceService, isViewActive else { return }
        do {
            let request = DismissAlertRequest(dismissedBy: "iOS User", reason: "Dismissed from iOS app")
            let updated = try await maintenanceService.dismissAlert(
                id: alert.id,
                request: request
            )
            guard isViewActive else { return }
            if let index = alerts.firstIndex(where: { $0.id == alert.id }) {
                alerts[index] = updated
            }
        } catch {
            guard isViewActive else { return }
            self.error = error.localizedDescription
        }
    }

    // MARK: - Computed

    var activeAlerts: [MaintenanceAlert] {
        alerts.filter { $0.dismissedAt == nil && $0.resolvedAt == nil }
    }

    var sortedUpcomingTasks: [UpcomingMaintenanceTask] {
        upcomingTasks.sorted { ($0.dueDate ?? .distantFuture) < ($1.dueDate ?? .distantFuture) }
    }
}
