import Foundation
import SwiftUI

/// App router owning shell-aware tab selection and per-tab navigation stacks.
///
/// The router renders one of two compact shells (see the Navigation Shell
/// section of `mobile/README.md` and ``AdaptiveNavigationShell``):
///
/// * **Simple** — Attention · Farm · Inventory · Oversight (hub).
/// * **Two modes** — pinned Floor | Oversight control with four Floor tabs
///   (Attention · Farm · Tasks · Inventory) and five Oversight tabs
///   (Overview · Fleet · Jobs · Upkeep · Reports).
///
/// Shell selection is driven by ``NavigationLayoutPreference`` and, in
/// `Automatic` mode, by the derivation rule that consumes `FarmShape` plus
/// the resolved `shiftPlanEnabled` capability. **`shiftPlanEnabled` is a
/// negative signal only** (its default is `true`, so a `true` value never
/// implies staffing); reading it positively reintroduces the bug the
/// A′ redesign closed. Fleet size is deliberately not a derivation signal.
/// When the farm-shape response is absent, shape is unknown and the router
/// derives Simple (see #2410 and `NavigationShellDerivation.automatic`).
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
/// See issue #2410 (A′ navigation redesign) for the shell contract and
/// #706 for the original operator-shell migration rationale.
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
    private(set) var requestedShell: NavigationShell = .current
    private(set) var configuredServerID: UUID?
    private(set) var configuredUserID: UUID?
    private(set) var configuredIsFarmAdmin = false
    private(set) var appliedNavigationPreference: NavigationLayoutPreference?
    private(set) var establishedAutomaticDerivation: NavigationShellDerivation?
    private(set) var oversightAvailability = OversightNavigationAvailability.fullyAvailable
    var presentsExpandedSidebar = false
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

    var notificationBadgeCount: Int = 0
    var pendingReadyCount: Int = 0
    var sidebarVisibility: NavigationSplitViewVisibility = .automatic
    var pendingNFCReadyPrinterId: UUID?
    var pendingSpoolHighlightId: Int?
    var pendingAttentionItemId: String?
    var pendingFilamentSwap: FilamentSwapDeepLink?
    var pendingExternalScanRequestID: UUID?
    var isScanFlowDismissing = false
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

    func navigate(
        to destination: DeepLinkDestination,
        capabilities: ResolvedSystemCapabilities
    ) {
        navigationEpoch &+= 1
        let capturedEpoch = navigationEpoch
        switch destination {
        case .scan:
            prepareExternalScan(capabilities: capabilities)
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
            guard makeTabVisibleIfPossible(.inventory, capabilities: capabilities) else {
                selectedTab = fallbackTab(for: capabilities)
                inventoryPath = NavigationPath()
                pendingSpoolHighlightId = nil
                return
            }
            selectedTab = .inventory
            inventoryPath = NavigationPath()
            pendingSpoolHighlightId = id
        case .attentionItem(let id):
            guard makeTabVisibleIfPossible(.attention, capabilities: capabilities) else {
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
        isScanFlowDismissing = false
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
        if presentsExpandedSidebar {
            return SidebarSection.allCases.flatMap {
                AppTab.visibleTabs(
                    in: $0,
                    for: activeShell,
                    capabilities: capabilities,
                    oversightAvailability: oversightAvailability
                )
            }
        }

        return AppTab.visibleTabs(
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
        oversightAvailability: OversightNavigationAvailability,
        preserveNavigationOnContextChange: Bool = false
    ) {
        let contextChanged = configuredServerID != serverID || configuredUserID != userID
        let preferenceChanged = appliedNavigationPreference != preference
        configuredIsFarmAdmin = isFarmAdmin

        if contextChanged && !preserveNavigationOnContextChange {
            invalidatePendingNavigation()
        }

        let automaticDerivation: NavigationShellDerivation
        if contextChanged || establishedAutomaticDerivation == nil {
            automaticDerivation = NavigationShellDerivation.automatic(
                farmShape: farmShape,
                shiftPlanEnabled: capabilities.shiftPlanEnabled,
                isFarmAdmin: isFarmAdmin
            )
            establishedAutomaticDerivation = automaticDerivation
        } else {
            automaticDerivation = establishedAutomaticDerivation
                ?? NavigationShellDerivation.automatic(
                    farmShape: farmShape,
                    shiftPlanEnabled: capabilities.shiftPlanEnabled,
                    isFarmAdmin: isFarmAdmin
                )
        }

        if contextChanged || preferenceChanged || appliedNavigationPreference == nil {
            configuredServerID = serverID
            configuredUserID = userID
            appliedNavigationPreference = preference
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
        presentsExpandedSidebar = false
        selectedTab = .attention
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

    func makeTabVisibleIfPossible(
        _ tab: AppTab,
        capabilities: ResolvedSystemCapabilities
    ) -> Bool {
        if visibleTabs(for: capabilities).contains(tab) {
            return true
        }

        guard activeShell == .twoModes, activeMode == .oversight else {
            return false
        }

        let floorTabs = AppTab.visibleTabs(
            for: .twoModes,
            mode: .floor,
            capabilities: capabilities,
            oversightAvailability: oversightAvailability
        )
        guard floorTabs.contains(tab) else { return false }

        transition(
            to: .twoModes,
            mode: .floor,
            capabilities: capabilities,
            oversightAvailability: oversightAvailability
        )
        return visibleTabs(for: capabilities).contains(tab)
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
