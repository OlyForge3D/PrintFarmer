import XCTest
@testable import PrintFarmer

/// Tests for the operator feature-gate capability contract (issue #725)
/// consumed by F1's Attention shell (issue #706).
///
/// Verifies:
/// * `SystemCapabilities` decodes the camelCase payload from
///   `/api/system/capabilities`.
/// * Missing/omitted flags fall back to the documented defaults so an
///   older PrintFarmer server (predating #725) is treated as fully
///   enabled.
/// * `SystemCapabilitiesService.refresh()` never throws:
///   * A 200 with the documented flags updates `resolved`.
///   * A 404 (endpoint absent) leaves `resolved` at defaults.
///   * A transient network failure leaves the previous snapshot in place
///     rather than disabling features (fail-open).
/// * `StubSystemCapabilitiesService` honors caller-supplied snapshots so
///   downstream view tests can exercise the disabled-fallback path.
@MainActor
final class SystemCapabilitiesTests: XCTestCase {

    nonisolated(unsafe) private var mockAPIClient: MockAPIClient!
    private var apiClient: APIClient!

    override func setUp() async throws {
        try await super.setUp()
        mockAPIClient = MockAPIClient()
        apiClient = mockAPIClient.apiClient
    }

    override func tearDown() async throws {
        apiClient = nil
        mockAPIClient = nil
        try await super.tearDown()
    }

    // MARK: - Defaults

    func testDefaultsMatchDocumentedContract() {
        // #725 acceptance: attention/filament-coverage/guided-swap/
        // multi-slot-fallback/shift-plan/offline-replay default true.
        // Native push defaults false until a relay is configured, and
        // printed-parts inventory defaults false until part SKUs and
        // output mappings exist (#1000).
        let defaults = ResolvedSystemCapabilities.defaults
        XCTAssertTrue(defaults.attentionEnabled)
        XCTAssertFalse(defaults.nativePushEnabled)
        XCTAssertTrue(defaults.filamentCoverageEnabled)
        XCTAssertTrue(defaults.guidedSwapEnabled)
        XCTAssertTrue(defaults.multiSlotFallbackEnabled)
        XCTAssertTrue(defaults.shiftPlanEnabled)
        XCTAssertFalse(defaults.printedPartsInventoryEnabled)
        XCTAssertTrue(defaults.offlineWriteReplayEnabled)
    }

    // MARK: - Decoding

    func testDecodesCanonicalNestedCamelCasePayload() throws {
        let json = """
        {
            "operatorFeatures": {
                "attentionEnabled": false,
                "nativePushEnabled": true,
                "filamentCoverageEnabled": false,
                "guidedSwapEnabled": false,
                "multiSlotFallbackEnabled": false,
                "shiftPlanEnabled": false,
                "printedPartsInventoryEnabled": false,
                "offlineWriteReplayEnabled": false
            }
        }
        """.data(using: .utf8)!

        let decoded = try JSONDecoder().decode(SystemCapabilities.self, from: json)
        let resolved = decoded.resolved

        XCTAssertFalse(resolved.attentionEnabled)
        XCTAssertTrue(resolved.nativePushEnabled)
        XCTAssertFalse(resolved.filamentCoverageEnabled)
        XCTAssertFalse(resolved.guidedSwapEnabled)
        XCTAssertFalse(resolved.multiSlotFallbackEnabled)
        XCTAssertFalse(resolved.shiftPlanEnabled)
        XCTAssertFalse(resolved.printedPartsInventoryEnabled)
        XCTAssertFalse(resolved.offlineWriteReplayEnabled)
    }

    func testMissingFlagsResolveToDocumentedDefaults() throws {
        // Simulate an older server that predates #725 and only exposes
        // an unrelated field. All flags must resolve to the defaults.
        let json = """
        { "unrelatedFuture": true }
        """.data(using: .utf8)!

        let decoded = try JSONDecoder().decode(SystemCapabilities.self, from: json)
        let resolved = decoded.resolved

        XCTAssertEqual(resolved, ResolvedSystemCapabilities.defaults)
    }

    func testPartiallyPopulatedResponseOnlyOverridesProvidedFlags() throws {
        // A server that ships #725 but does not yet expose the printed
        // parts flag must still resolve that flag to its documented
        // default of `false` (#1000).
        let json = """
        {
            "operatorFeatures": {
                "attentionEnabled": false,
                "nativePushEnabled": true
            }
        }
        """.data(using: .utf8)!

        let decoded = try JSONDecoder().decode(SystemCapabilities.self, from: json)
        let resolved = decoded.resolved

        XCTAssertFalse(resolved.attentionEnabled)
        XCTAssertTrue(resolved.nativePushEnabled)
        XCTAssertTrue(resolved.filamentCoverageEnabled)
        XCTAssertTrue(resolved.guidedSwapEnabled)
        XCTAssertTrue(resolved.multiSlotFallbackEnabled)
        XCTAssertTrue(resolved.shiftPlanEnabled)
        XCTAssertFalse(resolved.printedPartsInventoryEnabled)
        XCTAssertTrue(resolved.offlineWriteReplayEnabled)
    }

    func testLegacyTopLevelFlagsRemainSupported() throws {
        let json = """
        {
            "attentionEnabled": false,
            "nativePushEnabled": true,
            "shiftPlanEnabled": false
        }
        """.data(using: .utf8)!

        let resolved = try JSONDecoder()
            .decode(SystemCapabilities.self, from: json)
            .resolved

        XCTAssertFalse(resolved.attentionEnabled)
        XCTAssertTrue(resolved.nativePushEnabled)
        XCTAssertFalse(resolved.shiftPlanEnabled)
        XCTAssertFalse(resolved.printedPartsInventoryEnabled)
    }

    func testCanonicalNestedFlagsTakePrecedenceAndLegacyFillsNestedOmissions() throws {
        let json = """
        {
            "attentionEnabled": true,
            "filamentCoverageEnabled": true,
            "shiftPlanEnabled": false,
            "operatorFeatures": {
                "attentionEnabled": false,
                "filamentCoverageEnabled": false
            }
        }
        """.data(using: .utf8)!

        let resolved = try JSONDecoder()
            .decode(SystemCapabilities.self, from: json)
            .resolved

        XCTAssertFalse(resolved.attentionEnabled)
        XCTAssertFalse(resolved.filamentCoverageEnabled)
        XCTAssertFalse(resolved.shiftPlanEnabled)
        XCTAssertFalse(resolved.printedPartsInventoryEnabled)
    }

    // MARK: - APIError.code (ProblemDetails extension)

    func testAPIErrorDecodesFeatureDisabledExtension() throws {
        // #725: a disabled HTTP feature (e.g. /api/attention when
        // attentionEnabled=false) returns 404 with a ProblemDetails body
        // carrying `code: "featureDisabled"`. iOS must decode the
        // extension so downstream features (F2 etc.) can render a
        // sticky fallback rather than treating it as a missing resource.
        let json = """
        {
            "title": "Feature disabled",
            "status": 404,
            "detail": "The attention feed is disabled on this server.",
            "code": "featureDisabled"
        }
        """.data(using: .utf8)!

        let apiError = try JSONDecoder().decode(APIError.self, from: json)
        XCTAssertEqual(apiError.code, "featureDisabled")
        XCTAssertEqual(apiError.status, 404)
    }

    func testAPIErrorCodeIsOptionalForLegacyResponses() throws {
        // Older ProblemDetails responses that pre-date #725 must still
        // decode; the `code` extension is optional.
        let json = """
        {
            "title": "Not found",
            "status": 404
        }
        """.data(using: .utf8)!

        let apiError = try JSONDecoder().decode(APIError.self, from: json)
        XCTAssertNil(apiError.code)
    }

    // MARK: - SystemCapabilitiesService

    func testRefreshUpdatesResolvedOnSuccessfulResponse() async {
        mockAPIClient.stubResponse(json: """
        {
            "operatorFeatures": {
                "attentionEnabled": false,
                "nativePushEnabled": true
            }
        }
        """)

        let service = SystemCapabilitiesService(apiClient: apiClient)
        let outcome = await service.refresh()

        XCTAssertEqual(outcome, .loaded)
        XCTAssertFalse(service.resolved.attentionEnabled,
                       "attentionEnabled should be honored from the response")
        XCTAssertTrue(service.resolved.nativePushEnabled,
                      "nativePushEnabled should be honored from the response")
        XCTAssertTrue(service.resolved.filamentCoverageEnabled,
                      "Omitted flags must fall back to defaults")
    }

    func testRefreshKeepsDefaultsWhenEndpointReturns404() async {
        // Simulates a server that predates #725 and has no
        // `/api/system/capabilities` route. Must NOT throw and must
        // leave resolved at defaults (fully-enabled snapshot).
        mockAPIClient.requestHandler = { request in
            let response = TestData.httpResponse(url: request.url, statusCode: 404)
            return (response, Data("{}".utf8))
        }

        let service = SystemCapabilitiesService(apiClient: apiClient)
        let outcome = await service.refresh()

        XCTAssertEqual(outcome, .legacyDefaults)
        XCTAssertEqual(service.resolved, ResolvedSystemCapabilities.defaults)
    }

    func testPreparedRefreshIsConsumedByReadinessWithoutSecondRequest() async {
        mockAPIClient.stubResponse(json: """
        {
            "operatorFeatures": {
                "attentionEnabled": false
            }
        }
        """)
        let service = SystemCapabilitiesService(apiClient: apiClient)

        let prepared = await service.prepareForReadiness()
        let consumed = await service.refreshForReadiness()

        XCTAssertEqual(prepared, .loaded)
        XCTAssertEqual(consumed, .loaded)
        XCTAssertEqual(mockAPIClient.capturedRequests.count, 1)

        _ = await service.refreshForReadiness()
        XCTAssertEqual(mockAPIClient.capturedRequests.count, 2)
    }

    func testDiscardedPreparedRefreshForcesReadinessRequest() async {
        mockAPIClient.stubResponse(json: """
        {
            "operatorFeatures": {
                "attentionEnabled": true
            }
        }
        """)
        let service = SystemCapabilitiesService(apiClient: apiClient)

        _ = await service.prepareForReadiness()
        service.discardPreparedReadiness()
        _ = await service.refreshForReadiness()

        XCTAssertEqual(mockAPIClient.capturedRequests.count, 2)
    }

    func testAuthenticatedStartupResetDiscardsPreparedCapabilities() async {
        mockAPIClient.stubResponse(json: """
        {
            "operatorFeatures": {
                "attentionEnabled": true
            }
        }
        """)
        let service = SystemCapabilitiesService(apiClient: apiClient)
        let container = ServiceContainer(
            observeRegistry: false,
            synchronizeOfflineQueueOnStartup: false
        )
        container.capabilitiesService = service

        _ = await service.prepareForReadiness()
        container.resetAuthenticatedStartupState()
        _ = await service.refreshForReadiness()

        XCTAssertEqual(mockAPIClient.capturedRequests.count, 2)
    }

    func testConcurrentReadinessPreparationsShareOneRequest() async {
        let request = AsyncBarrier()
        addTeardownBlock { request.close() }
        let joined = expectation(description: "second preparation joined in-flight request")
        mockAPIClient.asyncRequestHandler = { urlRequest in
            await request.arriveAndWait()
            return (
                TestData.httpResponse(url: urlRequest.url, statusCode: 200),
                Data(#"{"operatorFeatures":{"attentionEnabled":true}}"#.utf8)
            )
        }
        let service = SystemCapabilitiesService(
            apiClient: apiClient,
            readinessPreparationJoinHook: {
                joined.fulfill()
            }
        )

        async let first = service.prepareForReadiness()
        await request.waitUntilArrived()
        async let second = service.prepareForReadiness()
        await fulfillment(of: [joined], timeout: 2)
        request.release()
        _ = await (first, second)

        XCTAssertEqual(mockAPIClient.capturedRequests.count, 1)
    }

    func testReadinessPreparationTimeoutDoesNotWaitForUncooperativeRequestAndRetriesFresh() async {
        let firstRequest = AsyncBarrier()
        let secondRequest = AsyncBarrier()
        let firstTimeout = AsyncBarrier()
        let secondTimeout = AsyncBarrier()
        let requestOrdinal = SystemCapabilitiesRequestOrdinal()
        let timeoutOrdinal = SystemCapabilitiesRequestOrdinal()
        addTeardownBlock {
            firstRequest.close()
            secondRequest.close()
            firstTimeout.close()
            secondTimeout.close()
        }
        mockAPIClient.asyncRequestHandler = { urlRequest in
            let ordinal = await requestOrdinal.next()
            if ordinal == 1 {
                await firstRequest.arriveAndWait()
            } else {
                await secondRequest.arriveAndWait()
            }
            return (
                TestData.httpResponse(url: urlRequest.url, statusCode: 200),
                Data(#"{"operatorFeatures":{"attentionEnabled":false}}"#.utf8)
            )
        }
        let service = SystemCapabilitiesService(
            apiClient: apiClient,
            preparationTimeout: .seconds(1),
            preparationTimeoutSleep: { _ in
                if await timeoutOrdinal.next() == 1 {
                    await firstTimeout.arriveAndWait()
                } else {
                    await secondTimeout.arriveAndWait()
                }
            }
        )

        let preparation = Task {
            await service.prepareForReadiness()
        }
        await firstRequest.waitUntilArrived()
        await firstTimeout.waitUntilArrived()
        firstTimeout.release()

        let outcome = await preparation.value
        XCTAssertEqual(outcome, .failed)
        XCTAssertEqual(mockAPIClient.capturedRequests.count, 1)

        let retry = Task {
            await service.prepareForReadiness()
        }
        await secondRequest.waitUntilArrived()
        await secondTimeout.waitUntilArrived()
        secondRequest.release()

        let retryOutcome = await retry.value
        XCTAssertEqual(retryOutcome, .loaded)
        XCTAssertEqual(mockAPIClient.capturedRequests.count, 2)
        XCTAssertFalse(service.resolved.attentionEnabled)
        firstRequest.release()
    }

    func testRefreshFailsOpenOnTransportError() async {
        // Transient network failure must not disable any feature —
        // documented contract in #725 is fail-open on unavailability.
        mockAPIClient.stubError(.notConnectedToInternet)

        let service = SystemCapabilitiesService(apiClient: apiClient)
        let outcome = await service.refresh()

        guard case .failedWithDiagnostics(let classification) = outcome else {
            return XCTFail("Expected a classified capabilities refresh failure")
        }
        XCTAssertEqual(classification.kind, .transport)
        XCTAssertEqual(classification.diagnosticDetail, "no internet connection")
        XCTAssertEqual(service.resolved, ResolvedSystemCapabilities.defaults)
    }

    func testRefreshDoesNotOverwriteExistingSnapshotOnTransportError() async {
        // First a successful refresh sets attentionEnabled=false; a
        // subsequent transport error must NOT flip it back to the
        // enabled default (fail-open means "don't touch existing state").
        mockAPIClient.stubResponse(json: """
        {
            "operatorFeatures": {
                "attentionEnabled": false
            }
        }
        """)

        let service = SystemCapabilitiesService(apiClient: apiClient)
        await service.refresh()
        XCTAssertFalse(service.resolved.attentionEnabled)

        mockAPIClient.stubError(.timedOut)
        await service.refresh()

        XCTAssertFalse(service.resolved.attentionEnabled,
                       "Existing resolved snapshot must persist through transient errors")
    }

    func testOlderSuccessfulRefreshCannotOverwriteNewerCapabilities() async {
        let olderRequest = AsyncBarrier()
        let calls = SystemCapabilitiesRequestOrdinal()
        addTeardownBlock { olderRequest.close() }
        mockAPIClient.asyncRequestHandler = { request in
            if await calls.next() == 1 {
                await olderRequest.arriveAndWait()
                return (
                    TestData.httpResponse(url: request.url, statusCode: 200),
                    Data(#"{"operatorFeatures":{"attentionEnabled":true,"filamentCoverageEnabled":true,"shiftPlanEnabled":true}}"#.utf8)
                )
            }
            return (
                TestData.httpResponse(url: request.url, statusCode: 200),
                Data(#"{"operatorFeatures":{"attentionEnabled":false,"filamentCoverageEnabled":false,"shiftPlanEnabled":false}}"#.utf8)
            )
        }

        let service = SystemCapabilitiesService(apiClient: apiClient)
        let olderRefresh = Task { await service.refresh() }
        await olderRequest.waitUntilArrived()

        let newerOutcome = await service.refresh()
        XCTAssertEqual(newerOutcome, .loaded)
        XCTAssertFalse(service.resolved.attentionEnabled)
        XCTAssertFalse(service.resolved.filamentCoverageEnabled)
        XCTAssertFalse(service.resolved.shiftPlanEnabled)

        olderRequest.release()
        let olderOutcome = await olderRefresh.value
        XCTAssertEqual(olderOutcome, .loaded)
        XCTAssertFalse(service.resolved.attentionEnabled)
        XCTAssertFalse(service.resolved.filamentCoverageEnabled)
        XCTAssertFalse(service.resolved.shiftPlanEnabled)
    }

    func testOlderLegacy404CannotOverwriteNewerCapabilities() async {
        let olderRequest = AsyncBarrier()
        let calls = SystemCapabilitiesRequestOrdinal()
        addTeardownBlock { olderRequest.close() }
        mockAPIClient.asyncRequestHandler = { request in
            if await calls.next() == 1 {
                await olderRequest.arriveAndWait()
                return (
                    TestData.httpResponse(url: request.url, statusCode: 404),
                    Data("{}".utf8)
                )
            }
            return (
                TestData.httpResponse(url: request.url, statusCode: 200),
                Data(#"{"operatorFeatures":{"attentionEnabled":false,"filamentCoverageEnabled":false,"shiftPlanEnabled":false}}"#.utf8)
            )
        }

        let service = SystemCapabilitiesService(apiClient: apiClient)
        let olderRefresh = Task { await service.refresh() }
        await olderRequest.waitUntilArrived()

        let newerOutcome = await service.refresh()
        XCTAssertEqual(newerOutcome, .loaded)
        XCTAssertFalse(service.resolved.attentionEnabled)
        XCTAssertFalse(service.resolved.filamentCoverageEnabled)
        XCTAssertFalse(service.resolved.shiftPlanEnabled)

        olderRequest.release()
        let olderOutcome = await olderRefresh.value
        XCTAssertEqual(olderOutcome, .legacyDefaults)
        XCTAssertFalse(service.resolved.attentionEnabled)
        XCTAssertFalse(service.resolved.filamentCoverageEnabled)
        XCTAssertFalse(service.resolved.shiftPlanEnabled)
    }

    func testCompletedDisabledRefreshPublishesWhileNewerRefreshIsStillInFlight() async {
        let olderRequest = AsyncBarrier()
        let newerRequest = AsyncBarrier()
        let calls = SystemCapabilitiesRequestOrdinal()
        addTeardownBlock {
            olderRequest.close()
            newerRequest.close()
        }
        mockAPIClient.asyncRequestHandler = { request in
            if await calls.next() == 1 {
                await olderRequest.arriveAndWait()
                return (
                    TestData.httpResponse(url: request.url, statusCode: 200),
                    Data(#"{"operatorFeatures":{"attentionEnabled":false,"filamentCoverageEnabled":false,"shiftPlanEnabled":false}}"#.utf8)
                )
            }
            await newerRequest.arriveAndWait()
            return (
                TestData.httpResponse(url: request.url, statusCode: 200),
                Data(#"{"operatorFeatures":{"attentionEnabled":true,"filamentCoverageEnabled":true,"shiftPlanEnabled":true}}"#.utf8)
            )
        }

        let service = SystemCapabilitiesService(apiClient: apiClient)
        let olderRefresh = Task { await service.refresh() }
        await olderRequest.waitUntilArrived()
        let newerRefresh = Task { await service.refresh() }
        await newerRequest.waitUntilArrived()

        olderRequest.release()
        let olderOutcome = await olderRefresh.value
        XCTAssertEqual(olderOutcome, .loaded)
        XCTAssertFalse(service.resolved.attentionEnabled)
        XCTAssertFalse(service.resolved.filamentCoverageEnabled)
        XCTAssertFalse(service.resolved.shiftPlanEnabled)

        newerRequest.release()
        let newerOutcome = await newerRefresh.value
        XCTAssertEqual(newerOutcome, .loaded)
        XCTAssertTrue(service.resolved.attentionEnabled)
        XCTAssertTrue(service.resolved.filamentCoverageEnabled)
        XCTAssertTrue(service.resolved.shiftPlanEnabled)
    }

    // MARK: - Stub

    func testStubReturnsCallerSuppliedSnapshot() {
        let snapshot = ResolvedSystemCapabilities(
            attentionEnabled: false,
            nativePushEnabled: false,
            filamentCoverageEnabled: false,
            guidedSwapEnabled: false,
            multiSlotFallbackEnabled: false,
            shiftPlanEnabled: false,
            printedPartsInventoryEnabled: false,
            offlineWriteReplayEnabled: false
        )
        let stub = StubSystemCapabilitiesService(resolved: snapshot)
        XCTAssertEqual(stub.resolved, snapshot)
    }

    func testStubSetResolvedMutatesSnapshot() async {
        let stub = StubSystemCapabilitiesService()
        XCTAssertTrue(stub.resolved.attentionEnabled)

        stub.setResolved(
            ResolvedSystemCapabilities(
                attentionEnabled: false,
                nativePushEnabled: false,
                filamentCoverageEnabled: true,
                guidedSwapEnabled: true,
                multiSlotFallbackEnabled: true,
                shiftPlanEnabled: true,
                printedPartsInventoryEnabled: true,
                offlineWriteReplayEnabled: true
            )
        )
        XCTAssertFalse(stub.resolved.attentionEnabled)

        // refresh() must be a no-op on the stub.
        await stub.refresh()
        XCTAssertFalse(stub.resolved.attentionEnabled)
    }
}

private actor SystemCapabilitiesRequestOrdinal {
    private var value = 0

    func next() -> Int {
        value += 1
        return value
    }
}
