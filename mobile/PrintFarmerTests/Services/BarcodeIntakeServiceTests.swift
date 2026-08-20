import XCTest
@testable import PrintFarmer

final class BarcodeIntakeServiceTests: XCTestCase {
    private var mockAPIClient: MockAPIClient!
    private var apiClient: APIClient!
    private var service: BarcodeIntakeService!

    override func setUp() {
        super.setUp()
        mockAPIClient = MockAPIClient()
        apiClient = mockAPIClient.apiClient
        service = BarcodeIntakeService(apiClient: apiClient)
    }

    override func tearDown() {
        service = nil
        apiClient = nil
        mockAPIClient = nil
        super.tearDown()
    }

    func testResolveFilamentMaps404ToNil() async throws {
        mockAPIClient.requestHandler = { request in
            XCTAssertEqual(request.url?.path, "/api/spoolman/filaments/by-barcode")
            XCTAssertEqual(URLComponents(url: request.url!, resolvingAgainstBaseURL: false)?.queryItems?.first(where: { $0.name == "code" })?.value, "UNKNOWN")
            return (TestData.httpResponse(url: request.url, statusCode: 404), Data("{}".utf8))
        }

        let filament = try await service.resolveFilament(barcode: "UNKNOWN")

        XCTAssertNil(filament)
    }

    func testResolveFilamentUsesQueryParameterAndEncodesSpecialCharacters() async throws {
        mockAPIClient.requestHandler = { request in
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

    func testSaveMappingPostsRawBarcodeAndDecodesDistinctGtinAndArticleNumber() async throws {
        let rawBarcode = "0850078714923"
        mockAPIClient.requestHandler = { request in
            XCTAssertEqual(request.httpMethod, "POST")
            XCTAssertEqual(request.url?.path, "/api/spoolman/barcodes")

            guard let body = request.capturedHTTPBody(),
                  let json = try? JSONSerialization.jsonObject(with: body) as? [String: Any] else {
                XCTFail("Expected JSON barcode mapping body")
                return (TestData.httpResponse(url: request.url, statusCode: 400), Data())
            }
            XCTAssertEqual(json["barcode"] as? String, rawBarcode)
            XCTAssertEqual(json["filamentId"] as? Int, 701)

            let response = """
            {
              "id": 701,
              "name": "PolyLite PLA",
              "articleNumber": "PM70820",
              "gtin": "00850078714923"
            }
            """
            return (TestData.httpResponse(url: request.url, statusCode: 200), Data(response.utf8))
        }

        let filament = try await service.saveMapping(barcode: rawBarcode, filamentId: 701)

        XCTAssertEqual(filament.gtin, "00850078714923")
        XCTAssertEqual(filament.articleNumber, "PM70820")
    }

    func testImportSpoolDecodesFullCreatedBackendResponse() async throws {
        mockAPIClient.stubResponse(
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
        mockAPIClient.stubResponse(
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

    func testImportSpoolMissingRequiredInUseThrowsKeyNotFoundWithServerVersionHint() async {
        mockAPIClient.stubResponse(
            json: """
            {
              "id": 503,
              "name": "Generic ABS",
              "material": "ABS"
            }
            """,
            statusCode: 201
        )

        do {
            _ = try await service.importSpool(barcode: "SP-503")
            XCTFail("Expected NetworkError.decodingFailed")
        } catch NetworkError.decodingFailed(let failure) {
            let description = NetworkError.decodingFailed(failure).errorDescription ?? ""
            XCTAssertEqual(failure.kind, "keyNotFound")
            XCTAssertEqual(failure.codingPath, "inUse")
            XCTAssertTrue(description.contains("keyNotFound"))
            XCTAssertTrue(description.contains("inUse"))
            XCTAssertTrue(description.contains("server version may be incompatible"))
            XCTAssertTrue(description.contains("update the server"))
        } catch {
            XCTFail("Unexpected error: \(error)")
        }
    }

    func testResolveFilamentDecodesFullBackendResponse() async throws {
        mockAPIClient.stubResponse(
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
              "gtin": "00123456789012",
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
        XCTAssertEqual(filament?.articleNumber, "PLA-GB-1000")
        XCTAssertEqual(filament?.gtin, "00123456789012")
        XCTAssertEqual(filament?.multiColorHexes, "#111111,#222222")
        XCTAssertEqual(filament?.externalId, "ext-701")
    }

    func testResolveFilamentDecodesIdOnlyBackendResponseWithOmittedNulls() async throws {
        mockAPIClient.stubResponse(json: #"{ "id": 702 }"#)

        let filament = try await service.resolveFilament(barcode: "FIL-702")

        XCTAssertEqual(filament?.id, 702)
        XCTAssertNil(filament?.name)
        XCTAssertNil(filament?.material)
        XCTAssertNil(filament?.colorHex)
        XCTAssertNil(filament?.vendor)
        XCTAssertNil(filament?.articleNumber)
        XCTAssertNil(filament?.gtin)
    }

    func testResolveFilamentDecodesLegacyArticleNumberWithoutGtin() async throws {
        mockAPIClient.stubResponse(
            json: """
            {
              "id": 703,
              "name": "Legacy PLA",
              "articleNumber": "012345678905"
            }
            """
        )

        let filament = try await service.resolveFilament(barcode: "012345678905")

        XCTAssertEqual(filament?.articleNumber, "012345678905")
        XCTAssertNil(filament?.gtin)
    }

    func testResolveFilamentRethrowsNon404Errors() async {
        mockAPIClient.requestHandler = { request in
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
            articleNumber: "PM70820",
            gtin: "00123456789012"
        )

        let json = try JSONSerialization.jsonObject(with: JSONEncoder().encode(request)) as? [String: Any]

        XCTAssertEqual(json?["vendorId"] as? Int, 42)
        XCTAssertNil(json?["vendor"])
        XCTAssertEqual(json?["articleNumber"] as? String, "PM70820")
        XCTAssertEqual(json?["gtin"] as? String, "00123456789012")
    }

    // MARK: - Filament create payload (issue #1067)

    func testNewFilamentAlwaysCarriesDensityAndDiameter() throws {
        let request = SpoolmanFilamentRequest.newFilament(
            name: "Sunlu PLA Grey",
            material: "PLA",
            vendorId: 7,
            colorHex: "#808080",
            weight: 1000,
            spoolWeight: 200,
            gtin: "6971170411231"
        )

        // Spoolman rejects a filament create with HTTP 422 when either field is missing.
        XCTAssertEqual(request.density, SpoolmanFilamentRequest.defaultDensityGramsPerCubicCentimeter)
        XCTAssertEqual(request.diameter, SpoolmanFilamentRequest.defaultDiameterMillimeters)

        let json = try JSONSerialization.jsonObject(with: JSONEncoder().encode(request)) as? [String: Any]
        XCTAssertNotNil(json?["density"])
        XCTAssertNotNil(json?["diameter"])
        XCTAssertEqual(json?["gtin"] as? String, "6971170411231")
        XCTAssertNil(json?["articleNumber"])
    }

    func testNewFilamentKeepsCallerSuppliedDensityAndDiameter() {
        let request = SpoolmanFilamentRequest.newFilament(
            name: "PETG",
            material: "PETG",
            density: 1.27,
            diameter: 2.85
        )

        XCTAssertEqual(request.density, 1.27)
        XCTAssertEqual(request.diameter, 2.85)
    }

    func testNewFilamentReplacesNonPositiveDensityAndDiameterWithDefaults() {
        let request = SpoolmanFilamentRequest.newFilament(
            name: "ABS",
            material: "ABS",
            density: 0,
            diameter: -1
        )

        XCTAssertEqual(request.density, SpoolmanFilamentRequest.defaultDensityGramsPerCubicCentimeter)
        XCTAssertEqual(request.diameter, SpoolmanFilamentRequest.defaultDiameterMillimeters)
    }

    func testNewFilamentDropsNonPositiveWeight() {
        let request = SpoolmanFilamentRequest.newFilament(
            name: "TPU",
            material: "TPU",
            weight: 0,
            spoolWeight: 0
        )

        // Spoolman requires weight > 0 when present, but allows spool_weight >= 0.
        XCTAssertNil(request.weight)
        XCTAssertEqual(request.spoolWeight, 0)
    }
}
