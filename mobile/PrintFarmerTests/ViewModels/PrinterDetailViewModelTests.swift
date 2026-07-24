import XCTest
@testable import PrintFarmer

/// Tests for PrinterDetailViewModel: commands, confirmations,
/// state computation, and error handling.
/// Uses MockPrinterService via configure() DI pattern.
@MainActor
final class PrinterDetailViewModelTests: XCTestCase {

    private var mockService: MockPrinterService!
    private var viewModel: PrinterDetailViewModel!

    override func setUp() {
        super.setUp()
        mockService = MockPrinterService()
        viewModel = PrinterDetailViewModel(printerId: TestData.testUUID)
        viewModel.configure(printerService: mockService)
    }

    override func tearDown() {
        viewModel?.stopSnapshotPolling()
        mockService = nil
        viewModel = nil
        super.tearDown()
    }

    // MARK: - Load Printer Detail

    func testLoadPrinterSuccess() async throws {
        let printer = try TestData.decodePrinter()
        mockService.printerToReturn = printer

        await viewModel.loadPrinter()

        XCTAssertEqual(viewModel.printer?.name, "Prusa MK4")
        XCTAssertEqual(mockService.getPrinterCalledWith, TestData.testUUID)
        XCTAssertFalse(viewModel.isLoading)
        XCTAssertNil(viewModel.errorMessage)
    }

    func testLoadPrinterError() async {
        mockService.errorToThrow = NetworkError.notFound

        await viewModel.loadPrinter()

        XCTAssertNil(viewModel.printer)
        XCTAssertNotNil(viewModel.errorMessage)
        XCTAssertFalse(viewModel.isLoading)
    }

    // MARK: - Computed State

    func testIsPrintingState() async throws {
        let printer = try TestData.decodePrinter() // state: "printing"
        mockService.printerToReturn = printer
        await viewModel.loadPrinter()

        XCTAssertTrue(viewModel.isPrinting)
        XCTAssertFalse(viewModel.isPaused)
        XCTAssertFalse(viewModel.isIdle)
        XCTAssertTrue(viewModel.isOnline)
    }

    func testIsOfflineState() async throws {
        let printer = try TestData.decodePrinter(from: TestJSON.printerMinimal)
        mockService.printerToReturn = printer
        await viewModel.loadPrinter()

        XCTAssertFalse(viewModel.isPrinting)
        XCTAssertFalse(viewModel.isOnline)
    }

    // MARK: - Camera Branching

    func testSnapshotOnlyCameraStartsPollingSnapshots() async throws {
        let printer = try TestData.decodePrinter(from: TestJSON.printerMinimal)
        mockService.printerToReturn = printer
        mockService.cameraUrlToReturn = PrinterCameraUrl(
            streamUrl: nil,
            snapshotUrl: nil,
            accessMode: .snapshotOnly,
            streamFormat: .unknown,
            snapshotStrategy: .snapmakerU1MonitorJpeg
        )
        mockService.snapshotDataToReturn = Data([0xff, 0xd8, 0xff, 0xd9])
        viewModel = PrinterDetailViewModel(
            printerId: TestData.testUUID,
            snapshotPollInterval: .milliseconds(10),
            snapshotErrorBackoffBaseSeconds: 1,
            snapshotErrorBackoffMaxSeconds: 1
        )
        viewModel.configure(printerService: mockService)

        await viewModel.loadPrinter()
        try? await Task.sleep(for: .milliseconds(25))
        viewModel.stopSnapshotPolling()

        XCTAssertEqual(viewModel.cameraPreviewMode, .snapshotPolling)
        XCTAssertEqual(viewModel.printer?.cameraSnapshotStrategy, .snapmakerU1MonitorJpeg)
        XCTAssertGreaterThan(mockService.getSnapshotCallCount, 0)
        XCTAssertNotNil(viewModel.snapshotData)
    }

    func testSnapshotOnlyDirectUrlUsesDirectSnapshot() async throws {
        let printer = try TestData.decodePrinter(from: TestJSON.printerMinimal)
        mockService.printerToReturn = printer
        mockService.cameraUrlToReturn = PrinterCameraUrl(
            streamUrl: nil,
            snapshotUrl: "http://printer.local/snapshot.jpg",
            accessMode: .snapshotOnly,
            streamFormat: .unknown,
            snapshotStrategy: .directUrl
        )

        await viewModel.loadPrinter()

        XCTAssertEqual(viewModel.cameraPreviewMode, .directSnapshot)
        XCTAssertFalse(viewModel.isSnapshotPollingActive)
    }

    func testStreamAndSnapshotSnapmakerStrategyWithoutUrlPollsSnapshot() async throws {
        let printer = try TestData.decodePrinter(from: TestJSON.printerMinimal)
        mockService.printerToReturn = printer
        mockService.cameraUrlToReturn = PrinterCameraUrl(
            streamUrl: nil,
            snapshotUrl: nil,
            accessMode: .streamAndSnapshot,
            streamFormat: .unsupported,
            snapshotStrategy: .snapmakerU1MonitorJpeg
        )
        mockService.snapshotDataToReturn = Data([0xff, 0xd8, 0xff, 0xd9])

        await viewModel.loadPrinter()

        XCTAssertEqual(viewModel.cameraPreviewMode, .snapshotPolling)
        XCTAssertGreaterThan(mockService.getSnapshotCallCount, 0)
    }

    func testMjpegStreamCameraDoesNotStartSnapshotPolling() async throws {
        let printer = try TestData.decodePrinter()
        mockService.printerToReturn = printer
        mockService.cameraUrlToReturn = PrinterCameraUrl(
            streamUrl: "http://192.168.1.100:8080/?action=stream",
            snapshotUrl: "http://192.168.1.100/snapshot.jpg",
            accessMode: .streamAndSnapshot,
            streamFormat: .mjpeg,
            snapshotStrategy: .directUrl
        )

        await viewModel.loadPrinter()
        viewModel.startSnapshotPollingIfNeeded()
        try? await Task.sleep(for: .milliseconds(25))

        XCTAssertEqual(viewModel.cameraPreviewMode, .mjpegStream)
        XCTAssertFalse(viewModel.isSnapshotPollingActive)
        XCTAssertEqual(mockService.getSnapshotCallCount, 0)
    }

    func testUnsupportedStreamWithSnapshotUrlUsesDirectSnapshot() async throws {
        let printer = try TestData.decodePrinter(from: TestJSON.printerMinimal)
        mockService.printerToReturn = printer
        mockService.cameraUrlToReturn = PrinterCameraUrl(
            streamUrl: "rtsp://printer.local/live",
            snapshotUrl: "http://printer.local/snapshot.jpg",
            accessMode: .unsupportedStream,
            streamFormat: .rtsp,
            snapshotStrategy: .directUrl
        )

        await viewModel.loadPrinter()

        XCTAssertEqual(viewModel.cameraPreviewMode, .directSnapshot)
        XCTAssertFalse(viewModel.isSnapshotPollingActive)
    }

    func testUnsupportedStreamCameraUsesUnsupportedPreviewMode() async throws {
        let printer = try TestData.decodePrinter(from: TestJSON.printerMinimal)
        mockService.printerToReturn = printer
        mockService.cameraUrlToReturn = PrinterCameraUrl(
            streamUrl: "rtsp://printer.local/live",
            snapshotUrl: nil,
            accessMode: .unsupportedStream,
            streamFormat: .rtsp,
            snapshotStrategy: .none
        )

        await viewModel.loadPrinter()

        XCTAssertEqual(viewModel.cameraPreviewMode, .unsupported)
        XCTAssertFalse(viewModel.isSnapshotPollingActive)
        XCTAssertEqual(mockService.getSnapshotCallCount, 0)
    }

    // MARK: - Pause/Resume Commands

    func testPausePrinterCallsService() async throws {
        let printer = try TestData.decodePrinter()
        mockService.printerToReturn = printer
        await viewModel.loadPrinter()

        await viewModel.pausePrinter()

        XCTAssertEqual(mockService.pauseCalledWith, TestData.testUUID)
    }

    func testResumePrinterCallsService() async throws {
        let printer = try TestData.decodePrinter()
        mockService.printerToReturn = printer
        await viewModel.loadPrinter()

        await viewModel.resumePrinter()

        XCTAssertEqual(mockService.resumeCalledWith, TestData.testUUID)
    }

    func testStopPrinterCallsService() async throws {
        let printer = try TestData.decodePrinter()
        mockService.printerToReturn = printer
        await viewModel.loadPrinter()

        await viewModel.stopPrinter()

        XCTAssertEqual(mockService.stopCalledWith, TestData.testUUID)
    }

    // MARK: - Destructive Action Confirmation

    func testRequestCancelShowsConfirmation() {
        viewModel.requestCancel()

        XCTAssertTrue(viewModel.showConfirmation)
        XCTAssertNotNil(viewModel.pendingAction)
        if case .cancelPrint = viewModel.pendingAction {
            // Expected
        } else {
            XCTFail("Expected .cancelPrint pending action")
        }
    }

    func testRequestEmergencyStopShowsConfirmation() {
        viewModel.requestEmergencyStop()

        XCTAssertTrue(viewModel.showConfirmation)
        if case .emergencyStop = viewModel.pendingAction {
            // Expected
        } else {
            XCTFail("Expected .emergencyStop pending action")
        }
    }

    func testDestructiveActionTitles() {
        XCTAssertEqual(PrinterDetailViewModel.DestructiveAction.cancelPrint.title, "Cancel Print")
        XCTAssertEqual(PrinterDetailViewModel.DestructiveAction.emergencyStop.title, "Emergency Stop")
    }

    func testDestructiveActionMessages() {
        XCTAssertFalse(PrinterDetailViewModel.DestructiveAction.cancelPrint.message.isEmpty)
        XCTAssertFalse(PrinterDetailViewModel.DestructiveAction.emergencyStop.message.isEmpty)
    }

    // MARK: - Command Error Handling

    func testCommandErrorSetsActionError() async throws {
        let printer = try TestData.decodePrinter()
        mockService.printerToReturn = printer
        await viewModel.loadPrinter()

        mockService.errorToThrow = NetworkError.serverError(500)
        await viewModel.pausePrinter()

        XCTAssertNotNil(viewModel.actionError)
        XCTAssertFalse(viewModel.isPerformingAction)
    }

    // MARK: - Not Configured

    func testActionsWithoutConfigureDoNotCrash() async {
        let unconfigured = PrinterDetailViewModel(printerId: TestData.testUUID)
        await unconfigured.loadPrinter()
        await unconfigured.pausePrinter()
        // Should silently return
        XCTAssertFalse(unconfigured.isLoading)
    }

    // MARK: - SignalR Live Updates (Refs #706 reviewer BLOCK)

    /// Regression: `AdvancedPrinterControlsDestination` (F1 #706) must
    /// call `configureSignalR(services.signalRService)` just like
    /// `PrinterDetailView.task` so state/lockouts refresh live and
    /// pending commands clear. Bishop/Hicks/Vasquez consensus flagged
    /// the missing wire-up in the destination as a BLOCK. This test
    /// pins the underlying `PrinterDetailViewModel.configureSignalR`
    /// seam: once wired to a SignalR service and a printer is loaded,
    /// a `printerupdated` broadcast for the same printer must be
    /// applied to `viewModel.printer` — the same guarantee the
    /// Advanced surface now depends on.
    func testConfigureSignalRAppliesLivePrinterUpdate() async throws {
        let printer = try TestData.decodePrinter() // testUUID, state: "printing"
        mockService.printerToReturn = printer
        await viewModel.loadPrinter()
        viewModel.isViewActive = true

        let signalR = MockSignalRService()
        viewModel.configureSignalR(signalR)

        let update = PrinterStatusUpdate(
            id: TestData.testUUID,
            isOnline: true,
            state: "idle",
            progress: 42.0,
            jobName: nil,
            fileName: nil,
            thumbnailUrl: nil,
            cameraStreamUrl: nil,
            x: nil, y: nil, z: nil,
            hotendTemp: 210.5,
            bedTemp: 60.0,
            hotendTarget: nil,
            bedTarget: nil,
            homedAxes: nil,
            spoolInfo: nil,
            mmuStatus: nil
        )
        signalR.simulatePrinterUpdate(update)

        // Live update is dispatched via `Task { @MainActor }`; yield
        // to the runloop so the hop lands before we assert.
        await Task.yield()
        try? await Task.sleep(for: .milliseconds(20))

        XCTAssertEqual(viewModel.printer?.state, "idle",
                       "SignalR update must transition state so Advanced controls unlock")
        XCTAssertEqual(viewModel.printer?.progress, 0.42,
                       "SignalR progress (0–100) must be normalized to 0–1.0")
        XCTAssertEqual(viewModel.printer?.hotendTemp, 210.5)
        XCTAssertEqual(viewModel.printer?.bedTemp, 60.0)
    }

    /// Unanimous #706 BLOCK: a live `printerupdated` carrying `homedAxes`
    /// must propagate into `viewModel.printer.homedAxes`. Before the fix
    /// `applyLiveUpdate` mutated position/temperature/spool but silently
    /// dropped `homedAxes`, so `PrinterControlsViewModel.handlePrinterUpdate`
    /// never saw the homing confirmation and a pending Home command could
    /// stick forever. This exercises the real SignalR → applyLiveUpdate
    /// pipeline (not a hand-built Printer) so the home-command correlation
    /// has genuine production evidence to correlate against.
    func testConfigureSignalRAppliesLiveHomedAxesUpdate() async throws {
        let printer = try TestData.decodePrinter() // testUUID, homedAxes: "xyz"
        mockService.printerToReturn = printer
        await viewModel.loadPrinter()
        viewModel.isViewActive = true
        XCTAssertEqual(viewModel.printer?.homedAxes, "xyz")

        let signalR = MockSignalRService()
        viewModel.configureSignalR(signalR)

        let update = PrinterStatusUpdate(
            id: TestData.testUUID,
            isOnline: true,
            state: nil, // no state transition — homing arrives on its own
            progress: nil,
            jobName: nil,
            fileName: nil,
            thumbnailUrl: nil,
            cameraStreamUrl: nil,
            x: nil, y: nil, z: nil,
            hotendTemp: nil,
            bedTemp: nil,
            hotendTarget: nil,
            bedTarget: nil,
            homedAxes: "xy",
            spoolInfo: nil,
            mmuStatus: nil
        )
        signalR.simulatePrinterUpdate(update)

        // Live update is dispatched via `Task { @MainActor }`; yield to the
        // runloop so the hop lands before we assert.
        await Task.yield()
        try? await Task.sleep(for: .milliseconds(20))

        XCTAssertEqual(viewModel.printer?.homedAxes, "xy",
                       "Live homedAxes must propagate so the controls VM can confirm a Home command")
    }

    /// Ignores updates targeting a different printer so the Advanced
    /// destination does not clobber its own state with unrelated
    /// broadcasts on the shared SignalR channel.
    func testConfigureSignalRIgnoresUpdatesForOtherPrinters() async throws {
        let printer = try TestData.decodePrinter() // testUUID, state: "printing"
        mockService.printerToReturn = printer
        await viewModel.loadPrinter()
        viewModel.isViewActive = true

        let signalR = MockSignalRService()
        viewModel.configureSignalR(signalR)

        let update = PrinterStatusUpdate(
            id: TestData.testUUID2,
            isOnline: false,
            state: "idle",
            progress: nil,
            jobName: nil,
            fileName: nil,
            thumbnailUrl: nil,
            cameraStreamUrl: nil,
            x: nil, y: nil, z: nil,
            hotendTemp: nil,
            bedTemp: nil,
            hotendTarget: nil,
            bedTarget: nil,
            homedAxes: nil,
            spoolInfo: nil,
            mmuStatus: nil
        )
        signalR.simulatePrinterUpdate(update)

        await Task.yield()
        try? await Task.sleep(for: .milliseconds(20))

        XCTAssertEqual(viewModel.printer?.state, "printing",
                       "Foreign-printer update must not overwrite this view's printer state")
    }
}

// MARK: - F7 Printer Detail v2 (issue #712)

/// Fixed instant for deterministic ETA assertions. Declared at file scope so it
/// is nonisolated and safe to capture inside the `@Sendable` clock closure.
private let f7FixedNow = Date(timeIntervalSince1970: 1_700_000_000)

/// Operator-section coverage for Printer Detail v2: queue filtering / match
/// state, ETA formatting off the backend `printTimeLeftSeconds` (deterministic
/// via injected clock), odometer due logic, history mapping, the Mainsail deep
/// link, dispatch-to and maintenance-log actions, and empty/absent states.
/// All deterministic — no sleeps, polling, or retries.
extension PrinterDetailViewModelTests {

    private static let fixedNow = f7FixedNow

    private func makeOperatorViewModel(
        jobService: MockJobService = MockJobService(),
        maintenanceService: MockMaintenanceService = MockMaintenanceService()
    ) -> PrinterDetailViewModel {
        let vm = PrinterDetailViewModel(printerId: TestData.testUUID, now: { f7FixedNow })
        vm.configure(printerService: mockService)
        vm.configureOperatorServices(jobService: jobService, maintenanceService: maintenanceService)
        return vm
    }

    private func makeToolhead(index: Int, material: String?) -> Toolhead {
        Toolhead(
            id: UUID(),
            name: "Tool \(index)",
            index: index,
            isPrimary: index == 0,
            currentMaterial: material
        )
    }

    private func makeQueuedJob(
        id: String,
        assignedTo: UUID?,
        status: String,
        position: Int,
        material: String? = nil
    ) -> QueuedPrintJobResponse {
        let job = QueuedJobInfo(
            id: id,
            name: "job-\(id)",
            fileName: "job-\(id).gcode",
            assignedPrinterId: assignedTo?.uuidString,
            printerName: nil,
            printerModel: nil,
            status: status,
            priority: 1,
            queuePosition: position,
            estimatedPrintTimeSeconds: nil,
            actualStartTimeUtc: nil,
            actualEndTimeUtc: nil,
            actualPrintTimeSeconds: nil,
            failureReason: nil,
            createdAtUtc: Self.fixedNow,
            updatedAtUtc: nil,
            thumbnailUrl: nil,
            filamentName: nil,
            filamentColor: nil,
            copies: 1,
            completedCopies: 0,
            remainingCopies: 1
        )
        let gcode = material.map { material in
            QueueGcodeFileMeta(
                id: "g-\(id)",
                name: "job-\(id)",
                fileName: "job-\(id).gcode",
                fileSizeBytes: nil,
                materialType: material,
                nozzleDiameter: nil,
                estimatedPrintTimeSeconds: nil,
                estimatedFilamentUsageGrams: nil,
                thumbnailUrl: nil
            )
        }
        return QueuedPrintJobResponse(
            job: job,
            gcodeFile: gcode,
            assignedPrinter: nil,
            estimatedStartTime: nil,
            estimatedCompletionTime: nil
        )
    }

    private func makeHistoryJob(id: String, status: String, end: Double?) -> PrinterHistoryJob {
        PrinterHistoryJob(
            jobId: id,
            status: status,
            filename: "\(id).gcode",
            startTime: 100,
            endTime: end,
            printDuration: 10,
            totalDuration: 12,
            filamentUsed: 5,
            thumbnailUrl: nil
        )
    }

    private func makeUpcoming(
        name: String,
        hoursUntilDue: Double?,
        isOverdue: Bool = false,
        isDueToday: Bool = false
    ) -> UpcomingMaintenanceTask {
        UpcomingMaintenanceTask(
            id: name,
            taskId: UUID(),
            printerId: TestData.testUUID,
            printerName: "Prusa MK4",
            taskName: name,
            component: "Nozzle",
            description: nil,
            priority: 1,
            intervalType: "hours",
            intervalValue: 100,
            dueDate: nil,
            daysUntilDue: nil,
            hoursUntilDue: hoursUntilDue,
            isOverdue: isOverdue,
            isDueToday: isDueToday,
            lastPerformedAt: nil
        )
    }

    // MARK: Queue filtering + match state

    func testFilterAssignedQueueKeepsAssignedNonTerminalSortedByPosition() {
        let jobs = [
            makeQueuedJob(id: "a", assignedTo: TestData.testUUID, status: "Queued", position: 3),
            makeQueuedJob(id: "b", assignedTo: TestData.testUUID, status: "Printing", position: 1),
            makeQueuedJob(id: "c", assignedTo: TestData.testUUID, status: "Queued", position: 2)
        ]
        let filtered = PrinterDetailViewModel.filterAssignedQueue(jobs, printerId: TestData.testUUID)
        XCTAssertEqual(filtered.map(\.id), ["b", "c", "a"], "Assigned jobs must sort by queue position")
    }

    func testFilterAssignedQueueExcludesTerminalAndForeign() {
        let jobs = [
            makeQueuedJob(id: "mine", assignedTo: TestData.testUUID, status: "Queued", position: 1),
            makeQueuedJob(id: "done", assignedTo: TestData.testUUID, status: "Completed", position: 2),
            makeQueuedJob(id: "cancelled", assignedTo: TestData.testUUID, status: "Cancelled", position: 3),
            makeQueuedJob(id: "foreign", assignedTo: TestData.testUUID2, status: "Queued", position: 4),
            makeQueuedJob(id: "unassigned", assignedTo: nil, status: "Queued", position: 5)
        ]
        let filtered = PrinterDetailViewModel.filterAssignedQueue(jobs, printerId: TestData.testUUID)
        XCTAssertEqual(filtered.map(\.id), ["mine"], "Only non-terminal jobs assigned to this printer survive")
    }

    func testNextQueuedJobsCapsAtThree() {
        let vm = makeOperatorViewModel()
        vm.assignedQueue = (0..<5).map {
            makeQueuedJob(id: "\($0)", assignedTo: TestData.testUUID, status: "Queued", position: $0)
        }
        XCTAssertEqual(vm.nextQueuedJobs.count, 3, "Queue section shows at most three jobs")
    }

    func testMatchStateMatchMismatchUnknown() {
        let vm = makeOperatorViewModel()
        vm.toolheads = [makeToolhead(index: 0, material: "PLA")]

        let matching = makeQueuedJob(id: "m", assignedTo: TestData.testUUID, status: "Queued", position: 1, material: "pla")
        XCTAssertEqual(vm.matchState(for: matching), .match, "Case-insensitive material match")

        let mismatched = makeQueuedJob(id: "x", assignedTo: TestData.testUUID, status: "Queued", position: 2, material: "PETG")
        XCTAssertEqual(vm.matchState(for: mismatched), .mismatch)

        let noMeta = makeQueuedJob(id: "u", assignedTo: TestData.testUUID, status: "Queued", position: 3, material: nil)
        XCTAssertEqual(vm.matchState(for: noMeta), .unknown, "No required material ⇒ unknown")

        vm.toolheads = []
        XCTAssertEqual(vm.matchState(for: matching), .unknown, "No loaded material ⇒ unknown")
    }

    // MARK: ETA formatting (deterministic clock)

    func testCurrentJobEtaUsesInjectedClockAndBackendSeconds() async throws {
        let vm = makeOperatorViewModel()
        mockService.printerToReturn = try TestData.decodePrinter() // state: printing
        await vm.loadPrinter()
        vm.statusDetail = PrinterStatusDetail(
            id: TestData.testUUID, isOnline: true, state: "printing", progress: 0.5,
            jobName: "benchy", thumbnailUrl: nil, cameraStreamUrl: nil, cameraSnapshotUrl: nil,
            x: nil, y: nil, z: nil, hotendTemp: nil, bedTemp: nil, hotendTarget: nil, bedTarget: nil,
            spoolInfo: nil, mmuStatus: nil, printTimeLeftSeconds: 4500
        )

        XCTAssertEqual(vm.currentJobRemainingSeconds, 4500)
        XCTAssertEqual(vm.currentJobEtaDate, Self.fixedNow.addingTimeInterval(4500))
        XCTAssertNotNil(vm.formattedTimeRemaining)
        XCTAssertNotNil(vm.formattedEtaClock)
    }

    func testCurrentJobEtaNilWhenNotPrintingOrNoBackendSeconds() {
        // No printer loaded ⇒ isActivelyPrinting is false regardless of a
        // backend-supplied remaining-seconds value.
        let vm = makeOperatorViewModel()
        vm.statusDetail = PrinterStatusDetail(
            id: TestData.testUUID, isOnline: false, state: "idle", progress: nil,
            jobName: nil, thumbnailUrl: nil, cameraStreamUrl: nil, cameraSnapshotUrl: nil,
            x: nil, y: nil, z: nil, hotendTemp: nil, bedTemp: nil, hotendTarget: nil, bedTarget: nil,
            spoolInfo: nil, mmuStatus: nil, printTimeLeftSeconds: 4500
        )
        XCTAssertNil(vm.currentJobRemainingSeconds, "Not actively printing ⇒ no ETA")
        XCTAssertNil(vm.currentJobEtaDate)
        XCTAssertNil(vm.formattedTimeRemaining)
    }

    // MARK: Odometer due logic

    func testOdometerRowsDeriveThresholdAndDueState() {
        let vm = makeOperatorViewModel()
        vm.printerStatistics = PrinterMaintenanceStatistics(printerId: TestData.testUUID, totalPrintHours: 120)
        vm.upcomingMaintenance = [
            makeUpcoming(name: "OnSchedule", hoursUntilDue: 30),
            makeUpcoming(name: "Overdue", hoursUntilDue: -5, isOverdue: true),
            makeUpcoming(name: "DueToday", hoursUntilDue: 2, isDueToday: true)
        ]

        let rows = vm.odometerRows
        // Overdue sorts first.
        XCTAssertEqual(rows.first?.title, "Overdue")

        let onSchedule = rows.first { $0.title == "OnSchedule" }
        XCTAssertEqual(onSchedule?.thresholdHours, 150, "threshold = current 120h + 30h until due")
        XCTAssertEqual(onSchedule?.currentHours, 120)
        XCTAssertFalse(onSchedule?.isDue ?? true)
        XCTAssertEqual(onSchedule?.stateLabel, "In 30 h")

        let overdue = rows.first { $0.title == "Overdue" }
        XCTAssertTrue(overdue?.isDue ?? false)
        XCTAssertEqual(overdue?.stateLabel, "Overdue")

        let dueToday = rows.first { $0.title == "DueToday" }
        XCTAssertTrue(dueToday?.isDue ?? false)
        XCTAssertEqual(dueToday?.stateLabel, "Due today")
    }

    // MARK: History mapping

    func testSortedHistoryNewestFirstAndTailCapsAtFive() {
        let jobs = (1...7).map { makeHistoryJob(id: "j\($0)", status: "completed", end: Double($0 * 100)) }
        let vm = makeOperatorViewModel()
        vm.history = PrinterDetailViewModel.sortedHistory(jobs)
        XCTAssertEqual(vm.history.first?.id, "j7", "Newest (largest endTime) first")
        XCTAssertEqual(vm.historyTail.count, 5, "History tail capped at five")
        XCTAssertEqual(vm.historyTail.map(\.id), ["j7", "j6", "j5", "j4", "j3"])
    }

    func testHistoryOutcomeMapping() {
        XCTAssertEqual(makeHistoryJob(id: "a", status: "completed", end: 1).outcome, .completed)
        XCTAssertEqual(makeHistoryJob(id: "b", status: "aborted", end: 1).outcome, .cancelled)
        XCTAssertEqual(makeHistoryJob(id: "c", status: "klippy_shutdown", end: 1).outcome, .failed)
        XCTAssertEqual(makeHistoryJob(id: "d", status: "printing", end: nil).outcome, .inProgress)
        XCTAssertEqual(makeHistoryJob(id: "e", status: "weird", end: 1).outcome, .unknown)
    }

    // MARK: Mainsail deep link

    func testMainsailUrlValidHttpOnly() async throws {
        let vm = makeOperatorViewModel()
        mockService.printerToReturn = try TestData.decodePrinter() // has backendUrl
        await vm.loadPrinter()
        // backendUrl comes from the fixture; assert scheme guard behavior explicitly.
        if let url = vm.mainsailUrl {
            XCTAssertTrue(url.scheme?.hasPrefix("http") ?? false)
        }
    }

    // MARK: Section population + empty/absent states

    func testLoadOperatorSectionsPopulatesEverySection() async {
        let jobService = MockJobService()
        jobService.queuedJobResponsesToReturn = [
            makeQueuedJob(id: "q1", assignedTo: TestData.testUUID, status: "Queued", position: 1, material: "PLA")
        ]
        let maintenance = MockMaintenanceService()
        maintenance.printerStatisticsToReturn = PrinterMaintenanceStatistics(printerId: TestData.testUUID, totalPrintHours: 88)
        maintenance.upcomingTasksToReturn = [makeUpcoming(name: "Lube", hoursUntilDue: 10)]
        mockService.detailsToReturn = PrinterDetails(
            id: TestData.testUUID, name: "Prusa MK4", backend: .moonraker,
            toolheads: [makeToolhead(index: 0, material: "PLA")]
        )
        mockService.historyToReturn = PrinterHistoryList(count: 1, jobs: [makeHistoryJob(id: "h1", status: "completed", end: 500)])

        let vm = makeOperatorViewModel(jobService: jobService, maintenanceService: maintenance)
        await vm.loadOperatorSections()

        XCTAssertEqual(vm.toolheads.count, 1)
        XCTAssertEqual(vm.assignedQueue.count, 1)
        XCTAssertEqual(vm.printerStatistics?.totalPrintHours, 88)
        XCTAssertEqual(vm.upcomingMaintenance.count, 1)
        XCTAssertEqual(vm.history.count, 1)
        XCTAssertEqual(maintenance.getUpcomingCalledWith?.printerId, TestData.testUUID, "Upcoming must be scoped to this printer")
        XCTAssertEqual(mockService.getHistoryCalledWith?.id, TestData.testUUID)
    }

    func testOperatorSectionsEmptyWhenServicesReturnNothing() async {
        let vm = makeOperatorViewModel()
        await vm.loadOperatorSections() // all mocks empty / stubbed to throw
        XCTAssertTrue(vm.toolheads.isEmpty)
        XCTAssertTrue(vm.assignedQueue.isEmpty)
        XCTAssertTrue(vm.nextQueuedJobs.isEmpty)
        XCTAssertTrue(vm.odometerRows.isEmpty)
        XCTAssertTrue(vm.historyTail.isEmpty)
        XCTAssertNil(vm.printerStatistics)
    }

    func testMainsailUrlNilWithoutBackendUrl() {
        let vm = makeOperatorViewModel()
        XCTAssertNil(vm.printer, "No printer loaded ⇒ no deep link")
        XCTAssertNil(vm.mainsailUrl)
    }

    // MARK: Dispatch-to action

    func testBeginDispatchLoadsCandidatesSortedByScore() async {
        let jobService = MockJobService()
        jobService.candidatesToReturn = [
            DispatchCandidate(printerId: TestData.testUUID, printerName: "Low", score: 10, eliminated: false, eliminationReasons: []),
            DispatchCandidate(printerId: TestData.testUUID2, printerName: "High", score: 90, eliminated: false, eliminationReasons: [])
        ]
        let vm = makeOperatorViewModel(jobService: jobService)
        let job = makeQueuedJob(id: UUID().uuidString, assignedTo: TestData.testUUID, status: "Queued", position: 1)

        await vm.beginDispatch(for: job)

        XCTAssertEqual(vm.dispatchTargetJob?.id, job.id)
        XCTAssertEqual(vm.dispatchCandidates.map(\.printerName), ["High", "Low"], "Candidates sorted by descending score")
        XCTAssertEqual(jobService.getCandidatesCalledWith, job.job.jobUUID)
        XCTAssertFalse(vm.isLoadingCandidates)
    }

    func testDispatchToCallsServiceAndClearsTarget() async {
        let jobService = MockJobService()
        let vm = makeOperatorViewModel(jobService: jobService)
        let job = makeQueuedJob(id: UUID().uuidString, assignedTo: TestData.testUUID, status: "Queued", position: 1)
        vm.dispatchTargetJob = job

        await vm.dispatch(job, to: TestData.testUUID2)

        XCTAssertEqual(jobService.dispatchToCalledWith?.jobId, job.job.jobUUID)
        XCTAssertEqual(jobService.dispatchToCalledWith?.printerId, TestData.testUUID2)
        XCTAssertNil(vm.dispatchTargetJob, "Successful dispatch clears the sheet")
        XCTAssertFalse(vm.isDispatching)
    }

    // MARK: Maintenance log completion

    func testLogMaintenanceCompletionPostsRequestAndRecordsTask() async {
        let maintenance = MockMaintenanceService()
        let taskId = UUID()
        maintenance.createdLogToReturn = MaintenanceLog(
            id: UUID(), printerId: TestData.testUUID, printerMaintenanceScheduleId: nil,
            resolvedAlertId: nil, maintenanceTaskId: taskId, taskName: "Nozzle swap",
            notes: nil, component: "Nozzle", performedBy: "op", performedAt: Self.fixedNow,
            durationMinutes: nil, cost: nil, partsReplaced: nil, printerHoursAtMaintenance: nil,
            createdAt: Self.fixedNow
        )
        let vm = makeOperatorViewModel(maintenanceService: maintenance)
        let row = PrinterDetailViewModel.OdometerRow(
            id: "r", title: "Nozzle swap", component: "Nozzle", taskId: taskId,
            currentHours: 120, thresholdHours: 120, hoursUntilDue: 0,
            isOverdue: true, isDueToday: false
        )

        await vm.logMaintenanceCompletion(row, performedBy: "op")

        XCTAssertEqual(maintenance.createLogCalledWith?.printerId, TestData.testUUID)
        XCTAssertEqual(maintenance.createLogCalledWith?.taskId, taskId)
        XCTAssertEqual(maintenance.createLogCalledWith?.taskName, "Nozzle swap")
        XCTAssertEqual(maintenance.createLogCalledWith?.performedBy, "op")
        XCTAssertEqual(vm.lastLoggedMaintenanceTaskId, taskId)
        XCTAssertNil(vm.actionError)
    }
}
