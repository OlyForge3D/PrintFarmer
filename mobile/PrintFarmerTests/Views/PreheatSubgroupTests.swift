import XCTest
import SwiftUI
@testable import PrintFarmer

/// Smoke tests for `PreheatSubgroup` (issue #284). Snapshot tests are
/// intentionally deferred to issue #289; these verify the public seams the
/// view exposes for rendering decisions.
@MainActor
final class PreheatSubgroupTests: XCTestCase {

    func test_thermalPhoneHeight() async throws {
        let (model, _) = try await thermalModel()
        let controller = UIHostingController(rootView: thermalContent(model, width: 326))
        let size = controller.sizeThatFits(in: CGSize(width: 326, height: 10000))
        // Measured at d34ae50688 on iOS 26.5 (23F77), same fixture/width/type.
        let priorPhoneHeight: CGFloat = 642.6667
        print("THERMAL_PHONE_HEIGHT width=\(size.width) height=\(size.height) prior=\(priorPhoneHeight)")
        XCTAssertLessThan(size.height, priorPhoneHeight / 2)
        XCTAssertGreaterThan(size.height, 88, "Both heater rows must remain visible")
    }

    func test_thermalRows_haveInlinePhoneActionsAndReadableAdaptiveEvidence() async throws {
        let (model, service) = try await thermalModel()
        var readings = model.printer
        readings.hotendTemp = 192.5
        readings.bedTemp = 37
        model.handlePrinterUpdate(readings)
        for (width, type): (CGFloat, DynamicTypeSize) in [
            (326, .large), (448, .large), (256, .large), (326, .accessibility5)
        ] {
            let (window, controller) = try await installThermal(model, width: width, type: type)
            defer { window.isHidden = true }
            let controls = nativeControls(controller.view)
            XCTAssertEqual(controls.count, 6)
            for heater in Heater.allCases {
                let field = try XCTUnwrap(controls.first {
                    $0.accessibilityIdentifier == "printer.controls.\(heater.rawValue).target"
                } as? UITextField)
                let set = try thermalButton(controller, heater: heater, action: "set")
                let off = try thermalButton(controller, heater: heater, action: "off")
                XCTAssertEqual(set.accessibilityLabel, "Set \(heater.title.lowercased()) target")
                XCTAssertEqual(off.accessibilityLabel, "Turn \(heater.title.lowercased()) off")
                XCTAssertEqual(field.accessibilityLabel, "\(heater.title) target in degrees Celsius")
                let fieldFrame = field.convert(field.bounds, to: controller.view)
                let setFrame = set.convert(set.bounds, to: controller.view)
                let offFrame = off.convert(off.bounds, to: controller.view)
                if type == .large && width >= 326 {
                    XCTAssertEqual(fieldFrame.maxY, setFrame.maxY, accuracy: 1)
                    XCTAssertEqual(fieldFrame.maxY, offFrame.maxY, accuracy: 1)
                    XCTAssertLessThanOrEqual(fieldFrame.maxX, setFrame.minX)
                } else {
                    XCTAssertLessThanOrEqual(fieldFrame.maxY, setFrame.minY)
                }
                XCTAssertLessThanOrEqual(setFrame.maxX, offFrame.minX)
                for control in [field, set, off] as [UIControl] {
                    let frame = control.convert(control.bounds, to: controller.view)
                    XCTAssertGreaterThanOrEqual(control.bounds.width, 44)
                    XCTAssertGreaterThanOrEqual(control.bounds.height, 44)
                    XCTAssertGreaterThanOrEqual(frame.minX, -1)
                    XCTAssertLessThanOrEqual(frame.maxX, width + 1)
                }
                XCTAssertGreaterThanOrEqual(field.bounds.height, try XCTUnwrap(field.font).lineHeight)
                for button in [set, off] {
                    let label = try XCTUnwrap(button.titleLabel)
                    XCTAssertGreaterThanOrEqual(label.bounds.height + 1, try XCTUnwrap(label.font).lineHeight)
                }
            }
            let image = UIGraphicsImageRenderer(bounds: controller.view.bounds).image { _ in
                XCTAssertTrue(controller.view.drawHierarchy(in: controller.view.bounds, afterScreenUpdates: true))
            }
            let attachment = XCTAttachment(image: image)
            attachment.name = "compact-thermal-\(Int(width))-\(type)"
            attachment.lifetime = .keepAlways
            add(attachment)
        }
        XCTAssertNil(service.setTemperaturesCalledWith)
    }

    func test_thermalSetAndOff_preserveValidationOmissionAndPendingLock() async throws {
        let (model, service) = try await thermalModel()
        let (window, controller) = try await installThermal(model)
        defer { window.isHidden = true }
        let field = try XCTUnwrap(nativeControls(controller.view).first {
            $0.accessibilityIdentifier == "printer.controls.hotend.target"
        } as? UITextField)
        let set = try thermalButton(controller, heater: .hotend, action: "set")
        for invalid in ["", "200.5", "-1", "NaN", "99999"] {
            field.text = invalid
            field.sendActions(for: .editingChanged)
            try await settle(controller)
            set.sendActions(for: .touchUpInside)
            try await settle(controller)
            XCTAssertNil(service.setTemperaturesCalledWith, invalid)
        }
        field.text = "205"
        field.sendActions(for: .editingChanged)
        try await settle(controller)
        set.sendActions(for: .touchUpInside)
        try await settle(controller)
        XCTAssertEqual(service.setTemperaturesCalledWith?.hotend, 205)
        XCTAssertNil(service.setTemperaturesCalledWith?.bed)
        XCTAssertTrue(model.isExecuting)
        XCTAssertTrue(nativeControls(controller.view).allSatisfy { !$0.isEnabled })
        model.cancelPendingCommand()
        try await settle(controller)
        try thermalButton(controller, heater: .bed, action: "off").sendActions(for: .touchUpInside)
        try await settle(controller)
        XCTAssertNil(service.setTemperaturesCalledWith?.hotend)
        XCTAssertEqual(service.setTemperaturesCalledWith?.bed, 0)
        model.cancelPendingCommand()
    }

    func test_thermalOff_withUnknownMaximum_ignoresDraftButHonorsOfflineLock() async throws {
        let (model, service) = try await thermalModel(knownLimits: false)
        let (window, controller) = try await installThermal(model)
        defer { window.isHidden = true }
        let field = try XCTUnwrap(nativeControls(controller.view).first {
            $0.accessibilityIdentifier == "printer.controls.hotend.target"
        } as? UITextField)
        field.text = "205"
        field.sendActions(for: .editingChanged)
        try await settle(controller)
        try thermalButton(controller, heater: .hotend, action: "set").sendActions(for: .touchUpInside)
        try await settle(controller)
        XCTAssertNil(service.setTemperaturesCalledWith)
        try thermalButton(controller, heater: .hotend, action: "off").sendActions(for: .touchUpInside)
        try await settle(controller)
        XCTAssertEqual(service.setTemperaturesCalledWith?.hotend, 0)
        XCTAssertNil(service.setTemperaturesCalledWith?.bed)
        XCTAssertEqual(field.text, "205", "Off does not reinterpret or submit the draft")
        model.cancelPendingCommand()
        var offline = model.printer
        offline.isOnline = false
        model.handlePrinterUpdate(offline)
        try await settle(controller)
        XCTAssertTrue(nativeControls(controller.view).allSatisfy { !$0.isEnabled })
    }

    func test_thermalDraft_survivesAccessibilityReflowAndUnconfirmedBedIsOmitted() async throws {
        let (model, _) = try await thermalModel(hotendOnly: true)
        let (window, controller) = try await installThermal(model)
        defer { window.isHidden = true }
        let field = try XCTUnwrap(nativeControls(controller.view).first as? UITextField)
        field.text = "231"
        field.sendActions(for: .editingChanged)
        try await settle(controller)
        controller.rootView = AnyView(thermalContent(model, width: 326, type: .accessibility5))
        try await settle(controller)
        let controls = nativeControls(controller.view)
        XCTAssertEqual(controls.count, 3)
        XCTAssertEqual((controls.first as? UITextField)?.text, "231")
        XCTAssertFalse(controls.contains { $0.accessibilityIdentifier?.contains(".bed.") == true })
    }

    private func thermalModel(
        knownLimits: Bool = true, hotendOnly: Bool = false
    ) async throws -> (PrinterControlsViewModel, MockPrinterService) {
        var printer = try TestData.decodePrinter()
        printer.state = "ready"
        let service = MockPrinterService()
        service.capabilitiesToReturn = hotendOnly ? .hotendOnlyFixture : .allControlsFixture
        service.detailsToReturn = knownLimits ? .controlsLimitsFixture(for: printer) : nil
        let model = PrinterControlsViewModel.configuredForTests(printerService: service, printer: printer)
        await model.loadCapabilities()
        return (model, service)
    }

    private func thermalContent(
        _ model: PrinterControlsViewModel, width: CGFloat, type: DynamicTypeSize = .large
    ) -> some View {
        VStack(alignment: .leading, spacing: 16) {
            PreheatSubgroup(viewModel: model)
            PreheatSubgroup.IndividualHeaterControls(viewModel: model)
        }
        .environment(\.dynamicTypeSize, type)
        .environment(\.horizontalSizeClass, width > 390 ? .regular : .compact)
        .frame(width: width)
        .fixedSize(horizontal: false, vertical: true)
        .background(Color.pfCard)
        .ignoresSafeArea()
    }

    private func installThermal(
        _ model: PrinterControlsViewModel, width: CGFloat = 326, type: DynamicTypeSize = .large
    ) async throws -> (UIWindow, UIHostingController<AnyView>) {
        let controller = UIHostingController(rootView: AnyView(thermalContent(model, width: width, type: type)))
        let size = controller.sizeThatFits(in: CGSize(width: width, height: 10000))
        let window = UIWindow()
        window.windowScene = UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }.first
        window.frame = CGRect(origin: .zero, size: size)
        window.rootViewController = controller
        window.makeKeyAndVisible()
        try await settle(controller)
        return (window, controller)
    }

    private func settle(_ controller: UIViewController) async throws {
        try await Task.sleep(for: .milliseconds(100))
        controller.view.setNeedsLayout()
        controller.view.layoutIfNeeded()
    }

    private func nativeControls(_ view: UIView) -> [UIControl] {
        (view as? UIControl).map { [$0] } ?? view.subviews.flatMap { nativeControls($0) }
    }

    private func thermalButton(
        _ controller: UIViewController, heater: Heater, action: String
    ) throws -> UIButton {
        try XCTUnwrap(nativeControls(controller.view).first {
            $0.accessibilityIdentifier == "printer.controls.\(heater.rawValue).\(action)"
        } as? UIButton)
    }

    func test_heaterInput_requiresWholeDegreesWithoutSilentRounding() throws {
        XCTAssertNil(try ControlNumberInput.heaterTarget(" "))
        for (text, value) in [("0", 0.0), ("200", 200.0), ("240.000", 240.0), ("2e2", 200.0)] {
            XCTAssertEqual(try ControlNumberInput.heaterTarget(text), value)
        }
        for text in ["200.5", "0.1", "200.00000000000000001", "1e-999", "NaN", "inf"] {
            XCTAssertThrowsError(try ControlNumberInput.heaterTarget(text), text)
        }
    }

    func test_individualHeaterLabels_preserveUnknownVersusZero() {
        XCTAssertEqual(PreheatSubgroup.HeaterTargetEditor.temperatureText(nil), "Unknown")
        XCTAssertEqual(PreheatSubgroup.HeaterTargetEditor.temperatureText(.nan), "Unknown")
        XCTAssertEqual(PreheatSubgroup.HeaterTargetEditor.temperatureText(0), "0 °C")
        XCTAssertEqual(Heater.hotend.title, "Hotend")
        XCTAssertEqual(Heater.bed.title, "Bed")
    }

    func test_nativeNumberFieldCoordinator_preservesBlankZeroAndSignedInput() {
        var value = ""
        let coordinator = ControlNumberField.Coordinator(text: Binding(get: { value }, set: { value = $0 }))
        let field = UITextField()
        for input in ["0", "-1.25", "240", ""] {
            field.text = input
            coordinator.changed(field)
            XCTAssertEqual(value, input)
        }
        field.text = nil
        coordinator.changed(field)
        XCTAssertEqual(value, "")
    }

    // MARK: - presets

    func test_presets_containsAllFourInFixedOrder() {
        XCTAssertEqual(PreheatSubgroup.presets, [.pla, .petg, .abs, .coolDown])
        XCTAssertEqual(PreheatSubgroup.presets.count, 4)
    }

    // MARK: - isVisible(capabilities:)

    func test_isVisible_falseWhenCapabilitiesNil() {
        XCTAssertFalse(PreheatSubgroup.isVisible(capabilities: nil))
    }

    func test_isVisible_trueForFullCaps() {
        let caps = PrinterBackendCapabilities.allControlsFixture
        XCTAssertTrue(caps.supportsTemperatureControl)
        XCTAssertTrue(PreheatSubgroup.isVisible(capabilities: caps))
    }

    func test_isVisible_trueForExplicitHotendOnlyEvidence() {
        let caps = PrinterBackendCapabilities.hotendOnlyFixture
        XCTAssertTrue(caps.supportsTemperatureControl)
        XCTAssertFalse(caps.supportsBedTemperature)
        XCTAssertTrue(PreheatSubgroup.isVisible(capabilities: caps))
    }

    func test_isVisible_falseWhenTemperatureControlMissing() {
        let caps = PrinterBackendCapabilities.fallback(for: .unknown)
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
        let vm = PrinterControlsViewModel.configuredForTests(printerService: PreheatSubgroupTestService(), printer: printer)
        XCTAssertFalse(vm.canControl)
        XCTAssertNotNil(vm.blockedReason)
    }

    func test_canControl_falseWhenOffline() throws {
        let printer = try Self.makePrinter(state: "ready", isOnline: false)
        let vm = PrinterControlsViewModel.configuredForTests(printerService: PreheatSubgroupTestService(), printer: printer)
        XCTAssertFalse(vm.canControl)
        XCTAssertEqual(vm.blockedReason, "Printer is offline.")
    }

    func test_canControl_trueWhenOnlineAndIdle() throws {
        let printer = try Self.makePrinter(state: "ready", isOnline: true)
        let vm = PrinterControlsViewModel.configuredForTests(printerService: PreheatSubgroupTestService(), printer: printer)
        XCTAssertTrue(vm.canControl)
        XCTAssertNil(vm.blockedReason)
    }

    // MARK: - body returns a non-nil view

    func test_body_doesNotCrashWhenVisible() throws {
        let printer = try Self.makePrinter(state: "ready", isOnline: true)
        let vm = PrinterControlsViewModel.configuredForTests(printerService: PreheatSubgroupTestService(), printer: printer)
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

    func test_accessibilityHint_idle_pla_withBed() async throws {
        let vm = try makeVM(state: "ready", isOnline: true)
        await vm.loadCapabilities()
        let view = PreheatSubgroup(viewModel: vm)
        XCTAssertEqual(
            view.accessibilityHint(preset: .pla, canControl: true, hasError: false),
            "Sets hotend to 200 degrees, bed to 60 degrees."
        )
    }

    func test_accessibilityHint_idle_petg_withBed() async throws {
        let vm = try makeVM(state: "ready", isOnline: true)
        await vm.loadCapabilities()
        let view = PreheatSubgroup(viewModel: vm)
        XCTAssertEqual(
            view.accessibilityHint(preset: .petg, canControl: true, hasError: false),
            "Sets hotend to 240 degrees, bed to 80 degrees."
        )
    }

    func test_accessibilityHint_idle_coolDown() async throws {
        let vm = try makeVM(state: "ready", isOnline: true)
        await vm.loadCapabilities()
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
        return PrinterControlsViewModel.configuredForTests(printerService: PreheatSubgroupTestService(), printer: printer)
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
    func getHistory(id: UUID, limit: Int?) async throws -> PrinterHistoryList { PrinterHistoryList(count: 0, jobs: []) }
    func pause(id: UUID) async throws -> CommandResult { CommandResult(success: true, message: nil) }
    func resume(id: UUID) async throws -> CommandResult { CommandResult(success: true, message: nil) }
    func cancel(id: UUID) async throws -> CommandResult { CommandResult(success: true, message: nil) }
    func stop(id: UUID) async throws -> CommandResult { CommandResult(success: true, message: nil) }
    func emergencyStop(id: UUID) async throws -> CommandResult { CommandResult(success: true, message: nil) }
    func setMaintenanceMode(
        id: UUID,
        inMaintenance: Bool,
        reviewedRowVersion: String
    ) async throws -> Printer {
        throw NetworkError.notFound
    }
    func getQueueOverview(model: String?, nozzle: Double?, material: String?) async throws -> [QueueOverview] { [] }
    func setActiveSpool(
        printerId: UUID,
        spoolId: Int?,
        reviewedRowVersion: String
    ) async throws -> CommandResult {
        CommandResult(success: true, message: nil)
    }
    func bindToolheadSpool(printerId: UUID, toolheadIndex: Int, request: ToolheadSpoolBindRequest, idempotencyKey: String) async throws -> CommandResult {
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
    func moveTo(printerId: UUID, x: Double?, y: Double?, z: Double?, feedrateMmMin: Int?) async throws -> CommandResult { throw NetworkError.notFound }
    func extrude(printerId: UUID, distanceMm: Double, feedrateMmPerMinute: Int) async throws -> CommandResult { throw NetworkError.notFound }
    func disableMotors(printerId: UUID) async throws -> CommandResult { throw NetworkError.notFound }
    func saveZOffset(printerId: UUID, offsetMm: Double, saveToFirmware: Bool, reviewedRowVersion: String) async throws -> CommandResult { throw NetworkError.notFound }
    func unloadFilament(printerId: UUID, toolheadIndex: Int?) async throws -> FilamentUnloadResult { throw NetworkError.notFound }
    func getBackendCapabilities(printerId: UUID) async throws -> PrinterBackendCapabilities {
        PrinterBackendCapabilities.allControlsFixture
    }

    // #711 F6 stubs — not exercised by preheat tests but required for conformance.
    func getDetails(id: UUID) async throws -> PrinterDetails { throw NetworkError.notFound }
    func listFallbackGroups(printerId: UUID) async throws -> [FilamentFallbackGroup] { [] }
    func getFallbackGroup(printerId: UUID, groupId: UUID) async throws -> FilamentFallbackGroup { throw NetworkError.notFound }
    func createFallbackGroup(printerId: UUID, _ request: CreateFilamentFallbackGroupRequest) async throws -> FilamentFallbackGroup { throw NetworkError.notFound }
    func updateFallbackGroup(printerId: UUID, groupId: UUID, _ request: UpdateFilamentFallbackGroupRequest) async throws -> FilamentFallbackGroup { throw NetworkError.notFound }
    func deleteFallbackGroup(printerId: UUID, groupId: UUID) async throws {}
    func getAvailableFallback(printerId: UUID, sourceToolheadId: UUID, material: String) async throws -> AvailableFallbackMember? { nil }
}
