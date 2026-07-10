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
        let base = try idlePrinter()
        let vm = try makeViewModel(printer: base, capabilities: Self.fullCaps)
        await vm.loadCapabilities()

        await vm.preheat(.pla)
        XCTAssertNotNil(vm.pendingCommand, "Pending stays set after success — cleared by SignalR")

        // A *confirming* snapshot must move the preheat's own domain (targets).
        // An identical snapshot carries no evidence and must NOT clear pending.
        vm.handlePrinterUpdate(base)
        XCTAssertNotNil(vm.pendingCommand, "No-op snapshot must not release the command")

        var warmed = base
        warmed.hotendTarget = 200
        vm.handlePrinterUpdate(warmed)
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


    func test_isExecuting_falseInitially() async throws {
        let vm = try makeViewModel(printer: try idlePrinter(), capabilities: Self.fullCaps)
        await vm.loadCapabilities()
        XCTAssertFalse(vm.isExecuting)
    }

    func test_isExecuting_trueWhileInFlight_falseAfterError() async throws {
        let gate = AsyncGate()
        mockService.beforeSetTemperatures = { await gate.wait() }
        let vm = try makeViewModel(printer: try idlePrinter(), capabilities: Self.fullCaps)
        await vm.loadCapabilities()

        async let first: Void = vm.preheat(.pla)

        // Deterministically wait until the task is blocked at the gate.
        while await !gate.hasWaiters { await Task.yield() }
        XCTAssertTrue(vm.isExecuting)

        // Fail the command so pendingCommand (and isExecuting) clears.
        mockService.errorToThrow = NetworkError.serverError(500)
        await gate.open()
        await first
        XCTAssertFalse(vm.isExecuting)
    }

    func test_isExecuting_falseAfterSuccess() async throws {
        let gate = AsyncGate()
        mockService.beforeSetTemperatures = { await gate.wait() }
        let vm = try makeViewModel(printer: try idlePrinter(), capabilities: Self.fullCaps)
        await vm.loadCapabilities()

        async let first: Void = vm.preheat(.pla)

        // Wait until blocked at gate.
        while await !gate.hasWaiters { await Task.yield() }
        XCTAssertTrue(vm.isExecuting)

        // Let it succeed (no error set).
        await gate.open()
        await first
        XCTAssertFalse(vm.isExecuting)
    }

    // MARK: - Live snapshot forwarding (issue #706 F1 review)
    //
    // These tests pin the regression where `PrinterControlsSection` only
    // forwarded snapshots that changed `state` or `isOnline`, leaving
    // jog/preheat/home controls jammed after position/temperature-only
    // updates. The fix is a `PrinterControlsUpdateSignal` that captures
    // every field the VM reads; the VM itself must clear `pendingCommand`
    // when a matching-id snapshot arrives regardless of which field
    // moved, and must ignore snapshots for other printers.

    func test_handlePrinterUpdate_clearsPending_onPositionOnlyUpdate() async throws {
        let base = try idlePrinter()
        let vm = try makeViewModel(printer: base, capabilities: Self.fullCaps)
        await vm.loadCapabilities()

        await vm.jog(axis: "X", distanceMm: 10)
        XCTAssertNotNil(vm.pendingCommand, "jog leaves pending set until SignalR confirms")

        // Snapshot mutates only x/y/z; state and isOnline are unchanged.
        var moved = base
        moved.x = (base.x ?? 0) + 10
        XCTAssertEqual(moved.state, base.state)
        XCTAssertEqual(moved.isOnline, base.isOnline)
        XCTAssertNotEqual(
            PrinterControlsUpdateSignal(printer: base),
            PrinterControlsUpdateSignal(printer: moved),
            "Position-only drift must produce a distinct signal so `.onChange` fires"
        )

        vm.handlePrinterUpdate(moved)
        XCTAssertNil(vm.pendingCommand,
                     "Position-only update must clear pending — regression from #706 review")
    }

    func test_handlePrinterUpdate_clearsPending_onTemperatureOnlyUpdate() async throws {
        let base = try idlePrinter()
        let vm = try makeViewModel(printer: base, capabilities: Self.fullCaps)
        await vm.loadCapabilities()

        await vm.preheat(.pla)
        XCTAssertNotNil(vm.pendingCommand, "preheat leaves pending set until SignalR confirms")

        // Snapshot mutates only hotend/bed telemetry.
        var warmed = base
        warmed.hotendTemp = 42
        warmed.bedTemp = 55
        warmed.hotendTarget = 200
        warmed.bedTarget = 60
        XCTAssertEqual(warmed.state, base.state)
        XCTAssertNotEqual(
            PrinterControlsUpdateSignal(printer: base),
            PrinterControlsUpdateSignal(printer: warmed)
        )

        vm.handlePrinterUpdate(warmed)
        XCTAssertNil(vm.pendingCommand,
                     "Temperature-only update must clear pending — regression from #706 review")
    }

    func test_handlePrinterUpdate_clearsPending_onHomingOnlyUpdate() async throws {
        // Start from a printer that reports no axes homed, so the
        // update we craft below actually moves `homedAxes`.
        let unhomedJSON = TestJSON.printer
            .replacingOccurrences(of: "\"state\": \"printing\"", with: "\"state\": \"ready\"")
            .replacingOccurrences(of: "\"homedAxes\": \"xyz\"", with: "\"homedAxes\": \"\"")
        let base = try TestData.decoder.decode(Printer.self, from: unhomedJSON.data(using: .utf8)!)
        let vm = PrinterControlsViewModel(printerService: mockService, printer: base)
        mockService.capabilitiesToReturn = Self.fullCaps
        await vm.loadCapabilities()

        await vm.homeAll()
        XCTAssertNotNil(vm.pendingCommand)

        // Home reports as new `homedAxes` without a state transition.
        var homed = base
        homed.homedAxes = "xyz"
        XCTAssertEqual(homed.state, base.state)
        XCTAssertNotEqual(
            PrinterControlsUpdateSignal(printer: base),
            PrinterControlsUpdateSignal(printer: homed),
            "Homing drift must produce a distinct signal so `.onChange` fires"
        )

        vm.handlePrinterUpdate(homed)
        XCTAssertNil(vm.pendingCommand,
                     "Homing-only update must clear pending — regression from #706 review")
    }

    func test_handlePrinterUpdate_ignoresUpdatesForDifferentPrinter() async throws {
        let base = try idlePrinter()
        let vm = try makeViewModel(printer: base, capabilities: Self.fullCaps)
        await vm.loadCapabilities()

        await vm.jog(axis: "Y", distanceMm: 1)
        XCTAssertNotNil(vm.pendingCommand)

        // Decode a second, unrelated printer (different id via printerMinimal fixture).
        let otherJSON = TestJSON.printerMinimal
            .replacingOccurrences(of: "\"isOnline\": false", with: "\"isOnline\": true")
        let other = try TestData.decoder.decode(Printer.self, from: otherJSON.data(using: .utf8)!)
        XCTAssertNotEqual(other.id, base.id)

        vm.handlePrinterUpdate(other)
        XCTAssertNotNil(vm.pendingCommand,
                        "Snapshots for a different printer id must never clear pending state")
    }

    // MARK: - Command correlation: cross-talk must NOT clear pending
    //
    // The controls surface consumes a merged telemetry stream. Ambient churn
    // in a field the pending command does not drive must leave it pending
    // (issue #706 F1 review defect A). Each negative test also proves the
    // cached snapshot is still advanced so the *next* diff is measured from
    // the freshly received values.

    func test_handlePrinterUpdate_temperatureNoise_doesNotClearPendingJog() async throws {
        let base = try idlePrinter()
        let vm = try makeViewModel(printer: base, capabilities: Self.fullCaps)
        await vm.loadCapabilities()

        await vm.jog(axis: "X", distanceMm: 10)
        XCTAssertNotNil(vm.pendingCommand)

        var warmed = base
        warmed.hotendTemp = (base.hotendTemp ?? 0) + 3
        warmed.bedTemp = (base.bedTemp ?? 0) + 1
        warmed.hotendTarget = 240
        warmed.bedTarget = 80
        XCTAssertEqual(warmed.x, base.x)

        vm.handlePrinterUpdate(warmed)
        XCTAssertNotNil(vm.pendingCommand,
                        "Temperature/target noise must not clear a pending jog")
        XCTAssertEqual(vm.printer.hotendTarget, 240,
                       "Cached snapshot must still advance even when pending is retained")
    }

    func test_handlePrinterUpdate_positionNoise_doesNotClearPendingPreheat() async throws {
        let base = try idlePrinter()
        let vm = try makeViewModel(printer: base, capabilities: Self.fullCaps)
        await vm.loadCapabilities()

        await vm.preheat(.pla)
        XCTAssertNotNil(vm.pendingCommand)

        var moved = base
        moved.x = (base.x ?? 0) + 25
        moved.y = (base.y ?? 0) - 5
        moved.z = (base.z ?? 0) + 1
        XCTAssertEqual(moved.hotendTarget, base.hotendTarget)

        vm.handlePrinterUpdate(moved)
        XCTAssertNotNil(vm.pendingCommand,
                        "Position noise must not clear a pending preheat")
        XCTAssertEqual(vm.printer.x, moved.x, "Cached snapshot must still advance")
    }

    func test_handlePrinterUpdate_otherAxisMotion_doesNotClearPendingJogX() async throws {
        let base = try idlePrinter()
        let vm = try makeViewModel(printer: base, capabilities: Self.fullCaps)
        await vm.loadCapabilities()

        await vm.jog(axis: "X", distanceMm: 10)
        XCTAssertNotNil(vm.pendingCommand)

        // Only Z moves; the pending jog is on X.
        var moved = base
        moved.z = (base.z ?? 0) + 5
        XCTAssertEqual(moved.x, base.x)

        vm.handlePrinterUpdate(moved)
        XCTAssertNotNil(vm.pendingCommand,
                        "Motion on an unrelated axis must not clear a jog on X")
    }

    func test_handlePrinterUpdate_homingNoise_doesNotClearPendingPreheat() async throws {
        let base = try idlePrinter()
        let vm = try makeViewModel(printer: base, capabilities: Self.fullCaps)
        await vm.loadCapabilities()

        await vm.preheat(.abs)
        XCTAssertNotNil(vm.pendingCommand)

        var rehomed = base
        rehomed.homedAxes = "xy"
        XCTAssertEqual(rehomed.hotendTarget, base.hotendTarget)

        vm.handlePrinterUpdate(rehomed)
        XCTAssertNotNil(vm.pendingCommand,
                        "Homed-axes churn must not clear a pending preheat")
    }

    func test_handlePrinterUpdate_positionAndTempNoise_doesNotClearPendingHome() async throws {
        // Start from a printer already reporting all axes homed so re-homing
        // does not move `homedAxes`; only unrelated telemetry drifts.
        let base = try idlePrinter()
        XCTAssertEqual(base.homedAxes, "xyz")
        let vm = try makeViewModel(printer: base, capabilities: Self.fullCaps)
        await vm.loadCapabilities()

        await vm.homeAll()
        XCTAssertNotNil(vm.pendingCommand)

        var drifted = base
        drifted.x = (base.x ?? 0) + 10
        drifted.hotendTemp = (base.hotendTemp ?? 0) + 4
        XCTAssertEqual(drifted.homedAxes, base.homedAxes)

        vm.handlePrinterUpdate(drifted)
        XCTAssertNotNil(vm.pendingCommand,
                        "Position/temperature noise must not clear a pending home; homedAxes is the signal")
    }

    // MARK: - Command correlation: relevant evidence DOES clear pending

    func test_handlePrinterUpdate_targetChange_clearsPendingPreheat() async throws {
        let base = try idlePrinter()
        let vm = try makeViewModel(printer: base, capabilities: Self.fullCaps)
        await vm.loadCapabilities()

        await vm.preheat(.pla)
        XCTAssertNotNil(vm.pendingCommand)

        var warmed = base
        warmed.hotendTarget = 200 // base is 215 → moves
        vm.handlePrinterUpdate(warmed)
        XCTAssertNil(vm.pendingCommand, "Target change confirms a preheat")
    }

    func test_handlePrinterUpdate_offlineTransition_clearsAnyPending() async throws {
        let base = try idlePrinter()
        let vm = try makeViewModel(printer: base, capabilities: Self.fullCaps)
        await vm.loadCapabilities()

        await vm.jog(axis: "X", distanceMm: 10)
        XCTAssertNotNil(vm.pendingCommand)

        var offline = base
        offline.isOnline = false // no position/temp change — lifecycle only
        XCTAssertEqual(offline.x, base.x)

        vm.handlePrinterUpdate(offline)
        XCTAssertNil(vm.pendingCommand,
                     "Going offline invalidates any in-flight command")
    }

    func test_handlePrinterUpdate_stateTransition_clearsAnyPending() async throws {
        let base = try idlePrinter()
        let vm = try makeViewModel(printer: base, capabilities: Self.fullCaps)
        await vm.loadCapabilities()

        await vm.preheat(.pla)
        XCTAssertNotNil(vm.pendingCommand)

        var printing = base
        printing.state = "printing" // no temp/target change — lifecycle only
        XCTAssertEqual(printing.hotendTarget, base.hotendTarget)

        vm.handlePrinterUpdate(printing)
        XCTAssertNil(vm.pendingCommand,
                     "A state transition supersedes any in-flight command")
    }

    // MARK: - PrinterControlsUpdateSignal

    func test_updateSignal_equalWhenIrrelevantFieldsChange() throws {
        let base = try idlePrinter()
        var churned = base
        // Camera / progress / job / spool churn must not trigger `.onChange`.
        churned.progress = (base.progress ?? 0) + 0.1
        churned.jobName = "\(base.jobName ?? "job")-v2"
        churned.thumbnailUrl = "http://example.com/other.png"
        churned.cameraStreamUrl = "http://example.com/stream"

        XCTAssertEqual(
            PrinterControlsUpdateSignal(printer: base),
            PrinterControlsUpdateSignal(printer: churned),
            "Signal must ignore fields the controls VM does not consume"
        )
    }

    func test_updateSignal_differentWhenStateChanges() throws {
        let base = try idlePrinter()
        var printing = base
        printing.state = "printing"
        XCTAssertNotEqual(
            PrinterControlsUpdateSignal(printer: base),
            PrinterControlsUpdateSignal(printer: printing)
        )
    }
}

// MARK: - Test gate helper

private actor AsyncGate {
    private var waiters: [CheckedContinuation<Void, Never>] = []
    private var opened = false

    var hasWaiters: Bool { !waiters.isEmpty || opened }

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
