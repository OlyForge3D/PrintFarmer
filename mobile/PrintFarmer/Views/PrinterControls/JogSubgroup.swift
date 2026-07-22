import SwiftUI

/// Jog subgroup of `PrinterControlsSection`. Axis picker (X/Y/Z) + step picker
/// (0.1/1/10/100 mm) + signed +/- buttons. Calls `viewModel.jog(axis:distanceMm:)`.
///
/// Spec: `mobile/docs/design/printer-controls-section.md` §2.3 (Jog), §2.4 (states).
/// Feedrate is internal to `PrinterControlsViewModel` (3000 mm/min XY, 600 mm/min Z) —
/// not exposed to the user in v1.
struct JogSubgroup: View {

    @ObservedObject var viewModel: PrinterControlsViewModel

    @State private var selectedAxis: String = "X"
    @State private var selectedStep: Double = 1
    /// Transient caption shown when the user taps a jog button while controls
    /// are disabled. Mirrors `PreheatSubgroup` so the disabled reason surfaces
    /// on touch devices where `.help()` doesn't fire.
    @State private var disabledTapMessage: String?

    @Environment(\.horizontalSizeClass) private var horizontalSizeClass

    static let stepOptions: [Double] = [0.1, 1, 10, 100]
    private static let canonicalAxes: [String] = ["X", "Y", "Z"]

    /// Filter the canonical X/Y/Z list to what the backend reports. When capabilities
    /// have not been fetched yet, default to the full list so the UI renders sensibly
    /// during initial load. Empty result → caller should hide the subgroup.
    static func visibleAxes(for capabilities: PrinterBackendCapabilities?) -> [String] {
        guard let caps = capabilities else { return canonicalAxes }
        return canonicalAxes.filter { caps.supportedAxes.contains($0) }
    }

    /// True when the subgroup must be removed from the layout entirely.
    /// Hide if the backend explicitly does not support movement, or if all canonical
    /// axes are filtered out by `supportedAxes`.
    static func isHidden(for capabilities: PrinterBackendCapabilities?) -> Bool {
        guard let caps = capabilities else { return false }
        if !caps.supportsMovement { return true }
        return visibleAxes(for: capabilities).isEmpty
    }

    var body: some View {
        if Self.isHidden(for: viewModel.capabilities) {
            EmptyView()
        } else {
            content
        }
    }

    private var content: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("Jog")
                .font(.headline)
                .foregroundStyle(Color.pfTextPrimary)
                .accessibilityAddTraits(.isHeader)

            axisPicker
            stepPicker
            jogButtons

            if let message = disabledTapMessage {
                Text(message)
                    .font(.footnote)
                    .foregroundStyle(Color.pfTextSecondary)
                    .transition(.opacity)
            }
        }
        .task {
            await viewModel.loadCapabilities()
        }
        .onChange(of: viewModel.capabilities) { _, newCaps in
            // If the printer reports a narrower axis set than what's currently
            // selected, snap to the first available axis so we never dispatch
            // a jog against an unsupported axis.
            let axes = Self.visibleAxes(for: newCaps)
            if !axes.contains(selectedAxis), let first = axes.first {
                selectedAxis = first
            }
        }
    }

    // MARK: - Subviews

    private var axisPicker: some View {
        let axes = Self.visibleAxes(for: viewModel.capabilities)
        return Picker(String(localized: "Jog axis", comment: "Jog subgroup axis picker label"), selection: $selectedAxis) {
            ForEach(axes, id: \.self) { axis in
                Text(axis).tag(axis)
            }
        }
        .pickerStyle(.segmented)
        .accessibilityLabel(String(localized: "Jog axis", comment: "VoiceOver label for jog axis picker"))
        .accessibilityHint(String(localized: "Choose X, Y, or Z axis to move.", comment: "VoiceOver hint for jog axis picker"))
        .disabled(!viewModel.canControl || viewModel.pendingCommand != nil)
    }

    private var stepPicker: some View {
        Picker(String(localized: "Jog step distance", comment: "Jog subgroup step picker label"), selection: $selectedStep) {
            ForEach(Self.stepOptions, id: \.self) { step in
                Text(stepLabel(step)).tag(step)
            }
        }
        .pickerStyle(.segmented)
        .accessibilityLabel(String(localized: "Jog step distance", comment: "VoiceOver label for jog step picker"))
        .accessibilityHint(String(localized: "Choose how many millimeters each tap moves.", comment: "VoiceOver hint for jog step picker"))
        .disabled(!viewModel.canControl || viewModel.pendingCommand != nil)
    }

    private var jogButtons: some View {
        HStack(spacing: 8) {
            jogButton(direction: -1, symbol: "minus.circle.fill")
            jogButton(direction: 1, symbol: "plus.circle.fill")
        }
    }

    @ViewBuilder
    private func jogButton(direction: Double, symbol: String) -> some View {
        let isPending = isPendingJog(direction: direction)
        let signedStep = direction * selectedStep
        let stepLabelText = stepLabel(selectedStep)
        let hasError = isErrored(direction: direction)
        let isInteractive = viewModel.canControl && viewModel.pendingCommand == nil

        Button {
            handleTap {
                Task { await viewModel.jog(axis: selectedAxis, distanceMm: signedStep) }
            }
        } label: {
            ZStack {
                if isPending {
                    ProgressView()
                } else {
                    Image(systemName: symbol)
                        .font(.title2)
                }
            }
            .frame(maxWidth: .infinity, minHeight: 60)
            .background(Color.pfBackgroundTertiary)
            .foregroundStyle(Color.pfTextPrimary)
            .clipShape(RoundedRectangle(cornerRadius: 10))
        }
        .buttonStyle(.plain)
        .disabled(!isInteractive && !shouldRevealDisabledTooltipOnTap)
        .disabledControlStyle(isDisabled: !isInteractive && !isPending)
        .errorBorderHighlight(isActive: hasError)
        .accessibilityLabel(jogAccessibilityLabel(direction: direction))
        .accessibilityHint(jogAccessibilityHint(direction: direction, stepLabelText: stepLabelText, hasError: hasError))
        .accessibilityValue(jogAccessibilityValue(isPending: isPending, hasError: hasError))
        .accessibilityAddTraits(isPending ? .updatesFrequently : .isButton)
        .help(viewModel.blockedReason ?? "")
    }

    // MARK: - Helpers

    private var shouldRevealDisabledTooltipOnTap: Bool {
        horizontalSizeClass != .regular
    }

    private func handleTap(_ action: () -> Void) {
        guard viewModel.canControl, viewModel.pendingCommand == nil else {
            let message = viewModel.blockedReason
                ?? String(localized: "Another command is in flight.", comment: "Fallback when jog tap blocked by single-flight")
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

    func jogAccessibilityLabel(direction: Double) -> String {
        if direction > 0 {
            return String(localized: "Jog forward", comment: "VoiceOver label for positive jog per spec §4.1")
        }
        return String(localized: "Jog backward", comment: "VoiceOver label for negative jog per spec §4.1")
    }

    func jogAccessibilityHint(direction: Double, stepLabelText: String, hasError: Bool) -> String {
        if hasError, let message = viewModel.lastError?.message {
            return String(localized: "Failed: \(message). Double tap to retry.", comment: "VoiceOver hint when last jog command failed")
        }
        if !viewModel.canControl {
            return String(localized: "Disabled while printing.", comment: "VoiceOver disabled hint per spec §4.1")
        }
        if direction > 0 {
            return String(localized: "Moves \(selectedAxis) positive \(stepLabelText) millimeters.", comment: "VoiceOver hint for positive jog per spec §4.1")
        }
        return String(localized: "Moves \(selectedAxis) negative \(stepLabelText) millimeters.", comment: "VoiceOver hint for negative jog per spec §4.1")
    }

    func jogAccessibilityValue(isPending: Bool, hasError: Bool) -> String {
        if isPending { return String(localized: "Pending", comment: "VoiceOver value while a jog command is in flight per spec §4.1") }
        if hasError { return String(localized: "Failed", comment: "VoiceOver value when last jog failed") }
        return ""
    }

    private func isErrored(direction: Double) -> Bool {
        guard let last = viewModel.lastError else { return false }
        if case let .jog(axis, distance) = last.command.kind {
            return axis.uppercased() == selectedAxis.uppercased()
                && (distance > 0 ? direction > 0 : direction < 0)
        }
        return false
    }

    private func isPendingJog(direction: Double) -> Bool {
        guard case let .jog(axis, distance)? = viewModel.pendingCommand?.kind else { return false }
        return axis.uppercased() == selectedAxis.uppercased()
            && (distance > 0 ? direction > 0 : direction < 0)
    }

    private func stepLabel(_ value: Double) -> String {
        if value == value.rounded() { return String(Int(value)) }
        return String(value)
    }
}
