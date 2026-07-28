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
        guard let autoDispatchService,
              let reviewedStatus = status,
              !isMarkingReady else {
            return
        }
        isMarkingReady = true
        error = nil
        do {
            readyResult = try await autoDispatchService.markReady(
                status: reviewedStatus
            )
            guard isViewActive else { isMarkingReady = false; return }
            // Optimistically transition away from PendingReady — the backend
            // processes the state machine asynchronously so an immediate reload
            // often still returns PendingReady even though the action succeeded.
            if var optimistic = status {
                optimistic.isReady = true
                optimistic.queueDepth = max(optimistic.queueDepth - 1, 0)
                optimistic.state = "Ready"
                status = optimistic
            }
            // Keep button disabled through the reload cycle so the user sees
            // sustained feedback. Re-enable only after the authoritative reload.
            try? await Task.sleep(for: .seconds(2))
            guard isViewActive else { isMarkingReady = false; return }
            await loadStatus(printerId: printerId)
            isMarkingReady = false
        } catch let acknowledgementError as BedClearAcknowledgementError {
            if acknowledgementError.requiresReview {
                await loadStatus(printerId: printerId)
            }
            self.error = acknowledgementError.localizedDescription
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
            guard let reviewedStatus = status else {
                throw NetworkError.invalidResponse
            }
            status = try await autoDispatchService.skip(status: reviewedStatus)
        } catch {
            self.error = error.localizedDescription
        }
        isSkipping = false
    }

    func toggleEnabled(printerId: UUID) async {
        guard let autoDispatchService,
              let reviewedStatus = status else { return }
        do {
            status = try await autoDispatchService.setEnabled(
                status: reviewedStatus,
                request: SetAutoDispatchEnabledRequest(
                    enabled: !reviewedStatus.enabled
                )
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
