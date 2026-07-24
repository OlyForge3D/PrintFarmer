import Foundation
import os

@MainActor @Observable
final class DashboardViewModel {
    typealias CallbackEnqueuer = @Sendable (
        @escaping @MainActor @Sendable () async -> Void
    ) -> Void

    var printers: [Printer] = []
    var queueOverview: [QueueOverview] = []
    var activeJobs: [QueuedPrintJobResponse] = []
    var summary: StatisticsSummary?
    var queueStats: QueueStats?
    var modelStats: [QueuePrinterModelStats] = []
    var upcomingJobs: [QueuedJobWithMeta] = []
    var isLoading = false
    var errorMessage: String?
    var isViewActive = true {
        didSet {
            if oldValue && !isViewActive {
                signalRAuthorityEpoch &+= 1
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
    var isReadOnly: Bool { isStale }

    /// The immutable UTC instant of the last successful canonical response now
    /// on screen (live) or backing the cached snapshot (stale). `nil` until the
    /// first hydrate/commit resolves.
    private(set) var lastUpdatedAt: Date?

    /// True once an authoritative session exists but its record is absent — the
    /// "no cached data yet" state, distinct from a present-but-empty fleet.
    var hasNoCachedData: Bool { farmSource == .absent }

    private let logger = Logger(subsystem: "com.printfarmer.ios", category: "Dashboard")

    private var printerService: (any PrinterServiceProtocol)?
    private var jobService: (any JobServiceProtocol)?
    private var statisticsService: (any StatisticsServiceProtocol)?
    private var jobAnalyticsService: (any JobAnalyticsServiceProtocol)?
    private var autoPrintService: (any AutoDispatchServiceProtocol)?
    private var signalRService: (any SignalRServiceProtocol)?
    @ObservationIgnored private var signalRSubscriptions: [SignalRSubscription] = []
    @ObservationIgnored private var signalRServiceIdentity: ObjectIdentifier?
    @ObservationIgnored private var signalRAuthorityEpoch: UInt64 = 0
    @ObservationIgnored private var lastObservedConnectionState: SignalRConnectionState?
    @ObservationIgnored private let callbackEnqueuer: CallbackEnqueuer

    // Snapshot lifecycle authority (#816), consumed unchanged.
    @ObservationIgnored private var snapshotStore: (any FarmSnapshotStoring)?
    @ObservationIgnored private var now: @Sendable () -> Date = { Date() }

    init(
        callbackEnqueuer: @escaping CallbackEnqueuer = { operation in
            Task { @MainActor in await operation() }
        }
    ) {
        self.callbackEnqueuer = callbackEnqueuer
    }

    func configure(
        printerService: any PrinterServiceProtocol,
        jobService: any JobServiceProtocol,
        statisticsService: any StatisticsServiceProtocol,
        jobAnalyticsService: any JobAnalyticsServiceProtocol
    ) {
        self.printerService = printerService
        self.jobService = jobService
        self.statisticsService = statisticsService
        self.jobAnalyticsService = jobAnalyticsService
    }

    /// Wire the published #816 snapshot store plus the auto-dispatch source
    /// used for the H6-compliant pending-ready projection, and an injectable
    /// clock so hydrate/commit ordering is deterministic under test.
    func configureSnapshot(
        store: any FarmSnapshotStoring,
        autoPrintService: (any AutoDispatchServiceProtocol)? = nil,
        now: @escaping @Sendable () -> Date = { Date() }
    ) {
        self.snapshotStore = store
        self.autoPrintService = autoPrintService
        self.now = now
    }

    func configureSignalR(_ service: any SignalRServiceProtocol) {
        self.signalRService = service
        for subscription in signalRSubscriptions { subscription.cancel() }
        signalRSubscriptions.removeAll(keepingCapacity: true)
        signalRAuthorityEpoch &+= 1
        let authorityEpoch = signalRAuthorityEpoch
        let serviceIdentity = ObjectIdentifier(service as AnyObject)
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
                await self.loadDashboard()
            }
        }
        lastObservedConnectionState = connectionRegistration.initial
        signalRSubscriptions.append(connectionRegistration.subscription)
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
        if let name = update.jobName { printers[idx].jobName = name }
        if let fn = update.fileName { printers[idx].fileName = fn }
        if let hotend = update.hotendTemp { printers[idx].hotendTemp = hotend }
        if let bed = update.bedTemp { printers[idx].bedTemp = bed }
        if let ht = update.hotendTarget { printers[idx].hotendTarget = ht }
        if let bt = update.bedTarget { printers[idx].bedTarget = bt }
        if let spool = update.spoolInfo { printers[idx].spoolInfo = spool }
    }

    /// Hydrate the ACTIVE exact-owner snapshot from the #816 store. Called
    /// before the canonical load on a cold/offline launch so the shell shows
    /// last-confirmed data immediately without a prior-namespace flash. Reading
    /// only via `hydrateActive()` means the store — not this view model —
    /// enforces which namespace's record (if any) is authoritative.
    func hydrateFromCache() async {
        guard let store = snapshotStore, isViewActive else { return }
        let hydration = await store.hydrateActive()
        guard isViewActive else { return }
        // A canonical load may have already published live data during the await;
        // never downgrade a confirmed live fleet back to cached.
        guard farmSource != .live else { return }
        switch hydration {
        case .snapshot(let envelope):
            printers = Self.reconstructPrinters(from: envelope.payload)
            pendingReadyPrinterIDs = Set(envelope.payload.filter(\.isPendingReady).map(\.id))
            lastUpdatedAt = Date(timeIntervalSince1970: Double(envelope.lastUpdatedAtMillis) / 1000.0)
            farmSource = .cached
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
        guard let printerService, let jobService, isViewActive else { return }
        isLoading = true
        errorMessage = nil

        // Capture the authoritative session BEFORE the network round-trip so the
        // commit is validated against the session in force when the load began.
        let capturedSession = await snapshotStore?.currentSession()

        do {
            async let printersTask = printerService.list()
            async let queueTask = jobService.list()
            async let allJobsTask = jobService.listAllJobs()
            let p = try await printersTask
            let q = try await queueTask
            let allJobs = try await allJobsTask
            guard isViewActive else { return }
            printers = p
            queueOverview = q
            activeJobs = allJobs.filter {
                guard let status = $0.job.jobStatus else { return false }
                return [.printing, .starting, .paused].contains(status)
            }
            await refreshPendingReady()

            do {
                let s = try await statisticsService?.getSummary()
                guard isViewActive else { return }
                summary = s
            } catch {
                logger.warning("Failed to load statistics summary: \(error.localizedDescription)")
            }

            // Load farm status data
            do {
                async let statsTask = jobAnalyticsService?.getStats()
                async let modelStatsTask = jobAnalyticsService?.getModelStats()
                async let upcomingTask = jobAnalyticsService?.getQueuedJobs(
                    filterStatus: "queued",
                    filterModel: nil,
                    filterMaterial: nil,
                    limit: 5,
                    offset: 0
                )
                let qs = try await statsTask
                let ms = try await modelStatsTask ?? []
                let uj = try await upcomingTask ?? []
                guard isViewActive else { return }
                queueStats = qs
                modelStats = ms
                upcomingJobs = uj
            } catch {
                logger.warning("Failed to load farm status data: \(error.localizedDescription)")
            }

            // Canonical response confirmed: publish it live, atomically replace
            // the cached snapshot/timestamp once, and clear any stale shell.
            await commitCanonicalSnapshot(printers, capturedSession: capturedSession)
        } catch {
            guard isViewActive else { return }
            handleLoadFailure(error)
        }

        guard isViewActive else { return }
        isLoading = false
    }

    // MARK: - Snapshot commit / failure handling (#817)

    /// Mark the on-screen fleet confirmed-live and persist it. The visible data
    /// is authoritative regardless of whether the durable write lands, so live
    /// state and `lastUpdatedAt` are published even if the store rejects/misses
    /// the commit (older-or-equal, superseded, persistence failure).
    private func commitCanonicalSnapshot(_ printers: [Printer], capturedSession: FarmSnapshotSession?) async {
        let instant = now()
        farmSource = .live
        lastUpdatedAt = instant
        errorMessage = nil
        guard let store = snapshotStore, let session = capturedSession else { return }
        let envelope = FarmSnapshotEnvelope(
            namespace: session.namespace,
            printers: printers,
            pendingReadyPrinterIDs: pendingReadyPrinterIDs,
            lastUpdatedAtMillis: Int64((instant.timeIntervalSince1970 * 1000).rounded())
        )
        _ = await store.commit(envelope, capturedSession: session)
    }

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
             .clientError, .serverError, .unexpectedStatus, .staleServerResponse:
            return .serverError
        }
    }

    /// Refresh the H6-compliant pending-ready projection from the authoritative
    /// auto-dispatch source. Non-critical: cards fall back to plain state on error.
    private func refreshPendingReady() async {
        guard let autoPrintService else { return }
        do {
            let statuses = try await autoPrintService.getAllStatus()
            guard isViewActive else { return }
            pendingReadyPrinterIDs = Set(
                statuses.printers.filter { $0.state == "PendingReady" }.map(\.printerId)
            )
        } catch {
            logger.info("Auto-dispatch status unavailable: \(error.localizedDescription)")
        }
    }

    func isPendingReady(_ printer: Printer) -> Bool {
        pendingReadyPrinterIDs.contains(printer.id)
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

    var activeJobCount: Int {
        queueOverview.filter { $0.currentJobId != nil }.count
    }

    var queuedJobCount: Int {
        queueOverview.reduce(0) { $0 + $1.queuedJobsCount }
    }

    var hasMaintenanceAlerts: Bool { maintenanceCount > 0 }

    var printersInMaintenance: [Printer] {
        printers.filter(\.inMaintenance)
    }

    // MARK: - Farm Status Helpers

    func activeJobForPrinter(_ printerId: UUID) -> QueuedPrintJobResponse? {
        let idString = printerId.uuidString
        return activeJobs.first { $0.job.assignedPrinterId?.caseInsensitiveCompare(idString) == .orderedSame }
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
