import Foundation

// MARK: - Attention Feed DTOs
//
// Mirrors the backend contract merged in PR #731 (issue #707):
//   - Route: GET /api/attention?cursor=&limit= → AttentionFeed
//   - Route: POST /api/attention/{id}/snooze { snoozedUntilUtc }
//   - Route: DELETE /api/attention/{id}/snooze
//   - Route: POST /api/attention/{id}/actions/{actionKind}
// Wire enums are lowercase/camelCase strings; property names are camelCase.
// See src/infra/Dtos/Attention/AttentionDtos.cs for the authoritative shapes.

// MARK: - Canonical Attention timestamp representation
//
// Attention items carry occurrence timestamps whose exact value participates
// in ``AttentionOccurrenceFingerprint`` — the identity used to correlate an
// item across pagination, media loads, and pending actions. The backend
// serialises `DateTime` via System.Text.Json at 100 ns tick precision; two
// occurrences for the same item within one wall-clock second therefore
// differ only in their fractional component and MUST remain distinguishable
// through decode → fingerprint → encode → decode.
//
// The app-wide `JSONEncoder`/`JSONDecoder` on `APIClient` uses `.iso8601`
// for encode (whole-second) and a custom decode strategy that falls back
// through fractional then plain formats. Those default strategies are
// correct for most DTOs (e.g. ``SnoozeAttentionRequest`` deliberately
// truncates to whole seconds — see its header below), but they collapse
// the fractional part on any encode round-trip, and their `Date` /
// `TimeInterval` intermediates lose sub-microsecond precision even before
// the encoder sees them — which would silently defeat the occurrence-
// identity contract if applied to ``AttentionItem``.
//
// ``AttentionExactTimestamp`` is the canonical wire-preserving pair used
// by ``AttentionItem``. It stores `(epochSeconds, nanosecond)` split
// components — never a total-nanoseconds `Int64` or any lossy `Double`
// derivative — so exact 100 ns tick equality/inequality is preserved for
// every year the backend contract can emit (0001–9999) with pre-epoch
// safety and no trapping conversions.
//
// ``AttentionTimestampCodec`` does the wire ↔ pair translation. The scope
// is deliberately narrow: only ``AttentionItem`` opts into it via its
// custom `Codable` conformance. Other Attention DTOs continue to honour
// the global date strategy on the surrounding coder.

/// An exact, wire-preserving instant used as the occurrence identity for
/// ``AttentionItem``. Storage is a signed epoch-seconds count paired with
/// an unsigned nanosecond remainder — the split form the backend .NET
/// `DateTime` contract can emit at 100 ns tick precision (up to 9
/// fractional digits) — so two wire strings that differ by a single tick
/// remain distinguishable through hash/equality.
///
/// The pair is never derived from `Date`/`TimeInterval` during decode
/// (that would collapse sub-microsecond precision for any instant far
/// from 1970). The presentation ``presentationDate`` is a *derived*
/// non-trapping view of the pair — never the other way round.
struct AttentionExactTimestamp: Hashable, Sendable {
    /// Signed seconds since 1970-01-01T00:00:00Z. Negative for pre-epoch
    /// instants; range covers years 0001–9999.
    let epochSeconds: Int64
    /// Sub-second remainder in the canonical range `0..<1_000_000_000`.
    /// Together with ``epochSeconds`` this specifies the exact instant
    /// with 1 ns granularity — strictly finer than the 100 ns tick the
    /// backend produces, so no wire precision is ever discarded.
    let nanosecond: UInt32

    /// Failable memberwise. Rejects out-of-range nanoseconds to preserve
    /// the canonical `0..<1_000_000_000` invariant that hash/equality
    /// depend on.
    init?(epochSeconds: Int64, nanosecond: UInt32) {
        guard nanosecond < AttentionTimestampCodec.nanosecondsPerSecond else {
            return nil
        }
        self.epochSeconds = epochSeconds
        self.nanosecond = nanosecond
    }

    /// Non-trapping best-effort projection of a programmatic `Date` into
    /// the exact pair. Server-decoded items never route through this —
    /// they use ``AttentionTimestampCodec/decode(_:)`` and keep the wire
    /// precision the codec preserved. This path exists for programmatic
    /// callers (fixtures, memberwise `init`) whose only input *is* a
    /// lossy `Date`.
    ///
    /// Clamps into the backend-supported UTC interval [year 0001,
    /// year 10000) — the upper edge is end-exclusive so a caller
    /// supplying a valid in-range fractional value at the top of the
    /// range keeps its fraction instead of collapsing onto the
    /// unrepresentable year-10000 second boundary.
    static func fromProgrammaticDate(_ date: Date) -> AttentionExactTimestamp {
        let interval = date.timeIntervalSince1970
        guard interval.isFinite else {
            return AttentionExactTimestamp(epochSeconds: 0, nanosecond: 0)!
        }
        // Backend .NET contract: valid UTC interval is [year 0001, year 10000).
        //   0001-01-01T00:00:00Z         = -62_135_596_800 (inclusive)
        //   10000-01-01T00:00:00Z        =  253_402_300_800 (EXCLUSIVE)
        //   Max representable instant    =  epoch 253_402_300_799 + 999_999_999 ns.
        let minSeconds: Double = -62_135_596_800
        let upperBoundExclusive: Double = 253_402_300_800
        let maxEpochSecondsInclusive: Int64 = 253_402_300_799
        let nanosPerSecond = AttentionTimestampCodec.nanosecondsPerSecond

        // Clamp below the range → snap to lower edge.
        if interval < minSeconds {
            return AttentionExactTimestamp(
                epochSeconds: -62_135_596_800,
                nanosecond: 0
            )!
        }
        // Clamp at or above the year-10000 boundary → saturate at the
        // maximum representable instant so we never emit an instant
        // whose UTC year is 10000.
        if interval >= upperBoundExclusive {
            return AttentionExactTimestamp(
                epochSeconds: maxEpochSecondsInclusive,
                nanosecond: UInt32(nanosPerSecond - 1)
            )!
        }
        // In-range: floor to whole seconds, take the fractional
        // remainder, and convert to nanoseconds. `interval` is strictly
        // below the upper bound so `floored` fits in Int64 and cannot
        // land on the year-10000 second.
        let floored = interval.rounded(.down)
        let seconds = Int64(floored)
        let fraction = interval - floored // guaranteed [0, 1)
        var nanos = UInt32(
            (fraction * Double(nanosPerSecond)).rounded()
        )
        var epochSecs = seconds
        // Rounding can promote 999_999_999.5 → 1_000_000_000. Carry
        // into the next second unless that carry would push us onto
        // the year-10000 boundary (unrepresentable); in that case
        // saturate at the max representable nanosecond so the
        // presentation instant stays strictly below year 10000.
        if nanos >= UInt32(nanosPerSecond) {
            if epochSecs < maxEpochSecondsInclusive {
                epochSecs += 1
                nanos = 0
            } else {
                nanos = UInt32(nanosPerSecond - 1)
            }
        }
        return AttentionExactTimestamp(
            epochSeconds: epochSecs,
            nanosecond: nanos
        )!
    }

    /// Non-trapping `Date` view of the pair, used for UI presentation
    /// only. Fingerprint identity never routes through this — `Date` is a
    /// `Double`-backed `TimeInterval` and loses ns precision far from
    /// 1970, so the pair is the only authority.
    ///
    /// The Double at the top of the range has an ULP (~3e-5 s) much
    /// larger than 1 ns, so adding the fractional nanoseconds to
    /// `epochSeconds = 253_402_300_799` can round *up* to
    /// `253_402_300_800.0` — a Date value inside year 10000. We snap the
    /// computed interval strictly below the year-10000 boundary so a
    /// UI/logging consumer can never observe a presentation instant
    /// with year 10000.
    var presentationDate: Date {
        let upperBoundExclusive: Double = 253_402_300_800
        let raw =
            TimeInterval(epochSeconds)
            + TimeInterval(nanosecond)
                / TimeInterval(AttentionTimestampCodec.nanosecondsPerSecond)
        let snapped = min(raw, upperBoundExclusive.nextDown)
        return Date(timeIntervalSince1970: snapped)
    }
}

/// Wire ↔ ``AttentionExactTimestamp`` codec. Narrow surface used by
/// ``AttentionItem`` for its custom `Codable` conformance. All arithmetic
/// is checked; every failure path returns `nil` so the caller can raise a
/// typed `DecodingError.dataCorruptedError` with the offending string —
/// no trapping paths, no silent truncation.
enum AttentionTimestampCodec {
    static let nanosecondsPerSecond: Int64 = 1_000_000_000
    static let secondsPerDay: Int64 = 86_400

    /// Inclusive year bounds mirroring the backend .NET `DateTime`
    /// contract (`DateTime.MinValue` = 0001-01-01, `DateTime.MaxValue` =
    /// 9999-12-31T23:59:59.9999999).
    static let minYear: Int64 = 1
    static let maxYear: Int64 = 9999

    // MARK: Decode

    /// Parse an ISO 8601 timestamp into an ``AttentionExactTimestamp``.
    /// Returns `nil` on any invalid input so the caller can throw a
    /// typed decoding error surfaced to the SignalR/API pipeline.
    ///
    /// Accepted shapes (backend .NET `DateTime` / `DateTimeOffset`):
    ///   * `YYYY-MM-DDTHH:MM:SS[.f{1,9}]Z`
    ///   * `YYYY-MM-DDTHH:MM:SS[.f{1,9}]±HH:MM`
    ///   * `YYYY-MM-DDTHH:MM:SS[.f{1,9}]±HHMM`
    ///   * `YYYY-MM-DDTHH:MM:SS[.f{1,9}]±HH`
    ///
    /// Rejects: >9 fractional digits (backend cannot produce them —
    /// silent truncation would lose real precision), calendar-invalid
    /// dates (e.g. Feb 30), years outside 0001–9999, invalid offsets,
    /// missing timezone suffix, or any Int64 overflow in the day →
    /// second conversion.
    ///
    /// Offset-equivalent wire strings canonicalise to the same
    /// `(epochSeconds, nanosecond)` — e.g.
    /// `2026-06-01T07:00:00-05:00` and `2026-06-01T12:00:00Z` decode
    /// equal.
    static func decode(_ wire: String) -> AttentionExactTimestamp? {
        // Locate the timezone suffix. `T` anchors the search so date
        // dashes (`YYYY-MM-DD`) cannot be misread as offset signs.
        let timezoneStart: String.Index
        if wire.hasSuffix("Z") {
            timezoneStart = wire.index(before: wire.endIndex)
        } else if let tIndex = wire.firstIndex(of: "T") {
            let afterT = wire.index(after: tIndex)
            guard afterT < wire.endIndex,
                  let offsetIdx = wire[afterT...].lastIndex(where: {
                      $0 == "+" || $0 == "-"
                  }) else {
                return nil
            }
            timezoneStart = offsetIdx
        } else {
            return nil
        }

        let timeSpan = wire[..<timezoneStart]
        let timezoneSpan = wire[timezoneStart...]

        guard let offsetSeconds = parseTimezone(timezoneSpan) else {
            return nil
        }

        // Split the whole seconds from any fractional block. The
        // fractional block must be 1–9 ASCII digits — reject anything
        // longer so we never silently truncate real backend precision.
        let wholeString: Substring
        var fractionalDigits: Substring = ""
        if let dotIdx = timeSpan.lastIndex(of: ".") {
            wholeString = timeSpan[..<dotIdx]
            fractionalDigits = timeSpan[timeSpan.index(after: dotIdx)...]
            guard !fractionalDigits.isEmpty,
                  fractionalDigits.count <= 9,
                  fractionalDigits.allSatisfy({
                      $0.isASCII && $0.isNumber
                  }) else {
                return nil
            }
        } else {
            wholeString = timeSpan
        }

        guard let parts = parseWholeComponents(wholeString) else {
            return nil
        }

        // Backend-contract range checks (year window + calendar validity).
        guard parts.year >= minYear, parts.year <= maxYear,
              isValidCalendarDate(
                  year: parts.year,
                  month: parts.month,
                  day: parts.day
              ),
              parts.hour >= 0, parts.hour <= 23,
              parts.minute >= 0, parts.minute <= 59,
              parts.second >= 0, parts.second <= 59 else {
            return nil
        }

        // Days since 1970-01-01 via the (well-tested) Howard Hinnant
        // proleptic Gregorian algorithm. Domain fits comfortably in
        // Int64 for the 0001..9999 window (|days| < 3e6), but we still
        // guard the seconds conversion with `.multipliedReportingOverflow`
        // so any future range change fails loudly rather than trapping.
        let days = daysSinceEpoch(
            year: parts.year,
            month: parts.month,
            day: parts.day
        )
        let (daySeconds, overflowDay) =
            days.multipliedReportingOverflow(by: secondsPerDay)
        guard !overflowDay else { return nil }
        let timeOfDaySeconds =
            Int64(parts.hour) * 3600
            + Int64(parts.minute) * 60
            + Int64(parts.second)
        let (localSeconds, overflowLocal) =
            daySeconds.addingReportingOverflow(timeOfDaySeconds)
        guard !overflowLocal else { return nil }
        // A `+HH:MM` offset means the local clock is `HH:MM` ahead of
        // UTC, so UTC = local − offset. The subtraction is checked so
        // ±14 h offsets at the year-9999 edge cannot silently wrap.
        let (utcSeconds, overflowUtc) =
            localSeconds.subtractingReportingOverflow(offsetSeconds)
        guard !overflowUtc else { return nil }

        // The `parts.year` bound above checks the LOCAL year. After
        // applying the offset the normalized UTC instant can fall
        // outside the backend contract (e.g. `0001-01-01T00:00:00+14:00`
        // → UTC year 0000, `9999-12-31T23:59:59.9999999-14:00` → UTC
        // year 10000). Reject those explicitly so the exact pair we
        // return is always encodable and round-trippable.
        let minUtcSeconds: Int64 = -62_135_596_800   // 0001-01-01T00:00:00Z (inclusive)
        let maxUtcSeconds: Int64 =  253_402_300_799  // 9999-12-31T23:59:59Z (inclusive; ns extends to <year 10000)
        guard utcSeconds >= minUtcSeconds,
              utcSeconds <= maxUtcSeconds else {
            return nil
        }

        // Fractional digits → nanoseconds. Right-pad with `0` to 9
        // digits so the count encodes precision without changing value.
        let nanoseconds: UInt32
        if fractionalDigits.isEmpty {
            nanoseconds = 0
        } else {
            let padCount = 9 - fractionalDigits.count
            let padded = String(fractionalDigits)
                + String(repeating: "0", count: padCount)
            guard let n = UInt32(padded) else { return nil }
            nanoseconds = n
        }

        return AttentionExactTimestamp(
            epochSeconds: utcSeconds,
            nanosecond: nanoseconds
        )
    }

    // MARK: Encode

    /// Emit a canonical UTC ISO 8601 string. Whole-second inputs
    /// (nanosecond == 0) emit no fractional block and are byte-identical
    /// to Foundation's `.iso8601` encoder — this is the legacy
    /// compatibility guarantee the pre-780 wire had.
    ///
    /// Fractional inputs preserve nanosecond precision with trailing
    /// zeros trimmed for readability (`.100Z`, not `.100000000Z`).
    static func encode(_ timestamp: AttentionExactTimestamp) -> String {
        // Split the signed epoch-seconds into days + seconds-of-day
        // using floored division so `secondsOfDay` is always in
        // `[0, 86_400)` even for pre-epoch instants.
        var days = timestamp.epochSeconds / secondsPerDay
        var secondsOfDay = timestamp.epochSeconds % secondsPerDay
        if secondsOfDay < 0 {
            days -= 1
            secondsOfDay += secondsPerDay
        }

        let civil = civilFromDays(days)
        let hour = Int(secondsOfDay / 3600)
        let minute = Int((secondsOfDay % 3600) / 60)
        let second = Int(secondsOfDay % 60)

        // Year is bounded to 0001..9999 by construction (see decode /
        // programmatic clamp), so a 4-digit `%04d` field is exact.
        let base = String(
            format: "%04d-%02d-%02dT%02d:%02d:%02d",
            Int(civil.year),
            Int(civil.month),
            Int(civil.day),
            hour, minute, second
        )
        guard timestamp.nanosecond != 0 else { return "\(base)Z" }

        var digits = String(format: "%09u", timestamp.nanosecond)
        while digits.count > 1 && digits.hasSuffix("0") {
            digits.removeLast()
        }
        return "\(base).\(digits)Z"
    }

    // MARK: Timezone parsing

    /// Parse an ISO 8601 timezone suffix into a signed offset in seconds.
    /// Accepts `Z`, `±HH:MM`, `±HHMM`, `±HH`. Hour range is 0–14 (the
    /// widest civil offset), minute range is 0–59.
    private static func parseTimezone(_ raw: Substring) -> Int64? {
        if raw == "Z" { return 0 }
        guard let first = raw.first else { return nil }
        let sign: Int64
        switch first {
        case "+": sign = 1
        case "-": sign = -1
        default: return nil
        }
        let body = raw.dropFirst()
        let hours: Int64
        let minutes: Int64
        switch body.count {
        case 5:
            // HH:MM
            let parts = body.split(separator: ":")
            guard parts.count == 2, parts[0].count == 2, parts[1].count == 2,
                  let h = Int64(parts[0]), let m = Int64(parts[1]) else {
                return nil
            }
            hours = h
            minutes = m
        case 4:
            // HHMM
            let hSlice = body.prefix(2)
            let mSlice = body.suffix(2)
            guard let h = Int64(hSlice), let m = Int64(mSlice) else {
                return nil
            }
            hours = h
            minutes = m
        case 2:
            // HH
            guard let h = Int64(body) else { return nil }
            hours = h
            minutes = 0
        default:
            return nil
        }
        // ISO-8601 civil offsets extend at most ±14:00 — anything
        // beyond that (including `+14:01`..`+14:59`) is invalid. The
        // hour must be in 0..14; when it is exactly 14 the minute must
        // be 0 so ±14:00 remains the strict maximum magnitude.
        guard hours >= 0, hours <= 14, minutes >= 0, minutes <= 59 else {
            return nil
        }
        if hours == 14 && minutes != 0 {
            return nil
        }
        return sign * (hours * 3600 + minutes * 60)
    }

    /// Parse the `YYYY-MM-DDTHH:MM:SS` whole-time head. Strict length
    /// (19 chars) + strict punctuation + strict ASCII-digit checks —
    /// any deviation returns `nil` so the caller can throw a typed
    /// decoding error rather than accept a partial parse.
    private static func parseWholeComponents(
        _ raw: Substring
    ) -> (year: Int64, month: Int32, day: Int32, hour: Int32, minute: Int32, second: Int32)? {
        guard raw.count == 19 else { return nil }
        let chars = Array(raw)
        guard chars[4] == "-", chars[7] == "-", chars[10] == "T",
              chars[13] == ":", chars[16] == ":" else {
            return nil
        }
        let digitPositions = [0, 1, 2, 3, 5, 6, 8, 9, 11, 12, 14, 15, 17, 18]
        for pos in digitPositions {
            guard chars[pos].isASCII, chars[pos].isNumber else { return nil }
        }
        guard let year = Int64(String(chars[0..<4])),
              let month = Int32(String(chars[5..<7])),
              let day = Int32(String(chars[8..<10])),
              let hour = Int32(String(chars[11..<13])),
              let minute = Int32(String(chars[14..<16])),
              let second = Int32(String(chars[17..<19])) else {
            return nil
        }
        return (year, month, day, hour, minute, second)
    }

    // MARK: Civil-date algorithm
    //
    // Howard Hinnant's algorithms from
    // https://howardhinnant.github.io/date_algorithms.html — well-tested
    // proleptic Gregorian calendar math that avoids hand-rolled leap-year
    // and month-length bugs and handles year 0001 through 9999 (and
    // beyond) without depending on Foundation `Calendar`, which is
    // timezone- and locale-sensitive.

    /// Days between 1970-01-01 (returns 0) and the given civil date.
    /// Negative for pre-epoch dates. Domain checked by the caller.
    static func daysSinceEpoch(
        year: Int64,
        month: Int32,
        day: Int32
    ) -> Int64 {
        let m = Int64(month)
        let d = Int64(day)
        // Shift January/February into the previous year so the year
        // starts in March — this is the trick that makes the algorithm
        // uniform across leap years.
        let y = year - (m <= 2 ? 1 : 0)
        // 400-year era; era 0 spans 0000..0399, era -1 spans -0400..-0001.
        let era = (y >= 0 ? y : y - 399) / 400
        let yoe = y - era * 400 // year-of-era in 0..399
        let doy = (153 * (m > 2 ? m - 3 : m + 9) + 2) / 5 + (d - 1) // 0..365
        let doe = yoe * 365 + yoe / 4 - yoe / 100 + doy // 0..146096
        return era * 146097 + doe - 719468
    }

    /// Inverse of ``daysSinceEpoch(year:month:day:)``. Returns the civil
    /// date for the given day count.
    static func civilFromDays(
        _ days: Int64
    ) -> (year: Int64, month: Int32, day: Int32) {
        let z = days + 719468
        let era = (z >= 0 ? z : z - 146096) / 146097
        let doe = z - era * 146097 // 0..146096
        let yoe =
            (doe - doe / 1460 + doe / 36524 - doe / 146096) / 365 // 0..399
        let y = yoe + era * 400
        let doy = doe - (365 * yoe + yoe / 4 - yoe / 100) // 0..365
        let mp = (5 * doy + 2) / 153 // 0..11
        let d = Int32(doy - (153 * mp + 2) / 5 + 1)
        let m = Int32(mp < 10 ? mp + 3 : mp - 9)
        return (year: y + (m <= 2 ? 1 : 0), month: m, day: d)
    }

    /// Whether `(year, month, day)` is a valid proleptic Gregorian date.
    static func isValidCalendarDate(
        year: Int64,
        month: Int32,
        day: Int32
    ) -> Bool {
        guard month >= 1, month <= 12, day >= 1 else { return false }
        return day <= daysInMonth(year: year, month: month)
    }

    /// Number of days in the given month/year (proleptic Gregorian).
    private static func daysInMonth(year: Int64, month: Int32) -> Int32 {
        switch month {
        case 1, 3, 5, 7, 8, 10, 12: return 31
        case 4, 6, 9, 11: return 30
        case 2: return isLeapYear(year) ? 29 : 28
        default: return 0
        }
    }

    /// Proleptic Gregorian leap-year rule.
    private static func isLeapYear(_ y: Int64) -> Bool {
        (y % 4 == 0 && y % 100 != 0) || y % 400 == 0
    }
}

/// Kind of attention item. Wire values are lowercase (`failure`, `runout`,
/// `harvest`, `maintenance`, `offline`). New kinds may appear; unknown values
/// decode to ``AttentionKind/unknown`` so a rolling backend update never
/// breaks the client.
enum AttentionKind: String, Codable, Sendable, Equatable {
    case failure
    case runout
    case harvest
    case maintenance
    case offline
    /// Forward-compatibility bucket for kinds the client does not recognise.
    case unknown

    init(from decoder: Decoder) throws {
        let raw = try decoder.singleValueContainer().decode(String.self)
        self = AttentionKind(rawValue: raw) ?? .unknown
    }
}

/// Severity ordering used by the feed (`critical` > `warning` > `info`).
/// Wire values are camelCase.
enum AttentionSeverity: String, Codable, Sendable, Equatable {
    case info
    case warning
    case critical
    /// Forward-compatibility bucket for severities the client does not
    /// recognise. Treated as ``AttentionSeverity/info`` for ordering.
    case unknown

    init(from decoder: Decoder) throws {
        let raw = try decoder.singleValueContainer().decode(String.self)
        self = AttentionSeverity(rawValue: raw) ?? .unknown
    }
}

/// Typed action kind. Clients dispatch by ``AttentionActionKind`` — never by
/// synthesising a URL. Wire values are camelCase.
enum AttentionActionKind: String, Codable, Sendable, Equatable {
    case pause
    case resume
    case cancel
    case acknowledge
    case resolve
    case dismiss
    case snooze
    case harvest
    /// Forward-compatibility bucket for action kinds the client does not
    /// recognise. Callers should skip advertising these to the user because
    /// the server may accept them but the client does not know what they do.
    case unknown

    init(from decoder: Decoder) throws {
        let raw = try decoder.singleValueContainer().decode(String.self)
        self = AttentionActionKind(rawValue: raw) ?? .unknown
    }
}

/// A typed action a client can invoke on an attention item.
struct AttentionAction: Codable, Sendable, Equatable {
    let kind: AttentionActionKind
    let label: String
    let requiresConfirmation: Bool
}

/// A single item in the attention feed. Computed on read on the server;
/// only per-user snoozes are persisted.
struct AttentionItem: Codable, Sendable, Equatable, Identifiable {
    /// Stable computed id of the form `"{kind}:{sourceId}"` (for example
    /// `"failure:{incidentId}"`). Snoozes reference this id.
    let id: String
    let kind: AttentionKind
    let severity: AttentionSeverity
    let printerId: UUID
    let printerName: String
    let title: String
    let detail: String
    /// Wire-exact occurrence pair. This is the fingerprint authority — a
    /// server-decoded item retains the backend's exact tick precision
    /// here (never routed through `Date` / `TimeInterval` first), and a
    /// programmatically-constructed item derives the best canonical pair
    /// non-trapping. Two items are `Equatable`-equal only when this pair
    /// (and every other stored property) match.
    let occurredAtExact: AttentionExactTimestamp
    let actions: [AttentionAction]
    let toolheadIndex: Int?
    /// Wire-exact deadline pair, present when the server surfaces one.
    /// Kept in the same canonical shape as ``occurredAtExact`` so a
    /// decode → encode round-trip preserves fractional precision even
    /// though `deadlineAt` itself does not participate in the fingerprint.
    let deadlineAtExact: AttentionExactTimestamp?
    let jobId: UUID?
    /// Defaults to `true` server-side; sources with non-stable timestamps
    /// set it to `false` to prevent a moving timestamp from silently
    /// defeating an active snooze.
    let allowFreshOccurrenceBypass: Bool

    /// Presentation-only `Date` derivative of ``occurredAtExact``. Uses
    /// `TimeInterval` internally so it loses sub-microsecond precision
    /// far from 1970 — never route fingerprint identity through this.
    var occurredAt: Date { occurredAtExact.presentationDate }

    /// Presentation-only `Date` derivative of ``deadlineAtExact``.
    var deadlineAt: Date? { deadlineAtExact?.presentationDate }

    init(
        id: String,
        kind: AttentionKind,
        severity: AttentionSeverity,
        printerId: UUID,
        printerName: String,
        title: String,
        detail: String,
        occurredAt: Date,
        occurredAtExact: AttentionExactTimestamp? = nil,
        actions: [AttentionAction],
        toolheadIndex: Int? = nil,
        deadlineAt: Date? = nil,
        deadlineAtExact: AttentionExactTimestamp? = nil,
        jobId: UUID? = nil,
        allowFreshOccurrenceBypass: Bool = true
    ) {
        self.id = id
        self.kind = kind
        self.severity = severity
        self.printerId = printerId
        self.printerName = printerName
        self.title = title
        self.detail = detail
        self.occurredAtExact =
            occurredAtExact
            ?? AttentionExactTimestamp.fromProgrammaticDate(occurredAt)
        self.actions = actions
        self.toolheadIndex = toolheadIndex
        self.deadlineAtExact =
            deadlineAtExact
            ?? deadlineAt.map(
                AttentionExactTimestamp.fromProgrammaticDate
            )
        self.jobId = jobId
        self.allowFreshOccurrenceBypass = allowFreshOccurrenceBypass
    }

    /// Default `allowFreshOccurrenceBypass` to `true` when the field is
    /// missing from the payload so an older-server response still decodes.
    /// `occurredAt` and `deadlineAt` are parsed directly into the
    /// canonical ``AttentionExactTimestamp`` pair *before* any `Date`
    /// derivative is exposed, so the fractional-tick precision that
    /// participates in ``AttentionOccurrenceFingerprint`` survives an
    /// encode/decode round trip regardless of the surrounding coder's
    /// global date strategy.
    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        self.id = try c.decode(String.self, forKey: .id)
        self.kind = try c.decode(AttentionKind.self, forKey: .kind)
        self.severity = try c.decode(AttentionSeverity.self, forKey: .severity)
        self.printerId = try c.decode(UUID.self, forKey: .printerId)
        self.printerName = try c.decode(String.self, forKey: .printerName)
        self.title = try c.decode(String.self, forKey: .title)
        self.detail = try c.decode(String.self, forKey: .detail)
        let occurredAtString = try c.decode(String.self, forKey: .occurredAt)
        guard let occurredAtExact = AttentionTimestampCodec.decode(
            occurredAtString
        ) else {
            throw DecodingError.dataCorruptedError(
                forKey: .occurredAt,
                in: c,
                debugDescription: "occurredAt is not a valid ISO 8601 timestamp for the backend .NET DateTime contract (years 0001-9999, 1-9 fractional digits, Z or ±HH:MM offset): \(occurredAtString)"
            )
        }
        self.occurredAtExact = occurredAtExact
        self.actions = try c.decode([AttentionAction].self, forKey: .actions)
        self.toolheadIndex = try c.decodeIfPresent(Int.self, forKey: .toolheadIndex)
        if let deadlineAtString = try c.decodeIfPresent(String.self, forKey: .deadlineAt) {
            guard let deadlineAtExact = AttentionTimestampCodec.decode(deadlineAtString) else {
                throw DecodingError.dataCorruptedError(
                    forKey: .deadlineAt,
                    in: c,
                    debugDescription: "deadlineAt is not a valid ISO 8601 timestamp for the backend .NET DateTime contract: \(deadlineAtString)"
                )
            }
            self.deadlineAtExact = deadlineAtExact
        } else {
            self.deadlineAtExact = nil
        }
        self.jobId = try c.decodeIfPresent(UUID.self, forKey: .jobId)
        self.allowFreshOccurrenceBypass =
            try c.decodeIfPresent(Bool.self, forKey: .allowFreshOccurrenceBypass) ?? true
    }

    /// Mirror of ``init(from:)`` so `AttentionItem` round-trips via the
    /// canonical codec even when the surrounding `JSONEncoder` uses the
    /// default `.iso8601` strategy. Emits directly from
    /// ``occurredAtExact`` / ``deadlineAtExact``, so no `Date` /
    /// `TimeInterval` conversion is ever inserted between the stored
    /// pair and the wire. Optional fields are emitted with
    /// `encodeIfPresent` so an absent `deadlineAt`, `jobId`, or
    /// `toolheadIndex` stays absent (not `null`) on the wire.
    func encode(to encoder: Encoder) throws {
        var c = encoder.container(keyedBy: CodingKeys.self)
        try c.encode(id, forKey: .id)
        try c.encode(kind, forKey: .kind)
        try c.encode(severity, forKey: .severity)
        try c.encode(printerId, forKey: .printerId)
        try c.encode(printerName, forKey: .printerName)
        try c.encode(title, forKey: .title)
        try c.encode(detail, forKey: .detail)
        try c.encode(
            AttentionTimestampCodec.encode(occurredAtExact),
            forKey: .occurredAt
        )
        try c.encode(actions, forKey: .actions)
        try c.encodeIfPresent(toolheadIndex, forKey: .toolheadIndex)
        if let deadlineAtExact {
            try c.encode(
                AttentionTimestampCodec.encode(deadlineAtExact),
                forKey: .deadlineAt
            )
        }
        try c.encodeIfPresent(jobId, forKey: .jobId)
        try c.encode(allowFreshOccurrenceBypass, forKey: .allowFreshOccurrenceBypass)
    }

    private enum CodingKeys: String, CodingKey {
        case id, kind, severity, printerId, printerName, title, detail,
             occurredAt, actions, toolheadIndex, deadlineAt, jobId,
             allowFreshOccurrenceBypass
    }
}

/// Cursor-paginated attention feed envelope from `GET /api/attention`.
///
/// - `items` is the current page's canonically-ordered items.
/// - `nextCursor` is opaque and `nil` when the current page is the last.
///   Callers pass it back verbatim as the `cursor` query parameter.
/// - `healthyPrinterCount` is page-independent — the same value ships on
///   every page so the client can render the "N printers running normally"
///   row without paging.
struct AttentionFeed: Codable, Sendable, Equatable {
    let items: [AttentionItem]
    let nextCursor: String?
    let healthyPrinterCount: Int
}

/// Request body for `POST /api/attention/{id}/snooze`.
///
/// The client intentionally does NOT send a fresh-occurrence anchor. The
/// backend derives an exact anchor from the current item's `occurredAt`
/// server-side when the field is omitted, and the mobile JSON encoder
/// serialises `Date` with `.iso8601` (no fractional seconds), which would
/// truncate any anchor the client tried to supply and defeat the strict
/// `item.OccurredAt > anchor` bypass check.
struct SnoozeAttentionRequest: Codable, Sendable, Equatable {
    let snoozedUntilUtc: Date
}

/// Response body for a successful `POST /api/attention/{id}/snooze`.
///
/// The server echoes back the anchor it derived (from the item's current
/// `occurredAt`) so the client can display or log it. This is a decoded
/// value only — the client never round-trips it back into a subsequent
/// snooze request.
struct SnoozeAttentionResponse: Codable, Sendable, Equatable {
    let snoozedUntilUtc: Date
    let attentionItemAnchorAtUtc: Date?
}

/// Response body for a successful
/// `POST /api/attention/{id}/actions/{actionKind}`.
struct AttentionActionResult: Codable, Sendable, Equatable {
    /// Server-supplied outcome descriptor (for example `"Ok"`). Retained as a
    /// plain string so the client does not couple to the server's internal
    /// outcome enum.
    let outcome: String
}
