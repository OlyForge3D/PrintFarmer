#if canImport(UIKit)
import Foundation
@preconcurrency import UserNotifications

// MARK: - Local Notification Manager

/// Presents and routes the v1 app's on-device `PENDING_READY` notifications.
/// Singleton accessed via `PushNotificationManager.shared`.
@MainActor
final class PushNotificationManager: NSObject, @unchecked Sendable {
    static let shared = PushNotificationManager()

    nonisolated private static let pendingReadyCategory = "PENDING_READY"
    nonisolated private static let pendingReadyIdentifierPrefix = "pending-ready-"

    private var pendingLocalTap: [AnyHashable: Any]?

    func consumePendingLocalTap() -> [AnyHashable: Any]? {
        defer { pendingLocalTap = nil }
        return pendingLocalTap
    }

    private func enqueuePendingReadyTap(printerId: String) {
        let userInfo: [AnyHashable: Any] = [
            "tab": "printers",
            "printerId": printerId
        ]
        pendingLocalTap = userInfo
        NotificationCenter.default.post(
            name: .localNotificationTapped,
            object: nil,
            userInfo: userInfo
        )
    }

    nonisolated static func pendingReadyPrinterId(
        categoryIdentifier: String,
        requestIdentifier: String
    ) -> String? {
        guard categoryIdentifier == pendingReadyCategory,
              requestIdentifier.hasPrefix(pendingReadyIdentifierPrefix) else {
            return nil
        }

        let printerId = String(requestIdentifier.dropFirst(pendingReadyIdentifierPrefix.count))
        return UUID(uuidString: printerId) == nil ? nil : printerId
    }
}

// MARK: - UNUserNotificationCenterDelegate

extension PushNotificationManager: UNUserNotificationCenterDelegate {
    /// Foreground presentation options for a local notification.
    nonisolated static func foregroundPresentationOptions() -> UNNotificationPresentationOptions {
        [.banner, .badge, .sound]
    }

    nonisolated func userNotificationCenter(
        _: UNUserNotificationCenter,
        willPresent _: UNNotification,
        withCompletionHandler completionHandler: @escaping (UNNotificationPresentationOptions) -> Void
    ) {
        completionHandler(Self.foregroundPresentationOptions())
    }

    nonisolated func userNotificationCenter(
        _: UNUserNotificationCenter,
        didReceive response: UNNotificationResponse,
        withCompletionHandler completionHandler: @escaping @Sendable () -> Void
    ) {
        guard response.actionIdentifier != UNNotificationDismissActionIdentifier else {
            completionHandler()
            return
        }

        let request = response.notification.request
        guard let printerId = Self.pendingReadyPrinterId(
            categoryIdentifier: request.content.categoryIdentifier,
            requestIdentifier: request.identifier
        ) else {
            completionHandler()
            return
        }

        Task { @MainActor in
            PushNotificationManager.shared.enqueuePendingReadyTap(printerId: printerId)
            completionHandler()
        }
    }
}

// MARK: - Notification Names

extension Notification.Name {
    static let localNotificationTapped = Notification.Name("PFLocalNotificationTapped")
}
#endif
