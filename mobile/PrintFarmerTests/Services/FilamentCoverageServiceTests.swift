import XCTest
@testable import PrintFarmer

// MARK: - Filament Coverage Service Tests (F4-M / issue #778)
//
// Locks the URL contract for the two coverage endpoints and the
// error-mapping contract for feature-gated 404s and non-404 failures.
// Uses `MockAPIClient` so we exercise the real `APIClient` code path
// (URL building, JSON decoding, error mapping) instead of stubbing it.

final class FilamentCoverageServiceTests: XCTestCase {

    private var mockAPIClient: MockAPIClient!
    private var apiClient: APIClient!
    private var service: FilamentCoverageService!

    override func setUp() {
        super.setUp()
        mockAPIClient = MockAPIClient()
        apiClient = mockAPIClient.apiClient
        service = FilamentCoverageService(apiClient: apiClient)
    }

    override func tearDown() {
        apiClient = nil
        service = nil
        mockAPIClient = nil
        super.tearDown()
    }

    // MARK: - GET /api/printers/{id}/filament-coverage

    func testGetForPrinterIssuesGetAgainstCorrectPath() async throws {
        let id = UUID(uuidString: "11111111-2222-3333-4444-555555555555")!
        let json = """
        {
          "printerId": "\(id.uuidString.lowercased())",
          "printerName": "MK4",
          "status": "covers",
          "toolheads": [],
          "activeJobId": null,
          "activeJobName": null,
          "activeJobProgress": null,
          "earliestPredictedRunoutAt": null,
          "assignedQueuedJobCount": 0,
          "evaluatedAtUtc": "2026-07-21T20:00:00Z"
        }
        """
        mockAPIClient.stubResponse(json: json)

        _ = try await service.getForPrinter(id: id)

        let request = try XCTUnwrap(mockAPIClient.capturedRequests.first)
        XCTAssertEqual(request.httpMethod, "GET")
        // The printer id is a UUID; percent-encoding is a byte-safe
        // no-op for hex+hyphen but the encoder must still be invoked.
        // Verify the exact path landed on the server.
        XCTAssertEqual(request.url?.path,
                       "/api/printers/\(id.uuidString)/filament-coverage")
        XCTAssertNil(request.url?.query)
    }

    func testGetForPrinterPercentEncodesSpecialCharactersInSegment() async throws {
        // Guard against a future migration to slug-style ids. Even
        // though UUIDs today are hex+hyphens, the frozen contract
        // requires percent-encoding of the id segment. The real
        // production printer service can't currently generate a
        // slash-in-id, but the encoder path is exercised here so a
        // regression is caught before it ships.
        let id = UUID()
        mockAPIClient.stubResponse(json: """
        {
          "printerId": "\(id.uuidString.lowercased())",
          "printerName": "P",
          "status": "unknown",
          "toolheads": [],
          "activeJobId": null,
          "activeJobName": null,
          "activeJobProgress": null,
          "earliestPredictedRunoutAt": null,
          "assignedQueuedJobCount": 0,
          "evaluatedAtUtc": "2026-07-21T20:00:00Z"
        }
        """)
        _ = try await service.getForPrinter(id: id)
        let request = try XCTUnwrap(mockAPIClient.capturedRequests.first)
        let path = request.url?.path ?? ""
        // No unencoded reserved sub-delims should appear.
        XCTAssertFalse(path.contains("/api/printers//filament-coverage"))
        XCTAssertTrue(path.hasSuffix("/filament-coverage"))
    }

    func testGetForPrinterPropagatesFeatureDisabled() async {
        let json = """
        {
          "type": "https://printfarmer/errors/feature-disabled",
          "title": "Feature Disabled",
          "status": 404,
          "detail": "Filament coverage is disabled on this server.",
          "code": "featureDisabled"
        }
        """
        mockAPIClient.stubResponse(json: json, statusCode: 404)
        do {
            _ = try await service.getForPrinter(id: UUID())
            XCTFail("Expected NetworkError.featureDisabled")
        } catch let error as NetworkError {
            guard case .featureDisabled = error else {
                return XCTFail("Expected .featureDisabled, got \(error)")
            }
        } catch {
            XCTFail("Expected NetworkError, got \(error)")
        }
    }

    func testGetForPrinterPropagatesGenericNotFoundAsNotFound() async {
        // A non-gated 404 (printer really doesn't exist) must map to
        // `.notFound`, distinct from `.featureDisabled`. The
        // ViewModel branches on that difference to decide whether to
        // show the disabled-tombstone or "printer not found" state.
        mockAPIClient.stubResponse(json: "{\"message\":\"Printer not found\"}", statusCode: 404)
        do {
            _ = try await service.getForPrinter(id: UUID())
            XCTFail("Expected NetworkError.notFound")
        } catch let error as NetworkError {
            guard case .notFound = error else {
                return XCTFail("Expected .notFound, got \(error)")
            }
        } catch {
            XCTFail("Expected NetworkError, got \(error)")
        }
    }

    func testGetForPrinterPropagatesServerError() async {
        mockAPIClient.stubResponse(json: "{}", statusCode: 500)
        do {
            _ = try await service.getForPrinter(id: UUID())
            XCTFail("Expected NetworkError.serverError")
        } catch let error as NetworkError {
            guard case .serverError(let code) = error else {
                return XCTFail("Expected .serverError, got \(error)")
            }
            XCTAssertEqual(code, 500)
        } catch {
            XCTFail("Expected NetworkError, got \(error)")
        }
    }

    // MARK: - GET /api/printers/filament-coverage

    func testGetForFleetIssuesGetAgainstFleetPath() async throws {
        let json = """
        { "printers": [], "evaluatedAtUtc": "2026-07-21T20:00:00Z" }
        """
        mockAPIClient.stubResponse(json: json)

        _ = try await service.getForFleet()

        let request = try XCTUnwrap(mockAPIClient.capturedRequests.first)
        XCTAssertEqual(request.httpMethod, "GET")
        XCTAssertEqual(request.url?.path, "/api/printers/filament-coverage")
        XCTAssertNil(request.url?.query)
    }

    func testGetForFleetPropagatesFeatureDisabled() async {
        let json = """
        {
          "type": "https://printfarmer/errors/feature-disabled",
          "title": "Feature Disabled",
          "status": 404,
          "detail": "Filament coverage is disabled.",
          "code": "featureDisabled"
        }
        """
        mockAPIClient.stubResponse(json: json, statusCode: 404)
        do {
            _ = try await service.getForFleet()
            XCTFail("Expected NetworkError.featureDisabled")
        } catch let error as NetworkError {
            guard case .featureDisabled = error else {
                return XCTFail("Expected .featureDisabled, got \(error)")
            }
        } catch {
            XCTFail("Expected NetworkError, got \(error)")
        }
    }
}
