import Foundation
import os

// MARK: - Feature Read-Cache Adapters (F10-C2, #789)
//
// Typed facades over `FeatureReadCacheStore`. Each adapter owns ONE feature's
// record key(s) and the projection between the canonical #779/#778 view-model
// state and the durable envelope. The adapters hold NO cache mechanics of their
// own — atomic persistence, namespace isolation, monotonic ordering, tombstones,
// and quarantine all live in the shared store (which reuses #785).
//
// Every `record*` method takes a `writeOrder` (#3007). Its default,
// `FeatureReadCacheWriteOrder.next()`, is evaluated at the CALL SITE, i.e. on the
// caller's actor before the call suspends, so a caller that records immediately
// after confirming a result stamps it in confirmation order. Every outcome is
// passed to `reportCommit`, so a refused confirmed-live write is never silent.

// MARK: Commit reporting

/// Default sink for feature read-cache commit outcomes (#3007). `.committed` is
/// the normal path; every other outcome means the durable record did NOT take
/// this confirmed-live write and is logged so it is diagnosable.
enum FeatureReadCacheCommitLog {
    private static let logger = Logger(subsystem: "com.printfarmer.ios", category: "FeatureReadCache")

    @Sendable
    static func report(recordKey: String, result: FeatureReadCacheCommitResult) {
        switch result {
        case .committed:
            return
        case .notNewer:
            logger.notice("Feature cache write for \(recordKey, privacy: .public) refused as notNewer; a later-confirmed write already holds the record")
        case .superseded:
            logger.info("Feature cache write for \(recordKey, privacy: .public) superseded by a session change")
        case .namespaceMismatch, .integrityFailure, .persistenceFailure:
            logger.error("Feature cache write for \(recordKey, privacy: .public) failed: \(String(describing: result), privacy: .public)")
        }
    }
}

typealias FeatureReadCacheCommitReporter = @Sendable (_ recordKey: String, _ result: FeatureReadCacheCommitResult) -> Void

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
    private let reportCommit: FeatureReadCacheCommitReporter

    init(
        store: any FeatureReadCacheStoring,
        now: @escaping @Sendable () -> Date = { Date() },
        reportCommit: @escaping FeatureReadCacheCommitReporter = FeatureReadCacheCommitLog.report
    ) {
        self.store = store
        self.now = now
        self.reportCommit = reportCommit
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
        writeOrder: FeatureReadCacheWriteOrder = .next(),
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
        let result = await store.commitSnapshot(
            payload,
            recordKey: Self.recordKey,
            lastUpdatedAtMillis: lastUpdatedAtMillis ?? Self.millis(now()),
            writeOrder: writeOrder,
            capturedSession: capturedSession
        )
        reportCommit(Self.recordKey, result)
        return result
    }

    /// Record a canonical feature-disabled tombstone (criterion 7).
    @discardableResult
    func recordDisabled(
        writeOrder: FeatureReadCacheWriteOrder = .next(),
        capturedSession: FarmSnapshotSession
    ) async -> FeatureReadCacheCommitResult {
        let result = await store.commitDisabled(
            recordKey: Self.recordKey,
            lastUpdatedAtMillis: Self.millis(now()),
            writeOrder: writeOrder,
            capturedSession: capturedSession
        )
        reportCommit(Self.recordKey, result)
        return result
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
    private let reportCommit: FeatureReadCacheCommitReporter

    init(
        store: any FeatureReadCacheStoring,
        now: @escaping @Sendable () -> Date = { Date() },
        reportCommit: @escaping FeatureReadCacheCommitReporter = FeatureReadCacheCommitLog.report
    ) {
        self.store = store
        self.now = now
        self.reportCommit = reportCommit
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
        writeOrder: FeatureReadCacheWriteOrder = .next(),
        capturedSession: FarmSnapshotSession
    ) async -> FeatureReadCacheCommitResult {
        let result = await store.commitSnapshot(
            fleet,
            recordKey: Self.fleetRecordKey,
            lastUpdatedAtMillis: lastUpdatedAtMillis ?? Self.millis(now()),
            writeOrder: writeOrder,
            capturedSession: capturedSession
        )
        reportCommit(Self.fleetRecordKey, result)
        return result
    }

    @discardableResult
    func recordFleetDisabled(
        writeOrder: FeatureReadCacheWriteOrder = .next(),
        capturedSession: FarmSnapshotSession
    ) async -> FeatureReadCacheCommitResult {
        let result = await store.commitDisabled(
            recordKey: Self.fleetRecordKey,
            lastUpdatedAtMillis: Self.millis(now()),
            writeOrder: writeOrder,
            capturedSession: capturedSession
        )
        reportCommit(Self.fleetRecordKey, result)
        return result
    }

    // Per-printer detail -----------------------------------------------------

    func loadCachedPrinter(id: UUID) async -> FeatureReadCacheHydration<PrinterFilamentCoverage> {
        await store.hydrate(recordKey: Self.printerRecordKey(id), as: PrinterFilamentCoverage.self)
    }

    @discardableResult
    func recordPrinter(
        _ coverage: PrinterFilamentCoverage,
        writeOrder: FeatureReadCacheWriteOrder = .next(),
        capturedSession: FarmSnapshotSession
    ) async -> FeatureReadCacheCommitResult {
        let recordKey = Self.printerRecordKey(coverage.printerId)
        let result = await store.commitSnapshot(
            coverage,
            recordKey: recordKey,
            lastUpdatedAtMillis: Self.millis(now()),
            writeOrder: writeOrder,
            capturedSession: capturedSession
        )
        reportCommit(recordKey, result)
        return result
    }

    @discardableResult
    func recordPrinterDisabled(
        id: UUID,
        writeOrder: FeatureReadCacheWriteOrder = .next(),
        capturedSession: FarmSnapshotSession
    ) async -> FeatureReadCacheCommitResult {
        let recordKey = Self.printerRecordKey(id)
        let result = await store.commitDisabled(
            recordKey: recordKey,
            lastUpdatedAtMillis: Self.millis(now()),
            writeOrder: writeOrder,
            capturedSession: capturedSession
        )
        reportCommit(recordKey, result)
        return result
    }

    private static func millis(_ date: Date) -> Int64 {
        Int64((date.timeIntervalSince1970 * 1000).rounded())
    }
}
