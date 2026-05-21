import Foundation

// swiftlint:disable file_length
// MARK: - Enums

enum PrinterBackend: String, Codable, Sendable {
    case unknown = "Unknown"
    case moonraker = "Moonraker"
    case prusaLink = "PrusaLink"
    case sdcp = "SDCP"
    case octoPrint = "OctoPrint"
    case flashForge = "FlashForge"

    init(from decoder: Decoder) throws {
        let container = try decoder.singleValueContainer()
        if let str = try? container.decode(String.self),
           let value = Self(rawValue: str) {
            self = value
        } else if let num = try? container.decode(Int.self) {
            switch num {
            case 0: self = .unknown
            case 1: self = .moonraker
            case 2: self = .prusaLink
            case 3: self = .sdcp
            case 4: self = .octoPrint
            case 5: self = .flashForge
            default: self = .unknown
            }
        } else {
            self = .unknown
        }
    }
}

enum MotionType: String, Codable, Sendable {
    case cartesian = "Cartesian"
    case coreXY = "CoreXY"
    case delta = "Delta"
    case unknown = "Unknown"

    init(from decoder: Decoder) throws {
        let container = try decoder.singleValueContainer()
        if let str = try? container.decode(String.self),
           let value = Self(rawValue: str) {
            self = value
        } else if let num = try? container.decode(Int.self) {
            switch num {
            case 0: self = .cartesian
            case 1: self = .coreXY
            case 2: self = .delta
            default: self = .unknown
            }
        } else {
            self = .unknown
        }
    }
}

enum PrintJobStatus: String, Codable, Sendable {
    case queued = "Queued"
    case assigned = "Assigned"
    case starting = "Starting"
    case printing = "Printing"
    case paused = "Paused"
    case completed = "Completed"
    case failed = "Failed"
    case cancelled = "Cancelled"

    init(from decoder: Decoder) throws {
        let container = try decoder.singleValueContainer()
        if let str = try? container.decode(String.self),
           let value = Self(rawValue: str) {
            self = value
        } else if let num = try? container.decode(Int.self) {
            switch num {
            case 0: self = .queued
            case 1: self = .assigned
            case 2: self = .starting
            case 3: self = .printing
            case 4: self = .paused
            case 5: self = .completed
            case 6: self = .failed
            case 7: self = .cancelled
            default: self = .queued
            }
        } else {
            self = .queued
        }
    }
}

enum PrintJobPriority: String, Codable, Sendable {
    case low = "Low"
    case normal = "Normal"
    case high = "High"
    case urgent = "Urgent"

    init(from decoder: Decoder) throws {
        let container = try decoder.singleValueContainer()
        if let str = try? container.decode(String.self),
           let value = Self(rawValue: str) {
            self = value
        } else if let num = try? container.decode(Int.self) {
            switch num {
            case 0: self = .low
            case 1: self = .normal
            case 2: self = .high
            case 3: self = .urgent
            default: self = .normal
            }
        } else {
            self = .normal
        }
    }

    /// Maps the backend integer priority value to the enum.
    static func from(intValue: Int) -> PrintJobPriority? {
        switch intValue {
        case 0: .low
        case 1: .normal
        case 2: .high
        case 3: .urgent
        default: nil
        }
    }
}

enum AutoDispatchState: String, Codable, Sendable {
    case none = "None"
    case pendingReady = "PendingReady"
    case ready = "Ready"
    case dismissed = "Dismissed"

    init(from decoder: Decoder) throws {
        let container = try decoder.singleValueContainer()
        if let str = try? container.decode(String.self),
           let value = Self(rawValue: str) {
            self = value
        } else if let num = try? container.decode(Int.self) {
            switch num {
            case 0: self = .none
            case 1: self = .pendingReady
            case 2: self = .ready
            case 3: self = .dismissed
            default: self = .none
            }
        } else {
            self = .none
        }
    }
}

enum NotificationType: String, Codable, Sendable {
    case jobStarted = "JobStarted"
    case jobCompleted = "JobCompleted"
    case jobFailed = "JobFailed"
    case jobPaused = "JobPaused"
    case jobResumed = "JobResumed"
    case queueAlert = "QueueAlert"
    case systemAlert = "SystemAlert"
    case bedClearRequired = "BedClearRequired"

    init(from decoder: Decoder) throws {
        let container = try decoder.singleValueContainer()
        let rawValue = try container.decode(String.self)
        self = NotificationType(rawValue: rawValue) ?? .systemAlert
    }
}

enum NotificationFrequency: String, Codable, Sendable {
    case realTime = "RealTime"
    case hourly = "Hourly"
    case daily = "Daily"
    case weekly = "Weekly"
    case never = "Never"
}

// MARK: - Printer (decodes both CompletePrinterDto and PrinterDto from backend)

struct Printer: Codable, Identifiable, Sendable {
    let id: UUID
    let name: String
    let notes: String?

    // Catalog
    let manufacturerId: UUID?
    let manufacturerName: String?
    let modelId: UUID?
    let modelName: String?
    let motionType: MotionType?

    // Config (defaults provided for PrinterDto which omits some fields)
    let backend: PrinterBackend
    let apiKey: String?
    let originalServerUrl: String?
    let backendPort: Int
    let frontendPort: Int?
    let inMaintenance: Bool
    let isEnabled: Bool

    // Live status (from SignalR cache)
    var isOnline: Bool
    var state: String?
    var progress: Double?
    var jobName: String?
    var fileName: String?
    var thumbnailUrl: String?
    var cameraStreamUrl: String?
    var cameraSnapshotUrl: String?

    // Telemetry
    var x: Double?
    var y: Double?
    var z: Double?
    var hotendTemp: Double?
    var bedTemp: Double?
    var hotendTarget: Double?
    var bedTarget: Double?
    var homedAxes: String?

    // Metadata
    var spoolInfo: PrinterSpoolInfo?
    let backendUrl: String?
    let frontendUrl: String?
    let location: LocationSummary?

    // Feature flags
    let obicoEnabled: Bool

    // CodingKeys — all property names match backend camelCase keys
    private enum CodingKeys: String, CodingKey {
        case id, name, notes
        case manufacturerId, manufacturerName, modelId, modelName, motionType
        case backend, apiKey, originalServerUrl, backendPort, frontendPort
        case inMaintenance, isEnabled
        case isOnline, state, progress, jobName, fileName, thumbnailUrl
        case cameraStreamUrl, cameraSnapshotUrl
        case x, y, z, hotendTemp, bedTemp, hotendTarget, bedTarget, homedAxes
        case spoolInfo, backendUrl, frontendUrl, location
        case obicoEnabled
    }

    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        id = try c.decode(UUID.self, forKey: .id)
        name = try c.decode(String.self, forKey: .name)
        notes = try c.decodeIfPresent(String.self, forKey: .notes)

        manufacturerId = try c.decodeIfPresent(UUID.self, forKey: .manufacturerId)
        manufacturerName = try c.decodeIfPresent(String.self, forKey: .manufacturerName)
        modelId = try c.decodeIfPresent(UUID.self, forKey: .modelId)
        modelName = try c.decodeIfPresent(String.self, forKey: .modelName)
        motionType = try c.decodeIfPresent(MotionType.self, forKey: .motionType)

        backend = try c.decodeIfPresent(PrinterBackend.self, forKey: .backend) ?? .unknown
        apiKey = try c.decodeIfPresent(String.self, forKey: .apiKey)
        originalServerUrl = try c.decodeIfPresent(String.self, forKey: .originalServerUrl)
        backendPort = try c.decodeIfPresent(Int.self, forKey: .backendPort) ?? 80
        frontendPort = try c.decodeIfPresent(Int.self, forKey: .frontendPort)
        inMaintenance = try c.decodeIfPresent(Bool.self, forKey: .inMaintenance) ?? false
        isEnabled = try c.decodeIfPresent(Bool.self, forKey: .isEnabled) ?? true

        isOnline = try c.decodeIfPresent(Bool.self, forKey: .isOnline) ?? false
        state = try c.decodeIfPresent(String.self, forKey: .state)
        // Backend sends progress as 0-100; normalize to 0-1.0 for SwiftUI
        progress = try c.decodeIfPresent(Double.self, forKey: .progress).map { $0 / 100.0 }
        jobName = try c.decodeIfPresent(String.self, forKey: .jobName)
        fileName = try c.decodeIfPresent(String.self, forKey: .fileName)
        thumbnailUrl = try c.decodeIfPresent(String.self, forKey: .thumbnailUrl)
        cameraStreamUrl = try c.decodeIfPresent(String.self, forKey: .cameraStreamUrl)
        cameraSnapshotUrl = try c.decodeIfPresent(String.self, forKey: .cameraSnapshotUrl)

        x = try c.decodeIfPresent(Double.self, forKey: .x)
        y = try c.decodeIfPresent(Double.self, forKey: .y)
        z = try c.decodeIfPresent(Double.self, forKey: .z)
        hotendTemp = try c.decodeIfPresent(Double.self, forKey: .hotendTemp)
        bedTemp = try c.decodeIfPresent(Double.self, forKey: .bedTemp)
        hotendTarget = try c.decodeIfPresent(Double.self, forKey: .hotendTarget)
        bedTarget = try c.decodeIfPresent(Double.self, forKey: .bedTarget)
        homedAxes = try c.decodeIfPresent(String.self, forKey: .homedAxes)

        spoolInfo = try c.decodeIfPresent(PrinterSpoolInfo.self, forKey: .spoolInfo)
        backendUrl = try c.decodeIfPresent(String.self, forKey: .backendUrl)
        frontendUrl = try c.decodeIfPresent(String.self, forKey: .frontendUrl)
        location = try c.decodeIfPresent(LocationSummary.self, forKey: .location)
        obicoEnabled = try c.decodeIfPresent(Bool.self, forKey: .obicoEnabled) ?? false
    }
}

struct PrinterSpoolInfo: Codable, Sendable {
    let hasActiveSpool: Bool
    let activeSpoolId: Int?
    let spoolName: String?
    let material: String?
    let colorHex: String?
    let filamentName: String?
    let vendor: String?
    let remainingWeightG: Double?
    let spoolInUse: Bool?

    init(
        hasActiveSpool: Bool,
        activeSpoolId: Int? = nil,
        spoolName: String? = nil,
        material: String? = nil,
        colorHex: String? = nil,
        filamentName: String? = nil,
        vendor: String? = nil,
        remainingWeightG: Double? = nil,
        spoolInUse: Bool? = nil
    ) {
        self.hasActiveSpool = hasActiveSpool
        self.activeSpoolId = activeSpoolId
        self.spoolName = spoolName
        self.material = material
        self.colorHex = colorHex
        self.filamentName = filamentName
        self.vendor = vendor
        self.remainingWeightG = remainingWeightG
        self.spoolInUse = spoolInUse
    }

    private enum CodingKeys: String, CodingKey {
        case hasActiveSpool, activeSpoolId, spoolName, material
        case colorHex, filamentName, vendor, remainingWeightG, spoolInUse
    }

    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        hasActiveSpool = try c.decodeIfPresent(Bool.self, forKey: .hasActiveSpool) ?? false
        activeSpoolId = try c.decodeIfPresent(Int.self, forKey: .activeSpoolId)
        spoolName = try c.decodeIfPresent(String.self, forKey: .spoolName)
        material = try c.decodeIfPresent(String.self, forKey: .material)
        colorHex = try c.decodeIfPresent(String.self, forKey: .colorHex)
        filamentName = try c.decodeIfPresent(String.self, forKey: .filamentName)
        vendor = try c.decodeIfPresent(String.self, forKey: .vendor)
        remainingWeightG = try c.decodeIfPresent(Double.self, forKey: .remainingWeightG)
        spoolInUse = try c.decodeIfPresent(Bool.self, forKey: .spoolInUse)
    }
}

// MARK: - Printer Status Detail (matches PrinterStatusDto)

struct PrinterStatusDetail: Codable, Sendable {
    let id: UUID
    let isOnline: Bool
    let state: String?
    let progress: Double?
    let jobName: String?
    let thumbnailUrl: String?
    let cameraStreamUrl: String?
    let cameraSnapshotUrl: String?
    let x: Double?
    let y: Double?
    let z: Double?
    let hotendTemp: Double?
    let bedTemp: Double?
    let hotendTarget: Double?
    let bedTarget: Double?
    let spoolInfo: PrinterSpoolInfo?
    let mmuStatus: MmuStatus?
}

// MARK: - MMU Status (matches MmuStatusDto)

struct MmuStatus: Codable, Sendable {
    let enabled: Bool
    let isHomed: Bool
    let activeTool: Int
    let activeGate: Int
    let filamentState: String?
    let action: String?
    let numGates: Int
    let hasBypass: Bool
    let endlessSpool: Bool
    let clogDetection: Bool
    let gates: [MmuGate]
    let mmuType: String

    private enum CodingKeys: String, CodingKey {
        case enabled, isHomed, activeTool, activeGate, filamentState, action
        case numGates, hasBypass, endlessSpool, clogDetection, gates, mmuType
    }

    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        enabled = try c.decodeIfPresent(Bool.self, forKey: .enabled) ?? false
        isHomed = try c.decodeIfPresent(Bool.self, forKey: .isHomed) ?? false
        activeTool = try c.decodeIfPresent(Int.self, forKey: .activeTool) ?? -1
        activeGate = try c.decodeIfPresent(Int.self, forKey: .activeGate) ?? -1
        filamentState = try c.decodeIfPresent(String.self, forKey: .filamentState)
        action = try c.decodeIfPresent(String.self, forKey: .action)
        numGates = try c.decodeIfPresent(Int.self, forKey: .numGates) ?? 0
        hasBypass = try c.decodeIfPresent(Bool.self, forKey: .hasBypass) ?? false
        endlessSpool = try c.decodeIfPresent(Bool.self, forKey: .endlessSpool) ?? false
        clogDetection = try c.decodeIfPresent(Bool.self, forKey: .clogDetection) ?? false
        gates = try c.decodeIfPresent([MmuGate].self, forKey: .gates) ?? []
        mmuType = try c.decodeIfPresent(String.self, forKey: .mmuType) ?? "Unknown"
    }
}

struct MmuGate: Codable, Sendable {
    let index: Int
    let status: Int
    let material: String?
    let color: String?
    let filamentName: String?
    let spoolId: Int
    let name: String?

    private enum CodingKeys: String, CodingKey {
        case index, status, material, color, filamentName, spoolId, name
    }

    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        index = try c.decodeIfPresent(Int.self, forKey: .index) ?? 0
        status = try c.decodeIfPresent(Int.self, forKey: .status) ?? 0
        material = try c.decodeIfPresent(String.self, forKey: .material)
        color = try c.decodeIfPresent(String.self, forKey: .color)
        filamentName = try c.decodeIfPresent(String.self, forKey: .filamentName)
        spoolId = try c.decodeIfPresent(Int.self, forKey: .spoolId) ?? -1
        name = try c.decodeIfPresent(String.self, forKey: .name)
    }
}

// MARK: - Print Job Status Info (matches PrintJobStatusDto)

struct PrintJobStatusInfo: Codable, Sendable {
    let state: String?
    let progress: Double?
    let jobName: String?
    let thumbnailUrl: String?
    let error: String?
}

// MARK: - Command Result (matches backend CommandResult)

struct CommandResult: Codable, Sendable {
    let success: Bool
    let message: String?
}

// MARK: - Print Job (matches JobQueuePrintJobDto)

struct PrintJob: Codable, Identifiable, Sendable {
    let id: UUID
    let status: PrintJobStatus?
    let priority: Int
    let queuePosition: Int
    let gcodeFileId: UUID?
    let gcodeFileName: String
    let assignedPrinterId: UUID?
    let assignedPrinterName: String?
    let createdAt: Date?
    let updatedAt: Date?
    let actualStartTime: Date?
    let actualEndTime: Date?
    let estimatedPrintTime: String?
    let actualPrintTime: String?
    let estimatedFilamentUsage: Double?
    let actualFilamentUsage: Double?
    let estimatedCost: Decimal?
    let actualCost: Decimal?
    let failureReason: String?
    let requiredNozzleDiameter: Double?
    let requiredMaterialType: String?
    let spoolmanFilamentId: Int?
    let filamentName: String?
    let filamentVendor: String?
    let filamentColor: String?
    let copies: Int
    let completedCopies: Int
    let remainingCopies: Int
    let projectFileId: UUID?
    let thumbnailUrl: String?

    var name: String { gcodeFileName }

    var isMultiCopy: Bool {
        copies > 1
    }
}

// MARK: - Queue Overview (matches QueueOverviewDto)

struct QueueOverview: Codable, Identifiable, Sendable {
    let printerId: UUID
    let printerName: String
    let printerModel: String
    let modelAliases: [String]?
    let isAvailable: Bool
    let queuedJobsCount: Int
    let currentJobId: UUID?
    let currentJobName: String?
    let estimatedCompletionTime: Date?
    let nozzleDiameter: Double?
    let supportedMaterials: [String]?

    var id: UUID { printerId }
}

// MARK: - Queued Print Job Response (matches QueuedPrintJobWithFileMetaDto from analytics)

struct QueuedPrintJobResponse: Codable, Identifiable, Sendable {
    let job: QueuedJobInfo
    let gcodeFile: QueueGcodeFileMeta?
    let assignedPrinter: QueuePrinterMeta?
    let estimatedStartTime: Date?
    let estimatedCompletionTime: Date?

    var id: String { job.id }
}

struct QueuedJobInfo: Codable, Identifiable, Sendable {
    let id: String
    let name: String
    let fileName: String?
    let assignedPrinterId: String?
    let printerName: String?
    let printerModel: String?
    let status: String
    let priority: Int
    let queuePosition: Int
    let estimatedPrintTimeSeconds: Int?
    let actualStartTimeUtc: Date?
    let actualEndTimeUtc: Date?
    let actualPrintTimeSeconds: Int?
    let failureReason: String?
    let createdAtUtc: Date
    let updatedAtUtc: Date?
    let thumbnailUrl: String?
    let filamentName: String?
    let filamentColor: String?
    let copies: Int
    let completedCopies: Int
    let remainingCopies: Int

    var jobStatus: PrintJobStatus? {
        PrintJobStatus(rawValue: status)
    }

    var jobUUID: UUID? {
        UUID(uuidString: id)
    }

    var isMultiCopy: Bool {
        copies > 1
    }

    var estimatedDuration: TimeInterval? {
        guard let seconds = estimatedPrintTimeSeconds else { return nil }
        return TimeInterval(seconds)
    }
}

struct QueuePrinterMeta: Codable, Sendable {
    let id: String
    let name: String
    let modelName: String
    let status: String
    let isOnline: Bool
}

struct QueueGcodeFileMeta: Codable, Sendable {
    let id: String
    let name: String
    let fileName: String
    let fileSizeBytes: Int?
    let materialType: String?
    let nozzleDiameter: Decimal?
    let estimatedPrintTimeSeconds: Int?
    let estimatedFilamentUsageGrams: Int?
    let thumbnailUrl: String?
}

// MARK: - Statistics Summary (matches StatisticsSummaryDto)

struct StatisticsSummary: Codable, Sendable {
    let totalJobs: Int
    let completedJobs: Int
    let failedJobs: Int
    let cancelledJobs: Int
    let successRate: Double
    let totalCost: Decimal
    let totalFilamentGrams: Double
    let totalPrintHours: Double

    private enum CodingKeys: String, CodingKey {
        case totalJobs, completedJobs, failedJobs, cancelledJobs
        case successRate, totalCost, totalFilamentGrams, totalPrintHours
    }

    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        totalJobs = try c.decodeIfPresent(Int.self, forKey: .totalJobs) ?? 0
        completedJobs = try c.decodeIfPresent(Int.self, forKey: .completedJobs) ?? 0
        failedJobs = try c.decodeIfPresent(Int.self, forKey: .failedJobs) ?? 0
        cancelledJobs = try c.decodeIfPresent(Int.self, forKey: .cancelledJobs) ?? 0
        successRate = try c.decodeIfPresent(Double.self, forKey: .successRate) ?? 0
        totalCost = try c.decodeIfPresent(Decimal.self, forKey: .totalCost) ?? 0
        totalFilamentGrams = try c.decodeIfPresent(Double.self, forKey: .totalFilamentGrams) ?? 0
        totalPrintHours = try c.decodeIfPresent(Double.self, forKey: .totalPrintHours) ?? 0
    }
}

// MARK: - App Notification (matches NotificationDto from backend)

struct AppNotification: Codable, Identifiable, Sendable {
    let id: String
    let userId: UUID
    let jobId: UUID?
    let type: NotificationType
    let subject: String
    let body: String
    let isRead: Bool
    let createdAt: Date
    let readAt: Date?
    let expiresAt: Date?
}

struct UnreadCountResponse: Codable, Sendable {
    let unreadCount: Int
}

// MARK: - Location

struct Location: Codable, Identifiable, Sendable {
    let id: UUID
    let name: String
    let description: String?
    let printerCount: Int
    let createdAt: Date
    let modifiedAt: Date
    let isActive: Bool
}

struct LocationSummary: Codable, Sendable {
    let id: UUID
    let name: String
    let description: String?
}

// MARK: - Auth (matches AuthenticationResult from backend)

struct LoginRequest: Codable, Sendable {
    let usernameOrEmail: String
    let password: String
    let rememberMe: Bool
}

struct AuthResponse: Codable, Sendable {
    let success: Bool
    let token: String?
    let expiresAt: Date?
    let user: UserDTO?
    let error: String?
}

struct UserDTO: Codable, Identifiable, Sendable {
    let id: UUID
    let username: String
    let email: String
    let firstName: String?
    let lastName: String?
    let isActive: Bool
    let emailConfirmed: Bool
    let lastLogin: Date?
    let createdAt: Date
    let roles: [String]
    let permissions: [String]
}
