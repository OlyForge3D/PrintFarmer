import SwiftUI

/// Shown after the unified scan station (#714) resolves a scanned code to a
/// printed-part SKU. Displays on-hand/reorder state and supports a manual
/// stock adjustment (miscount correction or QC reject) — harvest deltas are
/// recorded only through the atomic harvest flow, not here.
struct PartScanResultView: View {
    let part: PartInventoryResponse
    private let navigationTitle: String

    @Environment(ServiceContainer.self) private var services
    @Environment(\.dismiss) private var dismiss
    @State private var delta: Int = -1
    @State private var reason: PartAdjustmentReason = .qcReject
    @State private var notes: String = ""
    @State private var isSubmitting = false
    @State private var errorMessage: String?
    @State private var successMessage: String?
    @State private var latestOnHand: Int
    @State private var activeTasks: [Task<Void, Never>] = []

    init(part: PartInventoryResponse, navigationTitle: String = "Part Scanned") {
        self.part = part
        self.navigationTitle = navigationTitle
        _latestOnHand = State(initialValue: part.onHand)
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
                        Text("\(latestOnHand)")
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
                    Stepper(value: $delta, in: -9999...9999) {
                        HStack {
                            Text("Adjustment")
                            Spacer()
                            Text(delta >= 0 ? "+\(delta)" : "\(delta)")
                                .font(.body.monospacedDigit())
                                .foregroundStyle(delta >= 0 ? Color.pfSuccess : Color.pfError)
                        }
                    }
                    .accessibilityIdentifier("partScan.deltaStepper")

                    Picker("Reason", selection: $reason) {
                        Text("QC Reject").tag(PartAdjustmentReason.qcReject)
                        Text("Manual Correction").tag(PartAdjustmentReason.manual)
                    }
                    .accessibilityIdentifier("partScan.reasonPicker")

                    TextField("Notes (optional)", text: $notes, axis: .vertical)

                    Button {
                        let task = Task { await submitAdjustment() }
                        activeTasks.append(task)
                    } label: {
                        if isSubmitting {
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
                    .disabled(isSubmitting || delta == 0)
                    .frame(minHeight: 44)
                    .accessibilityIdentifier("partScan.applyAdjustment")
                } header: {
                    Text("Adjust Stock")
                } footer: {
                    Text("Harvest deltas are recorded automatically from the harvest flow — use this only for QC rejects or manual corrections.")
                }

                if let successMessage {
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
                }
            }
            .alert("Error", isPresented: .constant(errorMessage != nil)) {
                Button("OK") { errorMessage = nil }
            } message: {
                if let errorMessage {
                    Text(errorMessage)
                }
            }
            .onDisappear { activeTasks.forEach { $0.cancel() } }
        }
    }

    private func submitAdjustment() async {
        guard delta != 0 else { return }
        isSubmitting = true
        errorMessage = nil
        successMessage = nil

        let request = AdjustPartInventoryRequest(
            delta: delta,
            reason: reason,
            jobId: nil,
            binCode: nil,
            notes: notes.isEmpty ? nil : notes,
            operationKey: UUID().uuidString
        )

        do {
            let adjustment = try await services.partsInventoryService.adjustPart(sku: part.sku, request: request)
            latestOnHand = adjustment.resultingBalance
            successMessage = "New balance: \(adjustment.resultingBalance)"
            delta = -1
            notes = ""
        } catch {
            errorMessage = error.localizedDescription
        }

        isSubmitting = false
    }
}
