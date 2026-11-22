import SwiftUI

struct NFCWriteView: View {
    @Environment(\.dismiss) private var dismiss
    @AppStorage("nfcTagFormat") private var nfcTagFormat: NFCTagFormat = .openPrintTag

    let spool: SpoolmanSpool
    let filament: SpoolmanFilament?
    let onWrite: () async -> Bool

    @State private var writeState: WriteState = .ready
    @State private var writeTask: Task<Void, Never>?

    private enum WriteState {
        case ready
        case writing
        case success
        case error(String)
    }

    var body: some View {
        NavigationStack {
            ScrollView {
                VStack(spacing: 16) {
                    spoolSummary

                    Divider()

                    statusArea

                    if case .ready = writeState {
                        fieldPreview
                    }
                }
                .padding()
            }
            .accessibilityIdentifier("nfcWrite.preview")
            .safeAreaInset(edge: .bottom) {
                VStack(spacing: 8) {
                    actionButtons
                    Button {
                        dismiss()
                    } label: {
                        Text("Cancel")
                            .fullWidthActionButton()
                            .foregroundStyle(Color.pfTextPrimary)
                            .background(Color.pfBackgroundTertiary, in: RoundedRectangle(cornerRadius: 10))
                    }
                    .buttonStyle(.plain)
                }
                .padding()
                .background(.bar)
            }
            .navigationTitle("Write NFC Tag")
            #if os(iOS)
            .navigationBarTitleDisplayMode(.inline)
            #endif
            .onDisappear { writeTask?.cancel() }
        }
    }

    // MARK: - Spool Summary

    private var spoolSummary: some View {
        HStack(spacing: 14) {
            Circle()
                .fill(Color(hex: spool.colorHex ?? "#808080"))
                .frame(width: 40, height: 40)
                .overlay(
                    Circle()
                        .strokeBorder(Color.pfBorder, lineWidth: 1)
                )

            VStack(alignment: .leading, spacing: 4) {
                Text(spool.filamentName ?? spool.name)
                    .font(.headline)
                    .foregroundStyle(Color.pfTextPrimary)

                HStack(spacing: 6) {
                    Text(spool.material)
                        .font(.caption.weight(.medium))
                        .padding(.horizontal, 6)
                        .padding(.vertical, 2)
                        .background(Color.pfBackgroundTertiary, in: Capsule())

                    if let vendor = spool.vendor {
                        Text(vendor)
                            .font(.caption)
                            .foregroundStyle(Color.pfTextSecondary)
                    }
                }

                if let remaining = spool.remainingWeightG {
                    Text("\(Int(remaining))g remaining")
                        .font(.caption)
                        .foregroundStyle(Color.pfTextSecondary)
                }
            }

            Spacer()
        }
    }

    // MARK: - Field Preview

    private var fieldPreview: some View {
        VStack(alignment: .leading, spacing: 6) {
            if nfcTagFormat == .openSpool {
                fieldRow("material", spool.material)
                fieldRow("color_hex", spool.colorHex)
                fieldRow("brand", spool.vendor)
                fieldRow("weight_g", spool.initialWeightG.map { "\(Int($0))" })
                fieldRow("spoolman_id", "\(spool.id)")
            } else if nfcTagFormat == .openPrintTag {
                fieldRow("filament_type", spool.material)
                fieldRow("color", spool.colorHex)
                fieldRow("manufacturer", spool.vendor)
                fieldRow("net_weight", spool.initialWeightG.map { "\(Int($0))" })
                fieldRow("filament_diameter", filament?.diameter.map { "\($0)" })
                fieldRow("print_temp", filament?.settingsExtruderTemp.map { "\($0)" })
                fieldRow("bed_temp", filament?.settingsBedTemp.map { "\($0)" })
                fieldRow("spool_id", "\(spool.id)")
            } else {
                fieldRow("Material", spool.material)
                fieldRow("Manufacturer", spool.vendor)
                fieldRow("Color", spool.name)
                fieldRow("Color RGBA", spool.colorHex)
                fieldRow("Weight", spool.initialWeightG.map { "\(Int($0))g" })
                fieldRow("Diameter", "1.75mm")
                fieldRow("Lot Number", spool.lotNumber)
                if spool.remainingWeightG != nil || spool.spoolWeightG != nil {
                    fieldRow("Spool Weight", spool.spoolWeightG.map { "\(Int($0))g" })
                    fieldRow("Remaining", spool.remainingWeightG.map { "\(Int($0))g" })
                }
            }
        }
        .padding(12)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(Color.pfBackgroundSecondary, in: RoundedRectangle(cornerRadius: 10))
    }

    private func fieldRow(_ label: String, _ value: String?) -> some View {
        HStack(alignment: .firstTextBaseline, spacing: 12) {
            Text(label)
                .font(.caption)
                .foregroundStyle(Color.pfTextSecondary)
                .fixedSize(horizontal: false, vertical: true)
            Text(value ?? "—")
                .font(.caption.monospaced())
                .foregroundStyle(Color.pfTextPrimary)
                .fixedSize(horizontal: false, vertical: true)
            Spacer(minLength: 0)
        }
    }

    // MARK: - Status Area

    @ViewBuilder
    private var statusArea: some View {
        switch writeState {
        case .ready:
            VStack(spacing: 12) {
                Image(systemName: "wave.3.right")
                    .font(.system(size: 48))
                    .foregroundStyle(Color.pfAccent)
                    .accessibilityHidden(true)

                Text("Format: \(nfcTagFormat.rawValue)")
                    .font(.caption.weight(.medium))
                    .padding(.horizontal, 10)
                    .padding(.vertical, 4)
                    .background(Color.pfBackgroundTertiary, in: Capsule())
            }
            .frame(maxWidth: .infinity)
            .padding(.vertical, 8)

        case .writing:
            VStack(spacing: 16) {
                ProgressView()
                    .scaleEffect(1.5)

                Text("Hold iPhone near blank NFC tag…")
                    .font(.subheadline.weight(.medium))
                    .foregroundStyle(Color.pfTextPrimary)
                    .multilineTextAlignment(.center)
            }
            .frame(maxWidth: .infinity)
            .padding(.vertical, 24)

        case .success:
            VStack(spacing: 12) {
                Image(systemName: "checkmark.circle.fill")
                    .font(.system(size: 48))
                    .foregroundStyle(Color.pfSuccess)
                    .symbolEffect(.bounce, value: true)
                    .accessibilityHidden(true)

                Text("NFC tag written successfully!")
                    .font(.subheadline.weight(.medium))
                    .foregroundStyle(Color.pfTextPrimary)
            }
            .frame(maxWidth: .infinity)
            .padding(.vertical, 24)

        case .error(let message):
            VStack(spacing: 12) {
                Image(systemName: "exclamationmark.triangle.fill")
                    .font(.system(size: 48))
                    .foregroundStyle(Color.pfError)
                    .accessibilityHidden(true)

                Text(message)
                    .font(.subheadline)
                    .foregroundStyle(Color.pfTextSecondary)
                    .multilineTextAlignment(.center)
            }
            .frame(maxWidth: .infinity)
            .padding(.vertical, 24)
        }
    }

    // MARK: - Action Buttons

    @ViewBuilder
    private var actionButtons: some View {
        switch writeState {
        case .ready:
            Button {
                writeTask = Task { await performWrite() }
            } label: {
                Label("Write Tag", systemImage: "wave.3.right")
                    .fullWidthActionButton(prominence: .prominent)
            }
            .buttonStyle(.borderedProminent)
            .tint(Color.pfButtonPrimary)

        case .writing:
            EmptyView()

        case .success:
            Button("Done") { dismiss() }
                .buttonStyle(.borderedProminent)
                .tint(Color.pfButtonPrimary)
                .fullWidthActionButton()

        case .error:
            Button {
                writeTask = Task { await performWrite() }
            } label: {
                Label("Retry", systemImage: "arrow.clockwise")
                    .fullWidthActionButton(prominence: .prominent)
            }
            .buttonStyle(.borderedProminent)
            .tint(Color.pfButtonPrimary)
        }
    }

    private func performWrite() async {
        writeState = .writing
        let success = await onWrite()
        if success {
            writeState = .success
        } else {
            writeState = .error("Failed to write NFC tag. Make sure the tag is blank and close enough.")
        }
    }
}
