#if canImport(UIKit)
import Foundation
import UIKit
@preconcurrency import UserNotifications
import os

// MARK: - Push Notification Manager

/// Manages APNs registration, permission requests, and foreground notification display.
/// Singleton accessed via `PushNotificationManager.shared`.
@MainActor @Observable
final class PushNotificationManager: NSObject, @unchecked Sendable {
    static let shared = PushNotificationManager()

    // MARK: - State

    enum PermissionStatus: String, Sendable {
        case notDetermined
        case authorized
        case denied
        case provisional
    }

    private(set) var permissionStatus: PermissionStatus = .notDetermined
    private(set) var deviceToken: String?
    private(set) var registrationError: String?
    var pushEnabled: Bool {
        get { UserDefaults.standard.bool(forKey: Self.pushEnabledKey) }
        set {
            UserDefaults.standard.set(newValue, forKey: Self.pushEnabledKey)
            if newValue {
                Task { await requestPermissionAndRegister() }
            }
        }
    }

    private static let pushEnabledKey = "pf_push_notifications_enabled"
    private static let deviceTokenKey = "pf_device_token"
    private let logger = Logger(subsystem: "com.printfarmer.ios", category: "PushNotifications")

    // MARK: - Dependencies

    /// Set after login to enable server-side token registration.
    private var notificationService: (any NotificationServiceProtocol)?

    // MARK: - Init

    private override init() {
        super.init()
        // Restore cached token
        deviceToken = UserDefaults.standard.string(forKey: Self.deviceTokenKey)
    }

    // MARK: - Configuration

    func configure(notificationService: any NotificationServiceProtocol) {
        self.notificationService = notificationService
    }

    // MARK: - Permission & Registration

    func requestPermissionAndRegister() async {
        let center = UNUserNotificationCenter.current()

        do {
            let granted = try await center.requestAuthorization(options: [.alert, .badge, .sound])
            if granted {
                permissionStatus = .authorized
                registrationError = nil
                logger.info("Notification permission granted")
            } else {
                permissionStatus = .denied
                logger.info("Notification permission denied by user")
            }
        } catch {
            permissionStatus = .denied
            registrationError = error.localizedDescription
            logger.error("Failed to request notification permission: \(error.localizedDescription)")
        }
    }

    /// Check current authorization status without prompting.
    func refreshPermissionStatus() async {
        let settings = await UNUserNotificationCenter.current().notificationSettings()
        switch settings.authorizationStatus {
        case .authorized: permissionStatus = .authorized
        case .denied: permissionStatus = .denied
        case .provisional: permissionStatus = .provisional
        case .notDetermined: permissionStatus = .notDetermined
        case .ephemeral: permissionStatus = .authorized
        @unknown default: permissionStatus = .notDetermined
        }
    }

    // MARK: - Token Handling

    func didRegisterForRemoteNotifications(deviceToken data: Data) {
        let token = data.map { String(format: "%02.2hhx", $0) }.joined()
        self.deviceToken = token
        self.registrationError = nil
        UserDefaults.standard.set(token, forKey: Self.deviceTokenKey)
        logger.info("APNs device token received: \(token.prefix(8))...")

        Task { await sendTokenToServer(token) }
    }

    func didFailToRegisterForRemoteNotifications(error: Error) {
        self.registrationError = error.localizedDescription
        self.deviceToken = nil
        UserDefaults.standard.removeObject(forKey: Self.deviceTokenKey)
        logger.error("APNs registration failed: \(error.localizedDescription)")
    }

    // MARK: - Server Registration

    private func sendTokenToServer(_ token: String) async {
        guard let service = notificationService else {
            logger.warning("No notification service configured — device token not sent to server")
            return
        }

        do {
            try await service.registerDeviceToken(token, platform: "ios")
            logger.info("Device token registered with server")
        } catch {
            logger.error("Failed to register device token with server: \(error.localizedDescription)")
        }
    }

    /// Unregister the device token from the server (e.g., on logout).
    func unregisterFromServer() async {
        guard let token = deviceToken, let service = notificationService else { return }

        do {
            try await service.unregisterDeviceToken(token)
            logger.info("Device token unregistered from server")
        } catch {
            logger.error("Failed to unregister device token: \(error.localizedDescription)")
        }

        UserDefaults.standard.removeObject(forKey: Self.deviceTokenKey)
        self.deviceToken = nil
    }
}

// MARK: - UNUserNotificationCenterDelegate

extension PushNotificationManager: UNUserNotificationCenterDelegate {
    nonisolated func userNotificationCenter(
        _ center: UNUserNotificationCenter,
        willPresent notification: UNNotification,
        withCompletionHandler completionHandler: @escaping (UNNotificationPresentationOptions) -> Void
    ) {
        // Show notifications even when app is in foreground
        completionHandler([.banner, .badge, .sound])
    }

    nonisolated func userNotificationCenter(
        _ center: UNUserNotificationCenter,
        didReceive response: UNNotificationResponse,
        withCompletionHandler completionHandler: @escaping () -> Void
    ) {
        let userInfo = response.notification.request.content.userInfo
        let category = response.notification.request.content.categoryIdentifier

        if category == "PENDING_READY" {
            // Local bed-clear notification — extract printer ID and deep-link to detail
            let identifier = response.notification.request.identifier
            // Identifier format: "pending-ready-{UUID}"
            let printerId = identifier.replacingOccurrences(of: "pending-ready-", with: "")
            NotificationCenter.default.post(
                name: .localNotificationTapped,
                object: nil,
                userInfo: ["tab": "printers", "printerId": printerId]
            )
        } else {
            // Remote push notification — deep-link handling
            NotificationCenter.default.post(
                name: .pushNotificationTapped,
                object: nil,
                userInfo: userInfo
            )
        }

        completionHandler()
    }
}

// MARK: - Notification Names

extension Notification.Name {
    static let pushNotificationTapped = Notification.Name("PFPushNotificationTapped")
    static let localNotificationTapped = Notification.Name("PFLocalNotificationTapped")
}
#endif
