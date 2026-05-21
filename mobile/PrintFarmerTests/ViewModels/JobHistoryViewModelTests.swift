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
        let page = QueueHistoryPage(entries: [], totalCount: 5, currentPage: 1, pageSize: 30, stats: nil)
        mockJobAnalyticsService.historyPageToReturn = page
        await viewModel.loadHistory()
        
        // Clear call tracking
        mockJobAnalyticsService.getHistoryCalledWith = nil
        
        await viewModel.loadMore()
        
        // Should not call service since totalCount (5) <= currentOffset (0) + items.count (0)
        XCTAssertNil(mockJobAnalyticsService.getHistoryCalledWith)
    }
    
    func testLoadMoreHandlesError() async {
        let page = QueueHistoryPage(entries: [], totalCount: 100, currentPage: 1, pageSize: 30, stats: nil)
        mockJobAnalyticsService.historyPageToReturn = page
        await viewModel.loadHistory()
        
        mockJobAnalyticsService.errorToThrow = TestError.generic
        
        await viewModel.loadMore()
        
        XCTAssertNotNil(viewModel.error)
        XCTAssertFalse(viewModel.isLoadingMore)
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
        mockJobAnalyticsService.errorToThrow = TestError.generic
        
        await viewModel.loadTimeline(dateFrom: Date(), dateTo: Date())
        
        XCTAssertTrue(viewModel.timeline.isEmpty)
        XCTAssertNotNil(viewModel.error)
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
        viewModel.historyPage = QueueHistoryPage(
            entries: [],
            totalCount: 5,
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
