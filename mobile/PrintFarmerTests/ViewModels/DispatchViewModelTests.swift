import XCTest
@testable import PrintFarmer

/// Tests for DispatchViewModel: loading queue status, dispatch history,
/// computed properties, and error handling.
@MainActor
final class DispatchViewModelTests: XCTestCase {
    
    private var mockDispatchService: MockDispatchService!
    private var viewModel: DispatchViewModel!
    
    override func setUp() {
        super.setUp()
        mockDispatchService = MockDispatchService()
        viewModel = DispatchViewModel()
        viewModel.configure(dispatchService: mockDispatchService)
    }
    
    override func tearDown() {
        viewModel = nil
        mockDispatchService = nil
        super.tearDown()
    }
    
    // MARK: - Initial State
    
    func testInitialState() {
        XCTAssertNil(viewModel.queueStatus)
        XCTAssertTrue(viewModel.history.isEmpty)
        XCTAssertFalse(viewModel.isLoading)
        XCTAssertNil(viewModel.error)
    }
    
    // MARK: - Load Queue Status Success
    
    func testLoadQueueStatusPopulatesData() async {
        let status = DispatchQueueStatus(
            pendingUnassignedJobs: 5,
            totalQueuedJobs: 15,
            idlePrinters: 3,
            busyPrinters: 7,
            printerQueueDepths: [
                PrinterQueueDepth(
                    printerId: UUID(),
                    printerName: "Prusa MK3",
                    queueDepth: 2,
                    isPrinting: true,
                    isAvailable: true
                )
            ],
            stats: DispatchStats(
                dispatchesLast24Hours: 42,
                averageScoreLast24Hours: 85.5,
                autoDispatchesLast24Hours: 38,
                failedDispatchesLast24Hours: 4
            )
        )
        mockDispatchService.queueStatusToReturn = status
        
        await viewModel.loadQueueStatus()
        
        XCTAssertNotNil(viewModel.queueStatus)
        XCTAssertEqual(viewModel.queueStatus?.pendingUnassignedJobs, 5)
        XCTAssertEqual(viewModel.queueStatus?.totalQueuedJobs, 15)
        XCTAssertEqual(viewModel.queueStatus?.idlePrinters, 3)
        XCTAssertEqual(viewModel.queueStatus?.busyPrinters, 7)
        XCTAssertEqual(viewModel.queueStatus?.printerQueueDepths.count, 1)
        XCTAssertEqual(viewModel.queueStatus?.stats.dispatchesLast24Hours, 42)
        XCTAssertFalse(viewModel.isLoading)
        XCTAssertNil(viewModel.error)
        XCTAssertTrue(mockDispatchService.getQueueStatusCalled)
    }
    
    func testLoadQueueStatusHandlesError() async {
        mockDispatchService.errorToThrow = TestError.generic
        
        await viewModel.loadQueueStatus()
        
        XCTAssertNil(viewModel.queueStatus)
        XCTAssertNotNil(viewModel.error)
        XCTAssertFalse(viewModel.isLoading)
    }
    
    func testLoadQueueStatusClearsPreviousError() async {
        mockDispatchService.errorToThrow = TestError.generic
        await viewModel.loadQueueStatus()
        XCTAssertNotNil(viewModel.error)
        
        mockDispatchService.errorToThrow = nil
        mockDispatchService.queueStatusToReturn = DispatchQueueStatus(
            pendingUnassignedJobs: 0,
            totalQueuedJobs: 0,
            idlePrinters: 0,
            busyPrinters: 0,
            printerQueueDepths: [],
            stats: DispatchStats(
                dispatchesLast24Hours: 0,
                averageScoreLast24Hours: 0.0,
                autoDispatchesLast24Hours: 0,
                failedDispatchesLast24Hours: 0
            )
        )
        
        await viewModel.loadQueueStatus()
        
        XCTAssertNil(viewModel.error)
    }
    
    // MARK: - Load History Success
    
    func testLoadHistoryPopulatesData() async {
        let entry = DispatchHistoryEntry(
            id: UUID(),
            printJobId: UUID(),
            jobName: "test_print.gcode",
            printerId: UUID(),
            printerName: "Prusa MK3",
            action: "auto_dispatch",
            score: 92.5,
            reason: "Best match by material and availability",
            createdAtUtc: Date()
        )
        let page = DispatchHistoryPage(
            items: [entry],
            totalCount: 1,
            page: 1,
            pageSize: 50
        )
        mockDispatchService.historyPageToReturn = page
        
        await viewModel.loadHistory()
        
        XCTAssertEqual(viewModel.history.count, 1)
        XCTAssertEqual(viewModel.history.first?.jobName, "test_print.gcode")
        XCTAssertEqual(viewModel.history.first?.action, "auto_dispatch")
        XCTAssertEqual(viewModel.history.first?.score, 92.5)
        XCTAssertFalse(viewModel.isLoading)
        XCTAssertNil(viewModel.error)
        
        let called = mockDispatchService.getHistoryCalledWith
        XCTAssertEqual(called?.page, 1)
        XCTAssertEqual(called?.pageSize, 50)
    }
    
    func testLoadHistoryHandlesError() async {
        mockDispatchService.errorToThrow = TestError.generic
        
        await viewModel.loadHistory()
        
        XCTAssertNil(viewModel.history)
        XCTAssertNotNil(viewModel.error)
        XCTAssertFalse(viewModel.isLoading)
    }
    
    // MARK: - Computed Properties
    
    func testPendingJobCountReturnsCorrectValue() {
        viewModel.queueStatus = DispatchQueueStatus(
            pendingUnassignedJobs: 8,
            totalQueuedJobs: 20,
            idlePrinters: 2,
            busyPrinters: 5,
            printerQueueDepths: [],
            stats: DispatchStats(
                dispatchesLast24Hours: 30,
                averageScoreLast24Hours: 80.0,
                autoDispatchesLast24Hours: 25,
                failedDispatchesLast24Hours: 5
            )
        )
        
        XCTAssertEqual(viewModel.pendingJobCount, 8)
    }
    
    func testPendingJobCountReturnsZeroWhenStatusIsNil() {
        viewModel.queueStatus = nil
        
        XCTAssertEqual(viewModel.pendingJobCount, 0)
    }
    
    func testIdlePrinterCountReturnsCorrectValue() {
        viewModel.queueStatus = DispatchQueueStatus(
            pendingUnassignedJobs: 5,
            totalQueuedJobs: 10,
            idlePrinters: 4,
            busyPrinters: 6,
            printerQueueDepths: [],
            stats: DispatchStats(
                dispatchesLast24Hours: 20,
                averageScoreLast24Hours: 75.0,
                autoDispatchesLast24Hours: 18,
                failedDispatchesLast24Hours: 2
            )
        )
        
        XCTAssertEqual(viewModel.idlePrinterCount, 4)
    }
    
    func testIdlePrinterCountReturnsZeroWhenStatusIsNil() {
        viewModel.queueStatus = nil
        
        XCTAssertEqual(viewModel.idlePrinterCount, 0)
    }
    
    func testBusyPrinterCountReturnsCorrectValue() {
        viewModel.queueStatus = DispatchQueueStatus(
            pendingUnassignedJobs: 3,
            totalQueuedJobs: 12,
            idlePrinters: 2,
            busyPrinters: 8,
            printerQueueDepths: [],
            stats: DispatchStats(
                dispatchesLast24Hours: 35,
                averageScoreLast24Hours: 88.0,
                autoDispatchesLast24Hours: 30,
                failedDispatchesLast24Hours: 5
            )
        )
        
        XCTAssertEqual(viewModel.busyPrinterCount, 8)
    }
    
    func testBusyPrinterCountReturnsZeroWhenStatusIsNil() {
        viewModel.queueStatus = nil
        
        XCTAssertEqual(viewModel.busyPrinterCount, 0)
    }
    
    // MARK: - Unconfigured Guard
    
    func testLoadQueueStatusDoesNothingWhenUnconfigured() async {
        viewModel = DispatchViewModel()
        
        await viewModel.loadQueueStatus()
        
        XCTAssertNil(viewModel.queueStatus)
        XCTAssertFalse(mockDispatchService.getQueueStatusCalled)
    }
    
    func testLoadHistoryDoesNothingWhenUnconfigured() async {
        viewModel = DispatchViewModel()
        
        await viewModel.loadHistory()
        
        XCTAssertNil(viewModel.history)
        XCTAssertNil(mockDispatchService.getHistoryCalledWith)
    }
}
