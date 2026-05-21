import XCTest
@testable import PrintFarmer

@MainActor
final class JobDetailViewModelTests: XCTestCase {

    private var mockJobService: MockJobService!
    private var viewModel: JobDetailViewModel!
    private let testJobId = UUID(uuidString: "770e8400-e29b-41d4-a716-446655440002")!

    override func setUp() {
        super.setUp()
        mockJobService = MockJobService()
        viewModel = JobDetailViewModel(jobId: testJobId)
        viewModel.configure(jobService: mockJobService)
    }

    override func tearDown() {
        viewModel = nil
        mockJobService = nil
        super.tearDown()
    }

    // MARK: - Initial State

    func testInitialState() {
        XCTAssertNil(viewModel.job)
        XCTAssertFalse(viewModel.isLoading)
        XCTAssertNil(viewModel.errorMessage)
        XCTAssertFalse(viewModel.isPerformingAction)
        XCTAssertNil(viewModel.actionError)
        XCTAssertFalse(viewModel.showCancelConfirmation)
        XCTAssertEqual(viewModel.jobId, testJobId)
    }

    // MARK: - Load Job

    func testLoadJobSuccess() async throws {
        let job = try TestData.decodePrintJob()
        mockJobService.jobToReturn = job

        await viewModel.loadJob()

        XCTAssertNotNil(viewModel.job)
        XCTAssertEqual(mockJobService.getJobCalledWith, testJobId)
        XCTAssertFalse(viewModel.isLoading)
        XCTAssertNil(viewModel.errorMessage)
    }

    func testLoadJobSetsError() async {
        mockJobService.errorToThrow = NetworkError.notFound

        await viewModel.loadJob()

        XCTAssertNil(viewModel.job)
        XCTAssertNotNil(viewModel.errorMessage)
        XCTAssertFalse(viewModel.isLoading)
    }

    func testLoadWithoutConfigureDoesNotCrash() async {
        let unconfigured = JobDetailViewModel(jobId: testJobId)
        await unconfigured.loadJob()
        XCTAssertFalse(unconfigured.isLoading)
    }

    // MARK: - Dispatch

    func testDispatchJobCallsService() async throws {
        let job = try TestData.decodePrintJob()
        mockJobService.jobToReturn = job

        await viewModel.dispatchJob()

        XCTAssertEqual(mockJobService.dispatchCalledWith, testJobId)
        XCTAssertFalse(viewModel.isPerformingAction)
    }

    func testDispatchJobSetsActionError() async {
        mockJobService.errorToThrow = NetworkError.serverError(500)

        await viewModel.dispatchJob()

        XCTAssertNotNil(viewModel.actionError)
        XCTAssertFalse(viewModel.isPerformingAction)
    }

    func testDispatchReloadsJobOnSuccess() async throws {
        let job = try TestData.decodePrintJob()
        mockJobService.jobToReturn = job

        await viewModel.dispatchJob()

        XCTAssertEqual(mockJobService.getJobCalledWith, testJobId)
    }

    // MARK: - Cancel

    func testCancelJobCallsService() async throws {
        let job = try TestData.decodePrintJob()
        mockJobService.jobToReturn = job

        await viewModel.cancelJob()

        XCTAssertEqual(mockJobService.cancelCalledWith, testJobId)
    }

    func testCancelJobSetsActionError() async {
        mockJobService.errorToThrow = NetworkError.serverError(500)

        await viewModel.cancelJob()

        XCTAssertNotNil(viewModel.actionError)
    }

    // MARK: - Abort

    func testAbortJobCallsService() async throws {
        let job = try TestData.decodePrintJob()
        mockJobService.jobToReturn = job

        await viewModel.abortJob()

        XCTAssertEqual(mockJobService.abortCalledWith, testJobId)
    }

    func testAbortJobSetsActionError() async {
        mockJobService.errorToThrow = NetworkError.serverError(500)

        await viewModel.abortJob()

        XCTAssertNotNil(viewModel.actionError)
    }

    // MARK: - Computed Properties

    func testCanDispatchTrueForQueuedJob() async throws {
        let job = try TestData.decodePrintJob(from: TestJSON.printJobQueued)
        mockJobService.jobToReturn = job
        await viewModel.loadJob()

        XCTAssertTrue(viewModel.canDispatch)
    }

    func testCanDispatchFalseForPrintingJob() async throws {
        let job = try TestData.decodePrintJob(from: TestJSON.printJob)
        mockJobService.jobToReturn = job
        await viewModel.loadJob()

        XCTAssertFalse(viewModel.canDispatch)
    }

    func testCanDispatchFalseWhenNoJob() {
        XCTAssertFalse(viewModel.canDispatch)
    }

    func testCanCancelTrueForQueuedJob() async throws {
        let job = try TestData.decodePrintJob(from: TestJSON.printJobQueued)
        mockJobService.jobToReturn = job
        await viewModel.loadJob()

        XCTAssertTrue(viewModel.canCancel)
    }

    func testCanCancelFalseForPrintingJob() async throws {
        let job = try TestData.decodePrintJob(from: TestJSON.printJob)
        mockJobService.jobToReturn = job
        await viewModel.loadJob()

        XCTAssertFalse(viewModel.canCancel)
    }

    func testCanAbortTrueForPrintingJob() async throws {
        let job = try TestData.decodePrintJob(from: TestJSON.printJob)
        mockJobService.jobToReturn = job
        await viewModel.loadJob()

        XCTAssertTrue(viewModel.canAbort)
    }

    func testCanAbortFalseForQueuedJob() async throws {
        let job = try TestData.decodePrintJob(from: TestJSON.printJobQueued)
        mockJobService.jobToReturn = job
        await viewModel.loadJob()

        XCTAssertFalse(viewModel.canAbort)
    }

    func testCanAbortFalseWhenNoJob() {
        XCTAssertFalse(viewModel.canAbort)
    }

    func testIsActiveTrueForPrintingJob() async throws {
        let job = try TestData.decodePrintJob(from: TestJSON.printJob)
        mockJobService.jobToReturn = job
        await viewModel.loadJob()

        XCTAssertTrue(viewModel.isActive)
    }

    func testIsActiveFalseForQueuedJob() async throws {
        let job = try TestData.decodePrintJob(from: TestJSON.printJobQueued)
        mockJobService.jobToReturn = job
        await viewModel.loadJob()

        XCTAssertFalse(viewModel.isActive)
    }

    func testIsActiveFalseWhenNoJob() {
        XCTAssertFalse(viewModel.isActive)
    }

    // MARK: - Action Without Configure

    func testDispatchWithoutConfigureDoesNotCrash() async {
        let unconfigured = JobDetailViewModel(jobId: testJobId)
        await unconfigured.dispatchJob()
        XCTAssertFalse(unconfigured.isPerformingAction)
    }

    func testCancelWithoutConfigureDoesNotCrash() async {
        let unconfigured = JobDetailViewModel(jobId: testJobId)
        await unconfigured.cancelJob()
        XCTAssertFalse(unconfigured.isPerformingAction)
    }
}
