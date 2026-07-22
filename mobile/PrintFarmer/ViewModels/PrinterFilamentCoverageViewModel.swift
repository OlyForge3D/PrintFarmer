import Foundation

// MARK: - Printer Filament Coverage View Model (F4-M / issue #778)
//
// Per-printer variant of `FarmFilamentCoverageViewModel`. Same
// generation-authoritative and owner-epoch discipline as the fleet
// VM (see FarmFilamentCoverageViewModel.swift for the full
// contract); the only differences are:
//
//   * `load()` calls the single-printer endpoint
//     `GET /api/printers/{id}/filament-coverage`.
//   * The invalidation subscription filters events by
//     `event.printerId == self.printerId` OR `event.printerId == nil`
//     (fleet-scoped). Filtering happens under the owner-epoch check
//     so a stale event from an old subscription cannot even reach
//     the filter.

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

    private var authorityEpoch: UInt64 = 0

    @ObservationIgnored private var invalidationSubscription: SignalRSubscription?
    @ObservationIgnored private var connectionStateSubscription: SignalRSubscription?
    @ObservationIgnored private var hasSeenAnyConnected: Bool = false

    init(printerId: UUID) {
        self.printerId = printerId
    }

    // MARK: - Public API

    func configure(coverageService: any FilamentCoverageServiceProtocol) {
        self.coverageService = coverageService
        bumpAuthorityEpoch()
    }

    func configureSignalR(_ service: any SignalRServiceProtocol) {
        self.signalRService = service

        invalidationSubscription?.cancel()
        connectionStateSubscription?.cancel()
        bumpAuthorityEpoch()
        let myEpoch = authorityEpoch
        hasSeenAnyConnected = false

        let scopeId = printerId
        invalidationSubscription = service.onFilamentCoverageChanged { [weak self] event in
            Task { @MainActor [weak self] in
                guard let self else { return }
                await self.deliverInvalidationCallback(
                    event: event,
                    scopeId: scopeId,
                    underAuthority: myEpoch
                )
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

    func tearDownSignalR() {
        invalidationSubscription?.cancel()
        invalidationSubscription = nil
        connectionStateSubscription?.cancel()
        connectionStateSubscription = nil
        bumpAuthorityEpoch()
        hasSeenAnyConnected = false
    }

    func load() async {
        await load(underAuthority: authorityEpoch)
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
    /// Sound absence barrier: after `waitForCallbackTick` returns,
    /// every callback that was in flight has either dispatched (and
    /// its load committed) or was filtered. `dispatchedRequestCount`
    /// is authoritative at that point.
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
        guard epoch == authorityEpoch, let coverageService else { return }
        requestGeneration &+= 1
        let myGen = requestGeneration
        do {
            let snapshot = try await coverageService.getForPrinter(id: printerId)
            guard epoch == authorityEpoch else { return }
            commitSuccess(snapshot: snapshot, generation: myGen)
        } catch let error as NetworkError {
            guard epoch == authorityEpoch else { return }
            switch error {
            case .featureDisabled:
                commitFeatureDisabled(generation: myGen)
            case .notFound:
                commitNotFound(generation: myGen)
            default:
                commitError(error, generation: myGen)
            }
        } catch {
            guard epoch == authorityEpoch else { return }
            commitError(error, generation: myGen)
        }
    }

    // MARK: - SignalR callback delivery (single tick per callback body)

    private func deliverInvalidationCallback(
        event: FilamentCoverageChangedEvent,
        scopeId: UUID,
        underAuthority epoch: UInt64
    ) async {
        defer { advanceCallbackTick() }
        guard epoch == authorityEpoch else { return }
        // Fleet-scoped events (`printerId == nil`) apply to every
        // open coverage screen; otherwise refetch only on events
        // targeting our printer id.
        guard event.printerId == nil || event.printerId == scopeId else { return }
        await load(underAuthority: epoch)
    }

    private func deliverConnectionStateCallback(
        _ newState: SignalRConnectionState,
        underAuthority epoch: UInt64
    ) async {
        defer { advanceCallbackTick() }
        guard epoch == authorityEpoch else { return }
        guard newState == .connected else { return }
        if !hasSeenAnyConnected {
            hasSeenAnyConnected = true
            return
        }
        await load(underAuthority: epoch)
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

    /// See `FarmFilamentCoverageViewModel.seedColdConnectClassification`.
    private func seedColdConnectClassification(fromInitialState state: SignalRConnectionState) {
        switch state {
        case .connected, .reconnecting:
            hasSeenAnyConnected = true
        case .disconnected, .connecting:
            hasSeenAnyConnected = false
        }
    }
}
