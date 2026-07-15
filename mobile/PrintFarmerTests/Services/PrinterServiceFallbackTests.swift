import XCTest
@testable import PrintFarmer

/// Endpoint-shape tests for the fallback-group CRUD methods added to
/// `PrinterService` for issue #711 (F6). Verifies URLs, HTTP methods,
/// request bodies, `getAvailableFallback` 204 handling, and propagation
/// of `NetworkError.featureDisabled` / `.forbidden`.
final class PrinterServiceFallbackTests: XCTestCase {

    private var apiClient: APIClient!
    private var printerService: PrinterService!

    private let printerId = TestData.testUUID
    private let groupId = TestData.testUUID2
    private let toolheadId = TestData.testUUID3

    override func setUp() {
        super.setUp()
        MockURLProtocol.reset()
        apiClient = MockAPIClient.makeAPIClient()
        printerService = PrinterService(apiClient: apiClient)
    }

    override func tearDown() {
        MockURLProtocol.reset()
        apiClient = nil
        printerService = nil
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
        MockAPIClient.stubResponse(json: json)

        let details = try await printerService.getDetails(id: printerId)

        XCTAssertEqual(details.id, printerId)
        XCTAssertTrue(details.supportsPerToolAttribution)
        let captured = MockURLProtocol.capturedRequests.first
        XCTAssertEqual(captured?.httpMethod, "GET")
        XCTAssertTrue(captured?.url?.path.contains("/api/printers/\(printerId.uuidString)/details") ?? false)
    }

    // MARK: - listFallbackGroups

    func testListFallbackGroups_callsCorrectEndpoint() async throws {
        MockAPIClient.stubResponse(json: "[]")

        let groups = try await printerService.listFallbackGroups(printerId: printerId)

        XCTAssertTrue(groups.isEmpty)
        let captured = MockURLProtocol.capturedRequests.first
        XCTAssertEqual(captured?.httpMethod, "GET")
        XCTAssertTrue(captured?.url?.path.contains("/api/printers/\(printerId.uuidString)/fallback-groups") ?? false)
    }

    func testListFallbackGroups_propagatesFeatureDisabled404() async {
        // Simulate operator feature gate: 404 with ProblemDetails `code`.
        let body = """
        {"code":"featureDisabled","title":"Not Found","status":404}
        """
        MockURLProtocol.requestHandler = { req in
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
        MockAPIClient.stubResponse(json: json)

        let group = try await printerService.getFallbackGroup(printerId: printerId, groupId: groupId)

        XCTAssertEqual(group.id, groupId)
        let captured = MockURLProtocol.capturedRequests.first
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
        MockAPIClient.stubResponse(json: json, statusCode: 201)

        let request = CreateFilamentFallbackGroupRequest(
            name: "pla",
            materialType: "PLA",
            displayOrder: 1,
            toolheadIds: [toolheadId]
        )
        _ = try await printerService.createFallbackGroup(printerId: printerId, request)

        let captured = MockURLProtocol.capturedRequests.first
        XCTAssertEqual(captured?.httpMethod, "POST")
        XCTAssertTrue(captured?.url?.path.contains("/api/printers/\(printerId.uuidString)/fallback-groups") ?? false)
        let body = MockURLProtocol.capturedRequests.first?.capturedHTTPBody() ?? Data()
        let json2 = try JSONSerialization.jsonObject(with: body) as? [String: Any]
        XCTAssertEqual(json2?["name"] as? String, "pla")
        XCTAssertEqual(json2?["materialType"] as? String, "PLA")
        XCTAssertEqual(json2?["displayOrder"] as? Int, 1)
        XCTAssertEqual((json2?["toolheadIds"] as? [String])?.count, 1)
    }

    func testCreateFallbackGroup_forbiddenPropagatesAsForbidden() async {
        MockURLProtocol.requestHandler = { req in
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
        MockAPIClient.stubResponse(json: json)
        let request = UpdateFilamentFallbackGroupRequest(
            name: "n", materialType: "PLA", displayOrder: 0, toolheadIds: [toolheadId]
        )
        _ = try await printerService.updateFallbackGroup(printerId: printerId, groupId: groupId, request)

        let captured = MockURLProtocol.capturedRequests.first
        XCTAssertEqual(captured?.httpMethod, "PUT")
        XCTAssertTrue(captured?.url?.path.contains("/api/printers/\(printerId.uuidString)/fallback-groups/\(groupId.uuidString)") ?? false)
    }

    // MARK: - deleteFallbackGroup

    func testDeleteFallbackGroup_usesDELETE() async throws {
        MockAPIClient.stubEmptySuccess()
        try await printerService.deleteFallbackGroup(printerId: printerId, groupId: groupId)

        let captured = MockURLProtocol.capturedRequests.first
        XCTAssertEqual(captured?.httpMethod, "DELETE")
        XCTAssertTrue(captured?.url?.path.contains("/api/printers/\(printerId.uuidString)/fallback-groups/\(groupId.uuidString)") ?? false)
    }

    // MARK: - getAvailableFallback

    func testGetAvailableFallback_encodesQueryParams() async throws {
        let json = """
        {"groupId":"\(groupId.uuidString)","memberId":"\(TestData.testUUID3.uuidString)","toolheadId":"\(toolheadId.uuidString)","position":1,"loadedMaterial":"PLA","loadedSpoolId":null}
        """
        MockAPIClient.stubResponse(json: json)

        let member = try await printerService.getAvailableFallback(
            printerId: printerId,
            sourceToolheadId: toolheadId,
            material: "PLA-CF"
        )

        XCTAssertNotNil(member)
        XCTAssertEqual(member?.loadedMaterial, "PLA")
        let captured = MockURLProtocol.capturedRequests.first
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
        MockAPIClient.stubEmptySuccess()

        let member = try await printerService.getAvailableFallback(
            printerId: printerId,
            sourceToolheadId: toolheadId,
            material: "PLA"
        )

        XCTAssertNil(member)
    }
}
