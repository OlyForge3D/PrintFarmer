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
}
