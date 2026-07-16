import SwiftUI

/// Harvest sheet (#714, F9): confirm quantity (prefilled from the job's
/// output mapping), scan/select a destination bin, and submit the harvest.
/// Reachable from `JobDetailView` (completed jobs) and from
/// `BinScanResultView`'s harvest shortcut (bin preset).
struct HarvestSheetView: View {
    @Environment(ServiceContainer.self) private var services
    @Environment(\.dismiss) private var dismiss
    @State private var viewModel: HarvestViewModel
    @State private var activeTasks: [Task<Void, Never>] = []
    @State private var showOverrideSheet = false

    /// Called after a successful (or already-harvested) result so the
    /// presenter can refresh job/bin/inventory state.
    var onHarvested: (() -> Void)?

    init(job: PrintJob, presetBinCode: String? = nil, onHarvested: (() -> Void)? = nil) {
        _viewModel = State(initialValue: HarvestViewModel(job: job, presetBinCode: presetBinCode))
        self.onHarvested = onHarvested
    }

    var body: some View {
        NavigationStack {
            Group {
                if let result = viewModel.result {
                    successView(result)
                } else {
                    formView
                }
            }
            .navigationTitle("Harvest Plate")
            #if os(iOS)
            .navigationBarTitleDisplayMode(.inline)
            #endif
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Cancel") { dismiss() }
                        .disabled(viewModel.isSubmitting)
                }
            }
            .alert("Harvest Failed", isPresented: .constant(viewModel.errorMessage != nil)) {
                Button("OK") { viewModel.errorMessage = nil }
            } message: {
                if let error = viewModel.errorMessage {
                    Text(error)
                }
            }
            .sheet(isPresented: $showOverrideSheet) {
                wrongBinOverrideSheet
            }
            .task {
                await viewModel.loadContext(partsInventoryService: services.partsInventoryService)
            }
            .onChange(of: viewModel.hasWrongBinConflict) { _, hasConflict in
                showOverrideSheet = hasConflict
            }
        }
    }

    // MARK: - Form

    private var formView: some View {
        Form {
            Section {
                LabeledContent("Job", value: viewModel.job.name)
                if viewModel.job.isMultiCopy {
                    LabeledContent("Copies", value: "\(viewModel.job.completedCopies)/\(viewModel.job.copies)")
                }
            } header: {
                Text("Completed Plate")
            }

            if viewModel.hasMappingRequiredConflict {
                mappingRequiredSection
            }

            Section {
                if viewModel.isLoadingContext {
                    HStack {
                        ProgressView()
                        Text("Loading expected outputs…")
                            .foregroundStyle(.secondary)
                    }
                } else if viewModel.outputs.isEmpty {
                    Text("No output mapping found for this job. Add the SKU(s) produced by this plate below, or configure a mapping on the web.")
                        .font(.footnote)
                        .foregroundStyle(.secondary)
                } else {
                    ForEach($viewModel.outputs) { $output in
                        outputRow($output)
                    }
                    .onDelete { indexSet in
                        indexSet.forEach { viewModel.outputs.remove(at: $0) }
                    }
                }

                Button {
                    viewModel.addManualOutputRow()
                } label: {
                    Label("Add SKU", systemImage: "plus.circle")
                }
                .accessibilityIdentifier("harvest.addSku")
            } header: {
                Text("Confirm Quantity")
            } footer: {
                Text("Quantities are prefilled from the plate's output mapping. Adjust if the actual harvested count differs.")
            }

            Section {
                TextField("Bin code", text: $viewModel.sharedBinCode)
                    #if os(iOS)
                    .textInputAutocapitalization(.characters)
                    #endif
                    .autocorrectionDisabled()
                    .accessibilityIdentifier("harvest.binCode")

                if viewModel.outputs.count > 1 {
                    Toggle("Different bin per SKU", isOn: $viewModel.usePerOutputBins)
                        .accessibilityIdentifier("harvest.perOutputBinsToggle")

                    if viewModel.usePerOutputBins {
                        ForEach($viewModel.outputs) { $output in
                            TextField("\(output.sku) bin", text: $output.binCodeOverride)
                                #if os(iOS)
                                .textInputAutocapitalization(.characters)
                                #endif
                                .autocorrectionDisabled()
                        }
                    }
                }
            } header: {
                Text("Destination Bin")
            } footer: {
                Text("Scan or enter the bin code where these parts were placed. Leave blank to use each SKU's default bin.")
            }

            Section {
                Button {
                    let task = Task { await viewModel.submit(partsInventoryService: services.partsInventoryService) }
                    activeTasks.append(task)
                } label: {
                    if viewModel.isSubmitting {
                        HStack {
                            Spacer()
                            ProgressView()
                            Spacer()
                        }
                    } else {
                        Text("Harvest to Inventory")
                            .frame(maxWidth: .infinity)
                    }
                }
                .buttonStyle(.borderedProminent)
                .tint(Color.pfAccent)
                .disabled(!viewModel.canSubmit || viewModel.isLoadingContext)
                .frame(minHeight: 44)
                .accessibilityIdentifier("harvest.submit")
            }
        }
        .onDisappear {
            activeTasks.forEach { $0.cancel() }
        }
    }

    private func outputRow(_ output: Binding<HarvestOutputDraft>) -> some View {
        HStack {
            VStack(alignment: .leading, spacing: 2) {
                Text(output.wrappedValue.name ?? output.wrappedValue.sku)
                    .font(.subheadline.weight(.medium))
                if output.wrappedValue.name != nil {
                    Text(output.wrappedValue.sku)
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
            }

            Spacer()

            Stepper(value: output.quantity, in: 0...9999) {
                Text("\(output.wrappedValue.quantity)")
                    .font(.body.monospacedDigit())
                    .frame(minWidth: 32)
            }
            .accessibilityLabel("\(output.wrappedValue.sku) quantity")
            .accessibilityValue("\(output.wrappedValue.quantity)")
        }
        .frame(minHeight: 44)
    }

    private var mappingRequiredSection: some View {
        Section {
            Label {
                VStack(alignment: .leading, spacing: 4) {
                    Text("No output mapping configured")
                        .font(.subheadline.weight(.semibold))
                    if let guidance = viewModel.mappingRequiredConflict?.guidance {
                        Text(guidance)
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    } else {
                        Text("Add the SKU(s) produced by this plate below, then submit again.")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    }
                }
            } icon: {
                Image(systemName: "exclamationmark.triangle.fill")
                    .foregroundStyle(Color.pfWarning)
            }
            .accessibilityIdentifier("harvest.mappingRequiredWarning")
        }
    }

    // MARK: - Wrong-bin override

    private var wrongBinOverrideSheet: some View {
        NavigationStack {
            Form {
                Section {
                    ForEach(viewModel.wrongBinConflict?.mismatches ?? [], id: \.partSku) { mismatch in
                        VStack(alignment: .leading, spacing: 4) {
                            Label(mismatch.partSku, systemImage: "exclamationmark.triangle.fill")
                                .foregroundStyle(Color.pfWarning)
                                .font(.subheadline.weight(.semibold))
                            Text("Expected bin: \(mismatch.expectedBinCode ?? "—")  •  Scanned: \(mismatch.scannedBinCode)")
                                .font(.caption)
                                .foregroundStyle(.secondary)
                        }
                        .padding(.vertical, 2)
                    }
                } header: {
                    Text("Wrong Bin")
                } footer: {
                    Text("The scanned bin does not match the expected bin for one or more SKUs. Provide a reason to override, or cancel and rescan the correct bin.")
                }

                Section {
                    TextField("Reason for override", text: $viewModel.overrideReason, axis: .vertical)
                        .accessibilityIdentifier("harvest.overrideReason")
                }

                Section {
                    Button(role: .destructive) {
                        let task = Task {
                            await viewModel.confirmWrongBinOverride(partsInventoryService: services.partsInventoryService)
                            if viewModel.result != nil {
                                showOverrideSheet = false
                            }
                        }
                        activeTasks.append(task)
                    } label: {
                        Text("Override and Harvest")
                            .frame(maxWidth: .infinity)
                    }
                    .buttonStyle(.borderedProminent)
                    .tint(Color.pfError)
                    .disabled(!viewModel.canConfirmOverride || viewModel.isSubmitting)
                    .frame(minHeight: 44)
                    .accessibilityIdentifier("harvest.confirmOverride")
                }
            }
            .navigationTitle("Wrong Bin")
            #if os(iOS)
            .navigationBarTitleDisplayMode(.inline)
            #endif
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Cancel") {
                        showOverrideSheet = false
                        viewModel.wrongBinConflict = nil
                    }
                }
            }
        }
    }

    // MARK: - Success

    private func successView(_ result: HarvestJobResponse) -> some View {
        VStack(spacing: 20) {
            Image(systemName: result.alreadyHarvested ? "checkmark.seal" : "checkmark.circle.fill")
                .font(.system(size: 56))
                .foregroundStyle(.green)
                .accessibilityHidden(true)

            Text(result.alreadyHarvested ? "Already Harvested" : "Harvested")
                .font(.title2.weight(.semibold))

            if let binCode = result.binCode {
                Text("Bin \(binCode)")
                    .font(.subheadline)
                    .foregroundStyle(.secondary)
            }

            VStack(alignment: .leading, spacing: 8) {
                ForEach(result.outputs) { output in
                    HStack {
                        Text(output.partSku)
                            .font(.subheadline.weight(.medium))
                        Spacer()
                        Text("+\(output.quantity) → \(output.actualBinCode)")
                            .font(.subheadline)
                            .foregroundStyle(.secondary)
                    }
                }
            }
            .padding()
            .background(Color.pfCard, in: RoundedRectangle(cornerRadius: 12))

            Button {
                onHarvested?()
                dismiss()
            } label: {
                Text("Done")
                    .frame(maxWidth: .infinity)
            }
            .buttonStyle(.borderedProminent)
            .tint(Color.pfAccent)
            .frame(minHeight: 44)
            .accessibilityIdentifier("harvest.done")
        }
        .padding()
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }
}
