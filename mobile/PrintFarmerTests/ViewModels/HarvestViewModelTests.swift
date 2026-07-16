import XCTest
@testable import PrintFarmer

final class HarvestViewModelTests: XCTestCase {
    @MainActor
    func testLoadContextPrefillsOutputsFromGcodeMapping() async {
        let job = makeJob(gcodeFileId: gcodeFileId)
        let viewModel = HarvestViewModel(job: job)
        let service = MockPartsInventoryService()
        service.mappingsToReturn = [
            makeMapping(sku: "SKU-A", gcodeFileId: gcodeFileId, quantity: 2),
            makeMapping(sku: "SKU-B", printProjectFileId: UUID(), quantity: 1)
        ]
        service.partsToReturn = [makePart(sku: "SKU-A", name: "Bracket")]

        await viewModel.loadContext(partsInventoryService: service)

        XCTAssertEqual(viewModel.outputs.count, 1)
        XCTAssertEqual(viewModel.outputs.first?.sku, "SKU-A")
        XCTAssertEqual(viewModel.outputs.first?.quantity, 2)
        XCTAssertEqual(viewModel.outputs.first?.name, "Bracket")
        XCTAssertNil(viewModel.errorMessage)
    }

    @MainActor
    func testLoadContextPrefillsOutputsFromProjectFileMapping() async {
        let projectFileId = UUID()
        let job = makeJob(projectFileId: projectFileId)
        let viewModel = HarvestViewModel(job: job)
        let service = MockPartsInventoryService()
        service.mappingsToReturn = [
            makeMapping(sku: "SKU-C", printProjectFileId: projectFileId, quantity: 4)
        ]
        service.partsToReturn = []

        await viewModel.loadContext(partsInventoryService: service)

        XCTAssertEqual(viewModel.outputs.map(\.sku), ["SKU-C"])
        XCTAssertEqual(viewModel.outputs.first?.quantity, 4)
    }

    @MainActor
    func testLoadContextWithNoMatchingMappingsLeavesOutputsEmptyForServerFallback() async {
        let job = makeJob(gcodeFileId: UUID())
        let viewModel = HarvestViewModel(job: job)
        let service = MockPartsInventoryService()
        service.mappingsToReturn = [makeMapping(sku: "OTHER", gcodeFileId: UUID(), quantity: 1)]

        await viewModel.loadContext(partsInventoryService: service)

        XCTAssertTrue(viewModel.outputs.isEmpty)
    }

    @MainActor
    func testLoadContextFailureSurfacesErrorMessage() async {
        let job = makeJob()
        let viewModel = HarvestViewModel(job: job)
        let service = MockPartsInventoryService()
        service.mappingsError = NetworkError.serverError(500)

        await viewModel.loadContext(partsInventoryService: service)

        XCTAssertNotNil(viewModel.errorMessage)
        XCTAssertFalse(viewModel.isLoadingContext)
    }

    @MainActor
    func testSubmitWithEmptyOutputsSendsNilOutputsSoServerResolvesSnapshot() async {
        let job = makeJob()
        let viewModel = HarvestViewModel(job: job)
        let service = MockPartsInventoryService()
        service.harvestResponseToReturn = makeHarvestResponse(jobId: job.id)

        await viewModel.submit(partsInventoryService: service)

        XCTAssertEqual(service.harvestCalls.count, 1)
        XCTAssertNil(service.harvestCalls.first?.request.outputs)
        XCTAssertNotNil(viewModel.result)
    }

    @MainActor
    func testSubmitWithConfirmedOutputsSendsExplicitOutputsList() async {
        let job = makeJob()
        let viewModel = HarvestViewModel(job: job)
        viewModel.outputs = [HarvestOutputDraft(sku: "SKU-A", name: "Bracket", quantity: 3)]
        let service = MockPartsInventoryService()
        service.harvestResponseToReturn = makeHarvestResponse(jobId: job.id)

        await viewModel.submit(partsInventoryService: service)

        let sentOutputs = service.harvestCalls.first?.request.outputs
        XCTAssertEqual(sentOutputs?.count, 1)
        XCTAssertEqual(sentOutputs?.first?.sku, "SKU-A")
        XCTAssertEqual(sentOutputs?.first?.quantity, 3)
    }

    // MARK: - B1: unedited-mapping harvest must send nil outputs

    @MainActor
    func testSubmitWithUneditedResolvedMappingSendsNilOutputsAndNoOverrideReason() async {
        // The server (PartHarvestService.ResolveOutputsAsync) unconditionally
        // rejects any request with a non-empty `outputs` array unless
        // `overrideReason` is also non-blank — even when nothing was actually
        // overridden. A normal harvest of an auto-resolved, UNEDITED mapping
        // must therefore send `outputs: nil` so the server resolves it,
        // rather than tripping that validation for no reason.
        let job = makeJob(gcodeFileId: gcodeFileId)
        let viewModel = HarvestViewModel(job: job)
        let service = MockPartsInventoryService()
        service.mappingsToReturn = [makeMapping(sku: "SKU-A", gcodeFileId: gcodeFileId, quantity: 2)]
        service.partsToReturn = [makePart(sku: "SKU-A", name: "Bracket")]
        service.harvestResponseToReturn = makeHarvestResponse(jobId: job.id)

        await viewModel.loadContext(partsInventoryService: service)
        XCTAssertEqual(viewModel.outputs.count, 1, "sanity: mapping resolved a row")
        XCTAssertFalse(viewModel.hasManualOutputEdits, "unedited resolved mapping must not count as an edit")

        await viewModel.submit(partsInventoryService: service)

        XCTAssertEqual(service.harvestCalls.count, 1)
        XCTAssertNil(service.harvestCalls.first?.request.outputs, "unedited outputs must be sent as nil so the server resolves them")
        XCTAssertNil(service.harvestCalls.first?.request.overrideReason, "no override reason should be required when nothing was overridden")
        XCTAssertNotNil(viewModel.result)
    }

    @MainActor
    func testSubmitWithEditedQuantityOnResolvedMappingSendsExplicitOutputsAndRequiresReason() async {
        let job = makeJob(gcodeFileId: gcodeFileId)
        let viewModel = HarvestViewModel(job: job)
        let service = MockPartsInventoryService()
        service.mappingsToReturn = [makeMapping(sku: "SKU-A", gcodeFileId: gcodeFileId, quantity: 2)]
        service.partsToReturn = [makePart(sku: "SKU-A", name: "Bracket")]
        service.harvestResponseToReturn = makeHarvestResponse(jobId: job.id)

        await viewModel.loadContext(partsInventoryService: service)
        viewModel.outputs[0].quantity = 5 // operator edits the auto-resolved quantity

        XCTAssertTrue(viewModel.hasManualOutputEdits)
        XCTAssertFalse(viewModel.canSubmit, "editing without a reason must block submit")

        viewModel.overrideReason = "Recounted plate, five parts not two"
        XCTAssertTrue(viewModel.canSubmit)

        await viewModel.submit(partsInventoryService: service)

        let sent = service.harvestCalls.first?.request
        XCTAssertEqual(sent?.outputs?.first?.quantity, 5)
        XCTAssertEqual(sent?.overrideReason, "Recounted plate, five parts not two")
    }

    @MainActor
    func testManuallyAddedRowWithNoBaselineCountsAsEditAndRequiresReason() async {
        let job = makeJob()
        let viewModel = HarvestViewModel(job: job)
        let service = MockPartsInventoryService()
        service.partsToReturn = [makePart(sku: "SKU-Z", name: "Widget")]
        service.harvestResponseToReturn = makeHarvestResponse(jobId: job.id)

        await viewModel.loadContext(partsInventoryService: service)
        XCTAssertTrue(viewModel.outputs.isEmpty, "sanity: no mapping matched")

        viewModel.addManualOutputRow()
        XCTAssertTrue(viewModel.outputs.first?.isManuallyAdded ?? false)
        XCTAssertTrue(viewModel.hasManualOutputEdits)
        XCTAssertFalse(viewModel.canSubmit, "manual row requires a reason before submit")

        viewModel.overrideReason = "No mapping configured; adding SKU manually"
        await viewModel.submit(partsInventoryService: service)

        XCTAssertNotNil(service.harvestCalls.first?.request.outputs)
        XCTAssertEqual(service.harvestCalls.first?.request.overrideReason, "No mapping configured; adding SKU manually")
    }

    // MARK: - B2: exclusive project-first mapping precedence

    @MainActor
    func testLoadContextUsesProjectMappingExclusivelyWhenBothSourcesResolveDifferingSkuAndQuantity() async {
        // Matches the server's PartOutputMappingResolver.ResolveCurrentMappingsAsync:
        // project mappings are checked first and used EXCLUSIVELY when
        // present — gcode mappings are never unioned in, even if they'd also
        // match this job. Regression guard against the prior OR-union bug.
        let projectFileId = UUID()
        let job = makeJob(gcodeFileId: gcodeFileId, projectFileId: projectFileId)
        let viewModel = HarvestViewModel(job: job)
        let service = MockPartsInventoryService()
        service.mappingsToReturn = [
            makeMapping(sku: "SKU-GCODE", gcodeFileId: gcodeFileId, quantity: 9),
            makeMapping(sku: "SKU-PROJECT", printProjectFileId: projectFileId, quantity: 3)
        ]
        service.partsToReturn = []

        await viewModel.loadContext(partsInventoryService: service)

        XCTAssertEqual(viewModel.outputs.map(\.sku), ["SKU-PROJECT"], "project mapping must win exclusively, not union with gcode")
        XCTAssertEqual(viewModel.outputs.first?.quantity, 3)
    }

    @MainActor
    func testLoadContextFallsBackToGcodeMappingWhenNoProjectMappingExists() async {
        let projectFileId = UUID()
        let job = makeJob(gcodeFileId: gcodeFileId, projectFileId: projectFileId)
        let viewModel = HarvestViewModel(job: job)
        let service = MockPartsInventoryService()
        service.mappingsToReturn = [
            makeMapping(sku: "SKU-GCODE", gcodeFileId: gcodeFileId, quantity: 7)
        ]
        service.partsToReturn = []

        await viewModel.loadContext(partsInventoryService: service)

        XCTAssertEqual(viewModel.outputs.map(\.sku), ["SKU-GCODE"])
        XCTAssertEqual(viewModel.outputs.first?.quantity, 7)
    }

    // MARK: - H5: malformed wrongBin payload must not type as wrongBin

    @MainActor
    func testSubmitWithWrongBinCodeButEmptyMismatchesFallsBackToGenericConflict() async {
        let job = makeJob()
        let viewModel = HarvestViewModel(job: job)
        let service = MockPartsInventoryService()
        let malformed = PartsInventoryConflict(
            code: PartsInventoryConflict.wrongBinCode,
            title: "Wrong Bin",
            detail: "Scanned bin does not match expected bin.",
            mismatches: [], // malformed: coded wrongBin but no mismatch detail
            jobId: nil, projectFileId: nil, gcodeFileId: nil, guidance: nil
        )
        service.harvestError = NetworkError.partsInventoryConflict(malformed)

        await viewModel.submit(partsInventoryService: service)

        XCTAssertFalse(viewModel.hasWrongBinConflict, "empty mismatches must not present the override sheet")
        XCTAssertFalse(viewModel.hasMappingRequiredConflict)
        XCTAssertNotNil(viewModel.errorMessage, "must fall back to a generic error instead of a blind override")
    }

    @MainActor
    func testSubmitUsesJobIdAsIdempotencyKey() async {
        let job = makeJob()
        let viewModel = HarvestViewModel(job: job)
        let service = MockPartsInventoryService()
        service.harvestResponseToReturn = makeHarvestResponse(jobId: job.id)

        await viewModel.submit(partsInventoryService: service)

        XCTAssertEqual(service.harvestCalls.first?.request.operationKey, job.id.uuidString)
    }

    @MainActor
    func testSubmitWrongBinConflictSetsWrongBinConflictNotMappingRequired() async {
        let job = makeJob()
        let viewModel = HarvestViewModel(job: job)
        let service = MockPartsInventoryService()
        let conflict = PartsInventoryConflict(
            code: PartsInventoryConflict.wrongBinCode,
            title: "Wrong Bin",
            detail: "Scanned bin does not match expected bin.",
            mismatches: [WrongBinMismatch(partSku: "SKU-A", expectedBinCode: "BIN-1", scannedBinCode: "BIN-2")],
            jobId: nil, projectFileId: nil, gcodeFileId: nil, guidance: nil
        )
        service.harvestError = NetworkError.partsInventoryConflict(conflict)

        await viewModel.submit(partsInventoryService: service)

        XCTAssertTrue(viewModel.hasWrongBinConflict)
        XCTAssertFalse(viewModel.hasMappingRequiredConflict)
        XCTAssertEqual(viewModel.wrongBinConflict?.mismatches?.first?.scannedBinCode, "BIN-2")
        XCTAssertNil(viewModel.result)
    }

    @MainActor
    func testSubmitMappingRequiredConflictSetsMappingRequiredConflictNotWrongBin() async {
        let job = makeJob()
        let viewModel = HarvestViewModel(job: job)
        let service = MockPartsInventoryService()
        let conflict = PartsInventoryConflict(
            code: PartsInventoryConflict.partMappingRequiredCode,
            title: "Mapping Required",
            detail: "No output mapping exists for this job.",
            mismatches: nil, jobId: job.id, projectFileId: nil, gcodeFileId: nil,
            guidance: "Add SKU outputs manually or configure a mapping."
        )
        service.harvestError = NetworkError.partsInventoryConflict(conflict)

        await viewModel.submit(partsInventoryService: service)

        XCTAssertTrue(viewModel.hasMappingRequiredConflict)
        XCTAssertFalse(viewModel.hasWrongBinConflict)
        XCTAssertEqual(viewModel.mappingRequiredConflict?.guidance, "Add SKU outputs manually or configure a mapping.")
    }

    @MainActor
    func testConfirmWrongBinOverrideRequiresNonEmptyReason() async {
        let job = makeJob()
        let viewModel = HarvestViewModel(job: job)
        let service = MockPartsInventoryService()
        service.harvestResponseToReturn = makeHarvestResponse(jobId: job.id)
        viewModel.overrideReason = "   "

        await viewModel.confirmWrongBinOverride(partsInventoryService: service)

        XCTAssertTrue(service.harvestCalls.isEmpty)
        XCTAssertFalse(viewModel.canConfirmOverride)
    }

    @MainActor
    func testConfirmWrongBinOverrideResubmitsWithAllowWrongBinAndReason() async {
        let job = makeJob()
        let viewModel = HarvestViewModel(job: job)
        let service = MockPartsInventoryService()
        service.harvestResponseToReturn = makeHarvestResponse(jobId: job.id)
        viewModel.overrideReason = "Operator confirmed correct part, wrong bin label"

        await viewModel.confirmWrongBinOverride(partsInventoryService: service)

        XCTAssertEqual(service.harvestCalls.count, 1)
        XCTAssertEqual(service.harvestCalls.first?.request.allowWrongBin, true)
        XCTAssertEqual(service.harvestCalls.first?.request.overrideReason, "Operator confirmed correct part, wrong bin label")
        XCTAssertNotNil(viewModel.result)
    }

    @MainActor
    func testPresetBinCodeFromBinScanShortcutPrefillsSharedBinCode() {
        let job = makeJob()
        let viewModel = HarvestViewModel(job: job, presetBinCode: "BIN-42")

        XCTAssertEqual(viewModel.sharedBinCode, "BIN-42")
    }

    @MainActor
    func testPerOutputBinsOnlySentWhenUsePerOutputBinsEnabled() async {
        let job = makeJob()
        let viewModel = HarvestViewModel(job: job)
        viewModel.outputs = [HarvestOutputDraft(sku: "SKU-A", name: nil, quantity: 1, binCodeOverride: "BIN-9")]
        viewModel.usePerOutputBins = false
        let service = MockPartsInventoryService()
        service.harvestResponseToReturn = makeHarvestResponse(jobId: job.id)

        await viewModel.submit(partsInventoryService: service)

        XCTAssertNil(service.harvestCalls.first?.request.outputBins)
    }

    // MARK: - Fixtures

    private let gcodeFileId = UUID()

    private func makeJob(gcodeFileId: UUID? = nil, projectFileId: UUID? = nil) -> PrintJob {
        PrintJob(
            id: UUID(), status: .completed, priority: 1, queuePosition: 0,
            gcodeFileId: gcodeFileId, gcodeFileName: "part.gcode",
            assignedPrinterId: nil, assignedPrinterName: nil,
            createdAt: .now, updatedAt: .now, actualStartTime: nil, actualEndTime: nil,
            estimatedPrintTime: nil, actualPrintTime: nil,
            estimatedFilamentUsage: nil, actualFilamentUsage: nil,
            estimatedCost: nil, actualCost: nil, failureReason: nil,
            requiredNozzleDiameter: nil, requiredMaterialType: nil,
            spoolmanFilamentId: nil, filamentName: nil, filamentVendor: nil, filamentColor: nil,
            copies: 1, completedCopies: 1, remainingCopies: 0,
            projectFileId: projectFileId, thumbnailUrl: nil
        )
    }

    private func makeMapping(sku: String, gcodeFileId: UUID? = nil, printProjectFileId: UUID? = nil, quantity: Int) -> PartOutputMappingResponse {
        PartOutputMappingResponse(
            id: UUID(), partInventoryId: UUID(), sku: sku,
            gcodeFileId: gcodeFileId, printProjectFileId: printProjectFileId,
            quantity: quantity, createdAt: .now, updatedAt: .now
        )
    }

    private func makePart(sku: String, name: String) -> PartInventoryResponse {
        PartInventoryResponse(
            id: UUID(), sku: sku, name: name, description: nil, modelFileRef: nil,
            defaultBinId: nil, defaultBinCode: nil, defaultBinName: nil,
            onHand: 5, reorderPoint: 2, needsReorder: false, isActive: true,
            createdAt: .now, updatedAt: .now
        )
    }

    private func makeHarvestResponse(jobId: UUID) -> HarvestJobResponse {
        HarvestJobResponse(
            printJobId: jobId, harvestedAt: .now, binId: nil, binCode: "BIN-1",
            alreadyHarvested: false, adjustments: [], outputs: []
        )
    }
}
