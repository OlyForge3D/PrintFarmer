import SwiftUI

/// Colored badge showing printer or job state.
struct StatusBadge: View {
    let text: String
    let color: Color

    var body: some View {
        Text(text)
            .font(.caption2.weight(.semibold))
            .textCase(.uppercase)
            .padding(.horizontal, 8)
            .padding(.vertical, 3)
            .background(color.opacity(0.15), in: Capsule())
            .foregroundStyle(color)
            .accessibilityLabel("\(text) status")
    }
}

// MARK: - Printer State Helpers

extension StatusBadge {
    /// Creates a badge from a printer's live state string.
    init(printerState: String?, isOnline: Bool) {
        if !isOnline {
            self.init(text: "Offline", color: .pfTextTertiary)
            return
        }
        guard let state = printerState?.lowercased() else {
            self.init(text: "Unknown", color: .pfTextTertiary)
            return
        }
        switch state {
        case "printing":
            self.init(text: "Printing", color: .pfSuccess)
        case "paused":
            self.init(text: "Paused", color: .pfWarning)
        case "ready", "idle", "operational":
            self.init(text: "Idle", color: .pfSecondaryAccent)
        case "error":
            self.init(text: "Error", color: .pfError)
        case "maintenance":
            self.init(text: "Maintenance", color: .pfMaintenance)
        default:
            self.init(text: state.capitalized, color: .pfTextSecondary)
        }
    }

    /// Creates a badge from a job status enum.
    init(jobStatus: PrintJobStatus?) {
        guard let jobStatus else {
            self.init(text: "Unknown", color: .pfTextTertiary)
            return
        }
        switch jobStatus {
        case .queued:
            self.init(text: "Queued", color: .pfTextSecondary)
        case .assigned:
            self.init(text: "Assigned", color: .pfAssigned)
        case .starting:
            self.init(text: "Starting", color: .pfSecondaryAccent)
        case .printing:
            self.init(text: "Printing", color: .pfSecondaryAccent)
        case .paused:
            self.init(text: "Paused", color: .pfWarning)
        case .completed:
            self.init(text: "Completed", color: .pfSuccess)
        case .failed:
            self.init(text: "Failed", color: .pfError)
        case .cancelled:
            self.init(text: "Cancelled", color: .pfTextTertiary)
        }
    }
}
