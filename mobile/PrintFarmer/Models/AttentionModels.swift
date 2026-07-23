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
    static let nanosecondsPerSecond: Int64 = 1_000_000_000

    struct DecodedTimestamp {
        let date: Date
        let unixNanoseconds: Int64
    }

    nonisolated(unsafe) private static let wholeSecondFormatter: ISO8601DateFormatter = {
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime]
        return formatter
    }()

    static func approximateNanoseconds(
        forProgrammaticDate date: Date
    ) -> Int64 {
        Int64(
            (date.timeIntervalSince1970
                * TimeInterval(nanosecondsPerSecond)).rounded()
        )
    }

    static func decode(_ value: String) -> Date? {
        decodeCanonical(value)?.date
    }

    static func decodeCanonical(_ value: String) -> DecodedTimestamp? {
        let timezoneStart: String.Index
        if value.hasSuffix("Z") {
            timezoneStart = value.index(before: value.endIndex)
        } else if let timeStart = value.firstIndex(of: "T"),
                  let offsetStart = value[timeStart...].lastIndex(where: {
                      $0 == "+" || $0 == "-"
                  }) {
            timezoneStart = offsetStart
        } else {
            return nil
        }

        let timestamp = value[..<timezoneStart]
        let timezone = value[timezoneStart...]
        let components = timestamp.split(
            separator: ".",
            maxSplits: 1,
            omittingEmptySubsequences: false
        )
        guard let wholeComponent = components.first,
              components.count <= 2,
              let wholeDate = wholeSecondFormatter.date(
                  from: "\(wholeComponent)\(timezone)"
              ) else {
            return nil
        }

        let wholeSeconds = Int64(
            wholeDate.timeIntervalSince1970.rounded()
        )
        guard components.count == 2 else {
            return DecodedTimestamp(
                date: wholeDate,
                unixNanoseconds: wholeSeconds * nanosecondsPerSecond
            )
        }

        let fractionalDigits = String(components[1])
        guard !fractionalDigits.isEmpty,
              fractionalDigits.count <= 9,
              fractionalDigits.allSatisfy(\.isNumber) else {
            return nil
        }
        let padded = fractionalDigits.padding(
            toLength: 9,
            withPad: "0",
            startingAt: 0
        )
        guard let fraction = Int64(padded) else { return nil }
        return DecodedTimestamp(
            date: wholeDate.addingTimeInterval(
                TimeInterval(fraction)
                    / TimeInterval(nanosecondsPerSecond)
            ),
            unixNanoseconds:
                wholeSeconds * nanosecondsPerSecond + fraction
        )
    }

    static func encode(_ date: Date) -> String {
        encode(
            unixNanoseconds: approximateNanoseconds(
                forProgrammaticDate: date
            )
        )
    }

    static func encode(unixNanoseconds totalNanoseconds: Int64) -> String {
        var seconds = totalNanoseconds / nanosecondsPerSecond
        var fraction = totalNanoseconds % nanosecondsPerSecond
        if fraction < 0 {
            seconds -= 1
            fraction += nanosecondsPerSecond
        }
        let whole = wholeSecondFormatter.string(
            from: Date(timeIntervalSince1970: TimeInterval(seconds))
        )
        guard fraction != 0 else { return whole }

        var digits = String(format: "%09lld", fraction)
        while digits.count > 1 && digits.hasSuffix("0") {
            digits.removeLast()
        }
        return "\(whole.dropLast()).\(digits)Z"
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
    let occurredAtUnixNanoseconds: Int64
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
        occurredAtUnixNanoseconds: Int64? = nil,
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
        self.occurredAtUnixNanoseconds =
            occurredAtUnixNanoseconds
            ?? AttentionTimestampCodec.approximateNanoseconds(
                forProgrammaticDate: occurredAt
            )
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
        guard let occurredAt = AttentionTimestampCodec.decodeCanonical(
            occurredAtString
        ) else {
            throw DecodingError.dataCorruptedError(
                forKey: .occurredAt,
                in: c,
                debugDescription: "occurredAt is not a valid ISO 8601 timestamp: \(occurredAtString)"
            )
        }
        self.occurredAt = occurredAt.date
        self.occurredAtUnixNanoseconds = occurredAt.unixNanoseconds
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
        try c.encode(
            AttentionTimestampCodec.encode(
                unixNanoseconds: occurredAtUnixNanoseconds
            ),
            forKey: .occurredAt
        )
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
