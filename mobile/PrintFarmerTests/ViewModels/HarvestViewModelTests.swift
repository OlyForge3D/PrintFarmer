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
        viewModel.availableParts = [makePart(sku: "SKU-A", name: "Bracket")]
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

    // MARK: - Dispute A: invalid edited output states must disable Submit
    // and never fall back to `outputs: nil` or a partial baseline.
    // (Untouched -> outputs:nil is already covered by
    // testSubmitWithUneditedResolvedMappingSendsNilOutputsAndNoOverrideReason
    // above, and is re-verified not to have regressed by every test below
    // that asserts a non-nil, non-empty `outputs` payload.)

    @MainActor
    func testDeletingAllResolvedOutputsDisablesSubmitAndNeverSendsNilFallback() async {
        let job = makeJob(gcodeFileId: gcodeFileId)
        let viewModel = HarvestViewModel(job: job)
        let service = MockPartsInventoryService()
        service.mappingsToReturn = [makeMapping(sku: "SKU-A", gcodeFileId: gcodeFileId, quantity: 2)]
        service.partsToReturn = [makePart(sku: "SKU-A", name: "Bracket")]
        service.harvestResponseToReturn = makeHarvestResponse(jobId: job.id)

        await viewModel.loadContext(partsInventoryService: service)
        XCTAssertEqual(viewModel.outputs.count, 1, "sanity: mapping resolved a row")

        viewModel.outputs.removeAll() // operator swipes away every resolved row
        viewModel.overrideReason = "Plate actually produced nothing usable"

        XCTAssertTrue(viewModel.hasManualOutputEdits, "sanity: deleting a resolved row counts as an edit")
        XCTAssertTrue(viewModel.hasInvalidOutputEdits, "deleting all resolved outputs must be an invalid edit state")
        XCTAssertFalse(viewModel.canSubmit, "Submit must be disabled when all resolved outputs are deleted")

        await viewModel.submit(partsInventoryService: service)

        XCTAssertTrue(service.harvestCalls.isEmpty, "delete-all must never reach the server as a nil-fallback or any other request")
        XCTAssertNil(viewModel.result)
    }

    @MainActor
    func testDeletingOnlySomeResolvedOutputsLeavesIncompleteBaselineAndDisablesSubmit() async {
        let job = makeJob(gcodeFileId: gcodeFileId)
        let viewModel = HarvestViewModel(job: job)
        let service = MockPartsInventoryService()
        service.mappingsToReturn = [
            makeMapping(sku: "SKU-A", gcodeFileId: gcodeFileId, quantity: 2),
            makeMapping(sku: "SKU-B", gcodeFileId: gcodeFileId, quantity: 1)
        ]
        service.partsToReturn = [makePart(sku: "SKU-A", name: "Bracket"), makePart(sku: "SKU-B", name: "Clip")]
        service.harvestResponseToReturn = makeHarvestResponse(jobId: job.id)

        await viewModel.loadContext(partsInventoryService: service)
        XCTAssertEqual(viewModel.outputs.count, 2, "sanity: both mappings resolved")

        viewModel.outputs.removeAll { $0.sku == "SKU-B" } // partial delete: SKU-A survives, SKU-B doesn't
        viewModel.overrideReason = "Only recovered one of the two mapped SKUs"

        XCTAssertTrue(viewModel.hasInvalidOutputEdits, "dropping a subset of the resolved baseline must be invalid, not just delete-all")
        XCTAssertFalse(viewModel.canSubmit)

        await viewModel.submit(partsInventoryService: service)

        XCTAssertTrue(service.harvestCalls.isEmpty, "partial baseline membership must never reach the server")
    }

    @MainActor
    func testAddingExtraManualOutputAlongsideResolvedBaselineIsValidAndSendsAllOutputs() async {
        let job = makeJob(gcodeFileId: gcodeFileId)
        let viewModel = HarvestViewModel(job: job)
        let service = MockPartsInventoryService()
        service.mappingsToReturn = [makeMapping(sku: "SKU-A", gcodeFileId: gcodeFileId, quantity: 2)]
        service.partsToReturn = [makePart(sku: "SKU-A", name: "Bracket"), makePart(sku: "SKU-Z", name: "Widget")]
        service.harvestResponseToReturn = makeHarvestResponse(jobId: job.id)

        await viewModel.loadContext(partsInventoryService: service)
        viewModel.outputs.append(HarvestOutputDraft(sku: "SKU-Z", name: "Widget", quantity: 1, isManuallyAdded: true))
        viewModel.overrideReason = "Also harvested an extra widget from the same plate"

        XCTAssertFalse(viewModel.hasInvalidOutputEdits, "an extra row alongside a complete baseline is a valid edit")
        XCTAssertTrue(viewModel.canSubmit)

        await viewModel.submit(partsInventoryService: service)

        let sentSkus = Set(service.harvestCalls.first?.request.outputs?.map(\.sku) ?? [])
        XCTAssertEqual(sentSkus, ["SKU-A", "SKU-Z"], "the resolved baseline SKU and the extra manual SKU must both be sent")
    }

    @MainActor
    func testDuplicateNormalizedSkusAcrossOutputsDisablesSubmit() async {
        let job = makeJob()
        let viewModel = HarvestViewModel(job: job)
        let service = MockPartsInventoryService()
        service.partsToReturn = [makePart(sku: "SKU-A", name: "Bracket")]
        service.harvestResponseToReturn = makeHarvestResponse(jobId: job.id)

        await viewModel.loadContext(partsInventoryService: service)
        // Same identity once server-equivalent normalized (NFKC fold, trim,
        // uppercase) — differs only by case and incidental whitespace.
        viewModel.outputs = [
            HarvestOutputDraft(sku: "SKU-A", name: "Bracket", quantity: 1, isManuallyAdded: true),
            HarvestOutputDraft(sku: " sku-a ", name: "Bracket", quantity: 2, isManuallyAdded: true)
        ]
        viewModel.overrideReason = "Duplicate attempt"

        XCTAssertTrue(viewModel.hasInvalidOutputEdits, "normalized-duplicate SKUs must be invalid")
        XCTAssertFalse(viewModel.canSubmit)

        await viewModel.submit(partsInventoryService: service)

        XCTAssertTrue(service.harvestCalls.isEmpty)
    }

    @MainActor
    func testBlankSkuOutputDisablesSubmit() async {
        let job = makeJob()
        let viewModel = HarvestViewModel(job: job)
        let service = MockPartsInventoryService()
        service.harvestResponseToReturn = makeHarvestResponse(jobId: job.id)

        await viewModel.loadContext(partsInventoryService: service)
        viewModel.outputs = [HarvestOutputDraft(sku: "   ", name: nil, quantity: 1, isManuallyAdded: true)]
        viewModel.overrideReason = "Blank SKU"

        XCTAssertTrue(viewModel.hasInvalidOutputEdits, "a blank SKU must be invalid")
        XCTAssertFalse(viewModel.canSubmit)

        await viewModel.submit(partsInventoryService: service)

        XCTAssertTrue(service.harvestCalls.isEmpty)
    }

    @MainActor
    func testUnknownOrInactiveSkuOutputDisablesSubmit() async {
        let job = makeJob()
        let viewModel = HarvestViewModel(job: job)
        let service = MockPartsInventoryService()
        service.partsToReturn = [] // no known/active parts loaded
        service.harvestResponseToReturn = makeHarvestResponse(jobId: job.id)

        await viewModel.loadContext(partsInventoryService: service)
        viewModel.outputs = [HarvestOutputDraft(sku: "SKU-GHOST", name: nil, quantity: 1, isManuallyAdded: true)]
        viewModel.overrideReason = "Not a real part"

        XCTAssertTrue(viewModel.hasInvalidOutputEdits, "a SKU with no known active part must be invalid")
        XCTAssertFalse(viewModel.canSubmit)
    }

    @MainActor
    func testQuantityOutOfRangeDisablesSubmitAtBothBounds() async {
        let job = makeJob()
        let service = MockPartsInventoryService()
        service.partsToReturn = [makePart(sku: "SKU-A", name: "Bracket")]
        service.harvestResponseToReturn = makeHarvestResponse(jobId: job.id)

        let tooLow = HarvestViewModel(job: job)
        await tooLow.loadContext(partsInventoryService: service)
        tooLow.outputs = [HarvestOutputDraft(sku: "SKU-A", name: "Bracket", quantity: 0, isManuallyAdded: true)]
        tooLow.overrideReason = "Zero quantity"
        XCTAssertTrue(tooLow.hasInvalidOutputEdits, "quantity below 1 must be invalid")

        let tooHigh = HarvestViewModel(job: job)
        await tooHigh.loadContext(partsInventoryService: service)
        tooHigh.outputs = [HarvestOutputDraft(sku: "SKU-A", name: "Bracket", quantity: 10001, isManuallyAdded: true)]
        tooHigh.overrideReason = "Over the server's Range(1, 10000)"
        XCTAssertTrue(tooHigh.hasInvalidOutputEdits, "quantity above 10000 must be invalid")

        let atBounds = HarvestViewModel(job: job)
        await atBounds.loadContext(partsInventoryService: service)
        atBounds.outputs = [HarvestOutputDraft(sku: "SKU-A", name: "Bracket", quantity: 1, isManuallyAdded: true)]
        atBounds.overrideReason = "Minimum valid quantity"
        XCTAssertFalse(atBounds.hasInvalidOutputEdits, "quantity of exactly 1 must be valid")

        atBounds.outputs[0].quantity = 10000
        XCTAssertFalse(atBounds.hasInvalidOutputEdits, "quantity of exactly 10000 must be valid")
    }

    @MainActor
    func testOverrideReasonBoundsRequireNonBlankAndAtMost1000Characters() async {
        let job = makeJob(gcodeFileId: gcodeFileId)
        let viewModel = HarvestViewModel(job: job)
        let service = MockPartsInventoryService()
        service.mappingsToReturn = [makeMapping(sku: "SKU-A", gcodeFileId: gcodeFileId, quantity: 2)]
        service.partsToReturn = [makePart(sku: "SKU-A", name: "Bracket")]
        service.harvestResponseToReturn = makeHarvestResponse(jobId: job.id)

        await viewModel.loadContext(partsInventoryService: service)
        viewModel.outputs[0].quantity = 5 // valid edit, needs a reason

        viewModel.overrideReason = "   "
        XCTAssertFalse(viewModel.canSubmit, "a blank/whitespace-only reason must not satisfy the requirement")

        viewModel.overrideReason = String(repeating: "a", count: 1001)
        XCTAssertFalse(viewModel.canSubmit, "a reason over 1000 characters must be rejected client-side, matching the server's bound")

        viewModel.overrideReason = String(repeating: "a", count: 1000)
        XCTAssertTrue(viewModel.canSubmit, "a reason of exactly 1000 characters must be accepted")
    }

    @MainActor
    func testCompletePerOutputBinsRequiredWhenUsePerOutputBinsEnabled() async {
        let job = makeJob(gcodeFileId: gcodeFileId)
        let viewModel = HarvestViewModel(job: job)
        let service = MockPartsInventoryService()
        service.mappingsToReturn = [
            makeMapping(sku: "SKU-A", gcodeFileId: gcodeFileId, quantity: 2),
            makeMapping(sku: "SKU-B", gcodeFileId: gcodeFileId, quantity: 1)
        ]
        service.partsToReturn = [makePart(sku: "SKU-A", name: "Bracket"), makePart(sku: "SKU-B", name: "Clip")]
        service.binsToReturn = [makeBin(code: "BIN-1", name: "Shelf 1"), makeBin(code: "BIN-2", name: "Shelf 2")]
        service.harvestResponseToReturn = makeHarvestResponse(jobId: job.id)

        await viewModel.loadContext(partsInventoryService: service)
        viewModel.usePerOutputBins = true
        viewModel.outputs[0].binCodeOverride = "BIN-1"
        // SKU-B's bin is left blank — must never be silently compacted away.
        viewModel.overrideReason = "n/a" // no manual sku/quantity edits, but per-output bins still gate submit

        XCTAssertTrue(viewModel.hasInvalidOutputEdits, "a blank per-output bin must block submit, not be dropped")

        viewModel.outputs[1].binCodeOverride = "BIN-UNREGISTERED"
        XCTAssertTrue(viewModel.hasInvalidOutputEdits, "an unregistered bin code must also block submit")

        viewModel.outputs[1].binCodeOverride = "BIN-2"
        XCTAssertFalse(viewModel.hasInvalidOutputEdits, "a nonblank, registered, active bin per SKU must be valid")

        await viewModel.submit(partsInventoryService: service)

        let sentBins = service.harvestCalls.first?.request.outputBins
        XCTAssertEqual(sentBins?.count, 2, "every output row must be sent — none silently compacted away")
        XCTAssertEqual(Set(sentBins?.map(\.binCode) ?? []), ["BIN-1", "BIN-2"])
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
        viewModel.availableParts = [makePart(sku: "SKU-A", name: "Bracket")]
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

    private func makeBin(code: String, name: String) -> BinResponse {
        BinResponse(
            id: UUID(), code: code, name: name, location: nil, notes: nil,
            isActive: true, createdAt: .now, updatedAt: .now
        )
    }
}
