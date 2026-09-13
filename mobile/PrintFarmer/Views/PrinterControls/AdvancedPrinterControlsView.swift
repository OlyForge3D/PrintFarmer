import SwiftUI

@MainActor
enum AdvancedPrinterControlsAccess {
    static func matchesComposition(
        _ model: PrinterControlsViewModel,
        selectedServerID: UUID?,
        composition: PrinterControlsComposition?
    ) -> Bool {
        model.registeredServerID != nil
            && selectedServerID == model.registeredServerID
            && model.matchesComposition(composition)
    }

    static func isEntryVisible(isEnabled: Bool, for printer: Printer) -> Bool {
        isEnabled && !PrinterControlsSection.isHidden(for: printer)
    }

    static func blockedReason(
        enabled: Bool, authenticated: Bool, ready: Bool, user: UserDTO?,
        sameServer: Bool
    ) -> String? {
        guard sameServer else { return "Server changed. Reopen this printer." }
        guard enabled else { return "Enable printer controls for this server in Settings." }
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
        "\(registry.activeServerID?.uuidString ?? "")|\(services.activeServerGeneration)|\(String(describing: services.printerControlsComposition?.identity))|\(services.authOperationEpoch.current)|\(registry.advancedPrinterControlsEnabled)|\(auth.isAuthenticated)|\(auth.snapshotActivationPending)|\(auth.currentUser?.id.uuidString ?? "")|\(auth.currentUser?.isActive ?? false)|\(auth.currentUser?.permissions ?? [])|\(auth.currentUser?.roles ?? [])"
    }

    func body(content: Content) -> some View {
        content
            .onAppear { configure() }
            .onChange(of: viewModel.map(ObjectIdentifier.init)) { _, _ in configure() }
            .onChange(of: accessSignal) { _, _ in viewModel?.refreshAccess() }
            .task(id: "\(accessSignal)|\(scenePhase)|\(viewModel.map(ObjectIdentifier.init).map(String.init(describing:)) ?? "")") {
                guard scenePhase == .active, let viewModel else { return }
                configure()
                await viewModel.refreshControlOperation()
                while !Task.isCancelled && viewModel.isActive {
                    do { try await Task.sleep(for: .seconds(2)) } catch { return }
                    // Only status reads, never a retry of a physical request.
                    await viewModel.refreshControlOperation()
                }
            }
            .onDisappear { viewModel?.deactivate() }
    }

    private func configure() {
        guard let viewModel else { return }
        let serverID = viewModel.registeredServerID
        let userID = auth.currentUser?.id
        let authEpoch = services.authOperationEpoch.current
        let serverURL = registry.activeServer?.id == serverID ? registry.activeServer?.baseURL : nil
        viewModel.configureAccess(serverID: serverID, userID: userID, serverURL: serverURL) { [weak viewModel, registry, services, auth] in
            AdvancedPrinterControlsAccess.blockedReason(
                enabled: registry.advancedPrinterControlsEnabled,
                authenticated: auth.isAuthenticated,
                ready: !auth.snapshotActivationPending,
                user: auth.currentUser,
                sameServer: viewModel.map {
                    AdvancedPrinterControlsAccess.matchesComposition(
                        $0, selectedServerID: registry.activeServerID,
                        composition: services.printerControlsComposition
                    )
                } == true
                    && auth.currentUser?.id == userID
                    && services.authOperationEpoch.current == authEpoch
            )
        }
        viewModel.observeControlOperations(using: services.signalRService)
    }
}

/// Advanced printer controls surface.
///
/// F1 (#706) moves jog/preheat/z-offset/home/disable-motor controls off
/// the printer detail scroll and gates them behind this dedicated
/// "Advanced" destination. This ensures cockpit controls are never
/// visible in list, card, or attention contexts and require an explicit
/// tap-through from Printer Detail.
struct AdvancedPrinterControlsView: View {
    @Environment(ServiceContainer.self) private var services
    @Environment(ServerRegistry.self) private var serverRegistry
    @Environment(\.dismiss) private var dismiss
    let printer: Printer

    var body: some View {
        Group {
            if serverRegistry.advancedPrinterControlsEnabled {
                ScrollView {
                    VStack(alignment: .leading, spacing: 20) {
                        header

                        if let composition = services.printerControlsComposition {
                            PrinterControlsSection(printer: printer, composition: composition)
                                .id(composition.identity)
                        } else {
                            Text("Controls are unavailable while the registered server connection is changing.")
                                .font(.footnote)
                        }
                    }
                    .padding()
                }
            } else {
                Color.clear
                    .accessibilityHidden(true)
            }
        }
        .navigationTitle("Advanced")
        #if os(iOS)
        .navigationBarTitleDisplayMode(.inline)
        #endif
        .onAppear {
            dismissIfDisabled()
        }
        .onChange(of: serverRegistry.advancedPrinterControlsEnabled) { _, _ in
            dismissIfDisabled()
        }
    }

    private func dismissIfDisabled() {
        if !serverRegistry.advancedPrinterControlsEnabled {
            dismiss()
        }
    }

    private var header: some View {
        VStack(alignment: .leading, spacing: 8) {
            Label("Advanced Controls", systemImage: "slider.horizontal.3")
                .font(.title3.weight(.semibold))
                .foregroundStyle(Color.pfTextPrimary)

            Text("Jog, preheat, and homing are for setup and maintenance. Controls are disabled while a print is active.")
                .font(.footnote)
                .foregroundStyle(.secondary)
        }
        .accessibilityElement(children: .combine)
    }
}
