import XCTest
@testable import PrintFarmer

final class BarcodeIntakeViewModelTests: XCTestCase {
    @MainActor
    func testKnownBarcodeInstantImportAppendsToTally() async {
        let (viewModel, service) = makeSubject()
        service.filamentToResolve = makeFilament(
            id: 7,
            gtin: "00012345678905",
            articleNumber: "PRM-PLA-BK"
        )
        service.spoolToImport = makeSpool(id: 42, filamentId: 7)

        await viewModel.handleScannedBarcode("012345678905")

        XCTAssertEqual(service.resolveBarcodes, ["012345678905"])
        XCTAssertEqual(service.importCalls.map(\.barcode), ["012345678905"])
        XCTAssertEqual(viewModel.importedThisSession.map(\.id), [42])
        XCTAssertEqual(viewModel.lastResolvedBarcode, "00012345678905")
        XCTAssertEqual(viewModel.lastResolvedVendorSKU, "PRM-PLA-BK")
        XCTAssertNil(viewModel.pendingUnknownBarcode)
        XCTAssertNil(viewModel.errorMessage)
        XCTAssertFalse(viewModel.isBusy)
    }

    @MainActor
    func testUnknownBarcodeSetsPendingThenSaveMappingAndImportAfterResolution() async {
        let (viewModel, service) = makeSubject()
        service.filamentToResolve = nil
        service.filamentToSave = makeFilament(id: 8)
        service.spoolToImport = makeSpool(id: 77, filamentId: 8)

        await viewModel.handleScannedBarcode("4006381333931")

        XCTAssertEqual(viewModel.pendingUnknownBarcode, "4006381333931")
        XCTAssertTrue(viewModel.importedThisSession.isEmpty)

        let succeeded = await viewModel.importUnknownBarcode(filamentId: 8)

        XCTAssertTrue(succeeded)
        XCTAssertEqual(service.saveMappingCalls.count, 1)
        XCTAssertEqual(service.saveMappingCalls.first?.barcode, "4006381333931")
        XCTAssertEqual(service.saveMappingCalls.first?.filamentId, 8)
        XCTAssertEqual(service.importCalls.map(\.barcode), ["4006381333931"])
        XCTAssertEqual(viewModel.importedThisSession.map(\.id), [77])
        XCTAssertEqual(viewModel.lastResolvedBarcode, "00012345678905")
        XCTAssertEqual(viewModel.lastResolvedVendorSKU, "PRM-PLA-BK")
        XCTAssertNil(viewModel.pendingUnknownBarcode)
        XCTAssertNil(viewModel.errorMessage)
    }

    @MainActor
    func testUnknownBarcodeImportFailureKeepsPendingBarcodeAndSurfacesError() async {
        let (viewModel, service) = makeSubject()
        service.filamentToResolve = nil
        service.filamentToSave = makeFilament(id: 8)
        service.importError = NetworkError.serverError(500)

        await viewModel.handleScannedBarcode("4006381333931")
        let succeeded = await viewModel.importUnknownBarcode(filamentId: 8)

        XCTAssertFalse(succeeded)
        XCTAssertEqual(viewModel.pendingUnknownBarcode, "4006381333931")
        XCTAssertNotNil(viewModel.errorMessage)
        XCTAssertTrue(viewModel.importedThisSession.isEmpty)
        XCTAssertFalse(viewModel.isBusy)
    }

    @MainActor
    func testUnknownBarcodeSaveMappingFailureKeepsPendingAndNeverImports() async {
        let (viewModel, service) = makeSubject()
        service.filamentToResolve = nil
        service.saveMappingError = NetworkError.serverError(400)

        await viewModel.handleScannedBarcode("850078714923")
        let succeeded = await viewModel.importUnknownBarcode(filamentId: 8)

        XCTAssertFalse(succeeded)
        XCTAssertEqual(viewModel.pendingUnknownBarcode, "850078714923")
        XCTAssertNotNil(viewModel.errorMessage)
        XCTAssertTrue(service.importCalls.isEmpty, "a spool must not be created when the barcode mapping was not persisted")
        XCTAssertTrue(viewModel.importedThisSession.isEmpty)
    }

    @MainActor
    func testSkippingUnknownBarcodeCancelsWithoutPersistingOrCreatingSpool() async {
        let (viewModel, service) = makeSubject()
        service.filamentToResolve = nil
        service.filamentToSave = makeFilament(id: 8)

        await viewModel.handleScannedBarcode("850078714923")
        XCTAssertEqual(viewModel.pendingUnknownBarcode, "850078714923")
        viewModel.skipUnknownBarcode()

        XCTAssertNil(viewModel.pendingUnknownBarcode)
        XCTAssertNil(viewModel.errorMessage)
        XCTAssertTrue(service.saveMappingCalls.isEmpty)
        XCTAssertTrue(service.importCalls.isEmpty)
        let importedAfterCancel = await viewModel.importUnknownBarcode(filamentId: 8)
        XCTAssertFalse(importedAfterCancel, "a cancelled barcode must not be importable afterwards")
        XCTAssertTrue(service.saveMappingCalls.isEmpty)
    }

    @MainActor
    func testScanNextScannerErrorDoesNotWedgeScanningState() async throws {
        let service = MockBarcodeIntakeService()
        let scanner = MockScannerService()
        scanner.barcodeScanResultToReturn = .error(.notSupported)
        let viewModel = BarcodeIntakeViewModel()
        viewModel.configure(barcodeService: service, scanner: scanner)

        viewModel.scanNext()

        try await Task.sleep(nanoseconds: 50_000_000)
        XCTAssertEqual(scanner.barcodeScanCallCount, 1)
        XCTAssertFalse(viewModel.isScanning)
        XCTAssertEqual(viewModel.errorMessage, SpoolScanError.notSupported.localizedDescription)
    }

    @MainActor
    func testScanNextCompletedWhileInactiveResetsScanningStateAndIgnoresResult() async throws {
        let service = MockBarcodeIntakeService()
        service.filamentToResolve = makeFilament(id: 7)
        service.spoolToImport = makeSpool(id: 42, filamentId: 7)
        let scanner = MockScannerService()
        scanner.barcodeScanResultToReturn = .barcode("012345678905")
        scanner.barcodeScanDelayNanoseconds = 20_000_000
        let viewModel = BarcodeIntakeViewModel()
        viewModel.configure(barcodeService: service, scanner: scanner)

        viewModel.scanNext()
        viewModel.isViewActive = false

        try await Task.sleep(nanoseconds: 75_000_000)
        XCTAssertEqual(scanner.barcodeScanCallCount, 1)
        XCTAssertFalse(viewModel.isScanning)
        XCTAssertTrue(service.resolveBarcodes.isEmpty)
        XCTAssertTrue(service.importCalls.isEmpty)
        XCTAssertTrue(viewModel.importedThisSession.isEmpty)
        XCTAssertNil(viewModel.lastScannedBarcode)
    }

    @MainActor
    func testScanNextBarcodeResultImportsScannedBarcode() async throws {
        let service = MockBarcodeIntakeService()
        service.filamentToResolve = makeFilament(id: 7)
        service.spoolToImport = makeSpool(id: 42, filamentId: 7)
        let scanner = MockScannerService()
        scanner.barcodeScanResultToReturn = .barcode("012345678905")
        let viewModel = BarcodeIntakeViewModel()
        viewModel.configure(barcodeService: service, scanner: scanner)

        viewModel.scanNext()

        try await Task.sleep(nanoseconds: 50_000_000)
        XCTAssertEqual(scanner.barcodeScanCallCount, 1)
        XCTAssertFalse(viewModel.isScanning)
        XCTAssertEqual(viewModel.lastScannedBarcode, "012345678905")
        XCTAssertEqual(service.resolveBarcodes, ["012345678905"])
        XCTAssertEqual(service.importCalls.map(\.barcode), ["012345678905"])
        XCTAssertEqual(viewModel.importedThisSession.map(\.id), [42])
    }

    @MainActor
    func testScanNextCancelledScanResetsScanningState() async throws {
        let service = MockBarcodeIntakeService()
        let scanner = MockScannerService()
        scanner.barcodeScanResultToReturn = .cancelled
        let viewModel = BarcodeIntakeViewModel()
        viewModel.configure(barcodeService: service, scanner: scanner)

        viewModel.scanNext()

        try await Task.sleep(nanoseconds: 50_000_000)
        XCTAssertEqual(scanner.barcodeScanCallCount, 1)
        XCTAssertFalse(viewModel.isScanning)
        XCTAssertNil(viewModel.errorMessage)
        XCTAssertTrue(service.resolveBarcodes.isEmpty)
    }

    @MainActor
    func testResolveErrorSurfacesMessageWithoutAbortingSession() async {
        let (viewModel, service) = makeSubject()
        service.resolveError = NetworkError.serverError(500)

        await viewModel.handleScannedBarcode("96385074")

        XCTAssertEqual(viewModel.lastScannedBarcode, "96385074")
        XCTAssertNotNil(viewModel.errorMessage)
        XCTAssertTrue(viewModel.importedThisSession.isEmpty)
        XCTAssertNil(viewModel.pendingUnknownBarcode)
        XCTAssertFalse(viewModel.isBusy)

        service.resolveError = nil
        service.filamentToResolve = makeFilament(id: 9)
        service.spoolToImport = makeSpool(id: 99, filamentId: 9)

        await viewModel.handleScannedBarcode("96385074")

        XCTAssertEqual(viewModel.importedThisSession.map(\.id), [99])
        XCTAssertNil(viewModel.errorMessage)
    }

    @MainActor
    func testNonGtinValuesNeverResolveMapOrImport() async {
        // Barcode Intake's own camera also reports Code 128/39 and QR text.
        // The server strips non-digits, so none of these may be resolved,
        // mapped as a GTIN, or imported.
        let codes = ["LOT/850078714923", "ABC123", "SPL-AB12", "https://example.com/p/4006381333931",
                     "12345", "850078714924"]
        for code in codes {
            let (viewModel, service) = makeSubject()
            service.filamentToResolve = makeFilament(id: 7)

            await viewModel.handleScannedBarcode(code)
            let imported = await viewModel.importUnknownBarcode(filamentId: 7)

            XCTAssertEqual(viewModel.errorMessage, BarcodeIntakeViewModel.notProductBarcodeMessage, code)
            XCTAssertNil(viewModel.pendingUnknownBarcode, code)
            XCTAssertFalse(imported, code)
            XCTAssertFalse(viewModel.isBusy, code)
            XCTAssertTrue(service.resolveBarcodes.isEmpty, "\(code) must not be resolved")
            XCTAssertTrue(service.saveMappingCalls.isEmpty, "\(code) must not be mapped")
            XCTAssertTrue(service.importCalls.isEmpty, "\(code) must not be imported")
        }
    }

    @MainActor
    func testScanNextNonGtinResultIsRejectedBeforeResolution() async throws {
        let service = MockBarcodeIntakeService()
        let scanner = MockScannerService()
        scanner.barcodeScanResultToReturn = .barcode("LOT-A1B2")
        let viewModel = BarcodeIntakeViewModel()
        viewModel.configure(barcodeService: service, scanner: scanner)

        viewModel.scanNext()

        try await Task.sleep(nanoseconds: 50_000_000)
        XCTAssertEqual(viewModel.errorMessage, BarcodeIntakeViewModel.notProductBarcodeMessage)
        XCTAssertTrue(service.resolveBarcodes.isEmpty)
        XCTAssertTrue(service.saveMappingCalls.isEmpty)
        XCTAssertTrue(service.importCalls.isEmpty)
    }

    @MainActor
    private func makeSubject() -> (BarcodeIntakeViewModel, MockBarcodeIntakeService) {
        let service = MockBarcodeIntakeService()
        let viewModel = BarcodeIntakeViewModel()
        viewModel.configure(barcodeService: service)
        return (viewModel, service)
    }

    private func makeFilament(
        id: Int,
        gtin: String? = "00012345678905",
        articleNumber: String? = "PRM-PLA-BK"
    ) -> SpoolmanFilament {
        SpoolmanFilament(
            id: id,
            name: "PLA Black",
            material: "PLA",
            colorHex: "#000000",
            vendor: "Prusa Research",
            density: 1.24,
            diameter: 1.75,
            weight: 1000,
            spoolWeight: 200,
            price: 25,
            settingsExtruderTemp: 215,
            settingsBedTemp: 60,
            articleNumber: articleNumber,
            gtin: gtin,
            comment: nil,
            multiColorHexes: nil,
            externalId: nil
        )
    }

    private func makeSpool(id: Int, filamentId: Int) -> SpoolmanSpool {
        SpoolmanSpool(
            id: id,
            filamentId: filamentId,
            name: "PLA Black",
            material: "PLA",
            colorHex: "#000000",
            inUse: false,
            filamentName: "PLA Black",
            vendor: "Prusa Research",
            registeredAt: nil,
            firstUsedAt: nil,
            lastUsedAt: nil,
            remainingWeightG: 1000,
            initialWeightG: 1000,
            usedWeightG: 0,
            spoolWeightG: 200,
            remainingLengthMm: nil,
            usedLengthMm: nil,
            location: nil,
            lotNumber: nil,
            archived: false,
            price: 25,
            comment: nil,
            hasNfcTag: false,
            usedPercent: 0,
            remainingPercent: 100
        )
    }
}
