import XCTest
@testable import PrintFarmer

/// Gate C — the envelope is a structural secret-free allow-list projection.
final class FarmSnapshotEnvelopeTests: XCTestCase {

    func testProjectionCarriesEveryNonSecretCardField() {
        let printerID = UUID()
        let locationID = UUID()
        let printer = FarmSnapshotFixtures.printerWithSecrets(id: printerID, locationID: locationID)
        let projected = FarmSnapshotPrinter(printer, isPendingReady: false)

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
            pendingReadyPrinterIDs: [],
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
        let envelope = FarmSnapshotEnvelope(namespace: namespace, payload: [], lastUpdatedAtMillis: 42)
        let data = try FarmSnapshotEnvelope.makeEncoder().encode(envelope)
        let decoded = try FarmSnapshotEnvelope.makeDecoder().decode(FarmSnapshotEnvelope.self, from: data)
        XCTAssertEqual(decoded.payload.count, 0)
        XCTAssertEqual(decoded, envelope)
    }

    func testTimestampEncodesAsInteger() throws {
        let namespace = FarmSnapshotFixtures.namespace()
        let envelope = FarmSnapshotEnvelope(namespace: namespace, payload: [], lastUpdatedAtMillis: 100_900)
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
        XCTAssertFalse(FarmSnapshotPrinter(printer, isPendingReady: false).isPendingReady)
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

    // MARK: H6 — hand-authored v1 on-disk compatibility fixtures

    /// Builds a hand-authored v1 envelope JSON whose single printer contains exactly the
    /// v1-required keys, minus any listed in `omitting`.
    private func handAuthoredV1(omitting: Set<String> = []) -> Data {
        var printer: [String: Any] = [
            "id": UUID().uuidString,
            "name": "Voron-01",
            "isOnline": true,
            "isEnabled": true,
            "inMaintenance": false,
            "obicoEnabled": false,
            "isPendingReady": true
        ]
        for key in omitting { printer.removeValue(forKey: key) }
        let envelope: [String: Any] = [
            "schemaVersion": 1,
            "namespace": ["serverID": UUID().uuidString, "userID": UUID().uuidString],
            "payload": [printer],
            "lastUpdatedAtMillis": 1000
        ]
        return try! JSONSerialization.data(withJSONObject: envelope)
    }

    func testHandAuthoredV1MissingOnlyIsPendingReadyDecodesFalse() throws {
        // An older on-disk v1 record that predates isPendingReady must decode with the
        // field defaulted to false — never rejected.
        let data = handAuthoredV1(omitting: ["isPendingReady"])
        let decoded = try FarmSnapshotEnvelope.makeDecoder().decode(FarmSnapshotEnvelope.self, from: data)
        XCTAssertEqual(decoded.payload.count, 1)
        XCTAssertFalse(decoded.payload[0].isPendingReady, "absent isPendingReady defaults to false")
        XCTAssertTrue(decoded.isSupportedSchema)
    }

    func testHandAuthoredV1MissingEachRequiredFieldFailsToDecode() {
        // Every previously-required v1 field must remain REQUIRED; a fixture missing any
        // one must fail to decode (so the store quarantines a truncated/corrupt record).
        for key in ["id", "name", "isOnline", "isEnabled", "inMaintenance", "obicoEnabled"] {
            let data = handAuthoredV1(omitting: [key])
            XCTAssertThrowsError(
                try FarmSnapshotEnvelope.makeDecoder().decode(FarmSnapshotEnvelope.self, from: data),
                "missing required key \(key) must fail to decode"
            )
        }
    }

    func testStoreQuarantinesHandAuthoredCorruptV1() async {
        // A corrupt v1 record (missing a required key) written as the live file must be
        // quarantined by the store on hydrate — never coerced.
        let root = FarmSnapshotFixtures.tempRoot()
        defer { try? FileManager.default.removeItem(at: root) }
        let authority = FarmSnapshotFixtures.makeAuthority(tombstoneDefaults: UserDefaults(suiteName: trackedSuiteName("tomb"))!)
        let store = FarmSnapshotStore(authority: authority, rootURL: root)
        let ns = FarmSnapshotFixtures.namespace()
        let session = authority.mint(namespace: ns, generation: 0)!
        _ = await store.activate(session: session)

        let live = root.appendingPathComponent("servers", isDirectory: true)
            .appendingPathComponent(ns.serverID.uuidString, isDirectory: true)
            .appendingPathComponent("\(ns.userID.uuidString).json")
        try? FileManager.default.createDirectory(at: live.deletingLastPathComponent(), withIntermediateDirectories: true)
        try? handAuthoredV1(omitting: ["isOnline"]).write(to: live)

        let result = await store.hydrateActive()
        XCTAssertEqual(result, .recovered, "corrupt v1 is quarantined (recovered), not coerced")
        XCTAssertFalse(FileManager.default.fileExists(atPath: live.path), "corrupt live file is quarantined away")
    }
}
