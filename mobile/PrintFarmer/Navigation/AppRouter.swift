import SwiftUI

/// App router owning the operator-shell tab selection, the per-tab
/// navigation stacks, and the legacy fallback sheet stacks. The five
/// operator destinations are Attention, Farm, Tasks, Scan, and Inventory.
///
/// Tab-owned `NavigationPath` properties back each tab's stack:
/// * `printersPath` — Farm tab (PrinterListView)
/// * `jobsPath` — Tasks tab (JobListView)
/// * `notificationsPath` — Attention tab (AttentionView / NotificationsViewModel)
/// * `scanPath` — Scan tab (ScanView)
/// * `inventoryPath` — Inventory tab (SpoolInventoryView)
///
/// Legacy fallback sheet stacks (#727) are owned by the sheets themselves,
/// NOT the tabs, so dismissing a sheet can safely reset its stack without
/// disturbing the underlying tab. Reset via `resetLegacySheet(_:)` from the
/// sheet presenter on dismissal:
/// * `dashboardSheetPath` — Dashboard sheet (from Attention overflow)
/// * `maintenanceSheetPath` — Maintenance sheet (from Attention overflow)
/// * `notificationsSheetPath` — legacy Notifications sheet (from Attention
///   feature-disabled fallback). Kept distinct from `notificationsPath` so
///   the sheet never shares state with the Attention tab stack.
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

    var selectedTab: AppTab = .attention
    var printersPath = NavigationPath()
    var jobsPath = NavigationPath()
    var notificationsPath = NavigationPath()
    var inventoryPath = NavigationPath()
    var scanPath = NavigationPath()

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

    /// Monotonic token observed by legacy/operator sheet presenters to close
    /// any active sheet before a task-action destination is applied (#726).
    /// Bumped by `requestTransientSheetDismissal()`. Presenters react via
    /// `.onChange(of:)` and close their local `@State` sheet bindings, which in
    /// turn resets the corresponding legacy sheet stack through the existing
    /// dismissal wiring.
    var sheetDismissalNonce: Int = 0

    func navigate(to destination: DeepLinkDestination) {
        switch destination {
        case .printerDetail(let id):
            selectedTab = .farm
            printersPath = NavigationPath()
            Task { @MainActor in
                try? await Task.sleep(for: .milliseconds(50))
                printersPath.append(AppDestination.printerDetail(id: id))
            }
        case .printerReady(let id):
            selectedTab = .farm
            printersPath = NavigationPath()
            pendingNFCReadyPrinterId = id
            Task { @MainActor in
                try? await Task.sleep(for: .milliseconds(50))
                printersPath.append(AppDestination.printerDetail(id: id))
            }
        case .spoolDetail(let id):
            selectedTab = .inventory
            inventoryPath = NavigationPath()
            pendingSpoolHighlightId = id
        case .attentionItem(let id):
            selectedTab = .attention
            notificationsPath = NavigationPath()
            pendingAttentionItemId = id
        case .filamentSwap(let printerId, let toolheadIndex, let jobId):
            selectedTab = .farm
            printersPath = NavigationPath()
            pendingFilamentSwap = FilamentSwapDeepLink(
                printerId: printerId,
                toolheadIndex: toolheadIndex,
                jobId: jobId
            )
            Task { @MainActor in
                try? await Task.sleep(for: .milliseconds(50))
                printersPath.append(AppDestination.printerDetail(id: printerId))
            }
        }
    }

    func routeNotification(
        userInfo: [AnyHashable: Any],
        activeOriginServerId: UUID? = nil
    ) {
        switch NotificationDeepLinkRouting.destination(
            from: userInfo,
            activeOriginServerId: activeOriginServerId
        ) {
        case .success(let destination):
            notificationRoutingError = nil
            navigate(to: destination)
        case .failure(let failure):
            notificationRoutingError = failure.message
        }
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
        selectedTab = .farm
        printersPath = NavigationPath()
        printersPath.append(AppDestination.printerDetail(id: printerID))
    }

    func resetToRoot(tab: AppTab) {
        switch tab {
        case .attention:
            notificationsPath = NavigationPath()
        case .farm:
            printersPath = NavigationPath()
        case .tasks:
            jobsPath = NavigationPath()
        case .scan:
            scanPath = NavigationPath()
        case .inventory:
            inventoryPath = NavigationPath()
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

/// The five operator-shell destinations. Order matches the tab bar layout
/// on iPhone and the sidebar ordering on iPad (see `ContentView`).
enum AppTab: Hashable, CaseIterable {
    case attention
    case farm
    case tasks
    case scan
    case inventory
}
