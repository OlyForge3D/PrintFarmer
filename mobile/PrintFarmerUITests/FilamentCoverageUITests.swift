import XCTest

/// Real XCUI acceptance coverage for the F4-M iOS filament coverage
/// contract (#778). One suite drives BOTH iPhone and iPad destinations
/// via the standard `-destination` matrix; the same suite executes on
/// each device the scheme is configured with, so the frozen "iPhone +
/// iPad matrix" requirement is satisfied by running this test class
/// against both destination classes.
///
/// The app is launched with the deterministic
/// `--uitesting-filament-coverage-scenario` mode, which swaps the
/// demo coverage service for a pre-canned fleet spanning every
/// contract-required state:
///
///   * Prusa MK4 #1 → covers
///   * Prusa MK4 #2 → runout with predicted ETA
///   * Bambu X1C    → runout without predicted ETA
///   * Bambu P1S    → unknown (must never surface a badge)
///   * Voron 2.4    → runout with predicted ETA, three toolheads two
///                    of which share the display name "Extruder"
///
/// All assertions rely on `accessibilityIdentifier`s and
/// `accessibilityLabel`s baked into the production views. There are
/// NO sleeps, `Thread.sleep`, or retry loops — every wait is a
/// bounded `waitForExistence` against a specific element that either
/// appears once the deterministic bootstrap finishes rendering or is
/// asserted absent under a bounded window.
final class FilamentCoverageUITests: XCTestCase {

    private var app: XCUIApplication!

    override func setUp() {
        super.setUp()
        continueAfterFailure = false
        app = XCUIApplication()
        // Contract literals — see `UITestBootstrap`. UI-test targets
        // can't `import PrintFarmer`, so we hardcode the string,
        // exactly like `LoginFlowUITests` does with
        // `--uitesting-unauthenticated`.
        app.launchArguments += [
            "--uitesting",
            "--uitesting-filament-coverage-scenario"
        ]
        app.launch()
    }

    override func tearDown() {
        app = nil
        super.tearDown()
    }

    // MARK: - Entry helper (compact tab vs iPad sidebar)

    /// Enter the Farm view via the correct nav container for the
    /// running destination.
    ///
    /// * Compact-width (iPhone): tap the "Farm" tab in the bottom
    ///   TabBar.
    /// * Regular-width (iPad): tap the `sidebar.farm` button in the
    ///   NavigationSplitView sidebar. On iPad portrait the sidebar
    ///   may be collapsed, so we reveal it via the system toggle
    ///   (labelled "Sidebar" / "Toggle Sidebar" / "Show Sidebar")
    ///   before tapping. This matches the pattern used by
    ///   `OperatorShellUITests.selectAttentionSurface`.
    private func enterFarmView() {
        // Compact-width fast path.
        let tabFarm = app.tabBars.buttons["Farm"]
        if tabFarm.waitForExistence(timeout: 10) {
            tabFarm.tap()
            return
        }

        // iPad path: try the sidebar directly, then reveal if needed.
        let sidebarFarm = app.buttons["sidebar.farm"]
        if sidebarFarm.waitForExistence(timeout: 3) {
            sidebarFarm.tap()
            return
        }

        // Sidebar may be collapsed on iPad portrait — reveal it.
        for label in ["Sidebar", "Toggle Sidebar", "Show Sidebar"] {
            let toggle = app.navigationBars.buttons[label]
            if toggle.exists {
                toggle.tap()
                break
            }
        }
        if sidebarFarm.waitForExistence(timeout: 3) {
            sidebarFarm.tap()
            return
        }

        XCTFail("Neither compact 'Farm' tab nor iPad 'sidebar.farm' was reachable within the wait window.")
    }

    /// Locate a coverage badge by identifier. SwiftUI bubbles the
    /// accessibility identifier from a badge up to its wrapping
    /// `NavigationLink`-backed Button (because the card is a single
    /// accessibility container), so `app.descendants[id]` sees BOTH
    /// the outer Button (with the card's a11y label) and the inner
    /// leaf (with the coverage a11y label). We disambiguate by
    /// selecting the leaf whose label matches the frozen contract.
    private func coverageBadge(identifier: String, expectedLabel: String) -> XCUIElement {
        let matches = app.descendants(matching: .any)
            .matching(identifier: identifier)
            .matching(NSPredicate(format: "label == %@", expectedLabel))
        return matches.firstMatch
    }

    /// Same as `coverageBadge` but matches on an a11y-label prefix
    /// (used for the runout-with-ETA badge whose label carries a
    /// locale-formatted trailing time).
    private func coverageBadge(identifier: String, expectedLabelPrefix: String) -> XCUIElement {
        let matches = app.descendants(matching: .any)
            .matching(identifier: identifier)
            .matching(NSPredicate(format: "label BEGINSWITH %@", expectedLabelPrefix))
        return matches.firstMatch
    }

    /// The Farm card for a printer, located by the accessibility
    /// label PrinterListView applies to the wrapping NavigationLink.
    /// The label format is `"<name>, <state> status[, online|offline]"`,
    /// so we match on the `"<name>,"` prefix. SwiftUI accessibility
    /// bubbling gives the outer Button whichever identifier its
    /// descendant badge carries; we filter to Buttons whose label
    /// starts with the printer name to guarantee we get the card.
    private func card(for printerName: String) -> XCUIElement {
        app.buttons
            .matching(NSPredicate(format: "label BEGINSWITH %@", printerName + ","))
            .firstMatch
    }

    // MARK: - Farm badge presence + accessibility labels

    func testFarmCardShowsCoversBadgeForCoversPrinter() {
        enterFarmView()

        let card = self.card(for: "Prusa MK4 #1")
        XCTAssertTrue(card.waitForExistence(timeout: 10),
                      "Prusa MK4 #1 card should render in the Farm view.")

        let coversBadge = coverageBadge(
            identifier: "filament-coverage-badge-covers",
            expectedLabel: "Filament covers this job"
        )
        XCTAssertTrue(coversBadge.waitForExistence(timeout: 5),
                      "Covers badge with the contract a11y label must render for a `.covers` printer.")
    }

    func testFarmCardShowsRunoutETABadgeForRunoutWithETAPrinter() {
        enterFarmView()

        let card = self.card(for: "Prusa MK4 #2")
        XCTAssertTrue(card.waitForExistence(timeout: 10))

        // Frozen contract: label MUST begin with "Filament will run out at ".
        // The trailing text is a locale-formatted short time — we
        // key on the prefix so the assertion is timezone-independent.
        let etaBadge = coverageBadge(
            identifier: "filament-coverage-badge-runout-eta",
            expectedLabelPrefix: "Filament will run out at "
        )
        XCTAssertTrue(etaBadge.waitForExistence(timeout: 5),
                      "Runout-with-ETA badge with the contract a11y label must render for a runout printer whose fleet snapshot carries an ETA.")
    }

    func testFarmCardShowsRunoutMidJobBadgeForRunoutWithoutETAPrinter() {
        enterFarmView()

        let card = self.card(for: "Bambu X1C")
        XCTAssertTrue(card.waitForExistence(timeout: 10))

        let noETABadge = coverageBadge(
            identifier: "filament-coverage-badge-runout-no-eta",
            expectedLabel: "Filament will run out before the job finishes"
        )
        XCTAssertTrue(noETABadge.waitForExistence(timeout: 5),
                      "Runout-mid-job badge with the contract a11y label must render for a runout printer without a predicted ETA.")
    }

    func testFarmCardHasNoCoverageBadgeForUnknownPrinter() {
        enterFarmView()

        let unknownCard = self.card(for: "Bambu P1S")
        XCTAssertTrue(unknownCard.waitForExistence(timeout: 10),
                      "Bambu P1S card must exist even though its coverage is `.unknown`.")

        // Wait for a KNOWN badge (from another printer) to appear so
        // we're guaranteed the fleet snapshot has been rendered end
        // to end. Only then is "no badge on THIS card" a meaningful
        // observation.
        let coversAnywhere = coverageBadge(
            identifier: "filament-coverage-badge-covers",
            expectedLabel: "Filament covers this job"
        )
        XCTAssertTrue(coversAnywhere.waitForExistence(timeout: 10),
                      "Sibling covers badge must render before we can assert unknown card has none.")

        // The unknown card's a11y label starts with "Bambu P1S,".
        // Any coverage badge nested under this Button would carry
        // the same identifier as the leaf; we assert that no button
        // labelled "Bambu P1S, ..." matches any coverage-badge id.
        let unknownCardHasCovers = app.buttons
            .matching(NSPredicate(format: "label BEGINSWITH %@ AND identifier == %@", "Bambu P1S,", "filament-coverage-badge-covers"))
            .count
        let unknownCardHasETA = app.buttons
            .matching(NSPredicate(format: "label BEGINSWITH %@ AND identifier == %@", "Bambu P1S,", "filament-coverage-badge-runout-eta"))
            .count
        let unknownCardHasNoETA = app.buttons
            .matching(NSPredicate(format: "label BEGINSWITH %@ AND identifier == %@", "Bambu P1S,", "filament-coverage-badge-runout-no-eta"))
            .count

        XCTAssertEqual(unknownCardHasCovers, 0,
                       "Unknown coverage must NEVER surface a covers badge.")
        XCTAssertEqual(unknownCardHasETA, 0,
                       "Unknown coverage must NEVER surface a runout-ETA badge.")
        XCTAssertEqual(unknownCardHasNoETA, 0,
                       "Unknown coverage must NEVER surface a runout-mid-job badge.")
    }

    // MARK: - Detail: multi-toolhead rows keyed by stable id

    func testDetailShowsDistinctToolheadRowsEvenWhenDisplayNamesDuplicate() {
        enterFarmView()

        // Voron 2.4 has 3 toolheads. Rows 0 and 2 share the display
        // name "Extruder"; row 1 has a distinct backend `toolheadId`.
        // Each row's a11y id is `filament-coverage-toolhead-<stable id>`.
        // Backend-supplied rows get `id:<uuid>`; index-derived rows
        // get `index:<n>`.
        let card = self.card(for: "Voron 2.4")
        XCTAssertTrue(card.waitForExistence(timeout: 10))
        card.tap()

        // The coverage section exists exactly once on the detail
        // screen. Use .firstMatch to avoid ambiguity if SwiftUI
        // bubbles the identifier onto a parent container.
        let section = app.descendants(matching: .any)
            .matching(identifier: "filament-coverage-section").firstMatch
        XCTAssertTrue(section.waitForExistence(timeout: 10),
                      "Filament Coverage section must render on printer detail.")

        // Row 0 (duplicate display name "Extruder", no backend
        // toolheadId → index-derived stable id).
        let rowIndex0 = app.descendants(matching: .any)
            .matching(identifier: "filament-coverage-toolhead-index:0").firstMatch
        // Row 1 (backend UUID toolheadId).
        let rowBackendUUID = app.descendants(matching: .any)
            .matching(identifier: "filament-coverage-toolhead-id:20000000-1111-2222-3333-444444444444").firstMatch
        // Row 2 (duplicate display name "Extruder", no backend
        // toolheadId → index-derived stable id).
        let rowIndex2 = app.descendants(matching: .any)
            .matching(identifier: "filament-coverage-toolhead-index:2").firstMatch

        XCTAssertTrue(rowIndex0.waitForExistence(timeout: 5),
                      "Toolhead row at index 0 must be present with a stable index-derived id.")
        XCTAssertTrue(rowBackendUUID.waitForExistence(timeout: 5),
                      "Toolhead row carrying a backend UUID toolheadId must be keyed by that UUID, not its display name.")
        XCTAssertTrue(rowIndex2.waitForExistence(timeout: 5),
                      "Toolhead row at index 2 must remain distinct from row 0 even though both share the display name 'Extruder'.")
    }

    // MARK: - Navigation by stable printer id (iPhone tab AND iPad split)

    func testTappingCardNavigatesToDetailByStablePrinterId() {
        enterFarmView()

        let bambuCard = self.card(for: "Bambu X1C")
        XCTAssertTrue(bambuCard.waitForExistence(timeout: 10))
        bambuCard.tap()

        // Presence of the Filament Coverage section proves we landed
        // on printer detail. Presence of the runout-no-ETA badge
        // proves we landed on THIS specific printer (Bambu X1C's
        // fleet state), not just any detail — which is the stable-id
        // navigation contract.
        let section = app.descendants(matching: .any)
            .matching(identifier: "filament-coverage-section").firstMatch
        XCTAssertTrue(section.waitForExistence(timeout: 10),
                      "Tapping a Farm card must navigate to that printer's detail.")

        let noETABadge = coverageBadge(
            identifier: "filament-coverage-badge-runout-no-eta",
            expectedLabel: "Filament will run out before the job finishes"
        )
        XCTAssertTrue(noETABadge.waitForExistence(timeout: 5),
                      "Detail should render the same coverage presentation as the Farm card (runout-no-ETA for Bambu X1C).")
    }
}
