import XCTest
@testable import PrintFarmer

/// Decoder/construction-level tests for `PartsInventoryConflict.isWrongBin`
/// (#714 H5 remediation). A `wrongBin`-coded 409 payload must only be typed
/// as a wrong-bin conflict when it carries at least one well-formed
/// mismatch — malformed, missing, or empty `mismatches` must fall back to
/// the generic-conflict path so the UI never shows a blind override
/// affordance for an unverified claim.
final class PartsInventoryConflictTests: XCTestCase {
    func testIsWrongBinTrueWithWellFormedMismatch() {
        let conflict = makeConflict(
            code: PartsInventoryConflict.wrongBinCode,
            mismatches: [WrongBinMismatch(partSku: "SKU-A", expectedBinCode: "BIN-1", scannedBinCode: "BIN-2")]
        )

        XCTAssertTrue(conflict.isWrongBin)
    }

    func testIsWrongBinFalseWhenMismatchesIsNil() {
        let conflict = makeConflict(code: PartsInventoryConflict.wrongBinCode, mismatches: nil)

        XCTAssertFalse(conflict.isWrongBin, "missing mismatches must not type as wrongBin")
    }

    func testIsWrongBinFalseWhenMismatchesIsEmpty() {
        let conflict = makeConflict(code: PartsInventoryConflict.wrongBinCode, mismatches: [])

        XCTAssertFalse(conflict.isWrongBin, "empty mismatches must not type as wrongBin")
    }

    func testIsWrongBinFalseWhenMismatchEntryHasBlankPartSku() {
        let conflict = makeConflict(
            code: PartsInventoryConflict.wrongBinCode,
            mismatches: [WrongBinMismatch(partSku: "", expectedBinCode: "BIN-1", scannedBinCode: "BIN-2")]
        )

        XCTAssertFalse(conflict.isWrongBin, "a mismatch with a blank partSku must not type as wrongBin")
    }

    func testIsWrongBinFalseWhenMismatchEntryHasBlankScannedBinCode() {
        let conflict = makeConflict(
            code: PartsInventoryConflict.wrongBinCode,
            mismatches: [WrongBinMismatch(partSku: "SKU-A", expectedBinCode: "BIN-1", scannedBinCode: "")]
        )

        XCTAssertFalse(conflict.isWrongBin, "a mismatch with a blank scannedBinCode must not type as wrongBin")
    }

    func testIsWrongBinFalseForDifferentCodeEvenWithMismatches() {
        let conflict = makeConflict(
            code: PartsInventoryConflict.partMappingRequiredCode,
            mismatches: [WrongBinMismatch(partSku: "SKU-A", expectedBinCode: "BIN-1", scannedBinCode: "BIN-2")]
        )

        XCTAssertFalse(conflict.isWrongBin)
    }

    func testIsPartMappingRequiredUnaffectedByH5Fix() {
        let conflict = makeConflict(code: PartsInventoryConflict.partMappingRequiredCode, mismatches: nil)

        XCTAssertTrue(conflict.isPartMappingRequired)
        XCTAssertFalse(conflict.isWrongBin)
    }

    // MARK: - Fixtures

    private func makeConflict(code: String, mismatches: [WrongBinMismatch]?) -> PartsInventoryConflict {
        PartsInventoryConflict(
            code: code,
            title: "Conflict",
            detail: nil,
            mismatches: mismatches,
            jobId: nil,
            projectFileId: nil,
            gcodeFileId: nil,
            guidance: nil
        )
    }
}
