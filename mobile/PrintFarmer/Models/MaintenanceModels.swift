import Foundation

// MARK: - Lightweight embedded type for Printer navigation property

struct AlertPrinter: Codable, Sendable {
    let id: UUID
    let name: String
}

// MARK: - Maintenance Alert Status

enum MaintenanceAlertStatus: String, Codable, Sendable {
    case active = "Active"
    case acknowledged = "Acknowledged"
    case resolved = "Resolved"
    case dismissed = "Dismissed"
}

// MARK: - Maintenance Alert

struct MaintenanceAlert: Codable, Sendable, Identifiable {
    let id: UUID
    let printerId: UUID
    let printer: AlertPrinter?
    let printerMaintenanceScheduleId: UUID?
    let maintenanceTaskId: UUID?
    let title: String
    let message: String
    let severity: Int
    let status: MaintenanceAlertStatus
    let printerHoursAtTrigger: Double
    let hoursSinceLastMaintenance: Double?
    let daysSinceLastMaintenance: Int?
    let createdAt: Date
    let acknowledgedAt: Date?
    let acknowledgedBy: String?
    let resolvedAt: Date?
    let resolvedBy: String?
    let dismissedAt: Date?
    let dismissedBy: String?
    let dismissalReason: String?
    let updatedAt: Date

    /// Printer name from the embedded navigation property
    var printerName: String {
        printer?.name ?? "Unknown Printer"
    }

    /// Human-readable severity derived from the integer level
    var severityLabel: String {
        switch severity {
        case 4: return "critical"
        case 3: return "high"
        case 2: return "medium"
        default: return "low"
        }
    }

    private enum CodingKeys: String, CodingKey {
        case id, printerId, printer
        case printerMaintenanceScheduleId, maintenanceTaskId
        case title, message, severity, status
        case printerHoursAtTrigger, hoursSinceLastMaintenance, daysSinceLastMaintenance
        case createdAt, acknowledgedAt, acknowledgedBy
        case resolvedAt, resolvedBy
        case dismissedAt, dismissedBy, dismissalReason
        case updatedAt
    }
}

// MARK: - Upcoming Maintenance Task

struct UpcomingMaintenanceTask: Codable, Sendable, Identifiable {
    let id: String
    let taskId: UUID
    let printerId: UUID
    let printerName: String
    let taskName: String
    let component: String?
    let description: String?
    let priority: Int
    let intervalType: String
    let intervalValue: Double
    let dueDate: Date?
    let daysUntilDue: Int?
    let hoursUntilDue: Double?
    let isOverdue: Bool
    let isDueToday: Bool
    let lastPerformedAt: Date?
}

// MARK: - Maintenance Trend

struct MaintenanceTrend: Codable, Sendable {
    let date: Date
    let printerName: String
    let component: String?
    let action: String
    let cost: Decimal
}

// MARK: - Component Lifespan

struct ComponentLifespan: Codable, Sendable {
    let component: String
    let avgLifespanHours: Double
    let replacements: Int
}

// MARK: - Maintenance Cost

struct MaintenanceCost: Codable, Sendable {
    let month: String
    let totalCost: Decimal
}

// MARK: - Printer Uptime

struct PrinterUptime: Codable, Sendable {
    let printerName: String
    let printerId: UUID
    let uptimePercent: Double
    let maintenanceCount: Int
    let totalDowntimeMinutes: Int
}

// MARK: - Fleet Printer Statistics

struct FleetPrinterStatistics: Codable, Sendable, Identifiable {
    var id: UUID { printerId }

    let printerId: UUID
    let printerName: String
    let manufacturerName: String?
    let modelName: String?
    let isOnline: Bool
    let inMaintenance: Bool
    let totalPrintHours: Double
    let totalJobsCompleted: Int
    let totalJobsFailed: Int
    let totalFilamentUsedGrams: Double?
    let totalFilamentUsedMeters: Double?
    let lastSyncTime: Date?
    let daysUntilNextMaintenance: Int?
    let nextMaintenanceTask: String?

    enum CodingKeys: String, CodingKey {
        case printerId, printerName, manufacturerName, modelName
        case isOnline, inMaintenance
        case totalPrintHours, totalJobsCompleted, totalJobsFailed
        case totalFilamentUsedGrams, totalFilamentUsedMeters, lastSyncTime
        case daysUntilNextMaintenance, nextMaintenanceTask
    }
}

// MARK: - Maintenance Log

struct MaintenanceLog: Codable, Sendable, Identifiable {
    let id: UUID
    let printerId: UUID
    let printerMaintenanceScheduleId: UUID?
    let resolvedAlertId: UUID?
    let maintenanceTaskId: UUID?
    let taskName: String
    let notes: String?
    let component: String?
    let performedBy: String?
    let performedAt: Date
    let durationMinutes: Int?
    let cost: Decimal?
    let partsReplaced: String?
    let printerHoursAtMaintenance: Double?
    let createdAt: Date
}

// MARK: - Request Models

struct AcknowledgeAlertRequest: Encodable, Sendable {
    let acknowledgedBy: String
}

struct ResolveAlertRequest: Encodable, Sendable {
    let performedBy: String
    let notes: String?
    let durationMinutes: Int?
    let cost: Decimal?
    let partsReplaced: String?
}

struct DismissAlertRequest: Encodable, Sendable {
    let dismissedBy: String
    let reason: String?
}

// MARK: - Resolve Alert Response

struct ResolveAlertResponse: Codable, Sendable {
    let alert: MaintenanceAlert
    let maintenanceLog: MaintenanceLog?
}
