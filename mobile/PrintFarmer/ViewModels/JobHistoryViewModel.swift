import Foundation
import os

@MainActor @Observable
final class JobHistoryViewModel {
    var historyPage: QueueHistoryPage?
    var timeline: [TimelineEvent] = []
    var selectedJobHistory: JobStateHistory?
    var isLoading = false
    var isLoadingMore = false
    var error: String?
    var isViewActive = true
    var currentOffset = 0

    var dateFrom: Date?
    var dateTo: Date?

    private let pageSize = 30
    private let logger = Logger(subsystem: "com.printfarmer.ios", category: "JobHistory")
    private var jobAnalyticsService: (any JobAnalyticsServiceProtocol)?

    func configure(jobAnalyticsService: any JobAnalyticsServiceProtocol) {
        self.jobAnalyticsService = jobAnalyticsService
    }

    func loadHistory() async {
        guard let jobAnalyticsService, isViewActive else { return }
        isLoading = true
        error = nil
        currentOffset = 0

        do {
            let result = try await jobAnalyticsService.getHistory(
                limit: pageSize,
                offset: 0,
                sortBy: nil,
                statuses: nil,
                dateStart: dateFrom,
                dateEnd: dateTo
            )
            guard isViewActive else { return }
            historyPage = result
        } catch {
            guard isViewActive else { return }
            self.error = error.localizedDescription
        }

        guard isViewActive else { return }
        isLoading = false
    }

    /// Loads the next page of history and appends it to `historyPage`.
    ///
    /// Pagination invariant (issue #810): `currentOffset` is the offset of the
    /// most recently *committed* page. It is advanced only after the
    /// corresponding page has been successfully appended to `historyPage`.
    /// On failure, cancellation, or view deactivation mid-request the offset
    /// is left untouched, so the next `loadMore()` call retries the same
    /// offset exactly once and no page is skipped. Concurrent duplicate
    /// invocations are suppressed by the synchronous `!isLoadingMore` guard
    /// before the first suspension point.
    func loadMore() async {
        guard let jobAnalyticsService, !isLoadingMore, isViewActive else { return }
        guard let page = historyPage, page.entries.count < page.totalCount else { return }

        isLoadingMore = true
        let nextOffset = currentOffset + pageSize

        do {
            let nextPage = try await jobAnalyticsService.getHistory(
                limit: pageSize,
                offset: nextOffset,
                sortBy: nil,
                statuses: nil,
                dateStart: dateFrom,
                dateEnd: dateTo
            )
            guard isViewActive else { return }
            historyPage = QueueHistoryPage(
                entries: (historyPage?.entries ?? []) + nextPage.entries,
                totalCount: nextPage.totalCount,
                currentPage: nextPage.currentPage,
                pageSize: nextPage.pageSize,
                stats: nextPage.stats
            )
            currentOffset = nextOffset
        } catch {
            guard isViewActive else { return }
            logger.warning("Failed to load more history: \(error.localizedDescription)")
        }

        guard isViewActive else { return }
        isLoadingMore = false
    }

    func loadTimeline(dateFrom: Date?, dateTo: Date?) async {
        guard let jobAnalyticsService, isViewActive else { return }
        do {
            let result = try await jobAnalyticsService.getTimeline(
                dateFrom: dateFrom,
                dateTo: dateTo,
                printerId: nil,
                filterStatus: nil,
                limit: 100
            )
            guard isViewActive else { return }
            timeline = result
        } catch {
            guard isViewActive else { return }
            logger.warning("Failed to load timeline: \(error.localizedDescription)")
        }
    }

    func loadJobStateHistory(jobId: String) async {
        guard let jobAnalyticsService, isViewActive else { return }
        do {
            let result = try await jobAnalyticsService.getJobStateHistory(jobId: jobId)
            guard isViewActive else { return }
            selectedJobHistory = result
        } catch {
            guard isViewActive else { return }
            self.error = error.localizedDescription
        }
    }

    // MARK: - Computed

    var historyItems: [QueueHistoryEntry] {
        historyPage?.entries ?? []
    }

    var canLoadMore: Bool {
        guard let page = historyPage else { return false }
        return page.entries.count < page.totalCount
    }
}
