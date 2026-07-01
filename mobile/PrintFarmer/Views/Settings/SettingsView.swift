import SwiftUI
#if canImport(UIKit)
import UserNotifications
#endif

struct SettingsView: View {
    @Environment(AuthViewModel.self) private var authViewModel
    @Environment(ServerRegistry.self) private var serverRegistry
    @Environment(ThemeManager.self) private var themeManager
    @AppStorage("nfcTagFormat") private var nfcTagFormat: NFCTagFormat = .openPrintTag
    @State private var showLogoutConfirmation = false
    @State private var logoutTask: Task<Void, Never>?

    var body: some View {
        @Bindable var themeManager = themeManager

        NavigationStack {
            List {
                Section("Appearance") {
                    Picker("Theme", selection: $themeManager.themeMode) {
                        ForEach(ThemeMode.allCases) { mode in
                            Label(mode.displayName, systemImage: mode.icon)
                                .tag(mode)
                        }
                    }
                }

                #if canImport(UIKit)
                Section("Notifications") {
                    let pushManager = PushNotificationManager.shared
                    Toggle("Push Notifications", isOn: Binding(
                        get: { pushManager.pushEnabled },
                        set: { pushManager.pushEnabled = $0 }
                    ))

                    if pushManager.permissionStatus == .denied {
                        Label("Notifications are disabled in system Settings", systemImage: "exclamationmark.triangle")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    }

                    if let error = pushManager.registrationError {
                        Label(error, systemImage: "xmark.circle")
                            .font(.caption)
                            .foregroundStyle(.red)
                    }
                }
                #endif

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

                    NavigationLink {
                        ServersView()
                    } label: {
                        Label("Manage Servers", systemImage: "server.rack")
                    }
                }

                Section("About") {
                    LabeledContent("Version", value: AppConfig.appVersion)
                    LabeledContent("Build", value: AppConfig.buildNumber)
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
}
