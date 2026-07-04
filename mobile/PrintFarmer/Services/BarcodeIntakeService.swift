import Foundation

// MARK: - Barcode Intake Service

actor BarcodeIntakeService: BarcodeIntakeServiceProtocol {
    private let apiClient: APIClient

    init(apiClient: APIClient) {
        self.apiClient = apiClient
    }

    func resolveFilament(barcode: String) async throws -> SpoolmanFilament? {
        do {
            return try await apiClient.get("/api/spoolman/filaments/by-barcode?code=\(Self.encodedQueryValue(barcode))")
        } catch NetworkError.notFound {
            return nil
        }
    }

    func saveMapping(barcode: String, filamentId: Int) async throws -> SpoolmanFilament {
        try await apiClient.post(
            "/api/spoolman/barcodes",
            body: BarcodeMappingRequest(barcode: barcode, filamentId: filamentId)
        )
    }

    func importSpool(barcode: String, fields: SpoolImportFields = SpoolImportFields()) async throws -> SpoolmanSpool {
        try await apiClient.post(
            "/api/spoolman/spools/by-barcode",
            body: BarcodeSpoolImportRequest(barcode: barcode, fields: fields)
        )
    }

    private static func encodedQueryValue(_ value: String) -> String {
        var allowed = CharacterSet.urlQueryAllowed
        allowed.remove(charactersIn: ":#[]@!$&'()*+,;=/?% ")
        return value.addingPercentEncoding(withAllowedCharacters: allowed) ?? value
    }
}

private struct BarcodeMappingRequest: Encodable, Sendable {
    let barcode: String
    let filamentId: Int
}

private struct BarcodeSpoolImportRequest: Encodable, Sendable {
    let barcode: String
    let remainingWeight: Double?
    let initialWeight: Double?
    let spoolWeight: Double?
    let location: String?
    let lotNumber: String?
    let price: Double?
    let comment: String?

    init(barcode: String, fields: SpoolImportFields) {
        self.barcode = barcode
        self.remainingWeight = fields.remainingWeight
        self.initialWeight = fields.initialWeight
        self.spoolWeight = fields.spoolWeight
        self.location = fields.location
        self.lotNumber = fields.lotNumber
        self.price = fields.price
        self.comment = fields.comment
    }
}
