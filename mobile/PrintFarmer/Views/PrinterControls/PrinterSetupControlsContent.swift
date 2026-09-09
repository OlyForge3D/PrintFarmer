import SwiftUI

/// Presentation-only setup controls. The host owns the model, capability loading,
/// access gating and live-update forwarding even while this content is offscreen.
/// Use `PrinterControlsSection(printer:composition:)` for standalone ownership.
struct PrinterSetupControlsContent: View {
    let printer: Printer
    @ObservedObject var viewModel: PrinterControlsViewModel
    var usesColumns: Bool? = nil
    var materialPresentation: PrinterFilamentPresentation? = nil
    @Environment(\.horizontalSizeClass) private var horizontalSizeClass
    @Environment(\.dynamicTypeSize) private var dynamicTypeSize

    private var isPrintingOrPaused: Bool {
        switch printer.state?.lowercased() {
        case "printing", "paused", "starting": return true
        default: return false
        }
    }

    var body: some View {
        if !PrinterControlsSection.isHidden(for: printer) {
            content
        }
    }

    private var content: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Controls")
                .font(.title3.weight(.semibold))
                .foregroundStyle(Color.pfTextPrimary)
                .accessibilityAddTraits(.isHeader)

            VStack(alignment: .leading, spacing: 0) {
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
                    ? AnyLayout(HStackLayout(alignment: .top, spacing: 16))
                    : AnyLayout(VStackLayout(alignment: .leading, spacing: 16))
                // Keep Jog's axis/distance state when width or text size reflows.
                layout {
                    VStack(alignment: .leading, spacing: 16) {
                        PreheatSubgroup(viewModel: viewModel)
                        PreheatSubgroup.IndividualHeaterControls(viewModel: viewModel)
                        Divider()
                        if let materialPresentation {
                            PrinterFilamentSection(presentation: materialPresentation, actions: [], onAction: { _ in })
                        }
                        PrinterMaterialControls(viewModel: viewModel)
                    }
                    .frame(maxWidth: .infinity, alignment: .leading)
                    VStack(alignment: .leading, spacing: 12) {
                        HomeSubgroup(viewModel: viewModel)
                        Divider()
                            .background(Color.pfBorder)
                        JogSubgroup(viewModel: viewModel)
                        JogSubgroup.AbsolutePositionControls(viewModel: viewModel)
                        HomeSubgroup.MotorReleaseControls(viewModel: viewModel)
                        Divider()
                        PrinterZOffsetCalibrationControls(viewModel: viewModel)
                    }
                    .frame(maxWidth: .infinity, alignment: .leading)
                }

                if let notice = viewModel.commandNotice {
                    Text(notice)
                        .font(.footnote)
                        .foregroundStyle(Color.pfTextSecondary)
                        .padding(.top, 12)
                }
                if viewModel.pendingCommand != nil {
                    ControlActionButton(
                        title: "Stop waiting for command", identifier: "printer.controls.stop-waiting",
                        hint: "Does not stop the printer. Physical execution may continue."
                    ) { viewModel.cancelPendingCommand() }
                }
                if let error = viewModel.lastError {
                    errorBanner(error)
                        .padding(.top, 12)
                }
            }
            .padding()
            .background(Color.pfCard, in: RoundedRectangle(cornerRadius: 12))
            .overlay(
                RoundedRectangle(cornerRadius: 12)
                    .strokeBorder(Color.pfBorder, lineWidth: 1)
            )
        }
    }

    private var lockoutBanner: some View {
        HStack(spacing: 8) {
            Image(systemName: "lock.fill")
                .foregroundStyle(Color.pfWarning)
            Text("Controls are disabled while a print is active.")
                .font(.footnote)
                .foregroundStyle(Color.pfTextPrimary)
        }
        .padding(10)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(Color.pfWarning.opacity(0.12), in: RoundedRectangle(cornerRadius: 8))
        .accessibilityElement(children: .combine)
        .accessibilityLabel(
            String(localized: "Controls disabled, print is active.",
                   comment: "VoiceOver: lockout banner during print")
        )
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
            Spacer()
            Button {
                viewModel.dismissError()
            } label: {
                Text("Dismiss")
                    .font(.footnote)
                    .frame(minWidth: 44, minHeight: 44)
                    .contentShape(Rectangle())
            }
            .buttonStyle(.borderless)
        }
        .padding(10)
        .background(Color.pfError.opacity(0.12), in: RoundedRectangle(cornerRadius: 8))
        .accessibilityElement(children: .contain)
    }
}

/// Physical material commands deliberately never receive an inventory owner.
struct PrinterMaterialControls: View {
    @ObservedObject var viewModel: PrinterControlsViewModel
    @State private var distance = 10.0
    @State private var speed = 1
    @State private var confirmation: PhysicalFilamentOperation?

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Physical filament")
                .font(.headline)
                .accessibilityAddTraits(.isHeader)
            Text(
                "Printer-level commands only. No tool, lane or MMU slot is selected. Assign / Change spool and Clear assignment manage inventory; they do not load or unload filament. NFC and combined Eject remain separate."
            )
            .font(.footnote)
            .foregroundStyle(Color.pfTextSecondary)
            Picker("Extrusion distance", selection: $distance) {
                ForEach(MaterialControlInput.distances, id: \.self) { value in
                    Text("\(Int(value)) mm").tag(value)
                }
            }
            .accessibilityIdentifier("printer.controls.extrusion-distance")
            .frame(minHeight: 44)
            Picker("Extrusion speed", selection: $speed) {
                ForEach(MaterialControlInput.speeds, id: \.self) { value in
                    Text("\(value) mm/s").tag(value)
                }
            }
            .accessibilityIdentifier("printer.controls.extrusion-speed")
            .frame(minHeight: 44)
            if let reason = viewModel.extrusionBlockedReason {
                Text(reason)
                    .font(.footnote)
                    .accessibilityIdentifier("printer.controls.extrusion-unavailable")
            }
            ControlActionButton(title: "Extrude \(Int(distance)) mm", identifier: "printer.controls.extrude") {
                Task { await viewModel.extrude(distanceMm: distance, speedMmPerSecond: speed) }
            }
            .disabled(viewModel.isExecuting || viewModel.extrusionBlockedReason != nil)
            ControlActionButton(title: "Retract \(Int(distance)) mm", identifier: "printer.controls.retract") {
                Task { await viewModel.extrude(distanceMm: -distance, speedMmPerSecond: speed) }
            }
            .disabled(viewModel.isExecuting || viewModel.extrusionBlockedReason != nil)
            Text(
                "Use the Hotend controls above to preheat when appropriate. A hot target does not prove the nozzle is hot or safe for the physical material."
            )
            .font(.footnote)
            .foregroundStyle(Color.pfTextSecondary)
            ForEach(PhysicalFilamentOperation.allCases) { operation in
                VStack(alignment: .leading, spacing: 4) {
                    ControlActionButton(
                        title: operation.title,
                        identifier: "printer.controls.filament-\(operation.rawValue)",
                        hint: "Requests a physical printer operation, not a spool assignment change."
                    ) { confirmation = operation }
                    .disabled(viewModel.isExecuting || viewModel.filamentBlockedReason(operation) != nil)
                    if let reason = viewModel.filamentBlockedReason(operation) {
                        Text(reason).font(.footnote).foregroundStyle(Color.pfTextSecondary)
                    }
                }
            }
        }
        .foregroundStyle(Color.pfTextPrimary)
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

/// Inline rather than a modal: the detail host's independent, confirmed
/// Emergency Stop remains reachable throughout calibration.
struct PrinterZOffsetCalibrationControls: View {
    @ObservedObject var viewModel: PrinterControlsViewModel
    @State private var increment = 0.05

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Z-offset calibration")
                .font(.headline)
                .accessibilityAddTraits(.isHeader)
            if let reason = viewModel.calibrationBlockedReason {
                Text(reason).font(.footnote)
                    .accessibilityIdentifier("printer.controls.calibration-unavailable")
            }
            if let step = viewModel.calibrationStep {
                Text("Step: \(step.rawValue.capitalized)")
                    .font(.headline)
                    .accessibilityAddTraits(.isHeader)
                    .accessibilityIdentifier("printer.controls.calibration-step")
                steps(step)
                if let message = viewModel.calibrationMessage {
                    Text(message).font(.footnote)
                }
                ControlActionButton(
                    title: step == .done ? "Close calibration" : "Cancel calibration",
                    identifier: "printer.controls.calibration-cancel",
                    hint: "Stops the workflow, not physical motion. Emergency Stop remains separate."
                ) { viewModel.cancelCalibration() }
            } else {
                Text(
                    "Introduction → Home → Position → Adjust → Save → Done. Negative offsets bring the nozzle closer. Never move to a guessed bed center."
                )
                .font(.footnote)
                ControlActionButton(title: "Review calibration", identifier: "printer.controls.calibration-start") {
                    Task { await viewModel.startCalibration() }
                }
                .disabled(!viewModel.canControl || viewModel.isExecuting || viewModel.isReviewingCalibration)
            }
        }
        .foregroundStyle(Color.pfTextPrimary)
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
            if let reason = viewModel.calibrationPositionBlockedReason {
                Text(reason).font(.footnote)
            }
            ControlActionButton(title: "Position for calibration", identifier: "printer.controls.calibration-position") {
                Task { await viewModel.positionForCalibration() }
            }
            .disabled(viewModel.isExecuting || viewModel.calibrationPositionBlockedReason != nil)
        case .adjust:
            Text("Negative brings the nozzle closer; positive moves it farther away. Draft changes require matching Z-position telemetry.")
                .font(.footnote)
            Picker("Z adjustment increment", selection: $increment) {
                ForEach(MaterialControlInput.increments, id: \.self) { value in
                    Text("\(value.formatted()) mm").tag(value)
                }
            }
            .frame(minHeight: 44)
            ControlActionButton(title: "Closer −\(increment.formatted()) mm") {
                Task { await viewModel.adjustCalibration(delta: -increment) }
            }
            .disabled(viewModel.isExecuting || viewModel.calibrationPositionBlockedReason != nil)
            ControlActionButton(title: "Farther +\(increment.formatted()) mm") {
                Task { await viewModel.adjustCalibration(delta: increment) }
            }
            .disabled(viewModel.isExecuting || viewModel.calibrationPositionBlockedReason != nil)
            ControlActionButton(title: "Refresh and review save") {
                Task { await viewModel.reviewCalibration() }
            }
            .disabled(viewModel.isExecuting || viewModel.isReviewingCalibration)
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
            .disabled(viewModel.isExecuting || viewModel.calibrationReview == nil || viewModel.calibrationBlockedReason != nil)
        case .done:
            Text(
                "Save request accepted. Verify the firmware offset and first layer at the printer. This app has not measured nozzle clearance or proven calibration quality."
            )
            .font(.footnote)
        }
    }
}