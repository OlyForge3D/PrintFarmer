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
    static let pendingKey = "app.pendingExternalScanRequest"

    /// A request that has waited longer than this without reaching authenticated
    /// main content is abandoned rather than replayed into a later session.
    static let expiry: TimeInterval = 10 * 60

    @MainActor
    static func request(
        userDefaults: UserDefaults = .standard,
        now: Date = Date(),
        id: UUID = UUID()
    ) {
        store(
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
        userDefaults: UserDefaults = .standard,
        now: Date = Date()
    ) -> PendingExternalScanRequest? {
        if let data = userDefaults.data(forKey: pendingKey) {
            return try? JSONDecoder().decode(PendingExternalScanRequest.self, from: data)
        }
        guard userDefaults.bool(forKey: pendingKey) else { return nil }
        let upgraded = PendingExternalScanRequest(id: UUID(), requestedAt: now, scopedServerID: nil)
        store(upgraded, userDefaults: userDefaults)
        return upgraded
    }

    /// Binds a still-waiting request to the identity it was raised against so a
    /// later sign-in to a different server cannot claim it.
    static func scope(to serverID: UUID?, userDefaults: UserDefaults = .standard) {
        guard var request = pending(userDefaults: userDefaults),
              request.scopedServerID == nil,
              let serverID else { return }
        request.scopedServerID = serverID
        store(request, userDefaults: userDefaults)
    }

    @discardableResult
    static func consume(userDefaults: UserDefaults = .standard) -> Bool {
        guard pending(userDefaults: userDefaults) != nil else { return false }
        userDefaults.removeObject(forKey: pendingKey)
        return true
    }

    /// Drops the request without routing it — logout, an abandoned login, or a
    /// server/account switch all invalidate the original intent.
    static func cancel(userDefaults: UserDefaults = .standard) {
        userDefaults.removeObject(forKey: pendingKey)
    }

    private static func store(
        _ request: PendingExternalScanRequest,
        userDefaults: UserDefaults
    ) {
        guard let data = try? JSONEncoder().encode(request) else { return }
        userDefaults.set(data, forKey: pendingKey)
    }
}

/// Pure decision table for an external scan request, so the lifecycle is
/// testable without standing up the SwiftUI shell (#2480).
enum ExternalScanRouting {
    enum Decision: Equatable {
        /// Nothing pending.
        case idle
        /// Keep waiting for a coherent authenticated flow, binding the request to
        /// `scopeTo` if it is not bound yet.
        case retain(scopeTo: UUID?)
        /// The request can no longer be honoured coherently — drop it.
        case cancel
        /// Open the scanner now.
        case deliver
    }

    /// - Parameters:
    ///   - pending: the persisted request, if any.
    ///   - isShowingMainContent: whether the authenticated shell is on screen.
    ///   - activeServerID: the currently selected server.
    ///   - now: evaluation time, for expiry.
    static func decide(
        pending: PendingExternalScanRequest?,
        isShowingMainContent: Bool,
        activeServerID: UUID?,
        now: Date = Date()
    ) -> Decision {
        guard let pending else { return .idle }
        guard now.timeIntervalSince(pending.requestedAt) <= ExternalScanRequestStore.expiry else {
            return .cancel
        }
        guard isShowingMainContent else {
            return .retain(scopeTo: pending.scopedServerID ?? activeServerID)
        }
        if let scopedServerID = pending.scopedServerID, scopedServerID != activeServerID {
            return .cancel
        }
        return .deliver
    }
}

struct OpenScannerIntent: AppIntent {
    static let title: LocalizedStringResource = "Scan with PrintFarmer"
    static let description = IntentDescription(
        "Opens PrintFarmer directly to the camera scanner."
    )
    static let openAppWhenRun = true

    @MainActor
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
