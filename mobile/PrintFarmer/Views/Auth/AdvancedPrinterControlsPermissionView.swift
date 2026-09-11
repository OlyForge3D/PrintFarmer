import SwiftUI

@MainActor
enum AdvancedPrinterControlsPromptState {
    static let hasSeenPromptKey = "hasSeenAdvancedPrinterControlsPrompt"

    static func deferOptIn(defaults: UserDefaults = .standard) {
        defaults.set(true, forKey: hasSeenPromptKey)
    }

    static func enableOptIn(
        serverRegistry: ServerRegistry,
        defaults: UserDefaults = .standard
    ) {
        serverRegistry.setAdvancedPrinterControlsEnabled(true)
        defaults.set(true, forKey: hasSeenPromptKey)
    }
}

/// Prompts the user to opt into advanced printer controls during first-run setup.
///
/// Advanced controls directly manipulate printer hardware, so they stay off by
/// default until the user explicitly enables them.
struct AdvancedPrinterControlsPermissionView: View {
    let serverRegistry: ServerRegistry
    var onComplete: (() -> Void)? = nil
    @AppStorage(AdvancedPrinterControlsPromptState.hasSeenPromptKey)
    private var hasSeenPrompt: Bool = false

    var body: some View {
        VStack(spacing: 24) {
            Spacer()

            Image(systemName: "switch.2")
                .font(.system(size: 72))
                .foregroundStyle(Color.pfAccent)

            Text("Advanced Printer Controls")
                .font(.title)
                .fontWeight(.bold)
                .multilineTextAlignment(.center)

            Text("These controls allow direct manipulation of printer hardware, including temperature, motion, and similar machine actions.\n\nThey stay off by default for safety. You can change this anytime in Settings > Printer Safety.")
                .font(.body)
                .foregroundStyle(Color.pfTextSecondary)
                .multilineTextAlignment(.center)
                .padding(.horizontal, 32)

            Spacer()

            VStack(spacing: 12) {
                Button {
                    AdvancedPrinterControlsPromptState.deferOptIn()
                    hasSeenPrompt = true
                    onComplete?()
                } label: {
                    Text("Not Now")
                        .font(.headline)
                        .frame(maxWidth: .infinity)
                        .padding()
                }
                .buttonStyle(.bordered)
                .tint(Color.pfAccent)
                .accessibilityIdentifier("advancedPrinterControls.notNow")

                Button {
                    AdvancedPrinterControlsPromptState.enableOptIn(
                        serverRegistry: serverRegistry
                    )
                    hasSeenPrompt = true
                    onComplete?()
                } label: {
                    Text("Enable")
                        .font(.headline)
                        .frame(maxWidth: .infinity)
                        .padding()
                }
                .buttonStyle(.borderedProminent)
                .tint(Color.pfAccent)
                .accessibilityIdentifier("advancedPrinterControls.enable")
            }
            .padding(.horizontal, 32)

            Spacer()
                .frame(height: 40)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier("advancedPrinterControlsPermissionView")
    }
}
