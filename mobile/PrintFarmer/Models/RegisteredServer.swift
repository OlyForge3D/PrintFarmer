import Foundation

struct RegisteredServer: Identifiable, Codable, Sendable, Equatable {
    var id: UUID
    var displayName: String
    var baseURL: URL
    var normalizedURLString: String
    var lastKnownStatus: String?
    var lastCheckedAt: Date?
    var lastAuthenticatedUsername: String?
    var createdAt: Date
    var updatedAt: Date

    init(
        id: UUID = UUID(),
        displayName: String,
        baseURL: URL,
        normalizedURLString: String,
        lastKnownStatus: String? = nil,
        lastCheckedAt: Date? = nil,
        lastAuthenticatedUsername: String? = nil,
        createdAt: Date = Date(),
        updatedAt: Date = Date()
    ) {
        self.id = id
        self.displayName = displayName
        self.baseURL = baseURL
        self.normalizedURLString = normalizedURLString
        self.lastKnownStatus = lastKnownStatus
        self.lastCheckedAt = lastCheckedAt
        self.lastAuthenticatedUsername = lastAuthenticatedUsername
        self.createdAt = createdAt
        self.updatedAt = updatedAt
    }
}
