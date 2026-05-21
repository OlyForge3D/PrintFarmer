import XCTest
@testable import PrintFarmer

@MainActor
final class JobListViewModelTests: XCTestCase {

    private var mockJobService: MockJobService!
    private var viewModel: JobListViewModel!

    override func setUp() {
        super.setUp()
        mockJobService = MockJobService()
        viewModel = JobListViewModel()
        viewModel.configure(jobService: mockJobService)
    }

    override func tearDown() {
        viewModel = nil
        mockJobService = nil
        super.tearDown()
    }

    // MARK: - Initial State

    func testInitialState() {
        XCTAssertTrue(viewModel.jobs.isEmpty)
        XCTAssertFalse(viewModel.isLoading)
        XCTAssertNil(viewModel.errorMessage)
        XCTAssertFalse(viewModel.showRecentJobs)
        XCTAssertFalse(viewModel.hasAnyJobs)
    }

    // MARK: - Load Jobs

    func testLoadJobsCallsListAllJobs() async throws {
        let job = try TestData.decodeQueuedPrintJobResponse(from: TestJSON.queuedPrintJobResponsePrinting)
        mockJobService.queuedJobResponsesToReturn = [job]

        await viewModel.loadJobs()

        XCTAssertTrue(mockJobService.listAllJobsCalled)
        XCTAssertEqual(viewModel.jobs.count, 1)
        XCTAssertFalse(viewModel.isLoading)
        XCTAssertNil(viewModel.errorMessage)
    }

    func testLoadJobsSetsLoadingState() async {
        mockJobService.queuedJobResponsesToReturn = []
        await viewModel.loadJobs()
        XCTAssertFalse(viewModel.isLoading)
    }

    func testLoadJobsHandlesError() async {
        mockJobService.errorToThrow = NetworkError.noConnection

        await viewModel.loadJobs()

        XCTAssertFalse(viewModel.isLoading)
        XCTAssertNotNil(viewModel.errorMessage)
        XCTAssertTrue(viewModel.jobs.isEmpty)
    }

    func testLoadJobsHandlesServerError() async {
        mockJobService.errorToThrow = NetworkError.serverError(500)

        await viewModel.loadJobs()

        XCTAssertNotNil(viewModel.errorMessage)
    }

    func testLoadJobsClearsErrorOnSuccess() async throws {
        mockJobService.errorToThrow = NetworkError.noConnection
        await viewModel.loadJobs()
        XCTAssertNotNil(viewModel.errorMessage)

        mockJobService.errorToThrow = nil
        mockJobService.queuedJobResponsesToReturn = []
        await viewModel.loadJobs()

        XCTAssertNil(viewModel.errorMessage)
    }

    // MARK: - Without Configuration

    func testLoadWithoutConfigureDoesNotCrash() async {
        let unconfigured = JobListViewModel()
        await unconfigured.loadJobs()
        XCTAssertFalse(unconfigured.isLoading)
        XCTAssertNil(unconfigured.errorMessage)
    }

    // MARK: - Grouped Jobs: Active

    func testActiveJobsFiltersPrintingAndPaused() async throws {
        let printing = try TestData.decodeQueuedPrintJobResponse(from: TestJSON.queuedPrintJobResponsePrinting)
        let paused = try TestData.decodeQueuedPrintJobResponse(from: TestJSON.queuedPrintJobResponsePaused)
        let queued = try TestData.decodeQueuedPrintJobResponse(from: TestJSON.queuedPrintJobResponseQueued)
        let completed = try TestData.decodeQueuedPrintJobResponse(from: TestJSON.queuedPrintJobResponseCompleted)
        mockJobService.queuedJobResponsesToReturn = [printing, paused, queued, completed]

        await viewModel.loadJobs()

        XCTAssertEqual(viewModel.activeJobs.count, 2)
    }

    func testActiveJobsIncludesAssigned() async throws {
        // Assigned jobs should NOT be in activeJobs (they're in queuedJobs)
        let assigned = try TestData.decodeQueuedPrintJobResponse(from: TestJSON.queuedPrintJobResponseAssigned)
        mockJobService.queuedJobResponsesToReturn = [assigned]

        await viewModel.loadJobs()

        XCTAssertEqual(viewModel.activeJobs.count, 0)
    }

    func testActiveJobsEmptyWhenNoActive() async throws {
        let completed = try TestData.decodeQueuedPrintJobResponse(from: TestJSON.queuedPrintJobResponseCompleted)
        mockJobService.queuedJobResponsesToReturn = [completed]

        await viewModel.loadJobs()

        XCTAssertTrue(viewModel.activeJobs.isEmpty)
    }

    // MARK: - Grouped Jobs: Queued

    func testQueuedJobsFiltersQueuedAndAssigned() async throws {
        let queued = try TestData.decodeQueuedPrintJobResponse(from: TestJSON.queuedPrintJobResponseQueued)
        let assigned = try TestData.decodeQueuedPrintJobResponse(from: TestJSON.queuedPrintJobResponseAssigned)
        let printing = try TestData.decodeQueuedPrintJobResponse(from: TestJSON.queuedPrintJobResponsePrinting)
        mockJobService.queuedJobResponsesToReturn = [queued, assigned, printing]

        await viewModel.loadJobs()

        XCTAssertEqual(viewModel.queuedJobs.count, 2)
    }

    func testQueuedJobsSortedByPosition() async throws {
        let queued = try TestData.decodeQueuedPrintJobResponse(from: TestJSON.queuedPrintJobResponseQueued)
        let assigned = try TestData.decodeQueuedPrintJobResponse(from: TestJSON.queuedPrintJobResponseAssigned)
        mockJobService.queuedJobResponsesToReturn = [queued, assigned]

        await viewModel.loadJobs()

        let positions = viewModel.queuedJobs.map(\.job.queuePosition)
        XCTAssertEqual(positions, positions.sorted())
    }

    // MARK: - Grouped Jobs: Recent

    func testRecentJobsFiltersCompletedFailedCancelled() async throws {
        let completed = try TestData.decodeQueuedPrintJobResponse(from: TestJSON.queuedPrintJobResponseCompleted)
        let failed = try TestData.decodeQueuedPrintJobResponse(from: TestJSON.queuedPrintJobResponseFailed)
        let printing = try TestData.decodeQueuedPrintJobResponse(from: TestJSON.queuedPrintJobResponsePrinting)
        mockJobService.queuedJobResponsesToReturn = [completed, failed, printing]

        await viewModel.loadJobs()

        XCTAssertEqual(viewModel.recentJobs.count, 2)
    }

    // MARK: - hasAnyJobs

    func testHasAnyJobsTrueWhenJobsExist() async throws {
        let job = try TestData.decodeQueuedPrintJobResponse(from: TestJSON.queuedPrintJobResponsePrinting)
        mockJobService.queuedJobResponsesToReturn = [job]

        await viewModel.loadJobs()

        XCTAssertTrue(viewModel.hasAnyJobs)
    }

    func testHasAnyJobsFalseWhenEmpty() async {
        mockJobService.queuedJobResponsesToReturn = []
        await viewModel.loadJobs()
        XCTAssertFalse(viewModel.hasAnyJobs)
    }

    // MARK: - Cancel Job

    func testCancelJobCallsService() async {
        let id = UUID()
        mockJobService.queuedJobResponsesToReturn = []

        await viewModel.cancelJob(id: id)

        XCTAssertEqual(mockJobService.cancelCalledWith, id)
    }

    func testCancelJobReloadsOnSuccess() async {
        let id = UUID()
        mockJobService.queuedJobResponsesToReturn = []

        await viewModel.cancelJob(id: id)

        XCTAssertTrue(mockJobService.listAllJobsCalled)
    }

    func testCancelJobSetsErrorOnFailure() async {
        let id = UUID()
        mockJobService.errorToThrow = NetworkError.serverError(500)

        await viewModel.cancelJob(id: id)

        XCTAssertNotNil(viewModel.errorMessage)
    }

    func testCancelWithoutConfigureDoesNotCrash() async {
        let unconfigured = JobListViewModel()
        await unconfigured.cancelJob(id: UUID())
        XCTAssertNil(unconfigured.errorMessage)
    }

    // MARK: - Abort Job

    func testAbortJobCallsService() async {
        let id = UUID()
        mockJobService.queuedJobResponsesToReturn = []

        await viewModel.abortJob(id: id)

        XCTAssertEqual(mockJobService.abortCalledWith, id)
    }

    func testAbortJobReloadsOnSuccess() async {
        let id = UUID()
        mockJobService.queuedJobResponsesToReturn = []

        await viewModel.abortJob(id: id)

        XCTAssertTrue(mockJobService.listAllJobsCalled)
    }

    func testAbortJobSetsErrorOnFailure() async {
        let id = UUID()
        mockJobService.errorToThrow = NetworkError.serverError(500)

        await viewModel.abortJob(id: id)

        XCTAssertNotNil(viewModel.errorMessage)
    }

    // MARK: - Empty State

    func testAllGroupsEmptyWithNoJobs() async {
        mockJobService.queuedJobResponsesToReturn = []
        await viewModel.loadJobs()

        XCTAssertTrue(viewModel.activeJobs.isEmpty)
        XCTAssertTrue(viewModel.queuedJobs.isEmpty)
        XCTAssertTrue(viewModel.recentJobs.isEmpty)
    }
}
