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
    /// Shared monotonic transition epoch (H1). Advanced SYNCHRONOUSLY at every
    /// target-intent change (registry callback, demo/real, switch requests), not
    /// when the worker later observes it, so a suspended switch is invalidated the
    /// instant a newer intent arrives.
    @ObservationIgnored private let transitionEpoch = ActiveServerGeneration()
    /// The immutable desired composition target (H1). Set SYNCHRONOUSLY at every
    /// intent change together with a transition-epoch advance. The reconciliation
    /// worker reconciles ONLY this captured target and NEVER re-reads the registry to
    /// infer intent after a suspension — so a suspended real switch can never resume
    /// and undo a newer demo/logout intent.
    @ObservationIgnored private var desiredTarget: DesiredTarget = .none

    /// An immutable snapshot of the intended composition. `.server` carries the
    /// captured server so the worker never re-derives it from the mutable registry.
    enum DesiredTarget {
        case none        // logged out / no active server
        case demo        // demo composition (applied synchronously; worker never rebuilds real)
        case server(RegisteredServer)
    }

    /// Record a new desired target and advance the transition epoch synchronously,
    /// WITHOUT scheduling the worker (used by paths that apply the composition
    /// synchronously themselves, e.g. demo/real toggles).
    private func recordTarget(_ target: DesiredTarget) {
        transitionEpoch.advance()
        desiredTarget = target
    }

    /// Record a new desired target and schedule the reconciliation worker to apply it.
    private func requestTarget(_ target: DesiredTarget) {
        recordTarget(target)
        scheduleActiveServerSwitch()
    }

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
        // H4: sweep any durable-tombstone residue a prior crash may have left,
        // independently of (and before) any activation.
        let startupStore = self.farmSnapshotStore
        Task { await startupStore.prepareStartup() }
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
        // H1: record the demo desired target + advance the transition epoch
        // synchronously, so any suspended real switch is invalidated and the worker
        // reconciles `.demo` (a no-op that never rebuilds real) instead of re-reading
        // the registry and undoing demo.
        recordTarget(.demo)
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
        let server = serverRegistry?.activeServer
        // H1: record the real/none desired target + advance the epoch synchronously.
        recordTarget(server.map { .server($0) } ?? .none)
        // Revoke synchronously before the composition changes.
        farmSnapshotAuthority.revoke()
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
        // H1: request the server target and let the single reconciliation worker apply
        // it; await the worker so callers observe the settled switch.
        requestTarget(.server(server))
        await activeServerSwitchTask?.value
    }

    // MARK: - Farm snapshot lifecycle authority (issue #816)

    /// Activate the snapshot session for the settled active server. Awaits any
    /// pending registry-driven switch so binding happens against the truly-settled
    /// server, then resolves that server's OWN verified owner. A user verified on
    /// one server can never activate under another — `(serverB, userA)` is
    /// structurally impossible because the owner is read by the settled server id.
    func activateFarmSnapshotForActiveServer(authToken: Int? = nil) async {
        await activeServerSwitchTask?.value
        await bindSnapshotToActiveServer(authToken: authToken)
    }

    /// Await any in-flight active-server reconciliation so callers observe the settled
    /// composition. Public API (a caller may legitimately wait for a switch to settle).
    func awaitActiveServerSettled() async {
        await activeServerSwitchTask?.value
    }

    /// Capture the current session, then conditionally deactivate ONLY that captured
    /// session in both the synchronous authority and the async store. A newer
    /// activation that lands during the store await survives — this never globally
    /// revokes (H3).
    func revokeFarmSnapshot() async {
        guard let session = farmSnapshotAuthority.currentSession() else { return }
        farmSnapshotAuthority.deactivate(session)
        _ = await farmSnapshotStore.deactivate(session: session)
    }

    /// Bind the snapshot session to the current active server using only that
    /// server's persisted owner identity. Uses conditional deactivation only — it
    /// never globally revokes, so a concurrent newer switch's binding is never
    /// cleared (H1).
    ///
    /// When called from an authenticated login/restore, `authToken` carries that
    /// operation's auth epoch. A logout/session-expiry/newer login advances the auth
    /// epoch and revokes authority; this binding then fails its final exact-token CAS
    /// at the publication point — even if the logout lands DURING the activation await
    /// (H2, Bishop). Switch-driven binds pass `nil` and rely on the transition epoch.
    private func bindSnapshotToActiveServer(authToken: Int? = nil) async {
        func authStillCurrent() -> Bool { authToken.map { authOperationEpoch.isCurrent($0) } ?? true }
        guard let serverRegistry, let active = serverRegistry.activeServer else {
            // No active server: conditionally clear the current session if any.
            if let session = farmSnapshotAuthority.currentSession() {
                farmSnapshotAuthority.deactivate(session)
            }
            return
        }
        guard let ownerID = farmSnapshotOwnerStore.ownerUserID(serverID: active.id) else {
            // Token-only / unverified server: fail closed. Deactivate only a session
            // belonging to THIS active server; never a newer server's binding.
            if let session = farmSnapshotAuthority.currentSession(), session.serverID == active.id {
                farmSnapshotAuthority.deactivate(session)
            }
            return
        }
        // Fail closed early if this login/restore was already superseded.
        guard authStillCurrent() else { return }
        let namespace = FarmSnapshotNamespace(serverID: active.id, userID: ownerID)
        guard let session = farmSnapshotAuthority.mint(namespace: namespace, generation: activeGeneration.current) else {
            // Tombstoned (purged) server — do not resurrect.
            return
        }
        // Bind only if the authority ACCEPTS the session (H3: a rejected/older token
        // must not be treated as bound).
        guard await farmSnapshotStore.activate(session: session) else { return }
        // Final exact-token CAS at publication: the active server did not change, the
        // session is still authority-current, AND the auth operation is still current
        // (no logout/newer login landed during the activate await).
        guard serverRegistry.activeServerID == active.id,
              farmSnapshotAuthority.isCurrent(session),
              authStillCurrent() else {
            farmSnapshotAuthority.deactivate(session) // conditional; never clobbers newer
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
        let transitionEpoch = self.transitionEpoch
        withObservationTracking {
            _ = serverRegistry.activeServerID
            _ = serverRegistry.servers
        } onChange: { [weak self] in
            // Reserve an ordering slot SYNCHRONOUSLY on the mutating (MainActor) thread
            // so this notification is ordered against any concurrent explicit intent.
            transitionEpoch.advance()
            let stamp = transitionEpoch.current
            Task { @MainActor [weak self] in
                guard let self else { return }
                // Re-register for the next change (reads the new registry state).
                self.observeActiveServer()
                // Apply this observation's intent ONLY if no NEWER intent (a later
                // explicit demo/logout, or a newer registry change) has superseded it
                // (H1: a late/out-of-order observer notification cannot overwrite a
                // newer explicit intent). The registry read here TRANSLATES state into
                // a captured target; the worker itself never re-reads the registry.
                guard self.transitionEpoch.isCurrent(stamp) else { return }
                self.desiredTarget = serverRegistry.activeServer.map { .server($0) } ?? .none
                self.scheduleActiveServerSwitch()
            }
        }
    }

    private func scheduleActiveServerSwitch() {
        guard activeServerSwitchTask == nil else { return }
        activeServerSwitchTask = Task { @MainActor [weak self] in
            guard let self else { return }
            await self.runActiveServerSwitchLoop()
            self.activeServerSwitchTask = nil
        }
    }

    private func runActiveServerSwitchLoop() async {
        while true {
            // Capture the intent epoch AND the immutable desired target for THIS pass.
            // The worker reconciles ONLY the captured target and never re-reads the
            // registry to infer intent after a suspension (H1).
            let epoch = transitionEpoch.current
            let target = desiredTarget
            switch target {
            case .demo:
                // Demo is applied synchronously by `switchToDemo`; the worker never
                // rebuilds real services while demo is the desired target (no undo).
                break
            case .none:
                await switchToNoActiveServer(epoch: epoch)
            case .server(let server):
                await switchToActiveServer(server, epoch: epoch)
            }
            // Re-process only if the target intent changed during the pass.
            if transitionEpoch.isCurrent(epoch) { break }
        }
    }

    private func switchToActiveServer(_ server: RegisteredServer, epoch: Int) async {
        // Capture immutable target + outgoing service/session BEFORE any await (H1).
        let outgoingSignalR = signalRService
        let outgoingSession = farmSnapshotAuthority.currentSession()
        await outgoingSignalR.disconnect()
        guard transitionEpoch.isCurrent(epoch) else {
            // Superseded during disconnect. If NOBODY replaced the outgoing service we
            // just disconnected (identity match), install a fresh one so the next pass
            // does not re-tear-down a dead service. If a newer intent (e.g. demo)
            // already swapped `signalRService`, leave it untouched — an older switch
            // never rebuilds or clobbers newer state (H1).
            if signalRService === outgoingSignalR {
                replaceSignalRAfterSupersededSwitch()
            }
            return
        }

        // Conditionally deactivate ONLY the captured outgoing session (never a
        // global revoke that could clear a newer binding).
        if let outgoingSession {
            farmSnapshotAuthority.deactivate(outgoingSession)
        }
        activeServerGeneration = activeGeneration.advance()
        let capturedGeneration = activeServerGeneration

        let accessToken = Self.validAccessToken(for: server, credentialsStore: credentialsStore)
        guard transitionEpoch.isCurrent(epoch) else { return }
        // CAS publish: the synchronous rebuild follows the epoch check with no await
        // between them, so an older switch cannot publish stale services.
        let client = rebuildRealServices(baseURL: server.baseURL, server: server, accessToken: accessToken)
        userDefaultsBox.userDefaults.set(server.normalizedURLString, forKey: APIClient.serverURLKey)
        await configureTokenExpiryChecker(client: client, serverID: server.id)
        guard transitionEpoch.isCurrent(epoch) else { return } // superseded during the awaits

        // Bind the snapshot to the SAME captured server + generation the services
        // were rebuilt for.
        await bindSnapshotToServer(server, generation: capturedGeneration, epoch: epoch)

        guard transitionEpoch.isCurrent(epoch), accessToken != nil else { return }
        do {
            try await signalRService.connect()
        } catch {
            // RootView will also attempt connection when authenticated; keep switching non-fatal.
        }
    }

    /// Bind the snapshot to a specific captured server + generation for a switch
    /// operation, guarded by the transition epoch (H1). Resolves that server's OWN
    /// verified owner; token-only/unverified or tombstoned → no bind (fail closed).
    /// A superseded/older switch conditionally deactivates only its own session and
    /// never clears newer authority.
    private func bindSnapshotToServer(_ server: RegisteredServer, generation: Int, epoch: Int) async {
        guard let ownerID = farmSnapshotOwnerStore.ownerUserID(serverID: server.id) else { return }
        guard transitionEpoch.isCurrent(epoch) else { return }
        let namespace = FarmSnapshotNamespace(serverID: server.id, userID: ownerID)
        guard let session = farmSnapshotAuthority.mint(namespace: namespace, generation: generation) else { return }
        guard await farmSnapshotStore.activate(session: session) else { return }
        guard transitionEpoch.isCurrent(epoch), farmSnapshotAuthority.isCurrent(session) else {
            farmSnapshotAuthority.deactivate(session) // conditional: only if still exactly this session
            return
        }
    }

    private func switchToNoActiveServer(epoch: Int) async {
        guard activeServerID != nil else { return }
        let outgoingSignalR = signalRService
        let outgoingSession = farmSnapshotAuthority.currentSession()
        await outgoingSignalR.disconnect()
        guard transitionEpoch.isCurrent(epoch) else {
            if signalRService === outgoingSignalR {
                replaceSignalRAfterSupersededSwitch()
            }
            return
        }
        if let outgoingSession {
            farmSnapshotAuthority.deactivate(outgoingSession)
        }
        activeServerGeneration = activeGeneration.advance()
        _ = rebuildRealServices(baseURL: APIClient.savedBaseURL() ?? AppConfig.baseURL, server: nil, accessToken: nil)
    }

    /// After a superseded switch (which must not rebuild/publish), replace the
    /// already-disconnected signalR with a fresh one so the reconciliation loop's
    /// next pass tears down a clean service rather than the outgoing one again.
    private func replaceSignalRAfterSupersededSwitch() {
        guard let client = apiClient else { return }
        signalRService = signalRServiceFactory(APIClient.savedBaseURL() ?? AppConfig.baseURL, client)
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
