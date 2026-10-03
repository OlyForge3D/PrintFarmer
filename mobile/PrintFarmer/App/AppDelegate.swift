#if canImport(UIKit)
import UIKit
import UserNotifications

// MARK: - App Delegate

/// UIApplicationDelegate adapter for local notification handling.
/// Wired into SwiftUI lifecycle via `@UIApplicationDelegateAdaptor` in PFarmApp.
class AppDelegate: NSObject, UIApplicationDelegate {
    func application(
        _: UIApplication,
        didFinishLaunchingWithOptions _: [UIApplication.LaunchOptionsKey: Any]? = nil
    ) -> Bool {
        UNUserNotificationCenter.current().delegate = PushNotificationManager.shared
        // Issue #1321: category/action registration only needs
        // `setNotificationCategories` — it does not require notification
        // permission — so register unconditionally at launch, before the
        // user is ever prompted to allow notifications.
        PushNotificationManager.registerNotificationCategories()
        return true
    }

    // MARK: - Scene Configuration

    func application(
        _: UIApplication,
        configurationForConnecting connectingSceneSession: UISceneSession,
        options _: UIScene.ConnectionOptions
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
}
#endif
