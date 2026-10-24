import SwiftUI

/// Canonical view resolver shared by every `AppDestination` stack.
@MainActor @ViewBuilder
func destinationView(for destination: AppDestination) -> some View {
    switch destination {
    case .printerDetail(let id):
        PrinterDetailView(printerId: id)
    case .jobDetail(let id):
        JobDetailView(jobId: id)
    case .account:
        AccountView()
    case .notifications:
        NotificationsView(ownsNavigationStack: false)
    case .settings:
        SettingsView(ownsNavigationStack: false)
    case .jobQueue:
        JobListView(ownsNavigationStack: false)
    case .offlineQueue:
        OfflineQueueStatusView()
    case .manageServers:
        ServersView()
    }
}
