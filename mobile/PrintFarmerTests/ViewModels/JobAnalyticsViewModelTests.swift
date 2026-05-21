import XCTest
@testable import PrintFarmer

/// Tests for JobAnalyticsViewModel: loading queued jobs with filters, stats, model stats,
/// applying and clearing filters, and error handling.
@MainActor
final class JobAnalyticsViewModelTests: XCTestCase {
    
    private var mockJobAnalyticsService: MockJobAnalyticsService!
    private var viewModel: JobAnalyticsViewModel!
    
    override func setUp() {
        super.setUp()
        mockJobAnalyticsService = MockJobAnalyticsService()
        viewModel = JobAnalyticsViewModel()
        viewModel.configure(jobAnalyticsService: mockJobAnalyticsService)
    }
    
    override func tearDown() {
        viewModel = nil
        mockJobAnalyticsService = nil
        super.tearDown()
    }
    
    // MARK: - Initial State
    
    func testInitialState() {
        XCTAssertTrue(viewModel.jobs.isEmpty)
        XCTAssertNil(viewModel.stats)
        XCTAssertTrue(viewModel.modelStats.isEmpty)
        XCTAssertNil(viewModel.selectedStatus)
        XCTAssertNil(viewModel.selectedModel)
        XCTAssertNil(viewModel.selectedMaterial)
        XCTAssertFalse(viewModel.isLoading)
        XCTAssertNil(viewModel.error)
    }
    
    // MARK: - Load Jobs Success
    
    func testLoadJobsPopulatesData() async {
        let job = QueuedJobWithMeta(
            job: QueuedJobAnalytics(
                id: "1",
                name: "test_print.gcode",
                status: "queued",
                priority: 5,
                queuePosition: 1,
                assignedPrinterId: nil,
                printerName: nil,
                printerModel: nil,
                fileName: "test_print.gcode",
                thumbnailUrl: nil,
                createdAt: Date(),
                startedAt: nil,
                completedAt: nil
            ),
            gcodeFile: GcodeFileMeta(
                id: "gcode1",
                fileName: "test_print.gcode",
                materialType: "PLA",
                nozzleDiameter: 0.4,
                thumbnailUrl: nil
            ),
            assignedPrinter: PrinterMeta(
                id: "printer1",
                name: "Prusa MK3",
                model: "Prusa MK3S"
            ),
            estimatedStartTime: nil,
            estimatedCompletionTime: nil
        )
        mockJobAnalyticsService.queuedJobsToReturn = [job]
        
        await viewModel.loadJobs()
        
        XCTAssertEqual(viewModel.jobs.count, 1)
        XCTAssertEqual(viewModel.jobs.first?.job.id, "1")
        XCTAssertEqual(viewModel.jobs.first?.job.name, "test_print.gcode")
        XCTAssertFalse(viewModel.isLoading)
        XCTAssertNil(viewModel.error)
    }
    
    func testLoadJobsWithFilters() async {
        mockJobAnalyticsService.queuedJobsToReturn = []
        viewModel.selectedStatus = "printing"
        viewModel.selectedModel = "Prusa MK3S"
        viewModel.selectedMaterial = "PLA"
        
        await viewModel.loadJobs()
        
        let called = mockJobAnalyticsService.getQueuedJobsCalledWith
        XCTAssertEqual(called?.filterStatus, "printing")
        XCTAssertEqual(called?.filterModel, "Prusa MK3S")
        XCTAssertEqual(called?.filterMaterial, "PLA")
        XCTAssertNil(called?.limit)
        XCTAssertNil(called?.offset)
    }
    
    func testLoadJobsHandlesError() async {
        mockJobAnalyticsService.errorToThrow = TestError.generic
        
        await viewModel.loadJobs()
        
        XCTAssertTrue(viewModel.jobs.isEmpty)
        XCTAssertNotNil(viewModel.error)
        XCTAssertFalse(viewModel.isLoading)
    }
    
    func testLoadJobsClearsPreviousError() async {
        mockJobAnalyticsService.errorToThrow = TestError.generic
        await viewModel.loadJobs()
        XCTAssertNotNil(viewModel.error)
        
        mockJobAnalyticsService.errorToThrow = nil
        mockJobAnalyticsService.queuedJobsToReturn = []
        
        await viewModel.loadJobs()
        
        XCTAssertNil(viewModel.error)
    }
    
    // MARK: - Load Stats Success
    
    func testLoadStatsPopulatesData() async {
        let stats = QueueStats(
            totalQueued: 10,
            totalPrinting: 3,
            totalPaused: 1,
            averageWaitTimeMinutes: 45,
            byModel: []
        )
        let modelStats = [
            QueuePrinterModelStats(
                modelName: "Prusa MK3S",
                totalQueued: 5,
                currentlyPrinting: 2,
                oldestQueuedAtUtc: nil,
                averageQueueWaitMinutes: 40
            )
        ]
        mockJobAnalyticsService.statsToReturn = stats
        mockJobAnalyticsService.modelStatsToReturn = modelStats
        
        await viewModel.loadStats()
        
        XCTAssertNotNil(viewModel.stats)
        XCTAssertEqual(viewModel.stats?.totalQueued, 10)
        XCTAssertEqual(viewModel.stats?.totalPrinting, 3)
        XCTAssertEqual(viewModel.modelStats.count, 1)
        XCTAssertEqual(viewModel.modelStats.first?.modelName, "Prusa MK3S")
        XCTAssertFalse(viewModel.isLoading)
        XCTAssertNil(viewModel.error)
    }
    
    func testLoadStatsHandlesError() async {
        mockJobAnalyticsService.errorToThrow = TestError.generic
        
        await viewModel.loadStats()
        
        XCTAssertNil(viewModel.stats)
        XCTAssertTrue(viewModel.modelStats.isEmpty)
        XCTAssertNotNil(viewModel.error)
        XCTAssertFalse(viewModel.isLoading)
    }
    
    // MARK: - Apply Filters
    
    func testApplyFiltersReloadsJobs() async {
        mockJobAnalyticsService.queuedJobsToReturn = []
        viewModel.selectedStatus = "queued"
        
        await viewModel.applyFilters()
        
        XCTAssertNotNil(mockJobAnalyticsService.getQueuedJobsCalledWith)
        XCTAssertEqual(mockJobAnalyticsService.getQueuedJobsCalledWith?.filterStatus, "queued")
    }
    
    func testApplyFiltersWithMultipleFilters() async {
        mockJobAnalyticsService.queuedJobsToReturn = []
        viewModel.selectedStatus = "printing"
        viewModel.selectedModel = "Prusa MK3S"
        viewModel.selectedMaterial = "PETG"
        
        await viewModel.applyFilters()
        
        let called = mockJobAnalyticsService.getQueuedJobsCalledWith
        XCTAssertEqual(called?.filterStatus, "printing")
        XCTAssertEqual(called?.filterModel, "Prusa MK3S")
        XCTAssertEqual(called?.filterMaterial, "PETG")
    }
    
    // MARK: - Clear Filters
    
    func testClearFiltersResetsFiltersAndReloadsJobs() async {
        mockJobAnalyticsService.queuedJobsToReturn = []
        viewModel.selectedStatus = "printing"
        viewModel.selectedModel = "Prusa MK3S"
        viewModel.selectedMaterial = "PLA"
        
        await viewModel.clearFilters()
        
        XCTAssertNil(viewModel.selectedStatus)
        XCTAssertNil(viewModel.selectedModel)
        XCTAssertNil(viewModel.selectedMaterial)
        
        let called = mockJobAnalyticsService.getQueuedJobsCalledWith
        XCTAssertNil(called?.filterStatus)
        XCTAssertNil(called?.filterModel)
        XCTAssertNil(called?.filterMaterial)
    }
    
    // MARK: - Unconfigured Guard
    
    func testLoadJobsDoesNothingWhenUnconfigured() async {
        viewModel = JobAnalyticsViewModel()
        
        await viewModel.loadJobs()
        
        XCTAssertTrue(viewModel.jobs.isEmpty)
        XCTAssertNil(mockJobAnalyticsService.getQueuedJobsCalledWith)
    }
    
    func testLoadStatsDoesNothingWhenUnconfigured() async {
        viewModel = JobAnalyticsViewModel()
        
        await viewModel.loadStats()
        
        XCTAssertNil(viewModel.stats)
        XCTAssertFalse(mockJobAnalyticsService.getStatsCalled)
    }
}
