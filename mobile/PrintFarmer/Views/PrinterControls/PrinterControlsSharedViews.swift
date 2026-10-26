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

/// Inputs for writable non-fan sensor entries. Fans are not adjustable from the
/// app; read-only entries and descriptors never get controls.
struct PrinterSensorControlRows: View {
    @ObservedObject var viewModel: PrinterControlsViewModel
    var compact: Bool
    /// Heat passes its shared value column so sensor and heater steppers align.
    var valueColumnWidth: CGFloat?

    var body: some View {
        let activeExtruderId = viewModel.printer.activeExtruderId
        let sensors = viewModel.controllableSensors.filter {
            !PrinterSensorFormatting.isHidden($0) && !PrinterSensorFormatting.isFan($0)
                && !PrinterSensorFormatting.steppableDescriptors($0, activeExtruderId: activeExtruderId).isEmpty
        }
        if !sensors.isEmpty {
            ForEach(sensors) { sensor in
                ForEach(PrinterSensorFormatting.steppableDescriptors(sensor, activeExtruderId: activeExtruderId), id: \.quantity) { descriptor in
                    row(sensor: sensor, descriptor: descriptor)
                }
            }
        }
    }

    private func row(sensor: PrinterSensor, descriptor: SensorWritable) -> some View {
        let reading = sensor.reading(descriptor.quantity)
        let current = reading?.value.number
        let canAdjust = viewModel.sensorControlUnavailableReason == nil
        let title = "\(PrinterSensorFormatting.displayName(sensor.displayName)) \(PrinterSensorFormatting.quantityTitle(descriptor.quantity).lowercased())"
        let id = "printer.controls.sensor.\(sensor.id).\(descriptor.quantity.rawValue)"
        return VStack(alignment: .leading, spacing: 4) {
            HStack(spacing: 8) {
                Text(title).font(.subheadline.weight(.medium))
                Spacer(minLength: 0)
                HStack(spacing: valueColumnWidth == nil ? 8 : 4) {
                    if let current, canAdjust {
                        stepButton("minus", "Decrease \(title)", id + ".decrease",
                                   increasing: false, current: current, descriptor: descriptor, sensor: sensor)
                    }
                    Text(reading.map(PrinterSensorFormatting.valueText) ?? PrinterDetailViewModel.missingValuePlaceholder)
                        .font(.subheadline.monospacedDigit())
                        .frame(minWidth: valueColumnWidth)
                        .accessibilityLabel(reading.map { "\(title), \(PrinterSensorFormatting.spokenValue($0))" } ?? "\(title), not reported")
                        .accessibilityIdentifier(id + ".value")
                    if let current, canAdjust {
                        stepButton("plus", "Increase \(title)", id + ".increase",
                                   increasing: true, current: current, descriptor: descriptor, sensor: sensor)
                    }
                }
            }
        }
        .accessibilityElement(children: .contain)
    }

    private func stepButton(
        _ symbol: String, _ label: String, _ identifier: String,
        increasing: Bool, current: Double, descriptor: SensorWritable, sensor: PrinterSensor
    ) -> some View {
        let clamped = PrinterSensorFormatting.steppedTarget(current: current, increasing: increasing, descriptor: descriptor)
        // Native 44 pt stepper, matching the other Heat controls.
        return ControlActionButton(
            title: "",
            identifier: identifier,
            accessibilityTitle: label,
            compact: true,
            systemImage: symbol,
            minimumHeight: 44
        ) {
            Task { await viewModel.setSensorValue(sensorId: sensor.id, quantity: descriptor.quantity, value: clamped) }
        }
        .frame(minWidth: 44, minHeight: 44)
        .fixedSize()
        .disabled(viewModel.pendingCommand != nil)
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
            Text(
                "Save request accepted. Verify the firmware offset and first layer at the printer. This app has not measured nozzle clearance or proven calibration quality."
            )
            .font(.footnote)
        }
    }
}
