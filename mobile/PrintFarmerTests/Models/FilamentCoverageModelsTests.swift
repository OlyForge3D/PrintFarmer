import XCTest
@testable import PrintFarmer

// MARK: - Filament Coverage Model Decoding Tests (F4-M / issue #778)
//
// Locks the wire contract: canonical lowercase status vocabulary is
// mandatory, PascalCase and legacy `Insufficient` are tolerated on
// read only, integer enum values are rejected, camelCase keys are
// preserved, and re-encoding always emits the canonical spelling.

final class FilamentCoverageModelsTests: XCTestCase {

    // MARK: - Canonical lowercase decoding

    func testDecodesCanonicalCoversStatus() throws {
        let json = "\"covers\"".data(using: .utf8)!
        let status = try JSONDecoder().decode(FilamentCoverageStatus.self, from: json)
        XCTAssertEqual(status, .covers)
    }

    func testDecodesCanonicalRunoutStatus() throws {
        let json = "\"runout\"".data(using: .utf8)!
        let status = try JSONDecoder().decode(FilamentCoverageStatus.self, from: json)
        XCTAssertEqual(status, .runout)
    }

    func testDecodesCanonicalUnknownStatus() throws {
        let json = "\"unknown\"".data(using: .utf8)!
        let status = try JSONDecoder().decode(FilamentCoverageStatus.self, from: json)
        XCTAssertEqual(status, .unknown)
    }

    // MARK: - Migration decoding

    func testDecodesMigrationCoversStatus() throws {
        let json = "\"Covers\"".data(using: .utf8)!
        XCTAssertEqual(try JSONDecoder().decode(FilamentCoverageStatus.self, from: json), .covers)
    }

    func testDecodesMigrationRunoutStatus() throws {
        let json = "\"Runout\"".data(using: .utf8)!
        XCTAssertEqual(try JSONDecoder().decode(FilamentCoverageStatus.self, from: json), .runout)
    }

    func testDecodesMigrationUnknownStatus() throws {
        let json = "\"Unknown\"".data(using: .utf8)!
        XCTAssertEqual(try JSONDecoder().decode(FilamentCoverageStatus.self, from: json), .unknown)
    }

    func testDecodesLegacyInsufficientAsRunout() throws {
        // The backend converter accepts `Insufficient` for backwards
        // compatibility (see FilamentCoverageDtos.cs). The iOS client
        // must map it to `.runout` so an operator running against a
        // half-upgraded server still sees the runout affordance.
        let json = "\"Insufficient\"".data(using: .utf8)!
        XCTAssertEqual(try JSONDecoder().decode(FilamentCoverageStatus.self, from: json), .runout)
    }

    // MARK: - Rejected inputs

    func testRejectsIntegerStatusValue() {
        // Integers here would silently misalign with the local
        // lowercase converter (backend `enum` is 0/1/2). Decoding
        // must fail loud so schema drift is caught, never coerced.
        let json = "0".data(using: .utf8)!
        XCTAssertThrowsError(try JSONDecoder().decode(FilamentCoverageStatus.self, from: json)) { error in
            guard case DecodingError.dataCorrupted = error else {
                return XCTFail("Expected .dataCorrupted, got \(error)")
            }
        }
    }

    func testRejectsUnknownStringStatusValue() {
        let json = "\"partial\"".data(using: .utf8)!
        XCTAssertThrowsError(try JSONDecoder().decode(FilamentCoverageStatus.self, from: json)) { error in
            guard case DecodingError.dataCorrupted = error else {
                return XCTFail("Expected .dataCorrupted, got \(error)")
            }
        }
    }

    // MARK: - Canonical encoding

    func testEncodesCoversAsLowercase() throws {
        let data = try JSONEncoder().encode(FilamentCoverageStatus.covers)
        XCTAssertEqual(String(data: data, encoding: .utf8), "\"covers\"")
    }

    func testEncodesRunoutAsLowercase() throws {
        let data = try JSONEncoder().encode(FilamentCoverageStatus.runout)
        XCTAssertEqual(String(data: data, encoding: .utf8), "\"runout\"")
    }

    func testEncodesUnknownAsLowercase() throws {
        let data = try JSONEncoder().encode(FilamentCoverageStatus.unknown)
        XCTAssertEqual(String(data: data, encoding: .utf8), "\"unknown\"")
    }

    // MARK: - Envelope decoding (real-shape payloads)

    private func makeDecoder() -> JSONDecoder {
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .custom { d in
            let text = try d.singleValueContainer().decode(String.self)
            if let date = APIClient.iso8601WithFractional.date(from: text) { return date }
            if let date = APIClient.iso8601Plain.date(from: text) { return date }
            throw DecodingError.dataCorruptedError(in: try d.singleValueContainer(),
                                                    debugDescription: "bad date: \(text)")
        }
        return decoder
    }

    func testDecodesPrinterCoverageEnvelope() throws {
        let json = """
        {
          "printerId": "11111111-2222-3333-4444-555555555555",
          "printerName": "MK4-Alpha",
          "status": "covers",
          "toolheads": [
            {
              "toolheadIndex": 0,
              "toolheadName": "Extruder 1",
              "spoolId": 42,
              "material": "PLA",
              "filamentColor": "#FF0000",
              "remainingGrams": 420.5,
              "currentJobRequiredGrams": 120,
              "currentJobRemainingGrams": 80,
              "queuedRequiredGrams": 0,
              "totalDemandGrams": 80,
              "status": "covers",
              "statusReason": null,
              "predictedRunoutAt": null,
              "predictedRunoutLayer": null
            }
          ],
          "activeJobId": "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
          "activeJobName": "benchy.gcode",
          "activeJobProgress": 45.0,
          "earliestPredictedRunoutAt": null,
          "assignedQueuedJobCount": 0,
          "evaluatedAtUtc": "2026-07-21T20:00:00Z"
        }
        """.data(using: .utf8)!

        let coverage = try makeDecoder().decode(PrinterFilamentCoverage.self, from: json)
        XCTAssertEqual(coverage.status, .covers)
        XCTAssertEqual(coverage.printerName, "MK4-Alpha")
        XCTAssertEqual(coverage.toolheads.count, 1)
        XCTAssertEqual(coverage.toolheads.first?.material, "PLA")
        XCTAssertEqual(coverage.toolheads.first?.status, .covers)
        XCTAssertEqual(coverage.assignedQueuedJobCount, 0)
    }

    func testDecodesFleetCoverageEnvelope() throws {
        let json = """
        {
          "printers": [
            {
              "printerId": "11111111-2222-3333-4444-555555555555",
              "printerName": "MK4-Alpha",
              "status": "runout",
              "toolheads": [],
              "activeJobId": null,
              "activeJobName": null,
              "activeJobProgress": null,
              "earliestPredictedRunoutAt": "2026-07-21T22:30:00Z",
              "assignedQueuedJobCount": 2,
              "evaluatedAtUtc": "2026-07-21T20:00:00Z"
            }
          ],
          "evaluatedAtUtc": "2026-07-21T20:00:00Z"
        }
        """.data(using: .utf8)!
        let fleet = try makeDecoder().decode(FleetFilamentCoverage.self, from: json)
        XCTAssertEqual(fleet.printers.count, 1)
        XCTAssertEqual(fleet.printers.first?.status, .runout)
        XCTAssertNotNil(fleet.printers.first?.earliestPredictedRunoutAt)
    }

    func testToolheadStableIdPrefersBackendToolheadId() {
        let uuid = UUID()
        let withId = ToolheadFilamentCoverage(
            toolheadIndex: 3,
            toolheadId: uuid,
            toolheadName: "A",
            status: .unknown
        )
        XCTAssertTrue(withId.id.hasPrefix("id:\(uuid.uuidString)"))

        let noId = ToolheadFilamentCoverage(
            toolheadIndex: 3,
            toolheadName: "A",
            status: .unknown
        )
        XCTAssertEqual(noId.id, "index:3")
    }

    func testDuplicateToolheadNamesRemainDistinctByStableId() {
        // Two toolheads share the same display name but have different
        // indices. Their `.id` values MUST NOT collide — otherwise
        // ForEach would collapse them into one row.
        let a = ToolheadFilamentCoverage(toolheadIndex: 0, toolheadName: "Dup", status: .covers)
        let b = ToolheadFilamentCoverage(toolheadIndex: 1, toolheadName: "Dup", status: .runout)
        XCTAssertNotEqual(a.id, b.id)
    }

    // MARK: - Invalidation event

    func testDecodesFilamentCoverageChangedEventWithPrinterId() throws {
        let json = """
        {
          "printerId": "11111111-2222-3333-4444-555555555555",
          "reason": "printer-status-change",
          "occurredAt": "2026-07-21T20:00:00Z"
        }
        """.data(using: .utf8)!
        let event = try makeDecoder().decode(FilamentCoverageChangedEvent.self, from: json)
        XCTAssertEqual(event.reason, "printer-status-change")
        XCTAssertNotNil(event.printerId)
    }

    func testDecodesFilamentCoverageChangedEventWithNullPrinterId() throws {
        // Fleet-scoped invalidation: no printerId. Both scoped and
        // unscoped detail view models must handle this shape.
        let json = """
        {
          "printerId": null,
          "reason": "spoolman-reindex",
          "occurredAt": "2026-07-21T20:00:00Z"
        }
        """.data(using: .utf8)!
        let event = try makeDecoder().decode(FilamentCoverageChangedEvent.self, from: json)
        XCTAssertNil(event.printerId)
        XCTAssertEqual(event.reason, "spoolman-reindex")
    }
}
