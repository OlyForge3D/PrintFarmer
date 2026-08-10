import Foundation

struct RegisteredServer: Identifiable, Codable, Sendable, Equatable {
    var id: UUID
    var originServerId: UUID?
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
        originServerId: UUID? = nil,
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
        self.originServerId = originServerId
        self.displayName = displayName
        self.baseURL = baseURL
        self.normalizedURLString = normalizedURLString
        self.lastKnownStatus = lastKnownStatus
        self.lastCheckedAt = lastCheckedAt
        self.lastAuthenticatedUsername = lastAuthenticatedUsername
        self.createdAt = createdAt
        self.updatedAt = updatedAt
    }

    private enum CodingKeys: String, CodingKey {
        case id
        case originServerId
        case displayName
        case baseURL
        case normalizedURLString
        case lastKnownStatus
        case lastCheckedAt
        case lastAuthenticatedUsername
        case createdAt
        case updatedAt
    }

    init(from decoder: Decoder) throws {
        let values = try decoder.container(keyedBy: CodingKeys.self)
        id = try values.decode(UUID.self, forKey: .id)
        originServerId = try values.decodeIfPresent(UUID.self, forKey: .originServerId)
        displayName = try values.decode(String.self, forKey: .displayName)
        baseURL = try values.decode(URL.self, forKey: .baseURL)
        normalizedURLString = try values.decode(String.self, forKey: .normalizedURLString)
        lastKnownStatus = try values.decodeIfPresent(String.self, forKey: .lastKnownStatus)
        lastCheckedAt = try values.decodeIfPresent(Date.self, forKey: .lastCheckedAt)
        lastAuthenticatedUsername = try values.decodeIfPresent(String.self, forKey: .lastAuthenticatedUsername)
        createdAt = try values.decode(Date.self, forKey: .createdAt)
        updatedAt = try values.decode(Date.self, forKey: .updatedAt)
    }
}
