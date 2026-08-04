import XCTest
@testable import PrintFarmer

/// Tests for ConnectionMonitor: the pure state-resolution matrix and the
/// end-to-end `refresh()` path using a stubbed APIClient + mock SignalR hub.
@MainActor
final class ConnectionMonitorTests: XCTestCase {

    nonisolated(unsafe) private var mockAPIClient: MockAPIClient!

    override func setUp() async throws {
        try await super.setUp()
        mockAPIClient = MockAPIClient()
    }

    override func tearDown() async throws {
        mockAPIClient = nil
        try await super.tearDown()
    }

    // MARK: - resolve() matrix

    func testResolveOfflineWhenServerUnreachable() {
        for state in [SignalRConnectionState.disconnected, .connecting, .connected, .reconnecting] {
            XCTAssertEqual(
                ConnectionMonitor.resolve(isServerReachable: false, signalR: state),
                .offline,
                "Unreachable server must be offline regardless of hub state \(state)"
            )
        }
    }

    func testResolveConnectedWhenReachableAndHubConnected() {
        XCTAssertEqual(
            ConnectionMonitor.resolve(isServerReachable: true, signalR: .connected),
            .connected
        )
    }

    func testResolveConnectingWhenReachableAndHubConnecting() {
        XCTAssertEqual(
            ConnectionMonitor.resolve(isServerReachable: true, signalR: .connecting),
            .connecting
        )
    }

    func testResolveDegradedWhenReachableButHubDown() {
        XCTAssertEqual(
            ConnectionMonitor.resolve(isServerReachable: true, signalR: .reconnecting),
            .degraded
        )
        XCTAssertEqual(
            ConnectionMonitor.resolve(isServerReachable: true, signalR: .disconnected),
            .degraded
        )
    }

    // MARK: - hysteresis resolve() matrix

    func testResolveToleratesSingleFailureBelowThreshold() {
        XCTAssertEqual(
            ConnectionMonitor.resolve(
                isServerReachable: false,
                signalR: .connected,
                consecutiveFailures: 1,
                threshold: 2
            ),
            .degraded,
            "A single failed probe must not publish the alarming offline banner"
        )
    }

    func testResolveGoesOfflineAtThreshold() {
        XCTAssertEqual(
            ConnectionMonitor.resolve(
                isServerReachable: false,
                signalR: .connected,
                consecutiveFailures: 2,
                threshold: 2
            ),
            .offline
        )
        XCTAssertEqual(
            ConnectionMonitor.resolve(
                isServerReachable: false,
                signalR: .connected,
                consecutiveFailures: 7,
                threshold: 2
            ),
            .offline
        )
    }

    func testResolveHysteresisIsBypassedWhenReachable() {
        // A stale failure count must never suppress a healthy sample.
        XCTAssertEqual(
            ConnectionMonitor.resolve(
                isServerReachable: true,
                signalR: .connected,
                consecutiveFailures: 5,
                threshold: 2
            ),
            .connected
        )
    }

    func testResolveThresholdOfZeroBehavesAsImmediateOffline() {
        // Guard against a misconfigured threshold silently disabling offline.
        XCTAssertEqual(
            ConnectionMonitor.resolve(
                isServerReachable: false,
                signalR: .connected,
                consecutiveFailures: 1,
                threshold: 0
            ),
            .offline
        )
    }

    // MARK: - refresh() integration

    func testRefreshReportsConnectedWhenHealthyAndHubConnected() async {
        mockAPIClient.stubResponse(json: "{\"status\":\"ok\"}", statusCode: 200)
        let signalR = MockSignalRService()
        signalR.connectionState = .connected

        let monitor = ConnectionMonitor()
        monitor.configure(apiClient: mockAPIClient.apiClient, signalRService: signalR)

        await monitor.refresh()

        XCTAssertTrue(monitor.isServerReachable)
        XCTAssertEqual(monitor.status, .connected)
    }

    func testRefreshReportsDegradedWhenHealthyButHubDisconnected() async {
        mockAPIClient.stubResponse(json: "{\"status\":\"ok\"}", statusCode: 200)
        let signalR = MockSignalRService()
        signalR.connectionState = .disconnected

        let monitor = ConnectionMonitor()
        monitor.configure(apiClient: mockAPIClient.apiClient, signalRService: signalR)

        await monitor.refresh()

        XCTAssertTrue(monitor.isServerReachable)
        XCTAssertEqual(monitor.status, .degraded)
    }

    func testSingleTransportErrorDoesNotGoOffline() async {
        mockAPIClient.stubError(.cannotConnectToHost)
        let signalR = MockSignalRService()
        signalR.connectionState = .connected

        let monitor = ConnectionMonitor()
        monitor.configure(apiClient: mockAPIClient.apiClient, signalRService: signalR)

        await monitor.refresh()

        XCTAssertFalse(monitor.isServerReachable)
        XCTAssertEqual(monitor.consecutiveReachabilityFailures, 1)
        XCTAssertEqual(
            monitor.status,
            .degraded,
            "One dropped probe must not paint the red offline banner"
        )
    }

    func testRefreshReportsOfflineOnTransportError() async {
        mockAPIClient.stubError(.cannotConnectToHost)
        let signalR = MockSignalRService()
        signalR.connectionState = .connected

        let monitor = ConnectionMonitor()
        monitor.configure(apiClient: mockAPIClient.apiClient, signalRService: signalR)

        await monitor.refresh()
        await monitor.refresh()

        XCTAssertFalse(monitor.isServerReachable)
        XCTAssertEqual(monitor.status, .offline)
    }

    func testRefreshReportsOfflineOnServerError() async {
        mockAPIClient.stubResponse(json: "{}", statusCode: 503)
        let signalR = MockSignalRService()
        signalR.connectionState = .connected

        let monitor = ConnectionMonitor()
        monitor.configure(apiClient: mockAPIClient.apiClient, signalRService: signalR)

        await monitor.refresh()
        await monitor.refresh()

        XCTAssertFalse(monitor.isServerReachable)
        XCTAssertEqual(monitor.status, .offline)
    }

    func testSuccessfulProbeResetsFailureStreak() async {
        mockAPIClient.stubError(.cannotConnectToHost)
        let signalR = MockSignalRService()
        signalR.connectionState = .connected

        let monitor = ConnectionMonitor()
        monitor.configure(apiClient: mockAPIClient.apiClient, signalRService: signalR)

        await monitor.refresh()
        XCTAssertEqual(monitor.consecutiveReachabilityFailures, 1)

        mockAPIClient.stubResponse(json: "{\"status\":\"ok\"}", statusCode: 200)
        await monitor.refresh()
        XCTAssertEqual(monitor.consecutiveReachabilityFailures, 0)
        XCTAssertEqual(monitor.status, .connected)

        // Recovery must fully re-arm the hysteresis: the next single failure is
        // tolerated again rather than immediately tipping to offline.
        mockAPIClient.stubError(.cannotConnectToHost)
        await monitor.refresh()
        XCTAssertEqual(monitor.status, .degraded)
    }

    // MARK: - stop() resets displayed state

    func testStopClearsPreviousStatusImmediately() async {
        mockAPIClient.stubResponse(json: "{\"status\":\"ok\"}", statusCode: 200)
        let signalR = MockSignalRService()
        signalR.connectionState = .connected

        let monitor = ConnectionMonitor()
        monitor.configure(apiClient: mockAPIClient.apiClient, signalRService: signalR)

        await monitor.refresh()
        XCTAssertEqual(monitor.status, .connected)

        // Stopping (e.g. on a server switch) must clear the previous server's
        // status right away rather than leaving it on screen.
        monitor.stop()

        XCTAssertEqual(monitor.status, .connecting)
        XCTAssertEqual(monitor.signalRState, .disconnected)
        XCTAssertFalse(monitor.isServerReachable)
    }
}
