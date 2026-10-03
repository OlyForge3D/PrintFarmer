import SwiftUI

enum RootNavigationChrome {
    static let minimumTouchTarget: CGFloat = 44
    static let serverSwitcherIdentifier = "navigation.serverSwitcher"
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

    let tab: AppTab
    let screenActions: ScreenActions

    func body(content: Content) -> some View {
        content
            .toolbar {
                if router.isAtRoot(tab) {
                    ToolbarItem(placement: .topBarTrailing) {
                        HStack(spacing: 4) {
                            screenActions
                            accountButton
                        }
                    }
                }
            }
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
