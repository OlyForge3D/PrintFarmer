import Foundation
import SwiftUI

/// App router owning shell-aware tab selection, per-tab navigation stacks,
/// and the legacy fallback sheet stacks.
///
/// Tab-owned `NavigationPath` properties back each tab's stack:
/// * `printersPath` — Farm tab (PrinterListView)
/// * `tasksPath` — Tasks tab root (ShiftTasksView)
/// * `jobsPath` — Print queue nested under Tasks (JobListView)
/// * `notificationsPath` — Attention tab (AttentionView / NotificationsViewModel)
/// * `inventoryPath` — Inventory tab (SpoolInventoryView)
/// * `oversightPath` — Simple-shell Oversight tab
/// * `overviewPath` — Oversight-mode Overview tab
/// * `fleetPath` — Oversight-mode Fleet tab
/// * `oversightJobsPath` — Oversight-mode Jobs tab
/// * `upkeepPath` — Oversight-mode Upkeep tab
/// * `reportsPath` — Oversight-mode Reports tab
///
/// Legacy fallback sheet stacks (#727) are owned by the sheets themselves,
/// NOT the tabs, so dismissing a sheet can safely reset its stack without
/// disturbing the underlying tab. Reset via `resetLegacySheet(_:)` from the
/// sheet presenter on dismissal:
/// * `dashboardSheetPath` — Dashboard sheet (from Attention overflow)
/// * `maintenanceSheetPath` — Maintenance sheet (from Attention overflow)
/// * `notificationsSheetPath` — legacy Notifications sheet (from Attention
///   overflow). Kept distinct from `notificationsPath` so the sheet never
///   shares state with the Attention tab stack.
///
/// See issue #706 (F1 operator shell) and #727 (legacy sheet reset) for
/// the migration and hardening rationale.
@MainActor @Observable
final class AppRouter {
    static let selectedTabDefaultsKey = "app.selectedTab"

    struct FilamentSwapDeepLink: Equatable {
        let printerId: UUID
        let toolheadIndex: Int
        let jobId: UUID?
    }

    private(set) var activeShell: NavigationShell = .current
    private(set) var activeMode: OversightMode = .floor
    var selectedTab: AppTab {
        didSet {
            userDefaults?.set(selectedTab.rawValue, forKey: Self.selectedTabDefaultsKey)
        }
    }
    var printersPath = NavigationPath()
    var tasksPath = NavigationPath()
    var jobsPath = NavigationPath()
    var notificationsPath = NavigationPath()
    var inventoryPath = NavigationPath()
    var oversightPath = NavigationPath()
    var overviewPath = NavigationPath()
    var fleetPath = NavigationPath()
    var oversightJobsPath = NavigationPath()
    var upkeepPath = NavigationPath()
    var reportsPath = NavigationPath()

    // Legacy fallback sheet stacks (#727) — owned by the sheets, reset on
    // dismissal via `resetLegacySheet(_:)`. Never shared with a tab stack.
    var dashboardSheetPath = NavigationPath()
    var maintenanceSheetPath = NavigationPath()
    var notificationsSheetPath = NavigationPath()

    var notificationBadgeCount: Int = 0
    var pendingReadyCount: Int = 0
    var sidebarVisibility: NavigationSplitViewVisibility = .automatic
    var pendingNFCReadyPrinterId: UUID?
    var pendingSpoolHighlightId: Int?
    var pendingAttentionItemId: String?
    var pendingFilamentSwap: FilamentSwapDeepLink?
    var notificationRoutingError: String?
    private var navigationEpoch = 0
    @ObservationIgnored private let userDefaults: UserDefaults?

    init(userDefaults: UserDefaults? = nil) {
        self.userDefaults = userDefaults
        selectedTab = Self.restoredTab(
            from: userDefaults?.string(forKey: Self.selectedTabDefaultsKey)
        )
    }

    static func restoredTab(from persistedRawValue: String?) -> AppTab {
        guard let persistedRawValue else { return .attention }
        if persistedRawValue == "scan" {
            return .inventory
        }
        return AppTab(rawValue: persistedRawValue) ?? .attention
    }

    /// Monotonic token observed by legacy/operator sheet presenters to close
    /// any active sheet before a task-action destination is applied (#726).
    /// Bumped by `requestTransientSheetDismissal()`. Presenters react via
    /// `.onChange(of:)` and close their local `@State` sheet bindings, which in
    /// turn resets the corresponding legacy sheet stack through the existing
    /// dismissal wiring.
    var sheetDismissalNonce: Int = 0

    func navigate(
        to destination: DeepLinkDestination,
        capabilities: ResolvedSystemCapabilities
    ) {
        navigationEpoch &+= 1
        let capturedEpoch = navigationEpoch
        switch destination {
        case .printerDetail(let id):
            let tab = printerDestinationTab
            selectedTab = tab
            resetPrinterPath(for: tab)
            Task { @MainActor in
                try? await Task.sleep(for: .milliseconds(50))
                guard capturedEpoch == navigationEpoch else { return }
                appendPrinterDestination(.printerDetail(id: id), to: tab)
            }
        case .printerReady(let id):
            let tab = printerDestinationTab
            selectedTab = tab
            resetPrinterPath(for: tab)
            pendingNFCReadyPrinterId = id
            Task { @MainActor in
                try? await Task.sleep(for: .milliseconds(50))
                guard capturedEpoch == navigationEpoch else { return }
                appendPrinterDestination(.printerDetail(id: id), to: tab)
            }
        case .spoolDetail(let id):
            guard visibleTabs(for: capabilities).contains(.inventory) else {
                selectedTab = fallbackTab(for: capabilities)
                inventoryPath = NavigationPath()
                pendingSpoolHighlightId = nil
                return
            }
            selectedTab = .inventory
            inventoryPath = NavigationPath()
            pendingSpoolHighlightId = id
        case .attentionItem(let id):
            guard visibleTabs(for: capabilities).contains(.attention) else {
                selectedTab = fallbackTab(for: capabilities)
                notificationsPath = NavigationPath()
                pendingAttentionItemId = nil
                return
            }
            selectedTab = .attention
            notificationsPath = NavigationPath()
            pendingAttentionItemId = id
        case .filamentSwap(let printerId, let toolheadIndex, let jobId):
            let tab = printerDestinationTab
            selectedTab = tab
            resetPrinterPath(for: tab)
            pendingFilamentSwap = capabilities.guidedSwapEnabled
                ? FilamentSwapDeepLink(
                    printerId: printerId,
                    toolheadIndex: toolheadIndex,
                    jobId: jobId
                )
                : nil
            Task { @MainActor in
                try? await Task.sleep(for: .milliseconds(50))
                guard capturedEpoch == navigationEpoch else { return }
                appendPrinterDestination(
                    .printerDetail(id: printerId),
                    to: tab
                )
            }
        }
    }

    func invalidatePendingNavigation() {
        navigationEpoch &+= 1
        printersPath = NavigationPath()
        tasksPath = NavigationPath()
        jobsPath = NavigationPath()
        notificationsPath = NavigationPath()
        inventoryPath = NavigationPath()
        oversightPath = NavigationPath()
        overviewPath = NavigationPath()
        fleetPath = NavigationPath()
        oversightJobsPath = NavigationPath()
        upkeepPath = NavigationPath()
        reportsPath = NavigationPath()
        dashboardSheetPath = NavigationPath()
        maintenanceSheetPath = NavigationPath()
        notificationsSheetPath = NavigationPath()
        pendingNFCReadyPrinterId = nil
        pendingSpoolHighlightId = nil
        pendingAttentionItemId = nil
        pendingFilamentSwap = nil
        notificationRoutingError = nil
    }

    /// Every stack resolves `AppDestination` and can reach Printer Detail, so
    /// reset them all when the safety interlock is disabled.
    func revokeAdvancedPrinterControlsAccess() {
        navigationEpoch &+= 1
        printersPath = NavigationPath()
        tasksPath = NavigationPath()
        jobsPath = NavigationPath()
        notificationsPath = NavigationPath()
        inventoryPath = NavigationPath()
        oversightPath = NavigationPath()
        overviewPath = NavigationPath()
        fleetPath = NavigationPath()
        oversightJobsPath = NavigationPath()
        upkeepPath = NavigationPath()
        reportsPath = NavigationPath()
        dashboardSheetPath = NavigationPath()
        maintenanceSheetPath = NavigationPath()
        notificationsSheetPath = NavigationPath()
    }

    func routeNotification(
        userInfo: [AnyHashable: Any],
        activeOriginServerId: UUID? = nil,
        capabilities: ResolvedSystemCapabilities
    ) {
        switch NotificationDeepLinkRouting.destination(
            from: userInfo,
            activeOriginServerId: activeOriginServerId
        ) {
        case .success(let destination):
            notificationRoutingError = nil
            navigate(to: destination, capabilities: capabilities)
        case .failure(let failure):
            notificationRoutingError = failure.message
        }
    }

    func visibleTabs(
        for capabilities: ResolvedSystemCapabilities
    ) -> [AppTab] {
        AppTab.visibleTabs(
            for: activeShell,
            mode: activeMode,
            capabilities: capabilities
        )
    }

    func fallbackTab(
        for capabilities: ResolvedSystemCapabilities
    ) -> AppTab {
        AppTab.fallbackTab(
            for: activeShell,
            mode: activeMode,
            capabilities: capabilities
        )
    }

    func resolvedTab(for capabilities: ResolvedSystemCapabilities) -> AppTab {
        visibleTabs(for: capabilities).contains(selectedTab)
            ? selectedTab
            : fallbackTab(for: capabilities)
    }

    func selectTab(_ tab: AppTab, capabilities: ResolvedSystemCapabilities) {
        selectedTab = visibleTabs(for: capabilities).contains(tab)
            ? tab
            : fallbackTab(for: capabilities)
    }

    func setNavigationShell(
        _ shell: NavigationShell,
        mode: OversightMode? = nil,
        capabilities: ResolvedSystemCapabilities
    ) {
        let nextMode = mode ?? activeMode
        guard shell != activeShell || nextMode != activeMode else {
            selectedTab = resolvedTab(for: capabilities)
            return
        }

        navigationEpoch &+= 1
        pendingNFCReadyPrinterId = nil
        pendingSpoolHighlightId = nil
        pendingAttentionItemId = nil
        pendingFilamentSwap = nil
        activeShell = shell
        activeMode = nextMode
        selectedTab = resolvedTab(for: capabilities)
    }

    func reconcileCapabilities(_ capabilities: ResolvedSystemCapabilities) {
        if !capabilities.attentionEnabled {
            notificationsPath = NavigationPath()
            pendingAttentionItemId = nil
            notificationBadgeCount = 0
        }
        if !capabilities.shiftPlanEnabled {
            tasksPath = NavigationPath()
            jobsPath = NavigationPath()
        }
        if !capabilities.guidedSwapEnabled {
            pendingFilamentSwap = nil
        }
        selectedTab = resolvedTab(for: capabilities)
    }

    /// Requests that any active legacy/operator sheet be dismissed. Task-action
    /// routing calls this (and awaits the acknowledgement seam) BEFORE applying
    /// a destination so an action never opens behind a sheet (#726).
    func requestTransientSheetDismissal() {
        sheetDismissalNonce &+= 1
    }

    /// Applies the guided-swap (#710) destination for a task-action handoff:
    /// selects the Farm tab and deterministically drives the Farm stack to the
    /// target printer's detail (where the guided filament swap lives). Unlike
    /// `navigate(to:)`, this appends synchronously because the sheet has already
    /// been dismissed and the stack is mounted — no timing delay is needed, so
    /// the destination is testable without waiting on elapsed time.
    func routeToFilamentSwap(printerID: UUID, toolheadID: String?) {
        let tab = printerDestinationTab
        selectedTab = tab
        resetPrinterPath(for: tab)
        appendPrinterDestination(.printerDetail(id: printerID), to: tab)
    }

    func resetToRoot(tab: AppTab) {
        switch tab {
        case .attention:
            notificationsPath = NavigationPath()
        case .farm:
            printersPath = NavigationPath()
        case .tasks:
            tasksPath = NavigationPath()
            jobsPath = NavigationPath()
        case .inventory:
            inventoryPath = NavigationPath()
        case .oversight:
            oversightPath = NavigationPath()
        case .overview:
            overviewPath = NavigationPath()
        case .fleet:
            fleetPath = NavigationPath()
        case .jobs:
            oversightJobsPath = NavigationPath()
        case .upkeep:
            upkeepPath = NavigationPath()
        case .reports:
            reportsPath = NavigationPath()
        }
    }

    /// Resets navigation state owned by the given legacy fallback sheet so
    /// that reopening the sheet starts at its documented root (#727).
    ///
    /// Callers should invoke this from the sheet presenter (e.g.
    /// `AttentionView`) when the sheet's `isPresented` binding transitions
    /// from `true` to `false`. This must never touch a tab's stack — the
    /// active tab's `NavigationPath` (e.g. `notificationsPath`) is
    /// deliberately independent of the sheet paths.
    func resetLegacySheet(_ sheet: LegacySheet) {
        switch sheet {
        case .dashboard:
            dashboardSheetPath = NavigationPath()
        case .maintenance:
            maintenanceSheetPath = NavigationPath()
        case .notifications:
            notificationsSheetPath = NavigationPath()
        case .settings:
            // `SettingsView` owns a local `NavigationStack` with no shared
            // path, so there is nothing to reset here. The case exists so
            // presenters can wire every legacy sheet through the same
            // reset entry point without branching.
            break
        }
    }

    private var printerDestinationTab: AppTab {
        // IOS-1 must bind `fleetPath` to the Fleet NavigationStack before it
        // enables the Oversight shell in production.
        activeShell == .twoModes && activeMode == .oversight
            ? .fleet
            : .farm
    }

    private func resetPrinterPath(for tab: AppTab) {
        switch tab {
        case .fleet:
            fleetPath = NavigationPath()
        default:
            printersPath = NavigationPath()
        }
    }

    private func appendPrinterDestination(
        _ destination: AppDestination,
        to tab: AppTab
    ) {
        switch tab {
        case .fleet:
            fleetPath.append(destination)
        default:
            printersPath.append(destination)
        }
    }
}

/// Legacy fallback sheets presented from `AttentionView` (#706 / #725).
/// These sheets are the compatibility path while the operator shell rolls
/// out — each owns its own navigation state via `AppRouter` so dismissing
/// one never disturbs the active tab (#727).
enum LegacySheet: Hashable, CaseIterable {
    case dashboard
    case maintenance
    case notifications
    case settings
}
