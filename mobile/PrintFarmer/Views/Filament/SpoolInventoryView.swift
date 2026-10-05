import SwiftUI

// Bundles spool + filament so the sheet always receives both values atomically.
private struct NFCWriteTarget: Identifiable {
    let spool: SpoolmanSpool
    let filament: SpoolmanFilament?
    var id: Int { spool.id }
}

struct SpoolInventoryView: View {
    @Environment(ServiceContainer.self) private var services
    @Environment(AppRouter.self) private var router
    @State private var viewModel = SpoolInventoryViewModel()
    @State private var showAddSpool = false
    @State private var showScanFlow = false
    @State private var showBarcodeIntake = false
    @State private var showPrintedParts = false
    @State private var nfcWriteTarget: NFCWriteTarget?
    @State private var activeTasks: [Task<Void, Never>] = []

    var body: some View {
        @Bindable var router = router

        NavigationStack(path: $router.inventoryPath) {
            Group {
                if let error = viewModel.errorMessage, viewModel.spools.isEmpty {
                    ContentUnavailableView {
                        Label("Error", systemImage: "exclamationmark.triangle")
                    } description: {
                        Text(error)
                    } actions: {
                        Button("Retry") {
                            let task = Task { await viewModel.loadSpools() }
                            activeTasks.append(task)
                        }
                    }
                } else if viewModel.spools.isEmpty && !viewModel.isLoading {
                    ContentUnavailableView {
                        Label("No Spools", systemImage: "cylinder")
                    } description: {
                        Text("Add your filament spools to track inventory and assign them to printers.")
                    } actions: {
                        Button("Add Spool") {
                            showAddSpool = true
                        }
                        .buttonStyle(.borderedProminent)
                        .tint(Color.pfAccent)
                    }
                } else if viewModel.hasActiveSearch && viewModel.filteredSpools.isEmpty {
                    VStack(spacing: 0) {
                        inventoryFilters
                        Spacer()
                        ContentUnavailableView {
                            Label("No Matching Spools", systemImage: "line.3.horizontal.decrease.circle")
                        } description: {
                            Text(viewModel.activeFilterDescription)
                        } actions: {
                            Button("Reset") {
                                withAnimation {
                                    viewModel.clearFilters()
                                }
                            }
                            .buttonStyle(.borderedProminent)
                            .tint(Color.pfAccent)
                        }
                        Spacer()
                    }
                } else {
                    VStack(spacing: 0) {
                        inventoryFilters
                        spoolList
                    }
                }
            }
            .navigationTitle("Filament")
            #if os(iOS)
            .navigationBarTitleDisplayMode(.large)
            #endif
            .rootNavigationChrome(for: .filament) {
                if services.capabilitiesService.resolved.printedPartsInventoryEnabled {
                    Button {
                        showPrintedParts = true
                    } label: {
                        Image(systemName: "cube.box")
                            .frame(
                                minWidth: RootNavigationChrome.minimumTouchTarget,
                                minHeight: RootNavigationChrome.minimumTouchTarget
                            )
                    }
                    .accessibilityLabel("Printed parts")
                    .accessibilityHint("Opens printed-part stock and quantity adjustments.")
                    .accessibilityIdentifier("filament.printedParts")
                }
                Menu {
                    Button {
                        showScanFlow = true
                    } label: {
                        Label("Scan code", systemImage: "barcode.viewfinder")
                    }

                    Button {
                        viewModel.handleNFCScan()
                    } label: {
                        Label("Scan NFC tag", systemImage: "wave.3.right")
                    }
                    .accessibilityIdentifier("inventory.scan.nfc")

                    Button {
                        showBarcodeIntake = true
                    } label: {
                        Label("Log new spools", systemImage: "cylinder")
                    }
                    .accessibilityIdentifier("inventory.scan.barcodeIntake")
                } label: {
                    Image(systemName: "barcode.viewfinder")
                        .frame(
                            minWidth: RootNavigationChrome.minimumTouchTarget,
                            minHeight: RootNavigationChrome.minimumTouchTarget
                        )
                }
                .accessibilityLabel("Scan inventory")
                .accessibilityHint("Opens camera, NFC, and continuous spool intake actions.")
                .accessibilityIdentifier("inventory.scan")

            }
            .searchable(text: $viewModel.searchText, prompt: "Search by name, material, color…")
            .refreshable {
                await viewModel.loadSpools()
            }
            .navigationDestination(for: AppDestination.self) { destination in
                destinationView(for: destination)
            }
            .overlay {
                if viewModel.isLoading && viewModel.spools.isEmpty {
                    ProgressView("Loading inventory…")
                }
                if viewModel.isScanning {
                    ProgressView("Scanning…")
                        .padding()
                        .background(.ultraThinMaterial, in: RoundedRectangle(cornerRadius: 12))
                }
            }
            .alert("Scan Error", isPresented: .constant(viewModel.scanError != nil)) {
                Button("OK") { viewModel.scanError = nil }
            } message: {
                if let error = viewModel.scanError {
                    Text(error)
                }
            }
            .sheet(isPresented: $showAddSpool) {
                AddSpoolView()
                    .onDisappear {
                        let task = Task { await viewModel.loadSpools() }
                        activeTasks.append(task)
                    }
            }
            .sheet(isPresented: $showScanFlow, onDismiss: {
                router.completeScanFlowDismissal(capabilities: services.capabilitiesService.resolved)
            }, content: {
                ScanFlowView()
            })
            .sheet(isPresented: $showBarcodeIntake) {
                BarcodeIntakeView()
                    .onDisappear {
                        let task = Task { await viewModel.loadSpools() }
                        activeTasks.append(task)
                    }
            }
            .sheet(isPresented: $showPrintedParts) {
                NavigationStack {
                    if services.capabilitiesService.resolved.printedPartsInventoryEnabled {
                        PartsInventoryListView()
                            .navigationDestination(for: AppDestination.self) { destination in
                                destinationView(for: destination)
                            }
                            .toolbar {
                                ToolbarItem(placement: .cancellationAction) {
                                    Button("Done") { showPrintedParts = false }
                                }
                            }
                    }
                }
            }
            .onChange(of: services.capabilitiesService.resolved.printedPartsInventoryEnabled) { _, enabled in
                if !enabled { showPrintedParts = false }
            }
            .sheet(isPresented: $viewModel.showScannedDataSheet) {
                if let data = viewModel.scannedSpoolData {
                    AddSpoolView(scannedData: data)
                        .onDisappear {
                            let task = Task { await viewModel.loadSpools() }
                            activeTasks.append(task)
                        }
                }
            }
            .sheet(item: $nfcWriteTarget) { target in
                NFCWriteView(spool: target.spool, filament: target.filament) {
                    await viewModel.writeNFCTag(for: target.spool)
                }
            }
            .task {
                viewModel.configure(spoolService: services.spoolService)
                viewModel.configure(printerService: services.printerService)
                #if canImport(UIKit)
                if let nfc = services.nfcService {
                    viewModel.configureNFC(scanner: nfc)
                }
                #endif
                await viewModel.loadSpools()
                if let spoolId = router.pendingSpoolHighlightId {
                    router.pendingSpoolHighlightId = nil
                    viewModel.setHighlight(spoolId: spoolId)
                }
            }
            .onAppear { viewModel.isViewActive = true }
            .onChange(of: router.pendingSpoolHighlightId) { _, spoolId in
                guard let spoolId else { return }
                router.pendingSpoolHighlightId = nil
                viewModel.setHighlight(spoolId: spoolId)
            }
            .onDisappear {
                viewModel.isViewActive = false
                viewModel.invalidateHighlightOwnership()
                activeTasks.forEach { $0.cancel() }
            }
        }
    }

    private var inventoryFilters: some View {
        ScrollView(.horizontal, showsIndicators: false) {
            HStack(spacing: 8) {
                inventoryFilter(
                    title: "All \(viewModel.count(for: nil))",
                    selected: viewModel.selectedStatus == nil
                        && viewModel.selectedMaterial == nil
                        && !viewModel.showOnlyMissingNFC
                ) {
                    withAnimation { viewModel.clearFilters() }
                }
                inventoryFilter(
                    title: "Loaded \(viewModel.count(for: .inUse))",
                    selected: viewModel.selectedStatus == .inUse
                ) {
                    withAnimation {
                        viewModel.selectedStatus = viewModel.selectedStatus == .inUse ? nil : .inUse
                    }
                }
                inventoryFilter(
                    title: "Low \(viewModel.count(for: .low))",
                    selected: viewModel.selectedStatus == .low
                ) {
                    withAnimation {
                        viewModel.selectedStatus = viewModel.selectedStatus == .low ? nil : .low
                    }
                }

                ForEach(viewModel.availableMaterials, id: \.self) { material in
                    inventoryFilter(
                        title: material,
                        selected: viewModel.selectedMaterial == material
                    ) {
                        withAnimation {
                            if viewModel.selectedMaterial == material {
                                viewModel.selectedMaterial = nil
                            } else {
                                viewModel.selectedMaterial = material
                            }
                        }
                    }
                }
                Button {
                    withAnimation { viewModel.showOnlyMissingNFC.toggle() }
                } label: {
                    Label("No NFC", systemImage: "wave.3.right.circle")
                        .font(.caption.weight(.medium))
                        .foregroundStyle(viewModel.showOnlyMissingNFC ? .white : Color.pfTextSecondary)
                        .padding(.horizontal, 10)
                        .padding(.vertical, 6)
                        .background(
                            viewModel.showOnlyMissingNFC ? Color.pfAccent : Color.pfBackgroundTertiary,
                            in: Capsule()
                        )
                }
                .accessibilityLabel(
                    viewModel.showOnlyMissingNFC
                        ? "Showing spools without NFC tags"
                        : "Filter to spools without NFC tags"
                )
            }
            .padding(.horizontal)
        }
        .padding(.vertical, 8)
    }

    private func inventoryFilter(
        title: String,
        selected: Bool,
        action: @escaping () -> Void
    ) -> some View {
        Button(action: action) {
            Text(title)
                .font(.caption.weight(.medium))
                .foregroundStyle(selected ? .white : Color.pfTextSecondary)
                .padding(.horizontal, 10)
                .padding(.vertical, 6)
                .background(
                    selected ? Color.pfAccent : Color.pfBackgroundTertiary,
                    in: Capsule()
                )
        }
    }

    private var spoolList: some View {
        ScrollViewReader { proxy in
            VStack(spacing: 0) {
                Button {
                    showAddSpool = true
                } label: {
                    Label("Add spool", systemImage: "plus")
                        .font(.headline)
                        .frame(maxWidth: .infinity, minHeight: 48)
                }
                .buttonStyle(.borderedProminent)
                .tint(Color.pfAccent)
                .accessibilityHint("Opens the form to register a filament spool.")
                .accessibilityIdentifier("inventory.addSpool")
                .padding(.horizontal)
                .padding(.vertical, 8)

                List {
                    ForEach(viewModel.filteredSpools) { spool in
                        SpoolInventoryRowView(
                            spool: spool,
                            assignedPrinterName: viewModel.assignedPrinterName(for: spool.id),
                            assignmentsLoaded: viewModel.printerAssignmentsLoaded
                        )
                            .listRowBackground(
                                viewModel.highlightedSpoolId == spool.id
                                    ? Color.pfAccent.opacity(0.15)
                                    : nil
                            )
                            .id(spool.id)
                            .contextMenu {
                                if spool.hasNfcTag != true {
                                    Button {
                                        let task = Task {
                                            let filament = await viewModel.matchingFilamentForTagPreview(for: spool)
                                            nfcWriteTarget = NFCWriteTarget(spool: spool, filament: filament)
                                        }
                                        activeTasks.append(task)
                                    } label: {
                                        Label("Write NFC Tag", systemImage: "wave.3.right")
                                    }
                                }
                            }
                    }
                    .onDelete { indexSet in
                        let spoolsToDelete = indexSet.map { viewModel.filteredSpools[$0] }
                        for spool in spoolsToDelete {
                            let task = Task { await viewModel.deleteSpool(spool) }
                            activeTasks.append(task)
                        }
                    }
                }
                .listStyle(.plain)
            }
            .onChange(of: viewModel.highlightedSpoolId) { _, newId in
                // Scroll animation only. Highlight expiry is owned synchronously
                // by the view model inside `setHighlight`, so no expiry task is
                // spawned here — this avoids the assignment-to-authority gap
                // that previously let a stale timer clear a newer highlight.
                if let newId {
                    withAnimation {
                        proxy.scrollTo(newId, anchor: .center)
                    }
                }
            }
        }
    }
}

// MARK: - Inventory Row

struct SpoolInventoryRowView: View {
    let spool: SpoolmanSpool
    var assignedPrinterName: String? = nil
    var assignmentsLoaded = false

    private var weightPercent: Double? {
        guard let remaining = spool.remainingWeightG,
              let initial = spool.initialWeightG,
              remaining.isFinite, remaining >= 0,
              initial.isFinite, initial > 0 else { return nil }
        return min(max(remaining / initial, 0), 1)
    }

    private var weightColor: Color {
        guard let percent = weightPercent else { return .gray }
        if percent < 0.2 { return .pfError }
        if percent < 0.5 { return .pfWarning }
        return .pfSuccess
    }

    var body: some View {
        HStack(spacing: 12) {
            spoolReel

            VStack(alignment: .leading, spacing: 5) {
                Text(spool.filamentName ?? spool.name)
                    .font(.subheadline.weight(.medium))
                    .foregroundStyle(Color.pfTextPrimary)
                    .lineLimit(2)

                if let assignedPrinterName {
                    Label("On \(assignedPrinterName)", systemImage: "printer.fill")
                        .font(.caption)
                        .foregroundStyle(Color.pfAccent)
                        .lineLimit(1)
                } else if spool.inUse || !assignmentsLoaded {
                    Text("Printer assignment unavailable")
                        .font(.caption)
                        .foregroundStyle(Color.pfTextSecondary)
                } else {
                    Text(spool.location.map { "\($0) · unassigned" } ?? "Unassigned")
                        .font(.caption)
                        .foregroundStyle(Color.pfTextSecondary)
                        .lineLimit(1)
                }

                HStack(spacing: 6) {
                    Text(spool.material)
                        .font(.caption2.weight(.medium))
                        .foregroundStyle(Color.pfTextSecondary)
                    if let vendor = spool.vendor, !vendor.isEmpty {
                        Text("· \(vendor)")
                            .font(.caption2)
                            .foregroundStyle(Color.pfTextTertiary)
                            .lineLimit(1)
                    }
                    if spool.hasNfcTag == true {
                        Image(systemName: "wave.3.right")
                            .font(.caption2)
                            .foregroundStyle(Color.pfSuccess)
                            .accessibilityLabel("NFC tag present")
                    } else {
                        Image(systemName: "wave.3.right")
                            .font(.caption2)
                            .foregroundStyle(Color.pfTextTertiary)
                            .accessibilityLabel("NFC tag not written")
                    }
                }
            }

            Spacer(minLength: 4)

            VStack(alignment: .trailing, spacing: 4) {
                Text(spool.remainingWeightG.flatMap {
                    $0.isFinite && $0 >= 0 ? "\(Int($0.rounded())) g" : nil
                } ?? "— g")
                    .font(.subheadline.weight(.semibold).monospacedDigit())
                    .foregroundStyle(Color.pfTextPrimary)
                if let initial = spool.initialWeightG, initial.isFinite, initial > 0,
                   let remaining = spool.remainingWeightG, remaining.isFinite, remaining >= 0 {
                    Text("of \(Int(initial.rounded())) g")
                        .font(.caption2)
                        .foregroundStyle(Color.pfTextTertiary)
                    if let weightPercent {
                        GeometryReader { geo in
                            ZStack(alignment: .leading) {
                                Capsule().fill(Color.pfBackgroundTertiary)
                                Capsule()
                                    .fill(weightColor)
                                    .frame(width: geo.size.width * weightPercent)
                            }
                        }
                        .frame(width: 54, height: 4)
                    }
                    if let weightPercent, weightPercent < 0.2 {
                        Text("Low")
                            .font(.caption2.weight(.semibold))
                            .foregroundStyle(Color.pfError)
                    }
                } else {
                    Text("Weight unavailable")
                        .font(.caption2)
                        .foregroundStyle(Color.pfTextTertiary)
                }
            }
        }
        .padding(.vertical, 7)
        .accessibilityElement(children: .combine)
        .accessibilityIdentifier("inventory.spool.\(spool.id)")
        .accessibilityLabel(spoolAccessibilityLabel)
    }

    private var spoolAccessibilityLabel: String {
        var parts = [spool.filamentName ?? spool.name, spool.material]
        if let assignedPrinterName {
            parts.append("On \(assignedPrinterName)")
        } else if spool.inUse || !assignmentsLoaded {
            parts.append("Printer assignment unavailable")
        } else {
            parts.append(spool.location.map { "\($0), unassigned" } ?? "Unassigned")
        }
        if let remaining = spool.remainingWeightG, remaining.isFinite, remaining >= 0 {
            parts.append("\(Int(remaining.rounded())) grams remaining")
        } else {
            parts.append("Weight unavailable")
        }
        return parts.joined(separator: ", ")
    }

    private var spoolReel: some View {
        ZStack {
            Circle()
                .fill(Color(hex: spool.colorHex ?? "#808080"))
            Circle()
                .strokeBorder(Color.pfBorder.opacity(0.8), lineWidth: 1)
            Circle()
                .fill(Color.pfBackground)
                .frame(width: 13, height: 13)
            Circle()
                .strokeBorder(Color.pfBorder, lineWidth: 1)
                .frame(width: 13, height: 13)
        }
        .frame(width: 44, height: 44)
        .accessibilityHidden(true)
    }
}
