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
        window.rootViewController = controller
        window.isHidden = false
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

        func segmentedControls(in view: UIView) -> [UISegmentedControl] {
            (view as? UISegmentedControl).map { [$0] } ?? view.subviews.flatMap { segmentedControls(in: $0) }
        }
        let axisPicker = try XCTUnwrap(segmentedControls(in: controller.view).first {
            $0.numberOfSegments == 2 && $0.titleForSegment(at: 0) == "Y"
        })
        XCTAssertEqual(axisPicker.selectedSegmentIndex, 0, "An already-loaded YZ-only backend must not retain the unsupported X default")
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
        assertSnapshot(of: host(section), as: .image(on: .iPhone13), named: snapshotName)
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
        assertSnapshot(of: host(section), as: .image(on: .iPhone13), named: snapshotName)
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
        assertSnapshot(of: host(section), as: .image(on: .iPhone13), named: snapshotName)
    }

    // MARK: - State snapshots

    /// Capabilities load is async; the initial render before `loadCapabilities`
    /// resolves is the race window flagged in Bishop's #299 review. Asserting
    /// it here pins the loading-state pixels.
    func test_snapshot_loadingState_capabilitiesNil() throws {
        let printer = try makePrinter(backend: .moonraker)
        let svc = MockPrinterService()
        // Hold the capabilities call open by throwing — viewModel keeps caps == nil.
        svc.errorToThrow = NetworkError.notFound
        let section = PrinterControlsSection(printer: printer, printerService: svc)
        assertSnapshot(of: host(section), as: .image(on: .iPhone13), named: snapshotName)
    }

    /// The starting state keeps the section visible while `canControl` is false,
    /// exercising disabled subgroup controls without the printing lockout banner.
    func test_snapshot_disabledState_printerStarting() async throws {
        let printer = try makePrinter(backend: .moonraker, state: "starting")
        let svc = makeService(caps: Self.layoutCaps)
        let section = await loadedSection(printer: printer, service: svc)
        assertSnapshot(of: host(section), as: .image(on: .iPhone13), named: snapshotName)
    }
    // MARK: - Lockout banner (spec §2.2 — visible during print with disabled controls)

    /// Section remains visible during a print; a lockout banner is shown and
    /// all subgroup controls are disabled. The section is NOT hidden — only
    /// the offline state hides it (spec §2.2, §2.4).
    func test_snapshot_lockoutBanner_printingState() async throws {
        let printer = try makePrinter(backend: .moonraker, state: "printing")
        let svc = makeService(caps: Self.layoutCaps)
        let section = await loadedSection(printer: printer, service: svc)
        assertSnapshot(of: host(section), as: .image(on: .iPhone13), named: snapshotName)
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
