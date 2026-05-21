import Foundation

@MainActor @Observable
final class NotificationsViewModel {
    var notifications: [AppNotification] = []
    var unreadCount: Int = 0
    var isLoading = false
    var errorMessage: String?
    var isViewActive = true

    private var notificationService: (any NotificationServiceProtocol)?

    func configure(notificationService: any NotificationServiceProtocol) {
        self.notificationService = notificationService
    }

    func loadNotifications() async {
        guard let notificationService, isViewActive else { return }
        isLoading = true
        errorMessage = nil

        do {
            async let notificationsTask = notificationService.listNotifications()
            async let countTask = notificationService.getUnreadCount()
            let n = try await notificationsTask
            let c = try await countTask
            guard isViewActive else { return }
            notifications = n
            unreadCount = c
        } catch {
            guard isViewActive else { return }
            errorMessage = error.localizedDescription
        }

        guard isViewActive else { return }
        isLoading = false
    }

    func markRead(id: String) async {
        guard let notificationService, isViewActive else { return }
        do {
            try await notificationService.markRead(id: id)
            guard isViewActive else { return }
            await loadNotifications()
        } catch {
            guard isViewActive else { return }
            errorMessage = error.localizedDescription
        }
    }

    func markAllRead() async {
        guard let notificationService, isViewActive else { return }
        do {
            let unreadIds = notifications.filter { !$0.isRead }.map(\.id)
            guard !unreadIds.isEmpty else { return }
            try await notificationService.markAllRead(ids: unreadIds)
            guard isViewActive else { return }
            await loadNotifications()
        } catch {
            guard isViewActive else { return }
            errorMessage = error.localizedDescription
        }
    }

    func deleteNotification(id: String) async {
        guard let notificationService, isViewActive else { return }
        do {
            let wasUnread = notifications.first(where: { $0.id == id })?.isRead == false
            try await notificationService.delete(id: id)
            guard isViewActive else { return }
            notifications.removeAll { $0.id == id }
            if wasUnread {
                unreadCount = max(0, unreadCount - 1)
            }
        } catch {
            guard isViewActive else { return }
            errorMessage = error.localizedDescription
        }
    }
}
