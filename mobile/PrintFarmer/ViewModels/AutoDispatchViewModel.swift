import Foundation
import os

@MainActor @Observable
final class AutoDispatchViewModel {
    var status: AutoDispatchStatus?
    var readyResult: AutoDispatchReadyResult?
    var isLoading = false
    var isMarkingReady = false
    var isSkipping = false
    var error: String?
    var isViewActive = true

    private let logger = Logger(subsystem: "com.printfarmer.ios", category: "AutoDispatch")
    private var autoDispatchService: (any AutoDispatchServiceProtocol)?

    func configure(autoDispatchService: any AutoDispatchServiceProtocol) {
        self.autoDispatchService = autoDispatchService
    }

    func loadStatus(printerId: UUID) async {
        guard let autoDispatchService, isViewActive else { return }
        isLoading = true
        error = nil

        do {
            status = try await autoDispatchService.getStatus(printerId: printerId)
        } catch {
            self.error = error.localizedDescription
        }

        isLoading = false
    }

    func markReady(printerId: UUID) async {
        guard let autoDispatchService, !isMarkingReady else {
            return
        }
        isMarkingReady = true
        error = nil
        do {
            readyResult = try await autoDispatchService.markReady(printerId: printerId)
            guard isViewActive else { isMarkingReady = false; return }
            // Optimistically transition away from PendingReady — the backend
            // processes the state machine asynchronously so an immediate reload
            // often still returns PendingReady even though the action succeeded.
            if let s = status {
                status = AutoDispatchStatus(
                    printerId: s.printerId,
                    printerName: s.printerName,
                    enabled: s.enabled,
                    isReady: true,
                    currentJobName: s.currentJobName,
                    queueDepth: max(s.queueDepth - 1, 0),
                    readyGateChecks: s.readyGateChecks,
                    lastActivity: s.lastActivity,
                    state: "Ready",
                    bedPreConfirmed: s.bedPreConfirmed,
                    attentionMessage: s.attentionMessage
                )
            }
            // Keep button disabled through the reload cycle so the user sees
            // sustained feedback. Re-enable only after the authoritative reload.
            try? await Task.sleep(for: .seconds(2))
            guard isViewActive else { isMarkingReady = false; return }
            await loadStatus(printerId: printerId)
            isMarkingReady = false
        } catch {
            self.error = error.localizedDescription
            isMarkingReady = false
        }
    }

    func skip(printerId: UUID) async {
        guard let autoDispatchService else {
            isSkipping = false
            return
        }
        // isSkipping is set synchronously by the caller before this Task starts
        error = nil
        do {
            status = try await autoDispatchService.skip(printerId: printerId)
        } catch {
            self.error = error.localizedDescription
        }
        isSkipping = false
    }

    func toggleEnabled(printerId: UUID) async {
        guard let autoDispatchService, let currentlyEnabled = status?.enabled else { return }
        do {
            status = try await autoDispatchService.setEnabled(
                printerId: printerId,
                request: SetAutoDispatchEnabledRequest(enabled: !currentlyEnabled)
            )
        } catch {
            self.error = error.localizedDescription
        }
    }

    // MARK: - Computed

    var isEnabled: Bool? { status?.enabled }
    var currentState: String? { status?.state }

    var parsedState: AutoDispatchState? {
        guard let stateStr = status?.state else { return nil }
        return AutoDispatchState(rawValue: stateStr)
    }
}
