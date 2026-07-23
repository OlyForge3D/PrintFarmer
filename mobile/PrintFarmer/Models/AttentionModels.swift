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

// MARK: - Canonical Attention timestamp codec
//
// Attention items carry occurrence timestamps whose exact value participates
// in ``AttentionOccurrenceFingerprint`` — the identity used to correlate an
// item across pagination, media loads, and pending actions. The backend
// serialises `DateTime` via System.Text.Json, which preserves fractional
// seconds when present; two occurrences for the same item within one second
// therefore differ only in their fractional component and MUST decode to
// distinguishable ``Date`` values.
//
// The app-wide `JSONEncoder`/`JSONDecoder` on `APIClient` uses
// `.iso8601` for encode (whole-second) and a custom decode strategy that
// falls back through fractional then plain formats. Those default strategies
// are correct for most DTOs (e.g. ``SnoozeAttentionRequest`` deliberately
// truncates to whole seconds — see its header below), but they collapse the
// fractional part on any encode round-trip, which would silently defeat the
// occurrence-identity contract if applied to ``AttentionItem``.
//
// ``AttentionTimestampCodec`` establishes ONE shared canonical representation
// — signed `Int64` nanoseconds since the Unix epoch — that ``AttentionItem``
// uses for decode, encode, and fingerprint conversion. The scope is
// deliberately narrow: only ``AttentionItem`` opts into this codec via its
// custom `Codable` conformance. Other Attention DTOs continue to honour the
// global date strategy on the surrounding coder.
enum AttentionTimestampCodec {
    /// Nanoseconds per second. Kept as `Int64` so the modular arithmetic in
    /// ``encode(_:)`` is exact for the full ``Date`` range we care about.
    static let nanosecondsPerSecond: Int64 = 1_000_000_000

    /// Whole-second ISO 8601 formatter (`YYYY-MM-DDTHH:MM:SSZ` and offset
    /// forms). We build the fractional block manually because
    /// `ISO8601DateFormatter.withFractionalSeconds` is capped at
    /// millisecond precision on Apple platforms and would silently truncate
    /// sub-millisecond input.
    nonisolated(unsafe) private static let wholeSecondFormatter: ISO8601DateFormatter = {
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime]
        return formatter
    }()

    /// Canonical nanoseconds since 1970-01-01T00:00:00Z for a `Date`.
    ///
    /// Any two dates that decode from equal wire strings via ``decode(_:)``
    /// must produce equal nanosecond values here — this is what guarantees
    /// fingerprint round-trip stability across a decode/encode/decode cycle.
    static func nanoseconds(_ date: Date) -> Int64 {
        Int64((date.timeIntervalSince1970 * TimeInterval(nanosecondsPerSecond)).rounded())
    }

    /// Decode an ISO 8601 timestamp with an optional fractional-second block
    /// of 1–9 digits. Returns `nil` on any malformed input so the caller can
    /// throw a targeted decoding error.
    ///
    /// Supported timezone suffixes:
    ///   - `Z` (UTC — what the backend emits today)
    ///   - `±HH:MM` (defensive: the parser accepts, but we never emit them)
    static func decode(_ value: String) -> Date? {
        // Locate the timezone suffix so we can slice out the (optional)
        // fractional block that sits between the seconds and the timezone.
        let timezoneStart: String.Index
        if value.hasSuffix("Z") {
            timezoneStart = value.index(before: value.endIndex)
        } else if let tIndex = value.firstIndex(of: "T"),
                  let offsetIndex = value[tIndex...].lastIndex(where: {
                      $0 == "+" || $0 == "-"
                  }) {
            timezoneStart = offsetIndex
        } else {
            return nil
        }

        let timeSpan = value[..<timezoneStart]
        let timezoneSpan = value[timezoneStart...]

        let wholeString: String
        var fractionDigits: Substring = ""
        if let dotIndex = timeSpan.lastIndex(of: ".") {
            wholeString = String(timeSpan[..<dotIndex])
            let afterDot = timeSpan.index(after: dotIndex)
            fractionDigits = timeSpan[afterDot...]
            guard !fractionDigits.isEmpty,
                  fractionDigits.allSatisfy(\.isASCII),
                  fractionDigits.allSatisfy(\.isNumber) else {
                return nil
            }
        } else {
            wholeString = String(timeSpan)
        }

        guard let wholeDate = wholeSecondFormatter.date(from: wholeString + String(timezoneSpan)) else {
            return nil
        }

        guard !fractionDigits.isEmpty else {
            return wholeDate
        }

        // Left-pad or truncate to exactly nine digits so we can convert to
        // Int64 nanoseconds. Over-precise inputs (>9 digits) are truncated
        // defensively — the wire contract does not produce them today.
        var padded = String(fractionDigits.prefix(9))
        if padded.count < 9 {
            padded += String(repeating: "0", count: 9 - padded.count)
        }
        guard let fractionalNanoseconds = Int64(padded) else { return nil }
        return wholeDate.addingTimeInterval(
            TimeInterval(fractionalNanoseconds) / TimeInterval(nanosecondsPerSecond)
        )
    }

    /// Encode a `Date` back to an ISO 8601 UTC wire string. When the date's
    /// canonical nanoseconds have a nonzero fractional part, trailing
    /// zeros are trimmed for readability (`.100Z` rather than `.100000000Z`)
    /// while preserving at least one fractional digit. Whole-second dates
    /// emit no fractional block at all so an encode/decode round-trip of a
    /// legacy whole-second payload is byte-identical.
    static func encode(_ date: Date) -> String {
        let totalNanoseconds = nanoseconds(date)
        var seconds = totalNanoseconds / nanosecondsPerSecond
        var fraction = totalNanoseconds % nanosecondsPerSecond
        if fraction < 0 {
            seconds -= 1
            fraction += nanosecondsPerSecond
        }
        let wholeString = wholeSecondFormatter.string(
            from: Date(timeIntervalSince1970: TimeInterval(seconds))
        )
        guard fraction != 0 else { return wholeString }

        var digits = String(format: "%09lld", fraction)
        while digits.count > 1 && digits.hasSuffix("0") {
            digits.removeLast()
        }
        guard wholeString.hasSuffix("Z") else {
            // The formatter always emits Z for a UTC-anchored input; the
            // guard exists so a future change to formatter options fails
            // loudly rather than silently emitting a malformed timestamp.
            return wholeString
        }
        return "\(wholeString.dropLast()).\(digits)Z"
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
    let occurredAt: Date
    let actions: [AttentionAction]
    let toolheadIndex: Int?
    let deadlineAt: Date?
    let jobId: UUID?
    /// Defaults to `true` server-side; sources with non-stable timestamps
    /// set it to `false` to prevent a moving timestamp from silently
    /// defeating an active snooze.
    let allowFreshOccurrenceBypass: Bool

    init(
        id: String,
        kind: AttentionKind,
        severity: AttentionSeverity,
        printerId: UUID,
        printerName: String,
        title: String,
        detail: String,
        occurredAt: Date,
        actions: [AttentionAction],
        toolheadIndex: Int? = nil,
        deadlineAt: Date? = nil,
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
        self.occurredAt = occurredAt
        self.actions = actions
        self.toolheadIndex = toolheadIndex
        self.deadlineAt = deadlineAt
        self.jobId = jobId
        self.allowFreshOccurrenceBypass = allowFreshOccurrenceBypass
    }

    /// Default `allowFreshOccurrenceBypass` to `true` when the field is
    /// missing from the payload so an older-server response still decodes.
    /// `occurredAt` and `deadlineAt` are decoded via
    /// ``AttentionTimestampCodec`` so the fractional-second precision that
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
        guard let occurredAt = AttentionTimestampCodec.decode(occurredAtString) else {
            throw DecodingError.dataCorruptedError(
                forKey: .occurredAt,
                in: c,
                debugDescription: "occurredAt is not a valid ISO 8601 timestamp: \(occurredAtString)"
            )
        }
        self.occurredAt = occurredAt
        self.actions = try c.decode([AttentionAction].self, forKey: .actions)
        self.toolheadIndex = try c.decodeIfPresent(Int.self, forKey: .toolheadIndex)
        if let deadlineAtString = try c.decodeIfPresent(String.self, forKey: .deadlineAt) {
            guard let deadlineAt = AttentionTimestampCodec.decode(deadlineAtString) else {
                throw DecodingError.dataCorruptedError(
                    forKey: .deadlineAt,
                    in: c,
                    debugDescription: "deadlineAt is not a valid ISO 8601 timestamp: \(deadlineAtString)"
                )
            }
            self.deadlineAt = deadlineAt
        } else {
            self.deadlineAt = nil
        }
        self.jobId = try c.decodeIfPresent(UUID.self, forKey: .jobId)
        self.allowFreshOccurrenceBypass =
            try c.decodeIfPresent(Bool.self, forKey: .allowFreshOccurrenceBypass) ?? true
    }

    /// Mirror of ``init(from:)`` so `AttentionItem` round-trips via the
    /// canonical codec even when the surrounding `JSONEncoder` uses the
    /// default `.iso8601` strategy. Optional fields are emitted with
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
        try c.encode(AttentionTimestampCodec.encode(occurredAt), forKey: .occurredAt)
        try c.encode(actions, forKey: .actions)
        try c.encodeIfPresent(toolheadIndex, forKey: .toolheadIndex)
        if let deadlineAt {
            try c.encode(AttentionTimestampCodec.encode(deadlineAt), forKey: .deadlineAt)
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
