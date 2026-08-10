import SwiftUI

@main
struct PFarmApp: App {
    #if canImport(UIKit)
    @UIApplicationDelegateAdaptor(AppDelegate.self) private var appDelegate
    #endif
    @State private var router = AppRouter()
    @State private var authViewModel: AuthViewModel
    @State private var serverRegistry: ServerRegistry
    @State private var services: ServiceContainer
    @State private var themeManager = ThemeManager()

    init() {
        if UITestBootstrap.isEnabled {
            // Deterministic UI-test bootstrap (#706): seed an ephemeral
            // registry with an active server and wire demo services. The
            // launch mode decides auth state — `.authenticated` renders the
            // operator shell; `.unauthenticated` (login-flow tests) renders
            // LoginView on a fresh simulator without touching the network.
            let bundle = UITestBootstrap.makeBundle(mode: UITestBootstrap.mode)
            _serverRegistry = State(initialValue: bundle.serverRegistry)
            _services = State(initialValue: bundle.services)
            _authViewModel = State(initialValue: bundle.authViewModel)
            return
        }

        let registry = ServerRegistry()
        _serverRegistry = State(initialValue: registry)
        if DemoMode.shared.isActive {
            let container = ServiceContainer.demo(serverRegistry: registry)
            _services = State(initialValue: container)
            _authViewModel = State(initialValue: AuthViewModel(services: container))
        } else {
            let resolvedURL: URL
            if let mockURL = ProcessInfo.processInfo.environment["PFARM_MOCK_SERVER_URL"],
               let url = URL(string: mockURL) {
                resolvedURL = url
            } else {
                resolvedURL = APIClient.savedBaseURL() ?? AppConfig.baseURL
            }
            let container = ServiceContainer(baseURL: resolvedURL, serverRegistry: registry)
            _services = State(initialValue: container)
            _authViewModel = State(initialValue: AuthViewModel(services: container))
        }
    }

    var body: some Scene {
        WindowGroup {
            RootView()
                .environment(router)
                .environment(authViewModel)
                .environment(serverRegistry)
                .environment(services)
                .environment(themeManager)
                .tint(Color.pfAccent)
                .preferredColorScheme(themeManager.preferredColorScheme)
                .onOpenURL { url in
                    if let destination = DeepLinkHandler.parse(url: url) {
                        router.navigate(to: destination)
                    }
                }
                .onChange(of: serverRegistry.activeServerID) {
                    router.invalidatePendingNavigation()
                }
                .onChange(of: services.activeServerGeneration) {
                    router.invalidatePendingNavigation()
                }
                #if canImport(UIKit)
                .onReceive(NotificationCenter.default.publisher(for: .pushNotificationTapped)) { notification in
                    let userInfo = PushNotificationManager.shared.consumePendingRemoteTap()
                        ?? notification.userInfo
                    if !DemoMode.shared.isActive {
                        router.routeNotification(
                            userInfo: userInfo ?? [:],
                            activeOriginServerId: serverRegistry.activeServer?.originServerId
                        )
                    }
                }
                .onReceive(NotificationCenter.default.publisher(for: .localNotificationTapped)) { notification in
                    let userInfo = PushNotificationManager.shared.consumePendingLocalTap()
                        ?? notification.userInfo
                    if !DemoMode.shared.isActive,
                       let userInfo,
                       let printerIdString = userInfo["printerId"] as? String,
                       let printerId = UUID(uuidString: printerIdString) {
                        router.navigate(to: .printerReady(id: printerId))
                    } else {
                        // F1 (#706): notification-tap without a printer ID lands
                        // on Attention where the notification itself lives.
                        router.selectedTab = .attention
                    }
                }
                #endif
                .alert(
                    "Couldn't Open Notification",
                    isPresented: Binding(
                        get: { router.notificationRoutingError != nil },
                        set: { if !$0 { router.notificationRoutingError = nil } }
                    )
                ) {
                    Button("OK") {
                        router.notificationRoutingError = nil
                    }
                } message: {
                    Text(router.notificationRoutingError ?? "")
                }
                .task {
                    await authViewModel.restoreSession()
                    #if canImport(UIKit)
                    if !UITestBootstrap.isEnabled {
                        PushNotificationManager.shared.configure(
                            notificationService: services.notificationService,
                            serverRegistry: DemoMode.shared.isActive ? nil : serverRegistry,
                            serverID: DemoMode.shared.isActive ? nil : serverRegistry.activeServerID,
                            allowsUnscopedRegistration: !DemoMode.shared.isActive
                        )
                        // Issue #1321: wire the services job-attention lock-screen
                        // actions (Pause/Resume/Cancel/Snooze) execute against.
                        PushNotificationManager.shared.configureActionHandling(
                            printerService: services.printerService,
                            attentionService: services.attentionService
                        )
                        let pendingRemoteTap = PushNotificationManager.shared.consumePendingRemoteTap()
                        if !DemoMode.shared.isActive, let userInfo = pendingRemoteTap {
                            router.routeNotification(
                                userInfo: userInfo,
                                activeOriginServerId: serverRegistry.activeServer?.originServerId
                            )
                        }
                        let pendingLocalTap = PushNotificationManager.shared.consumePendingLocalTap()
                        if !DemoMode.shared.isActive, let userInfo = pendingLocalTap,
                           let printerIdString = userInfo["printerId"] as? String,
                           let printerId = UUID(uuidString: printerIdString) {
                            router.navigate(to: .printerReady(id: printerId))
                        }
                        await PushNotificationManager.shared.refreshPermissionStatus()
                        if PushNotificationManager.shared.pushEnabled {
                            await PushNotificationManager.shared.requestPermissionAndRegister()
                        }
                    }
                    #endif
                }
        }
    }
}
