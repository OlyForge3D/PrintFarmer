import SwiftUI

/// Shared scanner presented from every farm-floor tab.
///
/// The underlying dispatcher and scanner service are unchanged, so camera
/// permission is still requested only after the operator taps the primary Scan
/// control. The mobile app manages spools only, so every scan resolves to a
/// spool: existing spools dismiss this sheet and open in the Filament tab,
/// while product barcodes (known or new), NFC new-spool data, and unrecognized
/// codes remain in-place sheets.
struct ScanFlowView: View {
    let externalScanRequestID: UUID?

    @Environment(AppRouter.self) private var router
    @Environment(ServiceContainer.self) private var services
    @Environment(\.dismiss) private var dismiss
    @State private var viewModel = ScanViewModel()
    @State private var isErrorAlertPresented = false

    init(externalScanRequestID: UUID? = nil) {
        self.externalScanRequestID = externalScanRequestID
    }

    private let scanDescription = "Scan a spool label or filament barcode. New barcodes can be added as spools."
    private let scanHint = "Scans a spool label or filament barcode and opens the spool, or offers to add a new spool."

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
            .alert("Scan Error", isPresented: $isErrorAlertPresented) {
                Button("OK") {}
            } message: {
                if let error = viewModel.errorMessage {
                    Text(error)
                }
            }
            .onChange(of: viewModel.errorMessage, initial: true) { _, error in
                isErrorAlertPresented = error != nil
            }
            .onChange(of: isErrorAlertPresented) { _, isPresented in
                if !isPresented {
                    acknowledgePresentationDismissal(viewModel.clearError)
                }
            }
            .sheet(
                item: $viewModel.pendingOutcome,
                onDismiss: resultPresentationDidDismiss,
                content: { outcome in
                    Group {
                        switch outcome {
                        case .unknownCode(let code):
                            UnrecognizedScanView(code: code) {
                                viewModel.addSpool(fromUnrecognized: code)
                            }
                        case .newSpool(let data):
                            AddSpoolView(scannedData: data)
                        case .missingSpool(let id, let code):
                            UnrecognizedScanView(
                                code: code,
                                title: "Spool Not Found",
                                message: "Spool #\(id) isn't on this server. You can add a new spool for this label."
                            ) {
                                viewModel.addSpool(fromUnrecognized: code)
                            }
                        case .addSpoolManually:
                            AddSpoolView()
                        case .unknownNFCTag(let tag, let data):
                            NFCTagLinkView(
                                uid: tag.uid,
                                scannedData: data,
                                spoolService: services.spoolService
                            ) { spool in
                                viewModel.completeNFCTagLink(to: spool)
                            }
                        }
                    }
                    .onAppear { viewModel.resultPresentationDidAppear() }
                }
            )
            .sheet(isPresented: Binding(
                get: { viewModel.pendingSpoolBarcode != nil },
                set: { if !$0 { viewModel.pendingSpoolBarcode = nil } }
            ), onDismiss: resultPresentationDidDismiss, content: {
                if let barcode = viewModel.pendingSpoolBarcode {
                    BarcodeIntakeView(initialBarcode: barcode)
                        .onAppear { viewModel.resultPresentationDidAppear() }
                }
            })
            .onChange(of: viewModel.pendingDeepLinkDestination) { _, destination in
                guard let destination else { return }
                router.beginScanFlowDismissal(
                    queuedExternalRequestID: viewModel.finishNavigation()
                )
                router.navigate(
                    to: destination,
                    capabilities: services.capabilitiesService.resolved
                )
                dismiss()
            }
            .task {
                viewModel.configure(
                    scanner: services.barcodeScannerService,
                    barcodeIntakeService: services.barcodeIntakeService,
                    spoolService: services.spoolService,
                    waitForScannerPresentation: {
                        #if canImport(UIKit)
                        await ScanPresentationReadiness.waitUntilReady()
                        #else
                        true
                        #endif
                    }
                )
                #if canImport(UIKit)
                if let nfc = services.nfcService {
                    viewModel.configureNFC(scanner: nfc)
                }
                #endif
                startExternalScanIfNeeded()
                await services.capabilitiesService.refresh()
            }
            .onChange(of: externalScanRequestID) {
                startExternalScanIfNeeded()
            }
            .onAppear { viewModel.isViewActive = true }
            .onDisappear { viewModel.isViewActive = false }
        }
    }

    private func resultPresentationDidDismiss() {
        acknowledgePresentationDismissal(viewModel.resultPresentationDidDismiss)
    }

    private func acknowledgePresentationDismissal(_ acknowledge: @escaping @MainActor () -> Void) {
        Task { @MainActor in
            #if canImport(UIKit)
            guard await ScanPresentationReadiness.waitUntilReady() else { return }
            #endif
            acknowledge()
        }
    }

    private func startExternalScanIfNeeded() {
        guard let externalScanRequestID else { return }
        viewModel.requestExternalScan(externalScanRequestID)
        if viewModel.hasFinishedNavigation, let requestID = viewModel.finishNavigation() {
            router.beginScanFlowDismissal(queuedExternalRequestID: requestID)
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
            Button {
                viewModel.scanNFC()
            } label: {
                Label("Scan NFC tag", systemImage: "wave.3.right")
                    .frame(minHeight: 44)
            }
            .disabled(viewModel.isScanning)
            .accessibilityLabel("Scan NFC tag")
            .accessibilityHint("Reads a filament spool tag.")
            .accessibilityIdentifier("scan.nfc")
            Label(
                "Read a spool tag to open the spool or add a new one.",
                systemImage: "wave.3.right"
            )
            .font(.footnote)
            .foregroundStyle(.secondary)
            .accessibilityIdentifier("scan.nfc.hint")
        } header: {
            Text("NFC Tags")
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
    var title = "Unrecognized Code"
    var message: String?
    let onAddSpool: () -> Void

    @Environment(\.dismiss) private var dismiss

    private var description: String {
        message ?? "\"\(code)\" isn't a spool label, a filament barcode, or a code PrintFarmer can remember. "
            + "You can still add the spool manually."
    }

    var body: some View {
        NavigationStack {
            ContentUnavailableView {
                Label(title, systemImage: "questionmark.circle")
            } description: {
                Text(description)
            } actions: {
                Button {
                    onAddSpool()
                } label: {
                    Label("Add Spool", systemImage: "plus.circle")
                }
                .buttonStyle(.borderedProminent)
                .tint(Color.pfButtonPrimary)
                .foregroundStyle(Color.pfButtonPrimaryText)
                .accessibilityIdentifier("scan.unrecognized.addSpool")
            }
            .navigationTitle(message == nil ? "Unrecognized" : "Not Found")
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
