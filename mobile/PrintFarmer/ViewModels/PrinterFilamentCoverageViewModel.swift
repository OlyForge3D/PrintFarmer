import Foundation

// MARK: - Printer Filament Coverage View Model (F4-M / issue #778)
//
// Per-printer variant of `FarmFilamentCoverageViewModel`. Same
// generation-authoritative and SignalR-lifecycle discipline; the only
// differences are:
//
//   * It fetches the single-printer endpoint
//     (`GET /api/printers/{id}/filament-coverage`).
//   * The invalidation subscription filters on the event's
//     `printerId` matching this VM's printer id. A `nil` printerId in
//     the event scopes to the whole fleet, so we refetch too — the
//     server broadcasts a fleet-scoped invalidation whenever the
//     cause could impact any printer (e.g. Spoolman reindex).

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

    @ObservationIgnored private var invalidationSubscription: SignalRSubscription?
    @ObservationIgnored private var connectionStateSubscription: SignalRSubscription?
    @ObservationIgnored private var hasSeenInitialConnected: Bool = false

    init(printerId: UUID) {
        self.printerId = printerId
    }

    func configure(coverageService: any FilamentCoverageServiceProtocol) {
        self.coverageService = coverageService
    }

    func configureSignalR(_ service: any SignalRServiceProtocol) {
        self.signalRService = service

        invalidationSubscription?.cancel()
        connectionStateSubscription?.cancel()
        hasSeenInitialConnected = false

        let scopeId = printerId
        invalidationSubscription = service.onFilamentCoverageChanged { [weak self] event in
            // Fleet-scoped invalidations (`printerId == nil`) apply to
            // every open coverage screen. Otherwise refetch only on
            // events targeting our printer id.
            guard event.printerId == nil || event.printerId == scopeId else { return }
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
        if initial == .connected {
            hasSeenInitialConnected = true
        }
    }

    func tearDownSignalR() {
        invalidationSubscription?.cancel()
        invalidationSubscription = nil
        connectionStateSubscription?.cancel()
        connectionStateSubscription = nil
        hasSeenInitialConnected = false
    }

    // MARK: - Test seams (deterministic sync helpers)

    /// Test-only: park until the committed generation reaches at least
    /// `target`. Deterministic — resumed inside each `commit*` path when
    /// the counter advances. No fixed sleep, no polling.
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
            if current >= w.target {
                w.cont.resume()
            } else {
                remaining.append(w)
            }
        }
        commitWaiters = remaining
    }
    #endif

    func load() async {
        guard let coverageService else { return }
        requestGeneration &+= 1
        let myGen = requestGeneration
        do {
            let snapshot = try await coverageService.getForPrinter(id: printerId)
            commitSuccess(snapshot: snapshot, generation: myGen)
        } catch let error as NetworkError {
            switch error {
            case .featureDisabled:
                commitFeatureDisabled(generation: myGen)
            case .notFound:
                commitNotFound(generation: myGen)
            default:
                commitError(error, generation: myGen)
            }
        } catch {
            commitError(error, generation: myGen)
        }
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

    private func handleConnectionStateChange(_ newState: SignalRConnectionState) {
        guard newState == .connected else { return }
        if !hasSeenInitialConnected {
            hasSeenInitialConnected = true
            return
        }
        Task { @MainActor [weak self] in
            await self?.load()
        }
    }

    // MARK: - Test seams

    var dispatchedRequestCount: UInt64 { requestGeneration }
    var lastCommittedGenerationForTesting: UInt64 { lastCommittedGeneration }
}
