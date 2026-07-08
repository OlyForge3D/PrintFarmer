import XCTest
@testable import PrintFarmer

/// Tests for ConnectionMonitor: the pure state-resolution matrix and the
/// end-to-end `refresh()` path using a stubbed APIClient + mock SignalR hub.
@MainActor
final class ConnectionMonitorTests: XCTestCase {

    override func setUp() {
        super.setUp()
        MockURLProtocol.reset()
    }

    override func tearDown() {
        MockURLProtocol.reset()
        super.tearDown()
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

    // MARK: - refresh() integration

    func testRefreshReportsConnectedWhenHealthyAndHubConnected() async {
        MockAPIClient.stubResponse(json: "{\"status\":\"ok\"}", statusCode: 200)
        let signalR = MockSignalRService()
        signalR.connectionState = .connected

        let monitor = ConnectionMonitor()
        monitor.configure(apiClient: MockAPIClient.makeAPIClient(), signalRService: signalR)

        await monitor.refresh()

        XCTAssertTrue(monitor.isServerReachable)
        XCTAssertEqual(monitor.status, .connected)
    }

    func testRefreshReportsDegradedWhenHealthyButHubDisconnected() async {
        MockAPIClient.stubResponse(json: "{\"status\":\"ok\"}", statusCode: 200)
        let signalR = MockSignalRService()
        signalR.connectionState = .disconnected

        let monitor = ConnectionMonitor()
        monitor.configure(apiClient: MockAPIClient.makeAPIClient(), signalRService: signalR)

        await monitor.refresh()

        XCTAssertTrue(monitor.isServerReachable)
        XCTAssertEqual(monitor.status, .degraded)
    }

    func testRefreshReportsOfflineOnTransportError() async {
        MockAPIClient.stubError(.cannotConnectToHost)
        let signalR = MockSignalRService()
        signalR.connectionState = .connected

        let monitor = ConnectionMonitor()
        monitor.configure(apiClient: MockAPIClient.makeAPIClient(), signalRService: signalR)

        await monitor.refresh()

        XCTAssertFalse(monitor.isServerReachable)
        XCTAssertEqual(monitor.status, .offline)
    }

    func testRefreshReportsOfflineOnServerError() async {
        MockAPIClient.stubResponse(json: "{}", statusCode: 503)
        let signalR = MockSignalRService()
        signalR.connectionState = .connected

        let monitor = ConnectionMonitor()
        monitor.configure(apiClient: MockAPIClient.makeAPIClient(), signalRService: signalR)

        await monitor.refresh()

        XCTAssertFalse(monitor.isServerReachable)
        XCTAssertEqual(monitor.status, .offline)
    }

    // MARK: - stop() resets displayed state

    func testStopClearsPreviousStatusImmediately() async {
        MockAPIClient.stubResponse(json: "{\"status\":\"ok\"}", statusCode: 200)
        let signalR = MockSignalRService()
        signalR.connectionState = .connected

        let monitor = ConnectionMonitor()
        monitor.configure(apiClient: MockAPIClient.makeAPIClient(), signalRService: signalR)

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
