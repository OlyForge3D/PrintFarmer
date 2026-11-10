import Foundation

// MARK: - Barcode Intake Service Protocol

protocol BarcodeIntakeServiceProtocol: Sendable {
    /// Sends the scanned value unchanged. The server normalizes and validates GTINs,
    /// resolves against `gtin` only, and chooses a deterministic match when multiple
    /// filaments share a GTIN.
    func resolveFilament(barcode: String) async throws -> SpoolmanFilament?

    /// Sends the scanned value unchanged for the server to persist in `gtin`.
    /// Vendor `articleNumber` is a separate SKU and is not changed by this operation.
    func saveMapping(barcode: String, filamentId: Int) async throws -> SpoolmanFilament

    /// Sends the scanned value unchanged; GTIN normalization and validation stay server-side.
    func importSpool(barcode: String, fields: SpoolImportFields) async throws -> SpoolmanSpool

    /// Looks up a remembered non-GTIN code (e.g. an Amazon FNSKU) by exact match.
    /// A genuine miss returns `.notRemembered`; transport, auth and server
    /// failures throw so callers never treat an outage as a miss.
    func lookupCodeAlias(code: String) async throws -> ScanCodeAliasLookup

    /// Remembers a non-GTIN code for the explicitly chosen filament. Stored in
    /// PrintFarmer, separate from the Spoolman `gtin` mapping.
    func saveCodeAlias(code: String, filamentId: Int) async throws -> ScanCodeAlias

    /// Creates a spool of exactly `filamentId`.
    func createSpool(filamentId: Int) async throws -> SpoolmanSpool
}

enum ScanCodeAliasLookupState: String, Codable, Sendable {
    case found
    case notRemembered
    case filamentMissing
}

struct ScanCodeAlias: Codable, Sendable {
    let id: Int
    let code: String
    let filamentId: Int
    let filament: SpoolmanFilament?
}

struct ScanCodeAliasLookup: Codable, Sendable {
    let code: String
    let state: ScanCodeAliasLookupState
    let alias: ScanCodeAlias?

    /// The remembered filament, only when the alias resolved.
    var filament: SpoolmanFilament? { state == .found ? alias?.filament : nil }
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
