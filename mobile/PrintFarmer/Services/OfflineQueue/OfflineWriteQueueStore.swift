import Foundation

// MARK: - Durable Offline Write Queue — Persistence (F10-Q1, #787)
//
// The outbox must survive process death / relaunch, so its items live in a
// schema-versioned JSON envelope on disk. Persistence is resilient:
//   * a wholesale-unreadable or unknown-*envelope*-version file is quarantined
//     (copied aside) and the live path starts empty — never crashes, never
//     silently overwrites a recoverable blob;
//   * an individual item that cannot be decoded (unknown `kind`, malformed
//     body) is skipped, so one bad record can't strand every other intent.
//
// Items for every namespace `(serverID, userID)` share one file; the queue
// coordinator filters by the active identity. Retaining another namespace's
// bytes untouched is exactly what lets a server/user switch keep — but never
// replay — the previous identity's writes.

/// The durable store contract. Deliberately coarse (load-all / save-all): the
/// coordinator owns the authoritative in-memory list and rewrites the whole
/// bounded set atomically on each transition, which keeps "persist before
/// attempting" trivially correct.
protocol OfflineWriteQueueStoring: Sendable {
    /// Loads every persisted item across all namespaces, dropping any record
    /// that cannot be decoded. Never throws — a broken file yields `[]`.
    func loadAll() async -> [OfflineWriteItem]
    /// Atomically persists the full item set (temp-write + rename). Returns
    /// `true` only when the durable write actually succeeded; `false` on an
    /// encode, temp-write, atomic-replacement, or filesystem failure — so a
    /// caller that must guarantee durability (enqueue) can refuse to report the
    /// intent as safely queued.
    @discardableResult
    func saveAll(_ items: [OfflineWriteItem]) async -> Bool
}

// MARK: - Envelope

/// On-disk container. `version` gates forward compatibility at the whole-file
/// level; individual items are additionally guarded on decode.
struct OfflineWriteQueueEnvelope: Codable, Sendable, Equatable {
    static let currentVersion = 1
    var version: Int
    var items: [OfflineWriteItem]
}

// MARK: - Failable element decode

/// Decodes an element, capturing (not propagating) a per-element failure so a
/// single corrupt/unknown-kind item is skipped instead of failing the array.
private struct FailableItem: Decodable {
    let value: OfflineWriteItem?

    init(from decoder: Decoder) throws {
        let container = try decoder.singleValueContainer()
        value = try? container.decode(OfflineWriteItem.self)
    }
}

private struct ResilientEnvelope: Decodable {
    let version: Int?
    let items: [FailableItem]
}

// MARK: - File-backed store

/// File-backed durable store. Writes to `directory/offline-write-queue.json`
/// via a temp file + atomic rename, and quarantines an unreadable or
/// unknown-envelope-version file next to it before starting clean.
final class FileOfflineWriteQueueStore: OfflineWriteQueueStoring, @unchecked Sendable {
    static let fileName = "offline-write-queue.json"

    private let fileURL: URL
    private let directory: URL
    private let fileManager: FileManager
    private let lock = NSLock()

    /// - Parameter directory: the container directory (created on demand). In
    ///   production this is Application Support; tests pass a temp directory.
    init(directory: URL, fileManager: FileManager = .default) {
        self.directory = directory
        self.fileManager = fileManager
        self.fileURL = directory.appendingPathComponent(Self.fileName)
    }

    func loadAll() async -> [OfflineWriteItem] {
        lock.withLock { loadLocked() }
    }

    @discardableResult
    func saveAll(_ items: [OfflineWriteItem]) async -> Bool {
        lock.withLock { saveLocked(items) }
    }

    // MARK: Locked primitives

    private func loadLocked() -> [OfflineWriteItem] {
        guard let data = try? Data(contentsOf: fileURL), !data.isEmpty else {
            return []
        }
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        guard let envelope = try? decoder.decode(ResilientEnvelope.self, from: data) else {
            // Wholesale-unreadable: preserve the blob for recovery, start clean.
            quarantineLocked(data, reason: "decode")
            return []
        }
        if let version = envelope.version, version > OfflineWriteQueueEnvelope.currentVersion {
            // Unknown FUTURE schema — do not risk misinterpreting it.
            quarantineLocked(data, reason: "version-\(version)")
            return []
        }
        // Skip individually-undecodable items (unknown kind / malformed body).
        return envelope.items.compactMap { $0.value }
    }

    /// Returns `true` only when the item set is durably on disk. Any encode,
    /// temp-write, atomic-replacement, or move failure returns `false` (after
    /// best-effort temp cleanup) so the coordinator never reports an unsaved
    /// intent as enqueued.
    private func saveLocked(_ items: [OfflineWriteItem]) -> Bool {
        ensureDirectoryLocked()
        let envelope = OfflineWriteQueueEnvelope(
            version: OfflineWriteQueueEnvelope.currentVersion,
            items: items
        )
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        guard let data = try? encoder.encode(envelope) else { return false }
        let tempURL = directory.appendingPathComponent(".\(UUID().uuidString).tmp")
        do {
            try data.write(to: tempURL, options: .atomic)
            // Replace atomically so a crash mid-write never yields a torn file.
            if fileManager.fileExists(atPath: fileURL.path) {
                _ = try fileManager.replaceItemAt(fileURL, withItemAt: tempURL)
                if fileManager.fileExists(atPath: tempURL.path) {
                    try? fileManager.removeItem(at: tempURL)
                }
            } else {
                try fileManager.moveItem(at: tempURL, to: fileURL)
            }
            return true
        } catch {
            try? fileManager.removeItem(at: tempURL)
            return false
        }
    }

    private func quarantineLocked(_ data: Data, reason: String) {
        ensureDirectoryLocked()
        let quarantineURL = directory.appendingPathComponent(
            "offline-write-queue.corrupt.\(reason).\(UUID().uuidString).json"
        )
        try? data.write(to: quarantineURL, options: .atomic)
        try? fileManager.removeItem(at: fileURL)
    }

    private func ensureDirectoryLocked() {
        if !fileManager.fileExists(atPath: directory.path) {
            try? fileManager.createDirectory(at: directory, withIntermediateDirectories: true)
        }
    }
}

// MARK: - In-memory store

/// Non-durable store for pure-logic tests and previews. Seedable so a test can
/// simulate "items already on disk from a prior launch".
final class InMemoryOfflineWriteQueueStore: OfflineWriteQueueStoring, @unchecked Sendable {
    private let lock = NSLock()
    private var items: [OfflineWriteItem]
    private(set) var saveCount = 0

    init(seed: [OfflineWriteItem] = []) {
        self.items = seed
    }

    func loadAll() async -> [OfflineWriteItem] {
        lock.withLock { items }
    }

    @discardableResult
    func saveAll(_ items: [OfflineWriteItem]) async -> Bool {
        lock.withLock {
            self.items = items
            saveCount += 1
        }
        return true
    }

    /// Test helper: current durable contents without going through `async`.
    var persistedItems: [OfflineWriteItem] {
        lock.lock()
        defer { lock.unlock() }
        return items
    }
}
