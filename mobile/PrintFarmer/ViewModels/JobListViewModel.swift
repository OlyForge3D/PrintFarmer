import Foundation

@MainActor @Observable
final class JobListViewModel {
    var jobs: [QueuedPrintJobResponse] = []
    var isLoading = false
    var errorMessage: String?
    var showRecentJobs = false
    var isViewActive = true

    private var jobService: (any JobServiceProtocol)?

    func configure(jobService: any JobServiceProtocol) {
        self.jobService = jobService
    }

    func loadJobs() async {
        guard let jobService, isViewActive else { return }
        isLoading = true
        errorMessage = nil

        do {
            let result = try await jobService.listAllJobs()
            guard isViewActive else { return }
            jobs = result
        } catch {
            guard isViewActive else { return }
            errorMessage = error.localizedDescription
        }

        guard isViewActive else { return }
        isLoading = false
    }

    func cancelJob(id: UUID) async {
        guard let jobService, isViewActive else { return }
        guard let rowVersion = reviewedRowVersion(for: id) else {
            errorMessage = "Refresh and review this job before cancelling it."
            return
        }
        do {
            try await jobService.cancel(id: id, reviewedRowVersion: rowVersion)
            await loadJobs()
        } catch {
            guard isViewActive else { return }
            await handleActionError(error)
        }
    }

    func abortJob(id: UUID) async {
        guard let jobService, isViewActive else { return }
        guard let rowVersion = reviewedRowVersion(for: id) else {
            errorMessage = "Refresh and review this job before aborting it."
            return
        }
        do {
            try await jobService.abort(id: id, reviewedRowVersion: rowVersion)
            await loadJobs()
        } catch {
            guard isViewActive else { return }
            await handleActionError(error)
        }
    }

    func dispatchJob(id: UUID) async {
        guard let jobService, isViewActive else { return }
        guard let rowVersion = reviewedRowVersion(for: id) else {
            errorMessage = "Refresh and review this job before dispatching it."
            return
        }
        do {
            let result = try await jobService.dispatch(
                id: id,
                reviewedRowVersion: rowVersion
            )
            switch result {
            case .accepted:
                await loadJobs()
            case .reconciliation(let response):
                await loadJobs()
                errorMessage =
                    response.dispatchResult?.errorDetail
                    ?? "The dispatch outcome is being reconciled. Do not dispatch again."
            case .rejected(let response):
                await loadJobs()
                errorMessage =
                    response.dispatchResult?.errorDetail
                    ?? "The printer rejected the dispatch."
            }
        } catch {
            guard isViewActive else { return }
            await handleActionError(error)
        }
    }

    // MARK: - Grouped Jobs

    /// Jobs actively printing, starting, or paused on a printer
    var activeJobs: [QueuedPrintJobResponse] {
        jobs.filter {
            guard let status = $0.job.jobStatus else { return false }
            return [.printing, .starting, .paused].contains(status)
        }
        .sorted { ($0.job.actualStartTimeUtc ?? .distantPast) > ($1.job.actualStartTimeUtc ?? .distantPast) }
    }

    /// Jobs waiting in the queue (queued or assigned but not yet started)
    var queuedJobs: [QueuedPrintJobResponse] {
        jobs.filter {
            guard let status = $0.job.jobStatus else { return false }
            return [.queued, .assigned].contains(status)
        }
        .sorted { $0.job.queuePosition < $1.job.queuePosition }
    }

    /// Recently completed, failed, or cancelled jobs
    var recentJobs: [QueuedPrintJobResponse] {
        jobs.filter {
            guard let status = $0.job.jobStatus else { return false }
            return [.completed, .failed, .cancelled].contains(status)
        }
        .sorted { ($0.job.actualEndTimeUtc ?? $0.job.createdAtUtc) > ($1.job.actualEndTimeUtc ?? $1.job.createdAtUtc) }
    }

    var hasAnyJobs: Bool {
        !jobs.isEmpty
    }

    private func reviewedRowVersion(for id: UUID) -> String? {
        jobs.first(where: { $0.job.jobUUID == id })?.job.rowVersion
    }

    private func handleActionError(_ error: Error) async {
        if let networkError = error as? NetworkError,
           networkError.requiresReview {
            await loadJobs()
            errorMessage =
                "This job changed after you reviewed it. Review the refreshed row and confirm again."
            return
        }
        errorMessage = error.localizedDescription
    }
}

private extension NetworkError {
    var requiresReview: Bool {
        switch self {
        case .preconditionFailed, .preconditionRequired:
            return true
        case .clientError(let code, _):
            return code == 412 || code == 428
        default:
            return false
        }
    }
}
