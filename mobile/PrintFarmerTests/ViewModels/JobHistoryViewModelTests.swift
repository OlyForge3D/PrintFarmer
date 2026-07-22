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

        let gated = GatedJobAnalyticsService(maxGatedCalls: 1)
        gated.historyPageToReturn = QueueHistoryPage(
            entries: [],
            totalCount: 100,
            currentPage: 2,
            pageSize: 30,
            stats: nil
        )
        viewModel.configure(jobAnalyticsService: gated)

        let task = Task { await viewModel.loadMore() }

        // Deterministic barrier: `awaitEntered()` unblocks only after the
        // gate has installed the per-call release continuation, so the
        // subsequent `release(callIndex:)` cannot race installation and be
        // dropped. No sleeps, polling, Task.yield, or elapsed-time gates.
        guard let callIndex = await gated.awaitEntered() else {
            return XCTFail("expected gate entry signal")
        }

        viewModel.isViewActive = false
        await gated.release(callIndex: callIndex)
        await task.value

        let callCount = await gated.callCount
        XCTAssertEqual(callCount, 1)
        let recorded = await gated.recordedCalls
        XCTAssertEqual(recorded.map(\.offset), [30])
        XCTAssertEqual(viewModel.currentOffset, 0)
        // Cursor stayed at 0, and the historyPage was not overwritten.
        XCTAssertEqual(viewModel.historyPage?.entries.count, 0)
        XCTAssertEqual(viewModel.historyPage?.currentPage, 1)
    }

    /// Regression for #810. Concurrent duplicate `loadMore()` calls must be
    /// suppressed by the `!isLoadingMore` guard so the service is invoked
    /// exactly once and the cursor cannot drift by more than one page.
    ///
    /// `maxGatedCalls: 1` bounds the gate so a regression that lets the
    /// second call reach the service records a second entry and returns
    /// immediately (without gating). The subsequent `callCount == 1`
    /// assertion then fails deterministically — the test cannot hang
    /// waiting on an un-released continuation.
    func testLoadMoreConcurrentCallsAreSuppressed() async {
        viewModel.historyPage = QueueHistoryPage(
            entries: [],
            totalCount: 100,
            currentPage: 1,
            pageSize: 30,
            stats: nil
        )

        let gated = GatedJobAnalyticsService(maxGatedCalls: 1)
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
        guard let firstIndex = await gated.awaitEntered() else {
            return XCTFail("expected gate entry signal for first call")
        }
        XCTAssertEqual(firstIndex, 0)

        // Second call runs while the first is in flight. The synchronous
        // `!isLoadingMore` guard must reject it before any suspension so
        // the service is never re-invoked.
        await viewModel.loadMore()

        // Deterministic regression check: performed BEFORE releasing the
        // first call so a regressed guard fails via assertion, not by
        // hanging on an ungated request.
        let midCallCount = await gated.callCount
        XCTAssertEqual(midCallCount, 1)
        let recorded = await gated.recordedCalls
        XCTAssertEqual(recorded.count, 1)
        XCTAssertEqual(recorded.first?.offset, 30)
        XCTAssertEqual(recorded.first?.limit, 30)

        await gated.release(callIndex: firstIndex)
        await first.value

        let finalCallCount = await gated.callCount
        XCTAssertEqual(finalCallCount, 1)
        XCTAssertEqual(viewModel.currentOffset, 30)
    }

    /// Direct proof that the gate's install-then-signal ordering rules out
    /// dropped or overwritten release continuations. Two calls are gated in
    /// order; each `release(callIndex:)` targets a specific call, and the
    /// gate cannot leak or resume the wrong continuation because state is
    /// keyed by unique per-call indices under actor isolation.
    func testGatedFakeInstallsReleaseBeforeSignalingEntry() async {
        let gated = GatedJobAnalyticsService(maxGatedCalls: 2)
        gated.historyPageToReturn = QueueHistoryPage(
            entries: [],
            totalCount: 0,
            currentPage: 1,
            pageSize: 30,
            stats: nil
        )

        // Two concurrent gated getHistory calls.
        let taskA = Task {
            _ = try? await gated.getHistory(
                limit: 30, offset: 30,
                sortBy: nil, statuses: nil, dateStart: nil, dateEnd: nil
            )
        }
        guard let indexA = await gated.awaitEntered() else {
            return XCTFail("expected entry signal for call A")
        }

        let taskB = Task {
            _ = try? await gated.getHistory(
                limit: 30, offset: 60,
                sortBy: nil, statuses: nil, dateStart: nil, dateEnd: nil
            )
        }
        guard let indexB = await gated.awaitEntered() else {
            return XCTFail("expected entry signal for call B")
        }

        XCTAssertEqual(indexA, 0)
        XCTAssertEqual(indexB, 1)

        // Release out of order to prove per-call keying works and returns
        // `.released` for each matched continuation.
        let outcomeB = await gated.release(callIndex: indexB)
        XCTAssertEqual(outcomeB, .released)
        await taskB.value
        let outcomeA = await gated.release(callIndex: indexA)
        XCTAssertEqual(outcomeA, .released)
        await taskA.value

        let recorded = await gated.recordedCalls
        XCTAssertEqual(recorded.count, 2)
        XCTAssertEqual(recorded.map(\.offset), [30, 60])
    }

    /// Direct proof of the gate's release-lifecycle state machine. Covers
    /// every release outcome per Hicks's remediation-delta review: valid
    /// pre-entry buffered release, normal in-order release, duplicate
    /// release on an already-terminal index, out-of-order release across
    /// concurrent calls, and release of an ungated excess index. All
    /// duplicate/invalid/excess releases return `.rejected` without
    /// mutating actor state, so the gate is safe to invoke idempotently
    /// (including from teardown paths) and its release-lifecycle state
    /// remains bounded by `maxGatedCalls`.
    func testGatedFakeReleaseLifecycleReturnsExpectedOutcomes() async {
        let gated = GatedJobAnalyticsService(maxGatedCalls: 2)
        gated.historyPageToReturn = QueueHistoryPage(
            entries: [],
            totalCount: 0,
            currentPage: 1,
            pageSize: 30,
            stats: nil
        )

        // Invalid indices are rejected without state mutation, even before
        // any calls have been made.
        let earlyNegative = await gated.release(callIndex: -1)
        XCTAssertEqual(earlyNegative, .rejected)
        let earlyExcess = await gated.release(callIndex: 2)
        XCTAssertEqual(earlyExcess, .rejected)

        // Pre-entry release for a valid gated index buffers and is consumed
        // by the matching entry. `.buffered` is a terminal outcome for that
        // index; a second release must reject rather than double-buffer.
        let preEntry = await gated.release(callIndex: 0)
        XCTAssertEqual(preEntry, .buffered)
        let preEntryDuplicate = await gated.release(callIndex: 0)
        XCTAssertEqual(preEntryDuplicate, .rejected)

        // Call A consumes the buffered release and returns immediately.
        let taskA = Task {
            _ = try? await gated.getHistory(
                limit: 30, offset: 30,
                sortBy: nil, statuses: nil, dateStart: nil, dateEnd: nil
            )
        }
        await taskA.value
        _ = await gated.awaitEntered()

        // Duplicate release on the already-terminal index still rejects
        // after entry has consumed the buffered release. No crash.
        let postEntryDuplicate = await gated.release(callIndex: 0)
        XCTAssertEqual(postEntryDuplicate, .rejected)

        // Normal in-order release: call B enters, is released via `.released`.
        let taskB = Task {
            _ = try? await gated.getHistory(
                limit: 30, offset: 60,
                sortBy: nil, statuses: nil, dateStart: nil, dateEnd: nil
            )
        }
        guard let indexB = await gated.awaitEntered() else {
            return XCTFail("expected entry signal for call B")
        }
        XCTAssertEqual(indexB, 1)
        let normalRelease = await gated.release(callIndex: indexB)
        XCTAssertEqual(normalRelease, .released)
        await taskB.value

        // Duplicate release on the just-released index rejects.
        let normalDuplicate = await gated.release(callIndex: indexB)
        XCTAssertEqual(normalDuplicate, .rejected)

        // An excess call (index >= maxGatedCalls) is recorded and returns
        // immediately without gating, so a release on that index must
        // reject rather than buffer state for a call that will never wait.
        let taskExcess = Task {
            _ = try? await gated.getHistory(
                limit: 30, offset: 90,
                sortBy: nil, statuses: nil, dateStart: nil, dateEnd: nil
            )
        }
        await taskExcess.value
        _ = await gated.awaitEntered()
        let excessRelease = await gated.release(callIndex: 2)
        XCTAssertEqual(excessRelease, .rejected)

        // Complete request history is preserved across all outcomes.
        let recorded = await gated.recordedCalls
        XCTAssertEqual(recorded.map(\.offset), [30, 60, 90])
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
/// tests. State is serialized through an actor, per-call state is keyed by
/// unique call index, and each request installs its release continuation
/// **before** signaling entry — so a `release(callIndex:)` arriving from the
/// test task in the window between "gate entered" and "continuation
/// installed" cannot be dropped, and a duplicate call cannot overwrite a
/// prior continuation. Releases that arrive before their matching call are
/// buffered by call index, never lost. `maxGatedCalls` bounds the number of
/// gated calls: excess calls are recorded and returned immediately without
/// gating, so a regression in duplicate-load suppression fails via a
/// `callCount` assertion instead of hanging on an un-released continuation.
///
/// Each valid gated call index may be released **exactly once**. Every
/// release returns a `ReleaseOutcome` describing what happened — a
/// duplicate release, an out-of-range/negative index, or an index for an
/// ungated excess call all return `.rejected` without mutating actor
/// state. Release-lifecycle state (`bufferedReleases`, `pendingReleases`,
/// `terminated`) is therefore bounded by `maxGatedCalls` and safe to call
/// idempotently from teardown paths.
///
/// No sleep, polling, `Task.yield`, or elapsed-time gates are used.
private final class GatedJobAnalyticsService: JobAnalyticsServiceProtocol, @unchecked Sendable {
    struct RecordedCall: Sendable {
        let index: Int
        let limit: Int?
        let offset: Int?
        let sortBy: String?
        let statuses: String?
        let dateStart: Date?
        let dateEnd: Date?
    }

    /// Result of a single `release(callIndex:)` call. Terminal outcomes
    /// mark the index as consumed; further releases on the same or an
    /// invalid index return `.rejected` without buffering any state.
    enum ReleaseOutcome: Sendable, Equatable {
        /// Matched a pending gated continuation and resumed it.
        case released
        /// Arrived before the matching entry; buffered and consumed by the
        /// upcoming entry. Marks the index terminal.
        case buffered
        /// Duplicate release on an already-terminal index, negative index,
        /// or an index for an ungated excess call (>= `maxGatedCalls`).
        /// No state mutation, safe to invoke idempotently.
        case rejected
    }

    var historyPageToReturn: QueueHistoryPage?
    var errorToThrow: Error?

    private let gate: Gate

    init(maxGatedCalls: Int = .max) {
        self.gate = Gate(maxGatedCalls: maxGatedCalls)
    }

    /// Awaits the entry of the next `getHistory` invocation and returns its
    /// call index. Unblocks only after the gate has recorded the call AND
    /// installed the release continuation, so `release(callIndex:)` from the
    /// caller cannot race installation.
    func awaitEntered() async -> Int? {
        await gate.awaitEntered()
    }

    /// Resumes the specified gated call, or buffers the release for the
    /// call at that index if the request has not reached the gate yet.
    /// Returns a `ReleaseOutcome` describing the effect on gate state;
    /// duplicate/invalid/excess releases return `.rejected` and never
    /// mutate state (safe for idempotent teardown).
    @discardableResult
    func release(callIndex: Int) async -> ReleaseOutcome {
        await gate.release(callIndex: callIndex)
    }

    var callCount: Int {
        get async { await gate.callCount }
    }

    var recordedCalls: [RecordedCall] {
        get async { await gate.recordedCalls }
    }

    func getHistory(limit: Int?, offset: Int?, sortBy: String?, statuses: String?, dateStart: Date?, dateEnd: Date?) async throws -> QueueHistoryPage {
        _ = await gate.recordAndWait(
            limit: limit, offset: offset, sortBy: sortBy,
            statuses: statuses, dateStart: dateStart, dateEnd: dateEnd
        )
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

    private actor Gate {
        private var recorded: [RecordedCall] = []
        private var pendingReleases: [Int: CheckedContinuation<Void, Never>] = [:]
        private var bufferedReleases: Set<Int> = []
        /// Indices already consumed by a `.released` or `.buffered` outcome.
        /// Bounded by `maxGatedCalls` (only valid gated indices are ever
        /// inserted). Any subsequent `release(callIndex:)` on a terminal
        /// index returns `.rejected` without further state mutation.
        private var terminated: Set<Int> = []
        private var enteredQueue: [Int] = []
        private var enteredWaiter: CheckedContinuation<Int?, Never>?
        private let maxGatedCalls: Int

        init(maxGatedCalls: Int) {
            self.maxGatedCalls = maxGatedCalls
        }

        var recordedCalls: [RecordedCall] { recorded }
        var callCount: Int { recorded.count }

        /// Awaits the next entry signal. If entries have already been
        /// recorded but not yet consumed, returns immediately from the
        /// FIFO queue. Otherwise installs a single waiter continuation
        /// resumed by the next `signalEntered(_:)` call. At most one
        /// `awaitEntered()` may be pending at a time; the precondition
        /// surfaces any test-side misuse as a failure rather than a hang.
        func awaitEntered() async -> Int? {
            if !enteredQueue.isEmpty {
                return enteredQueue.removeFirst()
            }
            return await withCheckedContinuation { (continuation: CheckedContinuation<Int?, Never>) in
                precondition(enteredWaiter == nil, "only one awaitEntered() may be pending at a time")
                enteredWaiter = continuation
            }
        }

        private func signalEntered(_ index: Int) {
            if let waiter = enteredWaiter {
                enteredWaiter = nil
                waiter.resume(returning: index)
            } else {
                enteredQueue.append(index)
            }
        }

        func recordAndWait(limit: Int?, offset: Int?, sortBy: String?, statuses: String?, dateStart: Date?, dateEnd: Date?) async -> Int {
            let index = recorded.count
            recorded.append(RecordedCall(
                index: index, limit: limit, offset: offset,
                sortBy: sortBy, statuses: statuses,
                dateStart: dateStart, dateEnd: dateEnd
            ))

            guard index < maxGatedCalls else {
                // Excess call — signal entry so the test can observe it and
                // return immediately without gating so the test cannot hang.
                signalEntered(index)
                return index
            }

            await withCheckedContinuation { (continuation: CheckedContinuation<Void, Never>) in
                // Install-then-signal. `signalEntered` is invoked here,
                // AFTER `pendingReleases[index]` is populated (or a matching
                // buffered release is consumed), so any concurrent
                // `release(callIndex:)` reaching the actor after entry is
                // observed by the test cannot race installation.
                //
                // Indices are unique (recorded.count is monotonic), so
                // `pendingReleases[index]` is guaranteed nil here.
                if bufferedReleases.remove(index) != nil {
                    continuation.resume()
                } else {
                    pendingReleases[index] = continuation
                }
                signalEntered(index)
            }
            return index
        }

        func release(callIndex: Int) -> ReleaseOutcome {
            // Reject out-of-range/excess indices without mutating state.
            // Bounds release-lifecycle state to `maxGatedCalls`.
            guard callIndex >= 0, callIndex < maxGatedCalls else {
                return .rejected
            }
            // Reject duplicate releases idempotently — no crash, no state
            // mutation, safe to call from teardown paths.
            guard !terminated.contains(callIndex) else {
                return .rejected
            }
            if let continuation = pendingReleases.removeValue(forKey: callIndex) {
                terminated.insert(callIndex)
                continuation.resume()
                return .released
            }
            // No entry yet — buffer for the upcoming matching call and mark
            // terminal so a second release on the same index rejects.
            bufferedReleases.insert(callIndex)
            terminated.insert(callIndex)
            return .buffered
        }
    }
}
