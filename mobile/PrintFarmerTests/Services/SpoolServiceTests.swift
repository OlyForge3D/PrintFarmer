import XCTest
@testable import PrintFarmer

final class SpoolServiceTests: XCTestCase {
    private var apiClient: APIClient!
    private var service: SpoolService!

    override func setUp() {
        super.setUp()
        MockURLProtocol.reset()
        apiClient = MockAPIClient.makeAPIClient()
        service = SpoolService(apiClient: apiClient)
    }

    override func tearDown() {
        MockURLProtocol.reset()
        service = nil
        apiClient = nil
        super.tearDown()
    }

    // GET /api/spoolman/filaments returns a paged wrapper { items, totalCount },
    // not a bare array. Regression test for the "Failed to decode response for
    // Array<SpoolmanFilament>" barcode-scan error.
    func testListFilamentsDecodesPagedWrapper() async throws {
        MockURLProtocol.requestHandler = { request in
            XCTAssertEqual(request.url?.path, "/api/spoolman/filaments")
            let json = """
            {
              "items": [
                { "id": 701, "name": "Galaxy Black", "material": "PLA", "colorHex": "#111111", "vendor": "Prusament" },
                { "id": 702, "name": "Galaxy Silver", "material": "PLA" }
              ],
              "totalCount": 2
            }
            """
            return (TestData.httpResponse(url: request.url, statusCode: 200), Data(json.utf8))
        }

        let filaments = try await service.listFilaments()

        XCTAssertEqual(filaments.count, 2)
        XCTAssertEqual(filaments[0].id, 701)
        XCTAssertEqual(filaments[0].name, "Galaxy Black")
        XCTAssertEqual(filaments[0].vendor, "Prusament")
        XCTAssertEqual(filaments[1].id, 702)
        XCTAssertNil(filaments[1].vendor)
    }

    func testListFilamentsDecodesEmptyPage() async throws {
        MockAPIClient.stubResponse(json: #"{ "items": [], "totalCount": 0 }"#)

        let filaments = try await service.listFilaments()

        XCTAssertTrue(filaments.isEmpty)
    }
}
