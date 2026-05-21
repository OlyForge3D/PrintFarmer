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

    static let stepOptions: [Double] = [0.1, 1, 10, 100]
    private static let canonicalAxes: [String] = ["X", "Y", "Z"]

    /// Filter the canonical X/Y/Z list to what the backend reports. When capabilities
    /// have not been fetched yet, default to the full list so the UI renders sensibly
    /// during initial load. Empty result → caller should hide the subgroup.
    static func visibleAxes(for capabilities: PrinterBackendCapabilities?) -> [String] {
        guard let caps = capabilities else { return canonicalAxes }
        return canonicalAxes.filter { caps.supportedAxes.contains($0) }
    }

    /// True when the subgroup must be removed from the layout entirely.
    /// Hide if the backend explicitly does not support movement, or if all canonical
    /// axes are filtered out by `supportedAxes`.
    static func isHidden(for capabilities: PrinterBackendCapabilities?) -> Bool {
        guard let caps = capabilities else { return false }
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

            axisPicker
            stepPicker
            jogButtons
        }
        .task {
            await viewModel.loadCapabilities()
        }
        .onChange(of: viewModel.capabilities) { _, newCaps in
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
        return Picker("Jog axis", selection: $selectedAxis) {
            ForEach(axes, id: \.self) { axis in
                Text(axis).tag(axis)
            }
        }
        .pickerStyle(.segmented)
        .accessibilityLabel("Jog axis")
        .accessibilityHint("Choose X, Y, or Z axis to move.")
        .disabled(!viewModel.canControl || viewModel.pendingCommand != nil)
    }

    private var stepPicker: some View {
        Picker("Jog step distance", selection: $selectedStep) {
            ForEach(Self.stepOptions, id: \.self) { step in
                Text(stepLabel(step)).tag(step)
            }
        }
        .pickerStyle(.segmented)
        .accessibilityLabel("Jog step distance")
        .accessibilityHint("Choose how many millimeters each tap moves.")
        .disabled(!viewModel.canControl || viewModel.pendingCommand != nil)
    }

    private var jogButtons: some View {
        HStack(spacing: 8) {
            jogButton(direction: -1, symbol: "minus.circle.fill", labelText: "Jog backward")
            jogButton(direction: 1, symbol: "plus.circle.fill", labelText: "Jog forward")
        }
    }

    @ViewBuilder
    private func jogButton(direction: Double, symbol: String, labelText: String) -> some View {
        let isPending = isPendingJog(direction: direction)
        let signedStep = direction * selectedStep
        let stepLabelText = stepLabel(selectedStep)
        let signLabel = direction > 0 ? "plus" : "minus"

        Button {
            Task { await viewModel.jog(axis: selectedAxis, distanceMm: signedStep) }
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
        .disabled(!viewModel.canControl || viewModel.pendingCommand != nil)
        .accessibilityLabel("Jog \(selectedAxis) \(signLabel) \(stepLabelText) millimeters")
        .accessibilityHint("Moves \(selectedAxis) \(signLabel) \(stepLabelText) millimeters.")
        .accessibilityValue(viewModel.pendingCommand != nil ? "Pending" : "")
    }

    // MARK: - Helpers

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
