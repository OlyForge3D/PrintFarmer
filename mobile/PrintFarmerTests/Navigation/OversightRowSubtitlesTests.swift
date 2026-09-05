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

    // MARK: - Two-modes tab roots (round-3 wiring)

    /// The two-modes tab roots build a context that has only
    /// router-derived state — `attentionItemCount` and
    /// `upcomingMaintenance` stay `nil` because those fetches don't
    /// run in `OversightDestinationListRoot`. The helper must still
    /// produce the live Automatic reading for `.navigationSettings`,
    /// matching what the simple hub shows for the same destination.
    func testTwoModesReportsTabProducesLiveNavigationSubtitle() {
        var context = OversightRowSubtitleContext.unknown
        context.navigationPreference = .automatic
        context.automaticInputs = AutomaticInputsSnapshot(
            farmShape: FarmShape(
                accountCount: 3,
                locationCount: 2,
                printerCount: 25
            ),
            shiftPlanEnabled: true,
            isFarmAdmin: true
        )

        // No fetched signals; the router-only context still yields the
        // honest live subtitle for the navigation-settings row.
        XCTAssertNil(context.attentionItemCount)
        XCTAssertNil(context.upcomingMaintenance)

        XCTAssertEqual(
            OversightRowSubtitles.subtitle(for: .navigationSettings, in: context),
            "Automatic · currently the two-modes layout"
        )
    }

    /// Every destination the two-modes tab roots render — with a
    /// router-only context — must fall back to `destination.subtitle`
    /// whenever the underlying signals aren't loaded. This proves the
    /// wiring is safe: routing every row through the helper does not
    /// silently blank rows or fabricate counts on the two-modes surface.
    /// Kept in sync with `OversightRoot.destinations`.
    func testTwoModesRowsWithoutLoadedSignalsFallBackToDescriptiveSubtitles() {
        var context = OversightRowSubtitleContext.unknown
        context.navigationPreference = .twoModes
        context.automaticInputs = AutomaticInputsSnapshot(
            farmShape: nil,
            shiftPlanEnabled: false,
            isFarmAdmin: false
        )

        // Destinations rendered across Overview, Jobs, Upkeep, and
        // Reports tab roots. `.navigationSettings` is excluded — it
        // is intentionally live from router state and covered by
        // `testTwoModesReportsTabProducesLiveNavigationSubtitle`.
        let twoModesDestinations: [OversightDestination] = [
            .dashboard,
            .dispatch,
            .jobHistory,
            .jobTimeline,
            .maintenance,
            .maintenanceAnalytics,
            .predictiveInsights,
            .uptimeReliability,
            .filamentCoverage,
            .locations
        ]

        for destination in twoModesDestinations {
            XCTAssertEqual(
                OversightRowSubtitles.subtitle(for: destination, in: context),
                destination.subtitle,
                "\(destination.rawValue) has no fetched signals in the two-modes root; helper must fall back to the descriptive subtitle instead of blanking or fabricating."
            )
        }
    }

    // MARK: - Same-server invalidation (round-4 wiring)

    /// The round-1 fix ties `.task(id:)` to `activeServerGeneration`
    /// so a server switch clears stale attention counts. The refresh
    /// key must still change when the server generation bumps, since
    /// that gate protects against cross-server bleed-through.
    func testRefreshKeyChangesWhenServerGenerationBumps() {
        let before = OversightSubtitleRefreshKey(
            serverGeneration: 4,
            attentionEnabled: true
        )
        let after = OversightSubtitleRefreshKey(
            serverGeneration: 5,
            attentionEnabled: true
        )
        XCTAssertNotEqual(
            before,
            after,
            "Server generation bump must change the refresh key so .task(id:) cancels the stale fetch and reclears the context to .unknown."
        )
    }

    /// Round-4 core: same-server transitions must also invalidate the
    /// fetch. A `capabilities.attentionEnabled` toggle is the concrete
    /// signal Hicks called out — with the round-1-only wiring
    /// (`activeServerGeneration` alone), disabling attention mid-session
    /// left the previous `attentionItemCount` visible on the Right now
    /// row for a feature that no longer exists. The composite refresh
    /// key must differ when the capability toggles so `.task(id:)`
    /// re-fires and clears `subtitleContext` to `.unknown`.
    func testRefreshKeyChangesWhenAttentionCapabilityToggles() {
        let enabled = OversightSubtitleRefreshKey(
            serverGeneration: 7,
            attentionEnabled: true
        )
        let disabled = OversightSubtitleRefreshKey(
            serverGeneration: 7,
            attentionEnabled: false
        )
        XCTAssertNotEqual(
            enabled,
            disabled,
            "Toggling attentionEnabled on the same server must change the refresh key so the stale attentionItemCount is cleared."
        )
    }

    /// The refresh key must be stable when no relevant input changes.
    /// Unrelated view-model churn (router preference flips, farm shape
    /// updates) is routed through the observable computed subtitle
    /// context, not through the refetch trigger — refetching on every
    /// router tick would thrash the network.
    func testRefreshKeyIsStableWhenServerAndCapabilityAreUnchanged() {
        let a = OversightSubtitleRefreshKey(
            serverGeneration: 12,
            attentionEnabled: true
        )
        let b = OversightSubtitleRefreshKey(
            serverGeneration: 12,
            attentionEnabled: true
        )
        XCTAssertEqual(
            a,
            b,
            "Identical inputs must produce equal refresh keys or SwiftUI will refetch on every view rebuild."
        )
    }

    /// After a capability toggle invalidates the fetch, the very first
    /// thing `refreshSubtitleContext` does is reset `subtitleContext`
    /// to `.unknown`. Downstream, the derivation must fall back to the
    /// descriptive catalog subtitle for attention-carrying destinations
    /// — not display "0 attention items" or the pre-toggle count. This
    /// covers the round-1 invariant against staleness AND the round-4
    /// finding: the honest-unknown fallback must be robust to signals
    /// that were populated by a previous same-server run.
    func testAttentionCapabilityDisableClearsToHonestUnknownSubtitle() {
        // First: a fully-populated context (the state that existed
        // immediately before the capability was disabled).
        var populated = OversightRowSubtitleContext.unknown
        populated.attentionItemCount = 7
        populated.attentionHasMorePages = false
        populated.healthyPrinterCount = 12

        XCTAssertEqual(
            OversightRowSubtitles.subtitle(for: .dashboard, in: populated),
            "7 attention items",
            "Baseline: while attention is enabled, the dashboard row shows the live count."
        )

        // Then: the refresh key changes on disable, `.task(id:)`
        // reclears the context to `.unknown`, and — because the fetch
        // is skipped when the capability is off — the context stays
        // unknown. The derivation must fall back to the descriptive
        // subtitle instead of the pre-disable count.
        let cleared = OversightRowSubtitleContext.unknown
        XCTAssertNil(
            cleared.attentionItemCount,
            "Honest unknown: a disabled capability leaves attentionItemCount as nil, not zero."
        )
        XCTAssertEqual(
            OversightRowSubtitles.subtitle(for: .dashboard, in: cleared),
            OversightDestination.dashboard.subtitle,
            "Disabling attention mid-session must not leave the pre-disable count visible; the row falls back to the honest descriptive subtitle."
        )
    }

    // MARK: - Concurrency guard (round-5 wiring)

    /// The happy path: the request key we captured on entry still
    /// matches the current key and the enclosing task has not been
    /// cancelled. The fetched context is safe to commit.
    func testCommitGuardAllowsCommitWhenKeyUnchangedAndTaskLive() {
        let key = OversightSubtitleRefreshKey(
            serverGeneration: 3,
            attentionEnabled: true
        )
        XCTAssertTrue(
            key.isStillCurrent(comparedTo: key, taskCancelled: false),
            "Same key + live task must permit committing the fetched context."
        )
    }

    /// Round-1 cancellation path preserved: SwiftUI cancelled the
    /// `.task(id:)` because the refresh key changed while we were
    /// awaiting. Even if the underlying URLSession-style API didn't
    /// propagate cancellation and `try?` yielded a value, we discard.
    func testCommitGuardDiscardsWhenTaskCancelledEvenWithMatchingKey() {
        let key = OversightSubtitleRefreshKey(
            serverGeneration: 3,
            attentionEnabled: true
        )
        XCTAssertFalse(
            key.isStillCurrent(comparedTo: key, taskCancelled: true),
            "A cancelled task must never commit its fetched context, even if the key still nominally matches — this preserves the round-1 protection against a stale `.task(id:)` fetch resuming after supersession."
        )
    }

    /// Vasquez round-5 core: `.refreshable` racing with `.task(id:)`.
    /// A pull-to-refresh started under key K1 (attention enabled) is
    /// mid-fetch when the user disables the capability. `.task(id:)`
    /// fires with K2 (attention disabled), completes fast (no fetch),
    /// and commits `.unknown`. Then the slow K1 pull's fetch resumes.
    /// Its `Task.isCancelled` is `false` because `.refreshable` owns
    /// its own task that SwiftUI did NOT cancel — the key comparison
    /// is the ONLY signal that this result is stale.
    func testCommitGuardDiscardsWhenAttentionCapabilityToggledUnderConcurrentFetch() {
        let capturedAtStart = OversightSubtitleRefreshKey(
            serverGeneration: 3,
            attentionEnabled: true
        )
        let currentAfterToggle = OversightSubtitleRefreshKey(
            serverGeneration: 3,
            attentionEnabled: false
        )
        XCTAssertFalse(
            capturedAtStart.isStillCurrent(
                comparedTo: currentAfterToggle,
                taskCancelled: false
            ),
            "A slow `.refreshable` fetch whose captured key no longer matches the current key must be discarded even without task cancellation — otherwise it would overwrite the fresher `.task(id:)` snapshot with pre-toggle attention data."
        )
    }

    /// The server-switch counterpart: a concurrent `.refreshable` was
    /// mid-fetch when the user switched servers. The key comparison
    /// catches this too, so pre-switch counts never bleed onto the
    /// incoming server's chrome even if the pull's task somehow
    /// escaped SwiftUI cancellation.
    func testCommitGuardDiscardsWhenServerGenerationChangesUnderConcurrentFetch() {
        let capturedAtStart = OversightSubtitleRefreshKey(
            serverGeneration: 3,
            attentionEnabled: true
        )
        let currentAfterServerSwitch = OversightSubtitleRefreshKey(
            serverGeneration: 4,
            attentionEnabled: true
        )
        XCTAssertFalse(
            capturedAtStart.isStillCurrent(
                comparedTo: currentAfterServerSwitch,
                taskCancelled: false
            ),
            "A concurrent fetch whose captured key predates a server switch must be discarded — pre-switch attention counts must never bleed onto the incoming server's chrome."
        )
    }

    /// Belt-and-braces: when BOTH signals fire, we still discard. The
    /// two guards are independent — cancellation OR key mismatch means
    /// stale — and neither is allowed to override the other.
    func testCommitGuardDiscardsWhenBothKeyChangedAndTaskCancelled() {
        let capturedAtStart = OversightSubtitleRefreshKey(
            serverGeneration: 3,
            attentionEnabled: true
        )
        let currentAfterMultipleChanges = OversightSubtitleRefreshKey(
            serverGeneration: 4,
            attentionEnabled: false
        )
        XCTAssertFalse(
            capturedAtStart.isStillCurrent(
                comparedTo: currentAfterMultipleChanges,
                taskCancelled: true
            ),
            "When both cancellation and key mismatch fire simultaneously, the guard must still discard."
        )
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
