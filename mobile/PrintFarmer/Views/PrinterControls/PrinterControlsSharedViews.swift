import SwiftUI

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
                title: "", identifier: "printer.controls.dismiss-error",
                accessibilityTitle: "Dismiss error",
                compact: true, systemImage: "xmark", textSize: 13, textOnly: true
            ) { viewModel.dismissError() }
            .fixedSize(horizontal: true, vertical: false)
        }
        .padding(12)
        .background(Color.pfError.opacity(0.12), in: RoundedRectangle(cornerRadius: 10))
        .accessibilityElement(children: .contain)
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
        let allowsOff = descriptor.allowZero == true && descriptor.quantity == .targetTemperature
        let key = "sensor.\(sensor.id).\(descriptor.quantity.rawValue)"
        let title = Self.title(sensor: sensor, quantity: descriptor.quantity)
        return HeatTargetRow(
            title: title,
            measured: sensor.reading(measuredQuantity)?.value.number,
            identifier: id,
            fieldIdentifier: id + ".value",
            fieldLabel: spokenTitle + (unitSpoken.isEmpty ? "" : " in \(unitSpoken)"),
            // Disabled rows explain why to VoiceOver only; no visible caption (#3548).
            fieldHint: viewModel.sensorRowMessage
                ?? "Double tap to enter a value, then choose Set. Range \(descriptor.min.formatted()) to \(descriptor.max.formatted())"
                + (allowsOff ? "; zero switches it off." : "."),
            stepLabel: { "\($0 ? "Increase" : "Decrease") \(spokenTitle)" },
            unit: descriptor.unit,
            currentTarget: current,
            allowsOff: allowsOff,
            isPending: pending,
            queued: viewModel.queuedHeatTargets[key],
            // At a limit the clamped step equals the base, so ± disables instead of resending it.
            next: { base, increasing in
                base.flatMap { base in
                    let target = PrinterSensorFormatting.steppedTarget(current: base, increasing: increasing, descriptor: descriptor)
                    return target == base ? nil : target
                }
            },
            parse: { text in
                guard let value = try ControlNumberInput.optional(text) else { return nil }
                if let message = viewModel.sensorValueError(sensorId: sensor.id, quantity: descriptor.quantity, value: value) {
                    throw PrinterControlError.invalidRequest(message)
                }
                return value
            },
            schedule: { value, delay in
                viewModel.scheduleHeatTarget(
                    key: key, title: title, value: value, after: delay,
                    isSendable: { $0.canControlSensors && !$0.isPrintingOrPaused }
                ) {
                    await $0.setSensorValue(sensorId: sensor.id, quantity: descriptor.quantity, value: value)
                }
            }
        )
        .disabled(!viewModel.canControlSensors || current == nil)
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
            ControlActionButton(title: "Farther +\(increment.formatted()) mm", identifier: "printer.controls.calibration-farther") {
                Task { await viewModel.adjustCalibration(delta: increment) }
            }
            .disabled(viewModel.isExecuting || viewModel.calibrationAdjustmentBlockedReason(delta: increment) != nil)
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
            Text("Check the first layer before printing.")
                .font(.footnote)
        }
    }
}
