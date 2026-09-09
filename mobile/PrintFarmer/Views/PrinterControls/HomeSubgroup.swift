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
    @Environment(\.dynamicTypeSize) private var dynamicTypeSize

    /// Returns true when the entire subgroup must be removed from layout
    /// (capability gating per spec §3.5). The Controls section reflows the
    /// surrounding subgroups when this returns true.
    static func shouldHide(capabilities: PrinterBackendCapabilities?) -> Bool {
        guard let caps = capabilities else { return true }
        return !caps.supportsHoming && !caps.supportsHomingXY && !caps.supportsHomingZ
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
        viewModel.pendingCommand != nil
    }

    struct MotorReleaseControls: View {
        @ObservedObject var viewModel: PrinterControlsViewModel
        @State private var confirmsRelease = false

        static let warning = "Disabling motors removes holding force. Axes may move or drop under gravity. Support the mechanism and re-home before moving again. This is not Emergency Stop and does not turn heaters off."

        var body: some View {
            if viewModel.capabilities?.supportsDisableMotors == true {
                VStack(alignment: .leading, spacing: 8) {
                    Text("Motor maintenance")
                        .font(.headline)
                        .accessibilityAddTraits(.isHeader)
                    Text(Self.warning).font(.footnote).fixedSize(horizontal: false, vertical: true)
                    ControlActionButton(
                        title: "Disable motors", identifier: "printer.controls.disable-motors", isDestructive: true
                    ) {
                        confirmsRelease = true
                    }
                    .disabled(!viewModel.canControl || viewModel.isExecuting)
                }
                .foregroundStyle(Color.pfTextPrimary)
                .confirmationDialog("Disable motors?", isPresented: $confirmsRelease, titleVisibility: .visible) {
                    Button("Disable motors", role: .destructive) {
                        Task { await viewModel.disableMotors() }
                    }
                    Button("Cancel", role: .cancel) {}
                } message: {
                    Text(Self.warning)
                }
            }
        }
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

                if viewModel.capabilities?.supportsHoming == true {
                    homeAllButton
                }

                let layout = dynamicTypeSize.isAccessibilitySize
                    ? AnyLayout(VStackLayout(spacing: 8)) : AnyLayout(HStackLayout(spacing: 8))
                layout {
                    if viewModel.capabilities?.supportsHomingXY == true {
                        homeAxisButton(
                            label: String(localized: "Home XY", comment: "Home subgroup: Home X and Y axes button"),
                            symbol: "move.3d",
                            isPending: isXYPending,
                            hasError: isErrored(matching: ["X", "Y"]),
                            a11yLabel: isXYPending
                                ? String(localized: "Homing X and Y, in progress", comment: "VoiceOver: Home XY in flight")
                                : String(localized: "Home X and Y", comment: "VoiceOver: Home XY idle per spec §4.1"),
                            idleHint: String(localized: "Homes X and Y axes only.", comment: "VoiceOver idle hint Home XY per spec §4.1")
                        ) {
                            Task { await viewModel.homeXY() }
                        }
                        .disabled(isDisabled || (anyPending && !isXYPending))
                    }

                    if viewModel.capabilities?.supportsHomingZ == true {
                        homeAxisButton(
                            label: String(localized: "Home Z", comment: "Home subgroup: Home Z axis button"),
                            symbol: "arrow.up.and.down",
                            isPending: isZPending,
                            hasError: isErrored(matching: ["Z"]),
                            a11yLabel: isZPending
                                ? String(localized: "Homing Z, in progress", comment: "VoiceOver: Home Z in flight")
                                : String(localized: "Home Z", comment: "VoiceOver: Home Z idle per spec §4.1"),
                            idleHint: String(localized: "Homes Z axis only.", comment: "VoiceOver idle hint Home Z per spec §4.1")
                        ) {
                            Task { await viewModel.homeZ() }
                        }
                        .disabled(isDisabled || (anyPending && !isZPending))
                    }
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
        .accessibilityHint(accessibilityHint(hasError: isErrored(matching: ["X", "Y", "Z"]), idleHint: String(localized: "Homes X, Y, and Z.", comment: "VoiceOver idle hint Home All per spec §4.1")))
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
        idleHint: String,
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
        .accessibilityHint(accessibilityHint(hasError: hasError, idleHint: idleHint))
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

    func accessibilityHint(hasError: Bool, idleHint: String) -> String {
        if hasError, let message = viewModel.lastError?.message {
            return String(localized: "Failed: \(message). Double tap to retry.", comment: "VoiceOver hint when last home command failed")
        }
        if isDisabled {
            return String(localized: "Disabled while printing.", comment: "VoiceOver disabled hint per spec §4.1")
        }
        return idleHint
    }

    func accessibilityValue(isPending: Bool, hasError: Bool) -> String {
        if isPending { return String(localized: "Pending", comment: "VoiceOver value while command is in flight per spec §4.1") }
        if hasError { return String(localized: "Failed", comment: "VoiceOver value when last command failed") }
        return ""
    }
}
