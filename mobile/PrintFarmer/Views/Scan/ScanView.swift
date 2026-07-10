import SwiftUI

/// Scan tab entry — the operator's scan hub.
///
/// F1 (#706) introduces this destination as a hub that surfaces the
/// existing scan-driven flows (barcode/QR spool intake). NFC printer
/// tags are handled implicitly by iOS via the app's `printfarmer://`
/// deep-link scheme (see `DeepLinkHandler`), so no manual entry is
/// required here. F9 will replace this placeholder with a dedicated
/// scan-first inventory experience.
struct ScanView: View {
    @Environment(AppRouter.self) private var router
    @State private var presentedFlow: ScanFlow?

    var body: some View {
        @Bindable var router = router

        NavigationStack(path: $router.scanPath) {
            List {
                Section {
                    scanCard(
                        icon: "barcode.viewfinder",
                        title: "Spool Barcode / QR",
                        description: "Scan filament barcodes and QR codes to import spools into inventory.",
                        flow: .spoolBarcode
                    )
                    .accessibilityIdentifier("scan.spool.barcode")
                } header: {
                    Text("Filament Intake")
                }

                Section {
                    Label(
                        "Wave your device near a tagged printer to open its detail view.",
                        systemImage: "wave.3.right"
                    )
                    .font(.footnote)
                    .foregroundStyle(.secondary)
                    .accessibilityIdentifier("scan.nfc.hint")
                } header: {
                    Text("NFC Printer Tags")
                } footer: {
                    Text("iOS handles NFC printer tags automatically. Tag reading is available system-wide.")
                }
            }
            .navigationTitle("Scan")
            .sheet(item: $presentedFlow) { flow in
                switch flow {
                case .spoolBarcode:
                    BarcodeIntakeView()
                }
            }
        }
    }

    @ViewBuilder
    private func scanCard(icon: String, title: String, description: String, flow: ScanFlow) -> some View {
        Button {
            presentedFlow = flow
        } label: {
            HStack(alignment: .top, spacing: 16) {
                Image(systemName: icon)
                    .font(.title2)
                    .foregroundStyle(Color.pfAccent)
                    .frame(width: 44, height: 44)
                    .background(Color.pfAccent.opacity(0.12), in: RoundedRectangle(cornerRadius: 10))

                VStack(alignment: .leading, spacing: 4) {
                    Text(title)
                        .font(.subheadline.weight(.semibold))
                        .foregroundStyle(Color.pfTextPrimary)

                    Text(description)
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .multilineTextAlignment(.leading)
                }

                Spacer(minLength: 8)

                Image(systemName: "chevron.right")
                    .font(.caption)
                    .foregroundStyle(.tertiary)
            }
            .padding(.vertical, 4)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
    }
}

private enum ScanFlow: String, Identifiable {
    case spoolBarcode

    var id: String { rawValue }
}

