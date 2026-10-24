import SwiftUI

@MainActor
enum PrinterControlsAccess {
    static func matchesComposition(
        _ model: PrinterControlsViewModel,
        selectedServerID: UUID?,
        composition: PrinterControlsComposition?
    ) -> Bool {
        model.registeredServerID != nil
            && selectedServerID == model.registeredServerID
            && model.matchesComposition(composition)
    }

    static func isEntryVisible(for printer: Printer) -> Bool {
        !PrinterControlsSection.isHidden(for: printer)
    }

    static func blockedReason(
        authenticated: Bool, ready: Bool, user: UserDTO?,
        sameServer: Bool
    ) -> String? {
        guard sameServer else { return "Server changed. Reopen this printer." }
        guard authenticated, ready, let user, user.isActive else {
            return "Sign in and wait for this server to be ready."
        }
        guard user.permissions.contains("queue:start") || user.roles.contains("farm_admin") else {
            return "Printer controls require Queue.Start permission."
        }
        return nil
    }
}

/// Installed on the owner host, not a pager child: changing tabs never cancels
/// routine work. Leaving the detail or changing authority invalidates observation,
/// but an outstanding request's registered-server/printer lease survives new owners.
struct PrinterControlsAccessLifecycle: ViewModifier {
    let viewModel: PrinterControlsViewModel?
    @Environment(ServerRegistry.self) private var registry
    @Environment(ServiceContainer.self) private var services
    @Environment(AuthViewModel.self) private var auth
    @Environment(\.scenePhase) private var scenePhase

    private var accessSignal: String {
        "\(registry.activeServerID?.uuidString ?? "")|\(services.activeServerGeneration)|\(String(describing: services.printerControlsComposition?.identity))|\(services.authOperationEpoch.current)|\(auth.isAuthenticated)|\(auth.snapshotActivationPending)|\(auth.currentUser?.id.uuidString ?? "")|\(auth.currentUser?.isActive ?? false)|\(auth.currentUser?.permissions ?? [])|\(auth.currentUser?.roles ?? [])"
    }

    func body(content: Content) -> some View {
        content
            .onAppear { configure() }
            .onChange(of: viewModel.map(ObjectIdentifier.init)) { _, _ in configure() }
            .onChange(of: accessSignal) { _, _ in viewModel?.refreshAccess() }
            .onChange(of: scenePhase) { _, _ in
                guard scenePhase == .active, let viewModel else { return }
                configure()
                viewModel.refreshAccess()
            }
            .onDisappear { viewModel?.deactivate() }
    }

    private func configure() {
        guard let viewModel else { return }
        let serverID = viewModel.registeredServerID
        let userID = auth.currentUser?.id
        let authEpoch = services.authOperationEpoch.current
        viewModel.configureAccess(serverID: serverID) { [weak viewModel, registry, services, auth] in
            PrinterControlsAccess.blockedReason(
                authenticated: auth.isAuthenticated,
                ready: !auth.snapshotActivationPending,
                user: auth.currentUser,
                sameServer: viewModel.map {
                    PrinterControlsAccess.matchesComposition(
                        $0, selectedServerID: registry.activeServerID,
                        composition: services.printerControlsComposition
                    )
                } == true
                    && auth.currentUser?.id == userID
                    && services.authOperationEpoch.current == authEpoch
            )
        }
    }
}
