import SwiftUI

/// Presentation-only setup controls. The host owns the model, capability loading,
/// access gating and live-update forwarding even while this content is offscreen.
/// Use `PrinterControlsSection(printer:printerService:)` for standalone ownership.
struct PrinterSetupControlsContent: View {
    let printer: Printer
    @ObservedObject var viewModel: PrinterControlsViewModel
    var usesColumns: Bool? = nil
    @Environment(\.horizontalSizeClass) private var horizontalSizeClass
    @Environment(\.dynamicTypeSize) private var dynamicTypeSize

    private var isPrintingOrPaused: Bool {
        switch printer.state?.lowercased() {
        case "printing", "paused", "starting": return true
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
                if let error = viewModel.capabilityLoadError {
                    VStack(alignment: .leading, spacing: 8) {
                        Text("Control capabilities unavailable")
                            .font(.headline)
                        Text(error)
                            .font(.footnote)
                        ControlActionButton(title: "Retry capability check") {
                            Task { await viewModel.loadCapabilities() }
                        }
                        .disabled(viewModel.isLoadingCapabilities)
                    }
                    .foregroundStyle(Color.pfTextPrimary)
                    .padding(.bottom, 12)
                }

                if isPrintingOrPaused {
                    lockoutBanner
                        .padding(.bottom, 12)
                }

                let columns = (usesColumns ?? (horizontalSizeClass == .regular))
                    && !dynamicTypeSize.isAccessibilitySize
                let layout = columns
                    ? AnyLayout(HStackLayout(alignment: .top, spacing: 16))
                    : AnyLayout(VStackLayout(alignment: .leading, spacing: 16))
                // Keep Jog's axis/distance state when width or text size reflows.
                layout {
                    VStack(alignment: .leading, spacing: 16) {
                        PreheatSubgroup(viewModel: viewModel)
                        PreheatSubgroup.IndividualHeaterControls(viewModel: viewModel)
                    }
                    .frame(maxWidth: .infinity, alignment: .leading)
                    VStack(alignment: .leading, spacing: 12) {
                        HomeSubgroup(viewModel: viewModel)
                        Divider()
                            .background(Color.pfBorder)
                        JogSubgroup(viewModel: viewModel)
                        JogSubgroup.AbsolutePositionControls(viewModel: viewModel)
                        HomeSubgroup.MotorReleaseControls(viewModel: viewModel)
                    }
                    .frame(maxWidth: .infinity, alignment: .leading)
                }

                if let notice = viewModel.commandNotice {
                    Text(notice)
                        .font(.footnote)
                        .foregroundStyle(Color.pfTextSecondary)
                        .padding(.top, 12)
                }
                if viewModel.isExecuting {
                    ControlActionButton(
                        title: "Stop waiting for command", identifier: "printer.controls.stop-waiting",
                        hint: "Does not stop the printer. Physical execution may continue."
                    ) { viewModel.cancelPendingCommand() }
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
