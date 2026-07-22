import Foundation
import os

@MainActor @Observable
final class JobHistoryViewModel {
    typealias CancellationCleanupEnqueuer = @Sendable (
        @escaping @MainActor @Sendable () -> Void
    ) -> Void

    private final class CancellationAttempt: @unchecked Sendable {
        struct CleanupClaim {
            let isFirst: Bool
            let operationToken: UUID?
        }

        private let lock = NSLock()
        private var cancelled = false
        private var operationToken: UUID?
        private var cleanupClaimed = false
        private var completionClaimed = false

        var isCancelled: Bool {
            lock.lock()
            defer { lock.unlock() }
            return cancelled
        }

        func cancel() {
            lock.lock()
            cancelled = true
            lock.unlock()
        }

        func install(operationToken: UUID) -> Bool {
            lock.lock()
            defer { lock.unlock() }
            self.operationToken = operationToken
            return !cancelled
        }

        func claimCompletion() -> Bool {
            lock.lock()
            defer { lock.unlock() }
            guard !cancelled, !completionClaimed else { return false }
            completionClaimed = true
            return true
        }

        func claimCleanup() -> CleanupClaim {
            lock.lock()
            defer { lock.unlock() }
            guard !cleanupClaimed else {
                return CleanupClaim(isFirst: false, operationToken: nil)
            }
            cleanupClaimed = true
            return CleanupClaim(isFirst: true, operationToken: operationToken)
        }
    }

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

    private struct SecondaryOperation {
        let token: UUID
        let activationToken: UUID
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
    private(set) var isTimelineLoading = false
    private(set) var isJobStateLoading = false
    var error: String?
    private(set) var timelineError: String?
    private(set) var jobStateError: String?
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
    private var activeTimelineOperation: SecondaryOperation?
    private var activeJobStateOperation: SecondaryOperation?
    private var activeViewToken: UUID? = UUID()
    @ObservationIgnored private let cancellationCleanupEnqueuer: CancellationCleanupEnqueuer

    #if DEBUG
    @ObservationIgnored private var cancellationCleanupTick: UInt64 = 0
    @ObservationIgnored private var cancellationCleanupWaiters:
        [(target: UInt64, continuation: CheckedContinuation<Void, Never>)] = []
    @ObservationIgnored var beforeHistoryApplyForTesting:
        (@MainActor @Sendable () async -> Void)?

    var cancellationCleanupTickForTesting: UInt64 { cancellationCleanupTick }

    func waitForCancellationCleanupForTesting(atLeast target: UInt64) async {
        if cancellationCleanupTick >= target { return }
        await withCheckedContinuation { continuation in
            cancellationCleanupWaiters.append((target, continuation))
        }
    }
    #endif

    init(
        cancellationCleanupEnqueuer: @escaping CancellationCleanupEnqueuer = { operation in
            _ = Task { @MainActor in
                operation()
            }
        }
    ) {
        self.cancellationCleanupEnqueuer = cancellationCleanupEnqueuer
    }

    func configure(jobAnalyticsService: any JobAnalyticsServiceProtocol) {
        self.jobAnalyticsService = jobAnalyticsService
    }

    @discardableResult
    func activate() -> UUID {
        invalidateOperationAuthorities()
        let token = UUID()
        activeViewToken = token
        isViewActive = true
        return token
    }

    func deactivate(activationToken: UUID) {
        guard activeViewToken == activationToken else { return }
        activeViewToken = nil
        isViewActive = false
        invalidateOperationAuthorities()
    }

    func loadHistory() async {
        guard let activationToken = activeViewToken else { return }
        await loadHistory(activationToken: activationToken)
    }

    func loadHistory(activationToken: UUID) async {
        let cancellation = CancellationAttempt()
        let enqueueCleanup = cancellationCleanupEnqueuer

        await withTaskCancellationHandler {
            guard !Task.isCancelled,
                  !cancellation.isCancelled,
                  let jobAnalyticsService,
                  let operation = beginReload(activationToken: activationToken) else {
                return
            }

            guard cancellation.install(operationToken: operation.token),
                  !Task.isCancelled,
                  !cancellation.isCancelled else {
                cancelHistoryOperation(cancellation)
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
                #if DEBUG
                if let beforeHistoryApplyForTesting {
                    await beforeHistoryApplyForTesting()
                }
                #endif
                guard owns(operation), cancellation.claimCompletion() else { return }
                committedHistory = CommittedHistory(
                    page: result,
                    offset: operation.targetOffset,
                    filters: operation.filters,
                    generation: operation.generation
                )
            } catch {
                guard owns(operation), cancellation.claimCompletion() else { return }
                if !(error is CancellationError) {
                    self.error = error.localizedDescription
                }
            }

            finish(operation)
        } onCancel: {
            cancellation.cancel()
            enqueueCleanup { [weak self] in
                self?.cancelHistoryOperation(cancellation)
            }
        }
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
        let cancellation = CancellationAttempt()
        let enqueueCleanup = cancellationCleanupEnqueuer

        await withTaskCancellationHandler {
            guard !Task.isCancelled,
                  !cancellation.isCancelled,
                  let jobAnalyticsService,
                  let operation = beginPagination(activationToken: activationToken),
                  let basePage = operation.basePage else {
                return
            }

            guard cancellation.install(operationToken: operation.token),
                  !Task.isCancelled,
                  !cancellation.isCancelled else {
                cancelHistoryOperation(cancellation)
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
                #if DEBUG
                if let beforeHistoryApplyForTesting {
                    await beforeHistoryApplyForTesting()
                }
                #endif
                guard owns(operation), cancellation.claimCompletion() else { return }
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
                guard owns(operation), cancellation.claimCompletion() else { return }
                if !(error is CancellationError) {
                    logger.warning("Failed to load more history: \(error.localizedDescription)")
                }
            }

            finish(operation)
        } onCancel: {
            cancellation.cancel()
            enqueueCleanup { [weak self] in
                self?.cancelHistoryOperation(cancellation)
            }
        }
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
        let cancellation = CancellationAttempt()
        let enqueueCleanup = cancellationCleanupEnqueuer

        await withTaskCancellationHandler {
            guard !Task.isCancelled,
                  !cancellation.isCancelled,
                  let jobAnalyticsService,
                  let operation = beginTimeline(activationToken: activationToken) else {
                return
            }

            guard cancellation.install(operationToken: operation.token),
                  !Task.isCancelled,
                  !cancellation.isCancelled else {
                cancelTimelineOperation(cancellation)
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
                guard ownsTimeline(operation), cancellation.claimCompletion() else { return }
                timeline = result
            } catch {
                guard ownsTimeline(operation), cancellation.claimCompletion() else { return }
                if !(error is CancellationError) {
                    timelineError = error.localizedDescription
                    logger.warning("Failed to load timeline: \(error.localizedDescription)")
                }
            }

            finishTimeline(operation)
        } onCancel: {
            cancellation.cancel()
            enqueueCleanup { [weak self] in
                self?.cancelTimelineOperation(cancellation)
            }
        }
    }

    func loadJobStateHistory(jobId: String) async {
        guard let activationToken = activeViewToken else { return }
        await loadJobStateHistory(jobId: jobId, activationToken: activationToken)
    }

    func loadJobStateHistory(jobId: String, activationToken: UUID) async {
        let cancellation = CancellationAttempt()
        let enqueueCleanup = cancellationCleanupEnqueuer

        await withTaskCancellationHandler {
            guard !Task.isCancelled,
                  !cancellation.isCancelled,
                  let jobAnalyticsService,
                  let operation = beginJobState(activationToken: activationToken) else {
                return
            }

            guard cancellation.install(operationToken: operation.token),
                  !Task.isCancelled,
                  !cancellation.isCancelled else {
                cancelJobStateOperation(cancellation)
                return
            }

            do {
                let result = try await jobAnalyticsService.getJobStateHistory(jobId: jobId)
                guard ownsJobState(operation), cancellation.claimCompletion() else { return }
                selectedJobHistory = result
            } catch {
                guard ownsJobState(operation), cancellation.claimCompletion() else { return }
                if !(error is CancellationError) {
                    jobStateError = error.localizedDescription
                    self.error = error.localizedDescription
                }
            }

            finishJobState(operation)
        } onCancel: {
            cancellation.cancel()
            enqueueCleanup { [weak self] in
                self?.cancelJobStateOperation(cancellation)
            }
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

    private func beginTimeline(activationToken: UUID) -> SecondaryOperation? {
        guard !Task.isCancelled, matchesActiveView(activationToken) else { return nil }
        let operation = SecondaryOperation(token: UUID(), activationToken: activationToken)
        activeTimelineOperation = operation
        isTimelineLoading = true
        timelineError = nil
        return operation
    }

    private func beginJobState(activationToken: UUID) -> SecondaryOperation? {
        guard !Task.isCancelled, matchesActiveView(activationToken) else { return nil }
        let operation = SecondaryOperation(token: UUID(), activationToken: activationToken)
        activeJobStateOperation = operation
        isJobStateLoading = true
        jobStateError = nil
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

    private func ownsTimeline(_ operation: SecondaryOperation) -> Bool {
        matchesActiveView(operation.activationToken)
            && activeTimelineOperation?.token == operation.token
    }

    private func finishTimeline(_ operation: SecondaryOperation) {
        guard ownsTimeline(operation) else { return }
        activeTimelineOperation = nil
        isTimelineLoading = false
    }

    private func ownsJobState(_ operation: SecondaryOperation) -> Bool {
        matchesActiveView(operation.activationToken)
            && activeJobStateOperation?.token == operation.token
    }

    private func finishJobState(_ operation: SecondaryOperation) {
        guard ownsJobState(operation) else { return }
        activeJobStateOperation = nil
        isJobStateLoading = false
    }

    private func cancelHistoryOperation(_ cancellation: CancellationAttempt) {
        let claim = cancellation.claimCleanup()
        guard claim.isFirst else { return }
        if let operationToken = claim.operationToken,
           let operation = activeHistoryOperation,
           operation.token == operationToken {
            activeHistoryOperation = nil
            switch operation.kind {
            case .reload:
                isLoading = false
            case .pagination:
                isLoadingMore = false
            }
        }
        recordCancellationCleanup()
    }

    private func cancelTimelineOperation(_ cancellation: CancellationAttempt) {
        let claim = cancellation.claimCleanup()
        guard claim.isFirst else { return }
        if let operationToken = claim.operationToken,
           activeTimelineOperation?.token == operationToken {
            activeTimelineOperation = nil
            isTimelineLoading = false
        }
        recordCancellationCleanup()
    }

    private func cancelJobStateOperation(_ cancellation: CancellationAttempt) {
        let claim = cancellation.claimCleanup()
        guard claim.isFirst else { return }
        if let operationToken = claim.operationToken,
           activeJobStateOperation?.token == operationToken {
            activeJobStateOperation = nil
            isJobStateLoading = false
        }
        recordCancellationCleanup()
    }

    private func invalidateOperationAuthorities() {
        invalidateHistoryAuthority()
        invalidateTimelineAuthority()
        invalidateJobStateAuthority()
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

    private func invalidateTimelineAuthority() {
        activeTimelineOperation = nil
        isTimelineLoading = false
    }

    private func invalidateJobStateAuthority() {
        activeJobStateOperation = nil
        isJobStateLoading = false
    }

    private func matchesActiveView(_ activationToken: UUID) -> Bool {
        isViewActive && activeViewToken == activationToken
    }

    #if DEBUG
    private func recordCancellationCleanup() {
        cancellationCleanupTick &+= 1
        let current = cancellationCleanupTick
        var remaining:
            [(target: UInt64, continuation: CheckedContinuation<Void, Never>)] = []
        for waiter in cancellationCleanupWaiters {
            if current >= waiter.target {
                waiter.continuation.resume()
            } else {
                remaining.append(waiter)
            }
        }
        cancellationCleanupWaiters = remaining
    }
    #else
    private func recordCancellationCleanup() {}
    #endif

    // MARK: - Computed

    var historyItems: [QueueHistoryEntry] {
        historyPage?.entries ?? []
    }

    var canLoadMore: Bool {
        guard let page = historyPage else { return false }
        return page.entries.count < page.totalCount
    }
}
