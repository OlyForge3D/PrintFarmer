import XCTest
@testable import PrintFarmer

/// Wire-contract tests for `Toolhead` and `PrinterDetails` — the additive
/// F6 fields added in PR #752. Locks the guarantees:
/// - `cumulativePrintHours` is tri-state on the wire: numeric (including 0),
///   explicit null, or missing.
/// - `supportsPerToolAttribution` is always emitted; treat missing as false.
/// - `fallbackGroups` is `[]` (feature disabled) or absent (legacy); both
///   must produce an empty Swift array so UI callers never crash on nil.
/// - `toolheads` is index-sorted at decode.
final class ToolheadModelsTests: XCTestCase {

    private let decoder: JSONDecoder = {
        let d = JSONDecoder()
        d.dateDecodingStrategy = .iso8601
        return d
    }()

    // MARK: - Toolhead.cumulativePrintHours tri-state

    func testToolhead_cumulativePrintHours_present_numeric() throws {
        let json = """
        {
          "id": "550e8400-e29b-41d4-a716-446655440000",
          "name": "T0",
          "index": 0,
          "isPrimary": true,
          "toolheadType": "Physical",
          "cumulativePrintHours": 12.5
        }
        """
        let t = try decoder.decode(Toolhead.self, from: Data(json.utf8))
        XCTAssertEqual(t.cumulativePrintHours, 12.5)
    }

    func testToolhead_cumulativePrintHours_present_zero_isDistinctFromNil() throws {
        // Wire zero means "supported and fresh" — must NOT collapse to nil.
        let json = """
        {
          "id": "550e8400-e29b-41d4-a716-446655440000",
          "name": "T0",
          "index": 0,
          "isPrimary": true,
          "toolheadType": "Physical",
          "cumulativePrintHours": 0
        }
        """
        let t = try decoder.decode(Toolhead.self, from: Data(json.utf8))
        XCTAssertNotNil(t.cumulativePrintHours)
        XCTAssertEqual(t.cumulativePrintHours, 0)
    }

    func testToolhead_cumulativePrintHours_explicitNull_isNil() throws {
        // Backend emits explicit null when per-tool attribution is off; that
        // must decode to Swift nil so UI can suppress the odometer.
        let json = """
        {
          "id": "550e8400-e29b-41d4-a716-446655440000",
          "name": "T0",
          "index": 0,
          "isPrimary": true,
          "toolheadType": "Physical",
          "cumulativePrintHours": null
        }
        """
        let t = try decoder.decode(Toolhead.self, from: Data(json.utf8))
        XCTAssertNil(t.cumulativePrintHours)
    }

    func testToolhead_cumulativePrintHours_missingKey_isNil() throws {
        // Legacy/pre-#752 payloads may omit the key entirely; still nil.
        let json = """
        {
          "id": "550e8400-e29b-41d4-a716-446655440000",
          "name": "T0",
          "index": 0,
          "isPrimary": true,
          "toolheadType": "Physical"
        }
        """
        let t = try decoder.decode(Toolhead.self, from: Data(json.utf8))
        XCTAssertNil(t.cumulativePrintHours)
    }

    // MARK: - ToolheadType / NozzleType string enums

    func testToolheadType_decodesPascalCaseWireValues() throws {
        for (wire, expected) in [
            ("Physical", ToolheadType.physical),
            ("MmuGate", .mmuGate),
            ("Unknown", .unknown)
        ] as [(String, ToolheadType)] {
            let json = "\"\(wire)\""
            let t = try decoder.decode(ToolheadType.self, from: Data(json.utf8))
            XCTAssertEqual(t, expected)
        }
    }

    func testToolheadType_unknownWireValue_fallsBackToUnknown() throws {
        // Enum drift — a new backend case must not fail the whole decode.
        let json = "\"SomeFutureCase\""
        let t = try decoder.decode(ToolheadType.self, from: Data(json.utf8))
        XCTAssertEqual(t, .unknown)
    }

    func testNozzleType_decodesPascalCaseWireValues() throws {
        for (wire, expected) in [
            ("Brass", NozzleType.brass),
            ("HardenedSteel", .hardenedSteel),
            ("StainlessSteel", .stainlessSteel),
            ("TungstenCarbide", .tungstenCarbide),
            ("Abrasive", .abrasive),
            ("Unknown", .unknown)
        ] as [(String, NozzleType)] {
            let json = "\"\(wire)\""
            let n = try decoder.decode(NozzleType.self, from: Data(json.utf8))
            XCTAssertEqual(n, expected)
        }
    }

    func testNozzleType_unknownWireValue_fallsBackToUnknown() throws {
        let json = "\"Titanium\""
        let n = try decoder.decode(NozzleType.self, from: Data(json.utf8))
        XCTAssertEqual(n, .unknown)
    }

    // MARK: - PrinterDetails.supportsPerToolAttribution

    func testPrinterDetails_supportsPerToolAttribution_true() throws {
        let details = try decoder.decode(PrinterDetails.self, from: Data(Self.detailsJSON(attr: "true", fallbackGroups: "[]").utf8))
        XCTAssertTrue(details.supportsPerToolAttribution)
    }

    func testPrinterDetails_supportsPerToolAttribution_false() throws {
        let details = try decoder.decode(PrinterDetails.self, from: Data(Self.detailsJSON(attr: "false", fallbackGroups: "[]").utf8))
        XCTAssertFalse(details.supportsPerToolAttribution)
    }

    func testPrinterDetails_supportsPerToolAttribution_missingIsFalse() throws {
        // Backend always emits it, but Swift stays defensive against legacy
        // /details payloads that pre-date PR #752.
        let json = """
        {
          "id": "550e8400-e29b-41d4-a716-446655440000",
          "name": "MK4",
          "backend": "PrusaLink"
        }
        """
        let details = try decoder.decode(PrinterDetails.self, from: Data(json.utf8))
        XCTAssertFalse(details.supportsPerToolAttribution)
    }

    // MARK: - PrinterDetails.fallbackGroups

    func testPrinterDetails_fallbackGroups_emptyArray() throws {
        // Multi-slot fallback feature disabled → backend emits `[]`.
        let details = try decoder.decode(PrinterDetails.self, from: Data(Self.detailsJSON(attr: "true", fallbackGroups: "[]").utf8))
        XCTAssertTrue(details.fallbackGroups.isEmpty)
    }

    func testPrinterDetails_fallbackGroups_missingKeyDecodesAsEmpty() throws {
        // Legacy payload — must not crash; treat as feature disabled.
        let json = """
        {
          "id": "550e8400-e29b-41d4-a716-446655440000",
          "name": "MK4",
          "backend": "PrusaLink",
          "supportsPerToolAttribution": false
        }
        """
        let details = try decoder.decode(PrinterDetails.self, from: Data(json.utf8))
        XCTAssertTrue(details.fallbackGroups.isEmpty)
    }

    // MARK: - PrinterDetails.toolheads ordering

    func testPrinterDetails_toolheads_sortedByIndexAtDecode() throws {
        let json = """
        {
          "id": "550e8400-e29b-41d4-a716-446655440000",
          "name": "XL",
          "backend": "PrusaLink",
          "supportsPerToolAttribution": true,
          "toolheads": [
            {"id":"11111111-1111-1111-1111-111111111111","index":2,"isPrimary":false,"toolheadType":"Physical","cumulativePrintHours":5.0},
            {"id":"22222222-2222-2222-2222-222222222222","index":0,"isPrimary":true,"toolheadType":"Physical","cumulativePrintHours":10.0},
            {"id":"33333333-3333-3333-3333-333333333333","index":1,"isPrimary":false,"toolheadType":"Physical","cumulativePrintHours":null}
          ],
          "fallbackGroups": []
        }
        """
        let details = try decoder.decode(PrinterDetails.self, from: Data(json.utf8))
        XCTAssertEqual(details.toolheads.map { $0.index }, [0, 1, 2])
        // Also proves the tri-state carries through the container decode:
        XCTAssertEqual(details.toolheads[0].cumulativePrintHours, 10.0)
        XCTAssertNil(details.toolheads[1].cumulativePrintHours)
        XCTAssertEqual(details.toolheads[2].cumulativePrintHours, 5.0)
    }

    // MARK: - JSON helper

    private static func detailsJSON(attr: String, fallbackGroups: String) -> String {
        """
        {
          "id": "550e8400-e29b-41d4-a716-446655440000",
          "name": "MK4",
          "backend": "PrusaLink",
          "hasMmu": false,
          "manufacturerName": "Prusa",
          "modelName": "MK4",
          "toolheads": [],
          "fallbackGroups": \(fallbackGroups),
          "supportsPerToolAttribution": \(attr)
        }
        """
    }
}
