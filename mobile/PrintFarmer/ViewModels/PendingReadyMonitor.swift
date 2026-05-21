import Foundation
import os
#if canImport(UserNotifications)
import UserNotifications
#endif

@MainActor @Observable
final class PendingReadyMonitor {
    var pendingReadyCount: Int = 0
    private var pollingTask: Task<Void, Never>?
    private var autoPrintService: (any AutoDispatchServiceProtocol)?
    private var printerService: (any PrinterServiceProtocol)?

    /// Printer IDs for which we've already fired a local notification.
    /// Cleared when a printer leaves PendingReady so it can re-notify.
    private var notifiedPrinterIds: Set<UUID> = []

    private let logger = Logger(subsystem: "com.printfarmer.ios", category: "PendingReadyMonitor")
    private static let notificationCategory = "PENDING_READY"

    func configure(
        autoPrintService: any AutoDispatchServiceProtocol,
        printerService: any PrinterServiceProtocol
    ) {
        self.autoPrintService = autoPrintService
        self.printerService = printerService
    }

    func startMonitoring() {
        stopMonitoring()

        pollingTask = Task {
            while !Task.isCancelled {
                await updatePendingReadyCount()

                do {
                    try await Task.sleep(for: .seconds(10))
                } catch {
                    break
                }
            }
        }
    }

    func stopMonitoring() {
        pollingTask?.cancel()
        pollingTask = nil
        let idsToClean = notifiedPrinterIds
        notifiedPrinterIds.removeAll()
        Task { [weak self] in
            guard let self else { return }
            await self.updateBadgeCount(0)
            await self.removeDeliveredNotifications(for: idsToClean)
        }
    }

    // MARK: - Notification Permission

    /// Request local notification permission. Call once at first launch.
    func requestNotificationPermission() async {
        #if canImport(UserNotifications)
        let center = UNUserNotificationCenter.current()
        do {
            let granted = try await center.requestAuthorization(options: [.alert, .badge, .sound])
            logger.info("Notification permission \(granted ? "granted" : "denied")")
        } catch {
            logger.error("Failed to request notification permission: \(error.localizedDescription)")
        }
        #endif
    }

    // MARK: - Polling

    private func updatePendingReadyCount() async {
        guard let autoPrintService else { return }

        do {
            let statuses = try await autoPrintService.getAllStatus()
            let pendingReadyStatuses = statuses.printers.filter { $0.state == "PendingReady" }
            let currentPendingIds = Set(pendingReadyStatuses.map(\.printerId))

            pendingReadyCount = pendingReadyStatuses.count

            // Update app badge count to reflect pending printers
            await updateBadgeCount(pendingReadyCount)

            // Remove delivered notifications for printers that left PendingReady
            let clearedIds = notifiedPrinterIds.subtracting(currentPendingIds)
            if !clearedIds.isEmpty {
                await removeDeliveredNotifications(for: clearedIds)
            }
            notifiedPrinterIds = notifiedPrinterIds.intersection(currentPendingIds)

            // Find newly-entered PendingReady printers
            let newPrinterIds = currentPendingIds.subtracting(notifiedPrinterIds)
            if !newPrinterIds.isEmpty {
                await sendLocalNotification(for: newPrinterIds)
                notifiedPrinterIds.formUnion(newPrinterIds)
            }
        } catch {
            // Silently handle errors in background polling
        }
    }

    // MARK: - Local Notification

    private func sendLocalNotification(for printerIds: Set<UUID>) async {
        #if canImport(UserNotifications)
        let nameMap = await resolveNameMap(for: printerIds)
        let center = UNUserNotificationCenter.current()

        for printerId in printerIds {
            let name = nameMap[printerId] ?? printerId.uuidString.prefix(8).description
            let content = UNMutableNotificationContent()
            content.title = "Bed Clear Required"
            content.body = "\(name) is ready for the next job — clear the bed to continue."
            content.sound = .default
            content.categoryIdentifier = Self.notificationCategory
            content.badge = NSNumber(value: pendingReadyCount)

            // Use printer ID as identifier so we can replace/remove per printer
            let request = UNNotificationRequest(
                identifier: Self.notificationIdentifier(for: printerId),
                content: content,
                trigger: nil
            )

            do {
                try await center.add(request)
                logger.info("Local notification sent for printer \(printerId)")
            } catch {
                logger.error("Failed to schedule local notification: \(error.localizedDescription)")
            }
        }
        #endif
    }

    private func removeDeliveredNotifications(for printerIds: Set<UUID>) async {
        #if canImport(UserNotifications)
        let ids = printerIds.map { Self.notificationIdentifier(for: $0) }
        UNUserNotificationCenter.current().removeDeliveredNotifications(withIdentifiers: ids)
        UNUserNotificationCenter.current().removePendingNotificationRequests(withIdentifiers: ids)
        logger.info("Removed notifications for \(printerIds.count) printer(s) that left PendingReady")
        #endif
    }

    private static func notificationIdentifier(for printerId: UUID) -> String {
        "pending-ready-\(printerId.uuidString)"
    }

    // MARK: - Badge Count

    private func updateBadgeCount(_ count: Int) async {
        #if canImport(UserNotifications)
        do {
            try await UNUserNotificationCenter.current().setBadgeCount(count)
        } catch {
            logger.error("Failed to update badge count: \(error.localizedDescription)")
        }
        #endif
    }

    /// Resolve printer UUIDs to a name map, falling back to short ID.
    private func resolveNameMap(for ids: Set<UUID>) async -> [UUID: String] {
        guard let printerService else {
            return Dictionary(uniqueKeysWithValues: ids.map { ($0, $0.uuidString.prefix(8).description) })
        }

        do {
            let printers = try await printerService.list(includeDisabled: true)
            let nameMap = Dictionary(uniqueKeysWithValues: printers.map { ($0.id, $0.name) })
            return Dictionary(uniqueKeysWithValues: ids.map { ($0, nameMap[$0] ?? $0.uuidString.prefix(8).description) })
        } catch {
            return Dictionary(uniqueKeysWithValues: ids.map { ($0, $0.uuidString.prefix(8).description) })
        }
    }
}
