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
    @Environment(\.dynamicTypeSize) private var dynamicTypeSize

    static let stepOptions: [Double] = [0.1, 1, 10, 100]
    private static let canonicalAxes: [String] = ["X", "Y", "Z"]

    /// Filter the canonical X/Y/Z list to what the backend reports. When capabilities
    /// have not been fetched yet, default to the full list so the UI renders sensibly
    /// during initial load. Empty result → caller should hide the subgroup.
    static func visibleAxes(for capabilities: PrinterBackendCapabilities?) -> [String] {
        guard let caps = capabilities else { return [] }
        return canonicalAxes.filter { caps.supportedAxes.contains($0) }
    }

    /// True when the subgroup must be removed from the layout entirely.
    /// Hide if the backend explicitly does not support movement, or if all canonical
    /// axes are filtered out by `supportedAxes`.
    static func isHidden(for capabilities: PrinterBackendCapabilities?) -> Bool {
        guard let caps = capabilities else { return true }
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
        .onChange(of: viewModel.capabilities, initial: true) { _, newCaps in
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
        return selectionLayout {
            ForEach(axes, id: \.self) { axis in
                ControlActionButton(
                    title: axis, identifier: "printer.controls.jog.axis.\(axis.lowercased())",
                    accessibilityTitle: "Jog axis \(axis)",
                    selected: selectedAxis == axis
                ) { selectedAxis = axis }
            }
        }
        .disabled(!viewModel.canControl || viewModel.isExecuting)
    }

    private var stepPicker: some View {
        selectionLayout {
            ForEach(Self.stepOptions, id: \.self) { step in
                ControlActionButton(
                    title: stepLabel(step), identifier: "printer.controls.jog.step.\(stepLabel(step))",
                    accessibilityTitle: "Jog step \(stepLabel(step)) millimeters",
                    selected: selectedStep == step
                ) { selectedStep = step }
            }
        }
        .disabled(!viewModel.canControl || viewModel.isExecuting)
    }

    private var selectionLayout: AnyLayout {
        dynamicTypeSize.isAccessibilitySize
            ? AnyLayout(VStackLayout(spacing: 8)) : AnyLayout(HStackLayout(spacing: 8))
    }

    struct AbsolutePositionControls: View {
        @ObservedObject var viewModel: PrinterControlsViewModel
        @State private var x = ""
        @State private var y = ""
        @State private var z = ""
        @State private var feedrate = ""
        @State private var inputError: String?

        static func isVisible(_ capabilities: PrinterBackendCapabilities?) -> Bool {
            capabilities?.supportsAbsoluteMovement == true
                && Set(capabilities?.supportedAxes ?? []).isSuperset(of: ["X", "Y", "Z"])
        }

        static func destination(x: String, y: String, z: String) throws -> SafetyVector3Dto {
            try ControlNumberInput.absolutePosition(
                x: ControlNumberInput.coordinate(x),
                y: ControlNumberInput.coordinate(y),
                z: ControlNumberInput.coordinate(z)
            )
        }

        static func hasDestinationInput(x: String, y: String, z: String) -> Bool {
            [x, y, z].contains { !$0.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }
        }

        private var validationMessage: String? {
            do {
                let point = try Self.destination(x: x, y: y, z: z)
                return viewModel.absoluteMoveBlockedReason(
                    x: point.x, y: point.y, z: point.z,
                    feedrateMmMin: try ControlNumberInput.feedrate(feedrate)
                )
            } catch { return error.localizedDescription }
        }

        var body: some View {
            if Self.isVisible(viewModel.capabilities) {
                VStack(alignment: .leading, spacing: 8) {
                    Text("Absolute position")
                        .font(.headline)
                        .accessibilityAddTraits(.isHeader)
                    Text("Enter all X, Y and Z destinations in mm; zero is explicit. Verified frame, travel bounds, homing and clearance are required. The server rechecks before dispatch.")
                        .font(.footnote)
                        .fixedSize(horizontal: false, vertical: true)
                    ForEach(JogSubgroup.visibleAxes(for: viewModel.capabilities), id: \.self) { axis in
                        Text("\(axis) reported: \(positionText(axis))")
                            .font(.footnote)
                        ControlNumberField(
                            placeholder: "\(axis) destination (mm)", text: binding(axis),
                            label: "\(axis) required absolute destination in millimeters",
                            identifier: "printer.controls.absolute.\(axis.lowercased())",
                            hint: ControlNumberInput.coordinatePrecisionMessage
                        )
                    }


                    Text("Homed axes: \(viewModel.printer.homedAxes ?? "Unknown")")
                        .font(.footnote)
                    ControlNumberField(
                        placeholder: "Custom feedrate unavailable", text: $feedrate,
                        label: "Absolute movement feedrate in millimeters per minute",
                        identifier: "printer.controls.absolute.feedrate",
                        hint: ControlNumberInput.customFeedrateMessage
                    )
                    .disabled(true)
                    Text("No verified custom feedrate maximum. Uses \(PrinterControlsViewModel.zFeedrateMmMin) mm/min because every absolute move includes Z.")
                        .font(.footnote)
                    ControlActionButton(title: "Move to position", identifier: "printer.controls.absolute.move") {
                        do {
                            let point = try Self.destination(x: x, y: y, z: z)
                            let f = try ControlNumberInput.feedrate(feedrate)
                            if let reason = viewModel.absoluteMoveBlockedReason(
                                x: point.x, y: point.y, z: point.z, feedrateMmMin: f
                            ) {
                                inputError = reason
                                return
                            }
                            inputError = nil
                            Task { await viewModel.moveTo(x: point.x, y: point.y, z: point.z, feedrateMmMin: f) }
                        } catch {
                            inputError = error.localizedDescription
                        }
                    }
                    .disabled(validationMessage != nil)
                    if let message = validationMessage ?? inputError {
                        Text(message)
                            .font(.footnote)
                            .foregroundStyle(
                                Self.hasDestinationInput(x: x, y: y, z: z) || inputError != nil
                                    ? Color.pfError : Color.pfTextSecondary
                            )
                            .accessibilityAddTraits(.isStaticText)
                    }
                }
                .foregroundStyle(Color.pfTextPrimary)
                .disabled(!viewModel.canControl || viewModel.isExecuting)
            }
        }

        private func binding(_ axis: String) -> Binding<String> {
            switch axis {
            case "X": return $x
            case "Y": return $y
            default: return $z
            }
        }

        private func positionText(_ axis: String) -> String {
            let value = axis == "X" ? viewModel.printer.x : axis == "Y" ? viewModel.printer.y : viewModel.printer.z
            guard let value, value.isFinite else { return "Unknown" }
            return "\(value.formatted()) mm"
        }
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
        let isInteractive = viewModel.canControl && !viewModel.isExecuting

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
        guard viewModel.canControl, !viewModel.isExecuting else {
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

/// Essential's single Move & home group. The prototype supplies layout only;
/// every action still goes through the existing capability-gated command owner.
struct PrinterMotionControls: View {
    @ObservedObject var viewModel: PrinterControlsViewModel
    @State private var step = 1.0
    @State private var showsAbsolute = false
    @Environment(\.dynamicTypeSize) private var dynamicTypeSize

    private var row: AnyLayout {
        dynamicTypeSize.isAccessibilitySize
            ? AnyLayout(VStackLayout(alignment: .leading, spacing: 8))
            : AnyLayout(HStackLayout(alignment: .top, spacing: 8))
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            EssentialControlHeading(title: "Move & home", detail: homingDescription)
                .padding(.bottom, 14)
            row {
                position("X", value: viewModel.printer.x)
                position("Y", value: viewModel.printer.y)
                position("Z", value: viewModel.printer.z)
            }
            if !JogSubgroup.isHidden(for: viewModel.capabilities) {
                let stepsLayout = dynamicTypeSize.isAccessibilitySize
                    ? AnyLayout(VStackLayout(alignment: .leading, spacing: 8))
                    : AnyLayout(HStackLayout(spacing: 3))
                stepsLayout {
                    ForEach(JogSubgroup.stepOptions, id: \.self) { value in
                        ControlActionButton(
                            title: "\(value.formatted()) mm",
                            identifier: "printer.controls.jog.step.\(value.formatted())",
                            accessibilityTitle: "Jog step \(value.formatted()) millimeters",
                            selected: step == value, compact: true, textSize: 13, segmented: true
                        ) { step = value }
                    }
                }
                .padding(3)
                .background(Color.pfBackgroundTertiary, in: RoundedRectangle(cornerRadius: 11))
                .overlay(RoundedRectangle(cornerRadius: 11).strokeBorder(Color.pfBorder))
                .padding(.vertical, 12)
                .disabled(!viewModel.canControl || viewModel.isExecuting)
                if dynamicTypeSize.isAccessibilitySize {
                    ForEach(JogSubgroup.visibleAxes(for: viewModel.capabilities), id: \.self) { axis in
                        HStack(spacing: 8) {
                            jog(axis, sign: -1, title: "\(axis) -")
                            jog(axis, sign: 1, title: "\(axis) +")
                        }
                    }
                } else {
                    HStack(spacing: 16) {
                        Grid(horizontalSpacing: 6, verticalSpacing: 6) {
                            GridRow {
                                Color.clear.frame(height: 48).accessibilityHidden(true)
                                jog("Y", sign: 1, symbol: "arrow.up").frame(maxWidth: .infinity).frame(height: 48)
                                Color.clear.frame(height: 48).accessibilityHidden(true)
                            }
                            GridRow {
                                jog("X", sign: -1, symbol: "arrow.left").frame(maxWidth: .infinity).frame(height: 48)
                                homeAll(center: true).frame(maxWidth: .infinity).frame(height: 48)
                                jog("X", sign: 1, symbol: "arrow.right").frame(maxWidth: .infinity).frame(height: 48)
                            }
                            GridRow {
                                Color.clear.frame(height: 48).accessibilityHidden(true)
                                jog("Y", sign: -1, symbol: "arrow.down").frame(maxWidth: .infinity).frame(height: 48)
                                Color.clear.frame(height: 48).accessibilityHidden(true)
                            }
                        }
                        VStack(spacing: 8) {
                            jog("Z", sign: 1, title: "Z +")
                            jog("Z", sign: -1, title: "Z -")
                        }
                        .frame(width: 68)
                    }
                }
                if homingDescription != "Homed" {
                    Text("Confirm homing before moving an axis.")
                        .font(.footnote).foregroundStyle(Color.pfTextSecondary).padding(.top, 10)
                }
            } else {
                Text("Relative movement is unavailable without confirmed axis support.")
                    .font(.footnote).foregroundStyle(Color.pfTextSecondary)
            }
            EssentialControlSeparator()
            row {
                homeAll()
                home("XY", axes: ["X", "Y"]) { await viewModel.homeXY() }
                home("Z", axes: ["Z"]) { await viewModel.homeZ() }
            }
            if JogSubgroup.AbsolutePositionControls.isVisible(viewModel.capabilities) {
                EssentialControlSeparator()
                ControlActionButton(
                    title: showsAbsolute ? "Hide absolute movement" : "Go to XYZ…",
                    identifier: "printer.controls.absolute.disclosure",
                    compact: true, value: showsAbsolute ? "Expanded" : "Collapsed"
                ) { showsAbsolute.toggle() }
                if showsAbsolute {
                    JogSubgroup.AbsolutePositionControls(viewModel: viewModel)
                }
            }
            EssentialControlSeparator()
            row {
                HomeSubgroup.MotorReleaseControls(viewModel: viewModel)
                if viewModel.calibrationStep == nil {
                    ControlActionButton(
                        title: "Z-offset…", identifier: "printer.controls.calibration-start", compact: true
                    ) { Task { await viewModel.startCalibration() } }
                    .disabled(!viewModel.canControl || viewModel.isExecuting || viewModel.isReviewingCalibration)
                }
            }
            PrinterZOffsetCalibrationControls(viewModel: viewModel, showsEntry: false)
        }
        .foregroundStyle(Color.pfTextPrimary)
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier("printer.controls.motion-group")
    }

    private func position(_ axis: String, value: Double?) -> some View {
        let text = value.flatMap {
            $0.isFinite ? "\($0.formatted(.number.precision(.fractionLength(1)))) mm" : nil
        } ?? "Unknown"
        return Text("\(axis) \(text)").font(.caption.monospacedDigit())
            .foregroundStyle(Color.pfTextSecondary)
            .frame(maxWidth: .infinity, alignment: .leading)
            .frame(minHeight: 18)
    }

    private var homingDescription: String {
        guard let axes = viewModel.printer.homedAxes else { return "Homing unknown" }
        return ["x", "y", "z"].allSatisfy { axes.lowercased().contains($0) }
            ? "Homed" : axes.isEmpty ? "Not homed" : "Homed: \(axes.uppercased())"
    }

    private func jog(_ axis: String, sign: Double, title: String = "", symbol: String? = nil) -> some View {
        let available = viewModel.capabilities?.supportsMovement == true
            && JogSubgroup.visibleAxes(for: viewModel.capabilities).contains(axis)
        let direction = sign > 0 ? "positive" : "negative"
        let pending = viewModel.pendingCommand?.kind == .jog(axis: axis, distanceMm: sign * step)
        return ControlActionButton(
            title: title, identifier: "printer.controls.jog.\(axis.lowercased()).\(direction)",
            accessibilityTitle: "Move \(axis) \(direction)",
            hint: available ? "Moves \(step.formatted()) millimeters." : "\(axis) movement is unavailable.",
            compact: true, systemImage: symbol, value: pending ? "Pending" : nil, minimumHeight: 48
        ) { Task { await viewModel.jog(axis: axis, distanceMm: sign * step) } }
        .disabled(!available || !viewModel.canControl || viewModel.isExecuting)
    }

    private func homeAll(center: Bool = false) -> some View {
        home("all", axes: ["X", "Y", "Z"], center: center) { await viewModel.homeAll() }
    }

    private func home(
        _ name: String, axes: [String], center: Bool = false, action: @escaping @MainActor () async -> Void
    ) -> some View {
        let available = viewModel.capabilities?.supportsHome(axes: axes) == true
        return ControlActionButton(
            title: center ? "" : "Home \(name)",
            identifier: "printer.controls.home.\(name.lowercased())\(center ? ".center" : "")",
            accessibilityTitle: name == "all" ? "Home all axes" : "Home \(name)",
            hint: available ? "Homes \(axes.joined(separator: ", "))." : "This homing operation is unavailable.",
            compact: true, systemImage: center ? "house" : nil,
            value: viewModel.pendingCommand?.kind == .home(axes: axes) ? "Pending" : nil,
            tinted: center, minimumHeight: center ? 48 : 45
        ) { Task { await action() } }
        .disabled(!available || !viewModel.canControl || viewModel.isExecuting)
    }
}
