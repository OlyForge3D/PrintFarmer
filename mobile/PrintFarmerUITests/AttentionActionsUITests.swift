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

    func testFailureMediaStableNavigationAndActionsAtLargeDynamicType() {
        let failureCard = element(
            "attention.item.\(failureID)",
            type: .any
        )
        XCTAssertTrue(
            failureCard.waitForExistence(timeout: 10),
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
        XCTAssertTrue(
            maintenanceCard.waitForExistence(timeout: 5),
            "A pending failure action must not wedge an unrelated card"
        )

        let acknowledge = element(
            "attention.item.\(maintenanceID).action.acknowledge",
            type: .button
        )
        reveal(acknowledge)
        XCTAssertEqual(acknowledge.label, "Acknowledge")
        acknowledge.tap()
        XCTAssertTrue(
            maintenanceCard.waitForNonExistence(timeout: 10),
            "The non-failure action must dispatch and refresh independently"
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

        let retry = element(
            "attention.item.\(failureID).action.retry",
            type: .button
        )
        reveal(retry)
        XCTAssertEqual(retry.label, "Retry Resume")
        retry.tap()
        XCTAssertTrue(
            failureCard.waitForNonExistence(timeout: 10),
            "Successful retry must disappear only after canonical refresh"
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
        for _ in 0..<8 where !element.exists || !element.isHittable {
            if element.exists, element.frame.midY < app.frame.midY {
                app.swipeDown()
            } else {
                app.swipeUp()
            }
        }
        XCTAssertTrue(
            element.waitForExistence(timeout: 5),
            "Required control '\(element.identifier)' does not exist",
            file: file,
            line: line
        )
        XCTAssertTrue(
            element.isHittable,
            "Required control '\(element.identifier)' is hidden or overlapped at accessibility XXXL",
            file: file,
            line: line
        )
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
