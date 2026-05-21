import SwiftUI

/// Compact card for a printer in list/grid views.
struct PrinterCardView: View {
    let printer: Printer
    var isPendingReady: Bool = false

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            headerSection

            // Body — always show all elements for consistent card sizing
            VStack(alignment: .leading, spacing: 10) {
                // Job info + progress (above temps, matching web UI order)
                VStack(alignment: .leading, spacing: 4) {
                    Text(printer.fileName ?? printer.jobName ?? "---")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .lineLimit(1)

                    PrintProgressBar(progress: printer.progress ?? 0, height: 6)
                }

                // Temperature row — always visible with placeholders
                HStack(spacing: 16) {
                    Label {
                        temperatureText(current: printer.hotendTemp, target: printer.hotendTarget)
                    } icon: {
                        NozzleIcon()
                            .fill(hotendIconColor)
                            .frame(width: 14, height: 14)
                    }
                    .font(.caption)
                    .frame(maxWidth: .infinity, alignment: .leading)

                    Label {
                        temperatureText(current: printer.bedTemp, target: printer.bedTarget)
                    } icon: {
                        RadiatorIcon()
                            .fill(bedIconColor)
                            .frame(width: 14, height: 14)
                    }
                    .font(.caption)
                    .frame(maxWidth: .infinity, alignment: .leading)
                }

                // Filament row — always visible
                filamentSection
            }
            .padding(14)
        }
        .background(Color.pfCard, in: RoundedRectangle(cornerRadius: 12))
        .clipShape(RoundedRectangle(cornerRadius: 12))
        .overlay(
            RoundedRectangle(cornerRadius: 12)
                .strokeBorder(statusAccentColor.opacity(0.3), lineWidth: 1)
        )
    }

    private var headerSection: some View {
        let _ = print("DEBUG headerBaseColor: printer=\(printer.name), state='\(printer.state ?? "nil")', isOnline=\(printer.isOnline)")
        return HStack {
            VStack(alignment: .leading, spacing: 2) {
                Text(printer.name)
                    .font(.headline)
                    .foregroundStyle(.white)
                    .lineLimit(1)
                if let location = printer.location {
                    Label(location.name, systemImage: "building.2")
                        .font(.caption)
                        .foregroundStyle(.white.opacity(0.8))
                        .lineLimit(1)
                }
            }
            Spacer()
            if printer.obicoEnabled {
                Image(systemName: "shield.checkered")
                    .font(.caption)
                    .foregroundStyle(.white.opacity(0.85))
            }
            Text(statusLabel)
                .font(.caption2.weight(.semibold))
                .padding(.horizontal, 7)
                .padding(.vertical, 2)
                .background(.black.opacity(0.3), in: Capsule())
                .foregroundStyle(.white)
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 8)
        .background(headerGradient)
    }

    private var statusLabel: String {
        // Check pendingReady BEFORE isOnline — API may report isOnline=false for PendingReady printers
        if isPendingReady { return "Bed Clear" }
        guard printer.isOnline else { return "Offline" }
        guard let state = printer.state else { return "Idle" }
        switch state.lowercased() {
        case "printing": return "Printing"
        case "paused": return "Paused"
        case "error": return "Error"
        case "idle", "ready": return "Ready"
        default: return state.capitalized
        }
    }

    private var headerBaseColor: Color {
        // Check pendingReady BEFORE isOnline — a printer awaiting bed clear is reachable
        if isPendingReady { return Color(hex: "#eab308") }
        if !printer.isOnline { return Color(hex: "#4b5563") }
        switch printer.state?.lowercased() {
        case "printing": return Color(hex: "#059669")
        case "paused": return Color(hex: "#b45309")
        case "error": return Color(hex: "#dc2626")
        default: return Color(hex: "#1d4ed8")
        }
    }

    private var headerGradient: some ShapeStyle {
        LinearGradient(
            colors: [headerBaseColor, headerBaseColor.opacity(0.85)],
            startPoint: .leading,
            endPoint: .trailing
        )
    }

    private var statusAccentColor: Color {
        if isPendingReady { return .pfWarning }
        if !printer.isOnline { return .pfTextTertiary }
        switch printer.state?.lowercased() {
        case "printing": return .pfSuccess
        case "paused": return .pfWarning
        case "error": return .pfError
        default: return .pfSecondaryAccent
        }
    }

    private func temperatureText(current: Double?, target: Double?) -> some View {
        HStack(spacing: 2) {
            Text(current.map { String(format: "%.0f°C", $0) } ?? "---°C")
                .monospacedDigit()
            if let target, target > 0 {
                Text("→")
                    .foregroundStyle(.tertiary)
                Text(String(format: "%.0f°C", target))
                    .monospacedDigit()
                    .foregroundStyle(.secondary)
            }
        }
    }

    private var hotendIconColor: Color {
        if let target = printer.hotendTarget, target > 0 {
            return .red
        } else {
            return .red.opacity(0.35)
        }
    }

    private var bedIconColor: Color {
        if let target = printer.bedTarget, target > 0 {
            return .blue
        } else {
            return .blue.opacity(0.35)
        }
    }

    // MARK: - Filament Info

    @ViewBuilder
    private var filamentSection: some View {
        if let spool = printer.spoolInfo, spool.hasActiveSpool {
            HStack(spacing: 6) {
                if let hex = spool.colorHex {
                    Circle()
                        .fill(Color(hex: hex))
                        .frame(width: 10, height: 10)
                        .overlay(Circle().strokeBorder(.primary.opacity(0.2), lineWidth: 0.5))
                }

                if let material = spool.material {
                    Text(material)
                        .font(.caption.weight(.medium))
                        .foregroundStyle(.secondary)
                }

                if let name = spool.filamentName {
                    Text("·")
                        .foregroundStyle(.tertiary)
                    Text(name)
                        .font(.caption)
                        .foregroundStyle(.tertiary)
                        .lineLimit(1)
                }

                Spacer()

                if let weight = spool.remainingWeightG {
                    Label {
                        Text(String(format: "%.0fg", weight))
                            .font(.caption.monospacedDigit())
                    } icon: {
                        Image(systemName: "scalemass")
                            .font(.caption2)
                    }
                    .foregroundStyle(.secondary)
                }
            }
        } else {
            Label("No spool loaded", systemImage: "cylinder")
                .font(.caption)
                .foregroundStyle(.secondary)
        }
    }
}
