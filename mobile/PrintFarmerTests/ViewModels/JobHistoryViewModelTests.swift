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

    private func commitHistory(_ page: QueueHistoryPage) async {
        mockJobAnalyticsService.errorToThrow = nil
        mockJobAnalyticsService.historyPageToReturn = page
        await viewModel.loadHistory()
    }

    private func historyEntry(_ id: String) -> QueueHistoryEntry {
        QueueHistoryEntry(
            id: id,
            jobName: "\(id).gcode",
            printerName: "Printer \(id)",
            status: "completed",
            completedAt: Date(timeIntervalSince1970: 1_700_000_000),
            durationSeconds: 600
        )
    }

    private func historyPage(
        _ ids: [String],
        totalCount: Int = 100,
        currentPage: Int,
        stats: QueueHistoryStats? = nil
    ) -> QueueHistoryPage {
        QueueHistoryPage(
            entries: ids.map(historyEntry),
            totalCount: totalCount,
            currentPage: currentPage,
            pageSize: 30,
            stats: stats
        )
    }

    private func timelineEvent(_ id: String) -> TimelineEvent {
        TimelineEvent(
            jobId: id,
            jobName: "\(id).gcode",
            printerName: "Printer \(id)",
            state: "completed",
            enteredAtUtc: Date(timeIntervalSince1970: 1_700_000_000),
            exitedAtUtc: Date(timeIntervalSince1970: 1_700_000_600),
            durationSeconds: 600,
            estimatedDurationSeconds: 600,
            variancePercent: 0
        )
    }

    private func jobStateHistory(_ id: String) -> JobStateHistory {
        JobStateHistory(
            jobId: id,
            jobName: "\(id).gcode",
            transitions: [],
            totalDurationSeconds: 600,
            estimatedDurationSeconds: 600,
            variancePercent: 0
        )
    }

    private func requireEntry(
        _ service: ScriptedJobAnalyticsService,
        registration: Int,
        file: StaticString = #filePath,
        line: UInt = #line
    ) async -> ScriptedJobAnalyticsService.RecordedCall? {
        switch await service.awaitEntryOrOperationFinished(registration: registration) {
        case .entered(let call):
            XCTAssertEqual(call.index, registration, file: file, line: line)
            return call
        case .operationFinished:
            XCTFail(
                "history operation finished before registered call \(registration) entered the service",
                file: file,
                line: line
            )
            return nil
        }
    }

    private func requireTimelineEntry(
        _ service: ScriptedJobAnalyticsService,
        registration: Int,
        file: StaticString = #filePath,
        line: UInt = #line
    ) async -> ScriptedJobAnalyticsService.IndexedCall<
        ScriptedJobAnalyticsService.TimelineCall
    >? {
        switch await service.awaitTimelineEntryOrOperationFinished(
            registration: registration
        ) {
        case .entered(let call):
            XCTAssertEqual(call.index, registration, file: file, line: line)
            return call
        case .operationFinished:
            XCTFail(
                "timeline operation finished before registered call \(registration) entered",
                file: file,
                line: line
            )
            return nil
        }
    }

    private func requireJobStateEntry(
        _ service: ScriptedJobAnalyticsService,
        registration: Int,
        file: StaticString = #filePath,
        line: UInt = #line
    ) async -> ScriptedJobAnalyticsService.IndexedCall<
        ScriptedJobAnalyticsService.JobStateCall
    >? {
        switch await service.awaitJobStateEntryOrOperationFinished(
            registration: registration
        ) {
        case .entered(let call):
            XCTAssertEqual(call.index, registration, file: file, line: line)
            return call
        case .operationFinished:
            XCTFail(
                "job-state operation finished before registered call \(registration) entered",
                file: file,
                line: line
            )
            return nil
        }
    }

    private func runNextCleanup(
        _ enqueuer: ManualCancellationCleanupEnqueuer,
        file: StaticString = #filePath,
        line: UInt = #line
    ) async {
        let didRun = await enqueuer.runNext()
        XCTAssertTrue(didRun, file: file, line: line)
    }

    /// Per-call scripted history service for #853 interleaving tests. Every
    /// operation is registered before dispatch, signals entry only after its
    /// release continuation is installed, receives an explicit release outcome,
    /// and is drained by its owning test task. If the view model returns before
    /// entering the registered call, `operationFinished` resolves the entry wait
    /// with an assertion-friendly event instead of leaving the test suspended.
    private final class ScriptedJobAnalyticsService: JobAnalyticsServiceProtocol, @unchecked Sendable {
        struct RecordedCall: Sendable {
            let index: Int
            let limit: Int?
            let offset: Int?
            let sortBy: String?
            let statuses: String?
            let dateStart: Date?
            let dateEnd: Date?
        }

        enum Outcome: Sendable {
            case success(QueueHistoryPage)
            case failure
        }

        enum SecondaryOutcome<Value: Sendable>: Sendable {
            case success(Value)
            case failure
        }

        struct IndexedCall<Value: Sendable>: Sendable {
            let index: Int
            let value: Value
        }

        enum SecondaryEntryEvent<Value: Sendable>: Sendable {
            case entered(IndexedCall<Value>)
            case operationFinished
        }

        struct TimelineCall: Sendable {
            let dateFrom: Date?
            let dateTo: Date?
            let printerId: UUID?
            let filterStatus: String?
            let limit: Int?
        }

        struct JobStateCall: Sendable {
            let jobId: String
        }

        enum EntryEvent: Sendable {
            case entered(RecordedCall)
            case operationFinished
        }

        enum ReleaseOutcome: Sendable, Equatable {
            case released
            case buffered
            case rejected
        }

        private enum ScriptedFailure: Error {
            case expected
            case unexpectedCall
        }

        private let coordinator = Coordinator()
        private let timelineCoordinator = SecondaryCoordinator<[TimelineEvent], TimelineCall>()
        private let jobStateCoordinator = SecondaryCoordinator<JobStateHistory, JobStateCall>()

        func register(_ outcome: Outcome) async -> Int {
            await coordinator.register(outcome)
        }

        func awaitEntryOrOperationFinished(registration: Int) async -> EntryEvent {
            await coordinator.awaitEntryOrOperationFinished(registration: registration)
        }

        func operationFinished(registration: Int) async {
            await coordinator.operationFinished(registration: registration)
        }

        func release(registration: Int) async -> ReleaseOutcome {
            await coordinator.release(registration: registration)
        }

        var recordedCalls: [RecordedCall] {
            get async { await coordinator.recordedCalls }
        }

        func registerTimeline(_ outcome: SecondaryOutcome<[TimelineEvent]>) async -> Int {
            await timelineCoordinator.register(outcome)
        }

        func awaitTimelineEntryOrOperationFinished(
            registration: Int
        ) async -> SecondaryEntryEvent<TimelineCall> {
            await timelineCoordinator.awaitEntryOrOperationFinished(registration: registration)
        }

        func timelineOperationFinished(registration: Int) async {
            await timelineCoordinator.operationFinished(registration: registration)
        }

        func releaseTimeline(registration: Int) async -> ReleaseOutcome {
            await timelineCoordinator.release(registration: registration)
        }

        func registerJobState(_ outcome: SecondaryOutcome<JobStateHistory>) async -> Int {
            await jobStateCoordinator.register(outcome)
        }

        func awaitJobStateEntryOrOperationFinished(
            registration: Int
        ) async -> SecondaryEntryEvent<JobStateCall> {
            await jobStateCoordinator.awaitEntryOrOperationFinished(registration: registration)
        }

        func jobStateOperationFinished(registration: Int) async {
            await jobStateCoordinator.operationFinished(registration: registration)
        }

        func releaseJobState(registration: Int) async -> ReleaseOutcome {
            await jobStateCoordinator.release(registration: registration)
        }

        func getHistory(
            limit: Int?,
            offset: Int?,
            sortBy: String?,
            statuses: String?,
            dateStart: Date?,
            dateEnd: Date?
        ) async throws -> QueueHistoryPage {
            let outcome = await coordinator.recordAndWait(
                limit: limit,
                offset: offset,
                sortBy: sortBy,
                statuses: statuses,
                dateStart: dateStart,
                dateEnd: dateEnd
            )
            guard let outcome else {
                throw ScriptedFailure.unexpectedCall
            }
            switch outcome {
            case .success(let page):
                return page
            case .failure:
                throw ScriptedFailure.expected
            }
        }

        func getQueuedJobs(
            filterStatus: String?,
            filterModel: String?,
            filterMaterial: String?,
            limit: Int?,
            offset: Int?
        ) async throws -> [QueuedJobWithMeta] {
            []
        }

        func getStats() async throws -> QueueStats {
            QueueStats(
                totalQueued: 0,
                totalPrinting: 0,
                totalPaused: 0,
                averageWaitTimeMinutes: 0,
                byModel: []
            )
        }

        func getModelStats() async throws -> [QueuePrinterModelStats] {
            []
        }

        func getTimeline(
            dateFrom: Date?,
            dateTo: Date?,
            printerId: UUID?,
            filterStatus: String?,
            limit: Int?
        ) async throws -> [TimelineEvent] {
            let outcome = await timelineCoordinator.recordAndWait(
                TimelineCall(
                    dateFrom: dateFrom,
                    dateTo: dateTo,
                    printerId: printerId,
                    filterStatus: filterStatus,
                    limit: limit
                )
            )
            guard let outcome else { throw ScriptedFailure.unexpectedCall }
            switch outcome {
            case .success(let timeline):
                return timeline
            case .failure:
                throw ScriptedFailure.expected
            }
        }

        func getJobStateHistory(jobId: String) async throws -> JobStateHistory {
            let outcome = await jobStateCoordinator.recordAndWait(
                JobStateCall(jobId: jobId)
            )
            guard let outcome else { throw ScriptedFailure.unexpectedCall }
            switch outcome {
            case .success(let history):
                return history
            case .failure:
                throw ScriptedFailure.expected
            }
        }

        func getDurationAnalytics(
            printerId: UUID?,
            dateFrom: Date?,
            dateTo: Date?
        ) async throws -> DurationAnalytics {
            throw ScriptedFailure.expected
        }

        private actor Coordinator {
            private var outcomes: [Outcome] = []
            private var recorded: [RecordedCall] = []
            private var pendingReleases: [Int: CheckedContinuation<Void, Never>] = [:]
            private var bufferedReleases: Set<Int> = []
            private var terminalReleases: Set<Int> = []
            private var enteredRegistrations: Set<Int> = []
            private var entryEvents: [Int: EntryEvent] = [:]
            private var entryWaiters: [Int: CheckedContinuation<EntryEvent, Never>] = [:]

            var recordedCalls: [RecordedCall] { recorded }

            func register(_ outcome: Outcome) -> Int {
                let registration = outcomes.count
                outcomes.append(outcome)
                return registration
            }

            func awaitEntryOrOperationFinished(registration: Int) async -> EntryEvent {
                if let event = entryEvents.removeValue(forKey: registration) {
                    return event
                }
                return await withCheckedContinuation { continuation in
                    precondition(
                        entryWaiters[registration] == nil,
                        "only one entry waiter is allowed per scripted registration"
                    )
                    entryWaiters[registration] = continuation
                }
            }

            func operationFinished(registration: Int) {
                guard !enteredRegistrations.contains(registration) else { return }
                signal(.operationFinished, registration: registration)
            }

            func recordAndWait(
                limit: Int?,
                offset: Int?,
                sortBy: String?,
                statuses: String?,
                dateStart: Date?,
                dateEnd: Date?
            ) async -> Outcome? {
                let index = recorded.count
                let call = RecordedCall(
                    index: index,
                    limit: limit,
                    offset: offset,
                    sortBy: sortBy,
                    statuses: statuses,
                    dateStart: dateStart,
                    dateEnd: dateEnd
                )
                recorded.append(call)

                guard outcomes.indices.contains(index) else {
                    enteredRegistrations.insert(index)
                    signal(.entered(call), registration: index)
                    return nil
                }
                let outcome = outcomes[index]

                await withCheckedContinuation { continuation in
                    if bufferedReleases.remove(index) != nil {
                        continuation.resume()
                    } else {
                        pendingReleases[index] = continuation
                    }
                    enteredRegistrations.insert(index)
                    signal(.entered(call), registration: index)
                }
                return outcome
            }

            func release(registration: Int) -> ReleaseOutcome {
                guard outcomes.indices.contains(registration),
                      !terminalReleases.contains(registration) else {
                    return .rejected
                }

                terminalReleases.insert(registration)
                if let continuation = pendingReleases.removeValue(forKey: registration) {
                    continuation.resume()
                    return .released
                }

                bufferedReleases.insert(registration)
                return .buffered
            }

            private func signal(_ event: EntryEvent, registration: Int) {
                if let waiter = entryWaiters.removeValue(forKey: registration) {
                    waiter.resume(returning: event)
                } else if entryEvents[registration] == nil {
                    entryEvents[registration] = event
                }
            }
        }

        private actor SecondaryCoordinator<Response: Sendable, Call: Sendable> {
            private var outcomes: [SecondaryOutcome<Response>] = []
            private var recorded: [IndexedCall<Call>] = []
            private var pendingReleases: [Int: CheckedContinuation<Void, Never>] = [:]
            private var bufferedReleases: Set<Int> = []
            private var terminalReleases: Set<Int> = []
            private var enteredRegistrations: Set<Int> = []
            private var entryEvents: [Int: SecondaryEntryEvent<Call>] = [:]
            private var entryWaiters:
                [Int: CheckedContinuation<SecondaryEntryEvent<Call>, Never>] = [:]

            func register(_ outcome: SecondaryOutcome<Response>) -> Int {
                let registration = outcomes.count
                outcomes.append(outcome)
                return registration
            }

            func awaitEntryOrOperationFinished(
                registration: Int
            ) async -> SecondaryEntryEvent<Call> {
                if let event = entryEvents.removeValue(forKey: registration) {
                    return event
                }
                return await withCheckedContinuation { continuation in
                    precondition(
                        entryWaiters[registration] == nil,
                        "only one secondary entry waiter is allowed per registration"
                    )
                    entryWaiters[registration] = continuation
                }
            }

            func operationFinished(registration: Int) {
                guard !enteredRegistrations.contains(registration) else { return }
                signal(.operationFinished, registration: registration)
            }

            func recordAndWait(_ call: Call) async -> SecondaryOutcome<Response>? {
                let index = recorded.count
                let indexedCall = IndexedCall(index: index, value: call)
                recorded.append(indexedCall)

                guard outcomes.indices.contains(index) else {
                    enteredRegistrations.insert(index)
                    signal(.entered(indexedCall), registration: index)
                    return nil
                }
                let outcome = outcomes[index]

                await withCheckedContinuation { continuation in
                    if bufferedReleases.remove(index) != nil {
                        continuation.resume()
                    } else {
                        pendingReleases[index] = continuation
                    }
                    enteredRegistrations.insert(index)
                    signal(.entered(indexedCall), registration: index)
                }
                return outcome
            }

            func release(registration: Int) -> ReleaseOutcome {
                guard outcomes.indices.contains(registration),
                      !terminalReleases.contains(registration) else {
                    return .rejected
                }

                terminalReleases.insert(registration)
                if let continuation = pendingReleases.removeValue(forKey: registration) {
                    continuation.resume()
                    return .released
                }

                bufferedReleases.insert(registration)
                return .buffered
            }

            private func signal(
                _ event: SecondaryEntryEvent<Call>,
                registration: Int
            ) {
                if let waiter = entryWaiters.removeValue(forKey: registration) {
                    waiter.resume(returning: event)
                } else if entryEvents[registration] == nil {
                    entryEvents[registration] = event
                }
            }
        }
    }

    private actor TaskStartGate {
        enum ReleaseOutcome: Equatable {
            case released
            case buffered
            case rejected
        }

        private var parkedContinuation: CheckedContinuation<Void, Never>?
        private var enteredWaiter: CheckedContinuation<Void, Never>?
        private var hasEntered = false
        private var releaseBuffered = false
        private var releaseConsumed = false

        func park() async {
            precondition(!hasEntered, "TaskStartGate supports exactly one parked task")
            await withCheckedContinuation { continuation in
                hasEntered = true
                if releaseBuffered {
                    continuation.resume()
                } else {
                    parkedContinuation = continuation
                }
                enteredWaiter?.resume()
                enteredWaiter = nil
            }
        }

        func awaitEntered() async {
            if hasEntered { return }
            await withCheckedContinuation { continuation in
                precondition(enteredWaiter == nil, "only one entry waiter is allowed")
                enteredWaiter = continuation
            }
        }

        func release() -> ReleaseOutcome {
            guard !releaseConsumed else { return .rejected }
            releaseConsumed = true
            if let continuation = parkedContinuation {
                parkedContinuation = nil
                continuation.resume()
                return .released
            }
            releaseBuffered = true
            return .buffered
        }
    }

    private final class ManualCancellationCleanupEnqueuer: @unchecked Sendable {
        typealias Cleanup = @MainActor @Sendable () -> Void

        private let lock = NSLock()
        private var cleanups: [Cleanup] = []

        func enqueue(_ cleanup: @escaping Cleanup) {
            lock.lock()
            cleanups.append(cleanup)
            lock.unlock()
        }

        var pendingCount: Int {
            lock.lock()
            defer { lock.unlock() }
            return cleanups.count
        }

        func runNext() async -> Bool {
            guard let cleanup = takeNext() else { return false }
            await cleanup()
            return true
        }

        private func takeNext() -> Cleanup? {
            lock.lock()
            defer { lock.unlock() }
            guard !cleanups.isEmpty else { return nil }
            let cleanup = cleanups.removeFirst()
            return cleanup
        }
    }

    @MainActor
    private final class MainActorEvent {
        private var signaled = false
        private var waiter: CheckedContinuation<Void, Never>?
        private(set) var signalCount = 0

        func signal() {
            signalCount += 1
            signaled = true
            waiter?.resume()
            waiter = nil
        }

        func wait() async {
            if signaled { return }
            await withCheckedContinuation { continuation in
                precondition(waiter == nil, "only one MainActorEvent waiter is allowed")
                waiter = continuation
            }
        }
    }
    
    // MARK: - Initial State
    
    func testInitialState() {
        XCTAssertNil(viewModel.historyPage)
        XCTAssertTrue(viewModel.timeline.isEmpty)
        XCTAssertNil(viewModel.selectedJobHistory)
        XCTAssertFalse(viewModel.isLoading)
        XCTAssertFalse(viewModel.isLoadingMore)
        XCTAssertFalse(viewModel.isTimelineLoading)
        XCTAssertFalse(viewModel.isJobStateLoading)
        XCTAssertNil(viewModel.error)
        XCTAssertNil(viewModel.timelineError)
        XCTAssertNil(viewModel.jobStateError)
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
        let dateFrom = Date(timeIntervalSince1970: 1_700_000_000)
        let dateTo = Date(timeIntervalSince1970: 1_700_086_400)
        viewModel.dateFrom = dateFrom
        viewModel.dateTo = dateTo
        await commitHistory(QueueHistoryPage(
            entries: [previousEntry],
            totalCount: 100,
            currentPage: 1,
            pageSize: 30,
            stats: nil
        ))
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
        await commitHistory(QueueHistoryPage(
            entries: [],
            totalCount: 100,
            currentPage: 1,
            pageSize: 30,
            stats: nil
        ))
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
        await commitHistory(QueueHistoryPage(
            entries: [existing],
            totalCount: 100,
            currentPage: 1,
            pageSize: 30,
            stats: nil
        ))

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
        let activationToken = viewModel.activate()
        await commitHistory(QueueHistoryPage(
            entries: [],
            totalCount: 100,
            currentPage: 1,
            pageSize: 30,
            stats: nil
        ))

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

        viewModel.deactivate(activationToken: activationToken)
        await gated.release(callIndex: callIndex)
        await task.value

        let callCount = await gated.callCount
        XCTAssertEqual(callCount, 1)
        let recorded = await gated.recordedCalls
        XCTAssertEqual(recorded.map(\.offset), [30])
        XCTAssertEqual(viewModel.currentOffset, 0)
        XCTAssertFalse(viewModel.isLoadingMore)
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
        await commitHistory(QueueHistoryPage(
            entries: [],
            totalCount: 100,
            currentPage: 1,
            pageSize: 30,
            stats: nil
        ))

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

    // MARK: - Task-Scoped Mount Lifecycle (Issue #853)

    func testMountCancelledBeforeHelperBeginsDoesNotActivateOrRequest() async {
        let startGate = TaskStartGate()
        let cleanupQueue = ManualCancellationCleanupEnqueuer()
        var mirroredToken: UUID?
        let task = Task { @MainActor in
            await startGate.park()
            await JobAnalyticsMountLifecycle.runHistory(
                viewModel: viewModel,
                service: mockJobAnalyticsService,
                onAcquire: { mirroredToken = $0 },
                onRelease: { token in
                    if mirroredToken == token {
                        mirroredToken = nil
                    }
                },
                cleanupEnqueuer: { cleanupQueue.enqueue($0) }
            )
        }
        await startGate.awaitEntered()

        task.cancel()
        let startRelease = await startGate.release()
        XCTAssertEqual(startRelease, .released)
        await task.value

        XCTAssertEqual(cleanupQueue.pendingCount, 1)
        await runNextCleanup(cleanupQueue)
        XCTAssertEqual(viewModel.activationCountForTesting, 0)
        XCTAssertNil(mockJobAnalyticsService.getHistoryCalledWith)
        XCTAssertNil(mirroredToken)
    }

    func testPreInstallCleanupDoesNotConsumeExactTokenCleanup() async {
        mockJobAnalyticsService.historyPageToReturn = historyPage(
            ["must-not-load"],
            totalCount: 1,
            currentPage: 1
        )
        let beforeInstallGate = TaskStartGate()
        let cleanupQueue = ManualCancellationCleanupEnqueuer()
        var mirroredToken: UUID?
        var releasedToken: UUID?
        var releaseCount = 0
        let task = Task { @MainActor in
            await JobAnalyticsMountLifecycle.runHistory(
                viewModel: viewModel,
                service: mockJobAnalyticsService,
                onAcquire: { mirroredToken = $0 },
                onRelease: { token in
                    releasedToken = token
                    releaseCount += 1
                    if mirroredToken == token {
                        mirroredToken = nil
                    }
                },
                beforeInstall: {
                    await beforeInstallGate.park()
                },
                cleanupEnqueuer: { cleanupQueue.enqueue($0) }
            )
        }
        await beforeInstallGate.awaitEntered()
        XCTAssertEqual(viewModel.activationCountForTesting, 1)
        XCTAssertTrue(viewModel.isViewActive)
        XCTAssertNil(mirroredToken)

        task.cancel()
        XCTAssertEqual(cleanupQueue.pendingCount, 1)
        await runNextCleanup(cleanupQueue)
        XCTAssertEqual(releaseCount, 0)
        XCTAssertTrue(viewModel.isViewActive)

        let installRelease = await beforeInstallGate.release()
        XCTAssertEqual(installRelease, .released)
        await task.value

        XCTAssertEqual(cleanupQueue.pendingCount, 0)
        XCTAssertEqual(releaseCount, 1)
        XCTAssertNotNil(releasedToken)
        XCTAssertNil(mirroredToken)
        XCTAssertFalse(viewModel.isViewActive)
        XCTAssertNil(mockJobAnalyticsService.getHistoryCalledWith)
    }

    func testMountCancellationDeactivatesBeforeIgnoringServiceRelease() async {
        let service = ScriptedJobAnalyticsService()
        let registration = await service.register(
            .success(historyPage(["late"], totalCount: 1, currentPage: 1))
        )
        let cleanupQueue = ManualCancellationCleanupEnqueuer()
        let releaseEvent = MainActorEvent()
        var mirroredToken: UUID?
        let task = Task { @MainActor in
            await JobAnalyticsMountLifecycle.runHistory(
                viewModel: viewModel,
                service: service,
                onAcquire: { mirroredToken = $0 },
                onRelease: { token in
                    if mirroredToken == token {
                        mirroredToken = nil
                    }
                    releaseEvent.signal()
                },
                cleanupEnqueuer: { cleanupQueue.enqueue($0) }
            )
        }
        guard await requireEntry(service, registration: registration) != nil else {
            return
        }
        XCTAssertTrue(viewModel.isViewActive)
        XCTAssertTrue(viewModel.isLoading)
        XCTAssertNotNil(mirroredToken)

        let operationCleanupTarget = viewModel.cancellationCleanupTickForTesting + 1
        task.cancel()
        await viewModel.waitForCancellationCleanupForTesting(
            atLeast: operationCleanupTarget
        )
        XCTAssertEqual(cleanupQueue.pendingCount, 1)
        await runNextCleanup(cleanupQueue)
        await releaseEvent.wait()

        XCTAssertFalse(viewModel.isViewActive)
        XCTAssertFalse(viewModel.isLoading)
        XCTAssertNil(mirroredToken)

        let serviceRelease = await service.release(registration: registration)
        XCTAssertEqual(serviceRelease, .released)
        await task.value
        XCTAssertEqual(cleanupQueue.pendingCount, 1)
        await runNextCleanup(cleanupQueue)
        XCTAssertEqual(releaseEvent.signalCount, 1)
        XCTAssertTrue(viewModel.historyItems.isEmpty)
    }

    func testFailedInitialLoadKeepsMountOwnedUntilCancellation() async {
        let service = ScriptedJobAnalyticsService()
        let registration = await service.register(.failure)
        let cleanupQueue = ManualCancellationCleanupEnqueuer()
        let lifetimeArmed = MainActorEvent()
        var mirroredToken: UUID?
        var releaseCount = 0
        let task = Task { @MainActor in
            await JobAnalyticsMountLifecycle.runHistory(
                viewModel: viewModel,
                service: service,
                onAcquire: { mirroredToken = $0 },
                onRelease: { token in
                    releaseCount += 1
                    if mirroredToken == token {
                        mirroredToken = nil
                    }
                },
                onLifetimeArmed: { lifetimeArmed.signal() },
                cleanupEnqueuer: { cleanupQueue.enqueue($0) }
            )
        }
        guard await requireEntry(service, registration: registration) != nil else {
            task.cancel()
            await task.value
            return
        }
        let serviceRelease = await service.release(registration: registration)
        XCTAssertEqual(serviceRelease, .released)
        await lifetimeArmed.wait()

        XCTAssertNotNil(viewModel.error)
        XCTAssertTrue(viewModel.isViewActive)
        XCTAssertNotNil(mirroredToken)
        XCTAssertEqual(releaseCount, 0)
        XCTAssertFalse(viewModel.isLoading)

        task.cancel()
        await task.value
        XCTAssertEqual(cleanupQueue.pendingCount, 2)
        await runNextCleanup(cleanupQueue)
        await runNextCleanup(cleanupQueue)

        XCTAssertFalse(viewModel.isViewActive)
        XCTAssertNil(mirroredToken)
        XCTAssertEqual(releaseCount, 1)
    }

    func testCancellationBeforeLifetimeWaitIsBufferedAndCannotLoseWakeup() async {
        mockJobAnalyticsService.historyPageToReturn = historyPage(
            ["loaded"],
            totalCount: 1,
            currentPage: 1
        )
        let beforeLifetimeWaitGate = TaskStartGate()
        let cleanupQueue = ManualCancellationCleanupEnqueuer()
        var mirroredToken: UUID?
        var releaseCount = 0
        let task = Task { @MainActor in
            await JobAnalyticsMountLifecycle.runHistory(
                viewModel: viewModel,
                service: mockJobAnalyticsService,
                onAcquire: { mirroredToken = $0 },
                onRelease: { token in
                    releaseCount += 1
                    if mirroredToken == token {
                        mirroredToken = nil
                    }
                },
                beforeLifetimeWait: {
                    await beforeLifetimeWaitGate.park()
                },
                cleanupEnqueuer: { cleanupQueue.enqueue($0) }
            )
        }
        await beforeLifetimeWaitGate.awaitEntered()
        XCTAssertEqual(viewModel.historyItems.map(\.id), ["loaded"])
        XCTAssertNotNil(mirroredToken)

        task.cancel()
        XCTAssertEqual(cleanupQueue.pendingCount, 1)
        await runNextCleanup(cleanupQueue)
        XCTAssertFalse(viewModel.isViewActive)
        XCTAssertNil(mirroredToken)
        XCTAssertEqual(releaseCount, 1)

        let lifetimeRelease = await beforeLifetimeWaitGate.release()
        XCTAssertEqual(lifetimeRelease, .released)
        await task.value
        XCTAssertEqual(cleanupQueue.pendingCount, 1)
        await runNextCleanup(cleanupQueue)
        XCTAssertEqual(releaseCount, 1)
    }

    func testDelayedMountACleanupCannotClearMountBMirrorOrOwnedTasks() async {
        mockJobAnalyticsService.historyPageToReturn = historyPage(
            ["mounted"],
            totalCount: 1,
            currentPage: 1
        )
        let cleanupQueueA = ManualCancellationCleanupEnqueuer()
        let cleanupQueueB = ManualCancellationCleanupEnqueuer()
        let lifetimeA = MainActorEvent()
        let lifetimeB = MainActorEvent()
        let buttonGateA = TaskStartGate()
        let buttonGateB = TaskStartGate()
        let mountState = JobHistoryMountState()
        var buttonTaskA: Task<Void, Never>?
        var buttonTaskB: Task<Void, Never>?
        var releaseCountByToken: [UUID: Int] = [:]

        let taskA = Task { @MainActor in
            await JobAnalyticsMountLifecycle.runHistory(
                viewModel: viewModel,
                service: mockJobAnalyticsService,
                onAcquire: { token in
                    mountState.acquire(activationToken: token)
                    let task = Task { await buttonGateA.park() }
                    buttonTaskA = task
                    mountState.track(task: task, activationToken: token)
                },
                onRelease: { token in
                    mountState.release(activationToken: token)
                    releaseCountByToken[token, default: 0] += 1
                },
                onLifetimeArmed: { lifetimeA.signal() },
                cleanupEnqueuer: { cleanupQueueA.enqueue($0) }
            )
        }
        await lifetimeA.wait()
        await buttonGateA.awaitEntered()
        XCTAssertNotNil(mountState.activationToken)
        let tokenA = mountState.activationToken ?? UUID()

        taskA.cancel()
        await taskA.value
        XCTAssertEqual(cleanupQueueA.pendingCount, 2)

        let taskB = Task { @MainActor in
            await JobAnalyticsMountLifecycle.runHistory(
                viewModel: viewModel,
                service: mockJobAnalyticsService,
                onAcquire: { token in
                    mountState.acquire(activationToken: token)
                    let task = Task { await buttonGateB.park() }
                    buttonTaskB = task
                    mountState.track(task: task, activationToken: token)
                },
                onRelease: { token in
                    mountState.release(activationToken: token)
                    releaseCountByToken[token, default: 0] += 1
                },
                onLifetimeArmed: { lifetimeB.signal() },
                cleanupEnqueuer: { cleanupQueueB.enqueue($0) }
            )
        }
        await lifetimeB.wait()
        await buttonGateB.awaitEntered()
        XCTAssertNotNil(mountState.activationToken)
        let tokenB = mountState.activationToken ?? UUID()
        XCTAssertNotEqual(tokenA, tokenB)

        await runNextCleanup(cleanupQueueA)
        XCTAssertEqual(mountState.activationToken, tokenB)
        XCTAssertTrue(viewModel.isViewActive)
        XCTAssertEqual(mountState.trackedTaskCount(activationToken: tokenA), 0)
        XCTAssertEqual(mountState.trackedTaskCount(activationToken: tokenB), 1)
        XCTAssertTrue(buttonTaskA?.isCancelled == true)
        XCTAssertFalse(buttonTaskB?.isCancelled == true)
        XCTAssertEqual(releaseCountByToken[tokenA], 1)
        XCTAssertNil(releaseCountByToken[tokenB])
        let buttonReleaseA = await buttonGateA.release()
        XCTAssertEqual(buttonReleaseA, .released)
        await buttonTaskA?.value
        await runNextCleanup(cleanupQueueA)
        XCTAssertEqual(releaseCountByToken[tokenA], 1)

        taskB.cancel()
        await taskB.value
        XCTAssertEqual(cleanupQueueB.pendingCount, 2)
        await runNextCleanup(cleanupQueueB)
        XCTAssertTrue(buttonTaskB?.isCancelled == true)
        let buttonReleaseB = await buttonGateB.release()
        XCTAssertEqual(buttonReleaseB, .released)
        await buttonTaskB?.value
        await runNextCleanup(cleanupQueueB)
        XCTAssertNil(mountState.activationToken)
        XCTAssertFalse(viewModel.isViewActive)
        XCTAssertEqual(mountState.trackedTaskCount(activationToken: tokenB), 0)
        XCTAssertEqual(releaseCountByToken[tokenB], 1)
    }

    func testTimelineMountRemainsActiveAfterLoadUntilTaskCancellation() async {
        mockJobAnalyticsService.timelineToReturn = [timelineEvent("mounted")]
        let cleanupQueue = ManualCancellationCleanupEnqueuer()
        let lifetimeArmed = MainActorEvent()
        var mirroredToken: UUID?
        var releaseCount = 0
        let task = Task { @MainActor in
            await JobAnalyticsMountLifecycle.runTimeline(
                viewModel: viewModel,
                service: mockJobAnalyticsService,
                onAcquire: { mirroredToken = $0 },
                onRelease: { token in
                    releaseCount += 1
                    if mirroredToken == token {
                        mirroredToken = nil
                    }
                },
                onLifetimeArmed: { lifetimeArmed.signal() },
                cleanupEnqueuer: { cleanupQueue.enqueue($0) }
            )
        }
        await lifetimeArmed.wait()

        XCTAssertEqual(viewModel.timeline.map(\.jobId), ["mounted"])
        XCTAssertTrue(viewModel.isViewActive)
        XCTAssertNotNil(mirroredToken)
        XCTAssertEqual(releaseCount, 0)

        task.cancel()
        await task.value
        XCTAssertEqual(cleanupQueue.pendingCount, 2)
        await runNextCleanup(cleanupQueue)
        await runNextCleanup(cleanupQueue)

        XCTAssertFalse(viewModel.isViewActive)
        XCTAssertNil(mirroredToken)
        XCTAssertEqual(releaseCount, 1)
    }

    // MARK: - History Authority (Issue #853)

    func testCancelledAppearanceHistoryTaskCannotIssueRequestAfterDisappear() async {
        let activationToken = viewModel.activate()
        let startGate = TaskStartGate()
        let delayedTask = Task { @MainActor in
            await startGate.park()
            await viewModel.loadHistory(activationToken: activationToken)
        }
        await startGate.awaitEntered()

        let cleanupTarget = viewModel.cancellationCleanupTickForTesting + 1
        delayedTask.cancel()
        viewModel.deactivate(activationToken: activationToken)

        let release = await startGate.release()
        XCTAssertEqual(release, .released)
        await delayedTask.value
        await viewModel.waitForCancellationCleanupForTesting(atLeast: cleanupTarget)

        XCTAssertNil(mockJobAnalyticsService.getHistoryCalledWith)
        XCTAssertFalse(viewModel.isViewActive)
        XCTAssertFalse(viewModel.isLoading)
        XCTAssertFalse(viewModel.isLoadingMore)
    }

    func testAlreadyCancelledReloadDoesNotAcquireAuthorityOrMutateState() async {
        let activationToken = viewModel.activate()
        await commitHistory(historyPage(["committed"], currentPage: 1))
        mockJobAnalyticsService.getHistoryCalledWith = nil
        viewModel.error = "preserved-error"

        let startGate = TaskStartGate()
        let cleanupTarget = viewModel.cancellationCleanupTickForTesting + 1
        let task = Task { @MainActor in
            await startGate.park()
            await viewModel.loadHistory(activationToken: activationToken)
        }
        await startGate.awaitEntered()
        task.cancel()

        let release = await startGate.release()
        XCTAssertEqual(release, .released)
        await task.value
        await viewModel.waitForCancellationCleanupForTesting(atLeast: cleanupTarget)

        XCTAssertNil(mockJobAnalyticsService.getHistoryCalledWith)
        XCTAssertEqual(viewModel.historyItems.map(\.id), ["committed"])
        XCTAssertEqual(viewModel.currentOffset, 0)
        XCTAssertEqual(viewModel.error, "preserved-error")
        XCTAssertFalse(viewModel.isLoading)
        XCTAssertFalse(viewModel.isLoadingMore)
    }

    func testAlreadyCancelledLoadMoreDoesNotAcquireAuthorityOrIssueRequest() async {
        let activationToken = viewModel.activate()
        await commitHistory(historyPage(["committed"], currentPage: 1))
        mockJobAnalyticsService.getHistoryCalledWith = nil

        let startGate = TaskStartGate()
        let cleanupTarget = viewModel.cancellationCleanupTickForTesting + 1
        let task = Task { @MainActor in
            await startGate.park()
            await viewModel.loadMore(activationToken: activationToken)
        }
        await startGate.awaitEntered()
        task.cancel()

        let release = await startGate.release()
        XCTAssertEqual(release, .released)
        await task.value
        await viewModel.waitForCancellationCleanupForTesting(atLeast: cleanupTarget)

        XCTAssertNil(mockJobAnalyticsService.getHistoryCalledWith)
        XCTAssertEqual(viewModel.historyItems.map(\.id), ["committed"])
        XCTAssertEqual(viewModel.currentOffset, 0)
        XCTAssertFalse(viewModel.isLoading)
        XCTAssertFalse(viewModel.isLoadingMore)
    }

    func testCancelledEnteredReloadRelinquishesLoadingBeforeServiceRelease() async {
        let activationToken = viewModel.activate()
        await commitHistory(historyPage(["committed"], currentPage: 1))

        let service = ScriptedJobAnalyticsService()
        let registration = await service.register(
            .success(historyPage(["late"], totalCount: 1, currentPage: 1))
        )
        viewModel.configure(jobAnalyticsService: service)
        let cleanupTarget = viewModel.cancellationCleanupTickForTesting + 1
        let task = Task { @MainActor in
            await viewModel.loadHistory(activationToken: activationToken)
            await service.operationFinished(registration: registration)
        }
        guard await requireEntry(service, registration: registration) != nil else { return }
        XCTAssertTrue(viewModel.isLoading)

        task.cancel()
        await viewModel.waitForCancellationCleanupForTesting(atLeast: cleanupTarget)

        XCTAssertFalse(viewModel.isLoading)
        XCTAssertEqual(viewModel.historyItems.map(\.id), ["committed"])
        XCTAssertEqual(viewModel.currentOffset, 0)

        let release = await service.release(registration: registration)
        XCTAssertEqual(release, .released)
        await task.value

        XCTAssertEqual(viewModel.historyItems.map(\.id), ["committed"])
        XCTAssertFalse(viewModel.isLoading)
    }

    func testCancelledEnteredLoadMoreRelinquishesLoadingBeforeServiceRelease() async {
        let activationToken = viewModel.activate()
        await commitHistory(historyPage(["committed"], currentPage: 1))

        let service = ScriptedJobAnalyticsService()
        let registration = await service.register(
            .success(historyPage(["late"], currentPage: 2))
        )
        viewModel.configure(jobAnalyticsService: service)
        let cleanupTarget = viewModel.cancellationCleanupTickForTesting + 1
        let task = Task { @MainActor in
            await viewModel.loadMore(activationToken: activationToken)
            await service.operationFinished(registration: registration)
        }
        guard await requireEntry(service, registration: registration) != nil else { return }
        XCTAssertTrue(viewModel.isLoadingMore)

        task.cancel()
        await viewModel.waitForCancellationCleanupForTesting(atLeast: cleanupTarget)

        XCTAssertFalse(viewModel.isLoadingMore)
        XCTAssertEqual(viewModel.historyItems.map(\.id), ["committed"])
        XCTAssertEqual(viewModel.currentOffset, 0)

        let release = await service.release(registration: registration)
        XCTAssertEqual(release, .released)
        await task.value

        XCTAssertEqual(viewModel.historyItems.map(\.id), ["committed"])
        XCTAssertEqual(viewModel.currentOffset, 0)
        XCTAssertFalse(viewModel.isLoadingMore)
    }

    func testDelayedCancelledReloadCleanupCannotClearNewerReload() async {
        let cleanupQueue = ManualCancellationCleanupEnqueuer()
        viewModel = JobHistoryViewModel(
            cancellationCleanupEnqueuer: { cleanupQueue.enqueue($0) }
        )
        let activationToken = viewModel.activate()

        let service = ScriptedJobAnalyticsService()
        let staleRegistration = await service.register(.failure)
        let currentRegistration = await service.register(
            .success(historyPage(["current"], totalCount: 1, currentPage: 1))
        )
        viewModel.configure(jobAnalyticsService: service)

        let staleTask = Task { @MainActor in
            await viewModel.loadHistory(activationToken: activationToken)
            await service.operationFinished(registration: staleRegistration)
        }
        guard await requireEntry(service, registration: staleRegistration) != nil else {
            return
        }
        staleTask.cancel()
        XCTAssertEqual(cleanupQueue.pendingCount, 1)

        let currentTask = Task { @MainActor in
            await viewModel.loadHistory(activationToken: activationToken)
            await service.operationFinished(registration: currentRegistration)
        }
        guard await requireEntry(service, registration: currentRegistration) != nil else {
            _ = await service.release(registration: staleRegistration)
            await staleTask.value
            return
        }
        XCTAssertTrue(viewModel.isLoading)

        let ranCleanup = await cleanupQueue.runNext()
        XCTAssertTrue(ranCleanup)
        XCTAssertTrue(viewModel.isLoading)

        let currentRelease = await service.release(registration: currentRegistration)
        XCTAssertEqual(currentRelease, .released)
        await currentTask.value
        XCTAssertEqual(viewModel.historyItems.map(\.id), ["current"])
        XCTAssertNil(viewModel.error)
        XCTAssertFalse(viewModel.isLoading)

        let staleRelease = await service.release(registration: staleRegistration)
        XCTAssertEqual(staleRelease, .released)
        await staleTask.value

        XCTAssertEqual(viewModel.historyItems.map(\.id), ["current"])
        XCTAssertNil(viewModel.error)
        XCTAssertFalse(viewModel.isLoading)
    }

    func testRapidReappearanceRejectsDelayedPriorActivationWithoutRevokingCurrentLoad() async {
        let firstActivation = viewModel.activate()
        let startGate = TaskStartGate()
        let delayedFirstTask = Task { @MainActor in
            await startGate.park()
            await viewModel.loadHistory(activationToken: firstActivation)
        }
        await startGate.awaitEntered()

        let cleanupTarget = viewModel.cancellationCleanupTickForTesting + 1
        delayedFirstTask.cancel()
        viewModel.deactivate(activationToken: firstActivation)
        let secondActivation = viewModel.activate()

        let service = ScriptedJobAnalyticsService()
        let currentRegistration = await service.register(
            .success(historyPage(["current"], totalCount: 1, currentPage: 1))
        )
        viewModel.configure(jobAnalyticsService: service)
        let currentTask = Task { @MainActor in
            await viewModel.loadHistory(activationToken: secondActivation)
            await service.operationFinished(registration: currentRegistration)
        }
        guard await requireEntry(
            service,
            registration: currentRegistration
        ) != nil else {
            _ = await startGate.release()
            await delayedFirstTask.value
            return
        }
        XCTAssertTrue(viewModel.isLoading)

        let delayedRelease = await startGate.release()
        XCTAssertEqual(delayedRelease, .released)
        await delayedFirstTask.value
        await viewModel.waitForCancellationCleanupForTesting(atLeast: cleanupTarget)

        let calls = await service.recordedCalls
        XCTAssertEqual(calls.count, 1)
        XCTAssertEqual(calls.first?.offset, 0)
        XCTAssertTrue(viewModel.isViewActive)
        XCTAssertTrue(viewModel.isLoading)

        viewModel.deactivate(activationToken: firstActivation)
        XCTAssertTrue(viewModel.isViewActive)
        XCTAssertTrue(viewModel.isLoading)

        let currentRelease = await service.release(registration: currentRegistration)
        XCTAssertEqual(currentRelease, .released)
        await currentTask.value

        XCTAssertEqual(viewModel.historyItems.map(\.id), ["current"])
        XCTAssertFalse(viewModel.isLoading)
        XCTAssertFalse(viewModel.isLoadingMore)
    }

    func testCancelledAppearanceTimelineTaskCannotIssueRequestAfterDisappear() async {
        let activationToken = viewModel.activate()
        let startGate = TaskStartGate()
        let delayedTask = Task { @MainActor in
            await startGate.park()
            await viewModel.loadTimeline(
                dateFrom: nil,
                dateTo: nil,
                activationToken: activationToken
            )
        }
        await startGate.awaitEntered()

        let cleanupTarget = viewModel.cancellationCleanupTickForTesting + 1
        delayedTask.cancel()
        viewModel.deactivate(activationToken: activationToken)

        let release = await startGate.release()
        XCTAssertEqual(release, .released)
        await delayedTask.value
        await viewModel.waitForCancellationCleanupForTesting(atLeast: cleanupTarget)

        XCTAssertNil(mockJobAnalyticsService.getTimelineCalledWith)
        XCTAssertTrue(viewModel.timeline.isEmpty)
        XCTAssertFalse(viewModel.isViewActive)
    }

    func testReloadSupersedesLoadMoreWhenPaginationCompletesFirst() async {
        await commitHistory(historyPage(["old-1"], currentPage: 1))

        let service = ScriptedJobAnalyticsService()
        let paginationRegistration = await service.register(
            .success(historyPage(["old-2"], currentPage: 2))
        )
        let reloadRegistration = await service.register(
            .success(historyPage(["replacement"], totalCount: 1, currentPage: 1))
        )
        viewModel.configure(jobAnalyticsService: service)

        let paginationTask = Task { @MainActor in
            await viewModel.loadMore()
            await service.operationFinished(registration: paginationRegistration)
        }
        guard let paginationCall = await requireEntry(
            service,
            registration: paginationRegistration
        ) else {
            return
        }
        XCTAssertEqual(paginationCall.offset, 30)

        let reloadTask = Task { @MainActor in
            await viewModel.loadHistory()
            await service.operationFinished(registration: reloadRegistration)
        }
        guard let reloadCall = await requireEntry(
            service,
            registration: reloadRegistration
        ) else {
            _ = await service.release(registration: paginationRegistration)
            await paginationTask.value
            return
        }
        XCTAssertEqual(reloadCall.offset, 0)
        XCTAssertTrue(viewModel.isLoading)
        XCTAssertFalse(viewModel.isLoadingMore)

        let paginationRelease = await service.release(registration: paginationRegistration)
        XCTAssertEqual(paginationRelease, .released)
        await paginationTask.value

        XCTAssertEqual(viewModel.historyItems.map(\.id), ["old-1"])
        XCTAssertEqual(viewModel.currentOffset, 0)
        XCTAssertTrue(viewModel.isLoading)
        XCTAssertFalse(viewModel.isLoadingMore)

        let reloadRelease = await service.release(registration: reloadRegistration)
        XCTAssertEqual(reloadRelease, .released)
        await reloadTask.value

        XCTAssertEqual(viewModel.historyItems.map(\.id), ["replacement"])
        XCTAssertEqual(viewModel.historyPage?.currentPage, 1)
        XCTAssertEqual(viewModel.currentOffset, 0)
        XCTAssertFalse(viewModel.isLoading)
        XCTAssertFalse(viewModel.isLoadingMore)
    }

    func testReloadSupersedesLoadMoreWhenReloadCompletesFirst() async {
        await commitHistory(historyPage(["old-1"], currentPage: 1))

        let service = ScriptedJobAnalyticsService()
        let paginationRegistration = await service.register(
            .success(historyPage(["old-2"], currentPage: 2))
        )
        let reloadRegistration = await service.register(
            .success(historyPage(["replacement"], totalCount: 1, currentPage: 1))
        )
        viewModel.configure(jobAnalyticsService: service)

        let paginationTask = Task { @MainActor in
            await viewModel.loadMore()
            await service.operationFinished(registration: paginationRegistration)
        }
        guard await requireEntry(
            service,
            registration: paginationRegistration
        ) != nil else {
            return
        }

        let reloadTask = Task { @MainActor in
            await viewModel.loadHistory()
            await service.operationFinished(registration: reloadRegistration)
        }
        guard await requireEntry(
            service,
            registration: reloadRegistration
        ) != nil else {
            _ = await service.release(registration: paginationRegistration)
            await paginationTask.value
            return
        }

        let reloadRelease = await service.release(registration: reloadRegistration)
        XCTAssertEqual(reloadRelease, .released)
        await reloadTask.value

        XCTAssertEqual(viewModel.historyItems.map(\.id), ["replacement"])
        XCTAssertEqual(viewModel.currentOffset, 0)
        XCTAssertFalse(viewModel.isLoading)
        XCTAssertFalse(viewModel.isLoadingMore)

        let paginationRelease = await service.release(registration: paginationRegistration)
        XCTAssertEqual(paginationRelease, .released)
        await paginationTask.value

        XCTAssertEqual(viewModel.historyItems.map(\.id), ["replacement"])
        XCTAssertEqual(viewModel.historyPage?.currentPage, 1)
        XCTAssertEqual(viewModel.currentOffset, 0)
        XCTAssertFalse(viewModel.isLoading)
        XCTAssertFalse(viewModel.isLoadingMore)
    }

    func testFilterReplacementInvalidatesPaginationAndForwardsCapturedFilters() async {
        let oldFrom = Date(timeIntervalSince1970: 1_700_000_000)
        let oldTo = Date(timeIntervalSince1970: 1_700_086_400)
        let newFrom = Date(timeIntervalSince1970: 1_710_000_000)
        let newTo = Date(timeIntervalSince1970: 1_710_086_400)
        viewModel.dateFrom = oldFrom
        viewModel.dateTo = oldTo
        await commitHistory(historyPage(["old-1"], currentPage: 1))

        let service = ScriptedJobAnalyticsService()
        let paginationRegistration = await service.register(
            .success(historyPage(["old-2"], currentPage: 2))
        )
        let reloadRegistration = await service.register(
            .success(historyPage(["filtered"], totalCount: 1, currentPage: 1))
        )
        viewModel.configure(jobAnalyticsService: service)

        let paginationTask = Task { @MainActor in
            await viewModel.loadMore()
            await service.operationFinished(registration: paginationRegistration)
        }
        guard let paginationCall = await requireEntry(
            service,
            registration: paginationRegistration
        ) else {
            return
        }
        XCTAssertEqual(paginationCall.dateStart, oldFrom)
        XCTAssertEqual(paginationCall.dateEnd, oldTo)

        viewModel.dateFrom = newFrom
        viewModel.dateTo = newTo
        XCTAssertFalse(viewModel.isLoadingMore)

        let reloadTask = Task { @MainActor in
            await viewModel.loadHistory()
            await service.operationFinished(registration: reloadRegistration)
        }
        guard let reloadCall = await requireEntry(
            service,
            registration: reloadRegistration
        ) else {
            _ = await service.release(registration: paginationRegistration)
            await paginationTask.value
            return
        }
        XCTAssertEqual(reloadCall.dateStart, newFrom)
        XCTAssertEqual(reloadCall.dateEnd, newTo)

        let paginationRelease = await service.release(registration: paginationRegistration)
        XCTAssertEqual(paginationRelease, .released)
        await paginationTask.value
        XCTAssertEqual(viewModel.historyItems.map(\.id), ["old-1"])
        XCTAssertTrue(viewModel.isLoading)

        let reloadRelease = await service.release(registration: reloadRegistration)
        XCTAssertEqual(reloadRelease, .released)
        await reloadTask.value

        XCTAssertEqual(viewModel.historyItems.map(\.id), ["filtered"])
        XCTAssertEqual(viewModel.currentOffset, 0)
        XCTAssertFalse(viewModel.isLoading)
        XCTAssertFalse(viewModel.isLoadingMore)
    }

    func testCancelledRefreshReturningSuccessPreservesPaginatedSnapshot() async {
        let committedStats = QueueHistoryStats(
            totalCompleted: 2,
            totalFailed: 0,
            averageDurationMinutes: 20
        )
        await commitHistory(historyPage(["old-1"], currentPage: 1))
        mockJobAnalyticsService.historyPageToReturn = historyPage(
            ["old-2"],
            currentPage: 2,
            stats: committedStats
        )
        await viewModel.loadMore()
        XCTAssertEqual(viewModel.currentOffset, 30)

        let service = ScriptedJobAnalyticsService()
        let refreshRegistration = await service.register(
            .success(historyPage(["cancelled-replacement"], totalCount: 1, currentPage: 1))
        )
        viewModel.configure(jobAnalyticsService: service)

        let refreshTask = Task { @MainActor in
            await viewModel.loadHistory()
            await service.operationFinished(registration: refreshRegistration)
        }
        guard await requireEntry(
            service,
            registration: refreshRegistration
        ) != nil else {
            return
        }

        refreshTask.cancel()
        let refreshRelease = await service.release(registration: refreshRegistration)
        XCTAssertEqual(refreshRelease, .released)
        await refreshTask.value

        XCTAssertEqual(viewModel.historyItems.map(\.id), ["old-1", "old-2"])
        XCTAssertEqual(viewModel.historyPage?.currentPage, 2)
        XCTAssertEqual(viewModel.historyPage?.stats?.totalCompleted, 2)
        XCTAssertEqual(viewModel.currentOffset, 30)
        XCTAssertFalse(viewModel.isLoading)
        XCTAssertFalse(viewModel.isLoadingMore)
    }

    func testDeactivateReactivateRetriesPageAndStaleDeactivateCannotRevokeNewWork() async {
        let firstActivation = viewModel.activate()
        await commitHistory(historyPage(["old-1"], currentPage: 1))

        let service = ScriptedJobAnalyticsService()
        let staleRegistration = await service.register(
            .success(historyPage(["stale-page"], currentPage: 2))
        )
        let retryRegistration = await service.register(
            .success(historyPage(["retry-page"], currentPage: 2))
        )
        viewModel.configure(jobAnalyticsService: service)

        let staleTask = Task { @MainActor in
            await viewModel.loadMore(activationToken: firstActivation)
            await service.operationFinished(registration: staleRegistration)
        }
        guard let staleCall = await requireEntry(
            service,
            registration: staleRegistration
        ) else {
            return
        }
        XCTAssertEqual(staleCall.offset, 30)
        XCTAssertTrue(viewModel.isLoadingMore)

        viewModel.deactivate(activationToken: firstActivation)
        XCTAssertFalse(viewModel.isViewActive)
        XCTAssertFalse(viewModel.isLoadingMore)

        let secondActivation = viewModel.activate()
        let retryTask = Task { @MainActor in
            await viewModel.loadMore(activationToken: secondActivation)
            await service.operationFinished(registration: retryRegistration)
        }
        guard let retryCall = await requireEntry(
            service,
            registration: retryRegistration
        ) else {
            _ = await service.release(registration: staleRegistration)
            await staleTask.value
            return
        }
        XCTAssertEqual(retryCall.offset, 30)
        XCTAssertTrue(viewModel.isLoadingMore)

        viewModel.deactivate(activationToken: firstActivation)
        XCTAssertTrue(viewModel.isViewActive)
        XCTAssertTrue(viewModel.isLoadingMore)

        let retryRelease = await service.release(registration: retryRegistration)
        XCTAssertEqual(retryRelease, .released)
        await retryTask.value

        XCTAssertEqual(viewModel.historyItems.map(\.id), ["old-1", "retry-page"])
        XCTAssertEqual(viewModel.currentOffset, 30)
        XCTAssertFalse(viewModel.isLoadingMore)

        let staleRelease = await service.release(registration: staleRegistration)
        XCTAssertEqual(staleRelease, .released)
        await staleTask.value

        XCTAssertEqual(viewModel.historyItems.map(\.id), ["old-1", "retry-page"])
        XCTAssertEqual(viewModel.historyPage?.currentPage, 2)
        XCTAssertEqual(viewModel.currentOffset, 30)
        XCTAssertFalse(viewModel.isLoadingMore)

        viewModel.deactivate(activationToken: secondActivation)
    }

    func testPaginationFailureThenFilteredReloadFailureRetriesCommittedCursorAndFilters() async {
        let activationToken = viewModel.activate()
        let committedFrom = Date(timeIntervalSince1970: 1_700_000_000)
        let committedTo = Date(timeIntervalSince1970: 1_700_086_400)
        let requestedFrom = Date(timeIntervalSince1970: 1_710_000_000)
        let requestedTo = Date(timeIntervalSince1970: 1_710_086_400)
        viewModel.dateFrom = committedFrom
        viewModel.dateTo = committedTo
        await commitHistory(historyPage(["page-1"], currentPage: 1))

        let retryStats = QueueHistoryStats(
            totalCompleted: 2,
            totalFailed: 0,
            averageDurationMinutes: 30
        )
        let service = ScriptedJobAnalyticsService()
        let failedPageRegistration = await service.register(.failure)
        let failedReloadRegistration = await service.register(.failure)
        let retryRegistration = await service.register(
            .success(historyPage(["page-2"], currentPage: 2, stats: retryStats))
        )
        viewModel.configure(jobAnalyticsService: service)

        let failedPageTask = Task { @MainActor in
            await viewModel.loadMore(activationToken: activationToken)
            await service.operationFinished(registration: failedPageRegistration)
        }
        guard let failedPageCall = await requireEntry(
            service,
            registration: failedPageRegistration
        ) else {
            return
        }
        XCTAssertEqual(failedPageCall.offset, 30)
        XCTAssertEqual(failedPageCall.dateStart, committedFrom)
        XCTAssertEqual(failedPageCall.dateEnd, committedTo)
        let failedPageRelease = await service.release(
            registration: failedPageRegistration
        )
        XCTAssertEqual(failedPageRelease, .released)
        await failedPageTask.value
        XCTAssertEqual(viewModel.currentOffset, 0)
        XCTAssertEqual(viewModel.historyItems.map(\.id), ["page-1"])

        viewModel.dateFrom = requestedFrom
        viewModel.dateTo = requestedTo
        let failedReloadTask = Task { @MainActor in
            await viewModel.loadHistory(activationToken: activationToken)
            await service.operationFinished(registration: failedReloadRegistration)
        }
        guard let failedReloadCall = await requireEntry(
            service,
            registration: failedReloadRegistration
        ) else {
            return
        }
        XCTAssertEqual(failedReloadCall.offset, 0)
        XCTAssertEqual(failedReloadCall.dateStart, requestedFrom)
        XCTAssertEqual(failedReloadCall.dateEnd, requestedTo)
        let failedReloadRelease = await service.release(
            registration: failedReloadRegistration
        )
        XCTAssertEqual(failedReloadRelease, .released)
        await failedReloadTask.value
        let reloadError = viewModel.error
        XCTAssertNotNil(reloadError)
        XCTAssertEqual(viewModel.currentOffset, 0)
        XCTAssertEqual(viewModel.historyItems.map(\.id), ["page-1"])

        let retryTask = Task { @MainActor in
            await viewModel.loadMore(activationToken: activationToken)
            await service.operationFinished(registration: retryRegistration)
        }
        guard let retryCall = await requireEntry(
            service,
            registration: retryRegistration
        ) else {
            return
        }
        XCTAssertEqual(retryCall.limit, 30)
        XCTAssertEqual(retryCall.offset, 30)
        XCTAssertNil(retryCall.sortBy)
        XCTAssertNil(retryCall.statuses)
        XCTAssertEqual(retryCall.dateStart, committedFrom)
        XCTAssertEqual(retryCall.dateEnd, committedTo)
        let retryRelease = await service.release(registration: retryRegistration)
        XCTAssertEqual(retryRelease, .released)
        await retryTask.value

        XCTAssertEqual(viewModel.historyItems.map(\.id), ["page-1", "page-2"])
        XCTAssertEqual(viewModel.historyPage?.currentPage, 2)
        XCTAssertEqual(viewModel.historyPage?.stats?.totalCompleted, 2)
        XCTAssertEqual(viewModel.currentOffset, 30)
        XCTAssertEqual(viewModel.error, reloadError)
        XCTAssertFalse(viewModel.isLoading)
        XCTAssertFalse(viewModel.isLoadingMore)
    }

    func testCurrentDeactivationRevokesCompletionQueuedForHistoryApply() async {
        let activationToken = viewModel.activate()
        await commitHistory(historyPage(["committed"], currentPage: 1))

        let applyGate = TaskStartGate()
        viewModel.beforeHistoryApplyForTesting = {
            await applyGate.park()
        }
        let service = ScriptedJobAnalyticsService()
        let registration = await service.register(
            .success(historyPage(["late"], totalCount: 1, currentPage: 1))
        )
        viewModel.configure(jobAnalyticsService: service)
        let task = Task { @MainActor in
            await viewModel.loadHistory(activationToken: activationToken)
            await service.operationFinished(registration: registration)
        }
        guard await requireEntry(service, registration: registration) != nil else { return }

        let serviceRelease = await service.release(registration: registration)
        XCTAssertEqual(serviceRelease, .released)
        await applyGate.awaitEntered()

        viewModel.deactivate(activationToken: activationToken)
        XCTAssertFalse(viewModel.isLoading)
        let applyRelease = await applyGate.release()
        XCTAssertEqual(applyRelease, .released)
        await task.value

        XCTAssertEqual(viewModel.historyItems.map(\.id), ["committed"])
        XCTAssertEqual(viewModel.currentOffset, 0)
        XCTAssertFalse(viewModel.isViewActive)
        XCTAssertFalse(viewModel.isLoading)
    }

    func testEmptySuccessfulPagePreservesAppendBoundarySemantics() async {
        await commitHistory(historyPage(["page-1"], currentPage: 1))
        let stats = QueueHistoryStats(
            totalCompleted: 1,
            totalFailed: 0,
            averageDurationMinutes: 10
        )
        mockJobAnalyticsService.historyPageToReturn = historyPage(
            [],
            totalCount: 100,
            currentPage: 2,
            stats: stats
        )

        await viewModel.loadMore()

        XCTAssertEqual(viewModel.historyItems.map(\.id), ["page-1"])
        XCTAssertEqual(viewModel.historyPage?.currentPage, 2)
        XCTAssertEqual(viewModel.historyPage?.stats?.totalCompleted, 1)
        XCTAssertEqual(viewModel.currentOffset, 30)
        XCTAssertTrue(viewModel.canLoadMore)
        XCTAssertFalse(viewModel.isLoadingMore)
    }

    func testShortSuccessfulFinalPageClosesPaginationBoundary() async {
        await commitHistory(historyPage(["page-1"], totalCount: 3, currentPage: 1))
        let stats = QueueHistoryStats(
            totalCompleted: 3,
            totalFailed: 0,
            averageDurationMinutes: 20
        )
        mockJobAnalyticsService.historyPageToReturn = historyPage(
            ["page-2", "page-3"],
            totalCount: 3,
            currentPage: 2,
            stats: stats
        )

        await viewModel.loadMore()

        XCTAssertEqual(viewModel.historyItems.map(\.id), ["page-1", "page-2", "page-3"])
        XCTAssertEqual(viewModel.historyPage?.totalCount, 3)
        XCTAssertEqual(viewModel.historyPage?.currentPage, 2)
        XCTAssertEqual(viewModel.historyPage?.stats?.totalCompleted, 3)
        XCTAssertEqual(viewModel.currentOffset, 30)
        XCTAssertFalse(viewModel.canLoadMore)
        XCTAssertFalse(viewModel.isLoadingMore)
    }

    func testFailedRefreshAfterPaginationPreservesCommittedPageCursorAndStats() async {
        let committedStats = QueueHistoryStats(
            totalCompleted: 2,
            totalFailed: 1,
            averageDurationMinutes: 45
        )
        let service = ScriptedJobAnalyticsService()
        let initialRegistration = await service.register(
            .success(historyPage(["page-1"], currentPage: 1))
        )
        let paginationRegistration = await service.register(
            .success(historyPage(["page-2"], currentPage: 2, stats: committedStats))
        )
        let refreshRegistration = await service.register(.failure)
        viewModel.configure(jobAnalyticsService: service)

        let initialTask = Task { @MainActor in
            await viewModel.loadHistory()
            await service.operationFinished(registration: initialRegistration)
        }
        guard await requireEntry(
            service,
            registration: initialRegistration
        ) != nil else {
            return
        }
        let initialRelease = await service.release(registration: initialRegistration)
        XCTAssertEqual(initialRelease, .released)
        await initialTask.value

        let paginationTask = Task { @MainActor in
            await viewModel.loadMore()
            await service.operationFinished(registration: paginationRegistration)
        }
        guard await requireEntry(
            service,
            registration: paginationRegistration
        ) != nil else {
            return
        }
        let paginationRelease = await service.release(registration: paginationRegistration)
        XCTAssertEqual(paginationRelease, .released)
        await paginationTask.value

        XCTAssertEqual(viewModel.historyItems.map(\.id), ["page-1", "page-2"])
        XCTAssertEqual(viewModel.currentOffset, 30)

        let refreshTask = Task { @MainActor in
            await viewModel.loadHistory()
            await service.operationFinished(registration: refreshRegistration)
        }
        guard await requireEntry(
            service,
            registration: refreshRegistration
        ) != nil else {
            return
        }
        let refreshRelease = await service.release(registration: refreshRegistration)
        XCTAssertEqual(refreshRelease, .released)
        await refreshTask.value

        XCTAssertEqual(viewModel.historyItems.map(\.id), ["page-1", "page-2"])
        XCTAssertEqual(viewModel.historyPage?.totalCount, 100)
        XCTAssertEqual(viewModel.historyPage?.currentPage, 2)
        XCTAssertEqual(viewModel.historyPage?.pageSize, 30)
        XCTAssertEqual(viewModel.historyPage?.stats?.totalCompleted, 2)
        XCTAssertEqual(viewModel.historyPage?.stats?.totalFailed, 1)
        XCTAssertEqual(viewModel.historyPage?.stats?.averageDurationMinutes, 45)
        XCTAssertEqual(viewModel.currentOffset, 30)
        XCTAssertNotNil(viewModel.error)
        XCTAssertFalse(viewModel.isLoading)
        XCTAssertFalse(viewModel.isLoadingMore)
    }

    func testNewerReloadSuccessRemainsExactWhenOlderReloadSucceedsLater() async {
        let activationToken = viewModel.activate()
        let filterAFrom = Date(timeIntervalSince1970: 1_700_000_000)
        let filterATo = Date(timeIntervalSince1970: 1_700_086_400)
        let filterBFrom = Date(timeIntervalSince1970: 1_710_000_000)
        let filterBTo = Date(timeIntervalSince1970: 1_710_086_400)
        let statsA = QueueHistoryStats(
            totalCompleted: 10,
            totalFailed: 2,
            averageDurationMinutes: 55
        )
        let statsB = QueueHistoryStats(
            totalCompleted: 20,
            totalFailed: 1,
            averageDurationMinutes: 25
        )
        let service = ScriptedJobAnalyticsService()
        let staleRegistration = await service.register(
            .success(historyPage(["stale"], currentPage: 1, stats: statsA))
        )
        let currentRegistration = await service.register(
            .success(historyPage(["current"], currentPage: 1, stats: statsB))
        )
        viewModel.configure(jobAnalyticsService: service)

        viewModel.dateFrom = filterAFrom
        viewModel.dateTo = filterATo
        let staleTask = Task { @MainActor in
            await viewModel.loadHistory(activationToken: activationToken)
            await service.operationFinished(registration: staleRegistration)
        }
        guard let staleCall = await requireEntry(
            service,
            registration: staleRegistration
        ) else {
            return
        }
        XCTAssertEqual(staleCall.dateStart, filterAFrom)
        XCTAssertEqual(staleCall.dateEnd, filterATo)

        viewModel.dateFrom = filterBFrom
        viewModel.dateTo = filterBTo
        let currentTask = Task { @MainActor in
            await viewModel.loadHistory(activationToken: activationToken)
            await service.operationFinished(registration: currentRegistration)
        }
        guard let currentCall = await requireEntry(
            service,
            registration: currentRegistration
        ) else {
            _ = await service.release(registration: staleRegistration)
            await staleTask.value
            return
        }
        XCTAssertEqual(currentCall.dateStart, filterBFrom)
        XCTAssertEqual(currentCall.dateEnd, filterBTo)

        let currentRelease = await service.release(registration: currentRegistration)
        XCTAssertEqual(currentRelease, .released)
        await currentTask.value

        XCTAssertEqual(viewModel.historyItems.map(\.id), ["current"])
        XCTAssertEqual(viewModel.historyPage?.currentPage, 1)
        XCTAssertEqual(viewModel.historyPage?.stats?.totalCompleted, 20)
        XCTAssertEqual(viewModel.historyPage?.stats?.totalFailed, 1)
        XCTAssertEqual(viewModel.currentOffset, 0)
        XCTAssertEqual(viewModel.committedDateFromForTesting, filterBFrom)
        XCTAssertEqual(viewModel.committedDateToForTesting, filterBTo)
        XCTAssertNil(viewModel.error)
        XCTAssertFalse(viewModel.isLoading)
        XCTAssertFalse(viewModel.isLoadingMore)

        let staleRelease = await service.release(registration: staleRegistration)
        XCTAssertEqual(staleRelease, .released)
        await staleTask.value

        XCTAssertEqual(viewModel.historyItems.map(\.id), ["current"])
        XCTAssertEqual(viewModel.historyPage?.currentPage, 1)
        XCTAssertEqual(viewModel.historyPage?.stats?.totalCompleted, 20)
        XCTAssertEqual(viewModel.historyPage?.stats?.totalFailed, 1)
        XCTAssertEqual(viewModel.currentOffset, 0)
        XCTAssertEqual(viewModel.committedDateFromForTesting, filterBFrom)
        XCTAssertEqual(viewModel.committedDateToForTesting, filterBTo)
        XCTAssertNil(viewModel.error)
        XCTAssertFalse(viewModel.isLoading)
        XCTAssertFalse(viewModel.isLoadingMore)
    }

    func testSupersededReloadCannotClearNewerReloadLoadingFlag() async {
        await commitHistory(historyPage(["old"], currentPage: 1))

        let service = ScriptedJobAnalyticsService()
        let staleRegistration = await service.register(
            .success(historyPage(["stale"], totalCount: 1, currentPage: 1))
        )
        let currentRegistration = await service.register(
            .success(historyPage(["current"], totalCount: 1, currentPage: 1))
        )
        viewModel.configure(jobAnalyticsService: service)

        let staleTask = Task { @MainActor in
            await viewModel.loadHistory()
            await service.operationFinished(registration: staleRegistration)
        }
        guard await requireEntry(
            service,
            registration: staleRegistration
        ) != nil else {
            return
        }

        let currentTask = Task { @MainActor in
            await viewModel.loadHistory()
            await service.operationFinished(registration: currentRegistration)
        }
        guard await requireEntry(
            service,
            registration: currentRegistration
        ) != nil else {
            _ = await service.release(registration: staleRegistration)
            await staleTask.value
            return
        }

        let staleRelease = await service.release(registration: staleRegistration)
        XCTAssertEqual(staleRelease, .released)
        await staleTask.value

        XCTAssertEqual(viewModel.historyItems.map(\.id), ["old"])
        XCTAssertTrue(viewModel.isLoading)
        XCTAssertFalse(viewModel.isLoadingMore)

        let currentRelease = await service.release(registration: currentRegistration)
        XCTAssertEqual(currentRelease, .released)
        await currentTask.value

        XCTAssertEqual(viewModel.historyItems.map(\.id), ["current"])
        XCTAssertFalse(viewModel.isLoading)
        XCTAssertFalse(viewModel.isLoadingMore)
    }

    // MARK: - Secondary Operation Authority

    func testTimelineNewerRequestSurvivesOlderFailureCompletingFirst() async {
        let activationToken = viewModel.activate()
        let service = ScriptedJobAnalyticsService()
        let staleRegistration = await service.registerTimeline(.failure)
        let currentRegistration = await service.registerTimeline(
            .success([timelineEvent("current")])
        )
        viewModel.configure(jobAnalyticsService: service)

        let staleTask = Task { @MainActor in
            await viewModel.loadTimeline(
                dateFrom: nil,
                dateTo: nil,
                activationToken: activationToken
            )
            await service.timelineOperationFinished(registration: staleRegistration)
        }
        guard await requireTimelineEntry(
            service,
            registration: staleRegistration
        ) != nil else {
            return
        }

        let currentTask = Task { @MainActor in
            await viewModel.loadTimeline(
                dateFrom: nil,
                dateTo: nil,
                activationToken: activationToken
            )
            await service.timelineOperationFinished(registration: currentRegistration)
        }
        guard let currentCall = await requireTimelineEntry(
            service,
            registration: currentRegistration
        ) else {
            _ = await service.releaseTimeline(registration: staleRegistration)
            await staleTask.value
            return
        }
        XCTAssertNil(currentCall.value.dateFrom)
        XCTAssertNil(currentCall.value.dateTo)
        XCTAssertNil(currentCall.value.printerId)
        XCTAssertNil(currentCall.value.filterStatus)
        XCTAssertEqual(currentCall.value.limit, 100)
        XCTAssertTrue(viewModel.isTimelineLoading)

        let staleRelease = await service.releaseTimeline(
            registration: staleRegistration
        )
        XCTAssertEqual(staleRelease, .released)
        await staleTask.value

        XCTAssertTrue(viewModel.timeline.isEmpty)
        XCTAssertNil(viewModel.timelineError)
        XCTAssertTrue(viewModel.isTimelineLoading)

        let currentRelease = await service.releaseTimeline(
            registration: currentRegistration
        )
        XCTAssertEqual(currentRelease, .released)
        await currentTask.value

        XCTAssertEqual(viewModel.timeline.map(\.jobId), ["current"])
        XCTAssertNil(viewModel.timelineError)
        XCTAssertFalse(viewModel.isTimelineLoading)
    }

    func testTimelineOlderFailureCannotOverwriteNewerSuccessCompletingFirst() async {
        let activationToken = viewModel.activate()
        let service = ScriptedJobAnalyticsService()
        let staleRegistration = await service.registerTimeline(.failure)
        let currentRegistration = await service.registerTimeline(
            .success([timelineEvent("current")])
        )
        viewModel.configure(jobAnalyticsService: service)

        let staleTask = Task { @MainActor in
            await viewModel.loadTimeline(
                dateFrom: nil,
                dateTo: nil,
                activationToken: activationToken
            )
            await service.timelineOperationFinished(registration: staleRegistration)
        }
        guard await requireTimelineEntry(
            service,
            registration: staleRegistration
        ) != nil else {
            return
        }
        let currentTask = Task { @MainActor in
            await viewModel.loadTimeline(
                dateFrom: nil,
                dateTo: nil,
                activationToken: activationToken
            )
            await service.timelineOperationFinished(registration: currentRegistration)
        }
        guard await requireTimelineEntry(
            service,
            registration: currentRegistration
        ) != nil else {
            _ = await service.releaseTimeline(registration: staleRegistration)
            await staleTask.value
            return
        }

        let currentRelease = await service.releaseTimeline(
            registration: currentRegistration
        )
        XCTAssertEqual(currentRelease, .released)
        await currentTask.value
        XCTAssertEqual(viewModel.timeline.map(\.jobId), ["current"])
        XCTAssertNil(viewModel.timelineError)
        XCTAssertFalse(viewModel.isTimelineLoading)

        let staleRelease = await service.releaseTimeline(
            registration: staleRegistration
        )
        XCTAssertEqual(staleRelease, .released)
        await staleTask.value

        XCTAssertEqual(viewModel.timeline.map(\.jobId), ["current"])
        XCTAssertNil(viewModel.timelineError)
        XCTAssertFalse(viewModel.isTimelineLoading)
    }

    func testTimelineCurrentFailureSurfacesAndClearsLoading() async {
        let activationToken = viewModel.activate()
        let service = ScriptedJobAnalyticsService()
        let registration = await service.registerTimeline(.failure)
        viewModel.configure(jobAnalyticsService: service)
        let task = Task { @MainActor in
            await viewModel.loadTimeline(
                dateFrom: nil,
                dateTo: nil,
                activationToken: activationToken
            )
            await service.timelineOperationFinished(registration: registration)
        }
        guard await requireTimelineEntry(service, registration: registration) != nil else {
            return
        }

        let release = await service.releaseTimeline(registration: registration)
        XCTAssertEqual(release, .released)
        await task.value

        XCTAssertNotNil(viewModel.timelineError)
        XCTAssertFalse(viewModel.isTimelineLoading)
        XCTAssertTrue(viewModel.timeline.isEmpty)
    }

    func testDelayedCancelledTimelineCleanupCannotClearNewerTimelineLoad() async {
        let cleanupQueue = ManualCancellationCleanupEnqueuer()
        viewModel = JobHistoryViewModel(
            cancellationCleanupEnqueuer: { cleanupQueue.enqueue($0) }
        )
        let activationToken = viewModel.activate()
        let service = ScriptedJobAnalyticsService()
        let staleRegistration = await service.registerTimeline(
            .success([timelineEvent("stale")])
        )
        let currentRegistration = await service.registerTimeline(
            .success([timelineEvent("current")])
        )
        viewModel.configure(jobAnalyticsService: service)

        let staleTask = Task { @MainActor in
            await viewModel.loadTimeline(
                dateFrom: nil,
                dateTo: nil,
                activationToken: activationToken
            )
            await service.timelineOperationFinished(registration: staleRegistration)
        }
        guard await requireTimelineEntry(
            service,
            registration: staleRegistration
        ) != nil else {
            return
        }
        staleTask.cancel()
        XCTAssertEqual(cleanupQueue.pendingCount, 1)

        let currentTask = Task { @MainActor in
            await viewModel.loadTimeline(
                dateFrom: nil,
                dateTo: nil,
                activationToken: activationToken
            )
            await service.timelineOperationFinished(registration: currentRegistration)
        }
        guard await requireTimelineEntry(
            service,
            registration: currentRegistration
        ) != nil else {
            _ = await service.releaseTimeline(registration: staleRegistration)
            await staleTask.value
            return
        }

        let ranCleanup = await cleanupQueue.runNext()
        XCTAssertTrue(ranCleanup)
        XCTAssertTrue(viewModel.isTimelineLoading)

        let currentRelease = await service.releaseTimeline(
            registration: currentRegistration
        )
        XCTAssertEqual(currentRelease, .released)
        await currentTask.value
        let staleRelease = await service.releaseTimeline(
            registration: staleRegistration
        )
        XCTAssertEqual(staleRelease, .released)
        await staleTask.value

        XCTAssertEqual(viewModel.timeline.map(\.jobId), ["current"])
        XCTAssertNil(viewModel.timelineError)
        XCTAssertFalse(viewModel.isTimelineLoading)
    }

    func testJobStateNewerRequestSurvivesOlderFailureCompletingFirst() async {
        let activationToken = viewModel.activate()
        let service = ScriptedJobAnalyticsService()
        let staleRegistration = await service.registerJobState(.failure)
        let currentRegistration = await service.registerJobState(
            .success(jobStateHistory("current"))
        )
        viewModel.configure(jobAnalyticsService: service)

        let staleTask = Task { @MainActor in
            await viewModel.loadJobStateHistory(
                jobId: "stale",
                activationToken: activationToken
            )
            await service.jobStateOperationFinished(registration: staleRegistration)
        }
        guard await requireJobStateEntry(
            service,
            registration: staleRegistration
        ) != nil else {
            return
        }
        let currentTask = Task { @MainActor in
            await viewModel.loadJobStateHistory(
                jobId: "current",
                activationToken: activationToken
            )
            await service.jobStateOperationFinished(registration: currentRegistration)
        }
        guard let currentCall = await requireJobStateEntry(
            service,
            registration: currentRegistration
        ) else {
            _ = await service.releaseJobState(registration: staleRegistration)
            await staleTask.value
            return
        }
        XCTAssertEqual(currentCall.value.jobId, "current")
        XCTAssertTrue(viewModel.isJobStateLoading)

        let staleRelease = await service.releaseJobState(
            registration: staleRegistration
        )
        XCTAssertEqual(staleRelease, .released)
        await staleTask.value
        XCTAssertNil(viewModel.selectedJobHistory)
        XCTAssertNil(viewModel.jobStateError)
        XCTAssertTrue(viewModel.isJobStateLoading)

        let currentRelease = await service.releaseJobState(
            registration: currentRegistration
        )
        XCTAssertEqual(currentRelease, .released)
        await currentTask.value

        XCTAssertEqual(viewModel.selectedJobHistory?.jobId, "current")
        XCTAssertNil(viewModel.jobStateError)
        XCTAssertFalse(viewModel.isJobStateLoading)
    }

    func testJobStateOlderFailureCannotOverwriteNewerSuccessCompletingFirst() async {
        let activationToken = viewModel.activate()
        let service = ScriptedJobAnalyticsService()
        let staleRegistration = await service.registerJobState(.failure)
        let currentRegistration = await service.registerJobState(
            .success(jobStateHistory("current"))
        )
        viewModel.configure(jobAnalyticsService: service)

        let staleTask = Task { @MainActor in
            await viewModel.loadJobStateHistory(
                jobId: "stale",
                activationToken: activationToken
            )
            await service.jobStateOperationFinished(registration: staleRegistration)
        }
        guard await requireJobStateEntry(
            service,
            registration: staleRegistration
        ) != nil else {
            return
        }
        let currentTask = Task { @MainActor in
            await viewModel.loadJobStateHistory(
                jobId: "current",
                activationToken: activationToken
            )
            await service.jobStateOperationFinished(registration: currentRegistration)
        }
        guard await requireJobStateEntry(
            service,
            registration: currentRegistration
        ) != nil else {
            _ = await service.releaseJobState(registration: staleRegistration)
            await staleTask.value
            return
        }

        let currentRelease = await service.releaseJobState(
            registration: currentRegistration
        )
        XCTAssertEqual(currentRelease, .released)
        await currentTask.value
        XCTAssertEqual(viewModel.selectedJobHistory?.jobId, "current")
        XCTAssertNil(viewModel.jobStateError)
        XCTAssertFalse(viewModel.isJobStateLoading)

        let staleRelease = await service.releaseJobState(
            registration: staleRegistration
        )
        XCTAssertEqual(staleRelease, .released)
        await staleTask.value

        XCTAssertEqual(viewModel.selectedJobHistory?.jobId, "current")
        XCTAssertNil(viewModel.jobStateError)
        XCTAssertFalse(viewModel.isJobStateLoading)
    }

    func testJobStateCurrentFailureSurfacesAndClearsLoading() async {
        let activationToken = viewModel.activate()
        let service = ScriptedJobAnalyticsService()
        let registration = await service.registerJobState(.failure)
        viewModel.configure(jobAnalyticsService: service)
        let task = Task { @MainActor in
            await viewModel.loadJobStateHistory(
                jobId: "current",
                activationToken: activationToken
            )
            await service.jobStateOperationFinished(registration: registration)
        }
        guard await requireJobStateEntry(service, registration: registration) != nil else {
            return
        }

        let release = await service.releaseJobState(registration: registration)
        XCTAssertEqual(release, .released)
        await task.value

        XCTAssertNotNil(viewModel.jobStateError)
        XCTAssertNotNil(viewModel.error)
        XCTAssertFalse(viewModel.isJobStateLoading)
        XCTAssertNil(viewModel.selectedJobHistory)
    }

    func testDelayedCancelledJobStateCleanupCannotClearNewerJobStateLoad() async {
        let cleanupQueue = ManualCancellationCleanupEnqueuer()
        viewModel = JobHistoryViewModel(
            cancellationCleanupEnqueuer: { cleanupQueue.enqueue($0) }
        )
        let activationToken = viewModel.activate()
        let service = ScriptedJobAnalyticsService()
        let staleRegistration = await service.registerJobState(
            .success(jobStateHistory("stale"))
        )
        let currentRegistration = await service.registerJobState(
            .success(jobStateHistory("current"))
        )
        viewModel.configure(jobAnalyticsService: service)

        let staleTask = Task { @MainActor in
            await viewModel.loadJobStateHistory(
                jobId: "stale",
                activationToken: activationToken
            )
            await service.jobStateOperationFinished(registration: staleRegistration)
        }
        guard await requireJobStateEntry(
            service,
            registration: staleRegistration
        ) != nil else {
            return
        }
        staleTask.cancel()
        XCTAssertEqual(cleanupQueue.pendingCount, 1)

        let currentTask = Task { @MainActor in
            await viewModel.loadJobStateHistory(
                jobId: "current",
                activationToken: activationToken
            )
            await service.jobStateOperationFinished(registration: currentRegistration)
        }
        guard await requireJobStateEntry(
            service,
            registration: currentRegistration
        ) != nil else {
            _ = await service.releaseJobState(registration: staleRegistration)
            await staleTask.value
            return
        }

        let ranCleanup = await cleanupQueue.runNext()
        XCTAssertTrue(ranCleanup)
        XCTAssertTrue(viewModel.isJobStateLoading)

        let currentRelease = await service.releaseJobState(
            registration: currentRegistration
        )
        XCTAssertEqual(currentRelease, .released)
        await currentTask.value
        let staleRelease = await service.releaseJobState(
            registration: staleRegistration
        )
        XCTAssertEqual(staleRelease, .released)
        await staleTask.value

        XCTAssertEqual(viewModel.selectedJobHistory?.jobId, "current")
        XCTAssertNil(viewModel.jobStateError)
        XCTAssertFalse(viewModel.isJobStateLoading)
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
        XCTAssertNil(viewModel.timelineError)
        XCTAssertFalse(viewModel.isTimelineLoading)
        
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
        
        // Timeline failures use their own operation-owned error channel and
        // preserve the primary history error plus the last committed timeline.
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
        XCTAssertNotNil(viewModel.timelineError)
        XCTAssertFalse(viewModel.isTimelineLoading)
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
        XCTAssertNil(viewModel.jobStateError)
        XCTAssertFalse(viewModel.isJobStateLoading)
        XCTAssertEqual(mockJobAnalyticsService.getJobStateHistoryCalledWith, "1")
    }
    
    func testLoadJobStateHistoryHandlesError() async {
        mockJobAnalyticsService.errorToThrow = TestError.generic
        
        await viewModel.loadJobStateHistory(jobId: "1")
        
        XCTAssertNil(viewModel.selectedJobHistory)
        XCTAssertNotNil(viewModel.error)
        XCTAssertNotNil(viewModel.jobStateError)
        XCTAssertFalse(viewModel.isJobStateLoading)
    }
    
    // MARK: - Computed Properties
    
    func testHistoryItemsReturnsEntriesFromPage() async {
        let entry = QueueHistoryEntry(
            id: "1",
            jobName: "test_print.gcode",
            printerName: "Prusa MK3",
            status: "completed",
            completedAt: Date(),
            durationSeconds: 3600
        )
        await commitHistory(QueueHistoryPage(
            entries: [entry],
            totalCount: 1,
            currentPage: 1,
            pageSize: 30,
            stats: nil
        ))
        
        XCTAssertEqual(viewModel.historyItems.count, 1)
        XCTAssertEqual(viewModel.historyItems.first?.id, "1")
    }
    
    func testHistoryItemsReturnsEmptyWhenPageIsNil() {
        XCTAssertTrue(viewModel.historyItems.isEmpty)
    }
    
    func testCanLoadMoreReturnsTrueWhenMoreDataExists() async {
        await commitHistory(QueueHistoryPage(
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
        ))
        
        XCTAssertTrue(viewModel.canLoadMore)
    }
    
    func testCanLoadMoreReturnsFalseWhenNoMoreData() async {
        // canLoadMore returns `entries.count < totalCount`. When the loaded page
        // has drained the total, both sides equal zero (or n == n) and the flag
        // is false.
        await commitHistory(QueueHistoryPage(
            entries: [],
            totalCount: 0,
            currentPage: 1,
            pageSize: 30,
            stats: nil
        ))
        
        XCTAssertFalse(viewModel.canLoadMore)
    }
    
    func testCanLoadMoreReturnsFalseWhenPageIsNil() {
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
