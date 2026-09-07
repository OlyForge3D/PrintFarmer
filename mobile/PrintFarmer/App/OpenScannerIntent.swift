import AppIntents
import Foundation

extension Notification.Name {
    static let externalScanRequested = Notification.Name("externalScanRequested")
}

/// A scan request raised from outside the app (App Shortcut, widget, Siri).
///
/// The request outlives the process, so it carries enough context to decide
/// whether replaying it later is still coherent (#2480): when it was raised,
/// and — once the shell has seen it — which registered server it was intended
/// for.
struct PendingExternalScanRequest: Codable, Equatable, Sendable {
    let id: UUID
    let requestedAt: Date
    /// The server the request is bound to. `nil` until the shell first observes
    /// the request and scopes it to whatever identity is currently selected.
    var scopedServerID: UUID?
}

enum ExternalScanRequestStore {
    static let suiteName = "group.com.olyforge3d.printfarmer"
    static let pendingKey = "app.pendingExternalScanRequest"

    static var sharedUserDefaults: UserDefaults {
        guard let userDefaults = UserDefaults(suiteName: suiteName) else {
            preconditionFailure("Unable to access scanner App Group \(suiteName)")
        }
        return userDefaults
    }

    /// A request that has waited longer than this without reaching authenticated
    /// main content is abandoned rather than replayed into a later session.
    static let expiry: TimeInterval = 10 * 60

    @MainActor
    static func request(
        userDefaults: UserDefaults = sharedUserDefaults,
        now: Date = Date(),
        id: UUID = UUID()
    ) {
        _ = store(
            PendingExternalScanRequest(id: id, requestedAt: now, scopedServerID: nil),
            userDefaults: userDefaults
        )
        NotificationCenter.default.post(name: .externalScanRequested, object: nil)
    }

    /// Reads the pending request without clearing it.
    ///
    /// A previous build persisted a bare `Bool` under the same key; that value is
    /// upgraded in place so an in-flight request survives the app update instead
    /// of being silently dropped.
    static func pending(
        userDefaults: UserDefaults = sharedUserDefaults,
        now: Date = Date()
    ) -> PendingExternalScanRequest? {
        if let data = userDefaults.data(forKey: pendingKey) {
            return try? JSONDecoder().decode(PendingExternalScanRequest.self, from: data)
        }
        guard userDefaults.bool(forKey: pendingKey) else { return nil }
        let upgraded = PendingExternalScanRequest(id: UUID(), requestedAt: now, scopedServerID: nil)
        guard store(upgraded, userDefaults: userDefaults) else { return nil }
        return upgraded
    }

    /// Binds a still-waiting request to the identity it was raised against so a
    /// later sign-in to a different server cannot claim it.
    static func scope(
        to serverID: UUID?,
        userDefaults: UserDefaults = sharedUserDefaults
    ) {
        guard var request = pending(userDefaults: userDefaults),
              request.scopedServerID == nil,
              let serverID else { return }
        request.scopedServerID = serverID
        _ = store(request, userDefaults: userDefaults)
    }

    @discardableResult
    static func consume(userDefaults: UserDefaults = sharedUserDefaults) -> Bool {
        guard pending(userDefaults: userDefaults) != nil else { return false }
        userDefaults.removeObject(forKey: pendingKey)
        return true
    }

    /// Drops the request without routing it — logout, an abandoned login, or a
    /// server/account switch all invalidate the original intent.
    static func cancel(userDefaults: UserDefaults = sharedUserDefaults) {
        userDefaults.removeObject(forKey: pendingKey)
    }

    @discardableResult
    static func store(
        _ request: PendingExternalScanRequest,
        userDefaults: UserDefaults
    ) -> Bool {
        guard let data = try? JSONEncoder().encode(request) else { return false }
        userDefaults.set(data, forKey: pendingKey)
        return userDefaults.data(forKey: pendingKey) == data
    }
}

struct OpenScannerIntent: AppIntent {
    static let title: LocalizedStringResource = "Scan with PrintFarmer"
    static let description = IntentDescription(
        "Opens PrintFarmer directly to the camera scanner."
    )
    static let openAppWhenRun = true

    @available(iOS 26.0, *)
    static var supportedModes: IntentModes {
        .foreground(.immediate)
    }

    @MainActor
    func perform() async throws -> some IntentResult {
        try await perform(userDefaults: ExternalScanRequestStore.sharedUserDefaults)
    }

    @MainActor
    func perform(userDefaults: UserDefaults) async throws -> some IntentResult {
        ExternalScanRequestStore.request(userDefaults: userDefaults)
        return .result()
    }
}
