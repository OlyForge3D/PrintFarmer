import XCTest
@testable import PrintFarmer

@MainActor
final class JobListViewModelTests: XCTestCase {

    private var mockJobService: MockJobService!
    private var mockJobAnalyticsService: MockJobAnalyticsService!
    private var viewModel: JobListViewModel!

    override func setUp() async throws {
        try await super.setUp()
        mockJobService = MockJobService()
        mockJobAnalyticsService = MockJobAnalyticsService()
        viewModel = JobListViewModel()
        viewModel.configure(jobService: mockJobService)
        viewModel.configure(jobAnalyticsService: mockJobAnalyticsService)
    }

    override func tearDown() async throws {
        viewModel = nil
        mockJobService = nil
        mockJobAnalyticsService = nil
        try await super.tearDown()
    }

    // MARK: - Initial State

    func testInitialState() {
        XCTAssertTrue(viewModel.jobs.isEmpty)
        XCTAssertTrue(viewModel.recentFailures.isEmpty)
        XCTAssertFalse(viewModel.isLoading)
        XCTAssertNil(viewModel.errorMessage)
        XCTAssertNil(viewModel.recentFailuresError)
        XCTAssertFalse(viewModel.showRecentJobs)
        XCTAssertFalse(viewModel.hasAnyJobs)
    }

    // MARK: - Load Jobs

    func testLoadJobsCallsListAllJobs() async throws {
        let job = try TestData.decodeQueuedPrintJobResponse(from: TestJSON.queuedPrintJobResponsePrinting)
        mockJobService.queuedJobResponsesToReturn = [job]

        await viewModel.loadJobs()

        XCTAssertTrue(mockJobService.listAllJobsCalled)
        XCTAssertEqual(viewModel.jobs.count, 1)
        XCTAssertFalse(viewModel.isLoading)
        XCTAssertNil(viewModel.errorMessage)
    }

    func testCapped200JobPageDoesNotExposeExactSectionTotals() async throws {
        let queued = try TestData.decodeQueuedPrintJobResponse(from: TestJSON.queuedPrintJobResponseQueued)
        mockJobService.queuedJobResponsesToReturn = Array(
            repeating: queued,
            count: QueuedPrintJobPage.pageSize
        )
        mockJobService.queuedJobListMayHaveMore = true

        await viewModel.loadJobs()

        XCTAssertEqual(viewModel.jobs.count, QueuedPrintJobPage.pageSize)
        XCTAssertEqual(
            viewModel.queueSectionCountText(for: viewModel.queuedJobs.count),
            "\(QueuedPrintJobPage.pageSize)+"
        )
        XCTAssertEqual(viewModel.queueSectionCountText(for: viewModel.activeJobs.count), "?")
    }

    func testLoadJobsRequestsOnlyRecentFailedHistoryAndExcludesOtherStatuses() async throws {
        let failed = QueueHistoryEntry(
            id: UUID().uuidString,
            jobName: "failed.gcode",
            printerName: "MK4",
            status: "Failed",
            completedAt: Date(),
            durationSeconds: 300,
            completionPercentage: 42,
            failureReason: "Thermal runaway"
        )
        let completed = QueueHistoryEntry(
            id: UUID().uuidString,
            jobName: "completed.gcode",
            printerName: "MK4",
            status: "Completed",
            completedAt: Date(),
            durationSeconds: 300
        )
        mockJobAnalyticsService.historyPageToReturn = QueueHistoryPage(
            entries: [completed, failed],
            totalCount: 2,
            currentPage: 1,
            pageSize: 5,
            stats: nil
        )

        await viewModel.loadJobs()

        XCTAssertEqual(viewModel.recentFailures.map(\.id), [failed.id])
        XCTAssertNil(viewModel.recentFailuresError)
        XCTAssertEqual(mockJobAnalyticsService.getHistoryCalledWith?.limit, 5)
        XCTAssertEqual(mockJobAnalyticsService.getHistoryCalledWith?.offset, 0)
        XCTAssertEqual(mockJobAnalyticsService.getHistoryCalledWith?.sortBy, "newest")
        XCTAssertEqual(mockJobAnalyticsService.getHistoryCalledWith?.statuses, "failed")
    }

    func testRerunFailedJobFetchesCurrentRevisionAndRefreshesQueue() async throws {
        let id = UUID()
        mockJobService.jobToReturn = try TestData.decodePrintJob(from: """
        {
            "id": "\(id)",
            "rowVersion": "failed-job-v3",
            "status": "Failed",
            "priority": "Normal",
            "queuePosition": 0,
            "gcodeFileName": "failed.gcode",
            "copies": 1,
            "completedCopies": 0,
            "remainingCopies": 1
        }
        """)
        viewModel.setQueueWriteAuthorization(true)
        viewModel.setNetworkReachability(true)

        await viewModel.rerunFailedJob(id: id)

        XCTAssertEqual(mockJobService.getJobCalledWith, id)
        XCTAssertEqual(mockJobService.rerunCalledWith?.id, id)
        XCTAssertEqual(mockJobService.rerunCalledWith?.reviewedRowVersion, "failed-job-v3")
        XCTAssertEqual(mockJobService.listAllJobsCallCount, 1)
        XCTAssertNil(viewModel.errorMessage)
    }

    func testRerunFailedJobDoesNotActWithoutQueueWriteAuthorization() async {
        let id = UUID()
        viewModel.setNetworkReachability(true)

        await viewModel.rerunFailedJob(id: id)

        XCTAssertNil(mockJobService.getJobCalledWith)
        XCTAssertNil(mockJobService.rerunCalledWith)
    }

    func testRerunFailedJobIsUnavailableInDemoMode() async {
        let wasDemoActive = DemoMode.shared.isActive
        DemoMode.shared.activate()
        defer {
            wasDemoActive ? DemoMode.shared.activate() : DemoMode.shared.deactivate()
        }
        viewModel.setQueueWriteAuthorization(true)
        viewModel.setNetworkReachability(true)

        await viewModel.rerunFailedJob(id: UUID())

        XCTAssertFalse(viewModel.canRerunFailedJobs)
        XCTAssertNil(mockJobService.getJobCalledWith)
        XCTAssertNil(mockJobService.rerunCalledWith)
    }

    func testRerunFailedJobRejectsJobThatIsNoLongerFailed() async throws {
        let id = UUID()
        mockJobService.jobToReturn = try TestData.decodePrintJob(from: """
        {
            "id": "\(id)",
            "rowVersion": "queued-job-v1",
            "status": "Queued",
            "priority": "Normal",
            "queuePosition": 1,
            "gcodeFileName": "changed.gcode",
            "copies": 1,
            "completedCopies": 0,
            "remainingCopies": 1
        }
        """)
        viewModel.setQueueWriteAuthorization(true)
        viewModel.setNetworkReachability(true)

        await viewModel.rerunFailedJob(id: id)

        XCTAssertNil(mockJobService.rerunCalledWith)
        XCTAssertTrue(viewModel.errorMessage?.contains("no longer failed") == true)
    }

    func testRerunPreconditionFailureRefreshesQueueAndRequiresReview() async throws {
        let id = UUID()
        mockJobService.jobToReturn = try TestData.decodePrintJob(from: """
        {
            "id": "\(id)",
            "rowVersion": "failed-job-v3",
            "status": "Failed",
            "priority": "Normal",
            "queuePosition": 0,
            "gcodeFileName": "failed.gcode",
            "copies": 1,
            "completedCopies": 0,
            "remainingCopies": 1
        }
        """)
        mockJobService.actionErrorToThrow = NetworkError.preconditionFailed(nil)
        viewModel.setQueueWriteAuthorization(true)
        viewModel.setNetworkReachability(true)

        await viewModel.rerunFailedJob(id: id)

        XCTAssertEqual(mockJobService.rerunCalledWith?.reviewedRowVersion, "failed-job-v3")
        XCTAssertEqual(mockJobService.listAllJobsCallCount, 1)
        XCTAssertTrue(viewModel.errorMessage?.contains("Review the refreshed row") == true)
    }

    func testLoadJobsShowsHistoryErrorWithoutDroppingActiveQueue() async throws {
        let printing = try TestData.decodeQueuedPrintJobResponse(
            from: TestJSON.queuedPrintJobResponsePrinting
        )
        mockJobService.queuedJobResponsesToReturn = [printing]
        mockJobAnalyticsService.errorToThrow = NetworkError.serverError(500)

        await viewModel.loadJobs()

        XCTAssertEqual(viewModel.jobs.map(\.id), [printing.id])
        XCTAssertTrue(viewModel.hasFreshQueueSnapshot)
        XCTAssertNotNil(viewModel.recentFailuresError)
        XCTAssertNil(viewModel.errorMessage)
        XCTAssertTrue(viewModel.hasAnyJobs)
    }

    func testLoadJobsSetsLoadingState() async {
        mockJobService.queuedJobResponsesToReturn = []
        await viewModel.loadJobs()
        XCTAssertFalse(viewModel.isLoading)
    }

    func testLoadJobsHandlesError() async {
        mockJobService.errorToThrow = NetworkError.noConnection

        await viewModel.loadJobs()

        XCTAssertFalse(viewModel.isLoading)
        XCTAssertNotNil(viewModel.errorMessage)
        XCTAssertTrue(viewModel.jobs.isEmpty)
    }

    func testLoadJobsHandlesServerError() async {
        mockJobService.errorToThrow = NetworkError.serverError(500)

        await viewModel.loadJobs()

        XCTAssertNotNil(viewModel.errorMessage)
    }

    func testLoadJobsClearsErrorOnSuccess() async throws {
        mockJobService.errorToThrow = NetworkError.noConnection
        await viewModel.loadJobs()
        XCTAssertNotNil(viewModel.errorMessage)

        mockJobService.errorToThrow = nil
        mockJobService.queuedJobResponsesToReturn = []
        await viewModel.loadJobs()

        XCTAssertNil(viewModel.errorMessage)
    }

    // MARK: - Without Configuration

    func testLoadWithoutConfigureDoesNotCrash() async {
        let unconfigured = JobListViewModel()
        await unconfigured.loadJobs()
        XCTAssertFalse(unconfigured.isLoading)
        XCTAssertNil(unconfigured.errorMessage)
    }

    // MARK: - Grouped Jobs: Active

    func testActiveJobsFiltersPrintingAndPaused() async throws {
        let printing = try TestData.decodeQueuedPrintJobResponse(from: TestJSON.queuedPrintJobResponsePrinting)
        let paused = try TestData.decodeQueuedPrintJobResponse(from: TestJSON.queuedPrintJobResponsePaused)
        let queued = try TestData.decodeQueuedPrintJobResponse(from: TestJSON.queuedPrintJobResponseQueued)
        let completed = try TestData.decodeQueuedPrintJobResponse(from: TestJSON.queuedPrintJobResponseCompleted)
        mockJobService.queuedJobResponsesToReturn = [printing, paused, queued, completed]

        await viewModel.loadJobs()

        XCTAssertEqual(viewModel.activeJobs.count, 2)
    }

    func testActiveJobsIncludesAssigned() async throws {
        // Assigned jobs should NOT be in activeJobs (they're in queuedJobs)
        let assigned = try TestData.decodeQueuedPrintJobResponse(from: TestJSON.queuedPrintJobResponseAssigned)
        mockJobService.queuedJobResponsesToReturn = [assigned]

        await viewModel.loadJobs()

        XCTAssertEqual(viewModel.activeJobs.count, 0)
    }

    func testActiveJobsEmptyWhenNoActive() async throws {
        let completed = try TestData.decodeQueuedPrintJobResponse(from: TestJSON.queuedPrintJobResponseCompleted)
        mockJobService.queuedJobResponsesToReturn = [completed]

        await viewModel.loadJobs()

        XCTAssertTrue(viewModel.activeJobs.isEmpty)
    }

    // MARK: - Grouped Jobs: Queued

    func testQueuedJobsFiltersQueuedAndAssigned() async throws {
        let queued = try TestData.decodeQueuedPrintJobResponse(from: TestJSON.queuedPrintJobResponseQueued)
        let assigned = try TestData.decodeQueuedPrintJobResponse(from: TestJSON.queuedPrintJobResponseAssigned)
        let printing = try TestData.decodeQueuedPrintJobResponse(from: TestJSON.queuedPrintJobResponsePrinting)
        mockJobService.queuedJobResponsesToReturn = [queued, assigned, printing]

        await viewModel.loadJobs()

        XCTAssertEqual(viewModel.queuedJobs.count, 2)
    }

    func testQueuedJobsSortedByPosition() async throws {
        let queued = try TestData.decodeQueuedPrintJobResponse(from: TestJSON.queuedPrintJobResponseQueued)
        let assigned = try TestData.decodeQueuedPrintJobResponse(from: TestJSON.queuedPrintJobResponseAssigned)
        mockJobService.queuedJobResponsesToReturn = [assigned, queued]

        await viewModel.loadJobs()

        XCTAssertEqual(viewModel.queuedJobs.map(\.id), [assigned.id, queued.id])
    }

    func testReorderGroupsUsePrinterAndPriorityScopeAndPreserveServerOrder() async throws {
        let printerID = UUID()
        let first = try makeQueueJob(
            scope: printerID,
            priority: .high,
            queuePosition: 9,
            name: "first"
        )
        let second = try makeQueueJob(
            scope: printerID,
            priority: .high,
            queuePosition: 1,
            name: "second"
        )
        let differentPriority = try makeQueueJob(
            scope: printerID,
            priority: .normal,
            queuePosition: 2,
            name: "normal"
        )
        let differentScope = try makeQueueJob(
            scope: UUID(),
            priority: .high,
            queuePosition: 0,
            name: "other printer"
        )
        let assigned = try makeQueueJob(
            scope: printerID,
            status: .assigned,
            name: "assigned"
        )
        let printing = try makeQueueJob(
            scope: printerID,
            status: .printing,
            name: "printing"
        )
        mockJobService.queuedJobResponsesToReturn = [
            first, second, differentPriority, differentScope, assigned, printing
        ]

        await viewModel.loadJobs()

        XCTAssertEqual(viewModel.reorderableQueueGroups.count, 3)
        XCTAssertEqual(
            viewModel.reorderableQueueGroups[0].jobs.map(\.id),
            [first.id, second.id]
        )
        XCTAssertEqual(viewModel.reorderableQueueGroups[1].jobs.map(\.id), [differentPriority.id])
        XCTAssertEqual(viewModel.reorderableQueueGroups[2].jobs.map(\.id), [differentScope.id])
        XCTAssertEqual(viewModel.assignedJobs.map(\.id), [assigned.id])
        XCTAssertEqual(viewModel.activeJobs.map(\.id), [printing.id])
    }

    // MARK: - Queue Reordering

    func testPendingMoveKeepsNativeEditingSessionWithoutAdmittingAnotherMove() async throws {
        let first = try makeQueueJob(name: "first")
        let second = try makeQueueJob(name: "second")
        mockJobService.queuedJobResponsesByLoad = [[first, second], [second, first]]
        mockJobService.queuedJobResponsesToReturn = [second, first]
        let gate = QueueMoveGate()
        mockJobService.beforeMoveQueuedJob = { await gate.suspend() }
        viewModel.setQueueWriteAuthorization(true)
        viewModel.setNetworkReachability(true)
        await viewModel.loadJobs()
        let groupID = try XCTUnwrap(viewModel.reorderableQueueGroups.first?.id)
        XCTAssertTrue(viewModel.keepsQueueEditingActive)

        let move = Task {
            await viewModel.moveQueuedJobs(
                fromOffsets: IndexSet(integer: 0), toOffset: 2, inGroup: groupID
            )
        }
        await gate.waitUntilEntered()
        XCTAssertTrue(viewModel.isReorderingQueue)
        XCTAssertFalse(viewModel.hasFreshQueueSnapshot)
        XCTAssertFalse(viewModel.canReorderQueue)
        XCTAssertTrue(viewModel.keepsQueueEditingActive,
                      "An accepted native drag must not toggle List editing off during persistence.")
        XCTAssertFalse(viewModel.canMoveQueuedJob(
            id: try XCTUnwrap(first.job.jobUUID), direction: .up, inGroup: groupID
        ))
        await viewModel.moveQueuedJobs(
            fromOffsets: IndexSet(integer: 0), toOffset: 2, inGroup: groupID
        )
        XCTAssertEqual(mockJobService.moveQueuedJobCalledWith?.id, first.job.jobUUID,
                       "The second move must not reach the service with a stale queue snapshot.")

        await gate.release()
        await move.value
        XCTAssertEqual(viewModel.jobs.map(\.id), [second.id, first.id])
        XCTAssertFalse(viewModel.isReorderingQueue)
        XCTAssertTrue(viewModel.canReorderQueue)
        XCTAssertTrue(viewModel.keepsQueueEditingActive)

        viewModel.setQueueWriteAuthorization(false)
        XCTAssertFalse(viewModel.canReorderQueue)
        XCTAssertFalse(viewModel.keepsQueueEditingActive)
        viewModel.setQueueWriteAuthorization(true)
        viewModel.setNetworkReachability(false)
        XCTAssertFalse(viewModel.keepsQueueEditingActive)
    }

    func testMoveQueuedJobUsesSameGroupNeighborAndRefreshesAuthoritativeOrder() async throws {
        let printerID = UUID()
        let moved = try makeQueueJob(
            scope: printerID,
            priority: .high,
            queuePosition: 1,
            revision: "AQIDAA==",
            name: "moved"
        )
        let neighbor = try makeQueueJob(
            scope: printerID,
            priority: .high,
            queuePosition: 2,
            revision: "AQIDAg==",
            name: "neighbor"
        )
        let otherScope = try makeQueueJob(
            scope: UUID(),
            priority: .high,
            name: "other"
        )
        let otherPriority = try makeQueueJob(
            scope: printerID,
            priority: .normal,
            name: "other priority"
        )
        let initial = [moved, neighbor, otherScope, otherPriority]
        let serverOrder = [neighbor, moved, otherScope, otherPriority]
        mockJobService.queuedJobResponsesByLoad = [initial, serverOrder]
        mockJobService.queuedJobResponsesToReturn = serverOrder
        viewModel.setQueueWriteAuthorization(true)
        viewModel.setNetworkReachability(true)
        await viewModel.loadJobs()

        let groups = viewModel.reorderableQueueGroups
        let groupID = try XCTUnwrap(groups.first(where: { $0.jobs.contains(where: { $0.id == moved.id }) })?.id)
        let normalGroupID = try XCTUnwrap(
            groups.first(where: { $0.jobs.contains(where: { $0.id == otherPriority.id }) })?.id
        )
        XCTAssertFalse(viewModel.canMoveQueuedJob(
            id: try XCTUnwrap(neighbor.job.jobUUID), direction: .down, inGroup: groupID
        ))
        XCTAssertFalse(viewModel.canMoveQueuedJob(
            id: try XCTUnwrap(otherPriority.job.jobUUID), direction: .up, inGroup: normalGroupID
        ))
        await viewModel.moveQueuedJobs(
            fromOffsets: IndexSet(integer: 0),
            toOffset: 2
        )

        XCTAssertEqual(mockJobService.moveQueuedJobCalledWith?.id, moved.job.jobUUID)
        XCTAssertEqual(
            mockJobService.moveQueuedJobCalledWith?.reviewedRowVersion,
            "AQIDAA=="
        )
        XCTAssertEqual(
            mockJobService.moveQueuedJobCalledWith?.neighbor,
            .after(id: try XCTUnwrap(neighbor.job.jobUUID), rowVersion: "AQIDAg==")
        )
        XCTAssertEqual(viewModel.jobs.map(\.id), serverOrder.map(\.id))
        XCTAssertEqual(
            viewModel.jobs.first(where: { $0.id == moved.id })?.job.priority,
            .high
        )
        XCTAssertEqual(
            viewModel.jobs.first(where: { $0.id == otherPriority.id })?.job.priority,
            .normal
        )
        XCTAssertTrue(viewModel.hasFreshQueueSnapshot)
        XCTAssertFalse(viewModel.isReorderingQueue)
    }

    func testMoveQueuedRowsMapsOffsetsAroundAssignedJobs() async throws {
        let printerID = UUID()
        let assigned = try makeQueueJob(
            scope: printerID,
            status: .assigned,
            priority: .high,
            queuePosition: 0,
            name: "assigned"
        )
        let moved = try makeQueueJob(
            scope: printerID,
            priority: .high,
            queuePosition: 1,
            revision: "AQIDAg==",
            name: "moved"
        )
        let neighbor = try makeQueueJob(
            scope: printerID,
            priority: .high,
            queuePosition: 2,
            revision: "AQIDAw==",
            name: "neighbor"
        )
        let initial = [assigned, moved, neighbor]
        let serverOrder = [assigned, neighbor, moved]
        mockJobService.queuedJobResponsesByLoad = [initial, serverOrder]
        mockJobService.queuedJobResponsesToReturn = serverOrder
        viewModel.setQueueWriteAuthorization(true)
        viewModel.setNetworkReachability(true)
        await viewModel.loadJobs()

        await viewModel.moveQueuedRows(
            fromOffsets: IndexSet(integer: 1),
            toOffset: 3
        )

        XCTAssertEqual(mockJobService.moveQueuedJobCalledWith?.id, moved.job.jobUUID)
        XCTAssertEqual(
            mockJobService.moveQueuedJobCalledWith?.neighbor,
            .after(
                id: try XCTUnwrap(neighbor.job.jobUUID),
                rowVersion: try XCTUnwrap(neighbor.job.rowVersion)
            )
        )
        XCTAssertEqual(viewModel.jobs.map(\.id), serverOrder.map(\.id))
    }

    func testVoiceOverMovesRespectGroupBoundaries() async throws {
        let printerID = UUID()
        let highFirst = try makeQueueJob(scope: printerID, priority: .high, name: "high first")
        let highSecond = try makeQueueJob(scope: printerID, priority: .high, name: "high second")
        let normal = try makeQueueJob(scope: printerID, priority: .normal, name: "normal")
        let anotherScope = try makeQueueJob(scope: UUID(), priority: .high, name: "other scope")
        mockJobService.queuedJobResponsesToReturn = [highFirst, normal, highSecond, anotherScope]
        viewModel.setQueueWriteAuthorization(true)
        viewModel.setNetworkReachability(true)
        await viewModel.loadJobs()

        let groups = viewModel.reorderableQueueGroups
        XCTAssertFalse(viewModel.canMoveQueuedJob(
            id: try XCTUnwrap(highFirst.job.jobUUID),
            direction: .up,
            inGroup: groups[0].id
        ))
        XCTAssertTrue(viewModel.canMoveQueuedJob(
            id: try XCTUnwrap(highFirst.job.jobUUID),
            direction: .down,
            inGroup: groups[0].id
        ))
        XCTAssertFalse(viewModel.canMoveQueuedJob(
            id: try XCTUnwrap(anotherScope.job.jobUUID),
            direction: .down,
            inGroup: groups[2].id
        ))

        await viewModel.moveQueuedJob(
            id: try XCTUnwrap(highFirst.job.jobUUID),
            direction: .down,
            inGroup: groups[2].id
        )

        XCTAssertNil(mockJobService.moveQueuedJobCalledWith)
        XCTAssertEqual(viewModel.jobs.map(\.id), [highFirst.id, normal.id, highSecond.id, anotherScope.id])
    }

    func testQueueMoveRequiresWritePermissionAndReachableNetwork() async throws {
        let first = try makeQueueJob(name: "first")
        let second = try makeQueueJob(name: "second")
        mockJobService.queuedJobResponsesToReturn = [first, second]
        await viewModel.loadJobs()
        let groupID = try XCTUnwrap(viewModel.reorderableQueueGroups.first?.id)

        await viewModel.moveQueuedJobs(
            fromOffsets: IndexSet(integer: 0),
            toOffset: 2,
            inGroup: groupID
        )
        XCTAssertNil(mockJobService.moveQueuedJobCalledWith)
        XCTAssertTrue(viewModel.errorMessage?.contains("Queue.Write") == true)

        viewModel.setQueueWriteAuthorization(true)
        viewModel.setNetworkReachability(false)
        await viewModel.moveQueuedJobs(
            fromOffsets: IndexSet(integer: 0),
            toOffset: 2,
            inGroup: groupID
        )
        XCTAssertNil(mockJobService.moveQueuedJobCalledWith)
        XCTAssertTrue(viewModel.errorMessage?.contains("offline") == true)
    }

    func testQueueMoveConflictsRefreshAndReportVisibleErrors() async throws {
        let cases: [(NetworkError, String)] = [
            (.clientError(400, nil), "rejected"),
            (.conflict(nil), "Queue changed"),
            (.preconditionFailed(nil), "Queue changed"),
            (.preconditionRequired(nil), "current job revision"),
            (.noConnection, "Couldn't reorder"),
            (.forbidden, "Queue.Write access was revoked")
        ]

        for (error, message) in cases {
            let moved = try makeQueueJob(name: "moved")
            let neighbor = try makeQueueJob(name: "neighbor")
            mockJobService = MockJobService()
            viewModel = JobListViewModel()
            viewModel.configure(jobService: mockJobService)
            viewModel.configure(jobAnalyticsService: MockJobAnalyticsService())
            mockJobService.queuedJobResponsesToReturn = [moved, neighbor]
            mockJobService.actionErrorToThrow = error
            viewModel.setQueueWriteAuthorization(true)
            viewModel.setNetworkReachability(true)
            await viewModel.loadJobs()
            let groupID = try XCTUnwrap(viewModel.reorderableQueueGroups.first?.id)

            await viewModel.moveQueuedJobs(
                fromOffsets: IndexSet(integer: 0),
                toOffset: 2,
                inGroup: groupID
            )

            XCTAssertEqual(mockJobService.listAllJobsCallCount, 2)
            XCTAssertEqual(viewModel.jobs.map(\.id), [moved.id, neighbor.id])
            XCTAssertTrue(viewModel.errorMessage?.contains(message) == true)
            XCTAssertTrue(viewModel.hasFreshQueueSnapshot)
            if case .forbidden = error {
                XCTAssertFalse(viewModel.queueWriteAuthorized)
                XCTAssertFalse(viewModel.keepsQueueEditingActive)
            }
            XCTAssertEqual(viewModel.keepsQueueEditingActive, viewModel.canReorderQueue)
        }
    }

    func testSignalRRefreshDuringMoveCannotBeOverwrittenByStaleRollback() async throws {
        let printerID = UUID()
        let first = try makeQueueJob(scope: printerID, name: "first")
        let second = try makeQueueJob(scope: printerID, name: "second")
        let refreshedFirst = try makeQueueJob(
            scope: printerID,
            revision: "AQIDAw==",
            name: "server-first"
        )
        let refreshedSecond = try makeQueueJob(
            scope: printerID,
            revision: "AQIDBA==",
            name: "server-second"
        )
        let initial = [first, second]
        let signalROrder = [refreshedSecond, refreshedFirst]
        mockJobService.queuedJobResponsesByLoad = [initial, signalROrder]
        mockJobService.queuedJobResponsesToReturn = signalROrder
        mockJobService.actionErrorToThrow = NetworkError.preconditionFailed(nil)
        let gate = QueueMoveGate()
        mockJobService.beforeMoveQueuedJob = { await gate.suspend() }
        viewModel.setQueueWriteAuthorization(true)
        viewModel.setNetworkReachability(true)
        await viewModel.loadJobs()
        let signalR = MockSignalRService()
        viewModel.configureSignalR(signalR)
        let groupID = try XCTUnwrap(viewModel.reorderableQueueGroups.first?.id)

        let moveTask = Task {
            await viewModel.moveQueuedJobs(
                fromOffsets: IndexSet(integer: 0),
                toOffset: 2,
                inGroup: groupID
            )
        }
        await gate.waitUntilEntered()
        signalR.simulateJobQueueUpdate(JobQueueUpdate(printerId: printerID, jobs: []))
        await waitForJobIDs(signalROrder.map(\.id))
        await gate.release()
        await moveTask.value

        XCTAssertEqual(viewModel.jobs.map(\.id), signalROrder.map(\.id))
        XCTAssertEqual(viewModel.jobs.map(\.job.rowVersion), ["AQIDBA==", "AQIDAw=="])
        XCTAssertEqual(viewModel.errorMessage, "Queue changed — refreshed.")
    }

    // MARK: - Grouped Jobs: Recent

    func testRecentFailuresIncludesOnlyFailedJobs() async throws {
        let completed = try TestData.decodeQueuedPrintJobResponse(from: TestJSON.queuedPrintJobResponseCompleted)
        let failed = try TestData.decodeQueuedPrintJobResponse(from: TestJSON.queuedPrintJobResponseFailed)
        let printing = try TestData.decodeQueuedPrintJobResponse(from: TestJSON.queuedPrintJobResponsePrinting)
        mockJobService.queuedJobResponsesToReturn = [completed, failed, printing]
        mockJobAnalyticsService.historyPageToReturn = QueueHistoryPage(
            entries: [
                QueueHistoryEntry(
                    id: completed.id,
                    jobName: completed.job.name,
                    printerName: completed.job.printerName,
                    status: "Completed",
                    completedAt: completed.job.actualEndTimeUtc,
                    durationSeconds: nil
                ),
                QueueHistoryEntry(
                    id: failed.id,
                    jobName: failed.job.name,
                    printerName: failed.job.printerName,
                    status: "Failed",
                    completedAt: failed.job.actualEndTimeUtc,
                    durationSeconds: nil,
                    failureReason: failed.job.failureReason
                )
            ],
            totalCount: 2,
            currentPage: 1,
            pageSize: 5,
            stats: nil
        )

        await viewModel.loadJobs()

        XCTAssertEqual(viewModel.recentFailures.map(\.id), [failed.id])
    }

    func testCompletedHistoryKeepsCompletedAndCancelledJobsOutsideRecentFailures() async throws {
        let completed = try TestData.decodeQueuedPrintJobResponse(from: TestJSON.queuedPrintJobResponseCompleted)
        let cancelled = try makeQueueJob(status: .cancelled, name: "cancelled-job")
        let failed = try TestData.decodeQueuedPrintJobResponse(from: TestJSON.queuedPrintJobResponseFailed)
        viewModel.jobs = [completed, cancelled, failed]

        XCTAssertEqual(
            Set(viewModel.completedHistoryJobs.map(\.job.name)),
            Set([completed.job.name, "cancelled-job"])
        )
        XCTAssertTrue(viewModel.recentFailures.isEmpty)
    }

    // MARK: - hasAnyJobs

    func testHasAnyJobsTrueWhenJobsExist() async throws {
        let job = try TestData.decodeQueuedPrintJobResponse(from: TestJSON.queuedPrintJobResponsePrinting)
        mockJobService.queuedJobResponsesToReturn = [job]

        await viewModel.loadJobs()

        XCTAssertTrue(viewModel.hasAnyJobs)
    }

    func testHasAnyJobsFalseWhenEmpty() async {
        mockJobService.queuedJobResponsesToReturn = []
        await viewModel.loadJobs()
        XCTAssertFalse(viewModel.hasAnyJobs)
    }

    // MARK: - Cancel Job

    func testCancelJobCallsService() async throws {
        let job = try TestData.decodeQueuedPrintJobResponse(
            from: TestJSON.queuedPrintJobResponseQueued
        )
        let id = job.job.jobUUID!
        viewModel.jobs = [job]

        await viewModel.cancelJob(id: id)

        XCTAssertEqual(mockJobService.cancelCalledWith, id)
    }

    func testCancelJobReloadsOnSuccess() async throws {
        let job = try TestData.decodeQueuedPrintJobResponse(
            from: TestJSON.queuedPrintJobResponseQueued
        )
        let id = job.job.jobUUID!
        viewModel.jobs = [job]
        mockJobService.queuedJobResponsesToReturn = [job]

        await viewModel.cancelJob(id: id)

        XCTAssertTrue(mockJobService.listAllJobsCalled)
    }

    func testCancelJobSetsErrorOnFailure() async throws {
        let job = try TestData.decodeQueuedPrintJobResponse(
            from: TestJSON.queuedPrintJobResponseQueued
        )
        let id = job.job.jobUUID!
        viewModel.jobs = [job]
        mockJobService.actionErrorToThrow = NetworkError.serverError(500)

        await viewModel.cancelJob(id: id)

        XCTAssertNotNil(viewModel.errorMessage)
    }

    func testCancelJobStaleRevisionReloadsAndRequiresReconfirmation() async throws {
        let job = try TestData.decodeQueuedPrintJobResponse(
            from: TestJSON.queuedPrintJobResponseQueued
        )
        let id = try XCTUnwrap(job.job.jobUUID)
        viewModel.jobs = [job]
        mockJobService.queuedJobResponsesToReturn = [job]
        mockJobService.actionErrorToThrow = NetworkError.preconditionFailed(nil)

        await viewModel.cancelJob(id: id)

        XCTAssertTrue(mockJobService.listAllJobsCalled)
        XCTAssertEqual(
            viewModel.errorMessage,
            "This job changed after you reviewed it. Review the refreshed row and confirm again."
        )
    }

    func testCancelWithoutConfigureDoesNotCrash() async {
        let unconfigured = JobListViewModel()
        await unconfigured.cancelJob(id: UUID())
        XCTAssertNil(unconfigured.errorMessage)
    }

    // MARK: - Abort Job

    func testAbortJobCallsService() async throws {
        let job = try TestData.decodeQueuedPrintJobResponse(
            from: TestJSON.queuedPrintJobResponsePrinting
        )
        let id = job.job.jobUUID!
        viewModel.jobs = [job]

        await viewModel.abortJob(id: id)

        XCTAssertEqual(mockJobService.abortCalledWith, id)
    }

    func testAbortJobReloadsOnSuccess() async throws {
        let job = try TestData.decodeQueuedPrintJobResponse(
            from: TestJSON.queuedPrintJobResponsePrinting
        )
        let id = job.job.jobUUID!
        viewModel.jobs = [job]
        mockJobService.queuedJobResponsesToReturn = [job]

        await viewModel.abortJob(id: id)

        XCTAssertTrue(mockJobService.listAllJobsCalled)
    }

    func testAbortJobSetsErrorOnFailure() async throws {
        let job = try TestData.decodeQueuedPrintJobResponse(
            from: TestJSON.queuedPrintJobResponsePrinting
        )
        let id = job.job.jobUUID!
        viewModel.jobs = [job]
        mockJobService.actionErrorToThrow = NetworkError.serverError(500)

        await viewModel.abortJob(id: id)

        XCTAssertNotNil(viewModel.errorMessage)
    }

    // MARK: - Empty State

    func testAllGroupsEmptyWithNoJobs() async {
        mockJobService.queuedJobResponsesToReturn = []
        await viewModel.loadJobs()

        XCTAssertTrue(viewModel.activeJobs.isEmpty)
        XCTAssertTrue(viewModel.queuedJobs.isEmpty)
        XCTAssertTrue(viewModel.recentFailures.isEmpty)
        XCTAssertTrue(viewModel.completedHistoryJobs.isEmpty)
    }

    private func makeQueueJob(
        id: UUID = UUID(),
        scope: UUID? = nil,
        status: PrintJobStatus = .queued,
        priority: PrintJobPriority = .normal,
        queuePosition: Int = 1,
        revision: String = "AQIDAA==",
        name: String
    ) throws -> QueuedPrintJobResponse {
        let assignedPrinterID = scope.map { "\"\($0.uuidString)\"" } ?? "null"
        let printerName = scope.map { "\"Printer \($0.uuidString.prefix(8))\"" } ?? "null"
        return try TestData.decodeQueuedPrintJobResponse(
            from: """
            {
              "job": {
                "id": "\(id.uuidString)",
                "rowVersion": "\(revision)",
                "name": "\(name)",
                "fileName": "\(name).gcode",
                "assignedPrinterId": \(assignedPrinterID),
                "printerName": \(printerName),
                "status": "\(status.rawValue)",
                "priority": "\(priority.rawValue)",
                "queuePosition": \(queuePosition),
                "createdAtUtc": "2025-07-17T09:00:00Z",
                "copies": 1,
                "completedCopies": 0,
                "remainingCopies": 1
              },
              "gcodeFile": null,
              "assignedPrinter": null,
              "estimatedStartTime": null,
              "estimatedCompletionTime": null
            }
            """
        )
    }

    private func waitForJobIDs(_ expected: [String]) async {
        for _ in 0..<500 {
            if viewModel.jobs.map(\.id) == expected { return }
            await Task.yield()
        }
    }
}

private actor QueueMoveGate {
    private var isEntered = false
    private var isReleased = false
    private var moveContinuation: CheckedContinuation<Void, Never>?
    private var entryWaiters: [CheckedContinuation<Void, Never>] = []

    func suspend() async {
        isEntered = true
        let waiters = entryWaiters
        entryWaiters.removeAll()
        waiters.forEach { $0.resume() }
        guard !isReleased else { return }
        await withCheckedContinuation { continuation in
            moveContinuation = continuation
            if isReleased {
                moveContinuation = nil
                continuation.resume()
            }
        }
    }

    func waitUntilEntered() async {
        guard !isEntered else { return }
        await withCheckedContinuation { continuation in
            entryWaiters.append(continuation)
        }
    }

    func release() {
        isReleased = true
        moveContinuation?.resume()
        moveContinuation = nil
    }
}
