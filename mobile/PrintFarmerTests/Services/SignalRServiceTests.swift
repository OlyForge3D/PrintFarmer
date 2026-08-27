import XCTest
@testable import PrintFarmer

/// Regression tests for the SignalR negotiate request builder.
///
/// Guards against the redaction-in-transport defect (issue #873): the
/// negotiate POST must carry the real bearer token on the wire
/// (`Authorization: Bearer <jwt>`), never a redacted placeholder. These tests
/// capture the OUTGOING request via `MockURLProtocol` and assert the exact
/// header value, so a future change that masks the token before transmit fails
/// here instead of silently 401ing against a real server.
final class SignalRServiceTests: XCTestCase {

    private var mockSession: MockURLProtocol.Session!

    override func setUp() {
        super.setUp()
        mockSession = MockURLProtocol.makeSession()
    }

    override func tearDown() {
        mockSession = nil
        super.tearDown()
    }

    /// Fails the negotiate response (HTTP 500) so `connect()` throws before the
    /// WebSocket upgrade — which `MockURLProtocol` cannot service — while still
    /// capturing the fully built negotiate request for header assertions.
    private func makeService(token: String?) -> SignalRService {
        mockSession.requestHandler = { request in
            (TestData.httpResponse(url: request.url, statusCode: 500), Data("{}".utf8))
        }
        return SignalRService(
            serverURL: TestData.testBaseURL,
            session: mockSession.urlSession,
            tokenProvider: { token }
        )
    }

    func testNegotiateSendsBearerAuthorizationHeader() async {
        let token = "test-jwt-token-123"
        let service = makeService(token: token)

        // connect() is expected to throw once negotiate returns 500.
        do {
            try await service.connect()
            XCTFail("connect() should have thrown on a 500 negotiate response")
        } catch {
            // expected
        }
        await service.disconnect()

        let captured = mockSession.capturedRequests.first
        XCTAssertNotNil(captured, "negotiate request should have been sent")
        XCTAssertEqual(captured?.url?.path.hasSuffix("/hubs/printers/negotiate"), true)
        XCTAssertEqual(
            captured?.value(forHTTPHeaderField: "Authorization"),
            "Bearer \(token)",
            "negotiate must transmit the real bearer token, not a redacted placeholder"
        )
    }

    func testNegotiateOmitsAuthorizationWhenNoToken() async {
        let service = makeService(token: nil)

        do {
            try await service.connect()
            XCTFail("connect() should have thrown on a 500 negotiate response")
        } catch {
            // expected
        }
        await service.disconnect()

        let captured = mockSession.capturedRequests.first
        XCTAssertNotNil(captured, "negotiate request should have been sent")
        XCTAssertNil(captured?.value(forHTTPHeaderField: "Authorization"))
    }

    func testCancelledReadinessConnectAllowsLaterRecovery() async {
        let entered = AsyncBarrier()
        let release = AsyncBarrier()
        defer {
            entered.close()
            release.close()
        }
        mockSession.asyncRequestHandler = { request in
            entered.signal()
            await release.arriveAndWait()
            return (TestData.httpResponse(url: request.url, statusCode: 500), Data("{}".utf8))
        }
        let service = SignalRService(
            serverURL: TestData.testBaseURL,
            session: mockSession.urlSession,
            tokenProvider: { "test-token" }
        )

        let readiness = Task {
            try await service.connectForReadiness()
        }
        await entered.waitUntilArrived()
        readiness.cancel()
        _ = try? await readiness.value

        mockSession.asyncRequestHandler = nil
        mockSession.requestHandler = { request in
            (TestData.httpResponse(url: request.url, statusCode: 500), Data("{}".utf8))
        }
        await service.ensureConnected()

        XCTAssertGreaterThanOrEqual(
            mockSession.capturedRequests.count,
            2,
            "Readiness cancellation must not suppress the next recovery attempt"
        )
        await service.disconnect()
    }

    func testTransportCancellationDuringNormalConnectAllowsLaterRecovery() async {
        let entered = AsyncBarrier()
        let release = AsyncBarrier()
        defer {
            entered.close()
            release.close()
        }
        mockSession.asyncRequestHandler = { _ in
            entered.signal()
            await release.arriveAndWait()
            throw URLError(.cancelled)
        }
        let service = SignalRService(
            serverURL: TestData.testBaseURL,
            session: mockSession.urlSession,
            tokenProvider: { "test-token" }
        )

        let firstConnect = Task {
            try await service.connect()
        }
        await entered.waitUntilArrived()
        release.release()
        _ = try? await firstConnect.value

        mockSession.asyncRequestHandler = nil
        mockSession.requestHandler = { request in
            (TestData.httpResponse(url: request.url, statusCode: 500), Data("{}".utf8))
        }
        await service.ensureConnected()

        XCTAssertGreaterThanOrEqual(
            mockSession.capturedRequests.count,
            2,
            "A transport-cancelled normal connect must leave recovery enabled"
        )
        await service.disconnect()
    }

    func testReadinessWaitsForOverlappingConnectOutcome() async {
        let negotiateEntered = AsyncBarrier()
        let releaseNegotiate = AsyncBarrier()
        let readinessWaiting = AsyncBarrier()
        defer {
            negotiateEntered.close()
            releaseNegotiate.close()
            readinessWaiting.close()
        }
        mockSession.asyncRequestHandler = { request in
            negotiateEntered.signal()
            await releaseNegotiate.arriveAndWait()
            return (TestData.httpResponse(url: request.url, statusCode: 500), Data("{}".utf8))
        }
        let service = SignalRService(
            serverURL: TestData.testBaseURL,
            session: mockSession.urlSession,
            tokenProvider: { "test-token" },
            readinessWaitObserver: {
                readinessWaiting.signal()
            }
        )

        let initialConnect = Task {
            try await service.connect()
        }
        await negotiateEntered.waitUntilArrived()
        let readiness = Task {
            try await service.connectForReadiness()
        }
        await readinessWaiting.waitUntilArrived()
        releaseNegotiate.release()

        _ = try? await initialConnect.value
        do {
            try await readiness.value
            XCTFail("Readiness must not succeed when the overlapping connection fails")
        } catch {
            // expected
        }
        await service.disconnect()
    }
}
