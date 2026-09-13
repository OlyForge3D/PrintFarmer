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
    #if DEBUG
    func testMockControlInvalidationSupportsCancellationAndInFlightCallbackSimulation() {
        let service = MockSignalRService()
        let recorder = ControlHintRecorder()
        let subscription = service.onPrinterControlOperationUpdated { recorder.append($0) }
        let hint = PrinterControlOperationInvalidation(
            printerId: TestData.testUUID, operationId: ControlOperationTestJSON.operationId, rowVersion: "r1"
        )
        XCTAssertEqual(service.controlOperationSubscriberCount, 1)
        service.simulateControlOperationUpdated(hint)
        XCTAssertEqual(recorder.snapshot, [hint])
        subscription.cancel()
        XCTAssertEqual(service.controlOperationSubscriberCount, 0)
        service.simulateControlOperationUpdated(hint)
        XCTAssertEqual(recorder.snapshot.count, 1)
        service.simulateCapturedControlOperationUpdated(at: 0, event: hint)
        XCTAssertEqual(recorder.snapshot.count, 2)
    }

    func testControlOperationInvalidationIsStrictScopedAndCancellable() throws {
        let service = SignalRService(serverURL: TestData.testBaseURL, tokenProvider: { nil })
        let otherServer = SignalRService(serverURL: URL(string: "https://other.example.com")!, tokenProvider: { nil })
        let received = ControlHintRecorder()
        let foreign = ControlHintRecorder()
        let subscription = service.onPrinterControlOperationUpdated { received.append($0) }
        let otherSubscription = otherServer.onPrinterControlOperationUpdated { foreign.append($0) }
        defer {
            subscription.cancel()
            otherSubscription.cancel()
        }
        let payload = """
        {"printerId":"\(TestData.testUUID)","operationId":"\(ControlOperationTestJSON.operationId)","rowVersion":"opaque-r2"}
        """
        func frame(_ target: String, _ body: String) -> Data {
            Data("{\"type\":1,\"target\":\"\(target)\",\"arguments\":[\(body)]}\u{1E}".utf8)
        }
        service.processIncomingDataForTesting(frame("PrinterControlOperationUpdated", payload))
        service.processIncomingDataForTesting(frame("printercontroloperationupdated", "{}"))
        service.processIncomingDataForTesting(frame("printercontroloperationupdated", payload.replacingOccurrences(of: "opaque-r2", with: "")))
        service.processIncomingDataForTesting(frame("printercontroloperationupdated", payload))
        // Hints need no monotonic ordering: even an older version means refetch,
        // never an authoritative state transition or a reason to release a lock.
        service.processIncomingDataForTesting(frame("printercontroloperationupdated", payload.replacingOccurrences(of: "opaque-r2", with: "opaque-r1")))
        service.drainHubCoordinatorForTesting()
        otherServer.drainHubCoordinatorForTesting()
        XCTAssertEqual(received.snapshot.map(\.rowVersion), ["opaque-r2", "opaque-r1"])
        XCTAssertEqual(received.snapshot.first?.printerId, TestData.testUUID)
        XCTAssertTrue(foreign.snapshot.isEmpty)
        subscription.cancel()
        service.processIncomingDataForTesting(frame("printercontroloperationupdated", payload))
        service.drainHubCoordinatorForTesting()
        XCTAssertEqual(received.snapshot.count, 2)
    }

    private final class ControlHintRecorder: @unchecked Sendable {
        private let lock = NSLock()
        private var hints: [PrinterControlOperationInvalidation] = []

        func append(_ hint: PrinterControlOperationInvalidation) {
            lock.lock()
            defer { lock.unlock() }
            hints.append(hint)
        }

        var snapshot: [PrinterControlOperationInvalidation] {
            lock.lock()
            defer { lock.unlock() }
            return hints
        }
    }
    #endif

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
