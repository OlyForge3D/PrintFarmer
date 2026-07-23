import Foundation

// MARK: - Farm Snapshot Session & Lifecycle Authority Contract (F10-C1a, #816)

/// The origin-pinned authority tuple for an active snapshot session.
///
/// A session is only ever minted from an authenticated identity together with
/// the exact server it was verified against and the active-server generation in
/// force at activation. `token` is a strictly-monotonic activation stamp so a
/// same-user/same-server relogin supersedes any in-flight pre-logout work.
///
/// The `namespace` binds `serverID` and `userID` together; the store addresses
/// records only by this pair, which is what makes a cross-server `(B, userA)`
/// binding impossible to represent.
struct FarmSnapshotSession: Sendable, Equatable {
    let namespace: FarmSnapshotNamespace
    let generation: Int
    let token: UInt64

    var serverID: UUID { namespace.serverID }
    var userID: UUID { namespace.userID }

    init(namespace: FarmSnapshotNamespace, generation: Int, token: UInt64) {
        self.namespace = namespace
        self.generation = generation
        self.token = token
    }

    init(serverID: UUID, userID: UUID, generation: Int, token: UInt64) {
        self.init(
            namespace: FarmSnapshotNamespace(serverID: serverID, userID: userID),
            generation: generation,
            token: token
        )
    }
}

/// Outcome taxonomy for a farm load attempt. `applicability` is a read-only
/// classification the C1b caller uses to decide whether a load result should
/// replace the cached snapshot or preserve the existing one.
enum FarmLoadOutcome: Sendable, Equatable {
    case success
    case offline
    case unauthorized
    case forbidden
    case serverError
    case decodeFailure
    case cancelled

    enum Applicability: Sendable, Equatable {
        /// A complete, trustworthy canonical response — safe to replace.
        case apply
        /// Anything partial/failed/stale — the newer good snapshot must survive.
        case preserve
    }

    var applicability: Applicability {
        switch self {
        case .success:
            return .apply
        case .offline, .unauthorized, .forbidden, .serverError, .decodeFailure, .cancelled:
            return .preserve
        }
    }
}

/// Result of hydrating the active session's record. `absent` (a present-but-empty
/// vs never-written distinction) is deliberately separate from `inactive` (no
/// authoritative session) and from `unreadable`/`recovered` integrity states.
enum FarmSnapshotHydration: Sendable, Equatable {
    /// No authoritative session, or the session was revoked/superseded across a
    /// suspension before the read could be trusted.
    case inactive
    /// The session is authoritative but no record exists on disk yet.
    case absent
    /// A valid record was read. A present-but-empty farm is `payload == []`.
    case snapshot(FarmSnapshotEnvelope)
    /// A corrupt / unknown-schema / wrong-namespace record was quarantined; the
    /// live path is now clear and online loading may proceed.
    case recovered
    /// The record could not be read (genuine I/O error) — never treated as absence.
    case unreadable
}

/// Result of committing a canonical snapshot.
enum FarmSnapshotCommitResult: Sendable, Equatable {
    case committed
    /// The candidate was not strictly newer than the durable record — preserved.
    /// `cleanupFailed` surfaces a secondary temp/candidate removal failure (H7).
    case notNewer(cleanupFailed: Bool)
    /// Authority changed (revoke / generation advance / tombstone / cancellation)
    /// before the durable promotion — prior bytes preserved, nothing written.
    case superseded
    /// The candidate does not belong to the active session's namespace.
    case namespaceMismatch
    /// The incoming envelope's schema is unsupported — rejected before any write.
    case schemaUnsupported
    /// The existing durable record could not be read/validated — fail closed,
    /// prior bytes untouched (never treated as absence). `cleanupFailed` surfaces a
    /// secondary temp/candidate removal failure (H7).
    case integrityFailure(cleanupFailed: Bool)
    /// A durable write/promotion failed. `cleanupFailed` surfaces a secondary
    /// temp/candidate cleanup failure without masking the primary result; the
    /// live record still holds the exact prior accepted bytes.
    case persistenceFailure(cleanupFailed: Bool)
}

/// Result of purging a server namespace.
enum FarmSnapshotPurgeResult: Sendable, Equatable {
    /// The namespace (and all of its temp/quarantine artifacts) is gone, or was
    /// already absent. The server is tombstoned so nothing can resurrect it.
    case purged
    /// One or more artifacts could not be enumerated/removed. `failureCount`
    /// surfaces how many; the tombstone remains durable so removal can be retried.
    case failed(failureCount: Int)
}

// MARK: - Published Store Protocol (stable contract for #817)

/// The stable, `Sendable` lifecycle-authority interface published for the C1b UI
/// child. #817 consumes this unchanged and must not alter persistence, namespace,
/// generation, purge, envelope, or authority internals.
protocol FarmSnapshotStoring: Sendable {
    /// Replays durable tombstones and sweeps residual namespaces left by a crash,
    /// independently of activation. Memoized (runs once on success) and retried on
    /// failure. Returns whether preparation fully succeeded (H4).
    @discardableResult
    func prepareStartup() async -> Bool

    /// Binds the store to a new authoritative session, returning whether the
    /// authority accepted it (a delayed/older token is rejected). Callers must honor
    /// a `false` result and not treat the session as bound (H3).
    @discardableResult
    func activate(session: FarmSnapshotSession) async -> Bool

    /// Conditionally clears ONLY the given session if it is still exactly current;
    /// a newer activation survives. Returns whether it cleared (H3).
    @discardableResult
    func deactivate(session: FarmSnapshotSession) async -> Bool

    /// The currently authoritative session, if any.
    func currentSession() async -> FarmSnapshotSession?

    /// Reads the active session's record, revalidating authority after every
    /// suspension. Distinguishes absent / present-empty / recovered / unreadable.
    func hydrateActive() async -> FarmSnapshotHydration

    /// Commits a canonical snapshot for `capturedSession`. Applies only if the
    /// captured session is still authoritative at the durable boundary and the
    /// record is strictly newer than what is on disk.
    func commit(_ envelope: FarmSnapshotEnvelope, capturedSession: FarmSnapshotSession) async -> FarmSnapshotCommitResult

    /// Purges an entire server namespace (base + quarantine + temp). Tombstones
    /// the server first so activation/commit cannot resurrect it mid-purge.
    func purge(serverID: UUID) async -> FarmSnapshotPurgeResult
}

extension FarmSnapshotStoring {
    /// Read-only convenience for #817: apply a canonical load outcome. A
    /// `.preserve` outcome (offline / auth failure / partial decode / cancel /
    /// server error) never touches the durable record.
    func apply(
        _ outcome: FarmLoadOutcome,
        envelope: FarmSnapshotEnvelope,
        capturedSession: FarmSnapshotSession
    ) async -> FarmSnapshotCommitResult {
        switch outcome.applicability {
        case .apply:
            return await commit(envelope, capturedSession: capturedSession)
        case .preserve:
            return .superseded
        }
    }
}
