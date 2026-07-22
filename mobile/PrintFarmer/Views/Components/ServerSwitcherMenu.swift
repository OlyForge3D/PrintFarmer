import SwiftUI

enum ServerSwitcherMenuStyle {
    case toolbar
    case sidebar
}

struct ServerSwitcherMenu: View {
    @Environment(ServerRegistry.self) private var registry
    @State private var showingServers = false

    let style: ServerSwitcherMenuStyle

    private var viewModel: ServerSwitcherViewModel {
        ServerSwitcherViewModel(servers: registry.servers, activeServerID: registry.activeServerID)
    }

    var body: some View {
        if viewModel.isVisible {
            Menu {
                ForEach(viewModel.items) { item in
                    Button {
                        try? viewModel.activate(item.id, registry: registry)
                    } label: {
                        Label(item.displayName, systemImage: item.isActive ? "checkmark" : "server.rack")
                    }
                    .disabled(item.isActive)
                    .accessibilityLabel(item.accessibilityLabel)
                }

                Divider()

                Button {
                    showingServers = true
                } label: {
                    Label("Manage Servers…", systemImage: "slider.horizontal.3")
                }
                .accessibilityLabel("Manage servers")
            } label: {
                switcherLabel
            }
            .buttonStyle(.plain)
            .accessibilityLabel(viewModel.switcherAccessibilityLabel)
            .sheet(isPresented: $showingServers) {
                NavigationStack {
                    ServersView()
                }
            }
        }
    }

    @ViewBuilder
    private var switcherLabel: some View {
        switch style {
        case .toolbar:
            HStack(spacing: 4) {
                Text(viewModel.activeServerName)
                    .font(.subheadline.weight(.semibold))
                    .lineLimit(1)
                    .minimumScaleFactor(0.8)

                Image(systemName: "chevron.down")
                    .font(.caption2.weight(.bold))
                    .accessibilityHidden(true)
            }
            .padding(.horizontal, 10)
            .frame(minHeight: 44)
            .contentShape(Rectangle())

        case .sidebar:
            HStack(spacing: 10) {
                Image(systemName: "server.rack")
                    .font(.headline)
                    .accessibilityHidden(true)

                VStack(alignment: .leading, spacing: 2) {
                    Text("Server")
                        .font(.caption)
                        .foregroundStyle(.secondary)

                    Text(viewModel.activeServerName)
                        .font(.subheadline.weight(.semibold))
                        .lineLimit(1)
                }

                Spacer(minLength: 8)

                Image(systemName: "chevron.down")
                    .font(.caption.weight(.bold))
                    .foregroundStyle(.secondary)
                    .accessibilityHidden(true)
            }
            .padding(.horizontal, 12)
            .padding(.vertical, 8)
            .frame(maxWidth: .infinity, minHeight: 44, alignment: .leading)
            .background(Color.accentColor.opacity(0.10), in: RoundedRectangle(cornerRadius: 12, style: .continuous))
            .contentShape(Rectangle())
        }
    }
}
