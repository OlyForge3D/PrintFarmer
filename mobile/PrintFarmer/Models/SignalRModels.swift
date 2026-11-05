import Foundation

// MARK: - SignalR Event DTOs

/// Delta update broadcast via SignalR "printerupdated" event.
/// Matches backend PrinterStatusUpdate record.
struct PrinterStatusUpdate: Codable, Sendable {
    let id: UUID
    let isOnline: Bool
    let state: String?
    let progress: Double?
    var currentLayer: Int? = nil
    var totalLayers: Int? = nil
    var fanSpeedPercent: Double? = nil
    var liveZOffsetMm: Double? = nil
    /// Runtime overrides in percent; nil (absent or explicit null) clears a stale value.
    var speedFactorPercent: Double? = nil
    var flowFactorPercent: Double? = nil
    var activeExtruderId: String? = nil
    /// Nil means the update did not report sensors (keep cache); `[]` replaces it.
    var sensors: [PrinterSensor]? = nil
    let jobName: String?
    let fileName: String?
    let thumbnailUrl: String?
    var currentJobThumbnailUrl: String? = nil
    let cameraStreamUrl: String?
    let x: Double?
    let y: Double?
    let z: Double?
    let hotendTemp: Double?
    let bedTemp: Double?
    let hotendTarget: Double?
    let bedTarget: Double?
    let homedAxes: String?
    let spoolInfo: PrinterSpoolInfo?
    let mmuStatus: MmuStatus?
}

/// Job queue update broadcast via SignalR "jobqueueupdate" event.
///
/// Two backend producers share this event name:
/// - `PrintJobCompletionService` sends `{ printerId, jobs }` with legacy
///   entries (`queuedAt`, `actualStartTime`, `actualEndTime`).
/// - Queue reorder (`PrintJobManagementService`, #3684) sends
///   `{ printerId?, jobs: [QueuedPrintJobDto] }` where `printerId` is null
///   for unassigned jobs and entries use `queuedAtUtc` /
///   `actualStartTimeUtc` / `actualEndTimeUtc`.
///
/// The event is consumed as a queue invalidation hint, so decoding is
/// lenient: a missing/null `printerId` decodes to `nil` and entries that
/// cannot be decoded are skipped instead of failing the whole frame.
struct JobQueueUpdate: Codable, Sendable {
    let printerId: UUID?
    let jobs: [JobQueueUpdateEntry]

    init(printerId: UUID?, jobs: [JobQueueUpdateEntry]) {
        self.printerId = printerId
        self.jobs = jobs
    }

    private enum CodingKeys: String, CodingKey {
        case printerId
        case jobs
    }

    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        printerId = try? container.decodeIfPresent(UUID.self, forKey: .printerId)
        let entries = (try? container.decodeIfPresent(
            [LossyJobQueueUpdateEntry].self,
            forKey: .jobs
        )) ?? []
        jobs = entries.compactMap(\.entry)
    }
}

private struct LossyJobQueueUpdateEntry: Decodable {
    let entry: JobQueueUpdateEntry?

    init(from decoder: Decoder) throws {
        entry = try? JobQueueUpdateEntry(from: decoder)
    }
}

struct JobQueueUpdateEntry: Codable, Identifiable, Sendable {
    let id: UUID
    let name: String
    let status: PrintJobStatus
    let priority: PrintJobPriority
    let queuedAt: Date
    let actualStartTime: Date?
    let actualEndTime: Date?
}

extension JobQueueUpdateEntry {
    private enum CodingKeys: String, CodingKey {
        case id
        case name
        case status
        case priority
        case queuedAt
        case queuedAtUtc
        case actualStartTime
        case actualStartTimeUtc
        case actualEndTime
        case actualEndTimeUtc
    }

    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        id = try container.decode(UUID.self, forKey: .id)
        name = try container.decodeIfPresent(String.self, forKey: .name) ?? ""
        status = try container.decodeIfPresent(PrintJobStatus.self, forKey: .status) ?? .queued
        priority = try container.decodeIfPresent(PrintJobPriority.self, forKey: .priority) ?? .normal
        if let legacy = try container.decodeIfPresent(Date.self, forKey: .queuedAt) {
            queuedAt = legacy
        } else {
            queuedAt = try container.decode(Date.self, forKey: .queuedAtUtc)
        }
        actualStartTime = try container.decodeIfPresent(Date.self, forKey: .actualStartTime)
            ?? container.decodeIfPresent(Date.self, forKey: .actualStartTimeUtc)
        actualEndTime = try container.decodeIfPresent(Date.self, forKey: .actualEndTime)
            ?? container.decodeIfPresent(Date.self, forKey: .actualEndTimeUtc)
    }

    func encode(to encoder: Encoder) throws {
        var container = encoder.container(keyedBy: CodingKeys.self)
        try container.encode(id, forKey: .id)
        try container.encode(name, forKey: .name)
        try container.encode(status, forKey: .status)
        try container.encode(priority, forKey: .priority)
        try container.encode(queuedAt, forKey: .queuedAt)
        try container.encodeIfPresent(actualStartTime, forKey: .actualStartTime)
        try container.encodeIfPresent(actualEndTime, forKey: .actualEndTime)
    }
}

/// Envelope of the lowercase `queueevent` SignalR event published by the
/// backend queue outbox (`QueueEventEnvelope`, #3684). Printer-group
/// deliveries are redacted, so every field except `eventType` is optional
/// and unknown fields are ignored. The event is a queue invalidation hint:
/// consumers refetch the canonical queue and never treat it as job state.
struct QueueEvent: Codable, Sendable, Equatable {
    var schemaVersion: String? = nil
    var eventId: UUID? = nil
    var sequence: Int64? = nil
    var eventType: String
    var occurredAtUtc: Date? = nil
    var jobId: UUID? = nil
    var printerId: UUID? = nil
    var projectId: UUID? = nil
    var jobStatus: String? = nil

    /// Fallback delivered when a `queueevent` frame cannot be decoded, so
    /// consumers still invalidate the queue instead of dropping the signal.
    static let undecodable = QueueEvent(eventType: "unknown")
}

/// Payload of the lowercase `camerasupdated` SignalR event
/// (`CamerasUpdatedEvent`, #3684): the printer whose linked cameras changed
/// and its current camera ids. Consumers reload the printer's camera
/// metadata; the ids are a hint, not canonical camera state.
struct CamerasUpdatedEvent: Codable, Sendable, Equatable {
    let printerId: UUID
    var cameraIds: [UUID] = []

    init(printerId: UUID, cameraIds: [UUID] = []) {
        self.printerId = printerId
        self.cameraIds = cameraIds
    }

    private enum CodingKeys: String, CodingKey {
        case printerId
        case cameraIds
    }

    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        printerId = try container.decode(UUID.self, forKey: .printerId)
        cameraIds = (try? container.decodeIfPresent([UUID].self, forKey: .cameraIds)) ?? []
    }
}

/// Kind of change carried by an `attentionchanged` invalidation event
/// (issue #707). Wire values are lowercase (`created`, `updated`,
/// `resolved`). Unknown values decode to ``AttentionChangeKind/unknown`` so
/// a rolling backend update never breaks the SignalR consumer.
enum AttentionChangeKind: String, Codable, Sendable, Equatable {
    case created
    case updated
    case resolved
    /// Forward-compatibility bucket for change kinds the client does not
    /// recognise. Callers must still treat it as an invalidation signal.
    case unknown

    init(from decoder: Decoder) throws {
        let raw = try decoder.singleValueContainer().decode(String.self)
        self = AttentionChangeKind(rawValue: raw) ?? .unknown
    }
}

/// Invalidation payload for the lowercase `attentionchanged` SignalR event.
///
/// This is an invalidation hint, not a second source of item truth: the
/// client always refetches `GET /api/attention`. The typed payload lets
/// consumers coalesce per-item, log deterministic timings, and drop
/// obviously malformed frames.
///
/// - `itemId` is the stable computed attention id (for example
///   `"failure:{incidentId}"`).
/// - `changeKind` is exactly `created`, `updated`, or `resolved`.
/// - `occurredAt` is the authoritative source-transition/commit timestamp
///   used by the 1s dispatch / 5s visible-refresh SLOs.
struct AttentionChangedEvent: Codable, Sendable, Equatable {
    let itemId: String
    let changeKind: AttentionChangeKind
    let occurredAt: Date
}

// MARK: - Filament Coverage Invalidation (F4-M / issue #778)

/// Invalidation payload for the lowercase `filamentcoveragechanged`
/// SignalR event emitted by the backend broadcaster shipped in PR #732.
///
/// This is an **invalidation hint only**. The client refetches the
/// canonical `/api/printers/*` coverage endpoints and never persists any
/// field of this payload as the coverage truth — the wire event was
/// designed as a debounced "something changed" ping, not a
/// consistency-preserving delta.
///
/// - `printerId` scopes the invalidation. When `nil`, the event covers
///   the whole fleet (e.g. Spoolman reindex) and every open list/detail
///   view must invalidate. Detail view models filter by matching id or
///   this nil scope.
/// - `reason` is a machine-readable tag (e.g. `printer-status-change`,
///   `job-lifecycle`, `spool-loaded`). Callers may log it but MUST NOT
///   branch coverage rendering on it.
/// - `occurredAt` is the server-side commit timestamp; used for
///   deterministic logging only.
struct FilamentCoverageChangedEvent: Codable, Sendable, Equatable {
    let printerId: UUID?
    let reason: String
    let occurredAt: Date
}

// MARK: - SignalR Connection State

enum SignalRConnectionState: String, Sendable {
    case disconnected
    case connecting
    case connected
    case reconnecting
}

// MARK: - SignalR Protocol Types

/// SignalR handshake request sent over WebSocket after connecting.
struct SignalRHandshakeRequest: Codable, Sendable {
    let `protocol`: String
    let version: Int
}

/// SignalR negotiate response from the /negotiate endpoint.
struct SignalRNegotiateResponse: Codable, Sendable {
    let connectionId: String?
    let connectionToken: String?
    let negotiateVersion: Int?
    let availableTransports: [SignalRTransport]?
}

struct SignalRTransport: Codable, Sendable {
    let transport: String
    let transferFormats: [String]
}

/// Incoming SignalR invocation message (type 1).
struct SignalRInvocationMessage: Codable, Sendable {
    let type: Int
    let target: String?
    let arguments: [AnyCodable]?
}

/// Wrapper for heterogeneous JSON values in SignalR argument arrays.
struct AnyCodable: Codable, @unchecked Sendable {
    let value: Any

    init(_ value: Any) {
        self.value = value
    }

    init(from decoder: Decoder) throws {
        let container = try decoder.singleValueContainer()
        if container.decodeNil() {
            value = NSNull()
        } else if let int = try? container.decode(Int.self) {
            value = int
        } else if let double = try? container.decode(Double.self) {
            value = double
        } else if let bool = try? container.decode(Bool.self) {
            value = bool
        } else if let string = try? container.decode(String.self) {
            value = string
        } else {
            // Store raw JSON data for later decoding with concrete types
            let data = try container.decode(JSONFragment.self)
            value = data
        }
    }

    func encode(to encoder: Encoder) throws {
        var container = encoder.singleValueContainer()
        switch value {
        case is NSNull:
            try container.encodeNil()
        case let int as Int:
            try container.encode(int)
        case let double as Double:
            try container.encode(double)
        case let bool as Bool:
            try container.encode(bool)
        case let string as String:
            try container.encode(string)
        default:
            try container.encodeNil()
        }
    }
}

/// Captures a raw JSON fragment for deferred decoding.
struct JSONFragment: Codable, Sendable {
    let data: Data

    init(from decoder: Decoder) throws {
        // Re-encode the current decoder position to capture raw JSON
        let container = try decoder.singleValueContainer()
        if let dict = try? container.decode([String: AnyCodable].self) {
            data = try JSONEncoder().encode(dict)
        } else if let arr = try? container.decode([AnyCodable].self) {
            data = try JSONEncoder().encode(arr)
        } else {
            data = Data()
        }
    }

    func encode(to encoder: Encoder) throws {
        var container = encoder.singleValueContainer()
        try container.encode(data)
    }

    func decode<T: Decodable>(_ type: T.Type) throws -> T {
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        return try decoder.decode(type, from: data)
    }
}
