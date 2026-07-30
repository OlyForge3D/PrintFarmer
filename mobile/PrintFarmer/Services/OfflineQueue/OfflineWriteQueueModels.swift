import Foundation

// MARK: - Durable Offline Write Queue — Core Model (F10-Q1, #787)
//
// One bounded, actor-isolated outbox that durably retains the ONLY two
// operator part-writes that may be replayed offline: a printed-part stock
// adjustment (`POST /api/parts-inventory/{sku}/adjust`) and a job harvest
// (`POST /api/job-queue/{id}/harvest`). Both already ship typed clients
// (#714/#776) and server-side idempotency (PR #754). This queue adds durable
// retention + serialized, identity-isolated replay that survives relaunch and
// reuses each intent's ORIGINAL idempotency key + frozen body, so a canonical
// success or an idempotent replay produces EXACTLY ONE server effect.
//
// The queue is deliberately NOT a second idempotency owner: the
// `operationKey` frozen into each request body is minted by the #714
// cancellation-safe view models; the queue only re-sends that exact key+body.
//
// SCOPE LOCK: the allowlist (`OfflineWriteOperation`) has exactly two cases.
// Any live printer/filament command (pause/resume/cancel/motion/temperature/
// MMU/unload/load/purge) has NO representation here and therefore cannot be
// encoded, persisted, or enqueued. See `OfflineWriteAllowlistTests`.

// MARK: Kind (allowlist discriminator)

/// The closed allowlist of operations the offline queue may carry. Adding a
/// case here is the ONLY way to widen the queue's scope — a live device
/// command has no kind and so can never be represented.
enum OfflineWriteKind: String, Codable, Sendable, CaseIterable, Equatable {
    case partAdjustment
    case harvest
    // F10-Q2 (#790): the two adapters this child adds to the SAME engine.
    case taskComplete
    case toolheadBind
}

// MARK: Toolhead-spool bind body (frozen)

/// The immutable request body for a final toolhead→spool bookkeeping bind
/// (`PUT /api/printers/{id}/toolheads/{toolheadIndex}/spool`). Matches the
/// server `SetActiveSpoolRequest` contract shipped by #710/#711 (spoolId is the
/// Spoolman-source-qualified spool identity; the override fields carry a guided
/// mismatch "load anyway" decision). This is the ONLY final bookkeeping bind
/// the queue may carry — no physical load/unload/purge/temperature command has
/// any representation here.
struct ToolheadSpoolBindRequest: Codable, Sendable, Equatable {
    /// The Spoolman spool ID to bind (source-qualified spool identity).
    let spoolId: Int
    /// Operator override of a swap-validation mismatch, recorded for audit.
    let overrideMismatch: Bool
    /// Optional operator-supplied reason accompanying an override.
    let overrideReason: String?

    init(spoolId: Int, overrideMismatch: Bool = false, overrideReason: String? = nil) {
        self.spoolId = spoolId
        self.overrideMismatch = overrideMismatch
        self.overrideReason = overrideReason
    }
}

// MARK: Route identity

/// The exact canonical HTTP route an operation replays against. Persisted with
/// the item so the durable record carries the full route identity, not just a
/// kind tag.
struct OfflineWriteRoute: Codable, Sendable, Equatable {
    let method: String
    let path: String
}

// MARK: Operation (frozen typed body + allowlist)

/// The frozen, typed request an offline item replays. This enum IS the
/// allowlist: only `.partAdjustment`, `.harvest`, `.taskComplete`, and
/// `.toolheadBind` can be constructed, encoded, and decoded — EXACTLY four
/// operation kinds (F10-Q2, #790). A custom `Codable` conformance rejects any
/// unknown `kind` discriminator on read (corruption / forward-incompatible
/// record), so a tampered or unknown-version record can never decode into a
/// replayable operation. No skip/dismiss or physical printer/filament command
/// has any representation here.
enum OfflineWriteOperation: Sendable, Equatable {
    case partAdjustment(sku: String, request: AdjustPartInventoryRequest)
    case harvest(jobId: UUID, request: HarvestJobRequest)
    /// A shift task completion (`POST /api/tasks/{id}/complete`). Empty body;
    /// the idempotency key travels as a header. Skip and dismiss have NO case
    /// here and so can never be encoded or enqueued.
    case taskComplete(taskID: String, idempotencyKey: String)
    /// The final toolhead→spool bookkeeping bind. Carries the printer id, the
    /// invariant toolhead index, the header idempotency key, the frozen request
    /// body, and `expectedPriorSpoolId` — the toolhead's binding captured at
    /// enqueue time, used at replay to detect a newer binding (review, never a
    /// silent overwrite). `expectedPriorSpoolId` is a precondition snapshot
    /// only; it is NOT sent to the server.
    case toolheadBind(
        printerID: UUID,
        toolheadIndex: Int,
        idempotencyKey: String,
        request: ToolheadSpoolBindRequest,
        expectedPriorSpoolId: Int?
    )

    var kind: OfflineWriteKind {
        switch self {
        case .partAdjustment: return .partAdjustment
        case .harvest: return .harvest
        case .taskComplete: return .taskComplete
        case .toolheadBind: return .toolheadBind
        }
    }

    /// The intent's stable idempotency key — the `operationKey` frozen into
    /// the request body by the #714 view models. `nil` when the caller never
    /// set one, in which case the item must NOT be enqueued (a queued replay
    /// with no key could double-apply).
    var idempotencyKey: String? {
        switch self {
        case .partAdjustment(_, let request): return request.operationKey
        case .harvest(_, let request): return request.operationKey
        case .taskComplete(_, let key): return key
        case .toolheadBind(_, _, let key, _, _): return key
        }
    }

    /// The canonical route this operation replays against.
    var route: OfflineWriteRoute {
        switch self {
        case .partAdjustment(let sku, _):
            return OfflineWriteRoute(
                method: "POST",
                path: "/api/parts-inventory/\(Self.encodePathSegment(sku))/adjust"
            )
        case .harvest(let jobId, _):
            return OfflineWriteRoute(
                method: "POST",
                path: "/api/job-queue/\(jobId.uuidString)/harvest"
            )
        case .taskComplete(let taskID, _):
            return OfflineWriteRoute(
                method: "POST",
                path: "/api/tasks/\(Self.encodePathSegment(taskID))/complete"
            )
        case .toolheadBind(let printerID, let toolheadIndex, _, _, _):
            return OfflineWriteRoute(
                method: "PUT",
                path: "/api/printers/\(printerID.uuidString)/toolheads/\(toolheadIndex)/spool"
            )
        }
    }

    /// The ordering entity: adjustments sharing a SKU, and harvests sharing a
    /// job, must replay in creation order relative to one another. Normalized
    /// to match the server's width/case-insensitive identity comparison for
    /// SKUs so `"abc"` and `"ABC"` are treated as the same entity.
    var entityKey: String {
        switch self {
        case .partAdjustment(let sku, _):
            return "sku:\(Self.normalizeIdentity(sku))"
        case .harvest(let jobId, _):
            return "job:\(jobId.uuidString)"
        case .taskComplete(let taskID, _):
            return "task:\(taskID)"
        case .toolheadBind(let printerID, let toolheadIndex, _, _, _):
            return "toolhead:\(printerID.uuidString):\(toolheadIndex)"
        }
    }

    static func normalizeIdentity(_ value: String) -> String {
        value.trimmingCharacters(in: .whitespacesAndNewlines)
            .precomposedStringWithCompatibilityMapping
            .uppercased()
    }

    private static func encodePathSegment(_ segment: String) -> String {
        var set = CharacterSet.urlPathAllowed
        set.remove(charactersIn: ":/?#[]@")
        return segment.addingPercentEncoding(withAllowedCharacters: set) ?? segment
    }
}

extension OfflineWriteOperation: Codable {
    private enum CodingKeys: String, CodingKey {
        case kind
        case sku
        case jobId
        case request
        case taskId
        case idempotencyKey
        case printerId
        case toolheadIndex
        case expectedPriorSpoolId
    }

    /// Thrown when a persisted record carries a `kind` outside the allowlist —
    /// treated by the store as a corrupt/unknown-version item and dropped
    /// rather than decoded into something replayable.
    struct UnknownKindError: Error, Equatable {
        let raw: String
    }

    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        let rawKind = try container.decode(String.self, forKey: .kind)
        guard let kind = OfflineWriteKind(rawValue: rawKind) else {
            throw UnknownKindError(raw: rawKind)
        }
        switch kind {
        case .partAdjustment:
            let sku = try container.decode(String.self, forKey: .sku)
            let request = try container.decode(AdjustPartInventoryRequest.self, forKey: .request)
            self = .partAdjustment(sku: sku, request: request)
        case .harvest:
            let jobId = try container.decode(UUID.self, forKey: .jobId)
            let request = try container.decode(HarvestJobRequest.self, forKey: .request)
            self = .harvest(jobId: jobId, request: request)
        case .taskComplete:
            let taskID = try container.decode(String.self, forKey: .taskId)
            let key = try container.decode(String.self, forKey: .idempotencyKey)
            self = .taskComplete(taskID: taskID, idempotencyKey: key)
        case .toolheadBind:
            let printerID = try container.decode(UUID.self, forKey: .printerId)
            let toolheadIndex = try container.decode(Int.self, forKey: .toolheadIndex)
            let key = try container.decode(String.self, forKey: .idempotencyKey)
            let request = try container.decode(ToolheadSpoolBindRequest.self, forKey: .request)
            let expectedPriorSpoolId = try container.decodeIfPresent(Int.self, forKey: .expectedPriorSpoolId)
            self = .toolheadBind(
                printerID: printerID,
                toolheadIndex: toolheadIndex,
                idempotencyKey: key,
                request: request,
                expectedPriorSpoolId: expectedPriorSpoolId
            )
        }
    }

    func encode(to encoder: Encoder) throws {
        var container = encoder.container(keyedBy: CodingKeys.self)
        try container.encode(kind.rawValue, forKey: .kind)
        switch self {
        case .partAdjustment(let sku, let request):
            try container.encode(sku, forKey: .sku)
            try container.encode(request, forKey: .request)
        case .harvest(let jobId, let request):
            try container.encode(jobId, forKey: .jobId)
            try container.encode(request, forKey: .request)
        case .taskComplete(let taskID, let key):
            try container.encode(taskID, forKey: .taskId)
            try container.encode(key, forKey: .idempotencyKey)
        case .toolheadBind(let printerID, let toolheadIndex, let key, let request, let expectedPriorSpoolId):
            try container.encode(printerID, forKey: .printerId)
            try container.encode(toolheadIndex, forKey: .toolheadIndex)
            try container.encode(key, forKey: .idempotencyKey)
            try container.encode(request, forKey: .request)
            try container.encodeIfPresent(expectedPriorSpoolId, forKey: .expectedPriorSpoolId)
        }
    }
}

// MARK: Conflict / terminal review states

/// Why an item was parked in a state requiring explicit operator disposition
/// (Discard or Review-and-retry-as-new) rather than continued auto-replay.
enum OfflineWriteConflictReason: String, Codable, Sendable, Equatable {
    /// The server rejected a same-key replay whose body differed (should be
    /// impossible for our frozen-body replay, but surfaced explicitly if seen).
    case sameKeyDifferentBody
    /// A harvest `wrongBin` 409 — the scanned destination bin did not match.
    case wrongBin
    /// A harvest `partMappingRequired` 409 — no resolvable output→SKU mapping.
    case mappingRequired
    /// A 400/422 validation rejection.
    case validation
    /// A 401/403 authorization rejection.
    case authorization
    /// The canonical task/toolhead state changed since enqueue such that a
    /// replay would overwrite a newer binding or complete an incompatible
    /// terminal task — surfaced for review instead of a silent write (F10-Q2,
    /// #790). No mutation is ever performed when this is raised.
    case staleState
    /// The bind/completion target no longer exists in canonical state.
    case unavailable
    /// Any other business 409/404/405 the operator must adjudicate.
    case businessConflict
}

/// A terminal, needs-review disposition for a queued write. Carries the
/// server's verbatim typed conflict (for `wrongBin`/`mappingRequired`) so the
/// status surface can render the exact adjudicated detail, never a synthesized
/// message.
struct OfflineWriteConflict: Codable, Sendable, Equatable {
    let reason: OfflineWriteConflictReason
    let message: String
    let partsInventoryConflict: PartsInventoryConflict?

    init(reason: OfflineWriteConflictReason, message: String, partsInventoryConflict: PartsInventoryConflict? = nil) {
        self.reason = reason
        self.message = message
        self.partsInventoryConflict = partsInventoryConflict
    }
}

// MARK: Persisted item status

/// The durable status of a queued item. `replaying` is a transient in-memory
/// state only (surfaced through `OfflineWriteQueueEntry.isReplaying`) and is
/// never persisted — a record read back from disk is `pending`, `conflict`,
/// `expiredNeedsReview`, or `paused`.
enum OfflineWriteItemStatus: Codable, Sendable, Equatable {
    /// Eligible for automatic replay when online, gate-enabled, and not expired.
    case pending
    /// Terminal until the operator disposes of it (Discard / Retry-as-new).
    case conflict(OfflineWriteConflict)
    /// Older than the 7-day server idempotency window: no automatic network
    /// request may ever be made; only Discard or Review-and-retry-as-new.
    case expiredNeedsReview
    /// Replay is disabled by the operator feature gate: retained, never
    /// discarded, and resumed (to `pending`) when the gate is re-enabled.
    case paused

    var isPending: Bool { if case .pending = self { return true }; return false }
    var isTerminalNeedsReview: Bool {
        switch self {
        case .conflict, .expiredNeedsReview: return true
        case .pending, .paused: return false
        }
    }
}

// MARK: Durable item

/// One durable outbox entry. Stores ONLY non-secret intent: a stable queue id,
/// the operation kind, the frozen typed body, the original idempotency key, the
/// canonical route, the owning identity, and the immutable creation instant.
/// It never stores auth tokens, cookies, printer credentials, or mutable view
/// state — none of those types are even reachable from here.
struct OfflineWriteItem: Codable, Sendable, Equatable, Identifiable {
    /// Stable queue id (distinct from the idempotency key).
    let id: UUID
    let serverID: UUID
    let userID: UUID
    /// Immutable UTC instant the intent was durably enqueued. Drives the
    /// 7-day expiry decision; never mutated by a retry.
    let createdAt: Date
    let idempotencyKey: String
    let route: OfflineWriteRoute
    let operation: OfflineWriteOperation
    var status: OfflineWriteItemStatus
    /// The queue id of a prerequisite domain-write item that MUST replay
    /// (succeed and leave the queue) before this item becomes eligible (F10-Q2,
    /// #790, dependency ordering). `nil` for standalone items. While the
    /// prerequisite item is still present in the namespace (pending, in
    /// conflict, expired, or paused), this item is held back — so a linked task
    /// completion can never be applied before its required domain mutation
    /// succeeds. Optional + absent in older records, so #787 items are
    /// unaffected.
    var prerequisiteItemID: UUID?

    var kind: OfflineWriteKind { operation.kind }
    var entityKey: String { operation.entityKey }

    init(
        id: UUID = UUID(),
        serverID: UUID,
        userID: UUID,
        createdAt: Date,
        idempotencyKey: String,
        operation: OfflineWriteOperation,
        status: OfflineWriteItemStatus = .pending,
        prerequisiteItemID: UUID? = nil
    ) {
        self.id = id
        self.serverID = serverID
        self.userID = userID
        self.createdAt = createdAt
        self.idempotencyKey = idempotencyKey
        self.route = operation.route
        self.operation = operation
        self.status = status
        self.prerequisiteItemID = prerequisiteItemID
    }

    /// Whether this item may still auto-replay given a fixed evaluation instant
    /// and the retention window. Uses `<=` so the exact 7-day boundary still
    /// replays; strictly older items must expire.
    func isReplayable(at now: Date, retention: TimeInterval) -> Bool {
        guard status.isPending else { return false }
        return now.timeIntervalSince(createdAt) <= retention
    }

    /// Whether this item is strictly older than the retention window at `now`.
    func isExpired(at now: Date, retention: TimeInterval) -> Bool {
        now.timeIntervalSince(createdAt) > retention
    }
}

// MARK: Snapshot entry (UI)

/// A read-only view of a queued item augmented with the transient `isReplaying`
/// flag (the item currently in flight under the single replay owner).
struct OfflineWriteQueueEntry: Sendable, Equatable, Identifiable {
    let item: OfflineWriteItem
    let isReplaying: Bool

    var id: UUID { item.id }
}

// MARK: Replay outcome

/// The classification of a single replay attempt, used by the queue coordinator
/// to decide the item's next state. Produced by the transport adapter from a
/// concrete server response or `NetworkError`.
enum OfflineWriteReplayOutcome: Sendable, Equatable {
    /// A canonical success OR an idempotent replay of an already-applied
    /// effect. The item is removed (exactly one server effect recorded).
    case success
    /// A transient failure (offline / timeout / unreachable / 5xx). The item
    /// stays `pending`; the drain stops so ordering is preserved for retry.
    case retryable
    /// A terminal server rejection requiring operator disposition.
    case conflict(OfflineWriteConflict)
    /// The active identity is no longer valid for this request (session lost).
    /// The drain stops without mutating the item so nothing replays under a
    /// wrong identity.
    case identityChanged
}

// MARK: Enqueue result

/// The outcome of attempting to durably enqueue an intent.
enum OfflineWriteEnqueueResult: Sendable, Equatable {
    case enqueued(OfflineWriteItem)
    /// The operator feature gate is off: the caller must use direct-online only
    /// and must NOT retain the intent offline.
    case replayDisabled
    /// No authenticated identity is active (logged out / demo).
    case noIdentity
    /// The bounded outbox is full.
    case queueFull
    /// The intent carried no idempotency key and cannot be safely replayed.
    case missingIdempotencyKey
}

// MARK: Clock

/// Injectable time source so the 7-day boundary and expiry are driven by a
/// controllable clock in tests — never wall-clock sleeps.
protocol OfflineQueueClock: Sendable {
    func now() -> Date
}

struct SystemOfflineQueueClock: OfflineQueueClock {
    func now() -> Date { Date() }
}

// MARK: Configuration

struct OfflineWriteQueueConfiguration: Sendable, Equatable {
    /// Server idempotency retention window (PR #754): 7 days.
    var retention: TimeInterval
    /// Upper bound on retained items (the outbox is bounded).
    var maxItems: Int

    static let `default` = OfflineWriteQueueConfiguration(
        retention: 7 * 24 * 60 * 60,
        maxItems: 500
    )
}
