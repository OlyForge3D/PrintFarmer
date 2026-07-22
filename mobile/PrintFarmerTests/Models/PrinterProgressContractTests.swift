import XCTest
@testable import PrintFarmer

/// Pins the contract for `Printer.progress`:
///   - Backend sends `progress` as a percent in **0–100** (per `PrinterController` /
///     `CompletePrinterDto` on the .NET side).
///   - iOS internal scale is **0.0–1.0** for SwiftUI consumers (`PrintProgressBar`,
///     `Double.percentFormatted`, `ProgressView`).
///   - The decoder clamps out-of-range backend values to [0, 100] before normalizing,
///     so the in-memory `Printer.progress` is **always** in `0.0...1.0` (or `nil`).
///
/// If a future change drops normalization (e.g. backend switches to 0–1.0), or
/// admits values outside the 0–100 input range without clamping, these tests fail
/// loudly so the regression is caught before progress-aware UI silently misrenders.
///
/// See issue #277. This is a pin, not enforcement — the model uses optional Double,
/// and the decoder does not throw on out-of-range inputs (it clamps). If we ever
/// want strict rejection, change `clampingMode` and update the relevant cases here.
final class PrinterProgressContractTests: XCTestCase {

    private let decoder: JSONDecoder = {
        let d = JSONDecoder()
        d.dateDecodingStrategy = .iso8601
        return d
    }()

    // MARK: - In-range backend values normalize to 0.0...1.0

    func testProgressZeroDecodesToZero() throws {
        let printer = try decode(progressJSON: "0")
        XCTAssertEqual(printer.progress, 0.0)
        try assertProgressInContract(printer)
    }

    func testProgressFiftyDecodesToOneHalf() throws {
        let printer = try decode(progressJSON: "50")
        XCTAssertEqual(try XCTUnwrap(printer.progress), 0.5, accuracy: 0.0001)
        try assertProgressInContract(printer)
    }

    func testProgressOneHundredDecodesToOne() throws {
        let printer = try decode(progressJSON: "100")
        XCTAssertEqual(try XCTUnwrap(printer.progress), 1.0, accuracy: 0.0001)
        try assertProgressInContract(printer)
    }

    func testProgressFractionalInRange() throws {
        // Real backends emit decimals (e.g. 42.7%).
        let printer = try decode(progressJSON: "42.7")
        XCTAssertEqual(try XCTUnwrap(printer.progress), 0.427, accuracy: 0.0001)
        try assertProgressInContract(printer)
    }

    // MARK: - Out-of-range backend values clamp into the contract

    func testProgressNegativeClampsToZero() throws {
        // Drift case: if backend ever emits < 0, the model must clamp — not propagate
        // a negative value into UI math.
        let printer = try decode(progressJSON: "-5")
        XCTAssertEqual(printer.progress, 0.0)
        try assertProgressInContract(printer)
    }

    func testProgressOverflowClampsToOne() throws {
        // Drift case: if backend ever emits > 100, the model must clamp to 1.0
        // so PrintProgressBar / .percentFormatted don't render >100%.
        let printer = try decode(progressJSON: "150")
        XCTAssertEqual(printer.progress, 1.0)
        try assertProgressInContract(printer)
    }

    // MARK: - Optional handling

    func testProgressNullDecodesToNil() throws {
        let printer = try decode(progressJSON: "null")
        XCTAssertNil(printer.progress)
    }

    func testProgressMissingDecodesToNil() throws {
        let printer = try decoder.decode(
            Printer.self,
            from: Self.makePrinterJSON(progressFragment: nil).data(using: .utf8)!
        )
        XCTAssertNil(printer.progress)
    }

    // MARK: - Helpers

    private func decode(progressJSON: String) throws -> Printer {
        try decoder.decode(
            Printer.self,
            from: Self.makePrinterJSON(progressFragment: "\"progress\": \(progressJSON),")
                .data(using: .utf8)!
        )
    }

    /// Asserts the iOS internal contract: a non-nil `progress` is always within `0.0...1.0`.
    private func assertProgressInContract(_ printer: Printer, file: StaticString = #filePath, line: UInt = #line) throws {
        let progress = try XCTUnwrap(printer.progress, file: file, line: line)
        XCTAssertGreaterThanOrEqual(progress, 0.0, file: file, line: line)
        XCTAssertLessThanOrEqual(progress, 1.0, file: file, line: line)
    }

    /// Builds a minimal `CompletePrinterDto` payload, splicing in (or omitting) a
    /// `progress` JSON fragment. Other fields use the same camelCase names the .NET
    /// API emits.
    private static func makePrinterJSON(progressFragment: String?) -> String {
        let progress = progressFragment ?? ""
        return """
        {
            "id": "550e8400-e29b-41d4-a716-446655440000",
            "name": "Pin Test Printer",
            "backend": "Moonraker",
            "backendPort": 7125,
            "inMaintenance": false,
            "isEnabled": true,
            "isOnline": true,
            "state": "printing",
            \(progress)
            "obicoEnabled": false
        }
        """
    }
}
