import SwiftUI

// MARK: - Panel identity (issue #2522)

/// Stable identifier for the two printer-detail pages. Transient UI state
/// only — never persisted, never part of the routing/deep-link vocabulary.
/// Session identity remains registered server + printer UUID; this enum only
/// tracks which page is currently on screen.
enum PrinterDetailPanel: String, CaseIterable, Hashable, Sendable {
    case status
    case controls

    var title: String {
        switch self {
        case .status: return "Status"
        case .controls: return "Controls"
        }
    }

    /// Reserved by epic #2518: `printer.detail.panel.status` / `.controls`.
    var accessibilityIdentifier: String {
        switch self {
        case .status: return "printer.detail.panel.status"
        case .controls: return "printer.detail.panel.controls"
        }
    }
}

// MARK: - Panels host (pure paging shell)

/// Narrowly scoped host for the printer-detail Status/Controls paging
/// (issue #2522).
///
/// Purely a layout shell: a labeled selector plus native horizontal paging
/// over the two pages the caller supplies. The host owns NO models,
/// services, camera, sheets, or confirmations — those stay in
/// `PrinterDetailView` per the epic's "page selection sits above ownership"
/// contract. Both the selector tap and a horizontal swipe update the same
/// `selection` binding, so the two can never disagree.
struct PrinterDetailPanelsHost<Status: View, Controls: View>: View {
    @Binding var selection: PrinterDetailPanel
    let controlsAvailable: Bool
    @ViewBuilder let status: () -> Status
    @ViewBuilder let controls: () -> Controls

    /// Panels currently reachable given capability/access gates. A pure
    /// static function so panel-availability rules are unit-testable
    /// without hosting a view (`PrinterDetailPanelsTests`).
    static func availablePanels(controlsAvailable: Bool) -> [PrinterDetailPanel] {
        controlsAvailable ? PrinterDetailPanel.allCases : [.status]
    }

    /// Resolve a safe selection after a capability/access change. If the
    /// currently selected panel is no longer available (e.g. Controls access
    /// revoked while selected, or the printer went offline), fall back to
    /// `.status` rather than stranding the user on a page that no longer
    /// exists.
    static func resolvedSelection(
        current: PrinterDetailPanel,
        controlsAvailable: Bool
    ) -> PrinterDetailPanel {
        availablePanels(controlsAvailable: controlsAvailable).contains(current)
            ? current : .status
    }

    private var availablePanels: [PrinterDetailPanel] {
        Self.availablePanels(controlsAvailable: controlsAvailable)
    }

    var body: some View {
        VStack(spacing: 0) {
            if controlsAvailable {
                Picker("Printer detail panel", selection: $selection) {
                    ForEach(availablePanels, id: \.self) { panel in
                        Text(panel.title).tag(panel)
                    }
                }
                .pickerStyle(.segmented)
                .padding(.horizontal)
                .padding(.top, 8)
                .padding(.bottom, 4)
                .accessibilityIdentifier("printer.detail.panel.selector")
            }

            TabView(selection: $selection) {
                page(status(), panel: .status)
                    .tag(PrinterDetailPanel.status)

                if controlsAvailable {
                    page(controls(), panel: .controls)
                        .tag(PrinterDetailPanel.controls)
                }
            }
            #if os(iOS)
            .tabViewStyle(.page(indexDisplayMode: .never))
            #endif
        }
        .onChange(of: controlsAvailable) { _, newValue in
            selection = Self.resolvedSelection(current: selection, controlsAvailable: newValue)
        }
    }

    /// Wraps one page's content with its stable accessibility identifier and
    /// excludes it from VoiceOver traversal while it is not the active page —
    /// `TabView` can keep an adjacent page mounted for swipe animation, so
    /// exclusion has to be explicit rather than relied on from removal.
    private func page<Content: View>(_ content: Content, panel: PrinterDetailPanel) -> some View {
        content
            .accessibilityElement(children: .contain)
            .accessibilityIdentifier(panel.accessibilityIdentifier)
            .accessibilityLabel("\(panel.title) page")
            .accessibilityHidden(selection != panel)
    }
}

// MARK: - Run-action presentation mapping (issue #2522)

/// Pure mapping from `PrinterDetailViewModel` state to the shared
/// `PrinterRunActionPresentation` (issue #2520's bar). Extracted as a
/// standalone, host-agnostic function so the exact binding table from epic
/// #2518 — which kinds are visible in which state, and that Emergency Stop
/// is never blanket-disabled by another pending action — is unit-testable
/// without hosting a view or a live view model.
enum PrinterDetailRunActionMapping {
    /// - Parameters:
    ///   - isOnline: Printer's current connection state. This is the outer
    ///     gate for every descriptor: the original `actionSection` (which
    ///     held Pause/Resume/Cancel/Stop/Emergency Stop together) only
    ///     rendered at all `if printer.isOnline`, so a printer that reports
    ///     offline while retaining a stale `printing`/`paused` state must
    ///     never expose Stop (or any other run action) as if it were still
    ///     reachable. Emergency Stop is never a special case here — it is
    ///     gated by the same `isOnline` check as everything else, it is
    ///     just never additionally gated by `isPerformingAction`.
    ///   - isPrinting: Mirrors `PrinterDetailViewModel.isPrinting`.
    ///   - isPaused: Mirrors `PrinterDetailViewModel.isPaused`.
    ///   - isPerformingAction: Mirrors `PrinterDetailViewModel.isPerformingAction`,
    ///     the single-flight guard the view model already applies uniformly
    ///     to Pause/Resume/Cancel/Stop. Emergency Stop deliberately ignores
    ///     this input — it must never be blanket-disabled because another
    ///     action is pending (epic #2518 acceptance criterion).
    static func presentation(
        isOnline: Bool,
        isPrinting: Bool,
        isPaused: Bool,
        isPerformingAction: Bool
    ) -> PrinterRunActionPresentation {
        guard isOnline else { return .empty }
        var descriptors: [PrinterRunActionDescriptor] = []
        if isPrinting {
            descriptors.append(.init(kind: .pause, isEnabled: !isPerformingAction))
        }
        if isPaused {
            descriptors.append(.init(kind: .resume, isEnabled: !isPerformingAction))
        }
        if isPrinting || isPaused {
            descriptors.append(.init(kind: .cancel, isEnabled: !isPerformingAction))
            descriptors.append(.init(kind: .stop, isEnabled: !isPerformingAction))
        }
        descriptors.append(.init(kind: .emergencyStop, isEnabled: true))
        return PrinterRunActionPresentation(descriptors: descriptors)
    }
}

// MARK: - Filament action mapping (issue #2522)

/// Pure mapping from printer-detail filament state to the printer-level
/// `PrinterFilamentAction`s offered by #2519's `PrinterFilamentSection`.
/// Extracted so the "which native flows are offered, and when are they
/// disabled" rules are unit-testable without a live view model.
///
/// Only genuinely supported native flows are ever produced: `set`/`change`
/// (both route to the existing spool-picker sheet), `clearAssignment`
/// (routes to the existing eject flow) and `scanNFC`. `guidedSwap` is
/// intentionally never offered here — today it is reachable only via the
/// existing NFC deep-link (`AppRouter.pendingFilamentSwap`), not from an
/// in-page button, and inventing a button for it here would be exactly the
/// "unsupported slot mutation target" the epic forbids.
enum PrinterDetailFilamentActionMapping {
    static func supportedActions(hasActiveSpool: Bool) -> Set<PrinterFilamentAction.Kind> {
        var kinds: Set<PrinterFilamentAction.Kind> = hasActiveSpool ? [.change, .clearAssignment] : [.set]
        kinds.insert(.scanNFC)
        return kinds
    }

    static func actions(
        printerID: UUID,
        hasActiveSpool: Bool,
        isPerformingAction: Bool,
        nfcAvailable: Bool
    ) -> [PrinterFilamentAction] {
        let target = PrinterFilamentAction.Target.printer(printerID)
        let busyReason = isPerformingAction ? "Another action is in progress" : nil
        var actions: [PrinterFilamentAction] = []
        if hasActiveSpool {
            actions.append(PrinterFilamentAction(kind: .change, target: target, disabledReason: busyReason))
            actions.append(PrinterFilamentAction(kind: .clearAssignment, target: target, disabledReason: busyReason))
        } else {
            // `.set` must be disabled while another action is in progress
            // just like `.change`/`.clearAssignment` — it dispatches the
            // same single-flight `printerService.setActiveSpool` path via
            // the spool-picker sheet, so it is not safe to fire it
            // mid-flight either (Vasquez review finding 8).
            actions.append(PrinterFilamentAction(kind: .set, target: target, disabledReason: busyReason))
        }
        // NFC availability and single-flight busy state are independent
        // gates on the same action; combine them rather than letting a
        // busy scan remain tappable merely because NFC hardware is present
        // (Vasquez review finding 8).
        let scanNFCDisabledReason = !nfcAvailable
            ? "NFC scanning is not available on this device."
            : busyReason
        actions.append(PrinterFilamentAction(
            kind: .scanNFC,
            target: target,
            disabledReason: scanNFCDisabledReason
        ))
        return actions
    }
}

// MARK: - Filament coverage state mapping (issue #2522)

/// Pure mapping from `PrinterFilamentCoverageViewModel`'s published fields to
/// `PrinterFilamentPresentation.CoverageState`. Extracted so the precedence
/// (capability gate > feature-disabled > not-found > load error > success >
/// loading) is unit-testable without a live, SignalR-wired view model.
///
/// `lastLoadError` takes precedence over `hasCoverage` UNCONDITIONALLY, not
/// only when coverage is absent (Hicks review finding 9). In the real
/// `PrinterFilamentCoverageViewModel`, `commitSuccess` always clears
/// `lastLoadError`, so the only way both are non-nil/true at once is a
/// *retained* coverage snapshot from an earlier successful load followed by a
/// LATER canonical refresh that failed via `commitError` — which sets
/// `lastLoadError` but deliberately never clears `coverage`. Reporting
/// `.available` in that state would present stale, unconfirmed data as
/// current with every filament action left enabled. Reporting `.failed`
/// instead makes `PrinterFilamentPresentation` mark the printer stale (its
/// `isStale` computation treats any non-`.available`/non-`.disabled` state
/// with retained `coverage` as stale), which renders the "Last confirmed"
/// wording and empties `supportedActions`.
enum PrinterDetailFilamentCoverageStateMapping {
    static func coverageState(
        featureEnabled: Bool,
        isFeatureDisabled: Bool,
        isPrinterNotFound: Bool,
        hasCoverage: Bool,
        lastLoadError: String?
    ) -> PrinterFilamentPresentation.CoverageState {
        guard featureEnabled else { return .disabled }
        if isFeatureDisabled { return .disabled }
        if isPrinterNotFound { return .unavailable }
        if let lastLoadError { return .failed(lastLoadError) }
        if hasCoverage { return .available }
        return .loading
    }
}

// MARK: - Filament staleness mapping (issue #2522, Bishop review finding 6)

/// Pure mirror of `PrinterFilamentCoverageViewModel.isStaleCacheReportable`
/// (issue #789's truthful-staleness rule), used at the `PrinterDetailView`
/// wiring point so a unit test can pin the exact boolean the integration
/// passes into `PrinterFilamentPresentation.isStale` without needing a live,
/// SignalR-wired view model.
///
/// Passing the raw `isShowingStaleCache` flag directly would be wrong: it is
/// true from the instant a cache hydrates — before the first canonical load
/// has even concluded — and, per `PrinterFilamentCoverageViewModel
/// .commitError`, is never cleared by a generic (non-feature-disabled,
/// non-not-found) load error. Either would disable every filament action
/// (`PrinterFilamentPresentation` empties `supportedActions` while stale)
/// during ordinary warm-cache hydration, or indefinitely after one
/// transient error. Requiring `hasConcludedCanonicalLoad` too matches the
/// same precondition the stale banner already uses.
enum PrinterDetailFilamentStaleMapping {
    static func isStale(
        isShowingStaleCache: Bool,
        hasConcludedCanonicalLoad: Bool
    ) -> Bool {
        isShowingStaleCache && hasConcludedCanonicalLoad
    }
}
