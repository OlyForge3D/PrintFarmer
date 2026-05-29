import SwiftUI

/// Composite Printer Controls section. Hosts the three subgroups (Preheat,
/// Home, Jog), owns the `PrinterControlsViewModel`, and applies the section-
/// level visibility rules from issue #287:
///
/// * Hidden when `printer.isOnline == false`.
/// * Hidden when `printer.state` is `printing` / `paused` / `starting`.
/// * Phone: vertical stack (Preheat → Home → Jog).
/// * iPad (regular width): Preheat full-width, Home + Jog side-by-side.
///
/// The section forwards parent-driven printer updates (which the parent's
/// `PrinterDetailViewModel` already populates from the `printerupdated`
/// SignalR event) into `viewModel.handlePrinterUpdate(_:)` so pending command
/// state clears once the backend confirms the effect.
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

    static func isHidden(for printer: Printer) -> Bool {
        if !printer.isOnline { return true }
        switch printer.state?.lowercased() {
        case "printing", "paused", "starting": return true
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
                .font(.headline)
                .accessibilityAddTraits(.isHeader)

            VStack(alignment: .leading, spacing: 16) {
                if horizontalSizeClass == .regular {
                    PreheatSubgroup(viewModel: viewModel)
                    HStack(alignment: .top, spacing: 16) {
                        HomeSubgroup(viewModel: viewModel)
                            .frame(maxWidth: .infinity, alignment: .leading)
                        JogSubgroup(viewModel: viewModel)
                            .frame(maxWidth: .infinity, alignment: .leading)
                    }
                } else {
                    PreheatSubgroup(viewModel: viewModel)
                    HomeSubgroup(viewModel: viewModel)
                    JogSubgroup(viewModel: viewModel)
                }

                if let error = viewModel.lastError {
                    errorBanner(error)
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
        .background(Color.pfError.opacity(0.1), in: RoundedRectangle(cornerRadius: 8))
        .accessibilityElement(children: .combine)
    }
}
