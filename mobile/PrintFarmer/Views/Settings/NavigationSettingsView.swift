import SwiftUI

struct NavigationSettingsView: View {
    @Environment(AuthViewModel.self) private var authViewModel
    @Environment(AppRouter.self) private var router
    @Environment(ServerRegistry.self) private var serverRegistry
    @Environment(ServiceContainer.self) private var services

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
        let currentDerivation = NavigationShellDerivation.automatic(
            farmShape: services.farmShapeService.sessionShape,
            shiftPlanEnabled: services.capabilitiesService.resolved.shiftPlanEnabled,
            isFarmAdmin: authViewModel.currentUser?.roles.contains("farm_admin") == true
        )

        guard serverRegistry.navigationLayoutPreference == .automatic,
              let serverID = serverRegistry.activeServerID,
              let userID = authViewModel.currentUser?.id,
              router.hasAdaptiveShellConfiguration(
                  serverID: serverID,
                  userID: userID
              ) else {
            return currentDerivation
        }

        return router.establishedAutomaticDerivation ?? currentDerivation
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
