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
    func testNonGtinValuesNeverResolveMapOrImportAsGtin() async {
        // Barcode Intake's own camera also reports Code 128/39 and QR text.
        // The server strips non-digits, so none of these may be resolved,
        // mapped as a GTIN, or imported; they use the exact-match alias store.
        let codes = ["LOT/850078714923", "ABC123", "SPL-AB12", "https://example.com/p/4006381333931", "X0022Z40I1"]
        for code in codes {
            let (viewModel, service) = makeSubject()
            service.filamentToResolve = makeFilament(id: 7)
            service.filamentsById = [7: makeFilament(id: 7)]
            service.spoolToCreate = makeSpool(id: 70, filamentId: 7)

            await viewModel.handleScannedBarcode(code)
            XCTAssertEqual(viewModel.pendingUnknownBarcode, code, code)
            XCTAssertTrue(viewModel.pendingCodeIsAlias, code)
            let added = await viewModel.importUnknownBarcode(filamentId: 7)

            XCTAssertTrue(added, code)
            XCTAssertEqual(service.aliasLookups, [code], "\(code) is looked up verbatim")
            XCTAssertEqual(service.aliasSaveCalls.map(\.code), [code], "\(code) is remembered verbatim")
            XCTAssertEqual(service.createSpoolCalls, [7], code)
            XCTAssertTrue(service.resolveBarcodes.isEmpty, "\(code) must not be resolved as a GTIN")
            XCTAssertTrue(service.saveMappingCalls.isEmpty, "\(code) must not be mapped as a GTIN")
            XCTAssertTrue(service.importCalls.isEmpty, "\(code) must not use GTIN import")
        }
    }

    @MainActor
    func testUnrememberableNonGtinValuesAreRejectedWithoutAnyServiceCall() async {
        let codes = [String(repeating: "A", count: BarcodeIntakeViewModel.maxRememberedCodeLength + 1),
                     "X0022\u{200B}Z40I1", "LINE\u{0007}BELL", "12345", "850078714924", "00224"]
        for code in codes {
            let (viewModel, service) = makeSubject()

            await viewModel.handleScannedBarcode(code)
            let imported = await viewModel.importUnknownBarcode(filamentId: 7)

            XCTAssertEqual(viewModel.errorMessage, BarcodeIntakeViewModel.notProductBarcodeMessage)
            XCTAssertNil(viewModel.pendingUnknownBarcode)
            XCTAssertFalse(imported)
            XCTAssertTrue(service.aliasLookups.isEmpty)
            XCTAssertTrue(service.aliasSaveCalls.isEmpty)
            XCTAssertTrue(service.resolveBarcodes.isEmpty)
        }
    }

    func testRememberableCodeRuleMirrorsServerValidator() {
        XCTAssertTrue(BarcodeIntakeViewModel.isRememberableCode("X0022Z40I1"))
        XCTAssertTrue(BarcodeIntakeViewModel.isRememberableCode("  X0022Z40I1  "))
        XCTAssertTrue(BarcodeIntakeViewModel.isRememberableCode("00224X"))
        XCTAssertFalse(BarcodeIntakeViewModel.isRememberableCode("4006381333931"), "GTINs use the GTIN mapping")
        XCTAssertFalse(BarcodeIntakeViewModel.isRememberableCode("0-12345-67890-5"), "separated GTINs too")
        XCTAssertFalse(BarcodeIntakeViewModel.isRememberableCode(""))
        XCTAssertFalse(BarcodeIntakeViewModel.isRememberableCode("12345"), "digit-only values are spool IDs")
        XCTAssertFalse(BarcodeIntakeViewModel.isRememberableCode("850078714924"), "a bad-check-digit GTIN isn't an alias")
        XCTAssertTrue(BarcodeIntakeViewModel.isRememberableCode(String(repeating: "a", count: 128)))
        XCTAssertFalse(BarcodeIntakeViewModel.isRememberableCode(String(repeating: "a", count: 129)))
        XCTAssertFalse(
            BarcodeIntakeViewModel.isRememberableCode(String(repeating: "😀", count: 65)),
            "length is measured in UTF-16 units like the server"
        )
    }

    @MainActor
    func testScanNextFnskuLooksUpRememberedCodeNotGtin() async throws {
        let service = MockBarcodeIntakeService()
        let scanner = MockScannerService()
        scanner.barcodeScanResultToReturn = .barcode("X0022Z40I1")
        let viewModel = BarcodeIntakeViewModel()
        viewModel.configure(barcodeService: service, scanner: scanner)

        viewModel.scanNext()

        try await Task.sleep(nanoseconds: 50_000_000)
        XCTAssertNil(viewModel.errorMessage)
        XCTAssertEqual(viewModel.pendingUnknownBarcode, "X0022Z40I1")
        XCTAssertEqual(service.aliasLookups, ["X0022Z40I1"])
        XCTAssertTrue(service.resolveBarcodes.isEmpty)
        XCTAssertTrue(service.saveMappingCalls.isEmpty)
        XCTAssertTrue(service.importCalls.isEmpty)
    }

    // MARK: - Remembered codes (FNSKU X0022Z40I1)

    @MainActor
    func testFnskuFirstScanWithExistingFilamentRemembersItAndAddsSpool() async {
        let (viewModel, service) = makeSubject()
        service.filamentsById = [7: makeFilament(id: 7), 8: makeFilament(id: 8)]
        service.spoolToCreate = makeSpool(id: 70, filamentId: 8)

        await viewModel.handleScannedBarcode("X0022Z40I1")
        XCTAssertEqual(viewModel.pendingUnknownBarcode, "X0022Z40I1", "a genuine miss offers filament selection")
        XCTAssertNil(viewModel.pendingAliasMatch)
        XCTAssertTrue(service.aliasSaveCalls.isEmpty, "nothing is remembered before the user picks")
        XCTAssertTrue(service.createSpoolCalls.isEmpty)

        let added = await viewModel.importUnknownBarcode(filamentId: 8)

        XCTAssertTrue(added)
        XCTAssertEqual(service.aliasSaveCalls.map(\.filamentId), [8], "the explicitly picked filament is remembered")
        XCTAssertEqual(service.createSpoolCalls, [8], "the spool uses that exact filament ID")
        XCTAssertEqual(service.aliases["X0022Z40I1"]?.id, 8)
        XCTAssertNil(service.aliases["00224"], "the FNSKU is never reduced to GTIN digits")
        XCTAssertEqual(viewModel.importedThisSession.map(\.id), [70])
        XCTAssertNil(viewModel.pendingUnknownBarcode)
        XCTAssertNil(viewModel.errorMessage)
    }

    @MainActor
    func testFnskuFirstScanWithNewlyCreatedFilamentRemembersTheNewId() async {
        // The Map sheet creates the filament first (gtin: nil for an alias
        // code), then passes the new ID into importUnknownBarcode.
        let (viewModel, service) = makeSubject()
        service.filamentsById = [7: makeFilament(id: 7)]
        await viewModel.handleScannedBarcode("X0022Z40I1")

        let created = makeFilament(id: 42, gtin: nil)
        service.filamentsById[42] = created
        service.spoolToCreate = makeSpool(id: 71, filamentId: 42)
        let added = await viewModel.importUnknownBarcode(filamentId: created.id)

        XCTAssertTrue(added)
        XCTAssertEqual(service.aliasSaveCalls.map(\.filamentId), [42])
        XCTAssertEqual(service.createSpoolCalls, [42])
        XCTAssertTrue(service.saveMappingCalls.isEmpty, "the new filament's GTIN mapping is untouched")
    }

    @MainActor
    func testFnskuRescanPreselectsRememberedFilamentAndAddsAnotherSpoolOnlyAfterConfirmation() async {
        let (viewModel, service) = makeSubject()
        service.aliases = ["X0022Z40I1": makeFilament(id: 8)]
        service.spoolToCreate = makeSpool(id: 72, filamentId: 8)

        await viewModel.handleScannedBarcode("X0022Z40I1")

        XCTAssertEqual(viewModel.pendingAliasMatch?.filament.id, 8)
        XCTAssertEqual(viewModel.pendingAliasMatch?.code, "X0022Z40I1")
        XCTAssertNil(viewModel.pendingUnknownBarcode)
        XCTAssertTrue(service.createSpoolCalls.isEmpty, "a rescan never creates a spool without confirmation")

        let first = await viewModel.confirmAliasMatch()
        await viewModel.handleScannedBarcode("X0022Z40I1")
        let second = await viewModel.confirmAliasMatch()

        XCTAssertTrue(first)
        XCTAssertTrue(second)
        XCTAssertEqual(service.createSpoolCalls, [8, 8], "each confirmed rescan adds a new spool")
        XCTAssertTrue(service.aliasSaveCalls.isEmpty)
        XCTAssertNotNil(viewModel.lastResolvedFilament?.gtin)
        XCTAssertNil(viewModel.lastResolvedBarcode, "a remembered FNSKU is never presented as a GTIN")
        XCTAssertNil(viewModel.pendingAliasMatch)
    }

    @MainActor
    func testCancellingRememberedMatchCreatesNothing() async {
        let (viewModel, service) = makeSubject()
        service.aliases = ["X0022Z40I1": makeFilament(id: 8)]
        await viewModel.handleScannedBarcode("X0022Z40I1")

        viewModel.cancelAliasMatch()

        XCTAssertNil(viewModel.pendingAliasMatch)
        XCTAssertTrue(service.createSpoolCalls.isEmpty)
    }

    @MainActor
    func testRememberedFilamentDeletedOffersFilamentSelectionAgain() async {
        let (viewModel, service) = makeSubject()
        service.aliasLookupOverride = ScanCodeAliasLookup(
            code: "X0022Z40I1",
            state: .filamentMissing,
            alias: ScanCodeAlias(id: 3, code: "X0022Z40I1", filamentId: 99, filament: nil)
        )

        await viewModel.handleScannedBarcode("X0022Z40I1")

        XCTAssertNil(viewModel.pendingAliasMatch)
        XCTAssertEqual(viewModel.pendingUnknownBarcode, "X0022Z40I1")
        XCTAssertTrue(service.createSpoolCalls.isEmpty)
    }

    @MainActor
    func testAliasLookupFailureShowsRetryAndNeverOffersCreation() async {
        for error in [NetworkError.serverError(502), NetworkError.unauthorized, NetworkError.notFound] {
            let (viewModel, service) = makeSubject()
            service.aliasLookupError = error

            await viewModel.handleScannedBarcode("X0022Z40I1")

            XCTAssertNotNil(viewModel.errorMessage, "\(error)")
            XCTAssertEqual(viewModel.retryAction, .lookup("X0022Z40I1"), "\(error)")
            XCTAssertNil(viewModel.pendingUnknownBarcode, "a failed lookup is not proof the code is new: \(error)")
            XCTAssertNil(viewModel.pendingAliasMatch)
            XCTAssertTrue(service.aliasSaveCalls.isEmpty)
            XCTAssertTrue(service.createSpoolCalls.isEmpty)

            service.aliasLookupError = nil
            service.aliases = ["X0022Z40I1": makeFilament(id: 8)]
            await viewModel.retry()
            XCTAssertEqual(viewModel.pendingAliasMatch?.filament.id, 8, "retry repeats the lookup")
            XCTAssertNil(viewModel.retryAction)
        }
    }

    @MainActor
    func testAliasSaveFailureRetriesWithoutCreatingSpoolFirst() async {
        let (viewModel, service) = makeSubject()
        service.filamentsById = [8: makeFilament(id: 8)]
        service.spoolToCreate = makeSpool(id: 73, filamentId: 8)
        await viewModel.handleScannedBarcode("X0022Z40I1")
        service.aliasSaveError = NetworkError.serverError(500)

        let added = await viewModel.importUnknownBarcode(filamentId: 8)

        XCTAssertFalse(added)
        XCTAssertEqual(viewModel.retryAction, .remember(code: "X0022Z40I1", filamentId: 8))
        XCTAssertTrue(service.createSpoolCalls.isEmpty, "no spool is created when the code wasn't remembered")

        service.aliasSaveError = nil
        await viewModel.retry()
        XCTAssertEqual(service.aliasSaveCalls.count, 2)
        XCTAssertEqual(service.createSpoolCalls, [8])
        XCTAssertNil(viewModel.pendingUnknownBarcode)
    }

    @MainActor
    func testSpoolCreationFailureAfterRememberingRetriesIdempotently() async {
        let (viewModel, service) = makeSubject()
        service.filamentsById = [8: makeFilament(id: 8)]
        service.spoolToCreate = makeSpool(id: 74, filamentId: 8)
        await viewModel.handleScannedBarcode("X0022Z40I1")
        // A definite failure: the request never reached the server.
        service.createSpoolError = NetworkError.serverUnreachable

        let added = await viewModel.importUnknownBarcode(filamentId: 8)

        XCTAssertFalse(added)
        XCTAssertEqual(viewModel.retryAction, .addAliasSpool(code: "X0022Z40I1", filamentId: 8))
        XCTAssertFalse(viewModel.spoolOutcomeUncertain)
        XCTAssertEqual(service.aliases["X0022Z40I1"]?.id, 8, "the code stays remembered")
        XCTAssertTrue(viewModel.importedThisSession.isEmpty)

        service.createSpoolError = nil
        await viewModel.retry()
        XCTAssertEqual(service.aliasSaveCalls.map(\.filamentId), [8], "retry only adds the spool; the alias is not re-saved")
        XCTAssertEqual(service.createSpoolCalls, [8, 8])
        XCTAssertEqual(viewModel.importedThisSession.map(\.id), [74], "exactly one spool was created")
        XCTAssertNil(viewModel.pendingUnknownBarcode)
    }

    @MainActor
    func testRememberedMatchSpoolCreationFailureOffersRetry() async {
        let (viewModel, service) = makeSubject()
        service.aliases = ["X0022Z40I1": makeFilament(id: 8)]
        service.filamentsById = [8: makeFilament(id: 8)]
        service.spoolToCreate = makeSpool(id: 75, filamentId: 8)
        service.createSpoolError = NetworkError.serverUnreachable
        await viewModel.handleScannedBarcode("X0022Z40I1")

        let added = await viewModel.confirmAliasMatch()

        XCTAssertFalse(added)
        XCTAssertEqual(viewModel.retryAction, .addAliasSpool(code: "X0022Z40I1", filamentId: 8))
        service.createSpoolError = nil
        await viewModel.retry()
        XCTAssertEqual(viewModel.importedThisSession.map(\.id), [75])
        XCTAssertEqual(service.createSpoolCalls, [8, 8])
        XCTAssertTrue(service.aliasSaveCalls.isEmpty, "retrying a confirmation never writes the alias (admin-only)")
        XCTAssertNil(viewModel.pendingUnknownBarcode, "retry never presents the Remember sheet")
        XCTAssertNil(viewModel.pendingAliasMatch, "the confirmation card is cleared so it can't add a second spool")
        XCTAssertEqual(viewModel.lastResolvedFilament?.id, 8)
    }

    @MainActor
    func testUncertainSpoolCreationAfterConfirmationWithholdsRetry() async {
        let uncertain: [Error] = [
            NetworkError.timeout,
            NetworkError.noConnection,
            NetworkError.serverError(502),
            NetworkError.transportError(URLError(.badServerResponse)),
        ]
        for error in uncertain {
            let (viewModel, service) = makeSubject()
            service.aliases = ["X0022Z40I1": makeFilament(id: 8)]
            service.createSpoolError = error
            await viewModel.handleScannedBarcode("X0022Z40I1")

            let added = await viewModel.confirmAliasMatch()

            XCTAssertFalse(added, "\(error)")
            XCTAssertNil(viewModel.retryAction, "the spool may exist; retry could duplicate it: \(error)")
            XCTAssertTrue(viewModel.spoolOutcomeUncertain, "\(error)")
            XCTAssertEqual(viewModel.errorMessage, BarcodeIntakeViewModel.spoolOutcomeUncertainMessage)
            XCTAssertNil(viewModel.pendingAliasMatch, "Add Spool is withdrawn: \(error)")
            let again = await viewModel.confirmAliasMatch()
            XCTAssertFalse(again)
            XCTAssertEqual(service.createSpoolCalls, [8], "exactly one create attempt: \(error)")
        }
    }

    @MainActor
    func testUncertainSpoolCreationAfterRememberingWithholdsRetry() async {
        let (viewModel, service) = makeSubject()
        service.filamentsById = [8: makeFilament(id: 8)]
        await viewModel.handleScannedBarcode("X0022Z40I1")
        service.createSpoolError = NetworkError.timeout

        let added = await viewModel.importUnknownBarcode(filamentId: 8)

        XCTAssertFalse(added)
        XCTAssertNil(viewModel.retryAction)
        XCTAssertTrue(viewModel.spoolOutcomeUncertain)
        XCTAssertEqual(viewModel.errorMessage, BarcodeIntakeViewModel.spoolOutcomeUncertainMessage)
        XCTAssertEqual(service.aliases["X0022Z40I1"]?.id, 8, "the code is remembered so a rescan offers the filament")
        XCTAssertEqual(service.createSpoolCalls, [8])

        viewModel.skipUnknownBarcode()
        XCTAssertFalse(viewModel.spoolOutcomeUncertain)
    }

    @MainActor
    func testConfirmWhileBusyCreatesNothing() async {
        let (viewModel, service) = makeSubject()
        service.aliases = ["X0022Z40I1": makeFilament(id: 8)]
        await viewModel.handleScannedBarcode("X0022Z40I1")
        viewModel.isBusy = true

        let added = await viewModel.confirmAliasMatch()

        XCTAssertFalse(added)
        XCTAssertTrue(service.createSpoolCalls.isEmpty)
        XCTAssertNotNil(viewModel.pendingAliasMatch)
    }

    @MainActor
    func testAppLinkIsNeverOfferedAsRememberedCode() async {
        let (viewModel, service) = makeSubject()

        await viewModel.handleScannedBarcode("printfarmer://printer/\(UUID().uuidString)/ready")

        XCTAssertTrue(service.aliasLookups.isEmpty)
        XCTAssertNil(viewModel.pendingUnknownBarcode)
        XCTAssertEqual(viewModel.errorMessage, BarcodeIntakeViewModel.notProductBarcodeMessage)
    }

    @MainActor
    func testConflictingAliasCreatesNothingAndExplains() async {
        let (viewModel, service) = makeSubject()
        service.filamentsById = [7: makeFilament(id: 7), 8: makeFilament(id: 8)]
        await viewModel.handleScannedBarcode("X0022Z40I1")
        // Another client remembered the code for filament 7 meanwhile.
        service.aliases = ["X0022Z40I1": makeFilament(id: 7)]

        let added = await viewModel.importUnknownBarcode(filamentId: 8)

        XCTAssertFalse(added)
        XCTAssertEqual(viewModel.errorMessage, BarcodeIntakeViewModel.aliasConflictMessage)
        XCTAssertNil(viewModel.retryAction, "a conflict is not retryable")
        XCTAssertNil(viewModel.pendingUnknownBarcode)
        XCTAssertTrue(service.createSpoolCalls.isEmpty)
        XCTAssertEqual(service.aliases["X0022Z40I1"]?.id, 7, "the existing link is not overwritten")
    }

    @MainActor
    func testSkippingUnknownCodeRemembersNothing() async {
        let (viewModel, service) = makeSubject()
        await viewModel.handleScannedBarcode("X0022Z40I1")

        viewModel.skipUnknownBarcode()

        XCTAssertNil(viewModel.pendingUnknownBarcode)
        XCTAssertTrue(service.aliasSaveCalls.isEmpty)
        XCTAssertTrue(service.createSpoolCalls.isEmpty)
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
