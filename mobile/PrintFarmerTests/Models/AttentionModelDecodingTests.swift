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

    // MARK: - AttentionTimestampCodec / occurrence fingerprint round-trip
    //
    // These tests pin the canonical fractional-second contract that
    // ``AttentionOccurrenceFingerprint`` relies on. Because the fingerprint
    // is what correlates an item across pagination, media loads, and
    // pending actions, a lossy encode/decode cycle would silently break
    // authority correlation for any item whose occurredAt carries a
    // fractional component.

    /// Round-tripping an item that carries a fractional-second `occurredAt`
    /// through encode → decode must preserve the ``AttentionOccurrenceFingerprint``.
    /// This is the property Hicks blocker #2 requires: without it, a
    /// realtime `attentionchanged` invalidation whose refetched payload
    /// re-encodes through the app's own JSON coder would fall out of
    /// authority with any pending action.
    func testFractionalOccurredAtRoundTripPreservesFingerprint() throws {
        let json = attentionItemJSON(occurredAt: "2026-06-01T12:00:00.123456789Z")
        let original = try decoder.decode(AttentionItem.self, from: json)
        let originalFingerprint = AttentionOccurrenceFingerprint(item: original)

        let encoder = JSONEncoder()
        // Deliberately use the app's default `.iso8601` encoder strategy —
        // the codec must survive even when the surrounding encoder would
        // otherwise truncate. This is the exact configuration `APIClient`
        // uses today (see APIClient.init).
        encoder.dateEncodingStrategy = .iso8601
        let encoded = try encoder.encode(original)

        let object = try XCTUnwrap(
            JSONSerialization.jsonObject(with: encoded) as? [String: Any]
        )
        let emittedOccurredAt = try XCTUnwrap(object["occurredAt"] as? String)
        XCTAssertEqual(
            emittedOccurredAt,
            "2026-06-01T12:00:00.123456789Z"
        )

        let roundTripped = try decoder.decode(AttentionItem.self, from: encoded)
        let roundTrippedFingerprint = AttentionOccurrenceFingerprint(item: roundTripped)
        XCTAssertEqual(
            originalFingerprint,
            roundTrippedFingerprint,
            "Fingerprint must survive encode/decode of a fractional-second occurredAt."
        )
    }

    /// Two items with the same id/printer/job/toolhead but distinct
    /// fractional-second `occurredAt` values within the same wall-clock
    /// second must produce distinct fingerprints. Truncating the
    /// fingerprint to whole seconds would collapse them and cause a fresh
    /// occurrence to inherit the prior occurrence's action state — the
    /// exact hazard Hicks blocker #2 flags.
    func testDistinctFractionalOccurrencesWithinOneSecondHaveDistinctFingerprints() throws {
        let earlierJson = attentionItemJSON(occurredAt: "2026-06-01T12:00:00.000000100Z")
        let laterJson = attentionItemJSON(occurredAt: "2026-06-01T12:00:00.000000200Z")

        let earlier = try decoder.decode(AttentionItem.self, from: earlierJson)
        let later = try decoder.decode(AttentionItem.self, from: laterJson)

        XCTAssertEqual(earlier.id, later.id)
        XCTAssertEqual(earlier.printerId, later.printerId)
        XCTAssertEqual(earlier.jobId, later.jobId)
        XCTAssertEqual(earlier.toolheadIndex, later.toolheadIndex)
        let earlierFingerprint = AttentionOccurrenceFingerprint(item: earlier)
        let laterFingerprint = AttentionOccurrenceFingerprint(item: later)
        XCTAssertNotEqual(
            earlierFingerprint,
            laterFingerprint,
            "Distinct fractional occurredAt values within one second must produce distinct fingerprints."
        )
        // The exact pair preserves every fractional digit — same whole
        // second, delta of exactly 100 ns on the nanosecond field.
        XCTAssertEqual(
            earlierFingerprint.occurredAt.epochSeconds,
            laterFingerprint.occurredAt.epochSeconds
        )
        XCTAssertEqual(
            Int64(laterFingerprint.occurredAt.nanosecond)
                - Int64(earlierFingerprint.occurredAt.nanosecond),
            100
        )
    }

    /// Legacy whole-second timestamps must continue to decode and
    /// round-trip byte-identically. This guards the existing wire contract
    /// against the new codec accidentally forcing a fractional block onto
    /// every emitted timestamp.
    func testWholeSecondOccurredAtRoundTripsWithoutFractionalBlock() throws {
        let json = attentionItemJSON(occurredAt: "2026-06-01T12:00:00Z")
        let original = try decoder.decode(AttentionItem.self, from: json)
        let originalFingerprint = AttentionOccurrenceFingerprint(item: original)

        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        let encoded = try encoder.encode(original)

        let object = try XCTUnwrap(
            JSONSerialization.jsonObject(with: encoded) as? [String: Any]
        )
        let emittedOccurredAt = try XCTUnwrap(object["occurredAt"] as? String)
        XCTAssertEqual(emittedOccurredAt, "2026-06-01T12:00:00Z",
            "Whole-second occurredAt must emit no fractional block for wire byte-parity with the legacy encoder.")

        let roundTripped = try decoder.decode(AttentionItem.self, from: encoded)
        XCTAssertEqual(
            originalFingerprint,
            AttentionOccurrenceFingerprint(item: roundTripped),
            "Whole-second occurredAt round-trip must preserve the fingerprint."
        )
    }

    // MARK: - 100 ns tick precision (.NET DateTime contract)
    //
    // Backend `DateTime.Ticks` are 100 ns. Two wire strings that differ
    // by exactly one tick MUST remain distinguishable through the exact
    // pair — this is what the pre-780 lossy Int64-total-nanoseconds
    // scalar could not guarantee for far-from-epoch instants.

    /// Two 7-digit fractional wire strings differing by 1 tick (100 ns)
    /// at the same whole second must produce distinct fingerprints and
    /// exactly a 100-ns delta on the pair's `nanosecond` field.
    func testSevenDigitFractionalOneTickApartRemainsDistinct() throws {
        let earlier = try decoder.decode(
            AttentionItem.self,
            from: attentionItemJSON(occurredAt: "2026-06-01T12:00:00.1234567Z")
        )
        let later = try decoder.decode(
            AttentionItem.self,
            from: attentionItemJSON(occurredAt: "2026-06-01T12:00:00.1234568Z")
        )
        let earlierFp = AttentionOccurrenceFingerprint(item: earlier)
        let laterFp = AttentionOccurrenceFingerprint(item: later)
        XCTAssertNotEqual(earlierFp, laterFp,
            "A one-tick difference at the .NET 100-ns tick precision must not collapse to one fingerprint.")
        XCTAssertEqual(
            earlierFp.occurredAt.epochSeconds,
            laterFp.occurredAt.epochSeconds
        )
        XCTAssertEqual(earlierFp.occurredAt.nanosecond, 123_456_700)
        XCTAssertEqual(laterFp.occurredAt.nanosecond, 123_456_800)
    }

    // MARK: - Decode → encode string canonical stability

    /// A fractional wire string with 7 digits must survive a decode →
    /// encode → decode cycle byte-identically (with the trailing-zero
    /// canonicalisation that preserves the numerical value) and, after
    /// the same round-trip, produce the exact same fingerprint. This is
    /// the strongest wire-stability guarantee the codec makes.
    func testFractionalDecodeEncodeStringStability() throws {
        for wire in [
            "2026-06-01T12:00:00.1Z",
            "2026-06-01T12:00:00.12Z",
            "2026-06-01T12:00:00.123456789Z",
        ] {
            let original = try decoder.decode(
                AttentionItem.self,
                from: attentionItemJSON(occurredAt: wire)
            )
            let originalFp = AttentionOccurrenceFingerprint(item: original)

            let encoder = JSONEncoder()
            encoder.dateEncodingStrategy = .iso8601
            let encoded = try encoder.encode(original)
            let object = try XCTUnwrap(
                JSONSerialization.jsonObject(with: encoded) as? [String: Any]
            )
            let emittedWire = try XCTUnwrap(object["occurredAt"] as? String)
            XCTAssertEqual(emittedWire, wire,
                "Fractional wire string must survive decode → encode byte-identically for input \(wire).")

            let roundTripped = try decoder.decode(AttentionItem.self, from: encoded)
            XCTAssertEqual(
                originalFp,
                AttentionOccurrenceFingerprint(item: roundTripped),
                "Fingerprint must survive decode → encode → decode for input \(wire)."
            )
        }
    }

    /// Trailing-zero canonicalisation: the codec keeps the numerical
    /// value but trims trailing zeros on emit so `.100000000Z` becomes
    /// `.1Z`. Fingerprint identity is preserved.
    func testFractionalTrailingZerosCanonicaliseOnEncode() throws {
        let original = try decoder.decode(
            AttentionItem.self,
            from: attentionItemJSON(occurredAt: "2026-06-01T12:00:00.100000000Z")
        )
        let originalFp = AttentionOccurrenceFingerprint(item: original)

        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        let encoded = try encoder.encode(original)
        let object = try XCTUnwrap(
            JSONSerialization.jsonObject(with: encoded) as? [String: Any]
        )
        XCTAssertEqual(object["occurredAt"] as? String, "2026-06-01T12:00:00.1Z",
            "Trailing zeros on a fractional block must be trimmed to the shortest equivalent form.")

        let roundTripped = try decoder.decode(AttentionItem.self, from: encoded)
        XCTAssertEqual(
            originalFp,
            AttentionOccurrenceFingerprint(item: roundTripped),
            "Canonical trailing-zero trim must not change the fingerprint."
        )
    }

    // MARK: - Year boundary safety (0001, 9999)

    /// The backend .NET `DateTime` contract lower bound must decode,
    /// fingerprint, and encode without trapping — even though its
    /// epoch-seconds value is deeply negative and Foundation
    /// `ISO8601DateFormatter` is unreliable at this end of the range.
    func testYearOneOccurredAtDecodesFingerprintsAndEncodesWithoutTrap() throws {
        let wire = "0001-01-01T00:00:00Z"
        let item = try decoder.decode(
            AttentionItem.self,
            from: attentionItemJSON(occurredAt: wire)
        )
        XCTAssertEqual(item.occurredAtExact.epochSeconds, -62_135_596_800)
        XCTAssertEqual(item.occurredAtExact.nanosecond, 0)

        // Fingerprint construction must not trap on the deeply-negative
        // epoch-seconds value.
        let fp = AttentionOccurrenceFingerprint(item: item)
        XCTAssertEqual(fp.occurredAt, item.occurredAtExact)

        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        let encoded = try encoder.encode(item)
        let object = try XCTUnwrap(
            JSONSerialization.jsonObject(with: encoded) as? [String: Any]
        )
        XCTAssertEqual(object["occurredAt"] as? String, wire,
            "Year 0001 must round-trip byte-identically.")
    }

    /// Upper .NET `DateTime` boundary. Fractional block preserved to the
    /// 100 ns tick (.9999999) and encoded back with trailing zeros
    /// trimmed to `.9999999`.
    func testYearNineThousandNineHundredNinetyNineOccurredAtRoundTrips() throws {
        let wire = "9999-12-31T23:59:59.9999999Z"
        let item = try decoder.decode(
            AttentionItem.self,
            from: attentionItemJSON(occurredAt: wire)
        )
        // Sanity — the exact pair matches the wire, no overflow.
        XCTAssertEqual(item.occurredAtExact.epochSeconds, 253_402_300_799)
        XCTAssertEqual(item.occurredAtExact.nanosecond, 999_999_900)

        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        let encoded = try encoder.encode(item)
        let object = try XCTUnwrap(
            JSONSerialization.jsonObject(with: encoded) as? [String: Any]
        )
        XCTAssertEqual(object["occurredAt"] as? String, wire,
            "Year 9999 with .NET-tick fractional block must round-trip byte-identically.")

        let roundTripped = try decoder.decode(AttentionItem.self, from: encoded)
        XCTAssertEqual(
            AttentionOccurrenceFingerprint(item: item),
            AttentionOccurrenceFingerprint(item: roundTripped)
        )
    }

    // MARK: - Pre-epoch and offset equivalence

    /// Pre-epoch instants must decode with negative epoch seconds and
    /// zero nanosecond, and round-trip cleanly. This proves the codec
    /// does not depend on Foundation ranges that assume epoch-forward
    /// dates.
    func testPreEpochOccurredAtDecodesAndRoundTrips() throws {
        let wire = "1969-12-31T23:59:59Z"
        let item = try decoder.decode(
            AttentionItem.self,
            from: attentionItemJSON(occurredAt: wire)
        )
        XCTAssertEqual(item.occurredAtExact.epochSeconds, -1)
        XCTAssertEqual(item.occurredAtExact.nanosecond, 0)

        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        let encoded = try encoder.encode(item)
        let object = try XCTUnwrap(
            JSONSerialization.jsonObject(with: encoded) as? [String: Any]
        )
        XCTAssertEqual(object["occurredAt"] as? String, wire)
    }

    /// Two wire strings describing the SAME instant expressed in
    /// different offsets must produce equal exact pairs and therefore
    /// equal fingerprints — otherwise pagination correlation could
    /// silently break when a server flips its emit offset.
    func testOffsetEquivalentInstantsCanonicaliseEqual() throws {
        let utcJson = attentionItemJSON(occurredAt: "2026-06-01T12:00:00Z")
        let offsetJson = attentionItemJSON(occurredAt: "2026-06-01T07:00:00-05:00")
        let plusOffsetJson = attentionItemJSON(occurredAt: "2026-06-01T17:00:00+05:00")

        let utc = try decoder.decode(AttentionItem.self, from: utcJson)
        let offset = try decoder.decode(AttentionItem.self, from: offsetJson)
        let plusOffset = try decoder.decode(AttentionItem.self, from: plusOffsetJson)

        XCTAssertEqual(utc.occurredAtExact, offset.occurredAtExact,
            "Negative-offset instant must canonicalise to the same UTC epoch-seconds pair as its Z equivalent.")
        XCTAssertEqual(utc.occurredAtExact, plusOffset.occurredAtExact,
            "Positive-offset instant must canonicalise to the same UTC epoch-seconds pair as its Z equivalent.")
        XCTAssertEqual(
            AttentionOccurrenceFingerprint(item: utc),
            AttentionOccurrenceFingerprint(item: offset)
        )
        XCTAssertEqual(
            AttentionOccurrenceFingerprint(item: utc),
            AttentionOccurrenceFingerprint(item: plusOffset)
        )
    }

    // MARK: - Reject invalid / out-of-range input

    /// Ten or more fractional digits exceed what the .NET wire contract
    /// can emit. Silently truncating them would lose real precision —
    /// reject instead with a typed decoding error.
    func testMoreThanNineFractionalDigitsIsRejected() {
        let wire = "2026-06-01T12:00:00.1234567890Z" // 10 digits
        XCTAssertThrowsError(
            try decoder.decode(
                AttentionItem.self,
                from: attentionItemJSON(occurredAt: wire)
            )
        )
    }

    /// A calendar-invalid date (Feb 30) must be rejected — proves the
    /// codec is validating with `daysInMonth` and not just accepting any
    /// three-digit day.
    func testCalendarInvalidDateIsRejected() {
        let wire = "2026-02-30T12:00:00Z"
        XCTAssertThrowsError(
            try decoder.decode(
                AttentionItem.self,
                from: attentionItemJSON(occurredAt: wire)
            )
        )
    }

    /// A year outside the backend contract (0000 or 10000) must be
    /// rejected — the contract is 0001..9999 inclusive.
    func testYearOutsideContractRangeIsRejected() {
        for wire in ["0000-01-01T00:00:00Z", "10000-01-01T00:00:00Z"] {
            XCTAssertThrowsError(
                try decoder.decode(
                    AttentionItem.self,
                    from: attentionItemJSON(occurredAt: wire)
                ),
                "Year outside 0001-9999 must not decode: \(wire)"
            )
        }
    }

    /// An invalid month (13) must be rejected.
    func testInvalidMonthIsRejected() {
        XCTAssertThrowsError(
            try decoder.decode(
                AttentionItem.self,
                from: attentionItemJSON(occurredAt: "2026-13-01T12:00:00Z")
            )
        )
    }

    /// A missing timezone suffix must be rejected — the wire contract
    /// always carries one, and accepting a naked local-looking time
    /// would silently pick a caller-implied zone.
    func testMissingTimezoneSuffixIsRejected() {
        XCTAssertThrowsError(
            try decoder.decode(
                AttentionItem.self,
                from: attentionItemJSON(occurredAt: "2026-06-01T12:00:00")
            )
        )
    }

    /// Empty fractional block (trailing dot with no digits) must be
    /// rejected.
    func testEmptyFractionalBlockIsRejected() {
        XCTAssertThrowsError(
            try decoder.decode(
                AttentionItem.self,
                from: attentionItemJSON(occurredAt: "2026-06-01T12:00:00.Z")
            )
        )
    }

    /// A non-digit character in the fractional block must be rejected.
    func testFractionalBlockNonDigitIsRejected() {
        XCTAssertThrowsError(
            try decoder.decode(
                AttentionItem.self,
                from: attentionItemJSON(occurredAt: "2026-06-01T12:00:00.12x4Z")
            )
        )
    }

    /// Malformed structural input (bad separators) must be rejected.
    func testMalformedTimestampIsRejected() {
        XCTAssertThrowsError(
            try decoder.decode(
                AttentionItem.self,
                from: attentionItemJSON(occurredAt: "not-a-timestamp")
            )
        )
        XCTAssertThrowsError(
            try decoder.decode(
                AttentionItem.self,
                from: attentionItemJSON(occurredAt: "2026-06-01 12:00:00Z")
            )
        )
    }

    /// Offsets outside the ISO-8601 civil bound (14 hours) must be
    /// rejected.
    func testOffsetOutsideRangeIsRejected() {
        XCTAssertThrowsError(
            try decoder.decode(
                AttentionItem.self,
                from: attentionItemJSON(occurredAt: "2026-06-01T12:00:00+15:00")
            )
        )
    }

    // MARK: - Normalized-UTC boundary (offset-shifted) rejection

    /// The local `parts.year` bound is not sufficient — a valid-looking
    /// local timestamp combined with a ±14 h offset can shift the
    /// normalized UTC instant into year 0000 or year 10000, neither of
    /// which is representable by the backend .NET `DateTime` contract
    /// or by our own encoder. These inputs must be rejected before we
    /// hand back an exact pair the encoder cannot later round-trip.
    func testOffsetShiftedInstantOutsideNormalizedUtcRangeIsRejected() {
        // Lower edge: 0001-01-01T00:00:00 with a +14:00 offset means
        // UTC = local − 14 h = 0000-12-31T10:00:00Z (year 0000).
        // Upper edge: 9999-12-31T23:59:59.9999999 with a −14:00 offset
        // means UTC = local + 14 h = 10000-01-01T13:59:59.9999999Z
        // (year 10000).
        for wire in [
            "0001-01-01T00:00:00+14:00",
            "9999-12-31T23:59:59.9999999-14:00",
        ] {
            XCTAssertThrowsError(
                try decoder.decode(
                    AttentionItem.self,
                    from: attentionItemJSON(occurredAt: wire)
                ),
                "Offset-shifted UTC year outside 0001-9999 must not decode: \(wire)"
            )
        }
    }

    /// Valid ±14:00 offsets whose *normalized* UTC instant stays within
    /// the contract must still decode and canonicalise correctly. This
    /// pairs with the rejection test above so we don't over-reject.
    func testMaximumMagnitudeOffsetsWithInRangeUtcAreAccepted() throws {
        // Local 2026-06-01T14:00:00+14:00 → UTC 2026-06-01T00:00:00Z.
        let plus = try decoder.decode(
            AttentionItem.self,
            from: attentionItemJSON(occurredAt: "2026-06-01T14:00:00+14:00")
        )
        XCTAssertEqual(plus.occurredAtExact,
                       AttentionTimestampCodec.decode("2026-06-01T00:00:00Z"))

        // Local 2026-06-01T00:00:00-14:00 → UTC 2026-06-01T14:00:00Z.
        let minus = try decoder.decode(
            AttentionItem.self,
            from: attentionItemJSON(occurredAt: "2026-06-01T00:00:00-14:00")
        )
        XCTAssertEqual(minus.occurredAtExact,
                       AttentionTimestampCodec.decode("2026-06-01T14:00:00Z"))
    }

    /// Offsets whose *hour* is 14 but whose minute is nonzero (e.g.
    /// `+14:01`, `-14:59`) are outside the ISO-8601 civil bound and
    /// must be rejected. Combined with the +15:00 rejection above this
    /// pins the maximum magnitude at exactly ±14:00.
    func testOffsetsBeyondFourteenHourMaximumAreRejected() {
        for wire in [
            "2026-06-01T12:00:00+14:01",
            "2026-06-01T12:00:00+14:59",
            "2026-06-01T12:00:00-14:01",
            "2026-06-01T12:00:00-14:59",
        ] {
            XCTAssertThrowsError(
                try decoder.decode(
                    AttentionItem.self,
                    from: attentionItemJSON(occurredAt: wire)
                ),
                "Offset with hour=14 and nonzero minute must not decode: \(wire)"
            )
        }
    }

    /// Round-trip a valid boundary-adjacent offset instant: decode →
    /// encode → decode must produce the same canonical UTC pair. This
    /// proves that once the normalized-UTC bounds check passes, the
    /// encoder can always emit a byte-identical canonical form the
    /// decoder accepts back.
    func testBoundaryAdjacentOffsetInstantsRoundTripExact() throws {
        // Exact lower UTC boundary reached via +14:00 offset:
        //   0001-01-01T14:00:00+14:00 → 0001-01-01T00:00:00Z.
        let lowerLocal = "0001-01-01T14:00:00+14:00"
        let lowerCanonical = "0001-01-01T00:00:00Z"
        let lowerDecoded = try XCTUnwrap(AttentionTimestampCodec.decode(lowerLocal))
        XCTAssertEqual(AttentionTimestampCodec.encode(lowerDecoded), lowerCanonical)
        let lowerRoundTrip = try XCTUnwrap(
            AttentionTimestampCodec.decode(AttentionTimestampCodec.encode(lowerDecoded))
        )
        XCTAssertEqual(lowerDecoded, lowerRoundTrip)

        // Upper representable UTC instant reached via +14:00 offset:
        //   9999-12-31T23:59:59.9999999+14:00 → 9999-12-31T09:59:59.9999999Z.
        let upperLocal = "9999-12-31T23:59:59.9999999+14:00"
        let upperCanonical = "9999-12-31T09:59:59.9999999Z"
        let upperDecoded = try XCTUnwrap(AttentionTimestampCodec.decode(upperLocal))
        XCTAssertEqual(AttentionTimestampCodec.encode(upperDecoded), upperCanonical)
        let upperRoundTrip = try XCTUnwrap(
            AttentionTimestampCodec.decode(AttentionTimestampCodec.encode(upperDecoded))
        )
        XCTAssertEqual(upperDecoded, upperRoundTrip)
    }

    // MARK: - End-exclusive year-10000 clamp

    /// A programmatic caller supplying a `Date` whose interval carries
    /// a fractional component *at the top of the contract range* must
    /// keep that fraction. The previous implementation clamped to
    /// `Double(253_402_300_799)`, so `253_402_300_799.5` collapsed to
    /// `253_402_300_799.0` and lost the 0.5 s. The upper bound is
    /// end-exclusive at year 10000, so a value strictly below
    /// `253_402_300_800` must survive intact.
    func testProgrammaticDateFractionAtUpperBoundIsPreserved() {
        let date = Date(timeIntervalSince1970: 253_402_300_799.5)
        let ts = AttentionExactTimestamp.fromProgrammaticDate(date)
        XCTAssertEqual(ts.epochSeconds, 253_402_300_799,
            "Fractional upper-bound value must land on the max representable second, not year 10000.")
        XCTAssertEqual(ts.nanosecond, 500_000_000,
            "The 0.5 s fractional component must be preserved through the programmatic Date path.")
    }

    /// The presentation `Date` for the maximum representable pair must
    /// stay strictly below the year-10000 boundary. Binary-float
    /// rounding at magnitude ~2e11 has an ULP much larger than 1 ns, so
    /// naively adding `999_999_999 / 1e9` to `Double(253_402_300_799)`
    /// can round up to `253_402_300_800.0` — a Date value inside year
    /// 10000. The pair authority remains the exact `(epoch, ns)` pair;
    /// this test locks the presentation clamp.
    func testMaxWireTimestampPresentationDoesNotCrossYearTenThousandBoundary() throws {
        let wire = "9999-12-31T23:59:59.999999999Z"
        let ts = try XCTUnwrap(AttentionTimestampCodec.decode(wire))
        XCTAssertEqual(ts.epochSeconds, 253_402_300_799)
        XCTAssertEqual(ts.nanosecond, 999_999_999)
        let presented = ts.presentationDate.timeIntervalSince1970
        XCTAssertLessThan(presented, 253_402_300_800,
            "Presentation Date must never round up to the year-10000 boundary.")
        // Sanity: the exact-pair authority is still 999_999_999 ns —
        // the Double snap only affects `Date` presentation, not identity.
        let expected = try XCTUnwrap(AttentionExactTimestamp(
            epochSeconds: 253_402_300_799,
            nanosecond: 999_999_999
        ))
        XCTAssertEqual(ts, expected)
    }

    // MARK: - Programmatic Date initializer non-trapping

    /// The programmatic `Date` initializer path must not trap for any of
    /// the extreme dates a client-side caller could ever produce: the
    /// distant future, the distant past, exact epoch, and dates that
    /// would round the fractional nanoseconds up to 1e9. Instead of
    /// trapping, `AttentionExactTimestamp.fromProgrammaticDate` clamps
    /// into the backend contract window.
    func testProgrammaticDateInitializerNonTrappingForExtremeDates() {
        let cases: [Date] = [
            Date(timeIntervalSince1970: 0),
            Date.distantFuture,
            Date.distantPast,
            Date(timeIntervalSince1970: -1e30),
            Date(timeIntervalSince1970: 1e30),
            // Ends of the contract range.
            Date(timeIntervalSince1970: 253_402_300_799),
            Date(timeIntervalSince1970: -62_135_596_800),
        ]
        for date in cases {
            let ts = AttentionExactTimestamp.fromProgrammaticDate(date)
            // Never trap: nanosecond is always < 1e9.
            XCTAssertLessThan(ts.nanosecond, 1_000_000_000)
            // Always clamped into the contract range.
            XCTAssertGreaterThanOrEqual(ts.epochSeconds, -62_135_596_800)
            XCTAssertLessThanOrEqual(ts.epochSeconds, 253_402_300_799)
            // Build the item through the memberwise init path — it must
            // not trap and the derived `occurredAt` `Date` must be finite.
            let item = AttentionItem(
                id: "programmatic:test",
                kind: .failure,
                severity: .info,
                printerId: UUID(),
                printerName: "test",
                title: "t",
                detail: "d",
                occurredAt: date,
                actions: []
            )
            XCTAssertEqual(item.occurredAtExact, ts)
            XCTAssertTrue(item.occurredAt.timeIntervalSince1970.isFinite)
        }
    }

    /// Helper — builds a canonical single-item payload with the caller's
    /// `occurredAt` wire string, holding every other fingerprint dimension
    /// (id/printer/job/toolhead) constant so the tests can isolate the
    /// timestamp variable.
    private func attentionItemJSON(occurredAt: String) -> Data {
        """
        {
          "id": "failure:11111111-1111-1111-1111-111111111111",
          "kind": "failure",
          "severity": "critical",
          "printerId": "22222222-2222-2222-2222-222222222222",
          "printerName": "Voron 2.4",
          "title": "Print failed",
          "detail": "First-layer adhesion lost",
          "occurredAt": "\(occurredAt)",
          "actions": [],
          "toolheadIndex": 0,
          "jobId": "33333333-3333-3333-3333-333333333333",
          "allowFreshOccurrenceBypass": true
        }
        """.data(using: .utf8)!
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
