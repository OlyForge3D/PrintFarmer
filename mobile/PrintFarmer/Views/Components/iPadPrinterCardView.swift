import SwiftUI

/// Rich printer card for iPad — modeled after the web CompactPrinterCard.
/// Horizontal layout with state-tinted header, temps with targets, filament info,
/// and inline bed-clear banner for PendingReady state.
struct iPadPrinterCardView: View {
    let printer: Printer
    var isPendingReady: Bool = false

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            // Color-tinted header
            headerSection

            // Body content
            VStack(alignment: .leading, spacing: 10) {
                // Job info + progress (above temps, matching web UI order)
                jobSection

                // Temperature row
                temperatureSection

                // Filament info row — always visible
                if let spool = printer.spoolInfo, spool.hasActiveSpool {
                    filamentRow(spool)
                } else {
                    Label("No spool loaded", systemImage: "cylinder")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }

                // Bed clear banner
                if isPendingReady {
                    bedClearBanner
                }
            }
            .padding(14)
        }
        .background(Color.pfCard, in: RoundedRectangle(cornerRadius: 12))
        .overlay(
            RoundedRectangle(cornerRadius: 12)
                .strokeBorder(statusAccentColor.opacity(0.4), lineWidth: 1.5)
        )
        .clipShape(RoundedRectangle(cornerRadius: 12))
    }

    // MARK: - Header

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

            // Status pill
            Text(statusLabel)
                .font(.caption.weight(.semibold))
                .padding(.horizontal, 8)
                .padding(.vertical, 3)
                .background(.black.opacity(0.3), in: Capsule())
                .foregroundStyle(.white)
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 10)
        .background(headerGradient)
    }

    // MARK: - Temperatures

    private var temperatureSection: some View {
        HStack(spacing: 16) {
            // Hotend — always visible with placeholder
            Label {
                temperatureText(current: printer.hotendTemp, target: printer.hotendTarget)
            } icon: {
                NozzleIcon()
                    .fill(hotendIconColor)
                    .frame(width: 16, height: 16)
            }
            .font(.subheadline)
            .frame(maxWidth: .infinity, alignment: .leading)

            // Bed — always visible with placeholder
            Label {
                temperatureText(current: printer.bedTemp, target: printer.bedTarget)
            } icon: {
                RadiatorIcon()
                    .fill(bedIconColor)
                    .frame(width: 16, height: 16)
            }
            .font(.subheadline)
            .frame(maxWidth: .infinity, alignment: .leading)
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

    // MARK: - Job Progress

    private var jobSection: some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(printer.fileName ?? printer.jobName ?? "---")
                .font(.caption)
                .foregroundStyle(.secondary)
                .lineLimit(1)

            HStack(spacing: 6) {
                PrintProgressBar(progress: printer.progress ?? 0, height: 8)

                Text((printer.progress ?? 0).percentFormatted)
                    .font(.caption.weight(.medium).monospacedDigit())
                    .foregroundStyle(.secondary)
            }
        }
    }

    // MARK: - Filament Info

    private func filamentRow(_ spool: PrinterSpoolInfo) -> some View {
        HStack(spacing: 8) {
            // Spool color circle
            if let hex = spool.colorHex {
                Circle()
                    .fill(Color(hex: hex))
                    .frame(width: 12, height: 12)
                    .overlay(Circle().strokeBorder(.primary.opacity(0.2), lineWidth: 0.5))
            }

            // Material name
            if let material = spool.material {
                Text(material)
                    .font(.caption.weight(.medium))
                    .foregroundStyle(.secondary)
            }

            // Filament name
            if let name = spool.filamentName {
                Text("·")
                    .foregroundStyle(.tertiary)
                Text(name)
                    .font(.caption)
                    .foregroundStyle(.tertiary)
                    .lineLimit(1)
            }

            Spacer()

            // Remaining weight
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
    }

    // MARK: - Bed Clear Banner

    private var bedClearBanner: some View {
        HStack(spacing: 8) {
            Image(systemName: "exclamationmark.triangle.fill")
                .foregroundStyle(Color.pfWarning)

            Text("Bed clear required — confirm to continue")
                .font(.caption.weight(.medium))

            Spacer()

            Image(systemName: "chevron.right")
                .font(.caption2)
                .foregroundStyle(.tertiary)
        }
        .padding(10)
        .background(Color.pfWarning.opacity(0.12), in: RoundedRectangle(cornerRadius: 8))
    }

    // MARK: - Computed Properties

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

    private var statusAccentColor: Color {
        if isPendingReady { return .pfWarning }
        if !printer.isOnline { return .pfTextTertiary }
        switch printer.state?.lowercased() {
        case "printing": return .pfSecondaryAccent
        case "paused": return .pfWarning
        case "error": return .pfError
        default: return .pfSuccess
        }
    }

    private var headerGradient: some ShapeStyle {
        LinearGradient(
            colors: [headerBaseColor, headerBaseColor.opacity(0.85)],
            startPoint: .leading,
            endPoint: .trailing
        )
    }

    private var headerBaseColor: Color {
        // Check pendingReady BEFORE isOnline — a printer awaiting bed clear is reachable
        if isPendingReady { return Color(hex: "#eab308") }
        if !printer.isOnline { return Color(hex: "#4b5563") }
        switch printer.state?.lowercased() {
        case "printing": return Color(hex: "#1d4ed8")
        case "paused": return Color(hex: "#b45309")
        case "error": return Color(hex: "#dc2626")
        default: return Color(hex: "#059669")
        }
    }
}
