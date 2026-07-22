import XCTest
@testable import PrintFarmer

final class BarcodeIntakeViewModelTests: XCTestCase {
    @MainActor
    func testKnownBarcodeInstantImportAppendsToTally() async {
        let (viewModel, service) = makeSubject()
        service.filamentToResolve = makeFilament(id: 7)
        service.spoolToImport = makeSpool(id: 42, filamentId: 7)

        await viewModel.handleScannedBarcode("012345678905")

        XCTAssertEqual(service.resolveBarcodes, ["012345678905"])
        XCTAssertEqual(service.importCalls.map(\.barcode), ["012345678905"])
        XCTAssertEqual(viewModel.importedThisSession.map(\.id), [42])
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
        XCTAssertNil(viewModel.pendingUnknownBarcode)
        XCTAssertNil(viewModel.errorMessage)
    }

    @MainActor
    func testUnknownBarcodeImportFailureKeepsPendingBarcodeAndSurfacesError() async {
        let (viewModel, service) = makeSubject()
        service.filamentToResolve = nil
        service.filamentToSave = makeFilament(id: 8)
        service.importError = NetworkError.serverError(500)

        await viewModel.handleScannedBarcode("ABC/DEF 12")
        let succeeded = await viewModel.importUnknownBarcode(filamentId: 8)

        XCTAssertFalse(succeeded)
        XCTAssertEqual(viewModel.pendingUnknownBarcode, "ABC/DEF 12")
        XCTAssertNotNil(viewModel.errorMessage)
        XCTAssertTrue(viewModel.importedThisSession.isEmpty)
        XCTAssertFalse(viewModel.isBusy)
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

        await viewModel.handleScannedBarcode("ABC123")

        XCTAssertEqual(viewModel.lastScannedBarcode, "ABC123")
        XCTAssertNotNil(viewModel.errorMessage)
        XCTAssertTrue(viewModel.importedThisSession.isEmpty)
        XCTAssertNil(viewModel.pendingUnknownBarcode)
        XCTAssertFalse(viewModel.isBusy)

        service.resolveError = nil
        service.filamentToResolve = makeFilament(id: 9)
        service.spoolToImport = makeSpool(id: 99, filamentId: 9)

        await viewModel.handleScannedBarcode("ABC123")

        XCTAssertEqual(viewModel.importedThisSession.map(\.id), [99])
        XCTAssertNil(viewModel.errorMessage)
    }

    @MainActor
    private func makeSubject() -> (BarcodeIntakeViewModel, MockBarcodeIntakeService) {
        let service = MockBarcodeIntakeService()
        let viewModel = BarcodeIntakeViewModel()
        viewModel.configure(barcodeService: service)
        return (viewModel, service)
    }

    private func makeFilament(id: Int) -> SpoolmanFilament {
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
            articleNumber: nil,
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
