import XCTest
@testable import PrintFarmer

/// Scope A tests (issue #816): immutable per-request auth-session snapshot.
///
/// Every authenticated request must capture ONE auth-session snapshot BEFORE the
/// checker's await, and every 401 / session-expiry publication must carry that
/// snapshot's identity — never a concurrent same-server re-login's newer token.
final class APIClientAuthSessionTests: XCTestCase {

    // MARK: - Fixtures

    /// Captures every SessionExpired notification (payload only) for the lifetime
    /// of the test. Torn down in `deinit` so notifications cannot leak between tests.
    private final class ExpiryObserver: @unchecked Sendable {
        private let lock = NSLock()
        private var events: [(generation: Int, authSessionToken: Int)] = []
        private var observer: NSObjectProtocol?

        init() {
            observer = NotificationCenter.default.addObserver(
                forName: .sessionExpired, object: nil, queue: nil
            ) { [weak self] note in
                guard let self,
                      let info = note.userInfo,
                      let gen = info["generation"] as? Int,
                      let tok = info["authSessionToken"] as? Int
                else { return }
                self.lock.lock()
                self.events.append((gen, tok))
                self.lock.unlock()
            }
        }

        deinit {
            if let observer { NotificationCenter.default.removeObserver(observer) }
        }

        func snapshot() -> [(generation: Int, authSessionToken: Int)] {
            lock.lock()
            defer { lock.unlock() }
            return events
        }
    }

    /// Real APIClient wired through MockURLProtocol, with an ActiveServerGeneration
    /// and AuthOperationEpoch supplied by the test so `applySessionIfCurrent` can
    /// mutate the client's auth-session identity mid-flight.
    private func makeClient(
        gen: ActiveServerGeneration,
        transport: MockURLProtocol.Session
    ) async -> APIClient {
        APIClient(
            baseURL: URL(string: "https://a.example.com")!,
            session: transport.urlSession,
            serverGeneration: gen,
            accessToken: nil
        )
    }

    // MARK: - A tests

    /// A: T1 request enters expiry checker → parks on await → T2 (a newer login)
    /// re-applies the shared client to a different auth-session identity → T1's
    /// checker returns true → the posted session-expired event MUST carry T1's
    /// captured identity, NOT T2's freshly-applied identity.
    func testExpiryCheckerParkedThenNewerSessionAppliedCarriesOriginalIdentity() async throws {
        let observer = ExpiryObserver()
        let epoch = AuthOperationEpoch()
        let gen = ActiveServerGeneration()
        _ = gen.advance() // generation=1
        let transport = MockURLProtocol.makeSession()
        let client = await makeClient(gen: gen, transport: transport)

        // Apply T1 as the initial authenticated session.
        let t1 = epoch.advance()
        let appliedT1 = await client.applySessionIfCurrent(
            baseURL: nil, accessToken: "bearer-T1", epoch: epoch, token: t1)
        XCTAssertTrue(appliedT1)

        // Park the expiry checker at a barrier so we can interleave a T2 apply.
        let barrier = AsyncBarrier()
        defer { barrier.close() } // I: unstrand any parked continuation on failure
        defer { barrier.release() }
        await client.setTokenExpiryChecker { [weak barrier] in
            await barrier?.arriveAndWait()
            return true // report expired ONLY after we've mutated the shared client
        }

        // Stub any response; we won't reach the network because the checker throws.
        transport.requestHandler = { req in
            (TestData.httpResponse(url: req.url, statusCode: 200), Data("{}".utf8))
        }

        // T1's request enters and parks on the checker.
        let requestTask = Task { () -> NetworkError? in
            do {
                let _: [String: String] = try await client.get("/api/anything")
                return nil
            } catch let err as NetworkError {
                return err
            } catch {
                return nil
            }
        }
        await barrier.waitUntilArrived()

        // Interleave: apply T2 as the new authenticated session on the SAME client.
        let t2 = epoch.advance()
        let appliedT2 = await client.applySessionIfCurrent(
            baseURL: nil, accessToken: "bearer-T2", epoch: epoch, token: t2)
        XCTAssertTrue(appliedT2)

        // Release T1's checker; it returns true → posts .sessionExpired.
        barrier.release()
        let err = await requestTask.value
        XCTAssertNotNil(err)
        if case .unauthorized = err {} else { XCTFail("expected unauthorized, got \(String(describing: err))") }

        let events = observer.snapshot()
        XCTAssertEqual(events.count, 1, "exactly one session-expired must post")
        XCTAssertEqual(events[0].authSessionToken, t1,
                       "the posted authSessionToken MUST be T1 (captured at entry), not T2")
        XCTAssertEqual(events[0].generation, gen.current)
    }

    /// A: a client with an established authenticated session (as after a
    /// reconstructed ServiceContainer switch) that receives a 401 from the network
    /// MUST publish a session-expired carrying THAT session's identity.
    func testAuthenticatedClient401CarriesEstablishedSessionIdentity() async throws {
        let observer = ExpiryObserver()
        let epoch = AuthOperationEpoch()
        let gen = ActiveServerGeneration()
        _ = gen.advance() // generation=1
        let transport = MockURLProtocol.makeSession()
        let client = await makeClient(gen: gen, transport: transport)

        // Establish the reconstructed session identity (same primitive
        // ServiceContainer.establishReconstructedAuthSession uses under the hood).
        let established = epoch.advance()
        let ok = await client.applySessionIfCurrent(
            baseURL: nil, accessToken: "bearer-established", epoch: epoch, token: established)
        XCTAssertTrue(ok)

        transport.requestHandler = { req in
            (TestData.httpResponse(url: req.url, statusCode: 401), Data())
        }

        do {
            let _: [String: String] = try await client.get("/api/anything")
            XCTFail("expected unauthorized")
        } catch let err as NetworkError {
            if case .unauthorized = err {} else { XCTFail("expected .unauthorized, got \(err)") }
        }

        let events = observer.snapshot()
        XCTAssertEqual(events.count, 1)
        XCTAssertEqual(events[0].authSessionToken, established,
                       "a reconstructed client's 401 MUST carry the established identity")
        XCTAssertEqual(events[0].generation, gen.current)
    }

    /// A: an unauthenticated / login client (nil generation, no captured auth
    /// session) MUST NOT publish a session-expired even on a 401 — such a client
    /// is the login screen itself and cannot invalidate any authenticated session.
    func testUnauthenticatedClient401SuppressesSessionExpired() async throws {
        let observer = ExpiryObserver()
        let transport = MockURLProtocol.makeSession()
        // No ActiveServerGeneration → generationAtCreation is nil → suppression path.
        let client = APIClient(
            baseURL: URL(string: "https://login.example.com")!,
            session: transport.urlSession,
            serverGeneration: nil,
            accessToken: nil)

        transport.requestHandler = { req in
            (TestData.httpResponse(url: req.url, statusCode: 401), Data())
        }

        do {
            let _: [String: String] = try await client.get("/api/anything")
            XCTFail("expected unauthorized")
        } catch let err as NetworkError {
            if case .unauthorized = err {} else { XCTFail("expected .unauthorized, got \(err)") }
        }

        XCTAssertTrue(observer.snapshot().isEmpty,
                      "unauthenticated / login clients MUST NOT publish session-expired")
    }

    /// A: a `sessionSnapshotClient()` (used by AuthService.logout to issue /logout
    /// under the OLD session even after the shared client has been repointed by a
    /// concurrent login) carries the ORIGINAL bearer + baseURL, and is NEVER able
    /// to publish a session-expired event because its own generation is nil.
    func testSessionSnapshotClientIsFixedToOriginalBearerAndSuppressesExpiry() async throws {
        let observer = ExpiryObserver()
        let epoch = AuthOperationEpoch()
        let gen = ActiveServerGeneration()
        _ = gen.advance()
        let transport = MockURLProtocol.makeSession()
        let liveClient = await makeClient(gen: gen, transport: transport)

        // Apply T1 to live client; snapshot AT THIS POINT captures baseURL + T1 bearer.
        let t1 = epoch.advance()
        _ = await liveClient.applySessionIfCurrent(
            baseURL: URL(string: "https://a.example.com")!, accessToken: "bearer-T1",
            epoch: epoch, token: t1)
        let snapshot = await liveClient.sessionSnapshotClient()

        // Live client re-applied to T2 (a concurrent newer login).
        let t2 = epoch.advance()
        _ = await liveClient.applySessionIfCurrent(
            baseURL: URL(string: "https://b.example.com")!, accessToken: "bearer-T2",
            epoch: epoch, token: t2)

        // Snapshot still points at the ORIGINAL baseURL (a.example.com).
        let snapshotBase = await snapshot.currentBaseURL()
        XCTAssertEqual(snapshotBase.absoluteString, "https://a.example.com")

        // A 401 on the snapshot client (which has nil generation) MUST NOT publish.
        transport.requestHandler = { req in
            (TestData.httpResponse(url: req.url, statusCode: 401), Data())
        }
        do {
            let _: [String: String] = try await snapshot.get("/api/anything")
            XCTFail("expected unauthorized")
        } catch let err as NetworkError {
            if case .unauthorized = err {} else { XCTFail("expected .unauthorized, got \(err)") }
        }
        XCTAssertTrue(observer.snapshot().isEmpty,
                      "the session-snapshot client's 401 MUST NOT publish session-expired")
    }
}
