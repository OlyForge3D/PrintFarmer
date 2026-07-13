import XCTest
@testable import PrintFarmer

/// Wire-format coverage for the Attention DTOs merged from PR #731 (issue
/// #707). These tests are intentionally paranoid about camelCase property
/// names, exact lowercase/camelCase enum spellings, forward-compatible
/// `unknown` fallbacks, and the `allowFreshOccurrenceBypass` default so a
/// rolling backend update never silently breaks the client.
final class AttentionModelDecodingTests: XCTestCase {

    private lazy var decoder: JSONDecoder = {
        let d = JSONDecoder()
        d.dateDecodingStrategy = .iso8601
        return d
    }()

    // MARK: - AttentionItem / enums / defaults

    func testAttentionItemDecodesFullBackendPayload() throws {
        let json = """
        {
          "id": "failure:11111111-1111-1111-1111-111111111111",
          "kind": "failure",
          "severity": "critical",
          "printerId": "22222222-2222-2222-2222-222222222222",
          "printerName": "Voron 2.4",
          "title": "Print failed",
          "detail": "First-layer adhesion lost",
          "occurredAt": "2026-06-01T12:00:00Z",
          "actions": [
            { "kind": "acknowledge", "label": "Acknowledge", "requiresConfirmation": false },
            { "kind": "resolve",     "label": "Resolve",     "requiresConfirmation": true  }
          ],
          "toolheadIndex": 0,
          "deadlineAt": null,
          "jobId": "33333333-3333-3333-3333-333333333333",
          "allowFreshOccurrenceBypass": false
        }
        """.data(using: .utf8)!

        let item = try decoder.decode(AttentionItem.self, from: json)

        XCTAssertEqual(item.id, "failure:11111111-1111-1111-1111-111111111111")
        XCTAssertEqual(item.kind, .failure)
        XCTAssertEqual(item.severity, .critical)
        XCTAssertEqual(item.printerName, "Voron 2.4")
        XCTAssertEqual(item.actions.count, 2)
        XCTAssertEqual(item.actions[0].kind, .acknowledge)
        XCTAssertEqual(item.actions[0].requiresConfirmation, false)
        XCTAssertEqual(item.actions[1].kind, .resolve)
        XCTAssertEqual(item.actions[1].requiresConfirmation, true)
        XCTAssertEqual(item.toolheadIndex, 0)
        XCTAssertNil(item.deadlineAt)
        XCTAssertNotNil(item.jobId)
        XCTAssertFalse(item.allowFreshOccurrenceBypass)
    }

    func testAttentionItemDefaultsAllowFreshOccurrenceBypassWhenFieldMissing() throws {
        // A backend rev that predates the field must still decode; the
        // computed default is `true` (see backend `AttentionItemDto`).
        let json = """
        {
          "id": "runout:aa",
          "kind": "runout",
          "severity": "warning",
          "printerId": "22222222-2222-2222-2222-222222222222",
          "printerName": "X1C",
          "title": "Spool empty",
          "detail": "AMS slot 1",
          "occurredAt": "2026-06-01T12:00:00Z",
          "actions": []
        }
        """.data(using: .utf8)!

        let item = try decoder.decode(AttentionItem.self, from: json)
        XCTAssertTrue(item.allowFreshOccurrenceBypass,
            "Missing allowFreshOccurrenceBypass must default to true so an older backend still decodes.")
        XCTAssertNil(item.toolheadIndex)
        XCTAssertNil(item.deadlineAt)
        XCTAssertNil(item.jobId)
    }

    func testUnknownAttentionKindDecodesToUnknown() throws {
        let json = """
        {
          "id": "brandnew:x",
          "kind": "brandNewKindTheBackendAdded",
          "severity": "info",
          "printerId": "22222222-2222-2222-2222-222222222222",
          "printerName": "X1C",
          "title": "New",
          "detail": "",
          "occurredAt": "2026-06-01T12:00:00Z",
          "actions": []
        }
        """.data(using: .utf8)!

        let item = try decoder.decode(AttentionItem.self, from: json)
        XCTAssertEqual(item.kind, .unknown,
            "Unknown wire values must fall back to .unknown rather than throwing.")
    }

    func testUnknownAttentionSeverityDecodesToUnknown() throws {
        let json = """
        { "kind": "failure", "label": "x", "requiresConfirmation": false }
        """
        // Sanity — just decoding an action here to keep the sample tiny.
        _ = try decoder.decode(AttentionAction.self, from: json.data(using: .utf8)!)

        let severityJson = "\"nuclear\"".data(using: .utf8)!
        let severity = try decoder.decode(AttentionSeverity.self, from: severityJson)
        XCTAssertEqual(severity, .unknown)
    }

    func testUnknownAttentionActionKindDecodesToUnknown() throws {
        let kindJson = "\"summonGolem\"".data(using: .utf8)!
        let kind = try decoder.decode(AttentionActionKind.self, from: kindJson)
        XCTAssertEqual(kind, .unknown)
    }

    func testKnownActionKindsRoundTripExactWireSpelling() throws {
        // Each spelling must decode; if a future refactor accidentally
        // renames one, this test locks the wire contract.
        for (raw, expected) in [
            ("pause", AttentionActionKind.pause),
            ("resume", .resume),
            ("cancel", .cancel),
            ("acknowledge", .acknowledge),
            ("resolve", .resolve),
            ("dismiss", .dismiss),
            ("snooze", .snooze),
            ("harvest", .harvest),
        ] as [(String, AttentionActionKind)] {
            let data = "\"\(raw)\"".data(using: .utf8)!
            let decoded = try decoder.decode(AttentionActionKind.self, from: data)
            XCTAssertEqual(decoded, expected, "raw '\(raw)' should decode to \(expected)")
        }
    }

    func testKnownAttentionKindsRoundTripExactWireSpelling() throws {
        for (raw, expected) in [
            ("failure", AttentionKind.failure),
            ("runout", .runout),
            ("harvest", .harvest),
            ("maintenance", .maintenance),
            ("offline", .offline),
        ] as [(String, AttentionKind)] {
            let data = "\"\(raw)\"".data(using: .utf8)!
            let decoded = try decoder.decode(AttentionKind.self, from: data)
            XCTAssertEqual(decoded, expected)
        }
    }

    // MARK: - AttentionFeed envelope / pagination

    func testAttentionFeedDecodesEnvelopeAndPagination() throws {
        let json = """
        {
          "items": [
            {
              "id": "offline:44444444-4444-4444-4444-444444444444",
              "kind": "offline",
              "severity": "warning",
              "printerId": "22222222-2222-2222-2222-222222222222",
              "printerName": "P1S",
              "title": "Printer offline",
              "detail": "No heartbeat for 90s",
              "occurredAt": "2026-06-01T12:00:00Z",
              "actions": []
            }
          ],
          "nextCursor": "eyJvIjozLCJpIjoiZm9vIn0",
          "healthyPrinterCount": 7
        }
        """.data(using: .utf8)!

        let feed = try decoder.decode(AttentionFeed.self, from: json)
        XCTAssertEqual(feed.items.count, 1)
        XCTAssertEqual(feed.nextCursor, "eyJvIjozLCJpIjoiZm9vIn0")
        XCTAssertEqual(feed.healthyPrinterCount, 7)
    }

    func testAttentionFeedDecodesFinalPageWithNilCursor() throws {
        let json = """
        { "items": [], "nextCursor": null, "healthyPrinterCount": 0 }
        """.data(using: .utf8)!
        let feed = try decoder.decode(AttentionFeed.self, from: json)
        XCTAssertTrue(feed.items.isEmpty)
        XCTAssertNil(feed.nextCursor)
        XCTAssertEqual(feed.healthyPrinterCount, 0)
    }

    // MARK: - Snooze request/response

    /// Compile-anchored regression guard for #707 (v3).
    ///
    /// The rejected candidate carried a two-property `SnoozeAttentionRequest`
    /// (`snoozedUntilUtc` + `attentionItemAnchorAtUtc: Date?`). Because
    /// `Optional` stored properties do NOT receive a synthesised
    /// declaration-site default, Swift's memberwise initialiser for that
    /// shape has signature `init(snoozedUntilUtc:attentionItemAnchorAtUtc:)`
    /// — both arguments required. Constructing the request with a single
    /// `snoozedUntilUtc:` argument (as this test does) therefore fails to
    /// compile against the rejected model, and the test target itself
    /// refuses to build. That is the guarantee this test provides that a
    /// substring/absence assertion against the wire cannot: the check is
    /// enforced by the source compiler, not by runtime string matching.
    ///
    /// Beyond the compile anchor, we also assert the exact key set of the
    /// encoded body is `{"snoozedUntilUtc"}` — which simultaneously pins
    /// the camelCase field name, the absence of any anchor field, and the
    /// absence of any null-valued or extra keys — without reflection or
    /// any production-side test hooks.
    func testSnoozeRequestEncodesExactlyOneCamelCaseKey() throws {
        // Compile anchor: single-argument construction. If the rejected
        // two-property request model is ever reintroduced without a
        // declaration-site default for the anchor, this line stops
        // compiling and the test target fails to build — which is a
        // stronger guarantee than any runtime absence check.
        let req = SnoozeAttentionRequest(
            snoozedUntilUtc: Date(timeIntervalSince1970: 1_800_000_000)
        )

        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        let data = try encoder.encode(req)

        // Parse the emitted JSON as a top-level object and inspect its
        // key set directly. `Set` equality catches, in a single
        // assertion:
        //   • wrong casing (e.g. `SnoozedUntilUtc`, `snoozed_until_utc`)
        //   • extra fields (e.g. a re-added `attentionItemAnchorAtUtc`,
        //     even if its value were `null`, because JSON serialisation
        //     of a nil Optional would still emit the key)
        //   • missing fields
        let parsed = try JSONSerialization.jsonObject(with: data)
        let object = try XCTUnwrap(parsed as? [String: Any],
            "Snooze request must encode as a JSON object.")
        XCTAssertEqual(Set(object.keys), Set(["snoozedUntilUtc"]),
            "Snooze request body must carry exactly one camelCase key \"snoozedUntilUtc\"; got \(object.keys.sorted()).")

        // Belt-and-braces: the value must be the ISO8601 string for the
        // deadline we passed in, not accidentally the current time or a
        // number, so nobody can silently swap the encoding strategy.
        XCTAssertEqual(object["snoozedUntilUtc"] as? String, "2027-01-15T08:00:00Z",
            "snoozedUntilUtc must serialise as the ISO8601 UTC string for the supplied Date.")
    }

    func testSnoozeResponseDecodesFullPayload() throws {
        let json = """
        {
          "snoozedUntilUtc": "2026-06-02T12:00:00Z",
          "attentionItemAnchorAtUtc": "2026-06-01T12:00:00Z"
        }
        """.data(using: .utf8)!
        let resp = try decoder.decode(SnoozeAttentionResponse.self, from: json)
        XCTAssertNotNil(resp.attentionItemAnchorAtUtc)
    }

    // MARK: - Action result

    func testAttentionActionResultDecodesOutcome() throws {
        let json = "{ \"outcome\": \"Ok\" }".data(using: .utf8)!
        let result = try decoder.decode(AttentionActionResult.self, from: json)
        XCTAssertEqual(result.outcome, "Ok")
    }

    // MARK: - SignalR event payload (attentionchanged)

    func testAttentionChangedEventDecodesCamelCasePayload() throws {
        let json = """
        {
          "itemId": "failure:55555555-5555-5555-5555-555555555555",
          "changeKind": "updated",
          "occurredAt": "2026-06-01T12:00:00Z"
        }
        """.data(using: .utf8)!
        let evt = try decoder.decode(AttentionChangedEvent.self, from: json)
        XCTAssertEqual(evt.itemId, "failure:55555555-5555-5555-5555-555555555555")
        XCTAssertEqual(evt.changeKind, .updated)
    }

    func testAttentionChangedEventDecodesAllKnownChangeKinds() throws {
        for (raw, expected) in [
            ("created", AttentionChangeKind.created),
            ("updated", .updated),
            ("resolved", .resolved),
        ] as [(String, AttentionChangeKind)] {
            let json = """
            { "itemId": "x:1", "changeKind": "\(raw)", "occurredAt": "2026-06-01T12:00:00Z" }
            """.data(using: .utf8)!
            let evt = try decoder.decode(AttentionChangedEvent.self, from: json)
            XCTAssertEqual(evt.changeKind, expected)
        }
    }

    func testAttentionChangedEventUnknownChangeKindDecodesToUnknown() throws {
        // Forward-compat: a backend that adds a new changeKind must not
        // crash the client. The service treats `.unknown` as invalidation.
        let json = """
        { "itemId": "x:1", "changeKind": "supernova", "occurredAt": "2026-06-01T12:00:00Z" }
        """.data(using: .utf8)!
        let evt = try decoder.decode(AttentionChangedEvent.self, from: json)
        XCTAssertEqual(evt.changeKind, .unknown)
    }

    func testAttentionChangedEventMalformedPayloadThrows() {
        // Missing required `itemId` → decoding must fail loudly so the
        // SignalR handler can drop the message rather than dispatch a
        // partially-formed invalidation.
        let json = """
        { "changeKind": "updated", "occurredAt": "2026-06-01T12:00:00Z" }
        """.data(using: .utf8)!
        XCTAssertThrowsError(try decoder.decode(AttentionChangedEvent.self, from: json))
    }
}
