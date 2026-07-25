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
    @ObservationIgnored private var canonicalPassHasRecoveryDemand = false
    @ObservationIgnored private var canonicalPendingRecoveryDemand = false
    @ObservationIgnored private let canonicalLoadWaiters = CanonicalLoadWaiterRegistry()
    @ObservationIgnored private let canonicalTaskTracker = CanonicalLoadTaskTracker()
    @ObservationIgnored private var autoStatusLoadToken: UUID?

    init(
        callbackEnqueuer: @escaping CallbackEnqueuer = { operation in
            Task { @MainActor in await operation() }
        }
    ) {
        self.callbackEnqueuer = callbackEnqueuer
    }

    deinit {
        canonicalLoadTask?.cancel()
        canonicalLoadWaiters.completeAll()
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
                self.requestCanonicalLoad(isRecovery: true)
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
        guard let waiter = beginCanonicalLoad() else { return }
        await waiter.wait()
    }

    private func beginCanonicalLoad() -> CanonicalLoadWaiter? {
        guard canLoadPrinters else { return nil }
        let enqueue = callbackEnqueuer
        let waiter = canonicalLoadWaiters.registerWaiter { [weak self] in
            enqueue { [weak self] in
                self?.canonicalWaiterCancelled()
            }
        }
        requestCanonicalLoad()
        return waiter
    }

    private var canLoadPrinters: Bool {
        isActive && printerService != nil
    }

    private func requestCanonicalLoad(isRecovery: Bool = false) {
        guard canLoadPrinters else { return }
        canonicalLoadRequested = true
        if isRecovery {
            canonicalPendingRecoveryDemand = true
        }
        guard canonicalLoadTask == nil else { return }

        let token = UUID()
        canonicalLoadToken = token
        canonicalLoadRequested = false
        canonicalPassHasRecoveryDemand = canonicalPendingRecoveryDemand
        canonicalPendingRecoveryDemand = false
        autoStatusLoadToken = nil
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
        let input = CanonicalLoadInput(
            printerService: printerService,
            autoPrintService: autoPrintService,
            fallbackStatuses: autoDispatchStatuses
        )
        let taskTracker = canonicalTaskTracker
        taskTracker.taskStarted()
        canonicalLoadTask = Task { [weak self, taskTracker, input] in
            defer { taskTracker.taskFinished() }
            let result = await Self.loadCanonicalPrinters(input: input)
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
            autoStatusLoadToken = nil
            startCanonicalPass(authority: authority)
            return
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
            break
        }
        finishCanonicalLoad(authority: authority)
    }

    nonisolated private static func loadCanonicalPrinters(
        input: CanonicalLoadInput
    ) async -> CanonicalLoadResult {
        do {
            let loadedPrinters = try await input.printerService.list()
            guard !Task.isCancelled else { return .superseded }
            var loadedStatuses = input.fallbackStatuses
            if let autoPrintService = input.autoPrintService {
                do {
                    let statuses = try await autoPrintService.getAllStatus()
                    guard !Task.isCancelled else { return .superseded }
                    loadedStatuses = Dictionary(
                        uniqueKeysWithValues: statuses.printers.map { ($0.printerId, $0) }
                    )
                } catch where Task.isCancelled {
                    return .superseded
                } catch {
                }
            }
            return .success(
                CanonicalSnapshot(
                    printers: loadedPrinters,
                    autoDispatchStatuses: loadedStatuses
                )
            )
        } catch {
            guard !Task.isCancelled else { return .superseded }
            return .failure(error)
        }
    }

    private func finishCanonicalLoad(authority: CanonicalAuthority) {
        guard isCanonicalLoadCurrent(authority) else { return }
        canonicalLoadTask = nil
        canonicalLoadToken = nil
        canonicalLoadRequested = false
        canonicalPassHasRecoveryDemand = false
        canonicalPendingRecoveryDemand = false
        isLoading = false
        canonicalLoadWaiters.completeAll()
    }

    private func invalidateCanonicalLoad() {
        canonicalLifecycleEpoch &+= 1
        canonicalLoadTask?.cancel()
        canonicalLoadTask = nil
        canonicalLoadToken = nil
        canonicalLoadRequested = false
        canonicalPassHasRecoveryDemand = false
        canonicalPendingRecoveryDemand = false
        autoStatusLoadToken = nil
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
    }

    private struct CanonicalSnapshot {
        let printers: [Printer]
        let autoDispatchStatuses: [UUID: AutoDispatchStatus]
    }

    private struct CanonicalLoadInput: Sendable {
        let printerService: any PrinterServiceProtocol
        let autoPrintService: (any AutoDispatchServiceProtocol)?
        let fallbackStatuses: [UUID: AutoDispatchStatus]
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
