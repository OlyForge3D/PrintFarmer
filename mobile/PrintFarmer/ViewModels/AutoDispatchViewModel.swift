import Foundation
import os

@MainActor @Observable
final class AutoDispatchViewModel {
    var status: AutoDispatchStatus?
    var readyResult: AutoDispatchReadyResult?
    var filamentChallenge: AutoDispatchReadyResult?
    var dispatchMessage: String?
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
            let result = try await autoDispatchService.markReady(
                status: reviewedStatus
            )
            guard isViewActive else { isMarkingReady = false; return }
            await applyReadyResult(result, printerId: printerId)
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

    func confirmFilamentOverride(printerId: UUID) async {
        guard let autoDispatchService,
              let challenge = filamentChallenge,
              !isMarkingReady else {
            return
        }
        isMarkingReady = true
        error = nil
        do {
            let result = try await autoDispatchService
                .confirmFilamentOverride(challenge: challenge)
            guard isViewActive else {
                isMarkingReady = false
                return
            }
            await applyReadyResult(result, printerId: printerId)
        } catch {
            self.error = error.localizedDescription
            isMarkingReady = false
        }
    }

    private func applyReadyResult(
        _ result: AutoDispatchReadyResult,
        printerId: UUID
    ) async {
        readyResult = result
        status = result.status
        dispatchMessage = nil

        if !result.dispatchInitiated &&
            (result.requiresFilamentOverride ||
                result.filamentCheckChanged) {
            filamentChallenge = result
            isMarkingReady = false
            return
        }

        filamentChallenge = nil
        guard result.dispatchInitiated else {
            error = result.filamentCheck?.message ??
                "The server did not initiate dispatch."
            isMarkingReady = false
            return
        }

        dispatchMessage = result.dispatchReconciliationPending
            ? "Dispatch submitted; awaiting printer reconciliation."
            : "Dispatch accepted by the printer."
        try? await Task.sleep(for: .seconds(2))
        guard isViewActive else {
            isMarkingReady = false
            return
        }
        await loadStatus(printerId: printerId)
        isMarkingReady = false
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
