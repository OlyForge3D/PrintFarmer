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
        do {
            try await jobService.cancel(id: id)
            await loadJobs()
        } catch {
            guard isViewActive else { return }
            errorMessage = error.localizedDescription
        }
    }

    func abortJob(id: UUID) async {
        guard let jobService, isViewActive else { return }
        do {
            try await jobService.abort(id: id)
            await loadJobs()
        } catch {
            guard isViewActive else { return }
            errorMessage = error.localizedDescription
        }
    }

    func dispatchJob(id: UUID) async {
        guard let jobService, isViewActive else { return }
        do {
            try await jobService.dispatch(id: id)
            await loadJobs()
        } catch {
            guard isViewActive else { return }
            errorMessage = error.localizedDescription
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
}
