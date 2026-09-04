import SwiftUI

enum HarvestBinScanResolution: Equatable {
    case code(String)
    case cancelled
    case unavailable
    case error(String)
}

enum HarvestBinScanner {
    static func scan(
        scanner: (any BarcodeScannerProtocol)?,
        partsInventoryService: any PartsInventoryServiceProtocol
    ) async -> HarvestBinScanResolution {
        guard let scanner, scanner.isAvailable else {
            return .unavailable
        }

        switch await scanner.scanBarcode() {
        case .barcode(let code):
            if let bin = try? await partsInventoryService.resolveBinByBarcode(code) {
                return .code(bin.code)
            }
            return .code(code)
        case .cancelled:
            return .cancelled
        case .error(let error):
            return .error(error.localizedDescription)
        }
    }
}

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
    @State private var showBinPicker = false
    @State private var shouldAutoStartBinScan: Bool

    /// Called after a successful (or already-harvested) result so the
    /// presenter can refresh job/bin/inventory state. Delivery ("exactly
    /// once, immediately upon the first successful response") is owned and
    /// driven by `HarvestViewModel.onHarvested` — wired straight through at
    /// init below — rather than a View-level `.onChange(of: result)` side
    /// effect, so the once-per-sheet contract is provable from a unit test.
    var onHarvested: (() -> Void)?

    init(
        job: PrintJob,
        presetBinCode: String? = nil,
        autoStartBinScan: Bool = false,
        onHarvested: (() -> Void)? = nil
    ) {
        let model = HarvestViewModel(job: job, presetBinCode: presetBinCode)
        model.onHarvested = onHarvested
        _viewModel = State(initialValue: model)
        _shouldAutoStartBinScan = State(initialValue: autoStartBinScan)
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
            .sheet(isPresented: $showBinPicker) {
                BinPickerView(bins: viewModel.availableBins) { bin in
                    viewModel.sharedBinCode = bin.code
                    showBinPicker = false
                }
            }
            .task {
                await viewModel.loadContext(partsInventoryService: services.partsInventoryService)
                guard shouldAutoStartBinScan else { return }
                shouldAutoStartBinScan = false
                await scanBin()
            }
            .onChange(of: viewModel.hasWrongBinConflict) { _, hasConflict in
                showOverrideSheet = hasConflict
            }
            .interactiveDismissDisabled(viewModel.isSubmitting)
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

                Button {
                    let task = Task { await scanBin() }
                    activeTasks.append(task)
                } label: {
                    Label("Scan or Select Bin", systemImage: "barcode.viewfinder")
                }
                .accessibilityIdentifier("harvest.scanBin")

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

            if viewModel.hasInvalidOutputEdits {
                invalidOutputEditsSection
            }

            if viewModel.hasManualOutputEdits {
                Section {
                    TextField("Reason for edited outputs", text: $viewModel.overrideReason, axis: .vertical)
                        .accessibilityIdentifier("harvest.outputOverrideReason")
                } header: {
                    Text("Reason Required")
                } footer: {
                    Text("You've changed the auto-resolved SKUs/quantities. Provide a reason before submitting.")
                }
            }

            Section {
                Button {
                    // Once-per-sheet contract: the synchronous re-entrancy
                    // guard must run BEFORE Task creation, on the same
                    // run-loop turn as the tap, so a rapid or delayed
                    // double-tap (or a race with the wrong-bin override
                    // button) can never schedule a second submit `Task`.
                    guard viewModel.beginSubmit() else { return }
                    let task = Task { await viewModel.submit(partsInventoryService: services.partsInventoryService, offlineQueue: services.offlineWriteQueue) }
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
            if output.wrappedValue.isManuallyAdded {
                skuPicker(output)
            } else {
                VStack(alignment: .leading, spacing: 2) {
                    Text(output.wrappedValue.name ?? output.wrappedValue.sku)
                        .font(.subheadline.weight(.medium))
                    if output.wrappedValue.name != nil {
                        Text(output.wrappedValue.sku)
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    }
                }
            }

            Spacer()

            Stepper(value: output.quantity, in: 1...10000) {
                Text("\(output.wrappedValue.quantity)")
                    .font(.body.monospacedDigit())
                    .frame(minWidth: 32)
            }
            .accessibilityLabel("\(output.wrappedValue.sku) quantity")
            .accessibilityValue("\(output.wrappedValue.quantity)")
        }
        .frame(minHeight: 44)
    }

    /// SKU picker shown only for manually-added rows (no server-resolved
    /// mapping backs them) — auto-resolved rows keep the static label so
    /// operators can't accidentally retarget a mapped SKU by mis-tapping.
    private func skuPicker(_ output: Binding<HarvestOutputDraft>) -> some View {
        Picker("SKU", selection: Binding(
            get: { output.wrappedValue.sku },
            set: { newSku in
                output.wrappedValue.sku = newSku
                output.wrappedValue.name = viewModel.availableParts.first { $0.sku == newSku }?.name
            }
        )) {
            if viewModel.availableParts.isEmpty {
                Text(output.wrappedValue.sku).tag(output.wrappedValue.sku)
            }
            ForEach(viewModel.availableParts) { part in
                Text("\(part.name) (\(part.sku))").tag(part.sku)
            }
        }
        // The row's `id` is a stable, collision-free UUID (unlike its
        // editable `sku`), so tests match this with a BEGINSWITH predicate
        // rather than a predicted literal identifier.
        .accessibilityIdentifier("harvest.output.skuPicker.\(output.wrappedValue.id.uuidString)")
    }

    /// Scans a bin barcode via the shared scanner service when available;
    /// otherwise presents `BinPickerView` so operators without a working
    /// scanner (or on a device without camera access) can still select a
    /// destination bin. Preserves the existing `presetBinCode` scan-first
    /// shortcut flow unchanged — this affordance is additive.
    private func scanBin() async {
        switch await HarvestBinScanner.scan(
            scanner: services.barcodeScannerService,
            partsInventoryService: services.partsInventoryService
        ) {
        case .code(let code):
            viewModel.sharedBinCode = code
        case .unavailable:
            showBinPicker = true
        case .cancelled:
            return
        case .error(let message):
            viewModel.errorMessage = message
        }
    }

    /// Dispute A: surfaced whenever `HarvestViewModel.hasInvalidOutputEdits`
    /// is true — Submit is disabled in this state, and this can happen even
    /// without the "Reason Required" section showing (e.g. an incomplete
    /// per-output bin assignment doesn't touch SKU/quantity, so it isn't a
    /// `hasManualOutputEdits` case), so this section must not be nested
    /// inside that one.
    private var invalidOutputEditsSection: some View {
        Section {
            Label {
                Text(invalidOutputEditsMessage)
                    .font(.footnote)
                    .foregroundStyle(.secondary)
            } icon: {
                Image(systemName: "exclamationmark.triangle.fill")
                    .foregroundStyle(Color.pfWarning)
            }
            .accessibilityIdentifier("harvest.invalidOutputEditsWarning")
        }
    }

    private var invalidOutputEditsMessage: String {
        if viewModel.outputs.isEmpty {
            return "All resolved outputs were removed. Add at least one SKU back with \"Add SKU\" before submitting."
        }
        if viewModel.usePerOutputBins {
            return "Each SKU needs a registered destination bin above before you can submit."
        }
        return "Check the SKUs and quantities above — a duplicate, blank, unknown, or out-of-range value is blocking submit."
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
                        // Same synchronous re-entrancy guard as the primary
                        // submit button — shares `isSubmitting`/`result`
                        // state, so it must be equally atomic against a
                        // rapid double-tap or a race with the primary
                        // submit button.
                        guard viewModel.beginSubmit() else { return }
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
                    .tint(Color.pfErrorFill)
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

            // Dispute C: `onHarvested` already fired via
            // `HarvestViewModel.submit()` the moment the server responded —
            // "Done" only dismisses the sheet.
            Button {
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

/// Fallback destination-bin selector for the H3 scan/select affordance when
/// no scanner is available. Lists bins loaded by `HarvestViewModel
/// .loadContext()` so operators can pick a bin without a working camera.
private struct BinPickerView: View {
    @Environment(\.dismiss) private var dismiss
    let bins: [BinResponse]
    let onSelect: (BinResponse) -> Void

    var body: some View {
        NavigationStack {
            Group {
                if bins.isEmpty {
                    ContentUnavailableView(
                        "No Bins Available",
                        systemImage: "shippingbox",
                        description: Text("No printed-part bins are registered yet.")
                    )
                } else {
                    List(bins) { bin in
                        Button {
                            onSelect(bin)
                        } label: {
                            VStack(alignment: .leading, spacing: 2) {
                                Text(bin.name)
                                    .font(.subheadline.weight(.medium))
                                Text(bin.code)
                                    .font(.caption)
                                    .foregroundStyle(.secondary)
                            }
                        }
                        .frame(minHeight: 44)
                        .accessibilityIdentifier("harvest.binPicker.row.\(bin.code)")
                    }
                }
            }
            .navigationTitle("Select Bin")
            #if os(iOS)
            .navigationBarTitleDisplayMode(.inline)
            #endif
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Cancel") { dismiss() }
                }
            }
        }
    }
}
