import Foundation

// MARK: - Notification Service Protocol

protocol NotificationServiceProtocol: Sendable {
    func listNotifications(limit: Int?) async throws -> [AppNotification]
    func getUnreadCount() async throws -> Int
    func markRead(id: String) async throws
    func markAllRead(ids: [String]) async throws
    func delete(id: String) async throws
    func registerDeviceToken(_ token: String, platform: String) async throws
    func unregisterDeviceToken(_ token: String) async throws
}

extension NotificationServiceProtocol {
    func listNotifications() async throws -> [AppNotification] {
        try await listNotifications(limit: 50)
    }
}
