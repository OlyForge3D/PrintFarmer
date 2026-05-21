import XCTest
@testable import PrintFarmer

/// Tests for PrinterService: verifies correct endpoints, HTTP methods,
/// and error propagation. Now includes individual command endpoints.
final class PrinterServiceTests: XCTestCase {

    private var apiClient: APIClient!
    private var printerService: PrinterService!

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

    // MARK: - list()

    func testListPrintersCallsCorrectEndpoint() async throws {
        MockAPIClient.stubResponse(json: TestJSON.printerArray)

        let printers = try await printerService.list()

        XCTAssertEqual(printers.count, 2)

        let captured = MockURLProtocol.capturedRequests.first
        XCTAssertEqual(captured?.httpMethod, "GET")
        XCTAssertTrue(captured?.url?.path.contains("/api/printers") ?? false)
        XCTAssertFalse(captured?.url?.absoluteString.contains("includeDisabled") ?? true)
    }

    func testListPrintersIncludeDisabled() async throws {
        MockAPIClient.stubResponse(json: TestJSON.printerArray)

        _ = try await printerService.list(includeDisabled: true)

        let captured = MockURLProtocol.capturedRequests.first
        XCTAssertTrue(captured?.url?.absoluteString.contains("includeDisabled=true") ?? false)
    }

    func testListPrintersReturnsEmptyArray() async throws {
        MockAPIClient.stubResponse(json: "[]")

        let printers = try await printerService.list()

        XCTAssertEqual(printers.count, 0)
    }

    func testListPrintersThrowsOnNetworkError() async {
        MockAPIClient.stubError(.notConnectedToInternet)

        do {
            _ = try await printerService.list()
            XCTFail("Expected error")
        } catch let error as NetworkError {
            if case .noConnection = error { } else {
                XCTFail("Expected .noConnection, got \(error)")
            }
        } catch {
            XCTFail("Unexpected error: \(error)")
        }
    }

    // MARK: - get()

    func testGetPrinterCallsCorrectEndpoint() async throws {
        MockAPIClient.stubResponse(json: TestJSON.printer)

        let printer = try await printerService.get(id: TestData.testUUID)

        XCTAssertEqual(printer.id, TestData.testUUID)
        XCTAssertEqual(printer.name, "Prusa MK4")

        let captured = MockURLProtocol.capturedRequests.first
        XCTAssertEqual(captured?.httpMethod, "GET")
        XCTAssertTrue(captured?.url?.path.contains("/api/printers/\(TestData.testUUID)") ?? false)
    }

    func testGetPrinterThrows404WhenNotFound() async {
        MockAPIClient.stubResponse(json: "{}", statusCode: 404)

        do {
            _ = try await printerService.get(id: TestData.testUUID)
            XCTFail("Expected NetworkError.notFound")
        } catch let error as NetworkError {
            if case .notFound = error { } else {
                XCTFail("Expected .notFound, got \(error)")
            }
        } catch {
            XCTFail("Unexpected error: \(error)")
        }
    }

    // MARK: - update()

    func testUpdatePrinterCallsCorrectEndpoint() async throws {
        MockAPIClient.stubResponse(json: TestJSON.printer)

        let request = UpdatePrinterRequest(name: "Renamed MK4")
        _ = try await printerService.update(id: TestData.testUUID, request)

        let captured = MockURLProtocol.capturedRequests.first
        XCTAssertEqual(captured?.httpMethod, "PUT")
        XCTAssertTrue(captured?.url?.path.contains("/api/printers/\(TestData.testUUID)") ?? false)
        XCTAssertNotNil(captured?.httpBody)
    }

    // MARK: - delete()

    func testDeletePrinterCallsCorrectEndpoint() async throws {
        MockAPIClient.stubEmptySuccess()

        try await printerService.delete(id: TestData.testUUID)

        let captured = MockURLProtocol.capturedRequests.first
        XCTAssertEqual(captured?.httpMethod, "DELETE")
        XCTAssertTrue(captured?.url?.path.contains("/api/printers/\(TestData.testUUID)") ?? false)
    }

    // MARK: - setMaintenanceMode()

    func testSetMaintenanceModeCallsCorrectEndpoint() async throws {
        MockAPIClient.stubResponse(json: TestJSON.printer)

        _ = try await printerService.setMaintenanceMode(id: TestData.testUUID, inMaintenance: true)

        let captured = MockURLProtocol.capturedRequests.first
        XCTAssertEqual(captured?.httpMethod, "PUT")
        XCTAssertTrue(captured?.url?.path.contains("/api/printers/\(TestData.testUUID)/maintenance") ?? false)
    }

    // MARK: - Individual Command Endpoints

    func testPauseCallsCorrectEndpoint() async throws {
        MockAPIClient.stubResponse(json: TestJSON.commandSuccess)

        let result = try await printerService.pause(id: TestData.testUUID)

        XCTAssertTrue(result.success)
        let captured = MockURLProtocol.capturedRequests.first
        XCTAssertEqual(captured?.httpMethod, "POST")
        XCTAssertTrue(captured?.url?.path.contains("/api/printers/\(TestData.testUUID)/pause") ?? false)
    }

    func testResumeCallsCorrectEndpoint() async throws {
        MockAPIClient.stubResponse(json: TestJSON.commandSuccess)

        let result = try await printerService.resume(id: TestData.testUUID)

        XCTAssertTrue(result.success)
        let captured = MockURLProtocol.capturedRequests.first
        XCTAssertTrue(captured?.url?.path.contains("/api/printers/\(TestData.testUUID)/resume") ?? false)
    }

    func testCancelCallsCorrectEndpoint() async throws {
        MockAPIClient.stubResponse(json: TestJSON.commandSuccess)

        let result = try await printerService.cancel(id: TestData.testUUID)

        XCTAssertTrue(result.success)
        let captured = MockURLProtocol.capturedRequests.first
        XCTAssertTrue(captured?.url?.path.contains("/api/printers/\(TestData.testUUID)/cancel") ?? false)
    }

    func testStopCallsCorrectEndpoint() async throws {
        MockAPIClient.stubResponse(json: TestJSON.commandSuccess)

        let result = try await printerService.stop(id: TestData.testUUID)

        XCTAssertTrue(result.success)
        let captured = MockURLProtocol.capturedRequests.first
        XCTAssertTrue(captured?.url?.path.contains("/api/printers/\(TestData.testUUID)/stop") ?? false)
    }

    func testEmergencyStopCallsCorrectEndpoint() async throws {
        MockAPIClient.stubResponse(json: TestJSON.commandSuccess)

        let result = try await printerService.emergencyStop(id: TestData.testUUID)

        XCTAssertTrue(result.success)
        let captured = MockURLProtocol.capturedRequests.first
        XCTAssertTrue(captured?.url?.path.contains("/api/printers/\(TestData.testUUID)/emergency-stop") ?? false)
    }

    // MARK: - getStatus()

    func testGetStatusCallsCorrectEndpoint() async throws {
        let statusJSON = """
        {
            "id": "\(TestData.testUUID)",
            "isOnline": true,
            "state": "printing",
            "progress": 55.0,
            "hotendTemp": 215.0,
            "bedTemp": 60.0
        }
        """
        MockAPIClient.stubResponse(json: statusJSON)

        let status = try await printerService.getStatus(id: TestData.testUUID)

        XCTAssertEqual(status.state, "printing")
        XCTAssertEqual(status.progress, 55.0)
        let captured = MockURLProtocol.capturedRequests.first
        XCTAssertTrue(captured?.url?.path.contains("/api/printers/\(TestData.testUUID)/status") ?? false)
    }

    // MARK: - Command Error Handling

    func testCommandThrowsOnServerError() async {
        MockAPIClient.stubResponse(json: "{}", statusCode: 500)

        do {
            _ = try await printerService.pause(id: TestData.testUUID)
            XCTFail("Expected error")
        } catch let error as NetworkError {
            if case .serverError(let code) = error {
                XCTAssertEqual(code, 500)
            } else {
                XCTFail("Expected .serverError, got \(error)")
            }
        } catch {
            XCTFail("Unexpected error: \(error)")
        }
    }

    func testCommandThrowsWhenPrinterOffline() async {
        MockAPIClient.stubError(.cannotConnectToHost)

        do {
            _ = try await printerService.pause(id: TestData.testUUID)
            XCTFail("Expected error")
        } catch let error as NetworkError {
            if case .serverUnreachable = error { } else {
                XCTFail("Expected .serverUnreachable, got \(error)")
            }
        } catch {
            XCTFail("Unexpected error: \(error)")
        }
    }
}
