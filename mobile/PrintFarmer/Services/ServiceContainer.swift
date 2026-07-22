import Foundation
import Observation

/// Dependency container providing access to all services.
/// Created once at app startup and passed via SwiftUI environment.
@MainActor
@Observable
final class ServiceContainer: @unchecked Sendable {
    typealias APIClientFactory = @MainActor (URL, ActiveServerGeneration, String?) -> APIClient
    typealias SignalRServiceFactory = @MainActor (URL, APIClient) -> any SignalRServiceProtocol

    var apiClient: APIClient?
    var authService: any AuthServiceProtocol
    var printerService: any PrinterServiceProtocol
    var jobService: any JobServiceProtocol
    var locationService: any LocationServiceProtocol
    var statisticsService: any StatisticsServiceProtocol
    var notificationService: any NotificationServiceProtocol
    var signalRService: any SignalRServiceProtocol
    var spoolService: any SpoolServiceProtocol
    var barcodeIntakeService: any BarcodeIntakeServiceProtocol
    var maintenanceService: any MaintenanceServiceProtocol
    var attentionService: any AttentionServiceProtocol
    var filamentCoverageService: any FilamentCoverageServiceProtocol
    var shiftTaskService: any ShiftTaskServiceProtocol
    var partsInventoryService: any PartsInventoryServiceProtocol
    var autoPrintService: any AutoDispatchServiceProtocol
    var jobAnalyticsService: any JobAnalyticsServiceProtocol
    var predictiveService: any PredictiveServiceProtocol
    var dispatchService: any DispatchServiceProtocol
    var failureDetectionService: any FailureDetectionServiceProtocol
    /// Operator feature gate snapshot (issue #725). Views observe
    /// `resolved.attentionEnabled` etc. to render safe fallbacks when a
    /// gated feature is disabled server-side.
    var capabilitiesService: any SystemCapabilitiesServiceProtocol
    var activeServerGeneration = 0
    #if canImport(UIKit)
    var qrScannerService: QRSpoolScannerService?
    var barcodeScannerService: BarcodeScannerService?
    var nfcService: NFCService?
    #endif

    @ObservationIgnored private let serverRegistry: ServerRegistry?
    @ObservationIgnored private let credentialsStore: ServerCredentialsStore
    @ObservationIgnored private let userDefaultsBox: AuthServiceUserDefaultsBox
    @ObservationIgnored private let apiClientFactory: APIClientFactory
    @ObservationIgnored private let signalRServiceFactory: SignalRServiceFactory
    @ObservationIgnored private let activeGeneration: ActiveServerGeneration
    @ObservationIgnored private var observesRegistry: Bool
    @ObservationIgnored private var activeServerID: UUID?
    @ObservationIgnored private var activeServerSwitchTask: Task<Void, Never>?
    @ObservationIgnored private var activeServerSwitchRequested = false
    /// Monotonic switch-operation epoch (H1). Each service rebuild / snapshot
    /// (re)bind captures this; an older switch that has been superseded must not
    /// rebuild, bind, or clear newer state.
    @ObservationIgnored private var switchEpoch = 0

    // MARK: Farm snapshot lifecycle authority (issue #816)
    /// Shared synchronous authority for origin-pinned snapshot sessions. A
    /// container-side revoke/tombstone has program-order happens-before with the
    /// store's durable promotion, so no stale/cross-bound write can slip through.
    @ObservationIgnored let farmSnapshotAuthority: FarmSnapshotAuthority
    @ObservationIgnored let farmSnapshotStore: any FarmSnapshotStoring
    /// Non-secret per-server owner identity. Activation resolves the settled
    /// server's OWN owner from here — never a carried cross-server user id.
    @ObservationIgnored let farmSnapshotOwnerStore: FarmSnapshotOwnerStore
    /// Shared monotonic auth-operation epoch fencing late login/restore vs logout (H2).
    @ObservationIgnored let authOperationEpoch = AuthOperationEpoch()

    init(
        baseURL: URL? = nil,
        serverRegistry: ServerRegistry = ServerRegistry(),
        credentialsStore: ServerCredentialsStore = ServerCredentialsStore(),
        userDefaultsBox: AuthServiceUserDefaultsBox = AuthServiceUserDefaultsBox(.standard),
        observeRegistry: Bool = true,
        farmSnapshotAuthority: FarmSnapshotAuthority? = nil,
        farmSnapshotStore: (any FarmSnapshotStoring)? = nil,
        farmSnapshotOwnerStore: FarmSnapshotOwnerStore? = nil,
        apiClientFactory: @escaping APIClientFactory = { baseURL, generation, accessToken in
            APIClient(baseURL: baseURL, serverGeneration: generation, accessToken: accessToken)
        },
        signalRServiceFactory: @escaping SignalRServiceFactory = { baseURL, client in
            SignalRService(
                serverURL: baseURL,
                session: APIClient.makePrivateNetworkSession()
            ) {
                client.currentAccessToken()
            }
        }
    ) {
        self.serverRegistry = serverRegistry
        self.credentialsStore = credentialsStore
        self.userDefaultsBox = userDefaultsBox
        self.apiClientFactory = apiClientFactory
        self.signalRServiceFactory = signalRServiceFactory
        self.activeGeneration = ActiveServerGeneration()
        self.observesRegistry = observeRegistry

        let authority = farmSnapshotAuthority ?? FarmSnapshotAuthority()
        let ownerStore = farmSnapshotOwnerStore ?? FarmSnapshotOwnerStore(userDefaults: userDefaultsBox.userDefaults)
        self.farmSnapshotAuthority = authority
        self.farmSnapshotOwnerStore = ownerStore
        self.farmSnapshotStore = farmSnapshotStore ?? FarmSnapshotStore(authority: authority, ownerStore: ownerStore)

        let activeServer = serverRegistry.activeServer
        let resolvedURL = activeServer?.baseURL
            ?? baseURL
            ?? APIClient.savedBaseURL()
            ?? AppConfig.baseURL
        let accessToken = Self.validAccessToken(for: activeServer, credentialsStore: credentialsStore)
        let client = apiClientFactory(resolvedURL, activeGeneration, accessToken)

        self.apiClient = client
        self.authService = AuthService(
            apiClient: client,
            credentialsStore: credentialsStore,
            userDefaultsBox: userDefaultsBox,
            migrateLegacyServerURL: false,
            serverRegistry: serverRegistry,
            snapshotOwnerStore: ownerStore,
            authEpoch: authOperationEpoch
        )
        self.printerService = PrinterService(apiClient: client)
        self.jobService = JobService(apiClient: client)
        self.locationService = LocationService(apiClient: client)
        self.statisticsService = StatisticsService(apiClient: client)
        self.notificationService = NotificationService(apiClient: client)
        self.spoolService = SpoolService(apiClient: client)
        self.maintenanceService = MaintenanceService(apiClient: client)
        self.attentionService = AttentionService(apiClient: client)
        self.filamentCoverageService = FilamentCoverageService(apiClient: client)
        self.shiftTaskService = ShiftTaskService(apiClient: client)
        self.partsInventoryService = PartsInventoryService(apiClient: client)
        self.autoPrintService = AutoDispatchService(apiClient: client)
        self.jobAnalyticsService = JobAnalyticsService(apiClient: client)
        self.predictiveService = PredictiveService(apiClient: client)
        self.dispatchService = DispatchService(apiClient: client)
        self.failureDetectionService = FailureDetectionService(apiClient: client)
        self.capabilitiesService = SystemCapabilitiesService(apiClient: client)
        self.signalRService = signalRServiceFactory(resolvedURL, client)
        self.barcodeIntakeService = BarcodeIntakeService(apiClient: client)
        self.activeServerID = activeServer?.id
        #if canImport(UIKit)
        self.qrScannerService = QRSpoolScannerService()
        self.barcodeScannerService = BarcodeScannerService()
        self.nfcService = NFCService()
        PushNotificationManager.shared.configure(notificationService: self.notificationService)
        #endif

        if let activeServer {
            userDefaultsBox.userDefaults.set(activeServer.normalizedURLString, forKey: APIClient.serverURLKey)
            Task { await self.configureTokenExpiryChecker(client: client, serverID: activeServer.id) }
        }

        if observeRegistry {
            observeActiveServer()
        }

        wireSnapshotPurgeHandler()
    }

    /// Route registry deletion through the store's awaited purge (Gate E). Wired
    /// for every production composition that exposes a real registry, so deletion
    /// can never drop a server without first clearing its cached namespace.
    private func wireSnapshotPurgeHandler() {
        guard let serverRegistry else { return }
        let store = farmSnapshotStore
        serverRegistry.snapshotPurgeHandler = { serverID in
            await store.purge(serverID: serverID)
        }
    }

    /// Creates a ServiceContainer wired with demo (mock) services.
    ///
    /// A production `serverRegistry` may be supplied so that persisted-demo mode
    /// still routes server deletion through the awaited snapshot purge and can
    /// reattach the registry observer on demo exit (issue #816, Gate D/B).
    static func demo(
        serverRegistry: ServerRegistry? = nil,
        farmSnapshotAuthority: FarmSnapshotAuthority? = nil,
        farmSnapshotStore: (any FarmSnapshotStoring)? = nil,
        farmSnapshotOwnerStore: FarmSnapshotOwnerStore? = nil
    ) -> ServiceContainer {
        return ServiceContainer(
            authService: DemoAuthService(),
            printerService: DemoPrinterService(),
            jobService: DemoJobService(),
            locationService: DemoLocationService(),
            statisticsService: DemoStatisticsService(),
            notificationService: DemoNotificationService(),
            signalRService: DemoSignalRService(),
            spoolService: DemoSpoolService(),
            barcodeIntakeService: DemoBarcodeIntakeService(),
            maintenanceService: DemoMaintenanceService(),
            attentionService: DemoAttentionService(),
            filamentCoverageService: DemoFilamentCoverageService(),
            shiftTaskService: DemoShiftTaskService(),
            partsInventoryService: DemoPartsInventoryService(),
            autoPrintService: DemoAutoDispatchService(),
            jobAnalyticsService: DemoJobAnalyticsService(),
            predictiveService: DemoPredictiveService(),
            dispatchService: DemoDispatchService(),
            failureDetectionService: DemoFailureDetectionService(),
            capabilitiesService: StubSystemCapabilitiesService(),
            serverRegistry: serverRegistry,
            farmSnapshotAuthority: farmSnapshotAuthority,
            farmSnapshotStore: farmSnapshotStore,
            farmSnapshotOwnerStore: farmSnapshotOwnerStore
        )
    }

    /// Replaces all services with demo implementations at runtime.
    func switchToDemo() {
        // Revoke synchronously before advancing the generation so no stale
        // snapshot commit can apply across the demo transition.
        farmSnapshotAuthority.revoke()
        self.apiClient = nil
        self.authService = DemoAuthService()
        self.printerService = DemoPrinterService()
        self.jobService = DemoJobService()
        self.locationService = DemoLocationService()
        self.statisticsService = DemoStatisticsService()
        self.notificationService = DemoNotificationService()
        self.signalRService = DemoSignalRService()
        self.spoolService = DemoSpoolService()
        self.barcodeIntakeService = DemoBarcodeIntakeService()
        self.maintenanceService = DemoMaintenanceService()
        self.attentionService = DemoAttentionService()
        self.filamentCoverageService = DemoFilamentCoverageService()
        self.shiftTaskService = DemoShiftTaskService()
        self.partsInventoryService = DemoPartsInventoryService()
        self.autoPrintService = DemoAutoDispatchService()
        self.jobAnalyticsService = DemoJobAnalyticsService()
        self.predictiveService = DemoPredictiveService()
        self.dispatchService = DemoDispatchService()
        self.failureDetectionService = DemoFailureDetectionService()
        self.capabilitiesService = StubSystemCapabilitiesService()
        self.activeServerID = nil
        self.activeServerGeneration = activeGeneration.advance()
        #if canImport(UIKit)
        self.qrScannerService = nil
        self.barcodeScannerService = nil
        self.nfcService = nil
        #endif
    }

    /// Replaces all services with real implementations backed by the active or given base URL.
    func switchToReal(baseURL: URL? = nil) {
        // Revoke synchronously before the composition changes.
        farmSnapshotAuthority.revoke()
        let server = serverRegistry?.activeServer
        let resolvedURL = server?.baseURL
            ?? baseURL
            ?? APIClient.savedBaseURL()
            ?? AppConfig.baseURL
        let accessToken = Self.validAccessToken(for: server, credentialsStore: credentialsStore)
        let client = rebuildRealServices(baseURL: resolvedURL, server: server, accessToken: accessToken)
        // Persisted-demo exit: reattach the production registry observer so
        // subsequent real login/restore activates snapshots and observes switches
        // in the same process (issue #816, Gate B).
        ensureObservingRegistry()
        if let server {
            Task { await self.configureTokenExpiryChecker(client: client, serverID: server.id) }
        }
    }

    func switchToServer(_ server: RegisteredServer) async {
        guard activeServerID != server.id else { return }
        await switchToActiveServer(server)
    }

    // MARK: - Farm snapshot lifecycle authority (issue #816)

    /// Activate the snapshot session for the settled active server. Awaits any
    /// pending registry-driven switch so binding happens against the truly-settled
    /// server, then resolves that server's OWN verified owner. A user verified on
    /// one server can never activate under another — `(serverB, userA)` is
    /// structurally impossible because the owner is read by the settled server id.
    func activateFarmSnapshotForActiveServer() async {
        await activeServerSwitchTask?.value
        await bindSnapshotToActiveServer()
    }

    /// Synchronously revoke authority, then await the store's deactivation.
    func revokeFarmSnapshot() async {
        farmSnapshotAuthority.revoke()
        await farmSnapshotStore.deactivate()
    }

    /// Bind the snapshot session to the current active server using only that
    /// server's persisted owner identity. Does not await the switch task, so it is
    /// safe to call from inside the switch loop.
    private func bindSnapshotToActiveServer() async {
        guard let serverRegistry, let active = serverRegistry.activeServer else {
            await revokeFarmSnapshot()
            return
        }
        guard let ownerID = farmSnapshotOwnerStore.ownerUserID(serverID: active.id) else {
            // Token-only / unverified server: fail closed, no hydrate until an
            // online verification establishes the owner.
            await revokeFarmSnapshot()
            return
        }
        let namespace = FarmSnapshotNamespace(serverID: active.id, userID: ownerID)
        guard let session = farmSnapshotAuthority.mint(namespace: namespace, generation: activeGeneration.current) else {
            // Tombstoned (purged) server — do not resurrect.
            return
        }
        await farmSnapshotStore.activate(session: session)
        // Revalidate that the active server did not change under us during the
        // await; if it did, the newer switch owns activation.
        guard serverRegistry.activeServerID == active.id, farmSnapshotAuthority.isCurrent(session) else {
            await revokeFarmSnapshot()
            return
        }
    }

    /// Attach the registry observer if a production registry is present and not
    /// already observed. Used to reattach after a persisted-demo exit.
    private func ensureObservingRegistry() {
        guard serverRegistry != nil, !observesRegistry else { return }
        observesRegistry = true
        observeActiveServer()
    }

    private func observeActiveServer() {
        guard observesRegistry, let serverRegistry else { return }
        withObservationTracking {
            _ = serverRegistry.activeServerID
            _ = serverRegistry.servers
        } onChange: { [weak self] in
            Task { @MainActor [weak self] in
                guard let self else { return }
                self.observeActiveServer()
                self.scheduleActiveServerSwitch()
            }
        }
    }

    private func scheduleActiveServerSwitch() {
        activeServerSwitchRequested = true
        guard activeServerSwitchTask == nil else { return }

        activeServerSwitchTask = Task { @MainActor [weak self] in
            guard let self else { return }
            await self.runActiveServerSwitchLoop()
            self.activeServerSwitchTask = nil
        }
    }

    private func runActiveServerSwitchLoop() async {
        guard let serverRegistry else { return }

        while true {
            activeServerSwitchRequested = false
            let targetID = serverRegistry.activeServerID
            if let server = serverRegistry.activeServer {
                await switchToServer(server)
            } else {
                await switchToNoActiveServer()
            }

            if serverRegistry.activeServerID == targetID && !activeServerSwitchRequested {
                break
            }
        }
    }

    private func switchToActiveServer(_ server: RegisteredServer) async {
        switchEpoch += 1
        let epoch = switchEpoch
        let oldSignalRService = signalRService
        await oldSignalRService.disconnect()
        guard epoch == switchEpoch else { return } // superseded by a newer switch

        // H3: conditionally deactivate ONLY the outgoing session (compare-and-set),
        // so a stale switch cannot clear a newer login's session.
        if let outgoing = farmSnapshotAuthority.currentSession() {
            farmSnapshotAuthority.deactivate(outgoing)
        }
        activeServerGeneration = activeGeneration.advance()
        let capturedGeneration = activeServerGeneration

        let accessToken = Self.validAccessToken(for: server, credentialsStore: credentialsStore)
        let client = rebuildRealServices(baseURL: server.baseURL, server: server, accessToken: accessToken)
        userDefaultsBox.userDefaults.set(server.normalizedURLString, forKey: APIClient.serverURLKey)
        await configureTokenExpiryChecker(client: client, serverID: server.id)
        guard epoch == switchEpoch else { return } // superseded during the awaits

        // H1: bind the snapshot to the SAME captured server + generation the
        // services were rebuilt for — never a re-read that could diverge from the
        // rebuilt API client.
        await bindSnapshotToServer(server, generation: capturedGeneration, epoch: epoch)

        guard epoch == switchEpoch, accessToken != nil else { return }
        do {
            try await signalRService.connect()
        } catch {
            // RootView will also attempt connection when authenticated; keep switching non-fatal.
        }
    }

    /// Bind the snapshot to a specific captured server + generation for a switch
    /// operation, guarded by the switch epoch (H1). Resolves that server's OWN
    /// verified owner; token-only/unverified or tombstoned → no bind (fail closed).
    private func bindSnapshotToServer(_ server: RegisteredServer, generation: Int, epoch: Int) async {
        guard let ownerID = farmSnapshotOwnerStore.ownerUserID(serverID: server.id) else { return }
        let namespace = FarmSnapshotNamespace(serverID: server.id, userID: ownerID)
        guard let session = farmSnapshotAuthority.mint(namespace: namespace, generation: generation) else { return }
        await farmSnapshotStore.activate(session: session)
        // Revalidate: an older switch that lost the race must not leave its binding.
        guard epoch == switchEpoch, farmSnapshotAuthority.isCurrent(session) else {
            farmSnapshotAuthority.deactivate(session)
            return
        }
    }

    private func switchToNoActiveServer() async {
        guard activeServerID != nil else { return }
        switchEpoch += 1
        let epoch = switchEpoch
        let oldSignalRService = signalRService
        await oldSignalRService.disconnect()
        guard epoch == switchEpoch else { return }
        if let outgoing = farmSnapshotAuthority.currentSession() {
            farmSnapshotAuthority.deactivate(outgoing)
        }
        activeServerGeneration = activeGeneration.advance()
        _ = rebuildRealServices(baseURL: APIClient.savedBaseURL() ?? AppConfig.baseURL, server: nil, accessToken: nil)
    }

    @discardableResult
    private func rebuildRealServices(
        baseURL: URL,
        server: RegisteredServer?,
        accessToken: String?
    ) -> APIClient {
        let client = apiClientFactory(baseURL, activeGeneration, accessToken)
        self.apiClient = client
        self.authService = AuthService(
            apiClient: client,
            credentialsStore: credentialsStore,
            userDefaultsBox: userDefaultsBox,
            migrateLegacyServerURL: false,
            serverRegistry: serverRegistry,
            snapshotOwnerStore: farmSnapshotOwnerStore,
            authEpoch: authOperationEpoch
        )
        self.printerService = PrinterService(apiClient: client)
        self.jobService = JobService(apiClient: client)
        self.locationService = LocationService(apiClient: client)
        self.statisticsService = StatisticsService(apiClient: client)
        self.notificationService = NotificationService(apiClient: client)
        self.spoolService = SpoolService(apiClient: client)
        self.maintenanceService = MaintenanceService(apiClient: client)
        self.attentionService = AttentionService(apiClient: client)
        self.filamentCoverageService = FilamentCoverageService(apiClient: client)
        self.shiftTaskService = ShiftTaskService(apiClient: client)
        self.partsInventoryService = PartsInventoryService(apiClient: client)
        self.autoPrintService = AutoDispatchService(apiClient: client)
        self.jobAnalyticsService = JobAnalyticsService(apiClient: client)
        self.predictiveService = PredictiveService(apiClient: client)
        self.dispatchService = DispatchService(apiClient: client)
        self.failureDetectionService = FailureDetectionService(apiClient: client)
        self.capabilitiesService = SystemCapabilitiesService(apiClient: client)
        self.signalRService = signalRServiceFactory(baseURL, client)
        self.barcodeIntakeService = BarcodeIntakeService(apiClient: client)
        self.activeServerID = server?.id
        #if canImport(UIKit)
        self.qrScannerService = QRSpoolScannerService()
        self.barcodeScannerService = BarcodeScannerService()
        self.nfcService = NFCService()
        PushNotificationManager.shared.configure(notificationService: self.notificationService)
        #endif
        return client
    }

    private func configureTokenExpiryChecker(client: APIClient, serverID: UUID) async {
        let credentialsStore = credentialsStore
        await client.setTokenExpiryChecker {
            credentialsStore.isExpired(serverId: serverID)
        }
    }

    private static func validAccessToken(
        for server: RegisteredServer?,
        credentialsStore: ServerCredentialsStore
    ) -> String? {
        guard let server,
              !credentialsStore.isExpired(serverId: server.id) else {
            return nil
        }
        return credentialsStore.load(serverId: server.id)?.accessToken
    }

    /// Internal initializer used by the `demo()` factory.
    private init(
        authService: any AuthServiceProtocol,
        printerService: any PrinterServiceProtocol,
        jobService: any JobServiceProtocol,
        locationService: any LocationServiceProtocol,
        statisticsService: any StatisticsServiceProtocol,
        notificationService: any NotificationServiceProtocol,
        signalRService: any SignalRServiceProtocol,
        spoolService: any SpoolServiceProtocol,
        barcodeIntakeService: any BarcodeIntakeServiceProtocol,
        maintenanceService: any MaintenanceServiceProtocol,
        attentionService: any AttentionServiceProtocol,
        filamentCoverageService: any FilamentCoverageServiceProtocol,
        shiftTaskService: any ShiftTaskServiceProtocol,
        partsInventoryService: any PartsInventoryServiceProtocol,
        autoPrintService: any AutoDispatchServiceProtocol,
        jobAnalyticsService: any JobAnalyticsServiceProtocol,
        predictiveService: any PredictiveServiceProtocol,
        dispatchService: any DispatchServiceProtocol,
        failureDetectionService: any FailureDetectionServiceProtocol,
        capabilitiesService: any SystemCapabilitiesServiceProtocol,
        serverRegistry: ServerRegistry? = nil,
        farmSnapshotAuthority: FarmSnapshotAuthority? = nil,
        farmSnapshotStore: (any FarmSnapshotStoring)? = nil,
        farmSnapshotOwnerStore: FarmSnapshotOwnerStore? = nil
    ) {
        self.serverRegistry = serverRegistry
        self.credentialsStore = ServerCredentialsStore()
        self.userDefaultsBox = AuthServiceUserDefaultsBox(.standard)
        self.apiClientFactory = { baseURL, generation, accessToken in
            APIClient(baseURL: baseURL, serverGeneration: generation, accessToken: accessToken)
        }
        self.signalRServiceFactory = { baseURL, client in
            SignalRService(
                serverURL: baseURL,
                session: APIClient.makePrivateNetworkSession()
            ) {
                await client.currentAccessToken()
            }
        }
        self.activeGeneration = ActiveServerGeneration()
        // Demo composition does not rebuild real services on registry changes, so
        // it does not observe until a real login reattaches the observer.
        self.observesRegistry = false
        let authority = farmSnapshotAuthority ?? FarmSnapshotAuthority()
        self.farmSnapshotAuthority = authority
        let demoOwnerStore = farmSnapshotOwnerStore ?? FarmSnapshotOwnerStore()
        self.farmSnapshotOwnerStore = demoOwnerStore
        self.farmSnapshotStore = farmSnapshotStore ?? FarmSnapshotStore(authority: authority, ownerStore: demoOwnerStore)
        self.activeServerID = nil
        self.apiClient = nil
        self.authService = authService
        self.printerService = printerService
        self.jobService = jobService
        self.locationService = locationService
        self.statisticsService = statisticsService
        self.notificationService = notificationService
        self.signalRService = signalRService
        self.spoolService = spoolService
        self.barcodeIntakeService = barcodeIntakeService
        self.maintenanceService = maintenanceService
        self.attentionService = attentionService
        self.filamentCoverageService = filamentCoverageService
        self.shiftTaskService = shiftTaskService
        self.partsInventoryService = partsInventoryService
        self.autoPrintService = autoPrintService
        self.jobAnalyticsService = jobAnalyticsService
        self.predictiveService = predictiveService
        self.dispatchService = dispatchService
        self.failureDetectionService = failureDetectionService
        self.capabilitiesService = capabilitiesService
        #if canImport(UIKit)
        self.qrScannerService = nil
        self.barcodeScannerService = nil
        self.nfcService = nil
        #endif

        wireSnapshotPurgeHandler()
    }
}
