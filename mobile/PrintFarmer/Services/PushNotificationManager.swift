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
    private var serverRegistry: ServerRegistry?
    private var configuredServerID: UUID?
    private var configurationEpoch = 0
    private var allowsUnscopedRegistration = true
    private var registrationTask: Task<Void, Never>?
    private var registrationEpoch: UInt64 = 0
    private var pendingRemoteTap: [AnyHashable: Any]?
    private var pendingLocalTap: [AnyHashable: Any]?

    // Issue #1321: services needed to execute lock-screen/notification-center
    // actions (Pause/Resume/Cancel/Snooze) without opening the app. Kept
    // separate from `notificationService` so tests can configure only what a
    // given scenario needs.
    private var jobAttentionPrinterService: (any PrinterServiceProtocol)?
    private var jobAttentionAttentionService: (any AttentionServiceProtocol)?

    // MARK: - Init

    private override init() {
        super.init()
        // Restore cached token
        deviceToken = UserDefaults.standard.string(forKey: Self.deviceTokenKey)
    }

    // MARK: - Configuration

    func configure(
        notificationService: any NotificationServiceProtocol,
        serverRegistry: ServerRegistry? = nil,
        serverID: UUID? = nil,
        allowsUnscopedRegistration: Bool = true
    ) {
        self.notificationService = notificationService
        self.serverRegistry = serverRegistry
        self.configuredServerID = serverID
        self.allowsUnscopedRegistration = allowsUnscopedRegistration
        configurationEpoch &+= 1
        // #818: the disabled-push state is per-server. When the active server
        // changes (ServiceContainer reconfigures us with the new server's
        // service), clear the local-only signal so it is re-derived from the
        // next registration attempt rather than leaking across servers.
        self.localOnlyAlerting = false
    }

    /// Wires the services needed to execute job-attention notification
    /// actions (issue #1321). Call once services are available (e.g. after
    /// login / server selection), mirroring `configure(notificationService:)`.
    func configureActionHandling(
        printerService: any PrinterServiceProtocol,
        attentionService: any AttentionServiceProtocol
    ) {
        self.jobAttentionPrinterService = printerService
        self.jobAttentionAttentionService = attentionService
    }

    // MARK: - Notification Categories & Actions (issue #1321)
    //
    // Registers the actionable-notification category so lock-screen / long-press
    // / Notification Center actions can Pause, Resume, Cancel, Snooze, or Open
    // Swap without opening the app (except Open Swap, which foregrounds the app
    // and deep-links the same way a plain tap does). Registration only requires
    // `setNotificationCategories` — no notification permission is needed — so
    // this can and should run unconditionally at launch (`AppDelegate`).

    /// Category identifier stamped on job-attention push/local notifications.
    nonisolated static let jobAttentionCategory = "JOB_ATTENTION"

    /// Registers `UNNotificationCategory`/`UNNotificationAction`s for the
    /// job-attention category. Safe to call multiple times; the last call wins.
    static func registerNotificationCategories() {
        let pause = UNNotificationAction(
            identifier: JobAttentionAction.pauseJob.rawValue,
            title: "Pause",
            options: []
        )
        let resume = UNNotificationAction(
            identifier: JobAttentionAction.resumeJob.rawValue,
            title: "Resume",
            options: []
        )
        let cancel = UNNotificationAction(
            identifier: JobAttentionAction.cancelJob.rawValue,
            title: "Cancel",
            options: [.destructive, .authenticationRequired]
        )
        let snooze = UNNotificationAction(
            identifier: JobAttentionAction.snooze.rawValue,
            title: "Snooze",
            options: []
        )
        let openSwap = UNNotificationAction(
            identifier: JobAttentionAction.openSwap.rawValue,
            title: "Open Swap",
            options: [.foreground]
        )
        let category = UNNotificationCategory(
            identifier: jobAttentionCategory,
            actions: [pause, resume, cancel, snooze, openSwap],
            intentIdentifiers: [],
            options: []
        )
        UNUserNotificationCenter.current().setNotificationCategories([category])
    }

    /// Executes a job-attention notification action against the wired
    /// services. `userInfo` mirrors the tap-routing payload: a `printerId`
    /// (UUID string) for Pause/Resume/Cancel, and an `itemId` (attention item
    /// identifier) for Snooze. Errors are logged, never surfaced to the user —
    /// there is no UI to show one to from a background action.
    func handleJobAttentionAction(_ action: JobAttentionAction, userInfo: [AnyHashable: Any]) async {
        switch action {
        case .pauseJob:
            guard isNotificationOriginValid(userInfo, requireOrigin: true) else { return }
            let actionEpoch = configurationEpoch
            await performPrinterCommand(named: "pause", userInfo: userInfo, expectedEpoch: actionEpoch) { try await $0.pause(id: $1) }
        case .resumeJob:
            guard isNotificationOriginValid(userInfo, requireOrigin: true) else { return }
            let actionEpoch = configurationEpoch
            await performPrinterCommand(named: "resume", userInfo: userInfo, expectedEpoch: actionEpoch) { try await $0.resume(id: $1) }
        case .cancelJob:
            guard isNotificationOriginValid(userInfo, requireOrigin: true) else { return }
            let actionEpoch = configurationEpoch
            await performPrinterCommand(named: "cancel", userInfo: userInfo, expectedEpoch: actionEpoch) { try await $0.cancel(id: $1) }
        case .snooze:
            guard isNotificationOriginValid(userInfo, requireOrigin: true) else { return }
            let actionEpoch = configurationEpoch
            await performSnooze(userInfo: userInfo, expectedEpoch: actionEpoch)
        case .openSwap:
            // Foreground action (#1321): behaves like the existing tap-to-open
            // deep-link routing so it lands on the printer detail where the
            // guided filament swap lives — mirrors `didReceive response:`'s
            // default-tap branch below.
            let actionEpoch = configurationEpoch
            guard isNotificationOriginValid(userInfo, requireOrigin: true),
                  configurationEpoch == actionEpoch else { return }
            NotificationCenter.default.post(
                name: .pushNotificationTapped,
                object: nil,
                userInfo: userInfo
            )
        }
    }

    private func isNotificationOriginValid(
        _ userInfo: [AnyHashable: Any],
        requireOrigin: Bool
    ) -> Bool {
        // Legacy origin-less payloads remain parseable for passive deep links,
        // but mutating actions fail closed because they cannot prove ownership.
        guard requireOrigin else { return true }
        guard let serverRegistry else {
            logger.warning("Job-attention action ignored — server context is unavailable")
            return false
        }
        guard let activeServer = serverRegistry.activeServer,
              let expectedOrigin = activeServer.originServerId,
              let originValue = userInfo["originServerId"] as? String,
              let originServerId = UUID(uuidString: originValue) else {
            logger.warning("Job-attention action ignored — notification origin is unavailable")
            return false
        }
        guard configuredServerID == activeServer.id else {
            logger.warning("Job-attention action ignored — server services are still switching")
            return false
        }
        guard originServerId == expectedOrigin else {
            logger.warning("Job-attention action ignored — notification belongs to another server")
            return false
        }
        return true
    }

    private func performPrinterCommand(
        named actionName: String,
        userInfo: [AnyHashable: Any],
        expectedEpoch: Int,
        _ operation: (any PrinterServiceProtocol, UUID) async throws -> CommandResult
    ) async {
        guard let printerService = jobAttentionPrinterService else {
            logger.warning("Job-attention \(actionName) action ignored — no printer service configured")
            return
        }
        guard let printerIdString = userInfo["printerId"] as? String,
              let printerId = UUID(uuidString: printerIdString) else {
            logger.warning("Job-attention \(actionName) action ignored — missing/invalid printerId")
            return
        }
        guard configurationEpoch == expectedEpoch else {
            logger.warning("Job-attention \(actionName) action ignored — server changed before execution")
            return
        }
        do {
            _ = try await operation(printerService, printerId)
            logger.info("Job-attention \(actionName) action executed for printer \(printerId)")
        } catch {
            logger.error("Job-attention \(actionName) action failed: \(error.localizedDescription)")
        }
    }

    private func performSnooze(userInfo: [AnyHashable: Any], expectedEpoch: Int) async {
        guard let attentionService = jobAttentionAttentionService else {
            logger.warning("Job-attention snooze action ignored — no attention service configured")
            return
        }
        guard let itemId = userInfo["itemId"] as? String, !itemId.isEmpty else {
            logger.warning("Job-attention snooze action ignored — missing itemId")
            return
        }
        guard configurationEpoch == expectedEpoch else {
            logger.warning("Job-attention snooze action ignored — server changed before execution")
            return
        }
        do {
            _ = try await attentionService.snooze(
                itemId: itemId,
                snoozedUntilUtc: Date().addingTimeInterval(Self.defaultSnoozeInterval)
            )
            logger.info("Job-attention snooze action executed for item \(itemId)")
        } catch {
            logger.error("Job-attention snooze action failed: \(error.localizedDescription)")
        }
    }

    /// One hour, matching the in-app Attention feed's default snooze duration.
    private static let defaultSnoozeInterval: TimeInterval = 60 * 60

    // MARK: - Permission & Registration

    func requestPermissionAndRegister() async {
        let center = UNUserNotificationCenter.current()

        do {
            let granted = try await center.requestAuthorization(options: [.alert, .badge, .sound])
            if granted {
                permissionStatus = .authorized
                registrationError = nil
                logger.info("Notification permission granted")
                UIApplication.shared.registerForRemoteNotifications()
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

        startTokenRegistration(token)
    }

    func didFailToRegisterForRemoteNotifications(error: Error) {
        registrationEpoch &+= 1
        registrationTask?.cancel()
        registrationTask = nil
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
    func registerTokenWithServer(
        _ token: String,
        expectedRegistrationEpoch: UInt64? = nil
    ) async {
        if let expectedRegistrationEpoch,
           expectedRegistrationEpoch != registrationEpoch {
            return
        }
        guard let service = notificationService else {
            logger.warning("No notification service configured — device token not sent to server")
            return
        }

        if let serverRegistry {
            guard let configuredServerID,
                  serverRegistry.activeServerID == configuredServerID else {
                logger.warning("Device token registration ignored — no active server context")
                return
            }
        } else {
            guard allowsUnscopedRegistration else {
                logger.warning("Device token registration ignored — no active server context")
                return
            }
        }
        let initiatingServerID = configuredServerID
        let initiatingConfigurationEpoch = configurationEpoch
        do {
            let originServerId = try await service.registerDeviceToken(token, platform: "ios")
            if let expectedRegistrationEpoch,
               expectedRegistrationEpoch != registrationEpoch {
                return
            }
            guard deviceToken == nil || deviceToken == token else { return }
            if let serverRegistry,
               let initiatingServerID,
               configurationEpoch == initiatingConfigurationEpoch,
               configuredServerID == initiatingServerID,
               serverRegistry.activeServerID == initiatingServerID {
                try serverRegistry.associateOriginServerId(originServerId, with: initiatingServerID)
            }
            guard configurationEpoch == initiatingConfigurationEpoch,
                  configuredServerID == initiatingServerID,
                  serverRegistry?.activeServerID == initiatingServerID else {
                return
            }
            localOnlyAlerting = false
            registrationError = nil
            logger.info("Device token registered with server")
        } catch NetworkError.featureDisabled {
            guard configurationEpoch == initiatingConfigurationEpoch,
                  expectedRegistrationEpoch == nil || expectedRegistrationEpoch == registrationEpoch,
                  (deviceToken == nil || deviceToken == token),
                  configuredServerID == initiatingServerID,
                  serverRegistry?.activeServerID == initiatingServerID else {
                return
            }
            // Expected on the push-disabled beta default. Benign, not an error.
            localOnlyAlerting = true
            registrationError = nil
            logger.info("Native push disabled on this server; operating in local-only alerting mode (SignalR + local notifications)")
        } catch {
            logger.error("Failed to register device token with server: \(error.localizedDescription)")
        }
    }

    func startTokenRegistration(_ token: String) {
        registrationTask?.cancel()
        registrationEpoch &+= 1
        let expectedRegistrationEpoch = registrationEpoch
        registrationTask = Task { [weak self] in
            await self?.registerTokenWithServer(
                token,
                expectedRegistrationEpoch: expectedRegistrationEpoch
            )
        }
    }

    @MainActor
    func consumePendingRemoteTap() -> [AnyHashable: Any]? {
            defer { pendingRemoteTap = nil }
            return pendingRemoteTap
        }

    @MainActor
    func consumePendingLocalTap() -> [AnyHashable: Any]? {
            defer { pendingLocalTap = nil }
            return pendingLocalTap
        }

    @MainActor
    private func enqueueRemoteTap(_ userInfo: [AnyHashable: Any]) {
            pendingRemoteTap = userInfo
            NotificationCenter.default.post(name: .pushNotificationTapped, object: nil, userInfo: userInfo)
        }

    @MainActor
    private func enqueueLocalTap(_ userInfo: [AnyHashable: Any]) {
            pendingLocalTap = userInfo
            NotificationCenter.default.post(name: .localNotificationTapped, object: nil, userInfo: userInfo)
    }

    /// Unregister the device token from the server (e.g., on logout).
    @discardableResult
    func unregisterFromServer(clearLocalToken: Bool = true) async -> Bool {
        registrationTask?.cancel()
        await registrationTask?.value
        registrationTask = nil
        guard let token = deviceToken else { return true }
        guard let service = notificationService else { return false }
        let unregisterEpoch = configurationEpoch
        var succeeded = true

        do {
            try await service.unregisterDeviceToken(token)
            logger.info("Device token unregistered from server")
        } catch NetworkError.featureDisabled {
            // #818: server has native push disabled — nothing was registered, so
            // a no-op unregister is expected. Not an error.
            logger.info("Native push disabled on this server; skipping token unregistration (nothing to remove)")
        } catch {
            succeeded = false
            logger.error("Failed to unregister device token: \(error.localizedDescription)")
        }

        if succeeded && clearLocalToken,
           configurationEpoch == unregisterEpoch,
           deviceToken == token {
            UserDefaults.standard.removeObject(forKey: Self.deviceTokenKey)
            self.deviceToken = nil
        }
        return succeeded
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
        withCompletionHandler completionHandler: @escaping @Sendable () -> Void
    ) {
        let userInfo = response.notification.request.content.userInfo
        let category = response.notification.request.content.categoryIdentifier
        let actionIdentifier = response.actionIdentifier
        if actionIdentifier == UNNotificationDismissActionIdentifier {
            completionHandler()
            return
        }

        // Issue #1321: a job-attention action button (Pause/Resume/Cancel/
        // Snooze/Open Swap) was tapped rather than the notification body
        // itself. Dispatch to the wired services and skip the default
        // tap/dismiss routing below — Open Swap is the one exception, and it
        // performs that same routing itself via `handleJobAttentionAction`.
        if category == Self.jobAttentionCategory,
           let action = JobAttentionAction(rawValue: actionIdentifier) {
            Task { @MainActor in
                await PushNotificationManager.shared.handleJobAttentionAction(action, userInfo: userInfo)
                completionHandler()
            }
            return
        }

        if category == "PENDING_READY" {
            // Local bed-clear notification — extract printer ID and deep-link to detail
            let identifier = response.notification.request.identifier
            // Identifier format: "pending-ready-{UUID}"
            let printerId = identifier.replacingOccurrences(of: "pending-ready-", with: "")
            Task { @MainActor in
                PushNotificationManager.shared.enqueueLocalTap(
                    ["tab": "printers", "printerId": printerId]
                )
                completionHandler()
            }
            return
        } else {
            // Remote push notification — deep-link handling
            Task { @MainActor in
                PushNotificationManager.shared.enqueueRemoteTap(userInfo)
                completionHandler()
            }
            return
        }

        completionHandler()
    }
}

// MARK: - Job Attention Actions (issue #1321)

/// Notification action identifiers registered on `PushNotificationManager
/// .jobAttentionCategory`. Raw values match the wire identifiers the backend
/// push payload is expected to use, and the ones referenced by issue #1321.
enum JobAttentionAction: String, Sendable {
    case pauseJob = "PAUSE_JOB"
    case resumeJob = "RESUME_JOB"
    case cancelJob = "CANCEL_JOB"
    case snooze = "SNOOZE"
    case openSwap = "OPEN_SWAP"
}

// MARK: - Notification Names

extension Notification.Name {
    static let pushNotificationTapped = Notification.Name("PFPushNotificationTapped")
    static let localNotificationTapped = Notification.Name("PFLocalNotificationTapped")
}
#endif
