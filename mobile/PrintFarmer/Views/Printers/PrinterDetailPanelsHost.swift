import SwiftUI

// MARK: - Panel identity (issue #2522)

/// Stable identifier for the two printer-detail pages. Transient UI state
/// only — never persisted, never part of the routing/deep-link vocabulary.
/// Session identity remains registered server + printer UUID; this enum only
/// tracks which page is currently on screen.
enum PrinterDetailPanel: String, CaseIterable, Hashable, Sendable {
    case overview
    case controls

    var title: String {
        switch self {
        case .overview: return String(localized: "Overview")
        case .controls: return "Controls"
        }
    }

    /// Reserved by epic #2518: `printer.detail.panel.overview` / `.controls`.
    var accessibilityIdentifier: String {
        switch self {
        case .overview: return "printer.detail.panel.overview"
        case .controls: return "printer.detail.panel.controls"
        }
    }
}

// MARK: - Panels host (pure paging shell)

/// Narrowly scoped host for the printer-detail Overview/Controls paging
/// (issue #2522).
///
/// Purely a layout shell: a labeled selector plus native horizontal paging
/// over the two pages the caller supplies. The host owns NO models,
/// services, camera, sheets, or confirmations — those stay in
/// `PrinterDetailView` per the epic's "page selection sits above ownership"
/// contract. Both the selector tap and a horizontal swipe update the same
/// `selection` binding, so the two can never disagree.
struct PrinterDetailPanelsHost<Overview: View, Controls: View>: View {
    @Binding var selection: PrinterDetailPanel
    let controlsAvailable: Bool
    @ViewBuilder let overview: () -> Overview
    @ViewBuilder let controls: () -> Controls

    /// Discoverability is independent of command authorization. The Controls
    /// page explains unavailable access instead of removing the destination.
    static func availablePanels(controlsAvailable: Bool) -> [PrinterDetailPanel] {
        PrinterDetailPanel.allCases
    }

    /// Retain the chosen destination through offline/preference transitions.
    static func resolvedSelection(
        current: PrinterDetailPanel,
        controlsAvailable: Bool
    ) -> PrinterDetailPanel {
        current
    }

    private var availablePanels: [PrinterDetailPanel] {
        Self.availablePanels(controlsAvailable: controlsAvailable)
    }

    var body: some View {
        VStack(spacing: 0) {
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

            TabView(selection: $selection) {
                page(overview(), panel: .overview)
                    .tag(PrinterDetailPanel.overview)

                page(controls(), panel: .controls)
                    .tag(PrinterDetailPanel.controls)
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
    ///   - isOnline: Per-action gate restoring the ORIGINAL, pre-#2522 parity
    ///     (Hicks review finding 13): the old `primaryControlsRow` (header)
    ///     exposed Pause/Resume/Cancel purely from `isPrinting`/`isPaused`/
    ///     `isPerformingAction`, with NO online check at all, while only the
    ///     old `actionSection`'s Stop and Emergency Stop lived behind `if
    ///     printer.isOnline`. A blanket `isOnline` gate over every
    ///     descriptor (an earlier revision of this mapping) was stricter
    ///     than either original surface for Pause/Resume/Cancel. So: Pause,
    ///     Resume and Cancel are gated ONLY on print state and the pending
    ///     guard; Stop and Emergency Stop additionally require `isOnline`.
    ///   - isPrinting: Mirrors `PrinterDetailViewModel.isPrinting`.
    ///   - isPaused: Mirrors `PrinterDetailViewModel.isPaused`.
    ///   - isPerformingAction: Mirrors `PrinterDetailViewModel.isPerformingAction`,
    ///     the single-flight guard the view model already applies uniformly
    ///     to Pause/Resume/Cancel/Stop. Emergency Stop deliberately ignores
    ///     this input — it must never be blanket-disabled because another
    ///     action is pending (epic #2518 acceptance criterion).
    ///   - pendingKinds: Mirrors `PrinterDetailViewModel.pendingRunActionKinds`
    ///     (issue #2522, Vasquez review finding). Every descriptor previously
    ///     used the `isPending` default of `false` unconditionally, so
    ///     `PrinterRunActionBar`'s own re-entrant-tap guard — which keys off
    ///     `isPending`, not `isEnabled` — never actually engaged, and
    ///     VoiceOver never announced a "Pending" value/hint on the specific
    ///     button genuinely in flight. Each descriptor now marks itself
    ///     `isPending` exactly when ITS OWN kind is a member of this set —
    ///     including Emergency Stop, whose `isEnabled` stays unconditionally
    ///     `true` (per the acceptance criterion above) but whose `isPending`
    ///     must still reflect its OWN in-flight state.
    static func presentation(
        isOnline: Bool,
        isPrinting: Bool,
        isPaused: Bool,
        isPerformingAction: Bool,
        pendingKinds: Set<PrinterRunActionKind> = []
    ) -> PrinterRunActionPresentation {
        var descriptors: [PrinterRunActionDescriptor] = []
        if isPrinting {
            descriptors.append(.init(
                kind: .pause, isEnabled: !isPerformingAction, isPending: pendingKinds.contains(.pause)
            ))
        }
        if isPaused {
            descriptors.append(.init(
                kind: .resume, isEnabled: !isPerformingAction, isPending: pendingKinds.contains(.resume)
            ))
        }
        if isPrinting || isPaused {
            descriptors.append(.init(
                kind: .cancel, isEnabled: !isPerformingAction, isPending: pendingKinds.contains(.cancel)
            ))
            if isOnline {
                descriptors.append(.init(
                    kind: .stop, isEnabled: !isPerformingAction, isPending: pendingKinds.contains(.stop)
                ))
            }
        }
        descriptors.append(.init(
            kind: .emergencyStop,
            isEnabled: isOnline,
            isPending: pendingKinds.contains(.emergencyStop),
            unavailableReason: isOnline ? nil : "Printer is offline. Use the physical safety switch if needed."
        ))
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
/// (capability gate > load error > feature-disabled > not-found > success >
/// loading) is unit-testable without a live, SignalR-wired view model.
///
/// `lastLoadError` takes precedence over EVERY other ViewModel-sourced flag,
/// not just `hasCoverage` (Hicks review findings 9 and 12). In the real
/// `PrinterFilamentCoverageViewModel`, `commitSuccess`, `commitFeatureDisabled`
/// and `commitNotFound` all clear the other two flags/`lastLoadError` on
/// commit, but `commitError` clears NONE of `isFeatureDisabled`/
/// `isPrinterNotFound` — it only sets `lastLoadError`. So whenever
/// `lastLoadError` is non-nil, it always reflects the single MOST RECENT
/// commit's outcome; `isFeatureDisabled`/`isPrinterNotFound` being still
/// `true` alongside it is leftover sticky state from an OLDER commit that a
/// later network failure did not (and structurally cannot) clear. Only
/// `featureEnabled` — the live external capability gate, not part of the
/// ViewModel's commit history — outranks a load error: that one genuinely
/// describes the current moment, not a stale commit.
///
/// Concretely: a printer whose feature was disabled and is later found to
/// have failed a refresh (disabled → network failure) must report
/// `.failed`, not `.disabled`; a printer that was not-found and later fails
/// a refresh (not-found → network failure) must also report `.failed`, not
/// `.unavailable`. Reporting the stale flag instead would tell the operator
/// the feature is off, or the printer doesn't exist, when the true, most
/// recent fact is simply "the last refresh attempt failed."
///
/// Retained `hasCoverage` alongside a load error is the remaining case this
/// precedence protects: reporting `.available` there would present stale,
/// unconfirmed data as current with every filament action left enabled.
/// Reporting `.failed` instead makes `PrinterFilamentPresentation` mark the
/// printer stale (its `isStale` computation treats any
/// non-`.available`/non-`.disabled` state with retained `coverage` as
/// stale), which renders the "Last confirmed" wording and empties
/// `supportedActions`.
enum PrinterDetailFilamentCoverageStateMapping {
    static func coverageState(
        featureEnabled: Bool,
        isFeatureDisabled: Bool,
        isPrinterNotFound: Bool,
        hasCoverage: Bool,
        lastLoadError: String?
    ) -> PrinterFilamentPresentation.CoverageState {
        guard featureEnabled else { return .disabled }
        if let lastLoadError { return .failed(lastLoadError) }
        if isFeatureDisabled { return .disabled }
        if isPrinterNotFound { return .unavailable }
        if hasCoverage { return .available }
        return .loading
    }
}

// MARK: - Controls owner mapping (issue #2522, Hicks review finding 15)

/// Pure decision for whether the persistent Controls owner
/// (`PrinterControlsViewModel`, hoisted above the pager in
/// `PrinterDetailView` per finding 10) should be built or replaced.
///
/// Construction is gated on `controlsAvailable` so a Overview-only visit — the
/// common case, since Advanced Printer Controls defaults off — never
/// dispatches a capability request nobody can reach. Once an owner exists
/// for the CURRENT printer, this always says "no" again even if
/// `controlsAvailable` is (still or again) `true` — an owner already built
/// must be retained across a later transition back to unavailable (offline,
/// or the safety toggle revoked) and forward again once more; this function
/// only ever authorizes a fresh build, never a teardown.
enum PrinterDetailControlsOwnerMapping {
    static func shouldBuildOwner(
        existingOwnerPrinterID: UUID?,
        printerID: UUID,
        controlsAvailable: Bool
    ) -> Bool {
        guard controlsAvailable else { return false }
        return existingOwnerPrinterID != printerID
    }
}

// MARK: - Filament staleness mapping (issue #2522, Hicks review finding 16
// — reverses the interim fix from Bishop review finding 6)

/// Whether #2519's `PrinterFilamentPresentation` should treat filament data
/// as stale (last-confirmed wording, no enabled mutation actions).
///
/// This is the RAW `PrinterFilamentCoverageViewModel.isShowingStaleCache`
/// flag, deliberately NOT ANDed with `hasConcludedCanonicalLoad`. An earlier
/// revision of this mapping required `hasConcludedCanonicalLoad` too (Bishop
/// review finding 6), reasoning that raw `isShowingStaleCache` would
/// wrongly disable every action during ordinary warm-cache hydration. That
/// traded one bug for a worse one (Hicks review finding 16): while a
/// canonical refresh is still in flight, the on-screen coverage is
/// UNCONFIRMED cached data, and mapping it to `.available` with enabled
/// mutation actions before the refresh concludes lets an operator act on
/// stale data as if it were current — a real safety issue, not merely a
/// premature-banner cosmetic one.
///
/// `isStaleCacheReportable` (`isShowingStaleCache && hasConcludedCanonicalLoad`)
/// stays reserved for the connection-status BANNER only (wired directly in
/// `PrinterDetailView`, unaffected by this mapping), whose job genuinely is
/// different: suppressing a premature "offline" flash on an entirely
/// healthy cold open, not gating mutation safety.
enum PrinterDetailFilamentStaleMapping {
    static func isStale(isShowingStaleCache: Bool) -> Bool {
        isShowingStaleCache
    }
}

// MARK: - Camera lifecycle mapping (issue #2522, Hicks review finding 19)

/// Pure mirror of `PrinterDetailView.isOverviewPageForeground`, extracted so
/// the exact gate deciding whether camera snapshot polling/the MJPEG live
/// stream may run is unit-testable without hosting a view.
///
/// Native `TabView` paging keeps the Overview page's `cameraSection` mounted
/// alongside Controls for swipe animation, so `scenePhase == .active` alone
/// (the pre-#2522 single-page screen's only gate) is no longer sufficient:
/// leaving Overview for Controls must stop the camera exactly the same way
/// backgrounding the app already did, so both conditions are required.
enum PrinterDetailCameraLifecycleMapping {
    static func isForeground(
        scenePhase: ScenePhase,
        selectedPanel: PrinterDetailPanel
    ) -> Bool {
        scenePhase == .active && selectedPanel == .overview
    }
}

/// Use the actual detail width, not the device family: an iPad split can be
/// narrower than a phone in landscape. Accessibility text uses one column.
enum PrinterDetailLayout {
    static func usesColumns(width: CGFloat, dynamicTypeSize: DynamicTypeSize) -> Bool {
        width >= 760 && !dynamicTypeSize.isAccessibilitySize
    }
}

struct PrinterDetailTemperatureReading: Equatable {
    let measured: Double?
    let target: Double?
    let isOnline: Bool

    var measuredText: String {
        guard isOnline, let measured, measured.isFinite else {
            return String(localized: "Unavailable")
        }
        return measured.temperatureFormatted
    }

    var targetText: String {
        guard isOnline, let target, target.isFinite else {
            return String(localized: "Unknown")
        }
        return target == 0 ? String(localized: "Off") : target.temperatureFormatted
    }
}
