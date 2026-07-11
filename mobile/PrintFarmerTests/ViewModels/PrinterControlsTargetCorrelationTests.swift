import XCTest
@testable import PrintFarmer

/// Requested-target correlation for preheat/cool-down commands (issue #706).
///
/// Confirmation compares the live snapshot's commanded *targets* against the
/// concrete targets carried on the pending command — not a delta — so a
/// same-preset preheat or an already-zero cool-down can't hang forever when no
/// target field changes. Measured temperatures are irrelevant, and an
/// uncontrollable setpoint (`nil` target, e.g. a bed-less backend) is treated
/// as already satisfied.
@MainActor
final class PrinterControlsTargetCorrelationTests: XCTestCase {

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
        printer: Printer,
        capabilities: PrinterBackendCapabilities
    ) -> PrinterControlsViewModel {
        mockService.capabilitiesToReturn = capabilities
        return PrinterControlsViewModel(printerService: mockService, printer: printer)
    }

    /// Online + idle (state="ready") printer; base targets are 215/60.
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

    /// Bed control unsupported (e.g. FlashForge).
    private static let noBedCaps = PrinterBackendCapabilities(
        supportsMovement: true,
        supportsTemperatureControl: true,
        supportsBedTemperature: false,
        supportsFanControl: false,
        supportsHoming: true,
        supportedAxes: ["X", "Y", "Z"]
    )

    // MARK: - Already-at-target confirmation (the core regression)

    func test_preheat_alreadyAtRequestedTargets_confirmsOnSuccess_notStuck() async throws {
        var base = try idlePrinter()
        base.hotendTarget = 200
        base.bedTarget = 60 // already exactly at PLA targets — no delta will ever arrive
        let vm = makeViewModel(printer: base, capabilities: Self.fullCaps)
        await vm.loadCapabilities()

        await vm.preheat(.pla)

        XCTAssertFalse(vm.isExecuting,
                       "A same-preset preheat on a printer already at target must confirm on success, not hang")
        XCTAssertNil(vm.lastError)
    }

    func test_coolDown_alreadyAtZero_confirmsOnSuccess_notStuck() async throws {
        var base = try idlePrinter()
        base.hotendTarget = 0
        base.bedTarget = 0 // already cooled — Cool Down commands 0/0
        let vm = makeViewModel(printer: base, capabilities: Self.fullCaps)
        await vm.loadCapabilities()

        await vm.preheat(.coolDown)

        XCTAssertFalse(vm.isExecuting, "An already-zero Cool Down must not stick")
        XCTAssertNil(vm.lastError)
    }

    func test_preheat_bedUnsupported_hotendTargetConfirms_bedIgnored() async throws {
        var base = try idlePrinter()
        base.hotendTarget = 200 // at PLA hotend; the bed is uncontrollable here
        let vm = makeViewModel(printer: base, capabilities: Self.noBedCaps)
        await vm.loadCapabilities()

        await vm.preheat(.pla)

        XCTAssertFalse(vm.isExecuting,
                       "Hotend target alone must confirm when the bed is uncontrollable — never wait on an impossible bed value")
        XCTAssertNil(vm.lastError)
    }

    // MARK: - Non-matching cached targets keep waiting for live evidence

    func test_preheat_successWithNonmatchingCachedTargets_staysPendingUntilMatchingSnapshot() async throws {
        let base = try idlePrinter() // targets 215/60; PLA requests 200/60
        let vm = makeViewModel(printer: base, capabilities: Self.fullCaps)
        await vm.loadCapabilities()

        await vm.preheat(.pla)
        XCTAssertTrue(vm.isExecuting,
                      "Cached targets don't match the request yet — a successful send must stay pending")

        var warmed = base
        warmed.hotendTarget = 200 // now matches the requested hotend target
        vm.handlePrinterUpdate(warmed)
        XCTAssertFalse(vm.isExecuting, "A matching target snapshot confirms the command")
    }

    func test_preheat_measuredTempDriftWithNonmatchingTargets_staysPending() async throws {
        let base = try idlePrinter() // targets 215/60; PLA requests 200/60
        let vm = makeViewModel(printer: base, capabilities: Self.fullCaps)
        await vm.loadCapabilities()

        await vm.preheat(.pla)
        XCTAssertTrue(vm.isExecuting)

        var drifted = base
        drifted.hotendTemp = 199 // measured only; targets still 215/60 (≠ 200/60)
        drifted.bedTemp = 58
        vm.handlePrinterUpdate(drifted)
        XCTAssertTrue(vm.isExecuting,
                      "Measured-temperature drift with non-matching targets must not confirm a preheat")
    }

    // MARK: - Single-flight identity under a stale response race

    func test_staleSuccessResponse_doesNotClearNewerPendingCommand() async throws {
        let gate = AsyncGate()
        mockService.beforeSetTemperatures = { await gate.wait() }
        let base = try idlePrinter()
        let vm = makeViewModel(printer: base, capabilities: Self.fullCaps)
        await vm.loadCapabilities()

        // C1: a preheat blocked in-flight at the gate.
        async let first: Void = vm.preheat(.pla)
        while await !gate.hasWaiters { await Task.yield() }

        // Live evidence confirms and clears C1 *before* its HTTP response lands.
        var warmed = base
        warmed.hotendTarget = 200
        vm.handlePrinterUpdate(warmed)
        XCTAssertFalse(vm.isExecuting, "Live evidence cleared C1")

        // C2: a new jog begins and becomes the pending command.
        await vm.jog(axis: "X", distanceMm: 10)
        guard case .jog = vm.pendingCommand?.kind else {
            return XCTFail("Expected a pending jog (C2) after the preheat cleared")
        }

        // C1's stale success finally returns; it must not clear the newer C2.
        await gate.open()
        await first
        guard case .jog = vm.pendingCommand?.kind else {
            return XCTFail("A stale preheat response cleared the newer jog command")
        }
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
