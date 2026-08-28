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

    func testRefreshFailsOpenOnTransportError() async {
        // Transient network failure must not disable any feature —
        // documented contract in #725 is fail-open on unavailability.
        mockAPIClient.stubError(.notConnectedToInternet)

        let service = SystemCapabilitiesService(apiClient: apiClient)
        let outcome = await service.refresh()

        XCTAssertEqual(outcome, .failed)
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
