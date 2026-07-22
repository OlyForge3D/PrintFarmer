import Foundation

// MARK: - Barcode Intake Service Protocol

protocol BarcodeIntakeServiceProtocol: Sendable {
    func resolveFilament(barcode: String) async throws -> SpoolmanFilament?
    func saveMapping(barcode: String, filamentId: Int) async throws -> SpoolmanFilament
    func importSpool(barcode: String, fields: SpoolImportFields) async throws -> SpoolmanSpool
}

struct SpoolImportFields: Codable, Sendable, Equatable {
    var remainingWeight: Double?
    var initialWeight: Double?
    var spoolWeight: Double?
    var location: String?
    var lotNumber: String?
    var price: Double?
    var comment: String?

    init(
        remainingWeight: Double? = nil,
        initialWeight: Double? = nil,
        spoolWeight: Double? = nil,
        location: String? = nil,
        lotNumber: String? = nil,
        price: Double? = nil,
        comment: String? = nil
    ) {
        self.remainingWeight = remainingWeight
        self.initialWeight = initialWeight
        self.spoolWeight = spoolWeight
        self.location = location
        self.lotNumber = lotNumber
        self.price = price
        self.comment = comment
    }
}
