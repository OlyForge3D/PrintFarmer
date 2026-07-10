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
