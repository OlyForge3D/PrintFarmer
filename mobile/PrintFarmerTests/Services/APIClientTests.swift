import XCTest
@testable import PrintFarmer

/// Tests for the APIClient actor: request building, token injection,
/// error mapping, and base URL configuration.
final class APIClientTests: XCTestCase {

    private static let testServerID = UUID()

    private struct TransportValue: Decodable, Equatable, Sendable {
        let value: String
    }

    private var mockAPIClient: MockAPIClient!
    private var apiClient: APIClient!

    override func setUp() {
        super.setUp()
        mockAPIClient = MockAPIClient()
        apiClient = mockAPIClient.apiClient
    }

    override func tearDown() {
        apiClient = nil
        mockAPIClient = nil
        super.tearDown()
    }

    // MARK: - Base URL Configuration

    func testBaseURLIsSetOnInit() async {
        let url = await apiClient.currentBaseURL()
        XCTAssertEqual(url, TestData.testBaseURL)
    }

    func testUpdateBaseURL() async {
        let newURL = URL(string: "https://new.example.com")!
        await apiClient.updateBaseURL(newURL)
        let current = await apiClient.currentBaseURL()
        XCTAssertEqual(current, newURL)
    }

    func testSavedBaseURLPersistsToUserDefaults() async {
        let newURL = URL(string: "https://saved.example.com")!
        await apiClient.updateBaseURL(newURL)
        let saved = UserDefaults.standard.string(forKey: APIClient.serverURLKey)
        XCTAssertEqual(saved, "https://saved.example.com")
        // Cleanup
        UserDefaults.standard.removeObject(forKey: APIClient.serverURLKey)
    }

    func testSavedBaseURLMigratesLegacyHTTPIPToHTTPS() {
        UserDefaults.standard.set("http://100.119.81.25", forKey: APIClient.serverURLKey)

        let savedURL = APIClient.savedBaseURL()

        XCTAssertEqual(savedURL?.absoluteString, "https://100.119.81.25")
        XCTAssertEqual(
            UserDefaults.standard.string(forKey: APIClient.serverURLKey),
            "https://100.119.81.25"
        )

        UserDefaults.standard.removeObject(forKey: APIClient.serverURLKey)
    }

    func testNormalizedServerURLStringAddsHTTPSForBareIP() {
        XCTAssertEqual(
            APIClient.normalizedServerURLString("100.119.81.25"),
            "https://100.119.81.25"
        )
    }

    func testPrivateHostDetectionTreatsTailscaleIPAsPrivate() {
        XCTAssertTrue(PrivateNetworkSessionDelegate.isPrivateHost("100.119.81.25"))
        XCTAssertTrue(PrivateNetworkSessionDelegate.isPrivateHost("10.0.0.20"))
        XCTAssertTrue(PrivateNetworkSessionDelegate.isPrivateHost("printfarmer.local"))
        XCTAssertFalse(PrivateNetworkSessionDelegate.isPrivateHost("example.com"))
    }

    func testLeafAnchorFallbackOnlyAllowedForSingleCertificateChain() {
        XCTAssertTrue(PrivateNetworkSessionDelegate.shouldAttemptLeafAnchorFallback(certificateCount: 1))
        XCTAssertFalse(PrivateNetworkSessionDelegate.shouldAttemptLeafAnchorFallback(certificateCount: 2))
        XCTAssertFalse(PrivateNetworkSessionDelegate.shouldAttemptLeafAnchorFallback(certificateCount: 3))
    }

    func testTransportErrorIncludesMissingChallengeDiagnostics() {
        TLSDiagnostics.beginRequest(host: "100.119.81.25")
        let error = URLError(
            .secureConnectionFailed,
            userInfo: ["_kCFStreamErrorCodeKey": -9802]
        )

        let description = NetworkError.transportError(error).errorDescription

        XCTAssertNotNil(description)
        XCTAssertTrue(description?.contains("Network error (-1200, stream -9802)") == true)
        XCTAssertTrue(description?.contains("[tls: no trust challenge observed for 100.119.81.25]") == true)
        TLSDiagnostics.clear()
    }

    func testTransportErrorIncludesChallengeDispositionDiagnostics() {
        TLSDiagnostics.beginRequest(host: "100.119.81.25")
        TLSDiagnostics.recordChallenge(
            host: "100.119.81.25",
            authenticationMethod: NSURLAuthenticationMethodServerTrust,
            disposition: "useCredential",
            trustSource: "systemTrust"
        )
        let error = URLError(
            .secureConnectionFailed,
            userInfo: ["_kCFStreamErrorCodeKey": -9802]
        )

        let description = NetworkError.transportError(error).errorDescription

        XCTAssertNotNil(description)
        XCTAssertTrue(description?.contains("Network error (-1200, stream -9802)") == true)
        XCTAssertTrue(description?.contains("host=100.119.81.25") == true)
        XCTAssertTrue(description?.contains("method=NSURLAuthenticationMethodServerTrust") == true)
        XCTAssertTrue(description?.contains("disposition=useCredential") == true)
        XCTAssertTrue(description?.contains("source=systemTrust") == true)
        TLSDiagnostics.clear()
    }

    func testTransportErrorIncludesCertificateWarningDiagnostics() {
        TLSDiagnostics.beginRequest(host: "100.119.81.25")
        TLSDiagnostics.recordChallenge(
            host: "100.119.81.25",
            authenticationMethod: NSURLAuthenticationMethodServerTrust,
            disposition: "useCredential",
            certificateWarning: "leaf cert has CA:TRUE; leaf cert missing serverAuth EKU"
        )
        let error = URLError(
            .secureConnectionFailed,
            userInfo: ["_kCFStreamErrorCodeKey": -9802]
        )

        let description = NetworkError.transportError(error).errorDescription

        XCTAssertNotNil(description)
        XCTAssertTrue(description?.contains("cert=leaf cert has CA:TRUE; leaf cert missing serverAuth EKU") == true)
        TLSDiagnostics.clear()
    }

    func testTransportErrorExpandsConnectionRefusedBeforeTLSHandshake() {
        TLSDiagnostics.beginRequest(host: "10.0.0.20")
        let error = URLError(
            .cannotConnectToHost,
            userInfo: ["_kCFStreamErrorCodeKey": 61]
        )

        let description = NetworkError.transportError(error).errorDescription

        XCTAssertNotNil(description)
        XCTAssertTrue(description?.contains("Network error (-1004, stream 61: Connection refused)") == true)
        XCTAssertTrue(description?.contains("[transport: connection was refused before the TLS handshake started]") == true)
        XCTAssertTrue(description?.contains("[tls: no trust challenge observed for 10.0.0.20]") == true)
        TLSDiagnostics.clear()
    }

    func testTransportErrorAddsCertificateUsageTrustHint() {
        TLSDiagnostics.beginRequest(host: "10.0.0.20")
        TLSDiagnostics.recordChallenge(
            host: "10.0.0.20",
            authenticationMethod: NSURLAuthenticationMethodServerTrust,
            disposition: "cancelAuthenticationChallenge",
            trustError: "\"PrintFarmer\" certificate is not permitted for this usage",
            certificateWarning: "leaf cert has CA:TRUE; leaf cert missing serverAuth EKU"
        )
        let error = URLError(.cancelled)

        let description = NetworkError.transportError(error).errorDescription

        XCTAssertNotNil(description)
        XCTAssertTrue(description?.contains("server may be presenting a CA certificate instead of a TLS server leaf") == true)
        XCTAssertTrue(description?.contains("leaf certificate may be missing serverAuth") == true)
        XCTAssertTrue(description?.contains("trust=\"PrintFarmer\" certificate is not permitted for this usage") == true)
        XCTAssertTrue(description?.contains("cert=leaf cert has CA:TRUE; leaf cert missing serverAuth EKU") == true)
        TLSDiagnostics.clear()
    }

    func testTransportErrorAddsMissingIntermediateTrustHint() {
        TLSDiagnostics.beginRequest(host: "10.0.0.20")
        TLSDiagnostics.recordChallenge(
            host: "10.0.0.20",
            authenticationMethod: NSURLAuthenticationMethodServerTrust,
            disposition: "cancelAuthenticationChallenge",
            trustError: "Trust evaluate failure: [leaf ExtendedKeyUsage MissingIntermediate]",
            certificateWarning: "leaf cert missing serverAuth EKU"
        )
        let error = URLError(.cancelled)

        let description = NetworkError.transportError(error).errorDescription

        XCTAssertNotNil(description)
        XCTAssertTrue(description?.contains("[trust-hint: leaf certificate may be missing serverAuth; TLS chain may be missing an intermediate certificate]") == true)
        XCTAssertTrue(description?.contains("trust=Trust evaluate failure: [leaf ExtendedKeyUsage MissingIntermediate]") == true)
        XCTAssertTrue(description?.contains("cert=leaf cert missing serverAuth EKU") == true)
        TLSDiagnostics.clear()
    }

    func testTLSCertificateProfileFlagsCAAndMissingServerAuth() {
        let der = Data([
            0x06, 0x03, 0x55, 0x1d, 0x13,
            0x01, 0x01, 0xff,
            0x04, 0x05, 0x30, 0x03, 0x01, 0x01, 0xff
        ])

        let warning = TLSCertificateProfile.warningSummary(der: der)

        XCTAssertEqual(warning, "leaf cert has CA:TRUE; leaf cert missing serverAuth EKU")
    }

    func testTLSCertificateProfileAcceptsServerLeafWithServerAuth() {
        let der = Data([
            0x06, 0x03, 0x55, 0x1d, 0x13,
            0x01, 0x01, 0xff,
            0x04, 0x02, 0x30, 0x00,
            0x06, 0x08, 0x2b, 0x06, 0x01, 0x05, 0x05, 0x07, 0x03, 0x01
        ])

        let warning = TLSCertificateProfile.warningSummary(der: der)

        XCTAssertNil(warning)
    }

    // MARK: - JWT Token Injection

    func testRequestIncludesAuthorizationHeader() async throws {
        let token = "test-jwt-token-123"
        await apiClient.setAuthenticatedSession(
            AuthenticatedIdentity(accessToken: token, serverID: Self.testServerID))
        mockAPIClient.stubResponse(json: TestJSON.printerArray)

        let _: [Printer] = try await apiClient.get("/api/printers")

        let captured = mockAPIClient.capturedRequests.first
        XCTAssertNotNil(captured)
        XCTAssertEqual(captured?.value(forHTTPHeaderField: "Authorization"), "Bearer \(token)")
    }

    func testRequestOmitsAuthorizationWhenNoToken() async throws {
        await apiClient.clearSession()
        mockAPIClient.stubResponse(json: TestJSON.printerArray)

        let _: [Printer] = try await apiClient.get("/api/printers")

        let captured = mockAPIClient.capturedRequests.first
        XCTAssertNil(captured?.value(forHTTPHeaderField: "Authorization"))
    }

    func testRequestIncludesAcceptHeader() async throws {
        mockAPIClient.stubResponse(json: TestJSON.printerArray)

        let _: [Printer] = try await apiClient.get("/api/printers")

        let captured = mockAPIClient.capturedRequests.first
        XCTAssertEqual(captured?.value(forHTTPHeaderField: "Accept"), "application/json")
    }

    // MARK: - Request Building (HTTP Methods)

    func testGetRequestUsesCorrectMethod() async throws {
        mockAPIClient.stubResponse(json: TestJSON.printerArray)

        let _: [Printer] = try await apiClient.get("/api/printers")

        let captured = mockAPIClient.capturedRequests.first
        XCTAssertEqual(captured?.httpMethod, "GET")
        XCTAssertTrue(captured?.url?.path.contains("/api/printers") ?? false)
    }

    func testPostRequestUsesCorrectMethodAndBody() async throws {
        mockAPIClient.stubResponse(json: TestJSON.authResponseSuccess)

        let loginRequest = LoginRequest(usernameOrEmail: "admin", password: "pass", rememberMe: true)
        let _: AuthResponse = try await apiClient.post("/api/auth/login", body: loginRequest)

        let captured = mockAPIClient.capturedRequests.first
        XCTAssertEqual(captured?.httpMethod, "POST")
        XCTAssertEqual(captured?.value(forHTTPHeaderField: "Content-Type"), "application/json")
        XCTAssertNotNil(captured?.capturedHTTPBody())
    }

    func testLoginResponseDecodesTimezoneBearingFractionalTimestamps() async throws {
        mockAPIClient.stubResponse(json: """
        {
            "success": true,
            "token": "test-token",
            "expiresAt": "2026-08-19T04:37:44.138661Z",
            "user": {
                "id": "aab2c3d4-e5f6-7890-abcd-ef1234567890",
                "username": "admin",
                "email": "admin@printfarmer.local",
                "firstName": "Admin",
                "lastName": "User",
                "isActive": true,
                "emailConfirmed": true,
                "lastLogin": "2026-08-12T04:37:44.138661Z",
                "createdAt": "2026-08-01T01:02:03.456789Z",
                "roles": ["farm_admin"],
                "permissions": []
            }
        }
        """)

        let request = LoginRequest(usernameOrEmail: "admin", password: "pass", rememberMe: true)
        let response: AuthResponse = try await apiClient.post("/api/auth/login", body: request)
        let user = try XCTUnwrap(response.user)
        let lastLogin = try XCTUnwrap(user.lastLogin)
        let expiresAt = try XCTUnwrap(response.expiresAt)

        XCTAssertEqual(
            user.createdAt.timeIntervalSince1970,
            1_785_546_123.456,
            accuracy: 0.000_001
        )
        XCTAssertEqual(
            lastLogin.timeIntervalSince1970,
            1_786_509_464.138,
            accuracy: 0.000_001
        )
        XCTAssertEqual(
            expiresAt.timeIntervalSince1970,
            1_787_114_264.138,
            accuracy: 0.000_001
        )
    }

    func testLoginResponseRejectsTimezoneLessCreatedAt() async {
        let response = TestJSON.authResponseSuccess.replacingOccurrences(
            of: #""createdAt": "2025-01-01T00:00:00Z""#,
            with: #""createdAt": "2025-01-01T00:00:00""#
        )
        mockAPIClient.stubResponse(json: response)

        do {
            let request = LoginRequest(usernameOrEmail: "admin", password: "pass", rememberMe: true)
            let _: AuthResponse = try await apiClient.post("/api/auth/login", body: request)
            XCTFail("Expected timezone-less createdAt to be rejected")
        } catch NetworkError.decodingFailed {
            // Expected.
        } catch {
            XCTFail("Expected .decodingFailed, got \(error)")
        }
    }

    func testPostVoidRequestUsesCorrectMethod() async throws {
        mockAPIClient.stubEmptySuccess()

        try await apiClient.postVoid("/api/printers/\(TestData.testUUID)/pause")

        let captured = mockAPIClient.capturedRequests.first
        XCTAssertEqual(captured?.httpMethod, "POST")
    }

    func testPutRequestUsesCorrectMethodAndBody() async throws {
        mockAPIClient.stubResponse(json: TestJSON.printer)

        let update = UpdatePrinterRequest(name: "Renamed")
        let _: Printer = try await apiClient.put("/api/printers/\(TestData.testUUID)", body: update)

        let captured = mockAPIClient.capturedRequests.first
        XCTAssertEqual(captured?.httpMethod, "PUT")
        XCTAssertEqual(captured?.value(forHTTPHeaderField: "Content-Type"), "application/json")
    }

    func testDeleteRequestUsesCorrectMethod() async throws {
        mockAPIClient.stubEmptySuccess()

        try await apiClient.delete("/api/printers/\(TestData.testUUID)")

        let captured = mockAPIClient.capturedRequests.first
        XCTAssertEqual(captured?.httpMethod, "DELETE")
    }

    // MARK: - Error Response Parsing

    func testUnauthorizedResponseThrows401() async {
        mockAPIClient.stubResponse(json: "{}", statusCode: 401)

        do {
            let _: [Printer] = try await apiClient.get("/api/printers")
            XCTFail("Expected NetworkError.unauthorized")
        } catch let error as NetworkError {
            if case .unauthorized = error {
                // Expected
            } else {
                XCTFail("Expected .unauthorized, got \(error)")
            }
        } catch {
            XCTFail("Unexpected error type: \(error)")
        }
    }

    func testForbiddenResponseThrows403() async {
        mockAPIClient.stubResponse(json: "{}", statusCode: 403)

        do {
            let _: [Printer] = try await apiClient.get("/api/printers")
            XCTFail("Expected NetworkError.forbidden")
        } catch let error as NetworkError {
            if case .forbidden = error {
                // Expected
            } else {
                XCTFail("Expected .forbidden, got \(error)")
            }
        } catch {
            XCTFail("Unexpected error type: \(error)")
        }
    }

    func testNotFoundResponseThrows404() async {
        mockAPIClient.stubResponse(json: "{}", statusCode: 404)

        do {
            let _: Printer = try await apiClient.get("/api/printers/\(TestData.testUUID)")
            XCTFail("Expected NetworkError.notFound")
        } catch let error as NetworkError {
            if case .notFound = error {
                // Expected
            } else {
                XCTFail("Expected .notFound, got \(error)")
            }
        } catch {
            XCTFail("Unexpected error type: \(error)")
        }
    }

    // MARK: - Gated 404 ProblemDetails (#728)

    /// Structured operator feature-gate 404: body carries ProblemDetails
    /// with `code: "featureDisabled"`. Callers must be able to branch on
    /// this without parsing localized text.
    func testFeatureDisabled404ThrowsFeatureDisabled() async {
        let json = """
        {
            "type": "https://printfarmer/errors/feature-disabled",
            "title": "Feature Disabled",
            "status": 404,
            "detail": "The attention feature is disabled on this server.",
            "code": "featureDisabled"
        }
        """
        mockAPIClient.stubResponse(json: json, statusCode: 404)

        do {
            let _: Printer = try await apiClient.get("/api/attention")
            XCTFail("Expected NetworkError.featureDisabled")
        } catch let error as NetworkError {
            if case .featureDisabled(let apiError) = error {
                XCTAssertEqual(apiError.code, "featureDisabled")
                XCTAssertEqual(apiError.status, 404)
                XCTAssertEqual(apiError.title, "Feature Disabled")
                XCTAssertEqual(apiError.detail, "The attention feature is disabled on this server.")
            } else {
                XCTFail("Expected .featureDisabled, got \(error)")
            }
        } catch {
            XCTFail("Unexpected error type: \(error)")
        }
    }

    /// Ordinary ProblemDetails 404 without the gate `code` must remain a
    /// plain `.notFound` so existing callers behave unchanged.
    func testOrdinaryProblemDetails404FallsBackToNotFound() async {
        let json = """
        {
            "type": "https://printfarmer/errors/not-found",
            "title": "Not Found",
            "status": 404,
            "detail": "Printer not found."
        }
        """
        mockAPIClient.stubResponse(json: json, statusCode: 404)

        do {
            let _: Printer = try await apiClient.get("/api/printers/\(TestData.testUUID)")
            XCTFail("Expected NetworkError.notFound")
        } catch let error as NetworkError {
            if case .notFound = error {
                // Expected
            } else {
                XCTFail("Expected .notFound, got \(error)")
            }
        } catch {
            XCTFail("Unexpected error type: \(error)")
        }
    }

    /// Empty 404 body (legacy server behavior) maps to `.notFound`.
    func testEmpty404BodyFallsBackToNotFound() async {
        mockAPIClient.stubResponse(json: "", statusCode: 404)

        do {
            let _: Printer = try await apiClient.get("/api/printers/\(TestData.testUUID)")
            XCTFail("Expected NetworkError.notFound")
        } catch let error as NetworkError {
            if case .notFound = error {
                // Expected
            } else {
                XCTFail("Expected .notFound, got \(error)")
            }
        } catch {
            XCTFail("Unexpected error type: \(error)")
        }
    }

    /// Malformed JSON in a 404 body must safely fall back to `.notFound`
    /// instead of surfacing a decode error.
    func testMalformed404BodyFallsBackToNotFound() async {
        mockAPIClient.stubResponse(json: "{ not valid json", statusCode: 404)

        do {
            let _: Printer = try await apiClient.get("/api/printers/\(TestData.testUUID)")
            XCTFail("Expected NetworkError.notFound")
        } catch let error as NetworkError {
            if case .notFound = error {
                // Expected
            } else {
                XCTFail("Expected .notFound, got \(error)")
            }
        } catch {
            XCTFail("Unexpected error type: \(error)")
        }
    }

    /// A ProblemDetails 404 whose `code` extension is some unrecognized
    /// value (not `"featureDisabled"`) is treated as an ordinary
    /// not-found so the gate contract stays narrow.
    func testUnknownCode404FallsBackToNotFound() async {
        let json = """
        {
            "title": "Not Found",
            "status": 404,
            "code": "someOtherCode"
        }
        """
        mockAPIClient.stubResponse(json: json, statusCode: 404)

        do {
            let _: Printer = try await apiClient.get("/api/printers/\(TestData.testUUID)")
            XCTFail("Expected NetworkError.notFound")
        } catch let error as NetworkError {
            if case .notFound = error {
                // Expected
            } else {
                XCTFail("Expected .notFound, got \(error)")
            }
        } catch {
            XCTFail("Unexpected error type: \(error)")
        }
    }

    func testServerErrorResponseThrows500() async {
        mockAPIClient.stubResponse(json: "{}", statusCode: 500)

        do {
            let _: [Printer] = try await apiClient.get("/api/printers")
            XCTFail("Expected NetworkError.serverError")
        } catch let error as NetworkError {
            if case .serverError(let code) = error {
                XCTAssertEqual(code, 500)
            } else {
                XCTFail("Expected .serverError, got \(error)")
            }
        } catch {
            XCTFail("Unexpected error type: \(error)")
        }
    }

    func testConflictResponseThrows409() async {
        mockAPIClient.stubResponse(json: "{}", statusCode: 409)

        do {
            let _: Printer = try await apiClient.get("/api/printers/\(TestData.testUUID)")
            XCTFail("Expected NetworkError.conflict")
        } catch let error as NetworkError {
            if case .conflict = error {
                // Expected
            } else {
                XCTFail("Expected .conflict, got \(error)")
            }
        } catch {
            XCTFail("Unexpected error type: \(error)")
        }
    }

    func testClientErrorParseAPIError() async {
        mockAPIClient.stubResponse(json: TestJSON.apiError, statusCode: 400)

        do {
            let _: Printer = try await apiClient.get("/api/printers/\(TestData.testUUID)")
            XCTFail("Expected NetworkError.clientError")
        } catch let error as NetworkError {
            if case .clientError(let code, let apiError) = error {
                XCTAssertEqual(code, 400)
                XCTAssertEqual(apiError?.title, "Validation Error")
                XCTAssertEqual(apiError?.detail, "The printer name is required.")
            } else {
                XCTFail("Expected .clientError, got \(error)")
            }
        } catch {
            XCTFail("Unexpected error type: \(error)")
        }
    }

    func testPreconditionFailedResponseRequiresReview() async {
        mockAPIClient.stubResponse(json: "{}", statusCode: 412)

        do {
            let _: Printer = try await apiClient.get("/api/printers/\(TestData.testUUID)")
            XCTFail("Expected NetworkError.preconditionFailed")
        } catch let error as NetworkError {
            guard case .preconditionFailed = error else {
                XCTFail("Expected .preconditionFailed, got \(error)")
                return
            }
            XCTAssertEqual(
                error.errorDescription,
                "This item changed after you reviewed it. Refresh and confirm again."
            )
        } catch {
            XCTFail("Unexpected error type: \(error)")
        }
    }

    func testPreconditionRequiredResponseRequiresRefresh() async {
        mockAPIClient.stubResponse(json: "{}", statusCode: 428)

        do {
            let _: Printer = try await apiClient.get("/api/printers/\(TestData.testUUID)")
            XCTFail("Expected NetworkError.preconditionRequired")
        } catch let error as NetworkError {
            guard case .preconditionRequired = error else {
                XCTFail("Expected .preconditionRequired, got \(error)")
                return
            }
            XCTAssertEqual(
                error.errorDescription,
                "A reviewed revision is required. Refresh and confirm again."
            )
        } catch {
            XCTFail("Unexpected error type: \(error)")
        }
    }

    func testMethodNotAllowedResponseHasActionableMessage() async {
        mockAPIClient.stubResponse(json: "{}", statusCode: 405)

        do {
            let _: SpoolmanFilament = try await apiClient.get("/api/spoolman/filaments/by-barcode?code=STALE")
            XCTFail("Expected NetworkError.methodNotAllowed")
        } catch let error as NetworkError {
            guard case .methodNotAllowed = error else {
                XCTFail("Expected .methodNotAllowed, got \(error)")
                return
            }

            XCTAssertEqual(
                error.errorDescription,
                "This action isn't supported by your PrintFarmer server (405). Update the server to the latest version."
            )
        } catch {
            XCTFail("Unexpected error type: \(error)")
        }
    }

    // MARK: - Network Errors

    func testNoConnectionThrowsNetworkError() async {
        mockAPIClient.stubError(.notConnectedToInternet)

        do {
            let _: [Printer] = try await apiClient.get("/api/printers")
            XCTFail("Expected NetworkError.noConnection")
        } catch let error as NetworkError {
            if case .noConnection = error {
                // Expected
            } else {
                XCTFail("Expected .noConnection, got \(error)")
            }
        } catch {
            XCTFail("Unexpected error type: \(error)")
        }
    }

    func testTimeoutThrowsNetworkError() async {
        mockAPIClient.stubError(.timedOut)

        do {
            let _: [Printer] = try await apiClient.get("/api/printers")
            XCTFail("Expected NetworkError.timeout")
        } catch let error as NetworkError {
            if case .timeout = error {
                // Expected
            } else {
                XCTFail("Expected .timeout, got \(error)")
            }
        } catch {
            XCTFail("Unexpected error type: \(error)")
        }
    }

    func testCannotFindHostThrowsServerUnreachable() async {
        mockAPIClient.stubError(.cannotFindHost)

        do {
            let _: [Printer] = try await apiClient.get("/api/printers")
            XCTFail("Expected NetworkError.serverUnreachable")
        } catch let error as NetworkError {
            if case .serverUnreachable = error {
                // Expected
            } else {
                XCTFail("Expected .serverUnreachable, got \(error)")
            }
        } catch {
            XCTFail("Unexpected error type: \(error)")
        }
    }

    func testCannotConnectToPrivateHTTPSHostThrowsTransportError() async {
        let privateHTTPSMockAPIClient = MockAPIClient(baseURL: URL(string: "https://10.0.0.20")!)
        let privateHTTPSClient = privateHTTPSMockAPIClient.apiClient
        privateHTTPSMockAPIClient.stubError(.cannotConnectToHost)

        do {
            let _: [Printer] = try await privateHTTPSClient.get("/api/printers")
            XCTFail("Expected NetworkError.transportError")
        } catch let error as NetworkError {
            if case .transportError(let urlError) = error {
                XCTAssertEqual(urlError.code, .cannotConnectToHost)
            } else {
                XCTFail("Expected .transportError, got \(error)")
            }
        } catch {
            XCTFail("Unexpected error type: \(error)")
        }
    }

    // MARK: - Decoding

    func testDecodingFailureThrowsDecodingError() async {
        mockAPIClient.stubResponse(json: "{ \"invalid\": true }")

        do {
            let _: Printer = try await apiClient.get("/api/printers/\(TestData.testUUID)")
            XCTFail("Expected NetworkError.decodingFailed")
        } catch let error as NetworkError {
            if case .decodingFailed = error {
                // Expected
            } else {
                XCTFail("Expected .decodingFailed, got \(error)")
            }
        } catch {
            XCTFail("Unexpected error type: \(error)")
        }
    }

    func testDecodingFailureDescriptionIncludesPathTypeAndServerVersionHint() async {
        mockAPIClient.stubResponse(
            json: """
            {
              "id": "not-an-int",
              "name": "Bad Spool",
              "material": "PLA",
              "inUse": true
            }
            """
        )

        do {
            let _: SpoolmanSpool = try await apiClient.get("/api/spoolman/spools/501")
            XCTFail("Expected NetworkError.decodingFailed")
        } catch let error as NetworkError {
            guard case .decodingFailed = error else {
                XCTFail("Expected .decodingFailed, got \(error)")
                return
            }

            let description = error.errorDescription ?? ""
            XCTAssertTrue(description.contains("Failed to decode response for SpoolmanSpool"))
            XCTAssertTrue(description.contains("typeMismatch"))
            XCTAssertTrue(description.contains("id"))
            XCTAssertTrue(description.contains("expected Int"))
            XCTAssertTrue(description.contains("server version may be incompatible"))
            XCTAssertTrue(description.contains("update the server"))
        } catch {
            XCTFail("Unexpected error type: \(error)")
        }
    }
    
    // MARK: - Empty Response Handling
    
    func testEmptyResponseWithOptionalTypeReturnsNil() async throws {
        mockAPIClient.stubEmptySuccess()
        
        let result: Printer? = try await apiClient.get("/api/printers/\(TestData.testUUID)")
        
        XCTAssertNil(result, "Empty response should return nil for Optional type")
    }
    
    func testEmptyResponseWithNonOptionalTypeThrows() async {
        mockAPIClient.stubEmptySuccess()
        
        do {
            let _: Printer = try await apiClient.get("/api/printers/\(TestData.testUUID)")
            XCTFail("Expected NetworkError.decodingFailed for non-optional type with empty body")
        } catch let error as NetworkError {
            if case .decodingFailed = error {
                // Expected
            } else {
                XCTFail("Expected .decodingFailed, got \(error)")
            }
        } catch {
            XCTFail("Unexpected error type: \(error)")
        }
    }
    
    func testEmptyResponseWith204StatusReturnsNilForOptional() async throws {
        mockAPIClient.requestHandler = { request in
            let response = TestData.httpResponse(url: request.url, statusCode: 204)
            return (response, Data())
        }
        
        let result: Printer? = try await apiClient.get("/api/printers/\(TestData.testUUID)")
        
        XCTAssertNil(result, "204 No Content should return nil for Optional type")
    }
    
    func testEmptyResponseWithOptionalArrayReturnsNil() async throws {
        mockAPIClient.stubEmptySuccess()
        
        let result: [Printer]? = try await apiClient.get("/api/printers")
        
        XCTAssertNil(result, "Empty response should return nil for Optional array type")
    }
    
    func testNonEmptyResponseWithOptionalTypeDecodesProperly() async throws {
        mockAPIClient.stubResponse(json: TestJSON.printer)
        
        let result: Printer? = try await apiClient.get("/api/printers/\(TestData.testUUID)")
        
        XCTAssertNotNil(result, "Non-empty response should decode the value")
        XCTAssertEqual(result?.name, "Prusa MK4")
    }

    func testMockURLProtocolHandlersAreIsolatedPerSession_underDeterministicOverlap() async throws {
        let firstURL = URL(string: "https://mock.invalid/first")!
        let secondURL = URL(string: "https://mock.invalid/second")!
        let firstSession = MockURLProtocol.makeSession()
        let secondSession = MockURLProtocol.makeSession()

        // Explicit two-phase barrier: each handler signals `entryLatch` when
        // it is inside `startLoading`, then blocks on `releaseLatch`. The
        // test drains two entry signals — proving both handlers were live
        // concurrently — before it releases either. Handler waits use a
        // 10-second timeout so that if URLProtocol serialization ever
        // starves one handler, the test fails fast with an explicit
        // diagnostic instead of hanging XCTest. There is no `Task.sleep`,
        // yield, polling, elapsed-time correctness, or ignored timeout.
        //
        // Against the rejected process-global-handler implementation this
        // test fails deterministically: setting `secondSession.requestHandler`
        // overwrites the shared handler, so both URL requests would invoke
        // the second handler and both bodies would decode to
        // `"second-handler"`.
        let entryLatch = DispatchSemaphore(value: 0)
        let releaseLatch = DispatchSemaphore(value: 0)
        let barrierTimeout: DispatchTime = .now() + .seconds(10)

        firstSession.requestHandler = { request in
            entryLatch.signal()
            if releaseLatch.wait(timeout: barrierTimeout) == .timedOut {
                throw URLError(.timedOut)
            }
            let response = TestData.httpResponse(url: request.url, statusCode: 200)
            return (response, Data("first-handler".utf8))
        }

        secondSession.requestHandler = { request in
            entryLatch.signal()
            if releaseLatch.wait(timeout: barrierTimeout) == .timedOut {
                throw URLError(.timedOut)
            }
            let response = TestData.httpResponse(url: request.url, statusCode: 200)
            return (response, Data("second-handler".utf8))
        }

        async let firstData = firstSession.urlSession.data(from: firstURL).0
        async let secondData = secondSession.urlSession.data(from: secondURL).0

        // Drain two entry signals on a background dispatch thread so the
        // Swift concurrency executor is never blocked. Both handlers must
        // be simultaneously parked on `releaseLatch` before either returns.
        let bothEntered: Bool = await withCheckedContinuation { (cont: CheckedContinuation<Bool, Never>) in
            DispatchQueue.global(qos: .userInitiated).async {
                let firstEntered = entryLatch.wait(timeout: barrierTimeout) != .timedOut
                let secondEntered = firstEntered
                    ? entryLatch.wait(timeout: barrierTimeout) != .timedOut
                    : false
                cont.resume(returning: firstEntered && secondEntered)
            }
        }
        XCTAssertTrue(
            bothEntered,
            "Both per-session MockURLProtocol handlers must be simultaneously live inside startLoading before release. If this fails, sessions are sharing handler dispatch and the per-session isolation is broken."
        )

        // Both handlers are provably parked; release them.
        releaseLatch.signal()
        releaseLatch.signal()

        let firstBody = String(decoding: try await firstData, as: UTF8.self)
        let secondBody = String(decoding: try await secondData, as: UTF8.self)

        XCTAssertEqual(firstBody, "first-handler")
        XCTAssertEqual(secondBody, "second-handler")
        XCTAssertEqual(firstSession.capturedRequests.count, 1)
        XCTAssertEqual(firstSession.capturedRequests.first?.url, firstURL)
        XCTAssertEqual(secondSession.capturedRequests.count, 1)
        XCTAssertEqual(secondSession.capturedRequests.first?.url, secondURL)
    }

    func testConcurrentMockAPIClientsKeepResponsesAndCapturesIsolated_underDeterministicOverlap() async throws {
        let firstMockAPIClient = MockAPIClient(baseURL: URL(string: "https://first.mock.invalid")!)
        let secondMockAPIClient = MockAPIClient(baseURL: URL(string: "https://second.mock.invalid")!)

        // Same barrier discipline as the raw-URLSession isolation test, but
        // exercising real `APIClient` actors and their captured requests.
        // Overlap is enforced by the semaphore protocol; correctness never
        // depends on scheduling luck. Handler waits use a bounded timeout
        // so a starved handler produces an explicit diagnostic instead of
        // hanging XCTest. Against the rejected global-handler
        // implementation this test fails deterministically because both
        // APIClients would receive the second stub's payload.
        let entryLatch = DispatchSemaphore(value: 0)
        let releaseLatch = DispatchSemaphore(value: 0)
        let barrierTimeout: DispatchTime = .now() + .seconds(10)

        firstMockAPIClient.requestHandler = { request in
            entryLatch.signal()
            if releaseLatch.wait(timeout: barrierTimeout) == .timedOut {
                throw URLError(.timedOut)
            }
            let response = TestData.httpResponse(url: request.url, statusCode: 200)
            return (response, Data(#"{"value":"first"}"#.utf8))
        }

        secondMockAPIClient.requestHandler = { request in
            entryLatch.signal()
            if releaseLatch.wait(timeout: barrierTimeout) == .timedOut {
                throw URLError(.timedOut)
            }
            let response = TestData.httpResponse(url: request.url, statusCode: 200)
            return (response, Data(#"{"value":"second"}"#.utf8))
        }

        async let firstValue: TransportValue = firstMockAPIClient.apiClient.get("/value")
        async let secondValue: TransportValue = secondMockAPIClient.apiClient.get("/value")

        let bothEntered: Bool = await withCheckedContinuation { (cont: CheckedContinuation<Bool, Never>) in
            DispatchQueue.global(qos: .userInitiated).async {
                let firstEntered = entryLatch.wait(timeout: barrierTimeout) != .timedOut
                let secondEntered = firstEntered
                    ? entryLatch.wait(timeout: barrierTimeout) != .timedOut
                    : false
                cont.resume(returning: firstEntered && secondEntered)
            }
        }
        XCTAssertTrue(
            bothEntered,
            "Both APIClient actors must have concurrent MockURLProtocol handlers live inside startLoading before release. If this fails, per-session handler dispatch is serialized and the isolation contract is broken."
        )

        releaseLatch.signal()
        releaseLatch.signal()

        let firstResolved = try await firstValue
        let secondResolved = try await secondValue

        XCTAssertEqual(firstResolved, TransportValue(value: "first"))
        XCTAssertEqual(secondResolved, TransportValue(value: "second"))
        XCTAssertEqual(firstMockAPIClient.capturedRequests.count, 1)
        XCTAssertEqual(firstMockAPIClient.capturedRequests.first?.url?.host, "first.mock.invalid")
        XCTAssertEqual(secondMockAPIClient.capturedRequests.count, 1)
        XCTAssertEqual(secondMockAPIClient.capturedRequests.first?.url?.host, "second.mock.invalid")
    }

    // MARK: - Issue #812: session ordering & cancellation regressions

    /// Same-session handlers must run FIFO on one serial queue: request #1 fully
    /// completes before request #2 begins. Against the rejected concurrent
    /// global-queue dispatch, #2 would start while #1 is still parked, so
    /// "start2" would appear before "end1" and this exact-sequence assertion
    /// fails. Bounded semaphore waits only fail fast; correctness never depends
    /// on elapsed time, sleeps, polling, or retries.
    func testSameSessionHandlersRunSeriallyInFIFOOrder() async throws {
        let session = MockURLProtocol.makeSession()
        let firstURL = URL(string: "https://mock.invalid/serial-1")!
        let secondURL = URL(string: "https://mock.invalid/serial-2")!

        let events = OrderedEventLog()
        let firstEntered = DispatchSemaphore(value: 0)
        let firstMayFinish = DispatchSemaphore(value: 0)
        let completions = CompletionLog()
        let barrierTimeout: DispatchTime = .now() + .seconds(10)

        session.onCompletion = { delivered in completions.record(delivered: delivered) }

        session.requestHandler = { request in
            if request.url == firstURL {
                events.append("start1")
                firstEntered.signal()
                if firstMayFinish.wait(timeout: barrierTimeout) == .timedOut {
                    throw URLError(.timedOut)
                }
                events.append("end1")
                return (TestData.httpResponse(url: request.url, statusCode: 200), Data("1".utf8))
            } else {
                events.append("start2")
                events.append("end2")
                return (TestData.httpResponse(url: request.url, statusCode: 200), Data("2".utf8))
            }
        }

        async let firstData: Data = session.urlSession.data(from: firstURL).0

        // Wait until request #1 is provably running inside the serial queue.
        let entered: Bool = await withCheckedContinuation { (cont: CheckedContinuation<Bool, Never>) in
            DispatchQueue.global(qos: .userInitiated).async {
                cont.resume(returning: firstEntered.wait(timeout: barrierTimeout) != .timedOut)
            }
        }
        XCTAssertTrue(entered, "Request #1 handler must enter the serial queue before #2 is submitted.")

        // Submit #2 while #1 holds the serial queue, then release #1.
        async let secondData: Data = session.urlSession.data(from: secondURL).0
        firstMayFinish.signal()

        let firstBody = String(decoding: try await firstData, as: UTF8.self)
        let secondBody = String(decoding: try await secondData, as: UTF8.self)

        XCTAssertEqual(firstBody, "1")
        XCTAssertEqual(secondBody, "2")
        XCTAssertEqual(
            events.snapshot(),
            ["start1", "end1", "start2", "end2"],
            "Same-session handlers must execute serially in FIFO order. Interleaving (e.g. start2 before end1) means the per-session queue is not serial."
        )
        XCTAssertEqual(completions.deliveredCount(), 2, "Each request must deliver its terminal callback exactly once.")
        XCTAssertEqual(completions.suppressedCount(), 0)
    }

    /// Cancelling a request after its handler has entered `startLoading` but
    /// before it releases must suppress the terminal callback: the fence set by
    /// `stopLoading()` drops every later `URLProtocolClient` callback. The
    /// `onStopLoading` seam orders the stop strictly before the release, so the
    /// suppression is deterministic. Against the rejected unfenced dispatch the
    /// terminal callback would be delivered after cancellation instead of
    /// suppressed, so `deliveredCount == 0` / `suppressedCount == 1` fails.
    func testStopLoadingSuppressesCompletionAfterHandlerEntry() async throws {
        let session = MockURLProtocol.makeSession()
        let url = URL(string: "https://mock.invalid/cancel")!

        let entryLatch = DispatchSemaphore(value: 0)
        let releaseLatch = DispatchSemaphore(value: 0)
        let stopLatch = DispatchSemaphore(value: 0)
        let completions = CompletionLog()
        let barrierTimeout: DispatchTime = .now() + .seconds(10)

        session.onStopLoading = { stopLatch.signal() }
        session.onCompletion = { delivered in completions.record(delivered: delivered) }

        session.requestHandler = { request in
            entryLatch.signal()
            if releaseLatch.wait(timeout: barrierTimeout) == .timedOut {
                throw URLError(.timedOut)
            }
            return (TestData.httpResponse(url: request.url, statusCode: 200), Data("late".utf8))
        }

        let task = Task { () -> Data in
            try await session.urlSession.data(from: url).0
        }

        // Handler is provably parked inside startLoading.
        let entered: Bool = await withCheckedContinuation { (cont: CheckedContinuation<Bool, Never>) in
            DispatchQueue.global(qos: .userInitiated).async {
                cont.resume(returning: entryLatch.wait(timeout: barrierTimeout) != .timedOut)
            }
        }
        XCTAssertTrue(entered, "Handler must enter startLoading before cancellation.")

        // Cancel while the handler is parked, then wait until stopLoading fences.
        task.cancel()
        let fenced: Bool = await withCheckedContinuation { (cont: CheckedContinuation<Bool, Never>) in
            DispatchQueue.global(qos: .userInitiated).async {
                cont.resume(returning: stopLatch.wait(timeout: barrierTimeout) != .timedOut)
            }
        }
        XCTAssertTrue(fenced, "stopLoading() must fence the load after cancellation.")

        // Release the parked handler; its terminal callback must be suppressed.
        releaseLatch.signal()

        do {
            let data = try await task.value
            XCTFail("Cancelled request must not deliver a completed response; got \(String(decoding: data, as: UTF8.self)).")
        } catch {
            // Expected: cancellation propagates.
        }

        // The handler's terminal callback must reach the fence and be suppressed.
        let sawTerminal: Bool = await withCheckedContinuation { (cont: CheckedContinuation<Bool, Never>) in
            DispatchQueue.global(qos: .userInitiated).async {
                cont.resume(returning: completions.terminalLatch.wait(timeout: barrierTimeout) != .timedOut)
            }
        }
        XCTAssertTrue(sawTerminal, "The handler's terminal callback must reach the lifecycle fence.")
        XCTAssertEqual(completions.deliveredCount(), 0, "stopLoading() must suppress the terminal callback after cancellation.")
        XCTAssertEqual(completions.suppressedCount(), 1, "Exactly one terminal callback must be fenced/suppressed.")
        XCTAssertEqual(session.capturedRequests.count, 1)
    }

    func testLiveHTTPSLoginAgainstRealServer() async throws {
        let environment = ProcessInfo.processInfo.environment
        guard let rawURL = environment["PFARM_LIVE_LOGIN_URL"],
              let url = URL(string: rawURL),
              let username = environment["PFARM_LIVE_LOGIN_USERNAME"],
              let password = environment["PFARM_LIVE_LOGIN_PASSWORD"] else {
            throw XCTSkip("Set PFARM_LIVE_LOGIN_URL, PFARM_LIVE_LOGIN_USERNAME, and PFARM_LIVE_LOGIN_PASSWORD to run this diagnostic test.")
        }

        let liveClient = APIClient(baseURL: url)
        let request = LoginRequest(
            usernameOrEmail: username,
            password: password,
            rememberMe: true
        )

        let response: AuthResponse = try await liveClient.post("/api/auth/login", body: request)

        XCTAssertTrue(response.success)
        XCTAssertNotNil(response.token)
        XCTAssertEqual(response.user?.username, username)
    }
}

// MARK: - Issue #812 regression support (thread-safe recorders)

/// Ordered, synchronized event recorder used to assert same-session FIFO
/// serialization from inside sync `MockURLProtocol` handlers running on the
/// per-session serial queue.
private final class OrderedEventLog: @unchecked Sendable {
    private let lock = NSLock()
    private var events: [String] = []

    func append(_ event: String) {
        lock.lock()
        events.append(event)
        lock.unlock()
    }

    func snapshot() -> [String] {
        lock.lock()
        defer { lock.unlock() }
        return events
    }
}

/// Records the delivered/suppressed outcome of each terminal `MockURLProtocol`
/// callback via the session's `onCompletion` seam. `terminalLatch` lets a test
/// causally await the fenced terminal decision with a fail-fast timeout.
private final class CompletionLog: @unchecked Sendable {
    private let lock = NSLock()
    private var delivered = 0
    private var suppressed = 0
    let terminalLatch = DispatchSemaphore(value: 0)

    func record(delivered isDelivered: Bool) {
        lock.lock()
        if isDelivered { delivered += 1 } else { suppressed += 1 }
        lock.unlock()
        terminalLatch.signal()
    }

    func deliveredCount() -> Int {
        lock.lock()
        defer { lock.unlock() }
        return delivered
    }

    func suppressedCount() -> Int {
        lock.lock()
        defer { lock.unlock() }
        return suppressed
    }
}
