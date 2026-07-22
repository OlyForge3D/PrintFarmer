import Foundation
import os

@MainActor @Observable
final class JobHistoryViewModel {
    private struct HistoryFilters: Equatable {
        let dateFrom: Date?
        let dateTo: Date?
    }

    private struct CommittedHistory {
        var page: QueueHistoryPage?
        var offset: Int
        var filters: HistoryFilters
        var generation: UInt64
    }

    private enum HistoryOperationKind {
        case reload
        case pagination
    }

    private struct HistoryOperation {
        let token: UUID
        let activationToken: UUID
        let generation: UInt64
        let kind: HistoryOperationKind
        let filters: HistoryFilters
        let basePage: QueueHistoryPage?
        let baseOffset: Int
        let targetOffset: Int
    }

    private var committedHistory = CommittedHistory(
        page: nil,
        offset: 0,
        filters: HistoryFilters(dateFrom: nil, dateTo: nil),
        generation: 0
    )

    var historyPage: QueueHistoryPage? { committedHistory.page }
    var timeline: [TimelineEvent] = []
    var selectedJobHistory: JobStateHistory?
    var isLoading = false
    var isLoadingMore = false
    var error: String?
    private(set) var isViewActive = true
    var currentOffset: Int { committedHistory.offset }

    var dateFrom: Date? {
        didSet {
            if dateFrom != oldValue {
                invalidateHistoryAuthority()
            }
        }
    }

    var dateTo: Date? {
        didSet {
            if dateTo != oldValue {
                invalidateHistoryAuthority()
            }
        }
    }

    private let pageSize = 30
    private let logger = Logger(subsystem: "com.printfarmer.ios", category: "JobHistory")
    private var jobAnalyticsService: (any JobAnalyticsServiceProtocol)?
    private var historyGeneration: UInt64 = 0
    private var activeHistoryOperation: HistoryOperation?
    private var activeViewToken: UUID? = UUID()

    func configure(jobAnalyticsService: any JobAnalyticsServiceProtocol) {
        self.jobAnalyticsService = jobAnalyticsService
    }

    @discardableResult
    func activate() -> UUID {
        invalidateHistoryAuthority()
        let token = UUID()
        activeViewToken = token
        isViewActive = true
        return token
    }

    func deactivate(activationToken: UUID) {
        guard activeViewToken == activationToken else { return }
        activeViewToken = nil
        isViewActive = false
        invalidateHistoryAuthority()
    }

    func loadHistory() async {
        guard let activationToken = activeViewToken else { return }
        await loadHistory(activationToken: activationToken)
    }

    func loadHistory(activationToken: UUID) async {
        guard !Task.isCancelled,
              let jobAnalyticsService,
              let operation = beginReload(activationToken: activationToken) else {
            return
        }

        do {
            let result = try await jobAnalyticsService.getHistory(
                limit: pageSize,
                offset: operation.targetOffset,
                sortBy: nil,
                statuses: nil,
                dateStart: operation.filters.dateFrom,
                dateEnd: operation.filters.dateTo
            )
            guard owns(operation) else { return }
            guard !Task.isCancelled else {
                finish(operation)
                return
            }
            committedHistory = CommittedHistory(
                page: result,
                offset: operation.targetOffset,
                filters: operation.filters,
                generation: operation.generation
            )
        } catch {
            guard owns(operation) else { return }
            if !Task.isCancelled, !(error is CancellationError) {
                self.error = error.localizedDescription
            }
        }

        finish(operation)
    }

    /// Loads the next page of history and appends it to `historyPage`.
    ///
    /// The committed page, offset, filters, and generation move together.
    /// A page completion may append only while it still owns the captured
    /// operation token under the same active generation.
    func loadMore() async {
        guard let activationToken = activeViewToken else { return }
        await loadMore(activationToken: activationToken)
    }

    func loadMore(activationToken: UUID) async {
        guard !Task.isCancelled,
              let jobAnalyticsService,
              let operation = beginPagination(activationToken: activationToken),
              let basePage = operation.basePage else {
            return
        }

        do {
            let nextPage = try await jobAnalyticsService.getHistory(
                limit: pageSize,
                offset: operation.targetOffset,
                sortBy: nil,
                statuses: nil,
                dateStart: operation.filters.dateFrom,
                dateEnd: operation.filters.dateTo
            )
            guard owns(operation) else { return }
            guard !Task.isCancelled else {
                finish(operation)
                return
            }
            committedHistory = CommittedHistory(
                page: QueueHistoryPage(
                    entries: basePage.entries + nextPage.entries,
                    totalCount: nextPage.totalCount,
                    currentPage: nextPage.currentPage,
                    pageSize: nextPage.pageSize,
                    stats: nextPage.stats
                ),
                offset: operation.targetOffset,
                filters: operation.filters,
                generation: operation.generation
            )
        } catch {
            guard owns(operation) else { return }
            if !Task.isCancelled, !(error is CancellationError) {
                logger.warning("Failed to load more history: \(error.localizedDescription)")
            }
        }

        finish(operation)
    }

    func loadTimeline(dateFrom: Date?, dateTo: Date?) async {
        guard let activationToken = activeViewToken else { return }
        await loadTimeline(
            dateFrom: dateFrom,
            dateTo: dateTo,
            activationToken: activationToken
        )
    }

    func loadTimeline(
        dateFrom: Date?,
        dateTo: Date?,
        activationToken: UUID
    ) async {
        guard !Task.isCancelled,
              let jobAnalyticsService,
              matchesActiveView(activationToken) else {
            return
        }
        do {
            let result = try await jobAnalyticsService.getTimeline(
                dateFrom: dateFrom,
                dateTo: dateTo,
                printerId: nil,
                filterStatus: nil,
                limit: 100
            )
            guard matchesActiveView(activationToken), !Task.isCancelled else { return }
            timeline = result
        } catch {
            guard matchesActiveView(activationToken), !Task.isCancelled else { return }
            logger.warning("Failed to load timeline: \(error.localizedDescription)")
        }
    }

    func loadJobStateHistory(jobId: String) async {
        guard let jobAnalyticsService,
              let activationToken = activeViewToken,
              isViewActive else {
            return
        }
        do {
            let result = try await jobAnalyticsService.getJobStateHistory(jobId: jobId)
            guard matchesActiveView(activationToken), !Task.isCancelled else { return }
            selectedJobHistory = result
        } catch {
            guard matchesActiveView(activationToken), !Task.isCancelled else { return }
            self.error = error.localizedDescription
        }
    }

    private func beginReload(activationToken: UUID) -> HistoryOperation? {
        guard !Task.isCancelled, matchesActiveView(activationToken) else { return nil }

        invalidateHistoryAuthority()
        let operation = HistoryOperation(
            token: UUID(),
            activationToken: activationToken,
            generation: historyGeneration,
            kind: .reload,
            filters: selectedFilters,
            basePage: committedHistory.page,
            baseOffset: committedHistory.offset,
            targetOffset: 0
        )
        activeHistoryOperation = operation
        isLoading = true
        error = nil
        return operation
    }

    private func beginPagination(activationToken: UUID) -> HistoryOperation? {
        guard !Task.isCancelled,
              matchesActiveView(activationToken),
              activeHistoryOperation == nil,
              !isLoadingMore,
              let page = committedHistory.page,
              page.entries.count < page.totalCount,
              committedHistory.generation == historyGeneration else {
            return nil
        }

        let operation = HistoryOperation(
            token: UUID(),
            activationToken: activationToken,
            generation: historyGeneration,
            kind: .pagination,
            filters: committedHistory.filters,
            basePage: page,
            baseOffset: committedHistory.offset,
            targetOffset: committedHistory.offset + pageSize
        )
        activeHistoryOperation = operation
        isLoadingMore = true
        return operation
    }

    private var selectedFilters: HistoryFilters {
        HistoryFilters(dateFrom: dateFrom, dateTo: dateTo)
    }

    private func owns(_ operation: HistoryOperation) -> Bool {
        isViewActive
            && activeViewToken == operation.activationToken
            && historyGeneration == operation.generation
            && committedHistory.generation == operation.generation
            && activeHistoryOperation?.token == operation.token
    }

    private func finish(_ operation: HistoryOperation) {
        guard owns(operation) else { return }
        activeHistoryOperation = nil
        switch operation.kind {
        case .reload:
            isLoading = false
        case .pagination:
            isLoadingMore = false
        }
    }

    private func invalidateHistoryAuthority() {
        historyGeneration &+= 1
        committedHistory.generation = historyGeneration

        guard let operation = activeHistoryOperation else { return }
        activeHistoryOperation = nil
        switch operation.kind {
        case .reload:
            isLoading = false
        case .pagination:
            isLoadingMore = false
        }
    }

    private func matchesActiveView(_ activationToken: UUID) -> Bool {
        isViewActive && activeViewToken == activationToken
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
