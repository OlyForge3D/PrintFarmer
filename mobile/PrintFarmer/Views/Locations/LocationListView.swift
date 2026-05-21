import SwiftUI

struct LocationListView: View {
    var body: some View {
        NavigationStack {
            List {
                Text("Location management will be implemented here.")
                    .foregroundStyle(.secondary)
            }
            .navigationTitle("Locations")
        }
    }
}
