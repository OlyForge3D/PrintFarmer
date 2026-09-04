import SwiftUI

struct AccountDestinationRow: Identifiable, Hashable {
    let destination: AppDestination
    let title: String
    let systemImage: String
    let accessibilityHint: String
    let accessibilityIdentifier: String

    var id: AppDestination { destination }

    static func available(offlineQueueEnabled: Bool) -> [AccountDestinationRow] {
        var rows = [
            AccountDestinationRow(
                destination: .notifications,
                title: "Notifications",
                systemImage: "bell",
                accessibilityHint: "Opens notification history and status.",
                accessibilityIdentifier: "account.destination.notifications"
            ),
            AccountDestinationRow(
                destination: .settings,
                title: "Settings",
                systemImage: "gear",
                accessibilityHint: "Opens app, navigation, and account settings.",
                accessibilityIdentifier: "account.destination.settings"
            ),
            AccountDestinationRow(
                destination: .manageServers,
                title: "Manage Servers",
                systemImage: "server.rack",
                accessibilityHint: "Adds, edits, or switches PrintFarmer servers.",
                accessibilityIdentifier: "account.destination.manageServers"
            )
        ]

        if offlineQueueEnabled {
            rows.append(
                AccountDestinationRow(
                    destination: .offlineQueue,
                    title: "Offline Queue",
                    systemImage: "tray.full",
                    accessibilityHint: "Reviews and retries writes waiting to sync.",
                    accessibilityIdentifier: "account.destination.offlineQueue"
                )
            )
        }

        return rows
    }
}

struct AccountView: View {
    @Environment(ServiceContainer.self) private var services

    var body: some View {
        let rows = AccountDestinationRow.available(
            offlineQueueEnabled: services.capabilitiesService.resolved.offlineWriteReplayEnabled
        )

        List(rows) { row in
            NavigationLink(value: row.destination) {
                Label(row.title, systemImage: row.systemImage)
                    .frame(
                        maxWidth: .infinity,
                        minHeight: RootNavigationChrome.minimumTouchTarget,
                        alignment: .leading
                    )
            }
            .accessibilityLabel(row.title)
            .accessibilityHint(row.accessibilityHint)
            .accessibilityIdentifier(row.accessibilityIdentifier)
        }
        .listStyle(.insetGrouped)
        .navigationTitle("Account")
        #if os(iOS)
        .navigationBarTitleDisplayMode(.inline)
        #endif
        .accessibilityIdentifier(RootNavigationChrome.accountContainerIdentifier)
        .task {
            await services.capabilitiesService.refresh()
        }
    }
}
