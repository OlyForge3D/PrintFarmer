import SwiftUI

/// Printer detail Control tab: Move / Heat / Tune segments, with a position
/// readout on Move. Presentation only; the detail host owns the command model,
/// access gating and the pinned Emergency Stop.
struct PrinterInstrumentPanelControls: View {
    let printer: Printer
    @ObservedObject var viewModel: PrinterControlsViewModel
    @State private var selectedSegment: Segment = .move
    @State private var jogStep: Double = 1
    @State private var babystep: Double = 0.005
    @Environment(\.dynamicTypeSize) private var dynamicTypeSize
    @Environment(\.displayScale) private var displayScale

    enum Segment: String, CaseIterable, Identifiable {
        case move, heat, tune
        var id: String { rawValue }
        var title: String {
            switch self {
            case .move: "Move"
            case .heat: "Heat"
            case .tune: "Tune"
            }
        }
    }

    static let jogSteps: [Double] = [1, 10, 50]
    static let babystepOptions: [Double] = MaterialControlInput.babystepIncrements
    static let cornerRadius: CGFloat = 8

    /// Tune eligibility: printing or paused only. Transitional states such as
    /// `starting` cannot select Tune (#3496).
    static func isPrinting(state: String?) -> Bool {
        PrinterControlsViewModel.isRunningPrint(state: state)
    }

    /// Move and Heat stay locked for any active-print state, including `starting`.
    static func isLocked(state: String?) -> Bool {
        PrinterControlsViewModel.isPrintingOrPaused(state: state)
    }

    /// Tune shows heater targets only when the backend can apply them during a
    /// print; otherwise the block is omitted entirely (#3496).
    static func showsTuneTemperatures(isPrinting: Bool, heatDuringPrint: Bool) -> Bool {
        isPrinting && heatDuringPrint
    }

    /// iPad keeps the jog pad at a thumb-sized width, centred in the panel.
    static let maximumJogPadWidth: CGFloat = 480
    /// Shared Tune stepper value column so every row's − / + buttons line up.
    static let tuneValueColumnWidth: CGFloat = 64

    /// VoiceOver reads position values with their unit, e.g. "X 120.0 millimeters".
    static func readoutAccessibilityLabel(axis: String, value: Double?, homedAxes: String?) -> String {
        if JogSubgroup.isAxisHomed(axis, homedAxes: homedAxes) == false {
            return "\(axis) position unavailable, axis not homed"
        }
        guard let value, value.isFinite else { return "\(axis) position not reported" }
        let digits = readoutFractionDigits(axis: axis)
        return "\(axis) \(value.formatted(.number.precision(.fractionLength(digits)))) millimeters"
    }

    /// Move and Heat lock during a job (mockup B): presets would change
    /// temperatures mid-print, and Tune already carries the hotend/bed targets.
    /// Tune (speed, flow, babystep) only applies to a running or paused print
    /// (#3496), so it is disabled while idle.
    /// `isLocked` defaults to `isPrinting`; pass it separately for transitional
    /// states (`starting`) that lock Move/Heat without enabling Tune.
    static func isAvailable(_ segment: Segment, isPrinting: Bool, isLocked: Bool? = nil) -> Bool {
        switch segment {
        case .tune: isPrinting
        case .heat, .move: !(isLocked ?? isPrinting)
        }
    }

    static func resolvedSegment(_ selected: Segment, isPrinting: Bool, isLocked: Bool? = nil) -> Segment {
        isAvailable(selected, isPrinting: isPrinting, isLocked: isLocked) ? selected : (isPrinting ? .tune : .move)
    }

    /// Entering Control mid-print, or a print starting while it is open,
    /// selects Tune; when the print ends a Tune selection falls back to Move.
    static func selection(afterPrintingChange selected: Segment, isPrinting: Bool, isLocked: Bool? = nil) -> Segment {
        resolvedSegment(selected, isPrinting: isPrinting, isLocked: isLocked)
    }

    /// A transitional state (e.g. `starting`) disables every segment; a short
    /// label names the state without explanatory text (#3496 B10).
    static func transitionalStateLabel(isPrinting: Bool, isLocked: Bool) -> String? {
        isLocked && !isPrinting ? "Starting…" : nil
    }

    static func unavailableHint(_ segment: Segment) -> String {
        segment == .tune ? "Available while printing" : "Unavailable while printing"
    }

    static func babystepText(_ offset: Double?) -> String {
        guard let number = babystepNumberText(offset) else { return PrinterDetailViewModel.missingValuePlaceholder }
        return "\(number) mm"
    }

    /// Step chip labels drop trailing zeros: 0.005, 0.01, 0.025, 0.05.
    static func babystepOptionLabel(_ step: Double) -> String {
        step.formatted(.number.precision(.fractionLength(0...3)))
    }

    static func babystepAccessibilityLabel(_ offset: Double?) -> String {
        guard babystepNumberText(offset) != nil else { return "Z offset not reported" }
        return "Z offset \(babystepText(offset))"
    }

    static func babystepNumberText(_ offset: Double?) -> String? {
        guard let offset, offset.isFinite else { return nil }
        return offset.formatted(.number.precision(.fractionLength(3)).sign(strategy: .always(includingZero: false)))
    }

    /// Z is shown to 0.01 mm so babystep and layer-height changes are visible.
    static func readoutFractionDigits(axis: String) -> Int {
        axis == "Z" ? 2 : 1
    }

    private var isPrinting: Bool {
        Self.isPrinting(state: printer.state) || viewModel.isRunningPrint
    }

    private var isLocked: Bool {
        Self.isLocked(state: printer.state) || viewModel.isPrintingOrPaused
    }

    private var segment: Segment {
        Self.resolvedSegment(selectedSegment, isPrinting: isPrinting, isLocked: isLocked)
    }

    var body: some View {
        if !PrinterControlsSection.isHidden(for: printer) {
            VStack(alignment: .leading, spacing: 12) {
                loadIssues
                if !isLocked, let reason = viewModel.blockedReason {
                    Label(reason, systemImage: "lock.fill")
                        .font(.footnote)
                        .foregroundStyle(Color.pfTextPrimary)
                        .accessibilityElement(children: .combine)
                        .accessibilityLabel(reason)
                        .accessibilityIdentifier("printer.controls.blocked-reason")
                }
                if let label = Self.transitionalStateLabel(isPrinting: isPrinting, isLocked: isLocked) {
                    Text(label)
                        .font(.subheadline.weight(.semibold))
                        .foregroundStyle(Color.pfTextSecondary)
                        .accessibilityIdentifier("printer.controls.state-label")
                }
                segmentPicker
                Group {
                    switch segment {
                    case .move: movePanel
                    case .heat: heatPanel
                    case .tune: tunePanel
                    }
                }
                .frame(maxWidth: .infinity, alignment: .leading)
                .padding(12)
                .background(Color.pfBackground, in: RoundedRectangle(cornerRadius: Self.cornerRadius))
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            .foregroundStyle(Color.pfTextPrimary)
            .environment(\.controlActionCornerRadius, Self.cornerRadius)
            .environment(\.controlActionSelectedFill, Color.pfSegmentSelected)
            .environment(\.preheatPresetsFillRow, true)
            .accessibilityElement(children: .contain)
            .accessibilityIdentifier("printer.controls.instrument-panel")
            .onChange(of: isPrinting, initial: true) { _, printing in
                selectedSegment = Self.selection(
                    afterPrintingChange: selectedSegment, isPrinting: printing, isLocked: isLocked
                )
            }
        }
    }

    // MARK: - Load issues

    @ViewBuilder
    private var loadIssues: some View {
        if viewModel.needsHeaterLimits {
            VStack(alignment: .leading, spacing: 8) {
                Text(viewModel.isLoadingHardware ? "Loading heater limits…" : "Heater limits unavailable")
                    .font(.headline)
                Text(viewModel.hardwareLoadError ?? "Reported heater limits are unavailable. Commands remain subject to firmware limits.")
                    .font(.footnote)
                ControlActionButton(title: "Retry heater limits", identifier: "printer.controls.retry-limits") {
                    Task { await viewModel.loadHardware() }
                }
                .disabled(viewModel.isLoadingHardware || viewModel.isLoadingCapabilities)
            }
        }
        if let error = viewModel.capabilityLoadError {
            VStack(alignment: .leading, spacing: 8) {
                Text("Control capabilities unavailable")
                    .font(.headline)
                Text(error)
                    .font(.footnote)
                ControlActionButton(title: "Retry capability check") {
                    Task { await viewModel.loadCapabilities() }
                }
                .disabled(viewModel.isLoadingCapabilities)
            }
        }
    }

    // MARK: - Position readout

    /// The Move readout is hidden only when the printer reports no position at
    /// all; unhomed or unreported axes show `readoutPlaceholder` (#3496).
    static func showsPositionReadout(x: Double?, y: Double?, z: Double?, homedAxes: String?) -> Bool {
        [x, y, z].contains { $0?.isFinite == true }
    }

    static let readoutPlaceholder = "\u{2014}"

    static func readoutText(axis: String, value: Double?, homedAxes: String?) -> String {
        let text = JogSubgroup.positionDisplay(
            axis: axis, value: value, homedAxes: homedAxes, fractionDigits: readoutFractionDigits(axis: axis)
        ).text
        return text == PrinterDetailViewModel.missingValuePlaceholder ? readoutPlaceholder : text
    }

    /// One compact row at every text size, capped like the jog pad so it
    /// never pushes the pad below the fold (#3496 N02/N03).
    private var positionReadout: some View {
        HStack(alignment: .center, spacing: 8) {
            readoutColumn("X", value: viewModel.printer.x)
            readoutDivider()
            readoutColumn("Y", value: viewModel.printer.y)
            readoutDivider()
            readoutColumn("Z", value: viewModel.printer.z)
        }
        .padding(.vertical, 6)
        .dynamicTypeSize(...DynamicTypeSize.xxxLarge)
        .background(Color.pfBackgroundTertiary, in: RoundedRectangle(cornerRadius: Self.cornerRadius))
        .overlay { RoundedRectangle(cornerRadius: Self.cornerRadius).strokeBorder(Color.pfBorder) }
        .frame(maxWidth: Self.maximumJogPadWidth)
        .frame(maxWidth: .infinity)
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier("printer.controls.position")
    }

    private func readoutColumn(_ axis: String, value: Double?) -> some View {
        VStack(alignment: .center, spacing: 2) {
            Text(axis)
                .font(.caption.weight(.semibold))
                .foregroundStyle(Color.pfTextSecondary)
            Text(Self.readoutText(axis: axis, value: value, homedAxes: viewModel.printer.homedAxes))
                .font(.title3.monospacedDigit())
                .lineLimit(1)
                .minimumScaleFactor(0.7)
        }
        .frame(maxWidth: .infinity, alignment: .center)
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(Self.readoutAccessibilityLabel(
            axis: axis, value: value, homedAxes: viewModel.printer.homedAxes
        ))
        .accessibilityIdentifier("printer.controls.position.\(axis.lowercased())")
    }

    private func readoutDivider() -> some View {
        Rectangle()
            .fill(Color.pfBorder)
            .frame(width: 1 / displayScale, height: 32)
            .accessibilityHidden(true)
    }

    // MARK: - Segments

    /// Accessibility sizes stack the segments as full-width choices so
    /// "Move" never wraps mid-word (#3496 N01).
    private var segmentPicker: some View {
        let layout = dynamicTypeSize.isAccessibilitySize
            ? AnyLayout(VStackLayout(spacing: 2))
            : AnyLayout(HStackLayout(spacing: 2))
        return layout {
            ForEach(Segment.allCases) { option in
                let available = Self.isAvailable(option, isPrinting: isPrinting, isLocked: isLocked)
                ControlActionButton(
                    title: option.title,
                    identifier: "printer.controls.segment.\(option.rawValue)",
                    selected: segment == option,
                    compact: true,
                    segmented: true,
                    minimumHeight: 44
                ) { selectedSegment = option }
                .frame(maxWidth: .infinity)
                .disabled(!available)
                .accessibilityHint(available ? "" : Self.unavailableHint(option))
                .accessibilityAddTraits(segment == option ? .isSelected : [])
            }
        }
        .padding(2)
        .background(Color.pfBackgroundTertiary, in: RoundedRectangle(cornerRadius: Self.cornerRadius))
        .overlay { RoundedRectangle(cornerRadius: Self.cornerRadius).strokeBorder(Color.pfBorder) }
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier("printer.controls.segments")
    }

    // MARK: - Move

    static let moveEmptyStateSymbol = "arrow.up.and.down.and.arrow.left.and.right"

    private var movePanel: some View {
        VStack(alignment: .leading, spacing: 12) {
            if Self.showsPositionReadout(
                x: viewModel.printer.x, y: viewModel.printer.y, z: viewModel.printer.z,
                homedAxes: viewModel.printer.homedAxes
            ) {
                positionReadout
            }
            PrinterControlCommandFeedback(viewModel: viewModel, section: .motion)
            if JogSubgroup.isHidden(for: viewModel.capabilities) {
                HStack(spacing: 8) {
                    Image(systemName: Self.moveEmptyStateSymbol)
                        .font(.subheadline)
                        .foregroundStyle(Color.pfTextTertiary)
                        .accessibilityHidden(true)
                    Text(viewModel.capabilities == nil ? "Checking motion support…" : "Motion controls are unavailable on this printer.")
                        .font(.footnote)
                        .foregroundStyle(Color.pfTextSecondary)
                        .fixedSize(horizontal: false, vertical: true)
                }
                .accessibilityElement(children: .combine)
                .accessibilityIdentifier("printer.controls.motion-empty")
            } else {
                HStack(alignment: .top, spacing: 12) {
                    xyPad
                    zColumn.frame(width: 72)
                }
                .frame(maxWidth: Self.maximumJogPadWidth)
                .frame(maxWidth: .infinity)
                .dynamicTypeSize(dynamicTypeSize.isAccessibilitySize ? .xxxLarge : dynamicTypeSize)
                stepSelector(
                    title: "Jog distance (mm)",
                    options: Self.jogSteps,
                    selection: $jogStep,
                    identifier: { "printer.controls.jog.step.\($0.formatted())" },
                    label: { $0.formatted() },
                    accessibilityLabel: { "Jog distance \($0.formatted()) millimeters" }
                )
                .disabled(!viewModel.canControl || viewModel.isExecuting)
            }
        }
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier("printer.controls.motion-group")
    }

    private var xyPad: some View {
        Grid(horizontalSpacing: 6, verticalSpacing: 6) {
            GridRow {
                home("All", axes: ["X", "Y", "Z"], accessibilityTitle: "Home all axes") { await viewModel.homeAll() }
                jog("Y", sign: 1, symbol: "chevron.up")
                Color.clear.frame(height: 44).accessibilityHidden(true)
            }
            GridRow {
                jog("X", sign: -1, symbol: "chevron.left")
                home("XY", axes: ["X", "Y"], accessibilityTitle: "Home XY") { await viewModel.homeXY() }
                jog("X", sign: 1, symbol: "chevron.right")
            }
            GridRow {
                Color.clear.frame(height: 44).accessibilityHidden(true)
                jog("Y", sign: -1, symbol: "chevron.down")
                Color.clear.frame(height: 44).accessibilityHidden(true)
            }
        }
        .frame(maxWidth: .infinity)
    }

    private var zColumn: some View {
        VStack(spacing: 6) {
            jog("Z", sign: 1, symbol: "chevron.up", title: "Z")
            home("Z", axes: ["Z"], accessibilityTitle: "Home Z") { await viewModel.homeZ() }
            jog("Z", sign: -1, symbol: "chevron.down", title: "Z")
        }
    }

    private func jog(_ axis: String, sign: Double, symbol: String, title: String = "") -> some View {
        let available = viewModel.capabilities?.supportsMovement == true
            && JogSubgroup.visibleAxes(for: viewModel.capabilities).contains(axis)
        let direction = sign > 0 ? "positive" : "negative"
        let distance = sign * jogStep
        let pending = viewModel.pendingCommand?.kind == .jog(axis: axis, distanceMm: distance)
        return ControlActionButton(
            title: title,
            identifier: "printer.controls.jog.\(axis.lowercased()).\(direction)",
            accessibilityTitle: "Move \(axis) \(direction)",
            hint: available ? "Moves \(jogStep.formatted()) millimeters." : "\(axis) movement is unavailable.",
            compact: true, systemImage: symbol, value: pending ? "Pending" : nil,
            textSize: 13, minimumHeight: 44, isPending: pending
        ) { Task { await viewModel.jog(axis: axis, distanceMm: distance) } }
        .frame(maxWidth: .infinity, minHeight: 44)
        .disabled(!available || !viewModel.canControl || viewModel.isExecuting)
    }

    private func home(
        _ label: String, axes: [String], accessibilityTitle: String,
        action: @escaping @MainActor () async -> Void
    ) -> some View {
        let available = viewModel.capabilities?.supportsHome(axes: axes) == true
        let pending = viewModel.pendingCommand?.kind == .home(axes: axes)
        return ControlActionButton(
            title: label,
            identifier: "printer.controls.home.\(label.lowercased())",
            accessibilityTitle: accessibilityTitle,
            hint: available ? "Homes \(axes.joined(separator: ", "))." : "This homing operation is unavailable.",
            compact: true, systemImage: "house", value: pending ? "Pending" : nil,
            textSize: 12, tinted: true, minimumHeight: 44, isPending: pending
        ) { Task { await action() } }
        .frame(maxWidth: .infinity, minHeight: 44)
        .disabled(!available || !viewModel.canControl || viewModel.isExecuting)
    }

    // MARK: - Heat

    private var heatPanel: some View {
        VStack(alignment: .leading, spacing: 12) {
            PreheatSubgroup(viewModel: viewModel, usesSteppers: true, compactForDetail: true)
            // Writable sensors stay reachable while idle; Tune carries them mid-print.
            PrinterSensorControlRows(
                    viewModel: viewModel, compact: true, valueColumnWidth: Self.tuneValueColumnWidth
                )
        }
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier("printer.controls.heat-group")
    }

    // MARK: - Tune

    @ViewBuilder
    private var babystepReadout: some View {
        if let number = Self.babystepNumberText(viewModel.liveZOffsetMm) {
            let value = Text(number)
                .font(.system(.title3, design: .monospaced).weight(.medium))
                .foregroundStyle(Color.pfTextPrimary)
            let unit = Text("mm")
                .font(.system(.footnote, design: .monospaced))
                .foregroundStyle(Color.pfTextSecondary)
            Text("\(value) \(unit)")
                .lineLimit(1)
                .minimumScaleFactor(0.6)
        } else {
            Text(PrinterDetailViewModel.missingValuePlaceholder)
                .font(.system(.title3, design: .monospaced).weight(.medium))
                .foregroundStyle(Color.pfTextSecondary)
        }
    }

    /// Mockup B puts the live offset on the "Z babystep" header row.
    private var babystepHeaderRow: some View {
        EssentialControlHeading(title: "Z babystep") {
            babystepReadout
                .accessibilityLabel(Self.babystepAccessibilityLabel(viewModel.liveZOffsetMm))
                .accessibilityIdentifier("printer.controls.runtime.z-offset.value")
        }
    }

    private var tunePanel: some View {
        let reason = viewModel.zOffsetUnavailableReason
        let canAdjust = reason == nil && viewModel.canAdjustRuntimeControls
        return VStack(alignment: .leading, spacing: 12) {
            PrinterControlCommandFeedback(viewModel: viewModel, section: .runtime)
            runtimeFactorRows
            babystepHeaderRow
            if let reason {
                Text(reason)
                    .font(.footnote)
                    .foregroundStyle(Color.pfTextSecondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
            stepSelector(
                title: "Babystep (mm)",
                options: Self.babystepOptions,
                selection: $babystep,
                identifier: { "printer.controls.runtime.z-offset.step.\($0.formatted())" },
                label: { Self.babystepOptionLabel($0) },
                accessibilityLabel: { "Babystep \($0.formatted()) millimeters" },
                capsWidth: false
            )
            let buttons = dynamicTypeSize.isAccessibilitySize
                ? AnyLayout(VStackLayout(spacing: 8))
                : AnyLayout(HStackLayout(spacing: 8))
            buttons {
                babystepButton(raise: true, reason: reason).disabled(!canAdjust)
                babystepButton(raise: false, reason: reason).disabled(!canAdjust)
            }
            if Self.showsTuneTemperatures(isPrinting: isPrinting, heatDuringPrint: viewModel.supportsHeatDuringPrint) {
                Divider()
                tuneTemperatures
            }
            PrinterSensorControlRows(
                    viewModel: viewModel, compact: true, valueColumnWidth: Self.tuneValueColumnWidth
                )
        }
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier("printer.controls.runtime")
    }

    /// Hotend and bed target steppers, shown only when the backend can apply
    /// them during a print. No caption: disabled rows speak for themselves.
    private var tuneTemperatures: some View {
        VStack(alignment: .leading, spacing: 8) {
            PrinterControlCommandFeedback(viewModel: viewModel, section: .heat)
            PreheatSubgroup.IndividualHeaterControls(
                viewModel: viewModel, usesSteppers: true, compactForDetail: true, allowsActivePrint: true
            )
        }
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier("printer.controls.runtime.temperatures")
    }

    /// Mockup B: Speed and Flow percentage steppers above babystep. Each is
    /// shown only when the backend confirms support for that factor.
    @ViewBuilder
    private var runtimeFactorRows: some View {
        let factors = RuntimeFactor.allCases.filter(viewModel.supportsRuntimeFactor)
        if !factors.isEmpty {
            VStack(alignment: .leading, spacing: 8) {
                ForEach(factors) { runtimeFactorRow($0) }
            }
            .accessibilityElement(children: .contain)
            .accessibilityIdentifier("printer.controls.runtime.factors")
        }
    }

    private func runtimeFactorRow(_ factor: RuntimeFactor) -> some View {
        let value = viewModel.runtimeFactorPercent(factor)
        let reason = viewModel.runtimeFactorUnavailableReason(factor)
        let identifier = "printer.controls.runtime.\(factor.rawValue)-factor"
        let stepper = HStack(spacing: 4) {
            runtimeFactorButton(factor, delta: -RuntimeFactor.stepPercent, reason: reason)
            let renderedValue = Self.runtimeFactorText(value)
            Text(renderedValue)
                .font(.body.monospacedDigit().weight(.medium))
                .foregroundStyle(Color.pfTextPrimary)
                .frame(minWidth: Self.tuneValueColumnWidth)
                .accessibilityLabel(Self.runtimeFactorAccessibilityLabel(factor, value))
                .uiTestRenderedValue(renderedValue)
                .accessibilityIdentifier("\(identifier).value")
            runtimeFactorButton(factor, delta: RuntimeFactor.stepPercent, reason: reason)
        }
        let layout = dynamicTypeSize.isAccessibilitySize
            ? AnyLayout(VStackLayout(alignment: .leading, spacing: 6))
            : AnyLayout(HStackLayout(spacing: 8))
        return VStack(alignment: .leading, spacing: 4) {
            layout {
                Text(factor.title)
                    .font(.subheadline.weight(.medium))
                    .foregroundStyle(Color.pfTextPrimary)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .accessibilityAddTraits(.isHeader)
                stepper
            }
            .frame(minHeight: 48)
        }
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier(identifier)
    }

    private func runtimeFactorButton(_ factor: RuntimeFactor, delta: Double, reason: String?) -> some View {
        let increase = delta > 0
        let step = RuntimeFactor.stepPercent.formatted()
        let pending = Self.isRuntimeFactorPending(
            viewModel.pendingCommand?.kind, factor: factor, increase: increase,
            current: viewModel.runtimeFactorPercent(factor)
        )
        return ControlActionButton(
            title: "",
            identifier: "printer.controls.runtime.\(factor.rawValue)-factor.\(increase ? "increase" : "decrease")",
            accessibilityTitle: "\(increase ? "Increase" : "Decrease") \(factor.title.lowercased()) by \(step) percent",
            hint: reason ?? "Sends a new \(factor.title.lowercased()) percentage to the printer.",
            compact: true,
            systemImage: increase ? "plus" : "minus",
            value: pending ? "Pending" : nil,
            minimumHeight: 44,
            isPending: pending
        ) { Task { await viewModel.stepRuntimeFactor(factor, by: delta) } }
        .frame(minWidth: 44, minHeight: 44)
        .fixedSize()
        .disabled(!viewModel.canStepRuntimeFactor(factor, by: delta))
    }

    /// The in-flight runtime-factor request is shown on the stepper button that
    /// sent it: a target above the current readback came from "+".
    static func isRuntimeFactorPending(
        _ kind: ControlCommand.Kind?, factor: RuntimeFactor, increase: Bool, current: Double?
    ) -> Bool {
        guard case let .runtimeFactor(pendingFactor, percent) = kind, pendingFactor == factor else { return false }
        guard let current, current.isFinite else { return increase }
        return (percent > current) == increase
    }

    static func isBabystepPending(_ kind: ControlCommand.Kind?, raise: Bool) -> Bool {
        guard case let .transientZOffset(_, deltaMm) = kind else { return false }
        return (deltaMm > 0) == raise
    }

    static func runtimeFactorText(_ percent: Double?) -> String {
        guard let percent, percent.isFinite else { return PrinterDetailViewModel.missingValuePlaceholder }
        return "\(percent.formatted(.number.precision(.fractionLength(0...2))))%"
    }

    static func runtimeFactorAccessibilityLabel(_ factor: RuntimeFactor, _ percent: Double?) -> String {
        guard let percent, percent.isFinite else { return "\(factor.title) not reported" }
        return "\(factor.title) \(percent.formatted(.number.precision(.fractionLength(0...2)))) percent"
    }

    private func babystepButton(raise: Bool, reason: String?) -> some View {
        let pending = Self.isBabystepPending(viewModel.pendingCommand?.kind, raise: raise)
        return ControlActionButton(
            title: raise ? "Raise nozzle" : "Lower nozzle",
            identifier: "printer.controls.runtime.z-offset.\(raise ? "increase" : "decrease")",
            accessibilityTitle: raise ? "Raise nozzle" : "Lower nozzle",
            hint: reason ?? "Moves the live Z offset \(babystep.formatted()) millimeters.",
            compact: true,
            systemImage: raise ? "arrow.up" : "arrow.down",
            value: pending ? "Pending" : nil,
            minimumHeight: 44,
            isPending: pending
        ) { Task { await viewModel.adjustLiveZOffset(by: raise ? babystep : -babystep) } }
        .frame(maxWidth: .infinity, minHeight: 44)
    }

    // MARK: - Shared

    private func stepSelector(
        title: String,
        options: [Double],
        selection: Binding<Double>,
        identifier: @escaping (Double) -> String,
        label: @escaping (Double) -> String,
        accessibilityLabel: @escaping (Double) -> String,
        capsWidth: Bool = true
    ) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            Text(title)
                .font(.caption2.weight(.semibold))
                .foregroundStyle(Color.pfTextSecondary)
                .accessibilityAddTraits(.isHeader)
            HStack(spacing: 2) {
                ForEach(options, id: \.self) { value in
                    ControlActionButton(
                        title: label(value),
                        identifier: identifier(value),
                        accessibilityTitle: accessibilityLabel(value),
                        selected: selection.wrappedValue == value,
                        compact: true, textSize: 14, segmented: true, minimumHeight: 44
                    ) { selection.wrappedValue = value }
                    .accessibilityAddTraits(selection.wrappedValue == value ? .isSelected : [])
                }
            }
            .padding(2)
            .background(Color.pfBackgroundTertiary, in: RoundedRectangle(cornerRadius: Self.cornerRadius))
        }
        .dynamicTypeSize(...DynamicTypeSize.xxxLarge)
        // Move caps to the jog pad; Tune rows span the full panel width.
        .frame(maxWidth: capsWidth ? Self.maximumJogPadWidth : .infinity)
        .frame(maxWidth: .infinity)
    }
}

private extension View {
    /// UI-test harness only: exposes the visible text as the accessibility value,
    /// because the spoken `accessibilityLabel` masks it from XCUI. Normal launches
    /// keep VoiceOver output unchanged.
    @ViewBuilder
    func uiTestRenderedValue(_ text: String) -> some View {
        #if DEBUG
        if UITestBootstrap.isEnabled {
            accessibilityValue(text)
        } else {
            self
        }
        #else
        self
        #endif
    }
}
