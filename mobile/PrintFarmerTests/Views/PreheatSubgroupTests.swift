import XCTest
import SwiftUI
@testable import PrintFarmer

/// Smoke tests for `PreheatSubgroup` (issue #284). Snapshot tests are
/// intentionally deferred to issue #289; these verify the public seams the
/// view exposes for rendering decisions.
@MainActor
final class PreheatSubgroupTests: XCTestCase {

    // MARK: - presets

    func test_presets_containsAllFourInFixedOrder() {
        XCTAssertEqual(PreheatSubgroup.presets, [.pla, .petg, .abs, .coolDown])
        XCTAssertEqual(PreheatSubgroup.presets.count, 4)
    }

    // MARK: - isVisible(capabilities:)

    func test_isVisible_trueWhenCapabilitiesNil() {
        // Capabilities haven't loaded yet — fail open and render.
        XCTAssertTrue(PreheatSubgroup.isVisible(capabilities: nil))
    }

    func test_isVisible_trueForFullCaps() {
        let caps = PrinterBackendCapabilities.fallback(for: .moonraker)
        XCTAssertTrue(caps.supportsTemperatureControl)
        XCTAssertTrue(PreheatSubgroup.isVisible(capabilities: caps))
    }

    func test_isVisible_trueForFlashForge_hotendOnly() {
        // FlashForge supports temperature control on the hotend but not the
        // bed. The subgroup must still render — caption inside notes the
        // hotend-only state.
        let caps = PrinterBackendCapabilities.fallback(for: .flashForge)
        XCTAssertTrue(caps.supportsTemperatureControl)
        XCTAssertFalse(caps.supportsBedTemperature)
        XCTAssertTrue(PreheatSubgroup.isVisible(capabilities: caps))
    }

    func test_isVisible_falseWhenTemperatureControlMissing() {
        // SDCP backend reports no temperature control — the entire subgroup
        // must be hidden.
        let caps = PrinterBackendCapabilities.fallback(for: .sdcp)
        XCTAssertFalse(caps.supportsTemperatureControl)
        XCTAssertFalse(PreheatSubgroup.isVisible(capabilities: caps))
    }

    // MARK: - canControl gate (the disabled-state input)

    func test_canControl_falseWhilePrinting() throws {
        // When the printer is printing, the ViewModel reports canControl=false
        // and the subgroup renders all four buttons in the disabled visual
        // state. We assert the gate the view consumes; the visual treatment is
        // covered by the upcoming snapshot tests (#289).
        let printer = try Self.makePrinter(state: "printing", isOnline: true)
        let vm = PrinterControlsViewModel(printerService: PreheatSubgroupTestService(), printer: printer)
        XCTAssertFalse(vm.canControl)
        XCTAssertNotNil(vm.blockedReason)
    }

    func test_canControl_falseWhenOffline() throws {
        let printer = try Self.makePrinter(state: "ready", isOnline: false)
        let vm = PrinterControlsViewModel(printerService: PreheatSubgroupTestService(), printer: printer)
        XCTAssertFalse(vm.canControl)
        XCTAssertEqual(vm.blockedReason, "Printer is offline.")
    }

    func test_canControl_trueWhenOnlineAndIdle() throws {
        let printer = try Self.makePrinter(state: "ready", isOnline: true)
        let vm = PrinterControlsViewModel(printerService: PreheatSubgroupTestService(), printer: printer)
        XCTAssertTrue(vm.canControl)
        XCTAssertNil(vm.blockedReason)
    }

    // MARK: - body returns a non-nil view

    func test_body_doesNotCrashWhenVisible() throws {
        let printer = try Self.makePrinter(state: "ready", isOnline: true)
        let vm = PrinterControlsViewModel(printerService: PreheatSubgroupTestService(), printer: printer)
        let subgroup = PreheatSubgroup(viewModel: vm)
        // SwiftUI body evaluation should not throw or trap.
        _ = subgroup.body
    }


    // MARK: - Accessibility labels (spec §4.1)

    func test_accessibilityLabel_idle_pla() throws {
        let vm = try makeVM(state: "ready", isOnline: true)
        let view = PreheatSubgroup(viewModel: vm)
        XCTAssertEqual(view.accessibilityLabel(preset: .pla, isPending: false), "Preheat for PLA")
    }

    func test_accessibilityLabel_idle_petg() throws {
        let vm = try makeVM(state: "ready", isOnline: true)
        let view = PreheatSubgroup(viewModel: vm)
        XCTAssertEqual(view.accessibilityLabel(preset: .petg, isPending: false), "Preheat for PETG")
    }

    func test_accessibilityLabel_idle_abs() throws {
        let vm = try makeVM(state: "ready", isOnline: true)
        let view = PreheatSubgroup(viewModel: vm)
        XCTAssertEqual(view.accessibilityLabel(preset: .abs, isPending: false), "Preheat for ABS")
    }

    func test_accessibilityLabel_idle_coolDown() throws {
        let vm = try makeVM(state: "ready", isOnline: true)
        let view = PreheatSubgroup(viewModel: vm)
        XCTAssertEqual(view.accessibilityLabel(preset: .coolDown, isPending: false), "Cool down")
    }

    func test_accessibilityLabel_pending_pla() throws {
        let vm = try makeVM(state: "ready", isOnline: true)
        let view = PreheatSubgroup(viewModel: vm)
        XCTAssertEqual(view.accessibilityLabel(preset: .pla, isPending: true), "Preheat for PLA, in progress")
    }

    func test_accessibilityLabel_pending_coolDown() throws {
        let vm = try makeVM(state: "ready", isOnline: true)
        let view = PreheatSubgroup(viewModel: vm)
        XCTAssertEqual(view.accessibilityLabel(preset: .coolDown, isPending: true), "Cooling down, in progress")
    }

    // MARK: - Accessibility hints (spec §4.1)

    func test_accessibilityHint_idle_pla_withBed() throws {
        let vm = try makeVM(state: "ready", isOnline: true)
        let view = PreheatSubgroup(viewModel: vm)
        XCTAssertEqual(
            view.accessibilityHint(preset: .pla, canControl: true, hasError: false),
            "Sets hotend to 200 degrees, bed to 60 degrees."
        )
    }

    func test_accessibilityHint_idle_petg_withBed() throws {
        let vm = try makeVM(state: "ready", isOnline: true)
        let view = PreheatSubgroup(viewModel: vm)
        XCTAssertEqual(
            view.accessibilityHint(preset: .petg, canControl: true, hasError: false),
            "Sets hotend to 240 degrees, bed to 80 degrees."
        )
    }

    func test_accessibilityHint_idle_coolDown() throws {
        let vm = try makeVM(state: "ready", isOnline: true)
        let view = PreheatSubgroup(viewModel: vm)
        XCTAssertEqual(
            view.accessibilityHint(preset: .coolDown, canControl: true, hasError: false),
            "Sets hotend and bed to 0 degrees."
        )
    }

    func test_accessibilityHint_disabled_returnsSpec41Text() throws {
        let vm = try makeVM(state: "printing", isOnline: true)
        let view = PreheatSubgroup(viewModel: vm)
        XCTAssertEqual(
            view.accessibilityHint(preset: .pla, canControl: false, hasError: false),
            "Disabled while printing."
        )
    }

    // MARK: - Accessibility value (spec §4.1)

    func test_accessibilityValue_pending_returnsPending() throws {
        let vm = try makeVM(state: "ready", isOnline: true)
        let view = PreheatSubgroup(viewModel: vm)
        XCTAssertEqual(view.accessibilityValue(isPending: true, hasError: false), "Pending")
    }

    func test_accessibilityValue_error_returnsFailed() throws {
        let vm = try makeVM(state: "ready", isOnline: true)
        let view = PreheatSubgroup(viewModel: vm)
        XCTAssertEqual(view.accessibilityValue(isPending: false, hasError: true), "Failed")
    }

    func test_accessibilityValue_idle_isEmpty() throws {
        let vm = try makeVM(state: "ready", isOnline: true)
        let view = PreheatSubgroup(viewModel: vm)
        XCTAssertEqual(view.accessibilityValue(isPending: false, hasError: false), "")
    }

    // MARK: - Helpers (accessibility tests)

    private func makeVM(state: String, isOnline: Bool) throws -> PrinterControlsViewModel {
        let printer = try Self.makePrinter(state: state, isOnline: isOnline)
        return PrinterControlsViewModel(printerService: PreheatSubgroupTestService(), printer: printer)
    }

    // MARK: - Helpers

    private static func makePrinter(state: String, isOnline: Bool) throws -> Printer {
        let json = """
        {
            "id": "11111111-1111-1111-1111-111111111111",
            "name": "Test Printer",
            "backend": "moonraker",
            "backendPort": 80,
            "inMaintenance": false,
            "isEnabled": true,
            "isOnline": \(isOnline),
            "state": "\(state)",
            "obicoEnabled": false
        }
        """
        return try JSONDecoder().decode(Printer.self, from: Data(json.utf8))
    }
}

/// Bare-bones service stub. The view-layer smoke tests don't exercise any
/// network-flavored code path; the existing `MockPrinterService` is overkill
/// here. Local stub keeps these tests narrowly scoped to the view.
private final class PreheatSubgroupTestService: PrinterServiceProtocol, @unchecked Sendable {
    func list(includeDisabled: Bool) async throws -> [Printer] { [] }
    func get(id: UUID) async throws -> Printer { throw NetworkError.notFound }
    func getStatus(id: UUID) async throws -> PrinterStatusDetail { throw NetworkError.notFound }
    func listCameraUrls() async throws -> [PrinterCameraUrls] { [] }
    func getCameraUrl(id: UUID) async throws -> PrinterCameraUrl { throw NetworkError.notFound }
    func getSnapshot(id: UUID) async throws -> Data { Data() }
    func getCurrentJob(id: UUID) async throws -> PrintJobStatusInfo? { nil }
    func pause(id: UUID) async throws -> CommandResult { CommandResult(success: true, message: nil) }
    func resume(id: UUID) async throws -> CommandResult { CommandResult(success: true, message: nil) }
    func cancel(id: UUID) async throws -> CommandResult { CommandResult(success: true, message: nil) }
    func stop(id: UUID) async throws -> CommandResult { CommandResult(success: true, message: nil) }
    func emergencyStop(id: UUID) async throws -> CommandResult { CommandResult(success: true, message: nil) }
    func setMaintenanceMode(id: UUID, inMaintenance: Bool) async throws -> Printer {
        throw NetworkError.notFound
    }
    func getQueueOverview(model: String?, nozzle: Double?, material: String?) async throws -> [QueueOverview] { [] }
    func setActiveSpool(printerId: UUID, spoolId: Int?) async throws -> CommandResult {
        CommandResult(success: true, message: nil)
    }
    func listAvailableSpools(printerId: UUID) async throws -> [SpoolmanSpool] { [] }
    func loadFilament(printerId: UUID) async throws -> CommandResult { CommandResult(success: true, message: nil) }
    func unloadFilament(printerId: UUID) async throws -> CommandResult { CommandResult(success: true, message: nil) }
    func changeFilament(printerId: UUID) async throws -> CommandResult { CommandResult(success: true, message: nil) }
    func setTemperatures(printerId: UUID, hotend: Double?, bed: Double?) async throws {}
    func home(printerId: UUID, axes: [String]) async throws {}
    func homeXY(printerId: UUID) async throws {}
    func homeZ(printerId: UUID) async throws {}
    func move(printerId: UUID, axis: String, distanceMm: Double, feedrateMmMin: Int) async throws {}
    func getBackendCapabilities(printerId: UUID) async throws -> PrinterBackendCapabilities {
        PrinterBackendCapabilities.fallback(for: .moonraker)
    }
}
