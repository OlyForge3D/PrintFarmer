import Foundation

// MARK: - SignalR Event DTOs

/// Delta update broadcast via SignalR "printerupdated" event.
/// Matches backend PrinterStatusUpdate record.
struct PrinterStatusUpdate: Codable, Sendable {
    let id: UUID
    let isOnline: Bool
    let state: String?
    let progress: Double?
    let jobName: String?
    let fileName: String?
    let thumbnailUrl: String?
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
/// Matches the anonymous object shape from PrintJobCompletionService.
struct JobQueueUpdate: Codable, Sendable {
    let printerId: UUID
    let jobs: [JobQueueUpdateEntry]
}

struct JobQueueUpdateEntry: Codable, Identifiable, Sendable {
    let id: UUID
    let name: String
    let status: PrintJobStatus
    let priority: Int
    let queuedAt: Date
    let actualStartTime: Date?
    let actualEndTime: Date?
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
