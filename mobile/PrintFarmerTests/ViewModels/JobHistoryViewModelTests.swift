import XCTest
@testable import PrintFarmer

/// Tests for JobHistoryViewModel: loading history, pagination, timeline, job state history,
/// and error handling.
@MainActor
final class JobHistoryViewModelTests: XCTestCase {
    
    private var mockJobAnalyticsService: MockJobAnalyticsService!
    private var viewModel: JobHistoryViewModel!
    
    override func setUp() {
        super.setUp()
        mockJobAnalyticsService = MockJobAnalyticsService()
        viewModel = JobHistoryViewModel()
        viewModel.configure(jobAnalyticsService: mockJobAnalyticsService)
    }
    
    override func tearDown() {
        viewModel = nil
        mockJobAnalyticsService = nil
        super.tearDown()
    }
    
    // MARK: - Initial State
    
    func testInitialState() {
        XCTAssertNil(viewModel.historyPage)
        XCTAssertTrue(viewModel.timeline.isEmpty)
        XCTAssertNil(viewModel.selectedJobHistory)
        XCTAssertFalse(viewModel.isLoading)
        XCTAssertFalse(viewModel.isLoadingMore)
        XCTAssertNil(viewModel.error)
        XCTAssertEqual(viewModel.currentOffset, 0)
    }
    
    // MARK: - Load History Success
    
    func testLoadHistoryPopulatesData() async {
        let entry = QueueHistoryEntry(
            id: "1",
            jobName: "test_print.gcode",
            printerName: "Prusa MK3",
            status: "completed",
            completedAt: Date(),
            durationSeconds: 3600
        )
        let page = QueueHistoryPage(
            entries: [entry],
            totalCount: 1,
            currentPage: 1,
            pageSize: 30,
            stats: nil
        )
        mockJobAnalyticsService.historyPageToReturn = page
        
        await viewModel.loadHistory()
        
        XCTAssertNotNil(viewModel.historyPage)
        XCTAssertEqual(viewModel.historyPage?.entries.count, 1)
        XCTAssertEqual(viewModel.historyPage?.entries.first?.id, "1")
        XCTAssertEqual(viewModel.historyPage?.totalCount, 1)
        XCTAssertFalse(viewModel.isLoading)
        XCTAssertNil(viewModel.error)
    }
    
    func testLoadHistoryUsesDefaultParameters() async {
        let page = QueueHistoryPage(entries: [], totalCount: 0, currentPage: 1, pageSize: 30, stats: nil)
        mockJobAnalyticsService.historyPageToReturn = page
        
        await viewModel.loadHistory()
        
        let called = mockJobAnalyticsService.getHistoryCalledWith
        XCTAssertEqual(called?.limit, 30)
        XCTAssertEqual(called?.offset, 0)
        XCTAssertNil(called?.sortBy)
        XCTAssertNil(called?.statuses)
        XCTAssertNil(called?.dateStart)
        XCTAssertNil(called?.dateEnd)
    }
    
    func testLoadHistoryHandlesError() async {
        mockJobAnalyticsService.errorToThrow = TestError.generic
        
        await viewModel.loadHistory()
        
        XCTAssertNil(viewModel.historyPage)
        XCTAssertNotNil(viewModel.error)
        XCTAssertFalse(viewModel.isLoading)
    }
    
    func testLoadHistoryClearsPreviousError() async {
        mockJobAnalyticsService.errorToThrow = TestError.generic
        await viewModel.loadHistory()
        XCTAssertNotNil(viewModel.error)
        
        mockJobAnalyticsService.errorToThrow = nil
        mockJobAnalyticsService.historyPageToReturn = QueueHistoryPage(
            entries: [],
            totalCount: 0,
            currentPage: 1,
            pageSize: 30,
            stats: nil
        )
        
        await viewModel.loadHistory()
        
        XCTAssertNil(viewModel.error)
    }
    
    // MARK: - Load More (Pagination)
    
    func testLoadMoreAppendsEntries() async {
        let entry1 = QueueHistoryEntry(
            id: "1",
            jobName: "first.gcode",
            printerName: "Prusa MK3",
            status: "completed",
            completedAt: Date(),
            durationSeconds: 3600
        )
        let entry2 = QueueHistoryEntry(
            id: "2",
            jobName: "second.gcode",
            printerName: "Prusa MK3",
            status: "completed",
            completedAt: Date(),
            durationSeconds: 1800
        )
        
        // Load initial page
        let firstPage = QueueHistoryPage(entries: [entry1], totalCount: 2, currentPage: 1, pageSize: 30, stats: nil)
        mockJobAnalyticsService.historyPageToReturn = firstPage
        await viewModel.loadHistory()
        XCTAssertEqual(viewModel.historyPage?.entries.count, 1)
        XCTAssertEqual(viewModel.currentOffset, 0)
        
        // Load more
        let secondPage = QueueHistoryPage(entries: [entry2], totalCount: 2, currentPage: 2, pageSize: 30, stats: nil)
        mockJobAnalyticsService.historyPageToReturn = secondPage
        
        await viewModel.loadMore()
        
        XCTAssertEqual(viewModel.historyPage?.entries.count, 2)
        XCTAssertEqual(viewModel.historyPage?.entries.first?.id, "1")
        XCTAssertEqual(viewModel.historyPage?.entries.last?.id, "2")
        XCTAssertEqual(viewModel.currentOffset, 30)
        XCTAssertFalse(viewModel.isLoadingMore)
        XCTAssertNil(viewModel.error)
    }
    
    func testLoadMoreIncrementsOffsetBy30() async {
        let page1 = QueueHistoryPage(entries: [], totalCount: 100, currentPage: 1, pageSize: 30, stats: nil)
        mockJobAnalyticsService.historyPageToReturn = page1
        await viewModel.loadHistory()
        
        let page2 = QueueHistoryPage(entries: [], totalCount: 100, currentPage: 2, pageSize: 30, stats: nil)
        mockJobAnalyticsService.historyPageToReturn = page2
        await viewModel.loadMore()
        
        XCTAssertEqual(viewModel.currentOffset, 30)
        let called = mockJobAnalyticsService.getHistoryCalledWith
        XCTAssertEqual(called?.offset, 30)
    }
    
    func testLoadMoreDoesNothingWhenNoMoreData() async {
        // canLoadMore checks `entries.count < totalCount`; with both zero the guard
        // in loadMore() short-circuits without calling the service.
        let page = QueueHistoryPage(entries: [], totalCount: 0, currentPage: 1, pageSize: 30, stats: nil)
        mockJobAnalyticsService.historyPageToReturn = page
        await viewModel.loadHistory()
        
        // Clear call tracking
        mockJobAnalyticsService.getHistoryCalledWith = nil
        
        await viewModel.loadMore()
        
        // Should not call service since entries.count (0) >= totalCount (0).
        XCTAssertNil(mockJobAnalyticsService.getHistoryCalledWith)
    }
    
    func testLoadMoreHandlesError() async {
        let previousEntry = QueueHistoryEntry(
            id: "previous",
            jobName: "previous.gcode",
            printerName: "Previous Printer",
            status: "completed",
            completedAt: Date(),
            durationSeconds: 600
        )
        viewModel.historyPage = QueueHistoryPage(
            entries: [previousEntry],
            totalCount: 100,
            currentPage: 1,
            pageSize: 30,
            stats: nil
        )
        let dateFrom = Date(timeIntervalSince1970: 1_700_000_000)
        let dateTo = Date(timeIntervalSince1970: 1_700_086_400)
        viewModel.dateFrom = dateFrom
        viewModel.dateTo = dateTo
        viewModel.error = "prior-job-history-error-sentinel"
        mockJobAnalyticsService.errorToThrow = TestError.generic
        
        await viewModel.loadMore()
        
        // loadMore() is a secondary paginator: on failure it logs via `logger.warning`
        // and leaves `viewModel.error` untouched so the already-loaded page keeps rendering.
        // Issue #810 tracks recovery of the mutable offset after a failed request.
        // Seeding a nonnil sentinel proves the error channel is neither cleared
        // nor overwritten by the secondary path.
        let called = mockJobAnalyticsService.getHistoryCalledWith
        XCTAssertEqual(called?.limit, 30)
        XCTAssertEqual(called?.offset, 30)
        XCTAssertNil(called?.sortBy)
        XCTAssertNil(called?.statuses)
        XCTAssertEqual(called?.dateStart, dateFrom)
        XCTAssertEqual(called?.dateEnd, dateTo)
        XCTAssertEqual(viewModel.historyPage?.entries.count, 1)
        XCTAssertEqual(viewModel.historyPage?.entries.first?.id, "previous")
        XCTAssertEqual(viewModel.historyPage?.totalCount, 100)
        XCTAssertEqual(viewModel.historyPage?.currentPage, 1)
        XCTAssertEqual(viewModel.error, "prior-job-history-error-sentinel")
        XCTAssertFalse(viewModel.isLoadingMore)
        // Issue #810: pagination cursor must not advance on failure so the
        // caller can retry the same offset without skipping a page.
        XCTAssertEqual(viewModel.currentOffset, 0)
    }

    // MARK: - Load More (Issue #810: offset preservation on failure)

    /// Regression for #810. Before the fix, `currentOffset` was incremented
    /// synchronously before `await`, so a thrown request left the view model
    /// pointing at offset 30 despite having no entries from that page.
    func testLoadMorePreservesOffsetWhenRequestFails() async {
        viewModel.historyPage = QueueHistoryPage(
            entries: [],
            totalCount: 100,
            currentPage: 1,
            pageSize: 30,
            stats: nil
        )
        mockJobAnalyticsService.errorToThrow = TestError.generic

        await viewModel.loadMore()

        XCTAssertEqual(viewModel.currentOffset, 0)
        XCTAssertEqual(mockJobAnalyticsService.getHistoryCalledWith?.offset, 30)
    }

    /// Regression for #810. A retry after a failed `loadMore()` must request
    /// the same offset exactly once and append the returned page without
    /// gaps or duplicates.
    func testLoadMoreRetryAfterFailureRequestsSameOffset() async {
        let existing = QueueHistoryEntry(
            id: "existing",
            jobName: "existing.gcode",
            printerName: "Prusa MK3",
            status: "completed",
            completedAt: Date(),
            durationSeconds: 3600
        )
        viewModel.historyPage = QueueHistoryPage(
            entries: [existing],
            totalCount: 100,
            currentPage: 1,
            pageSize: 30,
            stats: nil
        )

        // First attempt: request fails. Cursor must stay at 0.
        mockJobAnalyticsService.errorToThrow = TestError.generic
        await viewModel.loadMore()
        XCTAssertEqual(viewModel.currentOffset, 0)
        XCTAssertEqual(mockJobAnalyticsService.getHistoryCalledWith?.offset, 30)
        XCTAssertEqual(viewModel.historyPage?.entries.count, 1)
        XCTAssertEqual(viewModel.historyPage?.entries.first?.id, "existing")

        // Retry: request succeeds. Same offset (30) is requested exactly
        // once, the returned entry is appended, and the cursor advances.
        let recovered = QueueHistoryEntry(
            id: "recovered",
            jobName: "recovered.gcode",
            printerName: "Prusa MK3",
            status: "completed",
            completedAt: Date(),
            durationSeconds: 1800
        )
        mockJobAnalyticsService.errorToThrow = nil
        mockJobAnalyticsService.historyPageToReturn = QueueHistoryPage(
            entries: [recovered],
            totalCount: 100,
            currentPage: 2,
            pageSize: 30,
            stats: nil
        )
        mockJobAnalyticsService.getHistoryCalledWith = nil

        await viewModel.loadMore()

        XCTAssertEqual(mockJobAnalyticsService.getHistoryCalledWith?.offset, 30)
        XCTAssertEqual(mockJobAnalyticsService.getHistoryCalledWith?.limit, 30)
        XCTAssertEqual(viewModel.currentOffset, 30)
        XCTAssertEqual(viewModel.historyPage?.entries.count, 2)
        XCTAssertEqual(viewModel.historyPage?.entries.map(\.id), ["existing", "recovered"])
        XCTAssertEqual(viewModel.historyPage?.totalCount, 100)
        XCTAssertEqual(viewModel.historyPage?.currentPage, 2)
    }

    /// Regression for #810. If the view is deactivated while a `loadMore()`
    /// request is in flight, neither the returned page nor the cursor may be
    /// published — the next active `loadMore()` must retry the same offset.
    func testLoadMoreDoesNotAdvanceOffsetWhenViewDeactivatedDuringRequest() async {
        viewModel.historyPage = QueueHistoryPage(
            entries: [],
            totalCount: 100,
            currentPage: 1,
            pageSize: 30,
            stats: nil
        )

        let gated = GatedJobAnalyticsService()
        gated.historyPageToReturn = QueueHistoryPage(
            entries: [],
            totalCount: 100,
            currentPage: 2,
            pageSize: 30,
            stats: nil
        )
        viewModel.configure(jobAnalyticsService: gated)

        let task = Task { await viewModel.loadMore() }

        // Deterministic barrier: wait until getHistory has been entered on the
        // background task before mutating view state. No sleeps, polling, or
        // Task.yield passes are used.
        _ = await gated.awaitEntered()

        viewModel.isViewActive = false
        gated.release()
        await task.value

        XCTAssertEqual(viewModel.currentOffset, 0)
        // Cursor stayed at 0, and the historyPage was not overwritten.
        XCTAssertEqual(viewModel.historyPage?.entries.count, 0)
        XCTAssertEqual(viewModel.historyPage?.currentPage, 1)
    }

    /// Regression for #810. Concurrent duplicate `loadMore()` calls must be
    /// suppressed by the `!isLoadingMore` guard so the service is invoked
    /// exactly once and the cursor cannot drift by more than one page.
    func testLoadMoreConcurrentCallsAreSuppressed() async {
        viewModel.historyPage = QueueHistoryPage(
            entries: [],
            totalCount: 100,
            currentPage: 1,
            pageSize: 30,
            stats: nil
        )

        let gated = GatedJobAnalyticsService()
        gated.historyPageToReturn = QueueHistoryPage(
            entries: [],
            totalCount: 100,
            currentPage: 2,
            pageSize: 30,
            stats: nil
        )
        viewModel.configure(jobAnalyticsService: gated)

        // First call suspends inside getHistory on the gate.
        let first = Task { await viewModel.loadMore() }
        _ = await gated.awaitEntered()

        // Second call runs while the first is in flight. The synchronous
        // `!isLoadingMore` guard must reject it before any suspension.
        await viewModel.loadMore()

        gated.release()
        await first.value

        XCTAssertEqual(gated.callCount, 1)
        XCTAssertEqual(viewModel.currentOffset, 30)
    }
    
    // MARK: - Load Timeline
    
    func testLoadTimelinePopulatesData() async {
        let event = TimelineEvent(
            jobId: "1",
            jobName: "test_print.gcode",
            printerName: "Prusa MK3",
            state: "printing",
            enteredAtUtc: Date(),
            exitedAtUtc: nil,
            durationSeconds: nil,
            estimatedDurationSeconds: nil,
            variancePercent: nil
        )
        mockJobAnalyticsService.timelineToReturn = [event]
        
        let dateFrom = Date().addingTimeInterval(-86400 * 7)
        let dateTo = Date()
        
        await viewModel.loadTimeline(dateFrom: dateFrom, dateTo: dateTo)
        
        XCTAssertEqual(viewModel.timeline.count, 1)
        XCTAssertEqual(viewModel.timeline.first?.jobId, "1")
        XCTAssertEqual(viewModel.timeline.first?.state, "printing")
        XCTAssertNil(viewModel.error)
        
        let called = mockJobAnalyticsService.getTimelineCalledWith
        XCTAssertNotNil(called?.dateFrom)
        XCTAssertNotNil(called?.dateTo)
    }
    
    func testLoadTimelineHandlesError() async {
        let previousEvent = TimelineEvent(
            jobId: "previous",
            jobName: "previous.gcode",
            printerName: "Previous Printer",
            state: "completed",
            enteredAtUtc: Date(timeIntervalSince1970: 1_699_999_000),
            exitedAtUtc: Date(timeIntervalSince1970: 1_700_000_000),
            durationSeconds: 1_000,
            estimatedDurationSeconds: 900,
            variancePercent: 11.1
        )
        viewModel.timeline = [previousEvent]
        let dateFrom = Date(timeIntervalSince1970: 1_700_000_000)
        let dateTo = Date(timeIntervalSince1970: 1_700_086_400)
        viewModel.error = "prior-timeline-error-sentinel"
        mockJobAnalyticsService.errorToThrow = TestError.generic
        
        await viewModel.loadTimeline(dateFrom: dateFrom, dateTo: dateTo)
        
        // loadTimeline() is a secondary load: it logs via `logger.warning`
        // and does not surface `viewModel.error` — the primary history page still owns
        // any error state that gets rendered and the previous timeline is preserved.
        // Seeding a nonnil sentinel proves the secondary path never clobbers
        // the primary error channel.
        let called = mockJobAnalyticsService.getTimelineCalledWith
        XCTAssertEqual(called?.dateFrom, dateFrom)
        XCTAssertEqual(called?.dateTo, dateTo)
        XCTAssertNil(called?.printerId)
        XCTAssertNil(called?.filterStatus)
        XCTAssertEqual(called?.limit, 100)
        XCTAssertEqual(viewModel.timeline.count, 1)
        XCTAssertEqual(viewModel.timeline.first?.jobId, "previous")
        XCTAssertEqual(viewModel.timeline.first?.state, "completed")
        XCTAssertEqual(viewModel.error, "prior-timeline-error-sentinel")
        XCTAssertFalse(viewModel.isLoading)
    }
    
    // MARK: - Load Job State History
    
    func testLoadJobStateHistoryPopulatesData() async {
        let history = JobStateHistory(
            jobId: "1",
            jobName: "test_print.gcode",
            transitions: [
                StateTransition(
                    state: "queued",
                    enteredAt: Date().addingTimeInterval(-7200),
                    exitedAt: Date().addingTimeInterval(-3600),
                    durationSeconds: 3600
                ),
                StateTransition(
                    state: "printing",
                    enteredAt: Date().addingTimeInterval(-3600),
                    exitedAt: Date(),
                    durationSeconds: 3600
                )
            ],
            totalDurationSeconds: 7200,
            estimatedDurationSeconds: 7000,
            variancePercent: 2.86
        )
        mockJobAnalyticsService.jobStateHistoryToReturn = history
        
        await viewModel.loadJobStateHistory(jobId: "1")
        
        XCTAssertNotNil(viewModel.selectedJobHistory)
        XCTAssertEqual(viewModel.selectedJobHistory?.jobId, "1")
        XCTAssertEqual(viewModel.selectedJobHistory?.transitions.count, 2)
        XCTAssertEqual(viewModel.selectedJobHistory?.transitions.first?.state, "queued")
        XCTAssertNil(viewModel.error)
        XCTAssertEqual(mockJobAnalyticsService.getJobStateHistoryCalledWith, "1")
    }
    
    func testLoadJobStateHistoryHandlesError() async {
        mockJobAnalyticsService.errorToThrow = TestError.generic
        
        await viewModel.loadJobStateHistory(jobId: "1")
        
        XCTAssertNil(viewModel.selectedJobHistory)
        XCTAssertNotNil(viewModel.error)
    }
    
    // MARK: - Computed Properties
    
    func testHistoryItemsReturnsEntriesFromPage() {
        let entry = QueueHistoryEntry(
            id: "1",
            jobName: "test_print.gcode",
            printerName: "Prusa MK3",
            status: "completed",
            completedAt: Date(),
            durationSeconds: 3600
        )
        viewModel.historyPage = QueueHistoryPage(
            entries: [entry],
            totalCount: 1,
            currentPage: 1,
            pageSize: 30,
            stats: nil
        )
        
        XCTAssertEqual(viewModel.historyItems.count, 1)
        XCTAssertEqual(viewModel.historyItems.first?.id, "1")
    }
    
    func testHistoryItemsReturnsEmptyWhenPageIsNil() {
        viewModel.historyPage = nil
        
        XCTAssertTrue(viewModel.historyItems.isEmpty)
    }
    
    func testCanLoadMoreReturnsTrueWhenMoreDataExists() {
        viewModel.historyPage = QueueHistoryPage(
            entries: Array(repeating: QueueHistoryEntry(
                id: "1",
                jobName: "test.gcode",
                printerName: "Prusa MK3",
                status: "completed",
                completedAt: Date(),
                durationSeconds: 3600
            ), count: 30),
            totalCount: 100,
            currentPage: 1,
            pageSize: 30,
            stats: nil
        )
        viewModel.currentOffset = 0
        
        XCTAssertTrue(viewModel.canLoadMore)
    }
    
    func testCanLoadMoreReturnsFalseWhenNoMoreData() {
        // canLoadMore returns `entries.count < totalCount`. When the loaded page
        // has drained the total, both sides equal zero (or n == n) and the flag
        // is false.
        viewModel.historyPage = QueueHistoryPage(
            entries: [],
            totalCount: 0,
            currentPage: 1,
            pageSize: 30,
            stats: nil
        )
        viewModel.currentOffset = 0
        
        XCTAssertFalse(viewModel.canLoadMore)
    }
    
    func testCanLoadMoreReturnsFalseWhenPageIsNil() {
        viewModel.historyPage = nil
        
        XCTAssertFalse(viewModel.canLoadMore)
    }
    
    // MARK: - Unconfigured Guard
    
    func testLoadHistoryDoesNothingWhenUnconfigured() async {
        viewModel = JobHistoryViewModel()
        
        await viewModel.loadHistory()
        
        XCTAssertNil(viewModel.historyPage)
        XCTAssertNil(mockJobAnalyticsService.getHistoryCalledWith)
    }
}

// MARK: - Test Helpers

/// Deterministic gated fake used by the #810 concurrency and cancellation
/// tests. `getHistory` suspends until `release()` is called, and emits a
/// signal on entry so the test can await the exact moment the request
/// begins without polling, sleeping, or `Task.yield`ing.
private final class GatedJobAnalyticsService: JobAnalyticsServiceProtocol, @unchecked Sendable {
    var historyPageToReturn: QueueHistoryPage?
    var errorToThrow: Error?
    private(set) var callCount = 0

    private let entered: AsyncStream<Int>
    private let enteredContinuation: AsyncStream<Int>.Continuation
    private var iterator: AsyncStream<Int>.AsyncIterator

    private var releaseContinuation: CheckedContinuation<Void, Never>?
    private let releaseLock = NSLock()

    init() {
        let stream = AsyncStream<Int>.makeStream()
        self.entered = stream.stream
        self.enteredContinuation = stream.continuation
        self.iterator = stream.stream.makeAsyncIterator()
    }

    /// Awaits the next entry into `getHistory` (returns the offset requested).
    func awaitEntered() async -> Int? {
        await iterator.next()
    }

    /// Resumes the currently gated `getHistory` call.
    func release() {
        releaseLock.lock()
        let continuation = releaseContinuation
        releaseContinuation = nil
        releaseLock.unlock()
        continuation?.resume()
    }

    func getHistory(limit: Int?, offset: Int?, sortBy: String?, statuses: String?, dateStart: Date?, dateEnd: Date?) async throws -> QueueHistoryPage {
        callCount += 1
        enteredContinuation.yield(offset ?? -1)
        await withCheckedContinuation { (continuation: CheckedContinuation<Void, Never>) in
            releaseLock.lock()
            releaseContinuation = continuation
            releaseLock.unlock()
        }
        if let error = errorToThrow { throw error }
        return historyPageToReturn!
    }

    // Unused protocol requirements — the #810 tests only exercise getHistory.
    func getQueuedJobs(filterStatus: String?, filterModel: String?, filterMaterial: String?, limit: Int?, offset: Int?) async throws -> [QueuedJobWithMeta] { [] }
    func getStats() async throws -> QueueStats {
        QueueStats(totalQueued: 0, totalPrinting: 0, totalPaused: 0, averageWaitTimeMinutes: 0, byModel: [])
    }
    func getModelStats() async throws -> [QueuePrinterModelStats] { [] }
    func getTimeline(dateFrom: Date?, dateTo: Date?, printerId: UUID?, filterStatus: String?, limit: Int?) async throws -> [TimelineEvent] { [] }
    func getJobStateHistory(jobId: String) async throws -> JobStateHistory {
        JobStateHistory(jobId: jobId, jobName: "", transitions: [], totalDurationSeconds: 0, estimatedDurationSeconds: nil, variancePercent: nil)
    }
    func getDurationAnalytics(printerId: UUID?, dateFrom: Date?, dateTo: Date?) async throws -> DurationAnalytics {
        throw TestError.generic
    }
}
