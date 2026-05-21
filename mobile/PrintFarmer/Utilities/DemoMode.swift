import Foundation
import Observation

/// Singleton managing demo mode state.
/// When active, the app uses mock services instead of the real backend.
@MainActor @Observable
final class DemoMode {
    static let shared = DemoMode()

    private static let key = "isDemoModeActive"

    var isActive: Bool {
        didSet { UserDefaults.standard.set(isActive, forKey: Self.key) }
    }

    private init() {
        self.isActive = UserDefaults.standard.bool(forKey: Self.key)
    }

    func activate() {
        isActive = true
    }

    func deactivate() {
        isActive = false
    }
}
