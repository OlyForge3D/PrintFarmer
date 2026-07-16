import SwiftUI

/// Scan tab entry — the operator's scan station (#714, F9).
///
/// A single generic camera scan type-dispatches to whichever physical
/// entity was scanned: a `printfarmer://printer/...` QR opens the printer
/// detail, a bin barcode opens `BinScanResultView`, a printed-part SKU
/// barcode opens `PartScanResultView`, and anything else falls back to the
/// existing spool-barcode intake flow. NFC printer tags continue to be
/// handled implicitly by iOS via the app's deep-link scheme — no manual
/// entry is required for those, and no new NFC formats were introduced
/// for bins/parts (QR/barcode only, matching the backend's barcode-based
/// bin/SKU identity).
struct ScanView: View {
    @Environment(AppRouter.self) private var router
    @Environment(ServiceContainer.self) private var services
    @State private var viewModel = ScanViewModel()
    @State private var showBarcodeIntake = false
    @State private var partsInventoryEnabled = true

    var body: some View {
        @Bindable var router = router

        NavigationStack(path: $router.scanPath) {
            List {
                scanSection
                quickActionsSection
                nfcHintSection
                if !viewModel.recentScans.isEmpty {
                    recentScansSection
                }
            }
            .navigationTitle("Scan")
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
            .sheet(isPresented: $showBarcodeIntake) {
                BarcodeIntakeView()
            }
            .sheet(item: $viewModel.pendingOutcome) { outcome in
                switch outcome {
                case .bin(let bin):
                    BinScanResultView(bin: bin)
                case .part(let part):
                    PartScanResultView(part: part)
                case .unknownCode(let code):
                    UnrecognizedScanView(code: code)
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
            .onChange(of: viewModel.pendingPrinterDestination) { _, destination in
                guard let destination else { return }
                router.navigate(to: destination)
                viewModel.pendingPrinterDestination = nil
            }
            .task {
                viewModel.configure(
                    scanner: services.barcodeScannerService,
                    partsInventoryService: services.partsInventoryService,
                    barcodeIntakeService: services.barcodeIntakeService
                )
                await services.capabilitiesService.refresh()
                partsInventoryEnabled = services.capabilitiesService.resolved.printedPartsInventoryEnabled
            }
            .onAppear { viewModel.isViewActive = true }
            .onDisappear { viewModel.isViewActive = false }
        }
    }

    // MARK: - Scan

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

                    Text("Scan a printer, bin, part, or spool code. PrintFarmer figures out what it is.")
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
            .accessibilityHint("Scans a printer, bin, part, or spool code and opens the matching screen.")
            .accessibilityAddTraits(.isButton)
        }
    }

    // MARK: - Quick Actions

    private var quickActionsSection: some View {
        Section {
            quickActionRow(
                icon: "cylinder",
                title: "Log New Spool",
                description: "Continuously scan filament barcodes and QR codes to import spools.",
                identifier: "scan.quickAction.spool"
            ) {
                showBarcodeIntake = true
            }
        } header: {
            Text("Quick Actions")
        }
    }

    private func quickActionRow(icon: String, title: String, description: String, identifier: String, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            HStack(alignment: .top, spacing: 16) {
                Image(systemName: icon)
                    .font(.title2)
                    .foregroundStyle(Color.pfAccent)
                    .frame(width: 44, height: 44)
                    .background(Color.pfAccent.opacity(0.12), in: RoundedRectangle(cornerRadius: 10))
                    .accessibilityHidden(true)

                VStack(alignment: .leading, spacing: 4) {
                    Text(title)
                        .font(.subheadline.weight(.semibold))
                        .foregroundStyle(Color.pfTextPrimary)

                    Text(description)
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .multilineTextAlignment(.leading)
                        .fixedSize(horizontal: false, vertical: true)
                }

                Spacer(minLength: 8)

                Image(systemName: "chevron.right")
                    .font(.caption)
                    .foregroundStyle(.tertiary)
                    .accessibilityHidden(true)
            }
            .padding(.vertical, 4)
            .frame(minHeight: 44)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .accessibilityIdentifier(identifier)
        .accessibilityLabel(title)
        .accessibilityHint(description)
        .accessibilityAddTraits(.isButton)
    }

    // MARK: - NFC hint

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
            Text("iOS handles NFC printer tags automatically. Bins and parts use QR/barcode only.")
        }
    }

    // MARK: - Recent Scans

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

/// Fallback shown when a scanned code doesn't match a printer, bin, part,
/// or known spool barcode.
private struct UnrecognizedScanView: View {
    let code: String

    @Environment(\.dismiss) private var dismiss

    var body: some View {
        NavigationStack {
            ContentUnavailableView {
                Label("Unrecognized Code", systemImage: "questionmark.circle")
            } description: {
                Text("\"\(code)\" didn't match a printer, bin, part, or known spool barcode.")
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
