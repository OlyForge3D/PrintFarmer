import SwiftUI

/// Printer detail Control tab: a position readout over Move / Heat / Tune
/// segments. Presentation only; the detail host owns the command model, access
/// gating and the pinned Emergency Stop.
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

    static func isPrinting(state: String?) -> Bool {
        PrinterControlsViewModel.isPrintingOrPaused(state: state)
    }

    /// Short Tune explanation shown only when the backend cannot apply heater
    /// targets right now; `nil` keeps the temperatures editable.
    static func tuneTemperatureBlockedReason(
        isPrinting: Bool, heatDuringPrint: Bool, canControlHeat: Bool, heatBlockedReason: String?
    ) -> String? {
        if isPrinting, !heatDuringPrint { return "This printer can't change temperatures during a print." }
        guard !canControlHeat else { return nil }
        return heatBlockedReason
    }

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
    static func isAvailable(_ segment: Segment, isPrinting: Bool) -> Bool {
        switch segment {
        case .tune: true
        case .heat, .move: !isPrinting
        }
    }

    static func resolvedSegment(_ selected: Segment, isPrinting: Bool) -> Segment {
        isAvailable(selected, isPrinting: isPrinting) ? selected : .tune
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
        Self.isPrinting(state: printer.state) || viewModel.isPrintingOrPaused
    }

    private var segment: Segment {
        Self.resolvedSegment(selectedSegment, isPrinting: isPrinting)
    }

    var body: some View {
        if !PrinterControlsSection.isHidden(for: printer) {
            VStack(alignment: .leading, spacing: 12) {
                loadIssues
                if !isPrinting, let reason = viewModel.blockedReason {
                    Label(reason, systemImage: "lock.fill")
                        .font(.footnote)
                        .foregroundStyle(Color.pfTextPrimary)
                        .accessibilityElement(children: .combine)
                        .accessibilityLabel(reason)
                        .accessibilityIdentifier("printer.controls.blocked-reason")
                }
                positionReadout
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
                if printing { selectedSegment = segment }
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
                Text(viewModel.hardwareLoadError ?? "Positive targets require valid reported maxima. Supported zero-off commands remain available.")
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

    private var positionReadout: some View {
        let layout = dynamicTypeSize.isAccessibilitySize
            ? AnyLayout(VStackLayout(alignment: .leading, spacing: 8))
            : AnyLayout(HStackLayout(alignment: .center, spacing: 8))
        let accessible = dynamicTypeSize.isAccessibilitySize
        return layout {
            readoutColumn("X", value: viewModel.printer.x)
            readoutDivider(vertical: !accessible)
            readoutColumn("Y", value: viewModel.printer.y)
            readoutDivider(vertical: !accessible)
            readoutColumn("Z", value: viewModel.printer.z)
        }
        .padding(12)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(Color.pfBackground, in: RoundedRectangle(cornerRadius: Self.cornerRadius))
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier("printer.controls.position")
    }

    private func readoutColumn(_ axis: String, value: Double?) -> some View {
        let display = JogSubgroup.positionDisplay(
            axis: axis, value: value, homedAxes: viewModel.printer.homedAxes,
            fractionDigits: Self.readoutFractionDigits(axis: axis)
        )
        return VStack(alignment: .center, spacing: 2) {
            Text(axis)
                .font(.caption.weight(.semibold))
                .foregroundStyle(Color.pfTextSecondary)
            Text(display.text)
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

    private func readoutDivider(vertical: Bool) -> some View {
        Rectangle()
            .fill(Color.pfBorder)
            .frame(width: vertical ? 1 / displayScale : nil, height: vertical ? 32 : 1 / displayScale)
            .accessibilityHidden(true)
    }

    // MARK: - Segments

    private var segmentPicker: some View {
        HStack(spacing: 2) {
            ForEach(Segment.allCases) { option in
                let available = Self.isAvailable(option, isPrinting: isPrinting)
                ControlActionButton(
                    title: option.title,
                    identifier: "printer.controls.segment.\(option.rawValue)",
                    hint: available ? nil : "Unavailable while printing",
                    selected: segment == option,
                    compact: true,
                    segmented: true,
                    minimumHeight: 44
                ) { selectedSegment = option }
                .disabled(!available)
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

    private var movePanel: some View {
        VStack(alignment: .leading, spacing: 12) {
            PrinterControlCommandFeedback(viewModel: viewModel, section: .motion)
            if JogSubgroup.isHidden(for: viewModel.capabilities) {
                Text(viewModel.capabilities == nil ? "Checking motion support…" : "Motion controls are unavailable on this printer.")
                    .font(.footnote)
                    .foregroundStyle(Color.pfTextSecondary)
            } else {
                HStack(alignment: .top, spacing: 12) {
                    xyPad
                    zColumn.frame(width: 72)
                }
                .dynamicTypeSize(dynamicTypeSize.isAccessibilitySize ? .xxxLarge : dynamicTypeSize)
                if let note = jogSafetyNote {
                    Text(note)
                        .font(.footnote)
                        .foregroundStyle(Color.pfWarning)
                        .fixedSize(horizontal: false, vertical: true)
                        .accessibilityIdentifier("printer.controls.jog.homing-safety")
                }
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

    private var jogSafetyNote: String? {
        guard let axes = viewModel.printer.homedAxes else {
            return "Homing status unavailable. Verify axes are homed before moving."
        }
        let required = JogSubgroup.visibleAxes(for: viewModel.capabilities)
        guard required.allSatisfy({ axes.lowercased().contains($0.lowercased()) }) else {
            return "Not homed. Home all axes before moving."
        }
        return nil
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
        PreheatSubgroup(viewModel: viewModel, usesSteppers: true, compactForDetail: true)
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
                accessibilityLabel: { "Babystep \($0.formatted()) millimeters" }
            )
            let buttons = dynamicTypeSize.isAccessibilitySize
                ? AnyLayout(VStackLayout(spacing: 8))
                : AnyLayout(HStackLayout(spacing: 8))
            buttons {
                babystepButton(raise: true, reason: reason).disabled(!canAdjust)
                babystepButton(raise: false, reason: reason).disabled(!canAdjust)
            }
            if isPrinting {
                Divider()
                tuneTemperatures
            }
            PrinterSensorControlRows(viewModel: viewModel, compact: true)
        }
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier("printer.controls.runtime")
    }

    /// Hotend and bed targets stay editable in Tune during a print when the
    /// backend can apply them; otherwise a short reason explains why not.
    private var tuneTemperatures: some View {
        let reason = Self.tuneTemperatureBlockedReason(
            isPrinting: isPrinting,
            heatDuringPrint: viewModel.supportsHeatDuringPrint,
            canControlHeat: viewModel.canControlHeat,
            heatBlockedReason: viewModel.heatBlockedReason
        )
        return VStack(alignment: .leading, spacing: 8) {
            EssentialControlHeading(title: "Temperatures")
            PrinterControlCommandFeedback(viewModel: viewModel, section: .heat)
            PreheatSubgroup.IndividualHeaterControls(
                viewModel: viewModel, usesSteppers: true, compactForDetail: true
            )
            .disabled(reason != nil)
            if let reason {
                Text(reason)
                    .font(.footnote)
                    .foregroundStyle(Color.pfTextSecondary)
                    .fixedSize(horizontal: false, vertical: true)
                    .accessibilityIdentifier("printer.controls.runtime.temperatures.reason")
            }
        }
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier("printer.controls.runtime.temperatures")
    }

    private func babystepButton(raise: Bool, reason: String?) -> some View {
        ControlActionButton(
            title: raise ? "Raise nozzle" : "Lower nozzle",
            identifier: "printer.controls.runtime.z-offset.\(raise ? "increase" : "decrease")",
            accessibilityTitle: raise ? "Raise nozzle" : "Lower nozzle",
            hint: reason ?? "Moves the live Z offset \(babystep.formatted()) millimeters.",
            compact: true,
            systemImage: raise ? "arrow.up" : "arrow.down",
            minimumHeight: 44
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
        accessibilityLabel: @escaping (Double) -> String
    ) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            Text(title)
                .font(.caption.weight(.semibold))
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
    }
}
