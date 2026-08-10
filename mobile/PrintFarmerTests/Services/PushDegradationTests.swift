import XCTest
import os
@testable import PrintFarmer

/// Issue #818 — graceful no-push degradation when native push is disabled.
///
/// The beta ships with native push DISABLED by default. When the iOS client
/// registers its APNs device token against a push-disabled backend,
/// `POST/DELETE /api/notifications/device-tokens` returns a 404 `ProblemDetails`
/// with `code == "featureDisabled"`. The app must treat that as a normal,
/// expected "push not configured" state: no user-visible error, no retry storm,
/// no crash — and keep surfacing operator alerts via SignalR + on-device local
/// notifications.
///
/// All tests are deterministic: they drive the real `APIClient` through
/// `MockURLProtocol` (Part A) or await `PushNotificationManager`'s awaitable
/// registration seam with a call-counting stub (Part B). No sleeps, polling, or
/// elapsed-time pass criteria.
final class PushDegradationTests: XCTestCase {

    // Canonical lowercase-hex APNs token (64 chars → 32 bytes, backend minimum).
    private static let sampleToken = String(repeating: "ab", count: 32)
    private static let sampleOriginServerId = "00000000-0000-0000-0000-000000000099"

    private static func registrationResponseJSON() -> String {
        "{\"serverId\":\"\(sampleOriginServerId)\"}"
    }

    private static func featureDisabledProblemJSON() -> String {
        """
        {
          "type": "https://printfarmer/errors/feature-disabled",
          "title": "Feature Disabled",
          "status": 404,
          "detail": "Native push is disabled on this server.",
          "code": "featureDisabled"
        }
        """
    }

    // MARK: - Part A: NotificationService wire contract

    func testRegisterPostsToPluralDeviceTokensRouteWithCanonicalBody() async throws {
        let mock = MockAPIClient()
        mock.stubResponse(json: Self.registrationResponseJSON())
        let service = NotificationService(apiClient: mock.apiClient)

        try await service.registerDeviceToken(Self.sampleToken, platform: "ios")

        let request = try XCTUnwrap(mock.capturedRequests.first)
        XCTAssertEqual(request.httpMethod, "POST")
        XCTAssertEqual(request.url?.path, "/api/notifications/device-tokens",
                       "Client must hit the plural device-tokens route the backend actually serves (#708/#818).")

        let body = try XCTUnwrap(request.capturedHTTPBody())
        let json = try XCTUnwrap(try JSONSerialization.jsonObject(with: body) as? [String: Any])
        XCTAssertEqual(json["token"] as? String, Self.sampleToken)
        XCTAssertEqual(json["platform"] as? String, "ios",
                       "Wire platform must be the canonical `ios` token.")
        XCTAssertEqual(json["environment"] as? String, "development",
                       "Debug/test builds report the `development` APNs environment.")
        let installationId = try XCTUnwrap(json["installationId"] as? String)
        XCTAssertFalse(installationId.isEmpty,
                       "A canonical installationId is required by the backend contract.")
        XCTAssertTrue(NativePushInstallation.isCanonicalInstallationId(installationId),
                      "installationId must be in canonical wire form; got \(installationId)")
    }

    func testRegisterPropagatesFeatureDisabled() async {
        let mock = MockAPIClient()
        mock.stubResponse(json: Self.featureDisabledProblemJSON(), statusCode: 404)
        let service = NotificationService(apiClient: mock.apiClient)

        do {
            try await service.registerDeviceToken(Self.sampleToken, platform: "ios")
            XCTFail("Expected NetworkError.featureDisabled from a push-disabled backend")
        } catch let error as NetworkError {
            guard case .featureDisabled(let apiError) = error else {
                return XCTFail("Expected .featureDisabled, got \(error)")
            }
            XCTAssertEqual(apiError.code, "featureDisabled")
            XCTAssertEqual(apiError.status, 404)
        } catch {
            XCTFail("Unexpected error type: \(error)")
        }
    }

    func testUnregisterDeletesPluralRouteWithInstallationIdBody() async throws {
        let mock = MockAPIClient()
        mock.stubResponse(json: Self.registrationResponseJSON())
        let service = NotificationService(apiClient: mock.apiClient)

        try await service.unregisterDeviceToken(Self.sampleToken)

        let request = try XCTUnwrap(mock.capturedRequests.first)
        XCTAssertEqual(request.httpMethod, "DELETE")
        XCTAssertEqual(request.url?.path, "/api/notifications/device-tokens")

        let body = try XCTUnwrap(request.capturedHTTPBody())
        let json = try XCTUnwrap(try JSONSerialization.jsonObject(with: body) as? [String: Any])
        let installationId = try XCTUnwrap(json["installationId"] as? String)
        XCTAssertTrue(NativePushInstallation.isCanonicalInstallationId(installationId))
    }

    func testUnregisterPropagatesFeatureDisabled() async {
        let mock = MockAPIClient()
        mock.stubResponse(json: Self.featureDisabledProblemJSON(), statusCode: 404)
        let service = NotificationService(apiClient: mock.apiClient)

        do {
            try await service.unregisterDeviceToken(Self.sampleToken)
            XCTFail("Expected NetworkError.featureDisabled")
        } catch let error as NetworkError {
            guard case .featureDisabled = error else {
                return XCTFail("Expected .featureDisabled, got \(error)")
            }
        } catch {
            XCTFail("Unexpected error type: \(error)")
        }
    }

    func testRegisterStableInstallationIdIsReusedAcrossCalls() async throws {
        let mock = MockAPIClient()
        mock.stubResponse(json: Self.registrationResponseJSON())
        let service = NotificationService(apiClient: mock.apiClient)

        try await service.registerDeviceToken(Self.sampleToken, platform: "ios")
        try await service.registerDeviceToken(Self.sampleToken, platform: "ios")

        XCTAssertEqual(mock.capturedRequests.count, 2)
        let ids: [String] = try mock.capturedRequests.map { request in
            let body = try XCTUnwrap(request.capturedHTTPBody())
            let json = try XCTUnwrap(try JSONSerialization.jsonObject(with: body) as? [String: Any])
            return try XCTUnwrap(json["installationId"] as? String)
        }
        XCTAssertEqual(ids[0], ids[1],
                       "installationId must be stable so re-registration is idempotent per server.")
    }

    func testRegisterReturnsCanonicalServerId() async throws {
        let mock = MockAPIClient()
        mock.stubResponse(json: Self.registrationResponseJSON())
        let service = NotificationService(apiClient: mock.apiClient)

        let originServerId = try await service.registerDeviceToken(Self.sampleToken, platform: "ios")

        XCTAssertEqual(originServerId.uuidString.lowercased(), Self.sampleOriginServerId)
    }

    // MARK: - Part B: PushNotificationManager degradation

    @MainActor
    func testRegisterWithFeatureDisabledSurfacesNoErrorAndNoRetry() async {
        let manager = PushNotificationManager.shared
        let stub = CountingNotificationService()
        stub.registerError = NetworkError.featureDisabled(Self.featureDisabledAPIError())
        manager.configure(notificationService: stub)

        await manager.registerTokenWithServer(Self.sampleToken)

        XCTAssertNil(manager.registrationError,
                     "featureDisabled must NOT surface a user-visible registration error.")
        XCTAssertTrue(manager.localOnlyAlerting,
                      "Disabled push must flip the app into the local-only alerting signal.")
        XCTAssertEqual(stub.registerCount, 1,
                       "Exactly one attempt — no retry storm on a disabled backend.")
    }

    @MainActor
    func testSuccessfulRegistrationPersistsOriginServerIdForActiveServer() async throws {
        let suiteName = "PushDegradationTests-\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suiteName))
        defer { defaults.removePersistentDomain(forName: suiteName) }
        let registry = ServerRegistry(
            userDefaults: defaults,
            migrateLegacyServerURL: false
        )
        let server = try registry.add(
            displayName: "Primary",
            baseURL: URL(string: "https://primary.example")!
        )
        let stub = MockNotificationService()

        PushNotificationManager.shared.configure(
            notificationService: stub,
            serverRegistry: registry,
            serverID: server.id
        )
        await PushNotificationManager.shared.registerTokenWithServer(Self.sampleToken)

        XCTAssertEqual(registry.activeServer?.originServerId, stub.originServerIdToReturn)
    }

    @MainActor
    func testRegisterSuccessClearsLocalOnlyAlerting() async {
        let manager = PushNotificationManager.shared
        let stub = CountingNotificationService()
        manager.configure(notificationService: stub)

        await manager.registerTokenWithServer(Self.sampleToken)

        XCTAssertNil(manager.registrationError)
        XCTAssertFalse(manager.localOnlyAlerting,
                       "A successful registration (push enabled) must clear the local-only signal.")
        XCTAssertEqual(stub.registerCount, 1)
    }

    @MainActor
    func testReEnableRegistersOnNextAttemptWithoutReinstall() async {
        let manager = PushNotificationManager.shared
        let stub = CountingNotificationService()
        // Server starts push-disabled…
        stub.registerError = NetworkError.featureDisabled(Self.featureDisabledAPIError())
        manager.configure(notificationService: stub)
        await manager.registerTokenWithServer(Self.sampleToken)
        XCTAssertTrue(manager.localOnlyAlerting)

        // …operator later enables relay/direct; next auth/refresh registers cleanly.
        stub.registerError = nil
        await manager.registerTokenWithServer(Self.sampleToken)

        XCTAssertFalse(manager.localOnlyAlerting,
                       "Re-enabling push must register on the next attempt with no reinstall.")
        XCTAssertNil(manager.registrationError)
        XCTAssertEqual(stub.registerCount, 2, "Exactly two attempts total across the transition.")
    }

    @MainActor
    func testPerServerIsolationEnabledThenDisabled() async {
        let manager = PushNotificationManager.shared

        // Server A: push enabled → registers, no local-only signal.
        let enabled = CountingNotificationService()
        manager.configure(notificationService: enabled)
        await manager.registerTokenWithServer(Self.sampleToken)
        XCTAssertFalse(manager.localOnlyAlerting)

        // Switch to Server B: push disabled. `configure` clears the prior signal,
        // and the next registration degrades cleanly for THIS server only.
        let disabled = CountingNotificationService()
        disabled.registerError = NetworkError.featureDisabled(Self.featureDisabledAPIError())
        manager.configure(notificationService: disabled)
        XCTAssertFalse(manager.localOnlyAlerting,
                       "Switching servers must clear the previous server's disabled state.")
        await manager.registerTokenWithServer(Self.sampleToken)
        XCTAssertTrue(manager.localOnlyAlerting)

        XCTAssertEqual(enabled.registerCount, 1)
        XCTAssertEqual(disabled.registerCount, 1)
    }

    @MainActor
    func testUnregisterFeatureDisabledIsBenign() async {
        let manager = PushNotificationManager.shared
        let stub = CountingNotificationService()
        stub.unregisterError = NetworkError.featureDisabled(Self.featureDisabledAPIError())
        manager.configure(notificationService: stub)
        // Seed a device token deterministically so unregister proceeds.
        manager.didRegisterForRemoteNotifications(deviceToken: Data([0xab, 0xcd]))

        await manager.unregisterFromServer()

        XCTAssertNil(manager.registrationError,
                     "A no-op unregister against a disabled server is not an error.")
        XCTAssertNil(manager.deviceToken, "Local token must still be cleared on unregister.")
    }

    @MainActor
    func testForegroundAlertingIsPresentedRegardlessOfPushDisabled() async {
        // AC#2: live foreground alerting (SignalR + local notifications) stays
        // functional with remote push disabled. Foreground presentation options
        // are constant and never gated on device-token registration.
        let manager = PushNotificationManager.shared
        let stub = CountingNotificationService()
        stub.registerError = NetworkError.featureDisabled(Self.featureDisabledAPIError())
        manager.configure(notificationService: stub)
        await manager.registerTokenWithServer(Self.sampleToken)
        XCTAssertTrue(manager.localOnlyAlerting)

        let options = PushNotificationManager.foregroundPresentationOptions()
        XCTAssertTrue(options.contains(.banner))
        XCTAssertTrue(options.contains(.sound))
        XCTAssertTrue(options.contains(.badge))
    }

    // MARK: - Helpers

    private static func featureDisabledAPIError() -> APIError {
        let json = Data(featureDisabledProblemJSON().utf8)
        // APIError mirrors the RFC 7807 body; decode the real shape so the stub
        // carries the same discriminator the backend emits.
        return try! JSONDecoder().decode(APIError.self, from: json)
    }
}

/// Call-counting `NotificationServiceProtocol` stub with per-outcome error
/// injection. Lets the degradation tests assert exact register/unregister counts
/// (zero retries) without any timing dependence.
private final class CountingNotificationService: NotificationServiceProtocol, @unchecked Sendable {
    private struct State {
        var registerCount = 0
        var unregisterCount = 0
        var registerError: Error?
        var unregisterError: Error?
    }
    private let state = OSAllocatedUnfairLock(initialState: State())

    var registerCount: Int { state.withLock { $0.registerCount } }
    var unregisterCount: Int { state.withLock { $0.unregisterCount } }

    var registerError: Error? {
        get { state.withLock { $0.registerError } }
        set { state.withLock { $0.registerError = newValue } }
    }
    var unregisterError: Error? {
        get { state.withLock { $0.unregisterError } }
        set { state.withLock { $0.unregisterError = newValue } }
    }

    func registerDeviceToken(_ token: String, platform: String) async throws -> UUID {
        let error = state.withLock { s -> Error? in
            s.registerCount += 1
            return s.registerError
        }
        if let error { throw error }
        return UUID(uuidString: "00000000-0000-0000-0000-000000000099")!
    }

    func unregisterDeviceToken(_ token: String) async throws {
        let error = state.withLock { s -> Error? in
            s.unregisterCount += 1
            return s.unregisterError
        }
        if let error { throw error }
    }

    // Unused by these tests.
    func listNotifications(limit: Int?) async throws -> [AppNotification] { [] }
    func getUnreadCount() async throws -> Int { 0 }
    func markRead(id: String) async throws {}
    func markAllRead(ids: [String]) async throws {}
    func delete(id: String) async throws {}
}
