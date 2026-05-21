import SwiftUI

struct SpoolPickerView: View {
    @Environment(ServiceContainer.self) private var services
    @Environment(\.dismiss) private var dismiss
    @State private var viewModel = SpoolPickerViewModel()
    @State private var activeTasks: [Task<Void, Never>] = []

    let onSelect: (SpoolmanSpool) -> Void

    var body: some View {
        NavigationStack {
            Group {
                if viewModel.phase == .selectMaterial {
                    materialSelectionView
                } else {
                    spoolSelectionView
                }
            }
            .navigationTitle(viewModel.phase == .selectMaterial ? "Select Material" : "Select Spool")
            #if os(iOS)
            .navigationBarTitleDisplayMode(.inline)
            #endif
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    if viewModel.phase == .selectMaterial {
                        Button("Cancel") { dismiss() }
                    } else {
                        Button("Back") {
                            withAnimation {
                                viewModel.backToMaterialSelection()
                            }
                        }
                    }
                }
                #if os(iOS)
                ToolbarItem(placement: .topBarTrailing) {
                    HStack(spacing: 12) {
                        Button {
                            viewModel.isQRScannerPresented = true
                        } label: {
                            Image(systemName: "qrcode.viewfinder")
                        }
                        .accessibilityLabel("Scan QR code")

                        Button {
                            viewModel.handleNFCScan()
                        } label: {
                            Image(systemName: "wave.3.right")
                        }
                        .accessibilityLabel("Scan NFC tag")
                    }
                }
                #endif
            }
            .overlay {
                if viewModel.isLoading {
                    ProgressView(viewModel.phase == .selectMaterial ? "Loading materials…" : "Loading spools…")
                }
                if viewModel.isScanning {
                    ProgressView("Looking up spool…")
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
            #if os(iOS)
            .sheet(isPresented: $viewModel.isQRScannerPresented) {
                QRScannerView(
                    onScan: { qrText in
                        viewModel.handleQRScan(qrText: qrText)
                    },
                    onCancel: {
                        viewModel.isQRScannerPresented = false
                    }
                )
            }
            #endif
            .sheet(isPresented: $viewModel.showScannedDataSheet) {
                if let data = viewModel.scannedSpoolData {
                    AddSpoolView(scannedData: data)
                        .onDisappear {
                            let task = Task { await viewModel.loadMaterials() }
                            activeTasks.append(task)
                        }
                }
            }
            .task {
                viewModel.configure(spoolService: services.spoolService)
                #if canImport(UIKit)
                if let nfc = services.nfcService {
                    viewModel.configureNFCScanner(nfc)
                }
                #endif
                viewModel.onAutoSelect = { spool in
                    onSelect(spool)
                    dismiss()
                }
                await viewModel.loadMaterials()
            }
            .onAppear { viewModel.isViewActive = true }
            .onDisappear {
                viewModel.isViewActive = false
                activeTasks.forEach { $0.cancel() }
            }
        }
    }

    // MARK: - Material Selection View

    private var materialSelectionView: some View {
        Group {
            if let error = viewModel.errorMessage, viewModel.availableMaterials.isEmpty {
                ContentUnavailableView {
                    Label("Error", systemImage: "exclamationmark.triangle")
                } description: {
                    Text(error)
                } actions: {
                    Button("Retry") {
                        let task = Task { await viewModel.loadMaterials() }
                        activeTasks.append(task)
                    }
                }
            } else if viewModel.availableMaterials.isEmpty && !viewModel.isLoading {
                ContentUnavailableView {
                    Label("No Materials", systemImage: "cube")
                } description: {
                    Text("No spools with remaining filament found. Add spools in the Inventory tab.")
                }
            } else {
                List(viewModel.availableMaterials, id: \.self) { material in
                    Button {
                        withAnimation {
                            viewModel.selectMaterial(material)
                        }
                    } label: {
                        HStack {
                            Text(material)
                                .font(.body)
                                .foregroundStyle(Color.pfTextPrimary)
                            Spacer()
                            Image(systemName: "chevron.right")
                                .font(.caption.weight(.semibold))
                                .foregroundStyle(Color.pfTextTertiary)
                        }
                    }
                }
            }
        }
        .refreshable {
            await viewModel.loadMaterials()
        }
    }

    // MARK: - Spool Selection View

    private var spoolSelectionView: some View {
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
                    if let material = viewModel.selectedMaterial {
                        Text("No \(material) spools available. Try a different material or add spools in the Inventory tab.")
                    } else {
                        Text("No spools available. Add spools in the Inventory tab.")
                    }
                }
            } else if viewModel.hasActiveSearch && viewModel.filteredSpools.isEmpty {
                VStack(spacing: 0) {
                    statusFilterChips
                    Spacer()
                    ContentUnavailableView {
                        Label("No Matching Spools", systemImage: "line.3.horizontal.decrease.circle")
                    } description: {
                        Text(viewModel.activeFilterDescription)
                    } actions: {
                        Button("Clear Filters") {
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
                    statusFilterChips
                    spoolList
                }
            }
        }
        .searchable(text: $viewModel.searchText, prompt: "Search by name, color, vendor…")
        .refreshable {
            await viewModel.loadSpools()
        }
    }

    private var statusFilterChips: some View {
        ScrollView(.horizontal, showsIndicators: false) {
            HStack(spacing: 8) {
                // "All" chip
                Button {
                    withAnimation {
                        viewModel.selectedStatus = nil
                    }
                } label: {
                    Text("All")
                        .font(.caption.weight(.medium))
                        .foregroundStyle(viewModel.selectedStatus == nil ? .white : Color.pfTextSecondary)
                        .padding(.horizontal, 10)
                        .padding(.vertical, 6)
                        .background(
                            viewModel.selectedStatus == nil ? Color.pfAccent : Color.pfBackgroundTertiary,
                            in: Capsule()
                        )
                }

                // Status chips
                ForEach(SpoolStatus.allCases, id: \.self) { status in
                    Button {
                        withAnimation {
                            if viewModel.selectedStatus == status {
                                viewModel.selectedStatus = nil
                            } else {
                                viewModel.selectedStatus = status
                            }
                        }
                    } label: {
                        Text(status.rawValue)
                            .font(.caption.weight(.medium))
                            .foregroundStyle(viewModel.selectedStatus == status ? .white : Color.pfTextSecondary)
                            .padding(.horizontal, 10)
                            .padding(.vertical, 6)
                            .background(
                                viewModel.selectedStatus == status ? Color.pfAccent : Color.pfBackgroundTertiary,
                                in: Capsule()
                            )
                    }
                }
            }
            .padding(.horizontal)
        }
        .padding(.vertical, 8)
    }

    private var spoolList: some View {
        List(viewModel.filteredSpools) { spool in
            Button {
                onSelect(spool)
                dismiss()
            } label: {
                SpoolRowView(spool: spool)
            }
            .tint(Color.pfTextPrimary)
        }
    }
}

// MARK: - Spool Row

struct SpoolRowView: View {
    let spool: SpoolmanSpool

    private var weightPercent: Double? {
        guard let remaining = spool.remainingWeightG,
              let initial = spool.initialWeightG,
              initial > 0 else { return nil }
        return remaining / initial
    }

    private var weightColor: Color {
        guard let percent = weightPercent else { return .gray }
        if percent > 0.5 { return .green }
        if percent > 0.2 { return .yellow }
        return .red
    }

    var body: some View {
        HStack(spacing: 12) {
            // Color swatch
            Circle()
                .fill(Color(hex: spool.colorHex ?? "#808080"))
                .frame(width: 28, height: 28)
                .overlay(
                    Circle()
                        .strokeBorder(Color.pfBorder, lineWidth: 1)
                )

            VStack(alignment: .leading, spacing: 2) {
                HStack(spacing: 6) {
                    Text(spool.filamentName ?? spool.name)
                        .font(.subheadline.weight(.medium))
                        .foregroundStyle(Color.pfTextPrimary)

                    if spool.inUse ?? false {
                        Image(systemName: "printer.fill")
                            .font(.caption2)
                            .foregroundStyle(Color.pfAccent)
                    }
                }

                HStack(spacing: 6) {
                    Text(spool.material)
                        .font(.caption)
                        .foregroundStyle(Color.pfTextSecondary)

                    if let vendor = spool.vendor {
                        Text("•")
                            .font(.caption)
                            .foregroundStyle(Color.pfTextTertiary)
                        Text(vendor)
                            .font(.caption)
                            .foregroundStyle(Color.pfTextSecondary)
                    }
                }
            }

            Spacer()

            VStack(alignment: .trailing, spacing: 4) {
                if let remaining = spool.remainingWeightG {
                    Text("\(Int(remaining))g")
                        .font(.caption.weight(.medium))
                        .foregroundStyle(Color.pfTextSecondary)
                }

                if let initial = spool.initialWeightG, spool.remainingWeightG != nil, initial > 0 {
                    // Weight progress bar
                    GeometryReader { geo in
                        ZStack(alignment: .leading) {
                            Capsule()
                                .fill(Color.pfBackgroundTertiary)

                            Capsule()
                                .fill(weightColor)
                                .frame(width: geo.size.width * (weightPercent ?? 0))
                        }
                    }
                    .frame(width: 50, height: 3)
                }
            }
        }
        .padding(.vertical, 4)
    }
}
