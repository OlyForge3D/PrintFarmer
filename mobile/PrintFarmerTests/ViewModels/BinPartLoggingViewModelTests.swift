import XCTest
@testable import PrintFarmer

/// Tests for `BinPartLoggingViewModel` (#714 Blocker B remediation):
/// `BinScanResultView.logParts()` previously bypassed the H4 idempotency
/// pattern entirely (fresh key every call, async-only guard). These tests
/// prove the generalized guard/key behavior now matches
/// `PartAdjustmentViewModel`'s proven pattern, plus cancellation safety.
final class BinPartLoggingViewModelTests: XCTestCase {
    @MainActor
    func testBeginSubmitReturnsFalseWhileAlreadySubmitting() {
        let viewModel = BinPartLoggingViewModel(bin: makeBin())
        viewModel.selectedSku = "SKU-A"

        XCTAssertTrue(viewModel.beginSubmit(), "first call should be allowed to proceed")
        XCTAssertFalse(viewModel.beginSubmit(), "a second synchronous call before the first completes must be rejected")
    }

    @MainActor
    func testBeginSubmitReturnsFalseWhenSkuIsEmpty() {
        let viewModel = BinPartLoggingViewModel(bin: makeBin())
        viewModel.selectedSku = ""

        XCTAssertFalse(viewModel.beginSubmit())
    }

    @MainActor
    func testBeginSubmitReturnsFalseWhenQuantityIsZero() {
        let viewModel = BinPartLoggingViewModel(bin: makeBin())
        viewModel.selectedSku = "SKU-A"
        viewModel.quantity = 0

        XCTAssertFalse(viewModel.beginSubmit())
    }

    @MainActor
    func testDoubleInvocationOnlyAppliesOneAdjustment() async {
        // Simulates two rapid taps of "Log Parts to This Bin": the second
        // must be rejected by beginSubmit() before any Task/adjustPart call
        // is made.
        let viewModel = BinPartLoggingViewModel(bin: makeBin())
        viewModel.selectedSku = "SKU-A"
        viewModel.quantity = 5
        let service = MockPartsInventoryService()
        service.adjustmentToReturn = makeAdjustment(resultingBalance: 5)

        XCTAssertTrue(viewModel.beginSubmit())
        XCTAssertFalse(viewModel.beginSubmit(), "rapid second tap must be rejected synchronously")

        _ = await viewModel.submit(partsInventoryService: service)

        XCTAssertEqual(service.adjustPartCalls.count, 1, "only one adjustment must reach the server")
    }

    @MainActor
    func testSubmitUsesStableOperationKeyAcrossRetryOfSameIntent() async {
        let viewModel = BinPartLoggingViewModel(bin: makeBin())
        viewModel.selectedSku = "SKU-A"
        viewModel.quantity = 5
        let service = MockPartsInventoryService()
        service.adjustPartError = NetworkError.serverError(500)

        XCTAssertTrue(viewModel.beginSubmit())
        _ = await viewModel.submit(partsInventoryService: service)
        XCTAssertNotNil(viewModel.errorMessage, "first attempt should have failed")

        // Retry the same intent (SKU/quantity unchanged) — no
        // noteIntentChanged() call, simulating the operator tapping the
        // same "Log Parts to This Bin" button again after a transient
        // failure.
        service.adjustPartError = nil
        service.adjustmentToReturn = makeAdjustment(resultingBalance: 12)
        XCTAssertTrue(viewModel.beginSubmit())
        let result = await viewModel.submit(partsInventoryService: service)

        XCTAssertNotNil(result)
        XCTAssertEqual(service.adjustPartCalls.count, 2)
        let firstKey = service.adjustPartCalls[0].request.operationKey
        let secondKey = service.adjustPartCalls[1].request.operationKey
        XCTAssertNotNil(firstKey)
        XCTAssertEqual(firstKey, secondKey, "retrying the same intent must reuse the original idempotency key")
    }

    @MainActor
    func testNoteIntentChangedResetsOperationKeyForANewIntent() async {
        let viewModel = BinPartLoggingViewModel(bin: makeBin())
        viewModel.selectedSku = "SKU-A"
        viewModel.quantity = 5
        let service = MockPartsInventoryService()
        service.adjustPartError = NetworkError.serverError(500)

        XCTAssertTrue(viewModel.beginSubmit())
        _ = await viewModel.submit(partsInventoryService: service)

        // Operator changes their mind: different SKU/quantity is a
        // genuinely new intent.
        viewModel.noteIntentChanged()
        viewModel.selectedSku = "SKU-B"
        viewModel.quantity = 2
        service.adjustPartError = nil
        service.adjustmentToReturn = makeAdjustment(resultingBalance: 2)

        XCTAssertTrue(viewModel.beginSubmit())
        _ = await viewModel.submit(partsInventoryService: service)

        let firstKey = service.adjustPartCalls[0].request.operationKey
        let secondKey = service.adjustPartCalls[1].request.operationKey
        XCTAssertNotEqual(firstKey, secondKey, "a new intent must mint a fresh idempotency key")
    }

    @MainActor
    func testNoteIntentChangedIsNoOpWhileSubmitting() async {
        // Guards against a mid-flight .onChange firing from invalidating
        // the in-flight operation's own retry key.
        let viewModel = BinPartLoggingViewModel(bin: makeBin())
        viewModel.selectedSku = "SKU-A"
        viewModel.quantity = 3
        let service = MockPartsInventoryService()
        service.adjustmentToReturn = makeAdjustment(resultingBalance: 3)

        XCTAssertTrue(viewModel.beginSubmit())
        viewModel.noteIntentChanged() // must be ignored: isSubmitting == true
        _ = await viewModel.submit(partsInventoryService: service)

        XCTAssertEqual(service.adjustPartCalls.first?.request.delta, 3)
    }

    @MainActor
    func testSubmitSendsFixedBinCodeAndManualReason() async {
        let viewModel = BinPartLoggingViewModel(bin: makeBin(code: "BIN-42"))
        viewModel.selectedSku = "SKU-A"
        viewModel.quantity = 7
        let service = MockPartsInventoryService()
        service.adjustmentToReturn = makeAdjustment(resultingBalance: 7)

        XCTAssertTrue(viewModel.beginSubmit())
        _ = await viewModel.submit(partsInventoryService: service)

        let sent = service.adjustPartCalls.first?.request
        XCTAssertEqual(service.adjustPartCalls.first?.sku, "SKU-A")
        XCTAssertEqual(sent?.binCode, "BIN-42")
        XCTAssertEqual(sent?.reason, .manual)
        XCTAssertEqual(sent?.delta, 7)
    }

    @MainActor
    func testSuccessfulSubmitReturnsAdjustmentAndClearsSubmittingState() async {
        let viewModel = BinPartLoggingViewModel(bin: makeBin())
        viewModel.selectedSku = "SKU-A"
        viewModel.quantity = 4
        let service = MockPartsInventoryService()
        service.adjustmentToReturn = makeAdjustment(resultingBalance: 9)

        XCTAssertTrue(viewModel.beginSubmit())
        let result = await viewModel.submit(partsInventoryService: service)

        XCTAssertEqual(result?.resultingBalance, 9)
        XCTAssertFalse(viewModel.isSubmitting)
        XCTAssertNotNil(viewModel.successMessage)
    }

    @MainActor
    func testCancelledSubmitDoesNotWriteErrorMessage() async {
        // Simulates the sheet being dismissed (onDisappear cancels
        // activeTasks) while a submission is in flight. A cancelled task
        // must not write error state into a view that's already gone.
        let viewModel = BinPartLoggingViewModel(bin: makeBin())
        viewModel.selectedSku = "SKU-A"
        viewModel.quantity = 3
        let service = MockPartsInventoryService()
        service.adjustPartError = NetworkError.serverError(500)

        XCTAssertTrue(viewModel.beginSubmit())
        let task = Task {
            await viewModel.submit(partsInventoryService: service)
        }
        task.cancel()
        _ = await task.value

        XCTAssertNil(viewModel.errorMessage, "a cancelled submission must not surface an error to a dismissed view")
        XCTAssertFalse(viewModel.isSubmitting)
    }

    @MainActor
    func testCancelledSubmitDoesNotWriteSuccessMessage() async {
        let viewModel = BinPartLoggingViewModel(bin: makeBin())
        viewModel.selectedSku = "SKU-A"
        viewModel.quantity = 3
        let service = MockPartsInventoryService()
        service.adjustmentToReturn = makeAdjustment(resultingBalance: 3)

        XCTAssertTrue(viewModel.beginSubmit())
        let task = Task {
            await viewModel.submit(partsInventoryService: service)
        }
        task.cancel()
        _ = await task.value

        XCTAssertNil(viewModel.successMessage, "a cancelled submission must not write success state into a dismissed view")
        XCTAssertFalse(viewModel.isSubmitting)
    }

    // MARK: - Fixtures

    private func makeBin(code: String = "BIN-1") -> BinResponse {
        BinResponse(
            id: UUID(), code: code, name: "Shelf A1", location: "Aisle 1",
            notes: nil, isActive: true, createdAt: .now, updatedAt: .now
        )
    }

    private func makeAdjustment(resultingBalance: Int) -> PartAdjustmentResponse {
        PartAdjustmentResponse(
            id: UUID(), partInventoryId: UUID(), sku: "SKU-A", binId: nil, binCode: "BIN-1",
            delta: 1, resultingBalance: resultingBalance, reason: .manual,
            printJobId: nil, operationKey: nil, notes: nil, userId: nil, createdAt: .now
        )
    }
}
