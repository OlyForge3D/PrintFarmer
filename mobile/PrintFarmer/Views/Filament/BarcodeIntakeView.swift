import SwiftUI

struct BarcodeIntakeView: View {
    @Environment(ServiceContainer.self) private var services
    @Environment(\.dismiss) private var dismiss
    @State private var viewModel = BarcodeIntakeViewModel()

    var body: some View {
        NavigationStack {
            List {
                Section {
                    VStack(spacing: 16) {
                        Image(systemName: "barcode.viewfinder")
                            .font(.system(size: 48))
                            .foregroundStyle(Color.pfAccent)

                        Text("Scan spool barcodes")
                            .font(.headline)
                            .foregroundStyle(Color.pfTextPrimary)

                        Text("Known barcodes import instantly. Unknown barcodes ask you to choose or create a filament once, then continue scanning.")
                            .font(.subheadline)
                            .foregroundStyle(Color.pfTextSecondary)
                            .multilineTextAlignment(.center)

                        Button {
                            viewModel.scanNext()
                        } label: {
                            Label(viewModel.importedThisSession.isEmpty ? "Start Scanning" : "Scan Next", systemImage: "barcode.viewfinder")
                                .frame(maxWidth: .infinity)
                        }
                        .buttonStyle(.borderedProminent)
                        .tint(Color.pfAccent)
                        .disabled(viewModel.isScanning || viewModel.isBusy)
                        .accessibilityLabel("Scan next barcode")
                    }
                    .frame(maxWidth: .infinity)
                    .padding(.vertical, 12)
                }

                if let barcode = viewModel.lastScannedBarcode {
                    Section("Last Scan") {
                        Text(barcode)
                            .font(.body.monospaced())
                    }
                }

                Section("Imported This Session (\(viewModel.importedCount))") {
                    if viewModel.importedThisSession.isEmpty {
                        ContentUnavailableView {
                            Label("No Imports Yet", systemImage: "tray")
                        } description: {
                            Text("Scanned spools will appear here and can be edited later from inventory.")
                        }
                    } else {
                        ForEach(viewModel.importedThisSession) { spool in
                            SpoolInventoryRowView(spool: spool)
                        }
                    }
                }
            }
            .navigationTitle("Barcode Intake")
            #if os(iOS)
            .navigationBarTitleDisplayMode(.inline)
            #endif
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Done") { dismiss() }
                        .accessibilityLabel("Done with barcode intake")
                }
            }
            .overlay {
                if viewModel.isScanning || viewModel.isBusy {
                    ProgressView(viewModel.isScanning ? "Scanning…" : "Importing…")
                        .padding()
                        .background(.ultraThinMaterial, in: RoundedRectangle(cornerRadius: 12))
                }
            }
            .alert("Barcode Intake Error", isPresented: .constant(viewModel.errorMessage != nil)) {
                Button("OK") { viewModel.clearError() }
            } message: {
                if let error = viewModel.errorMessage {
                    Text(error)
                }
            }
            .sheet(isPresented: Binding(
                get: { viewModel.pendingUnknownBarcode != nil },
                set: { if !$0 { viewModel.skipUnknownBarcode() } }
            )) {
                if let barcode = viewModel.pendingUnknownBarcode {
                    UnknownBarcodeView(
                        barcode: barcode,
                        spoolService: services.spoolService,
                        onSelectFilament: { filament in
                            await viewModel.importUnknownBarcode(with: filament)
                        }
                    )
                    .interactiveDismissDisabled(viewModel.isBusy)
                }
            }
            .task {
                viewModel.configure(
                    barcodeService: services.barcodeIntakeService,
                    scanner: services.barcodeScannerService
                )
            }
            .onAppear { viewModel.isViewActive = true }
            .onDisappear { viewModel.isViewActive = false }
        }
    }
}

private struct UnknownBarcodeView: View {
    let barcode: String
    let spoolService: any SpoolServiceProtocol
    let onSelectFilament: @MainActor (SpoolmanFilament) async -> Void

    @Environment(\.dismiss) private var dismiss
    @State private var filaments: [SpoolmanFilament] = []
    @State private var materials: [SpoolmanMaterial] = []
    @State private var vendors: [SpoolmanVendor] = []
    @State private var searchText = ""
    @State private var isLoading = false
    @State private var isSaving = false
    @State private var errorMessage: String?
    @State private var showCreateForm = false
    @State private var filamentName = ""
    @State private var selectedMaterial = ""
    @State private var selectedVendor = ""
    @State private var colorHex = "#10b981"
    @State private var totalWeightG: Double = 1000
    @State private var spoolWeightG: Double = 200

    private var filteredFilaments: [SpoolmanFilament] {
        guard !searchText.isEmpty else { return filaments }
        let query = searchText.lowercased()
        return filaments.filter {
            ($0.name?.lowercased().contains(query) ?? false)
            || ($0.material?.lowercased().contains(query) ?? false)
            || ($0.vendor?.lowercased().contains(query) ?? false)
            || ($0.articleNumber?.lowercased().contains(query) ?? false)
        }
    }

    private var canCreateFilament: Bool {
        !selectedMaterial.isEmpty
    }

    var body: some View {
        NavigationStack {
            List {
                Section {
                    VStack(alignment: .leading, spacing: 8) {
                        Text("Unknown barcode")
                            .font(.headline)
                        Text(barcode)
                            .font(.body.monospaced())
                            .foregroundStyle(Color.pfTextSecondary)
                        Text("Choose an existing filament or create a new filament to remember this barcode, then import the spool.")
                            .font(.caption)
                            .foregroundStyle(Color.pfTextSecondary)
                    }
                }

                if let errorMessage {
                    Section {
                        Text(errorMessage)
                            .font(.caption)
                            .foregroundStyle(Color.pfError)
                    }
                }

                if showCreateForm {
                    createFilamentSection
                } else {
                    Section {
                        Button {
                            showCreateForm = true
                        } label: {
                            Label("Create New Filament", systemImage: "plus.circle")
                        }
                        .accessibilityLabel("Create new filament for barcode")
                    }

                    Section("Existing Filaments") {
                        if isLoading {
                            ProgressView("Loading filaments…")
                        } else if filteredFilaments.isEmpty {
                            ContentUnavailableView("No Filaments", systemImage: "tray")
                        } else {
                            ForEach(filteredFilaments) { filament in
                                Button {
                                    selectFilament(filament)
                                } label: {
                                    filamentRow(filament)
                                }
                                .buttonStyle(.plain)
                                .accessibilityLabel("Use filament \(filament.name ?? filament.material ?? String(filament.id))")
                            }
                        }
                    }
                }
            }
            .navigationTitle("Map Barcode")
            #if os(iOS)
            .navigationBarTitleDisplayMode(.inline)
            #endif
            .searchable(text: $searchText, prompt: "Search filaments")
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Skip") { dismiss() }
                        .disabled(isSaving)
                }
            }
            .task { await loadReferenceData() }
        }
    }

    private var createFilamentSection: some View {
        Section("New Filament") {
            if materials.isEmpty {
                TextField("Material (e.g. PLA, PETG)", text: $selectedMaterial)
            } else {
                Picker("Material", selection: $selectedMaterial) {
                    Text("Select…").tag("")
                    ForEach(materials, id: \.name) { material in
                        Text(material.name).tag(material.name)
                    }
                }
            }

            if vendors.isEmpty {
                TextField("Vendor (optional)", text: $selectedVendor)
            } else {
                Picker("Vendor", selection: $selectedVendor) {
                    Text("None").tag("")
                    ForEach(vendors, id: \.name) { vendor in
                        Text(vendor.name).tag(vendor.name)
                    }
                }
            }

            TextField("Filament Name (optional)", text: $filamentName)
            TextField("Color Hex", text: $colorHex)
                #if os(iOS)
                .textInputAutocapitalization(.never)
                #endif
                .autocorrectionDisabled()

            HStack {
                Text("Total Weight")
                Spacer()
                TextField("grams", value: $totalWeightG, format: .number)
                    #if os(iOS)
                    .keyboardType(.decimalPad)
                    #endif
                    .multilineTextAlignment(.trailing)
                    .frame(width: 100)
                Text("g")
                    .foregroundStyle(Color.pfTextSecondary)
            }

            HStack {
                Text("Spool Weight")
                Spacer()
                TextField("grams", value: $spoolWeightG, format: .number)
                    #if os(iOS)
                    .keyboardType(.decimalPad)
                    #endif
                    .multilineTextAlignment(.trailing)
                    .frame(width: 100)
                Text("g")
                    .foregroundStyle(Color.pfTextSecondary)
            }

            Button {
                createAndSelectFilament()
            } label: {
                if isSaving {
                    ProgressView()
                } else {
                    Label("Create and Import", systemImage: "checkmark.circle")
                }
            }
            .disabled(!canCreateFilament || isSaving)
            .accessibilityLabel("Create filament and import spool")

            Button("Choose Existing Instead") {
                showCreateForm = false
            }
            .disabled(isSaving)
        }
    }

    private func filamentRow(_ filament: SpoolmanFilament) -> some View {
        HStack(spacing: 12) {
            Circle()
                .fill(Color(hex: filament.colorHex ?? "#808080"))
                .frame(width: 28, height: 28)
                .overlay(Circle().strokeBorder(Color.pfBorder, lineWidth: 1))

            VStack(alignment: .leading, spacing: 3) {
                Text(filament.name ?? filament.material ?? "Filament #\(filament.id)")
                    .foregroundStyle(Color.pfTextPrimary)
                HStack(spacing: 6) {
                    if let material = filament.material {
                        Text(material)
                    }
                    if let vendor = filament.vendor {
                        Text(vendor)
                    }
                }
                .font(.caption)
                .foregroundStyle(Color.pfTextSecondary)
            }

            Spacer()
            Image(systemName: "chevron.right")
                .font(.caption)
                .foregroundStyle(Color.pfTextTertiary)
        }
    }

    private func loadReferenceData() async {
        isLoading = true
        errorMessage = nil
        do {
            async let loadedFilaments = spoolService.listFilaments()
            async let loadedMaterials = spoolService.listMaterials()
            async let loadedVendors = spoolService.listVendors()
            filaments = try await loadedFilaments
            materials = try await loadedMaterials
            vendors = try await loadedVendors
        } catch {
            errorMessage = error.localizedDescription
        }
        isLoading = false
    }

    private func selectFilament(_ filament: SpoolmanFilament) {
        isSaving = true
        Task { @MainActor in
            await onSelectFilament(filament)
            isSaving = false
            dismiss()
        }
    }

    private func createAndSelectFilament() {
        isSaving = true
        errorMessage = nil
        Task { @MainActor in
            do {
                let request = SpoolmanFilamentRequest(
                    name: filamentName.isEmpty ? nil : filamentName,
                    material: selectedMaterial,
                    colorHex: colorHex.isEmpty ? nil : colorHex,
                    vendor: selectedVendor.isEmpty ? nil : selectedVendor,
                    weight: totalWeightG > 0 ? totalWeightG : nil,
                    spoolWeight: spoolWeightG > 0 ? spoolWeightG : nil,
                    articleNumber: barcode
                )
                let filament = try await spoolService.createFilament(request)
                await onSelectFilament(filament)
                isSaving = false
                dismiss()
            } catch {
                errorMessage = error.localizedDescription
                isSaving = false
            }
        }
    }
}
