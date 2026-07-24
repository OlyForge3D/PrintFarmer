import Foundation

// MARK: - Feature Read-Cache Store (F10-C2, #789)
//
// Typed read-cache adapters for the Attention (#779) and filament-coverage
// (#778) features are layered on the ALREADY-SHIPPED per-server/user cache
// foundation from #785. This file adds NO second cache engine and NO second
// namespace scheme: it reuses #785's exact primitives —
//
//   * `FarmSnapshotNamespace`  — the SAME (serverID, userID) identity pair.
//   * `FarmSnapshotSession` / `FarmSnapshotAuthority` — the SAME generation /
//     tombstone / activation authority, including `withPromotion` (the shared
//     per-domain coordinator lock), `isCurrent`, and `isTombstoned`.
//   * `FarmSnapshotFileIO` — the SAME suspendable read / candidate-write /
//     atomic-promote / compare-and-move filesystem seam.
//   * The SAME durable root URL. Feature records live UNDER the base server
//     directory, at `servers/{serverID}/features/{userID}/{recordKey}.json`, so
//     a server/user switch addresses a different directory (cannot read another
//     namespace), and #785's server-directory purge + tombstone already wipe and
//     fence these records for free.
//
// The `recordKey` never widens the namespace — the addressing identity is still
// exactly (serverID, userID); the key only disambiguates WHICH typed record
// (attention feed, fleet coverage, one printer's coverage) is stored under that
// identical namespace.
//
// Durable invariants preserved from #785:
//   * Only a strictly-newer (`lastUpdatedAtMillis`) complete record replaces the
//     live record; an older/equal success, error, or disabled completion cannot
//     overwrite newer state (monotonic, re-checked inside the promotion).
//   * The destructive promotion runs INSIDE `authority.withPromotion` so a
//     concurrent revoke / switch / tombstone has happens-before with the install.
//   * A corrupt / wrong-schema / wrong-namespace record is quarantined via an
//     authority-validated compare-and-move; the live path is left clear.

// MARK: Envelope

/// Versioned, self-describing at-rest record for one typed feature snapshot.
///
/// `kind` distinguishes a canonical `snapshot` (payload present) from a
/// feature-`disabled` tombstone (payload absent). A disabled tombstone is a
/// first-class record — NOT an empty success — so an older cached snapshot for
/// the same feature cannot resurface after the feature is gated off (criterion 7).
struct FeatureReadCacheEnvelope<Payload: Codable & Sendable & Equatable>: Codable, Sendable, Equatable {
    /// Bump only on an incompatible on-disk layout change. A record whose
    /// `schemaVersion` differs is treated as unreadable/quarantinable.
    static var currentSchemaVersion: Int { 1 }

    enum Kind: String, Codable, Sendable, Equatable {
        /// A complete, successful canonical response snapshot.
        case snapshot
        /// A canonical feature-disabled / gated-404 tombstone.
        case disabled
    }

    let schemaVersion: Int
    /// Stable per-feature record key (e.g. `attention-feed`, `coverage-fleet`).
    let featureKey: String
    let namespace: FarmSnapshotNamespace
    /// Immutable UTC instant (epoch-millis) of the successful/authoritative
    /// completion. The monotonic ordering key; integer-exact across restarts.
    let lastUpdatedAtMillis: Int64
    let kind: Kind
    /// Present iff `kind == .snapshot`.
    let payload: Payload?

    init(
        schemaVersion: Int = FeatureReadCacheEnvelope.currentSchemaVersion,
        featureKey: String,
        namespace: FarmSnapshotNamespace,
        lastUpdatedAtMillis: Int64,
        kind: Kind,
        payload: Payload?
    ) {
        self.schemaVersion = schemaVersion
        self.featureKey = featureKey
        self.namespace = namespace
        self.lastUpdatedAtMillis = lastUpdatedAtMillis
        self.kind = kind
        self.payload = payload
    }

    static func snapshot(
        featureKey: String,
        namespace: FarmSnapshotNamespace,
        lastUpdatedAtMillis: Int64,
        payload: Payload
    ) -> FeatureReadCacheEnvelope {
        FeatureReadCacheEnvelope(
            featureKey: featureKey,
            namespace: namespace,
            lastUpdatedAtMillis: lastUpdatedAtMillis,
            kind: .snapshot,
            payload: payload
        )
    }

    static func disabled(
        featureKey: String,
        namespace: FarmSnapshotNamespace,
        lastUpdatedAtMillis: Int64
    ) -> FeatureReadCacheEnvelope {
        FeatureReadCacheEnvelope(
            featureKey: featureKey,
            namespace: namespace,
            lastUpdatedAtMillis: lastUpdatedAtMillis,
            kind: .disabled,
            payload: nil
        )
    }

    var isSupportedSchema: Bool { schemaVersion == FeatureReadCacheEnvelope.currentSchemaVersion }

    /// A record is structurally valid only when a `snapshot` carries a payload and
    /// a `disabled` tombstone does not. Anything else is treated as corrupt.
    var isStructurallyValid: Bool {
        switch kind {
        case .snapshot: return payload != nil
        case .disabled: return payload == nil
        }
    }

    /// Deterministic, key-sorted encoder so byte-level compare-and-move is stable.
    static func makeEncoder() -> JSONEncoder {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.sortedKeys]
        return encoder
    }

    static func makeDecoder() -> JSONDecoder { JSONDecoder() }
}

// MARK: Result taxonomy

/// Outcome of hydrating one feature record for the active session.
enum FeatureReadCacheHydration<Payload: Sendable & Equatable>: Sendable, Equatable {
    /// No authoritative session, or authority was lost across a suspension.
    case inactive
    /// The session is authoritative but no record exists on disk yet.
    case absent
    /// A valid canonical snapshot was read.
    case snapshot(payload: Payload, lastUpdatedAtMillis: Int64)
    /// A valid feature-disabled tombstone was read.
    case disabled(lastUpdatedAtMillis: Int64)
    /// A corrupt / unknown-schema / wrong-namespace record was quarantined; the
    /// live path is now clear and online loading may proceed.
    case recovered
    /// The record could not be read (genuine I/O error) — never treated as absence.
    case unreadable
}

/// Outcome of committing one feature record. Mirrors `FarmSnapshotCommitResult`.
enum FeatureReadCacheCommitResult: Sendable, Equatable {
    case committed
    /// Not strictly newer than the durable record — preserved.
    case notNewer
    /// Authority changed (revoke / generation advance / tombstone / cancellation)
    /// before the durable promotion — prior bytes preserved.
    case superseded
    /// The candidate does not belong to the captured session's namespace.
    case namespaceMismatch
    /// The existing durable record could not be read/validated — fail closed.
    case integrityFailure
    /// A durable write/promotion failed; the live record still holds prior bytes.
    case persistenceFailure
}

// MARK: Store protocol

/// The `Sendable` interface the typed adapters consume. Kept minimal — the
/// authority / namespace / atomic-persistence internals stay owned by #785.
protocol FeatureReadCacheStoring: Sendable {
    func currentSession() async -> FarmSnapshotSession?

    func hydrate<Payload: Codable & Sendable & Equatable>(
        recordKey: String,
        as type: Payload.Type
    ) async -> FeatureReadCacheHydration<Payload>

    func commitSnapshot<Payload: Codable & Sendable & Equatable>(
        _ payload: Payload,
        recordKey: String,
        lastUpdatedAtMillis: Int64,
        capturedSession: FarmSnapshotSession
    ) async -> FeatureReadCacheCommitResult

    func commitDisabled(
        recordKey: String,
        lastUpdatedAtMillis: Int64,
        capturedSession: FarmSnapshotSession
    ) async -> FeatureReadCacheCommitResult
}

// MARK: Store

/// Actor that persists typed feature records by reusing #785's authority + file
/// IO + root. It is a thin typed LAYER over #785's primitives, not an
/// independent engine: every destructive step delegates to
/// `FarmSnapshotAuthority.withPromotion` (the shared coordinator lock) and every
/// filesystem op goes through `FarmSnapshotFileIO`.
actor FeatureReadCacheStore: FeatureReadCacheStoring {
    private let authority: FarmSnapshotAuthority
    private let fileIO: FarmSnapshotFileIO
    private let rootURL: URL

    init(
        authority: FarmSnapshotAuthority,
        fileIO: FarmSnapshotFileIO = DiskFarmSnapshotFileIO(),
        rootURL: URL = FarmSnapshotStore.defaultRootURL()
    ) {
        self.authority = authority
        self.fileIO = fileIO
        self.rootURL = rootURL
    }

    // MARK: Paths (SAME base server dir as #785 so purge/tombstone cover these)

    /// Identical to `FarmSnapshotStore.serverDir` so #785's recursive purge of a
    /// tombstoned server also removes every feature record beneath it.
    private func serverDir(_ serverID: UUID) -> URL {
        rootURL.appendingPathComponent("servers", isDirectory: true)
            .appendingPathComponent(serverID.uuidString, isDirectory: true)
    }

    private func featureDir(_ namespace: FarmSnapshotNamespace) -> URL {
        serverDir(namespace.serverID)
            .appendingPathComponent("features", isDirectory: true)
            .appendingPathComponent(namespace.userID.uuidString, isDirectory: true)
    }

    private func liveURL(_ namespace: FarmSnapshotNamespace, recordKey: String) -> URL {
        featureDir(namespace).appendingPathComponent("\(Self.sanitize(recordKey)).json")
    }

    private func candidateURL(_ namespace: FarmSnapshotNamespace, recordKey: String) -> URL {
        featureDir(namespace)
            .appendingPathComponent(".\(Self.sanitize(recordKey)).\(UUID().uuidString).tmp")
    }

    private func quarantineDir(_ namespace: FarmSnapshotNamespace) -> URL {
        serverDir(namespace.serverID)
            .appendingPathComponent("features-quarantine", isDirectory: true)
            .appendingPathComponent(namespace.userID.uuidString, isDirectory: true)
    }

    private func quarantineURL(_ namespace: FarmSnapshotNamespace, recordKey: String) -> URL {
        quarantineDir(namespace)
            .appendingPathComponent("\(Self.sanitize(recordKey)).\(UUID().uuidString).json")
    }

    /// Reduce a record key to a filesystem-safe token so a key can never escape
    /// its namespace directory. All shipped keys are already `[a-z0-9-]`.
    static func sanitize(_ key: String) -> String {
        let allowed = Set("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_")
        let mapped = key.map { allowed.contains($0) ? $0 : "-" }
        return String(mapped)
    }

    // MARK: Session

    func currentSession() async -> FarmSnapshotSession? {
        authority.currentSession()
    }

    // MARK: Hydrate

    func hydrate<Payload: Codable & Sendable & Equatable>(
        recordKey: String,
        as type: Payload.Type
    ) async -> FeatureReadCacheHydration<Payload> {
        guard let session = authority.currentSession() else { return .inactive }
        let live = liveURL(session.namespace, recordKey: recordKey)

        let data: Data?
        do {
            data = try await fileIO.readData(at: live)
        } catch {
            // A revoke/switch that landed during the read wins over an I/O error.
            guard authority.isCurrent(session) else { return .inactive }
            return .unreadable
        }

        guard authority.isCurrent(session) else { return .inactive }
        guard let data else { return .absent }

        if let decoded = try? FeatureReadCacheEnvelope<Payload>.makeDecoder()
            .decode(FeatureReadCacheEnvelope<Payload>.self, from: data),
           decoded.isSupportedSchema,
           decoded.isStructurallyValid,
           decoded.namespace == session.namespace,
           decoded.featureKey == Self.sanitize(recordKey) {
            switch decoded.kind {
            case .snapshot:
                if let payload = decoded.payload {
                    return .snapshot(payload: payload, lastUpdatedAtMillis: decoded.lastUpdatedAtMillis)
                }
                // isStructurallyValid guarantees payload != nil here; defensive.
                break
            case .disabled:
                return .disabled(lastUpdatedAtMillis: decoded.lastUpdatedAtMillis)
            }
        }

        // Corrupt / unknown-schema / wrong-namespace / wrong-key record → recover
        // via authority-validated, compare-and-move quarantine (mirrors #785).
        switch await recover(namespace: session.namespace, recordKey: recordKey, expected: data, session: session) {
        case .recovered: return .recovered
        case .changed, .revoked: return .inactive
        case .failed: return .unreadable
        }
    }

    private enum RecoverResult: Sendable { case recovered, changed, revoked, failed }

    private func recover(
        namespace: FarmSnapshotNamespace,
        recordKey: String,
        expected: Data,
        session: FarmSnapshotSession
    ) async -> RecoverResult {
        let live = liveURL(namespace, recordKey: recordKey)
        let dest = quarantineURL(namespace, recordKey: recordKey)
        do {
            try await fileIO.createDirectory(at: quarantineDir(namespace))
        } catch {
            return .failed
        }
        do {
            let moved: Bool? = try authority.withPromotion(session, cancelled: { Task.isCancelled }) {
                try self.fileIO.moveIfContentEquals(from: live, to: dest, expected: expected)
            }
            switch moved {
            case .none: return .revoked
            case .some(true): return .recovered
            case .some(false): return .changed
            }
        } catch {
            return .failed
        }
    }

    // MARK: Commit

    func commitSnapshot<Payload: Codable & Sendable & Equatable>(
        _ payload: Payload,
        recordKey: String,
        lastUpdatedAtMillis: Int64,
        capturedSession: FarmSnapshotSession
    ) async -> FeatureReadCacheCommitResult {
        let envelope = FeatureReadCacheEnvelope<Payload>.snapshot(
            featureKey: Self.sanitize(recordKey),
            namespace: capturedSession.namespace,
            lastUpdatedAtMillis: lastUpdatedAtMillis,
            payload: payload
        )
        return await commit(envelope, recordKey: recordKey, capturedSession: capturedSession)
    }

    func commitDisabled(
        recordKey: String,
        lastUpdatedAtMillis: Int64,
        capturedSession: FarmSnapshotSession
    ) async -> FeatureReadCacheCommitResult {
        // The disabled tombstone carries an empty payload type; encode against a
        // trivial payload so the same generic commit path applies.
        let envelope = FeatureReadCacheEnvelope<DisabledTombstonePayload>.disabled(
            featureKey: Self.sanitize(recordKey),
            namespace: capturedSession.namespace,
            lastUpdatedAtMillis: lastUpdatedAtMillis
        )
        return await commit(envelope, recordKey: recordKey, capturedSession: capturedSession)
    }

    /// Shared authority-validated atomic promotion, generic over payload. A copy
    /// of #785's `FarmSnapshotStore.commit` shape, so the durable guarantees are
    /// identical for feature records.
    private func commit<Payload: Codable & Sendable & Equatable>(
        _ envelope: FeatureReadCacheEnvelope<Payload>,
        recordKey: String,
        capturedSession: FarmSnapshotSession
    ) async -> FeatureReadCacheCommitResult {
        guard envelope.isSupportedSchema, envelope.isStructurallyValid else { return .persistenceFailure }
        guard envelope.namespace == capturedSession.namespace else { return .namespaceMismatch }
        guard authority.isCurrent(capturedSession) else { return .superseded }
        guard !authority.isTombstoned(capturedSession.serverID) else { return .superseded }

        let live = liveURL(capturedSession.namespace, recordKey: recordKey)

        // Early integrity + monotonic read (fail-closed on unreadable/corrupt).
        do {
            if let data = try await fileIO.readData(at: live) {
                guard let decoded = try? FeatureReadCacheEnvelope<Payload>.makeDecoder()
                    .decode(FeatureReadCacheEnvelope<Payload>.self, from: data),
                      decoded.isSupportedSchema,
                      decoded.namespace == capturedSession.namespace else {
                    return .integrityFailure
                }
                if decoded.lastUpdatedAtMillis >= envelope.lastUpdatedAtMillis {
                    return .notNewer
                }
            }
        } catch {
            return .integrityFailure
        }

        guard authority.isCurrent(capturedSession) else { return .superseded }

        guard let data = try? FeatureReadCacheEnvelope<Payload>.makeEncoder().encode(envelope) else {
            return .persistenceFailure
        }
        guard !authority.isTombstoned(capturedSession.serverID) else { return .superseded }

        let candidate = candidateURL(capturedSession.namespace, recordKey: recordKey)
        do {
            try await fileIO.writeCandidate(data, to: candidate)
        } catch {
            _ = await cleanup(candidate)
            return .persistenceFailure
        }

        let outcome: PromotionOutcome?
        do {
            outcome = try authority.withPromotion(capturedSession, cancelled: { Task.isCancelled }) {
                let liveData = try self.fileIO.readDataSync(at: live)
                if let liveData {
                    guard let decoded = try? FeatureReadCacheEnvelope<Payload>.makeDecoder()
                        .decode(FeatureReadCacheEnvelope<Payload>.self, from: liveData),
                          decoded.isSupportedSchema,
                          decoded.namespace == capturedSession.namespace else {
                        return .integrityFailure
                    }
                    if decoded.lastUpdatedAtMillis >= envelope.lastUpdatedAtMillis {
                        return .notNewer
                    }
                }
                try self.fileIO.promoteAtomically(candidate: candidate, to: live)
                return .promoted
            }
        } catch {
            _ = await cleanup(candidate)
            return .persistenceFailure
        }

        switch outcome {
        case .promoted:
            return .committed
        case .notNewer:
            _ = await cleanup(candidate)
            return .notNewer
        case .integrityFailure:
            _ = await cleanup(candidate)
            return .integrityFailure
        case nil:
            _ = await cleanup(candidate)
            return .superseded
        }
    }

    private enum PromotionOutcome: Sendable { case promoted, notNewer, integrityFailure }

    @discardableResult
    private func cleanup(_ url: URL) async -> Bool {
        do {
            try await fileIO.removeItem(at: url)
            return false
        } catch {
            return true
        }
    }
}

/// Empty payload used purely so a `disabled` tombstone encodes through the shared
/// generic commit path without inventing a feature-specific payload type.
struct DisabledTombstonePayload: Codable, Sendable, Equatable {}
