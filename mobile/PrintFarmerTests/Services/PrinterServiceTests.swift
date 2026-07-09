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

    // MARK: - Camera URLs

    func testListCameraUrlsCallsCorrectEndpoint() async throws {
        MockAPIClient.stubResponse(json: TestJSON.printerCameraUrls)

        let cameras = try await printerService.listCameraUrls()

        XCTAssertEqual(cameras.count, 2)
        XCTAssertEqual(cameras[1].cameraAccessMode, .snapshotOnly)
        XCTAssertEqual(cameras[1].cameraSnapshotStrategy, .snapmakerU1MonitorJpeg)

        let captured = MockURLProtocol.capturedRequests.first
        XCTAssertEqual(captured?.httpMethod, "GET")
        XCTAssertTrue(captured?.url?.path.hasSuffix("/api/printers/camera-urls") ?? false)
    }

    func testGetCameraUrlCallsCorrectEndpoint() async throws {
        MockAPIClient.stubResponse(json: TestJSON.printerCameraUrl)

        let camera = try await printerService.getCameraUrl(id: TestData.testUUID)

        XCTAssertEqual(camera.accessMode, .snapshotOnly)
        XCTAssertEqual(camera.snapshotStrategy, .snapmakerU1MonitorJpeg)

        let captured = MockURLProtocol.capturedRequests.first
        XCTAssertEqual(captured?.httpMethod, "GET")
        XCTAssertTrue(captured?.url?.path.hasSuffix("/api/printers/\(TestData.testUUID)/camera-url") ?? false)
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

    // MARK: - setTemperatures()

    func testSetTemperaturesPostsBothFields() async throws {
        MockAPIClient.stubEmptySuccess()

        try await printerService.setTemperatures(printerId: TestData.testUUID, hotend: 215, bed: 60)

        let captured = MockURLProtocol.capturedRequests.first
        XCTAssertEqual(captured?.httpMethod, "POST")
        XCTAssertTrue(captured?.url?.path.contains("/api/printers/\(TestData.testUUID)/temps") ?? false)

        let body = captured?.capturedHTTPBody()
        XCTAssertNotNil(body)
        let json = try JSONSerialization.jsonObject(with: body ?? Data()) as? [String: Any]
        XCTAssertEqual(json?["hotend"] as? Double, 215)
        XCTAssertEqual(json?["bed"] as? Double, 60)
    }

    func testSetTemperaturesOmitsNilFields() async throws {
        MockAPIClient.stubEmptySuccess()

        try await printerService.setTemperatures(printerId: TestData.testUUID, hotend: 200, bed: nil)

        let captured = MockURLProtocol.capturedRequests.first
        let body = captured?.capturedHTTPBody()
        let json = try JSONSerialization.jsonObject(with: body ?? Data()) as? [String: Any]
        XCTAssertEqual(json?["hotend"] as? Double, 200)
        XCTAssertNil(json?["bed"], "nil bed must be omitted from request body")
    }

    func testSetTemperaturesCooldownSendsZeros() async throws {
        MockAPIClient.stubEmptySuccess()

        try await printerService.setTemperatures(printerId: TestData.testUUID, hotend: 0, bed: 0)

        let captured = MockURLProtocol.capturedRequests.first
        let body = captured?.capturedHTTPBody()
        let json = try JSONSerialization.jsonObject(with: body ?? Data()) as? [String: Any]
        XCTAssertEqual(json?["hotend"] as? Double, 0)
        XCTAssertEqual(json?["bed"] as? Double, 0)
    }

    // MARK: - home()

    func testHomeAllAxesRoutesToHomeEndpoint() async throws {
        MockAPIClient.stubEmptySuccess()

        try await printerService.home(printerId: TestData.testUUID, axes: ["X", "Y", "Z"])

        let captured = MockURLProtocol.capturedRequests.first
        XCTAssertEqual(captured?.httpMethod, "POST")
        XCTAssertTrue(captured?.url?.path.hasSuffix("/home") ?? false,
                      "axes [X,Y,Z] must route to /home, got \(captured?.url?.path ?? "nil")")
    }

    func testHomeXYAxesRoutesToHomeXYEndpoint() async throws {
        MockAPIClient.stubEmptySuccess()

        try await printerService.home(printerId: TestData.testUUID, axes: ["X", "Y"])

        let captured = MockURLProtocol.capturedRequests.first
        XCTAssertTrue(captured?.url?.path.hasSuffix("/homexy") ?? false,
                      "axes [X,Y] must route to /homexy")
    }

    func testHomeZAxisRoutesToHomeZEndpoint() async throws {
        MockAPIClient.stubEmptySuccess()

        try await printerService.home(printerId: TestData.testUUID, axes: ["Z"])

        let captured = MockURLProtocol.capturedRequests.first
        XCTAssertTrue(captured?.url?.path.hasSuffix("/homez") ?? false,
                      "axes [Z] must route to /homez")
    }

    func testHomeXYConvenienceWrapper() async throws {
        MockAPIClient.stubEmptySuccess()

        try await printerService.homeXY(printerId: TestData.testUUID)

        let captured = MockURLProtocol.capturedRequests.first
        XCTAssertEqual(captured?.httpMethod, "POST")
        XCTAssertTrue(captured?.url?.path.hasSuffix("/homexy") ?? false)
    }

    func testHomeZConvenienceWrapper() async throws {
        MockAPIClient.stubEmptySuccess()

        try await printerService.homeZ(printerId: TestData.testUUID)

        let captured = MockURLProtocol.capturedRequests.first
        XCTAssertEqual(captured?.httpMethod, "POST")
        XCTAssertTrue(captured?.url?.path.hasSuffix("/homez") ?? false)
    }

    // MARK: - move()

    func testMoveOnXAxisSendsCorrectBody() async throws {
        MockAPIClient.stubEmptySuccess()

        try await printerService.move(printerId: TestData.testUUID, axis: "X", distanceMm: 10.0, feedrateMmMin: 3000)

        let captured = MockURLProtocol.capturedRequests.first
        XCTAssertEqual(captured?.httpMethod, "POST")
        XCTAssertTrue(captured?.url?.path.contains("/api/printers/\(TestData.testUUID)/move") ?? false)

        let body = captured?.capturedHTTPBody()
        XCTAssertNotNil(body)
        let json = try JSONSerialization.jsonObject(with: body ?? Data()) as? [String: Any]
        XCTAssertEqual(json?["x"] as? Double, 10.0)
        XCTAssertNil(json?["y"], "non-target axes must be omitted")
        XCTAssertNil(json?["z"], "non-target axes must be omitted")
        XCTAssertEqual(json?["f"] as? Double, 3000)
    }

    func testMoveOnYAxisSendsCorrectBody() async throws {
        MockAPIClient.stubEmptySuccess()

        try await printerService.move(printerId: TestData.testUUID, axis: "Y", distanceMm: -5.0, feedrateMmMin: 3000)

        let captured = MockURLProtocol.capturedRequests.first
        let body = captured?.capturedHTTPBody()
        let json = try JSONSerialization.jsonObject(with: body ?? Data()) as? [String: Any]
        XCTAssertNil(json?["x"])
        XCTAssertEqual(json?["y"] as? Double, -5.0)
        XCTAssertNil(json?["z"])
        XCTAssertEqual(json?["f"] as? Double, 3000)
    }

    func testMoveOnZAxisUsesLockedFeedrate() async throws {
        MockAPIClient.stubEmptySuccess()

        try await printerService.move(printerId: TestData.testUUID, axis: "Z", distanceMm: 0.1, feedrateMmMin: 600)

        let captured = MockURLProtocol.capturedRequests.first
        let body = captured?.capturedHTTPBody()
        let json = try JSONSerialization.jsonObject(with: body ?? Data()) as? [String: Any]
        XCTAssertNil(json?["x"])
        XCTAssertNil(json?["y"])
        XCTAssertEqual(json?["z"] as? Double, 0.1)
        XCTAssertEqual(json?["f"] as? Double, 600)
    }

    func testSetTemperaturesPropagatesServerError() async {
        MockAPIClient.stubResponse(json: "{}", statusCode: 502)

        do {
            try await printerService.setTemperatures(printerId: TestData.testUUID, hotend: 200, bed: 60)
            XCTFail("Expected error")
        } catch let error as NetworkError {
            if case .serverError(let code) = error {
                XCTAssertEqual(code, 502)
            } else {
                XCTFail("Expected .serverError, got \(error)")
            }
        } catch {
            XCTFail("Unexpected error: \(error)")
        }
    }

    // MARK: - getBackendCapabilities()

    func testGetBackendCapabilities_happyPath_returnsMergedCapabilities() async throws {
        let capJson = """
        {
            "printerId": "\(TestData.testUUID)",
            "printerName": "Test Moonraker",
            "backend": "Moonraker",
            "supportsMovement": true,
            "supportsTemperatureControl": true,
            "supportsCamera": true,
            "supportsControlOperations": true,
            "supportsFileList": true,
            "supportsFileUpload": true,
            "supportsFileDownload": true,
            "supportsStartPrint": true,
            "supportsFilamentControl": true,
            "supportsFileMetadata": true,
            "supportsPrinterInformation": true,
            "supportsHistory": true
        }
        """
        MockAPIClient.stubResponse(json: capJson)

        let caps = try await printerService.getBackendCapabilities(printerId: TestData.testUUID)

        let captured = MockURLProtocol.capturedRequests.first
        XCTAssertTrue(captured?.url?.path.contains("backend-capabilities") ?? false,
                      "Must hit the backend-capabilities endpoint first")
        XCTAssertTrue(caps.supportsMovement, "Moonraker wire DTO supportsMovement=true must be honoured")
        XCTAssertTrue(caps.supportsTemperatureControl)
    }

    func testGetBackendCapabilities_404_fallsBackToStaticTable() async throws {
        // Stub per-path: capabilities returns 404, printer get returns Moonraker printer
        MockAPIClient.stubResponses([
            "backend-capabilities": (statusCode: 404, json: "{}"),
            "/api/printers/\(TestData.testUUID)": (statusCode: 200, json: TestJSON.printer)
        ])

        let caps = try await printerService.getBackendCapabilities(printerId: TestData.testUUID)

        // Moonraker fallback: all controls supported
        XCTAssertTrue(caps.supportsMovement,
                      "Moonraker fallback must report supportsMovement=true")
        XCTAssertTrue(caps.supportsTemperatureControl)
        XCTAssertTrue(caps.supportsBedTemperature)
        XCTAssertTrue(caps.supportsFanControl)
    }

    func testGetBackendCapabilities_resin_sdcp_movementFalse() async throws {
        let sdcpPrinterJson = TestJSON.printer
            .replacingOccurrences(of: "\"backend\": \"Moonraker\"", with: "\"backend\": \"SDCP\"")

        MockAPIClient.stubResponses([
            "backend-capabilities": (statusCode: 404, json: "{}"),
            "/api/printers/\(TestData.testUUID)": (statusCode: 200, json: sdcpPrinterJson)
        ])

        let caps = try await printerService.getBackendCapabilities(printerId: TestData.testUUID)

        // SDCP (resin) fallback: no movement, no temperature
        XCTAssertFalse(caps.supportsMovement,
                       "Resin/SDCP fallback must report supportsMovement=false")
        XCTAssertFalse(caps.supportsTemperatureControl)
        XCTAssertFalse(caps.supportsBedTemperature)
        XCTAssertFalse(caps.supportsFanControl)
    }
}
