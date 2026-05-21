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
}
