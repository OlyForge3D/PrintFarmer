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

    func testLoadJobsStillLoadsQueueWhenAnalyticsServiceIsMissing() async throws {
        let printing = try TestData.decodeQueuedPrintJobResponse(
            from: TestJSON.queuedPrintJobResponsePrinting
        )
        mockJobService.queuedJobResponsesToReturn = [printing]
        let bare = JobListViewModel()
        bare.configure(jobService: mockJobService)

        await bare.loadJobs()

        XCTAssertEqual(bare.jobs.map(\.id), [printing.id])
        XCTAssertTrue(bare.hasFreshQueueSnapshot)
        XCTAssertEqual(bare.recentFailuresError, "Recent failures are unavailable.")
        XCTAssertNil(bare.errorMessage)
    }

    // MARK: - Active Printer Status Polling

    private func statusDetail(_ id: UUID) -> PrinterStatusDetail {
        PrinterStatusDetail(
            id: id, isOnline: true, state: "printing", progress: nil,
            jobName: nil, thumbnailUrl: nil, cameraStreamUrl: nil, cameraSnapshotUrl: nil,
            x: nil, y: nil, z: nil, hotendTemp: nil, bedTemp: nil,
            hotendTarget: nil, bedTarget: nil, spoolInfo: nil, mmuStatus: nil
        )
    }

    func testPollFetchesStatusesConcurrentlyAndMarksFailuresUnavailable() async {
        let printerService = MockPrinterService()
        let slow = UUID(), fast = UUID(), failing = UUID()
        let slowGate = QueueMoveGate()
        let slowStatus = statusDetail(slow), fastStatus = statusDetail(fast)
        printerService.statusHandler = { id in
            if id == slow {
                await slowGate.suspend()
                return slowStatus
            }
            if id == fast { return fastStatus }
            throw NetworkError.notFound
        }
        viewModel.configure(printerService: printerService)

        let task = Task { @MainActor in
            await viewModel.pollActivePrinterStatuses(
                printerIDs: [slow, fast, failing], interval: .seconds(60)
            )
        }
        await slowGate.waitUntilEntered()
        await waitForStatusResult(fast)
        await waitForStatusResult(failing)
        XCTAssertEqual(Set(printerService.statusRequestedIDs), [slow, fast, failing],
                       "A slow printer must not delay requests for the others.")
        XCTAssertNotNil(viewModel.activePrinterStatuses[fast],
                        "Fast status must be displayed before the slow request finishes.")
        XCTAssertNil(viewModel.activePrinterStatuses[slow])
        XCTAssertTrue(viewModel.unavailableActivePrinterStatuses.contains(failing))
        await slowGate.release()
        await waitForStatusResult(slow)
        task.cancel()
        await task.value

        XCTAssertEqual(Set(viewModel.activePrinterStatuses.keys), [slow, fast])
        XCTAssertEqual(viewModel.unavailableActivePrinterStatuses, [failing])
    }

    func testPollDiscardsResultsWhenGenerationIsStale() async {
        let printerService = MockPrinterService()
        let id = UUID()
        printerService.statusByPrinterID = [id: statusDetail(id)]
        viewModel.configure(printerService: printerService)

        await viewModel.pollActivePrinterStatuses(
            printerIDs: [id], interval: .seconds(60), isCurrent: { false }
        )

        XCTAssertTrue(viewModel.activePrinterStatuses.isEmpty)
        XCTAssertTrue(viewModel.unavailableActivePrinterStatuses.isEmpty)
        XCTAssertTrue(printerService.statusRequestedIDs.isEmpty)
    }

    func testSlowPrinterDoesNotDelaySubsequentFastRefreshes() async {
        let printerService = MockPrinterService()
        let slow = UUID(), fast = UUID()
        let gate = QueueMoveGate()
        let slowStatus = statusDetail(slow), fastStatus = statusDetail(fast)
        printerService.statusHandler = { id in
            if id == slow {
                await gate.suspend()
                return slowStatus
            }
            return fastStatus
        }
        viewModel.configure(printerService: printerService)
        let task = Task {
            await viewModel.pollActivePrinterStatuses(
                printerIDs: [slow, fast], interval: .milliseconds(10)
            )
        }
        await gate.waitUntilEntered()
        let deadline = ContinuousClock.now.advanced(by: .seconds(2))
        while printerService.statusRequestedIDs.filter({ $0 == fast }).count < 2,
              ContinuousClock.now < deadline {
            try? await Task.sleep(for: .milliseconds(1))
        }
        XCTAssertGreaterThanOrEqual(
            printerService.statusRequestedIDs.filter { $0 == fast }.count, 2,
            "Healthy printers must keep refreshing while another printer is blocked."
        )
        XCTAssertNil(viewModel.activePrinterStatuses[slow])
        task.cancel()
        await gate.release()
        await task.value
    }

    func testPollDoesNotStartInBackgroundAndResumesInForeground() async {
        let printerService = MockPrinterService()
        let id = UUID()
        printerService.statusByPrinterID = [id: statusDetail(id)]
        viewModel.configure(printerService: printerService)

        await viewModel.pollActivePrinterStatuses(printerIDs: [id], isForeground: false)
        XCTAssertTrue(printerService.statusRequestedIDs.isEmpty)

        let task = Task {
            await viewModel.pollActivePrinterStatuses(printerIDs: [id])
        }
        await waitForStatusResult(id)
        task.cancel()
        await task.value
        XCTAssertNotNil(viewModel.activePrinterStatuses[id])
        XCTAssertEqual(printerService.statusRequestedIDs, [id])
    }

    func testCancelledPollCannotPublishLateResults() async {
        let printerService = MockPrinterService()
        let id = UUID()
        let gate = QueueMoveGate()
        let status = statusDetail(id)
        printerService.statusHandler = { _ in
            await gate.suspend()
            return status
        }
        viewModel.configure(printerService: printerService)
        let task = Task {
            await viewModel.pollActivePrinterStatuses(printerIDs: [id])
        }
        await gate.waitUntilEntered()
        task.cancel()
        await gate.release()
        await task.value

        XCTAssertTrue(viewModel.activePrinterStatuses.isEmpty)
        XCTAssertTrue(viewModel.unavailableActivePrinterStatuses.isEmpty)
        XCTAssertEqual(printerService.statusRequestedIDs, [id])
    }

    func testChangingPrinterSetDiscardsOldPollResults() async {
        let printerService = MockPrinterService()
        let oldID = UUID(), newID = UUID()
        let gate = QueueMoveGate()
        let oldStatus = statusDetail(oldID), newStatus = statusDetail(newID)
        printerService.statusHandler = { id in
            if id == oldID {
                await gate.suspend()
                return oldStatus
            }
            return newStatus
        }
        viewModel.configure(printerService: printerService)
        let oldTask = Task {
            await viewModel.pollActivePrinterStatuses(printerIDs: [oldID])
        }
        await gate.waitUntilEntered()
        let newTask = Task {
            await viewModel.pollActivePrinterStatuses(printerIDs: [newID, newID])
        }
        await waitForStatusResult(newID)
        await gate.release()
        await oldTask.value
        newTask.cancel()
        await newTask.value

        XCTAssertEqual(Set(viewModel.activePrinterStatuses.keys), [newID])
        XCTAssertEqual(printerService.statusRequestedIDs.filter { $0 == newID }.count, 1)
    }

    private func waitForStatusResult(_ id: UUID) async {
        let deadline = ContinuousClock.now.advanced(by: .seconds(2))
        while ContinuousClock.now < deadline {
            if viewModel.activePrinterStatuses[id] != nil
                || viewModel.unavailableActivePrinterStatuses.contains(id) { return }
            try? await Task.sleep(for: .milliseconds(1))
        }
        XCTFail("Timed out waiting for printer status \(id)")
    }

    func testPollStopsWhenViewDeactivated() async {
        let printerService = MockPrinterService()
        let id = UUID()
        printerService.statusByPrinterID = [id: statusDetail(id)]
        viewModel.configure(printerService: printerService)
        viewModel.deactivate()

        await viewModel.pollActivePrinterStatuses(printerIDs: [id], interval: .milliseconds(10))

        XCTAssertTrue(printerService.statusRequestedIDs.isEmpty)
    }

    func testReactivationRestartsPollingForTheSamePrinterSet() async {
        let printerService = MockPrinterService()
        let id = UUID()
        printerService.statusByPrinterID = [id: statusDetail(id)]
        viewModel.configure(printerService: printerService)
        viewModel.deactivate()
        await viewModel.pollActivePrinterStatuses(printerIDs: [id])
        XCTAssertTrue(printerService.statusRequestedIDs.isEmpty)

        viewModel.activate()
        let task = Task {
            await viewModel.pollActivePrinterStatuses(printerIDs: [id])
        }
        await waitForStatusResult(id)
        task.cancel()
        await task.value

        XCTAssertEqual(printerService.statusRequestedIDs, [id])
        XCTAssertNotNil(viewModel.activePrinterStatuses[id])
    }

    func testBackgroundTransitionInvalidatesAnInFlightPoll() async {
        let printerService = MockPrinterService()
        let id = UUID()
        let gate = QueueMoveGate()
        let status = statusDetail(id)
        printerService.statusHandler = { _ in
            await gate.suspend()
            return status
        }
        viewModel.configure(printerService: printerService)
        let task = Task {
            await viewModel.pollActivePrinterStatuses(printerIDs: [id])
        }
        await gate.waitUntilEntered()
        await viewModel.pollActivePrinterStatuses(printerIDs: [id], isForeground: false)
        await gate.release()
        await task.value

        XCTAssertTrue(viewModel.activePrinterStatuses.isEmpty)
        XCTAssertTrue(viewModel.unavailableActivePrinterStatuses.isEmpty)
        XCTAssertEqual(printerService.statusRequestedIDs, [id])
    }

    func testActiveJobBlankPrinterNameUsesAssignedFallback() throws {
        for nameJSON in [#""""#, #""   ""#, #"" \t\r\n ""#, "null"] {
            let item = try TestData.decodeQueuedPrintJobResponse(
                from: TestJSON.queuedPrintJobResponsePrinting.replacingOccurrences(
                    of: #""printerName": "Prusa MK4""#,
                    with: #""printerName": \#(nameJSON)"#
                )
            )

            XCTAssertEqual(JobListView.activeJobPrinterName(item), "Assigned to printer", nameJSON)
        }
    }

    func testActiveJobNonemptyPrinterNameIsPreserved() throws {
        for name in ["Prusa MK4", "  Prusa MK4  "] {
            let item = try TestData.decodeQueuedPrintJobResponse(
                from: TestJSON.queuedPrintJobResponsePrinting.replacingOccurrences(
                    of: #""printerName": "Prusa MK4""#,
                    with: #""printerName": "\#(name)""#
                )
            )

            XCTAssertEqual(JobListView.activeJobPrinterName(item), name)
        }
    }

    func testActiveJobWithoutPrinterAssignmentOmitsBlankName() throws {
        for nameJSON in [#""""#, #"" \t\n ""#, "null"] {
            let item = try TestData.decodeQueuedPrintJobResponse(
                from: TestJSON.queuedPrintJobResponsePrinting
                    .replacingOccurrences(
                        of: #""assignedPrinterId": "550e8400-e29b-41d4-a716-446655440000""#,
                        with: #""assignedPrinterId": null"#
                    )
                    .replacingOccurrences(
                        of: #""printerName": "Prusa MK4""#,
                        with: #""printerName": \#(nameJSON)"#
                    )
            )

            XCTAssertNil(JobListView.activeJobPrinterName(item), nameJSON)
        }
    }

    func testActiveJobPrinterMetadataWithoutAssignmentIDUsesFallback() throws {
        let item = try TestData.decodeQueuedPrintJobResponse(
            from: TestJSON.queuedPrintJobResponseQueued.replacingOccurrences(
                of: "\"assignedPrinter\": null",
                with: """
                "assignedPrinter": {
                    "id": "\(UUID().uuidString)",
                    "name": "   ",
                    "modelName": "MK4",
                    "status": "Printing",
                    "isOnline": true
                }
                """
            )
        )

        XCTAssertEqual(JobListView.activeJobPrinterName(item), "Assigned to printer")
    }

    func testAssignedPrinterWhitespaceNameDoesNotRenderUnassigned() throws {
        let item = try TestData.decodeQueuedPrintJobResponse(
            from: TestJSON.queuedPrintJobResponseQueued.replacingOccurrences(
                of: "\"assignedPrinter\": null",
                with: """
                "assignedPrinter": {
                    "id": "\(UUID().uuidString)",
                    "name": "   ",
                    "modelName": "MK4",
                    "status": "Idle",
                    "isOnline": true
                }
                """
            )
        )

        XCTAssertTrue(JobListView.queuedJobMetadata(item).contains("Assigned to printer"))
        XCTAssertFalse(JobListView.queuedJobMetadata(item).contains("Unassigned"))
    }

    func testUnassignedPrinterStillRendersUnassigned() throws {
        let item = try TestData.decodeQueuedPrintJobResponse(
            from: TestJSON.queuedPrintJobResponseQueued
        )

        XCTAssertTrue(JobListView.queuedJobMetadata(item).contains("Unassigned"))
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
        mockJobService.errorToThrow = NetworkError.invalidResponse

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

    /// #3457 option A: on iPad the native List prevents these drops, so XCUI can only
    /// prove no `onMove` arrives. These pin the app's own rejection should one arrive.
    func testMoveQueuedRowsRejectsHarvestPriorityBoundaryDestination() async throws {
        let rows = try makeHarvestReorderRows(alphaBelowBeta: true)
        await viewModel.loadJobs()
        let before = viewModel.jobs.map(\.id)
        XCTAssertEqual(viewModel.queuedJobs.map(\.id), rows.map(\.id))

        // alpha (offset 2) dropped between the priority and printer boundary rows.
        await viewModel.moveQueuedRows(fromOffsets: IndexSet(integer: 2), toOffset: 5)

        XCTAssertEqual(viewModel.errorMessage,
                       "Jobs can only be reordered within the same printer and priority group.")
        XCTAssertNil(mockJobService.moveQueuedJobCalledWith)
        XCTAssertEqual(viewModel.jobs.map(\.id), before)
        XCTAssertTrue(viewModel.hasFreshQueueSnapshot)
    }

    func testMoveQueuedRowsRejectsHarvestPrinterBoundaryDestination() async throws {
        let rows = try makeHarvestReorderRows(alphaBelowBeta: false)
        await viewModel.loadJobs()
        let before = viewModel.jobs.map(\.id)
        XCTAssertEqual(viewModel.queuedJobs.map(\.id), rows.map(\.id))

        // alpha (offset 1) dropped below the printer boundary row, at the list end.
        await viewModel.moveQueuedRows(fromOffsets: IndexSet(integer: 1), toOffset: 6)

        XCTAssertEqual(viewModel.errorMessage,
                       "Jobs can only be reordered within the same printer and priority group.")
        XCTAssertNil(mockJobService.moveQueuedJobCalledWith)
        XCTAssertEqual(viewModel.jobs.map(\.id), before)
        XCTAssertTrue(viewModel.hasFreshQueueSnapshot)
    }

    /// Mirrors the queue-reorder XCUI fixture's visible queued rows: assigned, the
    /// high-priority alpha/beta/gamma group, a normal-priority row on the same
    /// printer, then a high-priority row on another printer.
    private func makeHarvestReorderRows(alphaBelowBeta: Bool) throws -> [QueuedPrintJobResponse] {
        let printerID = UUID()
        let assigned = try makeQueueJob(
            scope: printerID, status: .assigned, priority: .high, queuePosition: 0, name: "assigned"
        )
        let alpha = try makeQueueJob(
            scope: printerID, priority: .high, queuePosition: 1, revision: "AQIDAw==", name: "alpha"
        )
        let beta = try makeQueueJob(
            scope: printerID, priority: .high, queuePosition: 2, revision: "AQIDBA==", name: "beta"
        )
        let gamma = try makeQueueJob(
            scope: printerID, priority: .high, queuePosition: 3, revision: "AQIDBw==", name: "gamma"
        )
        let priorityBoundary = try makeQueueJob(
            scope: printerID, priority: .normal, queuePosition: 4, revision: "AQIDBQ==",
            name: "priority boundary"
        )
        let printerBoundary = try makeQueueJob(
            scope: UUID(), priority: .high, queuePosition: 1, revision: "AQIDBg==",
            name: "printer boundary"
        )
        let group = alphaBelowBeta ? [beta, alpha, gamma] : [alpha, beta, gamma]
        let rows = [assigned] + group + [priorityBoundary, printerBoundary]
        mockJobService.queuedJobResponsesToReturn = rows
        viewModel.setQueueWriteAuthorization(true)
        viewModel.setNetworkReachability(true)
        return rows
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

    func testQueueInvalidationKeepsDragAffordancesStableWhileRevalidating() async throws {
        // #3457: a SignalR queue update mid-drag must not flip edit mode or
        // `moveDisabled`, or UIKit silently cancels the user's drop.
        let printerID = UUID()
        let first = try makeQueueJob(scope: printerID, name: "first")
        let second = try makeQueueJob(scope: printerID, name: "second")
        mockJobService.queuedJobResponsesToReturn = [first, second]
        viewModel.setQueueWriteAuthorization(true)
        viewModel.setNetworkReachability(true)
        await viewModel.loadJobs()
        let signalR = MockSignalRService()
        viewModel.configureSignalR(signalR)
        let groupID = try XCTUnwrap(viewModel.reorderableQueueGroups.first?.id)
        let firstID = try XCTUnwrap(first.job.jobUUID)
        XCTAssertTrue(viewModel.keepsQueueEditingActive)
        XCTAssertTrue(viewModel.presentsMoveControl(id: firstID, inGroup: groupID))

        let gate = QueueMoveGate()
        mockJobService.beforeListAllJobs = { await gate.suspend() }
        signalR.simulateJobQueueUpdate(JobQueueUpdate(printerId: printerID, jobs: []))
        await gate.waitUntilEntered()

        XCTAssertTrue(viewModel.isRevalidatingQueue)
        XCTAssertFalse(viewModel.hasFreshQueueSnapshot)
        XCTAssertFalse(viewModel.canReorderQueue, "Mutations still require a fresh snapshot.")
        XCTAssertTrue(viewModel.keepsQueueEditingActive)
        XCTAssertTrue(viewModel.presentsQueueReorder)
        XCTAssertTrue(viewModel.presentsMoveControl(id: firstID, inGroup: groupID))
        XCTAssertFalse(viewModel.canMoveQueuedJob(id: firstID, direction: .down, inGroup: groupID))

        await viewModel.moveQueuedRows(fromOffsets: IndexSet(integer: 0), toOffset: 2)
        XCTAssertEqual(viewModel.errorMessage, "Refresh the queue before reordering to load current revisions.",
                       "A drop during revalidation must be rejected visibly, not silently cancelled.")
        XCTAssertNil(mockJobService.moveQueuedJobCalledWith)

        await gate.release()
        for _ in 0..<500 where viewModel.isRevalidatingQueue || !viewModel.hasFreshQueueSnapshot {
            await Task.yield()
        }
        XCTAssertFalse(viewModel.isRevalidatingQueue)
        XCTAssertTrue(viewModel.canReorderQueue)
        XCTAssertEqual(viewModel.keepsQueueEditingActive, viewModel.canReorderQueue)
        XCTAssertTrue(viewModel.canMoveQueuedJob(id: firstID, direction: .down, inGroup: groupID))
    }

    func testFailedQueueRevalidationEndsDragAffordances() async throws {
        let printerID = UUID()
        let first = try makeQueueJob(scope: printerID, name: "first")
        let second = try makeQueueJob(scope: printerID, name: "second")
        mockJobService.queuedJobResponsesToReturn = [first, second]
        viewModel.setQueueWriteAuthorization(true)
        viewModel.setNetworkReachability(true)
        await viewModel.loadJobs()
        let signalR = MockSignalRService()
        viewModel.configureSignalR(signalR)
        let groupID = try XCTUnwrap(viewModel.reorderableQueueGroups.first?.id)
        let firstID = try XCTUnwrap(first.job.jobUUID)

        mockJobService.errorToThrow = NetworkError.invalidResponse
        signalR.simulateJobQueueUpdate(JobQueueUpdate(printerId: printerID, jobs: []))
        for _ in 0..<500 where viewModel.errorMessage == nil || viewModel.isRevalidatingQueue {
            await Task.yield()
        }

        XCTAssertFalse(viewModel.isRevalidatingQueue)
        XCTAssertFalse(viewModel.hasFreshQueueSnapshot)
        XCTAssertFalse(viewModel.keepsQueueEditingActive)
        XCTAssertFalse(viewModel.presentsMoveControl(id: firstID, inGroup: groupID))
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
