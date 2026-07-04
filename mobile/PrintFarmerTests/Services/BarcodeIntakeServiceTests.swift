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
            XCTAssertEqual(request.url?.path, "/api/spoolman/filaments/by-barcode")
            XCTAssertEqual(URLComponents(url: request.url!, resolvingAgainstBaseURL: false)?.queryItems?.first(where: { $0.name == "code" })?.value, "UNKNOWN")
            return (TestData.httpResponse(url: request.url, statusCode: 404), Data("{}".utf8))
        }

        let filament = try await service.resolveFilament(barcode: "UNKNOWN")

        XCTAssertNil(filament)
    }

    func testResolveFilamentUsesQueryParameterAndEncodesSpecialCharacters() async throws {
        MockURLProtocol.requestHandler = { request in
            XCTAssertEqual(request.url?.path, "/api/spoolman/filaments/by-barcode")
            XCTAssertEqual(request.url?.query, "code=ABC%2FDEF%2012")
            let code = URLComponents(url: request.url!, resolvingAgainstBaseURL: false)?
                .queryItems?
                .first(where: { $0.name == "code" })?
                .value
            XCTAssertEqual(code, "ABC/DEF 12")
            return (TestData.httpResponse(url: request.url, statusCode: 200), Data(#"{"id":123}"#.utf8))
        }

        let filament = try await service.resolveFilament(barcode: "ABC/DEF 12")

        XCTAssertEqual(filament?.id, 123)
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

    func testFilamentRequestEncodesVendorIdContractField() throws {
        let request = SpoolmanFilamentRequest(
            name: "PLA Black",
            vendorId: 42,
            material: "PLA",
            colorHex: "#000000",
            weight: 1000,
            spoolWeight: 200,
            articleNumber: "000123"
        )

        let json = try JSONSerialization.jsonObject(with: JSONEncoder().encode(request)) as? [String: Any]

        XCTAssertEqual(json?["vendorId"] as? Int, 42)
        XCTAssertNil(json?["vendor"])
        XCTAssertEqual(json?["articleNumber"] as? String, "000123")
    }
}
