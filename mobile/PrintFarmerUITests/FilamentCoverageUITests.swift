import XCTest

/// Real XCUI acceptance for the F4-M iOS filament coverage
/// contract (#778) — cycle-3 edition.
///
/// Runs against BOTH an iPhone and an iPad destination through the
/// same suite; each destination is provided by the xcodebuild
/// `-destination` matrix.
///
/// The app is launched with the deterministic
/// `--uitesting-filament-coverage-scenario` mode; that swaps the
/// demo `filamentCoverageService` for `StubFilamentCoverageService`
/// and adds one duplicate-display-name printer to the fleet.
///
/// Seeded fleet:
///
///   * Prusa MK4 #1 (UUID …000000000001) → covers
///   * Prusa MK4 #2 (UUID …000000000002) → runout with predicted ETA
///   * Bambu X1C    (UUID …000000000003) → runout without ETA
///   * Bambu P1S    (UUID …000000000004) → unknown (no badge)
///   * Voron 2.4    (UUID …000000000005) → runout w/ ETA + three
///                                          toolheads including two
///                                          that share display name
///                                          "Extruder"
///   * "Prusa MK4 #1" DUPLICATE (UUID …0000000000AA) → runout no-ETA
///     (SAME display name as the demo original, DIFFERENT status)
///
/// Every badge / absence assertion is scoped BENEATH the specific
/// `farm-card-<uuid>` element (reviewer blocker D). Reaching the
/// wrong printer's card is not just an ambiguity error — it makes
/// the scoped query fail outright, so sibling badges cannot satisfy
/// a per-card assertion.
///
/// Deterministic-test discipline: every wait is a bounded
/// `waitForExistence`; no `Thread.sleep`, no `Task.sleep`, no
/// retry-until-pass, no elapsed-time gates. Absence assertions gate
/// on a paired sibling's positive appearance (so "not rendered" is
/// a real observation, not a race).
@MainActor
final class FilamentCoverageUITests: PrintFarmerUITestCase {

    // Stable UUIDs shared with UITestBootstrap. UI-test targets
    // cannot `import PrintFarmer`, so we hardcode the literals —
    // exactly matching the `DemoData.*_ID` constants.
    private let prusaMK4_1_ID  = "10000000-0001-0000-0000-000000000001"
    private let prusaMK4_2_ID  = "10000000-0001-0000-0000-000000000002"
    private let bambuX1C_ID    = "10000000-0001-0000-0000-000000000003"
    private let bambuP1S_ID    = "10000000-0001-0000-0000-000000000004"
    private let voron24_ID     = "10000000-0001-0000-0000-000000000005"
    private let duplicateID    = "10000000-0001-0000-0000-0000000000AA"

    override var additionalLaunchArguments: [String] {
        ["--uitesting-filament-coverage-scenario"]
    }

    // MARK: - Entry / scoping helpers

    /// Navigate into the Farm view. Compact-width uses the "Farm"
    /// bottom-tab; regular-width uses the `sidebar.farm` button,
    /// revealing the iPad NavigationSplitView sidebar via its
    /// system toggle if collapsed.
    private func enterFarmView() {
        let farm = shellDestinationButton(
            tabIdentifier: "tab.farm",
            timeout: 10
        )
        XCTAssertTrue(
            farm.exists,
            "Neither tab.farm nor sidebar.farm was reachable within the wait window."
        )
        farm.tap()
    }

    /// The Farm card for a specific stable printer UUID. Cards carry
    /// `accessibilityIdentifier("farm-card-<uuid>")` set on the
    /// `NavigationLink` wrapper. Every per-card assertion in this
    /// suite starts from this element so display-name duplication
    /// cannot cross-satisfy the query.
    private func card(uuid: String) -> XCUIElement {
        app.buttons["farm-card-\(uuid)"]
    }

    /// Scope a coverage badge query beneath the specified card and
    /// require the a11y label to equal the frozen contract value.
    /// Because SwiftUI's a11y tree bubbles descendant identifiers
    /// up to the wrapping `NavigationLink` Button, the card itself
    /// also carries the badge identifier — we disambiguate by
    /// label equality (or prefix for the ETA badge).
    private func badgeInsideCard(
        cardUUID: String,
        identifier: String,
        expectedLabel: String
    ) -> XCUIElement {
        card(uuid: cardUUID)
            .descendants(matching: .any)
            .matching(identifier: identifier)
            .matching(NSPredicate(format: "label == %@", expectedLabel))
            .firstMatch
    }

    private func badgeInsideCard(
        cardUUID: String,
        identifier: String,
        expectedLabelPrefix: String
    ) -> XCUIElement {
        card(uuid: cardUUID)
            .descendants(matching: .any)
            .matching(identifier: identifier)
            .matching(NSPredicate(format: "label BEGINSWITH %@", expectedLabelPrefix))
            .firstMatch
    }

    /// Absence gate: waits for a KNOWN badge on a SIBLING card to
    /// render, then returns. If the sibling has rendered, the whole
    /// fleet snapshot has propagated end to end, so "no badge on
    /// this card" becomes a real deterministic observation instead
    /// of a race.
    private func awaitFleetHasRendered() {
        let sibling = badgeInsideCard(
            cardUUID: prusaMK4_1_ID,
            identifier: "filament-coverage-badge-covers",
            expectedLabel: "Filament covers this job"
        )
        XCTAssertTrue(sibling.waitForExistence(timeout: 10),
                      "Sibling covers badge must render before absence assertions become meaningful.")
    }

    // MARK: - Per-state Farm-card scoped assertions

    func testFarmCardShowsCoversBadgeForCoversPrinter() {
        enterFarmView()
        XCTAssertTrue(card(uuid: prusaMK4_1_ID).waitForExistence(timeout: 10))

        let coversBadge = badgeInsideCard(
            cardUUID: prusaMK4_1_ID,
            identifier: "filament-coverage-badge-covers",
            expectedLabel: "Filament covers this job"
        )
        XCTAssertTrue(coversBadge.waitForExistence(timeout: 5),
                      "Covers badge with exact a11y label must render inside the covers printer's card.")
    }

    func testFarmCardShowsRunoutETABadgeForRunoutWithETAPrinter() {
        enterFarmView()
        XCTAssertTrue(card(uuid: prusaMK4_2_ID).waitForExistence(timeout: 10))

        let etaBadge = badgeInsideCard(
            cardUUID: prusaMK4_2_ID,
            identifier: "filament-coverage-badge-runout-eta",
            expectedLabelPrefix: "Filament will run out at "
        )
        XCTAssertTrue(etaBadge.waitForExistence(timeout: 5),
                      "Runout-ETA badge (label prefix 'Filament will run out at ') must render inside the runout-with-ETA printer's card.")
    }

    func testFarmCardShowsRunoutMidJobBadgeForRunoutWithoutETAPrinter() {
        enterFarmView()
        XCTAssertTrue(card(uuid: bambuX1C_ID).waitForExistence(timeout: 10))

        let noETABadge = badgeInsideCard(
            cardUUID: bambuX1C_ID,
            identifier: "filament-coverage-badge-runout-no-eta",
            expectedLabel: "Filament will run out before the job finishes"
        )
        XCTAssertTrue(noETABadge.waitForExistence(timeout: 5),
                      "Runout-mid-job badge with exact a11y label must render inside the runout-without-ETA printer's card.")
    }

    func testFarmCardHasNoCoverageBadgeForUnknownPrinter() {
        enterFarmView()
        XCTAssertTrue(card(uuid: bambuP1S_ID).waitForExistence(timeout: 10),
                      "Unknown-coverage printer's card must still render.")

        // Structural absence gate: wait for a sibling's badge before
        // asserting this card has none. Once the fleet snapshot has
        // rendered end to end, absence is authoritative.
        awaitFleetHasRendered()

        // Every badge identifier must have ZERO matches beneath the
        // unknown card. Scoped `.count == 0` is deterministic — no
        // wait needed, the snapshot is already rendered.
        let unknownCard = card(uuid: bambuP1S_ID)
        let coversInUnknown = unknownCard.descendants(matching: .any)
            .matching(identifier: "filament-coverage-badge-covers").count
        let etaInUnknown = unknownCard.descendants(matching: .any)
            .matching(identifier: "filament-coverage-badge-runout-eta").count
        let noETAInUnknown = unknownCard.descendants(matching: .any)
            .matching(identifier: "filament-coverage-badge-runout-no-eta").count

        XCTAssertEqual(coversInUnknown, 0,
                       "Unknown coverage MUST NEVER surface a covers badge inside its own card.")
        XCTAssertEqual(etaInUnknown, 0,
                       "Unknown coverage MUST NEVER surface a runout-ETA badge inside its own card.")
        XCTAssertEqual(noETAInUnknown, 0,
                       "Unknown coverage MUST NEVER surface a runout-mid-job badge inside its own card.")
    }

    // MARK: - Duplicate-display-name printers (reviewer blocker D)

    /// TWO Farm cards share the display name "Prusa MK4 #1" but
    /// have DIFFERENT stable UUIDs and DIFFERENT coverage badges
    /// (covers vs runout-no-ETA). Each card's scoped badge query
    /// finds ITS badge and NOT the sibling's.
    func testDuplicateDisplayNamePrintersHaveDistinctScopedBadges() {
        enterFarmView()

        let originalCard = card(uuid: prusaMK4_1_ID)
        let duplicateCard = card(uuid: duplicateID)
        XCTAssertTrue(originalCard.waitForExistence(timeout: 10),
                      "Original 'Prusa MK4 #1' card must render.")
        XCTAssertTrue(duplicateCard.waitForExistence(timeout: 10),
                      "Duplicate 'Prusa MK4 #1' card must render as a distinct element (keyed by UUID, not name).")

        // Original: covers.
        let originalCovers = badgeInsideCard(
            cardUUID: prusaMK4_1_ID,
            identifier: "filament-coverage-badge-covers",
            expectedLabel: "Filament covers this job"
        )
        XCTAssertTrue(originalCovers.waitForExistence(timeout: 5),
                      "Original 'Prusa MK4 #1' must show its covers badge.")

        // Duplicate: runout without ETA.
        let duplicateRunout = badgeInsideCard(
            cardUUID: duplicateID,
            identifier: "filament-coverage-badge-runout-no-eta",
            expectedLabel: "Filament will run out before the job finishes"
        )
        XCTAssertTrue(duplicateRunout.waitForExistence(timeout: 5),
                      "Duplicate 'Prusa MK4 #1' must show its runout-mid-job badge (proves scoped query hits the intended card).")

        // Cross-check absence: original has NO runout-no-eta badge,
        // duplicate has NO covers badge. If per-card scoping were
        // broken these would both fire.
        let originalNoETACount = originalCard.descendants(matching: .any)
            .matching(identifier: "filament-coverage-badge-runout-no-eta").count
        let duplicateCoversCount = duplicateCard.descendants(matching: .any)
            .matching(identifier: "filament-coverage-badge-covers").count
        XCTAssertEqual(originalNoETACount, 0,
                       "Original 'Prusa MK4 #1' card must not surface the duplicate's runout badge.")
        XCTAssertEqual(duplicateCoversCount, 0,
                       "Duplicate 'Prusa MK4 #1' card must not surface the original's covers badge.")
    }

    /// Tapping the DUPLICATE card (same display name as the demo
    /// original, different UUID) reaches the DUPLICATE's detail —
    /// proven by a filament row accessibility identifier that embeds
    /// the duplicate's own stable printer UUID (issue #2522: the old
    /// `FilamentCoverageDetailSection` is gone; #2519's
    /// `PrinterFilamentSection` owns the one Filament block, and its
    /// row ids are `printer.filament.row.<printerId>/coverage/<slot>`
    /// when the printer has no toolhead roster, as demo printers do).
    /// Confirms stable-id navigation, not display-name matching.
    func testTappingDuplicateNameCardNavigatesByStableIDToCorrectDetail() {
        enterFarmView()
        let duplicateCard = card(uuid: duplicateID)
        XCTAssertTrue(duplicateCard.waitForExistence(timeout: 10))
        duplicateCard.tap()

        let heading = app.descendants(matching: .any)
            .matching(identifier: "printer.filament.heading").firstMatch
        XCTAssertTrue(heading.waitForExistence(timeout: 10),
                      "Tapping the duplicate 'Prusa MK4 #1' card must navigate to A printer's detail.")

        // The DUPLICATE's coverage is runout-no-ETA (status .runout); the
        // ORIGINAL's is .covers. A filament row whose id embeds the
        // DUPLICATE's own printer UUID proves we landed on the duplicate.
        let duplicateRow = app.descendants(matching: .any)
            .matching(identifier: "printer.filament.row.\(duplicateID)/coverage/index:0")
            .firstMatch
        XCTAssertTrue(duplicateRow.waitForExistence(timeout: 5),
                      "A filament row keyed by the DUPLICATE's own UUID proves we landed on the DUPLICATE, not the original.")

        // Absence cross-check: the ORIGINAL's own printer-UUID-keyed row
        // must NOT be present on this detail — proves we did NOT navigate
        // to the demo original.
        let originalRowCount = app.descendants(matching: .any)
            .matching(identifier: "printer.filament.row.\(prusaMK4_1_ID)/coverage/index:0").count
        XCTAssertEqual(originalRowCount, 0,
                       "Detail must not carry a row keyed by the ORIGINAL's UUID — stable-id nav landed on the duplicate.")
    }

    // MARK: - Detail: multi-toolhead rows keyed by stable id

    func testDetailShowsDistinctToolheadRowsEvenWhenDisplayNamesDuplicate() {
        enterFarmView()

        let voronCard = card(uuid: voron24_ID)
        XCTAssertTrue(voronCard.waitForExistence(timeout: 10))
        voronCard.tap()

        let heading = app.descendants(matching: .any)
            .matching(identifier: "printer.filament.heading").firstMatch
        XCTAssertTrue(heading.waitForExistence(timeout: 10),
                      "Filament section must render on printer detail.")

        // Voron 2.4: 3 toolheads, no roster (demo printers report none), so
        // #2519's `PrinterFilamentPresentation` renders one coverage-only row
        // per slot, keyed by `<printerId>/coverage/<identity>`. Rows 0 and 2
        // share the display name "Extruder" but have distinct index-derived
        // stable ids. Row 1 carries a backend UUID toolheadId.
        let rowIndex0 = app.descendants(matching: .any)
            .matching(identifier: "printer.filament.row.\(voron24_ID)/coverage/index:0").firstMatch
        let rowBackendUUID = app.descendants(matching: .any)
            .matching(identifier: "printer.filament.row.\(voron24_ID)/coverage/id:20000000-1111-2222-3333-444444444444")
            .firstMatch
        let rowIndex2 = app.descendants(matching: .any)
            .matching(identifier: "printer.filament.row.\(voron24_ID)/coverage/index:2").firstMatch

        XCTAssertTrue(rowIndex0.waitForExistence(timeout: 5),
                      "Toolhead row at index 0 must be present with a stable index-derived id.")
        XCTAssertTrue(rowBackendUUID.waitForExistence(timeout: 5),
                      "Toolhead row carrying a backend UUID toolheadId must be keyed by that UUID.")
        XCTAssertTrue(rowIndex2.waitForExistence(timeout: 5),
                      "Toolhead row at index 2 must remain distinct from row 0 despite the shared 'Extruder' display name.")
    }

    // MARK: - Stable-id navigation (iPhone + iPad)

    func testTappingCardNavigatesToDetailByStablePrinterId() {
        enterFarmView()
        let bambuCard = card(uuid: bambuX1C_ID)
        XCTAssertTrue(bambuCard.waitForExistence(timeout: 10))
        bambuCard.tap()

        let heading = app.descendants(matching: .any)
            .matching(identifier: "printer.filament.heading").firstMatch
        XCTAssertTrue(heading.waitForExistence(timeout: 10),
                      "Tapping a Farm card must navigate to that printer's detail.")

        // A filament row keyed by Bambu X1C's own printer UUID proves we
        // landed on THIS printer (stable-id nav proof).
        let row = app.descendants(matching: .any)
            .matching(identifier: "printer.filament.row.\(bambuX1C_ID)/coverage/index:0")
            .firstMatch
        XCTAssertTrue(row.waitForExistence(timeout: 5),
                      "Detail must render a filament row keyed by Bambu X1C's own UUID (stable-id nav proof).")
    }
}
