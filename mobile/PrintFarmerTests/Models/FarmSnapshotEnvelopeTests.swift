import XCTest
@testable import PrintFarmer

/// Gate C — the envelope is a structural secret-free allow-list projection.
final class FarmSnapshotEnvelopeTests: XCTestCase {

    func testProjectionCarriesEveryNonSecretCardField() {
        let printerID = UUID()
        let locationID = UUID()
        let printer = FarmSnapshotFixtures.printerWithSecrets(id: printerID, locationID: locationID)
        let projected = FarmSnapshotPrinter(printer)

        XCTAssertEqual(projected.id, printerID)
        XCTAssertEqual(projected.name, "Voron-01")
        XCTAssertEqual(projected.location?.id, locationID)
        XCTAssertEqual(projected.location?.name, "Rack A")
        XCTAssertEqual(projected.modelName, "Trident")
        XCTAssertEqual(projected.manufacturerName, "Voron Design")
        XCTAssertTrue(projected.isOnline)
        XCTAssertTrue(projected.isEnabled)
        XCTAssertFalse(projected.inMaintenance)
        XCTAssertEqual(projected.state, "printing")
        // Printer decodes 0–100 progress into 0–1.0.
        XCTAssertEqual(projected.progress ?? 0, 0.42, accuracy: 0.0001)
        XCTAssertEqual(projected.jobName, "bracket.gcode")
        XCTAssertEqual(projected.fileName, "bracket.gcode")
        XCTAssertEqual(projected.hotendTemp, 210.0)
        XCTAssertEqual(projected.hotendTarget, 215.0)
        XCTAssertEqual(projected.bedTemp, 60.0)
        XCTAssertEqual(projected.bedTarget, 60.0)
        XCTAssertEqual(projected.spool?.spoolName, "PLA Black")
        XCTAssertEqual(projected.spool?.material, "PLA")
        XCTAssertEqual(projected.spool?.remainingWeightG, 812.5)
        XCTAssertTrue(projected.obicoEnabled)
    }

    func testEncodedEnvelopeContainsNoSecretBytesOrForbiddenKeys() throws {
        let namespace = FarmSnapshotFixtures.namespace()
        let printer = FarmSnapshotFixtures.printerWithSecrets()
        let envelope = FarmSnapshotEnvelope(
            namespace: namespace,
            printers: [printer],
            lastUpdatedAtMillis: 1_700_000_000_000
        )
        let data = try FarmSnapshotEnvelope.makeEncoder().encode(envelope)

        // (a) raw UTF-8 byte scan for each secret sentinel
        let text = String(decoding: data, as: UTF8.self)
        for sentinel in FarmSnapshotFixtures.secretSentinels {
            XCTAssertFalse(text.contains(sentinel), "encoded envelope leaked secret \(sentinel)")
        }

        // (b) deep decoded-JSON forbidden-key walk
        let object = try JSONSerialization.jsonObject(with: data)
        var keys = Set<String>()
        FarmSnapshotFixtures.collectKeys(object, into: &keys)
        let leaked = keys.intersection(FarmSnapshotFixtures.forbiddenKeys)
        XCTAssertTrue(leaked.isEmpty, "encoded envelope leaked forbidden keys: \(leaked)")
    }

    func testPresentEmptyPayloadRoundTrips() throws {
        let namespace = FarmSnapshotFixtures.namespace()
        let envelope = FarmSnapshotEnvelope(namespace: namespace, printers: [], lastUpdatedAtMillis: 42)
        let data = try FarmSnapshotEnvelope.makeEncoder().encode(envelope)
        let decoded = try FarmSnapshotEnvelope.makeDecoder().decode(FarmSnapshotEnvelope.self, from: data)
        XCTAssertEqual(decoded.payload.count, 0)
        XCTAssertEqual(decoded, envelope)
    }

    func testTimestampEncodesAsInteger() throws {
        let namespace = FarmSnapshotFixtures.namespace()
        let envelope = FarmSnapshotEnvelope(namespace: namespace, printers: [], lastUpdatedAtMillis: 100_900)
        let data = try FarmSnapshotEnvelope.makeEncoder().encode(envelope)
        let text = String(decoding: data, as: UTF8.self)
        XCTAssertTrue(text.contains("\"lastUpdatedAtMillis\":100900"))
        XCTAssertFalse(text.contains("100900.0"))
    }

    func testUnsupportedSchemaIsFlagged() {
        let namespace = FarmSnapshotFixtures.namespace()
        let envelope = FarmSnapshotEnvelope(
            schemaVersion: FarmSnapshotEnvelope.currentSchemaVersion + 1,
            namespace: namespace,
            payload: [],
            lastUpdatedAtMillis: 1
        )
        XCTAssertFalse(envelope.isSupportedSchema)
    }

    func testLoadOutcomeApplicability() {
        XCTAssertEqual(FarmLoadOutcome.success.applicability, .apply)
        for outcome: FarmLoadOutcome in [.offline, .unauthorized, .forbidden, .serverError, .decodeFailure, .cancelled] {
            XCTAssertEqual(outcome.applicability, .preserve)
        }
    }

    // MARK: H6 — pending-ready payload parity

    func testProjectionCarriesPendingReadyFromAuthoritativeSource() {
        let printer = FarmSnapshotFixtures.printerWithSecrets()
        // The Printer decodes with state "printing" (NOT PendingReady). The
        // pending-ready flag must come from the authoritative source, not the state.
        XCTAssertEqual(printer.state, "printing")
        let pendingProjection = FarmSnapshotPrinter(printer, isPendingReady: true)
        XCTAssertTrue(pendingProjection.isPendingReady)
        let notPendingProjection = FarmSnapshotPrinter(printer, isPendingReady: false)
        XCTAssertFalse(notPendingProjection.isPendingReady)
        // The Printer-only convenience defaults to false (no auto-dispatch context).
        XCTAssertFalse(FarmSnapshotPrinter(printer).isPendingReady)
    }

    func testEnvelopeWiresPendingReadyFromPrinterIDSet() throws {
        let printerA = FarmSnapshotFixtures.printerWithSecrets()
        let printerB = FarmSnapshotFixtures.printerWithSecrets()
        let namespace = FarmSnapshotFixtures.namespace()
        let envelope = FarmSnapshotEnvelope(
            namespace: namespace,
            printers: [printerA, printerB],
            pendingReadyPrinterIDs: [printerA.id],
            lastUpdatedAtMillis: 1
        )
        let a = try XCTUnwrap(envelope.payload.first { $0.id == printerA.id })
        let b = try XCTUnwrap(envelope.payload.first { $0.id == printerB.id })
        XCTAssertTrue(a.isPendingReady)
        XCTAssertFalse(b.isPendingReady)
    }

    func testPendingReadyRoundTripsAndStaysSecretFree() throws {
        let printer = FarmSnapshotFixtures.printerWithSecrets()
        let namespace = FarmSnapshotFixtures.namespace()
        let envelope = FarmSnapshotEnvelope(
            namespace: namespace,
            printers: [printer],
            pendingReadyPrinterIDs: [printer.id],
            lastUpdatedAtMillis: 1
        )
        let data = try FarmSnapshotEnvelope.makeEncoder().encode(envelope)
        let decoded = try FarmSnapshotEnvelope.makeDecoder().decode(FarmSnapshotEnvelope.self, from: data)
        XCTAssertEqual(decoded, envelope)
        XCTAssertTrue(decoded.payload[0].isPendingReady)
        // isPendingReady is non-secret; no secret bytes/keys leak.
        let text = String(decoding: data, as: UTF8.self)
        for sentinel in FarmSnapshotFixtures.secretSentinels {
            XCTAssertFalse(text.contains(sentinel))
        }
    }
}
