import Foundation

// MARK: - Barcode Intake Service Protocol

protocol BarcodeIntakeServiceProtocol: Sendable {
    /// Sends the scanned value unchanged. The server normalizes and validates GTINs,
    /// falls back to legacy `article_number` records, and chooses a deterministic
    /// match when multiple filaments share a GTIN.
    func resolveFilament(barcode: String) async throws -> SpoolmanFilament?

    /// Sends the scanned value unchanged for the server to persist in `gtin`.
    /// Vendor `articleNumber` is a separate SKU and is not changed by this operation.
    func saveMapping(barcode: String, filamentId: Int) async throws -> SpoolmanFilament

    /// Sends the scanned value unchanged; GTIN normalization and validation stay server-side.
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
