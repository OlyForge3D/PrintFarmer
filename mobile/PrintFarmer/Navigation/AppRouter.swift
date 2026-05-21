import SwiftUI

@MainActor @Observable
final class AppRouter {
    var selectedTab: AppTab = .dashboard
    var dashboardPath = NavigationPath()
    var printersPath = NavigationPath()
    var jobsPath = NavigationPath()
    var notificationsPath = NavigationPath()
    var inventoryPath = NavigationPath()
    var maintenancePath = NavigationPath()
    var notificationBadgeCount: Int = 0
    var pendingReadyCount: Int = 0
    var sidebarVisibility: NavigationSplitViewVisibility = .automatic
    var pendingNFCReadyPrinterId: UUID?
    var pendingSpoolHighlightId: Int?

    func navigate(to destination: DeepLinkDestination) {
        switch destination {
        case .printerDetail(let id):
            selectedTab = .printers
            printersPath = NavigationPath()
            Task { @MainActor in
                try? await Task.sleep(for: .milliseconds(50))
                printersPath.append(AppDestination.printerDetail(id: id))
            }
        case .printerReady(let id):
            selectedTab = .printers
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
        case .dashboard: dashboardPath = NavigationPath()
        case .printers: printersPath = NavigationPath()
        case .jobs: jobsPath = NavigationPath()
        case .notifications: notificationsPath = NavigationPath()
        case .inventory: inventoryPath = NavigationPath()
        case .maintenance: maintenancePath = NavigationPath()
        case .settings: break
        }
    }
}

enum AppTab: Hashable {
    case dashboard
    case printers
    case jobs
    case notifications
    case inventory
    case maintenance
    case settings
}
