import SwiftUI

/// Prompts the user to grant Local Network access before reaching the login screen.
///
/// On first launch the sign-in request can race with the iOS Local Network
/// permission dialog, causing a "No internet connection" error. This view
/// triggers the permission proactively so it's resolved before login.
struct LocalNetworkPermissionView: View {
    @Binding var hasCompletedNetworkPermission: Bool
    @State private var isRequesting = false
    @State private var didRequest = false
    @State private var permissionTask: Task<Void, Never>?

    private let networkAuth = LocalNetworkAuthorization()

    var body: some View {
        GeometryReader { geometry in
            ScrollView {
                content
                    .frame(maxWidth: .infinity, minHeight: geometry.size.height)
            }
        }
        .animation(.easeInOut(duration: 0.3), value: didRequest)
        .onDisappear { permissionTask?.cancel() }
    }

    private var content: some View {
        VStack(spacing: 24) {
            Spacer()

            Image(systemName: "network")
                .font(.system(size: 72))
                .foregroundStyle(Color.pfAccent)
                .accessibilityHidden(true)

            Text("Local Network Access")
                .font(.title)
                .fontWeight(.bold)
                .multilineTextAlignment(.center)

            VStack(spacing: 16) {
                Text("PrintFarmer needs to connect to your local network to reach your print farm server.")
                Text("When prompted, please tap **Allow** to continue.")
            }
            .font(.body)
            .foregroundStyle(Color.pfTextSecondary)
            .multilineTextAlignment(.center)
            .fixedSize(horizontal: false, vertical: true)
            .padding(.horizontal, 32)

            Spacer()

            if didRequest {
                Button {
                    hasCompletedNetworkPermission = true
                } label: {
                    Text("Continue")
                        .font(.headline)
                        .fixedSize(horizontal: false, vertical: true)
                        .frame(maxWidth: .infinity)
                        .padding()
                }
                .buttonStyle(.borderedProminent)
                .tint(Color.pfButtonPrimary)
                .padding(.horizontal, 32)
                .transition(.opacity.combined(with: .move(edge: .bottom)))
            } else {
                Button {
                    requestPermission()
                } label: {
                    Group {
                        if isRequesting {
                            ProgressView()
                                .tint(.white)
                        } else {
                            Text("Enable Network Access")
                                .font(.headline)
                                .fixedSize(horizontal: false, vertical: true)
                        }
                    }
                    .frame(maxWidth: .infinity)
                    .padding()
                }
                .buttonStyle(.borderedProminent)
                .tint(Color.pfButtonPrimary)
                .padding(.horizontal, 32)
                .disabled(isRequesting)
            }

            Spacer()
                .frame(height: 40)
        }
    }

    private func requestPermission() {
        isRequesting = true
        permissionTask = Task {
            _ = await networkAuth.requestAuthorization()
            isRequesting = false
            didRequest = true
        }
    }
}
