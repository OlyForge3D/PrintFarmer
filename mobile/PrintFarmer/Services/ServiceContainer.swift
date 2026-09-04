import Foundation
import Observation

/// Dependency container providing access to all services.
/// Created once at app startup and passed via SwiftUI environment.
@MainActor
@Observable
final class ServiceContainer: @unchecked Sendable {
    typealias APIClientFactory = @MainActor (URL, ActiveServerGeneration, String?, Int?, UUID?) -> APIClient
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
    /// Observed farm counts used only to select the shell arrangement.
    var farmShapeService: any FarmShapeServiceProtocol
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
    @ObservationIgnored private let farmShapeStore: FarmShapeStore
    @ObservationIgnored private let offlineReplayProviderResolutionHook: @Sendable () async -> Void
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
        case demo        // demo composition (applied by switchToDemo; worker never rebuilds real)
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
    /// Ephemeral canonical results captured by the current readiness gate.
    /// Uses the same `(serverID, userID, generation, token)` authority as snapshots.
    @ObservationIgnored let startupPrefetchStore: StartupPrefetchStore
    /// Non-secret per-server owner identity. Activation resolves the settled
    /// server's OWN owner from here — never a carried cross-server user id.
    @ObservationIgnored let farmSnapshotOwnerStore: FarmSnapshotOwnerStore
    /// H (issue #816 reject, Hicks): the CANONICAL file-backed durable authority
    /// record every production/demo composition wires into the shared
    /// `FarmSnapshotDomainCoordinator`. Held here so tests and inspection paths
    /// can observe the exact same record the coordinator uses for durability.
    @ObservationIgnored let farmSnapshotDurableRecord: FarmSnapshotDurableAuthorityRecord?
    /// Shared monotonic auth-operation epoch fencing late login/restore vs logout (H2).
    @ObservationIgnored let authOperationEpoch = AuthOperationEpoch()

    // MARK: Feature read-cache adapters (issue #789, F10-C2)
    /// Typed read-cache store for Attention (#779) and filament-coverage (#778),
    /// layered on the SAME #785 authority + root as `farmSnapshotStore`. Adds no
    /// second engine or namespace scheme — feature records live under the same
    /// `(serverID, userID)` server directory, so #785's purge/tombstone/switch
    /// fencing covers them for free.
    @ObservationIgnored let featureReadCacheStore: any FeatureReadCacheStoring
    /// Shared Attention read-cache facade. Stateless wrapper over the store.
    @ObservationIgnored private(set) lazy var attentionReadCache =
        AttentionReadCacheAdapter(store: featureReadCacheStore)
    /// Shared filament-coverage read-cache facade (fleet + per-printer detail).
    @ObservationIgnored private(set) lazy var filamentCoverageReadCache =
        FilamentCoverageReadCacheAdapter(store: featureReadCacheStore)

    // MARK: Durable offline write queue (issue #787, F10-Q1)
    @ObservationIgnored private let offlineWriteReplayAuthority = OfflineWriteReplayAuthority()
    @ObservationIgnored private let offlineWriteQueueStore: any OfflineWriteQueueStoring

    /// The single, actor-isolated outbox for offline part-adjustment / harvest
    /// writes. Lazily built (file-backed, Application Support) with a transport
    /// that resolves the CURRENT `partsInventoryService` at replay time so a
    /// server switch never replays through a stale client. Bound to the active
    /// `(serverID, userID)` namespace via `syncOfflineWriteQueue()`.
    @ObservationIgnored private(set) lazy var offlineWriteQueue: OfflineWriteQueue = {
        let transport = DynamicOfflineReplayTransport(
            replayAuthority: offlineWriteReplayAuthority,
            beforeProviderResolution: offlineReplayProviderResolutionHook
        ) { [weak self] in
            OfflineReplayServices(
                identity: self?.currentOfflineReplayIdentity(),
                parts: self?.partsInventoryService,
                tasks: self?.shiftTaskService,
                printers: self?.printerService
            )
        }
        return OfflineWriteQueue(
            store: offlineWriteQueueStore,
            transport: transport,
            replayAuthority: offlineWriteReplayAuthority
        )
    }()

    private func currentOfflineReplayIdentity() -> OfflineWriteReplayIdentity? {
        guard let activeServerID,
              serverRegistry?.activeServerID == activeServerID,
              let ownerID = farmSnapshotOwnerStore.ownerUserID(serverID: activeServerID) else {
            return nil
        }
        let derivedIdentity = OfflineWriteReplayIdentity(
            serverID: activeServerID,
            userID: ownerID
        )
        guard offlineWriteReplayAuthority.currentIdentity == derivedIdentity else {
            return nil
        }
        return derivedIdentity
    }

    private static func offlineWriteQueueDirectory() -> URL {
        let base = (try? FileManager.default.url(
            for: .applicationSupportDirectory,
            in: .userDomainMask,
            appropriateFor: nil,
            create: true
        )) ?? FileManager.default.temporaryDirectory
        return base.appendingPathComponent("OfflineWriteQueue", isDirectory: true)
    }

    /// Reconciles the outbox with the current identity + operator gate, then
    /// drives one replay pass. Binds to the active `(serverID, userID)`
    /// namespace (or unbinds when there is no verified active identity), applies
    /// `offlineWriteReplayEnabled`, and triggers a single serialized replay.
    /// Safe to call repeatedly — duplicate calls collapse to one replay owner.
    func syncOfflineWriteQueue(refreshCapabilities: Bool = true) async {
        let queue = offlineWriteQueue
        let authorityRevision = offlineWriteReplayAuthority.captureRevision()
        guard let serverRegistry,
              let active = serverRegistry.activeServer,
              activeServerID == active.id,
              Self.validAccessToken(for: active, credentialsStore: credentialsStore) != nil,
              let ownerID = farmSnapshotOwnerStore.ownerUserID(serverID: active.id) else {
            let invalidationRevision = offlineWriteReplayAuthority.invalidate()
            _ = await queue.unbind(authorityRevision: invalidationRevision)
            return
        }
        let expectedCapabilitiesService = capabilitiesService
        guard serverRegistry.activeServerID == active.id,
              activeServerID == active.id,
              farmSnapshotOwnerStore.ownerUserID(serverID: active.id) == ownerID,
              capabilitiesService === expectedCapabilitiesService,
              offlineWriteReplayAuthority.isCurrent(revision: authorityRevision) else {
            return
        }
        guard let binding = offlineWriteReplayAuthority.bindIfCurrent(
            revision: authorityRevision,
            serverID: active.id,
            userID: ownerID
        ) else {
            return
        }
        guard await queue.bind(binding: binding) else { return }
        guard serverRegistry.activeServerID == active.id,
              activeServerID == active.id,
              farmSnapshotOwnerStore.ownerUserID(serverID: active.id) == ownerID,
              capabilitiesService === expectedCapabilitiesService,
              offlineWriteReplayAuthority.isCurrent(binding) else {
            return
        }
        if refreshCapabilities {
            await expectedCapabilitiesService.prepareForReadiness()
        }
        guard serverRegistry.activeServerID == active.id,
              activeServerID == active.id,
              farmSnapshotOwnerStore.ownerUserID(serverID: active.id) == ownerID,
              capabilitiesService === expectedCapabilitiesService,
              offlineWriteReplayAuthority.isCurrent(binding) else {
            return
        }
        await queue.setReplayEnabled(expectedCapabilitiesService.resolved.offlineWriteReplayEnabled)
        guard offlineWriteReplayAuthority.isCurrent(binding) else { return }
        await queue.replayPending()
    }

    func invalidateOfflineWriteReplayAuthority() {
        offlineWriteReplayAuthority.invalidate()
    }

    func authorizeOfflineWriteReplayBinding() {
        offlineWriteReplayAuthority.authorizeBinding()
    }

    func makeOfflineReplaySessionExpiryInvalidator() -> @Sendable (Int?, Int?) -> Bool {
        let generation = activeGeneration
        let authEpoch = authOperationEpoch
        let authority = offlineWriteReplayAuthority
        return { eventGeneration, eventAuthToken in
            guard let eventGeneration,
                  let eventAuthToken,
                  generation.isCurrent(eventGeneration),
                  authEpoch.isCurrent(eventAuthToken) else {
                return false
            }
            authority.invalidate()
            return true
        }
    }

    var currentOfflineWriteReplayIdentity: OfflineWriteReplayIdentity? {
        offlineWriteReplayAuthority.currentIdentity
    }

    /// Immediately abandons any in-flight replay and unbinds the outbox (logout
    /// / server teardown). Retained items stay on disk but are never replayed
    /// until a matching identity is bound again.
    func unbindOfflineWriteQueue() async {
        let invalidationRevision = offlineWriteReplayAuthority.invalidate()
        _ = await offlineWriteQueue.unbind(authorityRevision: invalidationRevision)
    }

    /// Builds the operator-facing status view model for the outbox.
    func makeOfflineQueueStatusViewModel() -> OfflineQueueStatusViewModel {
        OfflineQueueStatusViewModel(
            queue: offlineWriteQueue,
            partsInventoryService: partsInventoryService
        )
    }

    init(
        baseURL: URL? = nil,
        serverRegistry: ServerRegistry = ServerRegistry(),
        credentialsStore: ServerCredentialsStore = ServerCredentialsStore(),
        userDefaultsBox: AuthServiceUserDefaultsBox = AuthServiceUserDefaultsBox(.standard),
        observeRegistry: Bool = true,
        farmSnapshotAuthority: FarmSnapshotAuthority? = nil,
        farmSnapshotStore: (any FarmSnapshotStoring)? = nil,
        farmSnapshotOwnerStore: FarmSnapshotOwnerStore? = nil,
        farmShapeStore: FarmShapeStore? = nil,
        /// H (issue #816 reject, Hicks): injectable canonical durable
        /// authority record. Tests pass a temp-rooted instance so the
        /// production-container reopen test can prove distinct record
        /// objects observe the same file WITHOUT polluting the real
        /// Application Support directory. When nil AND no explicit
        /// `farmSnapshotAuthority` is provided, production composition
        /// synthesises a record rooted at `farmSnapshotRootURL` (or
        /// `FarmSnapshotStore.defaultRootURL()` when that is nil too).
        farmSnapshotDurableAuthorityRecord: FarmSnapshotDurableAuthorityRecord? = nil,
        /// H4/E (issue #816 reject, Bishop+Hicks): inject ONLY the snapshot
        /// root URL and let the shipping ServiceContainer composition build
        /// its canonical `FarmSnapshotDurableAuthorityRecord` AND
        /// `FarmSnapshotStore` from that root. The reject-under-remediation
        /// asked that tests prove production composition — not tests-owned
        /// manually-injected record objects — actually converges on the
        /// same durable file across reopen. Passing a temp root here
        /// (instead of a temp-rooted pre-built record) exercises exactly
        /// the shipping composition path.
        farmSnapshotRootURL: URL? = nil,
        synchronizeOfflineQueueOnStartup: Bool = true,
        offlineWriteQueueStore: (any OfflineWriteQueueStoring)? = nil,
        offlineReplayProviderResolutionHook: @escaping @Sendable () async -> Void = {},
        apiClientFactory: @escaping APIClientFactory = { baseURL, generation, accessToken, authSessionToken, serverID in
            let identity = accessToken.flatMap { token in
                serverID.map { AuthenticatedIdentity(accessToken: token, serverID: $0, authSessionToken: authSessionToken) }
            }
            return APIClient(baseURL: baseURL, serverGeneration: generation, authenticated: identity)
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
        let shapeStore = farmShapeStore
            ?? FarmShapeStore(userDefaults: userDefaultsBox.userDefaults)
        self.farmShapeStore = shapeStore
        self.offlineReplayProviderResolutionHook = offlineReplayProviderResolutionHook
        self.activeGeneration = ActiveServerGeneration()
        self.observesRegistry = observeRegistry
        self.offlineWriteQueueStore = offlineWriteQueueStore
            ?? FileOfflineWriteQueueStore(directory: Self.offlineWriteQueueDirectory())

        // H4/E (issue #816 reject, Bishop+Hicks): the effective snapshot root
        // is the injected `farmSnapshotRootURL` (test seam) OR the shipping
        // default. This is the single source of truth for both the durable
        // authority record and the store when production composition is
        // used, so both objects converge on the same on-disk root.
        let effectiveRootURL = farmSnapshotRootURL ?? FarmSnapshotStore.defaultRootURL()

        let authority: FarmSnapshotAuthority
        let durableRecord: FarmSnapshotDurableAuthorityRecord?
        if let farmSnapshotAuthority {
            // Test/injected authority: honor caller-provided wiring exactly and do
            // not attach a canonical durable record (the injection owns durability).
            authority = farmSnapshotAuthority
            durableRecord = farmSnapshotDurableAuthorityRecord
        } else {
            // H (issue #816 reject, Hicks): production composition wires ONE
            // canonical file-backed durable authority record. If the caller
            // supplied a record (e.g. a tests's temp-rooted instance) use it;
            // otherwise construct one at the effective root, so the shared
            // coordinator sees durable reserved/adopted high-water AND
            // durable tombstones on every reopen — a distinct record object
            // constructed from the same root on the next launch observes
            // the exact same file.
            let record = farmSnapshotDurableAuthorityRecord
                ?? FarmSnapshotDurableAuthorityRecord(rootURL: effectiveRootURL)
            durableRecord = record
            authority = FarmSnapshotAuthority(durableAuthorityRecord: record)
        }
        let ownerStore = farmSnapshotOwnerStore ?? FarmSnapshotOwnerStore(userDefaults: userDefaultsBox.userDefaults)
        self.farmSnapshotAuthority = authority
        self.startupPrefetchStore = StartupPrefetchStore(authority: authority)
        self.farmSnapshotDurableRecord = durableRecord
        self.farmSnapshotOwnerStore = ownerStore
        self.farmSnapshotStore = farmSnapshotStore ?? FarmSnapshotStore(
            authority: authority,
            rootURL: effectiveRootURL,
            ownerStore: ownerStore
        )
        // #789: the feature read-cache reuses the SAME authority + root so a
        // server/user switch, logout, or stale generation fences these records
        // exactly as it does the base snapshot record.
        self.featureReadCacheStore = FeatureReadCacheStore(
            authority: authority,
            rootURL: effectiveRootURL
        )

        let activeServer = serverRegistry.activeServer
        let resolvedURL = activeServer?.baseURL
            ?? baseURL
            ?? APIClient.savedBaseURL()
            ?? AppConfig.baseURL
        let accessToken = Self.validAccessToken(for: activeServer, credentialsStore: credentialsStore)
        // A2: capture the auth-operation epoch SYNCHRONOUSLY (before any await or Task
        // spawn) and bind identity + bearer in the SAME atomic construction. A later
        // fire-and-forget Task cannot read a newer epoch and clobber this client's
        // identity with a superseded operation's token.
        let reconstructedAuthToken = accessToken == nil ? nil : authOperationEpoch.current
        // J4 (issue #816 reject, Hicks): reconstructing an authenticated
        // APIClient requires the stable serverID atomically at construction.
        // The active server IS the identity this reconstructed session was
        // established for.
        let reconstructedServerID = accessToken == nil ? nil : activeServer?.id
        let client = apiClientFactory(resolvedURL, activeGeneration, accessToken, reconstructedAuthToken, reconstructedServerID)

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
        self.farmShapeService = FarmShapeService(
            apiClient: client,
            serverID: activeServer?.id,
            store: shapeStore
        )
        self.signalRService = signalRServiceFactory(resolvedURL, client)
        self.barcodeIntakeService = BarcodeIntakeService(apiClient: client)
        self.activeServerID = activeServer?.id
        #if canImport(UIKit)
        self.qrScannerService = QRSpoolScannerService()
        self.barcodeScannerService = BarcodeScannerService()
        self.nfcService = NFCService()
        PushNotificationManager.shared.configure(
            notificationService: self.notificationService,
            serverRegistry: serverRegistry,
            serverID: activeServer?.id
        )
        // Issue #1321: keep lock-screen/Notification Center action handling
        // wired to whichever services are currently live (not just at first
        // launch) so job-attention actions never execute against stale
        // service instances from a previous server/session.
        PushNotificationManager.shared.configureActionHandling(
            printerService: self.printerService,
            attentionService: self.attentionService
        )
        if let token = PushNotificationManager.shared.deviceToken, activeServer != nil {
            PushNotificationManager.shared.startTokenRegistration(token)
        }
        #endif

        if let activeServer {
            userDefaultsBox.userDefaults.set(activeServer.normalizedURLString, forKey: APIClient.serverURLKey)
            Task {
                // A2: no fire-and-forget establishReconstructedAuthSession — bearer
                // AND identity were bound atomically at APIClient construction (above)
                // from a synchronously captured epoch, so a later fire-and-forget Task
                // cannot read a newer epoch and clobber a fresher session's identity.
                await self.configureTokenExpiryChecker(client: client, serverID: activeServer.id)
            }
        }

        if observeRegistry {
            observeActiveServer()
        }

        wireSnapshotPurgeHandler()
        // H4: sweep any durable-tombstone residue a prior crash may have left,
        // independently of (and before) any activation.
        let startupStore = self.farmSnapshotStore
        Task { await startupStore.prepareStartup() }
        // #787: bind the durable outbox to the (restored) active identity and
        // drive an initial replay pass on launch.
        if synchronizeOfflineQueueOnStartup {
            Task { await self.syncOfflineWriteQueue() }
        }
    }

    /// Route registry deletion through the store's awaited purge (Gate E). Wired
    /// for every production composition that exposes a real registry, so deletion
    /// can never drop a server without first clearing its cached namespace.
    private func wireSnapshotPurgeHandler() {
        guard let serverRegistry else { return }
        let store = farmSnapshotStore
        let shapeStore = farmShapeStore
        let startupPrefetchStore = startupPrefetchStore
        serverRegistry.snapshotPurgeHandler = { serverID in
            startupPrefetchStore.removeAll()
            let result = await store.purge(serverID: serverID)
            if result == .purged {
                shapeStore.clearShape(serverID: serverID)
            }
            return result
        }
        let pinStore = CertificatePinStore()
        serverRegistry.certificatePinPurgeHandler = { server, remainingServers in
            guard server.baseURL.scheme?.lowercased() == "https",
                  let host = server.baseURL.host,
                  let endpoint = NetworkHostClassifier.endpointKey(host: host, port: server.baseURL.port) else {
                return true
            }
            let endpointStillRegistered = remainingServers.contains { candidate in
                guard candidate.baseURL.scheme?.lowercased() == "https",
                      let candidateHost = candidate.baseURL.host else {
                    return false
                }
                return NetworkHostClassifier.endpointKey(
                    host: candidateHost,
                    port: candidate.baseURL.port
                ) == endpoint
            }
            return endpointStillRegistered || pinStore.delete(endpoint: endpoint)
        }
    }

    /// Resolved capability snapshot used by demo (mock) services.
    ///
    /// Mirrors ``ResolvedSystemCapabilities/defaults`` except for
    /// `printedPartsInventoryEnabled`, which the demo fleet enables so the
    /// full #714 harvest flow is reachable and demonstrable end-to-end
    /// without a real server's SKU/output-mapping configuration. #1002
    /// correctly changed the *production* default to `false` (harvest is
    /// gated until an admin configures part SKUs), but the demo/UI-test
    /// bootstrap must keep showcasing harvest — see `HarvestUITests`,
    /// `JobDetailIPadNavigationUITests`, and `TaskActionRoutingUITests`,
    /// which all depend on this flag being enabled here (issue #1344).
    private static let demoCapabilitiesDefaults: ResolvedSystemCapabilities = {
        var defaults = ResolvedSystemCapabilities.defaults
        defaults.printedPartsInventoryEnabled = true
        return defaults
    }()

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
            capabilitiesService: StubSystemCapabilitiesService(resolved: demoCapabilitiesDefaults),
            farmShapeService: StubFarmShapeService(),
            serverRegistry: serverRegistry,
            farmSnapshotAuthority: farmSnapshotAuthority,
            farmSnapshotStore: farmSnapshotStore,
            farmSnapshotOwnerStore: farmSnapshotOwnerStore
        )
    }

    /// Replaces all services with demo implementations at runtime.
    @discardableResult
    func switchToDemo() async -> Bool {
        let replayRevision = offlineWriteReplayAuthority.invalidate()
        recordTarget(.demo)
        let epoch = transitionEpoch.current
        authOperationEpoch.advance()
        if activeServerID != nil {
            guard await unregisterNotificationToken(clearLocalToken: false) else {
                return false
            }
            guard transitionEpoch.isCurrent(epoch) else { return false }
        }
        #if canImport(UIKit)
        // Invalidate real-server notification actions before the first await.
        PushNotificationManager.shared.configure(
            notificationService: self.notificationService,
            serverRegistry: nil,
            serverID: nil,
            allowsUnscopedRegistration: false
        )
        #endif
        // Revoke synchronously before advancing the generation so no stale
        // snapshot commit can apply across the demo transition.
        farmSnapshotAuthority.revoke()
        startupPrefetchStore.removeAll()
        // C: capture the displaced real signalR and disconnect that EXACT instance so a
        // connected real receive loop cannot linger as an orphan under demo.
        let displacedSignalR = self.signalRService
        guard await offlineWriteQueue.unbind(authorityRevision: replayRevision),
              transitionEpoch.isCurrent(epoch) else {
            return false
        }
        await displacedSignalR.disconnect()
        guard transitionEpoch.isCurrent(epoch) else { return false }
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
        self.capabilitiesService = StubSystemCapabilitiesService(resolved: Self.demoCapabilitiesDefaults)
        self.farmShapeService = StubFarmShapeService()
        self.activeServerID = nil
        self.activeServerGeneration = activeGeneration.advance()
        #if canImport(UIKit)
        PushNotificationManager.shared.configure(
            notificationService: self.notificationService,
            serverRegistry: nil,
            serverID: nil,
            allowsUnscopedRegistration: false
        )
        PushNotificationManager.shared.configureActionHandling(
            printerService: self.printerService,
            attentionService: self.attentionService
        )
        self.qrScannerService = nil
        self.barcodeScannerService = nil
        self.nfcService = nil
        #endif
        return true
    }

    /// Replaces all services with real implementations backed by the active or given base URL.
    func switchToReal(baseURL: URL? = nil) {
        offlineWriteReplayAuthority.invalidate()
        let server = serverRegistry?.activeServer
        // H1: record the real/none desired target + advance the epoch synchronously.
        recordTarget(server.map { .server($0) } ?? .none)
        // Revoke synchronously before the composition changes.
        farmSnapshotAuthority.revoke()
        startupPrefetchStore.removeAll()
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
            Task {
                // A2: no fire-and-forget identity establishment — bearer AND identity
                // are set atomically inside rebuildRealServices from a synchronously
                // captured epoch.
                await self.configureTokenExpiryChecker(client: client, serverID: server.id)
            }
        }
    }

    func switchToServer(_ server: RegisteredServer) async {
        guard activeServerID != server.id else { return }
        offlineWriteReplayAuthority.invalidate()
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
    @discardableResult
    func activateFarmSnapshotForActiveServer(authToken: Int? = nil) async -> FarmSnapshotActivationResult {
        await activeServerSwitchTask?.value
        return await bindSnapshotToActiveServer(authToken: authToken)
    }

    /// D: retry a previously `.preparationFailed` activation WITHOUT requiring a new
    /// login. Re-runs the bind for the settled active server under the given auth token;
    /// if startup preparation now succeeds the snapshot binds. Callers hold the auth
    /// token from the original login/restore so identity is preserved across the retry.
    ///
    /// D-strengthening: retry is PINNED to the failed server/generation. If the caller's
    /// pending record no longer matches the current active server or the current
    /// generation, the retry refuses to bind and returns `.notApplicable`. This
    /// implements the reject: "Retry targets only failed server and cannot bind current
    /// different server."
    @discardableResult
    func retryFarmSnapshotActivation(
        authToken: Int? = nil,
        expectedServerID: UUID? = nil,
        expectedGeneration: Int? = nil
    ) async -> FarmSnapshotActivationResult {
        await activeServerSwitchTask?.value
        if let expectedServerID,
           serverRegistry?.activeServerID != expectedServerID {
            return .notApplicable
        }
        if let expectedGeneration,
           !activeGeneration.isCurrent(expectedGeneration) {
            return .notApplicable
        }
        return await bindSnapshotToActiveServer(authToken: authToken)
    }

    /// Await any in-flight active-server reconciliation so callers observe the settled
    /// composition. Public API (a caller may legitimately wait for a switch to settle).
    func awaitActiveServerSettled() async {
        await activeServerSwitchTask?.value
    }

    /// D: expose the current active-server id (read-only) so the AuthViewModel can pin
    /// pending-activation state to the failed server and invalidate the pending record
    /// when the user switches servers.
    var currentActiveServerID: UUID? { serverRegistry?.activeServerID }

    /// Starts the shape and capability reads together while authentication is
    /// still holding the root loading gate. Shape waits only for its bounded
    /// startup race while capability refresh completes under the existing gate.
    /// Returns true only while the captured server, services, and auth operation
    /// remain current so callers can safely consume the prepared capability result.
    @discardableResult
    func prepareAuthenticatedStartup(authToken: Int? = nil) async -> Bool {
        await activeServerSwitchTask?.value
        guard let serverID = serverRegistry?.activeServerID,
              activeServerID == serverID else {
            return false
        }
        if let authToken, !authOperationEpoch.isCurrent(authToken) {
            return false
        }

        let expectedCapabilities = capabilitiesService
        let expectedShape = farmShapeService
        if let authToken {
            expectedShape.beginSession(authToken: authToken)
        }
        async let capabilitiesRefresh = expectedCapabilities.prepareForReadiness()
        await expectedShape.resolveForAuthenticatedSession(
            serverID: serverID,
            timeout: FarmShapeService.startupTimeout
        )
        _ = await capabilitiesRefresh
        guard serverRegistry?.activeServerID == serverID,
              activeServerID == serverID,
              capabilitiesService === expectedCapabilities,
              farmShapeService === expectedShape else {
            return false
        }
        if let authToken, !authOperationEpoch.isCurrent(authToken) {
            return false
        }
        return true
    }

    func beginAuthenticatedStartup(authToken: Int) {
        capabilitiesService.discardPreparedReadiness()
        farmShapeService.beginSession(authToken: authToken)
    }

    func resetAuthenticatedStartupState() {
        capabilitiesService.discardPreparedReadiness()
        farmShapeService.resetSession()
    }

    /// Whether `generation` is the current active-server generation. Used to discard a
    /// stale session-expiry event posted by an APIClient we already switched away from
    /// (issue #816 H2).
    func isActiveGeneration(_ generation: Int) -> Bool {
        activeGeneration.isCurrent(generation)
    }

    /// Capture the current session, then conditionally deactivate ONLY that captured
    /// session in both the synchronous authority and the async store. A newer
    /// activation that lands during the store await survives — this never globally
    /// revokes (H3).
    func revokeFarmSnapshot() async {
        startupPrefetchStore.removeAll()
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
    @discardableResult
    private func bindSnapshotToActiveServer(authToken: Int? = nil) async -> FarmSnapshotActivationResult {
        func authStillCurrent() -> Bool { authToken.map { authOperationEpoch.isCurrent($0) } ?? true }
        // Bishop: never bind a real snapshot session while demo is the desired target —
        // a late real login/activation must not resurrect a real binding under demo.
        if case .demo = desiredTarget {
            if let session = farmSnapshotAuthority.currentSession() {
                farmSnapshotAuthority.deactivate(session)
            }
            return .notApplicable
        }
        guard let serverRegistry, let active = serverRegistry.activeServer else {
            // No active server: conditionally clear the current session if any.
            if let session = farmSnapshotAuthority.currentSession() {
                farmSnapshotAuthority.deactivate(session)
            }
            return .notApplicable
        }
        guard let ownerID = farmSnapshotOwnerStore.ownerUserID(serverID: active.id) else {
            // Token-only / unverified server: fail closed. Deactivate only a session
            // belonging to THIS active server; never a newer server's binding.
            if let session = farmSnapshotAuthority.currentSession(), session.serverID == active.id {
                farmSnapshotAuthority.deactivate(session)
            }
            return .notApplicable
        }
        // Fail closed early if this login/restore was already superseded.
        guard authStillCurrent() else { return .superseded }
        let namespace = FarmSnapshotNamespace(serverID: active.id, userID: ownerID)
        // P3: RESERVE an unpublished candidate (not yet current, so no commit can be
        // authorized against it), await store readiness, THEN publish it via a single
        // synchronous critical section that re-validates target + generation + auth
        // token with NO await between the guard and the adopt.
        //
        // H: reserve/adopt are now typed-throwing (durable overflow / persistence
        // failure) — a caught error becomes `.preparationFailed` (retryable) so the
        // auth flow surfaces and retries without a new login.
        let capturedGeneration = activeGeneration.current
        let candidate: FarmSnapshotSession?
        do {
            candidate = try farmSnapshotAuthority.reserve(namespace: namespace, generation: capturedGeneration)
        } catch {
            return .preparationFailed
        }
        guard let candidate else {
            return .notApplicable // tombstoned (purged) server — do not resurrect
        }
        // D: fail closed with a RETRYABLE result if startup readiness (residue sweep)
        // did not succeed, so the auth flow can surface it and retry without a new login.
        guard await farmSnapshotStore.prepareStartup() else { return .preparationFailed }
        // Re-validate at publication (no await between here and the adopt): the desired
        // target must still be a real server (not demo), the active server unchanged,
        // the generation current, and the auth token current.
        guard !isDemoDesiredTarget,
              serverRegistry.activeServerID == active.id,
              activeGeneration.isCurrent(capturedGeneration),
              authStillCurrent() else {
            return .superseded
        }
        let adopted: Bool
        do {
            adopted = try farmSnapshotAuthority.adopt(candidate)
        } catch {
            return .preparationFailed
        }
        guard adopted else { return .superseded }
        return .activated
    }

    private var isDemoDesiredTarget: Bool {
        if case .demo = desiredTarget { return true }
        return false
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
        let offlineWriteReplayAuthority = self.offlineWriteReplayAuthority
        withObservationTracking {
            _ = serverRegistry.activeServerID
            _ = serverRegistry.servers
        } onChange: { [weak self] in
            offlineWriteReplayAuthority.invalidate()
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
                // Demo is applied explicitly by `switchToDemo`; the worker never
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
        offlineWriteReplayAuthority.invalidate()
        if activeServerID != nil {
            guard await unregisterNotificationToken(clearLocalToken: false) else {
                scheduleNotificationHandoffRetry(.server(server), epoch: epoch)
                return
            }
        }
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
        startupPrefetchStore.removeAll()
        activeServerGeneration = activeGeneration.advance()
        let capturedGeneration = activeServerGeneration

        let accessToken = Self.validAccessToken(for: server, credentialsStore: credentialsStore)
        guard transitionEpoch.isCurrent(epoch) else { return }
        // CAS publish: the synchronous rebuild follows the epoch check with no await
        // between them, so an older switch cannot publish stale services.
        let client = rebuildRealServices(baseURL: server.baseURL, server: server, accessToken: accessToken)
        userDefaultsBox.userDefaults.set(server.normalizedURLString, forKey: APIClient.serverURLKey)
        // A2: rebuildRealServices already bound bearer + identity atomically at
        // construction from a synchronously captured epoch. No fire-and-forget
        // identity establishment — a later Task could read a newer epoch and
        // overwrite this client's identity with a superseded token.
        await configureTokenExpiryChecker(client: client, serverID: server.id)
        guard transitionEpoch.isCurrent(epoch) else { return } // superseded during the awaits
        if accessToken != nil {
            let shapeService = farmShapeService
            shapeService.beginSession(authToken: authOperationEpoch.current)
            await shapeService.resolveForAuthenticatedSession(
                serverID: server.id,
                timeout: FarmShapeService.startupTimeout
            )
            guard transitionEpoch.isCurrent(epoch) else { return }
        }

        // Bind the snapshot to the SAME captured server + generation the services
        // were rebuilt for.
        await bindSnapshotToServer(server, generation: capturedGeneration, epoch: epoch)

        guard transitionEpoch.isCurrent(epoch), accessToken != nil else { return }
        // Capture the EXACT instance we are about to connect. After connect returns, if
        // this switch was superseded (demo/none/newer switch) or the field was swapped,
        // disconnect THIS exact instance so its receive loop cannot linger as an orphan
        // (Hicks H1 / C).
        let connectingSignalR = signalRService
        do {
            try await connectingSignalR.connect()
        } catch {
            // RootView will also attempt connection when authenticated; keep switching non-fatal.
        }
        if !transitionEpoch.isCurrent(epoch) || signalRService !== connectingSignalR {
            await connectingSignalR.disconnect()
        }
        // #787: rebind the outbox to the newly-active identity and replay its
        // non-expired items. Epoch-guarded so a superseded switch never binds a
        // stale identity. A change of identity abandons any prior in-flight
        // replay inside `bind`.
        guard transitionEpoch.isCurrent(epoch) else { return }
        offlineWriteReplayAuthority.authorizeBinding()
        await syncOfflineWriteQueue()
    }

    /// Bind the snapshot to a specific captured server + generation for a switch
    /// operation, guarded by the transition epoch (H1). Resolves that server's OWN
    /// verified owner; token-only/unverified or tombstoned → no bind (fail closed).
    /// A superseded/older switch conditionally deactivates only its own session and
    /// never clears newer authority.
    ///
    /// H: reserve/adopt are typed-throwing; a durable overflow or persistence failure
    /// silently fails closed here (this is a switch, not a login — there is no VM
    /// caller to expose a retryable state to yet; the next login/restore drives the
    /// retry through `bindSnapshotToActiveServer`).
    private func bindSnapshotToServer(_ server: RegisteredServer, generation: Int, epoch: Int) async {
        guard let ownerID = farmSnapshotOwnerStore.ownerUserID(serverID: server.id) else { return }
        guard transitionEpoch.isCurrent(epoch) else { return }
        let namespace = FarmSnapshotNamespace(serverID: server.id, userID: ownerID)
        // P3: reserve an unpublished candidate, await readiness, then publish it in a
        // single synchronous critical section that re-validates the transition epoch —
        // no await between the guard and the adopt.
        let candidate: FarmSnapshotSession?
        do {
            candidate = try farmSnapshotAuthority.reserve(namespace: namespace, generation: generation)
        } catch {
            return // durable overflow / persistence failure — fail closed
        }
        guard let candidate else { return }
        guard await farmSnapshotStore.prepareStartup() else { return } // D: fail closed on prep failure
        guard transitionEpoch.isCurrent(epoch) else { return }
        do {
            guard try farmSnapshotAuthority.adopt(candidate) else { return }
        } catch {
            return // durable persistence failure at adopt — do not publish
        }
    }

    private func switchToNoActiveServer(epoch: Int) async {
        let replayRevision = offlineWriteReplayAuthority.invalidate()
        guard await offlineWriteQueue.unbind(authorityRevision: replayRevision),
              transitionEpoch.isCurrent(epoch) else {
            return
        }
        guard activeServerID != nil else { return }
        guard await unregisterNotificationToken() else {
            scheduleNotificationHandoffRetry(.none, epoch: epoch)
            return
        }
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
        startupPrefetchStore.removeAll()
        activeServerGeneration = activeGeneration.advance()
        _ = rebuildRealServices(baseURL: APIClient.savedBaseURL() ?? AppConfig.baseURL, server: nil, accessToken: nil)
    }

    private func scheduleNotificationHandoffRetry(
        _ target: DesiredTarget,
        epoch: Int
    ) {
        Task { @MainActor [weak self] in
            try? await Task.sleep(for: .seconds(5))
            guard let self,
                  self.transitionEpoch.isCurrent(epoch) else { return }
            self.requestTarget(target)
        }
    }

    private func unregisterNotificationToken(clearLocalToken: Bool = true) async -> Bool {
            #if canImport(UIKit)
            return await PushNotificationManager.shared.unregisterFromServer(clearLocalToken: clearLocalToken)
            #else
            return true
            #endif
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
        // A2: capture the auth-operation epoch SYNCHRONOUSLY (no await, no Task
        // between capture and factory call) and bind identity + bearer atomically at
        // APIClient construction. A superseded operation cannot later overwrite this
        // client's identity because the identity is fixed at init.
        let reconstructedAuthToken = accessToken == nil ? nil : authOperationEpoch.current
        // J4: same atomic serverID binding on rebuild after a server switch.
        let reconstructedServerID = accessToken == nil ? nil : server?.id
        let client = apiClientFactory(baseURL, activeGeneration, accessToken, reconstructedAuthToken, reconstructedServerID)
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
        self.farmShapeService = FarmShapeService(
            apiClient: client,
            serverID: server?.id,
            store: farmShapeStore
        )
        self.signalRService = signalRServiceFactory(baseURL, client)
        self.barcodeIntakeService = BarcodeIntakeService(apiClient: client)
        self.activeServerID = server?.id
        #if canImport(UIKit)
        self.qrScannerService = QRSpoolScannerService()
        self.barcodeScannerService = BarcodeScannerService()
        self.nfcService = NFCService()
        PushNotificationManager.shared.configure(
            notificationService: self.notificationService,
            serverRegistry: serverRegistry,
            serverID: server?.id
        )
        // Issue #1321: re-wire lock-screen/Notification Center action handling
        // to the freshly rebuilt services on every rebuild (server switch,
        // re-login, logout->login), not just at initial launch. Without this,
        // job-attention notification actions would keep executing against the
        // previous server's (possibly now-invalid) service instances.
        PushNotificationManager.shared.configureActionHandling(
            printerService: self.printerService,
            attentionService: self.attentionService
        )
        if let token = PushNotificationManager.shared.deviceToken, server != nil {
            PushNotificationManager.shared.startTokenRegistration(token)
        }
        #endif
        return client
    }

    private func configureTokenExpiryChecker(client: APIClient, serverID: UUID) async {
        let credentialsStore = credentialsStore
        await client.setTokenExpiryChecker {
            credentialsStore.isExpired(serverId: serverID)
        }
    }

    /// A2: identity establishment is now atomic AT APIClient CONSTRUCTION. This method
    /// is retained only for the identity-carry test (AuthSnapshotIdentityTests /
    /// APIClientAuthSessionTests) that exercises the compare-and-set path directly.
    /// Production composition never calls this method — it captures the epoch
    /// synchronously in the same synchronous scope as the factory call and passes
    /// the token via `APIClient.init(authSessionToken:)`. A fire-and-forget Task
    /// that reads the epoch LATE would (and did, before A2) allow a superseded
    /// operation's identity to clobber a fresher session's identity.
    private func establishReconstructedAuthSession(client: APIClient, accessToken: String?) async {
        guard let accessToken else { return }
        let token = authOperationEpoch.current
        // E: the reconstructed client already carries its stable serverID from
        // the factory; re-apply the session under the current epoch using that
        // same identity, so the authenticated apply is structurally paired.
        guard let serverID = await client.currentServerIdentity() else { return }
        _ = await client.applyAuthenticatedSessionIfCurrent(
            baseURL: nil,
            identity: AuthenticatedIdentity(accessToken: accessToken, serverID: serverID),
            epoch: authOperationEpoch, token: token
        )
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
        farmShapeService: any FarmShapeServiceProtocol,
        serverRegistry: ServerRegistry? = nil,
        farmSnapshotAuthority: FarmSnapshotAuthority? = nil,
        farmSnapshotStore: (any FarmSnapshotStoring)? = nil,
        farmSnapshotOwnerStore: FarmSnapshotOwnerStore? = nil
    ) {
        self.serverRegistry = serverRegistry
        self.credentialsStore = ServerCredentialsStore()
        self.userDefaultsBox = AuthServiceUserDefaultsBox(.standard)
        self.apiClientFactory = { baseURL, generation, accessToken, authSessionToken, serverID in
            let identity = accessToken.flatMap { token in
                serverID.map { AuthenticatedIdentity(accessToken: token, serverID: $0, authSessionToken: authSessionToken) }
            }
            return APIClient(baseURL: baseURL, serverGeneration: generation, authenticated: identity)
        }
        self.signalRServiceFactory = { baseURL, client in
            SignalRService(
                serverURL: baseURL,
                session: APIClient.makePrivateNetworkSession()
            ) {
                await client.currentAccessToken()
            }
        }
        self.offlineReplayProviderResolutionHook = {}
        self.farmShapeStore = FarmShapeStore()
        self.activeGeneration = ActiveServerGeneration()
        self.offlineWriteQueueStore = FileOfflineWriteQueueStore(
            directory: Self.offlineWriteQueueDirectory()
        )
        // Demo composition does not rebuild real services on registry changes, so
        // it does not observe until a real login reattaches the observer.
        self.observesRegistry = false
        let authority: FarmSnapshotAuthority
        let durableRecord: FarmSnapshotDurableAuthorityRecord?
        if let farmSnapshotAuthority {
            authority = farmSnapshotAuthority
            durableRecord = nil
        } else {
            // H (issue #816 reject, Hicks): demo composition also wires the
            // canonical durable record so a demo→real→relaunch sequence
            // observes the same durable reserved/adopted/tombstone state as
            // a pure production launch.
            let record = FarmSnapshotDurableAuthorityRecord(
                rootURL: FarmSnapshotStore.defaultRootURL()
            )
            durableRecord = record
            authority = FarmSnapshotAuthority(durableAuthorityRecord: record)
        }
        self.farmSnapshotAuthority = authority
        self.startupPrefetchStore = StartupPrefetchStore(authority: authority)
        self.farmSnapshotDurableRecord = durableRecord
        let demoOwnerStore = farmSnapshotOwnerStore ?? FarmSnapshotOwnerStore()
        self.farmSnapshotOwnerStore = demoOwnerStore
        self.farmSnapshotStore = farmSnapshotStore ?? FarmSnapshotStore(authority: authority, ownerStore: demoOwnerStore)
        // #789: feature read-cache reuses the same authority (default root).
        self.featureReadCacheStore = FeatureReadCacheStore(authority: authority)
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
        self.farmShapeService = farmShapeService
        #if canImport(UIKit)
        self.qrScannerService = nil
        self.barcodeScannerService = nil
        self.nfcService = nil
        #endif

        wireSnapshotPurgeHandler()
    }
}
