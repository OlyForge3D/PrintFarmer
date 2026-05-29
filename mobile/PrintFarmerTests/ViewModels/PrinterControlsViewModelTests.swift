import XCTest
@testable import PrintFarmer

@MainActor
final class PrinterControlsViewModelTests: XCTestCase {

    private var mockService: MockPrinterService!

    override func setUp() {
        super.setUp()
        mockService = MockPrinterService()
    }

    override func tearDown() {
        mockService = nil
        super.tearDown()
    }

    // MARK: - Helpers

    private func makeViewModel(
        printer: Printer? = nil,
        capabilities: PrinterBackendCapabilities? = nil
    ) throws -> PrinterControlsViewModel {
        let p = try printer ?? TestData.decodePrinter() // online + state="printing" by default
        if let caps = capabilities {
            mockService.capabilitiesToReturn = caps
        }
        return PrinterControlsViewModel(printerService: mockService, printer: p)
    }

    /// Returns a printer that is online and idle (state="ready").
    private func idlePrinter() throws -> Printer {
        let json = TestJSON.printer
            .replacingOccurrences(of: "\"state\": \"printing\"", with: "\"state\": \"ready\"")
        return try TestData.decoder.decode(Printer.self, from: json.data(using: .utf8)!)
    }

    private static let fullCaps = PrinterBackendCapabilities(
        supportsMovement: true,
        supportsTemperatureControl: true,
        supportsBedTemperature: true,
        supportsFanControl: true,
        supportsHoming: true,
        supportedAxes: ["X", "Y", "Z"]
    )

    private static let flashForgeCaps = PrinterBackendCapabilities(
        supportsMovement: true,
        supportsTemperatureControl: true,
        supportsBedTemperature: false,
        supportsFanControl: false,
        supportsHoming: true,
        supportedAxes: ["X", "Y", "Z"]
    )

    // MARK: - Tests

    func test_loadCapabilities_cachesSecondCallNoFetch() async throws {
        let vm = try makeViewModel(printer: try idlePrinter(), capabilities: Self.fullCaps)

        await vm.loadCapabilities()
        XCTAssertEqual(vm.capabilities, Self.fullCaps)
        XCTAssertEqual(mockService.getBackendCapabilitiesCalledWith, vm.printer.id)

        mockService.getBackendCapabilitiesCalledWith = nil
        await vm.loadCapabilities()
        XCTAssertNil(mockService.getBackendCapabilitiesCalledWith, "Second loadCapabilities should not refetch")
    }

    func test_preheatPLA_callsSetTemperaturesWith200_60() async throws {
        let vm = try makeViewModel(printer: try idlePrinter(), capabilities: Self.fullCaps)
        await vm.loadCapabilities()

        await vm.preheat(.pla)

        XCTAssertEqual(mockService.setTemperaturesCalledWith?.printerId, vm.printer.id)
        XCTAssertEqual(mockService.setTemperaturesCalledWith?.hotend, 200)
        XCTAssertEqual(mockService.setTemperaturesCalledWith?.bed, 60)
        XCTAssertNil(vm.lastError)
    }

    func test_preheatPETG_onFlashForge_dropsBedSilently() async throws {
        let vm = try makeViewModel(printer: try idlePrinter(), capabilities: Self.flashForgeCaps)
        await vm.loadCapabilities()

        await vm.preheat(.petg)

        XCTAssertEqual(mockService.setTemperaturesCalledWith?.hotend, 240)
        XCTAssertNil(mockService.setTemperaturesCalledWith?.bed, "Bed must be silently dropped when unsupported")
        XCTAssertNil(vm.lastError, "Dropping the bed value must not surface as an error")
    }

    func test_coolDown_sendsZeroZero_evenWhenBedUnsupported() async throws {
        let vm = try makeViewModel(printer: try idlePrinter(), capabilities: Self.flashForgeCaps)
        await vm.loadCapabilities()

        await vm.preheat(.coolDown)

        XCTAssertEqual(mockService.setTemperaturesCalledWith?.hotend, 0)
        XCTAssertEqual(mockService.setTemperaturesCalledWith?.bed, 0)
    }

    func test_homeAll_callsHomeWithAllAxes() async throws {
        let vm = try makeViewModel(printer: try idlePrinter(), capabilities: Self.fullCaps)
        await vm.loadCapabilities()

        await vm.homeAll()

        XCTAssertEqual(mockService.homeCalledWith?.printerId, vm.printer.id)
        XCTAssertEqual(mockService.homeCalledWith?.axes, ["X", "Y", "Z"])
    }

    func test_jogX_usesXYFeedrate() async throws {
        let vm = try makeViewModel(printer: try idlePrinter(), capabilities: Self.fullCaps)
        await vm.loadCapabilities()

        await vm.jog(axis: "X", distanceMm: 10)

        XCTAssertEqual(mockService.moveCalledWith?.axis, "X")
        XCTAssertEqual(mockService.moveCalledWith?.distanceMm, 10)
        XCTAssertEqual(mockService.moveCalledWith?.feedrateMmMin, 3000)
    }

    func test_jogZ_usesZFeedrate() async throws {
        let vm = try makeViewModel(printer: try idlePrinter(), capabilities: Self.fullCaps)
        await vm.loadCapabilities()

        await vm.jog(axis: "Z", distanceMm: -1)

        XCTAssertEqual(mockService.moveCalledWith?.feedrateMmMin, 600)
    }

    func test_singleFlightDropsConcurrentCommands() async throws {
        let gate = AsyncGate()
        mockService.beforeSetTemperatures = { await gate.wait() }
        let vm = try makeViewModel(printer: try idlePrinter(), capabilities: Self.fullCaps)
        await vm.loadCapabilities()

        async let first: Void = vm.preheat(.pla)
        // Yield so first task enters beginCommand and sets pendingCommand.
        await Task.yield()
        await Task.yield()
        XCTAssertNotNil(vm.pendingCommand)

        // Second call while first is in flight must drop silently.
        mockService.setTemperaturesCalledWith = nil
        await vm.preheat(.abs)
        XCTAssertNil(mockService.setTemperaturesCalledWith, "Concurrent command must be dropped")
        XCTAssertNil(vm.lastError, "Dropped command must not surface as an error")

        await gate.open()
        await first
    }

    func test_signalRClearsPendingCommand() async throws {
        let vm = try makeViewModel(printer: try idlePrinter(), capabilities: Self.fullCaps)
        await vm.loadCapabilities()

        await vm.preheat(.pla)
        XCTAssertNotNil(vm.pendingCommand, "Pending stays set after success — cleared by SignalR")

        vm.handlePrinterUpdate(vm.printer)
        XCTAssertNil(vm.pendingCommand)
    }

    func test_canControlFalse_whilePrinting() async throws {
        let printer = try TestData.decodePrinter() // state="printing"
        let vm = PrinterControlsViewModel(printerService: mockService, printer: printer)

        XCTAssertFalse(vm.canControl)
        XCTAssertNotNil(vm.blockedReason)

        await vm.preheat(.pla)
        XCTAssertNil(mockService.setTemperaturesCalledWith)
        XCTAssertNotNil(vm.lastError)
        XCTAssertEqual(vm.lastError?.isRetryable, false)
    }

    func test_errorMapping_5xx_isRetryable() async throws {
        mockService.errorToThrow = NetworkError.serverError(503)
        let vm = try makeViewModel(printer: try idlePrinter(), capabilities: Self.fullCaps)
        await vm.loadCapabilities()

        await vm.preheat(.pla)

        XCTAssertNotNil(vm.lastError)
        XCTAssertEqual(vm.lastError?.isRetryable, true)
        XCTAssertNil(vm.pendingCommand, "Pending must clear on failure so user can retry")
    }

    func test_errorMapping_4xx_unauthorized_notRetryable() async throws {
        mockService.errorToThrow = NetworkError.unauthorized
        let vm = try makeViewModel(printer: try idlePrinter(), capabilities: Self.fullCaps)
        await vm.loadCapabilities()

        await vm.homeAll()

        XCTAssertEqual(vm.lastError?.isRetryable, false)
    }

    func test_errorMapping_network_isRetryable() async throws {
        mockService.errorToThrow = NetworkError.noConnection
        let vm = try makeViewModel(printer: try idlePrinter(), capabilities: Self.fullCaps)
        await vm.loadCapabilities()

        await vm.jog(axis: "X", distanceMm: 1)

        XCTAssertEqual(vm.lastError?.isRetryable, true)
    }

    func test_dismissError_clearsLastError() async throws {
        mockService.errorToThrow = NetworkError.serverError(500)
        let vm = try makeViewModel(printer: try idlePrinter(), capabilities: Self.fullCaps)
        await vm.loadCapabilities()
        await vm.preheat(.pla)
        XCTAssertNotNil(vm.lastError)

        vm.dismissError()
        XCTAssertNil(vm.lastError)
    }
}

// MARK: - Test gate helper

private actor AsyncGate {
    private var waiters: [CheckedContinuation<Void, Never>] = []
    private var opened = false

    func wait() async {
        if opened { return }
        await withCheckedContinuation { c in waiters.append(c) }
    }

    func open() {
        opened = true
        let toResume = waiters
        waiters.removeAll()
        for c in toResume { c.resume() }
    }
}
