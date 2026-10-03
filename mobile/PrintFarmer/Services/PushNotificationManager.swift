#if canImport(UIKit)
import Foundation
@preconcurrency import UserNotifications
import os

// MARK: - Push Notification Manager

/// Manages local notification categories, actions, presentation, and tap routing.
/// Singleton accessed via `PushNotificationManager.shared`.
@MainActor @Observable
final class PushNotificationManager: NSObject, @unchecked Sendable {
    static let shared = PushNotificationManager()

    private let logger = Logger(subsystem: "com.printfarmer.ios", category: "PushNotifications")

    // MARK: - Dependencies

    private var serverRegistry: ServerRegistry?
    private var configuredServerID: UUID?
    private var configurationEpoch = 0
    private var pendingNotificationTap: [AnyHashable: Any]?
    private var pendingLocalTap: [AnyHashable: Any]?

    // Issue #1321: services needed to execute lock-screen/notification-center
    // actions (Pause/Resume/Cancel/Snooze) without opening the app.
    private var jobAttentionPrinterService: (any PrinterServiceProtocol)?
    private var jobAttentionAttentionService: (any AttentionServiceProtocol)?

    // MARK: - Configuration

    func configure(
        serverRegistry: ServerRegistry? = nil,
        serverID: UUID? = nil
    ) {
        self.serverRegistry = serverRegistry
        self.configuredServerID = serverID
        configurationEpoch &+= 1
    }

    /// Wires the services needed to execute job-attention notification
    /// actions (issue #1321). Call once services are available (e.g. after
    /// login or server selection).
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

    /// Category identifier stamped on job-attention local notifications.
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
            enqueueNotificationTap(userInfo)
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

    @MainActor
    func consumePendingNotificationTap() -> [AnyHashable: Any]? {
        defer { pendingNotificationTap = nil }
        return pendingNotificationTap
    }

    @MainActor
    func consumePendingLocalTap() -> [AnyHashable: Any]? {
        defer { pendingLocalTap = nil }
        return pendingLocalTap
    }

    @MainActor
    private func enqueueNotificationTap(_ userInfo: [AnyHashable: Any]) {
        pendingNotificationTap = userInfo
        NotificationCenter.default.post(name: .notificationTapped, object: nil, userInfo: userInfo)
    }

    @MainActor
    private func enqueueLocalTap(_ userInfo: [AnyHashable: Any]) {
        pendingLocalTap = userInfo
        NotificationCenter.default.post(name: .localNotificationTapped, object: nil, userInfo: userInfo)
    }
}

// MARK: - UNUserNotificationCenterDelegate

extension PushNotificationManager: UNUserNotificationCenterDelegate {
    /// Foreground presentation options for an incoming notification. Held as a
    /// pure, `nonisolated` helper so tests can assert that local notifications
    /// remain visible while the app is in the foreground.
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
            // Other local notification — use the shared deep-link handling.
            Task { @MainActor in
                PushNotificationManager.shared.enqueueNotificationTap(userInfo)
                completionHandler()
            }
            return
        }
    }
}

// MARK: - Job Attention Actions (issue #1321)

/// Notification action identifiers registered on `PushNotificationManager
/// .jobAttentionCategory`. Raw values match the notification action identifiers
/// referenced by issue #1321.
enum JobAttentionAction: String, Sendable {
    case pauseJob = "PAUSE_JOB"
    case resumeJob = "RESUME_JOB"
    case cancelJob = "CANCEL_JOB"
    case snooze = "SNOOZE"
    case openSwap = "OPEN_SWAP"
}

// MARK: - Notification Names

extension Notification.Name {
    static let notificationTapped = Notification.Name("PFNotificationTapped")
    static let localNotificationTapped = Notification.Name("PFLocalNotificationTapped")
}
#endif
