import XCTest
@testable import PrintFarmer

/// Tests for PrinterListViewModel: loading, error handling, filtering,
/// and search using MockPrinterService via configure() DI pattern.
@MainActor
final class PrinterListViewModelTests: XCTestCase {

    private var mockService: MockPrinterService!
    private var mockAutoDispatchService: MockAutoDispatchService!
    private var viewModel: PrinterListViewModel!

    override func setUp() async throws {
        try await super.setUp()
        mockService = MockPrinterService()
        mockAutoDispatchService = MockAutoDispatchService()
        viewModel = PrinterListViewModel()
        viewModel.configure(printerService: mockService, autoPrintService: mockAutoDispatchService)
    }

    override func tearDown() async throws {
        viewModel = nil
        mockAutoDispatchService = nil
        mockService = nil
        try await super.tearDown()
    }

    // MARK: - Initial State

    func testInitialState() {
        XCTAssertTrue(viewModel.printers.isEmpty)
        XCTAssertFalse(viewModel.isLoading)
        XCTAssertNil(viewModel.errorMessage)
        XCTAssertEqual(viewModel.searchText, "")
        XCTAssertEqual(viewModel.selectedStatus, .all)
    }

    func testReconnectRecoveryRefreshesCanonicalListOnceAndFencesStaleService() async throws {
        let callbackQueue = ShiftTaskCallbackQueue()
        let oldPrinterService = MockPrinterService()
        oldPrinterService.printersToReturn = [try TestData.decodePrinter()]
        let oldSignalR = MockSignalRService()
        let currentPrinterService = MockPrinterService()
        currentPrinterService.printersToReturn = [
            try TestData.decodePrinter(from: TestJSON.printerMinimal)
        ]
        let currentSignalR = MockSignalRService()
        let vm = PrinterListViewModel(callbackEnqueuer: callbackQueue.enqueuer)
        vm.configure(
            printerService: oldPrinterService,
            autoPrintService: mockAutoDispatchService
        )
        vm.configureSignalR(oldSignalR)

        oldSignalR.simulateConnectionStateChange(.connected)
        await callbackQueue.runNext()
        XCTAssertEqual(oldPrinterService.listPrintersCallCount, 0)
        oldSignalR.simulateConnectionStateChange(.reconnecting)
        await callbackQueue.runNext()
        XCTAssertEqual(oldPrinterService.listPrintersCallCount, 0)
        oldSignalR.simulateConnectionStateChange(.connected)
        await callbackQueue.runNext()
        await vm.waitForCanonicalLoadToBecomeIdle()
        XCTAssertEqual(oldPrinterService.listPrintersCallCount, 1)
        XCTAssertEqual(vm.printers.first?.name, "Prusa MK4")

        oldSignalR.simulateConnectionStateChange(.connected)
        XCTAssertEqual(callbackQueue.count, 0)
        XCTAssertEqual(oldPrinterService.listPrintersCallCount, 1)

        vm.configure(
            printerService: currentPrinterService,
            autoPrintService: mockAutoDispatchService
        )
        vm.configureSignalR(currentSignalR)
        oldSignalR.simulateCapturedConnectionStateChange(at: 0, state: .reconnecting)
        oldSignalR.simulateCapturedConnectionStateChange(at: 0, state: .connected)
        XCTAssertEqual(callbackQueue.count, 2)
        await callbackQueue.runNext()
        await callbackQueue.runNext()
        XCTAssertEqual(oldPrinterService.listPrintersCallCount, 1)
        XCTAssertEqual(currentPrinterService.listPrintersCallCount, 0)

        currentSignalR.simulateConnectionStateChange(.connected)
        await callbackQueue.runNext()
        currentSignalR.simulateConnectionStateChange(.reconnecting)
        await callbackQueue.runNext()
        XCTAssertEqual(currentPrinterService.listPrintersCallCount, 0)
        currentSignalR.simulateConnectionStateChange(.connected)
        await callbackQueue.runNext()
        await vm.waitForCanonicalLoadToBecomeIdle()

        XCTAssertEqual(currentPrinterService.listPrintersCallCount, 1)
        XCTAssertEqual(vm.printers.first?.name, "Ender 3")
        XCTAssertEqual(callbackQueue.count, 0)
    }

    func testCanonicalListRejectsReconfiguredInFlightData() async throws {
        let oldGate = ShiftTaskResultGate<[Printer]>()
        let oldScript = ScriptedCanonicalResult<[Printer]>([.gated(oldGate)])
        mockService.listHandler = { _ in try await oldScript.next() }
        let currentService = MockPrinterService()
        currentService.printersToReturn = [
            try TestData.decodePrinter(from: TestJSON.printerMinimal)
        ]

        let oldRequest = Task { await viewModel.loadPrinters() }
        await oldScript.waitForCallCount(1)
        viewModel.configure(
            printerService: currentService,
            autoPrintService: mockAutoDispatchService
        )
        await viewModel.loadPrinters()
        XCTAssertEqual(viewModel.printers.first?.name, "Ender 3")
        XCTAssertNil(viewModel.errorMessage)
        XCTAssertFalse(viewModel.isLoading)

        await oldGate.succeed([try TestData.decodePrinter()])
        await viewModel.waitForSupersededCanonicalLoads()
        await oldRequest.value

        XCTAssertEqual(viewModel.printers.first?.name, "Ender 3")
        XCTAssertNil(viewModel.errorMessage)
        XCTAssertFalse(viewModel.isLoading)
    }

    func testCanonicalListRejectsReconfiguredInFlightError() async throws {
        let oldGate = ShiftTaskResultGate<[Printer]>()
        let oldScript = ScriptedCanonicalResult<[Printer]>([.gated(oldGate)])
        mockService.listHandler = { _ in try await oldScript.next() }
        let currentService = MockPrinterService()
        currentService.printersToReturn = [
            try TestData.decodePrinter(from: TestJSON.printerMinimal)
        ]

        let oldRequest = Task { await viewModel.loadPrinters() }
        await oldScript.waitForCallCount(1)
        viewModel.configure(
            printerService: currentService,
            autoPrintService: mockAutoDispatchService
        )
        await viewModel.loadPrinters()
        await oldGate.fail(.forced("stale list failure"))
        await viewModel.waitForSupersededCanonicalLoads()
        await oldRequest.value

        XCTAssertEqual(viewModel.printers.first?.name, "Ender 3")
        XCTAssertNil(viewModel.errorMessage)
        XCTAssertFalse(viewModel.isLoading)
    }

    func testManualListRefreshCoalescesReconnectIntoOneAuthoritativeFollowUp() async throws {
        let callbackQueue = ShiftTaskCallbackQueue()
        let firstGate = ShiftTaskResultGate<[Printer]>()
        let stale = [try TestData.decodePrinter()]
        let current = [try TestData.decodePrinter(from: TestJSON.printerMinimal)]
        let script = ScriptedCanonicalResult<[Printer]>([
            .gated(firstGate),
            .value(current)
        ])
        mockService.listHandler = { _ in try await script.next() }
        let signalR = MockSignalRService()
        let vm = PrinterListViewModel(callbackEnqueuer: callbackQueue.enqueuer)
        vm.configure(
            printerService: mockService,
            autoPrintService: mockAutoDispatchService
        )
        vm.configureSignalR(signalR)

        let manualRefresh = Task { await vm.loadPrinters() }
        await script.waitForCallCount(1)
        signalR.simulateConnectionStateChange(.reconnecting)
        await callbackQueue.runNext()
        signalR.simulateConnectionStateChange(.connected)
        await callbackQueue.runNext()
        let countBeforeRelease = await script.callCount
        XCTAssertEqual(countBeforeRelease, 1)

        await firstGate.succeed(stale)
        await script.waitForCallCount(2)
        await manualRefresh.value

        let finalCount = await script.callCount
        XCTAssertEqual(finalCount, 2)
        XCTAssertEqual(vm.printers.first?.name, "Ender 3")
        signalR.simulateCapturedConnectionStateChange(at: 0, state: .connected)
        await callbackQueue.runNext()
        await vm.waitForCanonicalLoadToBecomeIdle()
        let repeatedConnectedCount = await script.callCount
        XCTAssertEqual(repeatedConnectedCount, 2)
    }

    func testSameServiceReconfigurePreservesQueuedListReconnectEdge() async throws {
        let callbackQueue = ShiftTaskCallbackQueue()
        let current = [try TestData.decodePrinter(from: TestJSON.printerMinimal)]
        let script = ScriptedCanonicalResult<[Printer]>([.value(current)])
        mockService.listHandler = { _ in try await script.next() }
        let signalR = MockSignalRService()
        let vm = PrinterListViewModel(callbackEnqueuer: callbackQueue.enqueuer)
        vm.configure(
            printerService: mockService,
            autoPrintService: mockAutoDispatchService
        )
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
        XCTAssertEqual(vm.printers.first?.name, "Ender 3")
    }

    func testInactiveListFencesReconnectAndReactivationRestoresOneOwner() async throws {
        let callbackQueue = ShiftTaskCallbackQueue()
        let current = [try TestData.decodePrinter(from: TestJSON.printerMinimal)]
        let script = ScriptedCanonicalResult<[Printer]>([.value(current)])
        mockService.listHandler = { _ in try await script.next() }
        let signalR = MockSignalRService()
        let vm = PrinterListViewModel(callbackEnqueuer: callbackQueue.enqueuer)
        vm.configure(
            printerService: mockService,
            autoPrintService: mockAutoDispatchService
        )
        vm.configureSignalR(signalR)
        vm.deactivate()

        signalR.simulateCapturedConnectionStateChange(at: 0, state: .reconnecting)
        signalR.simulateCapturedConnectionStateChange(at: 0, state: .connected)
        await callbackQueue.runNext()
        await callbackQueue.runNext()
        var callCount = await script.callCount
        XCTAssertEqual(callCount, 0)
        XCTAssertTrue(vm.printers.isEmpty)

        vm.activate()
        vm.configure(
            printerService: mockService,
            autoPrintService: mockAutoDispatchService
        )
        vm.configureSignalR(signalR)
        signalR.simulateConnectionStateChange(.reconnecting)
        await callbackQueue.runNext()
        signalR.simulateConnectionStateChange(.connected)
        await callbackQueue.runNext()
        await script.waitForCallCount(1)
        await vm.waitForCanonicalLoadToBecomeIdle()

        callCount = await script.callCount
        XCTAssertEqual(callCount, 1)
        XCTAssertEqual(vm.printers.first?.name, "Ender 3")
    }

    func testProductionViewLifecycleFencesRetainedListAndRestoresExactlyOneOwner() async throws {
        let callbackQueue = ShiftTaskCallbackQueue()
        let printer = try TestData.decodePrinter(from: TestJSON.printerMinimal)
        let script = ScriptedCanonicalResult<[Printer]>([
            .value([printer]),
            .value([printer]),
            .value([printer])
        ])
        mockService.listHandler = { _ in try await script.next() }
        let signalR = MockSignalRService()
        let retainedViewModel = PrinterListViewModel(
            callbackEnqueuer: callbackQueue.enqueuer
        )

        PrinterListViewLifecycle.taskActivate(
            viewModel: retainedViewModel,
            printerService: mockService,
            autoPrintService: mockAutoDispatchService,
            signalRService: signalR
        )
        await retainedViewModel.loadPrinters()
        XAssertEqual(await script.callCount, 1)

        PrinterListViewLifecycle.onDisappear(
            viewModel: retainedViewModel,
            retryTask: nil
        )
        signalR.simulateCapturedConnectionStateChange(at: 0, state: .reconnecting)
        signalR.simulateCapturedConnectionStateChange(at: 0, state: .connected)
        await callbackQueue.runNext()
        await callbackQueue.runNext()
        await PrinterListViewLifecycle.willEnterForeground(
            viewModel: retainedViewModel
        )
        XAssertEqual(await script.callCount, 1)

        PrinterListViewLifecycle.taskActivate(
            viewModel: retainedViewModel,
            printerService: mockService,
            autoPrintService: mockAutoDispatchService,
            signalRService: signalR
        )
        await retainedViewModel.loadPrinters()
        XAssertEqual(await script.callCount, 2)

        signalR.simulateConnectionStateChange(.reconnecting)
        await callbackQueue.runNext()
        signalR.simulateConnectionStateChange(.connected)
        await callbackQueue.runNext()
        await retainedViewModel.waitForCanonicalLoadToBecomeIdle()
        XAssertEqual(await script.callCount, 3)

        signalR.simulateConnectionStateChange(.connected)
        XCTAssertEqual(callbackQueue.count, 0)
        XAssertEqual(await script.callCount, 3)
    }

    func testCancellingOneListCallerCompletesPromptlyAndPreservesPeerDemand() async throws {
        let firstGate = ShiftTaskResultGate<[Printer]>()
        let current = [try TestData.decodePrinter(from: TestJSON.printerMinimal)]
        let script = ScriptedCanonicalResult<[Printer]>([
            .gated(firstGate),
            .value(current)
        ])
        mockService.listHandler = { _ in try await script.next() }

        let cancelledCaller = Task { await viewModel.loadPrinters() }
        await script.waitForCallCount(1)
        let peerCaller = Task { await viewModel.loadPrinters() }
        await viewModel.waitForCanonicalWaiterCount(2)

        cancelledCaller.cancel()
        await cancelledCaller.value
        XCTAssertEqual(viewModel.canonicalWaiterCountForTesting, 1)
        XAssertEqual(await script.callCount, 1)

        await firstGate.succeed([try TestData.decodePrinter()])
        await peerCaller.value

        XAssertEqual(await script.callCount, 2)
        XCTAssertEqual(viewModel.printers.map(\.id), current.map(\.id))
        XCTAssertEqual(viewModel.canonicalWaiterCountForTesting, 0)
    }

    func testCancellingSoleListCallerUnwindsDemandWithoutWaitingForService() async throws {
        let callbackQueue = ShiftTaskCallbackQueue()
        let gate = ShiftTaskResultGate<[Printer]>()
        let script = ScriptedCanonicalResult<[Printer]>([.gated(gate)])
        mockService.listHandler = { _ in try await script.next() }
        let vm = PrinterListViewModel(callbackEnqueuer: callbackQueue.enqueuer)
        vm.configure(
            printerService: mockService,
            autoPrintService: mockAutoDispatchService
        )

        let caller = Task { await vm.loadPrinters() }
        await script.waitForCallCount(1)
        caller.cancel()
        await caller.value
        XCTAssertEqual(callbackQueue.count, 1)
        await callbackQueue.runNext()

        XCTAssertEqual(vm.canonicalWaiterCountForTesting, 0)
        XCTAssertFalse(vm.isLoading)
        XCTAssertTrue(vm.printers.isEmpty)
        XAssertEqual(await script.callCount, 1)

        await gate.succeed([try TestData.decodePrinter()])
        await vm.waitForSupersededCanonicalLoads()
        XCTAssertTrue(vm.printers.isEmpty)
    }

    func testParkedListServiceDoesNotRetainViewModelOrCaller() async {
        let gate = ShiftTaskResultGate<[Printer]>()
        let script = ScriptedCanonicalResult<[Printer]>([.gated(gate)])
        mockService.listHandler = { _ in try await script.next() }
        var vm: PrinterListViewModel? = PrinterListViewModel()
        vm?.configure(
            printerService: mockService,
            autoPrintService: mockAutoDispatchService
        )
        let weakVM = CanonicalOwnerWeakReference(vm)
        let waiter = vm?.beginCanonicalLoadForTesting()
        let caller = Task { await waiter?.wait() }
        await script.waitForCallCount(1)

        vm = nil
        XCTAssertNil(weakVM.value)
        await caller.value

        await gate.succeed([])
    }

    // MARK: - Load Printers

    func testLoadPrintersSuccessPopulatesList() async throws {
        let printer = try TestData.decodePrinter()
        mockService.printersToReturn = [printer]

        await viewModel.loadPrinters()

        XCTAssertEqual(viewModel.printers.count, 1)
        XCTAssertTrue(mockService.listPrintersCalled)
        XCTAssertFalse(viewModel.isLoading)
        XCTAssertNil(viewModel.errorMessage)
    }

    func testLoadPrintersEmptyList() async {
        mockService.printersToReturn = []

        await viewModel.loadPrinters()

        XCTAssertTrue(viewModel.printers.isEmpty)
        XCTAssertNil(viewModel.errorMessage)
    }

    func testLoadPrintersError() async {
        mockService.errorToThrow = NetworkError.noConnection

        await viewModel.loadPrinters()

        XCTAssertTrue(viewModel.printers.isEmpty)
        XCTAssertNotNil(viewModel.errorMessage)
        XCTAssertFalse(viewModel.isLoading)
    }

    // MARK: - Search Filtering

    func testSearchFiltersByName() async throws {
        let mk4 = try TestData.decodePrinter(from: TestJSON.printer)
        let ender = try TestData.decodePrinter(from: TestJSON.printerMinimal)
        mockService.printersToReturn = [mk4, ender]
        await viewModel.loadPrinters()

        viewModel.searchText = "Prusa"
        XCTAssertEqual(viewModel.filteredPrinters.count, 1)
        XCTAssertEqual(viewModel.filteredPrinters.first?.name, "Prusa MK4")
    }

    func testSearchIsCaseInsensitive() async throws {
        let printer = try TestData.decodePrinter(from: TestJSON.printer)
        mockService.printersToReturn = [printer]
        await viewModel.loadPrinters()

        viewModel.searchText = "prusa"
        XCTAssertEqual(viewModel.filteredPrinters.count, 1)
    }

    func testEmptySearchReturnsAll() async throws {
        let mk4 = try TestData.decodePrinter(from: TestJSON.printer)
        let ender = try TestData.decodePrinter(from: TestJSON.printerMinimal)
        mockService.printersToReturn = [mk4, ender]
        await viewModel.loadPrinters()

        viewModel.searchText = ""
        XCTAssertEqual(viewModel.filteredPrinters.count, 2)
    }

    // MARK: - Status Filtering

    func testFilterByOnline() async throws {
        let online = try TestData.decodePrinter(from: TestJSON.printer)     // isOnline: true
        let offline = try TestData.decodePrinter(from: TestJSON.printerMinimal) // isOnline: false
        mockService.printersToReturn = [online, offline]
        await viewModel.loadPrinters()

        viewModel.selectedStatus = .online
        XCTAssertEqual(viewModel.filteredPrinters.count, 1)
        XCTAssertEqual(viewModel.filteredPrinters.first?.name, "Prusa MK4")
    }

    func testFilterByOffline() async throws {
        let online = try TestData.decodePrinter(from: TestJSON.printer)
        let offline = try TestData.decodePrinter(from: TestJSON.printerMinimal)
        mockService.printersToReturn = [online, offline]
        await viewModel.loadPrinters()

        viewModel.selectedStatus = .offline
        XCTAssertEqual(viewModel.filteredPrinters.count, 1)
        XCTAssertEqual(viewModel.filteredPrinters.first?.name, "Ender 3")
    }

    func testFilterByPrinting() async throws {
        let printing = try TestData.decodePrinter(from: TestJSON.printer) // state: "printing"
        let offline = try TestData.decodePrinter(from: TestJSON.printerMinimal) // state: nil
        mockService.printersToReturn = [printing, offline]
        await viewModel.loadPrinters()

        viewModel.selectedStatus = .printing
        XCTAssertEqual(viewModel.filteredPrinters.count, 1)
    }

    func testFilterAllShowsEverything() async throws {
        let mk4 = try TestData.decodePrinter(from: TestJSON.printer)
        let ender = try TestData.decodePrinter(from: TestJSON.printerMinimal)
        mockService.printersToReturn = [mk4, ender]
        await viewModel.loadPrinters()

        viewModel.selectedStatus = .all
        XCTAssertEqual(viewModel.filteredPrinters.count, 2)
    }

    // MARK: - Pull to Refresh

    func testPullToRefreshReloadsData() async throws {
        mockService.printersToReturn = []
        await viewModel.loadPrinters()
        XCTAssertEqual(viewModel.printers.count, 0)

        let printer = try TestData.decodePrinter()
        mockService.printersToReturn = [printer]
        await viewModel.loadPrinters()

        XCTAssertEqual(viewModel.printers.count, 1)
    }

    func testRefreshClearsError() async {
        mockService.errorToThrow = NetworkError.noConnection
        await viewModel.loadPrinters()
        XCTAssertNotNil(viewModel.errorMessage)

        mockService.errorToThrow = nil
        mockService.printersToReturn = []
        await viewModel.loadPrinters()

        XCTAssertNil(viewModel.errorMessage)
    }

    // MARK: - Not Configured

    func testLoadWithoutConfigureDoesNotCrash() async {
        let unconfigured = PrinterListViewModel()
        await unconfigured.loadPrinters()
        XCTAssertFalse(unconfigured.isLoading)
    }
}
