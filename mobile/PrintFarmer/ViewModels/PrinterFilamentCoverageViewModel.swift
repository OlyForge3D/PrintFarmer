import Foundation

// MARK: - Printer Filament Coverage View Model (F4-M / issue #778)
//
// Per-printer variant of `FarmFilamentCoverageViewModel`. Same
// three-authority discipline (see FarmFilamentCoverageViewModel.swift
// for the full split-authority rationale); the only differences:
//
//   * `load()` calls the single-printer endpoint
//     `GET /api/printers/{id}/filament-coverage`.
//   * The invalidation subscription filters events by
//     `event.printerId == self.printerId` OR `event.printerId == nil`
//     (fleet-scoped). Filtering happens under the SignalR-epoch
//     check so a stale S1 callback cannot even reach the filter.
//   * Adds a `notFound` commit path distinct from `featureDisabled`.

@MainActor @Observable
final class PrinterFilamentCoverageViewModel {

    let printerId: UUID

    private(set) var coverage: PrinterFilamentCoverage?
    private(set) var isFeatureDisabled: Bool = false
    private(set) var isPrinterNotFound: Bool = false
    private(set) var lastLoadError: String?

    private var coverageService: (any FilamentCoverageServiceProtocol)?
    private var signalRService: (any SignalRServiceProtocol)?

    private var requestGeneration: UInt64 = 0
    private var lastCommittedGeneration: UInt64 = 0
    private var lastCommittedEvaluatedAt: Date?

    private var coverageAuthorityEpoch: UInt64 = 0
    private var signalRAuthorityEpoch: UInt64 = 0

    @ObservationIgnored private var invalidationSubscription: SignalRSubscription?
    @ObservationIgnored private var connectionStateSubscription: SignalRSubscription?
    @ObservationIgnored private var hasSeenAnyConnected: Bool = false

    // MARK: Read-cache (issue #789, F10-C2) — additive, optional-guarded.
    @ObservationIgnored private var coverageCache: FilamentCoverageReadCacheAdapter?
    /// True exactly while the on-screen detail is UNCONFIRMED cached data.
    private(set) var isShowingStaleCache: Bool = false
    /// True once a canonical load has CONCLUDED at least once under the current
    /// coverage authority — success, `featureDisabled`, `notFound`, or error.
    /// Starts false, so the interval between a cache hydrate and the first
    /// canonical result is *undecided* rather than "offline".
    ///
    /// This must be observed (never `@ObservationIgnored`): `isStaleCacheReportable`
    /// is read by SwiftUI, and a derived property whose only changing input is
    /// untracked state is never re-evaluated, so the banner would never appear.
    /// The same trap made `ConnectionMonitor.isReportable` inert (PR #2400).
    private(set) var hasConcludedCanonicalLoad: Bool = false
    /// Drives the shared stale banner. `isShowingStaleCache` alone is true from
    /// the instant `hydrateFromCache()` lands, which flashes an "offline" banner
    /// on an entirely healthy detail open — the canonical load simply had not
    /// returned yet. `PrinterDetailView` builds a fresh view model on every
    /// navigation, so that flash repeats on each tap of the same printer.
    var isStaleCacheReportable: Bool {
        isShowingStaleCache && hasConcludedCanonicalLoad
    }
    private(set) var cacheLastUpdatedAtMillis: Int64?
    var cacheLastUpdatedAt: Date? {
        cacheLastUpdatedAtMillis.map { Date(timeIntervalSince1970: Double($0) / 1000.0) }
    }

    init(printerId: UUID) {
        self.printerId = printerId
    }

    // MARK: - Public API

    func configure(coverageService: any FilamentCoverageServiceProtocol) {
        self.coverageService = coverageService
        coverageAuthorityEpoch &+= 1
        // The new authority has concluded nothing yet, so the next undecided
        // window must not inherit the previous authority's conclusion and flash
        // its banner. Reset ONLY the conclusion flag.
        //
        // `isShowingStaleCache` and `cacheLastUpdatedAtMillis` are provenance FOR
        // `coverage`, which `configure` deliberately does not clear, and they
        // must stay coupled to it. Clearing them here would leave retained
        // cached data marked as confirmed-live -- and because `hydrateFromCache`
        // refuses to re-run once `coverage` is non-nil (line 114), nothing would
        // ever restore the flag, permanently suppressing the banner even when
        // the next load fails. Authority changes that DO clear the payload
        // (`disableForCapabilityGate`) call `resetReadCacheState()` instead.
        hasConcludedCanonicalLoad = false
    }

    /// Clear all read-cache provenance. Only valid where the cached payload it
    /// describes is cleared in the same breath, or provenance desynchronises
    /// from the data on screen.
    private func resetReadCacheState() {
        isShowingStaleCache = false
        hasConcludedCanonicalLoad = false
        cacheLastUpdatedAtMillis = nil
    }

    func disableForCapabilityGate() {
        coverageAuthorityEpoch &+= 1
        coverageService = nil
        tearDownSignalR()
        coverage = nil
        isFeatureDisabled = true
        isPrinterNotFound = false
        lastLoadError = nil
        resetReadCacheState()
    }

    /// Wire the #789 per-printer read-cache. Additive; safe to call repeatedly.
    func configureCache(_ cache: FilamentCoverageReadCacheAdapter) {
        self.coverageCache = cache
    }

    /// Hydrate this printer's cached coverage BEFORE the first canonical load.
    /// Never downgrades confirmed-live data; honours `unknown` verbatim and a
    /// cached feature-disabled tombstone.
    func hydrateFromCache() async {
        guard let cache = coverageCache else { return }
        guard lastCommittedGeneration == 0, coverage == nil, !isFeatureDisabled, !isPrinterNotFound else { return }
        let hydration = await cache.loadCachedPrinter(id: printerId)
        guard lastCommittedGeneration == 0 else { return }
        switch hydration {
        case .snapshot(let snapshot, let millis):
            coverage = snapshot
            isFeatureDisabled = false
            isPrinterNotFound = false
            lastLoadError = nil
            isShowingStaleCache = true
            cacheLastUpdatedAtMillis = millis
        case .disabled(let millis):
            coverage = nil
            isFeatureDisabled = true
            isPrinterNotFound = false
            lastLoadError = nil
            isShowingStaleCache = true
            cacheLastUpdatedAtMillis = millis
        case .inactive, .absent, .recovered, .unreadable:
            break
        }
    }

    func configureSignalR(_ service: any SignalRServiceProtocol) {
        self.signalRService = service

        invalidationSubscription?.cancel()
        connectionStateSubscription?.cancel()
        signalRAuthorityEpoch &+= 1
        let mySigEpoch = signalRAuthorityEpoch
        hasSeenAnyConnected = false

        let scopeId = printerId
        invalidationSubscription = service.onFilamentCoverageChanged { [weak self] event in
            Task { @MainActor [weak self] in
                guard let self else { return }
                await self.deliverInvalidationCallback(
                    event: event,
                    scopeId: scopeId,
                    underSignalREpoch: mySigEpoch
                )
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

    func tearDownSignalR() {
        invalidationSubscription?.cancel()
        invalidationSubscription = nil
        connectionStateSubscription?.cancel()
        connectionStateSubscription = nil
        signalRAuthorityEpoch &+= 1
        hasSeenAnyConnected = false
    }

    func load() async {
        await load(underCoverageEpoch: coverageAuthorityEpoch)
    }

    // MARK: - Test seams (DEBUG-only)

    #if DEBUG
    private var commitWaiters: [(target: UInt64, cont: CheckedContinuation<Void, Never>)] = []
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

    private var callbackTick: UInt64 = 0
    private var callbackTickWaiters: [(target: UInt64, cont: CheckedContinuation<Void, Never>)] = []
    /// See `FarmFilamentCoverageViewModel.waitForCallbackTick` for
    /// the absence-barrier semantics.
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
        guard cvEpoch == coverageAuthorityEpoch, let coverageService else { return }

        // Capture the cache session BEFORE the round-trip so a mid-flight switch
        // cannot land this write in the new namespace. `nil` when no cache wired.
        let capturedCacheSession = await coverageCache?.currentSession()
        guard !Task.isCancelled, cvEpoch == coverageAuthorityEpoch else { return }

        requestGeneration &+= 1
        let myGen = requestGeneration
        do {
            let snapshot = try await coverageService.getForPrinter(id: printerId)
            guard cvEpoch == coverageAuthorityEpoch else { return }
            commitSuccess(snapshot: snapshot, generation: myGen)
            if lastCommittedGeneration == myGen {
                isShowingStaleCache = false
                hasConcludedCanonicalLoad = true
                if let cache = coverageCache, let session = capturedCacheSession {
                    _ = await cache.recordPrinter(snapshot, capturedSession: session)
                }
            }
        } catch let error as NetworkError {
            guard cvEpoch == coverageAuthorityEpoch else { return }
            switch error {
            case .featureDisabled:
                commitFeatureDisabled(generation: myGen)
                if lastCommittedGeneration == myGen {
                    isShowingStaleCache = false
                    hasConcludedCanonicalLoad = true
                    if let cache = coverageCache, let session = capturedCacheSession {
                        _ = await cache.recordPrinterDisabled(id: printerId, capturedSession: session)
                    }
                }
            case .notFound:
                commitNotFound(generation: myGen)
                if lastCommittedGeneration == myGen {
                    isShowingStaleCache = false
                    hasConcludedCanonicalLoad = true
                }
            default:
                commitError(error, generation: myGen)
                // The load genuinely failed, so the on-screen cached data really
                // is unconfirmed: this is the one path that should raise the
                // banner. Cancellation is excluded — it answers nothing.
                if lastCommittedGeneration == myGen, !isCancellationError(error) {
                    hasConcludedCanonicalLoad = true
                }
            }
        } catch {
            guard cvEpoch == coverageAuthorityEpoch else { return }
            commitError(error, generation: myGen)
            if lastCommittedGeneration == myGen, !isCancellationError(error) {
                hasConcludedCanonicalLoad = true
            }
        }
    }

    // MARK: - SignalR callback delivery (single tick per callback body)

    private func deliverInvalidationCallback(
        event: FilamentCoverageChangedEvent,
        scopeId: UUID,
        underSignalREpoch sigEpoch: UInt64
    ) async {
        defer { advanceCallbackTick() }
        guard sigEpoch == signalRAuthorityEpoch else { return }
        // Fleet-scoped events (`printerId == nil`) apply to every
        // open coverage screen; otherwise refetch only on events
        // targeting our printer id.
        guard event.printerId == nil || event.printerId == scopeId else { return }
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

    // MARK: - Commit paths

    private func commitSuccess(snapshot: PrinterFilamentCoverage, generation: UInt64) {
        if generation < lastCommittedGeneration { return }
        if generation == lastCommittedGeneration { return }
        _ = lastCommittedEvaluatedAt
        coverage = snapshot
        isFeatureDisabled = false
        isPrinterNotFound = false
        lastLoadError = nil
        lastCommittedGeneration = generation
        lastCommittedEvaluatedAt = snapshot.evaluatedAtUtc
        #if DEBUG
        resumeMatchingCommitWaiters()
        #endif
    }

    private func commitFeatureDisabled(generation: UInt64) {
        if generation < lastCommittedGeneration { return }
        coverage = nil
        isFeatureDisabled = true
        isPrinterNotFound = false
        lastLoadError = nil
        lastCommittedGeneration = generation
        lastCommittedEvaluatedAt = nil
        #if DEBUG
        resumeMatchingCommitWaiters()
        #endif
    }

    private func commitNotFound(generation: UInt64) {
        if generation < lastCommittedGeneration { return }
        coverage = nil
        isFeatureDisabled = false
        isPrinterNotFound = true
        lastLoadError = nil
        lastCommittedGeneration = generation
        lastCommittedEvaluatedAt = nil
        #if DEBUG
        resumeMatchingCommitWaiters()
        #endif
    }

    private func commitError(_ error: Error, generation: UInt64) {
        if generation < lastCommittedGeneration { return }
        lastLoadError = error.localizedDescription
        lastCommittedGeneration = generation
        #if DEBUG
        resumeMatchingCommitWaiters()
        #endif
    }

    // MARK: - Connection-state classification

    private func seedColdConnectClassification(fromInitialState state: SignalRConnectionState) {
        switch state {
        case .connected, .reconnecting:
            hasSeenAnyConnected = true
        case .disconnected, .connecting:
            hasSeenAnyConnected = false
        }
    }
}
