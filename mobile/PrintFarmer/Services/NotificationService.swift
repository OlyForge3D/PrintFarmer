import Foundation

// MARK: - Notification Service

actor NotificationService: NotificationServiceProtocol {
    private let apiClient: APIClient

    init(apiClient: APIClient) {
        self.apiClient = apiClient
    }

    func listNotifications(limit: Int? = 50) async throws -> [AppNotification] {
        let query = limit.map { "?limit=\($0)" } ?? ""
        return try await apiClient.get("/api/notifications\(query)")
    }

    func getUnreadCount() async throws -> Int {
        let response: UnreadCountResponse = try await apiClient.get("/api/notifications/unread/count")
        return response.unreadCount
    }

    func markRead(id: String) async throws {
        try await apiClient.putVoid("/api/notifications/\(id)/mark-read")
    }

    func markAllRead(ids: [String]) async throws {
        let request = MarkMultipleReadRequest(notificationIds: ids)
        try await apiClient.putVoid("/api/notifications/mark-read-batch", body: request)
    }

    func delete(id: String) async throws {
        try await apiClient.delete("/api/notifications/\(id)")
    }
}
