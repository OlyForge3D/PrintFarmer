import SwiftUI

/// The single startup surface shown from launch until the app shell is ready.
///
/// Startup used to be two visually distinct screens back to back: a bare
/// logo + small `ProgressView` while the saved session was restored, followed by
/// a *separate* readiness screen with a differently-sized spinner and its own
/// copy. Because both drew the same logo at different metrics, the handoff read
/// as "the app started twice" — two spinners rather than one continuous launch.
///
/// This view is that whole sequence. The chrome (background, logo, wordmark,
/// spinner geometry) is fixed for every phase, and only ``statusText`` /
/// ``detailText`` change underneath it, so advancing through startup animates
/// text rather than replacing the screen.
struct LaunchSplashView: View {
    /// Short headline for the current phase. `nil` during the earliest phase,
    /// where there is nothing meaningful to say yet.
    var statusText: String?
    /// Supporting explanation shown beneath ``statusText``.
    var detailText: String?
    /// When false the spinner is replaced by a failure glyph.
    var isBusy: Bool = true
    /// Spoken description of the spinner for VoiceOver.
    var busyAccessibilityLabel: String = "Starting PrintFarmer"

    var body: some View {
        GeometryReader { proxy in
            ScrollView {
                VStack(spacing: 16) {
                    Image("AppLogo")
                        .resizable()
                        .scaledToFit()
                        .frame(width: 56, height: 56)
                        .clipShape(RoundedRectangle(cornerRadius: 12))
                        .accessibilityHidden(true)

                    Text("PrintFarmer")
                        .font(.largeTitle.bold())
                        .foregroundStyle(Color("LaunchText"))

                    // Fixed-height slot so swapping the spinner for the failure
                    // glyph (or changing phase) never shifts the logo above it.
                    ZStack {
                        if isBusy {
                            ProgressView()
                                .controlSize(.large)
                                .tint(Color("LaunchText"))
                                .accessibilityLabel(busyAccessibilityLabel)
                        } else {
                            Image(systemName: "wifi.exclamationmark")
                                .font(.title)
                                .foregroundStyle(Color("LaunchText"))
                                .accessibilityHidden(true)
                        }
                    }
                    .frame(height: 44)

                    if let statusText {
                        Text(statusText)
                            .font(.headline)
                            .foregroundStyle(Color("LaunchText"))
                            .multilineTextAlignment(.center)
                    }

                    if let detailText {
                        Text(detailText)
                            .font(.subheadline)
                            .foregroundStyle(Color("LaunchText"))
                            .multilineTextAlignment(.center)
                    }
                }
                .padding(24)
                .frame(maxWidth: .infinity, minHeight: proxy.size.height)
                // Phase changes cross-fade the copy instead of re-mounting the
                // whole screen, which is what makes this read as one launch.
                .animation(.easeInOut(duration: 0.2), value: statusText)
                .animation(.easeInOut(duration: 0.2), value: isBusy)
            }
            .scrollBounceBehavior(.basedOnSize)
            .background(Color("LaunchBackground"))
        }
        .accessibilityIdentifier("launchSplash")
    }
}
