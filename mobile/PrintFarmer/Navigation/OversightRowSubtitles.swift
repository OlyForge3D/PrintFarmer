import Foundation

// MARK: - Oversight Row Subtitle Derivation (issue #2449)
//
// The Oversight hub's row subtitles are honest, data-driven summaries.
// When the hub has loaded a live signal (an attention feed, an upcoming
// maintenance list, a navigation preference), the derivation surfaces the
// most useful fragment of it. When no signal has loaded yet — cold hub,
// disabled capability, transient fetch failure — the derivation falls back
// to `OversightDestination.subtitle`, which is a static description of the
// destination rather than a fabricated count.
//
// This mirrors `NavigationShellDerivation.automatic`, which returns an
// explicit `"This server doesn't report its size. Using the simple
// layout."` when the farm shape is `nil`. The rule is the same here: never
// invent a count, never guess. If the signal is absent, describe the
// destination.

/// Optional signals feeding the Oversight hub row subtitles.
///
/// Every field is optional and defaults to `nil` (represented by
/// `OversightRowSubtitleContext.unknown`). A `nil` field means "we have
/// not loaded that signal yet" and MUST NOT be interpreted as
/// "there are zero items". The derivation falls back to the destination's
/// static subtitle whenever the relevant field is `nil`.
///
/// Loaded-but-empty and unknown are distinct states:
///   * `upcomingMaintenance = nil` → unknown, fall back
///   * `upcomingMaintenance = []` → loaded, honestly empty, surface it
struct OversightRowSubtitleContext: Equatable {
    /// Upcoming maintenance tasks currently returned by
    /// `MaintenanceService.getUpcoming(...)`. `nil` means the fetch has
    /// not completed (or was disabled). An empty array means the fetch
    /// completed and reported nothing due.
    var upcomingMaintenance: [UpcomingMaintenanceTask]? = nil

    /// Number of items currently loaded in the attention feed's first
    /// page (matches the semantics of `router.notificationBadgeCount`,
    /// which is set by `AttentionView` from the same first-page snapshot).
    /// `nil` means the feed has not loaded yet or the capability is
    /// disabled — do NOT surface a count in that case.
    var attentionItemCount: Int? = nil

    /// `true` when the attention feed reported a `nextCursor` on the
    /// page we counted — the total is strictly larger than
    /// `attentionItemCount` and the subtitle should qualify the count
    /// with a "+" suffix so we do not misrepresent a paginated first
    /// page as the fleet total. `nil` alongside `attentionItemCount`.
    var attentionHasMorePages: Bool? = nil

    /// Number of healthy printers reported on the same feed page.
    /// `nil` alongside `attentionItemCount` — both come from the same
    /// response. Page-independent per the feed contract.
    var healthyPrinterCount: Int? = nil

    /// The user's currently applied navigation layout preference
    /// (Automatic / Simple / Two modes). `nil` before the router has
    /// settled on a preference.
    var navigationPreference: NavigationLayoutPreference? = nil

    /// The automatic derivation the router last established. Used to
    /// explain what "Automatic" resolved to. `nil` before the router has
    /// established a derivation.
    var automaticDerivation: NavigationShellDerivation? = nil

    static let unknown = OversightRowSubtitleContext()
}

/// Pure derivation from `(OversightDestination, OversightRowSubtitleContext)`
/// to a display subtitle string. No SwiftUI, no side effects, no fetching.
enum OversightRowSubtitles {
    /// Resolve the subtitle for `destination` given the current `context`.
    /// Falls back to `destination.subtitle` when the relevant signal is
    /// absent.
    static func subtitle(
        for destination: OversightDestination,
        in context: OversightRowSubtitleContext
    ) -> String {
        switch destination {
        case .maintenance:
            return maintenanceSubtitle(context) ?? destination.subtitle
        case .dashboard:
            return dashboardSubtitle(context) ?? destination.subtitle
        case .navigationSettings:
            return navigationSettingsSubtitle(context) ?? destination.subtitle
        case .dispatch,
             .filamentCoverage,
             .maintenanceAnalytics,
             .predictiveInsights,
             .jobHistory,
             .jobTimeline,
             .locations,
             .uptimeReliability:
            // Records/Reports and the other Right-now rows keep their
            // descriptive static subtitle. Adding a lightweight recency
            // marker here would require a fetch this hub does not yet
            // perform; the honest choice is to describe the destination.
            return destination.subtitle
        }
    }

    // MARK: - Upkeep → Maintenance

    /// Summarize the most imminent maintenance work.
    ///
    /// * Overdue tasks lead (most-negative `daysUntilDue` first).
    /// * Then due-today tasks.
    /// * Then future tasks sorted by ascending `daysUntilDue`.
    /// * Tasks without a `daysUntilDue` (interval unknown) are dropped.
    ///
    /// Up to two fragments are joined with " · " so the row does not
    /// grow past a single subtitle line on small screens. The bullet
    /// separator matches the concept mockup and avoids confusion with
    /// commas that appear inside printer names.
    private static func maintenanceSubtitle(
        _ context: OversightRowSubtitleContext
    ) -> String? {
        guard let tasks = context.upcomingMaintenance else { return nil }
        if tasks.isEmpty {
            return "No maintenance due"
        }

        let sorted = tasks.sorted(by: maintenanceOrder(_:_:))
        let fragments = sorted.prefix(2).map(maintenanceFragment(_:))
        guard !fragments.isEmpty else { return nil }
        return fragments.joined(separator: " · ")
    }

    /// Ordering predicate: overdue before due-today before future, ties
    /// broken by ascending `daysUntilDue`, then by printer/task name for
    /// stability. Tasks with `nil` `daysUntilDue` sort last so they only
    /// surface when there is nothing else to show.
    private static func maintenanceOrder(
        _ lhs: UpcomingMaintenanceTask,
        _ rhs: UpcomingMaintenanceTask
    ) -> Bool {
        let lhsRank = maintenanceRank(lhs)
        let rhsRank = maintenanceRank(rhs)
        if lhsRank != rhsRank { return lhsRank < rhsRank }

        // Within the same rank, smaller (or more negative) daysUntilDue
        // wins. `nil` days sort last within a rank.
        switch (lhs.daysUntilDue, rhs.daysUntilDue) {
        case let (l?, r?) where l != r:
            return l < r
        case (nil, _?):
            return false
        case (_?, nil):
            return true
        default:
            break
        }

        // Stable tiebreak on printer + task name so identical days
        // produce a deterministic subtitle.
        if lhs.printerName != rhs.printerName {
            return lhs.printerName < rhs.printerName
        }
        return lhs.taskName < rhs.taskName
    }

    /// Lower rank = shown first. Ranks are derived from the same
    /// predicates as `maintenanceState` so a task that reads as
    /// "overdue" always sorts ahead of one that reads as "due today",
    /// even when the server has not set `isOverdue`/`isDueToday` and we
    /// have to infer the state from `daysUntilDue`.
    private static func maintenanceRank(_ task: UpcomingMaintenanceTask) -> Int {
        if isOverdueState(task) { return 0 }
        if isDueTodayState(task) { return 1 }
        return 2
    }

    /// Overdue = the server flagged it OR `daysUntilDue < 0`. Kept in
    /// one place so `maintenanceRank` and `maintenanceState` agree.
    private static func isOverdueState(_ task: UpcomingMaintenanceTask) -> Bool {
        if task.isOverdue { return true }
        if let days = task.daysUntilDue, days < 0 { return true }
        return false
    }

    /// Due today = the server flagged it OR `daysUntilDue == 0` (once
    /// overdue is ruled out). Kept in one place so `maintenanceRank`
    /// and `maintenanceState` agree.
    private static func isDueTodayState(_ task: UpcomingMaintenanceTask) -> Bool {
        if task.isDueToday { return true }
        if let days = task.daysUntilDue, days == 0 { return true }
        return false
    }

    /// Format a single maintenance task as
    /// `"<printer> <component-or-task> <state>"`. The component name is
    /// preferred over the task name when present because the concept
    /// mockup surfaces the failing part (`Voron-02 nozzle overdue`).
    private static func maintenanceFragment(
        _ task: UpcomingMaintenanceTask
    ) -> String {
        let subject = task.component?.trimmingCharacters(in: .whitespaces)
            .nonEmpty ?? task.taskName
        let state = maintenanceState(task)
        return "\(task.printerName) \(subject) \(state)"
    }

    /// State fragment: `"overdue"`, `"due today"`, `"in N days"`, or
    /// `"scheduled"` when we have no `daysUntilDue`. Consistency
    /// with `maintenanceRank` is enforced by sharing `isOverdueState`
    /// and `isDueTodayState`.
    private static func maintenanceState(
        _ task: UpcomingMaintenanceTask
    ) -> String {
        if isOverdueState(task) { return "overdue" }
        if isDueTodayState(task) { return "due today" }
        guard let days = task.daysUntilDue else {
            // We know it's upcoming, but not when. Describe honestly.
            return "scheduled"
        }
        // Overdue and due-today have already been ruled out, so `days`
        // is strictly positive here.
        if days == 1 { return "in 1 day" }
        return "in \(days) days"
    }

    // MARK: - Right now → Dashboard

    /// Mirror the operator attention count. When the feed loaded and the
    /// fleet is quiet we surface a healthy-printer count if we have one,
    /// otherwise a neutral "nothing needs attention" string.
    ///
    /// When `attentionHasMorePages == true` the count is qualified with
    /// a "+" suffix because the feed we sampled was paginated: the true
    /// total is strictly greater than what we counted, and rendering a
    /// bare number would misrepresent the fleet ("3 attention items"
    /// when the fleet is actually reporting 3+N).
    private static func dashboardSubtitle(
        _ context: OversightRowSubtitleContext
    ) -> String? {
        guard let count = context.attentionItemCount else { return nil }
        if count > 0 {
            let noun = count == 1 ? "item" : "items"
            let qualifier = (context.attentionHasMorePages == true) ? "+" : ""
            return "\(count)\(qualifier) attention \(noun)"
        }
        if let healthy = context.healthyPrinterCount, healthy > 0 {
            let noun = healthy == 1 ? "printer" : "printers"
            return "\(healthy) \(noun) running normally"
        }
        return "Nothing needs attention right now"
    }

    // MARK: - Reports → Navigation settings

    /// Explain which shell is currently selected. Automatic renders the
    /// resolved shell so operators can see what "Automatic" is picking.
    private static func navigationSettingsSubtitle(
        _ context: OversightRowSubtitleContext
    ) -> String? {
        guard let preference = context.navigationPreference else { return nil }
        switch preference {
        case .automatic:
            guard let derivation = context.automaticDerivation else {
                // We know the preference but not what it derived to yet.
                return "Automatic"
            }
            switch derivation.shell {
            case .simple:
                return "Automatic · currently the simple layout"
            case .twoModes:
                return "Automatic · currently the two-modes layout"
            case .current:
                return "Automatic"
            }
        case .simple:
            return "Simple layout"
        case .twoModes:
            return "Two modes"
        }
    }
}

// MARK: - Private helpers

private extension String {
    /// Return `nil` when the string is empty. Used to gate empty
    /// component names so the fragment does not include a double space.
    var nonEmpty: String? { isEmpty ? nil : self }
}
