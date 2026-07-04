import Foundation
@testable import PrintFarmer

final class MockBarcodeIntakeService: BarcodeIntakeServiceProtocol, @unchecked Sendable {
    var filamentToResolve: SpoolmanFilament?
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
        return filamentToResolve
    }

    func saveMapping(barcode: String, filamentId: Int) async throws -> SpoolmanFilament {
        saveMappingCalls.append((barcode, filamentId))
        if let saveMappingError { throw saveMappingError }
        guard let filamentToSave else { throw NetworkError.notFound }
        return filamentToSave
    }

    func importSpool(barcode: String, fields: SpoolImportFields) async throws -> SpoolmanSpool {
        importCalls.append((barcode, fields))
        if let importError { throw importError }
        guard let spoolToImport else { throw NetworkError.notFound }
        return spoolToImport
    }
}
