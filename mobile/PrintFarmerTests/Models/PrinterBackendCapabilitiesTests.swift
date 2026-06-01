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
    // MARK: - Wire DTO Decoder Fixtures

    // Full-support fixture: Moonraker backend — all operation flags true.
    // Validates that the wire DTO decodes camelCase JSON and preserves all bool fields.
    func testWireDto_fullSupport_moonraker() throws {
        let json = Data("""
        {
            "printerId": "550e8400-e29b-41d4-a716-446655440000",
            "printerName": "Test Moonraker",
            "backend": "Moonraker",
            "supportsMovement": true,
            "supportsTemperatureControl": true,
            "supportsCamera": true,
            "supportsFileDownload": true,
            "supportsFileList": true,
            "supportsFileUpload": true,
            "supportsStartPrint": true,
            "supportsControlOperations": true,
            "supportsFileMetadata": true,
            "supportsPrinterInformation": true,
            "supportsHistory": true,
            "supportsFilamentControl": true
        }
        """.utf8)
        let dto = try JSONDecoder().decode(PrinterBackendCapabilitiesWireDto.self, from: json)
        XCTAssertEqual(dto.backend, .moonraker)
        XCTAssertEqual(dto.supportsMovement, true)
        XCTAssertEqual(dto.supportsTemperatureControl, true)
        XCTAssertEqual(dto.supportsControlOperations, true)
        XCTAssertEqual(dto.supportsCamera, true)
    }

    // Partial-support fixture: FlashForge backend — movement and hotend temp supported,
    // but fan control, file management, and camera are absent.
    func testWireDto_partialSupport_flashForge() throws {
        let json = Data("""
        {
            "printerId": "550e8400-e29b-41d4-a716-446655440000",
            "printerName": "FlashForge Adventurer 5M",
            "backend": "FlashForge",
            "supportsMovement": true,
            "supportsTemperatureControl": true,
            "supportsCamera": false,
            "supportsFileDownload": false,
            "supportsFileList": false,
            "supportsFileUpload": false,
            "supportsStartPrint": false,
            "supportsControlOperations": false,
            "supportsFileMetadata": false,
            "supportsPrinterInformation": false,
            "supportsHistory": false,
            "supportsFilamentControl": false
        }
        """.utf8)
        let dto = try JSONDecoder().decode(PrinterBackendCapabilitiesWireDto.self, from: json)
        XCTAssertEqual(dto.backend, .flashForge)
        XCTAssertEqual(dto.supportsMovement, true,
                       "FlashForge supports movement (cartesian homing)")
        XCTAssertEqual(dto.supportsTemperatureControl, true,
                       "FlashForge supports hotend temp control")
        XCTAssertEqual(dto.supportsControlOperations, false,
                       "FlashForge does not expose fan/control operations")
        XCTAssertEqual(dto.supportsCamera, false)
    }

    // Resin fixture: SDCP/Elegoo backend — supportsMovement=false, supportsTemperatureControl=false.
    // This is the critical gating path: the UI must hide all movement and temp controls.
    func testWireDto_resin_sdcp_movementFalse() throws {
        let json = Data("""
        {
            "printerId": "550e8400-e29b-41d4-a716-446655440000",
            "printerName": "Elegoo Saturn 4 Ultra",
            "backend": "SDCP",
            "supportsMovement": false,
            "supportsTemperatureControl": false,
            "supportsCamera": false,
            "supportsFileDownload": true,
            "supportsFileList": true,
            "supportsFileUpload": true,
            "supportsStartPrint": true,
            "supportsControlOperations": false,
            "supportsFileMetadata": false,
            "supportsPrinterInformation": true,
            "supportsHistory": true,
            "supportsFilamentControl": false
        }
        """.utf8)
        let dto = try JSONDecoder().decode(PrinterBackendCapabilitiesWireDto.self, from: json)
        XCTAssertEqual(dto.backend, .sdcp)
        XCTAssertEqual(dto.supportsMovement, false,
                       "Resin printers have no gantry movement via SDCP")
        XCTAssertEqual(dto.supportsTemperatureControl, false,
                       "SDCP does not expose temp-set endpoints")
        XCTAssertEqual(dto.supportsControlOperations, false)
        // File ops present on SDCP — verify those decode correctly too
        XCTAssertEqual(dto.supportsFileList, true)
        XCTAssertEqual(dto.supportsStartPrint, true)
    }

    // Validates that optional fields absent from the wire response decode as nil
    // (graceful forward-compat: future backends may omit unsupported flags).
    func testWireDto_missingOptionalFields_decodedAsNil() throws {
        let json = Data("""
        {
            "printerId": "550e8400-e29b-41d4-a716-446655440000",
            "backend": "Moonraker"
        }
        """.utf8)
        let dto = try JSONDecoder().decode(PrinterBackendCapabilitiesWireDto.self, from: json)
        XCTAssertEqual(dto.backend, .moonraker)
        XCTAssertNil(dto.supportsMovement,
                     "Missing wire field must decode as nil, not crash")
        XCTAssertNil(dto.supportsTemperatureControl)
        XCTAssertNil(dto.printerName)
    }
}
