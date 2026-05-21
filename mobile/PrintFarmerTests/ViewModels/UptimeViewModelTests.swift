import XCTest
@testable import PrintFarmer

/// Tests for UptimeViewModel: loading uptime data and fleet statistics,
/// computed properties for aggregate metrics, and error handling.
@MainActor
final class UptimeViewModelTests: XCTestCase {
    
    private var mockMaintenanceService: MockMaintenanceService!
    private var viewModel: UptimeViewModel!
    
    override func setUp() {
        super.setUp()
        mockMaintenanceService = MockMaintenanceService()
        viewModel = UptimeViewModel()
        viewModel.configure(maintenanceService: mockMaintenanceService)
    }
    
    override func tearDown() {
        viewModel = nil
        mockMaintenanceService = nil
        super.tearDown()
    }
    
    // MARK: - Initial State
    
    func testInitialState() {
        XCTAssertTrue(viewModel.uptimeData.isEmpty)
        XCTAssertTrue(viewModel.fleetStats.isEmpty)
        XCTAssertFalse(viewModel.isLoading)
        XCTAssertNil(viewModel.error)
    }
    
    // MARK: - Load Data Success
    
    func testLoadDataPopulatesUptimeAndFleetStats() async {
        let uptime1 = PrinterUptime(
            printerName: "Prusa MK3",
            printerId: UUID(),
            uptimePercent: 95.0,
            maintenanceCount: 3,
            totalDowntimeMinutes: 180
        )
        let uptime2 = PrinterUptime(
            printerName: "Ender 3",
            printerId: UUID(),
            uptimePercent: 92.5,
            maintenanceCount: 5,
            totalDowntimeMinutes: 240
        )
        let fleetStat = FleetPrinterStatistics(
            printerId: UUID(),
            printerName: "Prusa MK3",
            manufacturerName: nil,
            modelName: nil,
            isOnline: true,
            inMaintenance: false,
            totalPrintHours: 180.5,
            totalJobsCompleted: 145,
            totalJobsFailed: 5,
            totalFilamentUsedGrams: nil,
            totalFilamentUsedMeters: nil,
            lastSyncTime: nil,
            daysUntilNextMaintenance: nil,
            nextMaintenanceTask: nil
        )
        mockMaintenanceService.uptimeToReturn = [uptime1, uptime2]
        mockMaintenanceService.fleetStatsToReturn = [fleetStat]
        
        await viewModel.loadData()
        
        XCTAssertEqual(viewModel.uptimeData.count, 2)
        XCTAssertEqual(viewModel.uptimeData.first?.printerName, "Prusa MK3")
        XCTAssertEqual(viewModel.uptimeData.last?.printerName, "Ender 3")
        XCTAssertEqual(viewModel.fleetStats.count, 1)
        XCTAssertEqual(viewModel.fleetStats.first?.totalJobsCompleted, 145)
        XCTAssertFalse(viewModel.isLoading)
        XCTAssertNil(viewModel.error)
        XCTAssertTrue(mockMaintenanceService.getUptimeCalled)
        XCTAssertTrue(mockMaintenanceService.getFleetStatisticsCalled)
    }
    
    func testLoadDataSetsLoadingState() async {
        mockMaintenanceService.uptimeToReturn = []
        mockMaintenanceService.fleetStatsToReturn = []
        
        let task = Task {
            await viewModel.loadData()
        }
        
        try? await Task.sleep(nanoseconds: 10_000_000)
        
        await task.value
        XCTAssertFalse(viewModel.isLoading)
    }
    
    // MARK: - Load Data Error
    
    func testLoadDataHandlesError() async {
        mockMaintenanceService.errorToThrow = TestError.generic
        
        await viewModel.loadData()
        
        XCTAssertTrue(viewModel.uptimeData.isEmpty)
        XCTAssertTrue(viewModel.fleetStats.isEmpty)
        XCTAssertNotNil(viewModel.error)
        XCTAssertFalse(viewModel.isLoading)
    }
    
    func testLoadDataClearsPreviousError() async {
        mockMaintenanceService.errorToThrow = TestError.generic
        await viewModel.loadData()
        XCTAssertNotNil(viewModel.error)
        
        mockMaintenanceService.errorToThrow = nil
        mockMaintenanceService.uptimeToReturn = []
        mockMaintenanceService.fleetStatsToReturn = []
        
        await viewModel.loadData()
        
        XCTAssertNil(viewModel.error)
    }
    
    // MARK: - Computed Properties
    
    func testAverageUptimeCalculatesCorrectly() {
        let uptime1 = PrinterUptime(
            printerName: "Prusa MK3",
            printerId: UUID(),
            uptimePercent: 95.0,
            maintenanceCount: 3,
            totalDowntimeMinutes: 180
        )
        let uptime2 = PrinterUptime(
            printerName: "Ender 3",
            printerId: UUID(),
            uptimePercent: 85.0,
            maintenanceCount: 5,
            totalDowntimeMinutes: 240
        )
        let uptime3 = PrinterUptime(
            printerName: "Anycubic",
            printerId: UUID(),
            uptimePercent: 90.0,
            maintenanceCount: 4,
            totalDowntimeMinutes: 200
        )
        viewModel.uptimeData = [uptime1, uptime2, uptime3]
        
        let average = viewModel.averageUptime
        XCTAssertEqual(average, 90.0, accuracy: 0.01)
    }
    
    func testAverageUptimeReturnsZeroWhenNoData() {
        viewModel.uptimeData = []
        
        XCTAssertEqual(viewModel.averageUptime, 0.0)
    }
    
    func testTotalDowntimeMinutesCalculatesCorrectly() {
        let uptime1 = PrinterUptime(
            printerName: "Prusa MK3",
            printerId: UUID(),
            uptimePercent: 95.0,
            maintenanceCount: 3,
            totalDowntimeMinutes: 180
        )
        let uptime2 = PrinterUptime(
            printerName: "Ender 3",
            printerId: UUID(),
            uptimePercent: 92.5,
            maintenanceCount: 5,
            totalDowntimeMinutes: 240
        )
        viewModel.uptimeData = [uptime1, uptime2]
        
        let total = viewModel.totalDowntimeMinutes
        XCTAssertEqual(total, 420)
    }
    
    func testTotalDowntimeMinutesReturnsZeroWhenNoData() {
        viewModel.uptimeData = []
        
        XCTAssertEqual(viewModel.totalDowntimeMinutes, 0)
    }
    
    func testTotalMaintenanceCountCalculatesCorrectly() {
        let uptime1 = PrinterUptime(
            printerName: "Prusa MK3",
            printerId: UUID(),
            uptimePercent: 95.0,
            maintenanceCount: 3,
            totalDowntimeMinutes: 180
        )
        let uptime2 = PrinterUptime(
            printerName: "Ender 3",
            printerId: UUID(),
            uptimePercent: 92.5,
            maintenanceCount: 5,
            totalDowntimeMinutes: 240
        )
        let uptime3 = PrinterUptime(
            printerName: "Anycubic",
            printerId: UUID(),
            uptimePercent: 88.0,
            maintenanceCount: 7,
            totalDowntimeMinutes: 300
        )
        viewModel.uptimeData = [uptime1, uptime2, uptime3]
        
        let total = viewModel.totalMaintenanceCount
        XCTAssertEqual(total, 15)
    }
    
    func testTotalMaintenanceCountReturnsZeroWhenNoData() {
        viewModel.uptimeData = []
        
        XCTAssertEqual(viewModel.totalMaintenanceCount, 0)
    }
    
    // MARK: - Edge Cases
    
    func testAverageUptimeWithSinglePrinter() {
        let uptime = PrinterUptime(
            printerName: "Prusa MK3",
            printerId: UUID(),
            uptimePercent: 97.5,
            maintenanceCount: 2,
            totalDowntimeMinutes: 100
        )
        viewModel.uptimeData = [uptime]
        
        XCTAssertEqual(viewModel.averageUptime, 97.5)
    }
    
    func testComputedPropertiesHandleZeroValues() {
        let uptime = PrinterUptime(
            printerName: "Prusa MK3",
            printerId: UUID(),
            uptimePercent: 0.0,
            maintenanceCount: 0,
            totalDowntimeMinutes: 0
        )
        viewModel.uptimeData = [uptime]
        
        XCTAssertEqual(viewModel.averageUptime, 0.0)
        XCTAssertEqual(viewModel.totalDowntimeMinutes, 0)
        XCTAssertEqual(viewModel.totalMaintenanceCount, 0)
    }
    
    // MARK: - Unconfigured Guard
    
    func testLoadDataDoesNothingWhenUnconfigured() async {
        viewModel = UptimeViewModel()
        
        await viewModel.loadData()
        
        XCTAssertTrue(viewModel.uptimeData.isEmpty)
        XCTAssertTrue(viewModel.fleetStats.isEmpty)
        XCTAssertFalse(mockMaintenanceService.getUptimeCalled)
        XCTAssertFalse(mockMaintenanceService.getFleetStatisticsCalled)
    }
}
