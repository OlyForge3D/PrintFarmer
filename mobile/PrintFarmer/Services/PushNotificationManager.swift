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

    /// Issue #818: `true` when the currently selected server reported native push
    /// as disabled (`code == "featureDisabled"`) on the last device-token
    /// registration attempt. This is a benign, expected state — the beta ships
    /// with push off by default — not an error: alerts continue to arrive via
    /// SignalR + on-device local notifications. Exposed as a lightweight
    /// "local-only alerting" signal for support/diagnostics; it never drives a
    /// user-facing error and is re-evaluated per server on the next registration.
    private(set) var localOnlyAlerting: Bool = false
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
        // #818: the disabled-push state is per-server. When the active server
        // changes (ServiceContainer reconfigures us with the new server's
        // service), clear the local-only signal so it is re-derived from the
        // next registration attempt rather than leaking across servers.
        self.localOnlyAlerting = false
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

        Task { await registerTokenWithServer(token) }
    }

    func didFailToRegisterForRemoteNotifications(error: Error) {
        self.registrationError = error.localizedDescription
        self.deviceToken = nil
        UserDefaults.standard.removeObject(forKey: Self.deviceTokenKey)
        logger.error("APNs registration failed: \(error.localizedDescription)")
    }

    // MARK: - Server Registration

    /// Register the APNs token with the backend. Awaitable so callers (and tests)
    /// can observe completion deterministically; `didRegisterForRemoteNotifications`
    /// drives it from a detached Task.
    ///
    /// #818: a `NetworkError.featureDisabled` response (native push off on this
    /// server) is treated as a normal "push not configured" outcome — no
    /// user-visible error, no retry — and flips the app into local-only alerting
    /// mode. A successful registration clears that signal (clean re-enable path).
    func registerTokenWithServer(_ token: String) async {
        guard let service = notificationService else {
            logger.warning("No notification service configured — device token not sent to server")
            return
        }

        do {
            try await service.registerDeviceToken(token, platform: "ios")
            localOnlyAlerting = false
            registrationError = nil
            logger.info("Device token registered with server")
        } catch NetworkError.featureDisabled {
            // Expected on the push-disabled beta default. Benign, not an error.
            localOnlyAlerting = true
            registrationError = nil
            logger.info("Native push disabled on this server; operating in local-only alerting mode (SignalR + local notifications)")
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
        } catch NetworkError.featureDisabled {
            // #818: server has native push disabled — nothing was registered, so
            // a no-op unregister is expected. Not an error.
            logger.info("Native push disabled on this server; skipping token unregistration (nothing to remove)")
        } catch {
            logger.error("Failed to unregister device token: \(error.localizedDescription)")
        }

        UserDefaults.standard.removeObject(forKey: Self.deviceTokenKey)
        self.deviceToken = nil
    }
}

// MARK: - UNUserNotificationCenterDelegate

extension PushNotificationManager: UNUserNotificationCenterDelegate {
    /// Foreground presentation options for an incoming notification. Held as a
    /// pure, `nonisolated` helper so tests can assert (issue #818) that live
    /// foreground alerting — banner/badge/sound — is always presented regardless
    /// of whether remote native push is disabled on the server. Local
    /// notifications (SignalR-driven bed-clear alerts, `PendingReadyMonitor`) and
    /// foreground presentation never depend on device-token registration.
    nonisolated static func foregroundPresentationOptions() -> UNNotificationPresentationOptions {
        [.banner, .badge, .sound]
    }

    nonisolated func userNotificationCenter(
        _ center: UNUserNotificationCenter,
        willPresent notification: UNNotification,
        withCompletionHandler completionHandler: @escaping (UNNotificationPresentationOptions) -> Void
    ) {
        // Show notifications even when app is in foreground
        completionHandler(Self.foregroundPresentationOptions())
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
