import Foundation
@testable import PrintFarmer

final class MockNotificationService: NotificationServiceProtocol, @unchecked Sendable {
    var notificationsToReturn: [AppNotification] = []
    var unreadCountToReturn: Int = 0
    var errorToThrow: Error?

    // Call tracking
    var listCalledWithLimit: Int?
    var unreadCountCalled = false
    var markReadCalledWith: String?
    var markAllReadCalledWith: [String]?
    var deleteCalledWith: String?

    func listNotifications(limit: Int? = 50) async throws -> [AppNotification] {
        listCalledWithLimit = limit
        if let error = errorToThrow { throw error }
        return notificationsToReturn
    }

    func getUnreadCount() async throws -> Int {
        unreadCountCalled = true
        if let error = errorToThrow { throw error }
        return unreadCountToReturn
    }

    func markRead(id: String) async throws {
        markReadCalledWith = id
        if let error = errorToThrow { throw error }
    }

    func markAllRead(ids: [String]) async throws {
        markAllReadCalledWith = ids
        if let error = errorToThrow { throw error }
    }

    func delete(id: String) async throws {
        deleteCalledWith = id
        if let error = errorToThrow { throw error }
    }
}
