import SwiftUI

/// Shown after the unified scan station (#714) resolves a scanned code to a
/// printed-parts storage bin. Supports the two "scan bin" outcomes from the
/// issue: logging printed parts directly into this bin, and a shortcut into
/// the harvest flow for an outstanding completed job.
struct BinScanResultView: View {
    let bin: BinResponse
    /// Called with the successful log-parts adjustment so a presenting
    /// parent with an owned parts-inventory list can refresh its on-hand
    /// state instead of showing stale data after this sheet dismisses.
    /// Mirrors `PartScanResultView.onAdjusted`; `ScanView`'s current call
    /// site has no owned list to refresh, so it passes none.
    var onAdjusted: ((PartAdjustmentResponse) -> Void)?

    @Environment(ServiceContainer.self) private var services
    @Environment(\.dismiss) private var dismiss
    @State private var viewModel: BinPartLoggingViewModel
    @State private var availableParts: [PartInventoryResponse] = []
    @State private var isLoadingParts = false
    @State private var showJobPicker = false
    @State private var harvestJob: PrintJob?
    @State private var activeTasks: [Task<Void, Never>] = []

    init(bin: BinResponse, onAdjusted: ((PartAdjustmentResponse) -> Void)? = nil) {
        self.bin = bin
        self.onAdjusted = onAdjusted
        _viewModel = State(initialValue: BinPartLoggingViewModel(bin: bin))
    }

    var body: some View {
        NavigationStack {
            Form {
                Section {
                    LabeledContent("Bin", value: bin.name)
                    LabeledContent("Code", value: bin.code)
                    if let location = bin.location, !location.isEmpty {
                        LabeledContent("Location", value: location)
                    }
                } header: {
                    Text("Scanned Bin")
                }

                Section {
                    if isLoadingParts {
                        ProgressView("Loading SKUs…")
                    } else if availableParts.isEmpty {
                        Text("No printed-part SKUs are configured yet. Add SKUs from the web admin, then scan again.")
                            .font(.footnote)
                            .foregroundStyle(.secondary)
                    } else {
                        Picker("SKU", selection: $viewModel.selectedSku) {
                            ForEach(availableParts) { part in
                                Text("\(part.name) (\(part.sku))").tag(part.sku)
                            }
                        }
                        .disabled(viewModel.isSubmitting)
                        .accessibilityIdentifier("binScan.skuPicker")
                        .onChange(of: viewModel.selectedSku) { _, _ in viewModel.noteIntentChanged() }

                        Stepper(value: $viewModel.quantity, in: 1...9999) {
                            HStack {
                                Text("Quantity")
                                Spacer()
                                Text("\(viewModel.quantity)")
                                    .font(.body.monospacedDigit())
                                    .foregroundStyle(.secondary)
                            }
                        }
                        .disabled(viewModel.isSubmitting)
                        .accessibilityIdentifier("binScan.quantityStepper")
                        .onChange(of: viewModel.quantity) { _, _ in viewModel.noteIntentChanged() }

                        Button {
                            // Synchronous re-entrancy guard runs BEFORE the
                            // Task is created, on the same run-loop turn as
                            // the tap — this is what actually prevents rapid
                            // duplicate mutations (an async guard inside
                            // submit() would leave a race window between two
                            // fast taps).
                            guard viewModel.beginSubmit() else { return }
                            let task = Task {
                                if let adjustment = await viewModel.submit(partsInventoryService: services.partsInventoryService) {
                                    onAdjusted?(adjustment)
                                }
                            }
                            activeTasks.append(task)
                        } label: {
                            if viewModel.isSubmitting {
                                HStack {
                                    Spacer()
                                    ProgressView()
                                    Spacer()
                                }
                            } else {
                                Text("Log Parts to This Bin")
                                    .frame(maxWidth: .infinity)
                            }
                        }
                        .buttonStyle(.borderedProminent)
                        .tint(Color.pfAccent)
                        .disabled(!viewModel.canSubmit)
                        .frame(minHeight: 44)
                        .accessibilityIdentifier("binScan.logParts")
                    }
                } header: {
                    Text("Log Printed Parts")
                } footer: {
                    Text("Records a manual stock adjustment into this bin. Use Harvest instead for a completed print job's plate.")
                }

                Section {
                    Button {
                        showJobPicker = true
                    } label: {
                        Label("Harvest a Completed Job Here", systemImage: "shippingbox.and.arrow.backward")
                            .frame(minHeight: 44)
                    }
                    .accessibilityIdentifier("binScan.harvestShortcut")
                } header: {
                    Text("Harvest Shortcut")
                }

                if let successMessage = viewModel.successMessage {
                    Section {
                        Label(successMessage, systemImage: "checkmark.circle.fill")
                            .foregroundStyle(.green)
                    }
                }
            }
            .navigationTitle("Bin Scanned")
            #if os(iOS)
            .navigationBarTitleDisplayMode(.inline)
            #endif
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Done") { dismiss() }
                        .disabled(viewModel.isSubmitting)
                }
            }
            // Blocks swipe-to-dismiss / tap-outside while a log-parts
            // submission is in flight, matching the disabled Done button —
            // an in-flight adjustment must not be abandoned mid-request.
            .interactiveDismissDisabled(viewModel.isSubmitting)
            .alert("Error", isPresented: .constant(viewModel.errorMessage != nil)) {
                Button("OK") { viewModel.errorMessage = nil }
            } message: {
                if let errorMessage = viewModel.errorMessage {
                    Text(errorMessage)
                }
            }
            .sheet(isPresented: $showJobPicker) {
                HarvestEligibleJobPickerView { job in
                    harvestJob = job
                    showJobPicker = false
                }
            }
            .sheet(item: $harvestJob) { job in
                HarvestSheetView(job: job, presetBinCode: bin.code)
            }
            .task { await loadParts() }
            .onDisappear { activeTasks.forEach { $0.cancel() } }
        }
    }

    private func loadParts() async {
        isLoadingParts = true
        do {
            availableParts = try await services.partsInventoryService.listParts()
            if viewModel.selectedSku.isEmpty {
                viewModel.selectedSku = availableParts.first?.sku ?? ""
            }
        } catch {
            viewModel.errorMessage = error.localizedDescription
        }
        isLoadingParts = false
    }
}

/// Bin-scan harvest shortcut eligibility (Dispute C, #714): a candidate must
/// be `Completed` and not yet harvested. Extracted to file scope (rather
/// than a private computed property on the picker view) so it has
/// deterministic, direct unit coverage independent of any UI/demo-data
/// rendering path.
func isHarvestEligible(_ candidate: QueuedPrintJobResponse) -> Bool {
    candidate.job.status.caseInsensitiveCompare("Completed") == .orderedSame && candidate.job.harvestedAt == nil
}

/// Lightweight picker listing completed jobs, used by the bin-scan harvest
/// shortcut (#714). Fetches the compact queue list, filters to completed
/// jobs client-side, then resolves the selected job's full detail (needed
/// for `gcodeFileId`/`projectFileId` mapping lookup) before handing it back.
private struct HarvestEligibleJobPickerView: View {
    let onSelect: (PrintJob) -> Void

    @Environment(ServiceContainer.self) private var services
    @Environment(\.dismiss) private var dismiss
    @State private var candidates: [QueuedPrintJobResponse] = []
    @State private var isLoading = false
    @State private var isResolving = false
    @State private var errorMessage: String?

    /// Completed jobs not yet harvested (Dispute C, #714) — once
    /// `harvestedAt` is set the job drops out of this picker even though it
    /// remains visible elsewhere in Recent/Tasks.
    private var completedCandidates: [QueuedPrintJobResponse] {
        candidates.filter(isHarvestEligible)
    }

    var body: some View {
        NavigationStack {
            Group {
                if isLoading {
                    ProgressView("Loading completed jobs…")
                } else if completedCandidates.isEmpty {
                    ContentUnavailableView {
                        Label("No Completed Jobs", systemImage: "checkmark.circle")
                    } description: {
                        Text("There are no completed jobs available to harvest right now.")
                    }
                } else {
                    List(completedCandidates) { candidate in
                        Button {
                            selectJob(candidate)
                        } label: {
                            VStack(alignment: .leading, spacing: 2) {
                                Text(candidate.job.name)
                                    .foregroundStyle(Color.pfTextPrimary)
                                if let completedAt = candidate.job.actualEndTimeUtc {
                                    Text(completedAt.formatted(date: .abbreviated, time: .shortened))
                                        .font(.caption)
                                        .foregroundStyle(.secondary)
                                }
                            }
                            .frame(minHeight: 44)
                        }
                        .accessibilityHint("Opens the harvest sheet for \(candidate.job.name)")
                    }
                }
            }
            .navigationTitle("Select Completed Job")
            #if os(iOS)
            .navigationBarTitleDisplayMode(.inline)
            #endif
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Cancel") { dismiss() }
                }
            }
            .overlay {
                if isResolving {
                    ProgressView("Opening…")
                        .padding()
                        .background(.ultraThinMaterial, in: RoundedRectangle(cornerRadius: 12))
                }
            }
            .alert("Error", isPresented: .constant(errorMessage != nil)) {
                Button("OK") { errorMessage = nil }
            } message: {
                if let errorMessage {
                    Text(errorMessage)
                }
            }
            .task { await loadCandidates() }
        }
    }

    private func loadCandidates() async {
        isLoading = true
        do {
            candidates = try await services.jobService.listAllJobs()
        } catch {
            errorMessage = error.localizedDescription
        }
        isLoading = false
    }

    private func selectJob(_ candidate: QueuedPrintJobResponse) {
        guard let id = UUID(uuidString: candidate.job.id) else {
            errorMessage = "Invalid job identifier"
            return
        }
        isResolving = true
        Task {
            do {
                let job = try await services.jobService.get(id: id)
                isResolving = false
                onSelect(job)
            } catch {
                isResolving = false
                errorMessage = error.localizedDescription
            }
        }
    }
}
