import XCTest
@testable import PrintFarmer

/// VM-level integration coverage for the F10-C2 (#789) read-cache wiring into the
/// SHIPPED #779 Attention and #778 coverage view models. The adapter engine itself
/// is proven in `FeatureReadCacheTests`; these tests prove the two behaviours that
/// only exist once the adapters are wired into the real VMs:
///
///  * criterion 3 — offline hydration reconstructs the #779 feed (ordering + id
///    dedupe + healthy count + cursor) AND pagination/load-more is refused while the
///    feed is unconfirmed-stale (never presented as a complete live feed);
///  * criterion 8 — the FIRST canonical refresh on reconnect routes through the
///    real #779/#778 path EXACTLY ONCE, replaces the stale snapshot, and rewrites the
///    cache exactly once; the pre-seeded cursor is never consumed by an offline call.
///  * criterion 4 — offline coverage hydration preserves `unknown` HONESTLY.
///
/// Every wait is barrier/ACK driven (scripted services, explicit `mint`) — no sleeps,
/// no polling, no elapsed-time pass criteria.
@MainActor
final class FeatureReadCacheVMTests: XCTestCase {

    private func newRoot() -> URL {
        let root = FarmSnapshotFixtures.tempRoot()
        addTeardownBlock { try? FileManager.default.removeItem(at: root) }
        return root
    }

    private func makeStore() throws -> (FeatureReadCacheStore, FarmSnapshotAuthority, FarmSnapshotSession) {
        let root = newRoot()
        let authority = FarmSnapshotFixtures.makeAuthority(
            tombstoneDefaults: UserDefaults(suiteName: trackedSuiteName("vm-tomb"))!
        )
        let store = FeatureReadCacheStore(authority: authority, rootURL: root)
        let ns = FarmSnapshotFixtures.namespace()
        let session = try XCTUnwrap(try authority.mint(namespace: ns, generation: 0))
        return (store, authority, session)
    }

    // MARK: - Attention (criteria 3 + 8)

    func testAttentionOfflineHydrateRefusesLoadMoreThenReconnectReplacesOnce() async throws {
        let (store, _, session) = try makeStore()
        let adapter = AttentionReadCacheAdapter(store: store)

        // Pre-seed a canonical snapshot into the (serverID,userID) namespace with a
        // duplicate id (must be dropped, first-wins) and a non-nil cursor.
        let a = makeAttentionItem(id: "failure:a", severity: .critical, title: "A")
        let b = makeAttentionItem(id: "warn:b", severity: .warning, title: "B")
        let dupA = makeAttentionItem(id: "failure:a", title: "DUP-should-drop")
        let seed = await adapter.recordRefresh(
            items: [a, b, dupA],
            nextCursor: "cursor-1",
            healthyPrinterCount: 4,
            capturedSession: session
        )
        XCTAssertEqual(seed, .committed)

        // A service that, if load-more erroneously fired offline, would be consumed
        // here — polluting the reconnect assertion below. It stays untouched until
        // the single reconnect refresh.
        let fresh = makeAttentionFeed(
            items: [makeAttentionItem(id: "failure:c", title: "C")],
            nextCursor: nil,
            healthyPrinterCount: 9
        )
        let service = ScriptedAttentionService(steps: [.value(fresh)])

        let vm = AttentionFeedViewModel()
        vm.configure(
            attentionService: service,
            signalRService: MockSignalRService(),
            attentionEnabled: true
        )
        vm.configureCache(adapter)

        // --- Offline hydrate (criterion 3): honestly-stale feed on screen ---
        await vm.hydrateFromCache()
        XCTAssertTrue(vm.isShowingStaleCache, "hydrated cache must be flagged unconfirmed-stale")
        XCTAssertEqual(vm.snapshot?.items.map(\.id), ["failure:a", "warn:b"], "ordering + id dedupe preserved")
        XCTAssertEqual(vm.snapshot?.items.first?.title, "A", "first-wins dedupe keeps the original item")
        XCTAssertEqual(vm.snapshot?.healthyPrinterCount, 4)
        XCTAssertEqual(vm.snapshot?.nextCursor, "cursor-1", "cursor carried for fidelity, not for offline paging")
        let hydrateCalls = await service.loadCallCount
        XCTAssertEqual(hydrateCalls, 0, "hydration must not touch the canonical service")

        // --- Load-more refused offline (criterion 3): service untouched ---
        let more = await vm.loadMore()
        XCTAssertFalse(more, "load-more must be refused while showing unconfirmed cached data")
        let afterMore = await service.loadCallCount
        XCTAssertEqual(afterMore, 0, "offline load-more must NOT issue a canonical request")
        XCTAssertEqual(vm.snapshot?.nextCursor, "cursor-1", "refused load-more must not mutate the cursor")

        // --- Reconnect (criterion 8): exactly one canonical request + replace ---
        let ok = await vm.refresh()
        XCTAssertTrue(ok)
        XCTAssertFalse(vm.isShowingStaleCache, "a confirmed-live snapshot is no longer stale")
        XCTAssertEqual(vm.snapshot?.items.map(\.id), ["failure:c"], "stale snapshot replaced by canonical truth")
        XCTAssertEqual(vm.snapshot?.healthyPrinterCount, 9)
        let reconnectCalls = await service.loadCallCount
        XCTAssertEqual(reconnectCalls, 1, "reconnect routes through the canonical path EXACTLY once")

        // --- Cache rewritten exactly once with the fresh canonical snapshot ---
        let hydration = await adapter.loadCached()
        guard case let .snapshot(payload, _) = hydration else {
            return XCTFail("expected a fresh cached snapshot after reconnect, got \(hydration)")
        }
        XCTAssertEqual(payload.items.map(\.id), ["failure:c"], "reconnect success replaced the cached snapshot")
        XCTAssertEqual(payload.healthyPrinterCount, 9)
    }

    // MARK: - Stale banner reportability (cold-launch flash regression)

    /// Seeds a cached snapshot and returns a view model wired to `service`,
    /// with the cache hydrated but no canonical refresh performed yet.
    private func makeHydratedAttentionVM(
        service: ScriptedAttentionService
    ) async throws -> AttentionFeedViewModel {
        let (store, _, session) = try makeStore()
        let adapter = AttentionReadCacheAdapter(store: store)
        let seed = await adapter.recordRefresh(
            items: [makeAttentionItem(id: "failure:a", severity: .critical, title: "A")],
            nextCursor: nil,
            healthyPrinterCount: 1,
            capturedSession: session
        )
        XCTAssertEqual(seed, .committed)

        let vm = AttentionFeedViewModel()
        vm.configure(
            attentionService: service,
            signalRService: MockSignalRService(),
            attentionEnabled: true
        )
        vm.configureCache(adapter)
        await vm.hydrateFromCache()
        return vm
    }

    /// THE REGRESSION: between a cold-launch cache hydrate and the first
    /// canonical result, the feed is genuinely stale but we do not yet KNOW the
    /// backend is unreachable. Reporting "offline" here flashed a red banner on
    /// every healthy launch. The underlying `isShowingStaleCache` must keep its
    /// value, because it still refuses offline load-more (#789 criterion 3).
    func testStaleBannerIsNotReportableBeforeFirstCanonicalResult() async throws {
        let vm = try await makeHydratedAttentionVM(
            service: ScriptedAttentionService(steps: [])
        )

        XCTAssertTrue(vm.isShowingStaleCache, "hydrated cache is still unconfirmed-stale")
        XCTAssertFalse(
            vm.hasConcludedCanonicalRefresh,
            "no canonical refresh has concluded yet"
        )
        XCTAssertFalse(
            vm.isStaleCacheReportable,
            "the stale banner must stay hidden while the first canonical refresh is undecided"
        )
    }

    /// A healthy cold launch must never show the banner at all: the successful
    /// refresh clears staleness, so there is no instant at which both inputs are
    /// true.
    func testStaleBannerNeverBecomesReportableOnHealthyLaunch() async throws {
        let fresh = makeAttentionFeed(
            items: [makeAttentionItem(id: "failure:c", title: "C")],
            nextCursor: nil,
            healthyPrinterCount: 3
        )
        let vm = try await makeHydratedAttentionVM(
            service: ScriptedAttentionService(steps: [.value(fresh)])
        )
        XCTAssertFalse(vm.isStaleCacheReportable)

        let ok = await vm.refresh()
        XCTAssertTrue(ok)

        XCTAssertFalse(vm.isShowingStaleCache, "a confirmed-live snapshot is not stale")
        XCTAssertTrue(vm.hasConcludedCanonicalRefresh, "the refresh concluded")
        XCTAssertFalse(
            vm.isStaleCacheReportable,
            "a healthy launch must never report the stale banner"
        )
    }

    /// A genuinely unreachable backend must still surface the banner — the fix
    /// suppresses the startup flash, not the honest offline signal.
    func testStaleBannerBecomesReportableWhenFirstCanonicalRefreshFails() async throws {
        let vm = try await makeHydratedAttentionVM(
            service: ScriptedAttentionService(steps: [.failure(.network("offline"))])
        )
        XCTAssertFalse(vm.isStaleCacheReportable, "undecided before the attempt")

        let ok = await vm.refresh()
        XCTAssertFalse(ok, "the scripted failure must surface as a failed refresh")

        XCTAssertTrue(vm.isShowingStaleCache, "cached data is still on screen and unconfirmed")
        XCTAssertTrue(vm.hasConcludedCanonicalRefresh, "the attempt concluded, unsuccessfully")
        XCTAssertTrue(
            vm.isStaleCacheReportable,
            "a confirmed-unreachable backend must still show the stale banner"
        )
    }

    // MARK: - Fleet coverage (criteria 4 + 8)

    func testFleetCoverageOfflineHydratePreservesUnknownThenReconnectReplaces() async throws {
        let (store, _, session) = try makeStore()
        let adapter = FilamentCoverageReadCacheAdapter(store: store)

        let unknownPrinterID = UUID()
        let coversPrinterID = UUID()
        let staleToolhead = ToolheadFilamentCoverage(
            toolheadIndex: 0,
            toolheadId: UUID(),
            toolheadName: "T0",
            remainingGrams: nil,
            status: .unknown,
            predictedRunoutAt: nil,
            predictedRunoutLayer: nil
        )
        let stalePrinter = PrinterFilamentCoverage(
            printerId: unknownPrinterID,
            printerName: "Unknowable",
            status: .unknown,
            toolheads: [staleToolhead],
            activeJobId: nil,
            activeJobName: nil,
            activeJobProgress: nil,
            earliestPredictedRunoutAt: nil,
            assignedQueuedJobCount: 0,
            evaluatedAtUtc: Date(timeIntervalSince1970: 5_000)
        )
        let staleFleet = FleetFilamentCoverage(printers: [stalePrinter], evaluatedAtUtc: Date(timeIntervalSince1970: 5_000))
        let seed = await adapter.recordFleet(staleFleet, capturedSession: session)
        XCTAssertEqual(seed, .committed)

        let service = ControlledFilamentCoverageService()
        let vm = FarmFilamentCoverageViewModel()
        vm.configure(coverageService: service)
        vm.configureCache(adapter)

        // --- Offline hydrate (criterion 4): unknown preserved honestly ---
        await vm.hydrateFromCache()
        XCTAssertTrue(vm.isShowingStaleCache)
        XCTAssertFalse(vm.isFeatureDisabled)
        XCTAssertEqual(vm.coverageByPrinter.count, 1)
        XCTAssertEqual(vm.coverageByPrinter[unknownPrinterID]?.status, .unknown, "unknown must survive the cache round-trip")
        XCTAssertNil(vm.coverageByPrinter[unknownPrinterID]?.toolheads.first?.remainingGrams)

        // --- Reconnect (criterion 8): canonical fleet replaces stale coverage ---
        let livePrinter = PrinterFilamentCoverage(
            printerId: coversPrinterID,
            printerName: "Fresh",
            status: .covers,
            toolheads: [],
            activeJobId: nil,
            activeJobName: nil,
            activeJobProgress: nil,
            earliestPredictedRunoutAt: nil,
            assignedQueuedJobCount: 0,
            evaluatedAtUtc: Date(timeIntervalSince1970: 6_000)
        )
        let liveFleet = FleetFilamentCoverage(printers: [livePrinter], evaluatedAtUtc: Date(timeIntervalSince1970: 6_000))
        async let load: Void = vm.load()
        await service.awaitPending(count: 1)
        await service.completeSuccess(index: 0, fleet: liveFleet)
        _ = await load

        XCTAssertFalse(vm.isShowingStaleCache, "a confirmed-live fleet is no longer stale")
        XCTAssertNil(vm.coverageByPrinter[unknownPrinterID], "stale printer must be gone after canonical replacement")
        XCTAssertEqual(vm.coverageByPrinter[coversPrinterID]?.status, .covers)

        // --- Cache now holds the canonical fleet ---
        let hydration = await adapter.loadCachedFleet()
        guard case let .snapshot(fleet, _) = hydration else {
            return XCTFail("expected fresh cached fleet after reconnect, got \(hydration)")
        }
        XCTAssertEqual(fleet.printers.map(\.printerId), [coversPrinterID])
    }
}
