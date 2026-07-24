import Foundation

@MainActor @Observable
final class PrinterListViewModel {
    typealias CallbackEnqueuer = @Sendable (
        @escaping @MainActor @Sendable () async -> Void
    ) -> Void

    var printers: [Printer] = []
    var locations: [Location] = []
    var autoDispatchStatuses: [UUID: AutoDispatchStatus] = [:]
    var isLoading = false
    var errorMessage: String?
    var searchText: String = ""
    var selectedStatus: StatusFilter = .all
    var selectedLocationId: UUID?

    enum StatusFilter: String, CaseIterable, Identifiable {
        case all = "All"
        case online = "Online"
        case printing = "Printing"
        case offline = "Offline"
        case error = "Error"

        var id: String { rawValue }
    }

    private var printerService: (any PrinterServiceProtocol)?
    private var autoPrintService: (any AutoDispatchServiceProtocol)?
    private var signalRService: (any SignalRServiceProtocol)?
    @ObservationIgnored private var signalRSubscriptions: [SignalRSubscription] = []
    @ObservationIgnored private var signalRServiceIdentity: ObjectIdentifier?
    @ObservationIgnored private var signalRAuthorityEpoch: UInt64 = 0
    @ObservationIgnored private var lastObservedConnectionState: SignalRConnectionState?
    @ObservationIgnored private let callbackEnqueuer: CallbackEnqueuer
    @ObservationIgnored private var isActive = true
    @ObservationIgnored private var canonicalLifecycleEpoch: UInt64 = 0
    @ObservationIgnored private var canonicalLoadToken: UUID?
    @ObservationIgnored private var canonicalLoadTask: Task<Void, Never>?
    @ObservationIgnored private var canonicalLoadRequested = false
    @ObservationIgnored private var canonicalLoadWaiters: [UUID: CheckedContinuation<Void, Never>] = [:]
    @ObservationIgnored private var autoStatusLoadToken: UUID?
#if DEBUG
    @ObservationIgnored private var canonicalIdleWaiters: [CheckedContinuation<Void, Never>] = []
    @ObservationIgnored private var retiredCanonicalLoadTasks: [Task<Void, Never>] = []
#endif

    init(
        callbackEnqueuer: @escaping CallbackEnqueuer = { operation in
            Task { @MainActor in await operation() }
        }
    ) {
        self.callbackEnqueuer = callbackEnqueuer
    }

    func configure(printerService: any PrinterServiceProtocol, autoPrintService: any AutoDispatchServiceProtocol) {
        let changed = !Self.identical(self.printerService, printerService)
            || !Self.identical(self.autoPrintService, autoPrintService)
        if changed {
            invalidateCanonicalLoad()
        }
        self.printerService = printerService
        self.autoPrintService = autoPrintService
    }

    func activate() {
        isActive = true
    }

    func deactivate() {
        guard isActive else { return }
        isActive = false
        invalidateCanonicalLoad()
        tearDownSignalR()
    }

    func configureSignalR(_ service: any SignalRServiceProtocol) {
        guard isActive else { return }
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
                self.applyListUpdate(update)
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
                self.requestCanonicalLoad()
            }
        }
        lastObservedConnectionState = connectionRegistration.initial
        signalRSubscriptions.append(connectionRegistration.subscription)
    }

    private func tearDownSignalR() {
        signalRAuthorityEpoch &+= 1
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
        isActive
            && signalRAuthorityEpoch == epoch
            && signalRServiceIdentity == serviceIdentity
    }

    private func applyListUpdate(_ update: PrinterStatusUpdate) {
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

    func loadPrinters() async {
        guard canLoadPrinters else { return }
        let waiterID = UUID()
        await withCheckedContinuation { continuation in
            canonicalLoadWaiters[waiterID] = continuation
            requestCanonicalLoad()
        }
    }

    private var canLoadPrinters: Bool {
        isActive && printerService != nil
    }

    private func requestCanonicalLoad() {
        guard canLoadPrinters else { return }
        canonicalLoadRequested = true
        guard canonicalLoadTask == nil else { return }

        let token = UUID()
        canonicalLoadToken = token
        canonicalLoadRequested = false
        autoStatusLoadToken = nil
        isLoading = true
        errorMessage = nil
        let authority = makeCanonicalAuthority(token: token)
        canonicalLoadTask = Task { [weak self] in
            await self?.runCanonicalLoad(authority: authority)
        }
    }

    private func runCanonicalLoad(authority: CanonicalAuthority) async {
        while isCanonicalLoadCurrent(authority) {
            let result = await loadCanonicalPrinters(authority: authority)
            guard isCanonicalLoadCurrent(authority) else { return }
            if canonicalLoadRequested {
                canonicalLoadRequested = false
                autoStatusLoadToken = nil
                continue
            }

            switch result {
            case .success(let snapshot):
                printers = snapshot.printers
                if autoStatusLoadToken == nil {
                    autoDispatchStatuses = snapshot.autoDispatchStatuses
                }
                errorMessage = nil
            case .failure(let error):
                errorMessage = error.localizedDescription
            case .superseded:
                continue
            }
            finishCanonicalLoad(authority: authority)
            return
        }
    }

    private func loadCanonicalPrinters(authority: CanonicalAuthority) async -> CanonicalLoadResult {
        guard let printerService else { return .superseded }
        do {
            let loadedPrinters = try await printerService.list()
            guard isCanonicalPassCurrent(authority) else { return .superseded }
            var loadedStatuses = autoDispatchStatuses
            if let autoPrintService {
                do {
                    let statuses = try await autoPrintService.getAllStatus()
                    guard isCanonicalPassCurrent(authority) else { return .superseded }
                    loadedStatuses = Dictionary(
                        uniqueKeysWithValues: statuses.printers.map { ($0.printerId, $0) }
                    )
                } catch {
                    guard isCanonicalPassCurrent(authority) else { return .superseded }
                }
            }
            return .success(
                CanonicalSnapshot(
                    printers: loadedPrinters,
                    autoDispatchStatuses: loadedStatuses
                )
            )
        } catch {
            guard isCanonicalPassCurrent(authority) else { return .superseded }
            return .failure(error)
        }
    }

    private func finishCanonicalLoad(authority: CanonicalAuthority) {
        guard isCanonicalLoadCurrent(authority) else { return }
        canonicalLoadTask = nil
        canonicalLoadToken = nil
        canonicalLoadRequested = false
        isLoading = false
        resumeCanonicalWaiters()
        resumeCanonicalIdleWaiters()
    }

    private func invalidateCanonicalLoad() {
        canonicalLifecycleEpoch &+= 1
#if DEBUG
        if let canonicalLoadTask {
            retiredCanonicalLoadTasks.append(canonicalLoadTask)
        }
#endif
        canonicalLoadTask?.cancel()
        canonicalLoadTask = nil
        canonicalLoadToken = nil
        canonicalLoadRequested = false
        autoStatusLoadToken = nil
        isLoading = false
        resumeCanonicalWaiters()
        resumeCanonicalIdleWaiters()
    }

    private func resumeCanonicalWaiters() {
        let waiters = canonicalLoadWaiters.values
        canonicalLoadWaiters.removeAll(keepingCapacity: true)
        for waiter in waiters {
            waiter.resume()
        }
    }

    func loadAutoDispatchStatuses() async {
        guard isActive, let autoPrintService else { return }
        let token = UUID()
        autoStatusLoadToken = token
        let lifecycleEpoch = canonicalLifecycleEpoch
        let serviceIdentity = Self.identity(autoPrintService)
        do {
            let statuses = try await autoPrintService.getAllStatus()
            guard isActive,
                  autoStatusLoadToken == token,
                  canonicalLifecycleEpoch == lifecycleEpoch,
                  Self.identity(self.autoPrintService) == serviceIdentity else {
                return
            }
            autoDispatchStatuses = Dictionary(uniqueKeysWithValues: statuses.printers.map { ($0.printerId, $0) })
        } catch {
            // Non-critical — cards will fall back to printer state
        }
    }

    private func makeCanonicalAuthority(token: UUID) -> CanonicalAuthority {
        CanonicalAuthority(
            token: token,
            lifecycleEpoch: canonicalLifecycleEpoch,
            printerServiceIdentity: Self.identity(printerService),
            autoPrintServiceIdentity: Self.identity(autoPrintService)
        )
    }

    private func isCanonicalLoadCurrent(_ authority: CanonicalAuthority) -> Bool {
        isActive
            && canonicalLoadToken == authority.token
            && canonicalLifecycleEpoch == authority.lifecycleEpoch
            && Self.identity(printerService) == authority.printerServiceIdentity
            && Self.identity(autoPrintService) == authority.autoPrintServiceIdentity
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
    func waitForCanonicalLoadToBecomeIdle() async {
        guard canonicalLoadTask != nil else { return }
        await withCheckedContinuation { continuation in
            canonicalIdleWaiters.append(continuation)
        }
    }

    func waitForSupersededCanonicalLoads() async {
        for task in retiredCanonicalLoadTasks {
            await task.value
        }
        retiredCanonicalLoadTasks.removeAll(keepingCapacity: true)
    }

    private func resumeCanonicalIdleWaiters() {
        let waiters = canonicalIdleWaiters
        canonicalIdleWaiters.removeAll(keepingCapacity: true)
        for waiter in waiters {
            waiter.resume()
        }
    }
#else
    private func resumeCanonicalIdleWaiters() {}
#endif

    private struct CanonicalAuthority {
        let token: UUID
        let lifecycleEpoch: UInt64
        let printerServiceIdentity: ObjectIdentifier?
        let autoPrintServiceIdentity: ObjectIdentifier?
    }

    private struct CanonicalSnapshot {
        let printers: [Printer]
        let autoDispatchStatuses: [UUID: AutoDispatchStatus]
    }

    private enum CanonicalLoadResult {
        case success(CanonicalSnapshot)
        case failure(Error)
        case superseded
    }

    // MARK: - Filtered Results

    var filteredPrinters: [Printer] {
        printers.filter { printer in
            matchesSearch(printer) && matchesStatus(printer) && matchesLocation(printer)
        }
        .sorted { sortPriority($0) < sortPriority($1) }
    }

    func isPendingReady(_ printer: Printer) -> Bool {
        autoDispatchStatuses[printer.id]?.state == "PendingReady"
    }

    private func sortPriority(_ printer: Printer) -> Int {
        // PendingReady always sorts to top regardless of isOnline
        if isPendingReady(printer) { return 0 }
        guard printer.isOnline else { return 100 }
        switch printer.state?.lowercased() {
        case "printing": return 1
        case "ready", "idle": return 2
        default: return 3
        }
    }

    private func matchesSearch(_ printer: Printer) -> Bool {
        guard !searchText.isEmpty else { return true }
        return printer.name.localizedCaseInsensitiveContains(searchText)
    }

    private func matchesStatus(_ printer: Printer) -> Bool {
        switch selectedStatus {
        case .all: return true
        case .online: return printer.isOnline
        case .printing: return printer.state?.lowercased() == "printing"
        case .offline: return !printer.isOnline
        case .error: return printer.state?.lowercased() == "error"
        }
    }

    private func matchesLocation(_ printer: Printer) -> Bool {
        guard let locationId = selectedLocationId else { return true }
        return printer.location?.id == locationId
    }

    /// Unique locations from loaded printers.
    var availableLocations: [LocationSummary] {
        let seen = NSMutableSet()
        return printers.compactMap(\.location).filter { loc in
            guard !seen.contains(loc.id) else { return false }
            seen.add(loc.id)
            return true
        }
    }
}
