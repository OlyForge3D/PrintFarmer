import XCTest
@testable import PrintFarmer

final class PrinterFilamentPresentationTests: XCTestCase {
    private func snapshot(
        printer: Printer,
        status: FilamentCoverageStatus = .covers,
        slots: [ToolheadFilamentCoverage] = []
    ) -> PrinterFilamentCoverage {
        PrinterFilamentCoverage(
            printerId: printer.id, printerName: printer.name, status: status,
            toolheads: slots, activeJobId: nil, activeJobName: nil,
            activeJobProgress: nil, earliestPredictedRunoutAt: nil,
            assignedQueuedJobCount: 2, evaluatedAtUtc: Date(timeIntervalSince1970: 1_700_000_000)
        )
    }

    private func build(
        printer: Printer, roster: [Toolhead] = [], spool: PrinterSpoolInfo? = nil,
        coverage: PrinterFilamentCoverage? = nil,
        state: PrinterFilamentPresentation.CoverageState = .available, stale: Bool = false,
        reportIntegrityFailure: (String) -> Void = { _ in }
    ) throws -> PrinterFilamentPresentation {
        PrinterFilamentPresentation(
            printer: printer, toolheads: roster, spool: spool, coverage: coverage,
            coverageState: state, isStale: stale,
            supportedActions: Set(PrinterFilamentAction.Kind.allCases),
            reportIntegrityFailure: reportIntegrityFailure
        )
    }

    func testCoversIncludesAssignedQueueAndHealthyRowsHaveNoSuccessNotice() throws {
        let printer = try TestData.decodePrinter()
        let slot = ToolheadFilamentCoverage(
            toolheadIndex: 0, toolheadName: "Tool", remainingGrams: 900,
            currentJobRemainingGrams: 100, queuedRequiredGrams: 200, totalDemandGrams: 300,
            status: .covers
        )
        let model = try build(printer: printer, coverage: snapshot(printer: printer, slots: [slot]))
        XCTAssertEqual(model.summary, "Covers active and assigned queued demand")
        XCTAssertNil(model.rows[0].notice)
        XCTAssertEqual(model.rows[0].coverage?.remainingGrams, 900)
        XCTAssertEqual(model.rows[0].coverage?.currentJobRemainingGrams, 100)
        XCTAssertEqual(model.rows[0].coverage?.queuedRequiredGrams, 200)
        XCTAssertEqual(model.rows[0].coverage?.totalDemandGrams, 300)
    }

    func testUnknownAndPartialFieldsNeverInferRunoutOrZero() throws {
        let printer = try TestData.decodePrinter()
        let slot = ToolheadFilamentCoverage(
            toolheadIndex: 0, toolheadName: "Tool", remainingGrams: 0,
            totalDemandGrams: 300, status: .unknown, statusReason: "Missing metadata",
            predictedRunoutAt: Date()
        )
        let model = try build(printer: printer, coverage: snapshot(printer: printer, status: .unknown, slots: [slot]))
        XCTAssertEqual(model.summary, "Coverage unknown")
        XCTAssertEqual(model.rows[0].notice, "Coverage unknown: Missing metadata")
        XCTAssertEqual(model.rows[0].coverage?.remainingGrams, 0)
        XCTAssertNil(model.rows[0].coverage?.queuedRequiredGrams)
        XCTAssertNil(model.rows[0].coverage?.currentJobRemainingGrams)
    }

    func testRunoutWithoutETAIsRetained() throws {
        let printer = try TestData.decodePrinter()
        let slot = ToolheadFilamentCoverage(toolheadIndex: 0, toolheadName: "Tool", status: .runout)
        let model = try build(printer: printer, coverage: snapshot(printer: printer, status: .runout, slots: [slot]))
        XCTAssertEqual(model.rows[0].notice, "Insufficient filament for active and assigned queued demand")
        XCTAssertNil(model.rows[0].coverage?.predictedRunoutAt)
    }

    func testStaleAndRetainedErrorSnapshotsAreLastConfirmedAndBlockActions() throws {
        let printer = try TestData.decodePrinter()
        for state in [PrinterFilamentPresentation.CoverageState.available, .loading, .failed("Offline")] {
            let model = try build(
                printer: printer, coverage: snapshot(printer: printer), state: state, stale: state == .available
            )
            XCTAssertEqual(model.summary, "Last confirmed: Covers active and assigned queued demand")
            XCTAssertTrue(model.isStale)
            XCTAssertTrue(model.supportedActions.isEmpty)
            XCTAssertNotNil(model.disabledReason(for: .init(kind: .set, target: .printer(printer.id), disabledReason: nil)))
            XCTAssertNotNil(model.evaluatedAt)
        }
    }

    func testMissingOrDisabledCoveragePreservesMaterialAndDoesNotInventStaleness() throws {
        let printer = try TestData.decodePrinter()
        let tool = Toolhead(id: UUID(), name: "Tool", index: 0, isPrimary: true, currentMaterial: "PETG")
        for state in [PrinterFilamentPresentation.CoverageState.loading, .unavailable, .disabled, .failed("Offline")] {
            let model = try build(printer: printer, roster: [tool], state: state)
            XCTAssertEqual(model.rows[0].material, "PETG")
            XCTAssertNil(model.rows[0].remainingGrams)
            XCTAssertNil(model.summary)
            XCTAssertNotNil(model.statusText)
            XCTAssertFalse(model.isStale)
        }
        let disabled = try build(printer: printer, roster: [tool], coverage: snapshot(printer: printer), state: .disabled)
        XCTAssertNil(disabled.summary)
        XCTAssertNil(disabled.evaluatedAt)
    }

    func testUUIDJoinsIgnoreRepeatedNamesAndCollidingIndices() throws {
        let printer = try TestData.decodePrinter()
        let first = Toolhead(id: UUID(), name: "Same", index: 0, isPrimary: true)
        let second = Toolhead(id: UUID(), name: "Same", index: 0, isPrimary: false)
        let slots = [
            ToolheadFilamentCoverage(toolheadIndex: 0, toolheadId: second.id, toolheadName: "Same", remainingGrams: 20, status: .runout),
            ToolheadFilamentCoverage(toolheadIndex: 0, toolheadId: first.id, toolheadName: "Same", remainingGrams: 80, status: .covers)
        ]
        let model = try build(printer: printer, roster: [first, second], coverage: snapshot(printer: printer, slots: slots))
        XCTAssertEqual(model.rows.count, 2)
        XCTAssertEqual(Set(model.rows.map(\.id)).count, 2)
        XCTAssertEqual(model.rows[0].coverage?.remainingGrams, 80)
        XCTAssertEqual(model.rows[1].coverage?.remainingGrams, 20)
        let reordered = try build(printer: printer, roster: [second, first], coverage: snapshot(printer: printer, slots: slots.reversed()))
        XCTAssertEqual(Set(model.rows.map(\.id)), Set(reordered.rows.map(\.id)))
    }

    func testIndexFallbackOnlyWhenBothSidesUnambiguousAndIDAbsent() throws {
        let printer = try TestData.decodePrinter()
        let first = Toolhead(id: UUID(), name: "Same", index: 0, isPrimary: true)
        let second = Toolhead(id: UUID(), name: "Same", index: 0, isPrimary: false)
        let slot = ToolheadFilamentCoverage(toolheadIndex: 0, toolheadName: "Same", status: .unknown)
        let unique = try build(printer: printer, roster: [first], coverage: snapshot(printer: printer, slots: [slot]))
        XCTAssertEqual(unique.rows.count, 1)
        XCTAssertNotNil(unique.rows[0].coverage)
        let ambiguous = try build(printer: printer, roster: [first, second], coverage: snapshot(printer: printer, slots: [slot]))
        XCTAssertEqual(ambiguous.rows.count, 3)
        XCTAssertNil(ambiguous.rows[0].coverage)
        XCTAssertNil(ambiguous.rows[1].coverage)
        let distinctID = ToolheadFilamentCoverage(toolheadIndex: 0, toolheadId: UUID(), toolheadName: "Same", status: .unknown)
        let distinct = try build(printer: printer, roster: [first], coverage: snapshot(printer: printer, slots: [distinctID]))
        XCTAssertEqual(distinct.rows.count, 2)
        XCTAssertNil(distinct.rows[0].coverage)
    }

    func testAmbiguousCoverageIndicesStaySeparateWithoutSlotAuthority() throws {
        let printer = try TestData.decodePrinter()
        let slot = ToolheadFilamentCoverage(toolheadIndex: 0, toolheadName: "Same", status: .unknown)
        let model = try build(printer: printer, coverage: snapshot(printer: printer, slots: [slot, slot]))
        XCTAssertEqual(model.rows.count, 2)
        XCTAssertEqual(Set(model.rows.map(\.id)).count, 2)
        XCTAssertTrue(model.rows.allSatisfy { $0.toolheadID == nil })
    }

    func testPrinterSpoolIsUnattributedOnceNotCopiedToToolheads() throws {
        let printer = try TestData.decodePrinter()
        let roster = (0..<2).map { Toolhead(id: UUID(), name: "Same", index: $0, isPrimary: $0 == 0) }
        let model = try build(
            printer: printer, roster: roster,
            spool: PrinterSpoolInfo(hasActiveSpool: true, activeSpoolId: 8, material: "PLA", remainingWeightG: 1200)
        )
        XCTAssertEqual(model.rows.count, 3)
        XCTAssertTrue(model.rows.prefix(2).allSatisfy { $0.spoolID == nil && $0.remainingGrams == nil })
        XCTAssertEqual(model.rows.last?.title, "Printer-level spool (slot not specified)")
        XCTAssertEqual(model.rows.last?.remainingGrams, 1200)
    }

    // MARK: - Nozzle diameter (issue #2522, Hicks review finding 21)

    func testRosterRowCarriesNozzleDiameterFromToolhead() throws {
        let printer = try TestData.decodePrinter()
        let roster = [
            Toolhead(id: UUID(), name: "Tool 0", index: 0, isPrimary: true, nozzleDiameter: 0.4),
            Toolhead(id: UUID(), name: "Tool 1", index: 1, isPrimary: false, nozzleDiameter: nil)
        ]
        let model = try build(printer: printer, roster: roster)
        XCTAssertEqual(model.rows.count, 2)
        XCTAssertEqual(model.rows[0].nozzleDiameter, 0.4)
        XCTAssertNil(model.rows[1].nozzleDiameter)
    }

    func testCoverageOnlyAndPrinterSpoolRowsHaveNoNozzleDiameter() throws {
        let printer = try TestData.decodePrinter()
        let id = UUID()
        let coverage = snapshot(
            printer: printer,
            slots: [ToolheadFilamentCoverage(
                toolheadIndex: 0, toolheadId: id, toolheadName: "Coverage-only",
                spoolId: 1, material: "PLA", remainingGrams: 500, status: .covers
            )]
        )
        let model = try build(
            printer: printer, roster: [], // no roster match -> coverage-only row
            spool: PrinterSpoolInfo(hasActiveSpool: true, activeSpoolId: 9, material: "PLA", remainingWeightG: 400),
            coverage: coverage
        )
        // One coverage-only row + one printer-level spool row.
        XCTAssertEqual(model.rows.count, 2)
        XCTAssertTrue(model.rows.allSatisfy { $0.nozzleDiameter == nil })
    }

    func testCoverageCannotResurrectClearedOrChangedAssignments() throws {
        let printer = try TestData.decodePrinter()
        let id = UUID()
        let old = ToolheadFilamentCoverage(
            toolheadIndex: 0, toolheadId: id, toolheadName: "Tool",
            spoolId: 10, material: "Old PLA", remainingGrams: 800, status: .covers
        )
        for newSpool: Int? in [nil, 20] {
            let roster = [Toolhead(id: id, name: "Tool", index: 0, isPrimary: true, currentSpoolId: newSpool)]
            for stale in [false, true] {
                let model = try build(printer: printer, roster: roster, coverage: snapshot(printer: printer, slots: [old]), stale: stale)
                XCTAssertEqual(model.rows[0].spoolID, newSpool)
                XCTAssertNil(model.rows[0].material)
                XCTAssertEqual(model.rows[0].coverage?.spoolId, 10)
                XCTAssertEqual(model.rows[0].coverage?.material, "Old PLA")
                XCTAssertFalse(model.rows[0].isCoverageOnly)
            }
        }
        let coverageOnly = try build(printer: printer, coverage: snapshot(printer: printer, slots: [old]), stale: true)
        XCTAssertTrue(coverageOnly.rows[0].isCoverageOnly)
        XCTAssertNil(coverageOnly.rows[0].spoolID)
        XCTAssertNil(coverageOnly.rows[0].material)
        XCTAssertEqual(coverageOnly.rows[0].coverage?.spoolId, 10)
    }

    func testUnassignedPrinterSpoolCannotRetainOldMaterialOrQuantity() throws {
        let printer = try TestData.decodePrinter()
        let model = try build(
            printer: printer,
            spool: PrinterSpoolInfo(hasActiveSpool: false, activeSpoolId: 10, material: "Old PLA", remainingWeightG: 800)
        )
        XCTAssertNil(model.rows[0].spoolID)
        XCTAssertNil(model.rows[0].material)
        XCTAssertNil(model.rows[0].remainingGrams)
        XCTAssertEqual(model.rows[0].spoolName, "No printer-level spool assigned")
    }

    func testRejectsWrongPrinterAndDuplicateUUIDsWithVisibleDegradedState() throws {
        let printer = try TestData.decodePrinter()
        let wrong = PrinterFilamentCoverage(
            printerId: UUID(), printerName: printer.name, status: .covers, toolheads: [],
            activeJobId: nil, activeJobName: nil, activeJobProgress: nil,
            earliestPredictedRunoutAt: nil, assignedQueuedJobCount: 0, evaluatedAtUtc: Date()
        )
        let tool = Toolhead(id: UUID(), name: "Same", index: 0, isPrimary: true)
        var reported: [String] = []
        let rejected = try build(printer: printer, roster: [tool], coverage: wrong) { reported.append($0) }
        XCTAssertEqual(rejected.rows.count, 1)
        XCTAssertNil(rejected.summary)
        XCTAssertEqual(rejected.coverageState, .unavailable)
        XCTAssertFalse(rejected.integrityNotices.isEmpty)
        XCTAssertEqual(reported, rejected.integrityNotices)
        XCTAssertTrue(rejected.supportedActions.isEmpty)
        let duplicateRoster = try build(
            printer: printer, roster: [tool, tool],
            spool: PrinterSpoolInfo(hasActiveSpool: true, material: "PLA")
        )
        XCTAssertEqual(duplicateRoster.rows.count, 1)
        XCTAssertEqual(duplicateRoster.rows[0].material, "PLA")
        XCTAssertFalse(duplicateRoster.integrityNotices.isEmpty)
        XCTAssertTrue(duplicateRoster.supportedActions.isEmpty)
        let slot = ToolheadFilamentCoverage(toolheadIndex: 0, toolheadId: tool.id, toolheadName: "Same", status: .unknown)
        let duplicateCoverage = try build(printer: printer, roster: [tool], coverage: snapshot(printer: printer, slots: [slot, slot]))
        XCTAssertNil(duplicateCoverage.rows[0].coverage)
        XCTAssertFalse(duplicateCoverage.integrityNotices.isEmpty)
        XCTAssertTrue(duplicateCoverage.supportedActions.isEmpty)
    }
}
