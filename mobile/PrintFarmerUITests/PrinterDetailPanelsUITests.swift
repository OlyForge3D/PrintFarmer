import XCTest
import UIKit

/// XCUI acceptance for the Status/Controls paging integration (issue #2522).
///
/// Runs against the deterministic `--uitesting` authenticated operator-shell
/// bootstrap (same as `OperatorShellUITests`) — no fake-service scenario
/// override is needed since these assertions only depend on demo fleet data
/// plus the per-server "Advanced Printer Controls" safety toggle already
/// exercised by `OperatorShellUITests`.
///
/// Deterministic-test discipline: every wait is a bounded
/// `waitForExistence`; no `Thread.sleep`/`Task.sleep`/retry-until-pass. The
/// navigation-entry helpers (`openFirstPrinterDetail`,
/// `enableAdvancedPrinterControls`) assert every REQUIRED step of the
/// deterministic bootstrap — the Farm tab, a printer card, Settings, and its
/// safety toggle all always exist in this environment, so a missing one is a
/// real regression, not tolerated as "environment variance" (Hicks review
/// finding 18). The one deliberate exception is the farm-card-vs-collection-
/// cell choice inside `openFirstPrinterDetail`: that is picking between two
/// equally valid ways to reach the SAME target, not tolerating its absence.
/// Once a test has actually reached printer detail, every assertion about
/// the Status page, the panel selector, the Controls page, and Emergency
/// Stop is likewise a deterministic `XCTAssertTrue`/`XCTAssertFalse`.
@MainActor
final class PrinterDetailPanelsUITests: PrintFarmerUITestCase {
    override var waitsForNavigationReadiness: Bool { true }
    override var additionalLaunchArguments: [String] {
        ["-UIPreferredContentSizeCategoryName", "UICTContentSizeCategoryL"]
    }

    // MARK: - Navigation helpers

    /// Navigates to the first printer's detail screen. Every step is a
    /// REQUIRED precondition of the deterministic `--uitesting` bootstrap
    /// and is asserted, not silently tolerated.
    private func openFirstPrinterDetail() {
        let farm = shellDestinationButton(tabIdentifier: "tab.farm", timeout: 5)
        XCTAssertTrue(
            farm.exists,
            "The Farm destination must be reachable in the deterministic UI-test bootstrap"
        )
        farm.tap()

        let farmCard = app.buttons
            .matching(NSPredicate(format: "identifier BEGINSWITH %@", "farm-card-"))
            .firstMatch
        if farmCard.waitForExistence(timeout: 5) {
            farmCard.tap()
            return
        }
        // Deliberate choice between two equally valid ways to reach the SAME
        // target (a stable farm-card wrapper vs. the raw collection-view
        // cell) — not a tolerance for the fleet being empty.
        let firstPrinter = app.collectionViews.cells.firstMatch
        XCTAssertTrue(
            firstPrinter.waitForExistence(timeout: 5),
            "The deterministic UI-test fleet must expose at least one printer card or collection cell"
        )
        firstPrinter.tap()
    }

    private func panelSelector() -> XCUIElement {
        // The segmented selector reports a different accessibility element
        // type on iPhone and iPad; its identifier is stable across both.
        app.descendants(matching: .any)["printer.detail.panel.selector"]
    }

    /// Enables the per-server "Advanced Printer Controls" safety toggle from
    /// Settings. Every step is a REQUIRED precondition of the deterministic
    /// bootstrap and is asserted, not silently tolerated.
    private func enableAdvancedPrinterControls() {
        let farm = shellDestinationButton(tabIdentifier: "tab.farm", timeout: 5)
        XCTAssertTrue(
            farm.exists,
            "The Farm destination must be reachable in the deterministic UI-test bootstrap"
        )
        farm.tap()

        // Account is shared root chrome; enter it before opening Settings.
        let account = app.buttons["navigation.account"]
        XCTAssertTrue(
            account.waitForExistence(timeout: 5),
            "The Account entry point must be reachable from Farm in the deterministic UI-test bootstrap"
        )
        account.tap()
        XCTAssertTrue(
            app.descendants(matching: .any)["account.root"].waitForExistence(timeout: 5),
            "The Account root must appear after tapping into it"
        )

        let settingsDestination = app.buttons["account.destination.settings"]
        XCTAssertTrue(
            settingsDestination.waitForExistence(timeout: 3),
            "Settings must be reachable from Account in the deterministic UI-test bootstrap"
        )
        settingsDestination.tap()

        XCTAssertTrue(
            app.navigationBars["Settings"].waitForExistence(timeout: 5),
            "The Settings screen must appear"
        )
        let toggle = app.switches["settings.advancedPrinterControls"]
        // Settings is a lazy list; scroll only if the target is not realized.
        if !toggle.exists { app.swipeUp() }
        XCTAssertTrue(
            toggle.waitForExistence(timeout: 3),
            "Settings must expose the Advanced Printer Controls safety toggle"
        )
        for _ in 0..<3 where !toggle.isHittable {
            app.swipeUp()
        }
        XCTAssertTrue(
            toggle.isHittable,
            "The Advanced Printer Controls safety toggle must be visible and hittable in Settings"
        )
        if toggle.value as? String != "1" {
            // The SwiftUI switch element includes its label, but tapping the
            // center lands on inert label text. Target the native knob instead.
            toggle.coordinate(withNormalizedOffset: CGVector(dx: 0.95, dy: 0.5)).tap()
            let becameOn = XCTNSPredicateExpectation(
                predicate: NSPredicate(format: "value == '1'"),
                object: toggle
            )
            let result = XCTWaiter().wait(for: [becameOn], timeout: 5)
            XCTAssertEqual(
                result, .completed,
                "Tapping the Advanced Printer Controls toggle must flip its own value to on"
            )
        }

        // The Farm sidebar selection does not pop the Farm NavigationStack on
        // iPad, so return from Settings through Account before reopening detail.
        // BackButton is XCTest's platform navigation-item identifier, not an app-owned ID.
        let settingsBack = app.navigationBars["Settings"].buttons["BackButton"]
        XCTAssertTrue(settingsBack.waitForExistence(timeout: 3))
        settingsBack.tap()
        XCTAssertTrue(app.descendants(matching: .any)["account.root"].waitForExistence(timeout: 3))
        let accountBack = app.navigationBars["Account"].buttons["BackButton"]
        XCTAssertTrue(accountBack.waitForExistence(timeout: 3))
        accountBack.tap()
        XCTAssertTrue(
            app.buttons["navigation.account"].waitForExistence(timeout: 3),
            "Returning from Settings must restore the Farm root before opening printer detail"
        )
    }

    // MARK: - Default entry / gating

    func testEssentialIdentityRemainsAboveBothPagesAndSelectorStaysCompact() {
        openFirstPrinterDetail()
        let farmButton = app.buttons["printer.detail.farm"]
        XCTAssertTrue(farmButton.waitForExistence(timeout: 5))
        XCTAssertEqual(farmButton.label, "‹ Farm")
        let webLink = app.descendants(matching: .any)["printer.detail.web"]
        XCTAssertTrue(webLink.waitForExistence(timeout: 5))
        XCTAssertEqual(webLink.label, "Open in web ↗")
        let identity = app.otherElements["printer.detail.identity"]
        XCTAssertTrue(identity.waitForExistence(timeout: 8))
        let names = app.staticTexts.matching(NSPredicate(
            format: "identifier BEGINSWITH %@", "printer.detail.destination."
        ))
        XCTAssertEqual(names.count, 1)
        let name = names.firstMatch.label
        let selector = panelSelector()
        XCTAssertTrue(selector.exists)
        XCTAssertLessThan(identity.frame.maxY, selector.frame.minY)
        XCTAssertGreaterThanOrEqual(selector.frame.height, 44)
        XCTAssertEqual(identity.frame.minX, selector.frame.minX, accuracy: 1)
        for title in ["Status", "Control", "Filament", "Queue", "Status"] {
            selector.buttons[title].tap()
            let page = app.descendants(matching: .any)["printer.detail.panel.\(title.lowercased())"]
            XCTAssertTrue(page.waitForExistence(timeout: 5))
            XCTAssertEqual(names.count, 1, "Printer identity must not be duplicated in a page")
            XCTAssertEqual(names.firstMatch.label, name)
            XCTAssertLessThan(identity.frame.maxY, selector.frame.minY)
            XCTAssertLessThanOrEqual(selector.frame.maxY, page.frame.minY)
            if title == "Control" {
                let emergency = app.buttons["printer.detail.control.emergencyStop"]
                XCTAssertTrue(emergency.exists)
                XCTAssertGreaterThan(emergency.frame.midY, page.frame.midY)
            } else {
                XCTAssertFalse(app.buttons["printer.detail.control.emergencyStop"].exists)
            }
            let screenshot = XCTAttachment(screenshot: XCUIScreen.main.screenshot())
            screenshot.name = "Essential complete page \(title)"
            screenshot.lifetime = .keepAlways
            add(screenshot)
        }
    }

    #if DEBUG
    @MainActor
    class Issue3259MockupCaptureUITests: PrintFarmerUITestCase {
        override var waitsForNavigationReadiness: Bool { true }
        var contentSizeCategory: String { "UICTContentSizeCategoryL" }

        override var additionalLaunchArguments: [String] {
            [
                "--uitesting-issue3259-visual-acceptance",
                "-pf_theme_mode", "dark",
                "-UIPreferredContentSizeCategoryName", contentSizeCategory
            ]
        }

        func captureApprovedMockupScreens() {
            let isIPad = UIDevice.current.userInterfaceIdiom == .pad
            if isIPad {
                XCUIDevice.shared.orientation = .landscapeLeft
            }
            defer {
                if isIPad {
                    XCUIDevice.shared.orientation = .portrait
                }
            }
            let device = isIPad ? "iPad" : "iPhone"
            let size = contentSizeCategory == "UICTContentSizeCategoryL" ? "normal" : "largest"

            let farm = shellDestinationButton(tabIdentifier: "tab.farm", timeout: 8)
            XCTAssertTrue(farm.exists)
            farm.tap()
            XCTAssertFalse(
                app.tabBars.firstMatch.exists,
                "The native glass tab capsule must stay hidden behind the flat custom dock."
            )
            XCTAssertTrue(
                app.staticTexts["navigation.title"].waitForExistence(timeout: 8)
                    || app.navigationBars["Farm"].waitForExistence(timeout: 8)
            )
            let farmAllFilter = app.buttons["farm.filter.All"]
            XCTAssertTrue(farmAllFilter.waitForExistence(timeout: 8))
            let scanButton = app.buttons["navigation.scan"]
            XCTAssertTrue(scanButton.isHittable)
            if size == "largest" {
                XCTAssertLessThanOrEqual(
                    scanButton.frame.height,
                    88,
                    "The primary Scan action must remain compact enough not to dominate the screen at accessibility text sizes."
                )
            }
            let filterButtons = app.buttons.matching(
                NSPredicate(format: "identifier BEGINSWITH %@", "farm.filter.")
            )
            XCTAssertEqual(filterButtons.count, 4, "Farm must expose all four status filters.")
            XCTAssertEqual(app.buttons["farm.filter.All"].label, isIPad ? "All 12" : "All 6")
            XCTAssertEqual(app.buttons["farm.filter.Printing"].label, isIPad ? "Printing 5" : "Printing 2")
            XCTAssertEqual(app.buttons["farm.filter.Needs attention"].label, "Needs attention 2")
            XCTAssertEqual(app.buttons["farm.filter.Idle"].label, isIPad ? "Idle 3" : "Idle 1")
            if isIPad {
                let farmCount = app.staticTexts["sidebar.count.farm"]
                XCTAssertTrue(farmCount.waitForExistence(timeout: 8))
                XCTAssertEqual(farmCount.label, "Farm count 12")
                let queueCount = app.staticTexts["sidebar.count.queue"]
                XCTAssertTrue(queueCount.waitForExistence(timeout: 8))
                XCTAssertEqual(queueCount.label, "Queue count 3")
                XCTAssertTrue(app.staticTexts["sidebar.count.filament"].waitForExistence(timeout: 8))
                XCTAssertTrue(app.buttons["sidebar.settings"].exists)
            }
            attachScreen("\(device)-\(size)-farm")
            let printerList = app.descendants(matching: .any)["farm.printerList"]
            if !isIPad {
                XCTAssertTrue(printerList.exists, "Farm printer cards must remain in a scrollable list.")
            }
            func scrollFarmUp() {
                if isIPad {
                    app.swipeUp()
                } else {
                    printerList.swipeUp()
                }
            }
            let failureCard = app.buttons.matching(NSPredicate(
                format: "label CONTAINS %@ AND label CONTAINS %@",
                "Voron 2.4",
                "Failure suspected:"
            )).firstMatch
            XCTAssertTrue(failureCard.waitForExistence(timeout: 5))
            XCTAssertTrue(failureCard.label.contains("gear_set_v3.gcode"))
            XCTAssertTrue(failureCard.label.contains("22% progress at failure"))
            XCTAssertTrue(failureCard.label.contains("2h 41m left"))
            XCTAssertTrue(failureCard.label.contains("Done"))
            let firstPrintingCard = app.buttons.matching(NSPredicate(
                format: "label CONTAINS %@ AND label CONTAINS %@",
                "Prusa MK4 #1",
                "benchy_0.2mm_PLA.gcode"
            )).firstMatch
            XCTAssertTrue(firstPrintingCard.waitForExistence(timeout: 5))
            let miniCard = app.buttons.matching(NSPredicate(
                format: "label CONTAINS %@ AND label CONTAINS %@",
                "Prusa Mini",
                "clip_holder_x4.gcode"
            )).firstMatch
            for _ in 0..<8 where !miniCard.exists {
                scrollFarmUp()
            }
            XCTAssertTrue(miniCard.waitForExistence(timeout: 5))
            XCTAssertTrue(miniCard.label.contains("22m left"))
            let bedClearPrinter = app.buttons.matching(
                NSPredicate(format: "label CONTAINS %@", "Bambu X1C")
            ).firstMatch
            for _ in 0..<8 where !bedClearPrinter.exists {
                scrollFarmUp()
            }
            XCTAssertTrue(bedClearPrinter.waitForExistence(timeout: 5))
            XCTAssertTrue(
                bedClearPrinter.label.localizedCaseInsensitiveContains("Bed clear"),
                "Bambu X1C must use the mockup's real PendingReady demo state rather than appear as a second print."
            )
            XCTAssertFalse(bedClearPrinter.label.contains("% complete"))
            XCTAssertFalse(bedClearPrinter.label.contains("ETA unavailable"))
            let pausedCard = app.buttons.matching(NSPredicate(
                format: "label CONTAINS %@ AND label CONTAINS %@",
                "Ender 3 S1",
                "Paused"
            )).firstMatch
            for _ in 0..<8 where !pausedCard.exists {
                scrollFarmUp()
            }
            XCTAssertTrue(pausedCard.waitForExistence(timeout: 5))
            if size == "normal" {
                if isIPad {
                    XCTAssertLessThan(firstPrintingCard.frame.minX, failureCard.frame.minX)
                    XCTAssertLessThan(failureCard.frame.minX, bedClearPrinter.frame.minX)
                    XCTAssertGreaterThan(pausedCard.frame.minY, firstPrintingCard.frame.minY)
                } else {
                    XCTAssertLessThan(firstPrintingCard.frame.minY, failureCard.frame.minY)
                    XCTAssertLessThan(failureCard.frame.minY, bedClearPrinter.frame.minY)
                    XCTAssertLessThan(bedClearPrinter.frame.minY, pausedCard.frame.minY)
                }
            }
            let lastFarmCard = app.buttons.matching(
                NSPredicate(
                    format: "label CONTAINS %@",
                    isIPad ? "Creality K1" : "Prusa MK4 #2"
                )
            ).firstMatch
            for _ in 0..<16 where
                !lastFarmCard.isHittable
                || lastFarmCard.frame.maxY > scanButton.frame.minY - 8
            {
                scrollFarmUp()
            }
            XCTAssertTrue(lastFarmCard.isHittable, "The final Farm card must scroll above floating navigation.")
            XCTAssertLessThanOrEqual(
                lastFarmCard.frame.maxY,
                scanButton.frame.minY - 8,
                "Farm cards must clear the floating Scan control when scrolled to the end."
            )
            func scrollFarmDown() {
                if isIPad {
                    app.swipeDown()
                } else {
                    printerList.swipeDown()
                }
            }
            let allFilterButton = app.buttons["farm.filter.All"]
            for _ in 0..<16 where !allFilterButton.isHittable {
                scrollFarmDown()
            }
            XCTAssertTrue(
                allFilterButton.isHittable,
                "Farm filters must remain reachable after scrolling through the cards. "
                    + "Filter frame: \(allFilterButton.frame), list frame: \(printerList.frame), "
                    + "Scan frame: \(scanButton.frame)."
            )
            XCTAssertFalse(app.staticTexts.containing(
                NSPredicate(format: "label BEGINSWITH %@", "Attention unavailable:")
            ).firstMatch.exists)
            for index in 0..<filterButtons.count {
                let button = filterButtons.element(boundBy: index)
                XCTAssertTrue(button.isHittable, "\(button.identifier) must be visible and tappable.")
            }
            let queue = shellDestinationButton(tabIdentifier: "tab.queue", timeout: 8)
            XCTAssertTrue(queue.exists)
            queue.tap()
            XCTAssertTrue(app.descendants(matching: .any)["jobList.root"].waitForExistence(timeout: 8))
            let queueList = app.descendants(matching: .any)["jobList.root"]
            XCTAssertTrue(queueList.exists)
            XCTAssertTrue(app.buttons["navigation.scan"].isHittable)
            let activeETA = queueList.staticTexts[
                "job.active.eta.30000000-0003-0000-0000-000000000001"
            ]
            XCTAssertTrue(activeETA.waitForExistence(timeout: 8))
            XCTAssertTrue(activeETA.label.contains("38m left"), "Queue ETA must use printer-status telemetry.")
            XCTAssertTrue(queueList.descendants(matching: .any)["jobList.section.printing"].exists)
            attachScreen("\(device)-\(size)-global-queue")
            let queuedThumbnail = queueList.descendants(matching: .any)
                .matching(identifier: "job.thumbnail.32590000-0000-0000-0000-000000000101")
                .firstMatch
            for _ in 0..<8 where !queuedThumbnail.exists {
                queueList.swipeUp()
            }
            XCTAssertTrue(queuedThumbnail.waitForExistence(timeout: 8))
            XCTAssertEqual(queuedThumbnail.value as? String, "Thumbnail loaded")
            attachScreen("\(device)-\(size)-global-queue-queued-thumbnail")
            for section in ["queued", "recent-failures"] {
                let heading = queueList.descendants(matching: .any)["jobList.section.\(section)"]
                for _ in 0..<8 where !heading.exists {
                    queueList.swipeUp()
                }
                XCTAssertTrue(heading.exists, "The \(section) section must remain in the combined queue.")
                if section == "recent-failures" {
                    XCTAssertTrue(heading.isHittable, "Recent failures must be reachable in the shared queue.")
                    let failedJob = queueList.buttons["job.row.30000000-0003-0000-0000-000000000009"]
                    let scan = app.buttons["navigation.scan"]
                    for _ in 0..<12 {
                        if failedJob.isHittable,
                           failedJob.frame.maxY <= scan.frame.minY - 8 {
                            break
                        }
                        queueList.swipeUp()
                    }
                    let failedJobExists = failedJob.waitForExistence(timeout: 5)
                    XCTAssertTrue(failedJobExists, "The visual acceptance fixture must expose the first failed job.")
                    guard failedJobExists else { return }
                    XCTAssertTrue(failedJob.label.localizedCaseInsensitiveContains("vase_mode_spiral.gcode"))
                    let retryButton = queueList.buttons["job.retry.30000000-0003-0000-0000-000000000009"]
                    XCTAssertTrue(retryButton.waitForExistence(timeout: 5))
                    XCTAssertTrue(retryButton.isEnabled, "Queue.Write-authorized operators can rerun a failed job.")
                    XCTAssertTrue(heading.isHittable, "Recent failures must remain visible while inspecting its rows.")
                    attachScreen("\(device)-\(size)-global-queue-recent-failures")
                    XCTAssertTrue(
                        retryButton.isHittable,
                        "Retry must remain visible beside the failed job. Retry: \(retryButton.frame), row: \(failedJob.frame), Scan: \(scan.frame)."
                    )
                    if size == "normal" {
                        XCTAssertLessThan(retryButton.frame.width, 80, "Retry should stay a compact inline action.")
                    }
                    XCTAssertTrue(
                        failedJob.isHittable,
                        "A failed-job row must be visible in Recent failures. Row: \(failedJob.frame), scan: \(scan.frame), list: \(queueList.frame)."
                    )
                    XCTAssertTrue(failedJob.label.localizedCaseInsensitiveContains("failed status"))
                    XCTAssertFalse(
                        failedJob.frame.intersects(heading.frame),
                        "The failed-job row must not overlap the Recent failures heading."
                    )
                    XCTAssertLessThanOrEqual(
                        failedJob.frame.maxY,
                        scan.frame.minY - 8,
                        "The failure row must be completely above the floating Scan action."
                    )
                    XCTAssertFalse(
                        failedJob.frame.intersects(scan.frame),
                        "The floating Scan action must not obscure failure details."
                    )

                    let lastFailure = queueList.buttons["job.row.30000000-0003-0000-0000-000000000010"]
                    for _ in 0..<20 {
                        if lastFailure.isHittable,
                           lastFailure.frame.maxY <= scan.frame.minY - 8 {
                            break
                        }
                        queueList.swipeUp()
                    }
                    let lastFailureExists = lastFailure.waitForExistence(timeout: 5)
                    XCTAssertTrue(lastFailureExists, "The visual acceptance fixture must expose the final failed job.")
                    guard lastFailureExists else { return }
                    XCTAssertTrue(lastFailure.label.localizedCaseInsensitiveContains("lamp_shade_textured.gcode"))
                    XCTAssertTrue(lastFailure.isHittable, "The last failed-job row must remain reachable.")
                    XCTAssertTrue(heading.isHittable, "Recent failures must remain visible while inspecting its rows.")
                    XCTAssertFalse(
                        lastFailure.frame.intersects(heading.frame),
                        "The last failed-job row must not overlap the Recent failures heading."
                    )
                    XCTAssertLessThanOrEqual(
                        lastFailure.frame.maxY,
                        scan.frame.minY - 8,
                        "The last failure row must scroll completely above Scan."
                    )
                    attachScreen("\(device)-\(size)-global-queue-recent-failures")
                }
                if section == "queued" {
                    let assignedSubheading = queueList.descendants(matching: .any)["jobList.assigned.subheading"]
                    XCTAssertFalse(
                        assignedSubheading.exists,
                        "Assigned jobs must not create a peer heading under Queued."
                    )
                    let assignedJob = queueList.descendants(matching: .any).matching(
                        NSPredicate(format: "label CONTAINS[c] %@", "Coral spool bracket.gcode")
                    ).firstMatch
                    for _ in 0..<12 where !assignedJob.isHittable { queueList.swipeUp() }
                    XCTAssertTrue(assignedJob.isHittable, "Assigned jobs stay visible within Queued.")
                    XCTAssertFalse(
                        queueList.descendants(matching: .any)["jobList.section.assigned"].exists,
                        "Assigned must not become a fourth top-level queue group."
                    )
                    attachScreen("\(device)-\(size)-global-queue-assigned")
                }
            }

            let inventory = shellDestinationButton(tabIdentifier: "tab.filament", timeout: 8)
            XCTAssertTrue(inventory.exists)
            inventory.tap()
            XCTAssertTrue(app.buttons["inventory.addSpool"].waitForExistence(timeout: 8))
            XCTAssertTrue(app.buttons["navigation.scan"].isHittable)
            let inventoryFilters = app.descendants(matching: .any)["inventory.filters"]
            XCTAssertTrue(inventoryFilters.waitForExistence(timeout: 5))
            let inventoryFilterButtons = app.buttons.matching(
                NSPredicate(format: "identifier BEGINSWITH %@", "inventory.filter.")
            )
            XCTAssertGreaterThanOrEqual(inventoryFilterButtons.count, 5)
            if size == "largest" {
                for identifier in [
                    "inventory.filter.all",
                    "inventory.filter.loaded",
                    "inventory.filter.low",
                    "inventory.filter.material.PLA",
                ] {
                    let filter = app.buttons[identifier]
                    XCTAssertTrue(filter.isHittable, "\(identifier) must remain directly reachable at largest text.")
                    XCTAssertGreaterThanOrEqual(filter.frame.minX, inventoryFilters.frame.minX - 1)
                    XCTAssertLessThanOrEqual(filter.frame.maxX, inventoryFilters.frame.maxX + 1)
                }
            } else {
                for index in 0..<inventoryFilterButtons.count {
                    let button = inventoryFilterButtons.element(boundBy: index)
                    XCTAssertGreaterThanOrEqual(button.frame.minX, inventoryFilters.frame.minX - 1)
                    XCTAssertLessThanOrEqual(button.frame.maxX, inventoryFilters.frame.maxX + 1)
                    XCTAssertLessThanOrEqual(button.frame.maxY, inventoryFilters.frame.maxY + 1)
                }
            }
            let moreFilters = app.buttons["inventory.filter.more"]
            XCTAssertTrue(moreFilters.isHittable, "Additional inventory filters must be reachable without horizontal scrolling.")
            moreFilters.tap()
            let missingNFCFilter = app.buttons["inventory.filter.no-nfc"]
            XCTAssertTrue(missingNFCFilter.waitForExistence(timeout: 5))
            missingNFCFilter.tap()
            let allFilter = app.buttons["inventory.filter.all"]
            XCTAssertTrue(allFilter.isHittable, "The All filter must remain visible after selecting a secondary filter.")
            allFilter.tap()
            attachScreen("\(device)-\(size)-filament-inventory")
            farm.tap()
            farm.tap()
            let printerCard = app.buttons["farm-card-10000000-0001-0000-0000-000000000001"]
            for _ in 0..<8 where !printerCard.exists {
                printerList.swipeDown()
            }
            XCTAssertTrue(printerCard.waitForExistence(timeout: 8))
            printerCard.tap()
            XCTAssertFalse(
                app.buttons["navigation.scan"].exists,
                "The root-level scanner must not cover printer-detail controls; in-page spool scan remains available."
            )

            let selector = app.descendants(matching: .any)["printer.detail.panel.selector"]
            XCTAssertTrue(selector.waitForExistence(timeout: 8))
            for panel in ["Status", "Control", "Filament", "Queue"] {
                selector.buttons[panel].tap()
                let page = app.descendants(matching: .any)["printer.detail.panel.\(panel.lowercased())"]
                XCTAssertTrue(page.waitForExistence(timeout: 8))
                if panel == "Control" {
                    let emergency = app.buttons["printer.detail.control.emergencyStop"]
                    XCTAssertTrue(emergency.waitForExistence(timeout: 5))
                    XCTAssertGreaterThanOrEqual(
                        emergency.frame.minY,
                        selector.frame.maxY,
                        "Emergency Stop stays at the bottom of the Control page, below its selector."
                    )
                }
                if panel == "Queue" {
                    XCTAssertTrue(
                        page.staticTexts["printer.detail.queue.reorderNote"].waitForExistence(timeout: 5)
                    )
                    XCTAssertFalse(
                        page.staticTexts["Assigned"].exists,
                        "Assigned jobs must not appear as a section before Up next."
                    )
                    let thumbnail = page.descendants(matching: .any)
                        .matching(
                            NSPredicate(
                                format: "identifier BEGINSWITH %@ AND value == %@",
                                "printer.detail.queue.thumbnail.",
                                "Thumbnail loaded"
                            )
                        )
                        .firstMatch
                    XCTAssertTrue(thumbnail.waitForExistence(timeout: 5))
                }
                if panel == "Status" {
                    XCTAssertTrue(app.staticTexts["Print progress 64 percent"].waitForExistence(timeout: 5))
                    XCTAssertTrue(app.staticTexts["benchy_0.2mm_PLA.gcode"].exists)
                    let remainingTime = app.staticTexts
                        .matching(identifier: "printer.detail.job")
                        .matching(NSPredicate(format: "label CONTAINS %@", "38m"))
                        .firstMatch
                    XCTAssertTrue(remainingTime.waitForExistence(timeout: 5))
                    let finishTime = app.staticTexts
                        .matching(identifier: "printer.detail.job")
                        .matching(NSPredicate(format: "label BEGINSWITH %@ AND NOT label CONTAINS %@", "Done at", "Unknown"))
                        .firstMatch
                    XCTAssertTrue(finishTime.exists)
                    let layerFact = app.staticTexts
                        .matching(identifier: "printer.detail.job")
                        .matching(NSPredicate(format: "label CONTAINS %@", "142/221"))
                        .firstMatch
                    XCTAssertTrue(layerFact.waitForExistence(timeout: 5))
                    let preview = app.images
                        .matching(identifier: "printer.detail.hero")
                        .matching(NSPredicate(format: "label == %@", "Current print preview"))
                        .firstMatch
                    XCTAssertTrue(preview.waitForExistence(timeout: 5))
                }
                if panel == "Control" {
                    XCTAssertTrue(
                        app.otherElements["printer.controls.runtime"].waitForExistence(timeout: 5),
                        "Control must expose the API-backed runtime fan and live Z-offset adjustments."
                    )
                    XCTAssertTrue(
                        app.buttons["printer.controls.runtime.fan.increase"].exists,
                        "Fan adjustments must be driven by the authenticated backend-capability response."
                    )
                    XCTAssertTrue(
                        app.buttons["printer.controls.runtime.z-offset.increase"].exists,
                        "Live Z-offset adjustments must be driven by the authenticated backend-capability response."
                    )
                }
                if panel == "Filament" {
                    let warning = app.descendants(matching: .any)["printer.filament.attention"]
                    XCTAssertTrue(warning.waitForExistence(timeout: 5))
                    XCTAssertEqual(warning.label, "About 84 g left. This job needs about 140 g.")
                    XCTAssertEqual(
                        app.staticTexts.matching(
                            NSPredicate(format: "label CONTAINS %@", "This job needs about 140 g")
                        ).count,
                        1,
                        "The current-job demand warning should be visible once."
                    )
                    let swap = app.buttons.matching(
                        NSPredicate(format: "identifier ENDSWITH %@", "/change")
                    ).firstMatch
                    let unassign = app.buttons.matching(
                        NSPredicate(format: "identifier ENDSWITH %@", "/clearAssignment")
                    ).firstMatch
                    XCTAssertTrue(swap.waitForExistence(timeout: 5))
                    XCTAssertTrue(unassign.exists)
                    XCTAssertTrue(swap.isEnabled, "Changing the inventory assignment does not issue a physical load.")
                    XCTAssertTrue(unassign.isEnabled, "Unassigning remains distinct from physical unload.")
                    let physicalLoad = app.buttons["printer.detail.filament.load"]
                    let physicalUnload = app.buttons["printer.detail.filament.unload"]
                    XCTAssertTrue(physicalLoad.exists)
                    XCTAssertTrue(physicalUnload.exists)
                    XCTAssertFalse(physicalLoad.isEnabled)
                    XCTAssertFalse(physicalUnload.isEnabled)
                    XCTAssertTrue(
                        app.staticTexts["printer.detail.filament.extruder.heading"].exists,
                        "Load and Unload belong directly beneath the loaded-spool hero."
                    )
                    XCTAssertEqual(
                        app.staticTexts["printer.detail.filament.printingLockout"].label,
                        "Unavailable while printing"
                    )
                    attachScreen("\(device)-\(size)-printer-filament")
                    if size == "largest" {
                        for _ in 0..<8 where !physicalLoad.isHittable || !physicalUnload.isHittable {
                            swipeUp(on: page)
                        }
                        XCTAssertTrue(physicalLoad.isHittable, "Large text must let users scroll to Load.")
                        XCTAssertTrue(physicalUnload.isHittable, "Large text must let users scroll to Unload.")
                        attachScreen("\(device)-largest-printer-filament-physical-controls")
                        for _ in 0..<8 where !unassign.isHittable {
                            swipeUp(on: page)
                        }
                    } else {
                        XCTAssertTrue(physicalLoad.isHittable, "Load remains visible on the compact detail page.")
                        XCTAssertTrue(physicalUnload.isHittable, "Unload remains visible on the compact detail page.")
                    }
                    XCTAssertTrue(unassign.isHittable, "Unassign remains reachable in the filament page.")
                    XCTAssertGreaterThan(
                        unassign.frame.minY,
                        physicalUnload.frame.maxY,
                        "Physical Load/Unload precede inventory Unassign."
                    )
                    attachScreen("\(device)-\(size)-printer-filament-unassign")
                    XCTAssertEqual(app.staticTexts["printer.filament.heading"].label, "Loaded")
                    XCTAssertFalse(app.staticTexts["Filament details"].exists)
                    XCTAssertFalse(app.staticTexts["Advanced filament tools"].exists)
                    let demand = app.descendants(matching: .any)
                        .matching(NSPredicate(format: "label CONTAINS %@", "Total demand: 140 g"))
                        .firstMatch
                    XCTAssertFalse(demand.exists, "Technical coverage details stay out of the compact loaded-spool hero.")
                    continue
                }
                let printStateSuffix = ["Control", "Queue"].contains(panel) ? "-active-print" : ""
                attachScreen("\(device)-\(size)-printer-\(panel.lowercased())\(printStateSuffix)")
            }
        }

        func attachScreen(_ name: String) {
            let screenshot = XCUIScreen.main.screenshot()
            let image = screenshot.image
            let normalizedImage = UIGraphicsImageRenderer(size: image.size).image { _ in
                image.draw(in: CGRect(origin: .zero, size: image.size))
            }
            let attachment = XCTAttachment(image: normalizedImage)
            attachment.name = "Issue 3259 \(name)"
            attachment.lifetime = .keepAlways
            add(attachment)
        }

        private func swipeUp(on page: XCUIElement) {
            let start = page.coordinate(withNormalizedOffset: CGVector(dx: 0.75, dy: 0.82))
            let end = page.coordinate(withNormalizedOffset: CGVector(dx: 0.75, dy: 0.24))
            start.press(forDuration: 0.1, thenDragTo: end)
        }

        func testRuntimeControlsUseAuthenticatedCommandsAndServerReadback() {
            let farm = shellDestinationButton(tabIdentifier: "tab.farm", timeout: 8)
            XCTAssertTrue(farm.exists)
            farm.tap()
            let printerCard = app.buttons["farm-card-10000000-0001-0000-0000-000000000001"]
            XCTAssertTrue(printerCard.waitForExistence(timeout: 8))
            printerCard.tap()

            let selector = app.descendants(matching: .any)["printer.detail.panel.selector"]
            XCTAssertTrue(selector.waitForExistence(timeout: 8))
            selector.buttons["Control"].tap()
            XCTAssertTrue(app.otherElements["printer.controls.runtime"].waitForExistence(timeout: 8))

            let fanValue = app.staticTexts["printer.controls.runtime.fan.value"]
            XCTAssertTrue(fanValue.waitForExistence(timeout: 5))
            XCTAssertEqual(fanValue.label, "60%")
            app.buttons["printer.controls.runtime.fan.increase"].tap()
            let updatedFan = app.staticTexts.matching(identifier: "printer.controls.runtime.fan.value")
                .matching(NSPredicate(format: "label == %@", "65%")).firstMatch
            XCTAssertTrue(
                updatedFan.waitForExistence(timeout: 5),
                "Fan readback must reflect the accepted API command; current=\(fanValue.label), feedback=\(app.otherElements["printer.controls.runtime.feedback"].label)"
            )

            let zOffsetValue = app.staticTexts["printer.controls.runtime.z-offset.value"]
            XCTAssertTrue(zOffsetValue.waitForExistence(timeout: 5))
            XCTAssertEqual(zOffsetValue.label, "0.025 mm")
            app.buttons["printer.controls.runtime.z-offset.increase"].tap()
            let updatedZOffset = app.staticTexts.matching(identifier: "printer.controls.runtime.z-offset.value")
                .matching(NSPredicate(format: "label == %@", "0.075 mm")).firstMatch
            XCTAssertTrue(updatedZOffset.waitForExistence(timeout: 5), "Live Z-offset readback must reflect the accepted API command.")
            attachScreen("control-authenticated-command-readback")
        }
    }

    #endif

    func testStatusUsesAvailableWidthAcrossRotation() {
        openFirstPrinterDetail()
        defer { XCUIDevice.shared.orientation = .portrait }
        for orientation in [UIDeviceOrientation.portrait, .landscapeLeft] {
            XCUIDevice.shared.orientation = orientation
            let overview = app.descendants(matching: .any)["printer.detail.panel.status"]
            XCTAssertTrue(overview.waitForExistence(timeout: 8))
            let expectedLayout = overview.frame.width >= 760
                ? "printer.detail.columns" : "printer.detail.readingColumn"
            XCTAssertTrue(app.otherElements[expectedLayout].waitForExistence(timeout: 5))
            let temperatures = app.otherElements["printer.detail.temperatures"]
            let job = app.otherElements["printer.detail.job"]
            XCTAssertTrue(temperatures.waitForExistence(timeout: 5))
            XCTAssertTrue(job.exists)
            if expectedLayout == "printer.detail.columns" {
                XCTAssertGreaterThan(temperatures.frame.minX, job.frame.minX)
            } else {
                XCTAssertGreaterThan(temperatures.frame.minY, job.frame.minY)
            }

            XCTAssertFalse(app.buttons["printer.detail.control.emergencyStop"].exists)
            let screenshot = XCTAttachment(screenshot: XCUIScreen.main.screenshot())
            screenshot.name = "Essential Status \(orientation == .portrait ? "portrait" : "landscape")"
            screenshot.lifetime = .keepAlways
            add(screenshot)
        }
    }

    func testAccessibilityTextKeepsReadingColumnAndLabeledEmergencyOnBothPages() {
        app.terminate()
        app.launchArguments.removeAll {
            $0 == "-UIPreferredContentSizeCategoryName" || $0 == "UICTContentSizeCategoryL"
        }
        app.launchArguments += [
            "--uitesting-issue3259-visual-acceptance",
            "-UIPreferredContentSizeCategoryName", "UICTContentSizeCategoryAccessibilityXXXL",
            "-pf_theme_mode", "dark"
        ]
        app.launchForPrintFarmerUITest()
        openFirstPrinterDetail()
        XCTAssertTrue(app.otherElements["printer.detail.readingColumn"].waitForExistence(timeout: 8))
        let identity = app.descendants(matching: .any)["printer.detail.identity"]
        XCTAssertTrue(identity.waitForExistence(timeout: 5))
        let printerName = identity.descendants(matching: .any).matching(
            NSPredicate(format: "identifier BEGINSWITH %@", "printer.detail.destination.")
        ).firstMatch
        XCTAssertTrue(printerName.exists)
        XCTAssertGreaterThanOrEqual(identity.frame.height, printerName.frame.height)
        XCTAssertGreaterThanOrEqual(printerName.frame.minY, identity.frame.minY)
        XCTAssertLessThanOrEqual(printerName.frame.maxY, identity.frame.maxY)
        let selector = app.descendants(matching: .any)
            .matching(identifier: "printer.detail.panel.selector").firstMatch
        XCTAssertTrue(selector.exists)
        for title in ["Status", "Control", "Filament", "Queue"] {
            XCTAssertTrue(selector.buttons[title].isHittable)
            selector.buttons[title].tap()
            XCTAssertTrue(selector.buttons[title].isSelected)
            let page = app.descendants(matching: .any)["printer.detail.panel.\(title.lowercased())"]
            XCTAssertTrue(page.exists)
            XCTAssertGreaterThanOrEqual(page.frame.height, 100, "The pinned header must leave a usable page viewport")
            if title == "Status" {
                let temperatures = app.otherElements["printer.detail.temperatures"]
                let beforeScroll = temperatures.frame.minY
                let statusContent = app.descendants(matching: .any)
                    .matching(identifier: "printer.detail.status.content").firstMatch
                XCTAssertTrue(statusContent.exists)
                app.swipeUp()
                XCTAssertLessThan(temperatures.frame.minY, beforeScroll, "The reading column must actually scroll")
            }
            let emergency = app.buttons["printer.detail.control.emergencyStop"]
            if title == "Control" {
                XCTAssertTrue(emergency.isHittable)
                XCTAssertGreaterThanOrEqual(emergency.frame.height, 44)
                XCTAssertGreaterThanOrEqual(emergency.frame.width, 44)
                XCTAssertEqual(emergency.label, "Emergency stop printer")
            } else {
                XCTAssertFalse(emergency.exists)
            }
            let screenshot = XCTAttachment(screenshot: XCUIScreen.main.screenshot())
            screenshot.name = "Essential \(title) accessibility text"
            screenshot.lifetime = .keepAlways
            add(screenshot)
        }
    }

    func testDefaultEntryLandsOnStatusPage() {
        openFirstPrinterDetail()

        let overviewPage = app.descendants(matching: .any)["printer.detail.panel.status"]
        XCTAssertTrue(
            overviewPage.waitForExistence(timeout: 8),
            "Printer detail must default to the Status page"
        )
    }

    func testControlsRemainDiscoverableWithExplanationAndExistingSettingsPathWhileDisabled() {
        openFirstPrinterDetail()

        XCTAssertTrue(
            app.descendants(matching: .any)["printer.detail.panel.status"]
                .waitForExistence(timeout: 8),
            "Printer detail must render the Status page"
        )
        XCTAssertTrue(
            panelSelector().exists,
            "Both destinations remain discoverable while the safety preference is off"
        )
        panelSelector().buttons["Control"].tap()
        XCTAssertTrue(
            app.descendants(matching: .any)["printer.detail.control.unavailable"]
                .waitForExistence(timeout: 5)
        )
        let settings = app.buttons["printer.detail.control.settings"]
        XCTAssertTrue(settings.waitForExistence(timeout: 5))
        settings.tap()
        XCTAssertTrue(app.navigationBars["Settings"].waitForExistence(timeout: 5))
        let toggle = app.switches["settings.advancedPrinterControls"]
        if !toggle.exists { app.swipeUp() }
        XCTAssertTrue(
            toggle.waitForExistence(timeout: 5),
            "Settings must expose the Advanced Printer Controls safety toggle"
        )
        if !toggle.isHittable { app.swipeUp() }
        XCTAssertEqual(toggle.value as? String, "0", "Discovering Controls must never enable the safety preference")
    }

    // MARK: - Selector reachability once Controls is available

    func testUnsettledControlsContextCannotExposeMaterialActuationAndKeepsEmergencyIndependent() {
        enableAdvancedPrinterControls()
        openFirstPrinterDetail()
        panelSelector().buttons["Control"].tap()
        XCTAssertTrue(app.staticTexts[
            "Controls require a settled registered server connection. Reopen this printer after reconnecting."
        ].waitForExistence(timeout: 8))
        XCTAssertFalse(app.buttons["printer.controls.extrude"].exists,
                       "Demo targets and spool assignment cannot establish physical-control access")
        for operation in ["load", "unload", "change"] {
            let action = app.buttons["printer.controls.filament-\(operation)"]
            XCTAssertFalse(action.exists, "An unsettled control composition must not expose physical actuation")
        }
        XCTAssertFalse(app.buttons["printer.controls.calibration-start"].exists)
        let emergency = app.buttons["printer.detail.control.emergencyStop"]
        XCTAssertTrue(emergency.isHittable)
        XCTAssertTrue(emergency.isEnabled)
        XCTAssertGreaterThanOrEqual(emergency.frame.height, 44)
        emergency.tap()
        XCTAssertTrue(app.alerts.firstMatch.waitForExistence(timeout: 3))
        app.alerts.firstMatch.buttons["Cancel"].tap()
        let evidence = XCTAttachment(screenshot: XCUIScreen.main.screenshot())
        evidence.name = "Unsettled material context blocked; independent confirmed emergency"
        evidence.lifetime = .keepAlways
        add(evidence)
    }

    func testSelectorTapSwitchesToControlsPageAndBackToStatus() {
        openFirstPrinterDetail()

        let selector = panelSelector()
        XCTAssertTrue(
            selector.waitForExistence(timeout: 8),
            "The panel selector must remain discoverable while Controls authorization is gated"
        )

        let controlsSegment = selector.buttons["Control"]
        XCTAssertTrue(
            controlsSegment.waitForExistence(timeout: 3),
            "Selector must expose a Control segment once Advanced Printer Controls is enabled"
        )
        controlsSegment.tap()

        XCTAssertTrue(
            app.descendants(matching: .any)["printer.detail.panel.control"]
                .waitForExistence(timeout: 8),
            "Tapping the Control segment must reveal the Controls page"
        )

        let statusSegment = selector.buttons["Status"]
        XCTAssertTrue(
            statusSegment.waitForExistence(timeout: 3),
            "Selector must expose a Status segment"
        )
        statusSegment.tap()

        XCTAssertTrue(
            app.descendants(matching: .any)["printer.detail.panel.status"]
                .waitForExistence(timeout: 8),
            "Tapping the Status segment must return to the Status page"
        )
    }

    func testFilamentChangeOpensMaterialAndSpoolSelection() {
        openFirstPrinterDetail()

        let selector = panelSelector()
        XCTAssertTrue(selector.waitForExistence(timeout: 8))
        selector.buttons["Filament"].tap()
        XCTAssertTrue(
            app.descendants(matching: .any)["printer.detail.panel.filament"]
                .waitForExistence(timeout: 8)
        )

        let changeSpool = app.buttons.matching(
            NSPredicate(format: "identifier CONTAINS %@", "/printer/change")
        ).firstMatch
        XCTAssertTrue(changeSpool.waitForExistence(timeout: 5))
        changeSpool.tap()

        XCTAssertTrue(app.navigationBars["Select Material"].waitForExistence(timeout: 5))
        app.buttons["spoolPicker.material.PLA"].tap()
        XCTAssertTrue(app.navigationBars["Select Spool"].waitForExistence(timeout: 5))

        let availableSpool = app.buttons["spoolPicker.spool.2"]
        XCTAssertTrue(availableSpool.waitForExistence(timeout: 5))
        availableSpool.tap()

        XCTAssertTrue(
            app.descendants(matching: .any)["printer.detail.panel.filament"]
                .waitForExistence(timeout: 5),
            "Selecting a spool must return to the printer's Filament page"
        )
    }

    func testRunActionBarStaysReachableAcrossPanelSwitch() {
        enableAdvancedPrinterControls()
        openFirstPrinterDetail()

        let selector = panelSelector()
        XCTAssertTrue(
            selector.waitForExistence(timeout: 8),
            "Panel selector must appear once Advanced Printer Controls is enabled for an online printer"
        )

        // Emergency Stop remains available for any online printer, but follows
        // the Control page content so it stays at the bottom of the composition.
        // Queried by its own stable identifier (Hicks review finding 22):
        // `PrinterRunActionBar`'s container previously combined
        // `.accessibilityIdentifier(...)` with `.accessibilityElement
        // (children: .contain)` in the wrong order, which made every
        // descendant button report the CONTAINER's identifier instead of
        // its own; fixed at the source by reordering those two modifiers
        // (`.contain` first, then the container's own identifier) so child
        // identifiers are reachable again.
        XCTAssertFalse(app.buttons["printer.detail.control.emergencyStop"].exists)

        let controlsSegment = selector.buttons["Control"]
        XCTAssertTrue(
            controlsSegment.waitForExistence(timeout: 3),
            "Selector must expose a Control segment once Advanced Printer Controls is enabled"
        )
        controlsSegment.tap()

        XCTAssertTrue(
            app.buttons["printer.detail.control.emergencyStop"].waitForExistence(timeout: 8),
            "Emergency Stop remains available at the bottom of Controls"
        )
        let emergency = app.buttons["printer.detail.control.emergencyStop"]
        XCTAssertGreaterThanOrEqual(emergency.frame.height, 44)
        XCTAssertGreaterThanOrEqual(emergency.frame.width, 44)
        XCTAssertGreaterThan(emergency.frame.minY, selector.frame.maxY)
        let controlPage = app.descendants(matching: .any)["printer.detail.panel.control"]
        for _ in 0..<5 where !emergency.isHittable {
            controlPage.swipeUp()
        }
        XCTAssertTrue(emergency.isHittable, "Emergency Stop must be reachable at the bottom of Control.")
        app.buttons["printer.detail.control.emergencyStop"].tap()
        XCTAssertTrue(app.alerts.firstMatch.waitForExistence(timeout: 3))
        app.alerts.firstMatch.buttons["Cancel"].tap()
    }

    // MARK: - Native horizontal swipe (Hicks review finding 11)

    func testSwipingTraversesAllFourPagesWithoutStatusMotionBadges() {
        openFirstPrinterDetail()
        let selector = panelSelector()
        XCTAssertTrue(selector.waitForExistence(timeout: 8))
        for (current, next) in [("status", "Control"), ("control", "Filament"), ("filament", "Queue")] {
            let page = app.descendants(matching: .any)["printer.detail.panel.\(current)"]
            XCTAssertTrue(page.waitForExistence(timeout: 5))
            if current == "status" {
                XCTAssertFalse(app.staticTexts["Homed axes"].exists)
                XCTAssertFalse(app.staticTexts["Not homed"].exists)
            }
            page.swipeLeft()
            XCTAssertTrue(app.descendants(matching: .any)["printer.detail.panel.\(next.lowercased())"]
                .waitForExistence(timeout: 5))
            XCTAssertTrue(selector.buttons[next].isSelected)
        }
        for (current, next) in [("queue", "Filament"), ("filament", "Control"), ("control", "Status")] {
            app.descendants(matching: .any)["printer.detail.panel.\(current)"].swipeRight()
            XCTAssertTrue(app.descendants(matching: .any)["printer.detail.panel.\(next.lowercased())"]
                .waitForExistence(timeout: 5))
            XCTAssertTrue(selector.buttons[next].isSelected)
        }
    }

    func testSwipeLeftToControlsPageSyncsSelectorAndExcludesStatusFromAccessibility() {
        enableAdvancedPrinterControls()
        openFirstPrinterDetail()

        let selector = panelSelector()
        XCTAssertTrue(
            selector.waitForExistence(timeout: 8),
            "Panel selector must appear once Advanced Printer Controls is enabled for an online printer"
        )

        let overviewPage = app.descendants(matching: .any)["printer.detail.panel.status"]
        XCTAssertTrue(overviewPage.waitForExistence(timeout: 8), "Must start on the Status page")

        // A native horizontal swipe — not a selector tap — must move the
        // pager exactly like tapping the Control segment does.
        overviewPage.swipeLeft()

        XCTAssertTrue(
            app.descendants(matching: .any)["printer.detail.panel.control"]
                .waitForExistence(timeout: 8),
            "Swiping left over the Status page must reveal the Controls page"
        )
        XCTAssertTrue(
            selector.buttons["Control"].isSelected,
            "The selector must sync to Controls after a native swipe, not just after a segment tap"
        )
        XCTAssertFalse(
            app.descendants(matching: .any)["printer.detail.panel.status"].exists,
            "The inactive Status page must be excluded from the accessibility tree (accessibilityHidden), not merely scrolled off"
        )
    }

    func testSwipeRightBackToStatusPageSyncsSelectorAndExcludesControlsFromAccessibility() {
        enableAdvancedPrinterControls()
        openFirstPrinterDetail()

        let selector = panelSelector()
        XCTAssertTrue(
            selector.waitForExistence(timeout: 8),
            "Panel selector must appear once Advanced Printer Controls is enabled for an online printer"
        )

        // Reach Controls first via the selector (already covered by
        // testSelectorTapSwitchesToControlsPageAndBackToStatus), then swipe
        // back natively so this test isolates the swipe-back behavior.
        let controlsSegment = selector.buttons["Control"]
        XCTAssertTrue(controlsSegment.waitForExistence(timeout: 3))
        controlsSegment.tap()

        let controlsPage = app.descendants(matching: .any)["printer.detail.panel.control"]
        XCTAssertTrue(controlsPage.waitForExistence(timeout: 8), "Must reach the Controls page before swiping back")

        controlsPage.swipeRight()

        XCTAssertTrue(
            app.descendants(matching: .any)["printer.detail.panel.status"]
                .waitForExistence(timeout: 8),
            "Swiping right over the Controls page must return to the Status page"
        )
        XCTAssertTrue(
            selector.buttons["Status"].isSelected,
            "The selector must sync back to Status after a native swipe, not just after a segment tap"
        )
        XCTAssertFalse(
            app.descendants(matching: .any)["printer.detail.panel.control"].exists,
            "The inactive Controls page must be excluded from the accessibility tree (accessibilityHidden) once swiped away from"
        )
    }
}

#if DEBUG
@MainActor
final class Issue3259MockupNormalUITests: PrinterDetailPanelsUITests.Issue3259MockupCaptureUITests {
    func testCaptureApprovedScreensAtNormalTextSize() {
        captureApprovedMockupScreens()
    }

    func testRetryFailedQueueJobUsesAuthenticatedRerunEndpoint() {
        let queue = shellDestinationButton(tabIdentifier: "tab.queue", timeout: 8)
        XCTAssertTrue(queue.waitForExistence(timeout: 8))
        queue.tap()
        let queueList = app.collectionViews["jobList.combined.list"]
        XCTAssertTrue(queueList.waitForExistence(timeout: 8))

        let retryButton = queueList.buttons["job.retry.30000000-0003-0000-0000-000000000009"]
        for _ in 0..<8 where !retryButton.isHittable {
            queueList.swipeUp()
        }
        XCTAssertTrue(retryButton.waitForExistence(timeout: 5))
        XCTAssertTrue(retryButton.isEnabled)
        retryButton.tap()

        let rerunRow = app.descendants(matching: .any)
            .matching(identifier: "job.row.32590000-0000-0000-0000-000000000104")
            .firstMatch
        for _ in 0..<12 where !rerunRow.isHittable {
            queueList.swipeDown()
        }
        XCTAssertTrue(
            rerunRow.waitForExistence(timeout: 8),
            "A successful authenticated rerun should add the new copy to the queue."
        )
    }
}

@MainActor
class Issue3259ControlIdleUITests: PrinterDetailPanelsUITests.Issue3259MockupCaptureUITests {
    override var additionalLaunchArguments: [String] {
        super.additionalLaunchArguments + ["--uitesting-issue3259-control-idle"]
    }

    func testCompactHeatStepperRequiresApplyAndUsesAuthenticatedAPI() {
        let farm = shellDestinationButton(tabIdentifier: "tab.farm", timeout: 8)
        XCTAssertTrue(farm.waitForExistence(timeout: 8))
        farm.tap()
        let printerCard = app.buttons["farm-card-10000000-0001-0000-0000-000000000001"]
        XCTAssertTrue(printerCard.waitForExistence(timeout: 8))
        printerCard.tap()

        let selector = app.descendants(matching: .any)["printer.detail.panel.selector"]
        XCTAssertTrue(selector.waitForExistence(timeout: 8))
        selector.buttons["Control"].tap()
        let increase = app.buttons["printer.controls.hotend.increase"]
        XCTAssertTrue(increase.waitForExistence(timeout: 8))
        increase.tap()

        let apply = app.buttons["printer.controls.heat.set-targets"]
        XCTAssertTrue(apply.waitForExistence(timeout: 5))
        apply.tap()
        let accepted = app.staticTexts.matching(
            NSPredicate(
                format: "label == %@",
                "Request accepted; waiting for matching telemetry. This does not confirm physical completion."
            )
        ).firstMatch
        XCTAssertTrue(
            accepted.waitForExistence(timeout: 8),
            "The authenticated temperature API must accept the guarded stepper draft without claiming physical completion."
        )
    }

    func testCaptureApprovedIdleControlScreen() {
        let isIPad = UIDevice.current.userInterfaceIdiom == .pad
        if isIPad {
            XCUIDevice.shared.orientation = .landscapeLeft
        }
        defer {
            if isIPad {
                XCUIDevice.shared.orientation = .portrait
            }
        }

        let farm = shellDestinationButton(tabIdentifier: "tab.farm", timeout: 8)
        XCTAssertTrue(farm.waitForExistence(timeout: 8))
        farm.tap()
        let printerCard = app.buttons["farm-card-10000000-0001-0000-0000-000000000002"]
        for _ in 0..<8 where !printerCard.exists {
            app.swipeUp()
        }
        XCTAssertTrue(printerCard.waitForExistence(timeout: 8))
        printerCard.tap()
        let selector = app.descendants(matching: .any)["printer.detail.panel.selector"]
        XCTAssertTrue(selector.waitForExistence(timeout: 8))
        selector.buttons["Control"].tap()
        XCTAssertTrue(app.descendants(matching: .any)["printer.detail.panel.control"].waitForExistence(timeout: 8))
        let controlPage = app.descendants(matching: .any)["printer.detail.panel.control"]
        XCTAssertFalse(
            app.descendants(matching: .any)["navigation.tabBar"].exists,
            "Printer detail should not retain the shell tab bar."
        )
        XCTAssertFalse(
            app.descendants(matching: .any)["printer.controls.temperatures"].exists,
            "The detail Control page must not repeat temperatures in a separate strip."
        )
        XCTAssertTrue(
            app.buttons["printer.detail.control.emergencyStop"].exists,
            "Emergency Stop must stay visible while the Control page is selected."
        )
        let emergency = app.buttons["printer.detail.control.emergencyStop"]
        XCTAssertGreaterThan(
            emergency.frame.midY,
            controlPage.frame.midY,
            "For an idle printer, the full-width Emergency Stop belongs at the bottom of Control."
        )
        XCTAssertGreaterThanOrEqual(
            emergency.frame.width,
            controlPage.frame.width * 0.85,
            "Idle Emergency Stop must fill the Control page width rather than use the compact active-print placement."
        )
        XCTAssertTrue(app.otherElements["printer.controls.motion-group"].waitForExistence(timeout: 5))
        XCTAssertTrue(app.otherElements["printer.controls.runtime"].exists)
        XCTAssertTrue(app.staticTexts["printer.controls.hotend.measured"].exists)
        XCTAssertEqual(app.staticTexts["printer.controls.hotend.measured"].label, "now 24°")
        XCTAssertEqual(app.staticTexts["printer.controls.bed.measured"].label, "now 23°")
        XCTAssertTrue(app.buttons["printer.controls.hotend.increase"].exists)
        XCTAssertTrue(app.buttons["printer.controls.hotend.increase"].isEnabled)
        XCTAssertTrue(app.buttons["printer.controls.runtime.fan.increase"].isEnabled)
        XCTAssertTrue(app.buttons["printer.controls.runtime.z-offset.increase"].isEnabled)
        let device = isIPad ? "iPad" : "iPhone"
        let size = contentSizeCategory == "UICTContentSizeCategoryL" ? "normal" : "largest"
        attachScreen("\(device)-\(size)-printer-control-idle")

        let motion = app.otherElements["printer.controls.motion-group"]
        for _ in 0..<4 where !motion.isHittable {
            controlPage.swipeUp()
        }
        XCTAssertTrue(motion.isHittable, "Idle motion controls must be visible in the approved Control composition.")
        attachScreen("\(device)-\(size)-printer-control-idle-motion")

        let runtime = app.otherElements["printer.controls.runtime"]
        for _ in 0..<4 where !runtime.isHittable {
            controlPage.swipeUp()
        }
        XCTAssertTrue(runtime.isHittable, "Fan and Z-offset controls must be visibly reachable.")
        attachScreen("\(device)-\(size)-printer-control-idle-runtime")

        selector.buttons["Filament"].tap()
        let filamentPage = app.descendants(matching: .any)["printer.detail.panel.filament"]
        XCTAssertTrue(filamentPage.waitForExistence(timeout: 5))
        let physicalLoad = app.buttons["printer.detail.filament.load"]
        let physicalUnload = app.buttons["printer.detail.filament.unload"]
        XCTAssertTrue(physicalLoad.waitForExistence(timeout: 5))
        XCTAssertTrue(physicalUnload.waitForExistence(timeout: 5))
        XCTAssertFalse(physicalLoad.isEnabled, "Room-temperature measured telemetry must not authorize physical filament load.")
        XCTAssertFalse(physicalUnload.isEnabled, "Room-temperature measured telemetry must not authorize physical filament unload.")
        XCTAssertTrue(
            app.staticTexts.matching(
                NSPredicate(format: "label CONTAINS %@", "required minimum of 220")
            ).firstMatch.exists,
            "The unavailable state must explain the live 220 °C safety floor, not claim the backend lacks support."
        )

        selector.buttons["Queue"].tap()
        let queue = app.descendants(matching: .any)["printer.detail.panel.queue"]
        XCTAssertTrue(queue.waitForExistence(timeout: 5))
        XCTAssertFalse(
            queue.staticTexts["Assigned"].exists,
            "Assigned jobs must not appear as a peer section ahead of Up next."
        )
        XCTAssertTrue(queue.staticTexts["Up next"].exists)
        XCTAssertTrue(queue.staticTexts["Then"].exists)
        XCTAssertTrue(queue.staticTexts["Coral spool bracket.gcode"].exists)
        XCTAssertFalse(
            queue.descendants(matching: .any).matching(
                NSPredicate(format: "label CONTAINS[c] %@", "benchy_0.2mm_PLA.gcode")
            ).firstMatch.exists,
            "An idle printer must not show the hidden active-print job from the shared demo fixture."
        )
        let startNext = app.buttons["printer.detail.queue.dispatch.32590000-0000-0000-0000-000000000021"]
        XCTAssertTrue(startNext.waitForExistence(timeout: 5))
        for _ in 0..<4 where !startNext.isHittable {
            queue.swipeUp()
        }
        XCTAssertTrue(startNext.isEnabled, "The idle printer's authenticated Start next action must be enabled.")
        XCTAssertTrue(startNext.isHittable, "The enabled Start next action must be visible in the idle Queue capture.")
        attachScreen("\(device)-\(size)-printer-queue-idle")
    }

    func testSafeIdlePrinterDispatchesAuthenticatedFilamentLoadAndUnload() {
        relaunchAppForTest(additionalArguments: [
            "--uitesting-issue3259-control-idle",
            "--uitesting-issue3259-safe-filament-actions"
        ])
        let farm = shellDestinationButton(tabIdentifier: "tab.farm", timeout: 8)
        XCTAssertTrue(farm.waitForExistence(timeout: 8))
        farm.tap()
        let printerCard = app.buttons["farm-card-10000000-0001-0000-0000-000000000001"]
        XCTAssertTrue(printerCard.waitForExistence(timeout: 8))
        printerCard.tap()

        let selector = app.descendants(matching: .any)["printer.detail.panel.selector"]
        XCTAssertTrue(selector.waitForExistence(timeout: 8))
        selector.buttons["Filament"].tap()

        let load = app.buttons["printer.detail.filament.load"]
        let unload = app.buttons["printer.detail.filament.unload"]
        XCTAssertTrue(load.waitForExistence(timeout: 8))
        XCTAssertTrue(unload.waitForExistence(timeout: 8))
        let loadEnabled = XCTNSPredicateExpectation(
            predicate: NSPredicate(format: "isEnabled == true"),
            object: load
        )
        let enabledResult = XCTWaiter().wait(for: [loadEnabled], timeout: 8)
        let visibleText = app.staticTexts.allElementsBoundByIndex.map(\.label).joined(separator: " | ")
        XCTAssertEqual(enabledResult, .completed, "Verified backend support and fresh 220 °C telemetry authorize Load. UI: \(visibleText)")
        XCTAssertTrue(unload.isEnabled, "Verified backend support and fresh 220 °C telemetry authorize Unload. UI: \(visibleText)")

        load.tap()
        let loadConfirmation = app.buttons["Load"]
        XCTAssertTrue(loadConfirmation.waitForExistence(timeout: 5))
        loadConfirmation.tap()
        XCTAssertFalse(app.alerts["Action Failed"].waitForExistence(timeout: 2))

        unload.tap()
        let unloadConfirmation = app.buttons["Unload"]
        XCTAssertTrue(unloadConfirmation.waitForExistence(timeout: 5))
        unloadConfirmation.tap()
        XCTAssertFalse(app.alerts["Action Failed"].waitForExistence(timeout: 2))
    }
}

@MainActor
final class Issue3259ControlIdleAccessibilityUITests: Issue3259ControlIdleUITests {
    override var contentSizeCategory: String {
        "UICTContentSizeCategoryAccessibilityXXXL"
    }
}

@MainActor
final class Issue3259MockupAccessibilityUITests: PrinterDetailPanelsUITests.Issue3259MockupCaptureUITests {
    override var contentSizeCategory: String {
        "UICTContentSizeCategoryAccessibilityXXXL"
    }

    func testCaptureApprovedScreensAtLargestTextSize() {
        captureApprovedMockupScreens()
    }
}
#endif
