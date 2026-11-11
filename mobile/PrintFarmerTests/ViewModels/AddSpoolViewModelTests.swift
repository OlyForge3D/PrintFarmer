import XCTest
@testable import PrintFarmer

@MainActor
final class AddSpoolViewModelTests: XCTestCase {
    private var service: MockSpoolService!
    private var viewModel: AddSpoolViewModel!

    override func setUp() async throws {
        try await super.setUp()
        service = MockSpoolService()
        viewModel = AddSpoolViewModel()
        viewModel.configure(spoolService: service)
        service.filamentsToReturn = [filament(id: 41), filament(id: 42)]
        service.filamentToReturn = filament(id: 43)
        service.spoolToReturn = spool()
        await viewModel.loadReferenceData()
    }

    override func tearDown() async throws {
        viewModel = nil
        service = nil
        try await super.tearDown()
    }

    func testMaterialVendorMatchNeverSilentlySelectsAProduct() async {
        viewModel.selectedMaterial = "PLA"
        viewModel.selectedVendor = "Prusament"

        await viewModel.saveSpool()

        XCTAssertFalse(viewModel.isFormValid)
        XCTAssertFalse(viewModel.didSave)
        XCTAssertNotNil(viewModel.errorMessage)
        XCTAssertEqual(service.createSpoolCallCount, 0)
        XCTAssertEqual(service.createFilamentCallCount, 0)
    }

    func testExplicitSelectionDistinguishesOtherwiseIdenticalFilaments() async {
        viewModel.selectedFilamentId = 42
        await viewModel.saveSpool()
        await viewModel.saveSpool()

        XCTAssertTrue(viewModel.didSave)
        XCTAssertEqual(service.createSpoolCalledWith?.filamentId, 42)
        XCTAssertEqual(service.createSpoolCalledWith?.initialWeight, 1000)
        XCTAssertEqual(service.createSpoolCalledWith?.remainingWeight, 1000)
        XCTAssertEqual(service.createSpoolCallCount, 1)
        XCTAssertEqual(service.createFilamentCallCount, 0)
    }

    func testSingleMaterialMatchStillRequiresExplicitSelection() async {
        service.filamentsToReturn = [filament(id: 41)]
        await viewModel.loadReferenceData()
        viewModel.selectedMaterial = "PLA"
        await viewModel.saveSpool()
        XCTAssertEqual(service.createSpoolCallCount, 0)
    }

    func testMissingAndInvalidFilamentIDsNeverReachSpoolCreate() async {
        for id in [nil, 0, -1, 999] as [Int?] {
            viewModel.selectedFilamentId = id
            await viewModel.saveSpool()
            XCTAssertFalse(viewModel.isFormValid)
        }
        XCTAssertEqual(service.createSpoolCallCount, 0)
    }

    func testNewFilamentIsCreatedBeforeSpoolUsingReturnedIdentity() async {
        prepareNewFilament()
        service.vendorsToReturn = [SpoolmanVendor(id: 7, name: "Prusament", externalId: nil)]
        await viewModel.loadReferenceData()
        viewModel.selectedVendor = "Prusament"
        viewModel.diameterMm = 2.85
        viewModel.densityGPerCm3 = 1.27
        viewModel.totalWeightG = 750

        await viewModel.saveSpool()

        XCTAssertTrue(viewModel.didSave)
        XCTAssertEqual(service.createFilamentCalledWith?.name, "PLA Red")
        XCTAssertEqual(service.createFilamentCalledWith?.material, "PLA")
        XCTAssertEqual(service.createFilamentCalledWith?.vendorId, 7)
        XCTAssertEqual(service.createFilamentCalledWith?.colorHex, "#FF0000")
        XCTAssertEqual(service.createFilamentCalledWith?.density, 1.27)
        XCTAssertEqual(service.createFilamentCalledWith?.diameter, 2.85)
        XCTAssertNil(service.createFilamentCalledWith?.gtin)
        XCTAssertEqual(service.createSpoolCalledWith?.filamentId, 43)
        XCTAssertEqual(service.createSpoolCalledWith?.remainingWeight, 750)
    }

    func testIncompleteNewFilamentDoesNotCreateAnything() async {
        prepareNewFilament()
        viewModel.filamentName = "  "
        await viewModel.saveSpool()
        viewModel.filamentName = "PLA Red"
        viewModel.selectedMaterial = " "
        await viewModel.saveSpool()
        viewModel.selectedMaterial = "PLA"
        viewModel.densityGPerCm3 = 0
        await viewModel.saveSpool()
        viewModel.densityGPerCm3 = .infinity
        await viewModel.saveSpool()
        viewModel.densityGPerCm3 = 1.24
        viewModel.diameterMm = .nan
        await viewModel.saveSpool()
        viewModel.diameterMm = 1.75
        viewModel.colorHex = "not a color"
        await viewModel.saveSpool()
        XCTAssertEqual(service.createFilamentCallCount, 0)
        XCTAssertEqual(service.createSpoolCallCount, 0)
        XCTAssertNotNil(viewModel.errorMessage)
    }

    func testUnknownVendorIsNotSilentlyDiscarded() async {
        prepareNewFilament()
        viewModel.selectedVendor = "Scanned vendor absent from server"
        await viewModel.saveSpool()
        XCTAssertFalse(viewModel.isFormValid)
        XCTAssertEqual(service.createFilamentCallCount, 0)
    }

    func testMissingNewFilamentColorPreventsBothWrites() async {
        prepareNewFilament()
        viewModel.colorHex = ""

        await viewModel.saveSpool()

        XCTAssertFalse(viewModel.isFormValid)
        XCTAssertNotNil(viewModel.errorMessage)
        XCTAssertEqual(service.createFilamentCallCount, 0)
        XCTAssertEqual(service.createSpoolCallCount, 0)
    }

    func testEmptyInventoryExplainsUncertaintyAndReloadRestoresSelection() async {
        service.filamentsToReturn = []
        await viewModel.loadReferenceData()
        await viewModel.saveSpool()

        XCTAssertTrue(viewModel.filamentInventoryMessage?.contains("empty or unavailable") == true)
        XCTAssertFalse(viewModel.isCreatingFilament)
        XCTAssertFalse(viewModel.isFormValid)
        XCTAssertEqual(service.createFilamentCallCount, 0)
        XCTAssertEqual(service.createSpoolCallCount, 0)

        service.filamentsToReturn = [filament(id: 42)]
        await viewModel.loadReferenceData()
        viewModel.selectedFilamentId = 42
        XCTAssertNil(viewModel.filamentInventoryMessage)
        XCTAssertTrue(viewModel.isFormValid)
    }

    func testMaterialSelectionAppliesKnownDensityButPreservesItForUnknownMaterials() {
        viewModel.materials = [
            SpoolmanMaterial(id: 1, name: "PETG", density: 1.27, colorHex: nil),
            SpoolmanMaterial(id: 2, name: "TPU", density: 1.21, colorHex: nil)
        ]
        viewModel.selectedMaterial = "PETG"
        XCTAssertEqual(viewModel.densityGPerCm3, 1.27)
        viewModel.selectedMaterial = "TPU"
        XCTAssertEqual(viewModel.densityGPerCm3, 1.21)
        viewModel.densityGPerCm3 = 1.3
        viewModel.selectedMaterial = "Custom"
        XCTAssertEqual(viewModel.densityGPerCm3, 1.3)
    }

    func testScannedMaterialGetsKnownDensityAfterReferenceLoad() async {
        viewModel.prefill(from: scan(filamentID: nil))
        viewModel.isCreatingFilament = true
        viewModel.filamentName = "Scanned PETG"
        viewModel.selectedVendor = ""
        service.materialsToReturn = [
            SpoolmanMaterial(id: 1, name: "PETG", density: 1.27, colorHex: nil)
        ]

        await viewModel.loadReferenceData()
        await viewModel.saveSpool()

        XCTAssertTrue(viewModel.didSave)
        XCTAssertEqual(service.createFilamentCalledWith?.density, 1.27)
    }

    func testReferenceLoadPreservesManuallyEditedDensityForScannedMaterial() async {
        viewModel.prefill(from: scan(filamentID: nil))
        viewModel.densityGPerCm3 = 1.3
        service.materialsToReturn = [
            SpoolmanMaterial(id: 1, name: "PETG", density: 1.27, colorHex: nil)
        ]

        await viewModel.loadReferenceData()

        XCTAssertEqual(viewModel.densityGPerCm3, 1.3)
    }

    func testReferenceLoadFailurePreservesFormAndRequiresSuccessfulReload() async {
        prepareNewFilament()
        service.errorToThrow = NetworkError.unauthorized
        await viewModel.loadReferenceData()
        await viewModel.saveSpool()
        XCTAssertFalse(viewModel.hasLoadedReferenceData)
        XCTAssertEqual(viewModel.filamentName, " PLA Red ")
        XCTAssertEqual(service.createFilamentCallCount, 0)
        service.errorToThrow = nil
        await viewModel.loadReferenceData()
        XCTAssertTrue(viewModel.isFormValid)
    }

    func testScannedPositiveFilamentIDIsPreservedWithoutGuessing() async {
        viewModel.prefill(from: scan(filamentID: 900))
        await viewModel.saveSpool()
        XCTAssertTrue(viewModel.isFilamentSelectionLocked)
        XCTAssertEqual(service.createSpoolCalledWith?.filamentId, 900)
        XCTAssertEqual(service.createFilamentCallCount, 0)
        XCTAssertEqual(viewModel.selectedMaterial, "PETG")
        XCTAssertEqual(viewModel.colorHex, "#FF0000")
        XCTAssertEqual(viewModel.totalWeightG, 750)
        XCTAssertEqual(viewModel.diameterMm, 2.85)
    }

    func testNonpositiveScannedIDDoesNotLockFormOrCreateSpool() async {
        viewModel.prefill(from: scan(filamentID: 0))
        await viewModel.saveSpool()
        XCTAssertFalse(viewModel.isFilamentSelectionLocked)
        XCTAssertEqual(service.createSpoolCallCount, 0)
    }

    func testScanWithMissingFieldsPreservesDefaultsButDoesNotResolveFilament() {
        viewModel.prefill(from: ScannedSpoolData(
            material: nil, colorHex: nil, vendor: nil,
            weight: -1, diameter: 0, temperature: 0, spoolmanId: nil
        ))
        XCTAssertTrue(viewModel.isPrefilledFromScan)
        XCTAssertEqual(viewModel.totalWeightG, 1000)
        XCTAssertEqual(viewModel.diameterMm, 1.75)
        XCTAssertFalse(viewModel.isFormValid)
        XCTAssertNil(viewModel.selectedFilamentId)
    }

    func testInvalidWeightsPreventWrites() async {
        viewModel.selectedFilamentId = 42
        for weight in [0, -100, Double.nan, Double.infinity] {
            viewModel.totalWeightG = weight
            await viewModel.saveSpool()
            XCTAssertFalse(viewModel.isFormValid)
        }
        viewModel.totalWeightG = 1000
        viewModel.spoolWeightG = -1
        await viewModel.saveSpool()
        XCTAssertEqual(service.createSpoolCallCount, 0)
    }

    func testZeroSpoolWeightStillOmitsOptionalSpoolWeight() async {
        viewModel.selectedFilamentId = 42
        viewModel.spoolWeightG = 0
        await viewModel.saveSpool()
        XCTAssertTrue(viewModel.didSave)
        XCTAssertNil(service.createSpoolCalledWith?.spoolWeight)
    }

    func testFailedFilamentAuthorizationCanRetryWithoutLosingForm() async {
        prepareNewFilament()
        service.createFilamentError = NetworkError.forbidden
        await viewModel.saveSpool()
        XCTAssertEqual(service.createSpoolCallCount, 0)
        XCTAssertNil(viewModel.uncertainCreation)
        XCTAssertEqual(viewModel.filamentName, " PLA Red ")
        service.createFilamentError = nil
        await viewModel.saveSpool()
        XCTAssertTrue(viewModel.didSave)
        XCTAssertEqual(service.createSpoolCalledWith?.filamentId, 43)
    }

    func testFilamentSuccessAndSpoolAuthorizationFailureRetryOnlySpool() async {
        prepareNewFilament()
        service.createSpoolError = NetworkError.unauthorized
        await viewModel.saveSpool()
        XCTAssertEqual(viewModel.createdFilament?.id, 43)
        XCTAssertFalse(viewModel.didSave)
        XCTAssertFalse(viewModel.isSaving)
        service.createSpoolError = nil
        await viewModel.saveSpool()
        XCTAssertTrue(viewModel.didSave)
        XCTAssertEqual(service.createFilamentCallCount, 1)
        XCTAssertEqual(service.createSpoolCallCount, 2)
    }

    func testUncertainFilamentWriteBlocksBlindRetry() async {
        prepareNewFilament()
        service.createFilamentError = NetworkError.timeout
        await viewModel.saveSpool()
        service.createFilamentError = nil
        await viewModel.saveSpool()
        XCTAssertEqual(service.createFilamentCallCount, 1)
        XCTAssertEqual(service.createSpoolCallCount, 0)
        XCTAssertEqual(viewModel.uncertainCreation, "filament")
        XCTAssertTrue(viewModel.errorMessage?.contains("Check Filament inventory") == true)
        XCTAssertEqual(viewModel.filamentName, " PLA Red ")
    }

    func testUncertainSpoolWriteBlocksBlindRetryIncludingProxy400() async {
        viewModel.selectedFilamentId = 42
        service.createSpoolError = NetworkError.clientError(400, nil)
        await viewModel.saveSpool()
        service.createSpoolError = nil
        await viewModel.saveSpool()
        XCTAssertEqual(service.createSpoolCallCount, 1)
        XCTAssertEqual(viewModel.uncertainCreation, "spool")
        XCTAssertFalse(viewModel.didSave)
        XCTAssertFalse(viewModel.isFormValid)
    }

    func testInvalidCreatedFilamentIDNeverReachesSpoolCreate() async {
        prepareNewFilament()
        service.filamentToReturn = filament(id: 0)
        await viewModel.saveSpool()
        XCTAssertEqual(service.createSpoolCallCount, 0)
        XCTAssertEqual(viewModel.uncertainCreation, "filament")
    }

    func testInvalidCreatedSpoolIDDoesNotReportSuccessOrPermitBlindRetry() async {
        viewModel.selectedFilamentId = 42
        service.spoolToReturn = spool(id: 0)
        await viewModel.saveSpool()
        await viewModel.saveSpool()
        XCTAssertFalse(viewModel.didSave)
        XCTAssertEqual(viewModel.uncertainCreation, "spool")
        XCTAssertEqual(service.createSpoolCallCount, 1)
    }

    func testLinkRetryUsesCreatedSpoolWithoutRepeatingEitherCreation() async {
        prepareNewFilament()
        await viewModel.saveSpool { _ in throw NetworkError.timeout }
        XCTAssertFalse(viewModel.didSave)
        XCTAssertNil(viewModel.uncertainCreation)
        XCTAssertEqual(viewModel.createdSpool?.id, 101)
        var linkedSpoolID: Int?
        await viewModel.saveSpool { spool in linkedSpoolID = spool.id }
        XCTAssertTrue(viewModel.didSave)
        XCTAssertEqual(linkedSpoolID, 101)
        XCTAssertEqual(service.createFilamentCallCount, 1)
        XCTAssertEqual(service.createSpoolCallCount, 1)
    }

    func testInactiveFormNeverCreatesAnything() async {
        viewModel.selectedFilamentId = 42
        viewModel.isViewActive = false
        await viewModel.saveSpool()
        XCTAssertEqual(service.createSpoolCallCount, 0)
    }

    // MARK: - Existing filament suggestions

    func testSuggestionsRequireMaterialAndNeverAutoSelect() async {
        service.filamentsToReturn = [filament(id: 41), filament(id: 42, name: "Galaxy Black")]
        await viewModel.loadReferenceData()
        viewModel.isCreatingFilament = true
        XCTAssertTrue(viewModel.filamentSuggestions.isEmpty)

        viewModel.selectedMaterial = "PLA"
        viewModel.filamentName = "red pla"

        XCTAssertEqual(viewModel.filamentSuggestions.map(\.id), [41, 42])
        XCTAssertEqual(viewModel.filamentSuggestions.map(\.match), [.sameName, .materialVendor])
        XCTAssertTrue(viewModel.hasSameNameSuggestion)
        XCTAssertNil(viewModel.selectedFilamentId)
        XCTAssertTrue(viewModel.isCreatingFilament)
    }

    func testNameMatchingIgnoresCaseSpacingPunctuationAndWordOrder() {
        let candidates = [
            filament(id: 1, name: "Prusament PLA  Galaxy-Black"),
            filament(id: 2, name: "Galaxy Silver"),
            filament(id: 3, name: "Jet Black"),
            filament(id: 4, name: "Signal White")
        ]
        let ranked = FilamentSuggestionMatcher.rankedSuggestions(
            in: candidates, material: " pla ", vendor: "prusament", name: "black GALAXY pla prusament"
        )
        XCTAssertEqual(ranked.map(\.id), [1, 2, 3, 4])
        XCTAssertEqual(ranked.map(\.match), [.sameName, .similarName, .similarName, .materialVendor])

        let partial = FilamentSuggestionMatcher.rankedSuggestions(
            in: candidates, material: "PLA", vendor: "", name: "galax"
        )
        XCTAssertEqual(partial.prefix(2).map(\.match), [.similarName, .similarName])
        XCTAssertEqual(Set(partial.prefix(2).map(\.id)), [1, 2])
    }

    func testSameNameWithDifferentMaterialOrVendorIsNeverSuggested() {
        let candidates = [
            filament(id: 1, name: "Red", material: "PETG"),
            filament(id: 2, name: "Red", material: "PLA+"),
            filament(id: 3, name: "Red", vendor: "Hatchbox"),
            filament(id: 4, name: "Red", vendor: nil),
            filament(id: 0, name: "Red"),
            filament(id: -5, name: "Red"),
            filament(id: 6, name: "Red")
        ]
        let ranked = FilamentSuggestionMatcher.rankedSuggestions(
            in: candidates, material: "PLA", vendor: "Prusament", name: "red"
        )
        XCTAssertEqual(ranked.map(\.id), [6])
        XCTAssertEqual(ranked.first?.match, .sameName)

        let anyVendor = FilamentSuggestionMatcher.rankedSuggestions(
            in: candidates, material: "PLA", vendor: "", name: "red"
        )
        XCTAssertEqual(Set(anyVendor.map(\.id)), [3, 4, 6])
    }

    func testSuggestionsAreCappedAndReportHiddenCount() async {
        service.filamentsToReturn = (1...8).map { filament(id: $0, name: "Color \($0)") }
        await viewModel.loadReferenceData()
        viewModel.isCreatingFilament = true
        viewModel.selectedMaterial = "PLA"

        XCTAssertEqual(viewModel.filamentSuggestions.count, FilamentSuggestionMatcher.limit)
        XCTAssertEqual(viewModel.hiddenFilamentSuggestionCount, 3)
    }

    func testTappingSuggestionSwitchesToExistingFilamentWithoutCreatingOne() async {
        prepareNewFilament()
        viewModel.useSuggestedFilament(id: 42)

        XCTAssertFalse(viewModel.isCreatingFilament)
        XCTAssertEqual(viewModel.selectedFilamentId, 42)
        XCTAssertEqual(viewModel.filamentName, " PLA Red ")
        await viewModel.saveSpool()
        XCTAssertTrue(viewModel.didSave)
        XCTAssertEqual(service.createFilamentCallCount, 0)
        XCTAssertEqual(service.createSpoolCalledWith?.filamentId, 42)
    }

    func testUnsuggestedOrBlockedSuggestionTapsAreIgnored() async {
        service.filamentsToReturn = [filament(id: 41), filament(id: 50, material: "PETG")]
        await viewModel.loadReferenceData()
        prepareNewFilament()

        viewModel.useSuggestedFilament(id: 50)
        viewModel.useSuggestedFilament(id: 999)
        XCTAssertTrue(viewModel.isCreatingFilament)
        XCTAssertNil(viewModel.selectedFilamentId)

        service.createFilamentError = NetworkError.timeout
        await viewModel.saveSpool()
        viewModel.useSuggestedFilament(id: 41)
        XCTAssertEqual(viewModel.uncertainCreation, "filament")
        XCTAssertTrue(viewModel.isCreatingFilament)
        XCTAssertNil(viewModel.selectedFilamentId)
        XCTAssertEqual(service.createSpoolCallCount, 0)
    }

    func testScannedFilamentLockHidesSuggestions() async {
        viewModel.prefill(from: scan(filamentID: 900))
        viewModel.isCreatingFilament = true
        viewModel.selectedMaterial = "PLA"
        XCTAssertTrue(viewModel.filamentSuggestions.isEmpty)
        viewModel.useSuggestedFilament(id: 41)
        XCTAssertEqual(viewModel.selectedFilamentId, 900)
    }

    private func prepareNewFilament() {
        viewModel.isCreatingFilament = true
        viewModel.filamentName = " PLA Red "
        viewModel.selectedMaterial = "PLA"
        viewModel.colorHex = "#FF0000"
    }

    private func scan(filamentID: Int?) -> ScannedSpoolData {
        ScannedSpoolData(
            material: "PETG", colorHex: "#FF0000", vendor: "Hatchbox",
            weight: 750, diameter: 2.85, temperature: 230,
            spoolmanId: nil, filamentId: filamentID
        )
    }

    private func filament(
        id: Int, name: String = "PLA Red", material: String = "PLA", vendor: String? = "Prusament"
    ) -> SpoolmanFilament {
        SpoolmanFilament(
            id: id, name: name, material: material, colorHex: "#FF0000",
            vendor: vendor, density: 1.24, diameter: 1.75, weight: 1000,
            spoolWeight: 200, price: nil, settingsExtruderTemp: nil,
            settingsBedTemp: nil, articleNumber: nil, gtin: nil,
            comment: nil, multiColorHexes: nil, externalId: nil
        )
    }

    private func spool(id: Int = 101) -> SpoolmanSpool {
        SpoolmanSpool(
            id: id, name: "Test Spool", material: "PLA", colorHex: "#FF0000",
            inUse: false, filamentName: "PLA Red", vendor: "Prusament",
            registeredAt: nil, firstUsedAt: nil, lastUsedAt: nil,
            remainingWeightG: 1000, initialWeightG: 1000, usedWeightG: 0,
            spoolWeightG: 200, remainingLengthMm: nil, usedLengthMm: nil,
            location: nil, lotNumber: nil, archived: false, price: nil,
            comment: nil, hasNfcTag: nil, usedPercent: 0, remainingPercent: 100
        )
    }
}
