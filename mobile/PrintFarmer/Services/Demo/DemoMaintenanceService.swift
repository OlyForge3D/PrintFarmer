import Foundation

// MARK: - Demo Maintenance Service

final class DemoMaintenanceService: MaintenanceServiceProtocol, @unchecked Sendable {

    private static let alerts: [MaintenanceAlert] = {
        let now = Date()
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        let iso = ISO8601DateFormatter()

        func decode(_ json: String) -> MaintenanceAlert {
            // swiftlint:disable:next force_try
            try! decoder.decode(MaintenanceAlert.self, from: Data(json.utf8))
        }

        return [
            decode("""
            {"id":"\(UUID())","printerId":"\(DemoData.voron24_ID)",
             "printer":{"id":"\(DemoData.voron24_ID)","name":"Voron 2.4"},
             "title":"Nozzle Replacement Overdue","message":"Nozzle has exceeded 500 print hours without replacement. Last replaced 620 hours ago.",
             "severity":4,"status":"Active","printerHoursAtTrigger":2100.0,
             "hoursSinceLastMaintenance":620.0,"daysSinceLastMaintenance":45,
             "createdAt":"\(iso.string(from: now.addingTimeInterval(-86400 * 3)))",
             "updatedAt":"\(iso.string(from: now.addingTimeInterval(-86400)))"}
            """),
            decode("""
            {"id":"\(UUID())","printerId":"\(DemoData.prusaMK4_1_ID)",
             "printer":{"id":"\(DemoData.prusaMK4_1_ID)","name":"Prusa MK4 #1"},
             "title":"Belt Tension Check Due","message":"Scheduled belt tension check approaching — 50 hours remaining.",
             "severity":2,"status":"Active","printerHoursAtTrigger":1450.0,
             "hoursSinceLastMaintenance":250.0,"daysSinceLastMaintenance":18,
             "createdAt":"\(iso.string(from: now.addingTimeInterval(-86400 * 2)))",
             "updatedAt":"\(iso.string(from: now.addingTimeInterval(-86400)))"}
            """),
            decode("""
            {"id":"\(UUID())","printerId":"\(DemoData.bambuX1C_ID)",
             "printer":{"id":"\(DemoData.bambuX1C_ID)","name":"Bambu X1C"},
             "title":"Carbon Rod Lubrication","message":"Linear rail lubrication recommended after 1000 hours. Currently at 980 hours.",
             "severity":2,"status":"Active","printerHoursAtTrigger":980.0,
             "hoursSinceLastMaintenance":480.0,"daysSinceLastMaintenance":35,
             "createdAt":"\(iso.string(from: now.addingTimeInterval(-86400)))",
             "updatedAt":"\(iso.string(from: now))"}
            """),
        ]
    }()

    func getAlerts() async throws -> [MaintenanceAlert] {
        Self.alerts
    }

    func getAlerts(printerId: UUID) async throws -> [MaintenanceAlert] {
        Self.alerts.filter { $0.printerId == printerId }
    }

    func acknowledgeAlert(id: UUID, request: AcknowledgeAlertRequest) async throws -> MaintenanceAlert {
        guard let alert = Self.alerts.first(where: { $0.id == id }) else {
            throw ServiceError.notImplemented("Alert not found in demo data")
        }
        return alert
    }

    func resolveAlert(id: UUID, request: ResolveAlertRequest) async throws -> ResolveAlertResponse {
        guard let alert = Self.alerts.first(where: { $0.id == id }) else {
            throw ServiceError.notImplemented("Alert not found in demo data")
        }
        return ResolveAlertResponse(alert: alert, maintenanceLog: nil)
    }

    func dismissAlert(id: UUID, request: DismissAlertRequest) async throws -> MaintenanceAlert {
        guard let alert = Self.alerts.first(where: { $0.id == id }) else {
            throw ServiceError.notImplemented("Alert not found in demo data")
        }
        return alert
    }

    func getUpcoming(lookaheadDays: Int?, includeOverdue: Bool?, printerId: UUID?) async throws -> [UpcomingMaintenanceTask] {
        let now = Date()
        return [
            UpcomingMaintenanceTask(id: UUID().uuidString, taskId: UUID(), printerId: DemoData.voron24_ID,
                printerName: "Voron 2.4", taskName: "Nozzle Replacement", component: "Hotend",
                description: "Replace hardened steel nozzle", priority: 4, intervalType: "Hours",
                intervalValue: 500, dueDate: now.addingTimeInterval(-86400 * 3), daysUntilDue: -3,
                hoursUntilDue: -72, isOverdue: true, isDueToday: false, lastPerformedAt: now.addingTimeInterval(-86400 * 45)),
            UpcomingMaintenanceTask(id: UUID().uuidString, taskId: UUID(), printerId: DemoData.prusaMK4_1_ID,
                printerName: "Prusa MK4 #1", taskName: "Belt Tension Check", component: "Motion System",
                description: "Check and adjust X/Y belt tension", priority: 2, intervalType: "Hours",
                intervalValue: 300, dueDate: now.addingTimeInterval(86400 * 5), daysUntilDue: 5,
                hoursUntilDue: 120, isOverdue: false, isDueToday: false, lastPerformedAt: now.addingTimeInterval(-86400 * 18)),
            UpcomingMaintenanceTask(id: UUID().uuidString, taskId: UUID(), printerId: DemoData.bambuX1C_ID,
                printerName: "Bambu X1C", taskName: "Rail Lubrication", component: "Linear Rails",
                description: "Apply fresh lubricant to linear rails", priority: 2, intervalType: "Hours",
                intervalValue: 1000, dueDate: now.addingTimeInterval(86400 * 7), daysUntilDue: 7,
                hoursUntilDue: 168, isOverdue: false, isDueToday: false, lastPerformedAt: now.addingTimeInterval(-86400 * 35)),
            UpcomingMaintenanceTask(id: UUID().uuidString, taskId: UUID(), printerId: DemoData.prusaMK4_2_ID,
                printerName: "Prusa MK4 #2", taskName: "PEI Sheet Cleaning", component: "Build Plate",
                description: "Deep clean PEI sheet with IPA", priority: 1, intervalType: "Days",
                intervalValue: 14, dueDate: now.addingTimeInterval(86400 * 12), daysUntilDue: 12,
                hoursUntilDue: 288, isOverdue: false, isDueToday: false, lastPerformedAt: now.addingTimeInterval(-86400 * 2)),
            UpcomingMaintenanceTask(id: UUID().uuidString, taskId: UUID(), printerId: DemoData.ender3V3_ID,
                printerName: "Ender 3 V3", taskName: "Extruder Gear Inspection", component: "Extruder",
                description: "Inspect and clean extruder drive gears", priority: 1, intervalType: "Hours",
                intervalValue: 500, dueDate: now.addingTimeInterval(86400 * 20), daysUntilDue: 20,
                hoursUntilDue: 480, isOverdue: false, isDueToday: false, lastPerformedAt: now.addingTimeInterval(-86400 * 30)),
        ]
    }

    func getTrends(startDate: Date?, endDate: Date?) async throws -> [MaintenanceTrend] {
        let now = Date()
        return [
            MaintenanceTrend(date: now.addingTimeInterval(-86400 * 30), printerName: "Voron 2.4",
                             component: "Hotend", action: "Nozzle replaced", cost: 12.99),
            MaintenanceTrend(date: now.addingTimeInterval(-86400 * 25), printerName: "Prusa MK4 #1",
                             component: "Motion System", action: "Belt tension adjusted", cost: 0),
            MaintenanceTrend(date: now.addingTimeInterval(-86400 * 20), printerName: "Bambu X1C",
                             component: "Linear Rails", action: "Rails lubricated", cost: 8.50),
            MaintenanceTrend(date: now.addingTimeInterval(-86400 * 15), printerName: "Ender 3 V3",
                             component: "Build Plate", action: "PEI sheet replaced", cost: 18.99),
        ]
    }

    func getComponentLifespan() async throws -> [ComponentLifespan] {
        [
            ComponentLifespan(component: "Nozzle", avgLifespanHours: 480, replacements: 12),
            ComponentLifespan(component: "PEI Sheet", avgLifespanHours: 1200, replacements: 4),
            ComponentLifespan(component: "Belts", avgLifespanHours: 3000, replacements: 2),
        ]
    }

    func getCost(months: Int?) async throws -> [MaintenanceCost] {
        [
            MaintenanceCost(month: "2024-01", totalCost: 45.50),
            MaintenanceCost(month: "2024-02", totalCost: 32.00),
            MaintenanceCost(month: "2024-03", totalCost: 28.49),
        ]
    }

    func getUptime() async throws -> [PrinterUptime] {
        [
            PrinterUptime(printerName: "Prusa MK4 #1", printerId: DemoData.prusaMK4_1_ID,
                          uptimePercent: 94.2, maintenanceCount: 3, totalDowntimeMinutes: 180),
            PrinterUptime(printerName: "Prusa MK4 #2", printerId: DemoData.prusaMK4_2_ID,
                          uptimePercent: 97.1, maintenanceCount: 1, totalDowntimeMinutes: 45),
            PrinterUptime(printerName: "Bambu X1C", printerId: DemoData.bambuX1C_ID,
                          uptimePercent: 95.8, maintenanceCount: 2, totalDowntimeMinutes: 120),
            PrinterUptime(printerName: "Bambu P1S", printerId: DemoData.bambuP1S_ID,
                          uptimePercent: 91.5, maintenanceCount: 4, totalDowntimeMinutes: 240),
            PrinterUptime(printerName: "Voron 2.4", printerId: DemoData.voron24_ID,
                          uptimePercent: 78.3, maintenanceCount: 8, totalDowntimeMinutes: 680),
            PrinterUptime(printerName: "Ender 3 V3", printerId: DemoData.ender3V3_ID,
                          uptimePercent: 65.0, maintenanceCount: 5, totalDowntimeMinutes: 900),
        ]
    }

    func getFleetStatistics() async throws -> [FleetPrinterStatistics] {
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        let iso = ISO8601DateFormatter()
        let now = Date()

        func decode(_ json: String) -> FleetPrinterStatistics {
            // swiftlint:disable:next force_try
            try! decoder.decode(FleetPrinterStatistics.self, from: Data(json.utf8))
        }

        return [
            decode("""
            {"printerId":"\(DemoData.prusaMK4_1_ID)","printerName":"Prusa MK4 #1",
             "manufacturerName":"Prusa Research","modelName":"MK4","isOnline":true,"inMaintenance":false,
             "totalPrintHours":1480.0,"totalJobsCompleted":312,"totalJobsFailed":8,
             "totalFilamentUsedGrams":4200.0,"totalFilamentUsedMeters":1400.0,
             "lastSyncTime":"\(iso.string(from: now))","daysUntilNextMaintenance":5,
             "nextMaintenanceTask":"Belt Tension Check"}
            """),
            decode("""
            {"printerId":"\(DemoData.prusaMK4_2_ID)","printerName":"Prusa MK4 #2",
             "manufacturerName":"Prusa Research","modelName":"MK4","isOnline":true,"inMaintenance":false,
             "totalPrintHours":820.0,"totalJobsCompleted":185,"totalJobsFailed":4,
             "totalFilamentUsedGrams":2400.0,"totalFilamentUsedMeters":800.0,
             "lastSyncTime":"\(iso.string(from: now))","daysUntilNextMaintenance":12,
             "nextMaintenanceTask":"PEI Sheet Cleaning"}
            """),
            decode("""
            {"printerId":"\(DemoData.bambuX1C_ID)","printerName":"Bambu X1C",
             "manufacturerName":"Bambu Lab","modelName":"X1 Carbon","isOnline":true,"inMaintenance":false,
             "totalPrintHours":980.0,"totalJobsCompleted":156,"totalJobsFailed":12,
             "totalFilamentUsedGrams":3800.0,"totalFilamentUsedMeters":1260.0,
             "lastSyncTime":"\(iso.string(from: now))","daysUntilNextMaintenance":7,
             "nextMaintenanceTask":"Rail Lubrication"}
            """),
        ]
    }
}
