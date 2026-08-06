import XCTest
@testable import PrintFarmer

/// Tests for AutoDispatchViewModel: loading status, marking ready, skipping, toggling enabled state,
/// and error handling.
@MainActor
final class AutoDispatchViewModelTests: XCTestCase {

    private var mockAutoDispatchService: MockAutoDispatchService!
    private var viewModel: AutoDispatchViewModel!
    private let testPrinterId = UUID()

    override func setUp() async throws {
        try await super.setUp()
        mockAutoDispatchService = MockAutoDispatchService()
        viewModel = AutoDispatchViewModel()
        viewModel.configure(autoDispatchService: mockAutoDispatchService)
    }

    override func tearDown() async throws {
        viewModel = nil
        mockAutoDispatchService = nil
        try await super.tearDown()
    }

    // MARK: - Initial State

    func testInitialState() {
        XCTAssertNil(viewModel.status)
        XCTAssertNil(viewModel.readyResult)
        XCTAssertFalse(viewModel.isLoading)
        XCTAssertNil(viewModel.error)
    }

    // MARK: - Load Status Success

    func testLoadStatusPopulatesData() async {
        let status = AutoDispatchStatus(
            printerId: testPrinterId,
            enabled: true,
            queueDepth: 3,
            state: "ready"
        )
        mockAutoDispatchService.statusToReturn = status

        await viewModel.loadStatus(printerId: testPrinterId)

        XCTAssertNotNil(viewModel.status)
        XCTAssertEqual(viewModel.status?.printerId, testPrinterId)
        XCTAssertEqual(viewModel.status?.enabled, true)
        XCTAssertEqual(viewModel.status?.state, "ready")
        XCTAssertEqual(viewModel.status?.queueDepth, 3)
        XCTAssertFalse(viewModel.isLoading)
        XCTAssertNil(viewModel.error)
        XCTAssertEqual(mockAutoDispatchService.getStatusCalledWith, testPrinterId)
    }

    func testLoadStatusSetsLoadingState() async {
        let status = AutoDispatchStatus(
            printerId: testPrinterId,
            enabled: true,
            queueDepth: 0,
            state: "ready"
        )
        mockAutoDispatchService.statusToReturn = status

        let task = Task {
            await viewModel.loadStatus(printerId: testPrinterId)
        }

        try? await Task.sleep(nanoseconds: 10_000_000)

        await task.value
        XCTAssertFalse(viewModel.isLoading)
    }

    // MARK: - Load Status Error

    func testLoadStatusHandlesError() async {
        mockAutoDispatchService.errorToThrow = TestError.generic

        await viewModel.loadStatus(printerId: testPrinterId)

        XCTAssertNil(viewModel.status)
        XCTAssertNotNil(viewModel.error)
        XCTAssertFalse(viewModel.isLoading)
    }

    func testLoadStatusClearsPreviousError() async {
        mockAutoDispatchService.errorToThrow = TestError.generic
        await viewModel.loadStatus(printerId: testPrinterId)
        XCTAssertNotNil(viewModel.error)

        mockAutoDispatchService.errorToThrow = nil
        mockAutoDispatchService.statusToReturn = AutoDispatchStatus(
            printerId: testPrinterId,
            enabled: false,
            queueDepth: 0,
            state: "idle"
        )

        await viewModel.loadStatus(printerId: testPrinterId)

        XCTAssertNil(viewModel.error)
    }

    // MARK: - Mark Ready

    func testMarkReadyUpdatesReadyResult() async {
        let readyResult = AutoDispatchReadyResult(
            status: AutoDispatchStatus(
                printerId: testPrinterId,
                enabled: true,
                queueDepth: 2,
                state: "ready"
            ),
            nextJob: AutoDispatchNextJob(
                id: UUID(),
                name: "test_print.gcode",
                estimatedFilamentUsageG: 50.5,
                requiredMaterialType: nil,
                estimatedPrintTime: "01:00:00"
            ),
            filamentCheck: FilamentCheckResult(
                sufficient: true,
                remainingWeightG: 100.0,
                requiredWeightG: 50.5,
                loadedMaterial: nil,
                requiredMaterial: nil,
                materialMismatch: false,
                message: nil
            ),
            dispatchInitiated: true,
            dispatchOutcome: "Accepted"
        )
        mockAutoDispatchService.readyResultToReturn = readyResult
        mockAutoDispatchService.statusToReturn = readyResult.status
        viewModel.status = readyResult.status

        await viewModel.markReady(printerId: testPrinterId)

        XCTAssertNotNil(viewModel.readyResult)
        XCTAssertNotNil(viewModel.readyResult?.nextJob?.id)
        XCTAssertEqual(viewModel.readyResult?.filamentCheck?.sufficient, true)
        XCTAssertNotNil(viewModel.status)
        XCTAssertEqual(viewModel.status?.state, "ready")
        XCTAssertNil(viewModel.error)
        XCTAssertEqual(mockAutoDispatchService.markReadyCalledWith, testPrinterId)
    }

    func testMarkReadyFilamentChallengeDoesNotOptimisticallyReportReady() async {
        let jobId = UUID()
        let pendingStatus = AutoDispatchStatus(
            printerId: testPrinterId,
            enabled: true,
            queueDepth: 1,
            state: "PendingReady",
            dispatchStateETag: "dispatch-v1",
            nextJobId: jobId,
            nextJobName: "Standard print",
            nextJobETag: "job-v1",
            nextJobKind: "Standard"
        )
        let challenge = AutoDispatchReadyResult(
            status: pendingStatus,
            nextJob: AutoDispatchNextJob(
                id: jobId,
                name: "Standard print",
                estimatedFilamentUsageG: 100,
                requiredMaterialType: "PETG",
                estimatedPrintTime: "01:00:00",
                jobKind: "Standard",
                jobETag: "job-v1"
            ),
            filamentCheck: FilamentCheckResult(
                outcome: "Incompatible",
                sufficient: false,
                remainingWeightG: 500,
                requiredWeightG: 100,
                loadedMaterial: "PLA",
                requiredMaterial: "PETG",
                materialMismatch: true,
                message: "Material mismatch: loaded PLA, job requires PETG"
            ),
            requiresFilamentOverride: true,
            filamentCheckETag: "filament-v1"
        )
        mockAutoDispatchService.readyResultToReturn = challenge
        mockAutoDispatchService.statusToReturn = pendingStatus
        viewModel.status = pendingStatus

        await viewModel.markReady(printerId: testPrinterId)

        XCTAssertEqual(viewModel.status?.state, "PendingReady")
        XCTAssertEqual(
            viewModel.filamentChallenge?.filamentCheck?.message,
            "Material mismatch: loaded PLA, job requires PETG"
        )
        XCTAssertNil(viewModel.dispatchMessage)
    }

    func testConfirmFilamentOverrideUsesAuthoritativeReconciliationResult() async {
        let pendingStatus = AutoDispatchStatus(
            printerId: testPrinterId,
            enabled: true,
            queueDepth: 1,
            state: "PendingReady",
            dispatchStateETag: "dispatch-v1"
        )
        let challenge = AutoDispatchReadyResult(
            status: pendingStatus,
            nextJob: nil,
            filamentCheck: FilamentCheckResult(
                outcome: "Unknown",
                sufficient: false,
                remainingWeightG: nil,
                requiredWeightG: 100,
                loadedMaterial: nil,
                requiredMaterial: "PETG",
                materialMismatch: false,
                message: "No spool is assigned to the printer."
            ),
            requiresFilamentOverride: true,
            filamentCheckETag: "filament-v1"
        )
        mockAutoDispatchService.readyResultToReturn = challenge
        mockAutoDispatchService.statusToReturn = pendingStatus
        viewModel.status = pendingStatus
        await viewModel.markReady(printerId: testPrinterId)
        mockAutoDispatchService.readyResultToReturn = AutoDispatchReadyResult(
            status: pendingStatus,
            nextJob: nil,
            filamentCheck: challenge.filamentCheck,
            dispatchInitiated: true,
            filamentOverrideApplied: true,
            dispatchOutcome: "Unknown",
            dispatchReconciliationPending: true
        )

        await viewModel.confirmFilamentOverride(printerId: testPrinterId)

        XCTAssertTrue(mockAutoDispatchService.confirmFilamentOverrideCalled)
        XCTAssertNil(viewModel.filamentChallenge)
        XCTAssertEqual(viewModel.status?.state, "PendingReady")
        XCTAssertEqual(
            viewModel.dispatchMessage,
            "Dispatch submitted; awaiting printer reconciliation."
        )
    }

    func testMarkReadyHandlesError() async {
        viewModel.status = AutoDispatchStatus(
            printerId: testPrinterId,
            enabled: true,
            queueDepth: 1,
            state: "PendingReady"
        )
        mockAutoDispatchService.errorToThrow = TestError.generic

        await viewModel.markReady(printerId: testPrinterId)

        XCTAssertNil(viewModel.readyResult)
        XCTAssertNotNil(viewModel.error)
    }

    // MARK: - Skip Job

    func testSkipUpdatesStatus() async {
        let status = AutoDispatchStatus(
            printerId: testPrinterId,
            enabled: true,
            queueDepth: 1,
            state: "idle"
        )
        mockAutoDispatchService.statusToReturn = status
        viewModel.status = status

        await viewModel.skip(printerId: testPrinterId)

        XCTAssertNotNil(viewModel.status)
        XCTAssertEqual(viewModel.status?.queueDepth, 1)
        XCTAssertNil(viewModel.error)
        XCTAssertEqual(mockAutoDispatchService.skipCalledWith, testPrinterId)
    }

    func testSkipHandlesError() async {
        viewModel.status = AutoDispatchStatus(
            printerId: testPrinterId,
            enabled: true,
            queueDepth: 1,
            state: "PendingReady"
        )
        mockAutoDispatchService.errorToThrow = TestError.generic

        await viewModel.skip(printerId: testPrinterId)

        XCTAssertNotNil(viewModel.error)
    }

    func testStaleBedClearRefetchesAndRequiresReview() async {
        viewModel.status = AutoDispatchStatus(
            printerId: testPrinterId,
            enabled: true,
            queueDepth: 1,
            state: "PendingReady",
            dispatchStateETag: "dispatch-v1",
            nextJobETag: "job-v1"
        )
        mockAutoDispatchService.statusToReturn = AutoDispatchStatus(
            printerId: testPrinterId,
            enabled: true,
            queueDepth: 1,
            state: "PendingReady",
            dispatchStateETag: "dispatch-v2",
            nextJobETag: "job-v2"
        )
        mockAutoDispatchService.errorToThrow =
            BedClearAcknowledgementError.stale(
                code: "dispatch_revision_conflict",
                detail: "Review the refreshed job."
            )

        await viewModel.markReady(printerId: testPrinterId)

        XCTAssertEqual(mockAutoDispatchService.getStatusCalledWith, testPrinterId)
        XCTAssertEqual(viewModel.error, "Review the refreshed job.")
        XCTAssertFalse(viewModel.isMarkingReady)
    }

    // MARK: - Toggle Enabled

    func testToggleEnabledFromTrueToFalse() async {
        viewModel.status = AutoDispatchStatus(
            printerId: testPrinterId,
            enabled: true,
            queueDepth: 2,
            state: "ready"
        )

        let newStatus = AutoDispatchStatus(
            printerId: testPrinterId,
            enabled: false,
            queueDepth: 2,
            state: "idle"
        )
        mockAutoDispatchService.statusToReturn = newStatus

        await viewModel.toggleEnabled(printerId: testPrinterId)

        XCTAssertNotNil(mockAutoDispatchService.setEnabledCalledWith)
        XCTAssertEqual(mockAutoDispatchService.setEnabledCalledWith?.printerId, testPrinterId)
        XCTAssertEqual(mockAutoDispatchService.setEnabledCalledWith?.request.enabled, false)
        XCTAssertEqual(viewModel.status?.enabled, false)
        XCTAssertNil(viewModel.error)
    }

    func testToggleEnabledFromFalseToTrue() async {
        viewModel.status = AutoDispatchStatus(
            printerId: testPrinterId,
            enabled: false,
            queueDepth: 0,
            state: "idle"
        )

        let newStatus = AutoDispatchStatus(
            printerId: testPrinterId,
            enabled: true,
            queueDepth: 0,
            state: "idle"
        )
        mockAutoDispatchService.statusToReturn = newStatus

        await viewModel.toggleEnabled(printerId: testPrinterId)

        XCTAssertNotNil(mockAutoDispatchService.setEnabledCalledWith)
        XCTAssertEqual(mockAutoDispatchService.setEnabledCalledWith?.request.enabled, true)
        XCTAssertEqual(viewModel.status?.enabled, true)
        XCTAssertNil(viewModel.error)
    }

    func testToggleEnabledDoesNothingWhenStatusIsNil() async {
        viewModel.status = nil

        await viewModel.toggleEnabled(printerId: testPrinterId)

        XCTAssertNil(mockAutoDispatchService.setEnabledCalledWith)
        XCTAssertNil(viewModel.status)
    }

    func testToggleEnabledHandlesError() async {
        viewModel.status = AutoDispatchStatus(
            printerId: testPrinterId,
            enabled: true,
            queueDepth: 1,
            state: "ready"
        )
        mockAutoDispatchService.errorToThrow = TestError.generic

        await viewModel.toggleEnabled(printerId: testPrinterId)

        XCTAssertNotNil(viewModel.error)
        XCTAssertEqual(viewModel.status?.enabled, true)
    }

    // MARK: - Computed Properties

    func testIsEnabledReturnsCorrectValue() {
        viewModel.status = AutoDispatchStatus(
            printerId: testPrinterId,
            enabled: true,
            queueDepth: 2,
            state: "ready"
        )
        XCTAssertEqual(viewModel.isEnabled, true)

        viewModel.status = AutoDispatchStatus(
            printerId: testPrinterId,
            enabled: false,
            queueDepth: 0,
            state: "idle"
        )
        XCTAssertEqual(viewModel.isEnabled, false)

        viewModel.status = nil
        XCTAssertNil(viewModel.isEnabled)
    }

    func testCurrentStateReturnsCorrectValue() {
        viewModel.status = AutoDispatchStatus(
            printerId: testPrinterId,
            enabled: true,
            queueDepth: 1,
            state: "printing"
        )
        XCTAssertEqual(viewModel.currentState, "printing")

        viewModel.status = nil
        XCTAssertNil(viewModel.currentState)
    }

    // MARK: - Parsed State Tests

    func testParsedStateReturnsPendingReady() {
        viewModel.status = AutoDispatchStatus(
            printerId: testPrinterId,
            enabled: true,
            queueDepth: 1,
            state: "PendingReady"
        )

        XCTAssertEqual(viewModel.parsedState, .pendingReady)
    }

    func testParsedStateReturnsReady() {
        viewModel.status = AutoDispatchStatus(
            printerId: testPrinterId,
            enabled: true,
            queueDepth: 1,
            state: "Ready"
        )

        XCTAssertEqual(viewModel.parsedState, .ready)
    }

    func testParsedStateReturnsNone() {
        viewModel.status = AutoDispatchStatus(
            printerId: testPrinterId,
            enabled: false,
            queueDepth: 0,
            state: "None"
        )

        XCTAssertEqual(viewModel.parsedState, AutoDispatchState.none)
    }

    func testParsedStateReturnsNilWhenNoStatus() {
        viewModel.status = nil

        XCTAssertNil(viewModel.parsedState)
    }

    func testMarkReadyFromPendingReadyTransitionsToReady() async {
        // Set up status in PendingReady state
        viewModel.status = AutoDispatchStatus(
            printerId: testPrinterId,
            enabled: true,
            queueDepth: 1,
            state: "PendingReady"
        )

        // Configure mock to return Ready state after markReady
        let readyResult = AutoDispatchReadyResult(
            status: AutoDispatchStatus(
                printerId: testPrinterId,
                enabled: true,
                queueDepth: 1,
                state: "Ready"
            ),
            nextJob: AutoDispatchNextJob(
                id: UUID(),
                name: "test_job.gcode",
                estimatedFilamentUsageG: 25.0,
                requiredMaterialType: nil,
                estimatedPrintTime: "00:30:00"
            ),
            filamentCheck: FilamentCheckResult(
                sufficient: true,
                remainingWeightG: 500.0,
                requiredWeightG: 25.0,
                loadedMaterial: nil,
                requiredMaterial: nil,
                materialMismatch: false,
                message: nil
            )
        )
        mockAutoDispatchService.readyResultToReturn = readyResult
        mockAutoDispatchService.statusToReturn = readyResult.status

        // Call markReady
        await viewModel.markReady(printerId: testPrinterId)

        // Verify transition to Ready state
        XCTAssertEqual(viewModel.status?.state, "Ready")
        XCTAssertEqual(viewModel.parsedState, .ready)
        XCTAssertNotNil(viewModel.readyResult)
    }

    func testCurrentStateReturnsNilWhenStatusIsNil() {
        viewModel.status = nil

        XCTAssertNil(viewModel.currentState)
    }

    // MARK: - Unconfigured Guard

    func testLoadStatusDoesNothingWhenUnconfigured() async {
        viewModel = AutoDispatchViewModel()

        await viewModel.loadStatus(printerId: testPrinterId)

        XCTAssertNil(viewModel.status)
        XCTAssertNil(mockAutoDispatchService.getStatusCalledWith)
    }
}
