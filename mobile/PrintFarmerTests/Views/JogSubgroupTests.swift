import XCTest
import SwiftUI
@testable import PrintFarmer

/// Smoke tests for `JogSubgroup` — focuses on pure decision helpers that
/// drive layout (visible axes, hidden state). Full SwiftUI rendering is
/// validated in CI via Xcode previews / snapshot tests.
@MainActor
final class JogSubgroupTests: XCTestCase {

    func test_coordinateInput_limitsPrecisionWithoutBinaryNoiseOrSilentRounding() throws {
        XCTAssertNil(try ControlNumberInput.coordinate(" "))
        for (text, value) in [
            ("0", 0.0), ("-1.234", -1.234), ("1.001", 1.001), ("1.234000", 1.234),
            (".001", 0.001), ("1e-3", 0.001), ("1000e-4", 0.1), ("-0.000", 0.0)
        ] {
            XCTAssertEqual(try ControlNumberInput.coordinate(text), value)
        }
        for text in ["1.2345", "-0.0001", "1e-4", "1e-999", "1.00100000000000001", "NaN", "inf"] {
            XCTAssertThrowsError(try ControlNumberInput.coordinate(text), text)
        }
    }

    func test_absoluteInputs_requireXYZPreserveZeroAndRejectEveryCustomFeedrate() throws {
        typealias Editor = JogSubgroup.AbsolutePositionControls
        for (x, y, z) in [("", "1", "10"), ("0", " ", "10"), ("0", "1", "")] {
            XCTAssertThrowsError(try Editor.destination(x: x, y: y, z: z)) {
                XCTAssertEqual($0.localizedDescription, ControlNumberInput.absoluteCoordinatesMessage)
            }
        }
        XCTAssertEqual(try Editor.destination(x: "0", y: "-1.234", z: "10"),
                       SafetyVector3Dto(x: 0, y: -1.234, z: 10))
        XCTAssertThrowsError(try Editor.destination(x: "1.2345", y: "0", z: "10"))
        XCTAssertNil(try ControlNumberInput.optional("  "))
        XCTAssertEqual(try ControlNumberInput.optional("0"), 0)
        XCTAssertEqual(try ControlNumberInput.optional("-1.25"), -1.25)
        XCTAssertNil(try ControlNumberInput.feedrate(""))
        XCTAssertNil(try ControlNumberInput.feedrate(" "))
        for input in ["nan", "inf", "-inf", "1e999", "abc"] {
            XCTAssertThrowsError(try ControlNumberInput.optional(input))
        }
        for input in ["0", "-1", "1", "600", "3000", "\(Int.max)", "1.5", "1e100"] {
            XCTAssertThrowsError(try ControlNumberInput.feedrate(input))
        }
    }

    func test_absoluteVisibility_requiresSpecificSupportAndKnownAxes() {
        XCTAssertFalse(JogSubgroup.AbsolutePositionControls.isVisible(nil))
        var caps = Self.fullCaps
        XCTAssertFalse(JogSubgroup.AbsolutePositionControls.isVisible(caps))
        caps.supportsAbsoluteMovement = true
        XCTAssertTrue(JogSubgroup.AbsolutePositionControls.isVisible(caps))
        var limited = Self.xyOnlyCaps
        limited.supportsAbsoluteMovement = true
        XCTAssertFalse(JogSubgroup.AbsolutePositionControls.isVisible(limited))
    }

    func test_absoluteGuidance_staysNeutralUntilDestinationInputExists() {
        typealias Editor = JogSubgroup.AbsolutePositionControls
        XCTAssertFalse(Editor.hasDestinationInput(x: "", y: "", z: ""))
        XCTAssertFalse(Editor.hasDestinationInput(x: " ", y: "\n", z: "\t"))
        XCTAssertTrue(Editor.hasDestinationInput(x: "0", y: "", z: ""))
        XCTAssertTrue(Editor.hasDestinationInput(x: "", y: "-1", z: ""))
        XCTAssertTrue(Editor.hasDestinationInput(x: "", y: "", z: "invalid"))
    }

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

    // MARK: - Fixtures

    /// Decodes the canonical printer JSON and forces `state` to `"idle"` so
    /// `PrinterControlsViewModel.canControl` evaluates `true`. The default
    /// fixture reports `state: "printing"`, which triggers the disabled hint
    /// ("Disabled while printing.") and hides the direction-specific spec §4.1
    /// text these tests exercise.
    static func idlePrinter() throws -> Printer {
        let json = TestJSON.printer.replacingOccurrences(
            of: "\"state\": \"printing\"",
            with: "\"state\": \"idle\""
        )
        return try TestData.decodePrinter(from: json)
    }

    // MARK: - visibleAxes

    func test_visibleAxes_whenCapabilitiesNil_returnsAllCanonicalAxes() {
        XCTAssertEqual(JogSubgroup.visibleAxes(for: nil), [])
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
        XCTAssertTrue(JogSubgroup.isHidden(for: nil))
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
        let vm = PrinterControlsViewModel.configuredForTests(
            printerService: MockPrinterService(),
            printer: try TestData.decodePrinter()
        )
        let view = JogSubgroup(viewModel: vm)
        XCTAssertEqual(view.jogAccessibilityLabel(direction: 1), "Jog forward")
    }

    func test_jogAccessibilityLabel_negative_isJogBackward() throws {
        let vm = PrinterControlsViewModel.configuredForTests(
            printerService: MockPrinterService(),
            printer: try TestData.decodePrinter()
        )
        let view = JogSubgroup(viewModel: vm)
        XCTAssertEqual(view.jogAccessibilityLabel(direction: -1), "Jog backward")
    }

    func test_jogAccessibilityHint_positive_usesPositiveDirection() throws {
        let vm = PrinterControlsViewModel.configuredForTests(
            printerService: MockPrinterService(),
            printer: try Self.idlePrinter()
        )
        let view = JogSubgroup(viewModel: vm)
        let hint = view.jogAccessibilityHint(direction: 1, stepLabelText: "1", hasError: false)
        XCTAssertTrue(hint.contains("positive"), "Hint for + jog must say 'positive' per spec §4.1, got: \(hint)")
    }

    func test_jogAccessibilityHint_negative_usesNegativeDirection() throws {
        let vm = PrinterControlsViewModel.configuredForTests(
            printerService: MockPrinterService(),
            printer: try Self.idlePrinter()
        )
        let view = JogSubgroup(viewModel: vm)
        let hint = view.jogAccessibilityHint(direction: -1, stepLabelText: "1", hasError: false)
        XCTAssertTrue(hint.contains("negative"), "Hint for - jog must say 'negative' per spec §4.1, got: \(hint)")
    }

    func test_jogAccessibilityHint_disabled_returnsSpec41Text() throws {
        // Default printer state is "printing" -> canControl = false
        let vm = PrinterControlsViewModel.configuredForTests(
            printerService: MockPrinterService(),
            printer: try TestData.decodePrinter()
        )
        let view = JogSubgroup(viewModel: vm)
        let hint = view.jogAccessibilityHint(direction: 1, stepLabelText: "1", hasError: false)
        XCTAssertEqual(hint, "Disabled while printing.")
    }

    func test_jogAccessibilityValue_pending_returnsPending() throws {
        let vm = PrinterControlsViewModel.configuredForTests(
            printerService: MockPrinterService(),
            printer: try TestData.decodePrinter()
        )
        let view = JogSubgroup(viewModel: vm)
        XCTAssertEqual(view.jogAccessibilityValue(isPending: true, hasError: false), "Pending")
    }

    func test_jogAccessibilityValue_idle_isEmpty() throws {
        let vm = PrinterControlsViewModel.configuredForTests(
            printerService: MockPrinterService(),
            printer: try TestData.decodePrinter()
        )
        let view = JogSubgroup(viewModel: vm)
        XCTAssertEqual(view.jogAccessibilityValue(isPending: false, hasError: false), "")
    }

}
