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

    func testImportSpoolDecodesFullCreatedBackendResponse() async throws {
        MockAPIClient.stubResponse(
            json: """
            {
              "id": 501,
              "name": "Polymaker PolyLite PLA Black",
              "material": "PLA",
              "inUse": true,
              "remainingWeightG": 876.5,
              "colorHex": "#101010",
              "filamentName": "PolyLite PLA Black",
              "vendor": "Polymaker",
              "registeredAt": "2026-07-06T17:00:00Z",
              "firstUsedAt": "2026-07-06T17:05:00Z",
              "lastUsedAt": "2026-07-06T18:05:00Z",
              "initialWeightG": 1000,
              "usedWeightG": 123.5,
              "spoolWeightG": 220,
              "remainingLengthMm": 287654.3,
              "usedLengthMm": 40567.8,
              "location": "Rack A",
              "lotNumber": "LOT-42",
              "archived": false,
              "price": 24.99,
              "comment": "Imported from barcode",
              "filamentId": 77,
              "usedPercent": 12.35,
              "remainingPercent": 87.65
            }
            """,
            statusCode: 201
        )

        let spool = try await service.importSpool(barcode: "SP-501")

        XCTAssertEqual(spool.id, 501)
        XCTAssertEqual(spool.name, "Polymaker PolyLite PLA Black")
        XCTAssertEqual(spool.material, "PLA")
        XCTAssertEqual(spool.inUse, true)
        XCTAssertEqual(spool.remainingWeightG, 876.5)
        XCTAssertEqual(spool.colorHex, "#101010")
        XCTAssertEqual(spool.filamentName, "PolyLite PLA Black")
        XCTAssertEqual(spool.vendor, "Polymaker")
        XCTAssertEqual(spool.filamentId, 77)
        XCTAssertEqual(spool.usedPercent, 12.35)
        XCTAssertEqual(spool.remainingPercent, 87.65)
    }

    func testImportSpoolDecodesMinimalBackendResponseWithOmittedNulls() async throws {
        MockAPIClient.stubResponse(
            json: """
            {
              "id": 502,
              "name": "Generic PETG",
              "material": "PETG",
              "inUse": false
            }
            """,
            statusCode: 201
        )

        let spool = try await service.importSpool(barcode: "SP-502")

        XCTAssertEqual(spool.id, 502)
        XCTAssertEqual(spool.name, "Generic PETG")
        XCTAssertEqual(spool.material, "PETG")
        XCTAssertEqual(spool.inUse, false)
        XCTAssertNil(spool.remainingWeightG)
        XCTAssertNil(spool.colorHex)
        XCTAssertNil(spool.filamentId)
    }

    func testResolveFilamentDecodesFullBackendResponse() async throws {
        MockAPIClient.stubResponse(
            json: """
            {
              "id": 701,
              "name": "Galaxy Black",
              "material": "PLA",
              "colorHex": "#111111",
              "vendor": "Prusament",
              "density": 1.24,
              "diameter": 1.75,
              "weight": 1000,
              "spoolWeight": 201,
              "price": 29.99,
              "settingsExtruderTemp": 215,
              "settingsBedTemp": 60,
              "articleNumber": "PLA-GB-1000",
              "comment": "Resolved from barcode",
              "multiColorHexes": "#111111,#222222",
              "externalId": "ext-701"
            }
            """
        )

        let filament = try await service.resolveFilament(barcode: "FIL-701")

        XCTAssertEqual(filament?.id, 701)
        XCTAssertEqual(filament?.name, "Galaxy Black")
        XCTAssertEqual(filament?.material, "PLA")
        XCTAssertEqual(filament?.colorHex, "#111111")
        XCTAssertEqual(filament?.vendor, "Prusament")
        XCTAssertEqual(filament?.density, 1.24)
        XCTAssertEqual(filament?.diameter, 1.75)
        XCTAssertEqual(filament?.settingsExtruderTemp, 215)
        XCTAssertEqual(filament?.settingsBedTemp, 60)
        XCTAssertEqual(filament?.multiColorHexes, "#111111,#222222")
        XCTAssertEqual(filament?.externalId, "ext-701")
    }

    func testResolveFilamentDecodesIdOnlyBackendResponseWithOmittedNulls() async throws {
        MockAPIClient.stubResponse(json: #"{ "id": 702 }"#)

        let filament = try await service.resolveFilament(barcode: "FIL-702")

        XCTAssertEqual(filament?.id, 702)
        XCTAssertNil(filament?.name)
        XCTAssertNil(filament?.material)
        XCTAssertNil(filament?.colorHex)
        XCTAssertNil(filament?.vendor)
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
