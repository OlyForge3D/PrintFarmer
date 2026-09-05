import XCTest

@MainActor
final class AttentionActionsUITests: PrintFarmerUITestCase {
    private let failureID =
        "failure:78000000-0000-0000-0000-000000000001"
    private let maintenanceID =
        "maintenance:78000000-0000-0000-0000-000000000002"
    private let unavailableMediaID =
        "failure:78000000-0000-0000-0000-000000000003"
    private let duplicatePrinterID =
        "10000000-0001-0000-0000-0000000000aa"
    private let jobID =
        "30000000-0003-0000-0000-000000000001"

    override var additionalLaunchArguments: [String] {
        [
            "--uitesting-attention-actions",
            "-UIPreferredContentSizeCategoryName",
            "UICTContentSizeCategoryAccessibilityExtraExtraExtraLarge",
        ]
    }

    func testRevealSearchesBothDirectionsAtLargeDynamicType() {
        let severity = element(
            "attention.item.\(failureID).severity",
            type: .any
        )
        XCTAssertTrue(severity.waitForExistence(timeout: 30))

        let retryMedia = element(
            "attention.item.\(unavailableMediaID).media.retry",
            type: .button
        )
        reveal(retryMedia)
        XCTAssertEqual(retryMedia.label, "Retry camera snapshot")

        reveal(severity)
        XCTAssertEqual(severity.label, "Severity, Critical")

        let acknowledge = element(
            "attention.item.\(maintenanceID).action.acknowledge",
            type: .button
        )
        reveal(acknowledge)
        XCTAssertEqual(acknowledge.label, "Acknowledge")
    }

    func testFailureMediaStableNavigationAndActionsAtLargeDynamicType() {
        let failureCard = element(
            "attention.item.\(failureID)",
            type: .any
        )
        XCTAssertTrue(
            failureCard.waitForExistence(timeout: 30),
            "The deterministic failure card must load on the Attention root"
        )

        let severity = element(
            "attention.item.\(failureID).severity",
            type: .any
        )
        XCTAssertTrue(severity.waitForExistence(timeout: 5))
        XCTAssertEqual(severity.label, "Severity, Critical")

        let summary = element(
            "attention.item.\(failureID).summary",
            type: .any
        )
        let deadline = element(
            "attention.item.\(failureID).deadline",
            type: .any
        )
        let snapshot = element(
            "attention.item.\(failureID).media.image",
            type: .image
        )
        XCTAssertTrue(summary.waitForExistence(timeout: 5))
        XCTAssertTrue(deadline.waitForExistence(timeout: 5))
        XCTAssertTrue(
            snapshot.waitForExistence(timeout: 10),
            "Failure media must load independently through the snapshot endpoint"
        )
        XCTAssertEqual(
            snapshot.label,
            "Failure camera snapshot for Prusa MK4 #1"
        )
        XCTAssertLessThan(severity.frame.minY, summary.frame.minY)
        XCTAssertLessThan(summary.frame.minY, deadline.frame.minY)
        XCTAssertLessThan(deadline.frame.minY, snapshot.frame.minY)

        let printerLink = element(
            "attention.item.\(failureID).navigation.printer",
            type: .button
        )
        reveal(printerLink)
        XCTAssertEqual(printerLink.label, "Open printer, Prusa MK4 #1")
        printerLink.tap()

        let printerDestination = element(
            "printer.detail.destination.\(duplicatePrinterID)",
            type: .any
        )
        XCTAssertTrue(
            printerDestination.waitForExistence(timeout: 10),
            "Duplicate names must still open the exact stable printer UUID"
        )
        navigateBack(expectedNavigationBar: "Printer")

        let jobLink = element(
            "attention.item.\(failureID).navigation.job",
            type: .button
        )
        reveal(jobLink)
        XCTAssertEqual(jobLink.label, "Open related job")
        jobLink.tap()

        let jobDestination = element(
            "job.detail.destination.\(jobID)",
            type: .any
        )
        XCTAssertTrue(
            jobDestination.waitForExistence(timeout: 10),
            "The related job destination must match the stable job UUID"
        )
        navigateBackToAttention()

        let resume = element(
            "attention.item.\(failureID).action.resume",
            type: .button
        )
        reveal(resume)
        XCTAssertEqual(resume.label, "Resume")
        resume.tap()

        let confirmResume = app.buttons["Confirm Resume"]
        XCTAssertTrue(
            confirmResume.waitForExistence(timeout: 5),
            "A confirmation-required resolution must be reachable on the second tap"
        )
        confirmResume.tap()

        let progress = element(
            "attention.item.\(failureID).action.progress",
            type: .any
        )
        XCTAssertTrue(
            progress.waitForExistence(timeout: 5),
            "The tapped failure action must expose item-scoped progress"
        )
        XCTAssertEqual(progress.label, "Resume in progress")
        XCTAssertLessThan(printerLink.frame.minY, resume.frame.minY)

        let maintenanceCard = element(
            "attention.item.\(maintenanceID)",
            type: .any
        )
        let acknowledge = element(
            "attention.item.\(maintenanceID).action.acknowledge",
            type: .button
        )
        reveal(acknowledge)
        XCTAssertTrue(
            maintenanceCard.waitForExistence(timeout: 5),
            "A pending failure action must not wedge an unrelated card"
        )
        XCTAssertEqual(acknowledge.label, "Acknowledge")
        acknowledge.tap()
        XCTAssertTrue(
            maintenanceCard.waitForNonExistence(timeout: 10),
            "The non-failure action must dispatch and refresh independently"
        )

        let retry = element(
            "attention.item.\(failureID).action.retry",
            type: .button
        )
        let actionError = element(
            "attention.item.\(failureID).action.error",
            type: .any
        )
        XCTAssertTrue(
            actionError.waitForExistence(timeout: 5),
            "The gated resume failure must surface on its own item"
        )
        XCTAssertTrue(
            actionError.label.contains(
                "The printer refused the first resume request."
            )
        )

        reveal(retry)
        XCTAssertEqual(retry.label, "Retry Resume")
        retry.tap()

        let refreshError = element(
            "attention.item.\(failureID).action.refreshError",
            type: .any
        )
        XCTAssertTrue(
            refreshError.waitForExistence(timeout: 10),
            "Successful mutation with failed canonical GET must expose refresh-only recovery"
        )
        XCTAssertTrue(
            refreshError.label.contains("Canonical attention refresh failed.")
        )
        XCTAssertTrue(
            failureCard.exists,
            "Stale card remains visible but non-actionable until canonical refresh applies"
        )
        XCTAssertFalse(
            resume.isEnabled,
            "Completed mutation must stay disabled while canonical refresh is pending"
        )

        let refreshRetry = element(
            "attention.item.\(failureID).action.refreshRetry",
            type: .button
        )
        reveal(refreshRetry)
        XCTAssertEqual(refreshRetry.label, "Retry attention refresh")
        refreshRetry.tap()
        XCTAssertTrue(
            failureCard.waitForNonExistence(timeout: 10),
            "Refresh-only retry must remove the stale card after canonical apply"
        )

        let unavailableMedia = element(
            "attention.item.\(unavailableMediaID).media.unavailable",
            type: .any
        )
        let retryMedia = element(
            "attention.item.\(unavailableMediaID).media.retry",
            type: .button
        )
        reveal(retryMedia)
        XCTAssertTrue(unavailableMedia.waitForExistence(timeout: 5))
        XCTAssertEqual(
            unavailableMedia.label,
            "Failure camera snapshot unavailable for Bambu X1C"
        )
        XCTAssertEqual(retryMedia.label, "Retry camera snapshot")

        let healthy = element("attention.healthy.summary", type: .button)
        XCTAssertTrue(
            healthy.waitForExistence(timeout: 5),
            "The healthy disclosure remains accessible after item resolution"
        )
        XCTAssertEqual(healthy.value as? String, "Collapsed")
    }

    private func element(
        _ identifier: String,
        type: XCUIElement.ElementType
    ) -> XCUIElement {
        app.descendants(matching: type)
            .matching(identifier: identifier)
            .firstMatch
    }

    private func reveal(
        _ element: XCUIElement,
        file: StaticString = #filePath,
        line: UInt = #line
    ) {
        var searchTowardTop = false
        var sweepLength = 4
        var stepsRemaining = sweepLength
        for _ in 0..<64 {
            let visibleFrame = visibleAttentionFrame
            let targetFrame = element.exists ? element.frame : nil
            // A thin strip above the tab bar can be hittable without a reliable tap target.
            if let targetFrame, !targetFrame.isEmpty,
               visibleFrame.contains(targetFrame), element.isHittable {
                break
            }

            let scrollDown: Bool
            let distance: CGFloat
            if let targetFrame, !targetFrame.isEmpty {
                scrollDown = targetFrame.midY < visibleFrame.midY
                // Travel across tall cards, then shorten the drag to align the target.
                distance = min(
                    max(abs(targetFrame.midY - visibleFrame.midY), 24),
                    visibleFrame.height * 0.7
                )
            } else {
                // Lazy rows have no AX frame until scrolled into view. Expanding
                // 4/8/16/32-drag sweeps search both sides of the starting viewport.
                scrollDown = searchTowardTop
                distance = visibleFrame.height * 0.7
                stepsRemaining -= 1
                if stepsRemaining == 0 {
                    searchTowardTop.toggle()
                    sweepLength *= 2
                    stepsRemaining = sweepLength
                }
            }
            let start = app.coordinate(withNormalizedOffset: .zero).withOffset(
                CGVector(
                    dx: visibleFrame.midX - app.frame.minX,
                    dy: visibleFrame.minY - app.frame.minY
                        + visibleFrame.height * (scrollDown ? 0.15 : 0.85)
                )
            )
            let end = start.withOffset(
                CGVector(dx: 0, dy: scrollDown ? distance : -distance)
            )
            start.press(
                forDuration: 0.1,
                thenDragTo: end,
                withVelocity: .slow,
                thenHoldForDuration: 0.1
            )
        }
        guard element.waitForExistence(timeout: 5) else {
            XCTFail(
                "Required attention control did not materialize within the scroll budget",
                file: file,
                line: line
            )
            return
        }
        XCTAssertTrue(
            element.isHittable,
            "Required control '\(element.identifier)' is hidden or overlapped at accessibility XXXL",
            file: file,
            line: line
        )
        XCTAssertTrue(
            visibleAttentionFrame.contains(element.frame),
            "Required control '\(element.identifier)' must be fully clear of navigation and tab bars",
            file: file,
            line: line
        )
    }

    private var visibleAttentionFrame: CGRect {
        var frame = app.collectionViews["attention.list"].frame.intersection(app.frame)
        let navigationBar = app.navigationBars["Attention"]
        if navigationBar.exists {
            let top = max(frame.minY, navigationBar.frame.maxY)
            frame = CGRect(x: frame.minX, y: top, width: frame.width, height: frame.maxY - top)
        }
        let tabBar = app.tabBars.firstMatch
        if tabBar.exists {
            frame.size.height = min(frame.maxY, tabBar.frame.minY) - frame.minY
        }
        return frame.insetBy(dx: 8, dy: 8)
    }

    private func navigateBack(
        expectedNavigationBar: String,
        file: StaticString = #filePath,
        line: UInt = #line
    ) {
        let bar = app.navigationBars[expectedNavigationBar]
        XCTAssertTrue(
            bar.waitForExistence(timeout: 5),
            "Expected \(expectedNavigationBar) destination navigation bar",
            file: file,
            line: line
        )
        let back = bar.buttons["Attention"]
        XCTAssertTrue(
            back.waitForExistence(timeout: 5),
            "The destination must expose a strict back path to Attention",
            file: file,
            line: line
        )
        back.tap()
        XCTAssertTrue(
            app.navigationBars["Attention"].waitForExistence(timeout: 5),
            "Back navigation did not return to Attention",
            file: file,
            line: line
        )
    }

    private func navigateBackToAttention(
        file: StaticString = #filePath,
        line: UInt = #line
    ) {
        let attentionBack = app.navigationBars.buttons["Attention"].firstMatch
        XCTAssertTrue(
            attentionBack.waitForExistence(timeout: 5),
            "Job detail must expose a back button to Attention",
            file: file,
            line: line
        )
        attentionBack.tap()
        XCTAssertTrue(
            app.navigationBars["Attention"].waitForExistence(timeout: 5),
            "Job back navigation did not return to Attention",
            file: file,
            line: line
        )
    }
}
