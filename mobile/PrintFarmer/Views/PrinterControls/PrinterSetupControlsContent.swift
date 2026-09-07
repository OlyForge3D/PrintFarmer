import SwiftUI

/// Presentation-only setup controls. The host owns the model, capability loading,
/// access gating and live-update forwarding even while this content is offscreen.
/// Use `PrinterControlsSection(printer:printerService:)` for standalone ownership.
struct PrinterSetupControlsContent: View {
    let printer: Printer
    @ObservedObject var viewModel: PrinterControlsViewModel
    @Environment(\.horizontalSizeClass) private var horizontalSizeClass
    @Environment(\.dynamicTypeSize) private var dynamicTypeSize

    private var isPrintingOrPaused: Bool {
        switch printer.state?.lowercased() {
        case "printing", "paused": return true
        default: return false
        }
    }

    var body: some View {
        if !PrinterControlsSection.isHidden(for: printer) {
            content
        }
    }

    private var content: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Controls")
                .font(.title3.weight(.semibold))
                .foregroundStyle(Color.pfTextPrimary)
                .accessibilityAddTraits(.isHeader)

            VStack(alignment: .leading, spacing: 0) {
                if isPrintingOrPaused {
                    lockoutBanner
                        .padding(.bottom, 12)
                }

                if horizontalSizeClass == .regular && !dynamicTypeSize.isAccessibilitySize {
                    HStack(alignment: .top, spacing: 16) {
                        PreheatSubgroup(viewModel: viewModel)
                            .frame(maxWidth: .infinity, alignment: .leading)
                        HomeSubgroup(viewModel: viewModel)
                            .frame(maxWidth: .infinity, alignment: .leading)
                    }
                    Divider()
                        .background(Color.pfBorder)
                        .padding(.vertical, 8)
                    JogSubgroup(viewModel: viewModel)
                } else {
                    PreheatSubgroup(viewModel: viewModel)
                    Divider()
                        .background(Color.pfBorder)
                        .padding(.vertical, 8)
                    HomeSubgroup(viewModel: viewModel)
                    Divider()
                        .background(Color.pfBorder)
                        .padding(.vertical, 8)
                    JogSubgroup(viewModel: viewModel)
                }

                if let error = viewModel.lastError {
                    errorBanner(error)
                        .padding(.top, 12)
                }
            }
            .padding()
            .background(Color.pfCard, in: RoundedRectangle(cornerRadius: 12))
            .overlay(
                RoundedRectangle(cornerRadius: 12)
                    .strokeBorder(Color.pfBorder, lineWidth: 1)
            )
        }
    }

    private var lockoutBanner: some View {
        HStack(spacing: 8) {
            Image(systemName: "lock.fill")
                .foregroundStyle(Color.pfWarning)
            Text("Controls are disabled while a print is active.")
                .font(.footnote)
                .foregroundStyle(Color.pfTextPrimary)
        }
        .padding(10)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(Color.pfWarning.opacity(0.12), in: RoundedRectangle(cornerRadius: 8))
        .accessibilityElement(children: .combine)
        .accessibilityLabel(
            String(localized: "Controls disabled, print is active.",
                   comment: "VoiceOver: lockout banner during print")
        )
    }

    private func errorBanner(_ error: ControlsError) -> some View {
        HStack(alignment: .top, spacing: 8) {
            Image(systemName: "exclamationmark.triangle.fill")
                .foregroundStyle(Color.pfError)
            VStack(alignment: .leading, spacing: 4) {
                Text(error.message)
                    .font(.footnote)
                    .foregroundStyle(Color.pfTextPrimary)
            }
            Spacer()
            Button {
                viewModel.dismissError()
            } label: {
                Text("Dismiss")
                    .font(.footnote)
                    .frame(minWidth: 44, minHeight: 44)
                    .contentShape(Rectangle())
            }
            .buttonStyle(.borderless)
        }
        .padding(10)
        .background(Color.pfError.opacity(0.12), in: RoundedRectangle(cornerRadius: 8))
        .accessibilityElement(children: .contain)
    }
}
