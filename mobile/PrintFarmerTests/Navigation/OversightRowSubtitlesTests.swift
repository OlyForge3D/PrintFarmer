import XCTest
@testable import PrintFarmer

@MainActor
final class OversightRowSubtitlesTests: XCTestCase {

    // MARK: - Unknown context falls back to the destination description

    func testUnknownContextFallsBackToDestinationSubtitleForEveryDestination() {
        let context = OversightRowSubtitleContext.unknown
        for destination in OversightDestination.allCases {
            XCTAssertEqual(
                OversightRowSubtitles.subtitle(for: destination, in: context),
                destination.subtitle,
                "Destination \(destination.rawValue) should describe itself when no signal has loaded."
            )
        }
    }

    // MARK: - Maintenance (Upkeep row)

    func testMaintenanceLoadedButEmptyReportsNoWorkDue() {
        var context = OversightRowSubtitleContext.unknown
        context.upcomingMaintenance = []

        XCTAssertEqual(
            OversightRowSubtitles.subtitle(for: .maintenance, in: context),
            "No maintenance due"
        )
    }

    func testMaintenanceOverdueTaskLeadsAheadOfFutureTask() {
        var context = OversightRowSubtitleContext.unknown
        context.upcomingMaintenance = [
            makeTask(
                printer: "X1C-04",
                task: "Belt tension",
                component: "belt",
                daysUntilDue: 3,
                isOverdue: false
            ),
            makeTask(
                printer: "Voron-02",
                task: "Nozzle clean",
                component: "nozzle",
                daysUntilDue: -2,
                isOverdue: true
            )
        ]

        XCTAssertEqual(
            OversightRowSubtitles.subtitle(for: .maintenance, in: context),
            "Voron-02 nozzle overdue, X1C-04 belt in 3 days"
        )
    }

    func testMaintenanceCapsAtTwoFragments() {
        var context = OversightRowSubtitleContext.unknown
        context.upcomingMaintenance = (1...5).map { (day: Int) in
            makeTask(
                printer: "P\(day)",
                task: "Task \(day)",
                component: "part\(day)",
                daysUntilDue: day,
                isOverdue: false
            )
        }

        let subtitle = OversightRowSubtitles.subtitle(for: .maintenance, in: context)
        XCTAssertEqual(subtitle, "P1 part1 in 1 day, P2 part2 in 2 days")
        XCTAssertFalse(subtitle.contains("part3"), "Only the two most-imminent tasks should be surfaced.")
    }

    func testMaintenanceDueTodayBeatsFutureButLosesToOverdue() {
        var context = OversightRowSubtitleContext.unknown
        context.upcomingMaintenance = [
            makeTask(
                printer: "Later",
                task: "Grease rails",
                component: "rails",
                daysUntilDue: 5,
                isOverdue: false
            ),
            makeTask(
                printer: "Today",
                task: "Check hotend",
                component: "hotend",
                daysUntilDue: 0,
                isOverdue: false,
                isDueToday: true
            ),
            makeTask(
                printer: "Late",
                task: "Replace belt",
                component: "belt",
                daysUntilDue: -1,
                isOverdue: true
            )
        ]

        XCTAssertEqual(
            OversightRowSubtitles.subtitle(for: .maintenance, in: context),
            "Late belt overdue, Today hotend due today"
        )
    }

    func testMaintenanceUsesTaskNameWhenComponentIsMissing() {
        var context = OversightRowSubtitleContext.unknown
        context.upcomingMaintenance = [
            makeTask(
                printer: "P1",
                task: "Full inspection",
                component: nil,
                daysUntilDue: 2,
                isOverdue: false
            )
        ]

        XCTAssertEqual(
            OversightRowSubtitles.subtitle(for: .maintenance, in: context),
            "P1 Full inspection in 2 days"
        )
    }

    func testMaintenanceTaskWithoutDaysDegradesToScheduled() {
        var context = OversightRowSubtitleContext.unknown
        context.upcomingMaintenance = [
            makeTask(
                printer: "P1",
                task: "Firmware update",
                component: nil,
                daysUntilDue: nil,
                isOverdue: false
            )
        ]

        XCTAssertEqual(
            OversightRowSubtitles.subtitle(for: .maintenance, in: context),
            "P1 Firmware update scheduled"
        )
    }

    func testMaintenancePrefersOverdueMostNegativeDaysFirst() {
        var context = OversightRowSubtitleContext.unknown
        context.upcomingMaintenance = [
            makeTask(
                printer: "MildlyLate",
                task: "T",
                component: "c",
                daysUntilDue: -1,
                isOverdue: true
            ),
            makeTask(
                printer: "VeryLate",
                task: "T",
                component: "c",
                daysUntilDue: -14,
                isOverdue: true
            )
        ]

        let subtitle = OversightRowSubtitles.subtitle(for: .maintenance, in: context)
        XCTAssertTrue(
            subtitle.hasPrefix("VeryLate"),
            "Most-overdue task should lead. Got: \(subtitle)"
        )
    }

    // MARK: - Dashboard (Right now row)

    func testDashboardSurfacesPluralAttentionCount() {
        var context = OversightRowSubtitleContext.unknown
        context.attentionItemCount = 3
        context.healthyPrinterCount = 8

        XCTAssertEqual(
            OversightRowSubtitles.subtitle(for: .dashboard, in: context),
            "3 attention items"
        )
    }

    func testDashboardSurfacesSingularAttentionCount() {
        var context = OversightRowSubtitleContext.unknown
        context.attentionItemCount = 1

        XCTAssertEqual(
            OversightRowSubtitles.subtitle(for: .dashboard, in: context),
            "1 attention item"
        )
    }

    func testDashboardShowsHealthyPrinterCountWhenNothingNeedsAttention() {
        var context = OversightRowSubtitleContext.unknown
        context.attentionItemCount = 0
        context.healthyPrinterCount = 12

        XCTAssertEqual(
            OversightRowSubtitles.subtitle(for: .dashboard, in: context),
            "12 printers running normally"
        )
    }

    func testDashboardFallsBackToNeutralPhraseWhenNoHealthySignal() {
        var context = OversightRowSubtitleContext.unknown
        context.attentionItemCount = 0
        context.healthyPrinterCount = 0

        XCTAssertEqual(
            OversightRowSubtitles.subtitle(for: .dashboard, in: context),
            "Nothing needs attention right now"
        )
    }

    // MARK: - Navigation settings (Reports row)

    func testNavigationSettingsSimplePreferenceReadsAsSimpleLayout() {
        var context = OversightRowSubtitleContext.unknown
        context.navigationPreference = .simple

        XCTAssertEqual(
            OversightRowSubtitles.subtitle(for: .navigationSettings, in: context),
            "Simple layout"
        )
    }

    func testNavigationSettingsTwoModesPreferenceReadsAsTwoModes() {
        var context = OversightRowSubtitleContext.unknown
        context.navigationPreference = .twoModes

        XCTAssertEqual(
            OversightRowSubtitles.subtitle(for: .navigationSettings, in: context),
            "Two modes"
        )
    }

    func testNavigationSettingsAutomaticExplainsResolvedShell() {
        var context = OversightRowSubtitleContext.unknown
        context.navigationPreference = .automatic
        context.automaticDerivation = NavigationShellDerivation(
            shell: .simple,
            explanation: "This server doesn't report its size. Using the simple layout."
        )

        XCTAssertEqual(
            OversightRowSubtitles.subtitle(for: .navigationSettings, in: context),
            "Automatic · currently the simple layout"
        )

        context.automaticDerivation = NavigationShellDerivation(
            shell: .twoModes,
            explanation: "Large farm."
        )
        XCTAssertEqual(
            OversightRowSubtitles.subtitle(for: .navigationSettings, in: context),
            "Automatic · currently the two-modes layout"
        )
    }

    func testNavigationSettingsAutomaticWithoutDerivationOnlyClaimsAutomatic() {
        var context = OversightRowSubtitleContext.unknown
        context.navigationPreference = .automatic

        XCTAssertEqual(
            OversightRowSubtitles.subtitle(for: .navigationSettings, in: context),
            "Automatic"
        )
    }

    // MARK: - Descriptive rows stay static

    func testDescriptiveRowsAreUnchangedByLoadedContext() {
        var context = OversightRowSubtitleContext.unknown
        context.upcomingMaintenance = []
        context.attentionItemCount = 0
        context.navigationPreference = .simple

        let descriptive: [OversightDestination] = [
            .dispatch,
            .filamentCoverage,
            .maintenanceAnalytics,
            .predictiveInsights,
            .jobHistory,
            .jobTimeline,
            .locations,
            .uptimeReliability
        ]

        for destination in descriptive {
            XCTAssertEqual(
                OversightRowSubtitles.subtitle(for: destination, in: context),
                destination.subtitle,
                "\(destination.rawValue) should stay descriptive; this row does not derive from data yet."
            )
        }
    }

    // MARK: - Helpers

    private func makeTask(
        printer: String,
        task: String,
        component: String?,
        daysUntilDue: Int?,
        isOverdue: Bool,
        isDueToday: Bool = false
    ) -> UpcomingMaintenanceTask {
        UpcomingMaintenanceTask(
            id: "\(printer)-\(task)",
            taskId: UUID(),
            printerId: UUID(),
            printerName: printer,
            taskName: task,
            component: component,
            description: nil,
            priority: 1,
            intervalType: "days",
            intervalValue: 30,
            dueDate: nil,
            daysUntilDue: daysUntilDue,
            hoursUntilDue: nil,
            isOverdue: isOverdue,
            isDueToday: isDueToday,
            lastPerformedAt: nil
        )
    }
}
