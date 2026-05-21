import XCTest
@testable import PrintFarmer

/// Tests for the APIClient actor: request building, token injection,
/// error mapping, and base URL configuration.
final class APIClientTests: XCTestCase {

    private var apiClient: APIClient!

    override func setUp() {
        super.setUp()
        MockURLProtocol.reset()
        apiClient = MockAPIClient.makeAPIClient()
    }

    override func tearDown() {
        MockURLProtocol.reset()
        apiClient = nil
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

    func testIPv4HostsSkipStrictHostnameValidation() {
        XCTAssertTrue(PrivateNetworkSessionDelegate.isIPv4Address("100.119.81.25"))
        XCTAssertFalse(PrivateNetworkSessionDelegate.isIPv4Address("printfarmer.local"))
        XCTAssertFalse(PrivateNetworkSessionDelegate.isIPv4Address("example.com"))
        XCTAssertFalse(PrivateNetworkSessionDelegate.isIPv4Address("localhost"))
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
        await apiClient.setAccessToken(token)
        MockAPIClient.stubResponse(json: TestJSON.printerArray)

        let _: [Printer] = try await apiClient.get("/api/printers")

        let captured = MockURLProtocol.capturedRequests.first
        XCTAssertNotNil(captured)
        XCTAssertEqual(captured?.value(forHTTPHeaderField: "Authorization"), "Bearer \(token)")
    }

    func testRequestOmitsAuthorizationWhenNoToken() async throws {
        await apiClient.setAccessToken(nil)
        MockAPIClient.stubResponse(json: TestJSON.printerArray)

        let _: [Printer] = try await apiClient.get("/api/printers")

        let captured = MockURLProtocol.capturedRequests.first
        XCTAssertNil(captured?.value(forHTTPHeaderField: "Authorization"))
    }

    func testRequestIncludesAcceptHeader() async throws {
        MockAPIClient.stubResponse(json: TestJSON.printerArray)

        let _: [Printer] = try await apiClient.get("/api/printers")

        let captured = MockURLProtocol.capturedRequests.first
        XCTAssertEqual(captured?.value(forHTTPHeaderField: "Accept"), "application/json")
    }

    // MARK: - Request Building (HTTP Methods)

    func testGetRequestUsesCorrectMethod() async throws {
        MockAPIClient.stubResponse(json: TestJSON.printerArray)

        let _: [Printer] = try await apiClient.get("/api/printers")

        let captured = MockURLProtocol.capturedRequests.first
        XCTAssertEqual(captured?.httpMethod, "GET")
        XCTAssertTrue(captured?.url?.path.contains("/api/printers") ?? false)
    }

    func testPostRequestUsesCorrectMethodAndBody() async throws {
        MockAPIClient.stubResponse(json: TestJSON.authResponseSuccess)

        let loginRequest = LoginRequest(usernameOrEmail: "admin", password: "pass", rememberMe: true)
        let _: AuthResponse = try await apiClient.post("/api/auth/login", body: loginRequest)

        let captured = MockURLProtocol.capturedRequests.first
        XCTAssertEqual(captured?.httpMethod, "POST")
        XCTAssertEqual(captured?.value(forHTTPHeaderField: "Content-Type"), "application/json")
        XCTAssertNotNil(captured?.capturedHTTPBody())
    }

    func testPostVoidRequestUsesCorrectMethod() async throws {
        MockAPIClient.stubEmptySuccess()

        try await apiClient.postVoid("/api/printers/\(TestData.testUUID)/pause")

        let captured = MockURLProtocol.capturedRequests.first
        XCTAssertEqual(captured?.httpMethod, "POST")
    }

    func testPutRequestUsesCorrectMethodAndBody() async throws {
        MockAPIClient.stubResponse(json: TestJSON.printer)

        let update = UpdatePrinterRequest(name: "Renamed")
        let _: Printer = try await apiClient.put("/api/printers/\(TestData.testUUID)", body: update)

        let captured = MockURLProtocol.capturedRequests.first
        XCTAssertEqual(captured?.httpMethod, "PUT")
        XCTAssertEqual(captured?.value(forHTTPHeaderField: "Content-Type"), "application/json")
    }

    func testDeleteRequestUsesCorrectMethod() async throws {
        MockAPIClient.stubEmptySuccess()

        try await apiClient.delete("/api/printers/\(TestData.testUUID)")

        let captured = MockURLProtocol.capturedRequests.first
        XCTAssertEqual(captured?.httpMethod, "DELETE")
    }

    // MARK: - Error Response Parsing

    func testUnauthorizedResponseThrows401() async {
        MockAPIClient.stubResponse(json: "{}", statusCode: 401)

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
        MockAPIClient.stubResponse(json: "{}", statusCode: 403)

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
        MockAPIClient.stubResponse(json: "{}", statusCode: 404)

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
        MockAPIClient.stubResponse(json: "{}", statusCode: 500)

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
        MockAPIClient.stubResponse(json: "{}", statusCode: 409)

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
        MockAPIClient.stubResponse(json: TestJSON.apiError, statusCode: 400)

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

    // MARK: - Network Errors

    func testNoConnectionThrowsNetworkError() async {
        MockAPIClient.stubError(.notConnectedToInternet)

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
        MockAPIClient.stubError(.timedOut)

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
        MockAPIClient.stubError(.cannotFindHost)

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
        let privateHTTPSClient = MockAPIClient.makeAPIClient(baseURL: URL(string: "https://10.0.0.20")!)
        MockAPIClient.stubError(.cannotConnectToHost)

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
        MockAPIClient.stubResponse(json: "{ \"invalid\": true }")

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
    
    // MARK: - Empty Response Handling
    
    func testEmptyResponseWithOptionalTypeReturnsNil() async throws {
        MockAPIClient.stubEmptySuccess()
        
        let result: Printer? = try await apiClient.get("/api/printers/\(TestData.testUUID)")
        
        XCTAssertNil(result, "Empty response should return nil for Optional type")
    }
    
    func testEmptyResponseWithNonOptionalTypeThrows() async {
        MockAPIClient.stubEmptySuccess()
        
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
        MockURLProtocol.requestHandler = { request in
            let response = TestData.httpResponse(url: request.url, statusCode: 204)
            return (response, Data())
        }
        
        let result: Printer? = try await apiClient.get("/api/printers/\(TestData.testUUID)")
        
        XCTAssertNil(result, "204 No Content should return nil for Optional type")
    }
    
    func testEmptyResponseWithOptionalArrayReturnsNil() async throws {
        MockAPIClient.stubEmptySuccess()
        
        let result: [Printer]? = try await apiClient.get("/api/printers")
        
        XCTAssertNil(result, "Empty response should return nil for Optional array type")
    }
    
    func testNonEmptyResponseWithOptionalTypeDecodesProperly() async throws {
        MockAPIClient.stubResponse(json: TestJSON.printer)
        
        let result: Printer? = try await apiClient.get("/api/printers/\(TestData.testUUID)")
        
        XCTAssertNotNil(result, "Non-empty response should decode the value")
        XCTAssertEqual(result?.name, "Prusa MK4")
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
