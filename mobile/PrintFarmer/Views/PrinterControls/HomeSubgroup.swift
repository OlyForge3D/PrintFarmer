import SwiftUI

/// Home subgroup of the printer Controls section. Three buttons in fixed order:
/// Home All (prominent, full-width), Home XY, Home Z (2-up standard row).
///
/// Spec: `mobile/docs/design/printer-controls-section.md` §2.3 Home, §2.4
/// (states), §4 (accessibility). Issue #288.
/// View model: `PrinterControlsViewModel`.
struct HomeSubgroup: View {

    @ObservedObject var viewModel: PrinterControlsViewModel

    /// Transient caption shown when the user taps a disabled control on a
    /// touch-only device (where `.help()` doesn't fire). Mirrors the pattern
    /// used in `PreheatSubgroup`.
    @State private var disabledTapMessage: String?

    @Environment(\.horizontalSizeClass) private var horizontalSizeClass

    /// Returns true when the entire subgroup must be removed from layout
    /// (capability gating per spec §3.5). The Controls section reflows the
    /// surrounding subgroups when this returns true.
    static func shouldHide(capabilities: PrinterBackendCapabilities?) -> Bool {
        guard let caps = capabilities else { return true }
        return !caps.supportsMovement || !caps.supportsHoming
    }

    private var isAllPending: Bool {
        if case .home(let axes) = viewModel.pendingCommand?.kind, axes == ["X", "Y", "Z"] { return true }
        return false
    }

    private var isXYPending: Bool {
        if case .home(let axes) = viewModel.pendingCommand?.kind, axes == ["X", "Y"] { return true }
        return false
    }

    private var isZPending: Bool {
        if case .home(let axes) = viewModel.pendingCommand?.kind, axes == ["Z"] { return true }
        return false
    }

    private var anyPending: Bool {
        if case .home = viewModel.pendingCommand?.kind { return true }
        return false
    }

    private var isDisabled: Bool {
        !viewModel.canControl
    }

    private func isErrored(matching axes: [String]) -> Bool {
        guard let last = viewModel.lastError else { return false }
        if case let .home(errAxes) = last.command.kind, errAxes == axes { return true }
        return false
    }

    var body: some View {
        if Self.shouldHide(capabilities: viewModel.capabilities) {
            EmptyView()
        } else {
            VStack(alignment: .leading, spacing: 8) {
                Text("Home")
                    .font(.headline)
                    .foregroundStyle(Color.pfTextPrimary)
                    .accessibilityAddTraits(.isHeader)

                homeAllButton

                HStack(spacing: 8) {
                    homeAxisButton(
                        label: String(localized: "Home XY", comment: "Home subgroup: Home X and Y axes button"),
                        symbol: "move.3d",
                        isPending: isXYPending,
                        hasError: isErrored(matching: ["X", "Y"]),
                        a11yLabel: isXYPending
                            ? String(localized: "Homing X and Y, in progress", comment: "VoiceOver: Home XY in flight")
                            : String(localized: "Home X and Y axes", comment: "VoiceOver: Home XY idle")
                    ) {
                        Task { await viewModel.homeXY() }
                    }
                    .disabled(isDisabled || (anyPending && !isXYPending))

                    homeAxisButton(
                        label: String(localized: "Home Z", comment: "Home subgroup: Home Z axis button"),
                        symbol: "arrow.up.and.down",
                        isPending: isZPending,
                        hasError: isErrored(matching: ["Z"]),
                        a11yLabel: isZPending
                            ? String(localized: "Homing Z, in progress", comment: "VoiceOver: Home Z in flight")
                            : String(localized: "Home Z axis", comment: "VoiceOver: Home Z idle")
                    ) {
                        Task { await viewModel.homeZ() }
                    }
                    .disabled(isDisabled || (anyPending && !isZPending))
                }

                if let message = disabledTapMessage {
                    Text(message)
                        .font(.footnote)
                        .foregroundStyle(Color.pfTextSecondary)
                        .transition(.opacity)
                }
            }
        }
    }

    private var homeAllButton: some View {
        Button {
            handleTap { Task { await viewModel.homeAll() } }
        } label: {
            homeAllLabel
        }
        .buttonStyle(ActionButtonStyle(size: .prominent))
        .frame(maxWidth: .infinity)
        .background(Color.pfButtonPrimary)
        .foregroundStyle(Color.pfButtonPrimaryText)
        .clipShape(RoundedRectangle(cornerRadius: 10, style: .continuous))
        .overlay(
            RoundedRectangle(cornerRadius: 10, style: .continuous)
                .strokeBorder(isAllPending ? Color.pfAssigned : Color.clear, lineWidth: 1.5)
        )
        .disabled((isDisabled && !shouldRevealDisabledTooltipOnTap) || (anyPending && !isAllPending))
        .disabledControlStyle(isDisabled: isDisabled && !isAllPending)
        .errorBorderHighlight(isActive: isErrored(matching: ["X", "Y", "Z"]))
        .accessibilityLabel(
            isAllPending
                ? String(localized: "Homing all axes, in progress", comment: "VoiceOver: Home All in flight")
                : String(localized: "Home all axes", comment: "VoiceOver: Home All idle")
        )
        .accessibilityHint(accessibilityHint(hasError: isErrored(matching: ["X", "Y", "Z"])))
        .accessibilityValue(accessibilityValue(isPending: isAllPending, hasError: isErrored(matching: ["X", "Y", "Z"])))
        .accessibilityAddTraits(isAllPending ? .updatesFrequently : .isButton)
        .help(viewModel.blockedReason ?? "")
    }

    @ViewBuilder
    private var homeAllLabel: some View {
        if isAllPending {
            ProgressView()
                .progressViewStyle(.circular)
                .tint(Color.pfButtonPrimaryText)
                .frame(maxWidth: .infinity)
        } else {
            Label("Home All", systemImage: "house.fill")
                .font(.subheadline.weight(.medium))
                .frame(maxWidth: .infinity)
        }
    }

    @ViewBuilder
    private func homeAxisButton(
        label: String,
        symbol: String,
        isPending: Bool,
        hasError: Bool,
        a11yLabel: String,
        action: @escaping () -> Void
    ) -> some View {
        Button {
            handleTap(action)
        } label: {
            ZStack {
                if isPending {
                    ProgressView()
                        .progressViewStyle(.circular)
                        .tint(Color.pfTextPrimary)
                } else {
                    Label(label, systemImage: symbol)
                        .font(.subheadline.weight(.medium))
                }
            }
            .frame(maxWidth: .infinity)
        }
        .buttonStyle(ActionButtonStyle(size: .standard))
        .background(Color.pfBackgroundTertiary)
        .foregroundStyle(Color.pfTextPrimary)
        .clipShape(RoundedRectangle(cornerRadius: 10, style: .continuous))
        .overlay(
            RoundedRectangle(cornerRadius: 10, style: .continuous)
                .strokeBorder(isPending ? Color.pfAssigned : Color.pfBorder, lineWidth: isPending ? 1.5 : 1)
        )
        .disabledControlStyle(isDisabled: isDisabled && !isPending)
        .errorBorderHighlight(isActive: hasError)
        .accessibilityLabel(a11yLabel)
        .accessibilityHint(accessibilityHint(hasError: hasError))
        .accessibilityValue(accessibilityValue(isPending: isPending, hasError: hasError))
        .accessibilityAddTraits(isPending ? .updatesFrequently : .isButton)
        .help(viewModel.blockedReason ?? "")
    }

    // MARK: - Disabled-tap reveal

    private var shouldRevealDisabledTooltipOnTap: Bool {
        horizontalSizeClass != .regular
    }

    /// Wraps the real action with the disabled-tap reveal: if the printer
    /// can't be controlled, surface `blockedReason` as a transient caption
    /// instead of dispatching the command. Mirrors `PreheatSubgroup`.
    private func handleTap(_ action: () -> Void) {
        guard viewModel.canControl else {
            let message = viewModel.blockedReason
                ?? String(localized: "Controls are unavailable.", comment: "Fallback when blockedReason is nil")
            withAnimation(.easeInOut(duration: 0.15)) {
                disabledTapMessage = message
            }
            Task { @MainActor in
                try? await Task.sleep(nanoseconds: 3_000_000_000)
                if disabledTapMessage == message {
                    withAnimation(.easeInOut(duration: 0.15)) {
                        disabledTapMessage = nil
                    }
                }
            }
            return
        }
        disabledTapMessage = nil
        action()
    }

    // MARK: - Accessibility strings

    private func accessibilityHint(hasError: Bool) -> String {
        if hasError, let message = viewModel.lastError?.message {
            return String(localized: "Failed: \(message). Double tap to retry.", comment: "VoiceOver hint when last home command failed")
        }
        if isDisabled, let reason = viewModel.blockedReason {
            return String(localized: "Disabled. \(reason)", comment: "VoiceOver hint when controls are disabled")
        }
        return ""
    }

    private func accessibilityValue(isPending: Bool, hasError: Bool) -> String {
        if isPending { return String(localized: "Sending command", comment: "VoiceOver value while a control command is in flight") }
        if hasError { return String(localized: "Failed", comment: "VoiceOver value when last command failed") }
        return ""
    }
}
