import Foundation

// MARK: - Public Types

enum PreheatPreset: String, Equatable, Sendable {
    case pla, petg, abs, coolDown

    var hotend: Double {
        switch self {
        case .pla: return 200
        case .petg: return 240
        case .abs: return 240
        case .coolDown: return 0
        }
    }

    var bed: Double {
        switch self {
        case .pla: return 60
        case .petg: return 80
        case .abs: return 100
        case .coolDown: return 0
        }
    }
}

struct ControlCommand: Equatable, Sendable {
    let kind: Kind
    let startedAt: Date

    enum Kind: Equatable, Sendable {
        case preheat(PreheatPreset)
        case home(axes: [String])
        case jog(axis: String, distanceMm: Double)
    }
}

struct ControlsError: Error, Equatable, Sendable {
    let command: ControlCommand
    let message: String
    let isRetryable: Bool
}

// MARK: - View Model

/// Owns capability fetching + caching, single-flight command dispatch, and
/// surfaces pending/error state to the controls UI. View layer wires
/// `handlePrinterUpdate(_:)` to SignalR `printerupdated` to clear pending state
/// after the command's effect lands. See `mobile/docs/design/printer-controls-section.md`.
@MainActor
final class PrinterControlsViewModel: ObservableObject {

    // Internal feedrates (mm/min). Not exposed; controls UI uses fixed jog distances.
    static let xyFeedrateMmMin: Int = 3000
    static let zFeedrateMmMin: Int = 600

    @Published private(set) var capabilities: PrinterBackendCapabilities?
    @Published private(set) var lastError: ControlsError?
    @Published private(set) var pendingCommand: ControlCommand?
    @Published private(set) var isLoadingCapabilities: Bool = false

    private(set) var printer: Printer

    private let printerService: any PrinterServiceProtocol
    private let clock: @Sendable () -> Date

    init(
        printerService: any PrinterServiceProtocol,
        printer: Printer,
        clock: @escaping @Sendable () -> Date = Date.init
    ) {
        self.printerService = printerService
        self.printer = printer
        self.clock = clock
    }

    // MARK: - Capabilities

    func loadCapabilities() async {
        if capabilities != nil { return } // cache for lifetime of view model
        isLoadingCapabilities = true
        defer { isLoadingCapabilities = false }
        do {
            capabilities = try await printerService.getBackendCapabilities(printerId: printer.id)
        } catch {
            // Fall back to backend-keyed defaults so UI stays usable. Don't surface
            // capability fetch errors via lastError (that channel is for command failures).
            capabilities = PrinterBackendCapabilities.fallback(for: printer.backend)
        }
    }

    // MARK: - Commands

    func preheat(_ preset: PreheatPreset) async {
        let command = ControlCommand(kind: .preheat(preset), startedAt: clock())
        guard beginCommand(command) else { return }
        defer { endCommand(command) }

        // Capability gating:
        //   * coolDown always sends 0/0 (safe even if backend ignores bed).
        //   * Preset hotend requires temperature control.
        //   * Preset bed silently dropped when bed control is unsupported (e.g. FlashForge).
        let caps = capabilities ?? PrinterBackendCapabilities.fallback(for: printer.backend)
        let hotend: Double?
        let bed: Double?
        if preset == .coolDown {
            hotend = 0
            bed = 0
        } else {
            guard caps.supportsTemperatureControl else {
                setError(command: command, message: "Printer doesn't support temperature control.", isRetryable: false)
                return
            }
            hotend = preset.hotend
            bed = caps.supportsBedTemperature ? preset.bed : nil
        }

        do {
            try await printerService.setTemperatures(printerId: printer.id, hotend: hotend, bed: bed)
        } catch {
            setError(command: command, error: error)
        }
    }

    func homeAll() async { await runHome(axes: ["X", "Y", "Z"]) { [printer, printerService] in
        try await printerService.home(printerId: printer.id, axes: ["X", "Y", "Z"])
    } }

    func homeXY() async { await runHome(axes: ["X", "Y"]) { [printer, printerService] in
        try await printerService.homeXY(printerId: printer.id)
    } }

    func homeZ() async { await runHome(axes: ["Z"]) { [printer, printerService] in
        try await printerService.homeZ(printerId: printer.id)
    } }

    func jog(axis: String, distanceMm: Double) async {
        let command = ControlCommand(kind: .jog(axis: axis, distanceMm: distanceMm), startedAt: clock())
        guard beginCommand(command) else { return }
        defer { endCommand(command) }

        let caps = capabilities ?? PrinterBackendCapabilities.fallback(for: printer.backend)
        guard caps.supportsMovement else {
            setError(command: command, message: "Printer doesn't support movement.", isRetryable: false)
            return
        }

        let normalized = axis.uppercased()
        let feedrate = (normalized == "Z") ? Self.zFeedrateMmMin : Self.xyFeedrateMmMin
        do {
            try await printerService.move(
                printerId: printer.id,
                axis: normalized,
                distanceMm: distanceMm,
                feedrateMmMin: feedrate
            )
        } catch {
            setError(command: command, error: error)
        }
    }

    func dismissError() {
        lastError = nil
    }

    // MARK: - SignalR Hook

    /// View layer calls this when a `printerupdated` SignalR event arrives for
    /// `printer.id`. Updates the cached printer (so `canControl` reflects fresh
    /// state) and clears `pendingCommand` — the command's effect has landed.
    func handlePrinterUpdate(_ updated: Printer) {
        guard updated.id == printer.id else { return }
        printer = updated
        pendingCommand = nil
    }

    // MARK: - Computed

    var isExecuting: Bool { pendingCommand != nil }

    var canControl: Bool {
        printer.isOnline && !isPrintingOrPaused
    }

    var blockedReason: String? {
        if !printer.isOnline { return "Printer is offline." }
        if isPrintingOrPaused { return "Controls are locked while a print is active." }
        return nil
    }

    private var isPrintingOrPaused: Bool {
        switch printer.state?.lowercased() {
        case "printing", "paused", "starting": return true
        default: return false
        }
    }

    // MARK: - Private

    private func runHome(axes: [String], _ call: @escaping () async throws -> Void) async {
        let command = ControlCommand(kind: .home(axes: axes), startedAt: clock())
        guard beginCommand(command) else { return }
        defer { endCommand(command) }

        let caps = capabilities ?? PrinterBackendCapabilities.fallback(for: printer.backend)
        guard caps.supportsHoming else {
            setError(command: command, message: "Printer doesn't support homing.", isRetryable: false)
            return
        }
        do {
            try await call()
        } catch {
            setError(command: command, error: error)
        }
    }

    /// Single-flight: rejects new commands while one is pending. Also enforces
    /// the lockout when the printer is printing or offline.
    private func beginCommand(_ command: ControlCommand) -> Bool {
        guard pendingCommand == nil else { return false }
        guard canControl else {
            lastError = ControlsError(
                command: command,
                message: blockedReason ?? "Controls are unavailable.",
                isRetryable: false
            )
            return false
        }
        lastError = nil
        pendingCommand = command
        return true
    }

    /// Note: pendingCommand is *not* cleared here on success — it stays until
    /// SignalR confirms the effect via `handlePrinterUpdate(_:)`. We only clear
    /// it on failure so the user can retry. See controls UX spec §"5-state model".
    private func endCommand(_ command: ControlCommand) {
        if lastError?.command == command {
            pendingCommand = nil
        }
    }

    private func setError(command: ControlCommand, error: Error) {
        let mapped = Self.mapError(error)
        lastError = ControlsError(command: command, message: mapped.message, isRetryable: mapped.isRetryable)
    }

    private func setError(command: ControlCommand, message: String, isRetryable: Bool) {
        lastError = ControlsError(command: command, message: message, isRetryable: isRetryable)
    }

    static func mapError(_ error: Error) -> (message: String, isRetryable: Bool) {
        if let net = error as? NetworkError {
            switch net {
            case .noConnection: return ("No internet connection.", true)
            case .timeout: return ("Request timed out.", true)
            case .serverUnreachable: return ("Printer is unreachable.", true)
            case .transportError: return ("Network error.", true)
            case .serverError: return ("Printer reported a server error.", true)
            case .conflict: return ("Printer is busy.", true)
            case .unauthorized: return ("Authentication required.", false)
            case .forbidden: return ("Access denied.", false)
            case .notFound: return ("Printer not found.", false)
            case .clientError(_, let api): return (api?.detail ?? api?.message ?? api?.title ?? "Command rejected.", false)
            case .unexpectedStatus(let code): return ("Unexpected response (\(code)).", false)
            case .invalidURL, .invalidResponse, .decodingFailed, .authFailed: return ("Command failed.", false)
            case .staleServerResponse: return ("Server changed. Refresh and try again.", false)
            }
        }
        if let url = error as? URLError {
            switch url.code {
            case .notConnectedToInternet, .networkConnectionLost: return ("No internet connection.", true)
            case .timedOut: return ("Request timed out.", true)
            case .cannotConnectToHost, .cannotFindHost: return ("Printer is unreachable.", true)
            default: return ("Network error.", true)
            }
        }
        return (error.localizedDescription, false)
    }
}
