import Foundation

// MARK: - Demo Notification Service

final class DemoNotificationService: NotificationServiceProtocol, @unchecked Sendable {
    private var notifications = DemoData.notifications

    func listNotifications(limit: Int?) async throws -> [AppNotification] {
        let max = limit ?? 50
        return Array(notifications.prefix(max))
    }

    func getUnreadCount() async throws -> Int {
        notifications.filter { !$0.isRead }.count
    }

    func markRead(id: String) async throws {
        // No-op in demo
    }

    func markAllRead(ids: [String]) async throws {
        // No-op in demo
    }

    func delete(id: String) async throws {
        // No-op in demo
    }

    func registerDeviceToken(_ token: String, platform: String) async throws {
        // No-op in demo
    }

    func unregisterDeviceToken(_ token: String) async throws {
        // No-op in demo
    }
}
