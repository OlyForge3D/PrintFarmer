import XCTest
@testable import PrintFarmer

final class PrinterBackendCapabilitiesTests: XCTestCase {

    // MARK: - Static fallback table

    func testFallback_moonraker_supportsEverythingWithXYZ() {
        let caps = PrinterBackendCapabilities.fallback(for: .moonraker)
        XCTAssertTrue(caps.supportsMovement)
        XCTAssertTrue(caps.supportsTemperatureControl)
        XCTAssertTrue(caps.supportsBedTemperature)
        XCTAssertTrue(caps.supportsFanControl)
        XCTAssertTrue(caps.supportsHoming)
        XCTAssertEqual(caps.supportedAxes, ["X", "Y", "Z"])
    }

    func testFallback_prusaLink_supportsEverythingWithXYZ() {
        let caps = PrinterBackendCapabilities.fallback(for: .prusaLink)
        XCTAssertTrue(caps.supportsMovement)
        XCTAssertTrue(caps.supportsTemperatureControl)
        XCTAssertTrue(caps.supportsBedTemperature)
        XCTAssertTrue(caps.supportsFanControl)
        XCTAssertTrue(caps.supportsHoming)
        XCTAssertEqual(caps.supportedAxes, ["X", "Y", "Z"])
    }

    func testFallback_octoPrint_supportsEverythingWithXYZ() {
        let caps = PrinterBackendCapabilities.fallback(for: .octoPrint)
        XCTAssertTrue(caps.supportsMovement)
        XCTAssertTrue(caps.supportsTemperatureControl)
        XCTAssertTrue(caps.supportsBedTemperature)
        XCTAssertTrue(caps.supportsFanControl)
        XCTAssertTrue(caps.supportsHoming)
        XCTAssertEqual(caps.supportedAxes, ["X", "Y", "Z"])
    }

    func testFallback_flashForge_movementAndHomingButNoBedOrFan() {
        let caps = PrinterBackendCapabilities.fallback(for: .flashForge)
        XCTAssertTrue(caps.supportsMovement)
        XCTAssertTrue(caps.supportsTemperatureControl)
        XCTAssertFalse(caps.supportsBedTemperature)
        XCTAssertFalse(caps.supportsFanControl)
        XCTAssertTrue(caps.supportsHoming)
        XCTAssertEqual(caps.supportedAxes, ["X", "Y", "Z"])
    }

    func testFallback_sdcp_allDisabledNoAxes() {
        let caps = PrinterBackendCapabilities.fallback(for: .sdcp)
        XCTAssertFalse(caps.supportsMovement)
        XCTAssertFalse(caps.supportsTemperatureControl)
        XCTAssertFalse(caps.supportsBedTemperature)
        XCTAssertFalse(caps.supportsFanControl)
        XCTAssertFalse(caps.supportsHoming)
        XCTAssertTrue(caps.supportedAxes.isEmpty)
    }

    func testFallback_unknown_allDisabledNoAxes() {
        let caps = PrinterBackendCapabilities.fallback(for: .unknown)
        XCTAssertFalse(caps.supportsMovement)
        XCTAssertFalse(caps.supportsTemperatureControl)
        XCTAssertFalse(caps.supportsBedTemperature)
        XCTAssertFalse(caps.supportsFanControl)
        XCTAssertFalse(caps.supportsHoming)
        XCTAssertTrue(caps.supportedAxes.isEmpty)
    }

    // MARK: - Codable

    func testCodableRoundTrip() throws {
        let original = PrinterBackendCapabilities.fallback(for: .moonraker)
        let data = try JSONEncoder().encode(original)
        let decoded = try JSONDecoder().decode(PrinterBackendCapabilities.self, from: data)
        XCTAssertEqual(decoded, original)
    }

    // MARK: - Equatable

    func testEquatable_sameValues_areEqual() {
        let a = PrinterBackendCapabilities.fallback(for: .moonraker)
        let b = PrinterBackendCapabilities.fallback(for: .moonraker)
        XCTAssertEqual(a, b)
    }

    func testEquatable_differentBackends_areNotEqual() {
        let moonraker = PrinterBackendCapabilities.fallback(for: .moonraker)
        let sdcp = PrinterBackendCapabilities.fallback(for: .sdcp)
        XCTAssertNotEqual(moonraker, sdcp)
    }
}
