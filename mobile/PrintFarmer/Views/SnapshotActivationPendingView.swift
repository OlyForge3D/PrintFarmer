import SwiftUI

/// D (issue #816 reject): visible readiness gate + retry UI shown when the user
/// is authenticated but the farm-snapshot activation could not complete (e.g.
/// startup preparation failed). Consumes `AuthViewModel.snapshotActivationPending`
/// so the app is NEVER silently gated only on `isAuthenticated`. Offers an
/// accessible retry that reuses the pinned pending record (same server, same
/// user, same generation, same auth token) — a server switch or auth change
/// invalidates the pending record and this view will disappear (either back to
/// ContentView on success or to LoginView on sign-out).
struct SnapshotActivationPendingView: View {
    @Environment(AuthViewModel.self) private var authViewModel
    @State private var isRetrying = false
    @State private var lastRetryFailed = false

    var body: some View {
        VStack(spacing: 20) {
            Image(systemName: "exclamationmark.arrow.triangle.2.circlepath")
                .resizable()
                .scaledToFit()
                .frame(width: 56, height: 56)
                .foregroundStyle(.orange)
                .accessibilityHidden(true)

            Text("Farm data isn’t ready")
                .font(.title2.bold())
                .multilineTextAlignment(.center)

            Text("You’re signed in, but your farm couldn’t finish loading. This usually clears up on retry.")
                .font(.body)
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.center)
                .padding(.horizontal, 24)

            if lastRetryFailed {
                Text("Retry didn’t succeed. You can try again or sign out.")
                    .font(.footnote)
                    .foregroundStyle(.red)
                    .multilineTextAlignment(.center)
                    .padding(.horizontal, 24)
                    .accessibilityLabel("Last retry did not succeed")
            }

            VStack(spacing: 12) {
                Button {
                    Task { await runRetry() }
                } label: {
                    HStack(spacing: 8) {
                        if isRetrying {
                            ProgressView()
                                .progressViewStyle(.circular)
                                .controlSize(.small)
                        }
                        Text(isRetrying ? "Retrying…" : "Retry")
                            .fontWeight(.semibold)
                    }
                    .frame(maxWidth: .infinity, minHeight: 44)
                }
                .buttonStyle(.borderedProminent)
                .disabled(isRetrying)
                .accessibilityIdentifier("snapshotActivationRetryButton")
                .accessibilityLabel(isRetrying ? "Retrying farm activation" : "Retry farm activation")
                .accessibilityHint("Attempts to reload your farm without signing you out")

                Button(role: .destructive) {
                    Task { await authViewModel.logout() }
                } label: {
                    Text("Sign out")
                        .frame(maxWidth: .infinity, minHeight: 44)
                }
                .buttonStyle(.bordered)
                .disabled(isRetrying)
                .accessibilityIdentifier("snapshotActivationSignOutButton")
                .accessibilityLabel("Sign out of PrintFarmer")
            }
            .padding(.horizontal, 24)
            .padding(.top, 8)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(Color(.systemBackground))
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier("snapshotActivationPendingView")
    }

    @MainActor
    private func runRetry() async {
        guard !isRetrying else { return }
        isRetrying = true
        let outcome = await authViewModel.retrySnapshotActivationIfPending()
        isRetrying = false
        // The pending flag will flip to `false` on success (or on a hard failure
        // that isn't `.preparationFailed`); if it's still true we mark the last
        // retry as failed so the accessible error text becomes visible.
        lastRetryFailed = (outcome == .preparationFailed)
            || (outcome == nil && authViewModel.snapshotActivationPending)
    }
}
