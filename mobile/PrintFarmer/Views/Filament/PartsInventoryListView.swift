import SwiftUI

private enum PartsInventorySheet: Identifiable {
    case scan
    case partLookup(serverGeneration: Int)
    case part(PartInventoryResponse)

    var id: String {
        switch self {
        case .scan:
            "scan"
        case .partLookup(let serverGeneration):
            "lookup-\(serverGeneration)"
        case .part(let part):
            "part-\(part.id.uuidString)"
        }
    }
}

private struct PendingPartSelection {
    let part: PartInventoryResponse
    let serverGeneration: Int
}

/// Printed-parts inventory list (#714, F9): on-hand/reorder state for every
/// active SKU, with unified scan history feeding recognition. Distinct from
/// `SpoolInventoryView` (filament spools) — see `InventoryView` for the
/// tab-level wrapper combining both.
struct PartsInventoryListView: View {
    @Environment(ServiceContainer.self) private var services
    @State private var viewModel = PartsInventoryViewModel()
    @State private var presentedSheet: PartsInventorySheet?
    @State private var pendingPartSelection: PendingPartSelection?
    @State private var activeTasks: [Task<Void, Never>] = []

    var body: some View {
        Group {
            if viewModel.featureDisabled {
                ContentUnavailableView {
                    Label("Printed Parts Inventory Disabled", systemImage: "shippingbox")
                } description: {
                    Text("Printed-parts inventory is turned off for this server.")
                }
            } else if viewModel.isLoading && viewModel.parts.isEmpty {
                ProgressView("Loading parts…")
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
            } else if viewModel.parts.isEmpty {
                ContentUnavailableView {
                    Label("No Printed Parts", systemImage: "cube.box")
                } description: {
                    Text("Harvest a completed job or register a SKU to see it here.")
                }
            } else if viewModel.filteredParts.isEmpty {
                ContentUnavailableView.search(text: viewModel.searchText)
            } else {
                List {
                    Section {
                        Toggle("Needs Reorder Only", isOn: $viewModel.showOnlyNeedingReorder)
                            .accessibilityIdentifier("partsInventory.reorderToggle")
                    }

                    Section {
                        ForEach(viewModel.filteredParts) { part in
                            Button {
                                presentedSheet = .part(part)
                            } label: {
                                partRow(part)
                            }
                            .buttonStyle(.plain)
                            .accessibilityIdentifier("partsInventory.row.\(part.sku)")
                        }
                    }
                }
                .listStyle(.insetGrouped)
            }
        }
        .searchable(text: $viewModel.searchText, prompt: "Search parts")
        .navigationTitle("Printed Parts")
        #if os(iOS)
        .navigationBarTitleDisplayMode(.large)
        #endif
        .toolbar {
            #if os(iOS)
            ToolbarItem(placement: .topBarTrailing) {
                Menu {
                    Button {
                        presentedSheet = .scan
                    } label: {
                        Label("Scan code", systemImage: "barcode.viewfinder")
                    }

                    Button {
                        presentedSheet = .partLookup(
                            serverGeneration: services.activeServerGeneration
                        )
                    } label: {
                        Label("Look up printed part", systemImage: "cube.box")
                    }
                    .accessibilityIdentifier("inventory.partLookup")
                } label: {
                    Image(systemName: "barcode.viewfinder")
                }
                .accessibilityLabel("Scan inventory")
                .accessibilityHint("Opens camera scanning or printed-part lookup.")
                .accessibilityIdentifier("inventory.scan")
            }
            #endif
        }
        .refreshable {
            await viewModel.loadParts()
        }
        .alert("Error", isPresented: .constant(viewModel.errorMessage != nil)) {
            Button("OK") { viewModel.errorMessage = nil }
        } message: {
            if let error = viewModel.errorMessage {
                Text(error)
            }
        }
        .sheet(item: $presentedSheet, onDismiss: presentPendingPartSelection) { sheet in
            switch sheet {
            case .scan:
                ScanFlowView()
            case .partLookup(let serverGeneration):
                PartLookupView(
                    partsInventoryService: services.partsInventoryService,
                    expectedServerGeneration: serverGeneration
                ) { part in
                    pendingPartSelection = PendingPartSelection(
                        part: part,
                        serverGeneration: serverGeneration
                    )
                    presentedSheet = nil
                }
            case .part(let part):
                PartScanResultView(part: part, navigationTitle: part.name) { _ in
                    let task = Task { await viewModel.loadParts() }
                    activeTasks.append(task)
                }
            }
        }
        .task {
            viewModel.isViewActive = true
            viewModel.configure(partsInventoryService: services.partsInventoryService)
            await viewModel.loadParts()
        }
        .onChange(of: services.activeServerGeneration) { _, _ in
            pendingPartSelection = nil
            presentedSheet = nil
            activeTasks.forEach { $0.cancel() }
            activeTasks.removeAll()
        }
        .onDisappear {
            activeTasks.forEach { $0.cancel() }
            activeTasks.removeAll()
            viewModel.isViewActive = false
        }
    }

    private func presentPendingPartSelection() {
        guard let pendingPartSelection else { return }
        self.pendingPartSelection = nil
        guard pendingPartSelection.serverGeneration == services.activeServerGeneration else {
            return
        }
        presentedSheet = .part(pendingPartSelection.part)
    }

    /// Direct printed-part lookup re-homed from the retired Scan tab.
    struct PartLookupView: View {
        let partsInventoryService: any PartsInventoryServiceProtocol
        let expectedServerGeneration: Int
        let onSelect: (PartInventoryResponse) -> Void

        @Environment(ServiceContainer.self) private var services
        @Environment(\.dismiss) private var dismiss
        @State private var parts: [PartInventoryResponse] = []
        @State private var isLoading = false
        @State private var errorMessage: String?
        @State private var searchText = ""

        private var filteredParts: [PartInventoryResponse] {
            guard !searchText.isEmpty else { return parts }
            return parts.filter {
                $0.name.localizedCaseInsensitiveContains(searchText)
                    || $0.sku.localizedCaseInsensitiveContains(searchText)
            }
        }

        var body: some View {
            NavigationStack {
                Group {
                    if isLoading {
                        ProgressView("Loading parts…")
                    } else if let errorMessage {
                        ContentUnavailableView {
                            Label("Unable to Load Parts", systemImage: "exclamationmark.triangle")
                        } description: {
                            Text(errorMessage)
                        }
                    } else if parts.isEmpty {
                        ContentUnavailableView {
                            Label("No Printed Parts", systemImage: "cube.box")
                        } description: {
                            Text("No printed-part SKUs are configured yet.")
                        }
                    } else {
                        List(filteredParts) { part in
                            Button {
                                guard expectedServerGeneration == services.activeServerGeneration else {
                                    dismiss()
                                    return
                                }
                                onSelect(part)
                            } label: {
                                VStack(alignment: .leading, spacing: 2) {
                                    Text(part.name)
                                        .font(.subheadline.weight(.medium))
                                    Text("SKU \(part.sku) • On hand \(part.onHand)")
                                        .font(.caption)
                                        .foregroundStyle(.secondary)
                                }
                            }
                            .frame(minHeight: 44)
                            .accessibilityIdentifier("inventory.partLookup.row.\(part.sku)")
                        }
                        .searchable(text: $searchText, prompt: "Search SKU or name")
                    }
                }
                .navigationTitle("Printed Parts")
                #if os(iOS)
                .navigationBarTitleDisplayMode(.inline)
                #endif
                .toolbar {
                    ToolbarItem(placement: .cancellationAction) {
                        Button("Cancel") { dismiss() }
                    }
                }
                .task {
                    guard expectedServerGeneration == services.activeServerGeneration else {
                        dismiss()
                        return
                    }
                    isLoading = true
                    do {
                        let loadedParts = try await partsInventoryService.listParts()
                        guard !Task.isCancelled,
                              expectedServerGeneration == services.activeServerGeneration else {
                            return
                        }
                        parts = loadedParts
                    } catch {
                        guard !Task.isCancelled,
                              expectedServerGeneration == services.activeServerGeneration else {
                            return
                        }
                        errorMessage = error.localizedDescription
                    }
                    guard expectedServerGeneration == services.activeServerGeneration else {
                        return
                    }
                    isLoading = false
                }
            }
        }
    }

    private func partRow(_ part: PartInventoryResponse) -> some View {
        HStack(alignment: .top, spacing: 12) {
            VStack(alignment: .leading, spacing: 4) {
                Text(part.name)
                    .font(.subheadline.weight(.semibold))
                    .foregroundStyle(Color.pfTextPrimary)
                Text(part.sku)
                    .font(.caption)
                    .foregroundStyle(.secondary)
                if let binName = part.defaultBinName {
                    Label(binName, systemImage: "shippingbox")
                        .font(.caption2)
                        .foregroundStyle(.tertiary)
                }
            }

            Spacer()

            VStack(alignment: .trailing, spacing: 4) {
                Text("\(part.onHand)")
                    .font(.title3.monospacedDigit().weight(.semibold))
                    .foregroundStyle(part.needsReorder ? Color.pfWarning : Color.pfTextPrimary)
                if part.needsReorder {
                    Label("Reorder", systemImage: "exclamationmark.triangle.fill")
                        .font(.caption2)
                        .foregroundStyle(Color.pfWarning)
                        .labelStyle(.titleAndIcon)
                }
            }
        }
        .padding(.vertical, 6)
        .frame(minHeight: 44)
        .contentShape(Rectangle())
        .accessibilityElement(children: .combine)
        .accessibilityLabel("\(part.name), SKU \(part.sku), \(part.onHand) on hand\(part.needsReorder ? ", needs reorder" : "")")
    }
}
