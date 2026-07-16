import XCTest
@testable import PrintFarmer

final class ScanViewModelTests: XCTestCase {
    @MainActor
    func testDispatchPrinterDeepLinkSetsPendingPrinterDestinationWithoutResolving() async {
        let (viewModel, partsService, barcodeService) = makeSubject()

        await viewModel.dispatch("printfarmer://printer/\(UUID().uuidString)")

        XCTAssertNotNil(viewModel.pendingPrinterDestination)
        XCTAssertNil(viewModel.pendingOutcome)
        XCTAssertTrue(partsService.resolveBinCodes.isEmpty)
        XCTAssertTrue(partsService.resolvePartBarcodes.isEmpty)
        XCTAssertTrue(barcodeService.resolveBarcodes.isEmpty)
        XCTAssertEqual(viewModel.recentScans.first?.title, "Printer")
    }

    @MainActor
    func testDispatchPrinterReadyDeepLinkSetsPendingPrinterDestination() async {
        let (viewModel, _, _) = makeSubject()
        let printerId = UUID()

        await viewModel.dispatch("printfarmer://printer/\(printerId.uuidString)/ready")

        guard case .printerReady(let id) = viewModel.pendingPrinterDestination else {
            return XCTFail("Expected .printerReady destination")
        }
        XCTAssertEqual(id, printerId)
    }

    @MainActor
    func testDispatchBinBarcodeResolvesBinAndSkipsPartAndSpoolResolution() async {
        let (viewModel, partsService, barcodeService) = makeSubject()
        let bin = makeBin(code: "BIN-01")
        partsService.binToResolve = bin

        await viewModel.dispatch("BIN-01")

        XCTAssertEqual(partsService.resolveBinCodes, ["BIN-01"])
        XCTAssertTrue(partsService.resolvePartBarcodes.isEmpty)
        XCTAssertTrue(barcodeService.resolveBarcodes.isEmpty)
        guard case .bin(let resolved) = viewModel.pendingOutcome else {
            return XCTFail("Expected .bin outcome")
        }
        XCTAssertEqual(resolved.id, bin.id)
        XCTAssertNil(viewModel.pendingPrinterDestination)
    }

    @MainActor
    func testDispatchFallsThroughBinNotFoundToPartResolution() async {
        let (viewModel, partsService, barcodeService) = makeSubject()
        partsService.resolveBinError = NetworkError.notFound
        let part = makePart(sku: "SKU-01")
        partsService.partToResolve = part

        await viewModel.dispatch("SKU-01")

        XCTAssertEqual(partsService.resolveBinCodes, ["SKU-01"])
        XCTAssertEqual(partsService.resolvePartBarcodes, ["SKU-01"])
        XCTAssertTrue(barcodeService.resolveBarcodes.isEmpty)
        guard case .part(let resolved) = viewModel.pendingOutcome else {
            return XCTFail("Expected .part outcome")
        }
        XCTAssertEqual(resolved.sku, "SKU-01")
    }

    // MARK: - H1a: feature-disabled must fall through, not block routing

    @MainActor
    func testDispatchFallsThroughFeatureDisabledBinResolutionToPartResolution() async {
        // #725 gate: a disabled printed-parts-inventory feature surfaces as
        // NetworkError.featureDisabled (distinct from .notFound) — it must
        // still fall through to part resolution, not stop and surface an
        // error, so operators on a server with the feature off can still
        // scan spools/printers via the same unified scan entry point.
        let (viewModel, partsService, barcodeService) = makeSubject()
        partsService.resolveBinError = NetworkError.featureDisabled(
            APIError(title: "Disabled", status: 404, detail: nil, errors: nil, message: nil, code: "featureDisabled")
        )
        let part = makePart(sku: "SKU-01")
        partsService.partToResolve = part

        await viewModel.dispatch("SKU-01")

        XCTAssertEqual(partsService.resolveBinCodes, ["SKU-01"])
        XCTAssertEqual(partsService.resolvePartBarcodes, ["SKU-01"])
        XCTAssertNil(viewModel.errorMessage, "feature-disabled must not surface as an error")
        guard case .part(let resolved) = viewModel.pendingOutcome else {
            return XCTFail("Expected .part outcome")
        }
        XCTAssertEqual(resolved.sku, "SKU-01")
    }

    @MainActor
    func testDispatchFallsThroughFeatureDisabledBinAndPartResolutionToSpoolResolution() async {
        // Both bin AND part resolution gated off must still reach spool
        // resolution — the feature gate must never block the pre-existing
        // spool/printer routing paths.
        let (viewModel, partsService, barcodeService) = makeSubject()
        let disabled = NetworkError.featureDisabled(
            APIError(title: "Disabled", status: 404, detail: nil, errors: nil, message: nil, code: "featureDisabled")
        )
        partsService.resolveBinError = disabled
        partsService.resolvePartError = disabled
        barcodeService.filamentToResolve = makeFilament(id: 3, name: "PLA Black")

        await viewModel.dispatch("012345678905")

        XCTAssertEqual(barcodeService.resolveBarcodes, ["012345678905"])
        XCTAssertEqual(viewModel.pendingSpoolBarcode, "012345678905")
        XCTAssertNil(viewModel.pendingOutcome)
        XCTAssertNil(viewModel.errorMessage)
    }

    @MainActor
    func testDispatchFallsThroughBinAndPartNotFoundToKnownSpoolBarcode() async {
        let (viewModel, partsService, barcodeService) = makeSubject()
        partsService.resolveBinError = NetworkError.notFound
        partsService.resolvePartError = NetworkError.notFound
        barcodeService.filamentToResolve = makeFilament(id: 3, name: "PLA Black")

        await viewModel.dispatch("012345678905")

        XCTAssertEqual(barcodeService.resolveBarcodes, ["012345678905"])
        XCTAssertEqual(viewModel.pendingSpoolBarcode, "012345678905")
        XCTAssertNil(viewModel.pendingOutcome)
    }

    @MainActor
    func testDispatchUnrecognizedCodeSetsUnknownOutcome() async {
        let (viewModel, partsService, barcodeService) = makeSubject()
        partsService.resolveBinError = NetworkError.notFound
        partsService.resolvePartError = NetworkError.notFound
        barcodeService.filamentToResolve = nil

        await viewModel.dispatch("garbage-code")

        guard case .unknownCode(let code) = viewModel.pendingOutcome else {
            return XCTFail("Expected .unknownCode outcome")
        }
        XCTAssertEqual(code, "garbage-code")
        XCTAssertNil(viewModel.pendingSpoolBarcode)
    }

    @MainActor
    func testDispatchBinResolutionNonNotFoundErrorSurfacesImmediatelyWithoutFallingThrough() async {
        let (viewModel, partsService, barcodeService) = makeSubject()
        partsService.resolveBinError = NetworkError.serverError(500)

        await viewModel.dispatch("BIN-CODE")

        XCTAssertNotNil(viewModel.errorMessage)
        XCTAssertNil(viewModel.pendingOutcome)
        XCTAssertTrue(partsService.resolvePartBarcodes.isEmpty)
        XCTAssertTrue(barcodeService.resolveBarcodes.isEmpty)
    }

    @MainActor
    func testDispatchPartResolutionNonNotFoundErrorSurfacesImmediatelyWithoutFallingThrough() async {
        let (viewModel, partsService, barcodeService) = makeSubject()
        partsService.resolveBinError = NetworkError.notFound
        partsService.resolvePartError = NetworkError.serverError(500)

        await viewModel.dispatch("SOME-CODE")

        XCTAssertNotNil(viewModel.errorMessage)
        XCTAssertNil(viewModel.pendingOutcome)
        XCTAssertTrue(barcodeService.resolveBarcodes.isEmpty)
    }

    @MainActor
    func testDispatchIgnoresBlankCode() async {
        let (viewModel, partsService, barcodeService) = makeSubject()

        await viewModel.dispatch("   ")

        XCTAssertNil(viewModel.pendingOutcome)
        XCTAssertNil(viewModel.pendingPrinterDestination)
        XCTAssertNil(viewModel.pendingSpoolBarcode)
        XCTAssertTrue(partsService.resolveBinCodes.isEmpty)
        XCTAssertTrue(barcodeService.resolveBarcodes.isEmpty)
    }

    @MainActor
    func testScanRecordsRecentScanForResolvedBin() async throws {
        let (viewModel, partsService, scanner) = makeSubjectWithScanner()
        let bin = makeBin(code: "BIN-77", name: "Shelf A")
        partsService.binToResolve = bin
        scanner.barcodeScanResultToReturn = .barcode("BIN-77")

        viewModel.scan()
        try await Task.sleep(nanoseconds: 80_000_000)

        XCTAssertFalse(viewModel.isScanning)
        XCTAssertEqual(viewModel.recentScans.first?.title, "Shelf A")
        XCTAssertEqual(viewModel.recentScans.first?.subtitle, "Bin BIN-77")
    }

    @MainActor
    func testScanUnavailableScannerSurfacesErrorWithoutDispatching() {
        let scanner = MockScannerService()
        scanner.mockIsAvailable = false
        let viewModel = ScanViewModel()
        let partsService = MockPartsInventoryService()
        let barcodeService = MockBarcodeIntakeService()
        viewModel.configure(scanner: scanner, partsInventoryService: partsService, barcodeIntakeService: barcodeService)

        viewModel.scan()

        XCTAssertNotNil(viewModel.errorMessage)
        XCTAssertFalse(viewModel.isScanning)
        XCTAssertEqual(scanner.barcodeScanCallCount, 0)
    }

    @MainActor
    func testScanCancelledWhileViewInactiveDoesNotDispatch() async throws {
        let (viewModel, partsService, scanner) = makeSubjectWithScanner()
        scanner.barcodeScanResultToReturn = .barcode("BIN-01")
        scanner.barcodeScanDelayNanoseconds = 40_000_000

        viewModel.scan()
        viewModel.isViewActive = false

        try await Task.sleep(nanoseconds: 90_000_000)

        XCTAssertNil(viewModel.pendingOutcome)
        XCTAssertTrue(partsService.resolveBinCodes.isEmpty)
    }

    @MainActor
    func testRecentScansCapAtTwentyEntries() async {
        let (viewModel, partsService, _) = makeSubject()

        for index in 0..<25 {
            partsService.binToResolve = makeBin(code: "BIN-\(index)")
            await viewModel.dispatch("BIN-\(index)")
        }

        XCTAssertEqual(viewModel.recentScans.count, 20)
        XCTAssertEqual(viewModel.recentScans.first?.subtitle, "Bin BIN-24")
    }

    // MARK: - Helpers

    @MainActor
    private func makeSubject() -> (ScanViewModel, MockPartsInventoryService, MockBarcodeIntakeService) {
        let viewModel = ScanViewModel()
        let partsService = MockPartsInventoryService()
        let barcodeService = MockBarcodeIntakeService()
        viewModel.configure(scanner: nil, partsInventoryService: partsService, barcodeIntakeService: barcodeService)
        return (viewModel, partsService, barcodeService)
    }

    @MainActor
    private func makeSubjectWithScanner() -> (ScanViewModel, MockPartsInventoryService, MockScannerService) {
        let viewModel = ScanViewModel()
        let partsService = MockPartsInventoryService()
        let barcodeService = MockBarcodeIntakeService()
        let scanner = MockScannerService()
        viewModel.configure(scanner: scanner, partsInventoryService: partsService, barcodeIntakeService: barcodeService)
        return (viewModel, partsService, scanner)
    }

    private func makeBin(code: String, name: String = "Bin") -> BinResponse {
        BinResponse(
            id: UUID(), code: code, name: name, location: nil, notes: nil,
            isActive: true, createdAt: .now, updatedAt: .now
        )
    }

    private func makePart(sku: String) -> PartInventoryResponse {
        PartInventoryResponse(
            id: UUID(), sku: sku, name: "Bracket", description: nil, modelFileRef: nil,
            defaultBinId: nil, defaultBinCode: nil, defaultBinName: nil,
            onHand: 10, reorderPoint: 5, needsReorder: false, isActive: true,
            createdAt: .now, updatedAt: .now
        )
    }

    private func makeFilament(id: Int, name: String) -> SpoolmanFilament {
        SpoolmanFilament(
            id: id, name: name, material: "PLA", colorHex: "#000000", vendor: "Vendor",
            density: 1.24, diameter: 1.75, weight: 1000, spoolWeight: 200, price: 25,
            settingsExtruderTemp: 215, settingsBedTemp: 60, articleNumber: nil,
            comment: nil, multiColorHexes: nil, externalId: nil
        )
    }
}
