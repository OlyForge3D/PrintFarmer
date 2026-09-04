import SwiftUI

/// App router owning shell-aware tab selection, per-tab navigation stacks,
/// and the legacy fallback sheet stacks.
///
/// Tab-owned `NavigationPath` properties back each tab's stack:
/// * `printersPath` — Farm tab (PrinterListView)
/// * `tasksPath` — Tasks tab root (ShiftTasksView)
/// * `jobsPath` — Print queue nested under Tasks (JobListView)
/// * `notificationsPath` — Attention tab (AttentionView / NotificationsViewModel)
/// * `scanPath` — Scan tab (ScanView)
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
    struct FilamentSwapDeepLink: Equatable {
        let printerId: UUID
        let toolheadIndex: Int
        let jobId: UUID?
    }

    private(set) var activeShell: NavigationShell = .current
    private(set) var activeMode: OversightMode = .floor
    private(set) var requestedShell: NavigationShell = .current
    private(set) var configuredServerID: UUID?
    private(set) var configuredUserID: UUID?
    private(set) var appliedNavigationPreference: NavigationLayoutPreference?
    private(set) var establishedAutomaticDerivation: NavigationShellDerivation?
    private(set) var oversightAvailability = OversightNavigationAvailability.fullyAvailable
    var selectedTab: AppTab = .attention
    var printersPath = NavigationPath()
    var tasksPath = NavigationPath()
    var jobsPath = NavigationPath()
    var notificationsPath = NavigationPath()
    var inventoryPath = NavigationPath()
    var scanPath = NavigationPath()
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
            guard visibleTabs(for: capabilities).contains(tab) else {
                resetPrinterPath(for: tab)
                selectedTab = fallbackTab(for: capabilities)
                return
            }
            selectedTab = tab
            resetPrinterPath(for: tab)
            Task { @MainActor in
                try? await Task.sleep(for: .milliseconds(50))
                guard capturedEpoch == navigationEpoch else { return }
                appendPrinterDestination(.printerDetail(id: id), to: tab)
            }
        case .printerReady(let id):
            let tab = printerDestinationTab
            guard visibleTabs(for: capabilities).contains(tab) else {
                resetPrinterPath(for: tab)
                pendingNFCReadyPrinterId = nil
                selectedTab = fallbackTab(for: capabilities)
                return
            }
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
            guard visibleTabs(for: capabilities).contains(tab) else {
                resetPrinterPath(for: tab)
                pendingFilamentSwap = nil
                selectedTab = fallbackTab(for: capabilities)
                return
            }
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
        scanPath = NavigationPath()
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
        scanPath = NavigationPath()
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
            capabilities: capabilities,
            oversightAvailability: oversightAvailability
        )
    }

    func fallbackTab(
        for capabilities: ResolvedSystemCapabilities
    ) -> AppTab {
        AppTab.fallbackTab(
            for: activeShell,
            mode: activeMode,
            capabilities: capabilities,
            oversightAvailability: oversightAvailability
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

    func configureAdaptiveShell(
        serverID: UUID,
        userID: UUID,
        preference: NavigationLayoutPreference,
        farmShape: FarmShape?,
        isFarmAdmin: Bool,
        capabilities: ResolvedSystemCapabilities,
        oversightAvailability: OversightNavigationAvailability
    ) {
        let contextChanged = configuredServerID != serverID || configuredUserID != userID
        let preferenceChanged = appliedNavigationPreference != preference

        if contextChanged {
            invalidatePendingNavigation()
        }

        if contextChanged || preferenceChanged || appliedNavigationPreference == nil {
            let automaticDerivation = NavigationShellDerivation.automatic(
                farmShape: farmShape,
                shiftPlanEnabled: capabilities.shiftPlanEnabled,
                isFarmAdmin: isFarmAdmin
            )
            configuredServerID = serverID
            configuredUserID = userID
            appliedNavigationPreference = preference
            establishedAutomaticDerivation = preference == .automatic
                ? automaticDerivation
                : nil
            requestedShell = AdaptiveNavigationShell.requestedShell(
                preference: preference,
                automaticDerivation: automaticDerivation
            )
            activeMode = .floor
        }

        reconcileCapabilities(
            capabilities,
            oversightAvailability: oversightAvailability
        )
    }

    func setNavigationShell(
        _ shell: NavigationShell,
        mode: OversightMode? = nil,
        capabilities: ResolvedSystemCapabilities,
        oversightAvailability: OversightNavigationAvailability = .fullyAvailable
    ) {
        let nextMode = mode ?? activeMode
        requestedShell = shell
        transition(
            to: AdaptiveNavigationShell.effectiveShell(
                requestedShell: shell,
                oversightAvailability: oversightAvailability
            ),
            mode: nextMode,
            capabilities: capabilities,
            oversightAvailability: oversightAvailability
        )
    }

    func setNavigationMode(
        _ mode: OversightMode,
        capabilities: ResolvedSystemCapabilities
    ) {
        guard requestedShell == .twoModes,
              oversightAvailability.supportsTwoModes else {
            return
        }

        transition(
            to: .twoModes,
            mode: mode,
            capabilities: capabilities,
            oversightAvailability: oversightAvailability
        )
    }

    func presentShippingShell(capabilities: ResolvedSystemCapabilities) {
        clearDisabledFeatureState(capabilities)
        transition(
            to: .current,
            mode: activeMode,
            capabilities: capabilities,
            oversightAvailability: oversightAvailability
        )
    }

    func reconcileCapabilities(
        _ capabilities: ResolvedSystemCapabilities,
        oversightAvailability: OversightNavigationAvailability? = nil
    ) {
        let nextOversightAvailability = oversightAvailability ?? self.oversightAvailability

        clearDisabledFeatureState(capabilities)

        transition(
            to: AdaptiveNavigationShell.effectiveShell(
                requestedShell: requestedShell,
                oversightAvailability: nextOversightAvailability
            ),
            mode: activeMode,
            capabilities: capabilities,
            oversightAvailability: nextOversightAvailability
        )
    }

    func isAtRoot(_ tab: AppTab) -> Bool {
        switch tab {
        case .attention:
            notificationsPath.isEmpty
        case .farm:
            printersPath.isEmpty
        case .tasks:
            tasksPath.isEmpty && jobsPath.isEmpty
        case .scan:
            scanPath.isEmpty
        case .inventory:
            inventoryPath.isEmpty
        case .oversight:
            oversightPath.isEmpty
        case .overview:
            overviewPath.isEmpty
        case .fleet:
            fleetPath.isEmpty
        case .jobs:
            oversightJobsPath.isEmpty
        case .upkeep:
            upkeepPath.isEmpty
        case .reports:
            reportsPath.isEmpty
        }
    }

    func shouldShowModeControl(for tab: AppTab) -> Bool {
        activeShell == .twoModes
            && oversightAvailability.supportsTwoModes
            && isAtRoot(tab)
    }

    func hasAdaptiveShellConfiguration(serverID: UUID, userID: UUID) -> Bool {
        configuredServerID == serverID && configuredUserID == userID
    }

    func resetAdaptiveShellSession() {
        invalidatePendingNavigation()
        activeShell = .current
        activeMode = .floor
        requestedShell = .current
        configuredServerID = nil
        configuredUserID = nil
        appliedNavigationPreference = nil
        establishedAutomaticDerivation = nil
        oversightAvailability = .fullyAvailable
        selectedTab = .attention
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
        case .scan:
            scanPath = NavigationPath()
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

    private func clearDisabledFeatureState(
        _ capabilities: ResolvedSystemCapabilities
    ) {
        if !capabilities.attentionEnabled {
            notificationsPath = NavigationPath()
            pendingAttentionItemId = nil
            notificationBadgeCount = 0
        }
        if !capabilities.shiftPlanEnabled {
            tasksPath = NavigationPath()
            jobsPath = NavigationPath()
            pendingReadyCount = 0
        }
        if !capabilities.guidedSwapEnabled {
            pendingFilamentSwap = nil
        }
    }

    private func transition(
        to shell: NavigationShell,
        mode: OversightMode,
        capabilities: ResolvedSystemCapabilities,
        oversightAvailability: OversightNavigationAvailability
    ) {
        let previousTabs = Set(visibleTabs(for: capabilities))
        let shellChanged = shell != activeShell || mode != activeMode

        self.oversightAvailability = oversightAvailability
        activeShell = shell
        activeMode = mode

        let nextTabs = Set(visibleTabs(for: capabilities))
        if shellChanged || previousTabs != nextTabs {
            navigationEpoch &+= 1
            pendingNFCReadyPrinterId = nil
            pendingSpoolHighlightId = nil
            pendingAttentionItemId = nil
            pendingFilamentSwap = nil
        }

        for tab in AppTab.allCases where !nextTabs.contains(tab) {
            resetToRoot(tab: tab)
        }

        selectedTab = nextTabs.contains(selectedTab)
            ? selectedTab
            : fallbackTab(for: capabilities)
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
