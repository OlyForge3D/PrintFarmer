import SwiftUI

struct NavigationSettingsView: View {
    @Environment(AppRouter.self) private var router
    @Environment(ServerRegistry.self) private var serverRegistry

    var body: some View {
        List {
            Section {
                ForEach(NavigationLayoutPreference.allCases) { preference in
                    preferenceButton(preference)
                }
            } header: {
                Text("Layout")
            } footer: {
                Text(automaticDerivation.explanation)
            }
        }
        .navigationTitle("Navigation")
        .navigationBarTitleDisplayMode(.inline)
        .accessibilityIdentifier("navigation.settings")
    }

    private var automaticDerivation: NavigationShellDerivation {
        guard let serverID = serverRegistry.activeServerID,
              router.configuredServerID == serverID,
              let established = router.establishedAutomaticDerivation else {
            return NavigationShellDerivation(
                shell: .simple,
                explanation: "Verifying this server's account and farm size before choosing an automatic layout."
            )
        }

        return established
    }

    private func preferenceButton(
        _ preference: NavigationLayoutPreference
    ) -> some View {
        Button {
            serverRegistry.setNavigationLayoutPreference(preference)
        } label: {
            HStack(alignment: .firstTextBaseline, spacing: 12) {
                VStack(alignment: .leading, spacing: 4) {
                    Text(preference.title)
                        .font(.body)
                        .foregroundStyle(.primary)
                    Text(preference.subtitle)
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
                Spacer(minLength: 12)
                if serverRegistry.navigationLayoutPreference == preference {
                    Image(systemName: "checkmark")
                        .font(.body.weight(.semibold))
                        .foregroundStyle(Color.accentColor)
                        .accessibilityHidden(true)
                }
            }
            .frame(minHeight: 44)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .disabled(serverRegistry.activeServerID == nil)
        .accessibilityLabel(preference.title)
        .accessibilityHint(
            serverRegistry.navigationLayoutPreference == preference
                ? "\(preference.subtitle). Selected."
                : "Uses \(preference.subtitle.lowercased())."
        )
        .accessibilityAddTraits(
            serverRegistry.navigationLayoutPreference == preference
                ? [.isButton, .isSelected]
                : .isButton
        )
        .accessibilityIdentifier(accessibilityIdentifier(for: preference))
    }

    private func accessibilityIdentifier(
        for preference: NavigationLayoutPreference
    ) -> String {
        switch preference {
        case .automatic:
            "navigation.layout.automatic"
        case .simple:
            "navigation.layout.simple"
        case .twoModes:
            "navigation.layout.twoModes"
        }
    }
}
