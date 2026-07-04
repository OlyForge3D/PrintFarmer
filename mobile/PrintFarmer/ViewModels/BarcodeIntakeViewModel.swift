import Foundation
import os

@MainActor @Observable
final class BarcodeIntakeViewModel {
    var isScanning = false
    var lastScannedBarcode: String?
    var importedThisSession: [SpoolmanSpool] = []
    var pendingUnknownBarcode: String?
    var isBusy = false
    var errorMessage: String?
    var isViewActive = true

    var importedCount: Int { importedThisSession.count }

    private let logger = Logger(subsystem: "com.printfarmer.ios", category: "BarcodeIntake")
    private var barcodeService: (any BarcodeIntakeServiceProtocol)?
    private var scanner: (any BarcodeScannerProtocol)?

    func configure(barcodeService: any BarcodeIntakeServiceProtocol, scanner: (any BarcodeScannerProtocol)? = nil) {
        self.barcodeService = barcodeService
        self.scanner = scanner
    }

    func scanNext() {
        guard let scanner, scanner.isAvailable else {
            errorMessage = "Barcode scanning is not available on this device."
            return
        }

        isScanning = true
        errorMessage = nil

        Task {
            defer { isScanning = false }
            let result = await scanner.scanBarcode()
            guard isViewActive else { return }
            switch result {
            case .barcode(let barcode):
                await handleScannedBarcode(barcode)
            case .cancelled:
                break
            case .error(let error):
                errorMessage = error.localizedDescription
            }
        }
    }

    func handleScannedBarcode(_ barcode: String) async {
        let trimmed = barcode.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return }
        guard let barcodeService else {
            errorMessage = "Barcode intake service not available"
            return
        }

        lastScannedBarcode = trimmed
        isBusy = true
        errorMessage = nil

        do {
            if try await barcodeService.resolveFilament(barcode: trimmed) != nil {
                let spool = try await barcodeService.importSpool(barcode: trimmed, fields: SpoolImportFields())
                importedThisSession.insert(spool, at: 0)
                pendingUnknownBarcode = nil
            } else {
                pendingUnknownBarcode = trimmed
            }
        } catch {
            logger.warning("Barcode intake failed: \(error.localizedDescription)")
            errorMessage = error.localizedDescription
        }

        isBusy = false
    }

    @discardableResult
    func importUnknownBarcode(with filament: SpoolmanFilament) async -> Bool {
        await importUnknownBarcode(filamentId: filament.id)
    }

    @discardableResult
    func importUnknownBarcode(filamentId: Int) async -> Bool {
        guard let barcode = pendingUnknownBarcode else { return false }
        guard let barcodeService else {
            errorMessage = "Barcode intake service not available"
            return false
        }

        isBusy = true
        errorMessage = nil

        do {
            _ = try await barcodeService.saveMapping(barcode: barcode, filamentId: filamentId)
            let spool = try await barcodeService.importSpool(barcode: barcode, fields: SpoolImportFields())
            importedThisSession.insert(spool, at: 0)
            pendingUnknownBarcode = nil
            isBusy = false
            return true
        } catch {
            logger.warning("Unknown barcode resolution failed: \(error.localizedDescription)")
            errorMessage = error.localizedDescription
        }

        isBusy = false
        return false
    }

    func skipUnknownBarcode() {
        pendingUnknownBarcode = nil
        errorMessage = nil
    }

    func clearError() {
        errorMessage = nil
    }
}
