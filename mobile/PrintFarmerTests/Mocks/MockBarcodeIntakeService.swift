import Foundation
@testable import PrintFarmer

final class MockBarcodeIntakeService: BarcodeIntakeServiceProtocol, @unchecked Sendable {
    /// Represents the deterministic filament selected by the server; the mock does not
    /// index by GTIN because multiple filament records may share the same value.
    var filamentToResolve: SpoolmanFilament?
    /// Opt-in model of the server persisting `POST /api/spoolman/barcodes`:
    /// once a mapping is saved, later resolves of that exact barcode return
    /// the saved filament, mirroring the filament `gtin` lookup.
    var persistsSavedMappings = false
    private(set) var savedMappings: [String: SpoolmanFilament] = [:]
    var filamentToSave: SpoolmanFilament?
    var spoolToImport: SpoolmanSpool?
    var resolveError: Error?
    var saveMappingError: Error?
    var importError: Error?

    private(set) var resolveBarcodes: [String] = []
    private(set) var saveMappingCalls: [(barcode: String, filamentId: Int)] = []
    private(set) var importCalls: [(barcode: String, fields: SpoolImportFields)] = []

    func resolveFilament(barcode: String) async throws -> SpoolmanFilament? {
        resolveBarcodes.append(barcode)
        if let resolveError { throw resolveError }
        if persistsSavedMappings, let saved = savedMappings[barcode] { return saved }
        return filamentToResolve
    }

    func saveMapping(barcode: String, filamentId: Int) async throws -> SpoolmanFilament {
        saveMappingCalls.append((barcode, filamentId))
        if let saveMappingError { throw saveMappingError }
        guard let filamentToSave else { throw NetworkError.notFound }
        if persistsSavedMappings { savedMappings[barcode] = filamentToSave }
        return filamentToSave
    }

    func importSpool(barcode: String, fields: SpoolImportFields) async throws -> SpoolmanSpool {
        importCalls.append((barcode, fields))
        if let importError { throw importError }
        guard let spoolToImport else { throw NetworkError.notFound }
        return spoolToImport
    }

    // MARK: Remembered non-GTIN codes

    /// Models the server's PrintFarmer-side alias table: exact (case-sensitive)
    /// keys, conflicts for a different filament, idempotent repeat saves.
    var aliases: [String: SpoolmanFilament] = [:]
    var filamentsById: [Int: SpoolmanFilament] = [:]
    var aliasLookupError: Error?
    /// When set, returned instead of the table result (e.g. `.filamentMissing`).
    var aliasLookupOverride: ScanCodeAliasLookup?
    var aliasSaveError: Error?
    var createSpoolError: Error?
    var spoolToCreate: SpoolmanSpool?
    private(set) var aliasLookups: [String] = []
    private(set) var aliasSaveCalls: [(code: String, filamentId: Int)] = []
    private(set) var createSpoolCalls: [Int] = []

    func lookupCodeAlias(code: String) async throws -> ScanCodeAliasLookup {
        aliasLookups.append(code)
        if let aliasLookupError { throw aliasLookupError }
        if let aliasLookupOverride { return aliasLookupOverride }
        guard let filament = aliases[code] else {
            return ScanCodeAliasLookup(code: code, state: .notRemembered, alias: nil)
        }
        return ScanCodeAliasLookup(
            code: code,
            state: .found,
            alias: ScanCodeAlias(id: 1, code: code, filamentId: filament.id, filament: filament)
        )
    }

    func saveCodeAlias(code: String, filamentId: Int) async throws -> ScanCodeAlias {
        aliasSaveCalls.append((code, filamentId))
        if let aliasSaveError { throw aliasSaveError }
        if let existing = aliases[code], existing.id != filamentId { throw NetworkError.conflict(nil) }
        guard let filament = filamentsById[filamentId] else { throw NetworkError.notFound }
        aliases[code] = filament
        return ScanCodeAlias(id: 1, code: code, filamentId: filamentId, filament: filament)
    }

    func createSpool(filamentId: Int) async throws -> SpoolmanSpool {
        createSpoolCalls.append(filamentId)
        if let createSpoolError { throw createSpoolError }
        guard let spoolToCreate else { throw NetworkError.notFound }
        return spoolToCreate
    }
}
