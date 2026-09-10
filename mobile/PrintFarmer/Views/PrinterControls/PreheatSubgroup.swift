import SwiftUI

/// Rounded SwiftUI fields keep a 34-point UIKit editor even inside a taller
/// frame. Size the native editor itself so both touch and VoiceOver get 44pt.
struct ControlNumberField: UIViewRepresentable {
    let placeholder: String
    @Binding var text: String
    let label: String
    let identifier: String
    var hint: String?
    @ScaledMetric(relativeTo: .body) private var fontSize: CGFloat = 17
    @Environment(\.isEnabled) private var isEnabled

    func makeUIView(context: Context) -> UITextField {
        let field = UITextField()
        field.borderStyle = .roundedRect
        field.keyboardType = .numbersAndPunctuation
        field.returnKeyType = .done
        field.autocorrectionType = .no
        field.delegate = context.coordinator
        field.addTarget(context.coordinator, action: #selector(Coordinator.changed(_:)), for: .editingChanged)
        return field
    }

    func updateUIView(_ field: UITextField, context: Context) {
        context.coordinator.text = $text
        if field.text != text { field.text = text }
        field.placeholder = placeholder
        field.font = .systemFont(ofSize: fontSize)
        field.isEnabled = isEnabled
        field.accessibilityLabel = label
        field.accessibilityIdentifier = identifier
        field.accessibilityHint = hint
        field.textColor = UIColor(Color.pfTextPrimary)
        field.backgroundColor = UIColor(Color.pfBackgroundTertiary)
    }

    func sizeThatFits(_ proposal: ProposedViewSize, uiView: UITextField, context: Context) -> CGSize? {
        CGSize(width: proposal.width ?? 200, height: max(44, ceil(uiView.font?.lineHeight ?? fontSize) + 16))
    }

    func makeCoordinator() -> Coordinator { Coordinator(text: $text) }

    final class Coordinator: NSObject, UITextFieldDelegate {
        var text: Binding<String>
        init(text: Binding<String>) { self.text = text }
        @objc func changed(_ field: UITextField) { text.wrappedValue = field.text ?? "" }
        func textFieldShouldReturn(_ textField: UITextField) -> Bool {
            textField.resignFirstResponder()
            return true
        }
    }
}

struct ControlActionButton: UIViewRepresentable {
    let title: String
    var identifier = ""
    var accessibilityTitle: String?
    var hint: String?
    var selected = false
    var isDestructive = false
    var compact = false
    let action: () -> Void
    @ScaledMetric(relativeTo: .body) private var fontSize: CGFloat = 17
    @Environment(\.isEnabled) private var isEnabled

    func makeUIView(context: Context) -> UIButton {
        let button = UIButton(type: .system)
        button.addTarget(context.coordinator, action: #selector(Coordinator.activate), for: .touchUpInside)
        return button
    }

    func updateUIView(_ button: UIButton, context: Context) {
        context.coordinator.action = action
        var configuration = UIButton.Configuration.gray()
        configuration.title = title
        configuration.buttonSize = .large
        if compact {
            configuration.contentInsets = .init(top: 8, leading: 8, bottom: 8, trailing: 8)
        }
        configuration.cornerStyle = .medium
        configuration.baseForegroundColor = UIColor(isDestructive ? Color.pfError : Color.pfTextPrimary)
        configuration.background.strokeColor = UIColor(selected ? Color.pfTextPrimary : Color.clear)
        configuration.background.strokeWidth = 1
        let font = UIFont.systemFont(ofSize: fontSize)
        configuration.titleTextAttributesTransformer = UIConfigurationTextAttributesTransformer {
            var attributes = $0
            attributes.font = font
            return attributes
        }
        button.configuration = configuration
        button.titleLabel?.numberOfLines = 0
        button.isSelected = selected
        button.isEnabled = isEnabled
        button.accessibilityLabel = accessibilityTitle ?? title
        button.accessibilityHint = hint
        button.accessibilityIdentifier = identifier
    }

    func sizeThatFits(_ proposal: ProposedViewSize, uiView: UIButton, context: Context) -> CGSize? {
        let size = uiView.sizeThatFits(CGSize(
            width: proposal.width ?? .greatestFiniteMagnitude, height: .greatestFiniteMagnitude
        ))
        return CGSize(width: max(44, proposal.width ?? size.width), height: max(44, size.height))
    }

    func makeCoordinator() -> Coordinator { Coordinator(action: action) }

    final class Coordinator: NSObject {
        var action: () -> Void
        init(action: @escaping () -> Void) { self.action = action }
        @objc func activate() { action() }
    }
}

/// Standalone Preheat subgroup of the Printer Controls section. Renders four
/// buttons (PLA, PETG, ABS, Cool Down) bound to `PrinterControlsViewModel`.
///
/// Spec: `mobile/docs/design/printer-controls-section.md` §2.3 (Preheat),
/// §2.4 (states), §4 (accessibility). Issue #284.
///
/// This file owns *only* the Preheat subgroup. The parent `PrinterControlsSection`
/// (issue #287) is responsible for layout in `PrinterDetailView`, the lockout
/// banner, dividers, and the surrounding subgroups (Home, Jog).
struct PreheatSubgroup: View {

    @ObservedObject var viewModel: PrinterControlsViewModel

    /// Transient caption shown when the user taps a button while controls are
    /// disabled (offline / mid-print). Cleared after a few seconds. Phone
    /// equivalent of the `.help()` tooltip that appears on iPad/Mac.
    @State private var disabledTapMessage: String?

    @Environment(\.horizontalSizeClass) private var horizontalSizeClass
    @Environment(\.dynamicTypeSize) private var dynamicTypeSize

    /// Fixed display order matching the UX spec.
    static let presets: [PreheatPreset] = [.pla, .petg, .abs, .coolDown]

    /// Presets require explicit hotend-control evidence, including Cool Down.
    static func isVisible(capabilities: PrinterBackendCapabilities?) -> Bool {
        capabilities?.supportsTemperatureControl == true
    }

    var body: some View {
        if Self.isVisible(capabilities: viewModel.capabilities) {
            content
        } else {
            EmptyView()
        }
    }

    private var content: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("Preheat")
                .font(.headline)
                .foregroundStyle(Color.pfTextPrimary)
                .accessibilityAddTraits(.isHeader)

            if !viewModel.supports(.bed) {
                Text("Hotend only — bed temperature control is unavailable.")
                    .font(.caption)
                    .foregroundStyle(Color.pfTextSecondary)
            }

            grid

            if let message = blockedReasonMessage {
                Text(message)
                    .font(.footnote)
                    .foregroundStyle(Color.pfTextSecondary)
                    .transition(.opacity)
                    .accessibilityAddTraits(.isStaticText)
            }
        }
    }

    @ViewBuilder
    private var grid: some View {
        let columns = gridColumns
        LazyVGrid(columns: columns, alignment: .leading, spacing: 8) {
            ForEach(Self.presets, id: \.self) { preset in
                button(for: preset)
            }
        }
    }

    private var gridColumns: [GridItem] {
        let count: Int
        if dynamicTypeSize.isAccessibilitySize {
            count = 1
        } else if dynamicTypeSize >= .xxLarge {
            count = 2
        } else {
            count = 4
        }
        return Array(repeating: GridItem(.flexible(), spacing: 8), count: count)
    }

    @ViewBuilder
    private func button(for preset: PreheatPreset) -> some View {
        let isPending: Bool = {
            if case let .preheat(pendingPreset, _, _) = viewModel.pendingCommand?.kind {
                return pendingPreset == preset
            }
            return false
        }()
        let canControl = viewModel.canControl
        let limitsReason = viewModel.preheatBlockedReason(preset)
        // Per spec §3.1 single-flight queue: if any preheat command is in
        // flight, *all* preheat siblings disable so the user can't stack
        // burst commands like "PLA then ABS".
        let isAnyPreheatInProgress = viewModel.isExecuting
        let isInteractive = canControl && !isAnyPreheatInProgress && limitsReason == nil

        let hasError = isErrored(preset: preset)
        Button {
            handleTap(preset: preset, canControl: canControl)
        } label: {
            buttonLabel(preset: preset, isPending: isPending)
        }
        .buttonStyle(PreheatButtonStyle(
            preset: preset, isEnabled: isInteractive, isPending: isPending,
            compact: !dynamicTypeSize.isAccessibilitySize
        ))
        .disabled(isAnyPreheatInProgress || limitsReason != nil || isBlockedWithoutTapReveal(canControl: canControl))
        // On compact layouts we keep blocked buttons tappable so the user can
        // reveal the disabled reason; regular width shows the reason inline.
        .disabledControlStyle(isDisabled: !isInteractive && !isPending, cornerRadius: 8)
        .errorBorderHighlight(isActive: hasError, cornerRadius: 8)
        .accessibilityLabel(accessibilityLabel(preset: preset, isPending: isPending))
        .accessibilityHint(limitsReason ?? accessibilityHint(preset: preset, canControl: canControl, hasError: hasError))
        .accessibilityValue(accessibilityValue(isPending: isPending, hasError: hasError))
        .accessibilityAddTraits(isPending ? .updatesFrequently : [])
        .help(limitsReason ?? viewModel.blockedReason ?? "")
    }

    struct IndividualHeaterControls: View {
        @ObservedObject var viewModel: PrinterControlsViewModel

        var body: some View {
            VStack(alignment: .leading, spacing: 8) {
                ForEach(Heater.allCases, id: \.self) { heater in
                    if viewModel.supports(heater) {
                        HeaterTargetEditor(viewModel: viewModel, heater: heater)
                    }
                }
            }
        }
    }

    struct HeaterTargetEditor: View {
        @ObservedObject var viewModel: PrinterControlsViewModel
        let heater: Heater
        @State private var target = ""
        @State private var inputError: String?
        @Environment(\.dynamicTypeSize) private var dynamicTypeSize

        static func temperatureText(_ value: Double?) -> String {
            guard let value, value.isFinite else { return "Unknown" }
            return "\(value.formatted(.number.precision(.fractionLength(0...1)))) °C"
        }

        var body: some View {
            VStack(alignment: .leading, spacing: 8) {
                if dynamicTypeSize.isAccessibilitySize {
                    stackedEditor
                } else {
                    ViewThatFits(in: .horizontal) {
                        HStack(alignment: .bottom, spacing: 8) {
                            reading
                                .frame(minWidth: 112, alignment: .leading)
                                .fixedSize(horizontal: true, vertical: false)
                            targetEditor.frame(width: 88)
                            actions.fixedSize(horizontal: true, vertical: false)
                        }
                        stackedEditor
                    }
                }
                if viewModel.maximum(for: heater) == nil {
                    Text("Heating unavailable. Retry heater limits; Off is still available.")
                        .font(.footnote)
                        .foregroundStyle(Color.pfTextSecondary)
                        .fixedSize(horizontal: false, vertical: true)
                }
                if let inputError {
                    Text(inputError)
                        .font(.footnote)
                        .foregroundStyle(Color.pfError)
                        .fixedSize(horizontal: false, vertical: true)
                }
            }
            .foregroundStyle(Color.pfTextPrimary)
            .disabled(!viewModel.canControl || viewModel.isExecuting)
        }

        private var reading: some View {
            VStack(alignment: .leading, spacing: 2) {
                Text(heater.title)
                    .font(.subheadline.weight(.semibold))
                    .accessibilityAddTraits(.isHeader)
                Text("Current: \(Self.temperatureText(actual))")
                    .font(.caption.monospacedDigit())
                    .foregroundStyle(Color.pfTextSecondary)
                    .accessibilityLabel("\(heater.title) current temperature, \(Self.temperatureText(actual))")
            }
            .fixedSize(horizontal: false, vertical: true)
        }

        private var targetEditor: some View {
            VStack(alignment: .leading, spacing: 2) {
                Text("Target: \(Self.temperatureText(setpoint))")
                    .font(.caption.monospacedDigit())
                    .foregroundStyle(Color.pfTextSecondary)
                    .fixedSize(horizontal: false, vertical: true)
                ControlNumberField(
                    placeholder: "New °C", text: $target,
                    label: "\(heater.title) target in degrees Celsius",
                    identifier: "printer.controls.\(heater.rawValue).target",
                    hint: targetHint
                )
            }
        }

        private var targetHint: String {
            let limit = viewModel.maximum(for: heater).map { "Maximum \($0) degrees. " } ?? ""
            return limit + ControlNumberInput.heaterPrecisionMessage
        }

        private var stackedEditor: some View {
            VStack(alignment: .leading, spacing: 8) {
                reading
                targetEditor
                actions
            }
        }

        private var actions: some View {
            HStack(spacing: 8) {
                ControlActionButton(
                    title: "Set", identifier: "printer.controls.\(heater.rawValue).set",
                    accessibilityTitle: "Set \(heater.title.lowercased()) target", compact: true
                ) {
                    do {
                        guard let value = try ControlNumberInput.heaterTarget(target) else {
                            throw PrinterControlError.invalidRequest("Enter a target or choose Off.")
                        }
                        if let message = viewModel.heaterTargetError(heater, target: value) {
                            throw PrinterControlError.invalidRequest(message)
                        }
                        inputError = nil
                        Task { await viewModel.setHeaterTarget(heater, target: value) }
                    } catch {
                        inputError = error.localizedDescription
                    }
                }
                ControlActionButton(
                    title: "Off", identifier: "printer.controls.\(heater.rawValue).off",
                    accessibilityTitle: "Turn \(heater.title.lowercased()) off",
                    hint: "Sets only this heater to zero degrees.", compact: true
                ) {
                    inputError = nil
                    Task { await viewModel.setHeaterTarget(heater, target: 0) }
                }
            }
        }

        private var actual: Double? {
            heater == .hotend ? viewModel.printer.hotendTemp : viewModel.printer.bedTemp
        }

        private var setpoint: Double? {
            heater == .hotend ? viewModel.printer.hotendTarget : viewModel.printer.bedTarget
        }
    }

    private var blockedReasonMessage: String? {
        if horizontalSizeClass == .regular {
            return viewModel.blockedReason
        }
        return disabledTapMessage
    }

    private var shouldRevealDisabledTooltipOnTap: Bool {
        horizontalSizeClass != .regular
    }

    private func isBlockedWithoutTapReveal(canControl: Bool) -> Bool {
        !canControl && !shouldRevealDisabledTooltipOnTap
    }

    private func buttonLabel(preset: PreheatPreset, isPending: Bool) -> some View {
        VStack(spacing: 2) {
            Text(preset == .coolDown && !dynamicTypeSize.isAccessibilitySize ? "Cool" : preset.displayLabel)
                .font(.subheadline.weight(.medium))
            Text(viewModel.supports(.bed) ? preset.temperatureLabel : "\(Int(preset.hotend))°")
                .font(.caption2.monospacedDigit())
                .opacity(isPending ? 0 : 1) // hide values during pending; spinner takes over
        }
        .frame(maxWidth: .infinity, minHeight: 44) // 44pt HIG hit target
        .overlay {
            if isPending {
                ProgressView()
                    .controlSize(.small)
                    .tint(Color.pfButtonPrimaryText)
            }
        }
    }

    private func handleTap(preset: PreheatPreset, canControl: Bool) {
        guard viewModel.preheatBlockedReason(preset) == nil else { return }
        guard canControl else {
            // Disabled tap: surface the blocked reason as a transient caption
            // (phone) and let `.help()` cover iPad/Mac hover.
            let message = viewModel.blockedReason ?? "Controls are unavailable."
            withAnimation(.easeInOut(duration: 0.15)) {
                disabledTapMessage = message
            }
            // Auto-clear after a moment so it doesn't linger.
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
        Task { await viewModel.preheat(preset) }
    }

    // MARK: - Accessibility strings

    // Exposed internal (not private) so accessibility tests can validate
    // that label and hint strings match spec §4.1 exactly.
    func accessibilityLabel(preset: PreheatPreset, isPending: Bool) -> String {
        if isPending {
            return preset == .coolDown
                ? String(localized: "Cooling down, in progress", comment: "VoiceOver: Cool Down while in flight")
                : String(localized: "Preheat for \(preset.spokenName), in progress", comment: "VoiceOver: preheat button while in flight")
        }
        if preset == .coolDown {
            return String(localized: "Cool down", comment: "VoiceOver: Cool Down idle label per spec §4.1")
        }
        return String(localized: "Preheat for \(preset.spokenName)", comment: "VoiceOver: preheat button idle label per spec §4.1")
    }

    func accessibilityHint(preset: PreheatPreset, canControl: Bool, hasError: Bool) -> String {
        if hasError, let message = viewModel.lastError?.message {
            return String(localized: "Failed: \(message). Double tap to retry.", comment: "VoiceOver hint when last preheat command failed")
        }
        if !canControl {
            return String(localized: "Disabled while printing.", comment: "VoiceOver disabled hint per spec §4.1")
        }
        return preset.a11yHint(hasBed: viewModel.supports(.bed))
    }

    func accessibilityValue(isPending: Bool, hasError: Bool) -> String {
        if isPending { return String(localized: "Pending", comment: "VoiceOver value while a control command is in flight per spec §4.1") }
        if hasError { return String(localized: "Failed", comment: "VoiceOver value when last command failed") }
        return ""
    }

    private func isErrored(preset: PreheatPreset) -> Bool {
        guard let last = viewModel.lastError else { return false }
        if case let .preheat(errPreset, _, _) = last.command.kind, errPreset == preset { return true }
        return false
    }
}

// MARK: - Preset display helpers

private extension PreheatPreset {
    var displayLabel: String {
        switch self {
        case .pla: return "PLA"
        case .petg: return "PETG"
        case .abs: return "ABS"
        case .coolDown: return "Cool Down"
        }
    }

    var spokenName: String {
        switch self {
        case .pla: return "PLA"
        case .petg: return "PETG"
        case .abs: return "ABS"
        case .coolDown: return "cool down"
        }
    }

    var temperatureLabel: String {
        "\(Int(hotend))° / \(Int(bed))°"
    }

    /// VoiceOver idle hint per spec §4.1. Omits bed segment when printer has no heated bed.
    func a11yHint(hasBed: Bool) -> String {
        switch self {
        case .coolDown:
            return hasBed
                ? String(localized: "Sets hotend and bed to 0 degrees.", comment: "VoiceOver: Cool Down idle hint")
                : String(localized: "Sets hotend to 0 degrees.", comment: "VoiceOver: Cool Down hotend-only hint")
        case .pla, .petg, .abs:
            if hasBed {
                return String(localized: "Sets hotend to \(Int(hotend)) degrees, bed to \(Int(bed)) degrees.", comment: "VoiceOver: preheat idle hint with bed")
            } else {
                return String(localized: "Sets hotend to \(Int(hotend)) degrees.", comment: "VoiceOver: preheat idle hint, hotend-only")
            }
        }
    }
}

// MARK: - Button style

/// Preheat-specific button style with per-state visual treatment per spec §2.4.
private struct PreheatButtonStyle: ButtonStyle {
    let preset: PreheatPreset
    let isEnabled: Bool
    let isPending: Bool
    let compact: Bool

    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .padding(.horizontal, compact ? 4 : 8)
            .padding(.vertical, compact ? 0 : 8)
            .background(background)
            .foregroundStyle(foreground)
            .overlay(border)
            .clipShape(RoundedRectangle(cornerRadius: 8, style: .continuous))
            .opacity(configuration.isPressed && isEnabled ? 0.7 : 1.0)
            .contentShape(Rectangle())
    }

    private var background: some View {
        Group {
            if isPending {
                Color.pfAssigned
            } else if !isEnabled {
                Color.pfBackgroundTertiary
            } else if preset == .coolDown {
                Color.pfSecondaryAccent
            } else {
                Color.pfButtonPrimary
            }
        }
    }

    private var foreground: Color {
        if !isEnabled && !isPending { return Color.pfTextTertiary }
        return Color.pfButtonPrimaryText
    }

    @ViewBuilder
    private var border: some View {
        RoundedRectangle(cornerRadius: 8, style: .continuous)
            .strokeBorder(
                isPending ? Color.pfAssigned : Color.clear,
                lineWidth: isPending ? 1.5 : 0
            )
    }
}

// MARK: - Previews

#Preview("Default — full caps, idle") {
    PreheatSubgroupPreviewHost(
        capabilities: .init(
            supportsMovement: true,
            supportsTemperatureControl: true,
            supportsBedTemperature: true,
            supportsFanControl: true,
            supportsHoming: true,
            supportedAxes: ["X", "Y", "Z"]
        ),
        printerState: "ready",
        isOnline: true,
        startPendingPreset: nil
    )
    .padding(16)
    .background(Color.pfCard)
    .preferredColorScheme(.dark)
}

#Preview("Pending — PLA in flight") {
    PreheatSubgroupPreviewHost(
        capabilities: .init(
            supportsMovement: true,
            supportsTemperatureControl: true,
            supportsBedTemperature: true,
            supportsFanControl: true,
            supportsHoming: true,
            supportedAxes: ["X", "Y", "Z"]
        ),
        printerState: "ready",
        isOnline: true,
        startPendingPreset: .pla
    )
    .padding(16)
    .background(Color.pfCard)
    .preferredColorScheme(.dark)
}

#Preview("Disabled — printing") {
    PreheatSubgroupPreviewHost(
        capabilities: .init(
            supportsMovement: true,
            supportsTemperatureControl: true,
            supportsBedTemperature: true,
            supportsFanControl: true,
            supportsHoming: true,
            supportedAxes: ["X", "Y", "Z"]
        ),
        printerState: "printing",
        isOnline: true,
        startPendingPreset: nil
    )
    .padding(16)
    .background(Color.pfCard)
    .preferredColorScheme(.dark)
}

#Preview("Hotend only — FlashForge") {
    PreheatSubgroupPreviewHost(
        capabilities: .init(
            supportsMovement: true,
            supportsTemperatureControl: true,
            supportsBedTemperature: false,
            supportsFanControl: false,
            supportsHoming: true,
            supportedAxes: ["X", "Y", "Z"]
        ),
        printerState: "ready",
        isOnline: true,
        startPendingPreset: nil
    )
    .padding(16)
    .background(Color.pfCard)
    .preferredColorScheme(.dark)
}

#Preview("Hidden — no temp control") {
    PreheatSubgroupPreviewHost(
        capabilities: .init(
            supportsMovement: false,
            supportsTemperatureControl: false,
            supportsBedTemperature: false,
            supportsFanControl: false,
            supportsHoming: false,
            supportedAxes: []
        ),
        printerState: "ready",
        isOnline: true,
        startPendingPreset: nil
    )
    .padding(16)
    .background(Color.pfCard)
    .overlay(
        Text("(Subgroup hidden — no temperature control)")
            .font(.caption)
            .foregroundStyle(.secondary)
    )
    .preferredColorScheme(.dark)
}

/// Preview-only host that wires a fake `PrinterControlsViewModel` so the
/// canvas renders without needing the real network stack.
private struct PreheatSubgroupPreviewHost: View {
    let capabilities: PrinterBackendCapabilities
    let printerState: String
    let isOnline: Bool
    let startPendingPreset: PreheatPreset?

    var body: some View {
        let vm = PreheatSubgroupPreviewFactory.makeViewModel(
            capabilities: capabilities,
            printerState: printerState,
            isOnline: isOnline,
            startPendingPreset: startPendingPreset
        )
        return PreheatSubgroup(viewModel: vm)
    }
}

private enum PreheatSubgroupPreviewFactory {
    @MainActor
    static func makeViewModel(
        capabilities: PrinterBackendCapabilities,
        printerState: String,
        isOnline: Bool,
        startPendingPreset: PreheatPreset?
    ) -> PrinterControlsViewModel {
        let printer = Printer.previewFallbackPrinter(state: printerState, isOnline: isOnline)
        let service = PreheatSubgroupPreviewService(capabilities: capabilities, hangForever: startPendingPreset != nil)
        let serverID = UUID()
        let composition = PrinterControlsComposition(
            identity: .init(serverID: serverID, generation: 0, revision: 0), printerService: service
        )
        let vm = PrinterControlsViewModel(composition: composition, printer: printer)
        vm.configureAccess(serverID: serverID) { nil }
        // Asynchronously load preview capabilities immediately so the canvas
        // settles on the configured visibility state.
        vm.previewLoadCapabilitiesAsync()
        if let preset = startPendingPreset {
            // Kick off a preheat that the preview service will never resolve,
            // pinning the button in pending state.
            Task { @MainActor in await vm.preheat(preset) }
        }
        return vm
    }
}

/// Minimal `PrinterServiceProtocol` shim used by previews. Returns the canned
/// capabilities and either resolves immediately or hangs forever (for the
/// pending preview). Not used outside `#Preview`.
private final class PreheatSubgroupPreviewService: PrinterServiceProtocol, @unchecked Sendable {
    private let capabilities: PrinterBackendCapabilities
    private let hangForever: Bool

    init(capabilities: PrinterBackendCapabilities, hangForever: Bool) {
        self.capabilities = capabilities
        self.hangForever = hangForever
    }

    func list(includeDisabled: Bool) async throws -> [Printer] { [] }
    func get(id: UUID) async throws -> Printer { throw NetworkError.notFound }
    func getStatus(id: UUID) async throws -> PrinterStatusDetail { throw NetworkError.notFound }
    func listCameraUrls() async throws -> [PrinterCameraUrls] { [] }
    func getCameraUrl(id: UUID) async throws -> PrinterCameraUrl { throw NetworkError.notFound }
    func getSnapshot(id: UUID) async throws -> Data { Data() }
    func getCurrentJob(id: UUID) async throws -> PrintJobStatusInfo? { nil }
    func getHistory(id: UUID, limit: Int?) async throws -> PrinterHistoryList { PrinterHistoryList(count: 0, jobs: []) }
    func pause(id: UUID) async throws -> CommandResult { CommandResult(success: true, message: nil) }
    func resume(id: UUID) async throws -> CommandResult { CommandResult(success: true, message: nil) }
    func cancel(id: UUID) async throws -> CommandResult { CommandResult(success: true, message: nil) }
    func stop(id: UUID) async throws -> CommandResult { CommandResult(success: true, message: nil) }
    func emergencyStop(id: UUID) async throws -> CommandResult { CommandResult(success: true, message: nil) }
    func setMaintenanceMode(
        id: UUID,
        inMaintenance: Bool,
        reviewedRowVersion: String
    ) async throws -> Printer {
        Printer.previewFallbackPrinter(state: "ready", isOnline: true)
    }
    func getQueueOverview(model: String?, nozzle: Double?, material: String?) async throws -> [QueueOverview] { [] }
    func setActiveSpool(
        printerId: UUID,
        spoolId: Int?,
        reviewedRowVersion: String
    ) async throws -> CommandResult {
        CommandResult(success: true, message: nil)
    }
    func bindToolheadSpool(printerId: UUID, toolheadIndex: Int, request: ToolheadSpoolBindRequest, idempotencyKey: String) async throws -> CommandResult { CommandResult(success: true, message: nil) }
    func listAvailableSpools(printerId: UUID) async throws -> [SpoolmanSpool] { [] }
    func loadFilament(printerId: UUID) async throws -> CommandResult { CommandResult(success: true, message: nil) }
    func unloadFilament(printerId: UUID) async throws -> CommandResult { CommandResult(success: true, message: nil) }
    func changeFilament(printerId: UUID) async throws -> CommandResult { CommandResult(success: true, message: nil) }

    func setTemperatures(printerId: UUID, hotend: Double?, bed: Double?) async throws {
        if hangForever { try await Task.sleep(nanoseconds: .max) }
    }

    func home(printerId: UUID, axes: [String]) async throws {}
    func homeXY(printerId: UUID) async throws {}
    func homeZ(printerId: UUID) async throws {}
    func move(printerId: UUID, axis: String, distanceMm: Double, feedrateMmMin: Int) async throws {}
    func moveTo(printerId: UUID, x: Double?, y: Double?, z: Double?, feedrateMmMin: Int?) async throws -> CommandResult { throw NetworkError.notFound }
    func extrude(printerId: UUID, distanceMm: Double, feedrateMmPerMinute: Int) async throws -> CommandResult { throw NetworkError.notFound }
    func disableMotors(printerId: UUID) async throws -> CommandResult { throw NetworkError.notFound }
    func saveZOffset(printerId: UUID, offsetMm: Double, saveToFirmware: Bool, reviewedRowVersion: String) async throws -> CommandResult { throw NetworkError.notFound }
    func unloadFilament(printerId: UUID, toolheadIndex: Int?) async throws -> FilamentUnloadResult { throw NetworkError.notFound }

    func getBackendCapabilities(printerId: UUID) async throws -> PrinterBackendCapabilities { capabilities }

    // MARK: - #711 F6 preview stubs

    func getDetails(id: UUID) async throws -> PrinterDetails {
        throw NetworkError.notFound
    }
    func listFallbackGroups(printerId: UUID) async throws -> [FilamentFallbackGroup] { [] }
    func getFallbackGroup(printerId: UUID, groupId: UUID) async throws -> FilamentFallbackGroup {
        throw NetworkError.notFound
    }
    func createFallbackGroup(printerId: UUID, _ request: CreateFilamentFallbackGroupRequest) async throws -> FilamentFallbackGroup {
        throw NetworkError.notFound
    }
    func updateFallbackGroup(printerId: UUID, groupId: UUID, _ request: UpdateFilamentFallbackGroupRequest) async throws -> FilamentFallbackGroup {
        throw NetworkError.notFound
    }
    func deleteFallbackGroup(printerId: UUID, groupId: UUID) async throws {}
    func getAvailableFallback(printerId: UUID, sourceToolheadId: UUID, material: String) async throws -> AvailableFallbackMember? { nil }
}

// MARK: - Preview seam on the ViewModel

private extension PrinterControlsViewModel {
    /// Kicks off the existing `loadCapabilities()` path through the preview
    /// service so the canvas converges on the configured capabilities without
    /// a production-code-affecting back door.
    func previewLoadCapabilitiesAsync() {
        Task { @MainActor in await self.loadCapabilities() }
    }
}

// MARK: - Printer preview stub

private extension Printer {
    /// Decodes a minimal `Printer` from a JSON literal for SwiftUI previews
    /// and preview-only service shims. The struct has no memberwise init, so
    /// we round-trip through `JSONDecoder`. This is preview infrastructure —
    /// never called from production code paths.
    static func previewFallbackPrinter(state: String, isOnline: Bool) -> Printer {
        let json = """
        {
            "id": "11111111-1111-1111-1111-111111111111",
            "name": "Preview Printer",
            "backend": "moonraker",
            "backendPort": 80,
            "inMaintenance": false,
            "isEnabled": true,
            "isOnline": \(isOnline),
            "state": "\(state)",
            "obicoEnabled": false
        }
        """
        // Decoding a string literal under our control is preview-only and
        // should never fail; if it does, surfacing it immediately is useful.
        return try! JSONDecoder().decode(Printer.self, from: Data(json.utf8))
    }
}
