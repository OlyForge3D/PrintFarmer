import SwiftUI

/// Pure, deterministic derivation of the connection/stale banner content.
///
/// Extracted from the view so the four banner states — connected, degraded,
/// offline-with-cache, and offline-without-cache — can be unit-tested without
/// rendering SwiftUI. Staleness is conveyed by TEXT and an explicit
/// accessibility label (never color alone), and the last-confirmed timestamp is
/// surfaced whenever cached fleet data is being shown while not live.
struct ConnectionStatusPresentation: Equatable {
    let iconName: String
    /// Short label rendered in the bar.
    let label: String
    /// Optional secondary line (e.g. the last-confirmed timestamp) shown when
    /// cached data is on screen. Non-nil implies a stale/cached shell.
    let timestampText: String?
    /// Full spoken accessibility label — always encodes staleness in words.
    let accessibilityLabel: String
    /// Longer explanation shown in the tap-through alert.
    let detailMessage: String
    /// True when the banner represents stale, read-only cached data.
    let isStale: Bool

    init(status: ConnectionStatus,
         lastConfirmedAt: Date? = nil,
         hasCache: Bool = false,
         now: Date = Date(),
         calendar: Calendar = .current) {
        let staleShell = (status == .offline || status == .degraded) && hasCache
        self.isStale = staleShell

        let confirmed = lastConfirmedAt.map {
            Self.formatConfirmed($0, now: now, calendar: calendar)
        }

        switch status {
        case .connected:
            iconName = "wifi"
            label = "Connected"
            timestampText = nil
            accessibilityLabel = "Server connection: Connected. Live updates are active."
            detailMessage = "Connected to your server. Live updates are active."
        case .connecting:
            iconName = "wifi"
            label = "Connecting…"
            timestampText = nil
            accessibilityLabel = "Server connection: Connecting to your server."
            detailMessage = "Connecting to your server…"
        case .degraded:
            iconName = "wifi.exclamationmark"
            if staleShell {
                label = "Live updates paused · Showing cached fleet"
                timestampText = confirmed.map { "Last updated \($0)" }
                let spokenTime = confirmed.map { " Last updated \($0)." } ?? ""
                accessibilityLabel = "Server connection: Live updates paused. Showing cached, read-only fleet data.\(spokenTime)"
            } else {
                label = "Live updates paused"
                timestampText = nil
                accessibilityLabel = "Server connection: Live updates paused."
            }
            detailMessage = "Your server is reachable, but real-time updates are paused. Pull to refresh to update manually while the app reconnects."
        case .offline:
            iconName = "wifi.slash"
            if staleShell {
                label = "Offline · Showing cached fleet"
                timestampText = confirmed.map { "Last updated \($0)" }
                let spokenTime = confirmed.map { " Last updated \($0)." } ?? ""
                accessibilityLabel = "Server connection: Offline. Showing cached, read-only fleet data.\(spokenTime)"
            } else {
                label = "Offline"
                timestampText = hasCache ? nil : "No cached data"
                accessibilityLabel = hasCache
                    ? "Server connection: Offline."
                    : "Server connection: Offline. No cached fleet data available."
            }
            detailMessage = "Can't reach your server. Check that the server is running and that your device is on the same network."
        }
    }

    /// Bar background color. Color is decorative only — staleness is always
    /// carried by `label`/`accessibilityLabel` text as well.
    var barBackground: Color {
        if label.hasPrefix("Connected") { return .pfSuccess }
        if label.hasPrefix("Connecting") { return .pfTextTertiary }
        if iconName == "wifi.slash" { return .pfError }
        return .pfWarning
    }

    /// Relative, human-readable rendering of the last-confirmed instant.
    static func formatConfirmed(_ date: Date, now: Date, calendar: Calendar) -> String {
        let seconds = now.timeIntervalSince(date)
        if seconds < 60 { return "just now" }
        let minutes = Int(seconds / 60)
        if minutes < 60 { return "\(minutes) min ago" }
        let df = DateFormatter()
        df.calendar = calendar
        if calendar.isDate(date, inSameDayAs: now) {
            df.timeStyle = .short
            df.dateStyle = .none
            return "at \(df.string(from: date))"
        }
        df.timeStyle = .short
        df.dateStyle = .short
        return "on \(df.string(from: date))"
    }
}

/// Global, slim status bar that surfaces the app's live-connection state
/// (REST reachability + SignalR hub).
///
/// Rendered near the top of the app shell so it is visible on every screen.
/// When cached, read-only fleet data is being shown (cold/offline launch) it
/// also carries the last-confirmed timestamp — conveyed by text + accessibility,
/// never color alone. Tapping it explains the current state.
struct ConnectionStatusBar: View {
    private let monitor: ConnectionMonitor?
    private let explicitStatus: ConnectionStatus?
    private let lastConfirmedAt: Date?
    private let hasCache: Bool
    @State private var showingDetail = false

    /// Reactive global bar driven by the live connection monitor.
    init(monitor: ConnectionMonitor, lastConfirmedAt: Date? = nil, hasCache: Bool = false) {
        self.monitor = monitor
        self.explicitStatus = nil
        self.lastConfirmedAt = lastConfirmedAt
        self.hasCache = hasCache
    }

    /// Deterministic embedded banner (e.g. inside the cold-offline shell) driven
    /// by an explicit status rather than a live monitor.
    init(status: ConnectionStatus, lastConfirmedAt: Date? = nil, hasCache: Bool = false) {
        self.monitor = nil
        self.explicitStatus = status
        self.lastConfirmedAt = lastConfirmedAt
        self.hasCache = hasCache
    }

    private var status: ConnectionStatus {
        explicitStatus ?? monitor?.status ?? .connecting
    }

    private var presentation: ConnectionStatusPresentation {
        ConnectionStatusPresentation(status: status, lastConfirmedAt: lastConfirmedAt, hasCache: hasCache)
    }

    var body: some View {
        let p = presentation
        Button {
            showingDetail = true
        } label: {
            VStack(spacing: 1) {
                HStack(spacing: 6) {
                    Image(systemName: p.iconName)
                        .font(.caption2)
                    Text(p.label)
                        .font(.caption2.weight(.semibold))
                        .tracking(0.3)
                }
                if let ts = p.timestampText {
                    Text(ts)
                        .font(.caption2)
                        .accessibilityIdentifier("connection-last-updated")
                }
            }
            .foregroundStyle(.white)
            .frame(maxWidth: .infinity)
            .padding(.vertical, 3)
            .background(p.barBackground)
        }
        .buttonStyle(.plain)
        .accessibilityElement(children: .combine)
        .accessibilityLabel(p.accessibilityLabel)
        .accessibilityHint("Shows details about the connection to your server.")
        .accessibilityIdentifier(p.isStale ? "connection-status-bar-stale" : "connection-status-bar")
        .alert("Connection", isPresented: $showingDetail) {
            Button("OK", role: .cancel) {}
        } message: {
            Text(p.detailMessage)
        }
    }
}
