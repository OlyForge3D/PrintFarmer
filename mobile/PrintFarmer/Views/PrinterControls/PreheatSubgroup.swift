import SwiftUI

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

    /// Fixed display order matching the UX spec.
    static let presets: [PreheatPreset] = [.pla, .petg, .abs, .coolDown]

    /// Whether the entire subgroup should render. Hidden when the printer has
    /// no temperature control at all (e.g. an SDCP printer reports
    /// `supportsTemperatureControl == false`).
    static func isVisible(capabilities: PrinterBackendCapabilities?) -> Bool {
        // Fail open: if capabilities haven't loaded yet, render the subgroup —
        // the ViewModel will fail the command with a clear error if the
        // backend really doesn't support it.
        guard let caps = capabilities else { return true }
        return caps.supportsTemperatureControl
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

            if viewModel.capabilities?.supportsBedTemperature == false {
                Text("Hotend only — printer has no heated bed.")
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
        // iPad (regular width): 1×4 row. Phone / compact: 2×2 grid.
        let count = horizontalSizeClass == .regular ? 4 : 2
        return Array(repeating: GridItem(.flexible(), spacing: 8), count: count)
    }

    @ViewBuilder
    private func button(for preset: PreheatPreset) -> some View {
        let isPending = viewModel.pendingCommand?.kind == .preheat(preset)
        let canControl = viewModel.canControl
        // Per spec §3.1 single-flight queue: if any preheat command is in
        // flight, *all* preheat siblings disable so the user can't stack
        // burst commands like "PLA then ABS".
        let isAnyPreheatInProgress: Bool = {
            guard case .preheat = viewModel.pendingCommand?.kind else { return false }
            return true
        }()
        let isInteractive = canControl && !isAnyPreheatInProgress

        let hasError = isErrored(preset: preset)
        Button {
            handleTap(preset: preset, canControl: canControl)
        } label: {
            buttonLabel(preset: preset, isPending: isPending)
        }
        .buttonStyle(PreheatButtonStyle(preset: preset, isEnabled: isInteractive, isPending: isPending))
        .disabled(isAnyPreheatInProgress || isBlockedWithoutTapReveal(canControl: canControl))
        // On compact layouts we keep blocked buttons tappable so the user can
        // reveal the disabled reason; regular width shows the reason inline.
        .disabledControlStyle(isDisabled: !isInteractive && !isPending, cornerRadius: 8)
        .errorBorderHighlight(isActive: hasError, cornerRadius: 8)
        .accessibilityLabel(accessibilityLabel(preset: preset, isPending: isPending))
        .accessibilityHint(accessibilityHint(canControl: canControl, hasError: hasError))
        .accessibilityValue(accessibilityValue(isPending: isPending, hasError: hasError))
        .accessibilityAddTraits(isPending ? .updatesFrequently : [])
        .help(viewModel.blockedReason ?? "")
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
            HStack(spacing: 4) {
                Image(systemName: preset.iconName)
                    .imageScale(.medium)
                Text(preset.displayLabel)
                    .font(.subheadline.weight(.medium))
                    .lineLimit(1)
            }
            Text(preset.temperatureLabel)
                .font(.caption.monospacedDigit())
                .lineLimit(1)
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

    private func accessibilityLabel(preset: PreheatPreset, isPending: Bool) -> String {
        if isPending {
            return preset == .coolDown
                ? String(localized: "Cooling down, in progress", comment: "VoiceOver: Cool Down button while command is in flight")
                : String(localized: "Preheating to \(preset.spokenName), in progress", comment: "VoiceOver: preheat button while command is in flight")
        }
        if preset == .coolDown {
            return String(localized: "Cool down, 0 degrees hotend, 0 degrees bed", comment: "VoiceOver: Cool Down button idle state")
        }
        let bedSegment = (viewModel.capabilities?.supportsBedTemperature == false)
            ? String(localized: "no heated bed", comment: "VoiceOver bed segment when printer has no heated bed")
            : String(localized: "\(Int(preset.bed)) degrees bed", comment: "VoiceOver bed segment temperature")
        return String(localized: "Preheat to \(preset.spokenName), \(Int(preset.hotend)) degrees hotend, \(bedSegment)", comment: "VoiceOver: preheat button idle state")
    }

    private func accessibilityHint(canControl: Bool, hasError: Bool) -> String {
        if hasError, let message = viewModel.lastError?.message {
            return String(localized: "Failed: \(message). Double tap to retry.", comment: "VoiceOver hint when last preheat command failed")
        }
        if !canControl, let reason = viewModel.blockedReason {
            return String(localized: "Disabled. \(reason)", comment: "VoiceOver hint when controls are disabled")
        }
        return ""
    }

    private func accessibilityValue(isPending: Bool, hasError: Bool) -> String {
        if isPending { return String(localized: "Sending command", comment: "VoiceOver value while a control command is in flight") }
        if hasError { return String(localized: "Failed", comment: "VoiceOver value when last command failed") }
        return ""
    }

    private func isErrored(preset: PreheatPreset) -> Bool {
        guard let last = viewModel.lastError else { return false }
        if case let .preheat(errPreset) = last.command.kind, errPreset == preset { return true }
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

    var iconName: String {
        switch self {
        case .pla, .petg, .abs: return "thermometer.high"
        case .coolDown: return "thermometer.snowflake"
        }
    }

    var temperatureLabel: String {
        "\(Int(hotend))° / \(Int(bed))°"
    }
}

// MARK: - Button style

/// Preheat-specific button style with per-state visual treatment per spec §2.4.
private struct PreheatButtonStyle: ButtonStyle {
    let preset: PreheatPreset
    let isEnabled: Bool
    let isPending: Bool

    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .padding(.horizontal, 8)
            .padding(.vertical, 8)
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
        let vm = PrinterControlsViewModel(printerService: service, printer: printer)
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
    func getSnapshot(id: UUID) async throws -> Data { Data() }
    func getCurrentJob(id: UUID) async throws -> PrintJobStatusInfo? { nil }
    func pause(id: UUID) async throws -> CommandResult { CommandResult(success: true, message: nil) }
    func resume(id: UUID) async throws -> CommandResult { CommandResult(success: true, message: nil) }
    func cancel(id: UUID) async throws -> CommandResult { CommandResult(success: true, message: nil) }
    func stop(id: UUID) async throws -> CommandResult { CommandResult(success: true, message: nil) }
    func emergencyStop(id: UUID) async throws -> CommandResult { CommandResult(success: true, message: nil) }
    func setMaintenanceMode(id: UUID, inMaintenance: Bool) async throws -> Printer {
        Printer.previewFallbackPrinter(state: "ready", isOnline: true)
    }
    func getQueueOverview(model: String?, nozzle: Double?, material: String?) async throws -> [QueueOverview] { [] }
    func setActiveSpool(printerId: UUID, spoolId: Int?) async throws -> CommandResult { CommandResult(success: true, message: nil) }
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

    func getBackendCapabilities(printerId: UUID) async throws -> PrinterBackendCapabilities { capabilities }
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
