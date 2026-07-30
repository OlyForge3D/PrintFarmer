import XCTest

/// #788 F8-M2 — real-device XCUI proof that actionable checklist rows hand off
/// to the exact stable-ID destination on BOTH iPhone (tab bar) and iPad
/// (sidebar). The routing scenario seeds harvest / filament-runout /
/// maintenance rows that all share the SAME display name ("Handle the plate")
/// but target different real demo entities, so a green run proves routing keys
/// off stable IDs and typed metadata — never the title.
///
/// The suite is device-agnostic: the same code runs against whichever
/// simulator `xcodebuild` targets, and `ShiftTasksUITestBase.openTasksDestination`
/// adapts to the tab bar (compact) or sidebar (regular) automatically, giving
/// iPhone + iPad parity from one implementation.
@MainActor
final class TaskActionRoutingUITests: ShiftTasksUITestBase {
    // Stable task IDs seeded by `DemoShiftTaskService.routingTasks`.
    private let harvestTaskID = "78810000-0000-0000-0000-000000000001"
    private let swapTaskID = "78810000-0000-0000-0000-000000000002"
    private let maintenanceTaskID = "78810000-0000-0000-0000-000000000003"

    // Stable destination entity IDs (real demo entities).
    private let swapPrinterID = "10000000-0001-0000-0000-000000000003"

    override var additionalLaunchArguments: [String] {
        ["--uitesting-task-action-routing"]
    }

    /// HarvestReady row → shipped #714 harvest destination for the exact job.
    func testHarvestRowRoutesToExactStableHarvestDestination() {
        openTasksDestination()

        let openButton = app.buttons["shiftTasks.action.open.\(harvestTaskID)"]
        XCTAssertTrue(
            openButton.waitForExistence(timeout: 8),
            "The harvest row's primary action must be present"
        )
        openButton.tap()

        let destination = app.descendants(matching: .any)[
            "shiftTasks.destination.harvest.\(harvestTaskID)"
        ]
        XCTAssertTrue(
            destination.waitForExistence(timeout: 8),
            "HarvestReady must open the exact harvest destination for its stable job ID"
        )
        XCTAssertTrue(
            destination.isHittable,
            "The harvest destination must be the frontmost, focusable surface"
        )

        // A duplicate display name must NEVER misroute to the other rows'
        // destinations.
        XCTAssertFalse(
            app.descendants(matching: .any)[
                "shiftTasks.destination.maintenance.\(maintenanceTaskID)"
            ].exists
        )
        XCTAssertFalse(
            app.descendants(matching: .any)[
                "printer.detail.root.\(swapPrinterID)"
            ].exists
        )
    }

    /// FilamentRunout row → shipped mobile #710 guided-swap destination
    /// (the target printer's detail) for the exact printer.
    func testRunoutRowRoutesToExactStableSwapDestination() {
        openTasksDestination()

        let openButton = app.buttons["shiftTasks.action.open.\(swapTaskID)"]
        XCTAssertTrue(
            openButton.waitForExistence(timeout: 8),
            "The runout row's primary action must be present"
        )
        openButton.tap()

        let destination = app.descendants(matching: .any)[
            "printer.detail.root.\(swapPrinterID)"
        ]
        XCTAssertTrue(
            destination.waitForExistence(timeout: 8),
            "FilamentRunout must route to the exact printer's detail (guided swap)"
        )
        XCTAssertTrue(
            destination.isHittable,
            "The swap destination must be the frontmost, focusable surface"
        )

        // Reached via the printer stack, not a harvest/maintenance sheet.
        XCTAssertFalse(
            app.descendants(matching: .any)[
                "shiftTasks.destination.harvest.\(harvestTaskID)"
            ].exists
        )
        XCTAssertFalse(
            app.descendants(matching: .any)[
                "shiftTasks.destination.maintenance.\(maintenanceTaskID)"
            ].exists
        )
    }

    /// MaintenanceInIdleWindow row → shipped maintenance ack/log destination
    /// for the exact printer/component context.
    func testMaintenanceRowRoutesToExactStableMaintenanceDestination() {
        openTasksDestination()

        let openButton = app.buttons["shiftTasks.action.open.\(maintenanceTaskID)"]
        XCTAssertTrue(
            openButton.waitForExistence(timeout: 8),
            "The maintenance row's primary action must be present"
        )
        openButton.tap()

        let destination = app.descendants(matching: .any)[
            "shiftTasks.destination.maintenance.\(maintenanceTaskID)"
        ]
        XCTAssertTrue(
            destination.waitForExistence(timeout: 8),
            "MaintenanceInIdleWindow must open the exact maintenance destination"
        )
        XCTAssertTrue(
            destination.isHittable,
            "The maintenance destination must be the frontmost, focusable surface"
        )

        XCTAssertFalse(
            app.descendants(matching: .any)[
                "shiftTasks.destination.harvest.\(harvestTaskID)"
            ].exists
        )
        XCTAssertFalse(
            app.descendants(matching: .any)[
                "printer.detail.root.\(swapPrinterID)"
            ].exists
        )
    }
}
