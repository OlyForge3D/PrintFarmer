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
    var autoPrintService: any AutoDispatchServiceProtocol
    var jobAnalyticsService: any JobAnalyticsServiceProtocol
    var predictiveService: any PredictiveServiceProtocol
    var dispatchService: any DispatchServiceProtocol
    var failureDetectionService: any FailureDetectionServiceProtocol
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
    @ObservationIgnored private let observesRegistry: Bool
    @ObservationIgnored private var activeServerID: UUID?
    @ObservationIgnored private var activeServerSwitchTask: Task<Void, Never>?
    @ObservationIgnored private var activeServerSwitchRequested = false

    init(
        baseURL: URL? = nil,
        serverRegistry: ServerRegistry = ServerRegistry(),
        credentialsStore: ServerCredentialsStore = ServerCredentialsStore(),
        userDefaultsBox: AuthServiceUserDefaultsBox = AuthServiceUserDefaultsBox(.standard),
        observeRegistry: Bool = true,
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
            serverRegistry: serverRegistry
        )
        self.printerService = PrinterService(apiClient: client)
        self.jobService = JobService(apiClient: client)
        self.locationService = LocationService(apiClient: client)
        self.statisticsService = StatisticsService(apiClient: client)
        self.notificationService = NotificationService(apiClient: client)
        self.spoolService = SpoolService(apiClient: client)
        self.maintenanceService = MaintenanceService(apiClient: client)
        self.autoPrintService = AutoDispatchService(apiClient: client)
        self.jobAnalyticsService = JobAnalyticsService(apiClient: client)
        self.predictiveService = PredictiveService(apiClient: client)
        self.dispatchService = DispatchService(apiClient: client)
        self.failureDetectionService = FailureDetectionService(apiClient: client)
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
    }

    /// Creates a ServiceContainer wired with demo (mock) services.
    static func demo() -> ServiceContainer {
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
            autoPrintService: DemoAutoDispatchService(),
            jobAnalyticsService: DemoJobAnalyticsService(),
            predictiveService: DemoPredictiveService(),
            dispatchService: DemoDispatchService(),
            failureDetectionService: DemoFailureDetectionService()
        )
    }

    /// Replaces all services with demo implementations at runtime.
    func switchToDemo() {
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
        self.autoPrintService = DemoAutoDispatchService()
        self.jobAnalyticsService = DemoJobAnalyticsService()
        self.predictiveService = DemoPredictiveService()
        self.dispatchService = DemoDispatchService()
        self.failureDetectionService = DemoFailureDetectionService()
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
        let resolvedURL = server?.baseURL
            ?? baseURL
            ?? APIClient.savedBaseURL()
            ?? AppConfig.baseURL
        let accessToken = Self.validAccessToken(for: server, credentialsStore: credentialsStore)
        let client = rebuildRealServices(baseURL: resolvedURL, server: server, accessToken: accessToken)
        if let server {
            Task { await self.configureTokenExpiryChecker(client: client, serverID: server.id) }
        }
    }

    func switchToServer(_ server: RegisteredServer) async {
        guard activeServerID != server.id else { return }
        await switchToActiveServer(server)
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
        let oldSignalRService = signalRService
        await oldSignalRService.disconnect()
        activeServerGeneration = activeGeneration.advance()

        let accessToken = Self.validAccessToken(for: server, credentialsStore: credentialsStore)
        let client = rebuildRealServices(baseURL: server.baseURL, server: server, accessToken: accessToken)
        userDefaultsBox.userDefaults.set(server.normalizedURLString, forKey: APIClient.serverURLKey)
        await configureTokenExpiryChecker(client: client, serverID: server.id)

        guard accessToken != nil else { return }
        do {
            try await signalRService.connect()
        } catch {
            // RootView will also attempt connection when authenticated; keep switching non-fatal.
        }
    }

    private func switchToNoActiveServer() async {
        guard activeServerID != nil else { return }
        let oldSignalRService = signalRService
        await oldSignalRService.disconnect()
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
            serverRegistry: serverRegistry
        )
        self.printerService = PrinterService(apiClient: client)
        self.jobService = JobService(apiClient: client)
        self.locationService = LocationService(apiClient: client)
        self.statisticsService = StatisticsService(apiClient: client)
        self.notificationService = NotificationService(apiClient: client)
        self.spoolService = SpoolService(apiClient: client)
        self.maintenanceService = MaintenanceService(apiClient: client)
        self.autoPrintService = AutoDispatchService(apiClient: client)
        self.jobAnalyticsService = JobAnalyticsService(apiClient: client)
        self.predictiveService = PredictiveService(apiClient: client)
        self.dispatchService = DispatchService(apiClient: client)
        self.failureDetectionService = FailureDetectionService(apiClient: client)
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
        autoPrintService: any AutoDispatchServiceProtocol,
        jobAnalyticsService: any JobAnalyticsServiceProtocol,
        predictiveService: any PredictiveServiceProtocol,
        dispatchService: any DispatchServiceProtocol,
        failureDetectionService: any FailureDetectionServiceProtocol
    ) {
        self.serverRegistry = nil
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
        self.observesRegistry = false
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
        self.autoPrintService = autoPrintService
        self.jobAnalyticsService = jobAnalyticsService
        self.predictiveService = predictiveService
        self.dispatchService = dispatchService
        self.failureDetectionService = failureDetectionService
        #if canImport(UIKit)
        self.qrScannerService = nil
        self.barcodeScannerService = nil
        self.nfcService = nil
        #endif
    }
}
