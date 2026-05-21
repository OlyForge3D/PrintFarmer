import XCTest
@testable import PrintFarmer

/// Tests for SpoolPickerViewModel scanning flows: QR, NFC, error handling.
@MainActor
final class SpoolPickerViewModelScanTests: XCTestCase {

    private var mockSpoolService: MockSpoolService!
    private var mockScanner: MockScannerService!
    private var viewModel: SpoolPickerViewModel!

    override func setUp() {
        super.setUp()
        mockSpoolService = MockSpoolService()
        mockScanner = MockScannerService()
        viewModel = SpoolPickerViewModel()
        viewModel.configure(spoolService: mockSpoolService)
        viewModel.configureNFCScanner(mockScanner)
    }

    override func tearDown() {
        viewModel = nil
        mockScanner = nil
        mockSpoolService = nil
        super.tearDown()
    }

    // MARK: - Helpers

    private func makeSpool(id: Int = 42, material: String = "PLA") -> SpoolmanSpool {
        SpoolmanSpool(
            id: id,
            name: "\(material) Spool",
            material: material,
            colorHex: "#000000",
            inUse: false,
            filamentName: nil,
            vendor: "TestVendor",
            registeredAt: nil,
            firstUsedAt: nil,
            lastUsedAt: nil,
            remainingWeightG: 750.0,
            initialWeightG: 1000.0,
            usedWeightG: 250.0,
            spoolWeightG: 200.0,
            remainingLengthMm: nil,
            usedLengthMm: nil,
            location: nil,
            lotNumber: nil,
            archived: false,
            price: nil,
            comment: nil,
            hasNfcTag: nil,
            usedPercent: nil,
            remainingPercent: nil
        )
    }

    // MARK: - QR Scan Success

    func testQRScanSuccessSelectsSpool() async {
        let spool = makeSpool(id: 42)
        mockSpoolService.spoolsPageToReturn = SpoolmanPagedResult(items: [spool], totalCount: 1)
        await viewModel.loadSpools()

        var selectedSpool: SpoolmanSpool?
        viewModel.onAutoSelect = { selectedSpool = $0 }

        viewModel.handleQRScan(qrText: "42")

        // Allow async task to complete
        try? await Task.sleep(nanoseconds: 100_000_000)

        XCTAssertEqual(selectedSpool?.id, 42)
        XCTAssertNil(viewModel.scanError)
    }

    func testQRScanURLFormatSelectsSpool() async {
        let spool = makeSpool(id: 123)
        mockSpoolService.spoolsPageToReturn = SpoolmanPagedResult(items: [spool], totalCount: 1)
        await viewModel.loadSpools()

        var selectedSpool: SpoolmanSpool?
        viewModel.onAutoSelect = { selectedSpool = $0 }

        viewModel.handleQRScan(qrText: "https://spoolman.local/spools/123")

        try? await Task.sleep(nanoseconds: 100_000_000)

        XCTAssertEqual(selectedSpool?.id, 123)
    }

    // MARK: - QR Scan Invalid

    func testQRScanInvalidTextShowsError() {
        viewModel.handleQRScan(qrText: "random garbage text")

        XCTAssertNotNil(viewModel.scanError)
        XCTAssertTrue(viewModel.scanError?.contains("spool ID") ?? false)
    }

    func testQRScanEmptyStringShowsError() {
        viewModel.handleQRScan(qrText: "")

        XCTAssertNotNil(viewModel.scanError)
    }

    // MARK: - QR Scan: Spool Not Found

    func testQRScanSpoolNotFoundShowsError() async {
        // Empty inventory — spool 42 won't be found
        mockSpoolService.spoolsPageToReturn = SpoolmanPagedResult(items: [], totalCount: 0)

        var selectedSpool: SpoolmanSpool?
        viewModel.onAutoSelect = { selectedSpool = $0 }

        viewModel.handleQRScan(qrText: "42")

        try? await Task.sleep(nanoseconds: 200_000_000)

        XCTAssertNil(selectedSpool)
        XCTAssertNotNil(viewModel.scanError)
        XCTAssertTrue(viewModel.scanError?.contains("not found") ?? false)
    }

    // MARK: - QR Scan Dismisses Presentation

    func testQRScanDismissesSheet() {
        viewModel.isQRScannerPresented = true

        viewModel.handleQRScan(qrText: "42")

        XCTAssertFalse(viewModel.isQRScannerPresented)
    }

    // MARK: - NFC Scan: Success with SpoolId

    func testNFCScanSpoolIdSelectsSpool() async {
        let spool = makeSpool(id: 42)
        mockSpoolService.spoolsPageToReturn = SpoolmanPagedResult(items: [spool], totalCount: 1)
        await viewModel.loadSpools()

        var selectedSpool: SpoolmanSpool?
        viewModel.onAutoSelect = { selectedSpool = $0 }

        mockScanner.scanResultToReturn = .spoolId(42)

        viewModel.handleNFCScan()

        try? await Task.sleep(nanoseconds: 200_000_000)

        XCTAssertEqual(selectedSpool?.id, 42)
        XCTAssertEqual(mockScanner.scanCallCount, 1)
        XCTAssertNil(viewModel.scanError)
    }

    // MARK: - NFC Scan: New Spool Data

    func testNFCScanNewSpoolDataShowsSheet() async {
        let scannedData = ScannedSpoolData(
            material: "PLA",
            colorHex: "#FF0000",
            vendor: "Prusament",
            weight: 1000.0,
            diameter: 1.75,
            temperature: 215,
            spoolmanId: nil
        )
        mockScanner.scanResultToReturn = .newSpoolData(scannedData)

        viewModel.handleNFCScan()

        try? await Task.sleep(nanoseconds: 200_000_000)

        XCTAssertTrue(viewModel.showScannedDataSheet)
        XCTAssertNotNil(viewModel.scannedSpoolData)
        XCTAssertEqual(viewModel.scannedSpoolData?.material, "PLA")
        XCTAssertEqual(viewModel.scannedSpoolData?.vendor, "Prusament")
    }

    // MARK: - NFC Scan: Cancelled

    func testNFCScanCancelledNoAction() async {
        mockScanner.scanResultToReturn = .cancelled

        viewModel.handleNFCScan()

        try? await Task.sleep(nanoseconds: 200_000_000)

        XCTAssertNil(viewModel.scanError)
        XCTAssertFalse(viewModel.showScannedDataSheet)
        XCTAssertEqual(mockScanner.scanCallCount, 1)
    }

    // MARK: - NFC Scan: Error

    func testNFCScanErrorShowsError() async {
        mockScanner.scanResultToReturn = .error(.permissionDenied)

        viewModel.handleNFCScan()

        try? await Task.sleep(nanoseconds: 200_000_000)

        XCTAssertNotNil(viewModel.scanError)
        XCTAssertTrue(viewModel.scanError?.contains("Permission") ?? false)
    }

    func testNFCScanNotSupportedError() async {
        mockScanner.scanResultToReturn = .error(.notSupported)

        viewModel.handleNFCScan()

        try? await Task.sleep(nanoseconds: 200_000_000)

        XCTAssertNotNil(viewModel.scanError)
    }

    func testNFCScanInvalidPayloadError() async {
        mockScanner.scanResultToReturn = .error(.invalidPayload("corrupt data"))

        viewModel.handleNFCScan()

        try? await Task.sleep(nanoseconds: 200_000_000)

        XCTAssertNotNil(viewModel.scanError)
        XCTAssertTrue(viewModel.scanError?.contains("Invalid") ?? false)
    }

    // MARK: - Scanner Not Available

    func testNFCScanNotAvailableShowsError() {
        mockScanner.mockIsAvailable = false

        viewModel.handleNFCScan()

        XCTAssertNotNil(viewModel.scanError)
        XCTAssertTrue(viewModel.scanError?.contains("not available") ?? false)
        XCTAssertEqual(mockScanner.scanCallCount, 0)
    }

    // MARK: - No Scanner Configured

    func testNFCScanWithoutScannerShowsError() {
        let unconfiguredVM = SpoolPickerViewModel()
        unconfiguredVM.configure(spoolService: mockSpoolService)
        // No NFC scanner configured

        unconfiguredVM.handleNFCScan()

        XCTAssertNotNil(unconfiguredVM.scanError)
        XCTAssertTrue(unconfiguredVM.scanError?.contains("not available") ?? false)
    }

    // MARK: - Scanning State

    func testNFCScanSetsIsScanningDuringScan() async {
        mockScanner.scanResultToReturn = .cancelled

        viewModel.handleNFCScan()

        // isScanning should be set before the async scan completes
        XCTAssertTrue(viewModel.isScanning)

        try? await Task.sleep(nanoseconds: 200_000_000)

        XCTAssertFalse(viewModel.isScanning)
    }

    // MARK: - handleScanResult Directly

    func testHandleScanResultSpoolId() async {
        let spool = makeSpool(id: 55)
        mockSpoolService.spoolsPageToReturn = SpoolmanPagedResult(items: [spool], totalCount: 1)
        await viewModel.loadSpools()

        var selectedSpool: SpoolmanSpool?
        viewModel.onAutoSelect = { selectedSpool = $0 }

        await viewModel.handleScanResult(.spoolId(55))

        XCTAssertEqual(selectedSpool?.id, 55)
    }

    func testHandleScanResultNewSpoolData() async {
        let data = ScannedSpoolData(
            material: "ABS",
            colorHex: nil,
            vendor: nil,
            weight: nil,
            diameter: nil,
            temperature: nil,
            spoolmanId: nil
        )

        await viewModel.handleScanResult(.newSpoolData(data))

        XCTAssertTrue(viewModel.showScannedDataSheet)
        XCTAssertEqual(viewModel.scannedSpoolData?.material, "ABS")
    }

    func testHandleScanResultCancelled() async {
        await viewModel.handleScanResult(.cancelled)

        XCTAssertNil(viewModel.scanError)
        XCTAssertFalse(viewModel.showScannedDataSheet)
    }

    func testHandleScanResultError() async {
        await viewModel.handleScanResult(.error(.cancelled))

        XCTAssertNotNil(viewModel.scanError)
    }

    // MARK: - Network Error During Fetch

    func testQRScanNetworkErrorShowsError() async {
        mockSpoolService.errorToThrow = NetworkError.noConnection

        viewModel.handleQRScan(qrText: "42")

        try? await Task.sleep(nanoseconds: 200_000_000)

        XCTAssertNotNil(viewModel.scanError)
    }
}
