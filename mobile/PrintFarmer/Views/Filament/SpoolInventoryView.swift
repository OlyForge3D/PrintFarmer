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
    @Environment(\.dynamicTypeSize) private var dynamicTypeSize
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
        Group {
            if dynamicTypeSize.isAccessibilitySize {
                ScrollView(.horizontal) {
                    HStack(spacing: 8) {
                        inventoryFilterButtons
                    }
                    .fixedSize(horizontal: true, vertical: false)
                }
                .scrollIndicators(.hidden)
                .accessibilityIdentifier("inventory.filters.accessibility")
            } else {
                ViewThatFits(in: .horizontal) {
                    HStack(spacing: 8) {
                        inventoryFilterButtons
                    }
                    .fixedSize(horizontal: true, vertical: false)

                    LazyVGrid(
                        columns: [GridItem(.adaptive(minimum: 88, maximum: 150), alignment: .leading)],
                        alignment: .leading,
                        spacing: 8
                    ) {
                        inventoryFilterButtons
                    }
                }
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(.horizontal)
        .padding(.vertical, 8)
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier("inventory.filters")
    }

    @ViewBuilder
    private var inventoryFilterButtons: some View {
        inventoryFilter(
            title: "All \(viewModel.count(for: nil))",
            identifier: "inventory.filter.all",
            selected: viewModel.selectedStatus == nil
                && viewModel.selectedMaterial == nil
                && !viewModel.showOnlyMissingNFC
        ) {
            withAnimation { viewModel.clearFilters() }
        }
        inventoryFilter(
            title: "Loaded \(viewModel.count(for: .inUse))",
            identifier: "inventory.filter.loaded",
            selected: viewModel.selectedStatus == .inUse
        ) {
            withAnimation {
                viewModel.selectedStatus = viewModel.selectedStatus == .inUse ? nil : .inUse
            }
        }
        inventoryFilter(
            title: "Low \(viewModel.count(for: .low))",
            identifier: "inventory.filter.low",
            selected: viewModel.selectedStatus == .low
        ) {
            withAnimation {
                viewModel.selectedStatus = viewModel.selectedStatus == .low ? nil : .low
            }
        }

        ForEach(primaryInventoryMaterials, id: \.self) { material in
            materialFilter(material)
        }

        Menu {
            if !additionalInventoryMaterials.isEmpty {
                Section("Material") {
                    ForEach(additionalInventoryMaterials, id: \.self) { material in
                        materialMenuFilter(material)
                    }
                }
            }

            Button {
                withAnimation { viewModel.showOnlyMissingNFC.toggle() }
            } label: {
                Label(
                    "No NFC",
                    systemImage: viewModel.showOnlyMissingNFC ? "checkmark" : "wave.3.right.circle"
                )
            }
            .accessibilityIdentifier("inventory.filter.no-nfc")
        } label: {
            let activeCount = (viewModel.selectedMaterial.map(additionalInventoryMaterials.contains) == true ? 1 : 0)
                + (viewModel.showOnlyMissingNFC ? 1 : 0)
            Text(activeCount == 0 ? "More" : "More \(activeCount)")
                .font(.caption.weight(.medium))
                .foregroundStyle(activeCount > 0 ? .white : Color.pfTextSecondary)
                .padding(.horizontal, 10)
                .padding(.vertical, 6)
                .background(
                    activeCount > 0 ? Color.pfAccent : Color.pfBackgroundTertiary,
                    in: Capsule()
                )
        }
        .accessibilityLabel(
            viewModel.showOnlyMissingNFC
                ? "More spool filters, including no NFC"
                : "More spool filters"
        )
        .accessibilityValue(
            viewModel.selectedMaterial.map(additionalInventoryMaterials.contains) == true
                ? "A material filter is active"
                : viewModel.showOnlyMissingNFC ? "No NFC filter is active" : ""
        )
        .accessibilityIdentifier("inventory.filter.more")
    }

    private var primaryInventoryMaterials: [String] {
        let preferred = ["PLA", "PETG"].compactMap { material in
            viewModel.availableMaterials.first {
                $0.localizedCaseInsensitiveCompare(material) == .orderedSame
            }
        }
        return Array((preferred + viewModel.availableMaterials.filter { material in
            !preferred.contains { $0.localizedCaseInsensitiveCompare(material) == .orderedSame }
        }).prefix(2))
    }

    private var additionalInventoryMaterials: [String] {
        viewModel.availableMaterials.filter { material in
            !primaryInventoryMaterials.contains {
                $0.localizedCaseInsensitiveCompare(material) == .orderedSame
            }
        }
    }

    private func materialFilter(_ material: String) -> some View {
        inventoryFilter(
            title: material,
            identifier: "inventory.filter.material.\(material)",
            selected: viewModel.selectedMaterial == material
        ) {
            withAnimation {
                viewModel.selectedMaterial = viewModel.selectedMaterial == material ? nil : material
            }
        }
    }

    private func materialMenuFilter(_ material: String) -> some View {
        Button {
            withAnimation {
                viewModel.selectedMaterial = viewModel.selectedMaterial == material ? nil : material
            }
        } label: {
            if viewModel.selectedMaterial == material {
                Label(material, systemImage: "checkmark")
            } else {
                Text(material)
            }
        }
        .accessibilityIdentifier("inventory.filter.material.\(material)")
    }

    private func inventoryFilter(
        title: String,
        identifier: String,
        selected: Bool,
        action: @escaping () -> Void
    ) -> some View {
        Button(action: action) {
            Text(title)
                .font(.caption.weight(.medium))
                .fixedSize(horizontal: dynamicTypeSize.isAccessibilitySize, vertical: false)
                .foregroundStyle(selected ? .white : Color.pfTextSecondary)
                .padding(.horizontal, 10)
                .padding(.vertical, 6)
                .frame(minHeight: 44)
                .background(
                    selected ? Color.pfAccent : Color.pfBackgroundTertiary,
                    in: Capsule()
                )
        }
        .accessibilityIdentifier(identifier)
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
    @Environment(\.dynamicTypeSize) private var dynamicTypeSize

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
        Group {
            if dynamicTypeSize.isAccessibilitySize {
                VStack(alignment: .leading, spacing: 10) {
                    HStack(alignment: .top, spacing: 12) {
                        spoolReel
                        spoolDetails
                    }
                    HStack(alignment: .top) {
                        Text("Remaining")
                            .font(.caption)
                            .foregroundStyle(Color.pfTextSecondary)
                        Spacer(minLength: 8)
                        weightSummary
                    }
                }
            } else {
                HStack(spacing: 12) {
                    spoolReel
                    spoolDetails
                    Spacer(minLength: 4)
                    weightSummary
                }
            }
        }
        .padding(.vertical, 7)
        .accessibilityElement(children: .combine)
        .accessibilityIdentifier("inventory.spool.\(spool.id)")
        .accessibilityLabel(spoolAccessibilityLabel)
    }

    private var spoolDetails: some View {
        VStack(alignment: .leading, spacing: 5) {
            Text(spool.filamentName ?? spool.name)
                .font(.subheadline.weight(.medium))
                .foregroundStyle(Color.pfTextPrimary)
                .lineLimit(dynamicTypeSize.isAccessibilitySize ? nil : 2)
                .fixedSize(horizontal: false, vertical: true)

            if let assignedPrinterName {
                Label("On \(assignedPrinterName)", systemImage: "printer.fill")
                    .font(.caption)
                    .foregroundStyle(Color.pfAccent)
                    .lineLimit(dynamicTypeSize.isAccessibilitySize ? nil : 1)
                    .fixedSize(horizontal: false, vertical: true)
            } else if spool.inUse || !assignmentsLoaded {
                Text("Printer assignment unavailable")
                    .font(.caption)
                    .foregroundStyle(Color.pfTextSecondary)
                    .fixedSize(horizontal: false, vertical: true)
            } else {
                Text(spool.location.map { "\($0) · unassigned" } ?? "Unassigned")
                    .font(.caption)
                    .foregroundStyle(Color.pfTextSecondary)
                    .lineLimit(dynamicTypeSize.isAccessibilitySize ? nil : 1)
                    .fixedSize(horizontal: false, vertical: true)
            }

            Group {
                if dynamicTypeSize.isAccessibilitySize {
                    VStack(alignment: .leading, spacing: 2) {
                        materialLabel
                        nfcStatus
                    }
                } else {
                    HStack(spacing: 6) {
                        materialLabel
                        nfcStatus
                    }
                }
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    private var materialLabel: some View {
        Group {
            if dynamicTypeSize.isAccessibilitySize {
                VStack(alignment: .leading, spacing: 2) {
                    materialName
                    vendorName
                }
            } else {
                HStack(spacing: 4) {
                    materialName
                    vendorName
                }
            }
        }
    }

    private var materialName: some View {
        Text(spool.material)
            .font(.caption2.weight(.medium))
            .foregroundStyle(Color.pfTextSecondary)
    }

    @ViewBuilder
    private var vendorName: some View {
        if let vendor = spool.vendor, !vendor.isEmpty {
            Text("· \(vendor)")
                .font(.caption2)
                .foregroundStyle(Color.pfTextTertiary)
                .lineLimit(dynamicTypeSize.isAccessibilitySize ? nil : 1)
                .fixedSize(horizontal: false, vertical: true)
        }
    }

    private var nfcStatus: some View {
        Image(systemName: "wave.3.right")
            .font(dynamicTypeSize.isAccessibilitySize ? .system(size: 16) : .caption2)
            .foregroundStyle(spool.hasNfcTag == true ? Color.pfSuccess : Color.pfTextTertiary)
            .accessibilityLabel(spool.hasNfcTag == true ? "NFC tag present" : "NFC tag not written")
    }

    private var weightSummary: some View {
        VStack(alignment: dynamicTypeSize.isAccessibilitySize ? .leading : .trailing, spacing: 4) {
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
                    .frame(width: dynamicTypeSize.isAccessibilitySize ? 96 : 54, height: 4)
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
