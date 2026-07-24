import SwiftUI

/// Shown after the unified scan station (#714) resolves a scanned code to a
/// printed-part SKU. Displays on-hand/reorder state and supports a manual
/// stock adjustment (miscount correction or QC reject) — harvest deltas are
/// recorded only through the atomic harvest flow, not here.
struct PartScanResultView: View {
    let part: PartInventoryResponse
    private let navigationTitle: String
    /// Called with the successful adjustment so a presenting parent (e.g.
    /// `PartsInventoryListView`) can refresh its on-hand/reorder state
    /// instead of showing stale data after this sheet dismisses.
    var onAdjusted: ((PartAdjustmentResponse) -> Void)?

    @Environment(ServiceContainer.self) private var services
    @Environment(\.dismiss) private var dismiss
    @State private var viewModel: PartAdjustmentViewModel
    @State private var activeTasks: [Task<Void, Never>] = []

    init(part: PartInventoryResponse, navigationTitle: String = "Part Scanned", onAdjusted: ((PartAdjustmentResponse) -> Void)? = nil) {
        self.part = part
        self.navigationTitle = navigationTitle
        self.onAdjusted = onAdjusted
        _viewModel = State(initialValue: PartAdjustmentViewModel(part: part))
    }

    var body: some View {
        NavigationStack {
            Form {
                Section {
                    LabeledContent("Name", value: part.name)
                    LabeledContent("SKU", value: part.sku)
                    HStack {
                        Text("On Hand")
                        Spacer()
                        Text("\(viewModel.latestOnHand)")
                            .font(.body.monospacedDigit())
                            .foregroundStyle(part.needsReorder ? Color.pfWarning : Color.pfTextPrimary)
                    }
                    if part.needsReorder {
                        Label("Below reorder point (\(part.reorderPoint))", systemImage: "exclamationmark.triangle.fill")
                            .font(.caption)
                            .foregroundStyle(Color.pfWarning)
                            .accessibilityIdentifier("partScan.reorderWarning")
                    }
                    if let binName = part.defaultBinName {
                        LabeledContent("Default Bin", value: "\(binName) (\(part.defaultBinCode ?? "—"))")
                    }
                } header: {
                    Text("Scanned Part")
                }

                Section {
                    Stepper(value: $viewModel.delta, in: -9999...9999) {
                        HStack {
                            Text("Adjustment")
                            Spacer()
                            Text(viewModel.delta >= 0 ? "+\(viewModel.delta)" : "\(viewModel.delta)")
                                .font(.body.monospacedDigit())
                                .foregroundStyle(viewModel.delta >= 0 ? Color.pfSuccess : Color.pfError)
                        }
                    }
                    .accessibilityIdentifier("partScan.deltaStepper")
                    .onChange(of: viewModel.delta) { _, _ in viewModel.noteIntentChanged() }
                    .disabled(viewModel.isSubmitting)

                    Picker("Reason", selection: $viewModel.reason) {
                        Text("QC Reject").tag(PartAdjustmentReason.qcReject)
                        Text("Manual Correction").tag(PartAdjustmentReason.manual)
                    }
                    .accessibilityIdentifier("partScan.reasonPicker")
                    .onChange(of: viewModel.reason) { _, _ in viewModel.noteIntentChanged() }
                    .disabled(viewModel.isSubmitting)

                    TextField("Notes (optional)", text: $viewModel.notes, axis: .vertical)
                        .onChange(of: viewModel.notes) { _, _ in viewModel.noteIntentChanged() }
                        .disabled(viewModel.isSubmitting)

                    Button {
                        // Synchronous re-entrancy guard runs BEFORE the Task
                        // is created, on the same run-loop turn as the tap —
                        // this is what actually prevents rapid duplicate
                        // mutations (an async guard inside submit() would
                        // leave a race window between two fast taps).
                        guard viewModel.beginSubmit() else { return }
                        let task = Task {
                            if let adjustment = await viewModel.submit(partsInventoryService: services.partsInventoryService, offlineQueue: services.offlineWriteQueue) {
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
                            Text("Apply Adjustment")
                                .frame(maxWidth: .infinity)
                        }
                    }
                    .buttonStyle(.borderedProminent)
                    .tint(Color.pfAccent)
                    .disabled(!viewModel.canSubmit)
                    .frame(minHeight: 44)
                    .accessibilityIdentifier("partScan.applyAdjustment")
                } header: {
                    Text("Adjust Stock")
                } footer: {
                    Text("Harvest deltas are recorded automatically from the harvest flow — use this only for QC rejects or manual corrections.")
                }

                if let successMessage = viewModel.successMessage {
                    Section {
                        Label(successMessage, systemImage: "checkmark.circle.fill")
                            .foregroundStyle(.green)
                    }
                }
            }
            .navigationTitle(navigationTitle)
            #if os(iOS)
            .navigationBarTitleDisplayMode(.inline)
            #endif
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Done") { dismiss() }
                        .disabled(viewModel.isSubmitting)
                }
            }
            .alert("Error", isPresented: .constant(viewModel.errorMessage != nil)) {
                Button("OK") { viewModel.errorMessage = nil }
            } message: {
                if let errorMessage = viewModel.errorMessage {
                    Text(errorMessage)
                }
            }
            // Interactive dismissal (swipe-down) must be blocked while a
            // submission is in flight, matching the disabled Done button —
            // otherwise the sheet could vanish mid-request and the caller
            // would lose track of an in-flight mutation.
            .interactiveDismissDisabled(viewModel.isSubmitting)
            .onDisappear { activeTasks.forEach { $0.cancel() } }
        }
    }
}

