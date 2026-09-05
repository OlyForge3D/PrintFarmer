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

    func testContextInitializesAllSignalsToNil() {
        // Guard: OversightRowSubtitleContext must be instantiable with
        // zero arguments, and every signal must default to `nil` so a
        // fresh context reads as "unknown" and every destination falls
        // back to its static subtitle. Without explicit `= nil`
        // defaults on the stored properties, `.unknown` (which calls
        // this initializer) would not compile.
        let context = OversightRowSubtitleContext()
        XCTAssertNil(context.upcomingMaintenance)
        XCTAssertNil(context.attentionItemCount)
        XCTAssertNil(context.attentionHasMorePages)
        XCTAssertNil(context.healthyPrinterCount)
        XCTAssertNil(context.navigationPreference)
        XCTAssertNil(context.automaticDerivation)
        XCTAssertNil(context.automaticInputs)
        XCTAssertEqual(context, .unknown)
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
            "Voron-02 nozzle overdue · X1C-04 belt in 3 days"
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
        XCTAssertEqual(subtitle, "P1 part1 in 1 day · P2 part2 in 2 days")
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
            "Late belt overdue · Today hotend due today"
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

    func testMaintenanceNegativeDaysCountAsOverdueEvenWithoutFlag() {
        // The rank predicate must agree with the state string: a task
        // that renders as "overdue" (because daysUntilDue < 0) has to
        // sort ahead of a task that renders as "in N days", even if the
        // server did not set isOverdue.
        var context = OversightRowSubtitleContext.unknown
        context.upcomingMaintenance = [
            makeTask(
                printer: "Future",
                task: "T",
                component: "part",
                daysUntilDue: 2,
                isOverdue: false
            ),
            makeTask(
                printer: "Late",
                task: "T",
                component: "part",
                daysUntilDue: -3,
                isOverdue: false
            )
        ]

        XCTAssertEqual(
            OversightRowSubtitles.subtitle(for: .maintenance, in: context),
            "Late part overdue · Future part in 2 days"
        )
    }

    func testMaintenanceZeroDaysCountAsDueTodayEvenWithoutFlag() {
        // Symmetric: daysUntilDue == 0 with no flags set has to sort
        // (and render) as due today, ahead of future work.
        var context = OversightRowSubtitleContext.unknown
        context.upcomingMaintenance = [
            makeTask(
                printer: "Future",
                task: "T",
                component: "part",
                daysUntilDue: 3,
                isOverdue: false
            ),
            makeTask(
                printer: "Now",
                task: "T",
                component: "part",
                daysUntilDue: 0,
                isOverdue: false,
                isDueToday: false
            )
        ]

        XCTAssertEqual(
            OversightRowSubtitles.subtitle(for: .maintenance, in: context),
            "Now part due today · Future part in 3 days"
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

    func testDashboardQualifiesCountWithPlusWhenFeedIsPaginated() {
        // The attention feed returned a nextCursor, so items.count is
        // only the first page — the true fleet total is strictly
        // greater. Rendering a bare "50 attention items" would
        // misrepresent it. Must qualify with "+".
        var context = OversightRowSubtitleContext.unknown
        context.attentionItemCount = 50
        context.attentionHasMorePages = true

        XCTAssertEqual(
            OversightRowSubtitles.subtitle(for: .dashboard, in: context),
            "50+ attention items"
        )
    }

    func testDashboardOmitsPlusWhenFeedFitInASinglePage() {
        // Explicit false (as opposed to nil-unknown) means the fetch
        // completed and reported no next page — the count is exact.
        var context = OversightRowSubtitleContext.unknown
        context.attentionItemCount = 3
        context.attentionHasMorePages = false

        XCTAssertEqual(
            OversightRowSubtitles.subtitle(for: .dashboard, in: context),
            "3 attention items"
        )
    }

    func testDashboardQualifiesSingularCountWhenPaginated() {
        // Singular case still qualifies — "1+ attention item" is still
        // more honest than a bare "1" when there are more pages.
        var context = OversightRowSubtitleContext.unknown
        context.attentionItemCount = 1
        context.attentionHasMorePages = true

        XCTAssertEqual(
            OversightRowSubtitles.subtitle(for: .dashboard, in: context),
            "1+ attention item"
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

    // MARK: - Navigation settings — live automatic recompute (#2449 round 2)
    //
    // AppRouter.configureAdaptiveShell only recomputes
    // establishedAutomaticDerivation on server/user context change.
    // A mid-session shift-plan toggle, farm-shape update, or admin
    // flip therefore leaves the stored derivation stale — Hicks's
    // round-2 finding. When the view captures live inputs into
    // `automaticInputs`, the derivation must recompute a fresh
    // Automatic reading rather than trust the stored one.

    func testNavigationSettingsAutomaticPrefersLiveInputsOverStaleStoredDerivation() {
        // Router's stored derivation still claims simple layout, but
        // live inputs (multi-account shape, shifts on, admin) would
        // resolve to two-modes right now. The subtitle must reflect
        // the fresh live derivation, not the stale stored one.
        var context = OversightRowSubtitleContext.unknown
        context.navigationPreference = .automatic
        context.automaticDerivation = NavigationShellDerivation(
            shell: .simple,
            explanation: "Stale reading from before shifts were enabled."
        )
        context.automaticInputs = AutomaticInputsSnapshot(
            farmShape: FarmShape(
                accountCount: 3,
                locationCount: 4,
                printerCount: 12
            ),
            shiftPlanEnabled: true,
            isFarmAdmin: true
        )

        XCTAssertEqual(
            OversightRowSubtitles.subtitle(for: .navigationSettings, in: context),
            "Automatic · currently the two-modes layout"
        )
    }

    func testNavigationSettingsAutomaticFallsBackToStoredDerivationWhenLiveInputsMissing() {
        // Transient case: view has not yet captured live inputs. Fall
        // back to whatever the router established so the subtitle
        // still has a shell to name.
        var context = OversightRowSubtitleContext.unknown
        context.navigationPreference = .automatic
        context.automaticDerivation = NavigationShellDerivation(
            shell: .twoModes,
            explanation: "Router-established."
        )
        XCTAssertNil(context.automaticInputs)

        XCTAssertEqual(
            OversightRowSubtitles.subtitle(for: .navigationSettings, in: context),
            "Automatic · currently the two-modes layout"
        )
    }

    func testNavigationSettingsAutomaticRecomputesAfterShiftPlanToggle() {
        // Same server/user (so router would NOT recompute
        // establishedAutomaticDerivation). Shift plan flips from off
        // to on, promoting the automatic reading from simple to
        // two-modes. The subtitle must reflect the toggle in real
        // time from the live inputs, without needing a router
        // context change.
        let shape = FarmShape(
            accountCount: 2,
            locationCount: 1,
            printerCount: 5
        )
        var context = OversightRowSubtitleContext.unknown
        context.navigationPreference = .automatic
        context.automaticDerivation = NavigationShellDerivation(
            shell: .simple,
            explanation: "Captured when shifts were off."
        )
        context.automaticInputs = AutomaticInputsSnapshot(
            farmShape: shape,
            shiftPlanEnabled: false,
            isFarmAdmin: true
        )

        XCTAssertEqual(
            OversightRowSubtitles.subtitle(for: .navigationSettings, in: context),
            "Automatic · currently the simple layout"
        )

        // Shift plan flips on — no router context change, stored
        // derivation still says simple. Live inputs must win.
        context.automaticInputs?.shiftPlanEnabled = true

        XCTAssertEqual(
            OversightRowSubtitles.subtitle(for: .navigationSettings, in: context),
            "Automatic · currently the two-modes layout"
        )
    }

    func testNavigationSettingsAutomaticRecomputesAfterAdminDemotion() {
        // Same server/user, admin flag flips from true to false. On
        // its own that demotion drops the automatic reading back to
        // simple (non-admin accounts always take the simple layout).
        // The router does not treat admin change as a context change,
        // so the subtitle needs to see it via live inputs.
        let shape = FarmShape(
            accountCount: 3,
            locationCount: 2,
            printerCount: 8
        )
        var context = OversightRowSubtitleContext.unknown
        context.navigationPreference = .automatic
        context.automaticDerivation = NavigationShellDerivation(
            shell: .twoModes,
            explanation: "Captured while user was admin."
        )
        context.automaticInputs = AutomaticInputsSnapshot(
            farmShape: shape,
            shiftPlanEnabled: true,
            isFarmAdmin: true
        )
        XCTAssertEqual(
            OversightRowSubtitles.subtitle(for: .navigationSettings, in: context),
            "Automatic · currently the two-modes layout"
        )

        context.automaticInputs?.isFarmAdmin = false

        XCTAssertEqual(
            OversightRowSubtitles.subtitle(for: .navigationSettings, in: context),
            "Automatic · currently the simple layout"
        )
    }

    func testNavigationSettingsAutomaticRecomputesAfterFarmShapeGrowth() {
        // Same server/user, but a live shape update lands (a second
        // account is added). The automatic reading promotes from
        // simple to two-modes. Router will NOT recompute — the
        // subtitle must still track it via live inputs.
        var context = OversightRowSubtitleContext.unknown
        context.navigationPreference = .automatic
        context.automaticDerivation = NavigationShellDerivation(
            shell: .simple,
            explanation: "Captured when there was one account."
        )
        context.automaticInputs = AutomaticInputsSnapshot(
            farmShape: FarmShape(
                accountCount: 1,
                locationCount: 1,
                printerCount: 4
            ),
            shiftPlanEnabled: true,
            isFarmAdmin: true
        )

        XCTAssertEqual(
            OversightRowSubtitles.subtitle(for: .navigationSettings, in: context),
            "Automatic · currently the simple layout"
        )

        context.automaticInputs?.farmShape = FarmShape(
            accountCount: 2,
            locationCount: 1,
            printerCount: 5
        )

        XCTAssertEqual(
            OversightRowSubtitles.subtitle(for: .navigationSettings, in: context),
            "Automatic · currently the two-modes layout"
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
