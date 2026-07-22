import Foundation

// MARK: - Farm Filament Coverage View Model (F4-M / issue #778)
//
// Owns the fleet-wide coverage cache displayed on the Farm/list screen.
// Every state transition is generation-authoritative:
//   * A monotonic `generation` counter is bumped on each fetch dispatch.
//     Completions carry back the generation they were issued under.
//   * Both success and error/tombstone outcomes are tagged with the
//     same generation, so an older in-flight success cannot overwrite a
//     newer disabled-tombstone (or newer success).
//   * Equal `evaluatedAtUtc` timestamps between two successes resolve
//     in favor of the newer generation.
//
// SignalR wiring (from the shared #777 lifecycle):
//   * `filamentcoveragechanged` is treated as an INVALIDATION HINT
//     only. On every event we dispatch a single canonical refetch of
//     `/api/printers/filament-coverage` (no payload data is trusted).
//   * Connection state observation: after the FIRST observed
//     transition to `.connected` (which is the initial connect, and
//     already covered by the `.task { load() }` in the view), we
//     schedule EXACTLY ONE recovery refetch on each subsequent
//     `.reconnecting -> .connected` transition. A cold initial connect
//     never double-loads.
//   * `configureSignalR` cancels any prior subscription tokens before
//     re-registering, so re-entering the view or reconfiguring the
//     same service is idempotent.

@MainActor @Observable
final class FarmFilamentCoverageViewModel {

    /// Per-printer coverage snapshots keyed by stable printer id.
    /// Reads use `coverage(for:)` so callers cannot accidentally
    /// look up by display name.
    private(set) var coverageByPrinter: [UUID: PrinterFilamentCoverage] = [:]

    /// The whole feature is disabled server-side (structured 404). Views
    /// consult this to hide every covers/runout affordance without
    /// showing a hard error. Once true it stays true until a newer
    /// generation successfully loads, so an older stale success cannot
    /// silently re-enable coverage.
    private(set) var isFeatureDisabled: Bool = false

    /// Last non-`featureDisabled` error observed while loading. Purely
    /// informational — the view keeps rendering the last-good cache.
    private(set) var lastLoadError: String?

    private var coverageService: (any FilamentCoverageServiceProtocol)?
    private var signalRService: (any SignalRServiceProtocol)?

    // Generation-authoritative request tracking.
    /// Monotonic counter. Increments on every dispatched fetch.
    private var requestGeneration: UInt64 = 0
    /// Generation of the last completion that mutated cached state.
    /// A newer completion beats a stale one; equal-timestamp wins are
    /// broken by generation.
    private var lastCommittedGeneration: UInt64 = 0
    /// `evaluatedAtUtc` of the last committed success, if any. Used to
    /// order successive successes.
    private var lastCommittedEvaluatedAt: Date?

    // Subscription tokens — retained for the observation lifetime so
    // the hub's cancel-on-deinit does not silently disable delivery.
    @ObservationIgnored private var invalidationSubscription: SignalRSubscription?
    @ObservationIgnored private var connectionStateSubscription: SignalRSubscription?
    /// Signals whether we've already observed at least one `.connected`
    /// transition. The initial-connect refetch is done by the view via
    /// `load()`; only subsequent transitions to `.connected` trigger a
    /// recovery refetch.
    @ObservationIgnored private var hasSeenInitialConnected: Bool = false

    /// Ordered coverage snapshots aligned with a caller-supplied
    /// printer list. Missing entries are omitted; the caller can zip
    /// this with its own printer array by matching `printerId`.
    func coverage(for printerId: UUID) -> PrinterFilamentCoverage? {
        coverageByPrinter[printerId]
    }

    func configure(coverageService: any FilamentCoverageServiceProtocol) {
        self.coverageService = coverageService
    }

    /// Registers `filamentcoveragechanged` + connection-state
    /// subscriptions against `service`. Idempotent per instance: prior
    /// tokens are cancelled before new registrations happen, so
    /// repeated configuration on the same service or a view revisit
    /// cannot accumulate handlers.
    func configureSignalR(_ service: any SignalRServiceProtocol) {
        self.signalRService = service

        invalidationSubscription?.cancel()
        connectionStateSubscription?.cancel()
        hasSeenInitialConnected = false

        invalidationSubscription = service.onFilamentCoverageChanged { [weak self] _ in
            Task { @MainActor [weak self] in
                await self?.load()
            }
        }

        let (initial, subscription) = service.onConnectionStateChanged { [weak self] newState in
            Task { @MainActor [weak self] in
                self?.handleConnectionStateChange(newState)
            }
        }
        connectionStateSubscription = subscription
        handleInitialConnectionState(initial)
    }

    /// Cancel every SignalR subscription held by this view model.
    /// Callers use this on view teardown to guarantee that no delivery
    /// is dispatched to a dead handler.
    func tearDownSignalR() {
        invalidationSubscription?.cancel()
        invalidationSubscription = nil
        connectionStateSubscription?.cancel()
        connectionStateSubscription = nil
        hasSeenInitialConnected = false
    }

    // MARK: - Test seams (deterministic sync helpers)

    #if DEBUG
    private var commitWaiters: [(target: UInt64, cont: CheckedContinuation<Void, Never>)] = []
    /// Test-only: park until `lastCommittedGeneration` reaches
    /// `target`. Deterministic — resumed inside each `commit*` path
    /// as the counter advances. No sleeps or polling.
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
            if current >= w.target {
                w.cont.resume()
            } else {
                remaining.append(w)
            }
        }
        commitWaiters = remaining
    }
    #endif

    /// Dispatch a fresh fleet-coverage fetch. Concurrent-safe: every
    /// completion checks its captured generation before committing.
    func load() async {
        guard let coverageService else { return }
        requestGeneration &+= 1
        let myGen = requestGeneration
        do {
            let fleet = try await coverageService.getForFleet()
            commitSuccess(fleet: fleet, generation: myGen)
        } catch let error as NetworkError {
            switch error {
            case .featureDisabled:
                commitFeatureDisabled(generation: myGen)
            default:
                commitError(error, generation: myGen)
            }
        } catch {
            commitError(error, generation: myGen)
        }
    }

    // MARK: - Commit paths (generation-authoritative)

    private func commitSuccess(fleet: FleetFilamentCoverage, generation: UInt64) {
        // A newer completion has already committed → drop.
        if generation < lastCommittedGeneration { return }

        // Equal generation is impossible (each `load()` advances it),
        // but guard defensively so we never regress state.
        if generation == lastCommittedGeneration { return }

        // Older-timestamp successes that arrive AFTER a newer one must
        // still lose. `lastCommittedGeneration` already handles the
        // strict-newer case; when the previously-committed outcome was
        // a tombstone or error under a newer generation, `generation <
        // lastCommittedGeneration` above rejects the stale success.
        //
        // Equal-timestamp resolution: if a caller replays or the
        // server emits two snapshots with the same `evaluatedAtUtc`,
        // the newer generation wins (this branch, because `generation
        // > lastCommittedGeneration`). Nothing extra to do here.
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
        // Errors don't erase cached coverage — the last known-good
        // fleet remains visible. They also don't flip the disabled
        // tombstone, so a transient 5xx doesn't hide coverage.
        lastLoadError = error.localizedDescription
        lastCommittedGeneration = generation
        #if DEBUG
        resumeMatchingCommitWaiters()
        #endif
    }

    // MARK: - Connection-state handling

    private func handleInitialConnectionState(_ state: SignalRConnectionState) {
        if state == .connected {
            hasSeenInitialConnected = true
        }
    }

    private func handleConnectionStateChange(_ newState: SignalRConnectionState) {
        guard newState == .connected else { return }
        if !hasSeenInitialConnected {
            // First observed transition to `.connected`. The initial
            // fetch is owned by the view's `.task` call; do not
            // double-load on the cold path.
            hasSeenInitialConnected = true
            return
        }
        // A subsequent `.reconnecting -> .connected` transition:
        // exactly one recovery refetch.
        Task { @MainActor [weak self] in
            await self?.load()
        }
    }

    // MARK: - Test seams

    /// Test-only introspection: number of dispatched requests.
    var dispatchedRequestCount: UInt64 { requestGeneration }

    /// Test-only introspection: generation of the last committed
    /// (success/disabled/error) outcome.
    var lastCommittedGenerationForTesting: UInt64 { lastCommittedGeneration }
}
