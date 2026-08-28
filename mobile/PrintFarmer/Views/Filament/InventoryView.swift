import SwiftUI

/// Inventory tab wrapper (#714, F9): a segmented switch between filament
/// spool inventory (existing) and printed-parts inventory (new). Each
/// segment is a fully independent, self-contained view with its own
/// `NavigationStack` — matching `SpoolInventoryView`'s existing pattern
/// rather than introducing a second, nested navigation container.
struct InventoryView: View {
    enum Segment: String, CaseIterable, Identifiable {
        case spools = "Spools"
        case parts = "Printed Parts"

        var id: String { rawValue }

        static func available(printedPartsInventoryEnabled: Bool) -> [Segment] {
            printedPartsInventoryEnabled ? allCases : [.spools]
        }

        static func resolved(
            _ segment: Segment,
            printedPartsInventoryEnabled: Bool
        ) -> Segment {
            available(printedPartsInventoryEnabled: printedPartsInventoryEnabled)
                .contains(segment) ? segment : .spools
        }
    }

    @Environment(ServiceContainer.self) private var services
    @State private var segment: Segment = .spools

    var body: some View {
        let printedPartsInventoryEnabled =
            services.capabilitiesService.resolved.printedPartsInventoryEnabled

        Group {
            if printedPartsInventoryEnabled {
                VStack(spacing: 0) {
                    Picker("Inventory", selection: $segment) {
                        ForEach(
                            Segment.available(
                                printedPartsInventoryEnabled: printedPartsInventoryEnabled
                            )
                        ) { segment in
                            Text(segment.rawValue).tag(segment)
                        }
                    }
                    .pickerStyle(.segmented)
                    .padding(.horizontal)
                    .padding(.top, 8)
                    .accessibilityIdentifier("inventory.segmentPicker")

                    inventoryContent(
                        for: Segment.resolved(
                            segment,
                            printedPartsInventoryEnabled: printedPartsInventoryEnabled
                        )
                    )
                }
            } else {
                SpoolInventoryView()
            }
        }
        .onChange(of: printedPartsInventoryEnabled) { _, isEnabled in
            segment = Segment.resolved(
                segment,
                printedPartsInventoryEnabled: isEnabled
            )
        }
    }

    @ViewBuilder
    private func inventoryContent(for segment: Segment) -> some View {
        switch segment {
        case .spools:
            SpoolInventoryView()
        case .parts:
            PartsInventoryListNavView()
        }
    }
}

/// Thin `NavigationStack` host for `PartsInventoryListView` so the Inventory
/// tab's parts segment behaves like the spools segment (its own root nav
/// container, own title/toolbar).
private struct PartsInventoryListNavView: View {
    var body: some View {
        NavigationStack {
            PartsInventoryListView()
        }
    }
}
