import SwiftUI

/// Composite Printer Controls section. Hosts the three subgroups (Preheat,
/// Home, Jog), owns the `PrinterControlsViewModel`, and applies the section-
/// level visibility rules from the v1 design spec (printer-controls-v1.md):
///
/// * Hidden entirely when `printer.isOnline == false`.
/// * **Visible** when `printing` / `paused` — a lockout banner is shown
///   and all subgroup buttons are disabled (section stays on-screen so
///   the user can see it is there but locked, per spec §2.2 and §2.4).
/// * Phone: vertical stack (Preheat → Divider → Home → Divider → Jog).
/// * iPad (regular width): Preheat + Home side-by-side (top), Jog full-width (bottom).
///
/// The section forwards parent-driven printer updates into
/// `viewModel.handlePrinterUpdate(_:)` so pending command state clears once
/// the backend confirms the effect via `printerupdated` SignalR.
struct PrinterControlsSection: View {

    let printer: Printer
    @StateObject private var viewModel: PrinterControlsViewModel
    @Environment(\.horizontalSizeClass) private var horizontalSizeClass

    init(printer: Printer, printerService: any PrinterServiceProtocol) {
        self.printer = printer
        _viewModel = StateObject(
            wrappedValue: PrinterControlsViewModel(printerService: printerService, printer: printer)
        )
    }

    /// Hides the entire section only when the printer is offline. During a
    /// print or pause the section remains visible with disabled controls and
    /// a lockout banner (spec §2.2, §2.4).
    static func isHidden(for printer: Printer) -> Bool {
        !printer.isOnline
    }

    private var isPrintingOrPaused: Bool {
        switch printer.state?.lowercased() {
        case "printing", "paused": return true
        default: return false
        }
    }

    var body: some View {
        if Self.isHidden(for: printer) {
            EmptyView()
        } else {
            content
                .onChange(of: printer.isOnline) { _, _ in
                    viewModel.handlePrinterUpdate(printer)
                }
                .onChange(of: printer.state) { _, _ in
                    viewModel.handlePrinterUpdate(printer)
                }
                .task { await viewModel.loadCapabilities() }
        }
    }

    @ViewBuilder
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

                if horizontalSizeClass == .regular {
                    // iPad: Preheat + Home side-by-side (top), Jog full-width (bottom).
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
                    // Phone: vertical stack with dividers between subgroups.
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

    // MARK: - Banners

    @ViewBuilder
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

    @ViewBuilder
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
            Button("Dismiss") { viewModel.dismissError() }
                .font(.footnote)
                .buttonStyle(.borderless)
        }
        .padding(10)
        .background(Color.pfError.opacity(0.12), in: RoundedRectangle(cornerRadius: 8))
        .accessibilityElement(children: .combine)
    }
}
