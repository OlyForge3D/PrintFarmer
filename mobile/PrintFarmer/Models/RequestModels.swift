import Foundation

// MARK: - Printer Request DTOs

struct UpdatePrinterRequest: Codable, Sendable {
    var name: String?
    var serverUrl: String?
    var notes: String?
    var manufacturerId: UUID?
    var modelId: UUID?
    var backend: PrinterBackend?
    var apiKey: String?
    var username: String?
    var password: String?
    var cameraStreamUrl: String?
    var cameraSnapshotUrl: String?
    var backendPort: Int?
    var frontendPort: Int?
    var isEnabled: Bool?
}

// MARK: - Job Request DTOs

struct CreatePrintJobRequest: Codable, Sendable {
    let name: String
    let priority: Int
    let gcodeFileId: UUID
    let hotendTemperature: Double?
    let bedTemperature: Double?
    let spoolId: Int?
    let requiredCapabilities: [String]?
    let autoAssign: Bool
    let preferredPrinterIds: [UUID]?
    let excludedPrinterIds: [UUID]?
}

struct UpdatePrintJobRequest: Codable, Sendable {
    let name: String
    let priority: Int
    let hotendTemperature: Double?
    let bedTemperature: Double?
    let spoolId: Int?
    let requiredCapabilities: [String]?
    let autoAssign: Bool
    let preferredPrinterIds: [UUID]?
    let excludedPrinterIds: [UUID]?
}

struct CreateLocationRequest: Codable, Sendable {
    let name: String
    let description: String?
}

struct UpdateLocationRequest: Codable, Sendable {
    let name: String?
    let description: String?
}

// MARK: - Notification Request DTOs

struct MarkMultipleReadRequest: Codable, Sendable {
    let notificationIds: [String]

    enum CodingKeys: String, CodingKey {
        case notificationIds = "NotificationIds"
    }
}

// MARK: - Device Token Registration

struct DeviceTokenRegistration: Codable, Sendable {
    let token: String
    let platform: String
}

// MARK: - API Response Wrappers

struct PaginatedResponse<T: Codable & Sendable>: Codable, Sendable {
    let items: [T]
    let totalCount: Int
    let page: Int
    let pageSize: Int
    let totalPages: Int
}

struct APIError: Codable, Sendable {
    let title: String?
    let status: Int?
    let detail: String?
    let errors: [String: [String]]?
    // Backend CommandResult returns "message" instead of "detail"
    let message: String?
}
