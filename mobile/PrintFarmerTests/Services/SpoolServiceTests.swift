import XCTest
@testable import PrintFarmer

final class SpoolServiceTests: XCTestCase {
    private var mockAPIClient: MockAPIClient!
    private var apiClient: APIClient!
    private var service: SpoolService!

    override func setUp() {
        super.setUp()
        mockAPIClient = MockAPIClient()
        apiClient = mockAPIClient.apiClient
        service = SpoolService(apiClient: apiClient)
    }

    override func tearDown() {
        service = nil
        apiClient = nil
        mockAPIClient = nil
        super.tearDown()
    }

    // GET /api/spoolman/filaments returns a paged wrapper { items, totalCount },
    // not a bare array. Regression test for the "Failed to decode response for
    // Array<SpoolmanFilament>" barcode-scan error.
    func testListFilamentsDecodesPagedWrapper() async throws {
        mockAPIClient.requestHandler = { request in
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
        mockAPIClient.stubResponse(json: #"{ "items": [], "totalCount": 0 }"#)

        let filaments = try await service.listFilaments()

        XCTAssertTrue(filaments.isEmpty)
    }

    func testSpoolLookupPagesUntilTheAssignedIDIsFound() async throws {
        let targetSpoolID = 902
        mockAPIClient.asyncRequestHandler = { request in
            let offset = request.url?.query?
                .split(separator: "&")
                .first(where: { $0.hasPrefix("offset=") })
                .map { String($0.dropFirst("offset=".count)) }
            let json: String
            switch offset {
            case "0":
                json = #"{"items":[{"id":101,"name":"First page","material":"PLA","inUse":false,"initialWeightG":1000,"remainingWeightG":850}],"totalCount":2}"#
            case "1":
                json = #"{"items":[{"id":902,"name":"Assigned spool","material":"PLA","inUse":false,"initialWeightG":1000,"remainingWeightG":84}],"totalCount":2}"#
            default:
                json = #"{"items":[],"totalCount":2}"#
            }
            return (TestData.httpResponse(url: request.url, statusCode: 200), Data(json.utf8))
        }

        let spool = try await service.spool(id: targetSpoolID)

        XCTAssertEqual(spool?.id, targetSpoolID)
        XCTAssertEqual(spool?.initialWeightG, 1000)
        XCTAssertEqual(spool?.remainingWeightG, 84)
        XCTAssertEqual(
            mockAPIClient.capturedRequests.compactMap(\.url?.query),
            ["limit=500&offset=0", "limit=500&offset=1"]
        )
    }
}
