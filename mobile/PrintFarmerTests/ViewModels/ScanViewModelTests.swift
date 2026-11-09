import XCTest
import Observation
@testable import PrintFarmer

final class ScanViewModelTests: XCTestCase {
    @MainActor
    private func runNFCScan(_ result: SpoolScanResult, viewModel: ScanViewModel) async {
        let scanner = MockQRSpoolScannerService()
        scanner.resultToReturn = result
        viewModel.configureNFC(scanner: scanner)
        viewModel.scanNFC()
        XCTAssertTrue(viewModel.isScanning)
        let finished = expectation(description: "NFC scan finishes")
        withObservationTracking {
            _ = viewModel.isScanning
        } onChange: {
            finished.fulfill()
        }
        await fulfillment(of: [finished], timeout: 2)
        XCTAssertFalse(viewModel.isScanning)
        XCTAssertEqual(scanner.scanCallCount, 1)
    }

    @MainActor
    func testNFCPrinterTagIsRejectedAsNotASpoolTag() async {
        let (viewModel, barcodeService, spoolService) = makeSubject()
        await runNFCScan(.printerId(UUID()), viewModel: viewModel)
        XCTAssertNil(viewModel.pendingDeepLinkDestination, "the scanner opens spools only")
        XCTAssertNil(viewModel.pendingOutcome)
        XCTAssertEqual(viewModel.errorMessage, ScanViewModel.notSpoolTagMessage)
        XCTAssertTrue(barcodeService.resolveBarcodes.isEmpty)
        XCTAssertFalse(spoolService.listSpoolsCalled)
    }

    @MainActor
    func testNFCSpoolRoutesOnlyAfterActiveServerLookup() async {
        let (viewModel, _, spoolService) = makeSubject()
        spoolService.spoolsPageToReturn = SpoolmanPagedResult(items: [makeSpool(id: 42)], totalCount: 1)
        await runNFCScan(.spoolId(42), viewModel: viewModel)
        XCTAssertEqual(viewModel.pendingDeepLinkDestination, .spoolDetail(id: 42))
        XCTAssertTrue(spoolService.listSpoolsCalled)
    }

    @MainActor
    func testNFCNewSpoolReusesIntakeAndErrorsAreVisible() async {
        let (viewModel, _, _) = makeSubject()
        let data = ScannedSpoolData(material: "PLA", colorHex: nil, vendor: nil, weight: nil,
                                    diameter: nil, temperature: nil, spoolmanId: nil)
        await runNFCScan(.newSpoolData(data), viewModel: viewModel)
        guard case .newSpool(let parsed) = viewModel.pendingOutcome else {
            return XCTFail("Expected AddSpool intake")
        }
        XCTAssertEqual(parsed.material, "PLA")
        let (failed, _, _) = makeSubject()
        await runNFCScan(.error(.notSupported), viewModel: failed)
        XCTAssertNotNil(failed.errorMessage)
        XCTAssertNil(failed.pendingDeepLinkDestination)
    }

    @MainActor
    func testDispatchPrinterDeepLinkIsNotRoutedFromSpoolScanner() async {
        let (viewModel, barcodeService, spoolService) = makeSubject()

        await viewModel.dispatch("printfarmer://printer/\(UUID().uuidString)/ready")

        XCTAssertNil(viewModel.pendingDeepLinkDestination, "printer codes are no longer routed by the spool scanner")
        XCTAssertNil(viewModel.pendingSpoolBarcode)
        guard case .unknownCode = viewModel.pendingOutcome else {
            return XCTFail("Expected .unknownCode outcome for a non-spool code")
        }
        XCTAssertTrue(barcodeService.resolveBarcodes.isEmpty, "non-spool deep links must never reach barcode resolution")
        XCTAssertFalse(spoolService.listSpoolsCalled)
    }

    @MainActor
    func testPrinterDeepLinkWhoseDigitsFormAKnownGtinNeverOpensIntake() async {
        // The server's GTIN normalizer strips non-digits; this printer UUID's
        // digits are the valid, mapped UPC-A 850078714923.
        let (viewModel, barcodeService, spoolService) = makeSubject()
        barcodeService.filamentToResolve = makeFilament(id: 9, name: "PETG Blue")

        await viewModel.dispatch("printfarmer://printer/85007871-4923-aaaa-aaaa-aaaaaaaaaaaa")

        guard case .unknownCode = viewModel.pendingOutcome else {
            return XCTFail("Expected .unknownCode outcome for a printer link")
        }
        XCTAssertNil(viewModel.pendingSpoolBarcode, "a printer link must never create a spool")
        XCTAssertNil(viewModel.pendingDeepLinkDestination)
        XCTAssertTrue(barcodeService.resolveBarcodes.isEmpty)
        XCTAssertTrue(barcodeService.saveMappingCalls.isEmpty)
        XCTAssertTrue(barcodeService.importCalls.isEmpty)
        XCTAssertFalse(spoolService.listSpoolsCalled)
    }

    @MainActor
    func testNonProductCodeResolvedByPermissiveServerDoesNotOpenIntake() async {
        let (viewModel, barcodeService, _) = makeSubject()
        barcodeService.filamentToResolve = makeFilament(id: 9, name: "PETG Blue")

        await viewModel.dispatch("LOT/850078714923")

        XCTAssertNil(viewModel.pendingSpoolBarcode, "only supported product barcodes may open intake")
        guard case .unknownCode = viewModel.pendingOutcome else {
            return XCTFail("Expected .unknownCode outcome for a non-product code")
        }
        XCTAssertTrue(barcodeService.importCalls.isEmpty)
    }

    // MARK: - Structured spool QR/deep-link recognition with active-server existence confirmation

    @MainActor
    func testDispatchSpoolDeepLinkRoutesAfterExistenceConfirmedWithoutBarcodeIntake() async {
        let (viewModel, barcodeService, spoolService) = makeSubject()
        spoolService.spoolsPageToReturn = SpoolmanPagedResult(items: [makeSpool(id: 42)], totalCount: 1)

        await viewModel.dispatch("printfarmer://spool/42")

        XCTAssertEqual(viewModel.pendingDeepLinkDestination, .spoolDetail(id: 42))
        XCTAssertNil(viewModel.pendingOutcome)
        XCTAssertTrue(barcodeService.resolveBarcodes.isEmpty, "a deep-linked spool ID must never reach Barcode Intake")
        XCTAssertTrue(spoolService.listSpoolsCalled)
        XCTAssertEqual(viewModel.recentScans.first?.title, "Spool")
    }

    @MainActor
    func testDispatchSpoolDeepLinkNotFoundOnServerSurfacesErrorWithoutRoutingOrFallback() async {
        let (viewModel, barcodeService, spoolService) = makeSubject()
        spoolService.spoolsPageToReturn = SpoolmanPagedResult(items: [], totalCount: 0)

        await viewModel.dispatch("printfarmer://spool/42")

        XCTAssertNil(viewModel.pendingDeepLinkDestination, "an ID absent from the active server must never route to spool detail")
        XCTAssertNil(viewModel.pendingOutcome)
        XCTAssertNotNil(viewModel.errorMessage)
        XCTAssertTrue(barcodeService.resolveBarcodes.isEmpty, "a not-found spool ID must never fall back to raw barcode registration")
    }

    @MainActor
    func testDispatchSpoolDeepLinkExistenceLookupNetworkErrorSurfacesErrorWithoutRoutingOrFallback() async {
        let (viewModel, barcodeService, spoolService) = makeSubject()
        spoolService.errorToThrow = NetworkError.serverError(500)

        await viewModel.dispatch("printfarmer://spool/42")

        XCTAssertNil(viewModel.pendingDeepLinkDestination, "a failed existence lookup must never route to spool detail")
        XCTAssertNotNil(viewModel.errorMessage)
        XCTAssertTrue(barcodeService.resolveBarcodes.isEmpty, "an existence-lookup error must never fall back to raw barcode registration")
    }

    @MainActor
    func testDispatchSpoolExistenceLookupUsesTheInjectedActiveServerSpoolService() async {
        let (viewModel, _, spoolService) = makeSubject()
        spoolService.spoolsPageToReturn = SpoolmanPagedResult(items: [makeSpool(id: 42)], totalCount: 1)

        await viewModel.dispatch("printfarmer://spool/42")

        XCTAssertEqual(spoolService.listSpoolsArgs?.limit, 500)
        XCTAssertEqual(spoolService.listSpoolsArgs?.offset, 0)
    }

    @MainActor
    func testDispatchStructuredSpoolURLRoutesAfterExistenceConfirmedNeverTouchingBarcodeIntake() async {
        let (viewModel, barcodeService, spoolService) = makeSubject()
        spoolService.spoolsPageToReturn = SpoolmanPagedResult(items: [makeSpool(id: 42)], totalCount: 1)

        await viewModel.dispatch("https://spoolman.example.com/spools/42")

        XCTAssertEqual(viewModel.pendingDeepLinkDestination, .spoolDetail(id: 42))
        XCTAssertTrue(barcodeService.resolveBarcodes.isEmpty, "a structured spool URL must never reach Barcode Intake")
        XCTAssertNil(viewModel.pendingOutcome)
    }

    @MainActor
    func testDispatchStructuredSpoolURLNotFoundNeverFallsBackToBarcodeIntake() async {
        let (viewModel, barcodeService, spoolService) = makeSubject()
        spoolService.spoolsPageToReturn = SpoolmanPagedResult(items: [], totalCount: 0)

        await viewModel.dispatch("https://spoolman.example.com/spools/42")

        XCTAssertNil(viewModel.pendingDeepLinkDestination)
        XCTAssertNotNil(viewModel.errorMessage)
        XCTAssertTrue(barcodeService.resolveBarcodes.isEmpty, "a structured payload must never fall back to raw barcode registration, found or not")
    }

    // MARK: - Bare numeric spool IDs

    @MainActor
    func testDispatchBareNumericSpoolIdRoutesAfterBarcodeMissAndExistenceConfirmed() async {
        let (viewModel, barcodeService, spoolService) = makeSubject()
        barcodeService.filamentToResolve = nil
        spoolService.spoolsPageToReturn = SpoolmanPagedResult(items: [makeSpool(id: 77)], totalCount: 1)

        await viewModel.dispatch("77")

        XCTAssertEqual(barcodeService.resolveBarcodes, ["77"], "a mapped barcode must still win over a spool ID")
        XCTAssertEqual(viewModel.pendingDeepLinkDestination, .spoolDetail(id: 77))
        XCTAssertNil(viewModel.pendingSpoolBarcode, "a non-GTIN spool ID must not open barcode intake")
        XCTAssertNil(viewModel.pendingOutcome)
    }

    @MainActor
    func testDispatchBareNumericNotFoundOnServerSurfacesErrorNotUnknownCode() async {
        let (viewModel, barcodeService, spoolService) = makeSubject()
        barcodeService.filamentToResolve = nil
        spoolService.spoolsPageToReturn = SpoolmanPagedResult(items: [], totalCount: 0)

        await viewModel.dispatch("77")

        XCTAssertNil(viewModel.pendingDeepLinkDestination)
        XCTAssertNil(viewModel.pendingOutcome)
        XCTAssertNil(viewModel.pendingSpoolBarcode)
        XCTAssertNotNil(viewModel.errorMessage)
    }

    @MainActor
    func testDispatchNumericCodeWithInvalidCheckDigitIsTreatedAsSpoolIdNotNewBarcode() async {
        let (viewModel, barcodeService, spoolService) = makeSubject()
        barcodeService.filamentToResolve = nil
        spoolService.spoolsPageToReturn = SpoolmanPagedResult(items: [], totalCount: 0)

        await viewModel.dispatch("850078714924")

        XCTAssertNil(viewModel.pendingSpoolBarcode, "the server rejects GTINs with a bad check digit, so intake must not be offered")
        XCTAssertTrue(spoolService.listSpoolsCalled)
        XCTAssertNotNil(viewModel.errorMessage)
    }

    // MARK: - Product barcodes (known and unknown)

    @MainActor
    func testDispatchKnownProductBarcodeOpensIntakeWithoutSpoolLookup() async {
        let (viewModel, barcodeService, spoolService) = makeSubject()
        barcodeService.filamentToResolve = makeFilament(id: 9, name: "PETG Blue")

        await viewModel.dispatch("012345678905")

        XCTAssertEqual(barcodeService.resolveBarcodes, ["012345678905"])
        XCTAssertEqual(viewModel.pendingSpoolBarcode, "012345678905")
        XCTAssertNil(viewModel.pendingDeepLinkDestination)
        XCTAssertNil(viewModel.pendingOutcome)
        XCTAssertFalse(spoolService.listSpoolsCalled, "a known barcode must never trigger a spool existence lookup")
        XCTAssertEqual(viewModel.recentScans.first?.title, "PETG Blue")
    }

    @MainActor
    func testDispatchUnknownProductBarcodesOfferSpoolCreationInsteadOfUnrecognized() async {
        // Regression: unknown GTINs (including numeric UPC/EAN codes) used to
        // fall through to the spool-ID lookup ("Spool #N was not found") or
        // the "Unrecognized code" sheet. They must hand off to intake.
        let codes = [
            "850078714923",       // UPC-A
            "4006381333931",      // EAN-13
            "96385074",           // EAN-8
            "00012345678905",     // GTIN-14
            "4-006381-333931",    // separators allowed by the server normalizer
            "  850078714923\n"    // surrounding whitespace from the scanner
        ]
        for code in codes {
            let (viewModel, barcodeService, spoolService) = makeSubject()
            barcodeService.filamentToResolve = nil

            await viewModel.dispatch(code)

            let trimmed = code.trimmingCharacters(in: .whitespacesAndNewlines)
            XCTAssertEqual(barcodeService.resolveBarcodes, [trimmed], code)
            XCTAssertEqual(viewModel.pendingSpoolBarcode, trimmed, "unknown \(code) must open spool intake")
            XCTAssertNil(viewModel.pendingOutcome, code)
            XCTAssertNil(viewModel.pendingDeepLinkDestination, code)
            XCTAssertNil(viewModel.errorMessage, code)
            XCTAssertEqual(
                spoolService.listSpoolsCalled, QRCodeParser.parse(trimmed) != nil,
                "a numeric unknown \(code) checks for an existing spool before intake"
            )
            XCTAssertEqual(viewModel.recentScans.first?.title, "New spool barcode", code)
        }
    }

    @MainActor
    func testUnmappedGtinThatIsAlsoAnExistingSpoolIdOpensTheSpoolNotIntake() async {
        // Regression: a numeric spool QR whose value is also a valid GTIN
        // must keep opening the existing spool when no filament is mapped.
        let (viewModel, barcodeService, spoolService) = makeSubject()
        barcodeService.filamentToResolve = nil
        spoolService.spoolsPageToReturn = SpoolmanPagedResult(items: [makeSpool(id: 850078714923)], totalCount: 1)

        await viewModel.dispatch("850078714923")

        XCTAssertEqual(viewModel.pendingDeepLinkDestination, .spoolDetail(id: 850078714923))
        XCTAssertNil(viewModel.pendingSpoolBarcode, "an existing spool must not be offered for new-spool intake")
        XCTAssertNil(viewModel.pendingOutcome)
        XCTAssertNil(viewModel.errorMessage)
        XCTAssertTrue(barcodeService.saveMappingCalls.isEmpty)
        XCTAssertTrue(barcodeService.importCalls.isEmpty)
    }

    @MainActor
    func testUnmappedGtinSpoolLookupFailureSurfacesErrorWithoutOfferingIntake() async {
        let (viewModel, barcodeService, spoolService) = makeSubject()
        barcodeService.filamentToResolve = nil
        spoolService.errorToThrow = NetworkError.serverError(500)

        await viewModel.dispatch("850078714923")

        XCTAssertNotNil(viewModel.errorMessage)
        XCTAssertNil(viewModel.pendingSpoolBarcode, "an unverified spool ID must fail closed, not open intake")
        XCTAssertNil(viewModel.pendingDeepLinkDestination)
        XCTAssertNil(viewModel.pendingOutcome)
    }

    @MainActor
    func testUnknownBarcodeScanPersistsMappingThroughIntakeAndResolvesOnRepeatScan() async {
        let barcodeService = MockBarcodeIntakeService()
        barcodeService.persistsSavedMappings = true
        barcodeService.filamentToSave = makeFilament(id: 12, name: "PLA Galaxy")
        barcodeService.spoolToImport = makeSpool(id: 501)
        let (scan, _, _) = makeSubject(barcodeService: barcodeService)

        await scan.dispatch("850078714923")
        let handedOff = scan.pendingSpoolBarcode
        XCTAssertEqual(handedOff, "850078714923")
        XCTAssertTrue(barcodeService.saveMappingCalls.isEmpty, "scanning alone must never persist or create anything")
        XCTAssertTrue(barcodeService.importCalls.isEmpty)

        let intake = BarcodeIntakeViewModel()
        intake.configure(barcodeService: barcodeService)
        await intake.handleScannedBarcode(handedOff ?? "")
        XCTAssertEqual(intake.pendingUnknownBarcode, "850078714923", "intake must offer filament selection for the scanned barcode")
        let imported = await intake.importUnknownBarcode(filamentId: 12)
        XCTAssertTrue(imported)
        XCTAssertEqual(barcodeService.saveMappingCalls.map(\.barcode), ["850078714923"])
        XCTAssertEqual(barcodeService.saveMappingCalls.map(\.filamentId), [12])
        XCTAssertEqual(barcodeService.importCalls.map(\.barcode), ["850078714923"])
        XCTAssertEqual(intake.importedThisSession.map(\.id), [501])

        let (rescan, _, rescanSpools) = makeSubject(barcodeService: barcodeService)
        await rescan.dispatch("850078714923")
        XCTAssertEqual(rescan.pendingSpoolBarcode, "850078714923")
        XCTAssertEqual(rescan.recentScans.first?.title, "PLA Galaxy", "a repeat scan must resolve the persisted mapping")
        XCTAssertFalse(rescanSpools.listSpoolsCalled)
    }

    @MainActor
    func testDispatchUnrecognizedTextCodeSetsUnknownOutcome() async {
        let (viewModel, barcodeService, spoolService) = makeSubject()
        barcodeService.filamentToResolve = nil

        await viewModel.dispatch("garbage-code")

        guard case .unknownCode(let code) = viewModel.pendingOutcome else {
            return XCTFail("Expected .unknownCode outcome")
        }
        XCTAssertEqual(code, "garbage-code")
        XCTAssertNil(viewModel.pendingSpoolBarcode, "non-GTIN text cannot be persisted as a barcode mapping")
        XCTAssertFalse(spoolService.listSpoolsCalled)
    }

    @MainActor
    func testDispatchResolveErrorSurfacesErrorWithoutOfferingIntake() async {
        let (viewModel, barcodeService, spoolService) = makeSubject()
        barcodeService.resolveError = NetworkError.serverError(500)

        await viewModel.dispatch("850078714923")

        XCTAssertNotNil(viewModel.errorMessage)
        XCTAssertNil(viewModel.pendingSpoolBarcode, "a lookup failure is not proof the barcode is unknown")
        XCTAssertNil(viewModel.pendingOutcome)
        XCTAssertFalse(spoolService.listSpoolsCalled)
    }

    @MainActor
    func testDispatchIgnoresBlankCode() async {
        let (viewModel, barcodeService, _) = makeSubject()

        await viewModel.dispatch("   ")

        XCTAssertNil(viewModel.pendingOutcome)
        XCTAssertNil(viewModel.pendingDeepLinkDestination)
        XCTAssertNil(viewModel.pendingSpoolBarcode)
        XCTAssertTrue(barcodeService.resolveBarcodes.isEmpty)
    }

    func testSupportedProductBarcodeMirrorsServerGtinRules() {
        for valid in ["96385074", "850078714923", "4006381333931", "00012345678905", "0 12345 67890 5", "4-006381-333931"] {
            XCTAssertTrue(ScanViewModel.isSupportedProductBarcode(valid), valid)
        }
        for invalid in ["", "77", "1234567", "850078714924", "123456789012345", "ABC123456789", "85007871492３",
                        "850078714923/1", "9" + String(repeating: "-", count: 60) + "6385074"] {
            XCTAssertFalse(ScanViewModel.isSupportedProductBarcode(invalid), invalid)
        }
    }

    @MainActor
    func testScanRecordsRecentScanForKnownSpoolBarcode() async throws {
        let (viewModel, barcodeService, scanner) = makeSubjectWithScanner()
        barcodeService.filamentToResolve = makeFilament(id: 3, name: "PLA Black")
        scanner.barcodeScanResultToReturn = .barcode("012345678905")

        viewModel.scan()
        try await Task.sleep(nanoseconds: 80_000_000)

        XCTAssertFalse(viewModel.isScanning)
        XCTAssertEqual(viewModel.recentScans.first?.title, "PLA Black")
        XCTAssertEqual(viewModel.recentScans.first?.subtitle, "Spool barcode")
    }

    @MainActor
    func testScanUnavailableScannerSurfacesErrorWithoutDispatching() {
        let scanner = MockScannerService()
        scanner.mockIsAvailable = false
        let viewModel = ScanViewModel()
        viewModel.configure(
            scanner: scanner,
            barcodeIntakeService: MockBarcodeIntakeService(),
            spoolService: MockSpoolService()
        )

        viewModel.scan()

        XCTAssertNotNil(viewModel.errorMessage)
        XCTAssertFalse(viewModel.isScanning)
        XCTAssertEqual(scanner.barcodeScanCallCount, 0)
    }

    @MainActor
    func testScanCancelledWhileViewInactiveDoesNotDispatch() async throws {
        let (viewModel, barcodeService, scanner) = makeSubjectWithScanner()
        scanner.barcodeScanResultToReturn = .barcode("012345678905")
        scanner.barcodeScanDelayNanoseconds = 40_000_000

        viewModel.scan()
        viewModel.isViewActive = false

        try await Task.sleep(nanoseconds: 90_000_000)

        XCTAssertNil(viewModel.pendingOutcome)
        XCTAssertTrue(barcodeService.resolveBarcodes.isEmpty)
    }

}

extension ScanViewModelTests {
    // MARK: - External scan request overlap
    @MainActor
    func testQueuedExternalScanTransfersAfterExistingSpoolNavigationInsteadOfReopeningCamera() async {
        await assertQueuedScanTransfersAfterNavigation(
            code: "printfarmer://spool/42",
            destination: .spoolDetail(id: 42)
        )
    }

    @MainActor
    func testExternalScanDeactivatedBeforeTaskRunsRetainsRequestWithoutInvokingCamera() async {
        let (viewModel, scanner) = makeControlledSubject()
        let requestID = UUID()
        viewModel.requestExternalScan(requestID)
        let stopped = expectation(description: "Inactive scan task stopped")
        withObservationTracking {
            _ = viewModel.isScanning
        } onChange: {
            stopped.fulfill()
        }
        viewModel.isViewActive = false
        await fulfillment(of: [stopped], timeout: 2)
        XCTAssertFalse(viewModel.isScanning)
        XCTAssertEqual(scanner.callCount, 0, "Activity must be checked inside the camera task")

        scanner.scanStarted = expectation(description: "Retained request resumes")
        viewModel.isViewActive = true
        await fulfillment(of: [scanner.scanStarted], timeout: 2)
        await completeScan(viewModel, scanner, returning: .cancelled)
        viewModel.requestExternalScan(requestID)
        XCTAssertFalse(viewModel.isScanning)
        XCTAssertEqual(scanner.callCount, 1)
    }

    @MainActor
    func testExternalRequestStartsOnceDespiteRedeliveryDuringAndAfterScan() async {
        let (viewModel, scanner) = makeControlledSubject()
        let requestID = UUID()
        scanner.scanStarted = expectation(description: "Initial scan")

        viewModel.requestExternalScan(requestID)
        viewModel.requestExternalScan(requestID)
        XCTAssertTrue(viewModel.isScanning)
        await fulfillment(of: [scanner.scanStarted], timeout: 2)

        await completeScan(viewModel, scanner, returning: .cancelled)
        viewModel.requestExternalScan(requestID)
        XCTAssertFalse(viewModel.isScanning)
        XCTAssertEqual(scanner.callCount, 1)
    }

    @MainActor
    func testExternalRequestDuringManualScanStartsAfterCancellation() async {
        let (viewModel, scanner) = makeControlledSubject()
        scanner.scanStarted = expectation(description: "Manual scan")
        viewModel.scan()
        await fulfillment(of: [scanner.scanStarted], timeout: 2)

        let requestID = UUID()
        viewModel.requestExternalScan(requestID)
        viewModel.scan()
        XCTAssertEqual(scanner.callCount, 1, "No concurrent scanner may start")
        XCTAssertTrue(viewModel.isScanning)

        scanner.scanStarted = expectation(description: "Queued external scan")
        scanner.complete(.cancelled)
        await fulfillment(of: [scanner.scanStarted], timeout: 2)
        XCTAssertEqual(scanner.callCount, 2)
        await completeScan(viewModel, scanner, returning: .cancelled)

        viewModel.requestExternalScan(requestID)
        XCTAssertFalse(viewModel.isScanning)
    }

    @MainActor
    func testQueuedExternalScanWaitsForSuccessfulResultSheetDismissal() async {
        let barcodeService = MockBarcodeIntakeService()
        let (viewModel, scanner) = makeControlledSubject(barcodeService: barcodeService)
        scanner.scanStarted = expectation(description: "Initial scan")
        viewModel.requestExternalScan(UUID())
        await fulfillment(of: [scanner.scanStarted], timeout: 2)

        viewModel.requestExternalScan(UUID())
        await completeScan(viewModel, scanner, returning: .barcode("CODE-OVERLAP"))

        XCTAssertEqual(barcodeService.resolveBarcodes, ["CODE-OVERLAP"])
        XCTAssertEqual(viewModel.recentScans.first?.title, "Unrecognized")
        XCTAssertNotNil(viewModel.pendingOutcome)
        XCTAssertEqual(scanner.callCount, 1)
        viewModel.resultPresentationDidAppear()
        viewModel.pendingOutcome = nil
        viewModel.scan()
        viewModel.isViewActive = true
        XCTAssertFalse(viewModel.isScanning, "Clearing the binding must not bypass the sheet's dismissal animation")

        scanner.scanStarted = expectation(description: "Retry after dismissal")
        viewModel.resultPresentationDidDismiss()
        await fulfillment(of: [scanner.scanStarted], timeout: 2)
        XCTAssertEqual(scanner.callCount, 2)
        await completeScan(viewModel, scanner, returning: .cancelled)
    }

    @MainActor
    func testExternalRequestsCoalesceAndDuplicateIDsDoNotCreateAnotherRetry() async {
        let (viewModel, scanner) = makeControlledSubject()
        let firstID = UUID()
        let latestID = UUID()
        scanner.scanStarted = expectation(description: "Initial scan")
        viewModel.requestExternalScan(firstID)
        await fulfillment(of: [scanner.scanStarted], timeout: 2)
        viewModel.requestExternalScan(UUID())
        viewModel.requestExternalScan(UUID())
        viewModel.requestExternalScan(latestID)
        viewModel.requestExternalScan(latestID)
        viewModel.requestExternalScan(firstID)
        XCTAssertEqual(scanner.callCount, 1)

        scanner.scanStarted = expectation(description: "Coalesced retry")
        scanner.complete(.cancelled)
        await fulfillment(of: [scanner.scanStarted], timeout: 2)
        viewModel.requestExternalScan(latestID)
        await completeScan(viewModel, scanner, returning: .cancelled)

        viewModel.requestExternalScan(latestID)
        XCTAssertFalse(viewModel.isScanning)
        XCTAssertEqual(scanner.callCount, 2)
    }

    @MainActor
    func testQueuedExternalScanWaitsForViewReactivation() async {
        let barcodeService = MockBarcodeIntakeService()
        let (viewModel, scanner) = makeControlledSubject(barcodeService: barcodeService)
        scanner.scanStarted = expectation(description: "Initial scan")
        viewModel.requestExternalScan(UUID())
        await fulfillment(of: [scanner.scanStarted], timeout: 2)
        viewModel.requestExternalScan(UUID())
        viewModel.isViewActive = false

        await completeScan(viewModel, scanner, returning: .barcode("CODE-INACTIVE"))
        XCTAssertTrue(barcodeService.resolveBarcodes.isEmpty)
        XCTAssertEqual(scanner.callCount, 1, "Leaving the flow must not reopen the camera")

        scanner.scanStarted = expectation(description: "Retry when active")
        viewModel.isViewActive = true
        await fulfillment(of: [scanner.scanStarted], timeout: 2)
        XCTAssertEqual(scanner.callCount, 2)
        await completeScan(viewModel, scanner, returning: .cancelled)
    }

    @MainActor
    func testQueuedExternalScanWaitsForScannerErrorAcknowledgment() async {
        let (viewModel, scanner) = makeControlledSubject()
        scanner.scanStarted = expectation(description: "Initial scan")
        viewModel.requestExternalScan(UUID())
        await fulfillment(of: [scanner.scanStarted], timeout: 2)
        viewModel.requestExternalScan(UUID())

        await completeScan(viewModel, scanner, returning: .error(.permissionDenied))
        XCTAssertEqual(viewModel.errorMessage, SpoolScanError.permissionDenied.localizedDescription)
        XCTAssertEqual(scanner.callCount, 1)
        viewModel.scan()
        XCTAssertFalse(viewModel.isScanning, "An unacknowledged error must not be cleared by a retry")

        scanner.scanStarted = expectation(description: "Retry after error acknowledgment")
        viewModel.clearError()
        await fulfillment(of: [scanner.scanStarted], timeout: 2)
        await completeScan(viewModel, scanner, returning: .error(.permissionDenied))

        XCTAssertEqual(viewModel.errorMessage, SpoolScanError.permissionDenied.localizedDescription)
        XCTAssertFalse(viewModel.isScanning)
        XCTAssertEqual(scanner.callCount, 2)
    }

    @MainActor
    func testRecentScansCapAtTwentyEntries() async {
        let (viewModel, _, _) = makeSubject()

        for index in 0..<25 {
            await viewModel.dispatch("CODE-\(index)")
        }

        XCTAssertEqual(viewModel.recentScans.count, 20)
        XCTAssertEqual(viewModel.recentScans.first?.subtitle, "CODE-24")
    }

    // MARK: - Final trio Item 1: paginated spoolExists(id:) existence check
    // These test `SpoolServiceProtocol.spoolExists(id:)` directly (not
    // through ScanViewModel.dispatch) since pagination is protocol-level
    // logic independent of the ViewModel that calls it.

    func testSpoolExistsFindsTargetOnFirstPageWithoutFetchingSecondPage() async throws {
        let spoolService = MockSpoolService()
        spoolService.spoolsPagesToReturn = [
            SpoolmanPagedResult(items: [makeSpool(id: 42)], totalCount: 600)
        ]

        let found = try await spoolService.spoolExists(id: 42)

        XCTAssertTrue(found)
        XCTAssertEqual(spoolService.listSpoolsCallHistory.count, 1, "must short-circuit on the first page — no second page fetched once found")
        XCTAssertEqual(spoolService.listSpoolsCallHistory.first?.offset, 0)
    }

    func testSpoolExistsFindsTargetOnSecondPageWhenTotalExceedsFirstPageSize() async throws {
        // >500 total spools: target ID only appears on page two (offset 500).
        let spoolService = MockSpoolService()
        let firstPage = (0..<500).map { makeSpool(id: $0) }
        spoolService.spoolsPagesToReturn = [
            SpoolmanPagedResult(items: firstPage, totalCount: 600),
            SpoolmanPagedResult(items: [makeSpool(id: 550)], totalCount: 600)
        ]

        let found = try await spoolService.spoolExists(id: 550)

        XCTAssertTrue(found)
        XCTAssertEqual(spoolService.listSpoolsCallHistory.count, 2)
        XCTAssertEqual(spoolService.listSpoolsCallHistory[0].offset, 0)
        XCTAssertEqual(spoolService.listSpoolsCallHistory[0].limit, 500)
        XCTAssertEqual(spoolService.listSpoolsCallHistory[1].offset, 500, "second page must request the advanced offset, not offset 0 again")
        XCTAssertEqual(spoolService.listSpoolsCallHistory[1].limit, 500)
    }

    func testSpoolExistsReturnsFalseWhenAbsentAfterAllPagesExhausted() async throws {
        let spoolService = MockSpoolService()
        let firstPage = (0..<500).map { makeSpool(id: $0) }
        let secondPage = (500..<600).map { makeSpool(id: $0) }
        spoolService.spoolsPagesToReturn = [
            SpoolmanPagedResult(items: firstPage, totalCount: 600),
            SpoolmanPagedResult(items: secondPage, totalCount: 600)
        ]

        let found = try await spoolService.spoolExists(id: 99_999)

        XCTAssertFalse(found)
        XCTAssertEqual(spoolService.listSpoolsCallHistory.count, 2, "must stop once every reported item (per totalCount) has been checked — no extra requests")
    }

    func testSpoolExistsTerminatesOnEmptyPageWithoutLoopingForever() async throws {
        // Defensive guard: a malformed/inconsistent response (totalCount
        // overstates what the server can actually return) must terminate
        // rather than loop indefinitely re-requesting an empty page.
        let spoolService = MockSpoolService()
        spoolService.spoolsPagesToReturn = [
            SpoolmanPagedResult(items: [], totalCount: 600)
        ]

        let found = try await spoolService.spoolExists(id: 42)

        XCTAssertFalse(found)
        XCTAssertEqual(spoolService.listSpoolsCallHistory.count, 1, "an empty page must terminate the walk immediately, not be re-requested")
    }

    func testSpoolExistsPropagatesNetworkErrorFromAnyPage() async throws {
        let spoolService = MockSpoolService()
        spoolService.errorToThrow = NetworkError.serverError(500)

        do {
            _ = try await spoolService.spoolExists(id: 42)
            XCTFail("expected the existence lookup's network error to propagate")
        } catch {
            XCTAssertTrue(error is NetworkError, "the underlying error must propagate unchanged so it can be distinguished from a genuine not-found")
        }
    }

    // MARK: - Helpers

    @MainActor
    private func assertQueuedScanTransfersAfterNavigation(
        code: String,
        destination: DeepLinkDestination
    ) async {
        let spoolService = MockSpoolService()
        spoolService.spoolsPageToReturn = SpoolmanPagedResult(items: [makeSpool(id: 42)], totalCount: 1)
        let (viewModel, scanner) = makeControlledSubject(spoolService: spoolService)
        scanner.scanStarted = expectation(description: "Initial camera")
        viewModel.requestExternalScan(UUID())
        await fulfillment(of: [scanner.scanStarted], timeout: 2)
        let requestID = UUID()
        viewModel.requestExternalScan(requestID)

        await completeScan(viewModel, scanner, returning: .barcode(code))
        XCTAssertEqual(viewModel.pendingDeepLinkDestination, destination)
        XCTAssertEqual(scanner.callCount, 1, "Terminal dispatch must fence the retry before the view observes it")
        viewModel.scan()
        XCTAssertFalse(viewModel.isScanning)

        let router = AppRouter()
        router.beginScanFlowDismissal(queuedExternalRequestID: viewModel.finishNavigation())
        router.navigate(to: destination, capabilities: .defaults)
        XCTAssertEqual(router.pendingExternalScanRequestID, requestID)
        XCTAssertFalse(router.consumeExternalScanRequest(), "The old sheet still owns dismissal")
        viewModel.isViewActive = true
        viewModel.scan()
        XCTAssertFalse(viewModel.isScanning, "A retired flow must never reopen its camera")
        XCTAssertNil(viewModel.pendingDeepLinkDestination)
        viewModel.requestExternalScan(requestID)
        XCTAssertNil(viewModel.finishNavigation(), "Redelivery must not duplicate the transferred request")

        router.completeScanFlowDismissal(capabilities: .defaults)
        XCTAssertEqual(router.selectedTab, .filament)
        XCTAssertTrue(router.consumeExternalScanRequest())
        let (replacement, replacementScanner) = makeControlledSubject()
        replacementScanner.scanStarted = expectation(description: "New owner starts retained request")
        replacement.requestExternalScan(requestID)
        await fulfillment(of: [replacementScanner.scanStarted], timeout: 2)
        await completeScan(replacement, replacementScanner, returning: .cancelled)
        replacement.requestExternalScan(requestID)
        XCTAssertFalse(replacement.isScanning)
        XCTAssertEqual(replacementScanner.callCount, 1)
        XCTAssertEqual(scanner.callCount, 1)
    }

    @MainActor
    func makeControlledSubject(
        spoolService: MockSpoolService = MockSpoolService(),
        barcodeService: MockBarcodeIntakeService = MockBarcodeIntakeService(),
        waitForScannerPresentation: @escaping @MainActor () async -> Bool = { true }
    ) -> (ScanViewModel, ControlledBarcodeScanner) {
        let viewModel = ScanViewModel()
        let scanner = ControlledBarcodeScanner()
        viewModel.configure(
            scanner: scanner,
            barcodeIntakeService: barcodeService,
            spoolService: spoolService,
            waitForScannerPresentation: waitForScannerPresentation
        )
        return (viewModel, scanner)
    }

    @MainActor
    func completeScan(
        _ viewModel: ScanViewModel,
        _ scanner: ControlledBarcodeScanner,
        returning result: BarcodeScanResult
    ) async {
        let completed = expectation(description: "Scan completed")
        withObservationTracking {
            _ = viewModel.isScanning
        } onChange: {
            completed.fulfill()
        }
        scanner.complete(result)
        await fulfillment(of: [completed], timeout: 2)
        XCTAssertFalse(viewModel.isScanning)
    }

    @MainActor
    private func makeSubject(
        barcodeService: MockBarcodeIntakeService = MockBarcodeIntakeService()
    ) -> (ScanViewModel, MockBarcodeIntakeService, MockSpoolService) {
        let viewModel = ScanViewModel()
        let spoolService = MockSpoolService()
        viewModel.configure(
            scanner: nil,
            barcodeIntakeService: barcodeService,
            spoolService: spoolService
        )
        return (viewModel, barcodeService, spoolService)
    }

    @MainActor
    final class ControlledBarcodeScanner: BarcodeScannerProtocol {
        nonisolated let isAvailable = true
        private(set) var callCount = 0
        var scanStarted = XCTestExpectation(description: "Scan started")
        private var continuation: CheckedContinuation<BarcodeScanResult, Never>?

        nonisolated func scanBarcode() async -> BarcodeScanResult {
            await waitForResult()
        }

        private func waitForResult() async -> BarcodeScanResult {
            callCount += 1
            return await withCheckedContinuation { continuation in
                XCTAssertNil(self.continuation, "Concurrent scanner invocation")
                self.continuation = continuation
                scanStarted.fulfill()
            }
        }

        func complete(_ result: BarcodeScanResult) {
            guard let continuation else {
                return XCTFail("No scan is waiting for a result")
            }
            self.continuation = nil
            continuation.resume(returning: result)
        }
    }

    @MainActor
    private func makeSubjectWithScanner() -> (ScanViewModel, MockBarcodeIntakeService, MockScannerService) {
        let viewModel = ScanViewModel()
        let barcodeService = MockBarcodeIntakeService()
        let scanner = MockScannerService()
        viewModel.configure(
            scanner: scanner,
            barcodeIntakeService: barcodeService,
            spoolService: MockSpoolService()
        )
        return (viewModel, barcodeService, scanner)
    }

    func makeFilament(id: Int, name: String) -> SpoolmanFilament {
        SpoolmanFilament(
            id: id, name: name, material: "PLA", colorHex: "#000000", vendor: "Vendor",
            density: 1.24, diameter: 1.75, weight: 1000, spoolWeight: 200, price: 25,
            settingsExtruderTemp: 215, settingsBedTemp: 60, articleNumber: nil, gtin: nil,
            comment: nil, multiColorHexes: nil, externalId: nil
        )
    }

    private func makeSpool(id: Int) -> SpoolmanSpool {
        SpoolmanSpool(
            id: id, filamentId: nil, name: "Spool #\(id)", material: "PLA", colorHex: "#000000",
            inUse: false, filamentName: "PLA Black", vendor: "Vendor",
            registeredAt: nil, firstUsedAt: nil, lastUsedAt: nil,
            remainingWeightG: 800, initialWeightG: 1000, usedWeightG: 200, spoolWeightG: 200,
            remainingLengthMm: nil, usedLengthMm: nil,
            location: nil, lotNumber: nil, archived: false, price: nil, comment: nil,
            hasNfcTag: false, usedPercent: 20, remainingPercent: 80
        )
    }
}
