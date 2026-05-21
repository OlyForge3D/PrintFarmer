import SwiftUI

@main
struct PFarmApp: App {
    #if canImport(UIKit)
    @UIApplicationDelegateAdaptor(AppDelegate.self) private var appDelegate
    #endif
    @State private var router = AppRouter()
    @State private var authViewModel: AuthViewModel
    @State private var services: ServiceContainer
    @State private var themeManager = ThemeManager()

    init() {
        if DemoMode.shared.isActive {
            let container = ServiceContainer.demo()
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
            let container = ServiceContainer(baseURL: resolvedURL)
            _services = State(initialValue: container)
            _authViewModel = State(initialValue: AuthViewModel(services: container))
        }
    }

    var body: some Scene {
        WindowGroup {
            RootView()
                .environment(router)
                .environment(authViewModel)
                .environment(services)
                .environment(themeManager)
                .tint(Color.pfAccent)
                .preferredColorScheme(themeManager.preferredColorScheme)
                .onOpenURL { url in
                    if let destination = DeepLinkHandler.parse(url: url) {
                        router.navigate(to: destination)
                    }
                }
                #if canImport(UIKit)
                .onReceive(NotificationCenter.default.publisher(for: .pushNotificationTapped)) { notification in
                    guard let userInfo = notification.userInfo,
                          let urlString = userInfo["link"] as? String,
                          let url = URL(string: urlString),
                          let destination = DeepLinkHandler.parse(url: url) else { return }
                    router.navigate(to: destination)
                }
                .onReceive(NotificationCenter.default.publisher(for: .localNotificationTapped)) { notification in
                    if let userInfo = notification.userInfo,
                       let printerIdString = userInfo["printerId"] as? String,
                       let printerId = UUID(uuidString: printerIdString) {
                        router.navigate(to: .printerReady(id: printerId))
                    } else {
                        router.selectedTab = .printers
                    }
                }
                #endif
                .task {
                    await authViewModel.restoreSession()
                    #if canImport(UIKit)
                    PushNotificationManager.shared.configure(notificationService: services.notificationService)
                    await PushNotificationManager.shared.refreshPermissionStatus()
                    if PushNotificationManager.shared.pushEnabled {
                        await PushNotificationManager.shared.requestPermissionAndRegister()
                    }
                    #endif
                }
        }
    }
}
