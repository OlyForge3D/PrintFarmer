import Foundation

// MARK: - Notification Service

actor NotificationService: NotificationServiceProtocol {
    private let apiClient: APIClient

    init(apiClient: APIClient) {
        self.apiClient = apiClient
    }

    func listNotifications(limit: Int? = 50) async throws -> [AppNotification] {
        let query = limit.map { "?limit=\($0)" } ?? ""
        return try await apiClient.get("/api/notifications\(query)")
    }

    func getUnreadCount() async throws -> Int {
        let response: UnreadCountResponse = try await apiClient.get("/api/notifications/unread/count")
        return response.unreadCount
    }

    func markRead(id: String) async throws {
        try await apiClient.putVoid("/api/notifications/\(id)/mark-read")
    }

    func markAllRead(ids: [String]) async throws {
        let request = MarkMultipleReadRequest(notificationIds: ids)
        try await apiClient.putVoid("/api/notifications/mark-read-batch", body: request)
    }

    func delete(id: String) async throws {
        try await apiClient.delete("/api/notifications/\(id)")
    }

    // MARK: - Device Token Registration

    /// Backend route for native-push device-token register/unregister (issue #708).
    /// When native push is disabled (`NativePush__Mode=disabled`, the beta default)
    /// this endpoint returns a 404 `ProblemDetails` with `code == "featureDisabled"`,
    /// which `APIClient` maps to `NetworkError.featureDisabled`. See issue #818.
    private static let deviceTokensPath = "/api/notifications/device-tokens"

    /// Register an APNs device token with the backend for native push.
    /// Sends the canonical registration contract expected by
    /// `POST /api/notifications/device-tokens` (see `NativePushRegistrationContract`).
    /// The `platform` argument is retained for protocol compatibility; the wire
    /// value is always the canonical `"ios"` token.
    func registerDeviceToken(_ token: String, platform: String = "ios") async throws -> UUID {
        let body = DeviceTokenRegistrationBody(
            installationId: NativePushInstallation.identifier(),
            token: token,
            platform: NativePushInstallation.canonicalPlatform,
            environment: NativePushInstallation.environment,
            appBundleId: NativePushInstallation.appBundleId
        )
        let response: DeviceTokenRegistrationResponse = try await apiClient.post(Self.deviceTokensPath, body: body)
        return response.serverId
    }

    /// Unregister this installation's device token from the backend (e.g., on
    /// logout or server switch). `DELETE /api/notifications/device-tokens` keys
    /// off the installation identifier carried in the body; the `token` argument
    /// is retained for protocol compatibility only.
    func unregisterDeviceToken(_ token: String) async throws {
        let body = DeviceTokenUnregistrationBody(
            installationId: NativePushInstallation.identifier()
        )
        try await apiClient.deleteVoid(Self.deviceTokensPath, body: body)
    }
}

// MARK: - Native-push wire contract

/// Canonical request body for `POST /api/notifications/device-tokens`.
/// Property names are the camelCase wire keys the backend binds; see
/// `DeviceTokenRegistrationRequest` and `NativePushRegistrationContract`.
private struct DeviceTokenRegistrationBody: Encodable, Sendable {
    let installationId: String
    let token: String
    let platform: String
    let environment: String
    let appBundleId: String?
}

private struct DeviceTokenRegistrationResponse: Decodable, Sendable {
    let serverId: UUID
}

/// Canonical request body for `DELETE /api/notifications/device-tokens`.
private struct DeviceTokenUnregistrationBody: Encodable, Sendable {
    let installationId: String
}

/// Derives the canonical native-push registration identity for this install.
enum NativePushInstallation {
    static let canonicalPlatform = "ios"

    /// APNs environment inferred from the build configuration, matching the
    /// `aps-environment` entitlement convention (development for debug builds,
    /// production for release). Both values satisfy the backend's canonical set.
    static var environment: String {
        #if DEBUG
        return "development"
        #else
        return "production"
        #endif
    }

    /// Lowercased app bundle identifier, included for diagnostics only when it is
    /// already in canonical form; otherwise omitted so a non-conforming bundle id
    /// never turns an otherwise-valid registration into a 400.
    static var appBundleId: String? {
        guard let raw = Bundle.main.bundleIdentifier?.lowercased(),
              isCanonicalBundleId(raw) else {
            return nil
        }
        return raw
    }

    /// Stable per-installation identifier, persisted once and reused across auth,
    /// token refresh, and server switches so re-registration is idempotent.
    static func identifier(defaults: UserDefaults = .standard) -> String {
        if let existing = defaults.string(forKey: installationIdKey),
           isCanonicalInstallationId(existing) {
            return existing
        }
        let generated = UUID().uuidString
        defaults.set(generated, forKey: installationIdKey)
        return generated
    }

    private static let installationIdKey = "pf_native_push_installation_id"

    static func isCanonicalInstallationId(_ value: String) -> Bool {
        guard !value.isEmpty, value.count <= 128,
              let first = value.first, first.isASCIILetterOrDigit else {
            return false
        }
        return value.allSatisfy { $0.isASCIILetterOrDigit || $0 == "." || $0 == "_" || $0 == ":" || $0 == "-" }
    }

    private static func isCanonicalBundleId(_ value: String) -> Bool {
        guard !value.isEmpty, value.count <= 256 else { return false }
        let segments = value.split(separator: ".", omittingEmptySubsequences: false)
            .flatMap { $0.split(separator: "-", omittingEmptySubsequences: false) }
        return segments.allSatisfy { segment in
            !segment.isEmpty && segment.allSatisfy { ($0 >= "a" && $0 <= "z") || ($0 >= "0" && $0 <= "9") }
        }
    }
}

private extension Character {
    var isASCIILetterOrDigit: Bool {
        (self >= "A" && self <= "Z") || (self >= "a" && self <= "z") || (self >= "0" && self <= "9")
    }
}
