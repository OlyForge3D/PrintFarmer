import SwiftUI

struct LoginView: View {
    @Environment(AuthViewModel.self) private var authViewModel
    @Environment(ServerRegistry.self) private var serverRegistry
    @Environment(\.horizontalSizeClass) private var sizeClass
    @State private var viewModel = LoginViewModel()
    @State private var appliedActiveServerID: UUID?
    @State private var loginTask: Task<Void, Never>?
    @FocusState private var focusedField: LoginField?

    private enum LoginField: Hashable {
        case serverURL, username, password
    }

    private var serverSwitcherViewModel: ServerSwitcherViewModel {
        ServerSwitcherViewModel(servers: serverRegistry.servers, activeServerID: serverRegistry.activeServerID)
    }

    var body: some View {
        ScrollView {
            VStack(spacing: 28) {
                Spacer(minLength: 40)

                // MARK: - Branding
                brandingSection

                // MARK: - Server URL
                serverSection

                // MARK: - Credentials
                credentialFields

                // MARK: - Error
                errorBanner

                // MARK: - Sign In
                signInButton

                // MARK: - Demo Mode
                demoModeButton

                Spacer(minLength: 60)
            }
            .padding(.horizontal, 32)
            .frame(maxWidth: sizeClass == .regular ? 500 : .infinity)
        }
        .scrollDismissesKeyboard(.interactively)
        .contentShape(Rectangle())
        .onTapGesture { focusedField = nil }
        .animation(.easeInOut(duration: 0.2), value: authViewModel.errorMessage)
        .animation(.easeInOut(duration: 0.2), value: viewModel.isServerURLExpanded)
        .onAppear {
            applyActiveServerIfNeeded()
        }
        .onChange(of: serverRegistry.activeServerID) {
            applyActiveServerIfNeeded()
        }
        .onDisappear { loginTask?.cancel() }
    }

    // MARK: - Branding

    private var brandingSection: some View {
        VStack(spacing: 12) {
            Image("AppLogo")
                .resizable()
                .scaledToFit()
                .frame(width: 56, height: 56)
                .clipShape(RoundedRectangle(cornerRadius: 12))

            Text("PrintFarmer")
                .font(.largeTitle.bold())

            Text("3D Print Farm Management")
                .font(.subheadline)
                .foregroundStyle(.secondary)
        }
        .padding(.bottom, 8)
    }

    // MARK: - Server

    private var serverSection: some View {
        VStack(alignment: .leading, spacing: 8) {
            Button {
                withAnimation { viewModel.isServerURLExpanded.toggle() }
            } label: {
                HStack {
                    Label("Server", systemImage: "server.rack")
                        .font(.subheadline.weight(.medium))
                        .foregroundStyle(.secondary)
                    Spacer()
                    if !viewModel.serverURL.isEmpty && !viewModel.isServerURLExpanded {
                        Text(viewModel.serverURL)
                            .font(.caption)
                            .foregroundStyle(.tertiary)
                            .lineLimit(1)
                    }
                    Image(systemName: "chevron.right")
                        .font(.caption2.weight(.semibold))
                        .foregroundStyle(.tertiary)
                        .rotationEffect(.degrees(viewModel.isServerURLExpanded ? 90 : 0))
                }
            }
            .buttonStyle(.plain)

            loginServerPicker

            if viewModel.isServerURLExpanded {
                TextField("https://print.example.com", text: $viewModel.serverURL)
                    .textContentType(.URL)
                    .autocorrectionDisabled()
                    #if os(iOS)
                    .textInputAutocapitalization(.never)
                    .keyboardType(.URL)
                    #endif
                    .textFieldStyle(.roundedBorder)
                    .focused($focusedField, equals: .serverURL)
                    .submitLabel(.next)
                    .onSubmit { focusedField = .username }
            }
        }
        .padding(12)
        .background(Color.pfCard, in: RoundedRectangle(cornerRadius: 10))
    }

    @ViewBuilder
    private var loginServerPicker: some View {
        if !serverRegistry.servers.isEmpty {
            let switcherViewModel = serverSwitcherViewModel

            if switcherViewModel.items.count > 1 {
                Menu {
                    ForEach(switcherViewModel.items) { item in
                        Button {
                            try? switcherViewModel.activate(item.id, registry: serverRegistry)
                        } label: {
                            Label(
                                "\(item.displayName)\n\(item.normalizedURLString)",
                                systemImage: item.isActive ? "checkmark" : "server.rack"
                            )
                        }
                        .disabled(item.isActive)
                        .accessibilityLabel(item.accessibilityLabel)
                        .accessibilityValue(item.normalizedURLString)
                    }
                } label: {
                    serverPickerLabel(for: switcherViewModel)
                }
                .buttonStyle(.plain)
                .accessibilityLabel(switcherViewModel.switcherAccessibilityLabel)
                .accessibilityValue(switcherViewModel.items.first(where: { $0.isActive })?.normalizedURLString ?? "")
            } else if let item = switcherViewModel.items.first {
                if item.isActive {
                    serverPickerLabel(displayName: item.displayName, normalizedURLString: item.normalizedURLString)
                        .accessibilityElement(children: .combine)
                        .accessibilityLabel(item.accessibilityLabel)
                        .accessibilityValue(item.normalizedURLString)
                } else {
                    Button {
                        try? switcherViewModel.activate(item.id, registry: serverRegistry)
                    } label: {
                        serverPickerLabel(displayName: item.displayName, normalizedURLString: item.normalizedURLString)
                    }
                    .buttonStyle(.plain)
                    .accessibilityElement(children: .combine)
                    .accessibilityLabel(item.accessibilityLabel)
                    .accessibilityValue(item.normalizedURLString)
                }
            }
        }
    }

    private func serverPickerLabel(for switcherViewModel: ServerSwitcherViewModel) -> some View {
        let activeItem = switcherViewModel.items.first { $0.isActive } ?? switcherViewModel.items.first
        return serverPickerLabel(
            displayName: activeItem?.displayName ?? switcherViewModel.activeServerName,
            normalizedURLString: activeItem?.normalizedURLString ?? viewModel.serverURL,
            showsChevron: true
        )
    }

    private func serverPickerLabel(
        displayName: String,
        normalizedURLString: String,
        showsChevron: Bool = false
    ) -> some View {
        HStack(spacing: 10) {
            Image(systemName: "server.rack")
                .font(.subheadline)
                .foregroundStyle(.secondary)
                .accessibilityHidden(true)

            VStack(alignment: .leading, spacing: 2) {
                Text(displayName)
                    .font(.subheadline.weight(.semibold))
                    .foregroundStyle(.primary)
                    .lineLimit(1)

                Text(normalizedURLString)
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
            }

            Spacer(minLength: 8)

            if showsChevron {
                Image(systemName: "chevron.down")
                    .font(.caption.weight(.bold))
                    .foregroundStyle(.secondary)
                    .accessibilityHidden(true)
            }
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 8)
        .frame(maxWidth: .infinity, minHeight: 44, alignment: .leading)
        .background(Color.accentColor.opacity(0.08), in: RoundedRectangle(cornerRadius: 10, style: .continuous))
        .contentShape(Rectangle())
    }

    // MARK: - Credentials

    private var credentialFields: some View {
        VStack(spacing: 14) {
            TextField("Username or Email", text: $viewModel.usernameOrEmail)
                .textContentType(.username)
                .autocorrectionDisabled()
                #if os(iOS)
                .textInputAutocapitalization(.never)
                #endif
                .textFieldStyle(.roundedBorder)
                .focused($focusedField, equals: .username)
                .submitLabel(.next)
                .onSubmit { focusedField = .password }

            SecureField("Password", text: $viewModel.password)
                .textContentType(.password)
                .textFieldStyle(.roundedBorder)
                .focused($focusedField, equals: .password)
                .submitLabel(.go)
                .onSubmit { attemptLogin() }
        }
    }

    // MARK: - Error

    @ViewBuilder
    private var errorBanner: some View {
        if let error = authViewModel.errorMessage {
            HStack(spacing: 8) {
                Image(systemName: "exclamationmark.triangle.fill")
                    .foregroundStyle(Color.pfError)
                Text(error)
                    .font(.callout)
            }
            .foregroundStyle(Color.pfError)
            .padding(12)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(Color.pfError.opacity(0.1), in: RoundedRectangle(cornerRadius: 8))
            .transition(.opacity.combined(with: .move(edge: .top)))
        }
    }

    // MARK: - Sign In Button

    private var signInButton: some View {
        Button(action: attemptLogin) {
            Group {
                if authViewModel.isLoading {
                    ProgressView()
                        .tint(.white)
                } else {
                    Text("Sign In")
                        .fontWeight(.semibold)
                }
            }
            .fullWidthActionButton(prominence: .prominent)
        }
        .buttonStyle(.borderedProminent)
        .disabled(!viewModel.isFormValid || authViewModel.isLoading)
    }

    // MARK: - Demo Mode

    private var demoModeButton: some View {
        Button {
            authViewModel.loginAsDemo()
        } label: {
            HStack(spacing: 6) {
                Image(systemName: "play.circle")
                Text("Try Demo Mode")
            }
            .font(.subheadline)
            .foregroundStyle(.secondary)
        }
        .buttonStyle(.plain)
        .padding(.top, 4)
    }

    // MARK: - Actions

    private func attemptLogin() {
        guard viewModel.isFormValid, !authViewModel.isLoading else { return }
        focusedField = nil
        loginTask = Task { await viewModel.login(using: authViewModel) }
    }

    private func applyActiveServerIfNeeded() {
        guard let activeServer = serverRegistry.activeServer,
              appliedActiveServerID != activeServer.id else { return }
        viewModel = LoginViewModel(activeServer: activeServer)
        appliedActiveServerID = activeServer.id
    }
}
