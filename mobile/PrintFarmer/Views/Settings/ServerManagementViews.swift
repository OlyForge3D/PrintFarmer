import SwiftUI

struct ServersView: View {
    @Environment(ServerRegistry.self) private var registry
    @State private var viewModel: ServerManagementViewModel?
    @State private var showingAddServer = false
    @State private var editingServer: RegisteredServer?
    @State private var deletingServer: RegisteredServer?

    var body: some View {
        Group {
            if let viewModel {
                serverList(viewModel)
            } else {
                ProgressView()
            }
        }
        .navigationTitle("Servers")
        .toolbar {
            ToolbarItem(placement: .topBarTrailing) {
                Button {
                    showingAddServer = true
                } label: {
                    Label("Add Server", systemImage: "plus")
                }
            }
        }
        .task {
            if viewModel == nil {
                viewModel = ServerManagementViewModel(registry: registry)
            }
        }
        .sheet(isPresented: $showingAddServer) {
            NavigationStack {
                if let viewModel {
                    ServerEditorView(viewModel: viewModel, mode: .add)
                }
            }
        }
        .sheet(item: $editingServer) { server in
            NavigationStack {
                if let viewModel {
                    ServerEditorView(viewModel: viewModel, mode: .edit(server))
                }
            }
        }
        .confirmationDialog(
            "Delete Server?",
            isPresented: Binding(
                get: { deletingServer != nil },
                set: { if !$0 { deletingServer = nil } }
            ),
            titleVisibility: .visible,
            presenting: deletingServer
        ) { server in
            Button("Delete \(server.displayName)", role: .destructive) {
                viewModel?.delete(server)
                deletingServer = nil
            }
            Button("Cancel", role: .cancel) { deletingServer = nil }
        } message: { server in
            Text(deleteMessage(for: server))
        }
    }

    @ViewBuilder
    private func serverList(_ viewModel: ServerManagementViewModel) -> some View {
        if viewModel.servers.isEmpty {
            ContentUnavailableView {
                Label("No Servers", systemImage: "server.rack")
            } description: {
                Text("Add a PrintFarmer server before signing in.")
            } actions: {
                Button("Add Server") { showingAddServer = true }
                    .buttonStyle(.borderedProminent)
            }
            .accessibilityIdentifier("serversEmptyState")
        } else {
            List {
                Section {
                    ForEach(viewModel.servers) { server in
                        ServerRow(
                            server: server,
                            isActive: server.id == viewModel.activeServerID,
                            lastCheckedText: viewModel.lastCheckedText(for: server)
                        )
                        .swipeActions(edge: .trailing, allowsFullSwipe: false) {
                            Button(role: .destructive) {
                                deletingServer = server
                            } label: {
                                Label("Delete", systemImage: "trash")
                            }

                            Button {
                                editingServer = server
                            } label: {
                                Label("Edit", systemImage: "pencil")
                            }
                            .tint(.blue)
                        }
                        .swipeActions(edge: .leading, allowsFullSwipe: true) {
                            if server.id != viewModel.activeServerID {
                                Button {
                                    viewModel.switchToServer(server)
                                } label: {
                                    Label("Switch", systemImage: "checkmark.circle")
                                }
                                .tint(.green)
                            }
                        }
                        .contextMenu {
                            if server.id != viewModel.activeServerID {
                                Button {
                                    viewModel.switchToServer(server)
                                } label: {
                                    Label("Switch to Server", systemImage: "checkmark.circle")
                                }
                            }
                            Button {
                                editingServer = server
                            } label: {
                                Label("Edit", systemImage: "pencil")
                            }
                            Button(role: .destructive) {
                                deletingServer = server
                            } label: {
                                Label("Delete", systemImage: "trash")
                            }
                        }
                    }
                } footer: {
                    Text("Switching changes the active server selection. Runtime service rebinding is handled by the multi-server runtime layer.")
                }

                if let errorMessage = viewModel.errorMessage {
                    Section {
                        Label(errorMessage, systemImage: "exclamationmark.triangle")
                            .foregroundStyle(Color.pfError)
                            .accessibilityIdentifier("serverManagementError")
                    }
                }
            }
            .accessibilityIdentifier("serversList")
        }
    }

    private func deleteMessage(for server: RegisteredServer) -> String {
        if server.id == viewModel?.activeServerID {
            if (viewModel?.servers.count ?? 0) > 1 {
                return "This is the active server. Deleting it will switch to another registered server."
            }
            return "This is the only server. Deleting it will return you to the add-server screen before login."
        }
        return "This server will be removed from this device."
    }
}

private struct ServerRow: View {
    let server: RegisteredServer
    let isActive: Bool
    let lastCheckedText: String

    var body: some View {
        HStack(alignment: .top, spacing: 12) {
            Image(systemName: isActive ? "checkmark.circle.fill" : "server.rack")
                .foregroundStyle(isActive ? Color.pfSuccess : .secondary)
                .imageScale(.large)
                .accessibilityHidden(true)

            VStack(alignment: .leading, spacing: 4) {
                HStack(alignment: .firstTextBaseline) {
                    Text(server.displayName)
                        .font(.headline)
                    if isActive {
                        Text("Active")
                            .font(.caption.weight(.semibold))
                            .padding(.horizontal, 8)
                            .padding(.vertical, 3)
                            .background(Color.pfSuccess.opacity(0.15), in: Capsule())
                            .foregroundStyle(Color.pfSuccess)
                    }
                }

                Text(server.normalizedURLString)
                    .font(.subheadline)
                    .foregroundStyle(.secondary)
                    .textSelection(.enabled)

                Label(lastCheckedText, systemImage: healthIcon)
                    .font(.caption)
                    .foregroundStyle(healthColor)
            }
        }
        .accessibilityElement(children: .combine)
        .accessibilityLabel(accessibilityLabel)
    }

    private var healthIcon: String {
        switch server.lastKnownStatus {
        case "Reachable": "checkmark.seal"
        case "Unreachable": "exclamationmark.triangle"
        default: "clock"
        }
    }

    private var healthColor: Color {
        switch server.lastKnownStatus {
        case "Reachable": Color.pfSuccess
        case "Unreachable": Color.pfWarning
        default: .secondary
        }
    }

    private var accessibilityLabel: String {
        "\(server.displayName), \(server.normalizedURLString), \(isActive ? "active server" : "inactive server"), \(lastCheckedText)"
    }
}

struct ServerEditorView: View {
    enum PresentationMode {
        case add
        case edit(RegisteredServer)
    }

    @Environment(\.dismiss) private var dismiss
    @Bindable var viewModel: ServerManagementViewModel
    let mode: PresentationMode
    @State private var saveTask: Task<Void, Never>?
    @State private var healthTask: Task<Void, Never>?
    @FocusState private var focusedField: Field?

    private enum Field: Hashable {
        case name, url
    }

    var body: some View {
        Form {
            Section {
                TextField("Display Name", text: $viewModel.displayName)
                    .textContentType(.organizationName)
                    .focused($focusedField, equals: .name)
                    .submitLabel(.next)
                    .onSubmit { focusedField = .url }
                    .accessibilityIdentifier("serverNameField")

                TextField("https://print.example.com", text: $viewModel.serverURL)
                    .textContentType(.URL)
                    .autocorrectionDisabled()
                    #if os(iOS)
                    .textInputAutocapitalization(.never)
                    .keyboardType(.URL)
                    #endif
                    .focused($focusedField, equals: .url)
                    .submitLabel(.done)
                    .onSubmit { focusedField = nil }
                    .accessibilityIdentifier("serverURLField")
            } header: {
                Text("Server")
            } footer: {
                if let error = viewModel.formValidationError {
                    Text(error)
                        .foregroundStyle(Color.pfError)
                } else if let normalized = viewModel.normalizedURLString {
                    Text("Will connect to \(normalized).")
                }
            }

            Section {
                HStack {
                    healthStatusLabel
                    Spacer()
                    Button("Check") {
                        healthTask?.cancel()
                        healthTask = Task { await viewModel.checkHealth() }
                    }
                    .disabled(viewModel.formValidationError != nil || viewModel.healthState == .checking)
                    .accessibilityIdentifier("checkServerHealthButton")
                }
            } header: {
                Text("Reachability")
            } footer: {
                Text("PrintFarmer checks /health and /healthz. Any server HTTP response counts as reachable; network failures are shown here.")
            }

            if let errorMessage = viewModel.errorMessage {
                Section {
                    Label(errorMessage, systemImage: "exclamationmark.triangle")
                        .foregroundStyle(Color.pfError)
                        .accessibilityIdentifier("serverEditorError")
                }
            }
        }
        .navigationTitle(title)
        .navigationBarTitleDisplayMode(.inline)
        .toolbar {
            ToolbarItem(placement: .cancellationAction) {
                Button("Cancel") { dismiss() }
            }
            ToolbarItem(placement: .confirmationAction) {
                Button("Save") {
                    focusedField = nil
                    saveTask?.cancel()
                    saveTask = Task {
                        if await viewModel.save() {
                            dismiss()
                        }
                    }
                }
                .disabled(!viewModel.canSave)
                .accessibilityIdentifier("saveServerButton")
            }
        }
        .onAppear(perform: prepare)
        .onDisappear {
            saveTask?.cancel()
            healthTask?.cancel()
        }
    }

    @ViewBuilder
    private var healthStatusLabel: some View {
        switch viewModel.healthState {
        case .notChecked:
            Label("Not checked", systemImage: "clock")
                .foregroundStyle(.secondary)
        case .checking:
            Label {
                Text("Checking…")
            } icon: {
                ProgressView()
            }
        case .reachable(let message):
            Label(message, systemImage: "checkmark.seal")
                .foregroundStyle(Color.pfSuccess)
        case .unreachable(let message):
            Label(message, systemImage: "exclamationmark.triangle")
                .foregroundStyle(Color.pfWarning)
        }
    }

    private var title: String {
        switch mode {
        case .add: "Add Server"
        case .edit: "Edit Server"
        }
    }

    private func prepare() {
        switch mode {
        case .add:
            viewModel.prepareForAdd()
        case .edit(let server):
            viewModel.prepareForEdit(server)
        }
    }
}

struct AddFirstServerView: View {
    @Environment(ServerRegistry.self) private var registry
    @State private var viewModel: ServerManagementViewModel?

    var body: some View {
        NavigationStack {
            VStack(spacing: 24) {
                Image(systemName: "server.rack")
                    .font(.system(size: 48))
                    .foregroundStyle(Color.pfAccent)
                    .accessibilityHidden(true)

                VStack(spacing: 8) {
                    Text("Add Your First Server")
                        .font(.title.bold())
                        .multilineTextAlignment(.center)
                    Text("Register a PrintFarmer server before signing in. You can add more servers later from Settings.")
                        .font(.body)
                        .foregroundStyle(.secondary)
                        .multilineTextAlignment(.center)
                }
                .padding(.horizontal)

                if let viewModel {
                    ServerEditorView(viewModel: viewModel, mode: .add)
                        .clipShape(RoundedRectangle(cornerRadius: 18))
                } else {
                    ProgressView()
                }
            }
            .padding(.vertical, 24)
            .navigationTitle("Server Setup")
            .navigationBarTitleDisplayMode(.inline)
            .task {
                if viewModel == nil {
                    viewModel = ServerManagementViewModel(registry: registry)
                }
            }
        }
        .accessibilityIdentifier("addFirstServerView")
    }
}
