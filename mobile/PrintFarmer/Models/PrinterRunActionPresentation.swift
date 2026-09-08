import Foundation
import SwiftUI

// MARK: - Kind

/// Stable action kinds the shared printer run-action bar knows how to render.
///
/// The set is fixed at five: Pause, Resume, Cancel, Stop, Emergency Stop.
/// The host maps each to the existing command path (per epic #2518 binding
/// table). The bar does not infer authorization from a display string and does
/// not add a synthetic "start" action for idle printers.
public enum PrinterRunActionKind: String, CaseIterable, Sendable, Equatable, Hashable {
    case pause
    case resume
    case cancel
    case stop
    case emergencyStop
}

// MARK: - Descriptor

/// Immutable per-action presentation the host supplies to `PrinterRunActionBar`.
///
/// The descriptor carries visibility, enablement, pending and an accessible
/// unavailable reason so operator state can flow through VoiceOver without the
/// component owning any command state, services, alerts, tasks or authorization
/// logic. Emergency Stop is deliberately independent — the bar never
/// blanket-disables it because another action is pending; that gate is the
/// host's to compute (epic #2518).
public struct PrinterRunActionDescriptor: Sendable, Equatable, Hashable {
    public let kind: PrinterRunActionKind
    /// Whether the action is applicable at all right now. Hidden descriptors
    /// are absent from layout and cannot fire callbacks.
    public let isVisible: Bool
    /// Whether the visible action is currently tappable. Disabled visible
    /// actions render dimmed with an accessible unavailable reason.
    public let isEnabled: Bool
    /// Whether the action's own command is already in flight. Pending visible
    /// actions render as busy and do not fire additional callbacks.
    public let isPending: Bool
    /// VoiceOver-facing reason when the action is unavailable (hidden or
    /// disabled). Free-form text supplied by the host; never inferred from a
    /// display label.
    public let unavailableReason: String?

    public init(
        kind: PrinterRunActionKind,
        isVisible: Bool = true,
        isEnabled: Bool = true,
        isPending: Bool = false,
        unavailableReason: String? = nil
    ) {
        self.kind = kind
        self.isVisible = isVisible
        self.isEnabled = isEnabled
        self.isPending = isPending
        self.unavailableReason = unavailableReason
    }
}

// MARK: - Presentation

/// Immutable presentation for `PrinterRunActionBar`.
///
/// Holds at most one descriptor per `PrinterRunActionKind`. Duplicate kinds in
/// the input are collapsed to the last-supplied descriptor so composition from
/// multiple presentation stages remains predictable rather than crashing.
public struct PrinterRunActionPresentation: Sendable, Equatable, Hashable {

    /// Stable render order the bar uses when laying out visible actions.
    ///
    /// This is the operator's expected reading order: Pause/Resume first (they
    /// are mutually exclusive but occupy the same slot), then the ordinary
    /// destructive Cancel, then the harder Stop, then the distinct Emergency
    /// Stop last.
    public static let renderOrder: [PrinterRunActionKind] = [
        .pause,
        .resume,
        .cancel,
        .stop,
        .emergencyStop,
    ]

    private let descriptorsByKind: [PrinterRunActionKind: PrinterRunActionDescriptor]

    public init(descriptors: [PrinterRunActionDescriptor] = []) {
        var byKind: [PrinterRunActionKind: PrinterRunActionDescriptor] = [:]
        for descriptor in descriptors {
            byKind[descriptor.kind] = descriptor
        }
        self.descriptorsByKind = byKind
    }

    /// An empty presentation. The bar renders nothing (used for idle/offline
    /// hosts that supply no actions).
    public static let empty = PrinterRunActionPresentation(descriptors: [])

    // MARK: Access

    /// Returns the descriptor for `kind`, or `nil` if the host did not supply
    /// one. Absent descriptors are not the same as invisible descriptors: an
    /// absent kind never renders and can never fire a callback.
    public func descriptor(for kind: PrinterRunActionKind) -> PrinterRunActionDescriptor? {
        descriptorsByKind[kind]
    }

    /// Descriptors in render order that are marked visible. Absent and hidden
    /// descriptors are filtered out so the bar can iterate without checking
    /// visibility itself.
    public var visibleDescriptors: [PrinterRunActionDescriptor] {
        Self.renderOrder.compactMap { descriptorsByKind[$0] }.filter { $0.isVisible }
    }

    /// Visible-kind set. Convenient for tests and host mapping.
    public var visibleKinds: [PrinterRunActionKind] {
        visibleDescriptors.map(\.kind)
    }

    public var routineActions: PrinterRunActionPresentation {
        .init(descriptors: visibleDescriptors.filter { $0.kind != .emergencyStop })
    }

    public var emergencyAction: PrinterRunActionPresentation {
        .init(descriptors: visibleDescriptors.filter { $0.kind == .emergencyStop })
    }

    // MARK: Callback gating

    /// Whether tapping `kind` right now should fire the host's `onSelect`.
    ///
    /// The bar consults this immediately before invoking the callback so a
    /// hidden, disabled, pending, or absent action can never emit a spurious
    /// event from a redraw, a page swipe or a stale gesture (issue #2520).
    public func shouldFireCallback(for kind: PrinterRunActionKind) -> Bool {
        guard let descriptor = descriptorsByKind[kind] else { return false }
        return descriptor.isVisible && descriptor.isEnabled && !descriptor.isPending
    }
}

// MARK: - Labels & accessibility strings

/// Display and accessibility strings for each `PrinterRunActionKind`.
///
/// Extracted from the SwiftUI bar so tests can validate the exact operator-
/// facing text (particularly the distinct Emergency Stop label and its
/// physical-safety hint) without hosting a view. All strings are English; if
/// localization is added later, mirror the change in `PrinterRunActionBarTests`.
public enum PrinterRunActionLabels {

    /// Visible button label. Emergency Stop keeps its full text — the epic
    /// forbids reducing it to an unlabeled icon.
    public static func title(for kind: PrinterRunActionKind) -> String {
        switch kind {
        case .pause: return "Pause"
        case .resume: return "Resume"
        case .cancel: return "Cancel"
        case .stop: return "Stop"
        case .emergencyStop: return "Emergency Stop"
        }
    }

    /// SF Symbol used for the button icon.
    public static func systemImage(for kind: PrinterRunActionKind) -> String {
        switch kind {
        case .pause: return "pause.fill"
        case .resume: return "play.fill"
        case .cancel: return "xmark.circle.fill"
        case .stop: return "stop.fill"
        case .emergencyStop: return "exclamationmark.octagon.fill"
        }
    }

    /// Distinct VoiceOver label for each kind. Emergency Stop's label is
    /// intentionally different from Stop's so a person using VoiceOver never
    /// confuses the two and never hears a generic "button".
    public static func accessibilityLabel(for kind: PrinterRunActionKind) -> String {
        switch kind {
        case .pause: return "Pause print"
        case .resume: return "Resume print"
        case .cancel: return "Cancel current print"
        case .stop: return "Stop print"
        case .emergencyStop: return "Emergency stop printer"
        }
    }

    /// VoiceOver hint spoken after the label. Cancel and Emergency Stop each
    /// announce that they require confirmation (verified against
    /// `PrinterDetailViewModel.requestCancel()` and `requestEmergencyStop()`,
    /// which both flip `showConfirmation`). Emergency Stop also announces
    /// that it is not a substitute for the physical safety switch — the epic
    /// pins this language. Stop does **not** promise confirmation because
    /// `PrinterDetailViewModel.stopPrinter()` dispatches immediately today;
    /// if the integrator (issue #2522) later adds a confirmation gate for
    /// Stop, they can update this hint in the same change.
    public static func accessibilityHint(for kind: PrinterRunActionKind) -> String {
        switch kind {
        case .pause:
            return "Pauses the active print. You can resume it later."
        case .resume:
            return "Resumes the paused print from where it stopped."
        case .cancel:
            return "Cancels the current print. Requires confirmation."
        case .stop:
            return "Stops the printer and clears the current job."
        case .emergencyStop:
            return "Immediately halts the printer. Requires confirmation. Not a substitute for the physical safety switch."
        }
    }

    /// Resolved VoiceOver hint for a descriptor.
    ///
    /// Composition (order matters — evaluate pending BEFORE disabled so a
    /// pending-and-disabled descriptor keeps the static hint that describes
    /// what activating the button will do; the "Pending" value already carries
    /// the in-flight signal, and swapping in a disable reason would drop that
    /// description):
    ///
    ///   1. Pending → static per-kind hint.
    ///   2. Disabled with a non-empty host reason → the reason (mirrors
    ///      sibling `PrinterControlsSection` in issue #2519 so both surfaces
    ///      on the printer-detail screen explain disable state the same way
    ///      when #2522 embeds them together).
    ///   3. Disabled without a reason (or empty) → the generic
    ///      "Unavailable" fallback so VoiceOver never reads a stale enabled
    ///      hint on a dimmed button.
    ///   4. Enabled and non-pending → the static per-kind hint.
    public static func resolvedAccessibilityHint(
        for descriptor: PrinterRunActionDescriptor
    ) -> String {
        if descriptor.isPending {
            return accessibilityHint(for: descriptor.kind)
        }
        if !descriptor.isEnabled {
            if let reason = descriptor.unavailableReason, !reason.isEmpty {
                return reason
            }
            return "Unavailable"
        }
        return accessibilityHint(for: descriptor.kind)
    }

    /// Resolved VoiceOver value for a descriptor. "Pending" while a command is
    /// in flight, empty otherwise. The disable reason lives in the hint (see
    /// `resolvedAccessibilityHint(for:)`), not here.
    public static func resolvedAccessibilityValue(
        for descriptor: PrinterRunActionDescriptor
    ) -> String {
        descriptor.isPending ? "Pending" : ""
    }

    /// Resolved VoiceOver traits for a descriptor.
    ///
    /// Mirrors the sibling pattern used by `HomeSubgroup`, `JogSubgroup` and
    /// `PreheatSubgroup` in issue #2519: `.updatesFrequently` while a command
    /// is in flight (so VoiceOver re-reads state as pending resolves),
    /// otherwise `.isButton`. **Never `.isSelected`** — that trait announces
    /// a chosen/on state (radio, tab), which on a dimmed destructive action
    /// like a disabled Emergency Stop would read as "armed" to a VoiceOver
    /// operator. Disabled state is already conveyed by SwiftUI's own
    /// `.disabled(...)` modifier (VoiceOver announces "dimmed") plus the
    /// host reason in the hint.
    public static func resolvedAccessibilityTraits(
        for descriptor: PrinterRunActionDescriptor
    ) -> AccessibilityTraits {
        descriptor.isPending ? .updatesFrequently : .isButton
    }

    /// Stable accessibility identifier used by both UI tests and the host's
    /// integration point. Kept aligned with the identifiers the epic reserved:
    /// `printer.detail.control.pause`, `.resume`, `.cancel`, `.stop`,
    /// `.emergencyStop`.
    public static func accessibilityIdentifier(for kind: PrinterRunActionKind) -> String {
        switch kind {
        case .pause: return "printer.detail.control.pause"
        case .resume: return "printer.detail.control.resume"
        case .cancel: return "printer.detail.control.cancel"
        case .stop: return "printer.detail.control.stop"
        case .emergencyStop: return "printer.detail.control.emergencyStop"
        }
    }

    /// Container identifier for the whole bar. Reserved by epic #2518.
    public static let containerAccessibilityIdentifier = "printer.detail.runActions"

    /// Whether the kind renders as a destructive action. Used both to pick
    /// the button role and to compose the tint so color is never the only
    /// signal — the accessibility label already distinguishes destructive
    /// actions in words.
    public static func isDestructive(_ kind: PrinterRunActionKind) -> Bool {
        switch kind {
        case .pause, .resume: return false
        case .cancel, .stop, .emergencyStop: return true
        }
    }
}
