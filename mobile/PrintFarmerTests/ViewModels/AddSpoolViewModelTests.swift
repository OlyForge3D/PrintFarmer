import XCTest
@testable import PrintFarmer

/// Tests for AddSpoolViewModel: form validation, reference data loading,
/// spool creation, prefill from scan data, and error handling.
@MainActor
final class AddSpoolViewModelTests: XCTestCase {

    private var mockService: MockSpoolService!
    private var viewModel: AddSpoolViewModel!

    override func setUp() {
        super.setUp()
        mockService = MockSpoolService()
        viewModel = AddSpoolViewModel()
        viewModel.configure(spoolService: mockService)
    }

    override func tearDown() {
        viewModel = nil
        mockService = nil
        super.tearDown()
    }

    // MARK: - Initial State

    func testInitialState() {
        XCTAssertEqual(viewModel.filamentName, "")
        XCTAssertEqual(viewModel.selectedMaterial, "")
        XCTAssertEqual(viewModel.selectedVendor, "")
        XCTAssertEqual(viewModel.colorHex, "#10b981")
        XCTAssertEqual(viewModel.totalWeightG, 1000)
        XCTAssertEqual(viewModel.spoolWeightG, 200)
        XCTAssertEqual(viewModel.diameterMm, 1.75)
        XCTAssertNil(viewModel.extruderTempC)
        XCTAssertTrue(viewModel.materials.isEmpty)
        XCTAssertTrue(viewModel.vendors.isEmpty)
        XCTAssertTrue(viewModel.filaments.isEmpty)
        XCTAssertFalse(viewModel.isLoading)
        XCTAssertFalse(viewModel.isSaving)
        XCTAssertNil(viewModel.errorMessage)
        XCTAssertFalse(viewModel.didSave)
        XCTAssertFalse(viewModel.isPrefilledFromScan)
    }

    // MARK: - Form Validation

    func testFormValidWhenMaterialAndWeightPresent() {
        viewModel.selectedMaterial = "PLA"
        viewModel.totalWeightG = 1000
        XCTAssertTrue(viewModel.isFormValid)
    }

    func testFormInvalidWhenMaterialEmpty() {
        viewModel.selectedMaterial = ""
        viewModel.totalWeightG = 1000
        XCTAssertFalse(viewModel.isFormValid)
    }

    func testFormInvalidWhenWeightZero() {
        viewModel.selectedMaterial = "PLA"
        viewModel.totalWeightG = 0
        XCTAssertFalse(viewModel.isFormValid)
    }

    func testFormInvalidWhenWeightNegative() {
        viewModel.selectedMaterial = "PLA"
        viewModel.totalWeightG = -100
        XCTAssertFalse(viewModel.isFormValid)
    }

    func testFormInvalidWhenBothEmpty() {
        viewModel.selectedMaterial = ""
        viewModel.totalWeightG = 0
        XCTAssertFalse(viewModel.isFormValid)
    }

    // MARK: - Color Swatches

    func testColorSwatchesNotEmpty() {
        XCTAssertFalse(AddSpoolViewModel.colorSwatches.isEmpty)
        XCTAssertEqual(AddSpoolViewModel.colorSwatches.count, 12)
    }

    func testColorSwatchesContainBlackAndWhite() {
        let names = AddSpoolViewModel.colorSwatches.map(\.name)
        XCTAssertTrue(names.contains("Black"))
        XCTAssertTrue(names.contains("White"))
    }

    // MARK: - Load Reference Data

    func testLoadReferenceDataSuccess() async {
        let material = SpoolmanMaterial(id: 1, name: "PLA", density: 1.24, colorHex: nil)
        let vendor = SpoolmanVendor(id: 1, name: "Prusament", externalId: nil)
        let filament = SpoolmanFilament(
            id: 1, name: "PLA Basic", material: "PLA", colorHex: "#000000",
            vendor: "Prusament", density: 1.24, diameter: 1.75, weight: 1000,
            spoolWeight: 200, price: 25.0, settingsExtruderTemp: 215,
            settingsBedTemp: 60, articleNumber: nil, comment: nil,
            multiColorHexes: nil, externalId: nil
        )

        mockService.materialsToReturn = [material]
        mockService.vendorsToReturn = [vendor]
        mockService.filamentsToReturn = [filament]

        await viewModel.loadReferenceData()

        XCTAssertEqual(viewModel.materials.count, 1)
        XCTAssertEqual(viewModel.vendors.count, 1)
        XCTAssertEqual(viewModel.filaments.count, 1)
        XCTAssertTrue(mockService.listMaterialsCalled)
        XCTAssertTrue(mockService.listVendorsCalled)
        XCTAssertTrue(mockService.listFilamentsCalled)
        XCTAssertFalse(viewModel.isLoading)
        XCTAssertNil(viewModel.errorMessage)
    }

    func testLoadReferenceDataError() async {
        mockService.errorToThrow = NetworkError.serverError(500)

        await viewModel.loadReferenceData()

        XCTAssertTrue(viewModel.materials.isEmpty)
        XCTAssertTrue(viewModel.vendors.isEmpty)
        XCTAssertTrue(viewModel.filaments.isEmpty)
        XCTAssertNotNil(viewModel.errorMessage)
        XCTAssertFalse(viewModel.isLoading)
    }

    func testLoadReferenceDataWithoutConfigureShowsError() async {
        let unconfigured = AddSpoolViewModel()

        await unconfigured.loadReferenceData()

        XCTAssertEqual(unconfigured.errorMessage, "Spool service not available")
    }

    // MARK: - Save Spool

    func testSaveSpoolSuccess() async {
        viewModel.selectedMaterial = "PLA"
        viewModel.totalWeightG = 1000
        viewModel.spoolWeightG = 200

        let spool = makeTestSpool(id: 1)
        mockService.spoolToReturn = spool

        await viewModel.saveSpool()

        XCTAssertTrue(viewModel.didSave)
        XCTAssertFalse(viewModel.isSaving)
        XCTAssertNil(viewModel.errorMessage)
        XCTAssertNotNil(mockService.createSpoolCalledWith)
        XCTAssertEqual(mockService.createSpoolCalledWith?.remainingWeight, 1000)
        XCTAssertEqual(mockService.createSpoolCalledWith?.initialWeight, 1000)
        XCTAssertEqual(mockService.createSpoolCalledWith?.spoolWeight, 200)
    }

    func testSaveSpoolClearsErrorBeforeSaving() async {
        viewModel.selectedMaterial = "PLA"
        viewModel.totalWeightG = 1000
        viewModel.errorMessage = "Previous error"

        let spool = makeTestSpool(id: 1)
        mockService.spoolToReturn = spool

        await viewModel.saveSpool()

        XCTAssertNil(viewModel.errorMessage)
    }

    func testSaveSpoolError() async {
        viewModel.selectedMaterial = "PLA"
        viewModel.totalWeightG = 1000
        mockService.errorToThrow = NetworkError.serverError(500)

        await viewModel.saveSpool()

        XCTAssertFalse(viewModel.didSave)
        XCTAssertFalse(viewModel.isSaving)
        XCTAssertNotNil(viewModel.errorMessage)
    }

    func testSaveSkipsWhenFormInvalid() async {
        viewModel.selectedMaterial = ""
        viewModel.totalWeightG = 0

        await viewModel.saveSpool()

        XCTAssertNil(mockService.createSpoolCalledWith)
        XCTAssertFalse(viewModel.didSave)
    }

    func testSaveSkipsWhenNotConfigured() async {
        let unconfigured = AddSpoolViewModel()
        unconfigured.selectedMaterial = "PLA"
        unconfigured.totalWeightG = 1000

        await unconfigured.saveSpool()

        XCTAssertFalse(unconfigured.didSave)
    }

    func testSaveSpoolZeroSpoolWeightSendsNil() async {
        viewModel.selectedMaterial = "PLA"
        viewModel.totalWeightG = 1000
        viewModel.spoolWeightG = 0

        let spool = makeTestSpool(id: 1)
        mockService.spoolToReturn = spool

        await viewModel.saveSpool()

        XCTAssertNil(mockService.createSpoolCalledWith?.spoolWeight)
    }

    func testSaveSpoolMatchesFilament() async {
        let filament = SpoolmanFilament(
            id: 42, name: "PLA Basic", material: "PLA", colorHex: "#000000",
            vendor: "Prusament", density: 1.24, diameter: 1.75, weight: 1000,
            spoolWeight: 200, price: 25.0, settingsExtruderTemp: 215,
            settingsBedTemp: 60, articleNumber: nil, comment: nil,
            multiColorHexes: nil, externalId: nil
        )
        viewModel.filaments = [filament]
        viewModel.selectedMaterial = "PLA"
        viewModel.selectedVendor = "Prusament"
        viewModel.totalWeightG = 1000

        let spool = makeTestSpool(id: 1)
        mockService.spoolToReturn = spool

        await viewModel.saveSpool()

        XCTAssertEqual(mockService.createSpoolCalledWith?.filamentId, 42)
    }

    func testSaveSpoolNoFilamentMatch() async {
        let filament = SpoolmanFilament(
            id: 42, name: "PETG Basic", material: "PETG", colorHex: nil,
            vendor: "Generic", density: nil, diameter: nil, weight: nil,
            spoolWeight: nil, price: nil, settingsExtruderTemp: nil,
            settingsBedTemp: nil, articleNumber: nil, comment: nil,
            multiColorHexes: nil, externalId: nil
        )
        viewModel.filaments = [filament]
        viewModel.selectedMaterial = "PLA"
        viewModel.totalWeightG = 1000

        let spool = makeTestSpool(id: 1)
        mockService.spoolToReturn = spool

        await viewModel.saveSpool()

        XCTAssertNil(mockService.createSpoolCalledWith?.filamentId)
    }

    func testSaveSpoolFilamentMatchIsCaseInsensitive() async {
        let filament = SpoolmanFilament(
            id: 10, name: "PLA", material: "pla", colorHex: nil,
            vendor: "prusament", density: nil, diameter: nil, weight: nil,
            spoolWeight: nil, price: nil, settingsExtruderTemp: nil,
            settingsBedTemp: nil, articleNumber: nil, comment: nil,
            multiColorHexes: nil, externalId: nil
        )
        viewModel.filaments = [filament]
        viewModel.selectedMaterial = "PLA"
        viewModel.selectedVendor = "Prusament"
        viewModel.totalWeightG = 1000

        let spool = makeTestSpool(id: 1)
        mockService.spoolToReturn = spool

        await viewModel.saveSpool()

        XCTAssertEqual(mockService.createSpoolCalledWith?.filamentId, 10)
    }

    // MARK: - Prefill from Scan

    func testPrefillSetsAllFields() {
        let scannedData = ScannedSpoolData(
            material: "PETG",
            colorHex: "#FF0000",
            vendor: "Hatchbox",
            weight: 750,
            diameter: 2.85,
            temperature: 230,
            spoolmanId: nil
        )

        viewModel.prefill(from: scannedData)

        XCTAssertTrue(viewModel.isPrefilledFromScan)
        XCTAssertEqual(viewModel.selectedMaterial, "PETG")
        XCTAssertEqual(viewModel.colorHex, "#FF0000")
        XCTAssertEqual(viewModel.selectedVendor, "Hatchbox")
        XCTAssertEqual(viewModel.totalWeightG, 750)
        XCTAssertEqual(viewModel.diameterMm, 2.85)
        XCTAssertEqual(viewModel.extruderTempC, 230)
    }

    func testPrefillSkipsNilFields() {
        let scannedData = ScannedSpoolData(
            material: nil, colorHex: nil, vendor: nil,
            weight: nil, diameter: nil, temperature: nil, spoolmanId: nil
        )

        viewModel.prefill(from: scannedData)

        XCTAssertTrue(viewModel.isPrefilledFromScan)
        // Defaults should be preserved
        XCTAssertEqual(viewModel.selectedMaterial, "")
        XCTAssertEqual(viewModel.colorHex, "#10b981")
        XCTAssertEqual(viewModel.selectedVendor, "")
        XCTAssertEqual(viewModel.totalWeightG, 1000)
        XCTAssertEqual(viewModel.diameterMm, 1.75)
        XCTAssertNil(viewModel.extruderTempC)
    }

    func testPrefillSkipsEmptyStrings() {
        let scannedData = ScannedSpoolData(
            material: "", colorHex: "", vendor: "",
            weight: nil, diameter: nil, temperature: nil, spoolmanId: nil
        )

        viewModel.prefill(from: scannedData)

        XCTAssertEqual(viewModel.selectedMaterial, "")
        XCTAssertEqual(viewModel.colorHex, "#10b981")
        XCTAssertEqual(viewModel.selectedVendor, "")
    }

    func testPrefillSkipsZeroWeight() {
        let scannedData = ScannedSpoolData(
            material: nil, colorHex: nil, vendor: nil,
            weight: 0, diameter: 0, temperature: 0, spoolmanId: nil
        )

        viewModel.prefill(from: scannedData)

        XCTAssertEqual(viewModel.totalWeightG, 1000)
        XCTAssertEqual(viewModel.diameterMm, 1.75)
        XCTAssertNil(viewModel.extruderTempC)
    }

    func testPrefillSkipsNegativeValues() {
        let scannedData = ScannedSpoolData(
            material: nil, colorHex: nil, vendor: nil,
            weight: -100, diameter: -1.75, temperature: -10, spoolmanId: nil
        )

        viewModel.prefill(from: scannedData)

        XCTAssertEqual(viewModel.totalWeightG, 1000)
        XCTAssertEqual(viewModel.diameterMm, 1.75)
        XCTAssertNil(viewModel.extruderTempC)
    }

    // MARK: - Helpers

    private func makeTestSpool(id: Int) -> SpoolmanSpool {
        SpoolmanSpool(
            id: id, name: "Test Spool", material: "PLA",
            colorHex: "#000000", inUse: false, filamentName: "PLA Basic",
            vendor: "Prusament", registeredAt: nil, firstUsedAt: nil,
            lastUsedAt: nil, remainingWeightG: 1000, initialWeightG: 1000,
            usedWeightG: 0, spoolWeightG: 200, remainingLengthMm: nil,
            usedLengthMm: nil, location: nil, lotNumber: nil,
            archived: false, price: nil, comment: nil,
            hasNfcTag: nil, usedPercent: 0, remainingPercent: 100
        )
    }
}
