#if canImport(UIKit)
import UIKit
import UserNotifications

// MARK: - App Delegate

/// UIApplicationDelegate adapter for handling push notification callbacks.
/// Wired into SwiftUI lifecycle via `@UIApplicationDelegateAdaptor` in PFarmApp.
class AppDelegate: NSObject, UIApplicationDelegate {
    func application(
        _ application: UIApplication,
        didFinishLaunchingWithOptions launchOptions: [UIApplication.LaunchOptionsKey: Any]? = nil
    ) -> Bool {
        UNUserNotificationCenter.current().delegate = PushNotificationManager.shared
        return true
    }

    func application(
        _ application: UIApplication,
        didRegisterForRemoteNotificationsWithDeviceToken deviceToken: Data
    ) {
        Task { @MainActor in
            PushNotificationManager.shared.didRegisterForRemoteNotifications(deviceToken: deviceToken)
        }
    }

    func application(
        _ application: UIApplication,
        didFailToRegisterForRemoteNotificationsWithError error: Error
    ) {
        Task { @MainActor in
            PushNotificationManager.shared.didFailToRegisterForRemoteNotifications(error: error)
        }
    }

    // MARK: - Scene Configuration

    func application(
        _ application: UIApplication,
        configurationForConnecting connectingSceneSession: UISceneSession,
        options: UIScene.ConnectionOptions
    ) -> UISceneConfiguration {
        // Only support standard window scenes. Return empty config for CarPlay or other scene types
        // to prevent crashes when connected to unsupported scene roles.
        if connectingSceneSession.role == .windowApplication {
            let config = UISceneConfiguration(name: "Default Configuration", sessionRole: .windowApplication)
            config.delegateClass = nil // SwiftUI manages scene lifecycle
            return config
        } else {
            // CarPlay or other unsupported scene types get minimal config
            return UISceneConfiguration(name: nil, sessionRole: connectingSceneSession.role)
        }
    }

    func application(
        _ application: UIApplication,
        didDiscardSceneSessions sceneSessions: Set<UISceneSession>
    ) {
        // Clean up any resources for discarded scenes if needed
    }
}
#endif
