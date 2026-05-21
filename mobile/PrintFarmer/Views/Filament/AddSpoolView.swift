import SwiftUI

struct AddSpoolView: View {
    @Environment(ServiceContainer.self) private var services
    @Environment(\.dismiss) private var dismiss
    @State private var viewModel = AddSpoolViewModel()
    @State private var saveTask: Task<Void, Never>?

    var scannedData: ScannedSpoolData?

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
                    materialSection
                    vendorSection
                    colorSection
                    weightSection

                    if let error = viewModel.errorMessage {
                        Section {
                            Text(error)
                                .foregroundStyle(Color.pfError)
                                .font(.caption)
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
                }
                ToolbarItem(placement: .confirmationAction) {
                    Button("Save") {
                        UIImpactFeedbackGenerator(style: .medium).impactOccurred()
                        saveTask = Task { await viewModel.saveSpool() }
                    }
                    .disabled(!viewModel.isFormValid || viewModel.isSaving)
                }
            }
            .task {
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
                }
            }
        }
    }

    // MARK: - Vendor

    private var vendorSection: some View {
        Section("Vendor") {
            if viewModel.vendors.isEmpty {
                TextField("Vendor (optional)", text: $viewModel.selectedVendor)
            } else {
                Picker("Vendor", selection: $viewModel.selectedVendor) {
                    Text("None").tag("")
                    ForEach(viewModel.vendors, id: \.name) { vendor in
                        Text(vendor.name).tag(vendor.name)
                    }
                }
            }

            TextField("Filament Name (optional)", text: $viewModel.filamentName)
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
