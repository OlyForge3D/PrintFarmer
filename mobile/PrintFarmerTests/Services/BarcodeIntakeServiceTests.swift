import XCTest
@testable import PrintFarmer

final class BarcodeIntakeServiceTests: XCTestCase {
    private var apiClient: APIClient!
    private var service: BarcodeIntakeService!

    override func setUp() {
        super.setUp()
        MockURLProtocol.reset()
        apiClient = MockAPIClient.makeAPIClient()
        service = BarcodeIntakeService(apiClient: apiClient)
    }

    override func tearDown() {
        MockURLProtocol.reset()
        service = nil
        apiClient = nil
        super.tearDown()
    }

    func testResolveFilamentMaps404ToNil() async throws {
        MockURLProtocol.requestHandler = { request in
            XCTAssertEqual(request.url?.path, "/api/spoolman/filaments/by-barcode/UNKNOWN")
            return (TestData.httpResponse(url: request.url, statusCode: 404), Data("{}".utf8))
        }

        let filament = try await service.resolveFilament(barcode: "UNKNOWN")

        XCTAssertNil(filament)
    }

    func testResolveFilamentRethrowsNon404Errors() async {
        MockURLProtocol.requestHandler = { request in
            (TestData.httpResponse(url: request.url, statusCode: 500), Data("{}".utf8))
        }

        do {
            _ = try await service.resolveFilament(barcode: "ERROR")
            XCTFail("Expected server error")
        } catch NetworkError.serverError(let statusCode) {
            XCTAssertEqual(statusCode, 500)
        } catch {
            XCTFail("Unexpected error: \(error)")
        }
    }
}
