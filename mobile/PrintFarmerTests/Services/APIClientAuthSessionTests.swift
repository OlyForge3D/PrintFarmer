import XCTest
@testable import PrintFarmer

/// Scope A tests (issue #816): immutable per-request auth-session snapshot.
///
/// Every authenticated request must capture ONE auth-session snapshot BEFORE the
/// checker's await, and every 401 / session-expiry publication must carry that
/// snapshot's identity — never a concurrent same-server re-login's newer token.
final class APIClientAuthSessionTests: XCTestCase {
    private static let testServerID = UUID()

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
            serverGeneration: gen
        )
    }

    private struct AcceptedPostResponse: Decodable, Sendable {}
    private struct MissingGetResponse: Decodable, Sendable {}

    private enum AcceptedPostVariant: Sendable {
        case body
        case headerOnly
    }

    private func assertAcceptedPostUsesOneRequestSession(
        _ variant: AcceptedPostVariant,
        file: StaticString = #filePath,
        line: UInt = #line
    ) async throws {
        let observer = ExpiryObserver()
        let epoch = AuthOperationEpoch()
        let gen = ActiveServerGeneration()
        _ = gen.advance()
        let transport = MockURLProtocol.makeSession()
        let client = await makeClient(gen: gen, transport: transport)
        let t1 = epoch.advance()
        _ = await client.applyAuthenticatedSessionIfCurrent(
            baseURL: URL(string: "https://a.example.com")!,
            identity: AuthenticatedIdentity(
                accessToken: "bearer-T1",
                serverID: Self.testServerID
            ),
            epoch: epoch,
            token: t1
        )

        let checkerBarrier = AsyncBarrier()
        addTeardownBlock { checkerBarrier.close() }
        await client.setTokenExpiryChecker { [weak checkerBarrier] in
            await checkerBarrier?.arriveAndWait()
            return false
        }
        transport.requestHandler = { request in
            (TestData.httpResponse(url: request.url, statusCode: 401), Data())
        }

        let requestTask = Task { () -> NetworkError? in
            do {
                switch variant {
                case .body:
                    let _: HTTPDecodedResponse<AcceptedPostResponse> = try await client.post(
                        "/api/accepted-body",
                        body: ["value": 1],
                        headers: ["X-Test": "body"],
                        accepting: [202]
                    )
                case .headerOnly:
                    let _: HTTPDecodedResponse<AcceptedPostResponse> = try await client.post(
                        "/api/accepted-header",
                        headers: ["X-Test": "header"],
                        accepting: [202]
                    )
                }
                return nil
            } catch let error as NetworkError {
                return error
            } catch {
                return nil
            }
        }
        await checkerBarrier.waitUntilArrived()

        let t2 = epoch.advance()
        _ = await client.applyAuthenticatedSessionIfCurrent(
            baseURL: URL(string: "https://b.example.com")!,
            identity: AuthenticatedIdentity(
                accessToken: "bearer-T2",
                serverID: Self.testServerID
            ),
            epoch: epoch,
            token: t2
        )
        checkerBarrier.release()

        let error = await requestTask.value
        if case .unauthorized? = error {
        } else {
            XCTFail("expected unauthorized, got \(String(describing: error))", file: file, line: line)
        }
        let request = try XCTUnwrap(transport.capturedRequests.first, file: file, line: line)
        XCTAssertEqual(request.url?.host, "a.example.com", file: file, line: line)
        XCTAssertEqual(
            request.value(forHTTPHeaderField: "Authorization"),
            "Bearer bearer-T1",
            file: file,
            line: line
        )
        let events = observer.snapshot()
        XCTAssertEqual(events.count, 1, file: file, line: line)
        XCTAssertEqual(
            events.first?.authSessionToken,
            t1,
            "the 401 must retain the same T1 identity used for URL and bearer",
            file: file,
            line: line
        )
    }

    // MARK: - A tests

    func testAcceptedStatusBodyPostUsesOneRequestSessionAcrossT1ToT2Race() async throws {
        try await assertAcceptedPostUsesOneRequestSession(.body)
    }

    func testAcceptedStatusHeaderPostUsesOneRequestSessionAcrossT1ToT2Race() async throws {
        try await assertAcceptedPostUsesOneRequestSession(.headerOnly)
    }

    func testMissingStatusGetSuppressesSessionExpiredForAuthenticatedClient() async throws {
        let observer = ExpiryObserver()
        let epoch = AuthOperationEpoch()
        let gen = ActiveServerGeneration()
        _ = gen.advance()
        let transport = MockURLProtocol.makeSession()
        let client = await makeClient(gen: gen, transport: transport)
        let token = epoch.advance()
        _ = await client.applyAuthenticatedSessionIfCurrent(
            baseURL: nil,
            identity: AuthenticatedIdentity(
                accessToken: "bearer",
                serverID: Self.testServerID
            ),
            epoch: epoch,
            token: token
        )
        transport.requestHandler = { request in
            (TestData.httpResponse(url: request.url, statusCode: 401), Data())
        }

        let response: MissingGetResponse? = try await client.get(
            "/api/system/farm-shape",
            treating: [401, 404]
        )

        XCTAssertNil(response)
        XCTAssertTrue(observer.snapshot().isEmpty)
    }

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
        let appliedT1 = await client.applyAuthenticatedSessionIfCurrent(
            baseURL: nil, identity: AuthenticatedIdentity(accessToken: "bearer-T1", serverID: Self.testServerID), epoch: epoch, token: t1)
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
        let appliedT2 = await client.applyAuthenticatedSessionIfCurrent(
            baseURL: nil, identity: AuthenticatedIdentity(accessToken: "bearer-T2", serverID: Self.testServerID), epoch: epoch, token: t2)
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
        let ok = await client.applyAuthenticatedSessionIfCurrent(
            baseURL: nil, identity: AuthenticatedIdentity(accessToken: "bearer-established", serverID: Self.testServerID), epoch: epoch, token: established)
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
            serverGeneration: nil)

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
        _ = await liveClient.applyAuthenticatedSessionIfCurrent(
            baseURL: URL(string: "https://a.example.com")!, identity: AuthenticatedIdentity(accessToken: "bearer-T1", serverID: Self.testServerID),
            epoch: epoch, token: t1)
        let snapshot = await liveClient.sessionSnapshotClient()

        // Live client re-applied to T2 (a concurrent newer login).
        let t2 = epoch.advance()
        _ = await liveClient.applyAuthenticatedSessionIfCurrent(
            baseURL: URL(string: "https://b.example.com")!, identity: AuthenticatedIdentity(accessToken: "bearer-T2", serverID: Self.testServerID),
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

    // MARK: - A1 tests: request-build bearer coherence

    /// A1: `getData` MUST build its URLRequest from the RequestSession snapshot's
    /// bearer captured at PUBLIC API ENTRY — never from `self.accessToken` after the
    /// checker's await. Otherwise a concurrent T2 apply during the checker's park
    /// would let this request go out with T2's bearer while it is still labeled with
    /// T1's generation/authSessionToken. This is the primary A1 regression trap.
    func testGetDataBuildsRequestWithSnapshotBearerAfterCheckerInterleave() async throws {
        let epoch = AuthOperationEpoch()
        let gen = ActiveServerGeneration()
        _ = gen.advance()
        let transport = MockURLProtocol.makeSession()
        let client = await makeClient(gen: gen, transport: transport)

        // Apply T1 as the initial authenticated session (bearer-T1).
        let t1 = epoch.advance()
        let appliedT1 = await client.applyAuthenticatedSessionIfCurrent(
            baseURL: nil, identity: AuthenticatedIdentity(accessToken: "bearer-T1", serverID: Self.testServerID), epoch: epoch, token: t1)
        XCTAssertTrue(appliedT1)

        // Park the expiry checker so T2 can apply during its await window.
        let checkerBarrier = AsyncBarrier()
        // I: register the unconditional close BEFORE any throwing operation so a
        // failed assertion mid-flight cannot strand the parked checker continuation.
        addTeardownBlock { checkerBarrier.close() }
        await client.setTokenExpiryChecker { [weak checkerBarrier] in
            await checkerBarrier?.arriveAndWait()
            return false // NOT expired — the request must proceed to the network.
        }

        // Handler asserts nothing here (bearer is verified after the network return).
        transport.requestHandler = { req in
            (TestData.httpResponse(url: req.url, statusCode: 200), Data("{}".utf8))
        }

        // Kick off the getData; it will park inside the checker.
        let requestTask = Task {
            _ = try await client.getData("/api/data")
        }
        await checkerBarrier.waitUntilArrived()

        // During the checker's park, apply T2 with a DIFFERENT bearer to the SAME
        // shared client. If `getData` were building from `self.accessToken` after
        // the await (the A1 bug), the outbound request would carry bearer-T2 while
        // still labeled with T1's identity.
        let t2 = epoch.advance()
        let appliedT2 = await client.applyAuthenticatedSessionIfCurrent(
            baseURL: nil, identity: AuthenticatedIdentity(accessToken: "bearer-T2", serverID: Self.testServerID), epoch: epoch, token: t2)
        XCTAssertTrue(appliedT2)

        // Release the checker; getData now builds the request AFTER the await.
        checkerBarrier.release()
        try await requestTask.value

        // Assert the captured request carries T1's bearer, NOT T2's — proving the
        // request was built from the immutable snapshot captured at entry.
        let captured = transport.capturedRequests
        XCTAssertEqual(captured.count, 1, "exactly one request should have been sent")
        let auth = captured.first?.value(forHTTPHeaderField: "Authorization") ?? ""
        XCTAssertTrue(auth.contains("bearer-T1"),
                      "getData MUST build with the snapshot's T1 bearer, not T2 applied during the checker's await. Actual header=\(auth)")
        XCTAssertFalse(auth.contains("bearer-T2"),
                       "getData MUST NOT carry T2's bearer for a T1-labeled request. Actual header=\(auth)")
    }

    /// A1: for every public request path (`get`, `post`, `postVoid`, `put`,
    /// `putVoid`, `patch`, `delete`), a request captured at entry uses THAT
    /// snapshot's bearer for the outbound request — even when the shared client
    /// is repointed BEFORE the request completes. This proves the snapshot-thread
    /// refactor covers every public API consistently (no path may build with T2
    /// bearer but label T1 or vice versa).
    func testAllPublicRequestPathsUseSnapshotBearerAcrossReapply() async throws {
        let epoch = AuthOperationEpoch()
        let gen = ActiveServerGeneration()
        _ = gen.advance()

        struct Case {
            let name: String
            let run: @Sendable (APIClient) async throws -> Void
        }

        let cases: [Case] = [
            Case(name: "get") { c in let _: [String: String] = try await c.get("/api/g") },
            Case(name: "post-decode") { c in
                let _: [String: String] = try await c.post("/api/p", body: ["x": 1])
            },
            Case(name: "post-decode-headers") { c in
                let _: [String: String] = try await c.post(
                    "/api/ph",
                    body: ["x": 1],
                    headers: ["X-Test": "value"]
                )
            },
            Case(name: "post-void") { c in try await c.postVoid("/api/pv") },
            Case(name: "post-void-body") { c in try await c.postVoid("/api/pvb", body: ["x": 1]) },
            Case(name: "put") { c in
                let _: [String: String] = try await c.put("/api/pu", body: ["x": 1])
            },
            Case(name: "put-void") { c in try await c.putVoid("/api/puv") },
            Case(name: "put-void-body") { c in try await c.putVoid("/api/puvb", body: ["x": 1]) },
            Case(name: "patch") { c in
                let _: [String: String] = try await c.patch("/api/pa", body: ["x": 1])
            },
            Case(name: "delete") { c in try await c.delete("/api/d") },
            Case(name: "getData") { c in _ = try await c.getData("/api/gd") },
        ]

        for kase in cases {
            let transport = MockURLProtocol.makeSession()
            let client = await makeClient(gen: gen, transport: transport)

            // Apply T1 (bearer-T1) as initial session; then park a checker so we
            // can interleave a T2 apply during the same-request in-flight window.
            let t1 = epoch.advance()
            _ = await client.applyAuthenticatedSessionIfCurrent(
                baseURL: nil, identity: AuthenticatedIdentity(accessToken: "bearer-T1-\(kase.name)", serverID: Self.testServerID), epoch: epoch, token: t1)

            let checkerBarrier = AsyncBarrier()
            addTeardownBlock { checkerBarrier.close() }
            await client.setTokenExpiryChecker { [weak checkerBarrier] in
                await checkerBarrier?.arriveAndWait()
                return false
            }

            transport.requestHandler = { req in
                (TestData.httpResponse(url: req.url, statusCode: 200), Data("{}".utf8))
            }

            let task = Task { try await kase.run(client) }
            await checkerBarrier.waitUntilArrived()

            // T2 apply during the checker's parked await.
            let t2 = epoch.advance()
            _ = await client.applyAuthenticatedSessionIfCurrent(
                baseURL: nil, identity: AuthenticatedIdentity(accessToken: "bearer-T2-\(kase.name)", serverID: Self.testServerID), epoch: epoch, token: t2)

            checkerBarrier.release()
            try await task.value

            let captured = transport.capturedRequests
            XCTAssertEqual(captured.count, 1, "\(kase.name): exactly one request expected")
            let auth = captured.first?.value(forHTTPHeaderField: "Authorization") ?? ""
            XCTAssertTrue(auth.contains("bearer-T1-\(kase.name)"),
                          "\(kase.name) MUST carry T1's snapshot bearer, got header=\(auth)")
            XCTAssertFalse(auth.contains("bearer-T2-\(kase.name)"),
                           "\(kase.name) MUST NOT carry T2's bearer, got header=\(auth)")
        }
    }

    // MARK: - A2 tests: atomic identity at APIClient construction

    /// A2: an APIClient constructed with `authSessionToken: t` at INIT time
    /// publishes a session-expired event with EXACTLY that token on a 401.
    /// This proves the reconstructed-client path in ServiceContainer no longer
    /// needs a fire-and-forget establishReconstructedAuthSession Task —
    /// bearer + identity are bound atomically at construction.
    func testInitBindsIdentityAtomicallyFromSynchronouslyCapturedToken() async throws {
        let observer = ExpiryObserver()
        let gen = ActiveServerGeneration()
        _ = gen.advance() // generation=1
        let transport = MockURLProtocol.makeSession()

        // Construct client with T2 identity ATOMICALLY (as production now does).
        // J4: authenticated construction requires a stable serverID.
        let client = APIClient(
            baseURL: URL(string: "https://a.example.com")!,
            session: transport.urlSession,
            serverGeneration: gen,
            authenticated: AuthenticatedIdentity(accessToken: "bearer-T2", serverID: UUID(), authSessionToken: 42)
        )

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
        XCTAssertEqual(events.count, 1, "expected exactly one session-expired publication")
        XCTAssertEqual(events.first?.authSessionToken, 42,
                       "identity must match the token captured at construction")
    }

    /// A2: a delayed reconstructed-client establishment (the old fire-and-forget
    /// pattern) CANNOT overwrite a fresher session's bearer + identity that has
    /// already been applied to the shared client. This proves the CAS in
    /// applySessionIfCurrent is the last line of defense: even a stray late-read
    /// caller (e.g. a hand-rolled Task that captured a stale accessToken and
    /// reads epoch after suspending) is rejected because the epoch has advanced.
    func testDelayedReconstructionCannotOverwriteFresherSession() async throws {
        let observer = ExpiryObserver()
        let epoch = AuthOperationEpoch()
        let gen = ActiveServerGeneration()
        _ = gen.advance()
        let transport = MockURLProtocol.makeSession()
        let client = await makeClient(gen: gen, transport: transport)

        // T1 apply (as if from an earlier login/rebuild).
        let t1 = epoch.advance()
        _ = await client.applyAuthenticatedSessionIfCurrent(
            baseURL: nil, identity: AuthenticatedIdentity(accessToken: "bearer-T1", serverID: Self.testServerID), epoch: epoch, token: t1)

        // T2 apply advances the epoch and takes over the shared client.
        let t2 = epoch.advance()
        _ = await client.applyAuthenticatedSessionIfCurrent(
            baseURL: nil, identity: AuthenticatedIdentity(accessToken: "bearer-T2", serverID: Self.testServerID), epoch: epoch, token: t2)

        // Now a STALE reconstruction runs (as the old fire-and-forget Task would
        // have): it holds T1's bearer and reads the CURRENT epoch (t2) — the very
        // race the A2 refactor eliminated. Even so, applySessionIfCurrent CAS with
        // the ACTUAL stale token (t1) must fail: the epoch has advanced beyond t1.
        let staleApplied = await client.applyAuthenticatedSessionIfCurrent(
            baseURL: nil, identity: AuthenticatedIdentity(accessToken: "bearer-T1", serverID: Self.testServerID), epoch: epoch, token: t1)
        XCTAssertFalse(staleApplied,
                       "a stale token's applySessionIfCurrent MUST NOT overwrite the fresher session")

        // Now assert the shared client still carries T2's bearer + T2's identity
        // via a 401 emission.
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
        XCTAssertEqual(events.first?.authSessionToken, t2,
                       "session-expiry must carry T2's identity, not the stale T1 or nil")

        // And the outbound request carried T2's bearer, not T1's.
        let captured = transport.capturedRequests
        XCTAssertEqual(captured.count, 1)
        let auth = captured.first?.value(forHTTPHeaderField: "Authorization") ?? ""
        XCTAssertTrue(auth.contains("bearer-T2"),
                      "shared client must still hold T2's bearer, got header=\(auth)")
    }

    /// A2/E: an unauthenticated construction (no bundled identity) has NIL
    /// authSessionToken — with the bundled `AuthenticatedIdentity` this is now
    /// structurally guaranteed (authSessionToken cannot be supplied without an
    /// identity), so a login-time client (no bearer yet) can never publish
    /// session-expired.
    func testInitWithNilAccessTokenForcesNilIdentity() async throws {
        let observer = ExpiryObserver()
        let gen = ActiveServerGeneration()
        _ = gen.advance()
        let transport = MockURLProtocol.makeSession()
        let client = APIClient(
            baseURL: URL(string: "https://a.example.com")!,
            session: transport.urlSession,
            serverGeneration: gen           // no bearer, no identity
        )

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
                      "an unauthenticated client MUST NOT publish session-expired even if init was passed a token")
    }

    /// A1 (issue #816 reject, Hicks): T1 request enters `getData`, captures its
    /// pre-await session snapshot (including baseURL), parks on the token-expiry
    /// checker's await → T2 apply repoints the SHARED apiClient's baseURL to a
    /// DIFFERENT server B → checker returns → T1's `buildRequest` resolves the
    /// URL against `requestSession.baseURL` (server A), NEVER `self.baseURL`
    /// (server B). The outbound URL host MUST be server A's — the primary A1
    /// regression trap. Bearer-only tests are insufficient: the whole ask is
    /// "URL/host/server identity in the same pre-await snapshot".
    func testGetDataUsesSnapshotHostAcrossBaseURLReapply() async throws {
        let epoch = AuthOperationEpoch()
        let gen = ActiveServerGeneration()
        _ = gen.advance()
        let transport = MockURLProtocol.makeSession()
        let client = APIClient(
            baseURL: URL(string: "https://a.example.com")!,
            session: transport.urlSession,
            serverGeneration: gen
        )

        // Apply T1 as bearer=T1 at server A.
        let t1 = epoch.advance()
        _ = await client.applyAuthenticatedSessionIfCurrent(
            baseURL: URL(string: "https://a.example.com")!,
            identity: AuthenticatedIdentity(accessToken: "bearer-T1", serverID: Self.testServerID),
            epoch: epoch, token: t1)

        let checkerBarrier = AsyncBarrier()
        addTeardownBlock { checkerBarrier.close() }
        await client.setTokenExpiryChecker { [weak checkerBarrier] in
            await checkerBarrier?.arriveAndWait()
            return false
        }

        transport.requestHandler = { req in
            (TestData.httpResponse(url: req.url, statusCode: 200), Data("{}".utf8))
        }

        // T1 request enters getData, parks on the checker await.
        let requestTask = Task { _ = try await client.getData("/api/data") }
        await checkerBarrier.waitUntilArrived()

        // T2 apply during the parked await: repoint apiClient to server B with a
        // DIFFERENT bearer. If getData resolves against `self.baseURL` after the
        // await (the A1 bug), the outbound request goes to server B under T1's
        // bearer, labeled with T1's identity.
        let t2 = epoch.advance()
        _ = await client.applyAuthenticatedSessionIfCurrent(
            baseURL: URL(string: "https://b.example.com")!,
            identity: AuthenticatedIdentity(accessToken: "bearer-T2", serverID: Self.testServerID),
            epoch: epoch, token: t2)

        checkerBarrier.release()
        try await requestTask.value

        let captured = transport.capturedRequests
        XCTAssertEqual(captured.count, 1, "exactly one request must be sent")
        // A1 primary invariant: HOST MUST BE SERVER A.
        XCTAssertEqual(captured.first?.url?.host, "a.example.com",
                       "getData MUST resolve URL against the SNAPSHOT's baseURL (server A), not self.baseURL (server B). Actual URL=\(String(describing: captured.first?.url))")
        XCTAssertNotEqual(captured.first?.url?.host, "b.example.com",
                          "getData MUST NOT hit server B under T1's identity")
        // A1 co-invariant: BEARER MUST BE T1.
        let auth = captured.first?.value(forHTTPHeaderField: "Authorization") ?? ""
        XCTAssertTrue(auth.contains("bearer-T1"),
                      "getData MUST carry T1 bearer; got header=\(auth)")
        XCTAssertFalse(auth.contains("bearer-T2"),
                       "getData MUST NOT carry T2 bearer; got header=\(auth)")
    }

    /// A1 (issue #816 reject, Hicks): every public request path — get / post
    /// (decode) / postVoid / postVoid+body / put / putVoid / putVoid+body /
    /// patch / delete / getData — resolves its outbound URL against the
    /// PRE-AWAIT snapshot's baseURL AND uses that snapshot's bearer, even
    /// when the shared apiClient is repointed to a DIFFERENT server (host)
    /// with a DIFFERENT bearer during the in-flight window. Bearer-only
    /// coverage does not exercise the host invariant that motivates A1.
    func testAllPublicRequestPathsUseSnapshotHostAndBearerAcrossReapply() async throws {
        let epoch = AuthOperationEpoch()
        let gen = ActiveServerGeneration()
        _ = gen.advance()

        struct Case {
            let name: String
            let run: @Sendable (APIClient) async throws -> Void
        }

        let cases: [Case] = [
            Case(name: "get") { c in let _: [String: String] = try await c.get("/api/g") },
            Case(name: "post-decode") { c in
                let _: [String: String] = try await c.post("/api/p", body: ["x": 1])
            },
            Case(name: "post-decode-headers") { c in
                let _: [String: String] = try await c.post(
                    "/api/ph",
                    body: ["x": 1],
                    headers: ["X-Test": "value"]
                )
            },
            Case(name: "post-void") { c in try await c.postVoid("/api/pv") },
            Case(name: "post-void-body") { c in try await c.postVoid("/api/pvb", body: ["x": 1]) },
            Case(name: "put") { c in
                let _: [String: String] = try await c.put("/api/pu", body: ["x": 1])
            },
            Case(name: "put-void") { c in try await c.putVoid("/api/puv") },
            Case(name: "put-void-body") { c in try await c.putVoid("/api/puvb", body: ["x": 1]) },
            Case(name: "patch") { c in
                let _: [String: String] = try await c.patch("/api/pa", body: ["x": 1])
            },
            Case(name: "delete") { c in try await c.delete("/api/d") },
            Case(name: "getData") { c in _ = try await c.getData("/api/gd") },
        ]

        for kase in cases {
            let transport = MockURLProtocol.makeSession()
            let client = APIClient(
                baseURL: URL(string: "https://a.example.com")!,
                session: transport.urlSession,
                serverGeneration: gen
            )
            let t1 = epoch.advance()
            _ = await client.applyAuthenticatedSessionIfCurrent(
                baseURL: URL(string: "https://a.example.com")!,
                identity: AuthenticatedIdentity(accessToken: "bearer-T1-\(kase.name)", serverID: Self.testServerID),
                epoch: epoch, token: t1)

            let checkerBarrier = AsyncBarrier()
            addTeardownBlock { checkerBarrier.close() }
            await client.setTokenExpiryChecker { [weak checkerBarrier] in
                await checkerBarrier?.arriveAndWait()
                return false
            }

            transport.requestHandler = { req in
                (TestData.httpResponse(url: req.url, statusCode: 200), Data("{}".utf8))
            }

            let task = Task { try await kase.run(client) }
            await checkerBarrier.waitUntilArrived()

            // Repoint to server B with a different bearer during the parked
            // in-flight window.
            let t2 = epoch.advance()
            _ = await client.applyAuthenticatedSessionIfCurrent(
                baseURL: URL(string: "https://b.example.com")!,
                identity: AuthenticatedIdentity(accessToken: "bearer-T2-\(kase.name)", serverID: Self.testServerID),
                epoch: epoch, token: t2)

            checkerBarrier.release()
            try await task.value

            let captured = transport.capturedRequests
            XCTAssertEqual(captured.count, 1, "\(kase.name): exactly one request expected")
            XCTAssertEqual(captured.first?.url?.host, "a.example.com",
                           "\(kase.name) MUST resolve URL against snapshot host (A), not self.baseURL (B). URL=\(String(describing: captured.first?.url))")
            XCTAssertNotEqual(captured.first?.url?.host, "b.example.com",
                              "\(kase.name) MUST NOT hit server B under T1's identity")
            let auth = captured.first?.value(forHTTPHeaderField: "Authorization") ?? ""
            XCTAssertTrue(auth.contains("bearer-T1-\(kase.name)"),
                          "\(kase.name) MUST carry T1's bearer; header=\(auth)")
            XCTAssertFalse(auth.contains("bearer-T2-\(kase.name)"),
                           "\(kase.name) MUST NOT carry T2's bearer; header=\(auth)")
        }
    }
}
