import SwiftUI

/// Advanced printer controls surface.
///
/// F1 (#706) moves jog/preheat/z-offset/home/disable-motor controls off
/// the printer detail scroll and gates them behind this dedicated
/// "Advanced" destination. This ensures cockpit controls are never
/// visible in list, card, or attention contexts and require an explicit
/// tap-through from Printer Detail.
struct AdvancedPrinterControlsView: View {
    @Environment(ServiceContainer.self) private var services
    let printer: Printer

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 20) {
                header

                PrinterControlsSection(
                    printer: printer,
                    printerService: services.printerService
                )
            }
            .padding()
        }
        .navigationTitle("Advanced")
        #if os(iOS)
        .navigationBarTitleDisplayMode(.inline)
        #endif
    }

    private var header: some View {
        VStack(alignment: .leading, spacing: 8) {
            Label("Advanced Controls", systemImage: "slider.horizontal.3")
                .font(.title3.weight(.semibold))
                .foregroundStyle(Color.pfTextPrimary)

            Text("Jog, preheat, and homing are for setup and maintenance. Controls are disabled while a print is active.")
                .font(.footnote)
                .foregroundStyle(.secondary)
        }
        .accessibilityElement(children: .combine)
    }
}
