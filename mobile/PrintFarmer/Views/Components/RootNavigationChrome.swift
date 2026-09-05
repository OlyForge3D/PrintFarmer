import SwiftUI

enum RootNavigationChrome {
    static let minimumTouchTarget: CGFloat = 44
    static let serverSwitcherIdentifier = "navigation.serverSwitcher"
    static let modeControlIdentifier = "navigation.modeControl"
    static let accountButtonIdentifier = "navigation.account"
    static let accountContainerIdentifier = "account.root"
}

extension View {
    func rootNavigationChrome(
        for tab: AppTab
    ) -> some View {
        modifier(
            RootNavigationChromeModifier(
                tab: tab,
                screenActions: EmptyView()
            )
        )
    }

    func rootNavigationChrome<ScreenActions: View>(
        for tab: AppTab,
        @ViewBuilder screenActions: () -> ScreenActions
    ) -> some View {
        modifier(
            RootNavigationChromeModifier(
                tab: tab,
                screenActions: screenActions()
            )
        )
    }
}

private struct RootNavigationChromeModifier<ScreenActions: View>: ViewModifier {
    @Environment(AppRouter.self) private var router
    @Environment(ServiceContainer.self) private var services

    let tab: AppTab
    let screenActions: ScreenActions

    func body(content: Content) -> some View {
        content
            .safeAreaInset(edge: .top, spacing: 0) {
                if router.shouldShowModeControl(for: tab) {
                    modeControl
                }
            }

            .toolbar {
                if router.isAtRoot(tab) {
                    ToolbarItem(placement: .topBarLeading) {
                        ServerSwitcherMenu(style: .toolbar)
                    }

                    ToolbarItem(placement: .topBarTrailing) {
                        HStack(spacing: 4) {
                            screenActions
                            accountButton
                        }
                    }
                }
            }
    }

    private var modeControl: some View {
        Picker(
            "Navigation mode",
            selection: Binding(
                get: { router.activeMode },
                set: {
                    router.setNavigationMode(
                        $0,
                        capabilities: services.capabilitiesService.resolved
                    )
                }
            )
        ) {
            Text("Floor").tag(OversightMode.floor)
            Text("Oversight").tag(OversightMode.oversight)
        }
        .pickerStyle(.segmented)
        .frame(minHeight: RootNavigationChrome.minimumTouchTarget)
        .padding(.horizontal)
        .background(.bar)
        .overlay(alignment: .bottom) {
            Divider()
        }
        .accessibilityLabel("Navigation mode")
        .accessibilityHint("Switches between Floor work and Oversight.")
        .accessibilityIdentifier(RootNavigationChrome.modeControlIdentifier)
    }

    private var accountButton: some View {
        NavigationLink(value: AppDestination.account) {
            Image(systemName: "person.crop.circle")
                .imageScale(.large)
                .frame(
                    minWidth: RootNavigationChrome.minimumTouchTarget,
                    minHeight: RootNavigationChrome.minimumTouchTarget
                )
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .accessibilityLabel("Account")
        .accessibilityHint("Opens notifications, settings, servers, and offline activity.")
        .accessibilityIdentifier(RootNavigationChrome.accountButtonIdentifier)
    }
}
