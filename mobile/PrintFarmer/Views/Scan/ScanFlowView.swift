import SwiftUI

/// Inventory-owned scanner surface after scanning stops being a top-level tab.
///
/// The underlying dispatcher and scanner service are unchanged, so camera
/// permission is still requested only after the operator taps the primary Scan
/// control. Printer and spool deep links dismiss this sheet and continue in
/// their owning tab; bin, part, barcode-intake, and unrecognized results remain
/// in-place sheets.
struct ScanFlowView: View {
    @Environment(AppRouter.self) private var router
    @Environment(ServiceContainer.self) private var services
    @Environment(\.dismiss) private var dismiss
    @State private var viewModel = ScanViewModel()

    private var partsInventoryEnabled: Bool {
        services.capabilitiesService.resolved.printedPartsInventoryEnabled
    }

    private var scanDescription: String {
        partsInventoryEnabled
            ? "Scan a printer, bin, part, or spool code. PrintFarmer figures out what it is."
            : "Scan a printer or spool code. PrintFarmer figures out what it is."
    }

    private var scanHint: String {
        partsInventoryEnabled
            ? "Scans a printer, bin, part, or spool code and opens the matching screen."
            : "Scans a printer or spool code and opens the matching screen."
    }

    var body: some View {
        NavigationStack {
            List {
                scanSection
                nfcHintSection
                if !viewModel.recentScans.isEmpty {
                    recentScansSection
                }
            }
            .navigationTitle("Scan")
            #if os(iOS)
            .navigationBarTitleDisplayMode(.inline)
            #endif
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Done") { dismiss() }
                }
            }
            .overlay {
                if viewModel.isScanning {
                    ProgressView("Scanning…")
                        .padding()
                        .background(.ultraThinMaterial, in: RoundedRectangle(cornerRadius: 12))
                }
            }
            .alert("Scan Error", isPresented: .constant(viewModel.errorMessage != nil)) {
                Button("OK") { viewModel.clearError() }
            } message: {
                if let error = viewModel.errorMessage {
                    Text(error)
                }
            }
            .sheet(item: $viewModel.pendingOutcome) { outcome in
                switch outcome {
                case .bin(let bin):
                    if partsInventoryEnabled {
                        BinScanResultView(bin: bin)
                    }
                case .part(let part):
                    if partsInventoryEnabled {
                        PartScanResultView(part: part)
                    }
                case .unknownCode(let code):
                    UnrecognizedScanView(
                        code: code,
                        printedPartsInventoryEnabled: partsInventoryEnabled
                    )
                }
            }
            .sheet(isPresented: Binding(
                get: { viewModel.pendingSpoolBarcode != nil },
                set: { if !$0 { viewModel.pendingSpoolBarcode = nil } }
            )) {
                if let barcode = viewModel.pendingSpoolBarcode {
                    BarcodeIntakeView(initialBarcode: barcode)
                }
            }
            .onChange(of: viewModel.pendingDeepLinkDestination) { _, destination in
                guard let destination else { return }
                router.navigate(
                    to: destination,
                    capabilities: services.capabilitiesService.resolved
                )
                viewModel.pendingDeepLinkDestination = nil
                dismiss()
            }
            .task(id: services.activeServerGeneration) {
                let serverGeneration = services.activeServerGeneration
                viewModel.isViewActive = true
                viewModel.configure(
                    scanner: services.barcodeScannerService,
                    partsInventoryService: services.partsInventoryService,
                    barcodeIntakeService: services.barcodeIntakeService,
                    spoolService: services.spoolService,
                    printedPartsInventoryEnabled: partsInventoryEnabled
                )
                await services.capabilitiesService.refresh()
                guard !Task.isCancelled,
                      serverGeneration == services.activeServerGeneration else {
                    return
                }
                viewModel.setPrintedPartsInventoryEnabled(partsInventoryEnabled)
            }
            .onChange(of: partsInventoryEnabled) { _, isEnabled in
                viewModel.setPrintedPartsInventoryEnabled(isEnabled)
            }
            .onDisappear { viewModel.isViewActive = false }
        }
    }

    private var scanSection: some View {
        Section {
            Button {
                viewModel.scan()
            } label: {
                VStack(spacing: 12) {
                    Image(systemName: "barcode.viewfinder")
                        .font(.system(size: 44))
                        .foregroundStyle(Color.pfAccent)
                        .accessibilityHidden(true)

                    Text("Scan")
                        .font(.headline)
                        .foregroundStyle(Color.pfTextPrimary)

                    Text(scanDescription)
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .multilineTextAlignment(.center)
                }
                .frame(maxWidth: .infinity)
                .padding(.vertical, 20)
                .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .disabled(viewModel.isScanning || !viewModel.isScannerAvailable)
            .accessibilityIdentifier("scan.primary")
            .accessibilityLabel("Scan")
            .accessibilityHint(scanHint)
            .accessibilityAddTraits(.isButton)
        }
    }

    private var nfcHintSection: some View {
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
            Text(
                partsInventoryEnabled
                    ? "iOS handles NFC printer tags automatically. Bins and parts use QR/barcode only."
                    : "iOS handles NFC printer tags automatically."
            )
        }
    }

    private var recentScansSection: some View {
        Section {
            ForEach(viewModel.recentScans) { scan in
                HStack(spacing: 12) {
                    Image(systemName: scan.icon)
                        .foregroundStyle(Color.pfAccent)
                        .frame(width: 24)
                        .accessibilityHidden(true)

                    VStack(alignment: .leading, spacing: 2) {
                        Text(scan.title)
                            .font(.subheadline)
                            .foregroundStyle(Color.pfTextPrimary)
                        Text(scan.subtitle)
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    }

                    Spacer()

                    Text(scan.scannedAt.formatted(date: .omitted, time: .shortened))
                        .font(.caption2)
                        .foregroundStyle(.tertiary)
                }
                .accessibilityElement(children: .combine)
            }
        } header: {
            Text("Recent Scans")
        } footer: {
            Text("Recent scans are kept for this session only.")
        }
    }
}

private struct UnrecognizedScanView: View {
    let code: String
    let printedPartsInventoryEnabled: Bool

    @Environment(\.dismiss) private var dismiss

    private var description: String {
        printedPartsInventoryEnabled
            ? "\"\(code)\" didn't match a printer, bin, part, or known spool barcode."
            : "\"\(code)\" didn't match a printer or known spool barcode."
    }

    var body: some View {
        NavigationStack {
            ContentUnavailableView {
                Label("Unrecognized Code", systemImage: "questionmark.circle")
            } description: {
                Text(description)
            }
            .navigationTitle("Unrecognized")
            #if os(iOS)
            .navigationBarTitleDisplayMode(.inline)
            #endif
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Done") { dismiss() }
                }
            }
        }
    }
}
