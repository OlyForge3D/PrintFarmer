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
    func testAddingExtraManualOutputAlongsideResolvedBaselineIsInvalidAndBlocksSubmit() async {
        // Final remediation, Blocker 1: an extra SKU beyond a nonempty
        // resolved baseline must now be REJECTED (exact set equality, not a
        // one-directional subset check) — this test was previously an
        // acceptance case and is rewritten into a rejection per the
        // unanimous review requirement. Extra, partial, and delete-all sets
        // must all block Submit.
        let job = makeJob(gcodeFileId: gcodeFileId)
        let viewModel = HarvestViewModel(job: job)
        let service = MockPartsInventoryService()
        service.mappingsToReturn = [makeMapping(sku: "SKU-A", gcodeFileId: gcodeFileId, quantity: 2)]
        service.partsToReturn = [makePart(sku: "SKU-A", name: "Bracket"), makePart(sku: "SKU-Z", name: "Widget")]
        service.harvestResponseToReturn = makeHarvestResponse(jobId: job.id)

        await viewModel.loadContext(partsInventoryService: service)
        viewModel.outputs.append(HarvestOutputDraft(sku: "SKU-Z", name: "Widget", quantity: 1, isManuallyAdded: true))
        viewModel.overrideReason = "Also harvested an extra widget from the same plate"

        XCTAssertTrue(viewModel.hasInvalidOutputEdits, "an extra SKU beyond the resolved baseline must be invalid")
        XCTAssertFalse(viewModel.canSubmit, "Submit must be disabled when an extra SKU is added beyond the resolved baseline")

        await viewModel.submit(partsInventoryService: service)

        XCTAssertTrue(service.harvestCalls.isEmpty, "an extra-SKU edit must never reach the server")
        XCTAssertNil(viewModel.result)
    }

    @MainActor
    func testNoBaselineManualOutputSetIsValidAndSendsExplicitOutputs() async {
        // The exact-set-equality rule only applies when a nonempty resolved
        // baseline exists. A manually-built output set from scratch (no
        // mapping matched, e.g. `partMappingRequired` recovery) has no
        // baseline membership to preserve and remains a valid, submittable
        // edit as long as the SKU rows themselves are otherwise valid.
        let job = makeJob()
        let viewModel = HarvestViewModel(job: job)
        let service = MockPartsInventoryService()
        service.partsToReturn = [makePart(sku: "SKU-Z", name: "Widget")]
        service.harvestResponseToReturn = makeHarvestResponse(jobId: job.id)

        await viewModel.loadContext(partsInventoryService: service)
        XCTAssertTrue(viewModel.outputs.isEmpty, "sanity: no mapping matched, so there is no resolved baseline")

        viewModel.addManualOutputRow()
        viewModel.outputs[0].sku = "SKU-Z"
        viewModel.overrideReason = "No mapping configured; adding SKU manually"

        XCTAssertFalse(viewModel.hasInvalidOutputEdits, "a manual output set with no baseline is valid")
        XCTAssertTrue(viewModel.canSubmit)

        await viewModel.submit(partsInventoryService: service)

        XCTAssertEqual(service.harvestCalls.first?.request.outputs?.map(\.sku), ["SKU-Z"])
        XCTAssertNotNil(viewModel.result)
    }

    @MainActor
    func testSwappingResolvedOutputForADifferentSkuOfSameCountIsInvalidExactSetMismatch() async {
        // A same-size but different-membership swap (drop SKU-A, add
        // SKU-Z) must be rejected just as clearly as a strict superset or
        // subset — exact SET equality, not just a count/size check.
        let job = makeJob(gcodeFileId: gcodeFileId)
        let viewModel = HarvestViewModel(job: job)
        let service = MockPartsInventoryService()
        service.mappingsToReturn = [makeMapping(sku: "SKU-A", gcodeFileId: gcodeFileId, quantity: 2)]
        service.partsToReturn = [makePart(sku: "SKU-A", name: "Bracket"), makePart(sku: "SKU-Z", name: "Widget")]

        await viewModel.loadContext(partsInventoryService: service)
        viewModel.outputs[0].sku = "SKU-Z" // swap the resolved row's SKU entirely
        viewModel.overrideReason = "Swapped to a different SKU"

        XCTAssertTrue(viewModel.hasInvalidOutputEdits, "swapping the resolved SKU for a different one must be invalid")
        XCTAssertFalse(viewModel.canSubmit)
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

    // MARK: - Final remediation Blocker 2: multi-copy totals

    @MainActor
    func testLoadContextMultipliesMappingQuantityByJobCopiesForDisplayedTotal() async {
        // Server (PartHarvestService) applies `QuantityPerPrint * copies`
        // for an untouched/implicit resolution — the prefilled/displayed
        // total must match, or an unedited `outputs: nil` submission would
        // silently harvest a different quantity than what the operator saw.
        let job = makeJob(gcodeFileId: gcodeFileId, copies: 4)
        let viewModel = HarvestViewModel(job: job)
        let service = MockPartsInventoryService()
        service.mappingsToReturn = [makeMapping(sku: "SKU-A", gcodeFileId: gcodeFileId, quantity: 2)]
        service.partsToReturn = [makePart(sku: "SKU-A", name: "Bracket")]

        await viewModel.loadContext(partsInventoryService: service)

        XCTAssertEqual(viewModel.outputs.first?.quantity, 8, "2 per print × 4 copies = 8")
    }

    @MainActor
    func testSubmitWithUntouchedMultiCopyMappingStillSendsNilOutputs() async {
        // Untouched (unedited) outputs are still sent as `outputs: nil`
        // regardless of the copies multiplier — the server independently
        // recomputes `QuantityPerPrint * copies` when resolving a nil
        // request, so the client must never send an explicit total for an
        // unedited mapping (that would trip the overrideReason validation).
        let job = makeJob(gcodeFileId: gcodeFileId, copies: 3)
        let viewModel = HarvestViewModel(job: job)
        let service = MockPartsInventoryService()
        service.mappingsToReturn = [makeMapping(sku: "SKU-A", gcodeFileId: gcodeFileId, quantity: 2)]
        service.partsToReturn = [makePart(sku: "SKU-A", name: "Bracket")]
        service.harvestResponseToReturn = makeHarvestResponse(jobId: job.id)

        await viewModel.loadContext(partsInventoryService: service)
        XCTAssertEqual(viewModel.outputs.first?.quantity, 6, "sanity: prefilled total is 2 × 3 copies")
        XCTAssertFalse(viewModel.hasManualOutputEdits, "untouched multi-copy total must not itself count as an edit")

        await viewModel.submit(partsInventoryService: service)

        XCTAssertNil(service.harvestCalls.first?.request.outputs, "untouched multi-copy mapping must still send nil outputs")
        XCTAssertNil(service.harvestCalls.first?.request.overrideReason)
    }

    @MainActor
    func testSubmitWithEditedQuantityOnMultiCopyMappingSendsWholeJobEditedTotal() async {
        // When the operator edits the (already-multiplied) prefilled total,
        // the edited explicit total sent to the server must match what the
        // operator actually typed — a whole-job total, not a re-derived
        // per-print amount.
        let job = makeJob(gcodeFileId: gcodeFileId, copies: 3)
        let viewModel = HarvestViewModel(job: job)
        let service = MockPartsInventoryService()
        service.mappingsToReturn = [makeMapping(sku: "SKU-A", gcodeFileId: gcodeFileId, quantity: 2)]
        service.partsToReturn = [makePart(sku: "SKU-A", name: "Bracket")]
        service.harvestResponseToReturn = makeHarvestResponse(jobId: job.id)

        await viewModel.loadContext(partsInventoryService: service)
        XCTAssertEqual(viewModel.outputs.first?.quantity, 6, "sanity: prefilled total is 2 × 3 copies")

        viewModel.outputs[0].quantity = 5 // operator recounts: only 5 of the 6 expected were usable
        viewModel.overrideReason = "One part failed QC, only 5 usable of the expected 6"

        await viewModel.submit(partsInventoryService: service)

        XCTAssertEqual(service.harvestCalls.first?.request.outputs?.first?.quantity, 5, "edited total must be sent exactly as entered")
    }

    @MainActor
    func testMultiCopyQuantityMultiplicationDoesNotOverflowOrTrap() async {
        // `Int * Int` traps on overflow — an absurd copies/quantity
        // combination must clamp rather than crash the app. The (1...10000)
        // bound is still enforced elsewhere as an editing constraint; this
        // only guards the multiplication itself.
        let job = makeJob(gcodeFileId: gcodeFileId, copies: Int.max)
        let viewModel = HarvestViewModel(job: job)
        let service = MockPartsInventoryService()
        service.mappingsToReturn = [makeMapping(sku: "SKU-A", gcodeFileId: gcodeFileId, quantity: 2)]
        service.partsToReturn = [makePart(sku: "SKU-A", name: "Bracket")]

        await viewModel.loadContext(partsInventoryService: service)

        XCTAssertEqual(viewModel.outputs.first?.quantity, Int.max, "overflow must clamp to Int.max, never trap")
    }

    // MARK: - Final remediation Blocker 3: once-per-sheet submit/callback

    @MainActor
    func testBeginSubmitReturnsFalseOnRapidSecondCallBeforeFirstResolves() async {
        // Simulates a rapid double-tap: the first `beginSubmit()` call
        // succeeds and sets `isSubmitting`; a second call on the same
        // run-loop turn (before any network response) must be rejected so
        // only one submit `Task` is ever created.
        let job = makeJob()
        let viewModel = HarvestViewModel(job: job)

        XCTAssertTrue(viewModel.beginSubmit(), "first call must be allowed to proceed")
        XCTAssertFalse(viewModel.beginSubmit(), "a second call while the first is still in flight must be rejected")
    }

    @MainActor
    func testBeginSubmitRemainsBlockedAfterSuccessNoSecondPostEverPossible() async {
        // Once-per-sheet contract (superseding the prior per-response
        // interpretation): the guard is held — never released — after a
        // successful response, so a delayed duplicate submit attempt for
        // the same sheet instance can never fire a second POST.
        let job = makeJob()
        let viewModel = HarvestViewModel(job: job)
        let service = MockPartsInventoryService()
        service.harvestResponseToReturn = makeHarvestResponse(jobId: job.id)

        XCTAssertTrue(viewModel.beginSubmit())
        await viewModel.submit(partsInventoryService: service)

        XCTAssertNotNil(viewModel.result)
        XCTAssertEqual(service.harvestCalls.count, 1)
        XCTAssertFalse(viewModel.beginSubmit(), "no further submit may ever be started once a result exists")

        await viewModel.submit(partsInventoryService: service)
        XCTAssertEqual(service.harvestCalls.count, 1, "a second submit() call after success must never reach the server again")
    }

    @MainActor
    func testBeginSubmitIsAllowedAgainOnlyAfterAGenuineFailure() async {
        // The guard must release on failure (so the operator can retry) but
        // never on success — this test proves the failure-release half of
        // that contract.
        let job = makeJob()
        let viewModel = HarvestViewModel(job: job)
        let service = MockPartsInventoryService()
        service.harvestError = NetworkError.serverError(500)

        XCTAssertTrue(viewModel.beginSubmit())
        await viewModel.submit(partsInventoryService: service)

        XCTAssertNil(viewModel.result, "sanity: the attempt failed")
        XCTAssertTrue(viewModel.beginSubmit(), "the guard must release after a genuine failure so the operator can retry")
    }

    // MARK: - Replacement remediation Item A: barrier-controlled causal proof
    //
    // The tests above this section only exercise `beginSubmit()`'s guard
    // booleans and sequential `await submit()` calls — they never prove
    // the "exactly once per sheet" contract through a genuine concurrent
    // in-flight window. These tests hold the mock's harvest response
    // behind an `AsyncGate` (a real suspension point, not a sleep/yield
    // poll) so a rapid AND a delayed second submission attempt can be
    // driven while the first request is still outstanding, then assert
    // the actual production delivery path — `HarvestViewModel.submit()`
    // calling the real `MockPartsInventoryService.harvestJob()` and
    // invoking the real `onHarvested` closure — produced exactly one POST
    // and exactly one callback.

    @MainActor
    func testRapidAndDelayedDoubleSubmitWhileInFlightProduceExactlyOnePostAndOneCallback() async {
        let job = makeJob()
        let viewModel = HarvestViewModel(job: job)
        let service = MockPartsInventoryService()
        // alreadyHarvested=true exercises the stale-sheet-replay release
        // case specifically, per Dallas's "first successful 200 per
        // presented sheet, including alreadyHarvested replay" ruling.
        service.harvestResponseToReturn = makeHarvestResponse(jobId: job.id, alreadyHarvested: true)
        let gate = AsyncGate()
        service.harvestGate = { await gate.wait() }
        let callbacks = CallbackCounter()
        viewModel.onHarvested = { callbacks.count += 1 }

        // First tap: mirrors HarvestSheetView's exact call site —
        // `guard viewModel.beginSubmit() else { return }` then
        // `Task { await viewModel.submit(...) }`.
        XCTAssertTrue(viewModel.beginSubmit())
        async let firstSubmit: Void = viewModel.submit(partsInventoryService: service)

        // Deterministically wait until the first request is blocked at
        // the gate (a real suspension point inside `harvestJob()`), not a
        // fixed sleep.
        while await !gate.hasWaiters { await Task.yield() }
        XCTAssertEqual(service.harvestCalls.count, 1, "the first request must have reached the mock before release")

        // Rapid second submission attempt while the first is in flight —
        // the same-run-loop-turn tap the reviewers described.
        XCTAssertFalse(viewModel.beginSubmit(), "a rapid second tap while the first is in flight must be rejected")

        // Delayed second submission attempt — still while the first is
        // in flight, but after additional scheduler turns have elapsed.
        await Task.yield()
        await Task.yield()
        XCTAssertFalse(viewModel.beginSubmit(), "a delayed second tap while the first is still in flight must also be rejected")

        // Release the held response and let the first submission finish.
        await gate.open()
        await firstSubmit

        XCTAssertEqual(service.harvestCalls.count, 1, "exactly one POST must have reached the server")
        XCTAssertEqual(callbacks.count, 1, "exactly one callback must have fired")
        XCTAssertNotNil(viewModel.result)
        XCTAssertEqual(viewModel.result?.alreadyHarvested, true)
    }

    @MainActor
    func testPostSuccessAttemptsAddNoAdditionalPostOrCallback() async {
        let job = makeJob()
        let viewModel = HarvestViewModel(job: job)
        let service = MockPartsInventoryService()
        service.harvestResponseToReturn = makeHarvestResponse(jobId: job.id)
        let callbacks = CallbackCounter()
        viewModel.onHarvested = { callbacks.count += 1 }

        XCTAssertTrue(viewModel.beginSubmit())
        await viewModel.submit(partsInventoryService: service)

        XCTAssertEqual(service.harvestCalls.count, 1)
        XCTAssertEqual(callbacks.count, 1)

        // A further tap attempt after success must never even create a Task.
        XCTAssertFalse(viewModel.beginSubmit(), "no further submit may ever start once a result exists")

        // A delayed duplicate `submit()` call bypassing `beginSubmit()`
        // entirely (e.g. a stale Task from an earlier tap that hadn't yet
        // observed `result != nil`) must also add nothing.
        await viewModel.submit(partsInventoryService: service)

        XCTAssertEqual(service.harvestCalls.count, 1, "a post-success attempt must add no POST")
        XCTAssertEqual(callbacks.count, 1, "a post-success attempt must add no callback")
    }

    @MainActor
    func testFailedFirstAttemptReleasesGuardAndPermitsExactlyOneRetryAndCallback() async {
        let job = makeJob()
        let viewModel = HarvestViewModel(job: job)
        let service = MockPartsInventoryService()
        service.harvestError = NetworkError.serverError(500)
        let callbacks = CallbackCounter()
        viewModel.onHarvested = { callbacks.count += 1 }

        XCTAssertTrue(viewModel.beginSubmit())
        await viewModel.submit(partsInventoryService: service)

        XCTAssertNil(viewModel.result, "sanity: the first attempt failed")
        XCTAssertEqual(service.harvestCalls.count, 1)
        XCTAssertEqual(callbacks.count, 0, "a failed attempt must never invoke the callback")

        // The guard released on failure, permitting exactly one retry.
        service.harvestError = nil
        service.harvestResponseToReturn = makeHarvestResponse(jobId: job.id)
        XCTAssertTrue(viewModel.beginSubmit(), "the guard must release after a genuine failure so the operator can retry")
        await viewModel.submit(partsInventoryService: service)

        XCTAssertEqual(service.harvestCalls.count, 2, "the retry is the second POST, following the failed first")
        XCTAssertEqual(callbacks.count, 1, "the retry's success must invoke the callback exactly once")
        XCTAssertNotNil(viewModel.result)

        // A further attempt after the retry succeeded must add nothing.
        XCTAssertFalse(viewModel.beginSubmit())
        await viewModel.submit(partsInventoryService: service)
        XCTAssertEqual(service.harvestCalls.count, 2)
        XCTAssertEqual(callbacks.count, 1)
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

    private func makeJob(gcodeFileId: UUID? = nil, projectFileId: UUID? = nil, copies: Int = 1) -> PrintJob {
        PrintJob(
            id: UUID(), rowVersion: nil, dispatchStateRowVersion: nil,
            status: .completed, priority: .normal, queuePosition: 0,
            gcodeFileId: gcodeFileId, gcodeFileName: "part.gcode",
            assignedPrinterId: nil, assignedPrinterName: nil,
            createdAt: .now, updatedAt: .now, actualStartTime: nil, actualEndTime: nil,
            estimatedPrintTime: nil, actualPrintTime: nil,
            estimatedFilamentUsage: nil, actualFilamentUsage: nil,
            estimatedCost: nil, actualCost: nil, failureReason: nil,
            requiredNozzleDiameter: nil, requiredMaterialType: nil,
            spoolmanFilamentId: nil, filamentName: nil, filamentVendor: nil, filamentColor: nil,
            copies: copies, completedCopies: copies, remainingCopies: 0,
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

    private func makeHarvestResponse(jobId: UUID, alreadyHarvested: Bool = false) -> HarvestJobResponse {
        HarvestJobResponse(
            printJobId: jobId, harvestedAt: .now, binId: nil, binCode: "BIN-1",
            alreadyHarvested: alreadyHarvested, adjustments: [], outputs: []
        )
    }

    private func makeBin(code: String, name: String) -> BinResponse {
        BinResponse(
            id: UUID(), code: code, name: name, location: nil, notes: nil,
            isActive: true, createdAt: .now, updatedAt: .now
        )
    }
}

// MARK: - Blocker A test-gate helpers

/// Deterministic suspension-point gate for barrier-controlled concurrency
/// tests — mirrors the identical helper already established in
/// `PrinterControlsViewModelTests.swift`. Callers `await wait()` inside the
/// code under test and the test `await open()`s it once it has observed
/// `hasWaiters` via a real-state busy-poll (`while await !gate.hasWaiters {
/// await Task.yield() }`), never a fixed sleep.
private actor AsyncGate {
    private var waiters: [CheckedContinuation<Void, Never>] = []
    private var opened = false

    var hasWaiters: Bool { !waiters.isEmpty || opened }

    func wait() async {
        if opened { return }
        await withCheckedContinuation { c in waiters.append(c) }
    }

    func open() {
        opened = true
        let toResume = waiters
        waiters.removeAll()
        for c in toResume { c.resume() }
    }
}

/// `@MainActor`-isolated counter for asserting exactly-once callback
/// delivery from `HarvestViewModel.onHarvested`, which is always invoked
/// on the main actor (from within `submit()`).
@MainActor
private final class CallbackCounter {
    var count = 0
}
