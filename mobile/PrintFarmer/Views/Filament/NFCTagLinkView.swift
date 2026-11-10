import SwiftUI

struct NFCTagLinkView: View {
    @Environment(\.dismiss) private var dismiss

    let uid: String
    let scannedData: ScannedSpoolData?
    let spoolService: any SpoolServiceProtocol
    let onLinked: (SpoolmanSpool) -> Void

    @State private var spools: [SpoolmanSpool] = []
    @State private var isLoading = true
    @State private var isLinking = false
    @State private var isCreating = false
    @State private var errorMessage: String?

    var body: some View {
        NavigationStack {
            List {
                Section {
                    LabeledContent("Tag UID", value: uid)
                        .textSelection(.enabled)
                    Text("This tag is not linked in Spoolman. Select an existing spool or create one to link it.")
                        .font(.footnote)
                        .foregroundStyle(Color.pfTextSecondary)
                }

                Section("Link to existing spool") {
                    if isLoading {
                        ProgressView("Loading spools…")
                    } else if spools.isEmpty {
                        Text("No spools found.")
                            .foregroundStyle(Color.pfTextSecondary)
                    } else {
                        ForEach(spools) { spool in
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
                            .disabled(isLinking || isLoading)
                        }
                    }
                }

                if let errorMessage {
                    Section {
                        Text(errorMessage)
                            .foregroundStyle(Color.pfError)
                            .font(.footnote)
                    }
                }
            }
            .navigationTitle("Link NFC Tag")
            #if os(iOS)
            .navigationBarTitleDisplayMode(.inline)
            #endif
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Cancel") { dismiss() }
                }
                ToolbarItem(placement: .primaryAction) {
                    Button("Create spool") { isCreating = true }
                        .disabled(isLinking || isLoading)
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
            errorMessage = error.localizedDescription
        }
        isLoading = false
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
