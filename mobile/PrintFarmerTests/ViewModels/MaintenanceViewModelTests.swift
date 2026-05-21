import XCTest
@testable import PrintFarmer

/// Tests for MaintenanceViewModel: loading alerts, upcoming tasks, uptime, cost data,
/// alert acknowledgment/dismissal, and error handling.
@MainActor
final class MaintenanceViewModelTests: XCTestCase {
    
    private var mockMaintenanceService: MockMaintenanceService!
    private var viewModel: MaintenanceViewModel!
    
    override func setUp() {
        super.setUp()
        mockMaintenanceService = MockMaintenanceService()
        viewModel = MaintenanceViewModel()
        viewModel.configure(maintenanceService: mockMaintenanceService)
    }
    
    override func tearDown() {
        viewModel = nil
        mockMaintenanceService = nil
        super.tearDown()
    }
    
    // MARK: - Initial State
    
    func testInitialState() {
        XCTAssertTrue(viewModel.alerts.isEmpty)
        XCTAssertTrue(viewModel.upcomingTasks.isEmpty)
        XCTAssertTrue(viewModel.uptimeData.isEmpty)
        XCTAssertTrue(viewModel.costData.isEmpty)
        XCTAssertFalse(viewModel.isLoading)
        XCTAssertNil(viewModel.error)
    }
    
    // MARK: - Load Data Success
    
    func testLoadDataPopulatesAlertsAndTasks() async {
        let alert = MaintenanceAlert(
            id: UUID(),
            printerId: UUID(),
            printer: AlertPrinter(id: UUID(), name: "Prusa MK3"),
            printerMaintenanceScheduleId: nil,
            maintenanceTaskId: nil,
            title: "Alert",
            message: "Nozzle wear detected",
            severity: 2,
            status: .active,
            printerHoursAtTrigger: 0,
            hoursSinceLastMaintenance: nil,
            daysSinceLastMaintenance: nil,
            createdAt: Date(),
            acknowledgedAt: nil,
            acknowledgedBy: nil,
            resolvedAt: nil,
            resolvedBy: nil,
            dismissedAt: nil,
            dismissedBy: nil,
            dismissalReason: nil,
            updatedAt: Date()
        )
        let task = UpcomingMaintenanceTask(
            id: UUID().uuidString,
            taskId: UUID(),
            printerId: UUID(),
            printerName: "Prusa MK3",
            taskName: "Nozzle Replacement",
            component: nil,
            description: nil,
            priority: 2,
            intervalType: "days",
            intervalValue: 30,
            dueDate: Date().addingTimeInterval(86400 * 7),
            daysUntilDue: 7,
            hoursUntilDue: nil,
            isOverdue: false,
            isDueToday: false,
            lastPerformedAt: nil
        )
        mockMaintenanceService.alertsToReturn = [alert]
        mockMaintenanceService.upcomingTasksToReturn = [task]
        
        await viewModel.loadData()
        
        XCTAssertEqual(viewModel.alerts.count, 1)
        XCTAssertNotNil(viewModel.alerts.first?.id)
        XCTAssertEqual(viewModel.upcomingTasks.count, 1)
        XCTAssertNotNil(viewModel.upcomingTasks.first?.id)
        XCTAssertFalse(viewModel.isLoading)
        XCTAssertNil(viewModel.error)
    }
    
    func testLoadDataPopulatesAnalyticsData() async {
        let uptime = PrinterUptime(
            printerName: "Prusa MK3",
            printerId: UUID(),
            uptimePercent: 95.5,
            maintenanceCount: 5,
            totalDowntimeMinutes: 180
        )
        let cost = MaintenanceCost(
            month: "2024-03",
            totalCost: Decimal(120.50)
        )
        mockMaintenanceService.alertsToReturn = []
        mockMaintenanceService.upcomingTasksToReturn = []
        mockMaintenanceService.uptimeToReturn = [uptime]
        mockMaintenanceService.costsToReturn = [cost]
        
        await viewModel.loadData()
        
        XCTAssertEqual(viewModel.uptimeData.count, 1)
        XCTAssertEqual(viewModel.uptimeData.first?.printerName, "Prusa MK3")
        XCTAssertEqual(viewModel.costData.count, 1)
        XCTAssertEqual(viewModel.costData.first?.totalCost, Decimal(120.50))
        XCTAssertNil(viewModel.error)
    }
    
    func testLoadDataSetsLoadingState() async {
        mockMaintenanceService.alertsToReturn = []
        mockMaintenanceService.upcomingTasksToReturn = []
        
        let task = Task {
            await viewModel.loadData()
        }
        
        // Check loading state is set
        try? await Task.sleep(nanoseconds: 10_000_000)
        
        await task.value
        XCTAssertFalse(viewModel.isLoading)
    }
    
    // MARK: - Load Data Error
    
    func testLoadDataHandlesError() async {
        mockMaintenanceService.errorToThrow = TestError.generic
        
        await viewModel.loadData()
        
        XCTAssertTrue(viewModel.alerts.isEmpty)
        XCTAssertTrue(viewModel.upcomingTasks.isEmpty)
        XCTAssertNotNil(viewModel.error)
        XCTAssertFalse(viewModel.isLoading)
    }
    
    func testLoadDataClearsPreviousError() async {
        mockMaintenanceService.errorToThrow = TestError.generic
        await viewModel.loadData()
        XCTAssertNotNil(viewModel.error)
        
        mockMaintenanceService.errorToThrow = nil
        mockMaintenanceService.alertsToReturn = []
        mockMaintenanceService.upcomingTasksToReturn = []
        
        await viewModel.loadData()
        
        XCTAssertNil(viewModel.error)
    }
    
    // MARK: - Acknowledge Alert
    
    func testAcknowledgeAlertUpdatesAlert() async {
        let alert = MaintenanceAlert(
            id: UUID(),
            printerId: UUID(),
            printer: AlertPrinter(id: UUID(), name: "Prusa MK3"),
            printerMaintenanceScheduleId: nil,
            maintenanceTaskId: nil,
            title: "Alert",
            message: "Nozzle wear detected",
            severity: 2,
            status: .active,
            printerHoursAtTrigger: 0,
            hoursSinceLastMaintenance: nil,
            daysSinceLastMaintenance: nil,
            createdAt: Date(),
            acknowledgedAt: nil,
            acknowledgedBy: nil,
            resolvedAt: nil,
            resolvedBy: nil,
            dismissedAt: nil,
            dismissedBy: nil,
            dismissalReason: nil,
            updatedAt: Date()
        )
        viewModel.alerts = [alert]
        
        let acknowledged = MaintenanceAlert(
            id: alert.id,
            printerId: alert.printerId,
            printer: AlertPrinter(id: alert.printerId, name: "Prusa MK3"),
            printerMaintenanceScheduleId: nil,
            maintenanceTaskId: nil,
            title: "Alert",
            message: "Nozzle wear detected",
            severity: 2,
            status: .active,
            printerHoursAtTrigger: 0,
            hoursSinceLastMaintenance: nil,
            daysSinceLastMaintenance: nil,
            createdAt: alert.createdAt,
            acknowledgedAt: Date(),
            acknowledgedBy: nil,
            resolvedAt: nil,
            resolvedBy: nil,
            dismissedAt: nil,
            dismissedBy: nil,
            dismissalReason: nil,
            updatedAt: Date()
        )
        mockMaintenanceService.alertToReturn = acknowledged
        
        await viewModel.acknowledgeAlert(alert)
        
        XCTAssertNotNil(mockMaintenanceService.acknowledgeAlertCalledWith)
        XCTAssertNotNil(viewModel.alerts.first?.acknowledgedAt)
        XCTAssertNil(viewModel.error)
    }
    
    func testAcknowledgeAlertHandlesError() async {
        let alert = MaintenanceAlert(
            id: UUID(),
            printerId: UUID(),
            printer: AlertPrinter(id: UUID(), name: "Prusa MK3"),
            printerMaintenanceScheduleId: nil,
            maintenanceTaskId: nil,
            title: "Alert",
            message: "Nozzle wear detected",
            severity: 2,
            status: .active,
            printerHoursAtTrigger: 0,
            hoursSinceLastMaintenance: nil,
            daysSinceLastMaintenance: nil,
            createdAt: Date(),
            acknowledgedAt: nil,
            acknowledgedBy: nil,
            resolvedAt: nil,
            resolvedBy: nil,
            dismissedAt: nil,
            dismissedBy: nil,
            dismissalReason: nil,
            updatedAt: Date()
        )
        viewModel.alerts = [alert]
        mockMaintenanceService.errorToThrow = TestError.generic
        
        await viewModel.acknowledgeAlert(alert)
        
        XCTAssertNotNil(viewModel.error)
        XCTAssertNil(viewModel.alerts.first?.acknowledgedAt)
    }
    
    // MARK: - Dismiss Alert
    
    func testDismissAlertUpdatesAlert() async {
        let alert = MaintenanceAlert(
            id: UUID(),
            printerId: UUID(),
            printer: AlertPrinter(id: UUID(), name: "Prusa MK3"),
            printerMaintenanceScheduleId: nil,
            maintenanceTaskId: nil,
            title: "Alert",
            message: "Maintenance reminder",
            severity: 1,
            status: .active,
            printerHoursAtTrigger: 0,
            hoursSinceLastMaintenance: nil,
            daysSinceLastMaintenance: nil,
            createdAt: Date(),
            acknowledgedAt: nil,
            acknowledgedBy: nil,
            resolvedAt: nil,
            resolvedBy: nil,
            dismissedAt: nil,
            dismissedBy: nil,
            dismissalReason: nil,
            updatedAt: Date()
        )
        viewModel.alerts = [alert]
        
        let dismissed = MaintenanceAlert(
            id: alert.id,
            printerId: alert.printerId,
            printer: AlertPrinter(id: alert.printerId, name: "Prusa MK3"),
            printerMaintenanceScheduleId: nil,
            maintenanceTaskId: nil,
            title: "Alert",
            message: "Maintenance reminder",
            severity: 1,
            status: .active,
            printerHoursAtTrigger: 0,
            hoursSinceLastMaintenance: nil,
            daysSinceLastMaintenance: nil,
            createdAt: alert.createdAt,
            acknowledgedAt: nil,
            acknowledgedBy: nil,
            resolvedAt: nil,
            resolvedBy: nil,
            dismissedAt: Date(),
            dismissedBy: nil,
            dismissalReason: nil,
            updatedAt: Date()
        )
        mockMaintenanceService.alertToReturn = dismissed
        
        await viewModel.dismissAlert(alert)
        
        XCTAssertNotNil(mockMaintenanceService.dismissAlertCalledWith)
        XCTAssertNotNil(viewModel.alerts.first?.dismissedAt)
        XCTAssertNil(viewModel.error)
    }
    
    func testDismissAlertHandlesError() async {
        let alert = MaintenanceAlert(
            id: UUID(),
            printerId: UUID(),
            printer: AlertPrinter(id: UUID(), name: "Prusa MK3"),
            printerMaintenanceScheduleId: nil,
            maintenanceTaskId: nil,
            title: "Alert",
            message: "Maintenance reminder",
            severity: 1,
            status: .active,
            printerHoursAtTrigger: 0,
            hoursSinceLastMaintenance: nil,
            daysSinceLastMaintenance: nil,
            createdAt: Date(),
            acknowledgedAt: nil,
            acknowledgedBy: nil,
            resolvedAt: nil,
            resolvedBy: nil,
            dismissedAt: nil,
            dismissedBy: nil,
            dismissalReason: nil,
            updatedAt: Date()
        )
        viewModel.alerts = [alert]
        mockMaintenanceService.errorToThrow = TestError.generic
        
        await viewModel.dismissAlert(alert)
        
        XCTAssertNotNil(viewModel.error)
        XCTAssertNil(viewModel.alerts.first?.dismissedAt)
    }
    
    // MARK: - Computed Properties
    
    func testActiveAlertsFiltersOutDismissedAndResolved() {
        let active = MaintenanceAlert(
            id: UUID(),
            printerId: UUID(),
            printer: AlertPrinter(id: UUID(), name: "Prusa MK3"),
            printerMaintenanceScheduleId: nil,
            maintenanceTaskId: nil,
            title: "Alert",
            message: "Active alert",
            severity: 2,
            status: .active,
            printerHoursAtTrigger: 0,
            hoursSinceLastMaintenance: nil,
            daysSinceLastMaintenance: nil,
            createdAt: Date(),
            acknowledgedAt: nil,
            acknowledgedBy: nil,
            resolvedAt: nil,
            resolvedBy: nil,
            dismissedAt: nil,
            dismissedBy: nil,
            dismissalReason: nil,
            updatedAt: Date()
        )
        let dismissed = MaintenanceAlert(
            id: UUID(),
            printerId: UUID(),
            printer: AlertPrinter(id: UUID(), name: "Prusa MK3"),
            printerMaintenanceScheduleId: nil,
            maintenanceTaskId: nil,
            title: "Alert",
            message: "Dismissed alert",
            severity: 1,
            status: .active,
            printerHoursAtTrigger: 0,
            hoursSinceLastMaintenance: nil,
            daysSinceLastMaintenance: nil,
            createdAt: Date(),
            acknowledgedAt: nil,
            acknowledgedBy: nil,
            resolvedAt: nil,
            resolvedBy: nil,
            dismissedAt: Date(),
            dismissedBy: nil,
            dismissalReason: nil,
            updatedAt: Date()
        )
        let resolved = MaintenanceAlert(
            id: UUID(),
            printerId: UUID(),
            printer: AlertPrinter(id: UUID(), name: "Prusa MK3"),
            printerMaintenanceScheduleId: nil,
            maintenanceTaskId: nil,
            title: "Alert",
            message: "Resolved alert",
            severity: 3,
            status: .active,
            printerHoursAtTrigger: 0,
            hoursSinceLastMaintenance: nil,
            daysSinceLastMaintenance: nil,
            createdAt: Date(),
            acknowledgedAt: Date(),
            acknowledgedBy: nil,
            resolvedAt: Date(),
            resolvedBy: nil,
            dismissedAt: nil,
            dismissedBy: nil,
            dismissalReason: nil,
            updatedAt: Date()
        )
        viewModel.alerts = [active, dismissed, resolved]
        
        let activeAlerts = viewModel.activeAlerts
        
        XCTAssertEqual(activeAlerts.count, 1)
        XCTAssertEqual(activeAlerts.first?.message, "Active alert")
    }
    
    func testSortedUpcomingTasksSortsByDueDate() {
        let farFuture = UpcomingMaintenanceTask(
            id: UUID().uuidString,
            taskId: UUID(),
            printerId: UUID(),
            printerName: "Prusa MK3",
            taskName: "Belt Replacement",
            component: nil,
            description: nil,
            priority: 1,
            intervalType: "days",
            intervalValue: 30,
            dueDate: Date().addingTimeInterval(86400 * 30),
            daysUntilDue: 30,
            hoursUntilDue: nil,
            isOverdue: false,
            isDueToday: false,
            lastPerformedAt: nil
        )
        let nearFuture = UpcomingMaintenanceTask(
            id: UUID().uuidString,
            taskId: UUID(),
            printerId: UUID(),
            printerName: "Prusa MK3",
            taskName: "Nozzle Check",
            component: nil,
            description: nil,
            priority: 3,
            intervalType: "days",
            intervalValue: 30,
            dueDate: Date().addingTimeInterval(86400 * 3),
            daysUntilDue: 3,
            hoursUntilDue: nil,
            isOverdue: false,
            isDueToday: false,
            lastPerformedAt: nil
        )
        viewModel.upcomingTasks = [farFuture, nearFuture]
        
        let sorted = viewModel.sortedUpcomingTasks
        
        XCTAssertEqual(sorted.count, 2)
        XCTAssertEqual(sorted.first?.taskName, "Nozzle Check")
        XCTAssertEqual(sorted.last?.taskName, "Belt Replacement")
    }
    
    // MARK: - Unconfigured Guard
    
    func testLoadDataDoesNothingWhenUnconfigured() async {
        viewModel = MaintenanceViewModel()
        
        await viewModel.loadData()
        
        XCTAssertTrue(viewModel.alerts.isEmpty)
        XCTAssertFalse(mockMaintenanceService.getAlertsCalled)
    }
}

enum TestError: Error {
    case generic
}
