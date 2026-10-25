import XCTest
import UIKit

/// XCUI acceptance for the Status/Controls paging integration (issue #2522).
///
/// Runs against the deterministic `--uitesting` authenticated operator-shell
/// bootstrap (same as `OperatorShellUITests`) — no fake-service scenario
/// override is needed since these assertions only depend on demo fleet data.
///
/// Deterministic-test discipline: every wait is a bounded
/// `waitForExistence`; no `Thread.sleep`/`Task.sleep`/retry-until-pass. The
/// navigation-entry helper (`openFirstPrinterDetail`) asserts every REQUIRED
/// step of the deterministic bootstrap — the Farm tab and a printer card
/// always exist in this environment, so a missing one is a
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
        /// #3509 sorts the Farm by state group, then name, so at large text sizes
        /// the card can sit below the fold or under the Scan dock. tap() targets its
        /// centre, so filter the Farm by name and tap the card's top edge clear of the dock.
        func openFarmCard(_ printerID: String) {
            let farm = shellDestinationButton(tabIdentifier: "tab.farm", timeout: 8)
            XCTAssertTrue(farm.waitForExistence(timeout: 8))
            farm.tap()
            let printerCard = app.buttons["farm-card-\(printerID)"]
            // SwiftUI surfaces the Farm scroll view as farm.printerList on iPhone
            // and as farm.root inside the iPad split view.
            let farmScroll = app.scrollViews
                .matching(NSPredicate(format: "identifier IN %@", ["farm.printerList", "farm.root"]))
                .firstMatch
            XCTAssertTrue(farmScroll.waitForExistence(timeout: 8))
            // At accessibility sizes a card can be taller than the space above the
            // Scan dock and keyboard, so tap near its top edge rather than its centre.
            func tapPoint() -> CGPoint? {
                guard printerCard.exists, printerCard.isHittable else { return nil }
                let card = printerCard.frame
                let point = CGPoint(x: card.midX, y: card.minY + min(44, card.height / 2))
                var bottom = farmScroll.frame.maxY
                let scan = app.buttons["navigation.scan"]
                if scan.exists { bottom = min(bottom, scan.frame.minY - 8) }
                let keyboard = app.keyboards.firstMatch
                if keyboard.exists { bottom = min(bottom, keyboard.frame.minY - 8) }
                return farmScroll.frame.contains(point) && point.y < bottom ? point : nil
            }
            // Scrolling the Farm grid in the simulator can trap the main thread in a
            // LazyVGrid placement loop, so filter with Farm search instead of swiping.
            if tapPoint() == nil, let name = Self.fixturePrinterNames[printerID] {
                searchFarm(name)
                _ = printerCard.waitForExistence(timeout: 8)
            }
            guard let point = tapPoint() else {
                XCTFail("The top of the printer card must be on screen before tapping")
                return
            }
            app.coordinate(withNormalizedOffset: .zero)
                .withOffset(CGVector(dx: point.x, dy: point.y))
                .tap()
        }

        /// Filters the Farm by name. Swiping the Farm LazyVGrid in the simulator can
        /// trap the main thread in a placement loop, so tests search instead.
        /// Submitting dismisses the keyboard so it stops covering cards and the dock.
        func searchFarm(_ query: String) {
            let searchButton = app.buttons["farm.search"]
            let field = app.searchFields.firstMatch
            func presentField() {
                if !field.exists {
                    XCTAssertTrue(searchButton.waitForExistence(timeout: 8))
                    searchButton.tap()
                }
                XCTAssertTrue(field.waitForExistence(timeout: 8))
                field.tap()
            }
            presentField()
            // Tapping places the caret mid-text, so clear with the field's own button.
            let clear = field.buttons["Clear text"]
            if clear.exists {
                clear.tap()
                presentField()
            }
            let current = (field.value as? String) ?? ""
            XCTAssertTrue(
                current.isEmpty || current == field.placeholderValue,
                "Farm search must be empty before typing a new query, found '\(current)'."
            )
            field.typeText(query + "\n")
        }

        static let fixturePrinterNames = [
            "10000000-0001-0000-0000-000000000001": "Prusa MK4 #1",
            "10000000-0001-0000-0000-000000000002": "Prusa MK4 #2",
        ]

        override var waitsForNavigationReadiness: Bool { true }
        var contentSizeCategory: String { "UICTContentSizeCategoryL" }

        override var additionalLaunchArguments: [String] {
            [
                "--uitesting-issue3259-visual-acceptance",
                "-pf_theme_mode", "dark",
                "-UIPreferredContentSizeCategoryName", contentSizeCategory
            ]
        }

        @discardableResult
        private func assertAccountToolbar() -> XCUIElement {
            let account = app.descendants(matching: .any)["navigation.account"]
            XCTAssertTrue(account.waitForExistence(timeout: 5), "Every iPhone root tab must expose the compact account avatar.")
            XCTAssertTrue(account.isHittable, "The account avatar must remain reachable at the current text size.")
            return account
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
            if !isIPad {
                let accountButton = assertAccountToolbar()
                let farmSearchButton = app.buttons["farm.search"]
                XCTAssertTrue(farmSearchButton.isHittable)
                XCTAssertGreaterThan(
                    accountButton.frame.minX,
                    farmSearchButton.frame.maxX,
                    "The Farm search action and compact account avatar must not overlap."
                )
            }
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
            XCTAssertEqual(app.buttons["farm.filter.All"].label, "All 12")
            XCTAssertEqual(app.buttons["farm.filter.Printing"].label, "Printing 5")
            XCTAssertEqual(app.buttons["farm.filter.Needs attention"].label, "Needs attention 2")
            XCTAssertEqual(app.buttons["farm.filter.Idle"].label, "Idle 3")
            if isIPad {
                let farmCount = app.staticTexts["sidebar.count.farm"]
                XCTAssertTrue(farmCount.waitForExistence(timeout: 8))
                XCTAssertEqual(farmCount.label, "Farm count 12")
                let queueCount = app.staticTexts["sidebar.count.queue"]
                XCTAssertTrue(queueCount.waitForExistence(timeout: 8))
                // #3284 derives totalPrinting from the fleet: 4 queued + 5 printing.
                XCTAssertEqual(queueCount.label, "Queue count 9")
                XCTAssertTrue(app.staticTexts["sidebar.count.filament"].waitForExistence(timeout: 8))
                XCTAssertTrue(app.buttons["sidebar.settings"].exists)
            }
            attachScreen("\(device)-\(size)-farm")
            let printerList = app.descendants(matching: .any)["farm.printerList"]
            if !isIPad {
                XCTAssertTrue(printerList.exists, "Farm printer cards must remain in a scrollable list.")
            }
            // Swiping the Farm LazyVGrid in the simulator can trap the main thread in a
            // placement loop (it also fails on development), so locate each card with
            // Farm search instead. PrinterListViewModelTests covers the #3509 sort.
            func farmCard(_ name: String, _ detail: String? = nil) -> XCUIElement {
                searchFarm(name)
                let predicate = detail.map {
                    NSPredicate(format: "label CONTAINS %@ AND label CONTAINS %@", name, $0)
                } ?? NSPredicate(format: "label CONTAINS %@", name)
                let card = app.buttons.matching(predicate).firstMatch
                XCTAssertTrue(card.waitForExistence(timeout: 8), "\(name) must appear in the Farm.")
                return card
            }
            _ = farmCard("Ender 3 S1", "Paused")
            _ = farmCard("Prusa MK4 #1", "benchy_0.2mm_PLA.gcode")
            let miniCard = farmCard("Prusa Mini", "clip_holder_x4.gcode")
            XCTAssertTrue(miniCard.label.contains("22m left"))
            let bedClearPrinter = farmCard("Bambu X1C")
            XCTAssertTrue(
                bedClearPrinter.label.localizedCaseInsensitiveContains("Bed clear"),
                "Bambu X1C must use the mockup's real PendingReady demo state rather than appear as a second print."
            )
            XCTAssertFalse(bedClearPrinter.label.contains("% complete"))
            XCTAssertFalse(bedClearPrinter.label.contains("ETA unavailable"))
            let failureCard = farmCard("Voron 2.4", "Failure suspected:")
            XCTAssertTrue(failureCard.label.contains("gear_set_v3.gcode"))
            XCTAssertTrue(failureCard.label.contains("22% progress at failure"))
            XCTAssertTrue(failureCard.label.contains("2h 41m left"))
            XCTAssertTrue(failureCard.label.contains("Done"))
            XCTAssertTrue(failureCard.isHittable, "A Farm card must sit above floating navigation.")
            XCTAssertLessThanOrEqual(
                failureCard.frame.minY,
                scanButton.frame.minY - 8,
                "Farm cards must start above the floating Scan control."
            )
            let allFilterButton = app.buttons["farm.filter.All"]
            XCTAssertTrue(allFilterButton.waitForExistence(timeout: 8))
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
            if !isIPad { assertAccountToolbar() }
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
            for _ in 0..<12 where !queuedThumbnail.isHittable {
                queueList.swipeUp()
            }
            XCTAssertTrue(queuedThumbnail.waitForExistence(timeout: 8))
            XCTAssertEqual(queuedThumbnail.value as? String, "Thumbnail loaded")
            attachScreen("\(device)-\(size)-global-queue-queued-thumbnail")
            for section in ["queued", "recent-failures"] {
                let heading = queueList.descendants(matching: .any)["jobList.section.\(section)"]
                if section == "queued" {
                    for _ in 0..<16 where !heading.isHittable {
                        queueList.swipeDown()
                    }
                } else {
                    for _ in 0..<16 where !heading.isHittable {
                        queueList.swipeUp()
                    }
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
                    XCTAssertTrue(failedJob.label.localizedCaseInsensitiveContains("cable_chain"))
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

                }
                if section == "queued" {
                    let assignedSubheading = queueList.descendants(matching: .any)["jobList.assigned.subheading"]
                    XCTAssertFalse(
                        assignedSubheading.exists,
                        "Assigned jobs must not create a peer heading under Queued."
                    )
                    let queuedAssignments = [
                        ("32590000-0000-0000-0000-000000000101", "Unassigned"),
                        ("32590000-0000-0000-0000-000000000102", "Assigned to Prusa MK4 #2"),
                        ("32590000-0000-0000-0000-000000000103", "Assigned to Ender 3 S1"),
                        ("32590000-0000-0000-0000-000000000106", "Assigned to Voron 2.4")
                    ]
                    for (jobID, assignment) in queuedAssignments {
                        let row = queueList.buttons["job.row.\(jobID)"]
                        for _ in 0..<16 where !row.isHittable { queueList.swipeUp() }
                        XCTAssertTrue(row.waitForExistence(timeout: 5), "Queued fixture row \(jobID) must exist.")
                        XCTAssertTrue(
                            row.label.localizedCaseInsensitiveContains(assignment),
                            "Queued fixture row \(jobID) must state \(assignment), not imply compatibility. Found: \(row.label)"
                        )
                    }
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
            if !isIPad { assertAccountToolbar() }
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
            openFarmCard("10000000-0001-0000-0000-000000000001")
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
                    XCTAssertTrue(page.staticTexts["Up next"].waitForExistence(timeout: 5))
                    XCTAssertFalse(page.staticTexts["printer.detail.queue.reorderNote"].exists)
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
                    // The fixture omits layer counters (#3284), so the Layer cell is
                    // omitted rather than showing a "--" placeholder (#3496).
                    XCTAssertFalse(
                        app.staticTexts.matching(NSPredicate(format: "label BEGINSWITH %@", "Layer")).firstMatch.exists
                    )
                    XCTAssertFalse(
                        page.staticTexts.matching(NSPredicate(format: "label CONTAINS[c] %@", "Unavailable")).firstMatch.exists,
                        "Missing Status values use the -- placeholder, not Unavailable."
                    )
                    let preview = app.images["printer.detail.camera.media"]
                    XCTAssertTrue(preview.waitForExistence(timeout: 5))
                    XCTAssertEqual(preview.label, "Current print preview")
                    XCTAssertEqual(app.staticTexts["printer.detail.camera.title"].label, "Print preview")
                    if size == "largest" {
                        let pause = app.buttons["printer.detail.control.pause"]
                        let cancel = app.buttons["printer.detail.control.cancel"]
                        for _ in 0..<8 where !pause.isHittable || !cancel.isHittable {
                            swipeUp(on: page)
                        }
                        XCTAssertTrue(pause.isHittable, "Large text must let users scroll to Pause.")
                        XCTAssertTrue(cancel.isHittable, "Large text must let users scroll to Cancel.")
                        attachScreen("\(device)-largest-printer-status-actions")
                    }
                }
                if panel == "Control" {
                    XCTAssertTrue(
                        app.otherElements["printer.controls.runtime"].waitForExistence(timeout: 5),
                        "Control must open on Tune with the API-backed live Z-offset adjustment while printing."
                    )
                    XCTAssertFalse(app.buttons["printer.controls.segment.move"].isEnabled, "Move is unavailable while printing.")
                    XCTAssertFalse(app.buttons["printer.controls.segment.heat"].isEnabled, "Heat is unavailable while printing.")
                    XCTAssertTrue(
                        app.buttons["printer.controls.runtime.z-offset.increase"].exists,
                        "Live Z-offset adjustments must be driven by the authenticated backend-capability response."
                    )
                }
                if panel == "Filament" {
                    let warning = app.descendants(matching: .any)["printer.filament.attention"]
                    XCTAssertTrue(warning.waitForExistence(timeout: 5))
                    XCTAssertEqual(warning.label, "84 g / needs 140 g")
                    XCTAssertEqual(
                        app.descendants(matching: .any).matching(
                            // The combined filament row repeats its children for VoiceOver; count visible copies only.
                            NSPredicate(
                                format: "label CONTAINS %@ AND NOT (identifier BEGINSWITH %@)",
                                "needs 140 g", "printer.filament.row."
                            )
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
                    XCTAssertFalse(
                        app.staticTexts["printer.detail.filament.printingLockout"].exists,
                        "Dimmed buttons and their VoiceOver hint replace the lockout caption (#3496)."
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
            openFarmCard("10000000-0001-0000-0000-000000000001")

            let selector = app.descendants(matching: .any)["printer.detail.panel.selector"]
            XCTAssertTrue(selector.waitForExistence(timeout: 8))
            selector.buttons["Control"].tap()
            let tune = app.buttons["printer.controls.segment.tune"]
            XCTAssertTrue(tune.waitForExistence(timeout: 8))
            tune.tap()
            XCTAssertTrue(app.otherElements["printer.controls.runtime"].waitForExistence(timeout: 8))

            let zOffsetValue = app.staticTexts["printer.controls.runtime.z-offset.value"]
            XCTAssertTrue(zOffsetValue.waitForExistence(timeout: 5))
            XCTAssertEqual(zOffsetValue.label, "Z offset +0.025 mm")
            app.buttons["printer.controls.runtime.z-offset.increase"].tap()
            let updatedZOffset = app.staticTexts.matching(identifier: "printer.controls.runtime.z-offset.value")
                .matching(NSPredicate(format: "label == %@", "Z offset +0.030 mm")).firstMatch
            XCTAssertTrue(updatedZOffset.waitForExistence(timeout: 5), "Live Z-offset readback must reflect the accepted API command.")
            attachScreen("control-authenticated-command-readback")
        }

        func testSpeedAndFlowSteppersSitAboveBabystepAndApplyServerReadback() {
            openFarmCard("10000000-0001-0000-0000-000000000001")

            let selector = app.descendants(matching: .any)["printer.detail.panel.selector"]
            XCTAssertTrue(selector.waitForExistence(timeout: 8))
            selector.buttons["Control"].tap()
            let tune = app.buttons["printer.controls.segment.tune"]
            XCTAssertTrue(tune.waitForExistence(timeout: 8))
            tune.tap()

            let speedValue = app.staticTexts["printer.controls.runtime.speed-factor.value"]
            let flowValue = app.staticTexts["printer.controls.runtime.flow-factor.value"]
            XCTAssertTrue(speedValue.waitForExistence(timeout: 8))
            XCTAssertTrue(flowValue.waitForExistence(timeout: 5))
            XCTAssertEqual(speedValue.label, "Speed 100 percent")
            XCTAssertEqual(flowValue.label, "Flow 98 percent")
            let babystep = app.buttons["printer.controls.runtime.z-offset.increase"]
            XCTAssertTrue(babystep.waitForExistence(timeout: 5))
            XCTAssertLessThan(speedValue.frame.minY, flowValue.frame.minY, "Speed precedes Flow")
            XCTAssertLessThan(flowValue.frame.minY, babystep.frame.minY, "Factors sit above babystep")

            let speedIncrease = app.buttons["printer.controls.runtime.speed-factor.increase"]
            XCTAssertEqual(speedIncrease.label, "Increase speed by 5 percent")
            XCTAssertGreaterThanOrEqual(speedIncrease.frame.height, 44)
            speedIncrease.tap()
            let updatedSpeed = app.staticTexts.matching(identifier: "printer.controls.runtime.speed-factor.value")
                .matching(NSPredicate(format: "label == %@", "Speed 105 percent")).firstMatch
            XCTAssertTrue(updatedSpeed.waitForExistence(timeout: 5), "Speed must reflect accepted command readback")
            XCTAssertEqual(flowValue.label, "Flow 98 percent", "A speed step never changes flow")
            attachScreen("control-tune-speed-flow")
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
            let job = app.otherElements["printer.detail.job"]
            XCTAssertTrue(job.waitForExistence(timeout: 5))
            // Temperatures and sensor readouts live on Control, not Status.
            XCTAssertFalse(app.otherElements["printer.detail.temperatures"].exists)
            XCTAssertFalse(app.descendants(matching: .any)["printer.detail.sensors"].exists)

            XCTAssertFalse(app.buttons["printer.detail.control.emergencyStop"].exists)
            let window = app.windows.firstMatch
            let isLandscape = orientation.isLandscape
            let settled = NSPredicate { _, _ in (window.frame.width > window.frame.height) == isLandscape }
            wait(for: [XCTNSPredicateExpectation(predicate: settled, object: nil)], timeout: 8)
            // Redraw upright so viewers that ignore EXIF orientation show true landscape (#3496 N14).
            let raw = XCUIScreen.main.screenshot().image
            let upright = UIGraphicsImageRenderer(size: raw.size).image { _ in
                raw.draw(in: CGRect(origin: .zero, size: raw.size))
            }
            let screenshot = XCTAttachment(image: upright)
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
                // printer.detail.job propagates to several children; the reading
                // column is the single element that scrolls with the job block.
                let readingColumn = app.otherElements["printer.detail.readingColumn"]
                let beforeScroll = readingColumn.frame.minY
                let statusContent = app.descendants(matching: .any)
                    .matching(identifier: "printer.detail.status.content").firstMatch
                XCTAssertTrue(statusContent.exists)
                XCTAssertGreaterThanOrEqual(statusContent.frame.minY, page.frame.minY, "Status content starts inside the page")
                // Contract: the whole Status content either fits (regular-width
                // iPads at AX sizes) or scrolling brings its end into the page.
                // Clipped, unreachable content leaves maxY below the page and fails.
                var swipes = 0
                while statusContent.frame.maxY > page.frame.maxY + 1, swipes < 10 {
                    let before = readingColumn.frame.minY
                    page.swipeUp()
                    swipes += 1
                    if readingColumn.frame.minY >= before { break }
                }
                if swipes > 0 {
                    XCTAssertLessThan(readingColumn.frame.minY, beforeScroll, "The reading column must actually scroll")
                }
                XCTAssertLessThanOrEqual(
                    statusContent.frame.maxY, page.frame.maxY + 1,
                    "The end of the Status content must fit or be reachable by scrolling"
                )
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

    // MARK: - Selector reachability once Controls is available

    func testUnsettledControlsContextCannotExposeMaterialActuationAndKeepsEmergencyIndependent() {
        openFirstPrinterDetail()
        panelSelector().buttons["Control"].tap()
        // A short empty state replaces the technical explanation (#3496 N15).
        XCTAssertTrue(app.staticTexts["Reconnect to control"].waitForExistence(timeout: 8))
        XCTAssertTrue(app.buttons["printer.detail.control.reconnect"].isHittable)
        XCTAssertFalse(app.staticTexts[
            "Controls require a settled registered server connection. Reopen this printer after reconnecting."
        ].exists)
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
            "Selector must expose a Control segment for an online printer"
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
        openFirstPrinterDetail()

        let selector = panelSelector()
        XCTAssertTrue(
            selector.waitForExistence(timeout: 8),
            "Panel selector must appear for an online printer"
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
            "Selector must expose a Control segment for an online printer"
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
        openFirstPrinterDetail()

        let selector = panelSelector()
        XCTAssertTrue(
            selector.waitForExistence(timeout: 8),
            "Panel selector must appear for an online printer"
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
        openFirstPrinterDetail()

        let selector = panelSelector()
        XCTAssertTrue(
            selector.waitForExistence(timeout: 8),
            "Panel selector must appear for an online printer"
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

    /// The "Camera" header sits above the media, which spans the card width (#3496).
    func testCameraHeaderSitsAboveFullWidthMedia() {
        openFarmCard("10000000-0001-0000-0000-000000000001")

        let content = app.descendants(matching: .any)["printer.detail.status.content"]
        let title = app.staticTexts["printer.detail.camera.title"]
        let preview = app.images["printer.detail.camera.media"]
        XCTAssertTrue(content.waitForExistence(timeout: 8))
        XCTAssertTrue(title.waitForExistence(timeout: 8))
        XCTAssertTrue(preview.waitForExistence(timeout: 8))
        // Without a camera the hero shows the sliced-model preview (#3496 D3).
        XCTAssertEqual(title.label, "Print preview")
        XCTAssertEqual(preview.label, "Current print preview")
        XCTAssertLessThanOrEqual(title.frame.maxY, preview.frame.minY + 1, "The header row sits above the media")
        XCTAssertLessThan(title.frame.midX, preview.frame.midX, "The title sits at the top left")
        XCTAssertLessThanOrEqual(
            preview.frame.height, app.windows.firstMatch.frame.height * 0.6 + 1,
            "The media height is capped relative to the container (#3496 B01)"
        )
        attachScreen("status-print-preview-header-full-width-media")
    }

    /// Tune is selected mid-print; when the print ends Tune disables and
    /// Control falls back to Move (#3496).
    func testTuneSegmentFallsBackToMoveWhenPrintEnds() {
        openFarmCard("10000000-0001-0000-0000-000000000001")

        let selector = app.descendants(matching: .any)["printer.detail.panel.selector"]
        XCTAssertTrue(selector.waitForExistence(timeout: 8))
        selector.buttons["Control"].tap()
        let tune = app.buttons["printer.controls.segment.tune"]
        let move = app.buttons["printer.controls.segment.move"]
        XCTAssertTrue(tune.waitForExistence(timeout: 8))
        XCTAssertTrue(tune.isEnabled, "Tune is enabled while printing")
        XCTAssertTrue(tune.isSelected, "Control opens on Tune while printing")
        XCTAssertFalse(move.isEnabled)

        selector.buttons["Status"].tap()
        let page = app.descendants(matching: .any)["printer.detail.panel.status"]
        let cancel = app.buttons["printer.detail.control.cancel"]
        XCTAssertTrue(cancel.waitForExistence(timeout: 8))
        for _ in 0..<8 where !cancel.isHittable {
            page.swipeUp()
        }
        cancel.tap()
        let confirm = app.alerts.buttons["Confirm"]
        XCTAssertTrue(confirm.waitForExistence(timeout: 5))
        confirm.tap()

        selector.buttons["Control"].tap()
        let tuneDisabled = NSPredicate(format: "isEnabled == false")
        expectation(for: tuneDisabled, evaluatedWith: tune)
        waitForExpectations(timeout: 10)
        XCTAssertTrue(move.isEnabled)
        XCTAssertTrue(move.isSelected, "Tune falls back to Move when the print ends")
        XCTAssertFalse(app.otherElements["printer.controls.runtime"].exists)
        attachScreen("control-tune-fallback-after-print-ends")
    }

    func testRetryFailedQueueJobUsesAuthenticatedRerunEndpoint() {
        let queue = shellDestinationButton(tabIdentifier: "tab.queue", timeout: 8)
        XCTAssertTrue(queue.waitForExistence(timeout: 8))
        queue.tap()
        let queueList = app.descendants(matching: .any)["jobList.root"]
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

    /// Speed, Flow and babystep only apply to a running print, so the Tune
    /// segment is disabled while idle and Control stays on Move (#3496).
    override func testSpeedAndFlowSteppersSitAboveBabystepAndApplyServerReadback() {
        assertTuneSegmentDisabledWhileIdle()
        attachScreen("control-tune-segment-idle-disabled")
    }

    override func testRuntimeControlsUseAuthenticatedCommandsAndServerReadback() {
        assertTuneSegmentDisabledWhileIdle()
    }

    func testIdleStatusIgnoresStaleFileNameAndShowsNoActivePrint() {
        openFarmCard("10000000-0001-0000-0000-000000000001")

        let status = app.descendants(matching: .any)["printer.detail.panel.status"]
        XCTAssertTrue(status.waitForExistence(timeout: 8))
        XCTAssertTrue(app.staticTexts["No active print"].waitForExistence(timeout: 8))
        XCTAssertFalse(
            app.staticTexts.matching(NSPredicate(format: "label CONTAINS[c] %@", "gear_set")).firstMatch.exists,
            "A stale Moonraker file name must not render as the current job"
        )
        XCTAssertFalse(
            app.staticTexts.matching(NSPredicate(format: "label BEGINSWITH %@", "Layer")).firstMatch.exists
        )
        attachScreen("status-idle-no-active-print")
    }

    private func assertTuneSegmentDisabledWhileIdle() {
        openFarmCard("10000000-0001-0000-0000-000000000001")

        let selector = app.descendants(matching: .any)["printer.detail.panel.selector"]
        XCTAssertTrue(selector.waitForExistence(timeout: 8))
        selector.buttons["Control"].tap()
        let tune = app.buttons["printer.controls.segment.tune"]
        let move = app.buttons["printer.controls.segment.move"]
        XCTAssertTrue(tune.waitForExistence(timeout: 8))
        XCTAssertFalse(tune.isEnabled, "Tune is disabled while the printer is not printing")
        XCTAssertTrue(move.isEnabled)
        XCTAssertTrue(move.isSelected, "Control opens on Move while idle")
        tune.tap()
        XCTAssertTrue(move.isSelected, "A disabled Tune segment cannot be selected")
        XCTAssertFalse(app.otherElements["printer.controls.runtime"].exists)
        XCTAssertFalse(app.buttons["printer.controls.runtime.z-offset.increase"].exists)
        XCTAssertFalse(app.staticTexts["printer.controls.runtime.speed-factor.value"].exists)
        XCTAssertFalse(
            app.staticTexts.matching(NSPredicate(format: "label CONTAINS[c] %@", "while printing")).firstMatch.exists,
            "The disabled segment carries no visible explanation"
        )
    }

    func testCompactHeatStepperRequiresApplyAndUsesAuthenticatedAPI() {
        openFarmCard("10000000-0001-0000-0000-000000000001")

        let selector = app.descendants(matching: .any)["printer.detail.panel.selector"]
        XCTAssertTrue(selector.waitForExistence(timeout: 8))
        selector.buttons["Control"].tap()
        let heat = app.buttons["printer.controls.segment.heat"]
        XCTAssertTrue(heat.waitForExistence(timeout: 8))
        heat.tap()
        let increase = app.buttons["printer.controls.hotend.increase"]
        XCTAssertTrue(increase.waitForExistence(timeout: 8))
        increase.tap()

        let apply = app.buttons["printer.controls.heat.set-targets"]
        XCTAssertTrue(apply.waitForExistence(timeout: 5))
        apply.tap()
        // Success is silent (#3496): no progress or confirmation text, and no error banner.
        XCTAssertFalse(app.buttons["printer.controls.dismiss-error"].waitForExistence(timeout: 3))
        for phrase in ["Command pending", "Request accepted", "Matching telemetry", "Command accepted"] {
            XCTAssertFalse(
                app.staticTexts.matching(NSPredicate(format: "label CONTAINS %@", phrase)).firstMatch.exists,
                "\(phrase) must not be shown after a successful command"
            )
        }
    }

    func testHeatPresetRowOffersMoreMenuWhileIdle() {
        openFarmCard("10000000-0001-0000-0000-000000000002")
        let selector = app.descendants(matching: .any)["printer.detail.panel.selector"]
        XCTAssertTrue(selector.waitForExistence(timeout: 8))
        selector.buttons["Control"].tap()
        let heat = app.buttons["printer.controls.segment.heat"]
        XCTAssertTrue(heat.waitForExistence(timeout: 8))
        heat.tap()

        let slots = ["Preheat for PLA", "Preheat for PETG", "Preheat for ABS", "printer.controls.preheat.more", "Cool down"]
        for key in slots {
            let slot = app.buttons[key]
            XCTAssertTrue(slot.waitForExistence(timeout: 5), key)
            XCTAssertTrue(slot.isEnabled, key)
            XCTAssertGreaterThanOrEqual(slot.frame.height, 44, key)
        }
        // N-AX2: More shares its row with Cool down and must match its height.
        XCTAssertEqual(
            app.buttons["printer.controls.preheat.more"].frame.height,
            app.buttons["Cool down"].frame.height, accuracy: 1
        )
        let device = UIDevice.current.userInterfaceIdiom == .pad ? "iPad" : "iPhone"
        attachScreen("\(device)-heat-presets-idle")

        app.buttons["printer.controls.preheat.more"].tap()
        for material in ["asa", "tpu", "pc", "nylon"] {
            XCTAssertTrue(app.buttons["printer.controls.preheat.\(material)"].waitForExistence(timeout: 5), material)
        }
        attachScreen("\(device)-heat-presets-more-open")
        // The native menu doesn't scroll, so Custom… must be fully visible
        // without scrolling at every text size.
        let custom = app.buttons["printer.controls.preheat.custom"]
        XCTAssertTrue(custom.waitForExistence(timeout: 5), "Custom…")
        // The menu's open animation can briefly report its items as not hittable.
        let hittable = expectation(for: NSPredicate(format: "isHittable == true"), evaluatedWith: custom)
        wait(for: [hittable], timeout: 5)
        XCTAssertGreaterThanOrEqual(custom.frame.minY, app.windows.firstMatch.frame.minY, "Custom… top edge")
        XCTAssertLessThanOrEqual(
            custom.frame.maxY, app.buttons["printer.detail.control.emergencyStop"].frame.minY,
            "Custom… must sit fully above the Emergency Stop bar"
        )
        attachScreen("\(device)-heat-presets-more-custom")
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

        openFarmCard("10000000-0001-0000-0000-000000000002")
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
        XCTAssertTrue(app.otherElements["printer.controls.position"].waitForExistence(timeout: 5))
        XCTAssertTrue(app.otherElements["printer.controls.motion-group"].waitForExistence(timeout: 5))
        for identifier in ["home.all", "home.xy", "home.z", "jog.step.1", "jog.step.10", "jog.step.50"] {
            XCTAssertTrue(app.buttons["printer.controls.\(identifier)"].exists, identifier)
        }
        XCTAssertFalse(app.buttons["printer.controls.jog.step.0.1"].exists, "0.1 mm jogging is replaced by babystep.")
        XCTAssertFalse(app.buttons["printer.controls.disable-motors"].exists)
        XCTAssertFalse(app.buttons["printer.controls.calibration-start"].exists)
        let device = isIPad ? "iPad" : "iPhone"
        let size = contentSizeCategory == "UICTContentSizeCategoryL" ? "normal" : "largest"
        let motion = app.otherElements["printer.controls.motion-group"]
        for _ in 0..<4 where !motion.isHittable {
            controlPage.swipeUp()
        }
        XCTAssertTrue(motion.isHittable, "Idle motion controls must be visible in the approved Control composition.")
        attachScreen("\(device)-\(size)-printer-control-idle-motion")

        app.buttons["printer.controls.segment.heat"].tap()
        XCTAssertTrue(app.staticTexts["printer.controls.hotend.measured"].waitForExistence(timeout: 5))
        XCTAssertEqual(app.staticTexts["printer.controls.hotend.measured"].label, "Current 24 degrees")
        XCTAssertEqual(app.staticTexts["printer.controls.bed.measured"].label, "Current 23 degrees")
        XCTAssertTrue(app.buttons["printer.controls.hotend.increase"].exists)
        XCTAssertTrue(app.buttons["printer.controls.hotend.increase"].isEnabled)
        attachScreen("\(device)-\(size)-printer-control-idle")

        XCTAssertFalse(app.buttons["printer.controls.segment.tune"].isEnabled, "Tune is print-only (#3496)")

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
        XCTAssertTrue(queue.staticTexts["clip_holder ×4"].exists)
        XCTAssertTrue(queue.staticTexts["shelf_bracket ×6"].exists)
        XCTAssertFalse(queue.descendants(matching: .any)["printer.detail.queue.reorderNote"].exists)
        XCTAssertFalse(
            queue.descendants(matching: .any).matching(
                NSPredicate(format: "label CONTAINS[c] %@", "benchy_0.2mm_PLA.gcode")
            ).firstMatch.exists,
            "An idle printer must not show the hidden active-print job from the shared demo fixture."
        )
        let startNext = app.buttons["printer.detail.queue.dispatch.32590000-0000-0000-0000-000000000020"]
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
        openFarmCard("10000000-0001-0000-0000-000000000001")

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
final class Issue3484MissingRuntimeFactorsUITests: PrinterDetailPanelsUITests.Issue3259MockupCaptureUITests {
    /// Runs only the Speed/Flow override; the inherited Z-offset runtime test
    /// is already covered by the base capture classes.
    override class var defaultTestSuite: XCTestSuite {
        let suite = XCTestSuite(name: String(describing: self))
        for test in super.defaultTestSuite.tests
        where test.name.contains("testSpeedAndFlowSteppersSitAboveBabystepAndApplyServerReadback") {
            suite.addTest(test)
        }
        return suite
    }

    override var additionalLaunchArguments: [String] {
        super.additionalLaunchArguments + ["--uitesting-issue3484-missing-runtime-factors"]
    }

    /// Supported Speed/Flow without readback render the "--" placeholder that
    /// VoiceOver reads as "not reported", with disabled steppers.
    override func testSpeedAndFlowSteppersSitAboveBabystepAndApplyServerReadback() {
        openFarmCard("10000000-0001-0000-0000-000000000001")

        let selector = app.descendants(matching: .any)["printer.detail.panel.selector"]
        XCTAssertTrue(selector.waitForExistence(timeout: 8))
        selector.buttons["Control"].tap()
        let tune = app.buttons["printer.controls.segment.tune"]
        XCTAssertTrue(tune.waitForExistence(timeout: 8))
        tune.tap()

        for (id, title) in [("speed", "Speed"), ("flow", "Flow")] {
            let value = app.staticTexts["printer.controls.runtime.\(id)-factor.value"]
            XCTAssertTrue(value.waitForExistence(timeout: 8), "\(title) stays visible without readback")
            // Wait for the fixture status refresh; the row can briefly show an earlier snapshot.
            let notReported = XCTNSPredicateExpectation(
                predicate: NSPredicate(format: "label == %@", "\(title) not reported"),
                object: value
            )
            XCTAssertEqual(XCTWaiter().wait(for: [notReported], timeout: 8), .completed)
            XCTAssertEqual(value.label, "\(title) not reported")
            // The spoken label masks the Text; the UI-test harness mirrors the
            // rendered glyph into the accessibility value.
            XCTAssertEqual(value.value as? String, "--", "\(title) renders the placeholder, not a number")
            let row = XCTAttachment(screenshot: value.screenshot())
            row.name = "\(id)-factor-value-placeholder"
            row.lifetime = .keepAlways
            add(row)
            for direction in ["increase", "decrease"] {
                let button = app.buttons["printer.controls.runtime.\(id)-factor.\(direction)"]
                XCTAssertTrue(button.waitForExistence(timeout: 5))
                XCTAssertFalse(button.isEnabled, "\(title) \(direction) is disabled without readback")
            }
        }
        attachScreen("control-tune-speed-flow-not-reported")
    }
}

@MainActor
final class Issue3259ControlIdleAccessibilityUITests: Issue3259ControlIdleUITests {
    override var contentSizeCategory: String {
        "UICTContentSizeCategoryAccessibilityXXXL"
    }
}

/// Camera header layout with real camera media (#3496 D2): the title sits
/// top left, Rotate/Refresh top right, and the media spans the card below.
@MainActor
class Issue3496CameraLayoutUITests: PrintFarmerUITestCase {
    override var waitsForNavigationReadiness: Bool { true }
    var cameraLaunchArgument: String { "" }

    override var additionalLaunchArguments: [String] {
        [
            "--uitesting-issue3259-visual-acceptance",
            cameraLaunchArgument,
            "-pf_theme_mode", "dark",
            "-UIPreferredContentSizeCategoryName", "UICTContentSizeCategoryL"
        ]
    }

    func openPrimaryPrinterStatus() {
        let farm = shellDestinationButton(tabIdentifier: "tab.farm", timeout: 8)
        XCTAssertTrue(farm.waitForExistence(timeout: 8))
        farm.tap()
        let printerCard = app.buttons["farm-card-10000000-0001-0000-0000-000000000001"]
        XCTAssertTrue(printerCard.waitForExistence(timeout: 8))
        printerCard.tap()
        XCTAssertTrue(
            app.descendants(matching: .any)["printer.detail.status.content"].waitForExistence(timeout: 8)
        )
    }

    func assertHeaderAboveMedia(
        buttons identifiers: [String],
        name: String,
        file: StaticString = #filePath,
        line: UInt = #line
    ) {
        let title = app.staticTexts["printer.detail.camera.title"]
        let media = app.descendants(matching: .any)["printer.detail.camera.media"]
        XCTAssertTrue(title.waitForExistence(timeout: 8), file: file, line: line)
        XCTAssertTrue(media.waitForExistence(timeout: 8), file: file, line: line)
        XCTAssertEqual(title.label, "Camera", file: file, line: line)
        XCTAssertLessThanOrEqual(title.frame.maxY, media.frame.minY + 1, file: file, line: line)
        for identifier in identifiers {
            let button = app.buttons[identifier]
            XCTAssertTrue(button.waitForExistence(timeout: 8), "\(identifier) is missing", file: file, line: line)
            XCTAssertLessThanOrEqual(
                button.frame.maxY, media.frame.minY + 1,
                "\(identifier) sits in the header row above the media", file: file, line: line
            )
            XCTAssertGreaterThan(button.frame.minX, title.frame.maxX, "\(identifier) sits right of the title",
                                 file: file, line: line)
            XCTAssertGreaterThanOrEqual(button.frame.width, 44, file: file, line: line)
            XCTAssertGreaterThanOrEqual(button.frame.height, 44, file: file, line: line)
            XCTAssertFalse(button.label.isEmpty, "\(identifier) keeps its VoiceOver label", file: file, line: line)
        }
        let card = app.descendants(matching: .any)["printer.detail.hero"]
        if media.frame.width < card.frame.width - 2 {
            // The height cap binds (e.g. a quarter-turned 4:3 image), so the
            // media aspect-fits inside the cap and centres in the card.
            XCTAssertLessThan(media.frame.width, media.frame.height, "Only portrait media narrows below the card width",
                              file: file, line: line)
            XCTAssertEqual(media.frame.midX, card.frame.midX, accuracy: 2, "Capped media is centred",
                           file: file, line: line)
        }
        XCTAssertLessThanOrEqual(
            media.frame.height, app.windows.firstMatch.frame.height * 0.6 + 1,
            "Media height is capped relative to the container", file: file, line: line
        )
        let screenshot = XCTAttachment(screenshot: XCUIScreen.main.screenshot())
        screenshot.name = "Issue 3496 \(UIDevice.current.userInterfaceIdiom == .pad ? "iPad" : "iPhone")-\(name)"
        screenshot.lifetime = .keepAlways
        add(screenshot)
    }
}

@MainActor
final class Issue3496SnapshotCameraLayoutUITests: Issue3496CameraLayoutUITests {
    override var cameraLaunchArgument: String { "--uitesting-issue3496-camera-snapshot" }

    func testSnapshotCameraHeaderSitsAboveFullWidthMedia() {
        openPrimaryPrinterStatus()
        assertHeaderAboveMedia(
            buttons: ["printer.detail.camera.rotate", "printer.detail.camera.refresh"],
            name: "camera-snapshot"
        )
        // Same header in every mode: the badge is gone (#3496 N08).
        XCTAssertFalse(app.staticTexts["SNAPSHOT"].exists)
        XCTAssertFalse(app.staticTexts["LIVE"].exists)
        let named = XCTAttachment(screenshot: XCUIScreen.main.screenshot())
        named.name = "status-camera-header-full-width-media"
        named.lifetime = .keepAlways
        add(named)
        // A quarter turn keeps the header above capped, full-width media.
        app.buttons["printer.detail.camera.rotate"].tap()
        assertHeaderAboveMedia(
            buttons: ["printer.detail.camera.rotate", "printer.detail.camera.refresh"],
            name: "camera-snapshot-rotated-90"
        )
    }
}

@MainActor
final class Issue3496MjpegCameraLayoutUITests: Issue3496CameraLayoutUITests {
    override var cameraLaunchArgument: String { "--uitesting-issue3496-camera-mjpeg" }

    func testMjpegCameraHeaderSitsAboveFullWidthMedia() {
        openPrimaryPrinterStatus()
        // Live mode keeps the same header as snapshot mode (#3496 N05).
        assertHeaderAboveMedia(
            buttons: ["printer.detail.camera.rotate", "printer.detail.camera.refresh"],
            name: "camera-mjpeg"
        )
        let media = app.descendants(matching: .any)["printer.detail.camera.media"]
        let toggle = app.buttons["printer.detail.camera.livetoggle"]
        XCTAssertTrue(toggle.waitForExistence(timeout: 5))
        XCTAssertGreaterThanOrEqual(toggle.frame.minY, media.frame.minY - 1, "The mode toggle sits on the media, not in the header")
        XCTAssertTrue(
            app.descendants(matching: .any)["printer.detail.camera.stream.placeholder"].waitForExistence(timeout: 5),
            "An unreachable stream shows a compact placeholder rather than a tall blank panel"
        )
        XCTAssertLessThanOrEqual(media.frame.height, 160, "The placeholder stays compact")
        XCTAssertFalse(app.staticTexts["LIVE"].exists)
    }
}

/// A printing printer with a writable chamber heater and a read-only part fan (#3496 N16).
@MainActor
final class Issue3496TuneSensorsUITests: Issue3496CameraLayoutUITests {
    override var cameraLaunchArgument: String { "--uitesting-issue3496-tune-sensors" }

    func testTuneWhilePrintingShowsWritableSensorRows() {
        openPrimaryPrinterStatus()
        let selector = app.descendants(matching: .any)["printer.detail.panel.selector"]
        XCTAssertTrue(selector.waitForExistence(timeout: 8))
        selector.buttons["Control"].tap()
        let tune = app.buttons["printer.controls.segment.tune"]
        XCTAssertTrue(tune.waitForExistence(timeout: 8))
        XCTAssertTrue(tune.isEnabled, "Tune is available while printing")
        tune.tap()
        let id = "printer.controls.sensor.heater_generic chamber.TargetTemperature"
        let value = app.staticTexts["\(id).value"]
        let control = app.descendants(matching: .any)["printer.controls.runtime"]
        for _ in 0..<4 where !value.exists { control.swipeUp() }
        XCTAssertTrue(value.waitForExistence(timeout: 8), "The writable chamber heater row shows on Tune")
        XCTAssertTrue(app.buttons["\(id).increase"].exists)
        XCTAssertTrue(app.buttons["\(id).decrease"].exists)
        // Fans are read-only in the app, so the part fan gets no controls.
        XCTAssertFalse(app.descendants(matching: .any)["printer.controls.sensor.fan.FanSpeed.value"].exists)
        let screenshot = XCTAttachment(screenshot: XCUIScreen.main.screenshot())
        screenshot.name = "Issue 3496 \(UIDevice.current.userInterfaceIdiom == .pad ? "iPad" : "iPhone")-tune-printing-sensors"
        screenshot.lifetime = .keepAlways
        add(screenshot)
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

/// Production-mode acceptance against the isolated local API and real backend adapters.
/// This deliberately does not inherit PrintFarmerUITestCase, which launches the app with
/// `--uitesting` and substitutes the mock service container.
private struct LivePrinterHubUpdate {
    let target: String
    let payload: Data
}

@MainActor
final class PrinterSensorsLiveBackendUITests: XCTestCase {
    private struct Credentials {
        let username: String
        let password: String
        let apiURL: URL
        let controlURL: URL

        private struct FileConfiguration: Decodable {
            let username: String
            let password: String
            let apiURL: URL
            let controlURL: URL
        }

        init(fileURL: URL) throws {
            do {
                let configuration = try JSONDecoder().decode(
                    FileConfiguration.self,
                    from: Data(contentsOf: fileURL)
                )
                self.username = configuration.username
                self.password = configuration.password
                self.apiURL = configuration.apiURL
                self.controlURL = configuration.controlURL
            } catch {
                throw NSError(
                    domain: "PrinterSensorsLiveBackendUITests",
                    code: 1,
                    userInfo: [
                        NSLocalizedDescriptionKey:
                            "Run through the live sensor E2E harness; local test configuration could not be read."
                    ]
                )
            }
        }
    }

    private struct Printer: Decodable {
        let id: UUID
        let name: String
    }

    private struct LoginResult: Decodable {
        let token: String
    }

    func testLiveSensorsSignalRControlsAndReconnectAgainstBothBackends() async throws {
        let configurationURL = URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .appendingPathComponent("build/sensor-e2e-3419-config.json")
        let credentials = try Credentials(fileURL: configurationURL)
        let token = try await authenticate(credentials)
        let printers = try await loadPrinters(credentials, token: token)
        let testEmulator = try XCTUnwrap(printers.first { $0.name == "Test Printer Alpha" })
        let moonraker = try XCTUnwrap(printers.first { $0.name == "Moonraker Sensor E2E" })

        let app = XCUIApplication()
        app.launchArguments = [
            "-hasSeenOnboarding", "YES",
            "-hasCompletedNetworkPermission", "YES",
            "-isDemoModeActive", "NO",
        ]
        app.launchEnvironment["PRINTFARMER_API_URL"] = credentials.apiURL.absoluteString
        app.launch()
        defer { app.terminate() }
        try signIn(app, credentials: credentials)

        try await verifyBackend(
            printer: testEmulator,
            sensorID: "chamber",
            temperatureQuantity: "TargetTemperature",
            temperatureValue: 31,
            fanID: "fan_part",
            reconnectTemperatureValue: nil,
            app: app,
            credentials: credentials,
            token: token
        )
        try await verifyBackend(
            printer: moonraker,
            sensorID: "temperature_sensor endstop",
            temperatureQuantity: "Temperature",
            temperatureValue: 31.5,
            fanID: "fan_generic auxiliary",
            reconnectTemperatureValue: 39.2,
            app: app,
            credentials: credentials,
            token: token
        )
    }

    private func authenticate(_ credentials: Credentials) async throws -> String {
        var request = URLRequest(url: credentials.apiURL.appending(path: "/api/auth/login"))
        request.httpMethod = "POST"
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.httpBody = try JSONSerialization.data(withJSONObject: [
            "usernameOrEmail": credentials.username,
            "password": credentials.password,
        ])
        let (data, response) = try await URLSession.shared.data(for: request)
        try assertSuccess(response, data: data, operation: "API login")
        return try JSONDecoder().decode(LoginResult.self, from: data).token
    }

    private func loadPrinters(_ credentials: Credentials, token: String) async throws -> [Printer] {
        let data = try await apiRequest(
            credentials,
            token: token,
            path: "/api/printers",
            method: "GET"
        )
        return try JSONDecoder().decode([Printer].self, from: data)
    }

    private func signIn(_ app: XCUIApplication, credentials: Credentials) throws {
        let firstServerName = app.textFields["serverNameField"]
        guard firstServerName.waitForExistence(timeout: 15) else {
            throw NSError(
                domain: "PrinterSensorsLiveBackendUITests",
                code: 1,
                userInfo: [
                    NSLocalizedDescriptionKey:
                        "Expected Add Your First Server on the fresh isolated simulator; refusing to modify any pre-existing account or server."
                ]
            )
        }
        replaceText(in: firstServerName, with: "Sensor E2E Local API", clearingPlaceholder: true)
        let firstServerURL = app.textFields["serverURLField"]
        guard firstServerURL.waitForExistence(timeout: 5) else {
            throw NSError(
                domain: "PrinterSensorsLiveBackendUITests",
                code: 2,
                userInfo: [NSLocalizedDescriptionKey: "The first-server form did not expose its URL field."]
            )
        }
        replaceText(
            in: firstServerURL,
            with: credentials.apiURL.absoluteString,
            clearingPlaceholder: true
        )
        let saveServer = app.buttons["saveServerButton"]
        guard saveServer.waitForExistence(timeout: 5), saveServer.isEnabled else {
            throw NSError(
                domain: "PrinterSensorsLiveBackendUITests",
                code: 3,
                userInfo: [NSLocalizedDescriptionKey: "The local test API server could not be saved."]
            )
        }
        saveServer.tap()

        let loginButton = app.buttons["loginButton"]
        guard loginButton.waitForExistence(timeout: 15) else {
            throw NSError(
                domain: "PrinterSensorsLiveBackendUITests",
                code: 4,
                userInfo: [
                    NSLocalizedDescriptionKey:
                        "Saving the local test server did not expose the production login screen."
                ]
            )
        }

        let localServer = app.buttons.matching(
            NSPredicate(format: "label CONTAINS %@", credentials.apiURL.absoluteString)
        ).firstMatch
        guard localServer.waitForExistence(timeout: 5) else {
            throw NSError(
                domain: "PrinterSensorsLiveBackendUITests",
                code: 5,
                userInfo: [NSLocalizedDescriptionKey: "The login screen did not identify the isolated local API."]
            )
        }

        let username = app.textFields["usernameField"]
        let password = app.secureTextFields["passwordField"]
        XCTAssertTrue(username.waitForExistence(timeout: 5))
        username.tap()
        replaceText(in: username, with: credentials.username)
        password.tap()
        replaceText(in: password, with: credentials.password)
        loginButton.tap()
        guard app.buttons["tab.farm"].waitForExistence(timeout: 30) else {
            throw NSError(
                domain: "PrinterSensorsLiveBackendUITests",
                code: 6,
                userInfo: [NSLocalizedDescriptionKey: "The production app did not reach the farm after sign-in."]
            )
        }
        try dismissNotificationPermissionPromptIfNeeded()
    }

    private func dismissNotificationPermissionPromptIfNeeded() throws {
        let springboard = XCUIApplication(bundleIdentifier: "com.apple.springboard")
        let alert = springboard.alerts.firstMatch
        guard alert.waitForExistence(timeout: 3) else { return }
        let denyButton = alert.buttons.allElementsBoundByIndex.first {
            $0.label.replacingOccurrences(of: "’", with: "'")
                .localizedCaseInsensitiveContains("Don't Allow")
        }
        guard let denyButton else {
            throw NSError(
                domain: "PrinterSensorsLiveBackendUITests",
                code: 14,
                userInfo: [
                    NSLocalizedDescriptionKey:
                        "An unexpected system alert blocked the isolated production app."
                ]
            )
        }
        denyButton.tap()
    }

    private func verifyBackend(
        printer: Printer,
        sensorID: String,
        temperatureQuantity: String,
        temperatureValue: Double,
        fanID: String,
        reconnectTemperatureValue: Double?,
        app: XCUIApplication,
        credentials: Credentials,
        token: String
    ) async throws {
        let detailFarm = app.buttons["printer.detail.farm"]
        if detailFarm.exists {
            detailFarm.tap()
        } else {
            let farm = app.buttons["tab.farm"]
            guard farm.waitForExistence(timeout: 8) else {
                throw NSError(
                    domain: "PrinterSensorsLiveBackendUITests",
                    code: 19,
                    userInfo: [NSLocalizedDescriptionKey: "The Farm destination did not appear before selecting a printer."]
                )
            }
            farm.tap()
        }
        let card = app.buttons["farm-card-\(printer.id.uuidString)"]
        guard card.waitForExistence(timeout: 20) else {
            throw NSError(
                domain: "PrinterSensorsLiveBackendUITests",
                code: 16,
                userInfo: [NSLocalizedDescriptionKey: "\(printer.name) must appear in the live farm."]
            )
        }
        card.tap()
        // Status no longer renders sensors; writable ones are read back from
        // the Control tab's Tune row. Read-only sensors are verified via SignalR only.
        let selector = app.descendants(matching: .any)["printer.detail.panel.selector"]
        guard selector.waitForExistence(timeout: 8) else {
            throw NSError(
                domain: "PrinterSensorsLiveBackendUITests",
                code: 18,
                userInfo: [NSLocalizedDescriptionKey: "The printer detail panel selector did not appear."]
            )
        }
        selector.buttons["Control"].tap()
        let tune = app.buttons["printer.controls.segment.tune"]
        XCTAssertTrue(tune.waitForExistence(timeout: 12), "\(printer.name) must expose the Tune segment.")
        tune.tap()
        let sensorIsWritable = reconnectTemperatureValue == nil
        let status = app.descendants(matching: .any)[
            "printer.controls.sensor.\(sensorID).\(temperatureQuantity).value"
        ]
        let observer = try await SignalRPrinterUpdateObserver.connect(
            baseURL: credentials.apiURL,
            token: token,
            printerID: printer.id
        )
        defer { observer.close() }
        let baselineValue = temperatureValue - 1
        let previousUpdateCount = observer.updateCount
        if reconnectTemperatureValue != nil {
            _ = try await updateMoonrakerSensor(
                credentials,
                objectName: sensorID,
                temperature: baselineValue
            )
        } else {
            try await setSensorValue(
                credentials,
                token: token,
                printerID: printer.id,
                sensorID: sensorID,
                quantity: temperatureQuantity,
                value: baselineValue
            )
        }
        let initial = try XCTUnwrap(observer.waitForUpdate(after: previousUpdateCount, timeout: 30) { event in
            guard let payload = try? Self.jsonObject(event.payload),
                  let sensors = payload["sensors"] as? [[String: Any]] else { return false }
            return event.target == "printerupdated"
                && Self.readingValue(sensors, sensorID: sensorID, quantity: temperatureQuantity) == baselineValue
        })
        let baselinePayload = try jsonObject(initial.payload)
        if sensorIsWritable {
            guard status.waitForExistence(timeout: 20) else {
                throw NSError(
                    domain: "PrinterSensorsLiveBackendUITests",
                    code: 17,
                    userInfo: [NSLocalizedDescriptionKey: "\(printer.name) must show its live \(sensorID) sensor."]
                )
            }
            assertLabelContains(status, text: baselineValue.formatted(), timeout: 20)
        }

        let temperatureUpdateCount = observer.updateCount
        if reconnectTemperatureValue != nil {
            _ = try await updateMoonrakerSensor(credentials, objectName: sensorID, temperature: temperatureValue)
        } else {
            try await setSensorValue(
                credentials,
                token: token,
                printerID: printer.id,
                sensorID: sensorID,
                quantity: temperatureQuantity,
                value: temperatureValue
            )
        }

        let updatedEvent = try XCTUnwrap(observer.waitForUpdate(after: temperatureUpdateCount, timeout: 30) { event in
            guard let payload = try? Self.jsonObject(event.payload),
                  let sensors = payload["sensors"] as? [[String: Any]] else { return false }
            return event.target == "printerupdated"
                && Self.readingValue(sensors, sensorID: sensorID, quantity: temperatureQuantity) == temperatureValue
        })
        let updatedPayload = try jsonObject(updatedEvent.payload)
        XCTAssertEqual(updatedEvent.target, "printerupdated")
        XCTAssertEqual(
            try canonicalJSON(withoutSensorReadings(baselinePayload)),
            try canonicalJSON(withoutSensorReadings(updatedPayload)),
            "A sensor-only printerupdated must not alter non-sensor printer fields."
        )
        if sensorIsWritable {
            assertLabelContains(status, text: temperatureValue.formatted(), timeout: 20)
            attachScreenshot(named: "\(printer.name) live Tune sensor readout")
        }

        let updatedSensors = try XCTUnwrap(updatedPayload["sensors"] as? [[String: Any]])
        let fanValue = try XCTUnwrap(Self.readingValue(updatedSensors, sensorID: fanID, quantity: "FanSpeed"))
        let increase = app.buttons["printer.controls.sensor.\(fanID).FanSpeed.increase"]
        XCTAssertTrue(increase.waitForExistence(timeout: 12), "\(printer.name) must expose the writable fan control.")
        attachScreenshot(named: "\(printer.name) live fan control")
        let fanUpdateCount = observer.updateCount
        increase.tap()

        let expectedFanValue = fanValue + 1
        let fanEvent = try XCTUnwrap(observer.waitForUpdate(after: fanUpdateCount, timeout: 30) { event in
            guard let payload = try? Self.jsonObject(event.payload),
                  let sensors = payload["sensors"] as? [[String: Any]] else { return false }
            return event.target == "printerupdated"
                && Self.readingValue(sensors, sensorID: fanID, quantity: "FanSpeed") == expectedFanValue
        })
        let fanPayload = try jsonObject(fanEvent.payload)
        XCTAssertEqual(
            try canonicalJSON(withoutSensorReadings(updatedPayload)),
            try canonicalJSON(withoutSensorReadings(fanPayload)),
            "A fan-only printerupdated must not alter non-sensor printer fields."
        )
        let fanReadback = app.descendants(matching: .any)["printer.controls.sensor.\(fanID).FanSpeed.value"]
        assertLabelContains(fanReadback, text: expectedFanValue.formatted(), timeout: 20)

        var controlRequest = URLRequest(url: credentials.controlURL.appending(path: "/restart-api"))
        controlRequest.httpMethod = "POST"
        controlRequest.setValue("application/json", forHTTPHeaderField: "Content-Type")
        if let reconnectTemperatureValue {
            controlRequest.httpBody = try JSONSerialization.data(withJSONObject: [
                "moonrakerSensor": [
                    "objectName": sensorID,
                    "temperature": reconnectTemperatureValue,
                ],
            ])
        } else {
            controlRequest.httpBody = Data("{}".utf8)
        }
        let (controlData, controlResponse) = try await URLSession.shared.data(for: controlRequest)
        try assertSuccess(controlResponse, data: controlData, operation: "API reconnect/resync")
        let controlResult = try XCTUnwrap(try JSONSerialization.jsonObject(with: controlData) as? [String: Any])
        XCTAssertEqual(controlResult["restarted"] as? Bool, true)
        if reconnectTemperatureValue != nil {
            XCTAssertEqual(controlResult["moonrakerSensorUpdated"] as? Bool, true)
        }

        let reconnectObserver = try await SignalRPrinterUpdateObserver.connect(
            baseURL: credentials.apiURL,
            token: token,
            printerID: printer.id
        )
        defer { reconnectObserver.close() }
        let expectedReconnectSensorValue = reconnectTemperatureValue ?? 0
        let reconnectEvent = try XCTUnwrap(reconnectObserver.waitForUpdate(after: 0, timeout: 30) { event in
            guard let payload = try? Self.jsonObject(event.payload),
                  let sensors = payload["sensors"] as? [[String: Any]] else { return false }
            return event.target == "printerupdated"
                && Self.readingValue(sensors, sensorID: sensorID, quantity: temperatureQuantity)
                    == expectedReconnectSensorValue
        })
        let reconnectPayload = try jsonObject(reconnectEvent.payload)
        XCTAssertEqual(reconnectEvent.target, "printerupdated")
        XCTAssertFalse(
            try XCTUnwrap(reconnectPayload["sensors"] as? [[String: Any]]).isEmpty,
            "The backend must replay its live sensor snapshot after the API reconnects."
        )

        let connectionStatus = app.buttons["connection-status-bar"]
        assertLabelContains(connectionStatus, text: "Connected", timeout: 45)
        if sensorIsWritable {
            assertLabelContains(status, text: ", 0", timeout: 30)
        }
        attachScreenshot(named: "\(printer.name) reconnect resync")
    }

    private func updateMoonrakerSensor(
        _ credentials: Credentials,
        objectName: String,
        temperature: Double
    ) async throws -> [String: Any] {
        var request = URLRequest(url: credentials.controlURL.appending(path: "/moonraker-sensor"))
        request.httpMethod = "POST"
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.httpBody = try JSONSerialization.data(withJSONObject: [
            "objectName": objectName,
            "temperature": temperature,
        ])
        let (data, response) = try await URLSession.shared.data(for: request)
        try assertSuccess(response, data: data, operation: "Moonraker sensor mutation")
        return try XCTUnwrap(try JSONSerialization.jsonObject(with: data) as? [String: Any])
    }

    private func setSensorValue(
        _ credentials: Credentials,
        token: String,
        printerID: UUID,
        sensorID: String,
        quantity: String,
        value: Double
    ) async throws {
        let body: [String: Any] = ["quantity": quantity, "value": value]
        let path = "/api/printers/\(printerID)/sensors/\(sensorID.addingPercentEncoding(withAllowedCharacters: .urlPathAllowed) ?? sensorID)"
        _ = try await apiRequest(credentials, token: token, path: path, method: "POST", body: body)
    }

    private func assertLabelContains(_ element: XCUIElement, text: String, timeout: TimeInterval) {
        let expectation = XCTNSPredicateExpectation(
            predicate: NSPredicate(format: "label CONTAINS %@", text),
            object: element
        )
        XCTAssertEqual(XCTWaiter().wait(for: [expectation], timeout: timeout), .completed)
    }

    private func attachScreenshot(named name: String) {
        let screenshot = XCTAttachment(screenshot: XCUIScreen.main.screenshot())
        screenshot.name = name
        screenshot.lifetime = .keepAlways
        add(screenshot)
    }

    private func apiRequest(
        _ credentials: Credentials,
        token: String,
        path: String,
        method: String,
        body: [String: Any]? = nil
    ) async throws -> Data {
        var request = URLRequest(url: credentials.apiURL.appending(path: path))
        request.httpMethod = method
        request.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")
        if let body {
            request.setValue("application/json", forHTTPHeaderField: "Content-Type")
            request.httpBody = try JSONSerialization.data(withJSONObject: body)
        }
        let (data, response) = try await URLSession.shared.data(for: request)
        try assertSuccess(response, data: data, operation: "\(method) \(path)")
        return data
    }

    private func replaceText(in field: XCUIElement, with value: String, clearingPlaceholder: Bool = false) {
        if clearingPlaceholder {
            field.coordinate(withNormalizedOffset: CGVector(dx: 0.98, dy: 0.5)).tap()
        }
        if let current = field.value as? String,
           current != field.placeholderValue || clearingPlaceholder {
            field.typeText(
                String(
                    repeating: XCUIKeyboardKey.delete.rawValue,
                    count: current.count + 10
                )
            )
        }
        field.typeText(value)
    }

    private func assertSuccess(_ response: URLResponse, data: Data, operation: String) throws {
        let httpResponse = try XCTUnwrap(response as? HTTPURLResponse, "\(operation) must return an HTTP response.")
        guard (200..<300).contains(httpResponse.statusCode) else {
            let responseBody = String(data: data, encoding: .utf8) ?? "<non-UTF8 response body>"
            throw NSError(
                domain: "PrinterSensorsLiveBackendUITests",
                code: httpResponse.statusCode,
                userInfo: [
                    NSLocalizedDescriptionKey:
                        "\(operation) returned HTTP \(httpResponse.statusCode): \(responseBody)"
                ]
            )
        }
    }

    private func jsonObject(_ data: Data) throws -> [String: Any] {
        try XCTUnwrap(try JSONSerialization.jsonObject(with: data) as? [String: Any])
    }

    private func withoutSensorReadings(_ payload: [String: Any]) -> [String: Any] {
        payload.filter { !["sensors", "updatedAt", "timestamp", "serverTime"].contains($0.key) }
    }

    private func canonicalJSON(_ value: Any) throws -> Data {
        try JSONSerialization.data(withJSONObject: value, options: [.sortedKeys, .fragmentsAllowed])
    }

    nonisolated private static func jsonObject(_ data: Data) throws -> [String: Any] {
        try XCTUnwrap(try JSONSerialization.jsonObject(with: data) as? [String: Any])
    }

    nonisolated private static func readingValue(
        _ sensors: [[String: Any]],
        sensorID: String,
        quantity: String
    ) -> Double? {
        guard let sensor = sensors.first(where: { $0["id"] as? String == sensorID }),
              let readings = sensor["readings"] as? [[String: Any]],
              let reading = readings.first(where: { $0["quantity"] as? String == quantity }) else {
            return nil
        }
        if let value = reading["value"] as? NSNumber {
            return value.doubleValue
        }
        if let value = reading["value"] as? [String: Any] {
            return (value["number"] as? NSNumber)?.doubleValue
        }
        return nil
    }
}

private final class SignalRPrinterUpdateObserver: @unchecked Sendable {
    private let condition = NSCondition()
    private let socket: URLSessionWebSocketTask
    private var receiveTask: Task<Void, Never>?
    private var updates: [LivePrinterHubUpdate] = []

    private init(socket: URLSessionWebSocketTask) {
        self.socket = socket
    }

    var updateCount: Int {
        condition.lock()
        defer { condition.unlock() }
        return updates.count
    }

    static func connect(baseURL: URL, token: String, printerID: UUID) async throws -> SignalRPrinterUpdateObserver {
        var negotiateURL = baseURL.appending(path: "/hubs/printers/negotiate")
        var components = URLComponents(url: negotiateURL, resolvingAgainstBaseURL: false)!
        components.queryItems = [URLQueryItem(name: "negotiateVersion", value: "1")]
        negotiateURL = try XCTUnwrap(components.url)
        var request = URLRequest(url: negotiateURL)
        request.httpMethod = "POST"
        request.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")
        let (data, response) = try await URLSession.shared.data(for: request)
        guard let http = response as? HTTPURLResponse, (200..<300).contains(http.statusCode) else {
            throw NSError(domain: "SignalRPrinterUpdateObserver", code: 2, userInfo: [
                NSLocalizedDescriptionKey: "SignalR negotiate did not return a successful HTTP response."
            ])
        }
        let negotiated = try JSONDecoder().decode(NegotiateResponse.self, from: data)
        var socketComponents = URLComponents(url: baseURL, resolvingAgainstBaseURL: false)!
        socketComponents.scheme = socketComponents.scheme == "https" ? "wss" : "ws"
        socketComponents.path = "/hubs/printers"
        socketComponents.queryItems = [URLQueryItem(name: "id", value: negotiated.connectionToken)]
        var socketRequest = URLRequest(url: try XCTUnwrap(socketComponents.url))
        socketRequest.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")
        let socket = URLSession.shared.webSocketTask(with: socketRequest)
        socket.resume()
        try await socket.send(.string("{\"protocol\":\"json\",\"version\":1}\u{001e}"))
        _ = try await socket.receive()
        let observer = SignalRPrinterUpdateObserver(socket: socket)
        let subscription = try JSONSerialization.data(withJSONObject: [
            "type": 1,
            "invocationId": "sensor-e2e-subscribe",
            "target": "SubscribeToPrinterAsync",
            "arguments": [printerID.uuidString],
        ])
        try await socket.send(.string(String(decoding: subscription, as: UTF8.self) + "\u{001e}"))
        observer.receiveTask = Task.detached { [weak observer] in
            await observer?.receiveUpdates()
        }
        return observer
    }

    func waitForUpdate(
        after index: Int,
        timeout: TimeInterval,
        matching predicate: (LivePrinterHubUpdate) -> Bool
    ) -> LivePrinterHubUpdate? {
        condition.lock()
        defer { condition.unlock() }
        let deadline = Date().addingTimeInterval(timeout)
        while true {
            if updates.count > index,
               let update = updates[index...].first(where: predicate) {
                return update
            }
            guard condition.wait(until: deadline) else { return nil }
        }
    }

    func close() {
        receiveTask?.cancel()
        socket.cancel(with: .normalClosure, reason: nil)
    }

    private func receiveUpdates() async {
        while !Task.isCancelled {
            do {
                let message = try await socket.receive()
                let text: String
                switch message {
                case .string(let value): text = value
                case .data(let data): text = String(decoding: data, as: UTF8.self)
                @unknown default: continue
                }
                for frame in text.split(separator: "\u{001e}") {
                    guard let data = String(frame).data(using: .utf8),
                          let message = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
                          message["target"] as? String == "printerupdated",
                          let arguments = message["arguments"] as? [Any],
                          let payload = arguments.first,
                          let payloadData = try? JSONSerialization.data(withJSONObject: payload) else {
                        continue
                    }
                    append(.init(target: "printerupdated", payload: payloadData))
                }
            } catch {
                return
            }
        }
    }

    private func append(_ update: LivePrinterHubUpdate) {
        condition.lock()
        defer { condition.unlock() }
        updates.append(update)
        condition.broadcast()
    }

    private struct NegotiateResponse: Decodable {
        let connectionToken: String
    }
}
