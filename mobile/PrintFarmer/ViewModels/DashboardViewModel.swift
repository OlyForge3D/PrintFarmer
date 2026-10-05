import Foundation
import os

@MainActor @Observable
final class DashboardViewModel {
    typealias CallbackEnqueuer = @Sendable (
        @escaping @MainActor @Sendable () async -> Void
    ) -> Void

    var printers: [Printer] = []
    var isLoading = false
    var errorMessage: String?
    var isViewActive = true {
        didSet {
            if oldValue && !isViewActive {
                invalidateCanonicalLoad()
                tearDownSignalR()
            }
        }
    }

    // MARK: - Cold-offline farm snapshot state (F10-C1b, #817)

    /// Where the currently-displayed fleet came from. This is what the shell
    /// keys its read-only / stale presentation on — never the mere presence of
    /// printers, so a present-but-empty cache still renders distinctly.
    enum FarmSource: Equatable {
        /// No hydration has happened yet (cold, pre-`.task`).
        case notLoaded
        /// A confirmed canonical response is on screen (fully interactive).
        case live
        /// The active exact-owner cached snapshot is on screen, unconfirmed
        /// (offline / degraded). The shell is read-only while in this state.
        case cached
        /// The session is authoritative but no snapshot has ever been written
        /// (never-loaded-online), and no canonical response is available.
        case absent
    }

    /// Auto-dispatch "pending ready" projection, keyed by printer id. Sourced
    /// from the authoritative auto-dispatch status (H6) — never derived from
    /// `Printer.state`. Used both for card projection and snapshot commit.
    var pendingReadyPrinterIDs: Set<UUID> = []

    private(set) var farmSource: FarmSource = .notLoaded

    /// True when the on-screen fleet is unconfirmed cached data. Mutations and
    /// command affordances are denied while stale.
    var isStale: Bool { farmSource == .cached }

    /// The shell is read-only exactly while showing unconfirmed cached data.
    /// Deliberately NOT gated on a concluded load: mutations must stay denied
    /// while the data is unconfirmed, including during the undecided window.
    var isReadOnly: Bool { isStale }

    /// True once a canonical pass has CONCLUDED at least once under the current
    /// authority — published or failed. Cancellation/supersession does not count
    /// (it maps to `.superseded`, which answers nothing). Starts false, so the
    /// window between a cache hydrate and the first canonical result is
    /// *undecided* rather than "offline".
    ///
    /// This must be observed (never `@ObservationIgnored`): the derived
    /// properties below are read by SwiftUI, and a derived property whose only
    /// changing input is untracked state is never re-evaluated. The same trap
    /// made `ConnectionMonitor.isReportable` inert (PR #2400).
    private(set) var hasConcludedCanonicalLoad: Bool = false

    /// Drives the cold-offline shell's stale banner. The shell itself renders as
    /// soon as the cache hydrates (that immediacy is the point of #817), but
    /// claiming "offline" before any canonical attempt has concluded is a lie
    /// that flashes on every healthy launch.
    var isStaleBannerReportable: Bool { isStale && hasConcludedCanonicalLoad }

    /// Drives the "No Cached Fleet" dead-end. Until a canonical pass concludes
    /// we do not know the fleet is unreachable — only that nothing was cached —
    /// so the undecided window must show loading, not a reconnect prompt.
    var isAbsentFleetReportable: Bool { hasNoCachedData && hasConcludedCanonicalLoad }

    /// The immutable UTC instant of the last successful canonical response now
    /// on screen (live) or backing the cached snapshot (stale). `nil` until the
    /// first hydrate/commit resolves.
    private(set) var lastUpdatedAt: Date?
    private(set) var farmDataRevision: UInt64 = 0

    /// True once an authoritative session exists but its record is absent — the
    /// "no cached data yet" state, distinct from a present-but-empty fleet.
    var hasNoCachedData: Bool { farmSource == .absent }

    private let logger = Logger(subsystem: "com.printfarmer.ios", category: "Dashboard")

    private var printerService: (any PrinterServiceProtocol)?
    private var autoPrintService: (any AutoDispatchServiceProtocol)?
    private var signalRService: (any SignalRServiceProtocol)?
    @ObservationIgnored private var signalRSubscriptions: [SignalRSubscription] = []
    @ObservationIgnored private var signalRServiceIdentity: ObjectIdentifier?
    @ObservationIgnored private var printerSubscriptionSyncGeneration: UInt64 = 0
    @ObservationIgnored private var pendingReadyRefreshGeneration: UInt64 = 0
    @ObservationIgnored private var signalRAuthorityEpoch: UInt64 = 0
    @ObservationIgnored private var lastObservedConnectionState: SignalRConnectionState?
    @ObservationIgnored private let callbackEnqueuer: CallbackEnqueuer
    @ObservationIgnored private var canonicalLifecycleEpoch: UInt64 = 0
    @ObservationIgnored private var canonicalLoadToken: UUID?
    @ObservationIgnored private var canonicalLoadTask: Task<Void, Never>?
    @ObservationIgnored private var canonicalLoadRequested = false
    @ObservationIgnored private var canonicalPassHasRecoveryDemand = false
    @ObservationIgnored private var canonicalPendingRecoveryDemand = false
    @ObservationIgnored private let canonicalLoadWaiters = CanonicalLoadWaiterRegistry()
    @ObservationIgnored private let canonicalTaskTracker = CanonicalLoadTaskTracker()
    @ObservationIgnored private var canonicalCommitAuthorization: FarmSnapshotCommitAuthorization?

    // Snapshot lifecycle authority (#816), consumed unchanged.
    @ObservationIgnored private var snapshotStore: (any FarmSnapshotStoring)?
    @ObservationIgnored private var now: @Sendable () -> Date = { Date() }
    @ObservationIgnored private var reportCommit: FarmSnapshotCommitReporter = FarmSnapshotCommitLog.report

    init(
        callbackEnqueuer: @escaping CallbackEnqueuer = { operation in
            Task { @MainActor in await operation() }
        }
    ) {
        self.callbackEnqueuer = callbackEnqueuer
    }

    deinit {
        canonicalCommitAuthorization?.invalidate()
        canonicalLoadTask?.cancel()
        canonicalLoadWaiters.completeAll()
    }

    func configure(
        printerService: any PrinterServiceProtocol
    ) {
        let changed = !Self.identical(self.printerService, printerService)
        if changed {
            invalidateCanonicalLoad()
            clearFarmDataForNewAuthority()
        }
        self.printerService = printerService
    }

    /// Wire the published #816 snapshot store plus the auto-dispatch source
    /// used for the H6-compliant pending-ready projection, and an injectable
    /// clock for the "last updated" instant. `reportCommit` receives every
    /// canonical commit outcome so a refused write is never silent (#3074).
    func configureSnapshot(
        store: any FarmSnapshotStoring,
        autoPrintService: (any AutoDispatchServiceProtocol)? = nil,
        now: @escaping @Sendable () -> Date = { Date() },
        reportCommit: @escaping FarmSnapshotCommitReporter = FarmSnapshotCommitLog.report
    ) {
        let changed = !Self.identical(self.snapshotStore, store)
            || !Self.identical(self.autoPrintService, autoPrintService)
        if changed {
            invalidateCanonicalLoad()
            clearFarmDataForNewAuthority()
        }
        self.snapshotStore = store
        self.autoPrintService = autoPrintService
        self.now = now
        self.reportCommit = reportCommit
    }

    private func clearFarmDataForNewAuthority() {
        invalidatePendingReadyRefresh()
        printers = []
        pendingReadyPrinterIDs = []
        lastUpdatedAt = nil
        farmSource = .notLoaded
        hasConcludedCanonicalLoad = false
        errorMessage = nil
        farmDataRevision &+= 1
    }

    func configureSignalR(_ service: any SignalRServiceProtocol) {
        let serviceIdentity = ObjectIdentifier(service as AnyObject)
        if signalRServiceIdentity == serviceIdentity, !signalRSubscriptions.isEmpty {
            return
        }
        invalidateCanonicalLoad()
        tearDownSignalR()
        self.signalRService = service
        signalRAuthorityEpoch &+= 1
        let authorityEpoch = signalRAuthorityEpoch
        signalRServiceIdentity = serviceIdentity
        let enqueue = callbackEnqueuer
        signalRSubscriptions.append(service.onPrinterUpdated { [weak self] update in
            enqueue { [weak self] in
                guard let self,
                      self.hasSignalRAuthority(
                        epoch: authorityEpoch,
                        serviceIdentity: serviceIdentity
                      ) else {
                    return
                }
                self.applyPrinterUpdate(update)
            }
        })
        let connectionRegistration = service.onConnectionStateChanged { [weak self] state in
            enqueue { [weak self] in
                guard let self,
                      self.hasSignalRAuthority(
                        epoch: authorityEpoch,
                        serviceIdentity: serviceIdentity
                      ) else {
                    return
                }
                let previous = self.lastObservedConnectionState
                self.lastObservedConnectionState = state
                guard previous == .reconnecting, state == .connected else {
                    return
                }
                self.requestCanonicalLoad(isRecovery: true)
            }
        }
        lastObservedConnectionState = connectionRegistration.initial
        signalRSubscriptions.append(connectionRegistration.subscription)
        synchronizePrinterSubscriptions(for: printers)
    }

    private func tearDownSignalR() {
        signalRAuthorityEpoch &+= 1
        printerSubscriptionSyncGeneration &+= 1
        for subscription in signalRSubscriptions { subscription.cancel() }
        signalRSubscriptions.removeAll(keepingCapacity: true)
        signalRService = nil
        signalRServiceIdentity = nil
        lastObservedConnectionState = nil
    }

    private func hasSignalRAuthority(
        epoch: UInt64,
        serviceIdentity: ObjectIdentifier
    ) -> Bool {
        isViewActive
            && signalRAuthorityEpoch == epoch
            && signalRServiceIdentity == serviceIdentity
    }

    private func applyPrinterUpdate(_ update: PrinterStatusUpdate) {
        guard let idx = printers.firstIndex(where: { $0.id == update.id }) else { return }
        printers[idx].isOnline = update.isOnline
        if let s = update.state { printers[idx].state = s }
        if let prog = update.progress { printers[idx].progress = prog / 100.0 }
        printers[idx].currentLayer = update.currentLayer
        printers[idx].totalLayers = update.totalLayers
        printers[idx].fanSpeedPercent = update.fanSpeedPercent
        printers[idx].liveZOffsetMm = update.liveZOffsetMm
        if let name = update.jobName { printers[idx].jobName = name }
        if let fn = update.fileName { printers[idx].fileName = fn }
        // SignalR omits nullable thumbnail fields when a job ends.
        printers[idx].currentJobThumbnailUrl = update.currentJobThumbnailUrl
        if let hotend = update.hotendTemp { printers[idx].hotendTemp = hotend }
        if let bed = update.bedTemp { printers[idx].bedTemp = bed }
        if let ht = update.hotendTarget { printers[idx].hotendTarget = ht }
        if let bt = update.bedTarget { printers[idx].bedTarget = bt }
        if let spool = update.spoolInfo { printers[idx].spoolInfo = spool }
        farmDataRevision &+= 1
    }

    private func synchronizePrinterSubscriptions(for printers: [Printer]) {
        guard let signalRService, let signalRServiceIdentity else { return }
        printerSubscriptionSyncGeneration &+= 1
        let syncGeneration = printerSubscriptionSyncGeneration
        let printerIDs = printers.map(\.id)
        Task { @MainActor [weak self] in
            guard let self,
                  self.printerSubscriptionSyncGeneration == syncGeneration,
                  self.signalRServiceIdentity == signalRServiceIdentity else {
                return
            }
            await signalRService.replacePrinterSubscriptions(printerIDs)
        }
    }

    /// Hydrate the ACTIVE exact-owner snapshot from the #816 store. Called
    /// before the canonical load on a cold/offline launch so the shell shows
    /// last-confirmed data immediately without a prior-namespace flash. Reading
    /// only via `hydrateActive()` means the store — not this view model —
    /// enforces which namespace's record (if any) is authoritative.
    func hydrateFromCache() async {
        guard let store = snapshotStore, isViewActive else { return }
        let lifecycleEpoch = canonicalLifecycleEpoch
        let storeIdentity = Self.identity(store)
        let hydration = await store.hydrateActive()
        guard isViewActive,
              canonicalLifecycleEpoch == lifecycleEpoch,
              Self.identity(snapshotStore) == storeIdentity else {
            return
        }
        // A canonical load may have already published live data during the await;
        // never downgrade a confirmed live fleet back to cached.
        guard farmSource != .live else { return }
        switch hydration {
        case .snapshot(let envelope):
            printers = Self.reconstructPrinters(from: envelope.payload)
            pendingReadyPrinterIDs = Set(envelope.payload.filter(\.isPendingReady).map(\.id))
            lastUpdatedAt = Date(timeIntervalSince1970: Double(envelope.lastUpdatedAtMillis) / 1000.0)
            farmSource = .cached
            farmDataRevision &+= 1
        case .absent:
            // Authoritative session but no record was ever written: the
            // distinct "no cached data" state, not a present-but-empty fleet.
            farmSource = .absent
        case .inactive, .recovered, .unreadable:
            // Nothing trustworthy to display; leave the canonical load to
            // populate the fleet. Do not surface prior-namespace data.
            break
        }
    }

    func loadDashboard() async {
        guard let waiter = beginCanonicalLoad() else { return }
        await waiter.wait()
    }

    private func beginCanonicalLoad() -> CanonicalLoadWaiter? {
        guard canLoadDashboard else { return nil }
        let enqueue = callbackEnqueuer
        let waiter = canonicalLoadWaiters.registerWaiter { [weak self] in
            enqueue { [weak self] in
                self?.canonicalWaiterCancelled()
            }
        }
        requestCanonicalLoad()
        return waiter
    }

    private var canLoadDashboard: Bool {
        isViewActive && printerService != nil
    }

    private func requestCanonicalLoad(isRecovery: Bool = false) {
        guard canLoadDashboard else { return }
        invalidatePendingReadyRefresh()
        canonicalLoadRequested = true
        if isRecovery {
            canonicalPendingRecoveryDemand = true
        }
        canonicalCommitAuthorization?.invalidate()
        guard canonicalLoadTask == nil else { return }

        let token = UUID()
        canonicalLoadToken = token
        canonicalLoadRequested = false
        canonicalPassHasRecoveryDemand = canonicalPendingRecoveryDemand
        canonicalPendingRecoveryDemand = false
        isLoading = true
        errorMessage = nil
        let authority = makeCanonicalAuthority(token: token)
        startCanonicalPass(authority: authority)
    }

    private func startCanonicalPass(authority: CanonicalAuthority) {
        guard isCanonicalLoadCurrent(authority),
              let printerService else {
            return
        }
        invalidatePendingReadyRefresh()
        let input = CanonicalLoadInput(
            printerService: printerService,
            autoPrintService: autoPrintService,
            snapshotStore: snapshotStore,
            pendingReadyPrinterIDs: pendingReadyPrinterIDs,
            now: now,
            reportCommit: reportCommit,
            logger: logger
        )
        let installAuthorization: @MainActor @Sendable (
            FarmSnapshotCommitAuthorization
        ) -> Bool = { [weak self] authorization in
            guard let self, self.isCanonicalPassCurrent(authority) else {
                authorization.invalidate()
                return false
            }
            self.canonicalCommitAuthorization = authorization
            return true
        }
        let taskTracker = canonicalTaskTracker
        taskTracker.taskStarted()
        canonicalLoadTask = Task { [weak self, taskTracker, input] in
            defer { taskTracker.taskFinished() }
            let result = await Self.loadCanonicalSnapshot(
                input: input,
                installAuthorization: installAuthorization
            )
            self?.completeCanonicalPass(result, authority: authority)
        }
    }

    private func completeCanonicalPass(
        _ result: CanonicalLoadResult,
        authority: CanonicalAuthority
    ) {
        guard isCanonicalLoadCurrent(authority) else { return }
        if canonicalLoadRequested {
            canonicalLoadRequested = false
            canonicalPassHasRecoveryDemand = canonicalPendingRecoveryDemand
            canonicalPendingRecoveryDemand = false
            canonicalCommitAuthorization = nil
            startCanonicalPass(authority: authority)
            return
        }

        switch result {
        case .success(let snapshot):
            publish(snapshot)
            hasConcludedCanonicalLoad = true
        case .failure(let error):
            handleLoadFailure(error)
            hasConcludedCanonicalLoad = true
        case .superseded:
            // Cancelled or replaced by a newer pass: nothing was answered, so
            // this must not license the offline banner.
            break
        }
        finishCanonicalLoad(authority: authority)
    }

    nonisolated private static func loadCanonicalSnapshot(
        input: CanonicalLoadInput,
        installAuthorization: @MainActor @Sendable (
            FarmSnapshotCommitAuthorization
        ) -> Bool
    ) async -> CanonicalLoadResult {
        let capturedSession = await input.snapshotStore?.currentSession()
        guard !Task.isCancelled else { return .superseded }
        let commitAuthorization: FarmSnapshotCommitAuthorization?
        if let store = input.snapshotStore, let capturedSession {
            commitAuthorization = await store.authorizeCommit(capturedSession: capturedSession)
            guard !Task.isCancelled,
                  let commitAuthorization,
                  await installAuthorization(commitAuthorization) else {
                commitAuthorization?.invalidate()
                return .superseded
            }
        } else {
            commitAuthorization = nil
        }

        do {
            let loadedPrinters = try await input.printerService.list()
            guard !Task.isCancelled else { return .superseded }

            var loadedPendingReady = input.pendingReadyPrinterIDs
            if let autoPrintService = input.autoPrintService {
                do {
                    let statuses = try await autoPrintService.getAllStatus()
                    guard !Task.isCancelled else { return .superseded }
                    loadedPendingReady = Set(
                        statuses.printers.filter { $0.state == "PendingReady" }.map(\.printerId)
                    )
                } catch {
                    guard !Task.isCancelled else { return .superseded }
                    input.logger.info("Auto-dispatch status unavailable: \(error.localizedDescription)")
                }
            }

            let instant = input.now()
            if let store = input.snapshotStore,
               let session = capturedSession,
               let commitAuthorization {
                // #3074: the store orders by `writeOrder`, minted here at the
                // confirmation point; the wall-clock millis are display-only, so a
                // backward clock step cannot make it refuse this confirmed-live write.
                let envelope = FarmSnapshotEnvelope(
                    namespace: session.namespace,
                    printers: loadedPrinters,
                    pendingReadyPrinterIDs: loadedPendingReady,
                    lastUpdatedAtMillis: Int64((instant.timeIntervalSince1970 * 1000).rounded()),
                    writeOrder: .next()
                )
                let commitResult = await store.commit(
                    envelope,
                    capturedSession: session,
                    authorization: commitAuthorization
                )
                input.reportCommit(commitResult)
                guard !Task.isCancelled,
                      commitAuthorization.withAuthorization({ true }) == true else {
                    return .superseded
                }
            }

            return .success(
                CanonicalSnapshot(
                    printers: loadedPrinters,
                    pendingReadyPrinterIDs: loadedPendingReady,
                    lastUpdatedAt: instant
                )
            )
        } catch {
            guard !Task.isCancelled else { return .superseded }
            // Defend the invariant structurally rather than by timing. A
            // cancellation answered nothing, so it must never reach `.failure`,
            // which concludes the canonical pass. `Task.isCancelled` covers the
            // ordinary case, but a nested sub-task cancelled without the parent
            // would surface `URLError(.cancelled)` with the parent still live.
            // Keeps all three read-cache view models consistent.
            guard !isCancellationError(error) else { return .superseded }
            return .failure(error)
        }
    }

    private func publish(_ snapshot: CanonicalSnapshot) {
        printers = snapshot.printers
        invalidatePendingReadyRefresh()
        pendingReadyPrinterIDs = snapshot.pendingReadyPrinterIDs
        lastUpdatedAt = snapshot.lastUpdatedAt
        farmSource = .live
        errorMessage = nil
        farmDataRevision &+= 1
        synchronizePrinterSubscriptions(for: snapshot.printers)
    }

    func refreshPendingReadyStatus() async {
        guard isViewActive, let autoPrintService else { return }
        pendingReadyRefreshGeneration &+= 1
        let refreshGeneration = pendingReadyRefreshGeneration
        let serviceIdentity = Self.identity(autoPrintService)
        do {
            let statuses = try await autoPrintService.getAllStatus()
            guard isViewActive,
                  pendingReadyRefreshGeneration == refreshGeneration,
                  Self.identity(self.autoPrintService) == serviceIdentity else {
                return
            }
            pendingReadyPrinterIDs = Set(
                statuses.printers.filter { $0.state == "PendingReady" }.map(\.printerId)
            )
            farmDataRevision &+= 1
        } catch {
            guard isViewActive,
                  pendingReadyRefreshGeneration == refreshGeneration,
                  Self.identity(self.autoPrintService) == serviceIdentity else {
                return
            }
            logger.info("Auto-dispatch status unavailable: \(error.localizedDescription)")
        }
    }

    private func invalidatePendingReadyRefresh() {
        pendingReadyRefreshGeneration &+= 1
    }

    private func finishCanonicalLoad(authority: CanonicalAuthority) {
        guard isCanonicalLoadCurrent(authority) else { return }
        canonicalLoadTask = nil
        canonicalLoadToken = nil
        canonicalLoadRequested = false
        canonicalPassHasRecoveryDemand = false
        canonicalPendingRecoveryDemand = false
        canonicalCommitAuthorization = nil
        isLoading = false
        canonicalLoadWaiters.completeAll()
    }

    private func invalidateCanonicalLoad() {
        canonicalLifecycleEpoch &+= 1
        invalidatePendingReadyRefresh()
        canonicalCommitAuthorization?.invalidate()
        canonicalCommitAuthorization = nil
        canonicalLoadTask?.cancel()
        canonicalLoadTask = nil
        canonicalLoadToken = nil
        canonicalLoadRequested = false
        canonicalPassHasRecoveryDemand = false
        canonicalPendingRecoveryDemand = false
        isLoading = false
        canonicalLoadWaiters.completeAll()
    }

    private func canonicalWaiterCancelled() {
        guard canonicalLoadWaiters.activeCount == 0,
              !canonicalPassHasRecoveryDemand,
              !canonicalPendingRecoveryDemand else {
            return
        }
        invalidateCanonicalLoad()
    }

    private func makeCanonicalAuthority(token: UUID) -> CanonicalAuthority {
        CanonicalAuthority(
            token: token,
            lifecycleEpoch: canonicalLifecycleEpoch,
            printerServiceIdentity: Self.identity(printerService),
            autoPrintServiceIdentity: Self.identity(autoPrintService),
            snapshotStoreIdentity: Self.identity(snapshotStore)
        )
    }

    private func isCanonicalLoadCurrent(_ authority: CanonicalAuthority) -> Bool {
        isViewActive
            && canonicalLoadToken == authority.token
            && canonicalLifecycleEpoch == authority.lifecycleEpoch
            && Self.identity(printerService) == authority.printerServiceIdentity
            && Self.identity(autoPrintService) == authority.autoPrintServiceIdentity
            && Self.identity(snapshotStore) == authority.snapshotStoreIdentity
    }

    private func isCanonicalPassCurrent(_ authority: CanonicalAuthority) -> Bool {
        isCanonicalLoadCurrent(authority) && !canonicalLoadRequested
    }

    private static func identity<T>(_ value: T?) -> ObjectIdentifier? {
        value.map { ObjectIdentifier($0 as AnyObject) }
    }

    private static func identical<T>(_ lhs: T?, _ rhs: T?) -> Bool {
        identity(lhs) == identity(rhs)
    }

#if DEBUG
    func beginCanonicalLoadForTesting() -> CanonicalLoadWaiter? {
        beginCanonicalLoad()
    }

    var canonicalWaiterCountForTesting: Int {
        canonicalLoadWaiters.activeCount
    }

    func waitForCanonicalWaiterCount(_ count: Int) async {
        await canonicalLoadWaiters.waitForActiveCount(count)
    }

    func waitForCanonicalLoadToBecomeIdle() async {
        await canonicalTaskTracker.waitForIdle()
    }

    func waitForSupersededCanonicalLoads() async {
        await canonicalTaskTracker.waitForIdle()
    }
#endif

    private struct CanonicalAuthority {
        let token: UUID
        let lifecycleEpoch: UInt64
        let printerServiceIdentity: ObjectIdentifier?
        let autoPrintServiceIdentity: ObjectIdentifier?
        let snapshotStoreIdentity: ObjectIdentifier?
    }

    private struct CanonicalSnapshot {
        let printers: [Printer]
        let pendingReadyPrinterIDs: Set<UUID>
        let lastUpdatedAt: Date
    }

    private struct CanonicalLoadInput: Sendable {
        let printerService: any PrinterServiceProtocol
        let autoPrintService: (any AutoDispatchServiceProtocol)?
        let snapshotStore: (any FarmSnapshotStoring)?
        let pendingReadyPrinterIDs: Set<UUID>
        let now: @Sendable () -> Date
        let reportCommit: FarmSnapshotCommitReporter
        let logger: Logger
    }

    private enum CanonicalLoadResult {
        case success(CanonicalSnapshot)
        case failure(Error)
        case superseded
    }

    // MARK: - Snapshot commit / failure handling (#817)

    /// A canonical load failed. Preserve a valid cached shell (stale) instead of
    /// replacing it — a `.preserve` outcome never touches the durable record and
    /// must never clear the read-only cache. With no cache to fall back on, the
    /// error surfaces so the empty/error/absent shell can offer recovery.
    private func handleLoadFailure(_ error: Error) {
        let outcome = Self.classify(error)
        switch farmSource {
        case .cached:
            // Already showing last-confirmed data: keep it read-only. The stale
            // banner + timestamp convey the degraded state; no blocking error.
            logger.info("Canonical load failed while cached (\(String(describing: outcome))); preserving stale shell.")
        case .absent, .notLoaded, .live:
            errorMessage = error.localizedDescription
        }
    }

    /// Read-only classification of a load failure into the #816 outcome taxonomy
    /// so the preserve-vs-apply decision is explicit and unit-testable.
    static func classify(_ error: Error) -> FarmLoadOutcome {
        if error is CancellationError { return .cancelled }
        guard let net = error as? NetworkError else { return .serverError }
        switch net {
        case .noConnection, .timeout, .serverUnreachable, .transportError:
            return .offline
        case .unauthorized, .authFailed:
            return .unauthorized
        case .forbidden:
            return .forbidden
        case .decodingFailed:
            return .decodeFailure
        case .invalidURL, .invalidResponse, .notFound, .featureDisabled,
             .methodNotAllowed, .conflict, .partsInventoryConflict,
             .preconditionFailed, .preconditionRequired,
             .clientError, .serverError, .unexpectedStatus, .staleServerResponse,
             .insecureTransportBlocked, .certificateChanged, .certificateNotTrusted:
            return .serverError
        }
    }

    func isPendingReady(_ printer: Printer) -> Bool {
        pendingReadyPrinterIDs.contains(printer.id)
    }

    func synchronizeFarmData(to projection: PrinterListViewModel) {
        projection.setFarmData(printers, pendingReadyPrinterIDs: pendingReadyPrinterIDs)
    }

    // MARK: - Snapshot → Printer projection

    /// Reconstruct display `Printer` values from the non-secret cached
    /// projection. `Printer` exposes only a decoding initializer, so we rebuild
    /// through its canonical JSON contract — keeping `Models.swift` untouched and
    /// guaranteeing the cached card renders through the exact same code path as a
    /// live card (online parity). Progress is re-scaled ×100 because the decoder
    /// normalizes 0–100 → 0–1.0.
    static func reconstructPrinters(from payload: [FarmSnapshotPrinter]) -> [Printer] {
        payload.compactMap(reconstructPrinter(from:))
    }

    static func reconstructPrinter(from p: FarmSnapshotPrinter) -> Printer? {
        var dict: [String: Any] = [
            "id": p.id.uuidString,
            "name": p.name,
            "isOnline": p.isOnline,
            "isEnabled": p.isEnabled,
            "inMaintenance": p.inMaintenance,
            "obicoEnabled": p.obicoEnabled
        ]
        if let v = p.modelName { dict["modelName"] = v }
        if let v = p.manufacturerName { dict["manufacturerName"] = v }
        if let v = p.state { dict["state"] = v }
        if let v = p.progress { dict["progress"] = v * 100.0 }
        if let v = p.jobName { dict["jobName"] = v }
        if let v = p.fileName { dict["fileName"] = v }
        if let v = p.hotendTemp { dict["hotendTemp"] = v }
        if let v = p.hotendTarget { dict["hotendTarget"] = v }
        if let v = p.bedTemp { dict["bedTemp"] = v }
        if let v = p.bedTarget { dict["bedTarget"] = v }
        if let loc = p.location {
            var l: [String: Any] = ["id": loc.id.uuidString, "name": loc.name]
            if let d = loc.description { l["description"] = d }
            dict["location"] = l
        }
        if let s = p.spool {
            var sd: [String: Any] = ["hasActiveSpool": s.hasActiveSpool]
            if let v = s.activeSpoolId { sd["activeSpoolId"] = v }
            if let v = s.spoolName { sd["spoolName"] = v }
            if let v = s.material { sd["material"] = v }
            if let v = s.colorHex { sd["colorHex"] = v }
            if let v = s.filamentName { sd["filamentName"] = v }
            if let v = s.vendor { sd["vendor"] = v }
            if let v = s.remainingWeightG { sd["remainingWeightG"] = v }
            if let v = s.spoolInUse { sd["spoolInUse"] = v }
            dict["spoolInfo"] = sd
        }
        guard let data = try? JSONSerialization.data(withJSONObject: dict) else { return nil }
        return try? JSONDecoder().decode(Printer.self, from: data)
    }

    // MARK: - Computed Summaries

    var onlineCount: Int { printers.filter(\.isOnline).count }

    var printingCount: Int {
        printers.filter { $0.state?.lowercased() == "printing" }.count
    }

    var pausedCount: Int {
        printers.filter { $0.state?.lowercased() == "paused" }.count
    }

    var offlineCount: Int { printers.filter { !$0.isOnline }.count }

    var errorCount: Int {
        printers.filter { $0.state?.lowercased() == "error" }.count
    }

    var maintenanceCount: Int {
        printers.filter(\.inMaintenance).count
    }

    var hasMaintenanceAlerts: Bool { maintenanceCount > 0 }

    var printersInMaintenance: [Printer] {
        printers.filter(\.inMaintenance)
    }

    var activePrintingPrinters: [Printer] {
        printers.filter { $0.state?.lowercased() == "printing" }
            .sorted { sortPriority($0) < sortPriority($1) }
    }
    
    private func sortPriority(_ printer: Printer) -> Int {
        guard printer.isOnline else { return 100 }
        switch printer.state?.lowercased() {
        case "pendingready": return 0
        case "printing": return 1
        case "ready", "idle": return 2
        default: return 3
        }
    }
}
