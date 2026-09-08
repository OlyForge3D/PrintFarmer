import XCTest
@testable import PrintFarmer

final class PrinterBackendCapabilitiesTests: XCTestCase {

    func testFallbackNeverProvesPhysicalControlForAnyBackend() {
        for backend in [PrinterBackend.moonraker, .prusaLink, .octoPrint, .flashForge, .sdcp, .unknown] {
            let caps = PrinterBackendCapabilities.fallback(for: backend)
            XCTAssertFalse(caps.supportsMovement)
            XCTAssertFalse(caps.supportsTemperatureControl)
            XCTAssertFalse(caps.supportsBedTemperature)
            XCTAssertFalse(caps.supportsHoming)
            XCTAssertFalse(caps.supportsAbsoluteMovement)
            XCTAssertFalse(caps.supportsDisableMotors)
            XCTAssertFalse(caps.supportsExtrusion)
            XCTAssertFalse(caps.supportsZOffset)
            XCTAssertFalse(caps.supportsZOffsetFirmwareSave)
            XCTAssertFalse(caps.supportsFilamentLoad)
            XCTAssertFalse(caps.supportsFilamentUnload)
            XCTAssertFalse(caps.supportsFilamentChange)
            XCTAssertTrue(caps.supportedAxes.isEmpty)
        }
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

    func testEquatable_differentEvidence_isNotEqual() {
        XCTAssertNotEqual(
            PrinterBackendCapabilities.allControlsFixture,
            PrinterBackendCapabilities.fallback(for: .unknown)
        )
    }

    func testDemoAndDefaultMockDoNotAdvertiseNoOpControls() async throws {
        let demo = DemoPrinterService()
        for printer in try await demo.list(includeDisabled: false) {
            let caps = try await demo.getBackendCapabilities(printerId: printer.id)
            XCTAssertEqual(caps, .fallback(for: .unknown))
        }
        let mockCaps = try await MockPrinterService().getBackendCapabilities(printerId: UUID())
        XCTAssertEqual(mockCaps, .fallback(for: .unknown))
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

    func testWireDto_flashForge_heatersRequirePairedRouteProof_withoutMovementOrHoming() throws {
        for heatersProven in [false, true] {
            let json = Data("""
            {
                "printerId": "550e8400-e29b-41d4-a716-446655440000",
                "printerName": "FlashForge Adventurer 5M",
                "backend": "FlashForge",
                "supportsMovement": false,
                "supportsTemperatureControl": true,
                "supportsRelativeMovement": false,
                "supportsAbsoluteMovement": false,
                "supportsHoming": false,
                "supportsHomingXY": false,
                "supportsHomingZ": false,
                "supportsHotendTemperature": \(heatersProven),
                "supportsBedTemperature": \(heatersProven),
                "supportedAxes": []
            }
            """.utf8)
            let dto = try JSONDecoder().decode(PrinterBackendCapabilitiesWireDto.self, from: json)
            XCTAssertEqual(dto.backend, .flashForge)
            XCTAssertEqual(dto.supportsMovement, false)
            let caps = PrinterBackendCapabilities(wire: dto)
            XCTAssertFalse(caps.supportsMovement)
            XCTAssertFalse(caps.supportsAbsoluteMovement)
            XCTAssertFalse(caps.supportsHoming)
            XCTAssertFalse(caps.supportsHomingXY)
            XCTAssertFalse(caps.supportsHomingZ)
            XCTAssertTrue(caps.supportedAxes.isEmpty)
            XCTAssertEqual(caps.supportsTemperatureControl, heatersProven,
                           "Legacy temperature metadata alone does not prove route support")
            XCTAssertEqual(caps.supportsBedTemperature, heatersProven,
                           "Current FlashForge route proves both heaters together")
        }
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

    func testGenericFlagsDoNotEnableSpecificOperations() throws {
        let json = Data("""
        {"printerId":"\(TestData.testUUID)","backend":"Moonraker",
         "supportsMovement":true,"supportsTemperatureControl":true,
         "supportsControlOperations":true,"supportsFilamentControl":true}
        """.utf8)
        let wire = try JSONDecoder().decode(PrinterBackendCapabilitiesWireDto.self, from: json)
        XCTAssertEqual(PrinterBackendCapabilities(wire: wire), .fallback(for: .unknown))
    }

    func testSpecificEvidenceIsUsedWithoutBackendNameAllowlist() throws {
        let json = Data("""
        {"printerId":"\(TestData.testUUID)","backend":"FutureBackend",
         "supportsRelativeMovement":true,"supportsAbsoluteMovement":true,
         "supportsHotendTemperature":true,"supportsBedTemperature":false,
         "supportsHoming":false,"supportsHomingXY":true,"supportsHomingZ":false,
         "supportsExtrusion":true,"supportsDisableMotors":true,
         "supportsFilamentLoad":true,"supportsFilamentUnload":true,"supportsFilamentChange":false,
         "supportsZOffset":true,"supportsZOffsetFirmwareSave":false,"supportedAxes":["x","y","e"]}
        """.utf8)
        let wire = try JSONDecoder().decode(PrinterBackendCapabilitiesWireDto.self, from: json)
        let caps = PrinterBackendCapabilities(wire: wire)
        XCTAssertTrue(caps.supportsMovement)
        XCTAssertTrue(caps.supportsAbsoluteMovement)
        XCTAssertTrue(caps.supportsExtrusion)
        XCTAssertTrue(caps.supportsDisableMotors)
        XCTAssertTrue(caps.supportsTemperatureControl)
        XCTAssertFalse(caps.supportsBedTemperature)
        XCTAssertFalse(caps.supportsHoming)
        XCTAssertTrue(caps.supportsHomingXY)
        XCTAssertFalse(caps.supportsHomingZ)
        XCTAssertTrue(caps.supportsZOffset)
        XCTAssertFalse(caps.supportsZOffsetFirmwareSave)
        XCTAssertTrue(caps.supportsFilamentLoad)
        XCTAssertTrue(caps.supportsFilamentUnload)
        XCTAssertFalse(caps.supportsFilamentChange)
        XCTAssertEqual(caps.supportedAxes, ["X", "Y"])
    }
}
