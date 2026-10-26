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
    override var waitsForNavigationReadiness: Bool { true }

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
        let sibling = card(uuid: prusaMK4_1_ID)
        XCTAssertTrue(sibling.waitForExistence(timeout: 10),
                      "Fleet card must render before absence assertions become meaningful.")
    }

    /// Opens the dedicated Filament page. Its technical coverage details were
    /// removed (#3496), so the page must not offer that disclosure.
    override func openPrinterFilamentDetails(printerID: String) -> XCUIElement {
        // Resolve unique containers without nested firstMatch queries: their
        // lazy AX walks can trip the app's main-run-loop stall watchdog.
        let root = app.descendants(matching: .any)
            .matching(identifier: "printer.detail.root.\(printerID)").element
        XCTAssertTrue(root.waitForExistence(timeout: 10),
                      "Navigation must reach the exact printer's detail, not a same-name sibling.")
        let selector = root.descendants(matching: .any)
            .matching(identifier: "printer.detail.panel.selector").element
        XCTAssertTrue(selector.waitForExistence(timeout: 5))
        let filamentSegment = selector.buttons["Filament"]
        XCTAssertTrue(filamentSegment.waitForExistence(timeout: 5))
        filamentSegment.tap()

        let filament = root.descendants(matching: .any)
            .matching(identifier: "printer.detail.panel.filament").element
        XCTAssertTrue(filament.waitForExistence(timeout: 5))
        let heading = filament.descendants(matching: .any)
            .matching(identifier: "printer.filament.heading").element
        XCTAssertTrue(heading.waitForExistence(timeout: 10))
        let coverageSummary = filament.descendants(matching: .any)
            .matching(identifier: "printer.filament.attention").element(boundBy: 0)
        XCTAssertTrue(
            coverageSummary.waitForExistence(timeout: 8),
            "The printer's coverage response must be rendered on the Filament page."
        )
        XCTAssertEqual(
            filament.descendants(matching: .any).matching(identifier: "printer.filament.disclosure").count, 0,
            "The Filament page no longer offers a Coverage details disclosure (#3496)."
        )
        return filament
    }

    // MARK: - Farm cards no longer show coverage (#3232)

    func testFarmCardOmitsCoversBadgeForCoversPrinter() {
        enterFarmView()
        XCTAssertTrue(card(uuid: prusaMK4_1_ID).waitForExistence(timeout: 10))

        let coversBadge = badgeInsideCard(
            cardUUID: prusaMK4_1_ID,
            identifier: "filament-coverage-badge-covers",
            expectedLabel: "Filament covers this job"
        )
        XCTAssertFalse(coversBadge.exists, "Coverage belongs in Filament detail, not the farm card.")
    }

    func testFarmCardOmitsRunoutETABadgeForRunoutWithETAPrinter() {
        enterFarmView()
        XCTAssertTrue(card(uuid: prusaMK4_2_ID).waitForExistence(timeout: 10))

        let etaBadge = badgeInsideCard(
            cardUUID: prusaMK4_2_ID,
            identifier: "filament-coverage-badge-runout-eta",
            expectedLabelPrefix: "Filament will run out at "
        )
        XCTAssertFalse(etaBadge.exists, "Coverage belongs in Filament detail, not the farm card.")
    }

    func testFarmCardOmitsRunoutMidJobBadgeForRunoutWithoutETAPrinter() {
        enterFarmView()
        XCTAssertTrue(card(uuid: bambuX1C_ID).waitForExistence(timeout: 10))

        let noETABadge = badgeInsideCard(
            cardUUID: bambuX1C_ID,
            identifier: "filament-coverage-badge-runout-no-eta",
            expectedLabel: "Filament will run out before the job finishes"
        )
        XCTAssertFalse(noETABadge.exists, "Coverage belongs in Filament detail, not the farm card.")
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

    /// Duplicate names remain distinct stable-ID navigation targets, with
    /// coverage intentionally absent from both cards.
    func testDuplicateDisplayNamePrintersRemainDistinctWithoutCoverageBadges() {
        enterFarmView()

        let originalCard = card(uuid: prusaMK4_1_ID)
        let duplicateCard = card(uuid: duplicateID)
        XCTAssertTrue(originalCard.waitForExistence(timeout: 10),
                      "Original 'Prusa MK4 #1' card must render.")
        XCTAssertTrue(duplicateCard.waitForExistence(timeout: 10),
                      "Duplicate 'Prusa MK4 #1' card must render as a distinct element (keyed by UUID, not name).")

        // Coverage is still present in the fixture, but not on the card.
        let originalCovers = badgeInsideCard(
            cardUUID: prusaMK4_1_ID,
            identifier: "filament-coverage-badge-covers",
            expectedLabel: "Filament covers this job"
        )
        XCTAssertFalse(originalCovers.exists)

        let duplicateRunout = badgeInsideCard(
            cardUUID: duplicateID,
            identifier: "filament-coverage-badge-runout-no-eta",
            expectedLabel: "Filament will run out before the job finishes"
        )
        XCTAssertFalse(duplicateRunout.exists)

        // Both cards omit every coverage state.
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
    /// proven by a filament action identifier that embeds the duplicate's
    /// own stable printer UUID (`printer.filament.action.<printerId>/printer/<kind>`;
    /// the coverage detail rows were removed in #3496).
    /// Confirms stable-id navigation, not display-name matching.
    func testTappingDuplicateNameCardNavigatesByStableIDToCorrectDetail() {
        enterFarmView()
        let duplicateCard = card(uuid: duplicateID)
        XCTAssertTrue(duplicateCard.waitForExistence(timeout: 10))
        duplicateCard.tap()

        let filament = openPrinterFilamentDetails(printerID: duplicateID)

        let duplicateAction = filament.buttons["printer.filament.action.\(duplicateID)/printer/scanNFC"]
        XCTAssertTrue(duplicateAction.waitForExistence(timeout: 5),
                      "A filament action keyed by the DUPLICATE's own UUID proves we landed on the DUPLICATE, not the original.")

        // Absence cross-check: the ORIGINAL's printer-UUID-keyed action must
        // NOT be present — proves we did NOT navigate to the demo original.
        let originalActionCount = filament.descendants(matching: .any)
            .matching(identifier: "printer.filament.action.\(prusaMK4_1_ID)/printer/scanNFC").count
        XCTAssertEqual(originalActionCount, 0,
                       "Detail must not carry an action keyed by the ORIGINAL's UUID — stable-id nav landed on the duplicate.")
    }

    // MARK: - Detail: multi-toolhead printer keeps stable action ids

    /// Voron 2.4 has three toolheads (two sharing the "Extruder" name). With
    /// coverage details removed (#3496), its Filament page must not render the
    /// technical summary yet still key every action by the printer's UUID.
    func testMultiToolheadFilamentPageOmitsCoverageDetailsAndKeepsStableActionIDs() {
        enterFarmView()

        let voronCard = card(uuid: voron24_ID)
        XCTAssertTrue(voronCard.waitForExistence(timeout: 10))
        voronCard.tap()

        let filament = openPrinterFilamentDetails(printerID: voron24_ID)

        XCTAssertFalse(filament.staticTexts["printer.filament.summary"].exists,
                       "Coverage summary text is removed from the Filament page.")
        XCTAssertEqual(
            filament.descendants(matching: .any)
                .matching(NSPredicate(format: "identifier BEGINSWITH %@", "printer.filament.details.")).count,
            0,
            "Per-toolhead coverage detail rows are removed from the Filament page."
        )
        for kind in ["clearAssignment", "scanNFC"] {
            XCTAssertTrue(
                filament.buttons["printer.filament.action.\(voron24_ID)/printer/\(kind)"].waitForExistence(timeout: 5),
                "The \(kind) action must retain its own stable identifier."
            )
        }
    }

    // MARK: - Stable-id navigation (iPhone + iPad)

    func testTappingCardNavigatesToDetailByStablePrinterId() {
        enterFarmView()
        let bambuCard = card(uuid: bambuX1C_ID)
        XCTAssertTrue(bambuCard.waitForExistence(timeout: 10))
        bambuCard.tap()

        let filament = openPrinterFilamentDetails(printerID: bambuX1C_ID)

        // A filament action keyed by Bambu X1C's own printer UUID proves we
        // landed on THIS printer (stable-id nav proof).
        let action = filament.buttons["printer.filament.action.\(bambuX1C_ID)/printer/scanNFC"]
        XCTAssertTrue(action.waitForExistence(timeout: 5),
                      "Detail must render a filament action keyed by Bambu X1C's own UUID (stable-id nav proof).")
    }
}
