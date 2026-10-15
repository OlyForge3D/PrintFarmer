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
    @Environment(\.horizontalSizeClass) private var sizeClass
    @Environment(\.dynamicTypeSize) private var dynamicTypeSize

    let tab: AppTab
    let screenActions: ScreenActions

    func body(content: Content) -> some View {
        navigationContent(content)
            .toolbar {
                if sizeClass == .regular && router.isAtRoot(tab) {
                    ToolbarItem(placement: .topBarTrailing) {
                        screenActions
                    }
                    ToolbarItem(placement: .topBarTrailing) {
                        accountButton
                    }
                }
            }
            .toolbarBackground(Color.pfBackground, for: .navigationBar)
            .toolbarBackground(.visible, for: .navigationBar)
    }

    @ViewBuilder
    private func navigationContent<Content: View>(_ content: Content) -> some View {
        if sizeClass == .compact && router.isAtRoot(tab) {
            content
                .toolbar(.hidden, for: .navigationBar)
                .safeAreaInset(edge: .top, spacing: 0) {
                    compactHeader
                }
        } else {
            content
        }
    }

    private var compactHeader: some View {
        HStack(spacing: 12) {
            Text(tab.title)
                .font(.headline.weight(.semibold))
                .lineLimit(1)
                .minimumScaleFactor(0.8)
                .foregroundStyle(Color.pfTextPrimary)
                .accessibilityAddTraits(.isHeader)
                .accessibilityIdentifier("navigation.title")
            Spacer(minLength: 8)
            screenActions
            accountButton
        }
        .padding(.horizontal, 16)
        .frame(height: 52)
        .background(Color.pfBackground)
        .dynamicTypeSize(dynamicTypeSize.isAccessibilitySize ? .xxxLarge : dynamicTypeSize)
    }

    private var accountButton: some View {
        NavigationLink(value: AppDestination.account) {
            Image(systemName: "person.crop.circle")
                .font(.system(size: 18))
                .foregroundStyle(Color.pfTextSecondary)
                .frame(width: 32, height: 32)
                .background(Color.pfBackgroundTertiary, in: Circle())
                .overlay {
                    Circle().strokeBorder(Color.pfBorder, lineWidth: 1)
                }
                .frame(
                    width: RootNavigationChrome.minimumTouchTarget,
                    height: RootNavigationChrome.minimumTouchTarget
                )
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .accessibilityLabel("Account")
        .accessibilityHint("Opens notifications, settings, servers, and offline activity.")
        .accessibilityIdentifier(RootNavigationChrome.accountButtonIdentifier)
    }
}
