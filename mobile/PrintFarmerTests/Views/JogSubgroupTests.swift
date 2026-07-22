import XCTest
import SwiftUI
@testable import PrintFarmer

/// Smoke tests for `JogSubgroup` — focuses on pure decision helpers that
/// drive layout (visible axes, hidden state). Full SwiftUI rendering is
/// validated in CI via Xcode previews / snapshot tests.
@MainActor
final class JogSubgroupTests: XCTestCase {

    private static let fullCaps = PrinterBackendCapabilities(
        supportsMovement: true,
        supportsTemperatureControl: true,
        supportsBedTemperature: true,
        supportsFanControl: true,
        supportsHoming: true,
        supportedAxes: ["X", "Y", "Z"]
    )

    private static let xyOnlyCaps = PrinterBackendCapabilities(
        supportsMovement: true,
        supportsTemperatureControl: true,
        supportsBedTemperature: false,
        supportsFanControl: false,
        supportsHoming: true,
        supportedAxes: ["X", "Y"]
    )

    private static let noMovementCaps = PrinterBackendCapabilities(
        supportsMovement: false,
        supportsTemperatureControl: false,
        supportsBedTemperature: false,
        supportsFanControl: false,
        supportsHoming: false,
        supportedAxes: []
    )

    // MARK: - visibleAxes

    func test_visibleAxes_whenCapabilitiesNil_returnsAllCanonicalAxes() {
        XCTAssertEqual(JogSubgroup.visibleAxes(for: nil), ["X", "Y", "Z"])
    }

    func test_visibleAxes_filtersByCapabilities() {
        XCTAssertEqual(JogSubgroup.visibleAxes(for: Self.fullCaps), ["X", "Y", "Z"])
        XCTAssertEqual(JogSubgroup.visibleAxes(for: Self.xyOnlyCaps), ["X", "Y"])
    }

    func test_visibleAxes_emptySupportedAxes_returnsEmpty() {
        XCTAssertEqual(JogSubgroup.visibleAxes(for: Self.noMovementCaps), [])
    }

    // MARK: - isHidden

    func test_isHidden_whenCapabilitiesNil_returnsFalse() {
        XCTAssertFalse(JogSubgroup.isHidden(for: nil))
    }

    func test_isHidden_whenSupportsMovementFalse_returnsTrue() {
        XCTAssertTrue(JogSubgroup.isHidden(for: Self.noMovementCaps))
    }

    func test_isHidden_whenSupportedAxesEmpty_returnsTrue() {
        let caps = PrinterBackendCapabilities(
            supportsMovement: true,
            supportsTemperatureControl: false,
            supportsBedTemperature: false,
            supportsFanControl: false,
            supportsHoming: false,
            supportedAxes: []
        )
        XCTAssertTrue(JogSubgroup.isHidden(for: caps))
    }

    func test_isHidden_whenMovementSupportedAndAxesPresent_returnsFalse() {
        XCTAssertFalse(JogSubgroup.isHidden(for: Self.fullCaps))
        XCTAssertFalse(JogSubgroup.isHidden(for: Self.xyOnlyCaps))
    }

    // MARK: - step options

    func test_stepOptions_areLockedToV1Values() {
        XCTAssertEqual(JogSubgroup.stepOptions, [0.1, 1, 10, 100])
    }

    // MARK: - Accessibility labels and hints (spec §4.1)

    func test_jogAccessibilityLabel_positive_isJogForward() throws {
        let vm = PrinterControlsViewModel(
            printerService: MockPrinterService(),
            printer: try TestData.decodePrinter()
        )
        let view = JogSubgroup(viewModel: vm)
        XCTAssertEqual(view.jogAccessibilityLabel(direction: 1), "Jog forward")
    }

    func test_jogAccessibilityLabel_negative_isJogBackward() throws {
        let vm = PrinterControlsViewModel(
            printerService: MockPrinterService(),
            printer: try TestData.decodePrinter()
        )
        let view = JogSubgroup(viewModel: vm)
        XCTAssertEqual(view.jogAccessibilityLabel(direction: -1), "Jog backward")
    }

    func test_jogAccessibilityHint_positive_usesPositiveDirection() throws {
        let vm = PrinterControlsViewModel(
            printerService: MockPrinterService(),
            printer: try TestData.decodePrinter()
        )
        let view = JogSubgroup(viewModel: vm)
        let hint = view.jogAccessibilityHint(direction: 1, stepLabelText: "1", hasError: false)
        XCTAssertTrue(hint.contains("positive"), "Hint for + jog must say 'positive' per spec §4.1, got: \(hint)")
    }

    func test_jogAccessibilityHint_negative_usesNegativeDirection() throws {
        let vm = PrinterControlsViewModel(
            printerService: MockPrinterService(),
            printer: try TestData.decodePrinter()
        )
        let view = JogSubgroup(viewModel: vm)
        let hint = view.jogAccessibilityHint(direction: -1, stepLabelText: "1", hasError: false)
        XCTAssertTrue(hint.contains("negative"), "Hint for - jog must say 'negative' per spec §4.1, got: \(hint)")
    }

    func test_jogAccessibilityHint_disabled_returnsSpec41Text() throws {
        // Default printer state is "printing" -> canControl = false
        let vm = PrinterControlsViewModel(
            printerService: MockPrinterService(),
            printer: try TestData.decodePrinter()
        )
        let view = JogSubgroup(viewModel: vm)
        let hint = view.jogAccessibilityHint(direction: 1, stepLabelText: "1", hasError: false)
        XCTAssertEqual(hint, "Disabled while printing.")
    }

    func test_jogAccessibilityValue_pending_returnsPending() throws {
        let vm = PrinterControlsViewModel(
            printerService: MockPrinterService(),
            printer: try TestData.decodePrinter()
        )
        let view = JogSubgroup(viewModel: vm)
        XCTAssertEqual(view.jogAccessibilityValue(isPending: true, hasError: false), "Pending")
    }

    func test_jogAccessibilityValue_idle_isEmpty() throws {
        let vm = PrinterControlsViewModel(
            printerService: MockPrinterService(),
            printer: try TestData.decodePrinter()
        )
        let view = JogSubgroup(viewModel: vm)
        XCTAssertEqual(view.jogAccessibilityValue(isPending: false, hasError: false), "")
    }

}
