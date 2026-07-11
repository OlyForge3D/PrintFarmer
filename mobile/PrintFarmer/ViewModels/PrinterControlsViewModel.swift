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
        /// A preheat/cool-down carries the concrete target setpoints it
        /// requested (not just the preset) so confirmation compares against the
        /// exact values sent. A `nil` component means that setpoint isn't
        /// controllable on this backend (e.g. the bed on a bed-less printer)
        /// and must be treated as already satisfied — never waited on.
        case preheat(PreheatPreset, hotendTarget: Double?, bedTarget: Double?)
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
        let caps = capabilities ?? PrinterBackendCapabilities.fallback(for: printer.backend)

        // Values actually sent to the backend:
        //   * coolDown always sends 0/0 (safe even if the backend ignores bed).
        //   * A preset's hotend requires temperature control (gated below).
        //   * A preset's bed is silently dropped when bed control is unsupported.
        let sentHotend: Double?
        let sentBed: Double?
        if preset == .coolDown {
            sentHotend = 0
            sentBed = 0
        } else {
            sentHotend = preset.hotend
            sentBed = caps.supportsBedTemperature ? preset.bed : nil
        }

        // Confirmation targets carried on the pending command. A setpoint the
        // backend can't drive is `nil` so we treat it as already satisfied and
        // never wait for an unobservable value. The preset setpoints are the
        // source of truth (0/0 for coolDown).
        let confirmHotend: Double? = caps.supportsTemperatureControl ? preset.hotend : nil
        let confirmBed: Double? = (caps.supportsTemperatureControl && caps.supportsBedTemperature)
            ? preset.bed : nil

        let command = ControlCommand(
            kind: .preheat(preset, hotendTarget: confirmHotend, bedTarget: confirmBed),
            startedAt: clock()
        )
        guard beginCommand(command) else { return }
        defer { endCommand(command) }

        if preset != .coolDown {
            guard caps.supportsTemperatureControl else {
                setError(command: command, message: "Printer doesn't support temperature control.", isRetryable: false)
                return
            }
        }

        do {
            try await printerService.setTemperatures(printerId: printer.id, hotend: sentHotend, bed: sentBed)
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
    /// `printer.id`. Always refreshes the cached printer snapshot (so
    /// `canControl` reflects fresh state and the next diff is measured from
    /// here), then clears `pendingCommand` **only** when the incoming snapshot
    /// actually confirms — or legitimately invalidates — the in-flight command.
    ///
    /// The controls surface receives a merged telemetry stream (position,
    /// temperatures/targets, homed axes, state, online status). Ambient churn
    /// in an unrelated field must not release the wrong command: temperature
    /// drift must not clear a pending jog, position noise must not clear a
    /// pending preheat, measured-temperature drift must not clear a pending
    /// preheat (only the commanded target counts), and so on. Correlation is a
    /// targeted diff from the previously cached snapshot to `updated`, scoped
    /// to the fields the specific command drives.
    func handlePrinterUpdate(_ updated: Printer) {
        guard updated.id == printer.id else { return }
        let previous = printer
        printer = updated
        guard let pending = pendingCommand else { return }
        if Self.transition(from: previous, to: updated, resolves: pending) {
            pendingCommand = nil
        }
    }

    /// Decides whether the transition `previous → updated` confirms or
    /// invalidates `command`.
    ///
    /// Two lifecycle transitions release *any* pending command:
    ///   * an `isOnline` change — going offline invalidates the in-flight
    ///     command; a reconnect resets control state, and
    ///   * a `state` transition — a print starting/stopping/pausing completes
    ///     or supersedes the command.
    ///
    /// Otherwise the diff is confined to the fields the command actually
    /// affects, so unrelated telemetry never clears it. There is deliberately
    /// no time-based fallback: a command is released only on real evidence.
    static func transition(
        from previous: Printer,
        to updated: Printer,
        resolves command: ControlCommand
    ) -> Bool {
        if previous.isOnline != updated.isOnline { return true }
        if previous.state != updated.state { return true }

        switch command.kind {
        case .jog(let axis, _):
            return jogAxisMoved(axis: axis, from: previous, to: updated)
        case let .preheat(_, hotendTarget, bedTarget):
            // A preheat/cool-down is confirmed when the snapshot's commanded
            // *targets* satisfy the requested setpoints — never by measured
            // `hotendTemp`/`bedTemp` drift, and with no delta required so a
            // printer already sitting at the setpoint still confirms.
            return targetsSatisfied(hotendTarget: hotendTarget, bedTarget: bedTarget, in: updated)
        case .home:
            // `homedAxes` is the authoritative homing confirmation; position
            // resets are a side effect and must not couple homing to jog noise.
            return previous.homedAxes != updated.homedAxes
        }
    }

    private static func jogAxisMoved(axis: String, from previous: Printer, to updated: Printer) -> Bool {
        switch axis.uppercased() {
        case "X": return previous.x != updated.x
        case "Y": return previous.y != updated.y
        case "Z": return previous.z != updated.z
        default:
            return previous.x != updated.x
                || previous.y != updated.y
                || previous.z != updated.z
        }
    }

    /// True when `printer`'s commanded *target* setpoints satisfy the requested
    /// targets. A `nil` requested target — a setpoint the backend can't drive,
    /// e.g. the bed on a bed-less printer — is treated as already satisfied so
    /// control never waits for an unobservable value. Measured
    /// `hotendTemp`/`bedTemp` are intentionally ignored: only the commanded
    /// setpoint confirms a preheat, and a snapshot already at the requested
    /// target is valid confirmation (no delta required).
    private static func targetsSatisfied(hotendTarget: Double?, bedTarget: Double?, in printer: Printer) -> Bool {
        let hotendSatisfied = hotendTarget.map { printer.hotendTarget == $0 } ?? true
        let bedSatisfied = bedTarget.map { printer.bedTarget == $0 } ?? true
        return hotendSatisfied && bedSatisfied
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

    /// Completion handler for a dispatched command. Runs on the MainActor via
    /// the `defer` in each command method.
    ///
    /// Identity gate (single-flight): a stale completion — success *or* failure
    /// — whose command was already confirmed/cleared by live telemetry and
    /// superseded by a newer command must never mutate `pendingCommand`. So we
    /// bail unless this exact invocation still owns the pending slot.
    ///
    /// Once ownership is established:
    ///   * Failure: this command's own error was recorded by `setError`; clear
    ///     it so the user can retry.
    ///   * Success: pending normally persists until a live snapshot confirms the
    ///     effect (see `handlePrinterUpdate(_:)`). The one exception is when the
    ///     latest cached same-printer snapshot *already* satisfies the command's
    ///     confirmation domain — e.g. a same-preset preheat on a printer already
    ///     at the requested targets, an already-zero cool-down, or a confirming
    ///     snapshot that landed before the HTTP response — where waiting for a
    ///     further delta would hang forever, so we clear now.
    private func endCommand(_ command: ControlCommand) {
        guard pendingCommand == command else { return }
        if lastError?.command == command {
            pendingCommand = nil
            return
        }
        if Self.transition(from: printer, to: printer, resolves: command) {
            pendingCommand = nil
        }
    }

    private func setError(command: ControlCommand, error: Error) {
        let mapped = Self.mapError(error)
        setError(command: command, message: mapped.message, isRetryable: mapped.isRetryable)
    }

    private func setError(command: ControlCommand, message: String, isRetryable: Bool) {
        // Identity gate: a stale failure from a command that is no longer
        // pending (already confirmed/cleared and possibly superseded) must not
        // overwrite the current command's error banner. Only the owner of the
        // pending slot may record an error here.
        guard pendingCommand == command else { return }
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
            case .methodNotAllowed: return (net.errorDescription ?? "Command not supported.", false)
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
