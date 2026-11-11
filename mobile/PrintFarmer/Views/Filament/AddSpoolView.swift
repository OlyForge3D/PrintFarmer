import SwiftUI

struct AddSpoolView: View {
    @Environment(ServiceContainer.self) private var services
    @Environment(\.dismiss) private var dismiss
    @State private var viewModel = AddSpoolViewModel()
    @State private var saveTask: Task<Void, Never>?

    var scannedData: ScannedSpoolData?
    var onSpoolCreated: ((SpoolmanSpool) async throws -> Void)?

    var body: some View {
        NavigationStack {
            Form {
                if viewModel.isPrefilledFromScan {
                    Section {
                        Label("Pre-filled from NFC tag scan", systemImage: "wave.3.right")
                            .font(.subheadline)
                            .foregroundStyle(Color.pfAccent)
                    }
                }

                if viewModel.isLoading {
                    Section {
                        HStack {
                            Spacer()
                            ProgressView("Loading options…")
                            Spacer()
                        }
                    }
                } else {
                    Group {
                        filamentSection
                        if viewModel.isCreatingFilament && !viewModel.isFilamentSelectionLocked {
                            newFilamentSection
                            materialSection
                            vendorSection
                            colorSection
                            filamentPropertiesSection
                        }
                        weightSection
                    }
                    .disabled(viewModel.isSaving || viewModel.createdSpool != nil || viewModel.uncertainCreation != nil)

                    if let error = viewModel.errorMessage {
                        Section {
                            Text(error)
                                .foregroundStyle(Color.pfError)
                                .font(.caption)
                        }
                        if !viewModel.hasLoadedReferenceData {
                            Button("Retry Loading Options") {
                                Task { await viewModel.loadReferenceData() }
                            }
                        }
                    }
                }
            }
            .navigationTitle("Add Spool")
            #if os(iOS)
            .navigationBarTitleDisplayMode(.inline)
            #endif
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Cancel") { dismiss() }
                        .disabled(viewModel.isSaving)
                }
                ToolbarItem(placement: .confirmationAction) {
                    Button(viewModel.createdSpool == nil ? "Save" : "Retry Link") {
                        UIImpactFeedbackGenerator(style: .medium).impactOccurred()
                        saveTask = Task { await viewModel.saveSpool(afterCreate: onSpoolCreated) }
                    }
                    .disabled(!viewModel.isFormValid || viewModel.isSaving || viewModel.didSave)
                }
            }
            .task {
                viewModel.isViewActive = true
                viewModel.configure(spoolService: services.spoolService)
                if let scannedData {
                    viewModel.prefill(from: scannedData)
                }
                await viewModel.loadReferenceData()
            }
            .onChange(of: viewModel.didSave) { _, saved in
                if saved { dismiss() }
            }
            .onDisappear {
                viewModel.isViewActive = false
                saveTask?.cancel()
            }
            .interactiveDismissDisabled(viewModel.isSaving)
        }
    }

    private var filamentSection: some View {
        Section {
            if viewModel.isFilamentSelectionLocked {
                if let filament = viewModel.createdFilament {
                    Text("Created filament #\(filament.id): \(filament.name ?? filament.material ?? "Filament")")
                } else {
                    Text("Using scanned filament #\(viewModel.selectedFilamentId ?? 0)")
                }
            } else {
                Toggle("Create New Filament", isOn: $viewModel.isCreatingFilament)
                    .accessibilityIdentifier("addSpool.createFilament")
                if !viewModel.isCreatingFilament {
                    Picker("Existing Filament", selection: $viewModel.selectedFilamentId) {
                        Text("Select a filament").tag(Optional<Int>.none)
                        ForEach(viewModel.filaments.filter { $0.id > 0 }) { filament in
                            Text([
                                "#\(filament.id)", filament.name, filament.material,
                                filament.vendor, filament.colorHex
                            ].compactMap { $0 }.joined(separator: " · "))
                                .tag(Optional(filament.id))
                        }
                    }
                    .accessibilityIdentifier("addSpool.filament")
                    if let message = viewModel.filamentInventoryMessage {
                        Text(message)
                            .font(.caption)
                            .foregroundStyle(Color.pfTextSecondary)
                        Button("Reload Filament Options") {
                            Task { await viewModel.loadReferenceData() }
                        }
                    }
                }
            }
        } header: {
            Text("Filament")
        } footer: {
            Text("Choose the exact filament product, not an existing physical spool. Saving adds a new spool.")
        }
    }

    private var newFilamentSection: some View {
        Section("New Filament") {
            TextField("Filament Name", text: $viewModel.filamentName)
        }
    }

    // MARK: - Material

    private var materialSection: some View {
        Section("Material") {
            if viewModel.materials.isEmpty {
                TextField("Material (e.g. PLA, PETG, ASA)", text: $viewModel.selectedMaterial)
            } else {
                Picker("Material", selection: $viewModel.selectedMaterial) {
                    Text("Select…").tag("")
                    ForEach(viewModel.materials, id: \.name) { mat in
                        Text(mat.name).tag(mat.name)
                    }
                    if !viewModel.selectedMaterial.isEmpty,
                       !viewModel.materials.contains(where: { $0.name == viewModel.selectedMaterial }) {
                        Text(viewModel.selectedMaterial).tag(viewModel.selectedMaterial)
                    }
                }
            }
        }
    }

    // MARK: - Vendor

    private var vendorSection: some View {
        Section("Vendor") {
            Picker("Vendor", selection: $viewModel.selectedVendor) {
                Text("None").tag("")
                ForEach(viewModel.vendors, id: \.name) { vendor in
                    Text(vendor.name).tag(vendor.name)
                }
                if !viewModel.selectedVendor.isEmpty,
                   !viewModel.vendors.contains(where: { $0.name == viewModel.selectedVendor }) {
                    Text("\(viewModel.selectedVendor) (unavailable)").tag(viewModel.selectedVendor)
                }
            }
            if !viewModel.selectedVendor.isEmpty,
               !viewModel.vendors.contains(where: { $0.name == viewModel.selectedVendor }) {
                Text("Choose a listed vendor or None. The scanned vendor is not available on this server.")
                    .font(.caption)
                    .foregroundStyle(Color.pfError)
            }
        }
    }

    private var filamentPropertiesSection: some View {
        Section("New Filament Properties") {
            HStack {
                Text("Density (g/cm³)")
                TextField("Density", value: $viewModel.densityGPerCm3, format: .number)
                    .keyboardType(.decimalPad)
                    .multilineTextAlignment(.trailing)
                    .accessibilityLabel("Filament density in grams per cubic centimeter")
            }
            HStack {
                Text("Diameter (mm)")
                TextField("Diameter", value: $viewModel.diameterMm, format: .number)
                    .keyboardType(.decimalPad)
                    .multilineTextAlignment(.trailing)
                    .accessibilityLabel("Filament diameter in millimeters")
            }
            Text("Confirm the name, material, color, density and diameter before saving. A new filament will be created first.")
                .font(.caption)
                .foregroundStyle(Color.pfTextSecondary)
        }
    }

    // MARK: - Color

    private var colorSection: some View {
        Section("Color") {
            ScrollView(.horizontal, showsIndicators: false) {
                HStack(spacing: 10) {
                    ForEach(AddSpoolViewModel.colorSwatches, id: \.hex) { swatch in
                        Button {
                            viewModel.colorHex = swatch.hex
                        } label: {
                            Circle()
                                .fill(Color(hex: swatch.hex))
                                .frame(width: 36, height: 36)
                                .overlay(
                                    Circle()
                                        .strokeBorder(
                                            viewModel.colorHex.lowercased() == swatch.hex.lowercased()
                                                ? Color.pfAccent : Color.pfBorder,
                                            lineWidth: viewModel.colorHex.lowercased() == swatch.hex.lowercased() ? 3 : 1
                                        )
                                )
                        }
                        .accessibilityLabel(swatch.name)
                        .accessibilityAddTraits(
                            viewModel.colorHex.lowercased() == swatch.hex.lowercased() ? [.isSelected] : []
                        )
                    }
                }
                .padding(.vertical, 4)
            }

            HStack {
                Text("Hex")
                    .foregroundStyle(Color.pfTextSecondary)
                TextField("#RRGGBB", text: $viewModel.colorHex)
                    #if os(iOS)
                    .textInputAutocapitalization(.never)
                    #endif
                    .autocorrectionDisabled()

                Circle()
                    .fill(Color(hex: viewModel.colorHex))
                    .frame(width: 24, height: 24)
                    .overlay(
                        Circle()
                            .strokeBorder(Color.pfBorder, lineWidth: 1)
                    )
            }
        }
    }

    // MARK: - Weight

    private var weightSection: some View {
        Section("Weight") {
            HStack {
                Text("Total Weight")
                Spacer()
                TextField("grams", value: $viewModel.totalWeightG, format: .number)
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
                TextField("grams", value: $viewModel.spoolWeightG, format: .number)
                    #if os(iOS)
                    .keyboardType(.decimalPad)
                    #endif
                    .multilineTextAlignment(.trailing)
                    .frame(width: 100)
                Text("g")
                    .foregroundStyle(Color.pfTextSecondary)
            }
        }
    }
}
