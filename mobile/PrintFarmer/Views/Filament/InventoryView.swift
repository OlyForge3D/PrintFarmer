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
    }

    @State private var segment: Segment = .spools

    var body: some View {
        VStack(spacing: 0) {
            Picker("Inventory", selection: $segment) {
                ForEach(Segment.allCases) { segment in
                    Text(segment.rawValue).tag(segment)
                }
            }
            .pickerStyle(.segmented)
            .padding(.horizontal)
            .padding(.top, 8)
            // Note: `ContentView`'s per-tab `.accessibilityIdentifier("tab.inventory")`
            // (applied to this whole view as the Inventory tab's content) lands on
            // this segmented control in the accessibility tree rather than a
            // separately-assigned identifier here, matching the existing app-wide
            // "tab.<name>" convention used to address each tab's root content.

            switch segment {
            case .spools:
                SpoolInventoryView()
            case .parts:
                PartsInventoryListNavView()
            }
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
