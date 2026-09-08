import SwiftUI

/// Shared, accessible run-action bar for printer detail (issue #2520).
///
/// The bar is deliberately passive: it renders a supplied
/// `PrinterRunActionPresentation` and, when an enabled action is tapped, calls
/// `onSelect` exactly once with that action's `PrinterRunActionKind`. It owns
/// no services, tasks, view models, confirmations, alerts, haptics policy,
/// networking or authorization logic. All of that stays in the host (see the
/// binding map in epic #2518).
///
/// Key invariants:
///   * At most one descriptor per kind; the host never gets duplicate slots.
///   * Absent, hidden, disabled, or pending descriptors never fire `onSelect`.
///   * Emergency Stop's own descriptor is the only gate on Emergency Stop.
///     The bar never applies a blanket disabled modifier that would remove it
///     because some other action is pending.
///   * Emergency Stop keeps its visible "Emergency Stop" label — it is never
///     reduced to an unlabeled icon — and is announced with a distinct
///     accessibility label so VoiceOver never confuses it with Stop.
///   * Every action has at least a 44×44 pt hit target (Apple HIG).
///   * Adaptive layout: at accessibility Dynamic Type sizes the row wraps to
///     stacked, so essential actions never clip or hide behind an overflow
///     menu.
///   * A horizontal page swipe never activates a run command — the enclosing
///     pager owns gestures; this component uses only ordinary SwiftUI
///     `Button`s.
public struct PrinterRunActionBar: View {

    public let presentation: PrinterRunActionPresentation
    private let onSelect: (PrinterRunActionKind) -> Void

    /// Callback-only. `onSelect` is invoked on the main actor exactly once per
    /// enabled activation. The host is responsible for confirmation flows,
    /// haptics, telemetry and command dispatch.
    public init(
        presentation: PrinterRunActionPresentation,
        onSelect: @escaping (PrinterRunActionKind) -> Void
    ) {
        self.presentation = presentation
        self.onSelect = onSelect
    }

    @Environment(\.dynamicTypeSize) private var dynamicTypeSize

    /// When Dynamic Type is at an accessibility size, the row wraps to a
    /// stacked column so labels are not clipped and every action keeps its
    /// 44 pt height. Emergency Stop remains separate from routine actions.
    private var shouldStack: Bool {
        dynamicTypeSize.isAccessibilitySize
    }

    public var body: some View {
        let visible = presentation.visibleDescriptors
        if visible.isEmpty {
            EmptyView()
        } else {
            VStack(spacing: 10) {
                let primary = visible.filter { $0.kind != .emergencyStop }
                if !primary.isEmpty {
                    if shouldStack {
                        VStack(spacing: 10) {
                            ForEach(primary, id: \.kind) { descriptor in
                                actionButton(for: descriptor)
                            }
                        }
                    } else {
                        HStack(spacing: 12) {
                            ForEach(primary, id: \.kind) { descriptor in
                                actionButton(for: descriptor)
                            }
                        }
                    }
                }
                if let emergency = presentation.descriptor(for: .emergencyStop),
                   emergency.isVisible {
                    emergencyStopButton(for: emergency)
                }
            }
            // Modifier ORDER matters here (issue #2522, Hicks review finding
            // 22): `.accessibilityElement(children: .contain)` must come
            // BEFORE `.accessibilityIdentifier(...)` so the identifier is
            // attached to the already-established contain boundary, not to
            // a plain VStack that `.contain` then wraps. The reverse order
            // (identifier first, `.contain` second) left every descendant
            // button reporting THIS container's identifier instead of its
            // own more specific one (each button below already sets its own
            // `.accessibilityIdentifier`) — confirmed via the accessibility
            // tree dump in `PrinterDetailPanelsUITests`, and matching the
            // ordering already fixed for `PrinterDetailView`'s outer root
            // identifier for the same underlying reason. Purely a modifier
            // reordering: no structural or behavioral change.
            .accessibilityElement(children: .contain)
            .accessibilityIdentifier(PrinterRunActionLabels.containerAccessibilityIdentifier)
        }
    }

    // MARK: - Buttons

    /// Ordinary Pause/Resume/Cancel/Stop button. Cancel and Stop use the
    /// destructive role and pfError tint; Pause/Resume are non-destructive
    /// bordered buttons using the accent tint. Color is a reinforcement, not
    /// the only signal — the accessibility label already distinguishes them.
    @ViewBuilder
    private func actionButton(for descriptor: PrinterRunActionDescriptor) -> some View {
        let kind = descriptor.kind
        let isDestructive = PrinterRunActionLabels.isDestructive(kind)
        Button(role: isDestructive ? .destructive : nil) {
            fire(kind)
        } label: {
            Label(
                PrinterRunActionLabels.title(for: kind),
                systemImage: PrinterRunActionLabels.systemImage(for: kind)
            )
            .fullWidthActionButton()
        }
        .buttonStyle(.bordered)
        .tint(isDestructive ? Color.pfError : Color.pfAccent)
        .disabled(!descriptor.isEnabled || descriptor.isPending)
        .accessibilityIdentifier(PrinterRunActionLabels.accessibilityIdentifier(for: kind))
        .accessibilityLabel(PrinterRunActionLabels.accessibilityLabel(for: kind))
        .accessibilityHint(accessibilityHint(for: descriptor))
        .accessibilityValue(accessibilityValue(for: descriptor))
        .accessibilityAddTraits(traits(for: descriptor))
    }

    /// Emergency Stop is a compact labeled action for the detail's top bar.
    /// carrying a distinct label. The bar never blanket-disables it because
    /// another descriptor is pending — the host's descriptor is the only gate.
    private func emergencyStopButton(for descriptor: PrinterRunActionDescriptor) -> some View {
        Button(role: .destructive) {
            fire(.emergencyStop)
        } label: {
            Label(
                PrinterRunActionLabels.title(for: .emergencyStop),
                systemImage: PrinterRunActionLabels.systemImage(for: .emergencyStop)
            )
            .fixedSize(horizontal: false, vertical: true)
            .frame(minWidth: 44, minHeight: 44)
            .contentShape(Rectangle())
            .fontWeight(.semibold)
        }
        .buttonStyle(.bordered)
        .tint(Color.pfError)
        .disabled(!descriptor.isEnabled || descriptor.isPending)
        .accessibilityIdentifier(
            PrinterRunActionLabels.accessibilityIdentifier(for: .emergencyStop)
        )
        .accessibilityLabel(
            PrinterRunActionLabels.accessibilityLabel(for: .emergencyStop)
        )
        .accessibilityHint(accessibilityHint(for: descriptor))
        .accessibilityValue(accessibilityValue(for: descriptor))
        .accessibilityAddTraits(traits(for: descriptor))
    }

    // MARK: - Callback gate

    /// Guards the callback so a hidden, disabled, pending or absent descriptor
    /// can never emit an `onSelect` event. Redraws and page-gesture side
    /// effects can't slip through because SwiftUI only invokes the closure on
    /// actual button activation, and this check filters those.
    ///
    /// Exposed as `internal` (via `@testable import`) so tests can drive the
    /// gate directly and observe whether `onSelect` fires with the expected
    /// kind — this closes the "manually appends kind" gap Hicks flagged: the
    /// test now goes through the same `fire → gate → onSelect` path the
    /// production button activation does.
    func fire(_ kind: PrinterRunActionKind) {
        guard presentation.shouldFireCallback(for: kind) else { return }
        onSelect(kind)
    }

    // MARK: - Accessibility helpers

    /// Delegates to `PrinterRunActionLabels.resolvedAccessibilityHint(for:)`
    /// so the composition (static hint when enabled or pending, host-supplied
    /// reason when disabled) is a pure, unit-testable function that lives with
    /// the labels helper. This mirrors the disable-reason-as-hint pattern the
    /// printer controls section adopted in issue #2519.
    private func accessibilityHint(for descriptor: PrinterRunActionDescriptor) -> String {
        PrinterRunActionLabels.resolvedAccessibilityHint(for: descriptor)
    }

    /// Delegates to `PrinterRunActionLabels.resolvedAccessibilityValue(for:)`.
    private func accessibilityValue(for descriptor: PrinterRunActionDescriptor) -> String {
        PrinterRunActionLabels.resolvedAccessibilityValue(for: descriptor)
    }

    /// Delegates to `PrinterRunActionLabels.resolvedAccessibilityTraits(for:)`
    /// so the trait composition (`.updatesFrequently` when pending,
    /// `.isButton` otherwise; never `.isSelected` on disabled or pending, per
    /// sibling pattern in issue #2519) is a pure, unit-testable function.
    private func traits(for descriptor: PrinterRunActionDescriptor) -> AccessibilityTraits {
        PrinterRunActionLabels.resolvedAccessibilityTraits(for: descriptor)
    }
}

// MARK: - Previews

#if DEBUG
#Preview("Printing") {
    PrinterRunActionBar(
        presentation: PrinterRunActionPresentation(descriptors: [
            .init(kind: .pause),
            .init(kind: .cancel),
            .init(kind: .stop),
            .init(kind: .emergencyStop),
        ]),
        onSelect: { _ in }
    )
    .padding()
}

#Preview("Paused") {
    PrinterRunActionBar(
        presentation: PrinterRunActionPresentation(descriptors: [
            .init(kind: .resume),
            .init(kind: .cancel),
            .init(kind: .stop),
            .init(kind: .emergencyStop),
        ]),
        onSelect: { _ in }
    )
    .padding()
}

#Preview("Idle with only Emergency Stop") {
    PrinterRunActionBar(
        presentation: PrinterRunActionPresentation(descriptors: [
            .init(kind: .emergencyStop),
        ]),
        onSelect: { _ in }
    )
    .padding()
}

#Preview("Pending Cancel, disabled Stop") {
    PrinterRunActionBar(
        presentation: PrinterRunActionPresentation(descriptors: [
            .init(kind: .pause),
            .init(kind: .cancel, isPending: true),
            .init(
                kind: .stop,
                isEnabled: false,
                unavailableReason: "no active job"
            ),
            .init(kind: .emergencyStop),
        ]),
        onSelect: { _ in }
    )
    .padding()
}
#endif
