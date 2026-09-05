import AppIntents
import Foundation

extension Notification.Name {
    static let externalScanRequested = Notification.Name("externalScanRequested")
}

enum ExternalScanRequestStore {
    static let pendingKey = "app.pendingExternalScanRequest"

    static func request(userDefaults: UserDefaults = .standard) {
        userDefaults.set(true, forKey: pendingKey)
        NotificationCenter.default.post(name: .externalScanRequested, object: nil)
    }

    static func consume(userDefaults: UserDefaults = .standard) -> Bool {
        guard userDefaults.bool(forKey: pendingKey) else { return false }
        userDefaults.removeObject(forKey: pendingKey)
        return true
    }
}

struct OpenScannerIntent: AppIntent {
    static let title: LocalizedStringResource = "Scan with PrintFarmer"
    static let description = IntentDescription(
        "Opens PrintFarmer directly to the camera scanner."
    )
    static let openAppWhenRun = true

    func perform() async throws -> some IntentResult {
        ExternalScanRequestStore.request()
        return .result()
    }
}

struct PrintFarmerAppShortcuts: AppShortcutsProvider {
    static var appShortcuts: [AppShortcut] {
        AppShortcut(
            intent: OpenScannerIntent(),
            phrases: [
                "Scan with \(.applicationName)",
                "Open the scanner in \(.applicationName)"
            ],
            shortTitle: "Scan",
            systemImageName: "barcode.viewfinder"
        )
    }

    static let shortcutTileColor: ShortcutTileColor = .orange
}
