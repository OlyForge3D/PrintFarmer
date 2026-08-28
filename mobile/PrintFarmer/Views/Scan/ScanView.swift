import SwiftUI

/// Scan tab entry — the operator's scan station (#714, F9).
///
/// A single generic camera scan type-dispatches to whichever physical
/// entity was scanned. Printer and spool destinations are always available;
/// printed-part bins and SKUs participate only when the backend capability
/// enables printed-parts inventory.
struct ScanView: View {
    @Environment(AppRouter.self) private var router
    @Environment(ServiceContainer.self) private var services
    @State private var viewModel = ScanViewModel()
    @State private var showBarcodeIntake = false
    @State private var showPartLookup = false
    @State private var showPrinterLookup = false
    @State private var partLookupSelection: PartInventoryResponse?
    @State private var showOfflineQueue = false

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
            .toolbar {
                ToolbarItem(placement: .topBarTrailing) {
                    Button {
                        showOfflineQueue = true
                    } label: {
                        Label("Offline Queue", systemImage: "tray.and.arrow.up")
                    }
                    .accessibilityIdentifier("scan.offlineQueue")
                }
            }
            .sheet(isPresented: $showOfflineQueue) {
                NavigationStack {
                    OfflineQueueStatusView()
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
            .sheet(isPresented: $showBarcodeIntake) {
                BarcodeIntakeView()
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
            .sheet(isPresented: $showPartLookup) {
                if partsInventoryEnabled {
                    PartLookupView(partsInventoryService: services.partsInventoryService) { part in
                        partLookupSelection = part
                        showPartLookup = false
                    }
                }
            }
            .sheet(item: $partLookupSelection) { part in
                if partsInventoryEnabled {
                    PartScanResultView(part: part)
                }
            }
            .sheet(isPresented: $showPrinterLookup) {
                PrinterLookupView(printerService: services.printerService) { printer in
                    showPrinterLookup = false
                    router.navigate(
                        to: .printerDetail(id: printer.id),
                        capabilities: services.capabilitiesService.resolved
                    )
                }
            }
            .onChange(of: viewModel.pendingDeepLinkDestination) { _, destination in
                guard let destination else { return }
                router.navigate(
                    to: destination,
                    capabilities: services.capabilitiesService.resolved
                )
                viewModel.pendingDeepLinkDestination = nil
            }
            .task {
                viewModel.configure(
                    scanner: services.barcodeScannerService,
                    partsInventoryService: services.partsInventoryService,
                    barcodeIntakeService: services.barcodeIntakeService,
                    spoolService: services.spoolService,
                    printedPartsInventoryEnabled: partsInventoryEnabled
                )
                await services.capabilitiesService.refresh()
            }
            .onChange(of: partsInventoryEnabled) { _, isEnabled in
                viewModel.setPrintedPartsInventoryEnabled(isEnabled)
                guard !isEnabled else { return }
                showPartLookup = false
                partLookupSelection = nil
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

            if partsInventoryEnabled {
                quickActionRow(
                    icon: "cube.box",
                    title: "Log Printed Parts",
                    description: "Look up a printed-part SKU and record a manual stock adjustment.",
                    identifier: "scan.quickAction.parts"
                ) {
                    showPartLookup = true
                }
            }

            quickActionRow(
                icon: "printer",
                title: "Printer Lookup",
                description: "Find a printer by name and open its detail view.",
                identifier: "scan.quickAction.printerLookup"
            ) {
                showPrinterLookup = true
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
            Text(
                partsInventoryEnabled
                    ? "iOS handles NFC printer tags automatically. Bins and parts use QR/barcode only."
                    : "iOS handles NFC printer tags automatically."
            )
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

/// "Log Printed Parts" quick-action lookup list (#714 H6). Lists active
/// printed-part SKUs and forwards the selection into the existing
/// `PartScanResultView` sheet — same manual-adjustment flow reached by
/// scanning a part's barcode directly.
private struct PartLookupView: View {
    let partsInventoryService: any PartsInventoryServiceProtocol
    let onSelect: (PartInventoryResponse) -> Void

    @Environment(\.dismiss) private var dismiss
    @State private var parts: [PartInventoryResponse] = []
    @State private var isLoading = false
    @State private var errorMessage: String?
    @State private var searchText = ""

    private var filteredParts: [PartInventoryResponse] {
        guard !searchText.isEmpty else { return parts }
        return parts.filter {
            $0.name.localizedCaseInsensitiveContains(searchText) || $0.sku.localizedCaseInsensitiveContains(searchText)
        }
    }

    var body: some View {
        NavigationStack {
            Group {
                if isLoading {
                    ProgressView("Loading parts…")
                } else if let errorMessage {
                    ContentUnavailableView {
                        Label("Unable to Load Parts", systemImage: "exclamationmark.triangle")
                    } description: {
                        Text(errorMessage)
                    }
                } else if parts.isEmpty {
                    ContentUnavailableView {
                        Label("No Printed Parts", systemImage: "cube.box")
                    } description: {
                        Text("No printed-part SKUs are configured yet.")
                    }
                } else {
                    List(filteredParts) { part in
                        Button {
                            onSelect(part)
                        } label: {
                            VStack(alignment: .leading, spacing: 2) {
                                Text(part.name)
                                    .font(.subheadline.weight(.medium))
                                Text("SKU \(part.sku) • On hand \(part.onHand)")
                                    .font(.caption)
                                    .foregroundStyle(.secondary)
                            }
                        }
                        .frame(minHeight: 44)
                        .accessibilityIdentifier("scan.partLookup.row.\(part.sku)")
                    }
                    .searchable(text: $searchText, prompt: "Search SKU or name")
                }
            }
            .navigationTitle("Printed Parts")
            #if os(iOS)
            .navigationBarTitleDisplayMode(.inline)
            #endif
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Cancel") { dismiss() }
                }
            }
            .task {
                isLoading = true
                do {
                    parts = try await partsInventoryService.listParts()
                } catch {
                    errorMessage = error.localizedDescription
                }
                isLoading = false
            }
        }
    }
}

/// "Printer Lookup" quick-action list (#714 H6). Lists registered printers
/// and, on selection, dismisses itself and reuses `AppRouter.navigate(to:)`
/// — the same navigation call already used for scanned printer QR codes —
/// to open the printer's detail view.
private struct PrinterLookupView: View {
    let printerService: any PrinterServiceProtocol
    let onSelect: (Printer) -> Void

    @Environment(\.dismiss) private var dismiss
    @State private var printers: [Printer] = []
    @State private var isLoading = false
    @State private var errorMessage: String?
    @State private var searchText = ""

    private var filteredPrinters: [Printer] {
        guard !searchText.isEmpty else { return printers }
        return printers.filter { $0.name.localizedCaseInsensitiveContains(searchText) }
    }

    var body: some View {
        NavigationStack {
            Group {
                if isLoading {
                    ProgressView("Loading printers…")
                } else if let errorMessage {
                    ContentUnavailableView {
                        Label("Unable to Load Printers", systemImage: "exclamationmark.triangle")
                    } description: {
                        Text(errorMessage)
                    }
                } else if printers.isEmpty {
                    ContentUnavailableView {
                        Label("No Printers", systemImage: "printer")
                    } description: {
                        Text("No printers are registered yet.")
                    }
                } else {
                    List(filteredPrinters) { printer in
                        Button {
                            onSelect(printer)
                        } label: {
                            VStack(alignment: .leading, spacing: 2) {
                                Text(printer.name)
                                    .font(.subheadline.weight(.medium))
                                if let notes = printer.notes, !notes.isEmpty {
                                    Text(notes)
                                        .font(.caption)
                                        .foregroundStyle(.secondary)
                                }
                            }
                        }
                        .frame(minHeight: 44)
                        .accessibilityIdentifier("scan.printerLookup.row.\(printer.id.uuidString)")
                    }
                    .searchable(text: $searchText, prompt: "Search printers")
                }
            }
            .navigationTitle("Printer Lookup")
            #if os(iOS)
            .navigationBarTitleDisplayMode(.inline)
            #endif
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Cancel") { dismiss() }
                }
            }
            .task {
                isLoading = true
                do {
                    printers = try await printerService.list(includeDisabled: false)
                } catch {
                    errorMessage = error.localizedDescription
                }
                isLoading = false
            }
        }
    }
}
