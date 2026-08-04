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

    // MARK: - shouldTriggerRecovery() policy (issue #1071)

    private static let wifi = NetworkPathSnapshot(reachability: .satisfied, interface: .wifi)
    private static let cellular = NetworkPathSnapshot(reachability: .satisfied, interface: .cellular)

    func testFirstPathSnapshotDoesNotTriggerRecovery() {
        // NWPathMonitor delivers the current path immediately on start(), and
        // start() already probes — triggering here would just double it.
        XCTAssertFalse(
            ConnectionMonitor.shouldTriggerRecovery(previous: nil, current: Self.wifi)
        )
    }

    func testIdenticalPathSnapshotsAreDeduped() {
        XCTAssertFalse(
            ConnectionMonitor.shouldTriggerRecovery(previous: Self.wifi, current: Self.wifi),
            "pathUpdateHandler repeats .satisfied freely; repeats must not fan out into probes"
        )
    }

    func testLosingThePathNeverTriggersRecovery() {
        for lost in [NetworkPathSnapshot.unsatisfied,
                     NetworkPathSnapshot(reachability: .requiresConnection, interface: .other)] {
            XCTAssertFalse(
                ConnectionMonitor.shouldTriggerRecovery(previous: Self.wifi, current: lost),
                "A path change is only ever a hint to probe — hysteresis owns .offline"
            )
        }
    }

    func testRegainingThePathTriggersRecovery() {
        XCTAssertTrue(
            ConnectionMonitor.shouldTriggerRecovery(previous: .unsatisfied, current: Self.wifi)
        )
    }

    func testInterfaceHandoffTriggersRecovery() {
        // The device never looked offline, but every existing socket is dead.
        XCTAssertTrue(
            ConnectionMonitor.shouldTriggerRecovery(previous: Self.wifi, current: Self.cellular)
        )
        XCTAssertTrue(
            ConnectionMonitor.shouldTriggerRecovery(previous: Self.cellular, current: Self.wifi)
        )
    }

    // MARK: - path change → recovery sequence

    func testRegainedPathRunsRecoverySequence() async {
        mockAPIClient.stubResponse(json: "{\"status\":\"ok\"}", statusCode: 200)
        let signalR = MockSignalRService()
        signalR.connectionState = .disconnected

        let monitor = ConnectionMonitor()
        monitor.pathChangeDebounce = .zero
        monitor.configure(apiClient: mockAPIClient.apiClient, signalRService: signalR)

        monitor.handlePathChange(.unsatisfied)
        monitor.handlePathChange(Self.wifi)
        await monitor.awaitPendingResume()

        XCTAssertEqual(signalR.ensureConnectedCallCount, 1, "the hub must be re-armed, not left in backoff")
        XCTAssertTrue(monitor.isServerReachable)
        XCTAssertEqual(
            monitor.status,
            .connected,
            "the post-hub re-sample must land so the bar updates now, not on the next poll tick"
        )
    }

    func testPathChangeBurstCollapsesToASingleRecovery() async {
        mockAPIClient.stubResponse(json: "{\"status\":\"ok\"}", statusCode: 200)
        let signalR = MockSignalRService()
        signalR.connectionState = .disconnected

        let monitor = ConnectionMonitor()
        monitor.pathChangeDebounce = .milliseconds(50)
        monitor.configure(apiClient: mockAPIClient.apiClient, signalRService: signalR)

        // A single Wi-Fi↔cellular handoff emits several events back-to-back.
        // They run synchronously on the main actor, so each supersedes the
        // previous pending resume before it can begin — no sleeps needed to
        // make this deterministic.
        monitor.handlePathChange(.unsatisfied)
        monitor.handlePathChange(Self.wifi)
        monitor.handlePathChange(Self.cellular)
        monitor.handlePathChange(Self.wifi)
        await monitor.awaitPendingResume()

        XCTAssertEqual(signalR.ensureConnectedCallCount, 1, "a burst must debounce to one recovery")
    }

    func testPathLossCannotPublishOffline() async {
        mockAPIClient.stubResponse(json: "{\"status\":\"ok\"}", statusCode: 200)
        let signalR = MockSignalRService()
        signalR.connectionState = .connected

        let monitor = ConnectionMonitor()
        monitor.pathChangeDebounce = .zero
        monitor.configure(apiClient: mockAPIClient.apiClient, signalRService: signalR)

        await monitor.refresh()
        XCTAssertEqual(monitor.status, .connected)

        monitor.handlePathChange(Self.wifi)
        monitor.handlePathChange(.unsatisfied)
        await monitor.awaitPendingResume()

        XCTAssertEqual(signalR.ensureConnectedCallCount, 0)
        XCTAssertEqual(
            monitor.status,
            .connected,
            "the path observer must never write status — only refresh() hysteresis may"
        )
    }

    // MARK: - path observer lifecycle

    func testStartBeginsObservingAndStopCancels() {
        let observer = FakeNetworkPathObserver()
        let monitor = ConnectionMonitor(pathObserver: observer)

        monitor.start()
        XCTAssertEqual(observer.startCount, 1)
        XCTAssertTrue(observer.isRunning)

        monitor.stop()
        XCTAssertFalse(observer.isRunning, "a stopped monitor must not keep a live NWPathMonitor")
        XCTAssertEqual(observer.cancelCount, 1)
    }

    func testRestartCancelsThePreviousObserver() {
        let observer = FakeNetworkPathObserver()
        let monitor = ConnectionMonitor(pathObserver: observer)

        // start() is called again on every server switch; each must replace the
        // observer rather than stack a second one.
        monitor.start()
        monitor.start()

        XCTAssertEqual(observer.startCount, 2)
        XCTAssertEqual(observer.cancelCount, 1, "the second start must cancel the first observer")

        monitor.stop()
    }

    func testObserverSnapshotsReachHandlePathChange() async {
        mockAPIClient.stubResponse(json: "{\"status\":\"ok\"}", statusCode: 200)
        let signalR = MockSignalRService()
        signalR.connectionState = .disconnected

        let observer = FakeNetworkPathObserver()
        let monitor = ConnectionMonitor(pathObserver: observer)
        monitor.pathChangeDebounce = .zero
        // A long poll interval keeps the loop from racing the assertion.
        monitor.pollInterval = .seconds(600)
        monitor.configure(apiClient: mockAPIClient.apiClient, signalRService: signalR)
        monitor.start()

        observer.emit(.unsatisfied)
        observer.emit(Self.wifi)
        await monitor.awaitPendingResume()

        XCTAssertEqual(signalR.ensureConnectedCallCount, 1)

        monitor.stop()
    }

    // MARK: - sample ticket fence

    func testStaleProbeCannotRepaintBannerAfterNewerSample() async {
        let barrier = AsyncBarrier()
        let calls = CallCounter()
        mockAPIClient.asyncRequestHandler = { request in
            if await calls.next() == 1 {
                // Park the older probe until a newer, healthy sample published.
                await barrier.arriveAndWait()
                throw URLError(.cannotConnectToHost)
            }
            return (
                TestData.httpResponse(url: request.url, statusCode: 200),
                Data("{\"status\":\"ok\"}".utf8)
            )
        }

        let signalR = MockSignalRService()
        signalR.connectionState = .connected
        let monitor = ConnectionMonitor()
        monitor.configure(apiClient: mockAPIClient.apiClient, signalRService: signalR)

        // A path-triggered refresh racing the 5s poll is exactly this shape.
        let stale = Task { await monitor.refresh() }
        await barrier.waitUntilArrived()

        await monitor.refresh()
        XCTAssertEqual(monitor.status, .connected)

        barrier.release()
        await stale.value

        XCTAssertEqual(
            monitor.status,
            .connected,
            "an older probe finishing late must not repaint the banner"
        )
        XCTAssertEqual(
            monitor.consecutiveReachabilityFailures,
            0,
            "a discarded sample must not pollute the hysteresis counter either"
        )
    }
}

// MARK: - Test doubles

/// Deterministic ``NetworkPathObserving`` — no radio, no `NWPathMonitor`.
@MainActor
private final class FakeNetworkPathObserver: NetworkPathObserving {
    private(set) var startCount = 0
    private(set) var cancelCount = 0
    private var handler: (@Sendable @MainActor (NetworkPathSnapshot) -> Void)?

    var isRunning: Bool { handler != nil }

    func start(onChange: @escaping @Sendable @MainActor (NetworkPathSnapshot) -> Void) {
        startCount += 1
        handler = onChange
    }

    func cancel() {
        cancelCount += 1
        handler = nil
    }

    /// Delivers a snapshot the way a real monitor would.
    func emit(_ snapshot: NetworkPathSnapshot) {
        handler?(snapshot)
    }
}

/// Counts handler invocations across the URLSession's execution queue.
private actor CallCounter {
    private var count = 0

    func next() -> Int {
        count += 1
        return count
    }
}
