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

    // MARK: - Capability fixtures (per backend)

    private static let moonrakerCaps = PrinterBackendCapabilities.fallback(for: .moonraker)
    private static let flashForgeCaps = PrinterBackendCapabilities.fallback(for: .flashForge)
    private static let sdcpCaps = PrinterBackendCapabilities.fallback(for: .sdcp)

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

    // MARK: - Backend profile snapshots

    func test_snapshot_moonrakerProfile() async throws {
        let printer = try makePrinter(backend: .moonraker)
        let svc = makeService(caps: Self.moonrakerCaps)
        let section = await loadedSection(printer: printer, service: svc)
        assertSnapshot(of: host(section), as: .image(on: .iPhone13))
    }

    func test_snapshot_flashForgeProfile() async throws {
        let printer = try makePrinter(backend: .flashForge)
        let svc = makeService(caps: Self.flashForgeCaps)
        let section = await loadedSection(printer: printer, service: svc)
        assertSnapshot(of: host(section), as: .image(on: .iPhone13))
    }

    func test_snapshot_sdcpProfile() async throws {
        let printer = try makePrinter(backend: .sdcp)
        let svc = makeService(caps: Self.sdcpCaps)
        let section = await loadedSection(printer: printer, service: svc)
        assertSnapshot(of: host(section), as: .image(on: .iPhone13))
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
        assertSnapshot(of: host(section), as: .image(on: .iPhone13))
    }

    /// The starting state keeps the section visible while `canControl` is false,
    /// exercising disabled subgroup controls without the printing lockout banner.
    func test_snapshot_disabledState_printerStarting() async throws {
        let printer = try makePrinter(backend: .moonraker, state: "starting")
        let svc = makeService(caps: Self.moonrakerCaps)
        let section = await loadedSection(printer: printer, service: svc)
        assertSnapshot(of: host(section), as: .image(on: .iPhone13))
    }
    // MARK: - Lockout banner (spec §2.2 — visible during print with disabled controls)

    /// Section remains visible during a print; a lockout banner is shown and
    /// all subgroup controls are disabled. The section is NOT hidden — only
    /// the offline state hides it (spec §2.2, §2.4).
    func test_snapshot_lockoutBanner_printingState() async throws {
        let printer = try makePrinter(backend: .moonraker, state: "printing")
        let svc = makeService(caps: Self.moonrakerCaps)
        let section = await loadedSection(printer: printer, service: svc)
        assertSnapshot(of: host(section), as: .image(on: .iPhone13))
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
        assertSnapshot(of: hostingController, as: .image(on: .iPhone13))
    }
}
