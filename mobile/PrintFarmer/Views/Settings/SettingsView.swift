import SwiftUI

struct SettingsView: View {
    @Environment(AuthViewModel.self) private var authViewModel
    @Environment(ServiceContainer.self) private var services
    @Environment(ServerRegistry.self) private var serverRegistry
    @Environment(ThemeManager.self) private var themeManager
    private let ownsNavigationStack: Bool
    @AppStorage("nfcTagFormat") private var nfcTagFormat: NFCTagFormat = .openPrintTag
    @State private var showLogoutConfirmation = false
    @State private var logoutTask: Task<Void, Never>?

    init(ownsNavigationStack: Bool = true) {
        self.ownsNavigationStack = ownsNavigationStack
    }

    var body: some View {
        Group {
            if ownsNavigationStack {
                NavigationStack {
                    screenContent
                        .navigationDestination(for: AppDestination.self) { destination in
                            destinationView(for: destination)
                        }
                }
            } else {
                screenContent
            }
        }
    }

    private var screenContent: some View {
        @Bindable var themeManager = themeManager

        return List {
                Section("Appearance") {
                    Picker("Theme", selection: $themeManager.themeMode) {
                        ForEach(ThemeMode.allCases) { mode in
                            Label(mode.displayName, systemImage: mode.icon)
                                .tag(mode)
                        }
                    }
                }

                Section {
                    Picker("Write Format", selection: $nfcTagFormat) {
                        ForEach(NFCTagFormat.allCases) { format in
                            Text(format.rawValue).tag(format)
                        }
                    }

                    Text(nfcTagFormat.description)
                        .font(.caption)
                        .foregroundStyle(.secondary)
                } header: {
                    Text("NFC Tags")
                }

                Section("Account") {
                    if let user = authViewModel.currentUser {
                        LabeledContent("Username", value: user.username)
                        LabeledContent("Email", value: user.email)

                        if !user.roles.isEmpty {
                            LabeledContent("Roles", value: user.roles.joined(separator: ", "))
                        }
                    }

                    Button("Sign Out", role: .destructive) {
                        showLogoutConfirmation = true
                    }
                }

                Section("Server") {
                    if let activeServer = serverRegistry.activeServer {
                        LabeledContent("Active", value: activeServer.displayName)
                        LabeledContent("API URL", value: activeServer.normalizedURLString)
                    } else {
                        LabeledContent("API URL", value: "Not configured")
                    }

                }

                Section("About") {
                    LabeledContent("Version", value: AppConfig.appVersion)
                    LabeledContent("Build", value: AppConfig.buildNumber)
                    Link("Privacy Policy", destination: URL(string: "https://olyforge3d.github.io/PrintFarmer/privacy/")!)
                }

                if DemoMode.shared.isActive {
                    Section {
                        Button(role: .destructive) {
                            logoutTask = Task { await authViewModel.exitDemoMode() }
                        } label: {
                            Label("Exit Demo Mode", systemImage: "arrow.left.circle")
                        }
                    } footer: {
                        Text("Return to login and connect with real credentials.")
                    }
                }
        }
        .navigationTitle("Settings")
        .task {
            await services.capabilitiesService.refresh()
        }
        .confirmationDialog("Sign Out?", isPresented: $showLogoutConfirmation, titleVisibility: .visible) {
            Button("Sign Out", role: .destructive) {
                logoutTask = Task { await authViewModel.logout() }
            }
            Button("Cancel", role: .cancel) {}
        } message: {
            Text("You will need to sign in again to access your print farm.")
        }
        .onDisappear { logoutTask?.cancel() }
    }
}
