import SwiftUI

/// Printed-parts inventory list (#714, F9): on-hand/reorder state for every
/// active SKU, with unified scan history feeding recognition. Distinct from
/// `SpoolInventoryView` (filament spools) — see `InventoryView` for the
/// tab-level wrapper combining both.
struct PartsInventoryListView: View {
    @Environment(ServiceContainer.self) private var services
    @Environment(AppRouter.self) private var router
    @State private var viewModel = PartsInventoryViewModel()
    @State private var selectedPart: PartInventoryResponse?
    @State private var showScanFlow = false
    @State private var showPartLookup = false
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
                                selectedPart = part
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
        .rootNavigationChrome(for: .inventory) {
            Menu {
                Button {
                    showScanFlow = true
                } label: {
                    Label("Scan code", systemImage: "barcode.viewfinder")
                }

                Button {
                    showPartLookup = true
                } label: {
                    Label("Look up printed part", systemImage: "cube.box")
                }
                .accessibilityIdentifier("inventory.partLookup")
            } label: {
                Image(systemName: "barcode.viewfinder")
                    .frame(
                        minWidth: RootNavigationChrome.minimumTouchTarget,
                        minHeight: RootNavigationChrome.minimumTouchTarget
                    )
            }
            .accessibilityLabel("Scan inventory")
            .accessibilityHint("Opens camera scanning or printed-part lookup.")
            .accessibilityIdentifier("inventory.scan")
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
        .sheet(item: $selectedPart) { part in
            PartScanResultView(part: part, navigationTitle: part.name) { _ in
                let task = Task { await viewModel.loadParts() }
                activeTasks.append(task)
            }
        }
        .sheet(isPresented: $showScanFlow, onDismiss: {
            router.completeScanFlowDismissal(capabilities: services.capabilitiesService.resolved)
        }, content: {
            ScanFlowView()
        })
        .sheet(isPresented: $showPartLookup) {
            PartLookupView(partsInventoryService: services.partsInventoryService) { part in
                selectedPart = part
                showPartLookup = false
            }
        }
        .task {
            viewModel.isViewActive = true
            viewModel.configure(partsInventoryService: services.partsInventoryService)
            await viewModel.loadParts()
        }
        .onDisappear {
            activeTasks.forEach { $0.cancel() }
            activeTasks.removeAll()
            viewModel.isViewActive = false
        }
    }

    /// Direct printed-part lookup re-homed from the retired Scan tab.
    struct PartLookupView: View {
        let partsInventoryService: any PartsInventoryServiceProtocol
        let onSelect: (PartInventoryResponse) -> Void

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
                    isLoading = true
                    do {
                        parts = try await partsInventoryService.listParts()
                    } catch {
                        errorMessage = error.localizedDescription
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
