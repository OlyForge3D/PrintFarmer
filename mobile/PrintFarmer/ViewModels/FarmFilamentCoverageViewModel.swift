import Foundation

// MARK: - Farm Filament Coverage View Model (F4-M / issue #778)
//
// Owns the fleet-wide coverage cache displayed on the Farm/list screen.
//
// THREE independent authorities gate every state change:
//
//   1. **Request-generation authority** (per-load-in-flight).
//      A monotonic `requestGeneration` counter is bumped on each
//      dispatched fetch. Every commit path (success/disabled/error)
//      refuses to write if a newer generation already committed.
//      Equal-`evaluatedAtUtc` snapshots resolve by generation.
//
//   2. **Coverage authority** (per-coverage-service-owner lifetime).
//      A monotonic `coverageAuthorityEpoch` counter is bumped ONLY by
//      `configure(coverageService:)`. Every `load()` captures the
//      current coverage epoch and refuses to (a) bump
//      `requestGeneration` under a stale epoch, or (b) commit after
//      an awaited network call under a stale epoch. This is what
//      prevents an OLD coverage service's in-flight GET from
//      overwriting the replacement owner's snapshot / tombstone /
//      error.
//
//   3. **SignalR-registration authority** (per-configureSignalR
//      / teardown lifetime).
//      A monotonic `signalRAuthorityEpoch` counter is bumped ONLY by
//      `configureSignalR(_:)` and `tearDownSignalR()`. Every SignalR
//      callback captures the epoch at REGISTRATION time and drops
//      silently on drain if the epoch moved on. This is what makes a
//      queued-but-not-drained OLD-subscription callback no-op after
//      re-configure / teardown.
//
// The two owner epochs are DELIBERATELY DECOUPLED (reviewer Hicks,
// cycle-3 round-2 finding):
//
//   * Replacing the coverage service (A → B) does NOT bump the
//     SignalR epoch. A SignalR callback that was queued before the
//     coverage replace, or fires afterwards from a still-current
//     subscription, remains valid and dispatches exactly one load
//     against the CURRENT coverage service (B). The load then
//     commits under B's coverage authority.
//   * Replacing the SignalR service (S1 → S2) or tearing it down
//     does NOT bump the coverage epoch. Any in-flight coverage GET
//     dispatched under the current coverage owner remains free to
//     commit if the coverage epoch is still current.
//
// SignalR wiring (reuses the shared #777 lifecycle):
//   * `filamentcoveragechanged` is treated as an INVALIDATION HINT
//     only. On every valid event we dispatch exactly ONE refetch of
//     `/api/printers/filament-coverage`.
//   * Cold-connect classification:
//       - Subscription's initial state is `.connected` or
//         `.reconnecting` ⇒ pre-seed the classifier as if we've
//         already seen a `.connected`. Any subsequent `.connected`
//         (including the recovery that follows a `.reconnecting` we
//         were configured under) IS recovery and triggers exactly
//         one refetch.
//       - Initial state `.disconnected` or `.connecting` ⇒ leave
//         unseeded so the first `.connected` we observe is the cold
//         initial connect and does NOT double-load with the view's
//         `.task { load() }` call.
//   * `configureSignalR` cancels prior subscription tokens BEFORE
//     re-registering, so repeat configuration cannot stack handlers.

@MainActor @Observable
final class FarmFilamentCoverageViewModel {

    private(set) var coverageByPrinter: [UUID: PrinterFilamentCoverage] = [:]
    private(set) var isFeatureDisabled: Bool = false
    private(set) var lastLoadError: String?

    private var coverageService: (any FilamentCoverageServiceProtocol)?
    private var signalRService: (any SignalRServiceProtocol)?

    // Request-generation authority.
    private var requestGeneration: UInt64 = 0
    private var lastCommittedGeneration: UInt64 = 0
    private var lastCommittedEvaluatedAt: Date?

    // Split owner epochs (see file header for the decoupling
    // rationale).
    private var coverageAuthorityEpoch: UInt64 = 0
    private var signalRAuthorityEpoch: UInt64 = 0

    @ObservationIgnored private var invalidationSubscription: SignalRSubscription?
    @ObservationIgnored private var connectionStateSubscription: SignalRSubscription?
    @ObservationIgnored private var hasSeenAnyConnected: Bool = false

    // MARK: Read-cache (issue #789, F10-C2) — additive, optional-guarded.
    /// Typed fleet read-cache facade. `nil` in every pre-#789 test, so all
    /// cache hooks below are skipped and behaviour is byte-for-byte unchanged.
    @ObservationIgnored private var coverageCache: FilamentCoverageReadCacheAdapter?
    /// True exactly while the on-screen fleet is UNCONFIRMED cached data (offline
    /// hydrate not yet replaced by a canonical response). Drives the stale banner.
    private(set) var isShowingStaleCache: Bool = false
    /// Epoch-millis of the cached fleet's originating canonical completion.
    private(set) var cacheLastUpdatedAtMillis: Int64?
    /// The cached fleet's last-updated instant, for the shared stale banner.
    var cacheLastUpdatedAt: Date? {
        cacheLastUpdatedAtMillis.map { Date(timeIntervalSince1970: Double($0) / 1000.0) }
    }

    func coverage(for printerId: UUID) -> PrinterFilamentCoverage? {
        coverageByPrinter[printerId]
    }

    /// Replace the coverage service. Bumps ONLY
    /// `coverageAuthorityEpoch`, so an in-flight load from the
    /// previous coverage owner cannot commit against the replacement.
    /// SignalR subscriptions are UNAFFECTED — any queued or future
    /// callback from the current SignalR registration will fire
    /// against the new coverage service.
    func configure(coverageService: any FilamentCoverageServiceProtocol) {
        self.coverageService = coverageService
        coverageAuthorityEpoch &+= 1
    }

    func disableForCapabilityGate() {
        coverageAuthorityEpoch &+= 1
        coverageService = nil
        tearDownSignalR()
        coverageByPrinter = [:]
        isFeatureDisabled = true
        lastLoadError = nil
        isShowingStaleCache = false
        cacheLastUpdatedAtMillis = nil
    }

    /// Wire the #789 fleet read-cache. Additive: when never called the view model
    /// behaves exactly as it did pre-#789. Safe to call more than once.
    func configureCache(_ cache: FilamentCoverageReadCacheAdapter) {
        self.coverageCache = cache
    }

    /// Hydrate the active namespace's cached fleet BEFORE the first canonical
    /// load, so a cold/offline launch shows honestly-stale coverage immediately.
    /// Never downgrades confirmed-live data (no-op once any canonical commit has
    /// landed), and honours a cached feature-disabled tombstone (criterion 7).
    func hydrateFromCache() async {
        guard let cache = coverageCache else { return }
        guard lastCommittedGeneration == 0, coverageByPrinter.isEmpty, !isFeatureDisabled else { return }
        let hydration = await cache.loadCachedFleet()
        // A canonical load may have committed during the await; never override it.
        guard lastCommittedGeneration == 0 else { return }
        switch hydration {
        case .snapshot(let fleet, let millis):
            coverageByPrinter = Dictionary(uniqueKeysWithValues:
                fleet.printers.map { ($0.printerId, $0) }
            )
            isFeatureDisabled = false
            lastLoadError = nil
            isShowingStaleCache = true
            cacheLastUpdatedAtMillis = millis
        case .disabled(let millis):
            // A gated feature must not resurface older cached coverage.
            coverageByPrinter = [:]
            isFeatureDisabled = true
            lastLoadError = nil
            isShowingStaleCache = true
            cacheLastUpdatedAtMillis = millis
        case .inactive, .absent, .recovered, .unreadable:
            break
        }
    }

    /// Register `filamentcoveragechanged` + connection-state
    /// subscriptions. Idempotent per instance: prior tokens are
    /// cancelled BEFORE new registrations happen. Bumps ONLY
    /// `signalRAuthorityEpoch`, so any queued callback from an
    /// older subscription drops silently on drain; the coverage
    /// owner is UNAFFECTED.
    func configureSignalR(_ service: any SignalRServiceProtocol) {
        self.signalRService = service

        invalidationSubscription?.cancel()
        connectionStateSubscription?.cancel()
        signalRAuthorityEpoch &+= 1
        let mySigEpoch = signalRAuthorityEpoch
        // Cold-connect classification is re-seeded from the current
        // hub state each configure — see `seedColdConnectClassification`.
        hasSeenAnyConnected = false

        invalidationSubscription = service.onFilamentCoverageChanged { [weak self] _ in
            Task { @MainActor [weak self] in
                guard let self else { return }
                await self.deliverInvalidationCallback(underSignalREpoch: mySigEpoch)
            }
        }

        let (initial, subscription) = service.onConnectionStateChanged { [weak self] newState in
            Task { @MainActor [weak self] in
                guard let self else { return }
                await self.deliverConnectionStateCallback(newState, underSignalREpoch: mySigEpoch)
            }
        }
        connectionStateSubscription = subscription
        seedColdConnectClassification(fromInitialState: initial)
    }

    /// Cancel every SignalR subscription and bump the SignalR epoch
    /// so any callback still queued for delivery no-ops when it
    /// drains. Coverage authority is UNAFFECTED, so an in-flight
    /// coverage load (dispatched directly, not via a callback) can
    /// still commit if its coverage epoch is current.
    func tearDownSignalR() {
        invalidationSubscription?.cancel()
        invalidationSubscription = nil
        connectionStateSubscription?.cancel()
        connectionStateSubscription = nil
        signalRAuthorityEpoch &+= 1
        hasSeenAnyConnected = false
    }

    /// Public entry: dispatch a fresh fleet-coverage fetch under the
    /// current coverage authority epoch.
    func load() async {
        await load(underCoverageEpoch: coverageAuthorityEpoch)
    }

    /// Consume the readiness gate's canonical fleet once, or preserve the
    /// existing hydrate-then-load behavior when no current-authority value exists.
    func bootstrap(startupPrefetchStore: StartupPrefetchStore?) async {
        if let startupPrefetchStore {
            var prefetched: StartupPrefetchValue<FleetFilamentCoverage>?
            startupPrefetchStore.consumeFilamentCoverage { entry in
                requestGeneration &+= 1
                commitSuccess(fleet: entry.value, generation: requestGeneration)
                isShowingStaleCache = false
                cacheLastUpdatedAtMillis = entry.lastUpdatedAtMillis
                prefetched = entry
            }
            if let prefetched {
                if let cache = coverageCache {
                    _ = await cache.recordFleet(
                        prefetched.value,
                        lastUpdatedAtMillis: prefetched.lastUpdatedAtMillis,
                        capturedSession: prefetched.session
                    )
                }
                return
            }
        }
        await hydrateFromCache()
        await load()
    }

    // MARK: - Test seams (DEBUG-only, deterministic — no polling)

    #if DEBUG
    private var commitWaiters: [(target: UInt64, cont: CheckedContinuation<Void, Never>)] = []
    /// Park until `lastCommittedGeneration >= target`. Resumed inside
    /// each `commit*` path.
    func waitForCommittedGeneration(atLeast target: UInt64) async {
        if lastCommittedGeneration >= target { return }
        await withCheckedContinuation { (cont: CheckedContinuation<Void, Never>) in
            commitWaiters.append((target, cont))
        }
    }
    private func resumeMatchingCommitWaiters() {
        let current = lastCommittedGeneration
        var remaining: [(target: UInt64, cont: CheckedContinuation<Void, Never>)] = []
        for w in commitWaiters {
            if current >= w.target { w.cont.resume() } else { remaining.append(w) }
        }
        commitWaiters = remaining
    }

    /// Monotonic count of callback bodies that have run to completion
    /// on the MainActor. Advanced by every invalidation and every
    /// connection-state callback, on both the filter/skip and the
    /// dispatch paths (the tick fires after the dispatched load's
    /// commit, so `waitForCallbackTick(atLeast: N)` also guarantees
    /// any positive dispatch has fully processed).
    ///
    /// Tests use this to prove ABSENCE of a dispatched refetch: after
    /// `waitForCallbackTick` returns, every in-flight callback has
    /// either dispatched (and its load committed) or was filtered.
    /// The resulting `dispatchedRequestCount` is authoritative.
    private var callbackTick: UInt64 = 0
    private var callbackTickWaiters: [(target: UInt64, cont: CheckedContinuation<Void, Never>)] = []
    func waitForCallbackTick(atLeast target: UInt64) async {
        if callbackTick >= target { return }
        await withCheckedContinuation { (cont: CheckedContinuation<Void, Never>) in
            callbackTickWaiters.append((target, cont))
        }
    }
    private func advanceCallbackTick() {
        callbackTick &+= 1
        let current = callbackTick
        var remaining: [(target: UInt64, cont: CheckedContinuation<Void, Never>)] = []
        for w in callbackTickWaiters {
            if current >= w.target { w.cont.resume() } else { remaining.append(w) }
        }
        callbackTickWaiters = remaining
    }

    var coverageAuthorityEpochForTesting: UInt64 { coverageAuthorityEpoch }
    var signalRAuthorityEpochForTesting: UInt64 { signalRAuthorityEpoch }
    var callbackTickForTesting: UInt64 { callbackTick }
    var hasSeenAnyConnectedForTesting: Bool { hasSeenAnyConnected }
    #else
    private func advanceCallbackTick() {}
    #endif

    var dispatchedRequestCount: UInt64 { requestGeneration }
    var lastCommittedGenerationForTesting: UInt64 { lastCommittedGeneration }

    // MARK: - Internal

    private func load(underCoverageEpoch cvEpoch: UInt64) async {
        // Coverage-authority gate #1: refuse to start under a stale
        // coverage epoch or when the service is unset. Capture the
        // service reference locally so a mid-load
        // `configure(coverageService:)` cannot swap it out from
        // under us.
        guard cvEpoch == coverageAuthorityEpoch, let coverageService else { return }

        // Capture the cache session BEFORE the network round-trip (mirrors the
        // #817 discipline) so a mid-flight server/user switch cannot make this
        // write land in the new namespace. `nil` when no cache is wired.
        let capturedCacheSession = await coverageCache?.currentSession()
        guard !Task.isCancelled, cvEpoch == coverageAuthorityEpoch else { return }

        requestGeneration &+= 1
        let myGen = requestGeneration
        do {
            let fleet = try await coverageService.getForFleet()
            // Coverage-authority gate #2: refuse to commit if the
            // coverage owner moved on during the network round-trip.
            guard cvEpoch == coverageAuthorityEpoch else { return }
            commitSuccess(fleet: fleet, generation: myGen)
            // Only persist when the in-VM commit actually applied (this response
            // was the newest), so an older success can never overwrite newer
            // cache state (criterion 6).
            if lastCommittedGeneration == myGen {
                isShowingStaleCache = false
                if let cache = coverageCache, let session = capturedCacheSession {
                    _ = await cache.recordFleet(fleet, capturedSession: session)
                }
            }
        } catch let error as NetworkError {
            guard cvEpoch == coverageAuthorityEpoch else { return }
            switch error {
            case .featureDisabled:
                commitFeatureDisabled(generation: myGen)
                if lastCommittedGeneration == myGen {
                    isShowingStaleCache = false
                    if let cache = coverageCache, let session = capturedCacheSession {
                        _ = await cache.recordFleetDisabled(capturedSession: session)
                    }
                }
            default:
                commitError(error, generation: myGen)
            }
        } catch {
            guard cvEpoch == coverageAuthorityEpoch else { return }
            commitError(error, generation: myGen)
        }
    }

    // MARK: - SignalR callback delivery (single tick per callback body)

    /// Handle one `filamentcoveragechanged` delivery. Advances the
    /// callback tick on exit regardless of filter/dispatch outcome
    /// so tests get a sound absence barrier.
    ///
    /// Gates on the SignalR epoch (not coverage). If the SignalR
    /// registration is still current, dispatch a load against the
    /// CURRENT coverage service by calling `load()` (which captures
    /// the current coverage epoch fresh).
    private func deliverInvalidationCallback(underSignalREpoch sigEpoch: UInt64) async {
        defer { advanceCallbackTick() }
        guard sigEpoch == signalRAuthorityEpoch else { return }
        await load()
    }

    private func deliverConnectionStateCallback(
        _ newState: SignalRConnectionState,
        underSignalREpoch sigEpoch: UInt64
    ) async {
        defer { advanceCallbackTick() }
        guard sigEpoch == signalRAuthorityEpoch else { return }
        guard newState == .connected else { return }
        if !hasSeenAnyConnected {
            hasSeenAnyConnected = true
            return
        }
        await load()
    }

    // MARK: - Commit paths (generation-authoritative)

    private func commitSuccess(fleet: FleetFilamentCoverage, generation: UInt64) {
        if generation < lastCommittedGeneration { return }
        if generation == lastCommittedGeneration { return }
        _ = lastCommittedEvaluatedAt
        coverageByPrinter = Dictionary(uniqueKeysWithValues:
            fleet.printers.map { ($0.printerId, $0) }
        )
        isFeatureDisabled = false
        lastLoadError = nil
        lastCommittedGeneration = generation
        lastCommittedEvaluatedAt = fleet.evaluatedAtUtc
        #if DEBUG
        resumeMatchingCommitWaiters()
        #endif
    }

    private func commitFeatureDisabled(generation: UInt64) {
        if generation < lastCommittedGeneration { return }
        coverageByPrinter = [:]
        isFeatureDisabled = true
        lastLoadError = nil
        lastCommittedGeneration = generation
        lastCommittedEvaluatedAt = nil
        #if DEBUG
        resumeMatchingCommitWaiters()
        #endif
    }

    private func commitError(_ error: Error, generation: UInt64) {
        if generation < lastCommittedGeneration { return }
        // Errors don't erase cached coverage; the last-good fleet
        // remains visible. They also don't flip the disabled
        // tombstone.
        lastLoadError = error.localizedDescription
        lastCommittedGeneration = generation
        #if DEBUG
        resumeMatchingCommitWaiters()
        #endif
    }

    // MARK: - Connection-state classification

    /// See file header for the classification rules.
    private func seedColdConnectClassification(fromInitialState state: SignalRConnectionState) {
        switch state {
        case .connected, .reconnecting:
            hasSeenAnyConnected = true
        case .disconnected, .connecting:
            hasSeenAnyConnected = false
        }
    }
}
