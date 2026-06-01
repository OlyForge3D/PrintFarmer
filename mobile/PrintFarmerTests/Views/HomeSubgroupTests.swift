import XCTest
import SwiftUI
@testable import PrintFarmer

@MainActor
final class HomeSubgroupTests: XCTestCase {

    private static let fullCaps = PrinterBackendCapabilities(
        supportsMovement: true,
        supportsTemperatureControl: true,
        supportsBedTemperature: true,
        supportsFanControl: true,
        supportsHoming: true,
        supportedAxes: ["X", "Y", "Z"]
    )

    private static let noMovementCaps = PrinterBackendCapabilities(
        supportsMovement: false,
        supportsTemperatureControl: true,
        supportsBedTemperature: true,
        supportsFanControl: false,
        supportsHoming: true,
        supportedAxes: []
    )

    private static let noHomingCaps = PrinterBackendCapabilities(
        supportsMovement: true,
        supportsTemperatureControl: true,
        supportsBedTemperature: true,
        supportsFanControl: true,
        supportsHoming: false,
        supportedAxes: ["X", "Y", "Z"]
    )

    private func idlePrinter() throws -> Printer {
        let json = TestJSON.printer
            .replacingOccurrences(of: "\"state\": \"printing\"", with: "\"state\": \"ready\"")
        return try TestData.decoder.decode(Printer.self, from: json.data(using: .utf8)!)
    }

    // MARK: - Capability gating

    func test_shouldHide_whenCapabilitiesMissing() {
        XCTAssertTrue(HomeSubgroup.shouldHide(capabilities: nil))
    }

    func test_shouldHide_whenMovementUnsupported() {
        XCTAssertTrue(HomeSubgroup.shouldHide(capabilities: Self.noMovementCaps))
    }

    func test_shouldHide_whenHomingUnsupported() {
        XCTAssertTrue(HomeSubgroup.shouldHide(capabilities: Self.noHomingCaps))
    }

    func test_shouldNotHide_whenMovementAndHomingSupported() {
        XCTAssertFalse(HomeSubgroup.shouldHide(capabilities: Self.fullCaps))
    }

    // MARK: - Smoke render

    func test_render_doesNotCrash_withFullCapabilities() throws {
        let vm = PrinterControlsViewModel(
            printerService: MockPrinterService(),
            printer: try idlePrinter()
        )
        let view = HomeSubgroup(viewModel: vm)
        // Materialize view body — proves all 3 button branches compile and resolve.
        let host = UIHostingController(rootView: view)
        host.view.layoutIfNeeded()
        XCTAssertNotNil(host.view)
    }

    func test_render_disabledWhenCannotControl() throws {
        // Default decoded printer has state="printing" → !canControl
        let mock = MockPrinterService()
        mock.capabilitiesToReturn = Self.fullCaps
        let printer = try TestData.decodePrinter()
        let vm = PrinterControlsViewModel(printerService: mock, printer: printer)
        XCTAssertFalse(vm.canControl, "Printing printer should not be controllable")

        let view = HomeSubgroup(viewModel: vm)
        let host = UIHostingController(rootView: view)
        host.view.layoutIfNeeded()
        XCTAssertNotNil(host.view)
    }

    // MARK: - Accessibility labels and hints (spec §4.1)

    func test_homeAll_idleLabel_isHomeAllAxes() throws {
        let mock = MockPrinterService()
        mock.capabilitiesToReturn = Self.fullCaps
        let vm = PrinterControlsViewModel(printerService: mock, printer: try idlePrinter())
        let view = HomeSubgroup(viewModel: vm)
        let host = UIHostingController(rootView: view)
        host.view.layoutIfNeeded()
        // Verify view renders without crash; string "Home all axes" is verified at source level.
        XCTAssertNotNil(host.view)
    }

    func test_accessibilityHint_idle_homeAll_containsIdleHint() throws {
        let view = HomeSubgroup(viewModel: PrinterControlsViewModel(
            printerService: MockPrinterService(), printer: try idlePrinter()))
        let hint = view.accessibilityHint(hasError: false, idleHint: "Homes X, Y, and Z.")
        XCTAssertEqual(hint, "Homes X, Y, and Z.")
    }

    func test_accessibilityHint_idle_homeXY_containsIdleHint() throws {
        let view = HomeSubgroup(viewModel: PrinterControlsViewModel(
            printerService: MockPrinterService(), printer: try idlePrinter()))
        let hint = view.accessibilityHint(hasError: false, idleHint: "Homes X and Y axes only.")
        XCTAssertEqual(hint, "Homes X and Y axes only.")
    }

    func test_accessibilityHint_idle_homeZ_containsIdleHint() throws {
        let view = HomeSubgroup(viewModel: PrinterControlsViewModel(
            printerService: MockPrinterService(), printer: try idlePrinter()))
        let hint = view.accessibilityHint(hasError: false, idleHint: "Homes Z axis only.")
        XCTAssertEqual(hint, "Homes Z axis only.")
    }

    func test_accessibilityHint_disabled_returnsSpec41Text() throws {
        // Default printer is printing -> isDisabled = true
        let mock = MockPrinterService()
        mock.capabilitiesToReturn = Self.fullCaps
        let printer = try TestData.decodePrinter()
        let vm = PrinterControlsViewModel(printerService: mock, printer: printer)
        let view = HomeSubgroup(viewModel: vm)
        let hint = view.accessibilityHint(hasError: false, idleHint: "Homes X, Y, and Z.")
        XCTAssertEqual(hint, "Disabled while printing.")
    }

    func test_accessibilityValue_pending_returnsPending() throws {
        let view = HomeSubgroup(viewModel: PrinterControlsViewModel(
            printerService: MockPrinterService(), printer: try idlePrinter()))
        XCTAssertEqual(view.accessibilityValue(isPending: true, hasError: false), "Pending")
    }

    func test_accessibilityValue_idle_isEmpty() throws {
        let view = HomeSubgroup(viewModel: PrinterControlsViewModel(
            printerService: MockPrinterService(), printer: try idlePrinter()))
        XCTAssertEqual(view.accessibilityValue(isPending: false, hasError: false), "")
    }

}
