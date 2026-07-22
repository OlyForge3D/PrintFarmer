import XCTest
import SwiftUI
import SnapshotTesting
@testable import PrintFarmer

/// Snapshot tests for `PrinterControlsSection` across the three backend
/// capability profiles plus loading and disabled states.
///
/// Closes OlyForge3D/PrintFarmer#289.
///
/// Baselines are recorded on iOS 17 simulator (iPhone 15). When the SDK
/// drifts (e.g., iOS 26.x), re-record on the CI simulator: set
/// `isRecording = true` locally, run the suite, commit the regenerated
/// `__Snapshots__/` directory, then flip back to false.
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

    private func host(_ view: some View) -> UIViewController {
        let host = UIHostingController(rootView: view.frame(width: 390))
        host.view.backgroundColor = .systemBackground
        return host
    }

    // MARK: - Backend profile snapshots

    func test_snapshot_moonrakerProfile() throws {
        let printer = try makePrinter(backend: .moonraker)
        let svc = makeService(caps: Self.moonrakerCaps)
        let section = PrinterControlsSection(printer: printer, printerService: svc)
        assertSnapshot(of: host(section), as: .image(on: .iPhone13))
    }

    func test_snapshot_flashForgeProfile() throws {
        let printer = try makePrinter(backend: .flashForge)
        let svc = makeService(caps: Self.flashForgeCaps)
        let section = PrinterControlsSection(printer: printer, printerService: svc)
        assertSnapshot(of: host(section), as: .image(on: .iPhone13))
    }

    func test_snapshot_sdcpProfile() throws {
        let printer = try makePrinter(backend: .sdcp)
        let svc = makeService(caps: Self.sdcpCaps)
        let section = PrinterControlsSection(printer: printer, printerService: svc)
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

    /// Disabled stripe modifier from #288: triggered when the printer is
    /// online but not in an idle/ready state that accepts control input.
    /// `"error"` is not in the hidden-state set (offline only); the section
    /// renders with disabled subgroup controls per spec §2.4.
    func test_snapshot_disabledState_printerInError() throws {
        let printer = try makePrinter(backend: .moonraker, state: "error")
        let svc = makeService(caps: Self.moonrakerCaps)
        let section = PrinterControlsSection(printer: printer, printerService: svc)
        assertSnapshot(of: host(section), as: .image(on: .iPhone13))
    }
    // MARK: - Lockout banner (spec §2.2 — visible during print with disabled controls)

    /// Section remains visible during a print; a lockout banner is shown and
    /// all subgroup controls are disabled. The section is NOT hidden — only
    /// the offline state hides it (spec §2.2, §2.4).
    func test_snapshot_lockoutBanner_printingState() throws {
        let printer = try makePrinter(backend: .moonraker, state: "printing")
        let svc = makeService(caps: Self.moonrakerCaps)
        let section = PrinterControlsSection(printer: printer, printerService: svc)
        assertSnapshot(of: host(section), as: .image(on: .iPhone13))
    }
}
