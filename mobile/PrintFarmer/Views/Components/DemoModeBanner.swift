import SwiftUI

/// Persistent banner shown at the top of the screen when demo mode is active.
struct DemoModeBanner: View {
    @State private var showingInfo = false

    var body: some View {
        Button {
            showingInfo = true
        } label: {
            HStack(spacing: 6) {
                Image(systemName: "info.circle.fill")
                    .font(.caption)
                Text("DEMO MODE")
                    .font(.caption.weight(.bold))
                    .tracking(0.5)
            }
            .foregroundStyle(.white)
            .frame(maxWidth: .infinity)
            .padding(.vertical, 6)
            .background(Color.pfWarning)
        }
        .buttonStyle(.plain)
        .alert("Demo Mode", isPresented: $showingInfo) {
            Button("OK", role: .cancel) {}
        } message: {
            Text("You're viewing demo data. Log in with real credentials to connect to your printer farm.")
        }
    }
}
