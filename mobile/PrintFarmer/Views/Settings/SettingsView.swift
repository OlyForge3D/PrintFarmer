import SwiftUI
#if canImport(UIKit)
import UserNotifications
#endif

struct SettingsView: View {
    @Environment(AuthViewModel.self) private var authViewModel
    @Environment(ThemeManager.self) private var themeManager
    @AppStorage("nfcTagFormat") private var nfcTagFormat: NFCTagFormat = .openPrintTag
    @State private var showLogoutConfirmation = false
    @State private var showChangeURL = false
    @State private var newServerURL = ""
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
                    let savedURL = APIClient.savedServerURLString() ?? "Not configured"
                    LabeledContent("API URL", value: savedURL)

                    Button {
                        newServerURL = APIClient.savedServerURLString() ?? ""
                        showChangeURL = true
                    } label: {
                        Label("Change Server URL", systemImage: "link")
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
            .alert("Change Server URL", isPresented: $showChangeURL) {
                TextField("https://print.example.com", text: $newServerURL)
                    .autocorrectionDisabled()
                    #if os(iOS)
                    .textInputAutocapitalization(.never)
                    .keyboardType(.URL)
                    #endif
                Button("Save") {
                    let trimmed = newServerURL.trimmingCharacters(in: .whitespacesAndNewlines)
                    if let normalized = APIClient.normalizedServerURLString(trimmed) {
                        UserDefaults.standard.set(normalized, forKey: APIClient.serverURLKey)
                        logoutTask = Task { await authViewModel.logout() }
                    }
                }
                Button("Cancel", role: .cancel) {}
            } message: {
                Text("Enter the URL of your Printfarmer server. You will need to sign in again.")
            }
            .onDisappear { logoutTask?.cancel() }
        }
    }
}
