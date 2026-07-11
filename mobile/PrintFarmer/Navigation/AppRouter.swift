import SwiftUI

/// App router owning the operator-shell tab selection and the per-tab
/// navigation stacks. The five operator destinations are Attention,
/// Farm, Tasks, Scan, and Inventory.
///
/// Legacy `NavigationPath` properties (e.g. `printersPath`, `jobsPath`,
/// `notificationsPath`, `dashboardPath`, `maintenancePath`) survive as the
/// backing stacks for their host views because those views are now embedded
/// in the new tabs or presented from Attention overflow:
/// * `printersPath` — Farm tab (PrinterListView)
/// * `jobsPath` — Tasks tab (JobListView)
/// * `notificationsPath` — Attention tab (AttentionView / NotificationsViewModel)
/// * `dashboardPath` / `maintenancePath` — sheets presented from Attention overflow
///
/// See issue #706 (F1 operator shell) for the migration rationale.
@MainActor @Observable
final class AppRouter {
    var selectedTab: AppTab = .attention
    var dashboardPath = NavigationPath()
    var printersPath = NavigationPath()
    var jobsPath = NavigationPath()
    var notificationsPath = NavigationPath()
    var inventoryPath = NavigationPath()
    var scanPath = NavigationPath()
    var maintenancePath = NavigationPath()
    var notificationBadgeCount: Int = 0
    var pendingReadyCount: Int = 0
    var sidebarVisibility: NavigationSplitViewVisibility = .automatic
    var pendingNFCReadyPrinterId: UUID?
    var pendingSpoolHighlightId: Int?

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
        }
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
