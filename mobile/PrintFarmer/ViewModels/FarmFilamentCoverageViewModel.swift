import Foundation

// MARK: - Farm Filament Coverage View Model (F4-M / issue #778)
//
// Owns the fleet-wide coverage cache displayed on the Farm/list screen.
//
// TWO orthogonal authorities gate every state change:
//
//   1. **Request-generation authority** (per-load-in-flight).
//      A monotonic `requestGeneration` counter is bumped on each
//      dispatched fetch. Every commit path (success/disabled/error)
//      refuses to write if a newer generation already committed.
//      Equal-`evaluatedAtUtc` snapshots resolve by generation.
//
//   2. **Owner-epoch authority** (per-VM-configuration lifetime).
//      A monotonic `authorityEpoch` counter is bumped on every
//      `configure(coverageService:)`, `configureSignalR(_:)`, and
//      `tearDownSignalR()`. Every callback captures the epoch it was
//      registered under and drops silently if the epoch moved on;
//      every in-flight `load()` re-checks the epoch (a) before it
//      even bumps `requestGeneration` and (b) before it commits, so
//      an old service's callback or in-flight GET cannot mutate the
//      replacement owner.
//
// Together these authorities close the cycle-2 lifetime/service
// blockers: a queued SignalR callback from an OLD service cannot
// cause a GET or commit on the replacement; an in-flight GET from an
// OLD service cannot overwrite the replacement owner's snapshot,
// tombstone, or error; a teardown before/during any in-flight step
// is honored.
//
// SignalR wiring (reuses the shared #777 lifecycle):
//   * `filamentcoveragechanged` is treated as an INVALIDATION HINT
//     only. On every valid event we dispatch exactly ONE refetch of
//     `/api/printers/filament-coverage`.
//   * Cold-connect classification (reviewer blocker B):
//       - If the subscription's initial connection state is
//         `.connected` or `.reconnecting`, we PRE-SEED the classifier
//         as if we've already seen a `.connected`. Any subsequent
//         `.connected` transition (including the recovery that
//         follows a `.reconnecting` we were configured under) IS a
//         recovery event and triggers exactly one refetch.
//       - If the initial state is `.disconnected` or `.connecting`,
//         the classifier starts unseeded so the first `.connected`
//         we observe is treated as the initial cold connect and does
//         NOT double-load with the view's `.task { load() }` call.
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

    // Owner-epoch authority.
    private var authorityEpoch: UInt64 = 0

    @ObservationIgnored private var invalidationSubscription: SignalRSubscription?
    @ObservationIgnored private var connectionStateSubscription: SignalRSubscription?
    @ObservationIgnored private var hasSeenAnyConnected: Bool = false

    // MARK: - Public API

    func coverage(for printerId: UUID) -> PrinterFilamentCoverage? {
        coverageByPrinter[printerId]
    }

    /// Replace the coverage service. Bumps the authority epoch so any
    /// in-flight load or queued callback from a prior configuration
    /// is invalidated.
    func configure(coverageService: any FilamentCoverageServiceProtocol) {
        self.coverageService = coverageService
        bumpAuthorityEpoch()
    }

    /// Register `filamentcoveragechanged` + connection-state
    /// subscriptions. Idempotent per instance: prior tokens are
    /// cancelled BEFORE new registrations happen. Bumps the authority
    /// epoch so any queued callback from an older subscription drops
    /// silently on delivery.
    func configureSignalR(_ service: any SignalRServiceProtocol) {
        self.signalRService = service

        invalidationSubscription?.cancel()
        connectionStateSubscription?.cancel()
        bumpAuthorityEpoch()
        let myEpoch = authorityEpoch
        // Cold-connect classification is re-seeded from the current
        // hub state each configure — see `seedColdConnectClassification`.
        hasSeenAnyConnected = false

        invalidationSubscription = service.onFilamentCoverageChanged { [weak self] _ in
            Task { @MainActor [weak self] in
                guard let self else { return }
                await self.deliverInvalidationCallback(underAuthority: myEpoch)
            }
        }

        let (initial, subscription) = service.onConnectionStateChanged { [weak self] newState in
            Task { @MainActor [weak self] in
                guard let self else { return }
                await self.deliverConnectionStateCallback(newState, underAuthority: myEpoch)
            }
        }
        connectionStateSubscription = subscription
        seedColdConnectClassification(fromInitialState: initial)
    }

    /// Cancel every SignalR subscription and invalidate every callback
    /// / in-flight commit from the current owner epoch.
    func tearDownSignalR() {
        invalidationSubscription?.cancel()
        invalidationSubscription = nil
        connectionStateSubscription?.cancel()
        connectionStateSubscription = nil
        bumpAuthorityEpoch()
        hasSeenAnyConnected = false
    }

    /// Public entry: dispatch a fresh fleet-coverage fetch under the
    /// current authority epoch.
    func load() async {
        await load(underAuthority: authorityEpoch)
    }

    // MARK: - Test seams (DEBUG-only, deterministic — no polling)

    #if DEBUG
    private var commitWaiters: [(target: UInt64, cont: CheckedContinuation<Void, Never>)] = []
    /// Park until `lastCommittedGeneration >= target`. Resumed inside
    /// each `commit*` path as the counter advances.
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
    /// connection-state callback, on both the filter/skip path and
    /// the dispatch path (the tick fires after the dispatched load's
    /// commit, so a `waitForCallbackTick(atLeast: N)` also guarantees
    /// any positive dispatch was fully processed).
    ///
    /// Tests use this to prove ABSENCE of a dispatched refetch
    /// deterministically: after `waitForCallbackTick` returns, every
    /// callback that was in flight has either dispatched (and its
    /// load has completed to commit) or was filtered out. The
    /// resulting `dispatchedRequestCount` is authoritative.
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

    var authorityEpochForTesting: UInt64 { authorityEpoch }
    var callbackTickForTesting: UInt64 { callbackTick }
    var hasSeenAnyConnectedForTesting: Bool { hasSeenAnyConnected }
    #else
    private func advanceCallbackTick() {}
    #endif

    var dispatchedRequestCount: UInt64 { requestGeneration }
    var lastCommittedGenerationForTesting: UInt64 { lastCommittedGeneration }

    // MARK: - Internal

    private func bumpAuthorityEpoch() {
        authorityEpoch &+= 1
    }

    private func load(underAuthority epoch: UInt64) async {
        // Owner-epoch gate #1: refuse to start under a stale epoch or
        // when the service is not set. Capture the service reference
        // locally so a mid-load `configure` cannot swap it out from
        // under us.
        guard epoch == authorityEpoch, let coverageService else { return }

        requestGeneration &+= 1
        let myGen = requestGeneration
        do {
            let fleet = try await coverageService.getForFleet()
            // Owner-epoch gate #2: refuse to commit if the owner
            // moved on while we were awaiting the network round-trip.
            guard epoch == authorityEpoch else { return }
            commitSuccess(fleet: fleet, generation: myGen)
        } catch let error as NetworkError {
            guard epoch == authorityEpoch else { return }
            switch error {
            case .featureDisabled:
                commitFeatureDisabled(generation: myGen)
            default:
                commitError(error, generation: myGen)
            }
        } catch {
            guard epoch == authorityEpoch else { return }
            commitError(error, generation: myGen)
        }
    }

    // MARK: - SignalR callback delivery (single tick per callback body)

    /// Handle one `filamentcoveragechanged` delivery. Always advances
    /// the callback tick on exit — regardless of whether the callback
    /// filtered out, dispatched a load, or the dispatched load
    /// committed. This makes `waitForCallbackTick` a sound absence
    /// barrier for tests.
    private func deliverInvalidationCallback(underAuthority epoch: UInt64) async {
        defer { advanceCallbackTick() }
        guard epoch == authorityEpoch else { return }
        await load(underAuthority: epoch)
    }

    /// Handle one connection-state delivery. Same tick-on-exit
    /// discipline as `deliverInvalidationCallback`.
    private func deliverConnectionStateCallback(
        _ newState: SignalRConnectionState,
        underAuthority epoch: UInt64
    ) async {
        defer { advanceCallbackTick() }
        guard epoch == authorityEpoch else { return }
        guard newState == .connected else { return }
        if !hasSeenAnyConnected {
            // Cold initial connect. Do not double-load; the view
            // owns this fetch via `.task { load() }`. Mark seen so
            // the next transition is treated as recovery.
            hasSeenAnyConnected = true
            return
        }
        // Recovery transition — dispatch exactly one refetch under
        // the same epoch the subscription was registered under.
        await load(underAuthority: epoch)
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
        lastLoadError = error.localizedDescription
        lastCommittedGeneration = generation
        #if DEBUG
        resumeMatchingCommitWaiters()
        #endif
    }

    // MARK: - Connection-state classification

    /// Seed the cold-connect classifier from the state the hub
    /// reports at subscription time.
    ///
    /// * `.connected` — connection was already up before we
    ///   subscribed. Not our cold connect. Any subsequent
    ///   `.reconnecting -> .connected` will be recovery.
    /// * `.reconnecting` — a reconnect is IN-FLIGHT (reviewer
    ///   blocker B). The pending `.connected` transition is a
    ///   recovery event that MUST dispatch a refetch, so we pre-seed
    ///   the flag to true.
    /// * `.disconnected` / `.connecting` — cold path. The first
    ///   `.connected` we observe is the initial connect and the
    ///   view's `.task` already covers that fetch; skip.
    private func seedColdConnectClassification(fromInitialState state: SignalRConnectionState) {
        switch state {
        case .connected, .reconnecting:
            hasSeenAnyConnected = true
        case .disconnected, .connecting:
            hasSeenAnyConnected = false
        }
    }
}
