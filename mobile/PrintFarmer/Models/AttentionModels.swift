import Foundation

// MARK: - Attention Feed DTOs
//
// Mirrors the backend contract merged in PR #731 (issue #707):
//   - Route: GET /api/attention?cursor=&limit= → AttentionFeed
//   - Route: POST /api/attention/{id}/snooze { snoozedUntilUtc, attentionItemAnchorAtUtc }
//   - Route: DELETE /api/attention/{id}/snooze
//   - Route: POST /api/attention/{id}/actions/{actionKind}
// Wire enums are lowercase/camelCase strings; property names are camelCase.
// See src/infra/Dtos/Attention/AttentionDtos.cs for the authoritative shapes.

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
    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        self.id = try c.decode(String.self, forKey: .id)
        self.kind = try c.decode(AttentionKind.self, forKey: .kind)
        self.severity = try c.decode(AttentionSeverity.self, forKey: .severity)
        self.printerId = try c.decode(UUID.self, forKey: .printerId)
        self.printerName = try c.decode(String.self, forKey: .printerName)
        self.title = try c.decode(String.self, forKey: .title)
        self.detail = try c.decode(String.self, forKey: .detail)
        self.occurredAt = try c.decode(Date.self, forKey: .occurredAt)
        self.actions = try c.decode([AttentionAction].self, forKey: .actions)
        self.toolheadIndex = try c.decodeIfPresent(Int.self, forKey: .toolheadIndex)
        self.deadlineAt = try c.decodeIfPresent(Date.self, forKey: .deadlineAt)
        self.jobId = try c.decodeIfPresent(UUID.self, forKey: .jobId)
        self.allowFreshOccurrenceBypass =
            try c.decodeIfPresent(Bool.self, forKey: .allowFreshOccurrenceBypass) ?? true
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
/// Setting ``attentionItemAnchorAtUtc`` to the item's current `occurredAt`
/// at snooze time enables fresh-occurrence bypass: a later occurrence with a
/// strictly greater `occurredAt` will surface again despite the snooze.
struct SnoozeAttentionRequest: Codable, Sendable, Equatable {
    let snoozedUntilUtc: Date
    let attentionItemAnchorAtUtc: Date?
}

/// Response body for a successful `POST /api/attention/{id}/snooze`.
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
