import XCTest
@testable import PrintFarmer

/// Endpoint-shape tests for the fallback-group CRUD methods added to
/// `PrinterService` for issue #711 (F6). Verifies URLs, HTTP methods,
/// request bodies, `getAvailableFallback` 204 handling, and propagation
/// of `NetworkError.featureDisabled` / `.forbidden`.
final class PrinterServiceFallbackTests: XCTestCase {

    private var mockAPIClient: MockAPIClient!
    private var apiClient: APIClient!
    private var printerService: PrinterService!

    private let printerId = TestData.testUUID
    private let groupId = TestData.testUUID2
    private let toolheadId = TestData.testUUID3

    override func setUp() {
        super.setUp()
        mockAPIClient = MockAPIClient()
        apiClient = mockAPIClient.apiClient
        printerService = PrinterService(apiClient: apiClient)
    }

    override func tearDown() {
        apiClient = nil
        printerService = nil
        mockAPIClient = nil
        super.tearDown()
    }

    // MARK: - getDetails

    func testGetDetails_callsCorrectEndpoint() async throws {
        let json = """
        {
          "id": "\(printerId.uuidString)",
          "name": "MK4",
          "backend": "PrusaLink",
          "supportsPerToolAttribution": true,
          "toolheads": [],
          "fallbackGroups": []
        }
        """
        mockAPIClient.stubResponse(json: json)

        let details = try await printerService.getDetails(id: printerId)

        XCTAssertEqual(details.id, printerId)
        XCTAssertTrue(details.supportsPerToolAttribution)
        let captured = mockAPIClient.capturedRequests.first
        XCTAssertEqual(captured?.httpMethod, "GET")
        XCTAssertTrue(captured?.url?.path.contains("/api/printers/\(printerId.uuidString)/details") ?? false)
    }

    // MARK: - listFallbackGroups

    func testListFallbackGroups_callsCorrectEndpoint() async throws {
        mockAPIClient.stubResponse(json: "[]")

        let groups = try await printerService.listFallbackGroups(printerId: printerId)

        XCTAssertTrue(groups.isEmpty)
        let captured = mockAPIClient.capturedRequests.first
        XCTAssertEqual(captured?.httpMethod, "GET")
        XCTAssertTrue(captured?.url?.path.contains("/api/printers/\(printerId.uuidString)/fallback-groups") ?? false)
    }

    func testListFallbackGroups_propagatesFeatureDisabled404() async {
        // Simulate operator feature gate: 404 with ProblemDetails `code`.
        let body = """
        {"code":"featureDisabled","title":"Not Found","status":404}
        """
        mockAPIClient.requestHandler = { req in
            let response = TestData.httpResponse(url: req.url, statusCode: 404)
            return (response, Data(body.utf8))
        }
        do {
            _ = try await printerService.listFallbackGroups(printerId: printerId)
            XCTFail("Expected featureDisabled")
        } catch let error as NetworkError {
            if case .featureDisabled(let apiError) = error {
                XCTAssertEqual(apiError.code, "featureDisabled")
            } else {
                XCTFail("Expected .featureDisabled, got \(error)")
            }
        } catch {
            XCTFail("Unexpected error: \(error)")
        }
    }

    // MARK: - getFallbackGroup

    func testGetFallbackGroup_callsCorrectEndpoint() async throws {
        let json = """
        {
          "id":"\(groupId.uuidString)","printerId":"\(printerId.uuidString)","name":"g","materialType":"PLA","displayOrder":0,
          "createdAt":"2025-01-01T00:00:00Z","updatedAt":"2025-01-01T00:00:00Z","members":[]
        }
        """
        mockAPIClient.stubResponse(json: json)

        let group = try await printerService.getFallbackGroup(printerId: printerId, groupId: groupId)

        XCTAssertEqual(group.id, groupId)
        let captured = mockAPIClient.capturedRequests.first
        XCTAssertEqual(captured?.httpMethod, "GET")
        XCTAssertTrue(captured?.url?.path.contains("/api/printers/\(printerId.uuidString)/fallback-groups/\(groupId.uuidString)") ?? false)
    }

    // MARK: - createFallbackGroup

    func testCreateFallbackGroup_postsCamelCaseBody() async throws {
        let json = """
        {
          "id":"\(groupId.uuidString)","printerId":"\(printerId.uuidString)","name":"pla","materialType":"PLA","displayOrder":0,
          "createdAt":"2025-01-01T00:00:00Z","updatedAt":"2025-01-01T00:00:00Z","members":[]
        }
        """
        mockAPIClient.stubResponse(json: json, statusCode: 201)

        let request = CreateFilamentFallbackGroupRequest(
            name: "pla",
            materialType: "PLA",
            displayOrder: 1,
            toolheadIds: [toolheadId]
        )
        _ = try await printerService.createFallbackGroup(printerId: printerId, request)

        let captured = mockAPIClient.capturedRequests.first
        XCTAssertEqual(captured?.httpMethod, "POST")
        XCTAssertTrue(captured?.url?.path.contains("/api/printers/\(printerId.uuidString)/fallback-groups") ?? false)
        let body = mockAPIClient.capturedRequests.first?.capturedHTTPBody() ?? Data()
        let json2 = try JSONSerialization.jsonObject(with: body) as? [String: Any]
        XCTAssertEqual(json2?["name"] as? String, "pla")
        XCTAssertEqual(json2?["materialType"] as? String, "PLA")
        XCTAssertEqual(json2?["displayOrder"] as? Int, 1)
        XCTAssertEqual((json2?["toolheadIds"] as? [String])?.count, 1)
    }

    func testCreateFallbackGroup_forbiddenPropagatesAsForbidden() async {
        mockAPIClient.requestHandler = { req in
            let response = TestData.httpResponse(url: req.url, statusCode: 403)
            return (response, Data())
        }
        do {
            let request = CreateFilamentFallbackGroupRequest(
                name: "x", materialType: "PLA", displayOrder: nil, toolheadIds: [toolheadId]
            )
            _ = try await printerService.createFallbackGroup(printerId: printerId, request)
            XCTFail("Expected forbidden")
        } catch let error as NetworkError {
            if case .forbidden = error { } else {
                XCTFail("Expected .forbidden, got \(error)")
            }
        } catch {
            XCTFail("Unexpected error: \(error)")
        }
    }

    // MARK: - updateFallbackGroup

    func testUpdateFallbackGroup_usesPUT() async throws {
        let json = """
        {
          "id":"\(groupId.uuidString)","printerId":"\(printerId.uuidString)","name":"n","materialType":"PLA","displayOrder":0,
          "createdAt":"2025-01-01T00:00:00Z","updatedAt":"2025-01-01T00:00:00Z","members":[]
        }
        """
        mockAPIClient.stubResponse(json: json)
        let request = UpdateFilamentFallbackGroupRequest(
            name: "n", materialType: "PLA", displayOrder: 0, toolheadIds: [toolheadId]
        )
        _ = try await printerService.updateFallbackGroup(printerId: printerId, groupId: groupId, request)

        let captured = mockAPIClient.capturedRequests.first
        XCTAssertEqual(captured?.httpMethod, "PUT")
        XCTAssertTrue(captured?.url?.path.contains("/api/printers/\(printerId.uuidString)/fallback-groups/\(groupId.uuidString)") ?? false)
    }

    // MARK: - deleteFallbackGroup

    func testDeleteFallbackGroup_usesDELETE() async throws {
        mockAPIClient.stubEmptySuccess()
        try await printerService.deleteFallbackGroup(printerId: printerId, groupId: groupId)

        let captured = mockAPIClient.capturedRequests.first
        XCTAssertEqual(captured?.httpMethod, "DELETE")
        XCTAssertTrue(captured?.url?.path.contains("/api/printers/\(printerId.uuidString)/fallback-groups/\(groupId.uuidString)") ?? false)
    }

    // MARK: - getAvailableFallback

    func testGetAvailableFallback_encodesQueryParams() async throws {
        let json = """
        {"groupId":"\(groupId.uuidString)","memberId":"\(TestData.testUUID3.uuidString)","toolheadId":"\(toolheadId.uuidString)","position":1,"loadedMaterial":"PLA","loadedSpoolId":null}
        """
        mockAPIClient.stubResponse(json: json)

        let member = try await printerService.getAvailableFallback(
            printerId: printerId,
            sourceToolheadId: toolheadId,
            material: "PLA-CF"
        )

        XCTAssertNotNil(member)
        XCTAssertEqual(member?.loadedMaterial, "PLA")
        let captured = mockAPIClient.capturedRequests.first
        XCTAssertEqual(captured?.httpMethod, "GET")
        let url = captured?.url?.absoluteString ?? ""
        XCTAssertTrue(url.contains("fallback-groups/available"))
        XCTAssertTrue(url.contains("sourceToolheadId=\(toolheadId.uuidString)"))
        // "PLA-CF" contains a hyphen (allowed in a URL query) so it survives
        // either raw or percent-encoded; the important thing is that the
        // material argument is present and unambiguous.
        XCTAssertTrue(url.contains("material=PLA-CF") || url.contains("material=PLA%2DCF"))
    }

    func testGetAvailableFallback_204ReturnsNil() async throws {
        // Emit a literal 204 (not 200 + empty body) so this test actually
        // exercises the "204 → nil" branch of the Optional-decoding path.
        mockAPIClient.requestHandler = { request in
            let response = TestData.httpResponse(url: request.url, statusCode: 204)
            return (response, Data())
        }

        let member = try await printerService.getAvailableFallback(
            printerId: printerId,
            sourceToolheadId: toolheadId,
            material: "PLA"
        )

        XCTAssertNil(member)
    }

    // MARK: - getAvailableFallback: reserved-character encoding (adversarial)
    //
    // Regression coverage for the RFC 3986 fix: sub-delimiters `& = + ? #`
    // in a free-text `material` must not leak into the query as separators
    // or (in the case of `+`) decode server-side as a space. The three
    // tests below drive real `PrinterService` → `APIClient` → URL
    // construction, parse the emitted URL with `URLComponents`, and
    // assert the server-visible `material` round-trips exactly and no
    // extra query item appears.

    private func stubOKAndCaptureURL() {
        let json = """
        {"groupId":"\(groupId.uuidString)","memberId":"\(TestData.testUUID3.uuidString)","toolheadId":"\(toolheadId.uuidString)","position":1,"loadedMaterial":"PLA","loadedSpoolId":null}
        """
        mockAPIClient.stubResponse(json: json)
    }

    /// Parses the single captured request URL and returns
    /// `(sourceToolheadId, material, allNames)` from its query, treating
    /// values as percent-decoded strings. Returns `nil` if the capture is
    /// missing or malformed so the caller can fail loudly.
    private func decodedQueryOfCapturedRequest() -> (sourceToolheadId: String?, material: String?, names: [String])? {
        guard let url = mockAPIClient.capturedRequests.first?.url,
              let components = URLComponents(url: url, resolvingAgainstBaseURL: false) else {
            return nil
        }
        // `URLComponents.queryItems.value` returns the percent-decoded
        // string, so this is exactly what a well-behaved server (or
        // ASP.NET Core's model binder) would see.
        let items = components.queryItems ?? []
        let sourceToolheadId = items.first(where: { $0.name == "sourceToolheadId" })?.value
        let material = items.first(where: { $0.name == "material" })?.value
        return (sourceToolheadId, material, items.map(\.name))
    }

    func testGetAvailableFallback_ampersandAndEquals_doNotInjectExtraParam() async throws {
        stubOKAndCaptureURL()

        _ = try await printerService.getAvailableFallback(
            printerId: printerId,
            sourceToolheadId: toolheadId,
            material: "PLA&x=1"
        )

        // Raw wire form: `&` MUST be `%26` and `=` MUST be `%3D`, otherwise
        // the server would parse an injected `x=1` parameter.
        let rawURL = mockAPIClient.capturedRequests.first?.url?.absoluteString ?? ""
        XCTAssertTrue(rawURL.contains("material=PLA%26x%3D1"),
                      "raw URL must percent-encode `&` and `=` in material; got: \(rawURL)")
        XCTAssertFalse(rawURL.contains("&x=1"),
                       "raw URL must not contain an injected `&x=1` parameter; got: \(rawURL)")

        // Round-trip: server-visible `material` decodes back to the exact
        // input and no spurious `x` parameter exists.
        let decoded = decodedQueryOfCapturedRequest()
        XCTAssertEqual(decoded?.material, "PLA&x=1")
        XCTAssertEqual(decoded?.sourceToolheadId, toolheadId.uuidString)
        XCTAssertEqual(decoded?.names.sorted(), ["material", "sourceToolheadId"],
                       "no extra query parameters may be injected")
    }

    func testGetAvailableFallback_plusSign_encodesAsPercent2B_notSpace() async throws {
        stubOKAndCaptureURL()

        _ = try await printerService.getAvailableFallback(
            printerId: printerId,
            sourceToolheadId: toolheadId,
            material: "PLA+"
        )

        // Raw wire form: `+` MUST be `%2B`. If it is emitted literally,
        // ASP.NET Core (and every RFC 1738 form parser) will bind it as a
        // space, so `PLA+` would be received as `PLA `.
        let rawURL = mockAPIClient.capturedRequests.first?.url?.absoluteString ?? ""
        XCTAssertTrue(rawURL.contains("material=PLA%2B"),
                      "raw URL must encode `+` as `%2B`; got: \(rawURL)")
        XCTAssertFalse(rawURL.contains("material=PLA+"),
                       "raw URL must not contain a literal `+` in material; got: \(rawURL)")

        let decoded = decodedQueryOfCapturedRequest()
        XCTAssertEqual(decoded?.material, "PLA+",
                       "server-visible material must round-trip exactly, not become `PLA `")
        XCTAssertEqual(decoded?.sourceToolheadId, toolheadId.uuidString)
        XCTAssertEqual(decoded?.names.sorted(), ["material", "sourceToolheadId"])
    }

    func testGetAvailableFallback_questionMarkAndHash_stayInsideMaterialValue() async throws {
        stubOKAndCaptureURL()

        // `?` starts a query and `#` starts a fragment — both must be
        // percent-encoded when they appear inside a value.
        _ = try await printerService.getAvailableFallback(
            printerId: printerId,
            sourceToolheadId: toolheadId,
            material: "PLA?y=1#frag"
        )

        let rawURL = mockAPIClient.capturedRequests.first?.url?.absoluteString ?? ""
        XCTAssertTrue(rawURL.contains("material=PLA%3Fy%3D1%23frag"),
                      "raw URL must percent-encode `?`, `=`, and `#` inside material; got: \(rawURL)")
        // There is only one `?` in the raw URL: the one that starts the
        // query string. Any additional `?` would be a bug.
        XCTAssertEqual(rawURL.filter { $0 == "?" }.count, 1,
                       "there must be exactly one `?` in the URL (the query separator); got: \(rawURL)")
        XCTAssertFalse(rawURL.contains("#"),
                       "raw URL must not contain a literal `#` — that would start a fragment; got: \(rawURL)")

        let decoded = decodedQueryOfCapturedRequest()
        XCTAssertEqual(decoded?.material, "PLA?y=1#frag")
        XCTAssertEqual(decoded?.sourceToolheadId, toolheadId.uuidString)
        XCTAssertEqual(decoded?.names.sorted(), ["material", "sourceToolheadId"])
    }
}
