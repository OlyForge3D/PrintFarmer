import SwiftUI

/// Presentation-only setup controls. The host owns the model, capability loading,
/// access gating and live-update forwarding even while this content is offscreen.
/// Use `PrinterControlsSection(printer:composition:)` for standalone ownership.
struct PrinterSetupControlsContent: View {
    let printer: Printer
    @ObservedObject var viewModel: PrinterControlsViewModel
    var usesColumns: Bool? = nil
    var materialPresentation: PrinterFilamentPresentation? = nil
    var observesSafety = false
    var showsMaterial = true
    var materialActions: [PrinterFilamentAction] = []
    var onMaterialAction: @MainActor (PrinterFilamentAction) -> Void = { _ in }
    @Environment(\.horizontalSizeClass) private var horizontalSizeClass
    @Environment(\.dynamicTypeSize) private var dynamicTypeSize
    @Environment(\.scenePhase) private var scenePhase

    private var isPrintingOrPaused: Bool {
        switch printer.state?.lowercased() {
        case "printing", "paused", "starting": return true
        default: return false
        }
    }

    var body: some View {
        if !PrinterControlsSection.isHidden(for: printer) {
            content
                .task(id: scenePhase) {
                    guard observesSafety else { return }
                    guard scenePhase == .active else {
                        viewModel.suspendSafetyObservation()
                        return
                    }
                    while !Task.isCancelled && viewModel.isActive {
                        // Initial owner loading already reads discovery and status.
                        // Do not race it with a second capability request on mount.
                        do { try await Task.sleep(for: .seconds(5)) } catch { return }
                        await viewModel.refreshSafetyEvidence()
                    }
                }
                .onDisappear {
                    if observesSafety { viewModel.suspendSafetyObservation() }
                }
        }
    }

    private var content: some View {
        VStack(alignment: .leading, spacing: 12) {
            VStack(alignment: .leading, spacing: 12) {
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
                    .foregroundStyle(Color.pfTextPrimary)
                    .padding(.bottom, 12)
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
                    .foregroundStyle(Color.pfTextPrimary)
                    .padding(.bottom, 12)
                }

                if isPrintingOrPaused {
                    lockoutBanner
                        .padding(.bottom, 12)
                } else if let reason = viewModel.blockedReason {
                    Label(reason, systemImage: "lock.fill")
                        .font(.footnote)
                        .foregroundStyle(Color.pfTextPrimary)
                        .padding(.bottom, 12)
                        .accessibilityElement(children: .combine)
                        .accessibilityLabel(reason)
                        .accessibilityIdentifier("printer.controls.blocked-reason")
                }

                let columns = (usesColumns ?? (horizontalSizeClass == .regular))
                    && !dynamicTypeSize.isAccessibilitySize
                let layout = columns
                    ? AnyLayout(EssentialControlsColumnsLayout())
                    : AnyLayout(VStackLayout(alignment: .leading, spacing: EssentialControlsStyle.groupSpacing))
                // Geometry changes, not view identity: retain drafts and disclosures.
                layout {
                    VStack(alignment: .leading, spacing: EssentialControlsStyle.groupSpacing) {
                        insetGroup {
                            PrinterDetailTemperatureStrip(
                                hotend: .init(measured: printer.hotendTemp, target: printer.hotendTarget, isOnline: printer.isOnline),
                                bed: .init(measured: printer.bedTemp, target: printer.bedTarget, isOnline: printer.isOnline),
                                showsBed: true,
                                identifier: "printer.controls.temperatures", essentialControls: true
                            )
                        }
                        insetGroup {
                            PreheatSubgroup(viewModel: viewModel)
                        }
                    }
                    .frame(maxWidth: .infinity, alignment: .leading)
                    insetGroup {
                        PrinterMotionControls(viewModel: viewModel)
                    }
                    if showsMaterial { insetGroup {
                        PrinterMaterialControls(
                            viewModel: viewModel, materialPresentation: materialPresentation,
                            materialActions: materialActions, onMaterialAction: onMaterialAction
                        )
                    } }
                }

            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    private func insetGroup<Content: View>(@ViewBuilder _ content: () -> Content) -> some View {
        content()
            .frame(maxWidth: .infinity, alignment: .leading)
            .padding(EssentialControlsStyle.groupPadding)
            .background(Color.pfBackground, in: RoundedRectangle(cornerRadius: 16))
    }

    private var lockoutBanner: some View {
        let message = String(localized: "Motion and filament setup are unavailable while a job is starting, printing or paused. Supported temperature and Z-offset controls remain available in Tune.")
        let layout = dynamicTypeSize.isAccessibilitySize
            ? AnyLayout(VStackLayout(alignment: .leading, spacing: 8))
            : AnyLayout(HStackLayout(alignment: .center, spacing: 8))
        return layout {
            Image(systemName: "lock.fill")
                .foregroundStyle(Color.pfWarning)
            Text(message)
                .font(.footnote)
                .foregroundStyle(Color.pfTextPrimary)
                .fixedSize(horizontal: false, vertical: true)
        }
        .padding(12)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(Color.pfWarning.opacity(0.12), in: RoundedRectangle(cornerRadius: 10))
        .accessibilityElement(children: .combine)
        .accessibilityLabel(message)
    }

}

/// The same command owner drives each card; only the originating card shows
/// its feedback, including after HTTP/telemetry observation has ended.
struct PrinterControlCommandFeedback: View {
    @ObservedObject var viewModel: PrinterControlsViewModel
    let section: ControlCommand.Section

    var body: some View {
        if viewModel.feedbackSection == section {
            VStack(alignment: .leading, spacing: 8) {
                if let error = viewModel.lastError {
                    errorBanner(error)
                } else if let notice = viewModel.commandNotice {
                    // Success is silent (#3496); the tapped control carries the spinner.
                    Label(notice, systemImage: "info.circle")
                        .font(.footnote)
                        .fixedSize(horizontal: false, vertical: true)
                }
            }
            .foregroundStyle(Color.pfTextPrimary)
            .padding(.bottom, viewModel.commandNotice != nil || viewModel.lastError != nil ? 14 : 0)
            .accessibilityElement(children: .contain)
            .accessibilityIdentifier("printer.controls.\(section.rawValue).feedback")
        }
    }

    private func errorBanner(_ error: ControlsError) -> some View {
        HStack(alignment: .top, spacing: 8) {
            Image(systemName: "exclamationmark.triangle.fill")
                .foregroundStyle(Color.pfError)
            VStack(alignment: .leading, spacing: 4) {
                Text(error.message)
                    .font(.footnote)
                    .foregroundStyle(Color.pfTextPrimary)
            }
            ControlActionButton(
                title: "Dismiss", identifier: "printer.controls.dismiss-error",
                compact: true, textSize: 13, textOnly: true
            ) { viewModel.dismissError() }
            .fixedSize(horizontal: true, vertical: false)
        }
        .padding(12)
        .background(Color.pfError.opacity(0.12), in: RoundedRectangle(cornerRadius: 10))
        .accessibilityElement(children: .contain)
    }
}

/// Physical material commands deliberately never receive an inventory owner.
struct PrinterMaterialControls: View {
    @ObservedObject var viewModel: PrinterControlsViewModel
    var materialPresentation: PrinterFilamentPresentation? = nil
    var materialActions: [PrinterFilamentAction] = []
    var onMaterialAction: @MainActor (PrinterFilamentAction) -> Void = { _ in }
    @State private var distance = 10.0
    @State private var speed = 1
    @State private var confirmation: PhysicalFilamentOperation?
    @Environment(\.dynamicTypeSize) private var dynamicTypeSize

    private var row: AnyLayout {
        dynamicTypeSize.isAccessibilitySize
            ? AnyLayout(VStackLayout(alignment: .leading, spacing: 8))
            : AnyLayout(HStackLayout(alignment: .top, spacing: 8))
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            EssentialControlHeading(title: "Filament tools").padding(.bottom, 14)
            PrinterControlCommandFeedback(viewModel: viewModel, section: .material)
            if let materialPresentation {
                PrinterFilamentSection(
                    presentation: materialPresentation, actions: materialActions,
                    onAction: onMaterialAction, embedded: true
                )
            }
            EssentialControlSeparator()
            row {
                ForEach(PhysicalFilamentOperation.allCases) { operation in
                    ControlActionButton(
                        title: operation.rawValue.capitalized,
                        identifier: "printer.controls.filament-\(operation.rawValue)",
                        accessibilityTitle: operation.title,
                        hint: viewModel.filamentBlockedReason(operation)
                            ?? "Requests a physical operation, not a spool assignment change.",
                        compact: true
                    ) { confirmation = operation }
                    .disabled(viewModel.isExecuting || viewModel.filamentBlockedReason(operation) != nil)
                }
            }
            row {
                VStack(alignment: .leading, spacing: 5) {
                    Text("Extrusion length").font(.footnote).foregroundStyle(Color.pfTextSecondary)
                    ControlActionButton(
                    title: "\(Int(distance)) mm", identifier: "printer.controls.extrusion-distance",
                    accessibilityTitle: "Extrusion distance", compact: true, systemImage: "chevron.down",
                    value: "\(Int(distance)) millimeters",
                    menu: UIMenu(children: MaterialControlInput.distances.map { value in
                        UIAction(title: "\(Int(value)) mm", state: distance == value ? .on : .off) { _ in distance = value }
                    }), textSize: 14, matchesInputHeight: true
                    ) {}
                }
                VStack(alignment: .leading, spacing: 5) {
                    Text("Rate").font(.footnote).foregroundStyle(Color.pfTextSecondary)
                    ControlActionButton(
                    title: "\(speed) mm/s", identifier: "printer.controls.extrusion-speed",
                    accessibilityTitle: "Extrusion speed", compact: true, systemImage: "chevron.down",
                    value: "\(speed) millimeters per second",
                    menu: UIMenu(children: MaterialControlInput.speeds.map { value in
                        UIAction(title: "\(value) mm/s", state: speed == value ? .on : .off) { _ in speed = value }
                    }), textSize: 14, matchesInputHeight: true
                    ) {}
                }
            }
            .padding(.vertical, 12)
            .disabled(!viewModel.canControl || viewModel.isExecuting || viewModel.capabilities?.supportsExtrusion != true)
            row {
                ControlActionButton(
                    title: "Extrude", identifier: "printer.controls.extrude",
                    accessibilityTitle: "Extrude \(Int(distance)) millimeters",
                    hint: viewModel.extrusionBlockedReason, compact: true
                ) {
                    Task { await viewModel.extrude(distanceMm: distance, speedMmPerSecond: speed) }
                }
                ControlActionButton(
                    title: "Retract", identifier: "printer.controls.retract",
                    accessibilityTitle: "Retract \(Int(distance)) millimeters",
                    hint: viewModel.extrusionBlockedReason, compact: true
                ) {
                    Task { await viewModel.extrude(distanceMm: -distance, speedMmPerSecond: speed) }
                }
            }
            .disabled(viewModel.isExecuting || viewModel.extrusionBlockedReason != nil)
        }
        .foregroundStyle(Color.pfTextPrimary)
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier("printer.controls.material-group")
        .confirmationDialog(
            confirmation?.title ?? "Physical filament",
            isPresented: Binding(get: { confirmation != nil }, set: { if !$0 { confirmation = nil } }),
            titleVisibility: .visible,
            presenting: confirmation
        ) { operation in
            Button(operation.title) {
                Task { await viewModel.performFilament(operation) }
            }
            Button("Cancel", role: .cancel) {}
        } message: { _ in
            Text(
                "Check the printer and follow its supported material procedure. This is printer-level only and will not change spool assignment. Request acceptance does not prove physical completion."
            )
        }
        .onChange(of: viewModel.isActive) { _, active in
            if !active { confirmation = nil }
        }
    }
}

/// Inputs for writable non-fan sensor entries. Fans are not adjustable from the
/// app; read-only entries and descriptors never get controls. Each entry uses
/// the shared `HeatTargetRow`, so Chamber and Heater Box rows match Nozzle and
/// Bed exactly (#3547).
struct PrinterSensorControlRows: View {
    @ObservedObject var viewModel: PrinterControlsViewModel
    var compact: Bool

    var body: some View {
        let activeExtruderId = viewModel.printer.activeExtruderId
        let sensors = viewModel.controllableSensors.filter {
            !PrinterSensorFormatting.isHidden($0) && !PrinterSensorFormatting.isFan($0)
                && !PrinterSensorFormatting.steppableDescriptors($0, activeExtruderId: activeExtruderId).isEmpty
        }
        if !sensors.isEmpty {
            VStack(alignment: .leading, spacing: 8) {
                ForEach(sensors) { sensor in
                    ForEach(PrinterSensorFormatting.steppableDescriptors(sensor, activeExtruderId: activeExtruderId), id: \.quantity) { descriptor in
                        row(sensor: sensor, descriptor: descriptor)
                    }
                }
                // Genuine unsupported/access reasons only; loading and
                // in-flight states just disable the rows.
                if let reason = viewModel.sensorRowMessage {
                    Text(reason).font(.footnote).foregroundStyle(Color.pfTextSecondary)
                        .fixedSize(horizontal: false, vertical: true)
                        .accessibilityIdentifier("printer.controls.sensor.reason")
                }
            }
        }
    }

    static func title(sensor: PrinterSensor, quantity: SensorQuantity) -> String {
        let name = PrinterSensorFormatting.displayName(sensor.displayName)
        return quantity == .targetTemperature
            ? name : "\(name) \(PrinterSensorFormatting.quantityTitle(quantity).lowercased())"
    }

    private func row(sensor: PrinterSensor, descriptor: SensorWritable) -> some View {
        let name = PrinterSensorFormatting.displayName(sensor.displayName)
        let spokenTitle = "\(name) \(PrinterSensorFormatting.quantityTitle(descriptor.quantity).lowercased())"
        let current = sensor.reading(descriptor.quantity)?.value.number
        let measuredQuantity: SensorQuantity = descriptor.quantity == .targetTemperature ? .temperature : descriptor.quantity
        let id = "printer.controls.sensor.\(sensor.id).\(descriptor.quantity.rawValue)"
        let pending: Bool = {
            if case .sensorControl(let sensorId, let quantity, _) = viewModel.pendingCommand?.kind {
                return sensorId == sensor.id && quantity == descriptor.quantity
            }
            return false
        }()
        let unitSpoken = descriptor.unit.spoken
        return HeatTargetRow(
            title: Self.title(sensor: sensor, quantity: descriptor.quantity),
            measured: sensor.reading(measuredQuantity)?.value.number,
            identifier: id,
            fieldIdentifier: id + ".value",
            fieldLabel: spokenTitle + (unitSpoken.isEmpty ? "" : " in \(unitSpoken)"),
            fieldHint: "Double tap to enter a value, then choose Set. Range \(descriptor.min.formatted()) to \(descriptor.max.formatted())"
                + (descriptor.allowZero == true ? "; zero switches it off." : "."),
            stepLabel: { "\($0 ? "Increase" : "Decrease") \(spokenTitle)" },
            unit: descriptor.unit,
            currentTarget: current,
            isPending: pending,
            next: { base, increasing in
                base.map { PrinterSensorFormatting.steppedTarget(current: $0, increasing: increasing, descriptor: descriptor) }
            },
            parse: { text in
                guard let value = try ControlNumberInput.optional(text) else { return nil }
                guard Self.accepts(value, descriptor: descriptor) else {
                    throw PrinterControlError.invalidRequest(
                        "Enter \(descriptor.min.formatted()) to \(descriptor.max.formatted())"
                            + (descriptor.allowZero == true ? ", or 0 for off" : "")
                            + ((descriptor.step ?? 0) > 0 ? " in steps of \(descriptor.step!.formatted())." : ".")
                    )
                }
                return value
            },
            isBusy: { viewModel.isExecuting },
            apply: { await viewModel.setSensorValue(sensorId: sensor.id, quantity: descriptor.quantity, value: $0) }
        )
        .disabled(!viewModel.canControlSensors || current == nil)
    }

    /// Mirrors `PrinterControlsViewModel.setSensorValue` validation.
    static func accepts(_ value: Double, descriptor: SensorWritable) -> Bool {
        value.isFinite
            && ((value == 0 && descriptor.allowZero == true && descriptor.quantity == .targetTemperature)
                || (value >= descriptor.min && value <= descriptor.max))
            && PrinterSensorFormatting.isOnGrid(value, descriptor: descriptor)
    }
}

/// Inline rather than a modal: the detail host's independent, confirmed
/// Emergency Stop remains reachable throughout calibration.
struct PrinterZOffsetCalibrationControls: View {
    @ObservedObject var viewModel: PrinterControlsViewModel
    var showsEntry = true
    @State private var increment = 0.05
    @AccessibilityFocusState private var stepFocused: Bool

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            if let step = viewModel.calibrationStep {
                Text("Z-offset calibration").font(.headline).accessibilityAddTraits(.isHeader)
                Text("Step: \(step.rawValue.capitalized)")
                    .font(.subheadline.weight(.semibold))
                    .foregroundStyle(Color.pfTextSecondary)
                    .accessibilityAddTraits(.isHeader)
                    .accessibilityIdentifier("printer.controls.calibration-step")
                    .accessibilityFocused($stepFocused)
                steps(step)
                if let message = viewModel.calibrationMessage {
                    Text(message).font(.footnote)
                }
                ControlActionButton(
                    title: step == .done ? "Close calibration" : "Cancel calibration",
                    identifier: "printer.controls.calibration-cancel",
                    hint: "Stops the workflow, not physical motion. Emergency Stop remains separate."
                ) { viewModel.cancelCalibration() }
            } else if showsEntry {
                ControlActionButton(title: "Z-offset…", identifier: "printer.controls.calibration-start", compact: true) {
                    Task { await viewModel.startCalibration() }
                }
                .disabled(!viewModel.canControl || viewModel.isExecuting || viewModel.isReviewingCalibration
                          || viewModel.calibrationBlockedReason != nil)
            }
            if viewModel.calibrationStep != nil, let reason = viewModel.calibrationBlockedReason {
                Text(reason).font(.footnote)
                    .accessibilityIdentifier("printer.controls.calibration-unavailable")
            }
        }
        .foregroundStyle(Color.pfTextPrimary)
        .onChange(of: viewModel.calibrationStep) { _, step in
            stepFocused = step != nil
        }
        .onDisappear { viewModel.cancelCalibration() }
    }

    @ViewBuilder
    private func steps(_ step: ZOffsetCalibrationStep) -> some View {
        switch step {
        case .introduction:
            Text(
                "Stay at the printer with the bed clear. Firmware saves may restart the printer. Canceling cannot recall an issued command. Do not continue if geometry, homing or the existing offset is unknown."
            )
            .font(.footnote)
            if let offset = viewModel.calibrationOffset {
                Text("Existing stored offset: \(offset.formatted()) mm. This is not a measured nozzle gap.")
                    .font(.footnote)
            }
            ControlActionButton(title: "Continue to Home") { viewModel.beginCalibrationHome() }
                .disabled(
                    viewModel.calibrationBlockedReason != nil || viewModel.calibrationOffset == nil || viewModel.isReviewingCalibration)
        case .home:
            Text(
                "Home all axes. A successful request alone does not establish fresh homing completion. If fresh homing telemetry is unavailable, cancel and use the printer's procedure."
            )
            .font(.footnote)
            ControlActionButton(title: "Home all axes", identifier: "printer.controls.calibration-home") {
                Task { await viewModel.homeForCalibration() }
            }
            .disabled(viewModel.isExecuting || viewModel.calibrationBlockedReason != nil)
        case .position:
            Text("If necessary, lift vertically to verified clearance first, then continue to the verified travel center. No guessed bed size or paper-test height is used.")
                .font(.footnote)
            if let reason = viewModel.calibrationPositionBlockedReason {
                Text(reason).font(.footnote)
            }
            ControlActionButton(title: "Position for calibration", identifier: "printer.controls.calibration-position") {
                Task { await viewModel.positionForCalibration() }
            }
            .disabled(viewModel.isExecuting || viewModel.calibrationPositionBlockedReason != nil)
        case .adjust:
            if let offset = viewModel.calibrationOffset {
                Text("Draft offset: \(offset.formatted()) mm").font(.headline)
            }
            Text("Negative brings the nozzle closer; positive moves it farther away. Draft changes require matching Z-position telemetry.")
                .font(.footnote)
            Picker("Z adjustment increment", selection: $increment) {
                ForEach(MaterialControlInput.increments, id: \.self) { value in
                    Text("\(value.formatted()) mm").tag(value)
                }
            }
            .pickerStyle(.segmented)
            .frame(minHeight: 44)
            ControlActionButton(title: "Closer −\(increment.formatted()) mm", identifier: "printer.controls.calibration-closer") {
                Task { await viewModel.adjustCalibration(delta: -increment) }
            }
            .disabled(viewModel.isExecuting || viewModel.calibrationAdjustmentBlockedReason(delta: -increment) != nil)
            if let reason = viewModel.calibrationAdjustmentBlockedReason(delta: -increment) {
                Text("Closer: \(reason)").font(.footnote)
            }
            ControlActionButton(title: "Farther +\(increment.formatted()) mm", identifier: "printer.controls.calibration-farther") {
                Task { await viewModel.adjustCalibration(delta: increment) }
            }
            .disabled(viewModel.isExecuting || viewModel.calibrationAdjustmentBlockedReason(delta: increment) != nil)
            if let reason = viewModel.calibrationAdjustmentBlockedReason(delta: increment) {
                Text("Farther: \(reason)").font(.footnote)
            }
            ControlActionButton(title: "Refresh and review save", identifier: "printer.controls.calibration-review") {
                Task { await viewModel.reviewCalibration() }
            }
            .disabled(viewModel.isExecuting || viewModel.isReviewingCalibration || viewModel.calibrationPositionBlockedReason != nil)
        case .save:
            Text(
                "Save \(viewModel.calibrationOffset?.formatted() ?? "unknown") mm to firmware and PrintFarmer. A stale review will not be retried. The printer may restart or disconnect; inspect it before printing."
            )
            .font(.footnote)
            if let revision = viewModel.calibrationReview?.rowVersion {
                Text("Reviewed printer revision: \(revision)").font(.caption).textSelection(.enabled)
            }
            ControlActionButton(title: "Save reviewed offset to firmware", identifier: "printer.controls.calibration-save") {
                Task { await viewModel.saveCalibration() }
            }
            .disabled(viewModel.isExecuting || viewModel.calibrationReview == nil || viewModel.calibrationPositionBlockedReason != nil)
        case .done:
            Text(
                "Save request accepted. Verify the firmware offset and first layer at the printer. This app has not measured nozzle clearance or proven calibration quality."
            )
            .font(.footnote)
        }
    }
}

/// The same three children read Heat / Move / Filament on phone, but place
/// Heat + Filament in the leading iPad column without recreating child state.
private struct EssentialControlsColumnsLayout: Layout {
    private let spacing = EssentialControlsStyle.columnSpacing

    func sizeThatFits(proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) -> CGSize {
        let width = proposal.width ?? 760
        let unit = max(0, (width - spacing) / 2.1)
        let sizes = subviews.enumerated().map {
            $0.element.sizeThatFits(ProposedViewSize(width: unit * ($0.offset == 1 ? 1 : 1.1), height: nil))
        }
        precondition(sizes.count == 3, "Essential controls requires thermal, motion and material children")
        return CGSize(width: width, height: max(sizes[0].height + EssentialControlsStyle.groupSpacing + sizes[2].height, sizes[1].height))
    }

    func placeSubviews(in bounds: CGRect, proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) {
        precondition(subviews.count == 3, "Essential controls requires thermal, motion and material children")
        let unit = max(0, (bounds.width - spacing) / 2.1)
        let width = unit * 1.1
        let child = ProposedViewSize(width: width, height: nil)
        let thermalHeight = subviews[0].sizeThatFits(child).height
        subviews[0].place(at: bounds.origin, anchor: .topLeading, proposal: child)
        subviews[1].place(at: CGPoint(x: bounds.minX + width + spacing, y: bounds.minY), anchor: .topLeading,
                          proposal: ProposedViewSize(width: unit, height: nil))
        subviews[2].place(at: CGPoint(x: bounds.minX, y: bounds.minY + thermalHeight + EssentialControlsStyle.groupSpacing),
                          anchor: .topLeading, proposal: child)
    }
}
