import Foundation
import SwiftUI

@MainActor @Observable
final class JobDetailViewModel {
    var job: PrintJob?
    var isLoading = false
    var errorMessage: String?
    var isPerformingAction = false
    var actionError: String?
    var showCancelConfirmation = false
    var isViewActive = true

    let jobId: UUID
    private var jobService: (any JobServiceProtocol)?

    init(jobId: UUID) {
        self.jobId = jobId
    }

    func configure(jobService: any JobServiceProtocol) {
        self.jobService = jobService
    }

    func loadJob() async {
        guard let jobService, isViewActive else { return }
        isLoading = true
        errorMessage = nil

        do {
            let result = try await jobService.get(id: jobId)
            guard isViewActive else { return }
            job = result
        } catch {
            guard isViewActive else { return }
            errorMessage = error.localizedDescription
        }

        guard isViewActive else { return }
        isLoading = false
    }

    // MARK: - Actions

    func dispatchJob() async {
        await performAction { try await $0.dispatch(id: self.jobId) }
    }

    func cancelJob() async {
        await performAction { try await $0.cancel(id: self.jobId) }
        #if os(iOS)
        UINotificationFeedbackGenerator().notificationOccurred(.warning)
        #endif
    }

    func abortJob() async {
        await performAction { try await $0.abort(id: self.jobId) }
        #if os(iOS)
        UINotificationFeedbackGenerator().notificationOccurred(.warning)
        #endif
    }

    func pauseJob() async {
        await performAction { try await $0.pause(id: self.jobId) }
    }

    func resumeJob() async {
        await performAction { try await $0.resume(id: self.jobId) }
    }

    // MARK: - Computed

    var canDispatch: Bool {
        job?.status == .queued
    }

    var canCancel: Bool {
        guard let status = job?.status else { return false }
        return [.queued, .assigned].contains(status)
    }

    var canAbort: Bool {
        guard let status = job?.status else { return false }
        return [.printing, .starting, .paused].contains(status)
    }

    var canPause: Bool {
        job?.status == .printing
    }

    var canResume: Bool {
        job?.status == .paused
    }

    var isActive: Bool {
        guard let status = job?.status else { return false }
        return [.printing, .starting, .paused, .assigned].contains(status)
    }

    // MARK: - Private

    private func performAction(_ action: @escaping (any JobServiceProtocol) async throws -> Void) async {
        guard isViewActive else { return }
        guard let jobService else { return }
        isPerformingAction = true
        actionError = nil

        do {
            try await action(jobService)
            guard isViewActive else { return }
            await loadJob()
        } catch {
            guard isViewActive else { return }
            actionError = error.localizedDescription
        }

        guard isViewActive else { return }
        isPerformingAction = false
    }
}
