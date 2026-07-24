import XCTest

/// Real XCUI acceptance for the F10-C1b cold-offline read-only farm shell
/// (issue #817). Runs on whichever simulator the `-destination` selects, so
/// the same suite is exercised on an iPhone AND an iPad target.
///
/// The `--uitesting-cold-offline-shell` bootstrap seeds a present cached
/// snapshot of the demo fleet (fixed last-confirmed timestamp) into a stub
/// `FarmSnapshotStoring`, forces the printer service offline, and disables the
/// attention gate so the legacy `DashboardView` surface is reachable through
/// the attention fallback. On launch the dashboard hydrates the cached fleet,
/// the canonical load then fails offline, and the cache is preserved — the
/// read-only stale shell.
///
/// Every wait is gated purely on accessibility state (`waitForExistence`);
/// there are NO fixed sleeps, polling loops, retries, or test-iteration hacks.
final class ColdOfflineShellUITests: PrintFarmerUITestCase {

    override var additionalLaunchArguments: [String] {
        ["--uitesting-cold-offline-shell"]
    }

    /// Opens the read-only `DashboardView` cold-offline shell via the
    /// attention-disabled fallback (the initial Attention tab renders the
    /// fallback; its "Dashboard" button presents the sheet). Returns the stale
    /// connection banner, which the read-only shell always mounts.
    @discardableResult
    private func openColdOfflineShell() -> XCUIElement {
        let dashboardButton = app.buttons["attention.fallback.dashboard"]
        XCTAssertTrue(
            dashboardButton.waitForExistence(timeout: 15),
            "Attention-disabled fallback must expose the Dashboard entry point"
        )
        dashboardButton.tap()

        let staleBanner = app.buttons["connection-status-bar-stale"]
        if !staleBanner.waitForExistence(timeout: 15) {
            XCTFail(
                "Cold/offline launch must render the read-only cached farm shell.\n"
                + app.debugDescription
            )
        }
        return staleBanner
    }

    /// Matches the first descendant whose accessibility identifier begins with
    /// the given prefix (used for the stable-UUID `farm-card-<uuid>` cards).
    private func firstElement(idPrefix: String) -> XCUIElement {
        app.descendants(matching: .any)
            .matching(NSPredicate(format: "identifier BEGINSWITH %@", idPrefix))
            .firstMatch
    }

    /// Matches the first descendant with the exact accessibility identifier,
    /// regardless of its XCUI element type. SwiftUI attaches an identifier placed
    /// on a transparent container to whatever concrete view it collapses onto
    /// (e.g. `cold-offline-shell` lands on the cards `ScrollView`, so it surfaces
    /// under `scrollViews`, not `otherElements`). Querying by identifier across
    /// any type keeps these state anchors robust without asserting a brittle,
    /// layout-dependent element class.
    private func element(id: String) -> XCUIElement {
        app.descendants(matching: .any)
            .matching(NSPredicate(format: "identifier == %@", id))
            .firstMatch
    }

    // MARK: - Tests

    /// Offline launch renders the cached read-only shell with populated cards.
    func testOfflineLaunchShowsCachedReadOnlyShell() {
        openColdOfflineShell()

        XCTAssertTrue(
            element(id: "cold-offline-shell").waitForExistence(timeout: 10),
            "The cold-offline shell content container must be present"
        )

        // At least one cached Farm card is projected (online parity), addressed
        // by its stable UUID identifier prefix.
        XCTAssertTrue(
            firstElement(idPrefix: "farm-card-").waitForExistence(timeout: 10),
            "Cached read-only shell must project the last-confirmed fleet as Farm cards"
        )

        // The distinct "no cached data" and present-empty dead-ends must NOT be
        // shown when a populated snapshot exists.
        XCTAssertFalse(
            element(id: "farm-absent-state").exists,
            "A populated cached snapshot must not render the absent-fleet state"
        )
        XCTAssertFalse(
            element(id: "farm-cached-empty-state").exists,
            "A populated cached snapshot must not render the cached-empty state"
        )
    }

    /// The stale banner is shown and conveys the last-confirmed timestamp by
    /// text + accessibility — never color alone.
    func testStaleBannerShowsLastConfirmedTimestamp() {
        let staleBanner = openColdOfflineShell()
        XCTAssertTrue(staleBanner.exists, "Read-only shell must present the STALE banner")

        // Staleness + the concrete last-confirmed instant are carried in the
        // banner's text/accessibility label (color is decorative only). The
        // fixed seed timestamp is 2024-01-01, so the banner announces a
        // "Last updated …" phrase.
        let label = staleBanner.label
        XCTAssertTrue(
            label.contains("cached") || label.contains("read-only"),
            "Stale banner must convey cached/read-only state as text, not color alone. Got: \(label)"
        )
        XCTAssertTrue(
            label.contains("Last updated"),
            "Stale banner must show the last-confirmed timestamp as text. Got: \(label)"
        )
    }

    /// No mutation or command affordances are mounted while the shell is stale.
    /// The read-only shell exposes only the banner + cached cards; the live
    /// dashboard's active-job command rows must be absent.
    func testReadOnlyShellMountsNoMutationAffordances() {
        openColdOfflineShell()

        // The live analytics dashboard mounts `dashboard.activeJob.*` command
        // rows; none may exist while read-only/stale.
        XCTAssertFalse(
            firstElement(idPrefix: "dashboard.activeJob.").exists,
            "Read-only cold-offline shell must not mount live active-job command affordances"
        )
    }
}
