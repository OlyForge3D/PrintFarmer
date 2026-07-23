import SwiftUI

/// Global, slim status bar that surfaces the app's live-connection state
/// (REST reachability + SignalR hub) using color-coded iconography.
///
/// Rendered near the top of the app shell (below the demo banner) so it is
/// visible on every screen. Tapping it explains the current state.
struct ConnectionStatusBar: View {
    let monitor: ConnectionMonitor
    @State private var showingDetail = false

    var body: some View {
        Button {
            showingDetail = true
        } label: {
            HStack(spacing: 6) {
                Image(systemName: iconName)
                    .font(.caption2)
                Text(label)
                    .font(.caption2.weight(.semibold))
                    .tracking(0.3)
            }
            .foregroundStyle(.white)
            .frame(maxWidth: .infinity)
            .padding(.vertical, 3)
            .background(background)
        }
        .buttonStyle(.plain)
        .accessibilityElement(children: .combine)
        .accessibilityLabel("Server connection: \(label)")
        .accessibilityHint("Shows details about the connection to your server.")
        .alert("Connection", isPresented: $showingDetail) {
            Button("OK", role: .cancel) {}
        } message: {
            Text(detailMessage)
        }
    }

    private var iconName: String {
        switch monitor.status {
        case .connected: return "wifi"
        case .connecting: return "wifi"
        case .degraded: return "wifi.exclamationmark"
        case .offline: return "wifi.slash"
        }
    }

    private var label: String {
        switch monitor.status {
        case .connected: return "Connected"
        case .connecting: return "Connecting…"
        case .degraded: return "Live updates paused"
        case .offline: return "Offline"
        }
    }

    private var background: Color {
        switch monitor.status {
        case .connected: return .pfSuccess
        case .connecting: return .pfTextTertiary
        case .degraded: return .pfWarning
        case .offline: return .pfError
        }
    }

    private var detailMessage: String {
        switch monitor.status {
        case .connected:
            return "Connected to your server. Live updates are active."
        case .connecting:
            return "Connecting to your server…"
        case .degraded:
            return "Your server is reachable, but real-time updates are paused. Pull to refresh to update manually while the app reconnects."
        case .offline:
            return "Can't reach your server. Check that the server is running and that your device is on the same network."
        }
    }
}
