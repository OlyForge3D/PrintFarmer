import XCTest
import SwiftUI
import SnapshotTesting
@testable import PrintFarmer

/// Snapshot tests for printer controls across backend capability profiles,
/// loading and disabled states, and shared detail-screen control styles.
///
/// Closes OlyForge3D/PrintFarmer#289.
///
/// SnapshotTesting stores references relative to this file under
/// `Views/__Snapshots__/PrinterControlsSectionSnapshotTests/`. See the adjacent
/// `__Snapshots__/README.md` before regenerating SDK-sensitive images.
@MainActor
final class PrinterControlsSectionSnapshotTests: XCTestCase {

    // UIKit/SwiftUI rasterization differs between iPhone and iPad test hosts,
    // even with the same layout and an explicit display-scale override.
    private var snapshotName: String? {
        UIDevice.current.userInterfaceIdiom == .pad ? "iPad" : nil
    }

    // Synthetic capability evidence for layout/lifecycle tests, not backend profiles.
    private static let layoutCaps = PrinterBackendCapabilities.allControlsFixture

    private func wireCapabilities(backend: String, operationFields: String) throws -> PrinterBackendCapabilities {
        let data = Data("""
        {
            "printerId": "550e8400-e29b-41d4-a716-446655440000",
            "printerName": "Contract fixture",
            "backend": "\(backend)",
            \(operationFields)
        }
        """.utf8)
        return PrinterBackendCapabilities(wire: try JSONDecoder().decode(PrinterBackendCapabilitiesWireDto.self, from: data))
    }

    // MARK: - Printer fixture (force state to idle so the section renders)

    /// Decodes the canonical printer JSON and overrides live-status fields so
    /// `PrinterControlsSection.isHidden(for:)` returns `false`.
    private func makePrinter(
        backend: PrinterBackend,
        isOnline: Bool = true,
        state: String? = "idle"
    ) throws -> Printer {
        // Substitute the backend in the JSON fixture; the `state`/`isOnline`
        // overrides are mutated post-decode because the live-status fields
        // are `var` on `Printer`.
        let backendString: String = {
            switch backend {
            case .moonraker: return "Moonraker"
            case .prusaLink: return "PrusaLink"
            case .octoPrint: return "OctoPrint"
            case .flashForge: return "FlashForge"
            case .sdcp: return "Sdcp"
            case .unknown: return "Unknown"
            }
        }()
        let json = TestJSON.printer.replacingOccurrences(
            of: "\"backend\": \"Moonraker\"",
            with: "\"backend\": \"\(backendString)\""
        )
        var printer = try TestData.decodePrinter(from: json)
        printer.isOnline = isOnline
        printer.state = state
        return printer
    }

    private func makeService(caps: PrinterBackendCapabilities?) -> MockPrinterService {
        let svc = MockPrinterService()
        svc.capabilitiesToReturn = caps
        return svc
    }

    private func loadedSection(
        printer: Printer,
        service: MockPrinterService
    ) async -> PrinterControlsSection {
        let viewModel = PrinterControlsViewModel(printerService: service, printer: printer)
        await viewModel.loadCapabilities()
        return PrinterControlsSection(printer: printer, viewModel: viewModel)
    }

    private func host(_ view: some View) -> UIViewController {
        let host = UIHostingController(rootView: view.frame(width: 390))
        host.view.backgroundColor = .systemBackground
        return host
    }

    // MARK: - Embedded content and standalone ownership

    private func install(_ view: some View) -> (UIWindow, UIHostingController<AnyView>) {
        let controller = UIHostingController(rootView: AnyView(view))
        let window = UIWindow(frame: CGRect(x: 0, y: 0, width: 390, height: 844))
        window.windowScene = UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }.first
        window.frame = CGRect(x: 0, y: 0, width: 390, height: 844)
        window.rootViewController = controller
        window.makeKeyAndVisible()
        controller.view.layoutIfNeeded()
        return (window, controller)
    }

    private func settle(_ controller: UIViewController) async throws {
        // Allow SwiftUI's scheduled body/onChange work to reach the hosted UIKit tree.
        try await Task.sleep(for: .milliseconds(100))
        controller.view.setNeedsLayout()
        controller.view.layoutIfNeeded()
    }

    func test_embeddedContent_mountAndRemount_doNotLoadOrDispatch() async throws {
        let printer = try makePrinter(backend: .moonraker)
        let service = makeService(caps: Self.layoutCaps)
        let model = PrinterControlsViewModel(printerService: service, printer: printer)
        let content = PrinterSetupControlsContent(printer: printer, viewModel: model)
        XCTAssertTrue(content.viewModel === model)
        let (window, controller) = install(content)
        defer { window.isHidden = true }
        try await settle(controller)
        controller.rootView = AnyView(EmptyView())
        try await settle(controller)
        controller.rootView = AnyView(PrinterSetupControlsContent(printer: printer, viewModel: model))
        try await settle(controller)

        XCTAssertNil(model.capabilities, "Embedded content must not start capability loading, even indirectly via Jog")
        XCTAssertNil(service.getBackendCapabilitiesCalledWith)
        XCTAssertNil(service.setTemperaturesCalledWith)
        XCTAssertNil(service.homeCalledWith)
        XCTAssertNil(service.moveCalledWith)
    }

    func test_embeddedContent_remount_preservesPendingAndErrorOnExternalOwner() async throws {
        let printer = try makePrinter(backend: .moonraker)
        let service = makeService(caps: Self.layoutCaps)
        let model = PrinterControlsViewModel(printerService: service, printer: printer)
        await model.loadCapabilities()
        await model.jog(axis: "X", distanceMm: 10)
        let pending = try XCTUnwrap(model.pendingCommand)
        service.getBackendCapabilitiesCalledWith = nil
        service.moveCalledWith = nil
        let (window, controller) = install(PrinterSetupControlsContent(printer: printer, viewModel: model))
        defer { window.isHidden = true }
        try await settle(controller)
        controller.rootView = AnyView(EmptyView())
        try await settle(controller)
        XCTAssertEqual(model.pendingCommand, pending)
        controller.rootView = AnyView(PrinterSetupControlsContent(printer: printer, viewModel: model))
        try await settle(controller)
        XCTAssertEqual(model.pendingCommand, pending, "Embedding again must retain the same in-flight command")
        controller.rootView = AnyView(EmptyView())
        try await settle(controller)

        // The external owner receives updates while its presentation is absent.
        var offline = printer
        offline.isOnline = false
        model.handlePrinterUpdate(offline)
        XCTAssertNil(model.pendingCommand)
        model.handlePrinterUpdate(printer)
        service.errorToThrow = NetworkError.timeout
        await model.preheat(.pla)
        let error = try XCTUnwrap(model.lastError)

        controller.rootView = AnyView(PrinterSetupControlsContent(printer: printer, viewModel: model))
        try await settle(controller)
        XCTAssertEqual(model.lastError, error)
        XCTAssertEqual(model.capabilities, Self.layoutCaps)
        XCTAssertNil(service.getBackendCapabilitiesCalledWith)
        XCTAssertNil(service.moveCalledWith, "Remounting must not replay a pending command")

        model.dismissError()
        service.errorToThrow = nil
        await model.preheat(.pla)
        XCTAssertNil(model.lastError)
        XCTAssertEqual(service.setTemperaturesCalledWith?.hotend, 200)
    }

    func test_standaloneOwner_forwardsOfflineSnapshotOutsideHiddenContent() async throws {
        let printer = try makePrinter(backend: .moonraker)
        let service = makeService(caps: Self.layoutCaps)
        let model = PrinterControlsViewModel(printerService: service, printer: printer)
        await model.loadCapabilities()
        let (window, controller) = install(PrinterControlsSection(printer: printer, viewModel: model))
        defer { window.isHidden = true }
        try await settle(controller)
        await model.jog(axis: "X", distanceMm: 10)
        XCTAssertNotNil(model.pendingCommand)

        var offline = printer
        offline.isOnline = false
        controller.rootView = AnyView(PrinterControlsSection(printer: offline, viewModel: model))
        try await settle(controller)

        XCTAssertNil(model.pendingCommand, "The actual wrapper must forward an offline update after hiding content")
        XCTAssertFalse(model.printer.isOnline)
        XCTAssertFalse(model.canControl)
    }

    func test_standaloneOwner_redraw_retainsInstalledModelAndTargetCorrelation() async throws {
        var printer = try makePrinter(backend: .moonraker)
        printer.hotendTarget = 215
        let service = makeService(caps: Self.layoutCaps)
        let model = PrinterControlsViewModel(printerService: service, printer: printer)
        await model.loadCapabilities()
        let (window, controller) = install(PrinterControlsSection(printer: printer, viewModel: model))
        defer { window.isHidden = true }
        try await settle(controller)
        await model.preheat(.pla)
        let pending = try XCTUnwrap(model.pendingCommand)

        let replacement = PrinterControlsViewModel(printerService: service, printer: printer)
        var noisy = printer
        noisy.hotendTemp = 199
        controller.rootView = AnyView(PrinterControlsSection(printer: noisy, viewModel: replacement))
        try await settle(controller)
        XCTAssertEqual(model.pendingCommand, pending)
        XCTAssertEqual(model.printer.hotendTemp, 199, "Redraw must update the installed owner, not the new initializer argument")
        XCTAssertNil(replacement.capabilities)

        noisy.hotendTarget = 200
        noisy.bedTarget = 60
        controller.rootView = AnyView(PrinterControlsSection(printer: noisy, viewModel: replacement))
        try await settle(controller)
        XCTAssertNil(model.pendingCommand)
        XCTAssertEqual(model.printer.hotendTarget, 200)
    }

    func test_embeddedLoadedLimitedAxes_selectsSupportedAxisImmediately() async throws {
        let printer = try makePrinter(backend: .moonraker)
        let caps = PrinterBackendCapabilities(
            supportsMovement: true,
            supportsTemperatureControl: true,
            supportsBedTemperature: true,
            supportsFanControl: true,
            supportsHoming: true,
            supportedAxes: ["Y", "Z"]
        )
        let service = makeService(caps: caps)
        let model = PrinterControlsViewModel(printerService: service, printer: printer)
        await model.loadCapabilities()
        service.getBackendCapabilitiesCalledWith = nil
        let (window, controller) = install(PrinterSetupControlsContent(printer: printer, viewModel: model))
        defer { window.isHidden = true }
        try await settle(controller)

        window.frame.size.height = 3000
        controller.view.frame = window.bounds
        try await settle(controller)
        let controls = nativeControls(in: controller.view)
        let y = try XCTUnwrap(controls.compactMap { $0 as? UIButton }.first {
            $0.accessibilityIdentifier == "printer.controls.jog.axis.y"
        })
        XCTAssertTrue(y.isSelected, "A loaded YZ-only backend must select Y, not the unsupported X default")
        XCTAssertEqual(y.accessibilityLabel, "Jog axis Y")
        XCTAssertGreaterThanOrEqual(y.bounds.height, 44)
        XCTAssertFalse(controls.contains { $0.accessibilityIdentifier == "printer.controls.jog.axis.x" })
        XCTAssertNil(service.getBackendCapabilitiesCalledWith)
        XCTAssertNil(service.moveCalledWith)
    }

    func test_embeddedContent_largeType_reflowsAtPhoneAndTabletWidths() async throws {
        let printer = try makePrinter(backend: .moonraker, state: "paused")
        let service = makeService(caps: Self.layoutCaps)
        let model = PrinterControlsViewModel(printerService: service, printer: printer)
        await model.loadCapabilities()
        for width: CGFloat in [390, 1024] {
            let content = PrinterSetupControlsContent(printer: printer, viewModel: model)
                .environment(\.horizontalSizeClass, width == 390 ? .compact : .regular)
            let standard = UIHostingController(rootView: content.environment(\.dynamicTypeSize, .large))
            let accessible = UIHostingController(rootView: content.environment(\.dynamicTypeSize, .accessibility3))
            let proposedSize = CGSize(width: width, height: 10_000)
            let normalSize = standard.sizeThatFits(in: proposedSize)
            let largeSize = accessible.sizeThatFits(in: proposedSize)
            XCTAssertGreaterThan(largeSize.height, normalSize.height)
            XCTAssertLessThanOrEqual(largeSize.width, width)
        }
        XCTAssertFalse(model.canControl)
        XCTAssertEqual(PreheatSubgroup(viewModel: model).accessibilityLabel(preset: .pla, isPending: false), "Preheat for PLA")
        XCTAssertEqual(JogSubgroup(viewModel: model).jogAccessibilityLabel(direction: 1), "Jog forward")
        XCTAssertNil(service.moveCalledWith)
    }

    // MARK: - Backend profile snapshots

    private func nativeControls(in view: UIView) -> [UIControl] {
        (view as? UIControl).map { [$0] } ?? view.subviews.flatMap { nativeControls(in: $0) }
    }

    func test_individualControls_phoneFullContentEvidence() async throws {
        try await captureIndividualControls(width: 390, dynamicType: .large, name: "phone")
    }

    func test_pendingCommand_disablesNativeActionsButKeepsStopWaitingEnabled() async throws {
        let printer = try makePrinter(backend: .moonraker)
        let service = makeService(caps: Self.layoutCaps)
        let model = PrinterControlsViewModel(printerService: service, printer: printer)
        await model.loadCapabilities()
        await model.setHeaterTarget(.hotend, target: 220)
        XCTAssertNotNil(model.pendingCommand)
        let (window, controller) = install(PrinterSetupControlsContent(printer: printer, viewModel: model))
        defer { window.isHidden = true }
        window.frame.size.height = 3000
        controller.view.frame = window.bounds
        try await settle(controller)
        let controls = nativeControls(in: controller.view)
        let apply = try XCTUnwrap(controls.first { $0.accessibilityIdentifier == "printer.controls.hotend.set" })
        let input = try XCTUnwrap(controls.first { $0.accessibilityIdentifier == "printer.controls.hotend.target" })
        let stop = try XCTUnwrap(controls.first { $0.accessibilityIdentifier == "printer.controls.stop-waiting" })
        XCTAssertFalse(apply.isEnabled)
        XCTAssertFalse(input.isEnabled)
        XCTAssertTrue(stop.isEnabled)
        XCTAssertGreaterThanOrEqual(stop.bounds.height, 44)
        stop.sendActions(for: .touchUpInside)
        XCTAssertNil(model.pendingCommand)
        XCTAssertTrue(model.commandNotice?.contains("may still execute") == true)
    }

    func test_individualControls_regularWidthFullContentEvidence() async throws {
        try await captureIndividualControls(width: 1024, dynamicType: .large, name: "regular")
    }

    func test_blockedEditors_explainOfflinePreferenceAndPermissionGates() async throws {
        for reason in [
            "Printer is offline.",
            "Enable printer controls for this server in Settings.",
            "Printer controls require Queue.Start permission."
        ] {
            let printer = try makePrinter(backend: .moonraker, isOnline: reason != "Printer is offline.")
            var caps = Self.layoutCaps
            caps.supportsAbsoluteMovement = true
            let service = makeService(caps: caps)
            let model = PrinterControlsViewModel(printerService: service, printer: printer)
            await model.loadCapabilities()
            if printer.isOnline { model.configureAccess { reason } }
            XCTAssertEqual(model.blockedReason, reason)
            let (window, controller) = install(PrinterSetupControlsContent(printer: printer, viewModel: model))
            defer { window.isHidden = true }
            window.frame.size.height = 3000
            controller.view.frame = window.bounds
            try await settle(controller)
            for label in ["Hotend target in degrees Celsius", "X absolute destination in millimeters"] {
                let control = try XCTUnwrap(nativeControls(in: controller.view).first { $0.accessibilityLabel == label })
                XCTAssertFalse(control.isEnabled)
            }
            let image = UIGraphicsImageRenderer(bounds: controller.view.bounds).image { _ in
                controller.view.drawHierarchy(in: controller.view.bounds, afterScreenUpdates: true)
            }
            let attachment = XCTAttachment(image: image)
            attachment.name = "blocked-controls-\(reason)"
            attachment.lifetime = .keepAlways
            add(attachment)
        }
    }

    func test_individualControls_narrowSplitLargeTypeEvidence() async throws {
        try await captureIndividualControls(width: 500, dynamicType: .accessibility3, name: "narrow-accessibility")
    }

    private func captureIndividualControls(width: CGFloat, dynamicType: DynamicTypeSize, name: String) async throws {
        var printer = try makePrinter(backend: .moonraker)
        printer.hotendTemp = nil
        printer.bedTemp = 0
        printer.homedAxes = nil
        var caps = Self.layoutCaps
        caps.supportsAbsoluteMovement = true
        caps.supportsDisableMotors = true
        let service = makeService(caps: caps)
        service.detailsToReturn = PrinterDetails(
            id: printer.id, name: printer.name, backend: printer.backend,
            capabilities: PrinterHardwareCapabilities(
                maxBuildVolumeX: 256, maxBuildVolumeY: 256, maxBuildVolumeZ: 256,
                maxHotendTemp: 280, maxBedTemp: 110, hasHeatedBed: true
            )
        )
        let model = PrinterControlsViewModel(printerService: service, printer: printer)
        await model.loadCapabilities()
        let content = PrinterSetupControlsContent(
            printer: printer, viewModel: model,
            usesColumns: PrinterDetailLayout.usesColumns(width: width, dynamicTypeSize: dynamicType)
        )
        .environment(\.horizontalSizeClass, width >= 760 ? .regular : .compact)
        .environment(\.dynamicTypeSize, dynamicType)
        .frame(width: width)
        .fixedSize(horizontal: false, vertical: true)
        let controller = UIHostingController(rootView: content)
        let size = controller.sizeThatFits(in: CGSize(width: width, height: 10000))
        XCTAssertGreaterThan(size.height, 500)
        XCTAssertLessThan(size.height, 10000, "The complete layout must fit without clipping")
        XCTAssertEqual(size.width, width, accuracy: 1)
        let window = UIWindow(frame: CGRect(origin: .zero, size: size))
        window.windowScene = UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }.first
        window.frame = CGRect(origin: .zero, size: size)
        window.rootViewController = controller
        window.makeKeyAndVisible()
        defer { window.isHidden = true }
        controller.view.frame = window.bounds
        try await settle(controller)
        let controls = nativeControls(in: controller.view)
        for label in [
            "Hotend target in degrees Celsius", "Bed target in degrees Celsius",
            "X absolute destination in millimeters", "Y absolute destination in millimeters",
            "Z absolute destination in millimeters",
            "Absolute movement feedrate in millimeters per minute",
            "Set hotend target", "Set bed target", "Move to position", "Disable motors"
        ] {
            let target = try XCTUnwrap(controls.first { $0.accessibilityLabel == label }, label)
            XCTAssertGreaterThanOrEqual(target.bounds.height, 44, label)
            XCTAssertGreaterThanOrEqual(target.bounds.width, 44, label)
            XCTAssertTrue(target.isEnabled, label)
            XCTAssertTrue(target.point(inside: CGPoint(x: 1, y: 1), with: nil), label)
        }
        let image = UIGraphicsImageRenderer(bounds: controller.view.bounds).image { _ in
            controller.view.drawHierarchy(in: controller.view.bounds, afterScreenUpdates: true)
        }
        let attachment = XCTAttachment(image: image)
        attachment.name = "issue-2598-\(snapshotName ?? "iPhone")-\(name)"
        attachment.lifetime = XCTAttachment.Lifetime.keepAlways
        add(attachment)
        XCTAssertTrue(model.supports(.hotend))
        XCTAssertTrue(model.supports(.bed))
        XCTAssertTrue(JogSubgroup.AbsolutePositionControls.isVisible(model.capabilities))
        XCTAssertEqual(model.hardware?.maxHotendTemp, 280)
        XCTAssertNil(model.printer.hotendTemp)
        XCTAssertEqual(model.printer.bedTemp, 0)
        XCTAssertNil(model.printer.homedAxes)
        XCTAssertNil(service.setTemperaturesCalledWith)
        XCTAssertNil(service.moveToCalledWith)
        XCTAssertNil(service.disableMotorsCalledWith)
    }

    func test_snapshot_moonrakerProfile() async throws {
        let printer = try makePrinter(backend: .moonraker)
        // Resolved current Moonraker interfaces: homing/heaters, never jogging.
        let caps = try wireCapabilities(backend: "Moonraker", operationFields: """
        "supportsRelativeMovement": false, "supportsAbsoluteMovement": false,
        "supportsHoming": true, "supportsHomingXY": true, "supportsHomingZ": true,
        "supportsHotendTemperature": true, "supportsBedTemperature": true,
        "supportsExtrusion": true, "supportsDisableMotors": true, "supportsZOffset": true,
        "supportedAxes": ["x", "y", "z"]
        """)
        XCTAssertFalse(caps.supportsMovement)
        XCTAssertTrue(caps.supportsHome(axes: ["X", "Y", "Z"]))
        let svc = makeService(caps: caps)
        let section = await loadedSection(printer: printer, service: svc)
        assertSnapshot(of: host(ScrollView { section }), as: .image(on: .iPhone13), named: snapshotName)
    }

    func test_snapshot_flashForgeProfile() async throws {
        let printer = try makePrinter(backend: .flashForge)
        // Current unmatched-route-port case: no physical support is proven.
        let caps = try wireCapabilities(backend: "FlashForge", operationFields: """
        "supportsZOffset": true, "supportsRelativeMovement": false,
        "supportsHoming": false, "supportsHotendTemperature": false,
        "supportsBedTemperature": false, "supportedAxes": []
        """)
        XCTAssertFalse(caps.supportsMovement)
        XCTAssertFalse(PreheatSubgroup.isVisible(capabilities: caps))
        let svc = makeService(caps: caps)
        let section = await loadedSection(printer: printer, service: svc)
        assertSnapshot(of: host(ScrollView { section }), as: .image(on: .iPhone13), named: snapshotName)
    }

    func test_snapshot_sdcpProfile() async throws {
        let printer = try makePrinter(backend: .sdcp)
        let caps = try wireCapabilities(backend: "Sdcp", operationFields: """
        "supportsZOffset": true, "supportsRelativeMovement": false,
        "supportsHoming": false, "supportsHotendTemperature": false,
        "supportedAxes": []
        """)
        let svc = makeService(caps: caps)
        let section = await loadedSection(printer: printer, service: svc)
        assertSnapshot(of: host(ScrollView { section }), as: .image(on: .iPhone13), named: snapshotName)
    }

    // MARK: - State snapshots

    /// Capabilities load is async; the initial render before `loadCapabilities`
    /// resolves is the race window flagged in Bishop's #299 review. Asserting
    /// it here pins the loading-state pixels.
    func test_snapshot_loadingState_capabilitiesNil() throws {
        let printer = try makePrinter(backend: .moonraker)
        let svc = MockPrinterService()
        let barrier = AsyncBarrier()
        addTeardownBlock { barrier.close() }
        svc.beforeGetBackendCapabilities = { await barrier.arriveAndWait() }
        let model = PrinterControlsViewModel(printerService: svc, printer: printer)
        let section = PrinterControlsSection(printer: printer, viewModel: model)
        assertSnapshot(of: host(ScrollView { section }), as: .image(on: .iPhone13), named: snapshotName)
    }

    /// The starting state keeps the section visible while `canControl` is false,
    /// exercising disabled subgroup controls without the printing lockout banner.
    func test_snapshot_disabledState_printerStarting() async throws {
        let printer = try makePrinter(backend: .moonraker, state: "starting")
        let svc = makeService(caps: Self.layoutCaps)
        let section = await loadedSection(printer: printer, service: svc)
        assertSnapshot(of: host(ScrollView { section }), as: .image(on: .iPhone13), named: snapshotName)
    }
    // MARK: - Lockout banner (spec §2.2 — visible during print with disabled controls)

    /// Section remains visible during a print; a lockout banner is shown and
    /// all subgroup controls are disabled. The section is NOT hidden — only
    /// the offline state hides it (spec §2.2, §2.4).
    func test_snapshot_lockoutBanner_printingState() async throws {
        let printer = try makePrinter(backend: .moonraker, state: "printing")
        let svc = makeService(caps: Self.layoutCaps)
        let section = await loadedSection(printer: printer, service: svc)
        assertSnapshot(of: host(ScrollView { section }), as: .image(on: .iPhone13), named: snapshotName)
    }

    func test_snapshot_printerDetailBorderedDestructiveControls_darkMode() {
        let controls = HStack(spacing: 12) {
            PrinterDetailBorderedDestructiveButton(kind: .eject, action: {})
            PrinterDetailBorderedDestructiveButton(kind: .cancel, action: {})
        }
        .padding()
        .frame(maxWidth: .infinity)

        let rootView = VStack {
            controls
            Spacer()
        }
        .background(Color.pfBackground)
        .preferredColorScheme(.dark)

        let hostingController = host(rootView)
        hostingController.overrideUserInterfaceStyle = .dark
        assertSnapshot(of: hostingController, as: .image(on: .iPhone13), named: snapshotName)
    }
}
