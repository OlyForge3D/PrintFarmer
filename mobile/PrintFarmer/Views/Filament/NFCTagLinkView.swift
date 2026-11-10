import SwiftUI

struct NFCTagLinkView: View {
    @Environment(\.dismiss) private var dismiss
    @Environment(\.colorScheme) private var colorScheme

    let uid: String
    let scannedData: ScannedSpoolData?
    let spoolService: any SpoolServiceProtocol
    let onLinked: (SpoolmanSpool) -> Void

    @State private var spools: [SpoolmanSpool] = []
    @State private var searchText = ""
    @State private var isLoading = true
    @State private var isLinking = false
    @State private var isCreating = false
    @State private var loadErrorMessage: String?
    @State private var errorMessage: String?

    var body: some View {
        NavigationStack {
            List {
                Section {
                    LabeledContent("Tag UID", value: uid)
                        .textSelection(.enabled)
                        .listRowBackground(Color.pfCard)
                    Text("This tag is not linked in Spoolman. Select an existing spool or create one to link it.")
                        .font(.footnote)
                        .foregroundStyle(Color.pfTextSecondary)
                        .listRowBackground(Color.pfCard)
                }

                Section {
                    if isLoading {
                        ProgressView("Loading spools…")
                            .listRowBackground(Color.pfCard)
                    } else if let loadErrorMessage {
                        VStack(alignment: .leading, spacing: 10) {
                            Label("Couldn't load spools", systemImage: "exclamationmark.triangle")
                                .foregroundStyle(Color.pfError)
                            Text(loadErrorMessage)
                                .font(.footnote)
                                .foregroundStyle(Color.pfTextSecondary)
                            Text("Retry loading before creating a spool so you don't accidentally create a duplicate.")
                                .font(.footnote)
                                .foregroundStyle(Color.pfTextSecondary)
                            Button("Retry") {
                                Task { await loadSpools() }
                            }
                            .disabled(isLoading)
                        }
                        .listRowBackground(Color.pfCard)
                    } else if spools.isEmpty {
                        VStack(alignment: .leading, spacing: 4) {
                            Text("No existing spools found.")
                                .foregroundStyle(Color.pfTextPrimary)
                            Text("Create a spool to link this tag.")
                                .font(.footnote)
                                .foregroundStyle(Color.pfTextSecondary)
                        }
                        .listRowBackground(Color.pfCard)
                    } else if visibleSpools.isEmpty {
                        Text("No spools match “\(searchText)”.")
                            .foregroundStyle(Color.pfTextSecondary)
                            .listRowBackground(Color.pfCard)
                    } else {
                        ForEach(visibleSpools) { spool in
                            Button {
                                Task { await link(spool) }
                            } label: {
                                VStack(alignment: .leading, spacing: 3) {
                                    Text(spool.filamentName ?? spool.name)
                                        .foregroundStyle(Color.pfTextPrimary)
                                    Text("\(spool.material) · Spool #\(spool.id)")
                                        .font(.caption)
                                        .foregroundStyle(Color.pfTextSecondary)
                                }
                                .frame(minHeight: 44, alignment: .leading)
                            }
                            .listRowBackground(Color.pfCard)
                            .disabled(isLinking || isLoading)
                        }
                    }
                } header: {
                    Text("Link to existing spool")
                        .foregroundStyle(Color.pfTextSecondary)
                }

                if let errorMessage {
                    Section {
                        Text(errorMessage)
                            .foregroundStyle(Color.pfError)
                            .font(.footnote)
                            .listRowBackground(Color.pfCard)
                    }
                }
            }
            .listStyle(.insetGrouped)
            .scrollContentBackground(.hidden)
            .background(Color.pfBackground)
            .searchable(text: $searchText, prompt: "Name, material, or ID")
            .tint(colorScheme == .dark ? Color.pfAccent : Color.pfButtonPrimary)
            .navigationTitle("Link NFC Tag")
            #if os(iOS)
            .navigationBarTitleDisplayMode(.inline)
            #endif
            .toolbarBackground(Color.pfBackground, for: .navigationBar)
            .toolbarBackground(.visible, for: .navigationBar)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Cancel") { dismiss() }
                }
                ToolbarItem(placement: .primaryAction) {
                    Button("Create spool") { isCreating = true }
                        .disabled(isLinking || isLoading || loadErrorMessage != nil)
                }
            }
            .overlay {
                if isLinking {
                    ProgressView("Linking tag…")
                        .padding()
                        .background(.ultraThinMaterial, in: RoundedRectangle(cornerRadius: 12))
                }
            }
            .sheet(isPresented: $isCreating) {
                AddSpoolView(scannedData: scannedData) { spool in
                    try await spoolService.linkTag(uid: uid, toSpoolID: spool.id)
                    onLinked(spool)
                }
            }
            .task { await loadSpools() }
        }
    }

    private func loadSpools() async {
        isLoading = true
        loadErrorMessage = nil
        defer { isLoading = false }
        do {
            var all: [SpoolmanSpool] = []
            var offset = 0
            var totalCount = 0
            repeat {
                let page = try await spoolService.listSpools(
                    limit: 500,
                    offset: offset,
                    search: nil,
                    material: nil,
                    vendor: nil
                )
                all.append(contentsOf: page.items.filter { !($0.archived ?? false) })
                totalCount = page.totalCount
                let nextOffset = offset + page.items.count
                guard nextOffset > offset, nextOffset < totalCount else { break }
                offset = nextOffset
            } while true
            spools = all
        } catch {
            loadErrorMessage = error.localizedDescription
        }
    }

    private var visibleSpools: [SpoolmanSpool] {
        let query = searchText.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !query.isEmpty else { return spools }
        return spools.filter { spool in
            "\(spool.id)".localizedCaseInsensitiveContains(query)
                || spool.name.localizedCaseInsensitiveContains(query)
                || spool.filamentName?.localizedCaseInsensitiveContains(query) == true
                || spool.material.localizedCaseInsensitiveContains(query)
        }
    }

    private func link(_ spool: SpoolmanSpool) async {
        isLinking = true
        errorMessage = nil
        defer { isLinking = false }
        do {
            try await spoolService.linkTag(uid: uid, toSpoolID: spool.id)
            onLinked(spool)
            dismiss()
        } catch {
            errorMessage = error.localizedDescription
        }
    }
}
