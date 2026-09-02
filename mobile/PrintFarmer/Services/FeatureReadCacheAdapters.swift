import Foundation

// MARK: - Feature Read-Cache Adapters (F10-C2, #789)
//
// Typed facades over `FeatureReadCacheStore`. Each adapter owns ONE feature's
// record key(s) and the projection between the canonical #779/#778 view-model
// state and the durable envelope. The adapters hold NO cache mechanics of their
// own — atomic persistence, namespace isolation, monotonic ordering, tombstones,
// and quarantine all live in the shared store (which reuses #785).

// MARK: Attention

/// Codable projection of a single successful Attention canonical refresh — the
/// visible, ordered, de-duplicated items plus the page-independent healthy count
/// and the cursor state needed to render the snapshot. Pagination/load-more is
/// disabled offline, so the cursor is carried only for fidelity, never to fetch.
struct AttentionCacheSnapshot: Codable, Sendable, Equatable {
    let items: [AttentionItem]
    let nextCursor: String?
    let healthyPrinterCount: Int

    init(items: [AttentionItem], nextCursor: String?, healthyPrinterCount: Int) {
        self.items = items
        self.nextCursor = nextCursor
        self.healthyPrinterCount = healthyPrinterCount
    }
}

/// Read-cache adapter for the Attention feed (#779).
final class AttentionReadCacheAdapter: Sendable {
    static let recordKey = "attention-feed"

    private let store: any FeatureReadCacheStoring
    private let now: @Sendable () -> Date

    init(store: any FeatureReadCacheStoring, now: @escaping @Sendable () -> Date = { Date() }) {
        self.store = store
        self.now = now
    }

    func currentSession() async -> FarmSnapshotSession? {
        await store.currentSession()
    }

    /// Hydrate the active namespace's cached Attention snapshot (or tombstone).
    func loadCached() async -> FeatureReadCacheHydration<AttentionCacheSnapshot> {
        await store.hydrate(recordKey: Self.recordKey, as: AttentionCacheSnapshot.self)
    }

    /// Persist a successful canonical refresh snapshot. Only the caller's
    /// full-refresh success path invokes this — partial-page appends, failures,
    /// and cancellations never do (criterion 2). The snapshot is de-duplicated by
    /// stable id, preserving #779 server ordering (criterion 3).
    @discardableResult
    func recordRefresh(
        items: [AttentionItem],
        nextCursor: String?,
        healthyPrinterCount: Int,
        lastUpdatedAtMillis: Int64? = nil,
        capturedSession: FarmSnapshotSession
    ) async -> FeatureReadCacheCommitResult {
        var seen: Set<String> = []
        var ordered: [AttentionItem] = []
        ordered.reserveCapacity(items.count)
        for item in items where seen.insert(item.id).inserted {
            ordered.append(item)
        }
        let payload = AttentionCacheSnapshot(
            items: ordered,
            nextCursor: nextCursor,
            healthyPrinterCount: healthyPrinterCount
        )
        return await store.commitSnapshot(
            payload,
            recordKey: Self.recordKey,
            lastUpdatedAtMillis: lastUpdatedAtMillis ?? Self.millis(now()),
            capturedSession: capturedSession
        )
    }

    /// Record a canonical feature-disabled tombstone (criterion 7).
    @discardableResult
    func recordDisabled(capturedSession: FarmSnapshotSession) async -> FeatureReadCacheCommitResult {
        await store.commitDisabled(
            recordKey: Self.recordKey,
            lastUpdatedAtMillis: Self.millis(now()),
            capturedSession: capturedSession
        )
    }

    private static func millis(_ date: Date) -> Int64 {
        Int64((date.timeIntervalSince1970 * 1000).rounded())
    }
}

// MARK: Filament coverage

/// Read-cache adapter for filament coverage (#778): one fleet record plus a
/// stable-id per-printer detail record. `unknown` coverage is preserved honestly
/// because the canonical DTOs are stored verbatim. SignalR `filamentcoveragechanged`
/// events are invalidation-only and are NEVER written here (criterion 4).
final class FilamentCoverageReadCacheAdapter: Sendable {
    static let fleetRecordKey = "coverage-fleet"

    private let store: any FeatureReadCacheStoring
    private let now: @Sendable () -> Date

    init(store: any FeatureReadCacheStoring, now: @escaping @Sendable () -> Date = { Date() }) {
        self.store = store
        self.now = now
    }

    static func printerRecordKey(_ id: UUID) -> String {
        "coverage-printer-\(id.uuidString)"
    }

    func currentSession() async -> FarmSnapshotSession? {
        await store.currentSession()
    }

    // Fleet ------------------------------------------------------------------

    func loadCachedFleet() async -> FeatureReadCacheHydration<FleetFilamentCoverage> {
        await store.hydrate(recordKey: Self.fleetRecordKey, as: FleetFilamentCoverage.self)
    }

    @discardableResult
    func recordFleet(
        _ fleet: FleetFilamentCoverage,
        lastUpdatedAtMillis: Int64? = nil,
        capturedSession: FarmSnapshotSession
    ) async -> FeatureReadCacheCommitResult {
        await store.commitSnapshot(
            fleet,
            recordKey: Self.fleetRecordKey,
            lastUpdatedAtMillis: lastUpdatedAtMillis ?? Self.millis(now()),
            capturedSession: capturedSession
        )
    }

    @discardableResult
    func recordFleetDisabled(capturedSession: FarmSnapshotSession) async -> FeatureReadCacheCommitResult {
        await store.commitDisabled(
            recordKey: Self.fleetRecordKey,
            lastUpdatedAtMillis: Self.millis(now()),
            capturedSession: capturedSession
        )
    }

    // Per-printer detail -----------------------------------------------------

    func loadCachedPrinter(id: UUID) async -> FeatureReadCacheHydration<PrinterFilamentCoverage> {
        await store.hydrate(recordKey: Self.printerRecordKey(id), as: PrinterFilamentCoverage.self)
    }

    @discardableResult
    func recordPrinter(
        _ coverage: PrinterFilamentCoverage,
        capturedSession: FarmSnapshotSession
    ) async -> FeatureReadCacheCommitResult {
        await store.commitSnapshot(
            coverage,
            recordKey: Self.printerRecordKey(coverage.printerId),
            lastUpdatedAtMillis: Self.millis(now()),
            capturedSession: capturedSession
        )
    }

    @discardableResult
    func recordPrinterDisabled(
        id: UUID,
        capturedSession: FarmSnapshotSession
    ) async -> FeatureReadCacheCommitResult {
        await store.commitDisabled(
            recordKey: Self.printerRecordKey(id),
            lastUpdatedAtMillis: Self.millis(now()),
            capturedSession: capturedSession
        )
    }

    private static func millis(_ date: Date) -> Int64 {
        Int64((date.timeIntervalSince1970 * 1000).rounded())
    }
}
