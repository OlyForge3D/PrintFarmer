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
    var printer: Printer? = nil
    @ViewBuilder let overview: () -> Overview
    @ViewBuilder let controls: () -> Controls
    @Environment(\.dynamicTypeSize) private var dynamicTypeSize

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
        GeometryReader { geometry in
            let inset: CGFloat = PrinterDetailLayout.usesColumns(
                width: geometry.size.width, dynamicTypeSize: dynamicTypeSize
            ) ? 24 : 16
            VStack(spacing: 0) {
                VStack(alignment: .leading, spacing: 16) {
                    if let printer {
                        if dynamicTypeSize.isAccessibilitySize {
                            ScrollView {
                                PrinterDetailIdentityHeader(printer: printer)
                            }
                            .frame(height: geometry.size.height / 4)
                            .accessibilityIdentifier("printer.detail.identity.scroll")
                        } else {
                            PrinterDetailIdentityHeader(printer: printer)
                        }
                    }
                    PrinterDetailPanelPicker(selection: $selection)
                    .frame(maxWidth: dynamicTypeSize.isAccessibilitySize ? .infinity : 380)
                }
                .frame(maxWidth: .infinity, alignment: .leading)
                .padding(.horizontal, inset)
                .padding(.top, 12)
                .padding(.bottom, 16)
                .background(Color.pfBackground)

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
        }
        .onChange(of: controlsAvailable) { _, newValue in
            selection = Self.resolvedSelection(current: selection, controlsAvailable: newValue)
        }
    }

    private struct PrinterDetailPanelPicker: View {
        @Binding var selection: PrinterDetailPanel
        @ScaledMetric(relativeTo: .subheadline) private var fontSize: CGFloat = 15

        private var minimumWidth: CGFloat {
            let titleWidth = PrinterDetailPanel.allCases.flatMap { panel in
                [UIFont.Weight.regular, .semibold].map { weight in
                    (panel.title as NSString).size(withAttributes: [
                        .font: UIFont.systemFont(ofSize: fontSize, weight: weight)
                    ]).width
                }
            }.max() ?? 0
            return CGFloat(PrinterDetailPanel.allCases.count) * (ceil(titleWidth) + 32)
        }

        var body: some View {
            ViewThatFits(in: .horizontal) {
                NativePrinterDetailPanelPicker(
                    selection: $selection, fontSize: fontSize, minimumWidth: minimumWidth
                )
                .frame(minWidth: minimumWidth)

                VStack(spacing: 3) {
                    ForEach(PrinterDetailPanel.allCases, id: \.self) { panel in
                        ControlActionButton(
                            title: panel.title,
                            identifier: "printer.detail.panel.select.\(panel.rawValue)",
                            selected: selection == panel,
                            textSize: 15, segmented: true
                        ) {
                            selection = panel
                        }
                    }
                }
                .padding(3)
                .background(Color.pfBackgroundTertiary, in: RoundedRectangle(cornerRadius: 11))
                .accessibilityElement(children: .contain)
                .accessibilityIdentifier("printer.detail.panel.selector")
                .accessibilityLabel("Printer detail panel")
            }
        }
    }

    private struct NativePrinterDetailPanelPicker: UIViewRepresentable {
        @Binding var selection: PrinterDetailPanel
        let fontSize: CGFloat
        let minimumWidth: CGFloat

        func makeUIView(context: Context) -> UISegmentedControl {
            let control = UISegmentedControl(items: PrinterDetailPanel.allCases.map(\.title))
            control.addTarget(context.coordinator, action: #selector(Coordinator.selectPanel), for: .valueChanged)
            control.accessibilityIdentifier = "printer.detail.panel.selector"
            control.accessibilityLabel = "Printer detail panel"
            return control
        }

        func updateUIView(_ control: UISegmentedControl, context: Context) {
            context.coordinator.selection = $selection
            control.selectedSegmentIndex = PrinterDetailPanel.allCases.firstIndex(of: selection) ?? 0
            control.setTitleTextAttributes([.font: UIFont.systemFont(ofSize: fontSize)], for: .normal)
            control.setTitleTextAttributes([.font: UIFont.systemFont(ofSize: fontSize, weight: .semibold)], for: .selected)
        }

        func sizeThatFits(_ proposal: ProposedViewSize, uiView: UISegmentedControl, context: Context) -> CGSize? {
            CGSize(width: max(minimumWidth, proposal.width ?? minimumWidth), height: max(48, ceil(fontSize * 1.2) + 20))
        }

        func makeCoordinator() -> Coordinator { Coordinator(selection: $selection) }

        final class Coordinator: NSObject {
            var selection: Binding<PrinterDetailPanel>
            init(selection: Binding<PrinterDetailPanel>) { self.selection = selection }

            @objc func selectPanel(_ control: UISegmentedControl) {
                guard PrinterDetailPanel.allCases.indices.contains(control.selectedSegmentIndex) else {
                    assertionFailure("Printer detail panel selection must name a displayed segment")
                    return
                }
                selection.wrappedValue = PrinterDetailPanel.allCases[control.selectedSegmentIndex]
            }
        }
    }

    struct PrinterDetailIdentityHeader: View {
        let printer: Printer
        @Environment(\.dynamicTypeSize) private var dynamicTypeSize

        private var status: String {
            guard printer.isOnline else { return "Offline" }
            guard let state = printer.state, !state.isEmpty else { return "Unknown" }
            return state.capitalized
        }

        private var statusColor: Color {
            guard printer.isOnline else { return .pfTextSecondary }
            switch printer.state?.lowercased() {
            case "printing", "idle", "ready": return .pfSuccess
            case "paused": return .pfWarning
            case "error": return .pfError
            default: return .pfTextSecondary
            }
        }

        private var subtitle: String {
            [printer.manufacturerName, printer.modelName, printer.location?.name]
                .compactMap { $0 }
                .filter { !$0.isEmpty }
                .joined(separator: " · ")
        }

        var body: some View {
            let layout = dynamicTypeSize.isAccessibilitySize
                ? AnyLayout(VStackLayout(alignment: .leading, spacing: 8))
                : AnyLayout(HStackLayout(alignment: .center, spacing: 12))
            layout {
                VStack(alignment: .leading, spacing: 4) {
                    Text(printer.name)
                        .font(.title2.weight(.semibold))
                        .foregroundStyle(Color.pfTextPrimary)
                        .fixedSize(horizontal: false, vertical: true)
                        .accessibilityAddTraits(.isHeader)
                        .accessibilityLabel("\(printer.name), printer detail")
                        .accessibilityIdentifier("printer.detail.destination.\(printer.id.uuidString.lowercased())")
                    if !subtitle.isEmpty {
                        Text(subtitle)
                            .font(.subheadline)
                            .foregroundStyle(Color.pfTextSecondary)
                            .fixedSize(horizontal: false, vertical: true)
                    }
                }
                .frame(maxWidth: .infinity, alignment: .leading)
                VStack(alignment: dynamicTypeSize.isAccessibilitySize ? .leading : .trailing, spacing: 6) {
                    HStack(spacing: 6) {
                        Circle().fill(statusColor).frame(width: 6, height: 6)
                            .accessibilityHidden(true)
                        Text(status)
                            .font(.caption)
                            .foregroundStyle(statusColor)
                        if printer.obicoEnabled {
                            Image(systemName: "shield.checkered")
                                .accessibilityLabel("Failure detection enabled")
                        }
                    }
                    if printer.inMaintenance {
                        Text("Maintenance")
                            .font(.caption)
                            .foregroundStyle(Color.pfTextPrimary)
                    }
                }
            }
            .accessibilityElement(children: .contain)
            .accessibilityIdentifier("printer.detail.identity")
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

struct PrinterDetailTemperatureStrip: View {
    let hotend: PrinterDetailTemperatureReading
    let bed: PrinterDetailTemperatureReading
    var showsBed = true
    var identifier = "printer.detail.temperatures"
    var essentialControls = false
    @Environment(\.dynamicTypeSize) private var dynamicTypeSize
    @ScaledMetric(relativeTo: .title2) private var essentialReadingSize: CGFloat = 24

    var body: some View {
        if essentialControls {
            essentialStrip
        } else {
            originalStrip
        }
    }

    private var originalStrip: some View {
        let layout = dynamicTypeSize.isAccessibilitySize
            ? AnyLayout(VStackLayout(alignment: .leading, spacing: 12))
            : AnyLayout(HStackLayout(alignment: .top, spacing: 12))
        return layout {
            reading(title: "Hotend", value: hotend)
            if showsBed { reading(title: "Bed", value: bed) }
        }
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier(identifier)
    }

    private var essentialStrip: some View {
        let layout = dynamicTypeSize.isAccessibilitySize
            ? AnyLayout(VStackLayout(alignment: .leading, spacing: 16))
            : AnyLayout(HStackLayout(alignment: .center, spacing: 0))
        return layout {
            essentialReading(title: "Hotend", symbol: "thermometer", value: hotend)
            if showsBed {
                if dynamicTypeSize.isAccessibilitySize {
                    Divider().padding(.horizontal, 18)
                } else {
                    Rectangle().fill(Color.pfBorder).frame(width: 1, height: 72)
                        .accessibilityHidden(true)
                }
                essentialReading(title: "Bed", symbol: "square.3.layers.3d", value: bed)
            }
        }
        .padding(.vertical, 16)
        .frame(minHeight: dynamicTypeSize.isAccessibilitySize ? nil : 108)
        .background(Color.pfBackground, in: RoundedRectangle(cornerRadius: 16))
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier(identifier)
    }

    private func essentialReading(title: String, symbol: String, value: PrinterDetailTemperatureReading) -> some View {
        // Match the web UI: colorize the heater glyph itself when the
        // target is on, rather than a separate "Heater off"/"Target set"
        // caption string.
        let isHeating = value.isOnline && (value.target ?? 0) > 0
        let glyphColor: Color = title == "Bed"
            ? (isHeating ? .blue : .blue.opacity(0.35))
            : (isHeating ? .red : .red.opacity(0.35))
        return VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: 4) {
                Image(systemName: symbol)
                    .foregroundStyle(glyphColor)
                Text(title)
                    .foregroundStyle(Color.pfTextSecondary)
            }
            .font(.footnote)
            .padding(.bottom, 5)
            if dynamicTypeSize.isAccessibilitySize {
                Text(value.measuredText).font(.system(size: essentialReadingSize)).monospacedDigit()
                Text("Target: \(value.targetText)").font(.footnote).monospacedDigit()
                    .foregroundStyle(Color.pfTextSecondary)
            } else {
                (
                    Text(compactTemperature(value.measured, isOnline: value.isOnline))
                        .font(.system(size: essentialReadingSize))
                    + Text(" / " + compactTemperature(value.target, isOnline: value.isOnline))
                        .font(.footnote).foregroundColor(.pfTextSecondary)
                )
                .monospacedDigit()
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(.horizontal, 18)
        .accessibilityElement(children: .ignore)
        .accessibilityLabel("\(title), measured \(value.measuredText), target \(value.targetText)")
    }

    private func compactTemperature(_ value: Double?, isOnline: Bool) -> String {
        guard isOnline, let value, value.isFinite else { return "—" }
        return "\(value.formatted(.number.precision(.fractionLength(0...1))))°"
    }

    private func reading(title: String, value: PrinterDetailTemperatureReading) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(title)
                .font(.subheadline.weight(.medium))
                .foregroundStyle(Color.pfTextSecondary)
            Text(value.measuredText)
                .font(.title3.monospacedDigit().weight(.semibold))
            Text("Target: \(value.targetText)")
                .font(.caption.monospacedDigit())
                .foregroundStyle(Color.pfTextSecondary)
        }
        .fixedSize(horizontal: false, vertical: true)
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(12)
        .background(Color.pfCard, in: RoundedRectangle(cornerRadius: 12))
        .accessibilityElement(children: .ignore)
        .accessibilityLabel("\(title), measured \(value.measuredText), target \(value.targetText)")
    }
}
