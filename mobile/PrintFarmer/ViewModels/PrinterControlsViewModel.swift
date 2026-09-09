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
    let id = UUID()
    let kind: Kind
    let startedAt: Date

    static func == (lhs: Self, rhs: Self) -> Bool { lhs.id == rhs.id }

    enum Kind: Equatable, Sendable {
        /// A preheat/cool-down carries the concrete target setpoints it
        /// requested (not just the preset) so confirmation compares against the
        /// exact values sent. A `nil` component means that setpoint isn't
        /// controllable on this backend (e.g. the bed on a bed-less printer)
        /// and must be treated as already satisfied — never waited on.
        case preheat(PreheatPreset, hotendTarget: Double?, bedTarget: Double?)
        case home(axes: [String])
        case jog(axis: String, distanceMm: Double)
        case heater(Heater, target: Double)
        case moveTo(x: Double?, y: Double?, z: Double?, feedrateMmMin: Int?)
        case disableMotors
    }
}

enum Heater: String, CaseIterable, Sendable {
    case hotend, bed

    var title: String { self == .hotend ? "Hotend" : "Bed" }
}

enum ControlNumberInput {
    static func optional(_ text: String) throws -> Double? {
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return nil }
        guard let value = Double(trimmed), value.isFinite else {
            throw PrinterControlError.invalidRequest("Enter a finite number using a decimal point.")
        }
        return value
    }

    static func feedrate(_ text: String) throws -> Int? {
        guard let value = try optional(text) else { return nil }
        guard value > 0, value < Double(Int.max), value.rounded() == value else {
            throw PrinterControlError.invalidRequest("Feedrate must be a positive whole number in mm/min.")
        }
        return Int(value)
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
    @Published private(set) var capabilityLoadError: String?
    @Published private(set) var hardware: PrinterHardwareCapabilities?
    @Published private(set) var commandNotice: String?
    @Published private(set) var isActive = true

    private var accessCheck: @MainActor () -> String? = { nil }
    private var hasConfiguredAccess = false
    private var commandTask: Task<Void, Error>?
    private var telemetryConfirmed = false
    private var commandStateInvalidated = false
    private var lifecycleGeneration = 0

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
        if !isActive || accessCheck() != nil || isLoadingCapabilities { return }
        isLoadingCapabilities = true
        capabilityLoadError = nil
        let generation = lifecycleGeneration
        defer { isLoadingCapabilities = false }
        do {
            if capabilities == nil {
                let loaded = try await printerService.getBackendCapabilities(printerId: printer.id)
                try Task.checkCancellation()
                guard canPublishRead(generation) else { return }
                capabilities = loaded
            }
            if hardware == nil { await loadHardware() }
        } catch {
            guard !Task.isCancelled, canPublishRead(generation) else { return }
            // Failed reads are not cached as proof of unsupported hardware.
            // Retrying this read never replays a physical command.
            capabilityLoadError = error.localizedDescription
        }
    }

    func loadHardware() async {
        guard isActive, accessCheck() == nil else { return }
        let generation = lifecycleGeneration
        do {
            let details = try await printerService.getDetails(id: printer.id)
            try Task.checkCancellation()
            guard canPublishRead(generation), details.id == printer.id else { return }
            hardware = details.capabilities
        } catch {
            // Unknown catalog limits are not replaced with illustrative defaults.
        }
    }

    private func canPublishRead(_ generation: Int) -> Bool {
        generation == lifecycleGeneration && isActive && accessCheck() == nil
    }

    func configureAccess(_ check: @escaping @MainActor () -> String?) {
        if !hasConfiguredAccess {
            accessCheck = check
            hasConfiguredAccess = true
        }
        isActive = true
        refreshAccess()
    }

    func refreshAccess() {
        if accessCheck() != nil {
            lifecycleGeneration += 1
            cancelPendingCommand()
        }
    }

    func deactivate() {
        isActive = false
        lifecycleGeneration += 1
        cancelPendingCommand()
    }

    func cancelPendingCommand() {
        commandTask?.cancel()
        commandTask = nil
        guard pendingCommand != nil else { return }
        pendingCommand = nil
        commandNotice = "Stopped waiting. The printer may still execute the request. Check the machine before another action."
    }

    // MARK: - Commands

    func preheat(_ preset: PreheatPreset) async {
        let caps = capabilities ?? PrinterBackendCapabilities.fallback(for: printer.backend)

        // Cool-down uses the same evidence and omission rules as heating.
        let sentHotend: Double? = caps.supportsTemperatureControl ? preset.hotend : nil
        let sentBed: Double? = caps.supportsBedTemperature && hardware?.hasHeatedBed != false ? preset.bed : nil

        // Confirmation targets carried on the pending command. A setpoint the
        // backend can't drive is `nil` so we treat it as already satisfied and
        // never wait for an unobservable value. The preset setpoints are the
        // source of truth (0/0 for coolDown).
        let confirmHotend: Double? = caps.supportsTemperatureControl ? preset.hotend : nil
        let confirmBed = sentBed

        let command = ControlCommand(
            kind: .preheat(preset, hotendTarget: confirmHotend, bedTarget: confirmBed),
            startedAt: clock()
        )
        guard beginCommand(command) else { return }
        defer { endCommand(command) }

        guard caps.supportsTemperatureControl else {
            setError(command: command, message: "Temperature control is unavailable without confirmed backend support.", isRetryable: false)
            return
        }

        guard validateTemperature(sentHotend, heater: .hotend, command: command),
              validateTemperature(sentBed, heater: .bed, command: command) else { return }
        await perform(command) { [printerService, printer] in
            try await printerService.setTemperatures(printerId: printer.id, hotend: sentHotend, bed: sentBed)
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
        guard caps.supportsMovement, caps.supportedAxes.contains(axis.uppercased()),
              ["X", "Y", "Z"].contains(axis.uppercased()),
              distanceMm.isFinite, distanceMm != 0 else {
            setError(command: command, message: "Movement is unavailable without confirmed backend support.", isRetryable: false)
            return
        }

        let normalized = axis.uppercased()
        let feedrate = (normalized == "Z") ? Self.zFeedrateMmMin : Self.xyFeedrateMmMin
        await perform(command) { [printerService, printer] in
            try await printerService.move(
                printerId: printer.id,
                axis: normalized,
                distanceMm: distanceMm,
                feedrateMmMin: feedrate
            )
        }
    }

    func supports(_ heater: Heater) -> Bool {
        switch heater {
        case .hotend: return capabilities?.supportsHotendTemperature == true
        case .bed: return capabilities?.supportsBedTemperature == true && hardware?.hasHeatedBed != false
        }
    }

    func maximum(for heater: Heater) -> Int? {
        heater == .hotend ? hardware?.maxHotendTemp : hardware?.maxBedTemp
    }

    func setHeaterTarget(_ heater: Heater, target: Double) async {
        let command = ControlCommand(kind: .heater(heater, target: target), startedAt: clock())
        guard beginCommand(command) else { return }
        defer { endCommand(command) }
        guard supports(heater) else {
            setError(command: command, message: "\(heater.title) control is unavailable.", isRetryable: false)
            return
        }
        guard validateTemperature(target, heater: heater, command: command) else { return }
        await perform(command) { [printerService, printer] in
            try await printerService.setTemperatures(
                printerId: printer.id, hotend: heater == .hotend ? target : nil,
                bed: heater == .bed ? target : nil
            )
        }
    }

    func moveTo(x: Double?, y: Double?, z: Double?, feedrateMmMin: Int?) async {
        let command = ControlCommand(
            kind: .moveTo(x: x, y: y, z: z, feedrateMmMin: feedrateMmMin), startedAt: clock()
        )
        guard beginCommand(command) else { return }
        defer { endCommand(command) }
        let coordinates = [("X", x), ("Y", y), ("Z", z)].filter { $0.1 != nil }
        guard capabilities?.supportsAbsoluteMovement == true, !coordinates.isEmpty,
              coordinates.allSatisfy({ capabilities?.supportedAxes.contains($0.0) == true && $0.1!.isFinite }),
              feedrateMmMin.map({ $0 > 0 }) ?? true else {
            setError(command: command, message: "Provide finite coordinates on supported axes and a positive feedrate.", isRetryable: false)
            return
        }
        await perform(command) { [printerService, printer] in
            let result = try await printerService.moveTo(
                printerId: printer.id, x: x, y: y, z: z, feedrateMmMin: feedrateMmMin
            )
            guard result.success else {
                throw PrinterControlError.invalidRequest(result.message ?? "Absolute movement was rejected.")
            }
        }
    }

    func disableMotors() async {
        let command = ControlCommand(kind: .disableMotors, startedAt: clock())
        guard beginCommand(command) else { return }
        defer { endCommand(command) }
        guard capabilities?.supportsDisableMotors == true else {
            setError(command: command, message: "Motor release is unavailable.", isRetryable: false)
            return
        }
        await perform(command) { [printerService, printer] in
            let result = try await printerService.disableMotors(printerId: printer.id)
            guard result.success else {
                throw PrinterControlError.invalidRequest(result.message ?? "Motor release was rejected.")
            }
        }
        guard pendingCommand == command, lastError == nil else { return }
        commandNotice = "Motor release request accepted. Motor state is not reported; verify the machine and re-home before moving."
        pendingCommand = nil
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
        if !canControl {
            commandStateInvalidated = true
            if commandTask == nil {
                cancelPendingCommand()
            } else {
                // Keep the response observable and the slot occupied. Losing
                // printer readiness does not cancel the server's execution.
                commandNotice = "Printer controls became unavailable. Waiting for the request outcome; check the machine."
            }
            return
        }
        guard let pending = pendingCommand, !commandStateInvalidated else { return }
        if Self.transition(from: previous, to: updated, resolves: pending) {
            telemetryConfirmed = true
            if commandTask == nil {
                pendingCommand = nil
                commandNotice = Self.confirmationNotice(for: pending)
            }
        }
    }

    /// Decides whether the transition `previous → updated` confirms or
    /// invalidates `command`.
    ///
    /// State/readiness changes are handled separately from confirmation.
    /// The diff is confined to the fields the command actually affects,
    /// so unrelated telemetry never clears it. There is deliberately
    /// no time-based fallback: a command is released only on real evidence.
    static func transition(
        from previous: Printer,
        to updated: Printer,
        resolves command: ControlCommand
    ) -> Bool {
        switch command.kind {
        case .jog(let axis, _):
            return jogAxisMoved(axis: axis, from: previous, to: updated)
        case let .preheat(_, hotendTarget, bedTarget):
            // A preheat/cool-down is confirmed when the snapshot's commanded
            // *targets* satisfy the requested setpoints — never by measured
            // `hotendTemp`/`bedTemp` drift, and with no delta required so a
            // printer already sitting at the setpoint still confirms.
            return targetsSatisfied(hotendTarget: hotendTarget, bedTarget: bedTarget, in: updated)
        case let .heater(heater, target):
            return targetsSatisfied(
                hotendTarget: heater == .hotend ? target : nil,
                bedTarget: heater == .bed ? target : nil, in: updated
            )
        case let .moveTo(x, y, z, _):
            return (x != nil || y != nil || z != nil)
                && (x.map { updated.x == $0 } ?? true)
                && (y.map { updated.y == $0 } ?? true)
                && (z.map { updated.z == $0 } ?? true)
        case .disableMotors:
            return false
        case .home(let axes):
            // `homedAxes` is the authoritative homing confirmation; position
            // resets are a side effect and must not couple homing to jog noise.
            return previous.homedAxes != updated.homedAxes && homedAxesSatisfied(axes, in: updated)
        }
    }

    private static func homedAxesSatisfied(_ axes: [String], in printer: Printer) -> Bool {
        guard let homed = printer.homedAxes?.uppercased() else { return false }
        return axes.allSatisfy { homed.contains($0.uppercased()) }
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
        isActive && accessCheck() == nil && printer.isOnline && !isPrintingOrPaused
    }

    var blockedReason: String? {
        if !isActive { return "Controls are no longer active." }
        if let reason = accessCheck() { return reason }
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

    private func runHome(axes: [String], _ call: @escaping @MainActor () async throws -> Void) async {
        let command = ControlCommand(kind: .home(axes: axes), startedAt: clock())
        guard beginCommand(command) else { return }
        defer { endCommand(command) }

        let caps = capabilities ?? PrinterBackendCapabilities.fallback(for: printer.backend)
        guard caps.supportsHome(axes: axes) else {
            setError(command: command, message: "Homing is unavailable without confirmed backend support.", isRetryable: false)
            return
        }
        let alreadyHomed = Self.homedAxesSatisfied(axes, in: printer)
        await perform(command, call)
        if pendingCommand == command, lastError == nil, alreadyHomed, !telemetryConfirmed {
            pendingCommand = nil
            commandNotice = "Homing request accepted. Requested axes were already reported homed; fresh physical completion is not confirmed. Check the machine before moving."
        }
    }

    /// Single-flight: rejects new commands while one is pending. Also enforces
    /// the lockout when the printer is printing or offline.
    private func beginCommand(_ command: ControlCommand) -> Bool {
        guard !Task.isCancelled else { return false }
        guard pendingCommand == nil else { return false }
        guard canControl else {
            commandNotice = nil
            lastError = ControlsError(
                command: command,
                message: blockedReason ?? "Controls are unavailable.",
                isRetryable: false
            )
            return false
        }
        lastError = nil
        commandNotice = nil
        telemetryConfirmed = false
        commandStateInvalidated = false
        pendingCommand = command
        return true
    }

    private func validateTemperature(_ target: Double?, heater: Heater, command: ControlCommand) -> Bool {
        guard let target else { return true }
        guard target.isFinite, target >= 0,
              maximum(for: heater).map({ target <= Double($0) }) ?? true else {
            setError(command: command, message: "\(heater.title) target must be nonnegative and within the configured maximum.", isRetryable: false)
            return false
        }
        return true
    }

    private func perform(_ command: ControlCommand, _ call: @escaping @MainActor () async throws -> Void) async {
        let task = Task { @MainActor in
            try Task.checkCancellation()
            guard self.canControl else { throw CancellationError() }
            try await call()
            try Task.checkCancellation()
        }
        commandTask = task
        do {
            try await withTaskCancellationHandler {
                try await task.value
            } onCancel: {
                task.cancel()
            }
            guard pendingCommand == command else { return }
            if !canControl || commandStateInvalidated {
                commandTask = nil
                pendingCommand = nil
                commandNotice = "Request accepted, but printer controls became unavailable. Physical outcome is unknown; check the machine before another action."
                return
            }
            commandNotice = "Request accepted; waiting for matching telemetry. This does not confirm physical completion."
        } catch {
            guard pendingCommand == command else { return }
            if error is CancellationError {
                cancelPendingCommand()
            } else {
                setError(command: command, error: error)
            }
        }
        if pendingCommand == command { commandTask = nil }
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
        if telemetryConfirmed || Self.transition(from: printer, to: printer, resolves: command) {
            pendingCommand = nil
            commandNotice = Self.confirmationNotice(for: command)
        }
    }

    private static func confirmationNotice(for command: ControlCommand) -> String {
        switch command.kind {
        case .preheat, .heater:
            return "Matching telemetry received. A heater target is a setpoint, not a measured temperature."
        default:
            return "Matching telemetry received. Check the machine before further setup."
        }
    }

    private func setError(command: ControlCommand, error: Error) {
        let mapped = Self.mapError(error)
        let uncertainResponse: Bool
        if let network = error as? NetworkError {
            switch network {
            case .invalidResponse, .decodingFailed, .unexpectedStatus, .staleServerResponse:
                uncertainResponse = true
            default:
                uncertainResponse = false
            }
        } else {
            uncertainResponse = false
        }
        let message = mapped.isRetryable || uncertainResponse
            ? "\(mapped.message) Outcome may be unknown. Check the printer before sending another request."
            : mapped.message
        setError(command: command, message: message, isRetryable: mapped.isRetryable)
    }

    private func setError(command: ControlCommand, message: String, isRetryable: Bool) {
        // Identity gate: a stale failure from a command that is no longer
        // pending (already confirmed/cleared and possibly superseded) must not
        // overwrite the current command's error banner. Only the owner of the
        // pending slot may record an error here.
        guard pendingCommand == command else { return }
        commandNotice = nil
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
            case .partsInventoryConflict(let conflict): return (conflict.detail ?? conflict.title ?? "Printed-parts conflict.", false)
            case .unauthorized: return ("Authentication required.", false)
            case .forbidden: return ("Access denied.", false)
            case .notFound: return ("Printer not found.", false)
            case .featureDisabled: return ("This feature is disabled on the server.", false)
            case .methodNotAllowed: return (net.errorDescription ?? "Command not supported.", false)
            case .preconditionFailed, .preconditionRequired:
                return (
                    net.errorDescription
                        ?? "This item changed. Refresh and confirm again.",
                    false
                )
            case .clientError(_, let api): return (api?.detail ?? api?.message ?? api?.title ?? "Command rejected.", false)
            case .unexpectedStatus(let code): return ("Unexpected response (\(code)).", false)
            case .invalidURL, .invalidResponse, .decodingFailed, .authFailed: return ("Command failed.", false)
            case .staleServerResponse: return ("Server changed. Refresh and try again.", false)
            case .insecureTransportBlocked, .certificateChanged, .certificateNotTrusted:
                return (net.errorDescription ?? "Secure connection failed.", false)
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
