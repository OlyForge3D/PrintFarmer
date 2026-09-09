import XCTest
@testable import PrintFarmer

/// Requested-target correlation for preheat/cool-down commands (issue #706).
///
/// Confirmation compares the live snapshot's commanded *targets* against the
/// concrete targets carried on the pending command — not a delta — so a
/// same-preset preheat or an already-zero cool-down can't hang forever when no
/// target field changes. Cached matches report acceptance, never fresh telemetry.
/// Measured temperatures are irrelevant, and an
/// uncontrollable setpoint (`nil` target, e.g. a bed-less backend) is treated
/// as already satisfied.
@MainActor
final class PrinterControlsTargetCorrelationTests: XCTestCase {

    // MARK: - Helpers

    /// Builds a view model over a per-test `MockPrinterService`. The service is
    /// created and owned by each test (never a shared mutable property) so no
    /// MainActor-isolated state is mutated from the nonisolated
    /// `setUp()`/`tearDown()`.
    private func makeViewModel(
        printer: Printer,
        capabilities: PrinterBackendCapabilities,
        service: MockPrinterService
    ) -> PrinterControlsViewModel {
        service.capabilitiesToReturn = capabilities
        service.detailsToReturn = .controlsLimitsFixture(for: printer)
        return PrinterControlsViewModel(printerService: service, printer: printer)
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

    func test_repeatedPresets_alreadyAtRequestedTargets_reportAcceptanceOnly_notStuck() async throws {
        for preset in [PreheatPreset.pla, .petg, .abs] {
            let service = MockPrinterService()
            var base = try idlePrinter()
            base.hotendTarget = preset.hotend
            base.bedTarget = preset.bed
            let vm = makeViewModel(printer: base, capabilities: Self.fullCaps, service: service)
            await vm.loadCapabilities()
            for _ in 0..<2 {
                await vm.preheat(preset)
                assertAcceptanceOnly(vm)
                XCTAssertEqual(service.setTemperaturesCalledWith?.hotend, preset.hotend)
                XCTAssertEqual(service.setTemperaturesCalledWith?.bed, preset.bed)
                XCTAssertEqual(vm.printer.hotendTemp, base.hotendTemp)
            }
        }
    }

    func test_coolDown_alreadyAtZero_reportsAcceptanceOnly_regardlessOfMeasuredHeat() async throws {
        for measured in [0.0, 84.0] {
            let service = MockPrinterService()
            var base = try idlePrinter()
            base.hotendTarget = 0
            base.bedTarget = 0
            base.hotendTemp = measured
            base.bedTemp = measured
            let vm = makeViewModel(printer: base, capabilities: Self.fullCaps, service: service)
            await vm.loadCapabilities()
            await vm.preheat(.coolDown)
            assertAcceptanceOnly(vm)
            XCTAssertEqual(vm.printer.hotendTemp, measured)
            XCTAssertEqual(service.setTemperaturesCalledWith?.hotend, 0)
            XCTAssertEqual(service.setTemperaturesCalledWith?.bed, 0)
        }
    }

    func test_preheat_bedUnsupported_hotendTargetConfirms_bedIgnored() async throws {
        let service = MockPrinterService()
        var base = try idlePrinter()
        base.hotendTarget = 200 // at PLA hotend; the bed is uncontrollable here
        let vm = makeViewModel(printer: base, capabilities: Self.noBedCaps, service: service)
        await vm.loadCapabilities()

        await vm.preheat(.pla)

        XCTAssertFalse(vm.isExecuting,
                       "Hotend target alone must confirm when the bed is uncontrollable — never wait on an impossible bed value")
        XCTAssertNil(vm.lastError)
        assertAcceptanceOnly(vm)
    }

    func test_individualHeaters_cachedTargets_reportAcceptanceOnly() async throws {
        for heater in Heater.allCases {
            let service = MockPrinterService()
            var base = try idlePrinter()
            base.hotendTarget = 200
            base.bedTarget = 0
            let target = heater == .hotend ? 200.0 : 0.0
            let vm = makeViewModel(printer: base, capabilities: Self.fullCaps, service: service)
            await vm.loadCapabilities()
            await vm.setHeaterTarget(heater, target: target)
            assertAcceptanceOnly(vm)
            XCTAssertEqual(service.setTemperaturesCalledWith?.hotend, heater == .hotend ? target : nil)
            XCTAssertEqual(service.setTemperaturesCalledWith?.bed, heater == .bed ? target : nil)
        }
    }

    func test_absoluteCachedZero_reportsAcceptanceOnlyAndPreservesOmissions() async throws {
        let service = MockPrinterService()
        var base = try idlePrinter()
        base.x = 0
        var caps = Self.fullCaps
        caps.supportsAbsoluteMovement = true
        let vm = makeViewModel(printer: base, capabilities: caps, service: service)
        await vm.loadCapabilities()
        await vm.moveTo(x: 0, y: nil, z: nil, feedrateMmMin: nil)
        assertAcceptanceOnly(vm)
        XCTAssertEqual(service.moveToCalledWith?.x, 0)
        XCTAssertNil(service.moveToCalledWith?.y)
        XCTAssertNil(service.moveToCalledWith?.z)
        XCTAssertEqual(service.moveToCalledWith?.feedrateMmMin, 3000)
    }

    private func assertAcceptanceOnly(
        _ vm: PrinterControlsViewModel, file: StaticString = #filePath, line: UInt = #line
    ) {
        XCTAssertNil(vm.pendingCommand, file: file, line: line)
        XCTAssertNil(vm.lastError, file: file, line: line)
        XCTAssertTrue(vm.commandNotice?.hasPrefix("Request accepted.") == true, file: file, line: line)
        XCTAssertTrue(vm.commandNotice?.contains("fresh physical completion is not confirmed") == true, file: file, line: line)
        XCTAssertFalse(vm.commandNotice?.contains("Matching telemetry") == true, file: file, line: line)
    }

    // MARK: - Non-matching cached targets keep waiting for live evidence

    func test_preheat_successWithNonmatchingCachedTargets_staysPendingUntilMatchingSnapshot() async throws {
        let service = MockPrinterService()
        let base = try idlePrinter() // targets 215/60; PLA requests 200/60
        let vm = makeViewModel(printer: base, capabilities: Self.fullCaps, service: service)
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
        let service = MockPrinterService()
        let base = try idlePrinter() // targets 215/60; PLA requests 200/60
        let vm = makeViewModel(printer: base, capabilities: Self.fullCaps, service: service)
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

    func test_telemetryBeforeResponse_doesNotPermitOverlappingDispatch() async throws {
        let service = MockPrinterService()
        let gate = AsyncGate()
        service.beforeSetTemperatures = { await gate.wait() }
        let base = try idlePrinter()
        let vm = makeViewModel(printer: base, capabilities: Self.fullCaps, service: service)
        await vm.loadCapabilities()

        // C1: a preheat blocked in-flight at the gate.
        async let first: Void = vm.preheat(.pla)
        while await !gate.hasWaiters { await Task.yield() }

        // Telemetry alone must not release the transport's single-flight slot.
        var warmed = base
        warmed.hotendTarget = 200
        vm.handlePrinterUpdate(warmed)
        XCTAssertTrue(vm.isExecuting)

        // C2: a new jog begins and becomes the pending command.
        await vm.jog(axis: "X", distanceMm: 10)
        XCTAssertNil(service.moveCalledWith)

        // C1's stale success finally returns; it must not clear the newer C2.
        await gate.open()
        await first
        XCTAssertNil(vm.pendingCommand)
        XCTAssertEqual(vm.commandNotice, "Matching telemetry received. A heater target is a setpoint, not a measured temperature.")
        await vm.jog(axis: "X", distanceMm: 10)
        guard case .jog = vm.pendingCommand?.kind else {
            return XCTFail("A stale preheat response cleared the newer jog command")
        }
    }

    func test_staleFailureResponse_doesNotClearNewerPendingCommand_norOverwriteError() async throws {
        let service = MockPrinterService()
        let gate = AsyncGate()
        service.beforeSetTemperatures = { await gate.wait() }
        let base = try idlePrinter()
        let vm = makeViewModel(printer: base, capabilities: Self.fullCaps, service: service)
        await vm.loadCapabilities()

        // C1: a preheat blocked in-flight at the gate.
        async let first: Void = vm.preheat(.pla)
        while await !gate.hasWaiters { await Task.yield() }

        // Explicit cancellation abandons waiting, not physical execution.
        var warmed = base
        warmed.hotendTarget = 200
        vm.handlePrinterUpdate(warmed)
        XCTAssertTrue(vm.isExecuting)
        vm.cancelPendingCommand()
        XCTAssertFalse(vm.isExecuting)

        // C2: a new jog begins and becomes the pending command (its move
        // succeeds because no error is armed yet).
        await vm.jog(axis: "X", distanceMm: 10)
        guard case .jog = vm.pendingCommand?.kind else {
            return XCTFail("Expected a pending jog (C2) after the preheat cleared")
        }
        XCTAssertNil(vm.lastError, "C2 started cleanly with no error")

        // Arm the error so C1 fails *late* when it resumes past the gate.
        service.errorToThrow = NetworkError.serverError(500)
        await gate.open()
        await first

        // C1's stale failure must neither clear C2 nor surface its own error.
        guard case .jog = vm.pendingCommand?.kind else {
            return XCTFail("A stale preheat failure cleared the newer jog command")
        }
        XCTAssertTrue(vm.isExecuting, "C2 must remain pending")
        XCTAssertNil(vm.lastError,
                     "A stale failure from an already-confirmed C1 must not overwrite current error state")
    }

    func test_currentCommandFailure_recordsError_andClearsPending() async throws {
        let service = MockPrinterService()
        let vm = makeViewModel(printer: try idlePrinter(), capabilities: Self.fullCaps, service: service)
        await vm.loadCapabilities()
        service.errorToThrow = NetworkError.serverError(500)

        await vm.preheat(.pla)

        XCTAssertNotNil(service.setTemperaturesCalledWith)
        XCTAssertNil(vm.pendingCommand, "The current command's failure clears pending for retry")
        XCTAssertFalse(vm.isExecuting)
        XCTAssertEqual(vm.lastError?.isRetryable, true, "The current command's error is surfaced")
        XCTAssertTrue(vm.lastError?.message.contains("Outcome may be unknown") == true)
    }

    func test_individualTarget_ignoresMeasurementsOtherHeaterAndPosition() async throws {
        let service = MockPrinterService()
        let base = try idlePrinter()
        let vm = makeViewModel(printer: base, capabilities: Self.fullCaps, service: service)
        await vm.loadCapabilities()
        await vm.setHeaterTarget(.hotend, target: 220)
        var update = base
        update.hotendTemp = 220
        update.bedTarget = 220
        update.x = 99
        vm.handlePrinterUpdate(update)
        XCTAssertNotNil(vm.pendingCommand)
        update.hotendTarget = 220
        vm.handlePrinterUpdate(update)
        XCTAssertNil(vm.pendingCommand)
    }

    func test_absolute_requiresAllRequestedAxes_notUnrelatedNoise() async throws {
        let service = MockPrinterService()
        let base = try idlePrinter()
        var caps = Self.fullCaps
        caps.supportsAbsoluteMovement = true
        let vm = makeViewModel(printer: base, capabilities: caps, service: service)
        await vm.loadCapabilities()
        await vm.moveTo(x: 0, y: nil, z: 2, feedrateMmMin: nil)
        var update = base
        update.y = 50
        update.hotendTarget = 0
        vm.handlePrinterUpdate(update)
        XCTAssertNotNil(vm.pendingCommand)
        update.x = 0
        update.z = 1
        vm.handlePrinterUpdate(update)
        XCTAssertNotNil(vm.pendingCommand)
        update.z = 2
        vm.handlePrinterUpdate(update)
        XCTAssertNil(vm.pendingCommand)
    }

    func test_homeZ_doesNotResolveOnUnrelatedHomingOrMissingTelemetry() throws {
        var base = try idlePrinter()
        base.homedAxes = nil
        let command = ControlCommand(kind: .home(axes: ["Z"]), startedAt: Date())
        var update = base
        update.homedAxes = "xy"
        XCTAssertFalse(PrinterControlsViewModel.transition(from: base, to: update, resolves: command))
        update.homedAxes = nil
        XCTAssertFalse(PrinterControlsViewModel.transition(from: base, to: update, resolves: command))
        update.homedAxes = "xyz"
        XCTAssertTrue(PrinterControlsViewModel.transition(from: base, to: update, resolves: command))
    }

    func test_offlineReconnect_abandonsPendingWithoutReplaying() async throws {
        let service = MockPrinterService()
        let base = try idlePrinter()
        let vm = makeViewModel(printer: base, capabilities: Self.fullCaps, service: service)
        await vm.loadCapabilities()
        await vm.setHeaterTarget(.hotend, target: 220)
        var offline = base
        offline.isOnline = false
        vm.handlePrinterUpdate(offline)
        XCTAssertNil(vm.pendingCommand)
        XCTAssertTrue(vm.commandNotice?.contains("may still execute") == true)
        service.setTemperaturesCalledWith = nil
        vm.handlePrinterUpdate(base)
        XCTAssertNil(service.setTemperaturesCalledWith)
        XCTAssertNil(vm.pendingCommand)
    }

    func test_sameTimestampCommands_haveDistinctInvocationIdentity() {
        let date = Date()
        let first = ControlCommand(kind: .heater(.bed, target: 60), startedAt: date)
        let second = ControlCommand(kind: .heater(.bed, target: 60), startedAt: date)
        XCTAssertNotEqual(first, second)
        XCTAssertEqual(first, first)
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
