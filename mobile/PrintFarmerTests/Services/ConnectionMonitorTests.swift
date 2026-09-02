import Foundation
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

    // MARK: - Backend readiness gate

    func testReadinessSucceedsWhenEveryEnabledProbeSucceeds() async {
        let recorder = BackendProbeRecorder()
        let diagnostics = BackendReadinessDiagnosticRecorder()
        let capabilities = TestCapabilitiesService()
        let plan = BackendReadinessPlan(
            capabilitiesService: capabilities,
            probes: [
                BackendReadinessProbe(endpoint: .api) {
                    await recorder.record(.api)
                },
                BackendReadinessProbe(endpoint: .attention) {
                    await recorder.record(.attention)
                },
            ]
        )

        let result = await BackendReadinessChecker(
            diagnosticRecorder: diagnostics.record
        ).check(plan: plan)

        XCTAssertFalse(result.wasCancelled)
        XCTAssertTrue(result.failures.isEmpty)
        let recorded = await recorder.snapshot()
        XCTAssertEqual(Set(recorded), Set([.api, .attention]))
        let diagnosticSnapshot = diagnostics.snapshot()
        XCTAssertEqual(
            Set(diagnosticSnapshot.map(\.endpoint)),
            Set([.api, .systemCapabilities, .attention])
        )
        XCTAssertTrue(diagnosticSnapshot.allSatisfy { $0.outcome == .succeeded })
    }

    func testSuccessfulReadinessPublishesAttentionCoverageAndPrinters() async throws {
        let root = FarmSnapshotFixtures.tempRoot()
        addTeardownBlock { try? FileManager.default.removeItem(at: root) }
        let authority = FarmSnapshotFixtures.makeAuthority(
            tombstoneDefaults: UserDefaults(suiteName: trackedSuiteName("startup-prefetch-success"))!
        )
        let session = try XCTUnwrap(try authority.mint(
            namespace: FarmSnapshotNamespace(serverID: UUID(), userID: UUID()),
            generation: 0
        ))
        let feed = makeAttentionFeed(healthyPrinterCount: 4)
        let fleet = FleetFilamentCoverage(
            printers: [],
            evaluatedAtUtc: Date(timeIntervalSince1970: 1_000)
        )
        let printers = [try TestData.decodePrinter()]
        let reachabilityFeed = makeAttentionFeed(healthyPrinterCount: 1)
        let container = ServiceContainer(
            observeRegistry: false,
            farmSnapshotAuthority: authority,
            farmSnapshotRootURL: root,
            synchronizeOfflineQueueOnStartup: false
        )
        let attention = MockAttentionService()
        attention.getFeedHandler = { _, limit in
            limit == nil ? feed : reachabilityFeed
        }
        let printer = MockPrinterService()
        printer.printersToReturn = printers
        container.attentionService = attention
        container.filamentCoverageService = StubFilamentCoverageService(fleet: fleet)
        container.printerService = printer
        container.capabilitiesService = TestCapabilitiesService()
        let shippingPlan = BackendReadinessPlan(services: container)
        let plan = BackendReadinessPlan(
            capabilitiesService: shippingPlan.capabilitiesService,
            probes: shippingPlan.probes.filter {
                [.attention, .filamentCoverage, .printers].contains($0.endpoint)
            },
            startupPrefetchAttempt: shippingPlan.startupPrefetchAttempt
        )
        let gate = BackendConnectionGate()

        await gate.check(plan: plan, generation: 0) { true }

        XCTAssertEqual(gate.state, .ready)
        let attentionCalls = attention.getFeedCalls
        XCTAssertEqual(attentionCalls.count, 2)
        XCTAssertTrue(attentionCalls.allSatisfy { $0.cursor == nil })
        XCTAssertEqual(attentionCalls.filter { $0.limit == nil }.count, 1)
        XCTAssertEqual(attentionCalls.filter { $0.limit == 1 }.count, 1)
        XCTAssertEqual(printer.listIncludeDisabledArg, false)
        var consumedFeed: AttentionFeed?
        var consumedFleet: FleetFilamentCoverage?
        var consumedPrinters: [Printer]?
        XCTAssertTrue(container.startupPrefetchStore.consumeAttention { consumedFeed = $0.value })
        XCTAssertTrue(container.startupPrefetchStore.consumeFilamentCoverage { consumedFleet = $0.value })
        XCTAssertTrue(container.startupPrefetchStore.consumePrinters { consumedPrinters = $0.value })
        XCTAssertEqual(consumedFeed, feed)
        XCTAssertEqual(consumedFleet, fleet)
        XCTAssertEqual(consumedPrinters?.map(\.id), printers.map(\.id))
        XCTAssertFalse(container.startupPrefetchStore.consumeAttention { _ in XCTFail("attention must be one-shot") })
        XCTAssertFalse(container.startupPrefetchStore.consumeFilamentCoverage { _ in XCTFail("coverage must be one-shot") })
        XCTAssertFalse(container.startupPrefetchStore.consumePrinters { _ in XCTFail("printers must be one-shot") })
        XCTAssertTrue(authority.isCurrent(session))
    }

    func testConstructingReadinessPlanPreservesPrefetchUntilGateStarts() async throws {
        let root = FarmSnapshotFixtures.tempRoot()
        addTeardownBlock { try? FileManager.default.removeItem(at: root) }
        let authority = FarmSnapshotFixtures.makeAuthority(
            tombstoneDefaults: UserDefaults(suiteName: trackedSuiteName("startup-prefetch-plan-lifecycle"))!
        )
        let session = try XCTUnwrap(try authority.mint(
            namespace: FarmSnapshotNamespace(serverID: UUID(), userID: UUID()),
            generation: 0
        ))
        let container = ServiceContainer(
            observeRegistry: false,
            farmSnapshotAuthority: authority,
            farmSnapshotRootURL: root,
            synchronizeOfflineQueueOnStartup: false
        )
        let initialFeed = makeAttentionFeed(healthyPrinterCount: 4)
        let initialAttempt = try XCTUnwrap(
            container.startupPrefetchStore.makeAttempt(session: session, generation: 0)
        )
        initialAttempt.captureAttention(initialFeed)
        initialAttempt.publish()

        let shippingPlan = BackendReadinessPlan(services: container)

        var consumedFeed: AttentionFeed?
        XCTAssertTrue(
            container.startupPrefetchStore.consumeAttention {
                consumedFeed = $0.value
            }
        )
        XCTAssertEqual(consumedFeed, initialFeed)

        let supersededFeed = makeAttentionFeed(healthyPrinterCount: 8)
        let supersededAttempt = try XCTUnwrap(
            container.startupPrefetchStore.makeAttempt(session: session, generation: 0)
        )
        supersededAttempt.captureAttention(supersededFeed)
        supersededAttempt.publish()

        let started = AsyncBarrier()
        let release = AsyncBarrier()
        defer {
            started.close()
            release.close()
        }
        let gatePlan = BackendReadinessPlan(
            capabilitiesService: shippingPlan.capabilitiesService,
            probes: [
                BackendReadinessProbe(endpoint: .api) {
                    started.signal()
                    await release.arriveAndWait()
                },
            ],
            startupPrefetchAttempt: shippingPlan.startupPrefetchAttempt
        )
        let gate = BackendConnectionGate()
        let check = Task {
            await gate.check(plan: gatePlan, generation: 0) { true }
        }

        await started.waitUntilArrived()
        XCTAssertFalse(
            container.startupPrefetchStore.consumeAttention {
                XCTFail("a real gate attempt must supersede the preceding handoff")
            }
        )
        release.release()
        await check.value
        XCTAssertEqual(gate.state, .ready)
    }

    func testStaleGateStartDoesNotClearCurrentSessionPrefetch() async throws {
        let authority = FarmSnapshotFixtures.makeAuthority(
            tombstoneDefaults: UserDefaults(suiteName: trackedSuiteName("startup-prefetch-stale-plan"))!
        )
        let staleSession = try XCTUnwrap(try authority.mint(
            namespace: FarmSnapshotNamespace(serverID: UUID(), userID: UUID()),
            generation: 1
        ))
        let store = StartupPrefetchStore(authority: authority)
        let staleAttempt = try XCTUnwrap(
            store.makeAttempt(session: staleSession, generation: 1)
        )
        let stalePlan = BackendReadinessPlan(
            capabilitiesService: TestCapabilitiesService(),
            probes: [],
            startupPrefetchAttempt: staleAttempt
        )

        let currentSession = try XCTUnwrap(try authority.mint(
            namespace: FarmSnapshotNamespace(serverID: UUID(), userID: UUID()),
            generation: 2
        ))
        let currentFeed = makeAttentionFeed(healthyPrinterCount: 6)
        let currentAttempt = try XCTUnwrap(
            store.makeAttempt(session: currentSession, generation: 2)
        )
        currentAttempt.captureAttention(currentFeed)
        currentAttempt.publish()

        let gate = BackendConnectionGate()
        await gate.check(plan: stalePlan, generation: 1) { false }

        var consumedFeed: AttentionFeed?
        XCTAssertTrue(store.consumeAttention { consumedFeed = $0.value })
        XCTAssertEqual(consumedFeed, currentFeed)
        XCTAssertEqual(gate.state, .idle)
    }

    func testSlowAttentionPrefetchFallsBackToCheapProbeAndNormalTabLoad() async throws {
        let authority = FarmSnapshotFixtures.makeAuthority(
            tombstoneDefaults: UserDefaults(suiteName: trackedSuiteName("startup-prefetch-slow"))!
        )
        let session = try XCTUnwrap(try authority.mint(
            namespace: FarmSnapshotNamespace(serverID: UUID(), userID: UUID()),
            generation: 1
        ))
        let store = StartupPrefetchStore(authority: authority)
        let attempt = try XCTUnwrap(store.makeAttempt(session: session, generation: 1))
        let fullPageGate = AttentionResultGate<AttentionFeed>()
        let fullPageStarted = AsyncBarrier()
        defer { fullPageStarted.close() }
        let tabFeed = makeAttentionFeed(
            items: [makeAttentionItem(id: "tab-canonical")],
            healthyPrinterCount: 3
        )
        let service = MockAttentionService()
        service.getFeedHandler = { _, limit in
            if limit == nil {
                fullPageStarted.signal()
                return try await fullPageGate.wait()
            }
            return makeAttentionFeed(healthyPrinterCount: 1)
        }
        let probe = BackendReadinessProbe.attention(
            service: service,
            startupPrefetchAttempt: attempt,
            prefetchTimeoutSleep: { _ in
                await fullPageStarted.waitUntilArrived()
            }
        )
        let plan = BackendReadinessPlan(
            capabilitiesService: TestCapabilitiesService(),
            probes: [probe],
            startupPrefetchAttempt: attempt
        )
        let gate = BackendConnectionGate()

        await gate.check(plan: plan, generation: 1) { true }

        XCTAssertEqual(gate.state, .ready)
        let gateCalls = service.getFeedCalls
        XCTAssertEqual(gateCalls.count, 2)
        XCTAssertEqual(gateCalls.filter { $0.limit == nil }.count, 1)
        XCTAssertEqual(gateCalls.filter { $0.limit == 1 }.count, 1)
        XCTAssertFalse(store.consumeAttention { _ in XCTFail("cheap probe is not a canonical page") })

        service.getFeedHandler = { _, _ in tabFeed }
        let viewModel = AttentionFeedViewModel()
        let bootstrapped = await viewModel.bootstrap(
            attentionService: service,
            signalRService: MockSignalRService(),
            attentionEnabled: true,
            startupPrefetchStore: store
        )

        XCTAssertTrue(bootstrapped)
        let snapshot = try XCTUnwrap(viewModel.snapshot)
        XCTAssertEqual(snapshot.items, tabFeed.items)
        XCTAssertEqual(snapshot.nextCursor, tabFeed.nextCursor)
        XCTAssertEqual(snapshot.healthyPrinterCount, tabFeed.healthyPrinterCount)
        XCTAssertEqual(service.getFeedCallCount, 3)
    }

    func testFailedAttentionPrefetchCannotFailSuccessfulCheapProbe() async throws {
        let authority = FarmSnapshotFixtures.makeAuthority(
            tombstoneDefaults: UserDefaults(suiteName: trackedSuiteName("startup-prefetch-failed-warm"))!
        )
        let session = try XCTUnwrap(try authority.mint(
            namespace: FarmSnapshotNamespace(serverID: UUID(), userID: UUID()),
            generation: 1
        ))
        let store = StartupPrefetchStore(authority: authority)
        let attempt = try XCTUnwrap(store.makeAttempt(session: session, generation: 1))
        let service = MockAttentionService()
        service.getFeedHandler = { _, limit in
            guard limit == 1 else {
                throw ShiftTaskProofError.forced("canonical page failed")
            }
            return makeAttentionFeed(healthyPrinterCount: 1)
        }
        let plan = BackendReadinessPlan(
            capabilitiesService: TestCapabilitiesService(),
            probes: [
                BackendReadinessProbe.attention(
                    service: service,
                    startupPrefetchAttempt: attempt
                ),
            ],
            startupPrefetchAttempt: attempt
        )
        let gate = BackendConnectionGate()

        await gate.check(plan: plan, generation: 1) { true }

        XCTAssertEqual(gate.state, .ready)
        XCTAssertEqual(service.getFeedCallCount, 2)
        XCTAssertFalse(store.consumeAttention { _ in XCTFail("failed warming published prefetch") })
    }

    func testPreCancelledAttentionProbeStartsNoRequests() async {
        let service = MockAttentionService()
        let probe = BackendReadinessProbe.attention(
            service: service,
            startupPrefetchAttempt: nil
        )

        let wasCancelled = await Task { () -> Bool in
            withUnsafeCurrentTask { $0?.cancel() }
            do {
                try await probe.operation()
                return false
            } catch is CancellationError {
                return true
            } catch {
                return false
            }
        }.value

        XCTAssertTrue(wasCancelled)
        XCTAssertEqual(service.getFeedCallCount, 0)
    }

    func testAttentionGatingAndPrefetchRequestsAreConcurrent() async throws {
        let bothInFlight = AsyncBarrier()
        let suspendedTimeout = AsyncBarrier()
        defer {
            bothInFlight.close()
            suspendedTimeout.close()
        }
        let service = MockAttentionService()
        service.getFeedHandler = { _, _ in
            await bothInFlight.arriveAndWait()
            return makeAttentionFeed()
        }
        let probe = BackendReadinessProbe.attention(
            service: service,
            startupPrefetchAttempt: nil,
            prefetchTimeoutSleep: { _ in
                await suspendedTimeout.arriveAndWait()
            }
        )
        let probeTask = Task { try await probe.operation() }
        let bothParked = expectation(description: "both attention requests in flight")
        let observer = Task {
            await bothInFlight.waitUntilReleaseWaiterCount(2)
            bothParked.fulfill()
        }

        // Failure-path bound only: the passing path is causal and uses no
        // sleeps, yields, polling, or wall-clock ordering.
        await fulfillment(of: [bothParked], timeout: 5)
        bothInFlight.close()
        await observer.value
        try await probeTask.value
        XCTAssertEqual(service.getFeedCallCount, 2)
    }

    func testUnreachableAttentionStillFailsAfterBestEffortPrefetch() async throws {
        let authority = FarmSnapshotFixtures.makeAuthority(
            tombstoneDefaults: UserDefaults(suiteName: trackedSuiteName("startup-prefetch-unreachable"))!
        )
        let session = try XCTUnwrap(try authority.mint(
            namespace: FarmSnapshotNamespace(serverID: UUID(), userID: UUID()),
            generation: 1
        ))
        let store = StartupPrefetchStore(authority: authority)
        let attempt = try XCTUnwrap(store.makeAttempt(session: session, generation: 1))
        let service = MockAttentionService()
        service.getFeedHandler = { _, _ in
            throw ShiftTaskProofError.forced("unreachable")
        }
        let plan = BackendReadinessPlan(
            capabilitiesService: TestCapabilitiesService(),
            probes: [
                BackendReadinessProbe.attention(
                    service: service,
                    startupPrefetchAttempt: attempt
                ),
            ],
            startupPrefetchAttempt: attempt
        )
        let gate = BackendConnectionGate()

        await gate.check(plan: plan, generation: 1) { true }

        XCTAssertEqual(gate.failures?.map(\.endpoint), [.attention])
        XCTAssertFalse(gate.allowsMainContent)
        let calls = service.getFeedCalls
        XCTAssertEqual(calls.count, 2)
        XCTAssertEqual(calls.filter { $0.limit == nil }.count, 1)
        XCTAssertEqual(calls.filter { $0.limit == 1 }.count, 1)
        XCTAssertFalse(store.consumeAttention { _ in XCTFail("failed probe published prefetch") })
    }

    func testPrefetchRejectsGenerationUserAndServerAuthorityChanges() throws {
        let authority = FarmSnapshotFixtures.makeAuthority(
            tombstoneDefaults: UserDefaults(suiteName: trackedSuiteName("startup-prefetch-fences"))!
        )
        let store = StartupPrefetchStore(authority: authority)
        let serverA = UUID()
        let userA = UUID()
        var session = try XCTUnwrap(try authority.mint(
            namespace: FarmSnapshotNamespace(serverID: serverA, userID: userA),
            generation: 1
        ))

        func publish(_ current: FarmSnapshotSession) throws {
            let attempt = try XCTUnwrap(
                store.makeAttempt(session: current, generation: current.generation)
            )
            attempt.captureAttention(makeAttentionFeed(healthyPrinterCount: current.generation))
            attempt.publish()
        }

        try publish(session)
        session = try XCTUnwrap(try authority.mint(
            namespace: session.namespace,
            generation: 2
        ))
        XCTAssertFalse(store.consumeAttention { _ in XCTFail("old generation leaked") })

        try publish(session)
        session = try XCTUnwrap(try authority.mint(
            namespace: FarmSnapshotNamespace(serverID: serverA, userID: UUID()),
            generation: 3
        ))
        XCTAssertFalse(store.consumeAttention { _ in XCTFail("old user leaked") })

        try publish(session)
        session = try XCTUnwrap(try authority.mint(
            namespace: FarmSnapshotNamespace(serverID: UUID(), userID: UUID()),
            generation: 4
        ))
        XCTAssertFalse(store.consumeAttention { _ in XCTFail("old server leaked") })

        try publish(session)
        try authority.tombstone(session.serverID)
        XCTAssertFalse(store.consumeAttention { _ in XCTFail("tombstoned server leaked") })

        let logoutAuthority = FarmSnapshotFixtures.makeAuthority(
            tombstoneDefaults: UserDefaults(suiteName: trackedSuiteName("startup-prefetch-logout"))!
        )
        let logoutStore = StartupPrefetchStore(authority: logoutAuthority)
        let logoutSession = try XCTUnwrap(try logoutAuthority.mint(
            namespace: FarmSnapshotNamespace(serverID: UUID(), userID: UUID()),
            generation: 1
        ))
        let logoutAttempt = try XCTUnwrap(
            logoutStore.makeAttempt(session: logoutSession, generation: 1)
        )
        logoutAttempt.captureAttention(makeAttentionFeed(healthyPrinterCount: 1))
        logoutAttempt.publish()
        logoutAuthority.revoke()
        XCTAssertFalse(logoutStore.consumeAttention { _ in XCTFail("logout leaked") })
    }

    func testFailedReadinessAndContinueOfflineDiscardPrefetch() async throws {
        let authority = FarmSnapshotFixtures.makeAuthority(
            tombstoneDefaults: UserDefaults(suiteName: trackedSuiteName("startup-prefetch-failed"))!
        )
        let session = try XCTUnwrap(try authority.mint(
            namespace: FarmSnapshotNamespace(serverID: UUID(), userID: UUID()),
            generation: 1
        ))
        let store = StartupPrefetchStore(authority: authority)
        let attempt = try XCTUnwrap(store.makeAttempt(session: session, generation: 1))
        let plan = BackendReadinessPlan(
            capabilitiesService: TestCapabilitiesService(),
            probes: [
                BackendReadinessProbe(endpoint: .attention) {
                    attempt.captureAttention(makeAttentionFeed(healthyPrinterCount: 9))
                },
                BackendReadinessProbe(endpoint: .jobs) {
                    throw ReadinessTestError.failed
                },
            ],
            startupPrefetchAttempt: attempt
        )
        let gate = BackendConnectionGate()

        await gate.check(plan: plan, generation: 1) { true }
        XCTAssertEqual(gate.failures?.map(\.endpoint), [.jobs])
        XCTAssertFalse(store.consumeAttention { _ in XCTFail("failed gate published") })

        gate.continueOffline()
        XCTAssertEqual(gate.state, .proceedingOffline)
        XCTAssertFalse(store.consumeAttention { _ in XCTFail("offline continuation published") })
    }

    func testCancelledReadinessDiscardsPrefetchAndLateCapture() async throws {
        let started = AsyncBarrier()
        let release = AsyncBarrier()
        defer {
            started.close()
            release.close()
        }
        let authority = FarmSnapshotFixtures.makeAuthority(
            tombstoneDefaults: UserDefaults(suiteName: trackedSuiteName("startup-prefetch-cancel"))!
        )
        let session = try XCTUnwrap(try authority.mint(
            namespace: FarmSnapshotNamespace(serverID: UUID(), userID: UUID()),
            generation: 1
        ))
        let store = StartupPrefetchStore(authority: authority)
        let attempt = try XCTUnwrap(store.makeAttempt(session: session, generation: 1))
        let plan = BackendReadinessPlan(
            capabilitiesService: TestCapabilitiesService(),
            probes: [
                BackendReadinessProbe(endpoint: .attention) {
                    started.signal()
                    await release.arriveAndWait()
                    attempt.captureAttention(makeAttentionFeed(healthyPrinterCount: 8))
                },
            ],
            startupPrefetchAttempt: attempt
        )
        let gate = BackendConnectionGate()
        let check = Task {
            await gate.check(plan: plan, generation: 1) { true }
        }

        await started.waitUntilArrived()
        check.cancel()
        release.release()
        await check.value
        attempt.captureAttention(makeAttentionFeed(healthyPrinterCount: 10))
        attempt.publish()

        XCTAssertEqual(gate.state, .idle)
        XCTAssertFalse(store.consumeAttention { _ in XCTFail("cancelled gate published") })
    }

    /// Covers guard 1 of `BackendConnectionGate.check` — the attempt/generation
    /// fence. `testConnectionGateResetInvalidatesInFlightAttempt` already asserts
    /// the state transition, but it builds a plan with no attempt attached, so
    /// `plan.startupPrefetchAttempt?.discard()` is a no-op there and that test
    /// would still pass if the superseded branch published instead of discarding.
    /// Staging a real attempt is what makes the disposal observable.
    func testSupersededGateDiscardsStagedPrefetch() async throws {
        let started = AsyncBarrier()
        let release = AsyncBarrier()
        defer {
            started.close()
            release.close()
        }
        let authority = FarmSnapshotFixtures.makeAuthority(
            tombstoneDefaults: UserDefaults(suiteName: trackedSuiteName("startup-prefetch-superseded"))!
        )
        let session = try XCTUnwrap(try authority.mint(
            namespace: FarmSnapshotNamespace(serverID: UUID(), userID: UUID()),
            generation: 1
        ))
        let store = StartupPrefetchStore(authority: authority)
        let attempt = try XCTUnwrap(store.makeAttempt(session: session, generation: 1))
        let plan = BackendReadinessPlan(
            capabilitiesService: TestCapabilitiesService(),
            probes: [
                BackendReadinessProbe(endpoint: .attention) {
                    attempt.captureAttention(makeAttentionFeed(healthyPrinterCount: 7))
                    started.signal()
                    await release.arriveAndWait()
                },
            ],
            startupPrefetchAttempt: attempt
        )
        let gate = BackendConnectionGate()
        let check = Task {
            await gate.check(plan: plan, generation: 1) { true }
        }

        await started.waitUntilArrived()
        gate.reset()
        release.release()
        await check.value

        XCTAssertEqual(gate.state, .idle)
        XCTAssertFalse(gate.allowsMainContent)
        XCTAssertFalse(store.consumeAttention { _ in XCTFail("superseded gate published") })

        // The attempt must also be sealed, so a probe that completes after
        // supersession cannot publish the data it staged too late.
        attempt.captureAttention(makeAttentionFeed(healthyPrinterCount: 11))
        attempt.publish()
        XCTAssertFalse(store.consumeAttention { _ in XCTFail("sealed attempt published after supersession") })
    }

    /// Covers guard 2 of `BackendConnectionGate.check` — the `isCurrent` fence.
    /// `testConnectionGateDiscardsSupersededGenerationResult` asserts the state
    /// transition for this branch but likewise attaches no attempt, leaving the
    /// prefetch disposal itself unobserved.
    func testNonCurrentGateDiscardsStagedPrefetch() async throws {
        let started = AsyncBarrier()
        let release = AsyncBarrier()
        defer {
            started.close()
            release.close()
        }
        let authority = FarmSnapshotFixtures.makeAuthority(
            tombstoneDefaults: UserDefaults(suiteName: trackedSuiteName("startup-prefetch-not-current"))!
        )
        let session = try XCTUnwrap(try authority.mint(
            namespace: FarmSnapshotNamespace(serverID: UUID(), userID: UUID()),
            generation: 1
        ))
        let store = StartupPrefetchStore(authority: authority)
        let attempt = try XCTUnwrap(store.makeAttempt(session: session, generation: 1))
        let plan = BackendReadinessPlan(
            capabilitiesService: TestCapabilitiesService(),
            probes: [
                BackendReadinessProbe(endpoint: .attention) {
                    attempt.captureAttention(makeAttentionFeed(healthyPrinterCount: 6))
                    started.signal()
                    await release.arriveAndWait()
                },
            ],
            startupPrefetchAttempt: attempt
        )
        let gate = BackendConnectionGate()
        var isCurrent = true
        let check = Task {
            await gate.check(plan: plan, generation: 1) { isCurrent }
        }

        await started.waitUntilArrived()
        isCurrent = false
        release.release()
        await check.value

        XCTAssertEqual(gate.state, .idle)
        XCTAssertFalse(gate.allowsMainContent)
        XCTAssertFalse(store.consumeAttention { _ in XCTFail("non-current gate published") })
    }

    func testReadinessSkipsCapabilityDisabledProbes() async {
        var resolved = ResolvedSystemCapabilities.defaults
        resolved.attentionEnabled = false
        resolved.filamentCoverageEnabled = false
        resolved.shiftPlanEnabled = false
        resolved.printedPartsInventoryEnabled = false
        let recorder = BackendProbeRecorder()
        let capabilities = TestCapabilitiesService(resolved: resolved)
        let plan = BackendReadinessPlan(
            capabilitiesService: capabilities,
            probes: [
                BackendReadinessProbe(endpoint: .printers) {
                    await recorder.record(.printers)
                },
                BackendReadinessProbe(
                    endpoint: .attention,
                    isEnabled: { $0.attentionEnabled }
                ) {
                    await recorder.record(.attention)
                },
                BackendReadinessProbe(
                    endpoint: .filamentCoverage,
                    isEnabled: { $0.filamentCoverageEnabled }
                ) {
                    await recorder.record(.filamentCoverage)
                },
                BackendReadinessProbe(
                    endpoint: .shiftTasks,
                    isEnabled: { $0.shiftPlanEnabled }
                ) {
                    await recorder.record(.shiftTasks)
                },
                BackendReadinessProbe(
                    endpoint: .partsInventory,
                    isEnabled: { $0.printedPartsInventoryEnabled }
                ) {
                    await recorder.record(.partsInventory)
                },
            ]
        )

        let result = await BackendReadinessChecker().check(plan: plan)

        XCTAssertTrue(result.failures.isEmpty)
        let recorded = await recorder.snapshot()
        XCTAssertEqual(recorded, [.printers])
    }

    func testReadinessAggregatesFailuresInStableDisplayOrder() async {
        let capabilities = TestCapabilitiesService()
        let plan = BackendReadinessPlan(
            capabilitiesService: capabilities,
            probes: [
                BackendReadinessProbe(endpoint: .api) {},
                BackendReadinessProbe(endpoint: .dispatch) {
                    throw ReadinessTestError.failed
                },
                BackendReadinessProbe(endpoint: .jobs) {
                    throw ReadinessTestError.failed
                },
            ]
        )

        let result = await BackendReadinessChecker().check(plan: plan)

        XCTAssertEqual(result.failures.map(\.endpoint), [.jobs, .dispatch])
    }

    func testReadinessShortCircuitsWhenAPIIsUnavailable() async {
        let recorder = BackendProbeRecorder()
        let capabilities = TestCapabilitiesService()
        let plan = BackendReadinessPlan(
            capabilitiesService: capabilities,
            probes: [
                BackendReadinessProbe(endpoint: .api) {
                    throw ReadinessTestError.failed
                },
                BackendReadinessProbe(endpoint: .jobs) {
                    await recorder.record(.jobs)
                },
            ]
        )

        let result = await BackendReadinessChecker().check(plan: plan)

        XCTAssertEqual(result.failures.map(\.endpoint), [.api])
        XCTAssertEqual(capabilities.refreshCount, 0)
        let recorded = await recorder.snapshot()
        XCTAssertTrue(recorded.isEmpty)
    }

    func testReadinessTreatsUnsupportedEndpointAsAvailable() async {
        let capabilities = TestCapabilitiesService()
        let plan = BackendReadinessPlan(
            capabilitiesService: capabilities,
            probes: [
                BackendReadinessProbe(endpoint: .api) {},
                BackendReadinessProbe(
                    endpoint: .attention,
                    treatsUnsupportedAsAvailable: true
                ) {
                    throw NetworkError.notFound
                },
                BackendReadinessProbe(
                    endpoint: .dispatch,
                    treatsUnsupportedAsAvailable: true
                ) {
                    throw NetworkError.methodNotAllowed
                },
            ]
        )

        let result = await BackendReadinessChecker().check(plan: plan)

        XCTAssertTrue(result.failures.isEmpty)
    }

    func testReadinessReportsUnsupportedEndpointByDefault() async {
        let capabilities = TestCapabilitiesService()
        let plan = BackendReadinessPlan(
            capabilitiesService: capabilities,
            probes: [
                BackendReadinessProbe(endpoint: .printers) {
                    throw NetworkError.notFound
                },
            ]
        )

        let result = await BackendReadinessChecker().check(plan: plan)

        XCTAssertEqual(result.failures.map(\.endpoint), [.printers])
    }

    func testReadinessReportsCapabilitiesRefreshFailureButStillChecksFeatures() async {
        let recorder = BackendProbeRecorder()
        let capabilities = TestCapabilitiesService(outcome: .failed)
        let plan = BackendReadinessPlan(
            capabilitiesService: capabilities,
            probes: [
                BackendReadinessProbe(endpoint: .printers) {
                    await recorder.record(.printers)
                },
            ]
        )

        let result = await BackendReadinessChecker().check(plan: plan)

        XCTAssertEqual(result.failures.map(\.endpoint), [.systemCapabilities])
        XCTAssertEqual(result.failures.first?.kind, .transport)
        let recorded = await recorder.snapshot()
        XCTAssertEqual(recorded, [.printers])
    }

    func testReadinessAcceptsLegacyCapabilitiesDefaults() async {
        let capabilities = TestCapabilitiesService(outcome: .legacyDefaults)
        let plan = BackendReadinessPlan(
            capabilitiesService: capabilities,
            probes: [BackendReadinessProbe(endpoint: .api) {}]
        )

        let result = await BackendReadinessChecker().check(plan: plan)

        XCTAssertTrue(result.failures.isEmpty)
    }

    func testReadinessTimesOutAnUnresponsiveProbe() async {
        let operationStarted = AsyncBarrier()
        let timeoutGate = AsyncBarrier()
        let diagnostics = BackendReadinessDiagnosticRecorder()
        defer {
            operationStarted.close()
            timeoutGate.close()
        }
        let capabilities = TestCapabilitiesService()
        let plan = BackendReadinessPlan(
            capabilitiesService: capabilities,
            probes: [
                BackendReadinessProbe(endpoint: .signalR) {
                    operationStarted.signal()
                    try await Task.sleep(for: .seconds(30))
                },
            ]
        )
        let checker = BackendReadinessChecker(
            timeout: .seconds(30),
            probeTimeoutSleep: { _ in
                await timeoutGate.arriveAndWait()
            },
            diagnosticRecorder: diagnostics.record
        )
        let checkTask = Task {
            await checker.check(plan: plan)
        }

        await operationStarted.waitUntilArrived()
        timeoutGate.release()
        let result = await checkTask.value

        XCTAssertEqual(result.failures.map(\.endpoint), [.signalR])
        XCTAssertEqual(result.failures.first?.kind, .timeout)
        let diagnostic = diagnostics.snapshot().first { $0.endpoint == .signalR }
        XCTAssertEqual(diagnostic?.outcome, .failed)
        XCTAssertEqual(diagnostic?.failureKind, .timeout)
        XCTAssertEqual(diagnostic?.detail, "readiness timeout budget 30.0 seconds")
    }

    func testReadinessTimeoutReturnsWhenProbeIgnoresCancellation() async {
        let operationStarted = AsyncBarrier()
        let blocker = AsyncBarrier()
        let timeoutGate = AsyncBarrier()
        defer {
            operationStarted.close()
            blocker.close()
            timeoutGate.close()
        }
        let capabilities = TestCapabilitiesService()
        let plan = BackendReadinessPlan(
            capabilitiesService: capabilities,
            probes: [
                BackendReadinessProbe(endpoint: .maintenance) {
                    operationStarted.signal()
                    await blocker.arriveAndWait()
                },
            ]
        )
        let checker = BackendReadinessChecker(
            timeout: .seconds(30),
            probeTimeoutSleep: { _ in
                await timeoutGate.arriveAndWait()
            }
        )
        let checkTask = Task {
            await checker.check(plan: plan)
        }

        await operationStarted.waitUntilArrived()
        timeoutGate.release()
        let result = await checkTask.value

        XCTAssertEqual(result.failures.map(\.endpoint), [.maintenance])
        blocker.release()
    }

    func testReadinessCancellationDoesNotBecomeAServiceFailure() async {
        let started = AsyncBarrier()
        defer { started.close() }
        let capabilities = TestCapabilitiesService()
        let plan = BackendReadinessPlan(
            capabilitiesService: capabilities,
            probes: [
                BackendReadinessProbe(endpoint: .api) {
                    started.signal()
                    try await Task.sleep(for: .seconds(30))
                },
            ]
        )

        let task = Task {
            await BackendReadinessChecker().check(plan: plan)
        }
        await started.waitUntilArrived()
        task.cancel()
        let result = await task.value

        XCTAssertTrue(result.wasCancelled)
        XCTAssertTrue(result.failures.isEmpty)
    }

    func testConnectionGateDiscardsSupersededGenerationResult() async {
        let started = AsyncBarrier()
        let release = AsyncBarrier()
        defer {
            started.close()
            release.close()
        }
        let capabilities = TestCapabilitiesService()
        let plan = BackendReadinessPlan(
            capabilitiesService: capabilities,
            probes: [
                BackendReadinessProbe(endpoint: .api) {
                    started.signal()
                    await release.arriveAndWait()
                },
            ]
        )
        let gate = BackendConnectionGate()
        var isCurrent = true

        let task = Task {
            await gate.check(plan: plan, generation: 7) {
                isCurrent
            }
        }
        await started.waitUntilArrived()
        isCurrent = false
        release.release()
        await task.value

        XCTAssertEqual(gate.state, .idle)
        XCTAssertFalse(gate.allowsMainContent)
    }

    func testConnectionGateResetInvalidatesInFlightAttempt() async {
        let started = AsyncBarrier()
        let release = AsyncBarrier()
        defer {
            started.close()
            release.close()
        }
        let capabilities = TestCapabilitiesService()
        let plan = BackendReadinessPlan(
            capabilitiesService: capabilities,
            probes: [
                BackendReadinessProbe(endpoint: .api) {
                    started.signal()
                    await release.arriveAndWait()
                },
            ]
        )
        let gate = BackendConnectionGate()

        let task = Task {
            await gate.check(plan: plan, generation: 1) { true }
        }
        await started.waitUntilArrived()
        gate.reset()
        release.release()
        await task.value

        XCTAssertEqual(gate.state, .idle)
        XCTAssertFalse(gate.allowsMainContent)
    }

    func testConnectionGateAcknowledgesFailureOnceAndAllowsOfflineUI() async {
        let capabilities = TestCapabilitiesService()
        let plan = BackendReadinessPlan(
            capabilitiesService: capabilities,
            probes: [
                BackendReadinessProbe(endpoint: .attention) {
                    throw ReadinessTestError.failed
                },
            ]
        )
        let gate = BackendConnectionGate()

        await gate.check(plan: plan, generation: 2) { true }
        XCTAssertEqual(gate.failures?.map(\.endpoint), [.attention])
        XCTAssertFalse(gate.allowsMainContent)
        XCTAssertTrue(gate.failureMessage?.contains("Attention") == true)
        XCTAssertTrue(gate.failureMessage?.contains("Check the network and server") == true)
        XCTAssertEqual(gate.failureTitle, "Some services are unavailable")

        gate.continueOffline()
        gate.continueOffline()

        XCTAssertEqual(gate.state, .proceedingOffline)
        XCTAssertTrue(gate.allowsMainContent)
        XCTAssertNil(gate.failures)
    }

    func testConnectionGateRetryRearmsTheTaskIdentity() async {
        let gate = BackendConnectionGate()
        let initialRevision = gate.retryRevision

        gate.retry()

        XCTAssertEqual(gate.state, .idle)
        XCTAssertEqual(gate.retryRevision, initialRevision + 1)
        XCTAssertTrue(gate.isChecking)
    }

    func testConnectionGateCallsTimeoutsRespondingSlowly() async {
        let operationStarted = AsyncBarrier()
        let timeoutGate = AsyncBarrier()
        defer {
            operationStarted.close()
            timeoutGate.close()
        }
        let plan = BackendReadinessPlan(
            capabilitiesService: TestCapabilitiesService(),
            probes: [
                BackendReadinessProbe(endpoint: .attention) {
                    operationStarted.signal()
                    try await Task.sleep(for: .seconds(30))
                },
            ]
        )
        let gate = BackendConnectionGate(timeout: .milliseconds(1))

        await gate.check(plan: plan, generation: 3) { true }

        XCTAssertEqual(gate.failures?.first?.kind, .timeout)
        XCTAssertEqual(gate.failureTitle, "Some services are responding slowly")
        XCTAssertTrue(gate.failureMessage?.contains("Responding slowly") == true)
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

@MainActor
private final class TestCapabilitiesService: SystemCapabilitiesServiceProtocol, @unchecked Sendable {
    private(set) var resolved: ResolvedSystemCapabilities
    private(set) var refreshCount = 0
    private let outcome: SystemCapabilitiesRefreshOutcome

    init(
        resolved: ResolvedSystemCapabilities = .defaults,
        outcome: SystemCapabilitiesRefreshOutcome = .loaded
    ) {
        self.resolved = resolved
        self.outcome = outcome
    }

    @discardableResult
    func refresh() async -> SystemCapabilitiesRefreshOutcome {
        refreshCount += 1
        return outcome
    }
}

private actor BackendProbeRecorder {
    private var endpoints: [BackendServiceEndpoint] = []

    func record(_ endpoint: BackendServiceEndpoint) {
        endpoints.append(endpoint)
    }

    func snapshot() -> [BackendServiceEndpoint] {
        endpoints
    }
}

private final class BackendReadinessDiagnosticRecorder: @unchecked Sendable {
    private let lock = NSLock()
    private var diagnostics: [BackendReadinessProbeDiagnostic] = []

    func record(_ diagnostic: BackendReadinessProbeDiagnostic) {
        lock.withLock {
            diagnostics.append(diagnostic)
        }
    }

    func snapshot() -> [BackendReadinessProbeDiagnostic] {
        lock.withLock { diagnostics }
    }
}

private enum ReadinessTestError: Error {
    case failed
}
