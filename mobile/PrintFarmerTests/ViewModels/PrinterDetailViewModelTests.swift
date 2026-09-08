import XCTest
@testable import PrintFarmer

/// Tests for PrinterDetailViewModel: commands, confirmations,
/// state computation, and error handling.
/// Uses MockPrinterService via configure() DI pattern.
@MainActor
final class PrinterDetailViewModelTests: XCTestCase {

    private var mockService: MockPrinterService!
    private var viewModel: PrinterDetailViewModel!

    override func setUp() async throws {
        try await super.setUp()
        mockService = MockPrinterService()
        viewModel = PrinterDetailViewModel(printerId: TestData.testUUID)
        viewModel.configure(printerService: mockService)
    }

    override func tearDown() async throws {
        viewModel?.stopSnapshotPolling()
        mockService = nil
        viewModel = nil
        try await super.tearDown()
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

    func testReconnectRecoveryRefreshesCanonicalDetailOnceAndFencesStaleService() async throws {
        let callbackQueue = ShiftTaskCallbackQueue()
        let oldPrinterService = MockPrinterService()
        oldPrinterService.printerToReturn = try TestData.decodePrinter()
        let oldSignalR = MockSignalRService()
        let currentPrinterService = MockPrinterService()
        currentPrinterService.printerToReturn = try TestData.decodePrinter(
            from: TestJSON.printerMinimal
        )
        let currentSignalR = MockSignalRService()
        let vm = PrinterDetailViewModel(
            printerId: TestData.testUUID,
            callbackEnqueuer: callbackQueue.enqueuer
        )
        vm.configure(printerService: oldPrinterService)
        vm.configureSignalR(oldSignalR)

        oldSignalR.simulateConnectionStateChange(.connected)
        await callbackQueue.runNext()
        XCTAssertEqual(oldPrinterService.getPrinterCallCount, 0)
        oldSignalR.simulateConnectionStateChange(.reconnecting)
        await callbackQueue.runNext()
        XCTAssertEqual(oldPrinterService.getPrinterCallCount, 0)
        oldSignalR.simulateConnectionStateChange(.connected)
        await callbackQueue.runNext()
        await vm.waitForCanonicalLoadToBecomeIdle()
        XCTAssertEqual(oldPrinterService.getPrinterCallCount, 1)
        XCTAssertEqual(vm.printer?.name, "Prusa MK4")

        oldSignalR.simulateConnectionStateChange(.connected)
        XCTAssertEqual(callbackQueue.count, 0)
        XCTAssertEqual(oldPrinterService.getPrinterCallCount, 1)

        vm.configure(printerService: currentPrinterService)
        vm.configureSignalR(currentSignalR)
        oldSignalR.simulateCapturedConnectionStateChange(at: 0, state: .reconnecting)
        oldSignalR.simulateCapturedConnectionStateChange(at: 0, state: .connected)
        XCTAssertEqual(callbackQueue.count, 2)
        await callbackQueue.runNext()
        await callbackQueue.runNext()
        XCTAssertEqual(oldPrinterService.getPrinterCallCount, 1)
        XCTAssertEqual(currentPrinterService.getPrinterCallCount, 0)

        currentSignalR.simulateConnectionStateChange(.connected)
        await callbackQueue.runNext()
        currentSignalR.simulateConnectionStateChange(.reconnecting)
        await callbackQueue.runNext()
        XCTAssertEqual(currentPrinterService.getPrinterCallCount, 0)

        currentSignalR.simulateConnectionStateChange(.connected)
        vm.isViewActive = false
        await callbackQueue.runNext()
        XCTAssertEqual(currentPrinterService.getPrinterCallCount, 0)

        vm.isViewActive = true
        vm.configureSignalR(currentSignalR)
        currentSignalR.simulateConnectionStateChange(.reconnecting)
        await callbackQueue.runNext()
        currentSignalR.simulateConnectionStateChange(.connected)
        await callbackQueue.runNext()
        await vm.waitForCanonicalLoadToBecomeIdle()

        XCTAssertEqual(currentPrinterService.getPrinterCallCount, 1)
        XCTAssertEqual(vm.printer?.name, "Ender 3")
        XCTAssertEqual(callbackQueue.count, 0)
        vm.stopSnapshotPolling()
    }

    func testCanonicalDetailRejectsReconfiguredInFlightDataAndSnapshotPublication() async throws {
        let oldGate = ShiftTaskResultGate<Printer>()
        let oldScript = ScriptedCanonicalResult<Printer>([.gated(oldGate)])
        mockService.getHandler = { _ in try await oldScript.next() }
        let currentService = MockPrinterService()
        currentService.printerToReturn = try TestData.decodePrinter(
            from: TestJSON.printerMinimal
        )

        let oldRequest = Task { await viewModel.loadPrinter() }
        await oldScript.waitForCallCount(1)
        viewModel.configure(printerService: currentService)
        await viewModel.loadPrinter()
        XCTAssertEqual(viewModel.printer?.name, "Ender 3")
        XCTAssertNil(viewModel.errorMessage)
        XCTAssertNil(viewModel.snapshotData)
        XCTAssertFalse(viewModel.isLoading)

        await oldGate.succeed(try TestData.decodePrinter())
        await viewModel.waitForSupersededCanonicalLoads()
        await oldRequest.value

        XCTAssertEqual(viewModel.printer?.name, "Ender 3")
        XCTAssertNil(viewModel.errorMessage)
        XCTAssertNil(viewModel.snapshotData)
        XCTAssertFalse(viewModel.isLoading)
    }

    func testCanonicalDetailRejectsReconfiguredInFlightErrorPublication() async throws {
        let oldGate = ShiftTaskResultGate<Printer>()
        let oldScript = ScriptedCanonicalResult<Printer>([.gated(oldGate)])
        mockService.getHandler = { _ in try await oldScript.next() }
        let currentService = MockPrinterService()
        currentService.printerToReturn = try TestData.decodePrinter(
            from: TestJSON.printerMinimal
        )

        let oldRequest = Task { await viewModel.loadPrinter() }
        await oldScript.waitForCallCount(1)
        viewModel.configure(printerService: currentService)
        await viewModel.loadPrinter()
        await oldGate.fail(.forced("stale detail failure"))
        await viewModel.waitForSupersededCanonicalLoads()
        await oldRequest.value

        XCTAssertEqual(viewModel.printer?.name, "Ender 3")
        XCTAssertNil(viewModel.errorMessage)
        XCTAssertNil(viewModel.snapshotData)
        XCTAssertFalse(viewModel.isLoading)
    }

    func testManualDetailRefreshCoalescesReconnectIntoOneAuthoritativeFollowUp() async throws {
        let callbackQueue = ShiftTaskCallbackQueue()
        let firstGate = ShiftTaskResultGate<Printer>()
        let stale = try TestData.decodePrinter()
        let current = try TestData.decodePrinter(from: TestJSON.printerMinimal)
        let script = ScriptedCanonicalResult<Printer>([
            .gated(firstGate),
            .value(current)
        ])
        mockService.getHandler = { _ in try await script.next() }
        let signalR = MockSignalRService()
        let vm = PrinterDetailViewModel(
            printerId: TestData.testUUID,
            callbackEnqueuer: callbackQueue.enqueuer
        )
        vm.configure(printerService: mockService)
        vm.configureSignalR(signalR)

        let manualRefresh = Task { await vm.loadPrinter() }
        await script.waitForCallCount(1)
        signalR.simulateConnectionStateChange(.reconnecting)
        await callbackQueue.runNext()
        signalR.simulateConnectionStateChange(.connected)
        await callbackQueue.runNext()
        var callCount = await script.callCount
        XCTAssertEqual(callCount, 1)

        await firstGate.succeed(stale)
        await script.waitForCallCount(2)
        await manualRefresh.value

        callCount = await script.callCount
        XCTAssertEqual(callCount, 2)
        XCTAssertEqual(vm.printer?.name, "Ender 3")
        signalR.simulateCapturedConnectionStateChange(at: 0, state: .connected)
        await callbackQueue.runNext()
        await vm.waitForCanonicalLoadToBecomeIdle()
        callCount = await script.callCount
        XCTAssertEqual(callCount, 2)
        vm.stopSnapshotPolling()
    }

    func testSameServiceReconfigurePreservesQueuedDetailReconnectEdge() async throws {
        let callbackQueue = ShiftTaskCallbackQueue()
        let current = try TestData.decodePrinter(from: TestJSON.printerMinimal)
        let script = ScriptedCanonicalResult<Printer>([.value(current)])
        mockService.getHandler = { _ in try await script.next() }
        let signalR = MockSignalRService()
        let vm = PrinterDetailViewModel(
            printerId: TestData.testUUID,
            callbackEnqueuer: callbackQueue.enqueuer
        )
        vm.configure(printerService: mockService)
        vm.configureSignalR(signalR)

        signalR.simulateConnectionStateChange(.reconnecting)
        signalR.simulateConnectionStateChange(.connected)
        XCTAssertEqual(callbackQueue.count, 2)
        vm.configureSignalR(signalR)
        await callbackQueue.runNext()
        await callbackQueue.runNext()
        await script.waitForCallCount(1)
        await vm.waitForCanonicalLoadToBecomeIdle()

        let callCount = await script.callCount
        XCTAssertEqual(callCount, 1)
        XCTAssertEqual(vm.printer?.name, "Ender 3")
        vm.stopSnapshotPolling()
    }

    func testDeactivationRejectsParkedDetailPublication() async throws {
        let gate = ShiftTaskResultGate<Printer>()
        let script = ScriptedCanonicalResult<Printer>([.gated(gate)])
        mockService.getHandler = { _ in try await script.next() }

        let request = Task { await viewModel.loadPrinter() }
        await script.waitForCallCount(1)
        viewModel.isViewActive = false
        await gate.succeed(try TestData.decodePrinter())
        await viewModel.waitForSupersededCanonicalLoads()
        await request.value

        XCTAssertNil(viewModel.printer)
        XCTAssertNil(viewModel.errorMessage)
        XCTAssertNil(viewModel.snapshotData)
        XCTAssertFalse(viewModel.isLoading)
    }

    func testCancellingOneDetailCallerCompletesPromptlyAndPreservesPeerDemand() async throws {
        let firstGate = ShiftTaskResultGate<Printer>()
        let current = try TestData.decodePrinter(from: TestJSON.printerMinimal)
        let script = ScriptedCanonicalResult<Printer>([
            .gated(firstGate),
            .value(current)
        ])
        mockService.getHandler = { _ in try await script.next() }

        let cancelledCaller = Task { await viewModel.loadPrinter() }
        await script.waitForCallCount(1)
        let peerCaller = Task { await viewModel.loadPrinter() }
        await viewModel.waitForCanonicalWaiterCount(2)

        cancelledCaller.cancel()
        await cancelledCaller.value
        XCTAssertEqual(viewModel.canonicalWaiterCountForTesting, 1)
        XAssertEqual(await script.callCount, 1)

        await firstGate.succeed(try TestData.decodePrinter())
        await peerCaller.value

        XAssertEqual(await script.callCount, 2)
        XCTAssertEqual(viewModel.printer?.id, current.id)
        XCTAssertEqual(viewModel.canonicalWaiterCountForTesting, 0)
    }

    func testCancellingSoleDetailCallerUnwindsDemandWithoutWaitingForService() async throws {
        let callbackQueue = ShiftTaskCallbackQueue()
        let gate = ShiftTaskResultGate<Printer>()
        let script = ScriptedCanonicalResult<Printer>([.gated(gate)])
        mockService.getHandler = { _ in try await script.next() }
        let vm = PrinterDetailViewModel(
            printerId: TestData.testUUID,
            callbackEnqueuer: callbackQueue.enqueuer
        )
        vm.configure(printerService: mockService)

        let caller = Task { await vm.loadPrinter() }
        await script.waitForCallCount(1)
        caller.cancel()
        await caller.value
        XCTAssertEqual(callbackQueue.count, 1)
        await callbackQueue.runNext()

        XCTAssertEqual(vm.canonicalWaiterCountForTesting, 0)
        XCTAssertFalse(vm.isLoading)
        XCTAssertNil(vm.printer)
        XAssertEqual(await script.callCount, 1)

        await gate.succeed(try TestData.decodePrinter())
        await vm.waitForSupersededCanonicalLoads()
        XCTAssertNil(vm.printer)
    }

    func testParkedDetailServiceDoesNotRetainViewModelOrCaller() async throws {
        let gate = ShiftTaskResultGate<Printer>()
        let script = ScriptedCanonicalResult<Printer>([.gated(gate)])
        mockService.getHandler = { _ in try await script.next() }
        var vm: PrinterDetailViewModel? = PrinterDetailViewModel(
            printerId: TestData.testUUID
        )
        vm?.configure(printerService: mockService)
        let weakVM = CanonicalOwnerWeakReference(vm)
        let waiter = vm?.beginCanonicalLoadForTesting()
        let caller = Task { await waiter?.wait() }
        await script.waitForCallCount(1)

        vm = nil
        XCTAssertNil(weakVM.value)
        await caller.value

        await gate.succeed(try TestData.decodePrinter())
    }

    func testOldSnapshotPollCannotPublishIntoReactivatedNewService() async throws {
        let oldPollGate = ShiftTaskResultGate<Data>()
        let initialData = Data([0x01])
        let staleData = Data([0x02])
        let oldSnapshotScript = ScriptedCanonicalResult<Data>([
            .value(initialData),
            .gated(oldPollGate)
        ])
        mockService.snapshotHandler = { _ in try await oldSnapshotScript.next() }
        mockService.printerToReturn = try TestData.decodePrinter(from: TestJSON.printerMinimal)
        mockService.cameraUrlToReturn = PrinterCameraUrl(
            streamUrl: nil,
            snapshotUrl: nil,
            accessMode: .snapshotOnly,
            streamFormat: .unknown,
            snapshotStrategy: .snapmakerU1MonitorJpeg
        )

        await viewModel.loadPrinter()
        await oldSnapshotScript.waitForCallCount(2)
        XCTAssertTrue(viewModel.isSnapshotPollingActive)
        XCTAssertEqual(viewModel.snapshotData, initialData)

        viewModel.setSnapshotPollingAllowed(false)
        viewModel.isViewActive = false
        let newService = MockPrinterService()
        newService.printerToReturn = try TestData.decodePrinter(from: TestJSON.printerMinimal)
        newService.cameraUrlToReturn = PrinterCameraUrl(
            streamUrl: nil,
            snapshotUrl: nil,
            accessMode: .snapshotOnly,
            streamFormat: .unknown,
            snapshotStrategy: .snapmakerU1MonitorJpeg
        )
        let newCanonicalData = Data([0x03])
        let newPollData = Data([0x04])
        let newPollGate = ShiftTaskResultGate<Data>()
        let newSnapshotScript = ScriptedCanonicalResult<Data>([
            .value(newCanonicalData),
            .gated(newPollGate)
        ])
        newService.snapshotHandler = { _ in try await newSnapshotScript.next() }
        viewModel.configure(printerService: newService)
        viewModel.isViewActive = true

        await viewModel.loadPrinter()
        XCTAssertEqual(viewModel.snapshotData, newCanonicalData)
        XCTAssertFalse(viewModel.isSnapshotPollingActive)

        await oldPollGate.succeed(staleData)
        await viewModel.waitForSnapshotRequestsToBecomeIdle()
        XCTAssertEqual(viewModel.snapshotData, newCanonicalData)
        XCTAssertFalse(viewModel.snapshotPublicationsForTesting.contains(staleData))

        viewModel.setSnapshotPollingAllowed(true)
        await newSnapshotScript.waitForCallCount(2)
        XCTAssertTrue(viewModel.isSnapshotPollingActive)
        await newPollGate.succeed(newPollData)
        await viewModel.waitForSnapshotRequestsToBecomeIdle()
        viewModel.stopSnapshotPolling()

        XCTAssertEqual(viewModel.snapshotData, newPollData)
        XCTAssertEqual(
            viewModel.snapshotPublicationsForTesting,
            [initialData, newCanonicalData, newPollData]
        )
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

    // MARK: - Filament Assignment (issue #2522, Vasquez review finding 7)

    /// `clearActiveSpoolAssignment()` must be assignment-only: it dispatches
    /// `setActiveSpool(spoolId: nil, ...)` and must NEVER also dispatch the
    /// physical `unloadFilament()` POST — that combined behavior belongs
    /// exclusively to `ejectFilament()`, reachable separately with distinct,
    /// truthful wording ("Eject Filament").
    func testClearActiveSpoolAssignmentClearsAssignmentWithoutPhysicalUnload() async throws {
        // Uses `printerMinimal`, not the default fixture: it carries a
        // non-empty `rowVersion`, required by `reviewedPrinterRowVersion()`
        // before either service call can dispatch.
        let printer = try TestData.decodePrinter(from: TestJSON.printerMinimal)
        mockService.printerToReturn = printer
        await viewModel.loadPrinter()

        await viewModel.clearActiveSpoolAssignment()

        guard let called = mockService.setActiveSpoolCalledWith else {
            XCTFail("setActiveSpool must be called")
            return
        }
        XCTAssertEqual(called.printerId, TestData.testUUID)
        XCTAssertNil(called.spoolId, "Assignment-only clear must pass a nil spoolId")
        XCTAssertNil(
            mockService.unloadFilamentCalledWith,
            "Assignment-only clear must never dispatch a physical unload"
        )
        XCTAssertNil(viewModel.actionError)
        XCTAssertFalse(viewModel.isPerformingAction)
    }

    /// `ejectFilament()` itself is unchanged and still performs the combined
    /// operation — this pins that its behavior did NOT silently change while
    /// `clearActiveSpoolAssignment()` was split out.
    func testEjectFilamentStillClearsAssignmentAndPhysicallyUnloads() async throws {
        let printer = try TestData.decodePrinter(from: TestJSON.printerMinimal)
        mockService.printerToReturn = printer
        await viewModel.loadPrinter()

        await viewModel.ejectFilament()

        guard let called = mockService.setActiveSpoolCalledWith else {
            XCTFail("setActiveSpool must be called")
            return
        }
        XCTAssertEqual(called.printerId, TestData.testUUID)
        XCTAssertNil(called.spoolId)
        XCTAssertEqual(
            mockService.unloadFilamentCalledWith,
            TestData.testUUID,
            "Eject must still dispatch the physical unload"
        )
    }

    /// Shared fixture for tests that need a printer starting WITH an active
    /// spool assignment (used by both `clearActiveSpoolAssignment` and
    /// `ejectFilament`'s stale-snapshot-override regression tests).
    private static let assignedPrinterJSON = """
    {
        "id": "660e8400-e29b-41d4-a716-446655440001",
        "rowVersion": "AQIDBA==",
        "name": "Ender 3",
        "backend": "Moonraker",
        "backendPort": 7125,
        "inMaintenance": false,
        "isEnabled": true,
        "isOnline": true,
        "spoolInfo": {
            "hasActiveSpool": true,
            "activeSpoolId": 42
        }
    }
    """

    /// Hicks review finding 14: a confirmed server-side clear must survive a
    /// FAILED post-clear `loadPrinter()` refresh. `loadPrinter()`'s failure
    /// path leaves `printer` untouched, so without an explicit local
    /// override the pre-clear snapshot's `spoolInfo.hasActiveSpool == true`
    /// would win in `effectiveSpoolInfo` and resurrect the very assignment
    /// the server just confirmed cleared.
    func testClearActiveSpoolAssignmentOverridesStalePrinterSnapshotWhenReloadFails() async throws {
        let printer = try TestData.decodePrinter(from: Self.assignedPrinterJSON)
        mockService.printerToReturn = printer
        await viewModel.loadPrinter()
        guard let before = viewModel.effectiveSpoolInfo, before.hasActiveSpool else {
            XCTFail("Setup: printer must start with an active spool assignment")
            return
        }

        // The reload `clearActiveSpoolAssignment()` triggers must fail,
        // while `setActiveSpool` itself still succeeds.
        mockService.getHandler = { _ in throw NetworkError.invalidResponse }

        await viewModel.clearActiveSpoolAssignment()

        guard let called = mockService.setActiveSpoolCalledWith else {
            XCTFail("setActiveSpool must still be called")
            return
        }
        XCTAssertNil(called.spoolId)
        XCTAssertNil(mockService.unloadFilamentCalledWith)
        XCTAssertFalse(
            viewModel.effectiveSpoolInfo?.hasActiveSpool ?? true,
            "A confirmed server-side clear must not be resurrected by a failed post-clear reload"
        )
    }

    // MARK: - Guided-swap toolhead bind retired-session protection
    // (issue #2522, Hicks review finding 20)

    private func makeSpool(id: Int = 42) -> SpoolmanSpool {
        SpoolmanSpool(
            id: id,
            name: "PLA Spool",
            material: "PLA",
            colorHex: "#000000",
            inUse: false,
            filamentName: nil,
            vendor: "TestVendor",
            registeredAt: nil,
            firstUsedAt: nil,
            lastUsedAt: nil,
            remainingWeightG: 750.0,
            initialWeightG: 1000.0,
            usedWeightG: 250.0,
            spoolWeightG: 200.0,
            remainingLengthMm: nil,
            usedLengthMm: nil,
            location: nil,
            lotNumber: nil,
            archived: false,
            price: nil,
            comment: nil,
            hasNfcTag: nil,
            usedPercent: nil,
            remainingPercent: nil
        )
    }

    /// Navigation-away: the view tears down (`isViewActive = false`, as
    /// `PrinterDetailView.onDisappear` does) WHILE the bind request is still
    /// in flight. The retired session must not still refresh the printer
    /// once the request completes, and the deactivation itself must clear
    /// `isPerformingAction` rather than leaving it stuck `true` forever on a
    /// view model nobody is observing anymore.
    func testBindToolheadSpoolSkipsRefreshAfterViewTornDownMidFlight() async throws {
        let printer = try TestData.decodePrinter(from: TestJSON.printerMinimal)
        mockService.printerToReturn = printer
        mockService.beforeBindToolheadSpool = { [weak viewModel] in
            await MainActor.run { viewModel?.isViewActive = false }
        }

        await viewModel.bindToolheadSpool(makeSpool(), at: 0)

        XCTAssertEqual(mockService.bindToolheadSpoolCalls.count, 1, "The in-flight request itself must still fire")
        XCTAssertEqual(
            mockService.getPrinterCallCount, 0,
            "A torn-down session must not refresh the printer after the bind completes"
        )
        XCTAssertFalse(viewModel.isPerformingAction)
    }

    /// ABA (issue #2522, Hicks review finding 24): the view deactivates
    /// THEN REACTIVATES — a materially different session — before the bind
    /// request's uncancellable network await resolves. A boolean-only
    /// `isViewActive` check would see `true` again at completion time and
    /// wrongly conclude this stale call still owns the current session;
    /// `actionLifecycleEpoch` (bumped on every activation-state transition)
    /// must still detect it and skip both the refresh and any busy-flag
    /// mutation — the reactivation transition itself is what already reset
    /// `isPerformingAction` for the NEW session, not this stale call's own
    /// tail check.
    func testBindToolheadSpoolSkipsRefreshAfterDeactivateReactivateABAMidFlight() async throws {
        let printer = try TestData.decodePrinter(from: TestJSON.printerMinimal)
        mockService.printerToReturn = printer
        mockService.beforeBindToolheadSpool = { [weak viewModel] in
            await MainActor.run {
                viewModel?.isViewActive = false
                viewModel?.isViewActive = true
            }
        }

        await viewModel.bindToolheadSpool(makeSpool(), at: 0)

        XCTAssertEqual(mockService.bindToolheadSpoolCalls.count, 1)
        XCTAssertEqual(
            mockService.getPrinterCallCount, 0,
            "A stale call surviving a deactivate/reactivate ABA cycle must not refresh the reactivated session"
        )
        XCTAssertFalse(
            viewModel.isPerformingAction,
            "The reactivated session must not be stranded with a busy flag the retired call no longer owns"
        )
    }

    /// Same-UUID/different-server: `configure(printerService:)` reassigns a
    /// DIFFERENT service instance for the SAME view (a supported, tested
    /// scenario elsewhere in this view model — server reconnect/hot-swap)
    /// WHILE the bind request against the OLD service is still in flight.
    /// The stale request's completion must not refresh through either the
    /// old (retired) or the newly reconfigured service, and the
    /// reconfiguration itself must clear `isPerformingAction` for the new
    /// session rather than leaving it stuck `true` (issue #2522, Hicks
    /// review finding 24 — "service replacement busy cleanup").
    func testBindToolheadSpoolSkipsRefreshAfterServiceReconfiguredMidFlight() async throws {
        let printer = try TestData.decodePrinter(from: TestJSON.printerMinimal)
        mockService.printerToReturn = printer
        let newService = MockPrinterService()
        newService.printerToReturn = printer
        mockService.beforeBindToolheadSpool = { [weak viewModel] in
            await MainActor.run { viewModel?.configure(printerService: newService) }
        }

        await viewModel.bindToolheadSpool(makeSpool(), at: 0)

        XCTAssertEqual(mockService.bindToolheadSpoolCalls.count, 1)
        XCTAssertEqual(
            mockService.getPrinterCallCount, 0,
            "The retired old service must not be refreshed after a mid-flight reconfigure"
        )
        XCTAssertEqual(
            newService.getPrinterCallCount, 0,
            "The new service must not be refreshed on behalf of an operation it never targeted"
        )
        XCTAssertFalse(
            viewModel.isPerformingAction,
            "The reconfigured session must not be stranded with a busy flag the retired call no longer owns"
        )
    }

    // MARK: - Generalized action authority: sibling actions, concurrency,
    // ABA (Frost directive following Hicks/Bishop review findings 24/25)
    //
    // `bindToolheadSpool`'s `ActionAuthority` (epoch + service identity,
    // `defer`-based busy cleanup) is now the SHARED implementation every
    // `printerService`-dispatching, `isPerformingAction`-tracking sibling
    // action uses: `ejectFilament`, `clearActiveSpoolAssignment`,
    // `setActiveSpool`, `loadSpoolById` (private, exercised via NFC scan),
    // and `performAction` (pause/resume/cancel/stop/emergencyStop). The two
    // tests below prove the generalization on a DIFFERENT sibling action
    // than the ones already covered above, and prove the specific hazard a
    // single-action epoch check cannot by itself rule out: two sibling
    // actions racing concurrently must not clobber each other's busy state.

    /// ABA on a sibling action, not `bindToolheadSpool`: `setActiveSpool`
    /// deactivates then REACTIVATES before its network await resolves.
    func testSetActiveSpoolSkipsRefreshAfterDeactivateReactivateABAMidFlight() async throws {
        let printer = try TestData.decodePrinter(from: TestJSON.printerMinimal)
        mockService.printerToReturn = printer
        // `setActiveSpool` requires `viewModel.printer` to already carry a
        // non-empty `rowVersion` (via `reviewedPrinterRowVersion()`) before
        // it can dispatch at all — without this, the ABA hook below would
        // never fire.
        await viewModel.loadPrinter()
        let baselineGetPrinterCallCount = mockService.getPrinterCallCount

        mockService.beforeSetActiveSpool = { [weak viewModel] in
            await MainActor.run {
                viewModel?.isViewActive = false
                viewModel?.isViewActive = true
            }
        }

        await viewModel.setActiveSpool(makeSpool())

        XCTAssertNotNil(mockService.setActiveSpoolCalledWith, "The in-flight request itself must still fire")
        XCTAssertEqual(
            mockService.getPrinterCallCount, baselineGetPrinterCallCount,
            "A stale call surviving a deactivate/reactivate ABA cycle must not refresh the reactivated session"
        )
        XCTAssertFalse(
            viewModel.isPerformingAction,
            "The reactivated session must not be stranded with a busy flag the retired call no longer owns"
        )
    }

    /// Concurrent sibling actions: `bindToolheadSpool` (operation A, against
    /// the OLD service) is still in flight when a `configure(printerService:)`
    /// hot-swap occurs — a lifecycle event unrelated to A itself — and
    /// `setActiveSpool` (operation B, against the NEW service) starts and is
    /// ALSO still in flight when A's stale, now-authority-less network call
    /// finally resolves. A's stale completion must not clear
    /// `isPerformingAction` out from under B, which still legitimately owns
    /// it; only B's own completion may do so.
    func testStaleOperationCompletionDoesNotClobberConcurrentSiblingActionsBusyState() async throws {
        let printer = try TestData.decodePrinter(from: TestJSON.printerMinimal)
        mockService.printerToReturn = printer
        let newService = MockPrinterService()
        newService.printerToReturn = printer

        // `setActiveSpool` (operation B, below) requires `viewModel.printer`
        // to already carry a non-empty `rowVersion` before it can dispatch
        // at all — without this, B's mock hook would never fire and this
        // test would hang forever waiting on `bEntered`.
        await viewModel.loadPrinter()
        let baselineOldServiceGetPrinterCallCount = mockService.getPrinterCallCount

        let aEntered = ShiftTaskResultGate<Void>()
        let aRelease = ShiftTaskResultGate<Void>()
        mockService.beforeBindToolheadSpool = {
            await aEntered.succeed(())
            _ = try? await aRelease.wait()
        }

        let aTask = Task { await viewModel.bindToolheadSpool(makeSpool(id: 1), at: 0) }
        _ = try await aEntered.wait()
        XCTAssertTrue(viewModel.isPerformingAction, "Setup: operation A must be genuinely in flight")

        // An unrelated lifecycle event: the server is hot-swapped WHILE A is
        // still suspended in its network await. This revokes A's RESULT/
        // REFRESH authority, but must NOT touch busy-state tracking — A's
        // own token remains held until A's own completion releases it, so
        // busy correctly stays `true` straight through this reconfigure
        // (a later Hicks review round: an earlier revision force-cleared
        // the shared boolean here, which is exactly the same hazard the
        // per-operation token set now replaces).
        viewModel.configure(printerService: newService)
        XCTAssertTrue(
            viewModel.isPerformingAction,
            "A's own outstanding token must keep busy true across an unrelated reconfigure, not be reset by the transition itself"
        )

        let bEntered = ShiftTaskResultGate<Void>()
        let bRelease = ShiftTaskResultGate<Void>()
        newService.beforeSetActiveSpool = {
            await bEntered.succeed(())
            _ = try? await bRelease.wait()
        }

        let bTask = Task { await viewModel.setActiveSpool(makeSpool(id: 2)) }
        _ = try await bEntered.wait()
        XCTAssertTrue(viewModel.isPerformingAction, "Setup: operation B must be genuinely in flight")

        // Release A. Its stale completion must detect it lost RESULT/REFRESH
        // authority (the epoch moved on when B's host service was
        // reconfigured above) and skip refreshing — but releasing A's OWN
        // token must not clear B's separate token.
        await aRelease.succeed(())
        await aTask.value
        XCTAssertTrue(
            viewModel.isPerformingAction,
            "Operation A's own token release must not clobber operation B's separate token while B is still genuinely in flight"
        )
        XCTAssertEqual(
            mockService.getPrinterCallCount, baselineOldServiceGetPrinterCallCount,
            "Operation A must not refresh through the retired old service"
        )

        // Release B. Its own, still-current completion must clear the flag.
        await bRelease.succeed(())
        await bTask.value
        XCTAssertFalse(
            viewModel.isPerformingAction,
            "Operation B's own legitimate completion must clear the busy flag it still owns"
        )
        XCTAssertEqual(newService.getPrinterCallCount, 1, "Operation B must refresh through the service it actually targeted")
    }

    /// Same-service, SAME epoch Emergency Stop overlap (Hicks review
    /// finding: per-operation busy identity) — completion order 1: the
    /// ordinary filament action (`setActiveSpool`) finishes FIRST while
    /// Emergency Stop is still in flight. Deliberately does NOT reconfigure
    /// or deactivate between the two: `ActionAuthority`'s epoch/service
    /// identity are IDENTICAL for both calls, so only the per-operation
    /// token set can tell them apart. `setActiveSpool`'s own completion
    /// must not clear busy while Emergency Stop — a materially different,
    /// still-outstanding operation against the exact same session — is
    /// still genuinely in flight.
    func testSameServiceEmergencyStopOverlapFilamentActionCompletesFirstKeepsBusyUntilEmergencyStopCompletes() async throws {
        let printer = try TestData.decodePrinter(from: TestJSON.printerMinimal)
        mockService.printerToReturn = printer
        await viewModel.loadPrinter()

        let spoolEntered = ShiftTaskResultGate<Void>()
        let spoolRelease = ShiftTaskResultGate<Void>()
        mockService.beforeSetActiveSpool = {
            await spoolEntered.succeed(())
            _ = try? await spoolRelease.wait()
        }
        let spoolTask = Task { await viewModel.setActiveSpool(makeSpool(id: 1)) }
        _ = try await spoolEntered.wait()
        XCTAssertTrue(viewModel.isPerformingAction, "Setup: the filament action must be genuinely in flight")

        let stopEntered = ShiftTaskResultGate<Void>()
        let stopRelease = ShiftTaskResultGate<Void>()
        mockService.beforeEmergencyStop = {
            await stopEntered.succeed(())
            _ = try? await stopRelease.wait()
        }
        viewModel.requestEmergencyStop()
        let stopTask = Task { await viewModel.confirmAction() }
        _ = try await stopEntered.wait()
        XCTAssertTrue(
            viewModel.isPerformingAction,
            "Setup: Emergency Stop must ALSO be genuinely in flight, overlapping the filament action"
        )

        // Complete the filament action FIRST. Ordinary/filament actions and
        // Emergency Stop must both stay disabled (busy) throughout, since
        // Emergency Stop is still outstanding.
        await spoolRelease.succeed(())
        await spoolTask.value
        XCTAssertTrue(
            viewModel.isPerformingAction,
            "The filament action's own completion must not clear busy while Emergency Stop is still genuinely in flight"
        )

        // Complete Emergency Stop. Only NOW may busy clear.
        await stopRelease.succeed(())
        await stopTask.value
        XCTAssertFalse(
            viewModel.isPerformingAction,
            "Busy must clear once every overlapping operation has completed"
        )
    }

    /// Same scenario, REVERSED completion order: Emergency Stop finishes
    /// FIRST while the ordinary filament action is still in flight.
    func testSameServiceEmergencyStopOverlapEmergencyStopCompletesFirstKeepsBusyUntilFilamentActionCompletes() async throws {
        let printer = try TestData.decodePrinter(from: TestJSON.printerMinimal)
        mockService.printerToReturn = printer
        await viewModel.loadPrinter()

        let spoolEntered = ShiftTaskResultGate<Void>()
        let spoolRelease = ShiftTaskResultGate<Void>()
        mockService.beforeSetActiveSpool = {
            await spoolEntered.succeed(())
            _ = try? await spoolRelease.wait()
        }
        let spoolTask = Task { await viewModel.setActiveSpool(makeSpool(id: 1)) }
        _ = try await spoolEntered.wait()
        XCTAssertTrue(viewModel.isPerformingAction, "Setup: the filament action must be genuinely in flight")

        let stopEntered = ShiftTaskResultGate<Void>()
        let stopRelease = ShiftTaskResultGate<Void>()
        mockService.beforeEmergencyStop = {
            await stopEntered.succeed(())
            _ = try? await stopRelease.wait()
        }
        viewModel.requestEmergencyStop()
        let stopTask = Task { await viewModel.confirmAction() }
        _ = try await stopEntered.wait()
        XCTAssertTrue(
            viewModel.isPerformingAction,
            "Setup: Emergency Stop must ALSO be genuinely in flight, overlapping the filament action"
        )

        // Complete Emergency Stop FIRST this time. Busy must stay true: the
        // filament action is still genuinely outstanding.
        await stopRelease.succeed(())
        await stopTask.value
        XCTAssertTrue(
            viewModel.isPerformingAction,
            "Emergency Stop's own completion must not clear busy while the filament action is still genuinely in flight"
        )

        // Complete the filament action. Only NOW may busy clear.
        await spoolRelease.succeed(())
        await spoolTask.value
        XCTAssertFalse(
            viewModel.isPerformingAction,
            "Busy must clear once every overlapping operation has completed"
        )
    }

    /// Bishop review: same-service, same-epoch Pause + Emergency Stop
    /// overlap — a DIFFERENT `performAction`-routed pair than the filament-
    /// action tests above, both dispatched via `performAction` itself.
    /// Completion order 1: Pause finishes FIRST while Emergency Stop is
    /// still in flight.
    func testSameServicePauseAndEmergencyStopOverlapPauseCompletesFirstKeepsBusyUntilEmergencyStopCompletes() async throws {
        let printer = try TestData.decodePrinter(from: TestJSON.printerMinimal)
        mockService.printerToReturn = printer
        await viewModel.loadPrinter()

        let pauseEntered = ShiftTaskResultGate<Void>()
        let pauseRelease = ShiftTaskResultGate<Void>()
        mockService.beforePause = {
            await pauseEntered.succeed(())
            _ = try? await pauseRelease.wait()
        }
        let pauseTask = Task { await viewModel.pausePrinter() }
        _ = try await pauseEntered.wait()
        XCTAssertTrue(viewModel.isPerformingAction, "Setup: Pause must be genuinely in flight")

        let stopEntered = ShiftTaskResultGate<Void>()
        let stopRelease = ShiftTaskResultGate<Void>()
        mockService.beforeEmergencyStop = {
            await stopEntered.succeed(())
            _ = try? await stopRelease.wait()
        }
        viewModel.requestEmergencyStop()
        let stopTask = Task { await viewModel.confirmAction() }
        _ = try await stopEntered.wait()
        XCTAssertTrue(
            viewModel.isPerformingAction,
            "Setup: Emergency Stop must ALSO be genuinely in flight, overlapping Pause"
        )

        await pauseRelease.succeed(())
        await pauseTask.value
        XCTAssertTrue(
            viewModel.isPerformingAction,
            "Pause's own completion must not clear busy while Emergency Stop is still genuinely in flight"
        )

        await stopRelease.succeed(())
        await stopTask.value
        XCTAssertFalse(
            viewModel.isPerformingAction,
            "Busy must clear once every overlapping operation has completed"
        )
    }

    /// Same scenario, REVERSED completion order: Emergency Stop finishes
    /// FIRST while Pause is still in flight.
    func testSameServicePauseAndEmergencyStopOverlapEmergencyStopCompletesFirstKeepsBusyUntilPauseCompletes() async throws {
        let printer = try TestData.decodePrinter(from: TestJSON.printerMinimal)
        mockService.printerToReturn = printer
        await viewModel.loadPrinter()

        let pauseEntered = ShiftTaskResultGate<Void>()
        let pauseRelease = ShiftTaskResultGate<Void>()
        mockService.beforePause = {
            await pauseEntered.succeed(())
            _ = try? await pauseRelease.wait()
        }
        let pauseTask = Task { await viewModel.pausePrinter() }
        _ = try await pauseEntered.wait()
        XCTAssertTrue(viewModel.isPerformingAction, "Setup: Pause must be genuinely in flight")

        let stopEntered = ShiftTaskResultGate<Void>()
        let stopRelease = ShiftTaskResultGate<Void>()
        mockService.beforeEmergencyStop = {
            await stopEntered.succeed(())
            _ = try? await stopRelease.wait()
        }
        viewModel.requestEmergencyStop()
        let stopTask = Task { await viewModel.confirmAction() }
        _ = try await stopEntered.wait()
        XCTAssertTrue(
            viewModel.isPerformingAction,
            "Setup: Emergency Stop must ALSO be genuinely in flight, overlapping Pause"
        )

        await stopRelease.succeed(())
        await stopTask.value
        XCTAssertTrue(
            viewModel.isPerformingAction,
            "Emergency Stop's own completion must not clear busy while Pause is still genuinely in flight"
        )

        await pauseRelease.succeed(())
        await pauseTask.value
        XCTAssertFalse(
            viewModel.isPerformingAction,
            "Busy must clear once every overlapping operation has completed"
        )
    }

    // MARK: - Eject physical-unload safety (Bishop review finding)

    /// The physical unload leg of `ejectFilament()` must ALWAYS complete
    /// once the assignment-clear leg has already succeeded, even if this
    /// call's result/refresh authority is lost BETWEEN the two legs (a
    /// deactivate/reactivate ABA cycle here) — never silently skipped.
    func testEjectFilamentCompletesPhysicalUnloadEvenAfterAuthorityLostBetweenLegs() async throws {
        let printer = try TestData.decodePrinter(from: TestJSON.printerMinimal)
        mockService.printerToReturn = printer
        // `ejectFilament`'s first leg requires `viewModel.printer` to
        // already carry a non-empty `rowVersion` before it can dispatch at
        // all — without this it throws before the mock hook below ever
        // fires.
        await viewModel.loadPrinter()
        mockService.beforeUnloadFilament = { [weak viewModel] in
            await MainActor.run {
                viewModel?.isViewActive = false
                viewModel?.isViewActive = true
            }
        }

        await viewModel.ejectFilament()

        XCTAssertNotNil(mockService.setActiveSpoolCalledWith, "The assignment-clear leg must have fired")
        XCTAssertEqual(
            mockService.unloadFilamentCalledWith, TestData.testUUID,
            "The physical unload leg must still complete despite authority loss between the two legs"
        )
    }

    /// If the physical unload leg itself fails AFTER the assignment was
    /// already cleared, that must be surfaced as an explicit error — never
    /// silently swallowed — regardless of this call's own result/refresh
    /// authority.
    func testEjectFilamentSurfacesExplicitErrorWhenPhysicalUnloadFailsAfterAssignmentCleared() async throws {
        let printer = try TestData.decodePrinter(from: TestJSON.printerMinimal)
        mockService.printerToReturn = printer
        await viewModel.loadPrinter()
        mockService.unloadFilamentErrorToThrow = NetworkError.invalidResponse

        await viewModel.ejectFilament()

        XCTAssertNotNil(mockService.setActiveSpoolCalledWith, "The assignment-clear leg must have fired and succeeded")
        XCTAssertEqual(mockService.unloadFilamentCalledWith, TestData.testUUID, "The physical unload leg must still have been attempted")
        let error = try XCTUnwrap(viewModel.actionError)
        XCTAssertTrue(
            error.contains("assignment cleared"),
            "The error must explicitly say the assignment was already cleared, not just that the unload failed: \(error)"
        )
    }

    /// Safety precedence (issue #2522, Vasquez review finding — CRITICAL):
    /// Emergency Stop, engaged BETWEEN eject's two legs (right as the
    /// assignment-clear leg resolves, simulated via the mock hook below,
    /// before the physical-unload leg would otherwise dispatch), must
    /// preempt the physical unload rather than letting it proceed — never
    /// silently: the operator must still see an explicit message.
    func testEjectFilamentSkipsPhysicalUnloadWhenEmergencyStopEngagedBetweenLegs() async throws {
        let printer = try TestData.decodePrinter(from: TestJSON.printerMinimal)
        mockService.printerToReturn = printer
        await viewModel.loadPrinter()
        mockService.beforeSetActiveSpool = { [weak viewModel] in
            await MainActor.run { viewModel?.engageEmergencyStopSafetyOverrideForTesting() }
        }

        await viewModel.ejectFilament()

        XCTAssertNotNil(mockService.setActiveSpoolCalledWith, "The assignment-clear leg must still have fired and succeeded")
        XCTAssertNil(
            mockService.unloadFilamentCalledWith,
            "The physical unload must NOT be sent once Emergency Stop has been engaged"
        )
        let error = try XCTUnwrap(viewModel.actionError)
        XCTAssertTrue(
            error.contains("Emergency Stop"),
            "The error must explicitly name Emergency Stop as the reason the unload was withheld, not silently return: \(error)"
        )
    }

    /// Bishop review finding (parity with `clearActiveSpoolAssignment()`'s
    /// own Hicks review finding 14 fix): the assignment is ALREADY cleared
    /// server-side once the first leg succeeds, so the local
    /// `printer.spoolInfo` snapshot must be reconciled immediately — before
    /// Emergency Stop preempts the physical unload — not left showing an
    /// assigned spool the server no longer has recorded.
    func testEjectFilamentOverridesStalePrinterSnapshotWhenEmergencyStopPreemptsUnload() async throws {
        let printer = try TestData.decodePrinter(from: Self.assignedPrinterJSON)
        mockService.printerToReturn = printer
        await viewModel.loadPrinter()
        guard let before = viewModel.effectiveSpoolInfo, before.hasActiveSpool else {
            XCTFail("Setup: printer must start with an active spool assignment")
            return
        }
        mockService.beforeSetActiveSpool = { [weak viewModel] in
            await MainActor.run { viewModel?.engageEmergencyStopSafetyOverrideForTesting() }
        }

        await viewModel.ejectFilament()

        XCTAssertFalse(
            viewModel.effectiveSpoolInfo?.hasActiveSpool ?? true,
            "The local snapshot must show the assignment cleared even though Emergency Stop preempted the physical unload"
        )
    }

    /// Same parity fix, different early-return path: the physical unload
    /// leg itself fails AFTER the assignment was already cleared. The local
    /// snapshot must still be reconciled, not left showing a stale
    /// assignment.
    func testEjectFilamentOverridesStalePrinterSnapshotWhenPhysicalUnloadFails() async throws {
        let printer = try TestData.decodePrinter(from: Self.assignedPrinterJSON)
        mockService.printerToReturn = printer
        await viewModel.loadPrinter()
        guard let before = viewModel.effectiveSpoolInfo, before.hasActiveSpool else {
            XCTFail("Setup: printer must start with an active spool assignment")
            return
        }
        mockService.unloadFilamentErrorToThrow = NetworkError.invalidResponse

        await viewModel.ejectFilament()

        XCTAssertFalse(
            viewModel.effectiveSpoolInfo?.hasActiveSpool ?? true,
            "The local snapshot must show the assignment cleared even though the physical unload leg failed"
        )
    }

    // MARK: - actionError Observability (Hicks review finding — the
    // #2400 `@Observable` trap)

    /// `operationErrorMessages`/`operationErrorOrder` (which
    /// `actionError`'s computed getter reads) must NOT be
    /// `@ObservationIgnored`, or SwiftUI would never re-evaluate
    /// `actionError` on a path that mutates nothing else observable
    /// alongside it. `markPrinterReady()`'s missing-status early return is
    /// exactly such a path: it sets an error and returns BEFORE ever
    /// calling `beginBusyToken()`, so `activeActionTokens` — which IS
    /// tracked, and whose own mutation is what incidentally masked this bug
    /// on every OTHER sibling action's error path — never changes here at
    /// all. The failing pass leaves every other observable property
    /// untouched, so the invalidation below can only have come from the
    /// error storage itself.
    func testActionErrorObservationInvalidatesOnSetWithoutAnyBusyTokenChange() async throws {
        let invalidated = expectation(description: "actionError observation fired on set")
        withObservationTracking {
            _ = viewModel.actionError
        } onChange: {
            invalidated.fulfill()
        }

        await viewModel.markPrinterReady()

        await fulfillment(of: [invalidated], timeout: 2)
        XCTAssertEqual(viewModel.actionError, "Refresh the auto-dispatch status before confirming.")
    }

    /// Same trap, the clearing direction: dismissing the alert
    /// (`viewModel.actionError = nil`) must also invalidate Observation.
    func testActionErrorObservationInvalidatesOnClear() async throws {
        await viewModel.markPrinterReady()
        XCTAssertNotNil(viewModel.actionError, "precondition: an error is already recorded")

        let invalidated = expectation(description: "actionError observation fired on clear")
        withObservationTracking {
            _ = viewModel.actionError
        } onChange: {
            invalidated.fulfill()
        }

        viewModel.actionError = nil

        await fulfillment(of: [invalidated], timeout: 2)
        XCTAssertNil(viewModel.actionError)
    }

    // MARK: - Concurrent error preservation (Hicks review finding: a single
    // shared `actionError` let two concurrent operations silently clobber
    // each other's error message)

    /// Both eject AND Emergency Stop are genuinely concurrent and BOTH
    /// fail. Completion order 1: eject completes FIRST. Emergency Stop's
    /// own error, set when it completes SECOND, must not be lost, and it
    /// must not clobber eject's own error either.
    func testConcurrentFailingEjectAndEmergencyStopPreserveBothErrorsWhenEjectCompletesFirst() async throws {
        let printer = try TestData.decodePrinter(from: TestJSON.printerMinimal)
        mockService.printerToReturn = printer
        await viewModel.loadPrinter()
        // Eject's first leg (`setActiveSpool`) fails via the shared
        // `errorToThrow`; Emergency Stop fails via its OWN dedicated error
        // so the two produce DISTINGUISHABLE messages.
        mockService.errorToThrow = NetworkError.invalidResponse
        mockService.emergencyStopErrorToThrow = ShiftTaskProofError.forced("Emergency Stop rejected by printer")

        let ejectEntered = ShiftTaskResultGate<Void>()
        let ejectRelease = ShiftTaskResultGate<Void>()
        mockService.beforeSetActiveSpool = {
            await ejectEntered.succeed(())
            _ = try? await ejectRelease.wait()
        }
        let ejectTask = Task { await viewModel.ejectFilament() }
        _ = try await ejectEntered.wait()

        let stopEntered = ShiftTaskResultGate<Void>()
        let stopRelease = ShiftTaskResultGate<Void>()
        mockService.beforeEmergencyStop = {
            await stopEntered.succeed(())
            _ = try? await stopRelease.wait()
        }
        viewModel.requestEmergencyStop()
        let stopTask = Task { await viewModel.confirmAction() }
        _ = try await stopEntered.wait()

        // Eject completes FIRST.
        await ejectRelease.succeed(())
        await ejectTask.value
        let afterEject = try XCTUnwrap(viewModel.actionError)
        XCTAssertTrue(
            afterEject.contains("Invalid server response"),
            "Eject's own error must be visible immediately after it completes: \(afterEject)"
        )

        // Emergency Stop completes SECOND — must not clobber eject's error.
        await stopRelease.succeed(())
        await stopTask.value
        let combined = try XCTUnwrap(viewModel.actionError)
        XCTAssertTrue(
            combined.contains("Invalid server response"),
            "Eject's error must survive Emergency Stop completing second: \(combined)"
        )
        XCTAssertTrue(
            combined.contains("Emergency Stop rejected by printer"),
            "Emergency Stop's own error must also be present, not lost: \(combined)"
        )
    }

    /// Same scenario, REVERSED completion order: Emergency Stop completes
    /// FIRST. Eject's own error, set when it completes SECOND, must not be
    /// lost, and it must not clobber Emergency Stop's error either.
    func testConcurrentFailingEjectAndEmergencyStopPreserveBothErrorsWhenEmergencyStopCompletesFirst() async throws {
        let printer = try TestData.decodePrinter(from: TestJSON.printerMinimal)
        mockService.printerToReturn = printer
        await viewModel.loadPrinter()
        mockService.errorToThrow = NetworkError.invalidResponse
        mockService.emergencyStopErrorToThrow = ShiftTaskProofError.forced("Emergency Stop rejected by printer")

        let ejectEntered = ShiftTaskResultGate<Void>()
        let ejectRelease = ShiftTaskResultGate<Void>()
        mockService.beforeSetActiveSpool = {
            await ejectEntered.succeed(())
            _ = try? await ejectRelease.wait()
        }
        let ejectTask = Task { await viewModel.ejectFilament() }
        _ = try await ejectEntered.wait()

        let stopEntered = ShiftTaskResultGate<Void>()
        let stopRelease = ShiftTaskResultGate<Void>()
        mockService.beforeEmergencyStop = {
            await stopEntered.succeed(())
            _ = try? await stopRelease.wait()
        }
        viewModel.requestEmergencyStop()
        let stopTask = Task { await viewModel.confirmAction() }
        _ = try await stopEntered.wait()

        // Emergency Stop completes FIRST this time.
        await stopRelease.succeed(())
        await stopTask.value
        let afterStop = try XCTUnwrap(viewModel.actionError)
        XCTAssertTrue(
            afterStop.contains("Emergency Stop rejected by printer"),
            "Emergency Stop's own error must be visible immediately after it completes: \(afterStop)"
        )

        // Eject completes SECOND — must not clobber Emergency Stop's error.
        await ejectRelease.succeed(())
        await ejectTask.value
        let combined = try XCTUnwrap(viewModel.actionError)
        XCTAssertTrue(
            combined.contains("Emergency Stop rejected by printer"),
            "Emergency Stop's error must survive eject completing second: \(combined)"
        )
        XCTAssertTrue(
            combined.contains("Invalid server response"),
            "Eject's own error must also be present, not lost: \(combined)"
        )
    }

    // MARK: - Pull-to-refresh transition-during-refresh (issue #2522, Hicks
    // review finding 23)

    /// `PrinterDetailViewLifecycle.refresh` must re-evaluate its caller's
    /// CURRENT page/scene state when it finally applies
    /// `setSnapshotPollingAllowed`, not a value captured before its
    /// `loadPrinter()` await — a pull-to-refresh can leave that await in
    /// flight for a while, long enough for the operator to switch from the
    /// Status page (foreground for the camera) to Controls (not) or back.
    func testRefreshAppliesSnapshotPollingStateAsOfCompletionNotAsOfStart() async throws {
        let printer = try TestData.decodePrinter(from: TestJSON.printerMinimal)
        let gate = ShiftTaskResultGate<Printer>()
        let script = ScriptedCanonicalResult<Printer>([.gated(gate)])
        mockService.getHandler = { _ in try await script.next() }
        let coverageViewModel = PrinterFilamentCoverageViewModel(printerId: TestData.testUUID)

        // Mirrors `PrinterDetailView.isStatusPageForeground` at the moment
        // refresh STARTS: the Status page is foreground, so polling should
        // resume once refresh concludes — UNLESS the operator switches away
        // before it does, below.
        var isStatusPageForegroundNow = true

        let refreshTask = Task {
            await PrinterDetailViewLifecycle.refresh(
                viewModel: viewModel,
                coverageViewModel: coverageViewModel,
                refreshCoverage: false,
                snapshotPollingAllowed: { isStatusPageForegroundNow }
            )
        }
        await script.waitForCallCount(1)

        // The operator switches to the Controls page while `loadPrinter()`'s
        // network request is still in flight.
        isStatusPageForegroundNow = false

        await gate.succeed(printer)
        await refreshTask.value

        XCTAssertFalse(
            viewModel.isSnapshotPollingActive,
            "Refresh must apply the CURRENT (post-switch) page state, not the state captured when refresh started"
        )
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
            priority: .normal,
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
