import Combine
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
        case extrusion(distanceMm: Double, feedrateMmMin: Int)
        case filament(PhysicalFilamentOperation)
        case calibrationHome
        case calibrationAdjust(delta: Double, expectedZ: Double)
        case calibrationSave(offsetMm: Double)
    }
}

enum PhysicalFilamentOperation: String, CaseIterable, Identifiable, Sendable {
    case load, unload, change
    var id: String { rawValue }
    var title: String { "\(rawValue.capitalized) filament" }
}

enum ZOffsetCalibrationStep: String, CaseIterable, Sendable {
    case introduction, home, position, adjust, save, done
}

/// Native choices and validation only; never a source of hardware safety evidence.
enum MaterialControlInput {
    static let distances = [10.0, 25, 50, 100]
    static let speeds = [1, 5, 10]
    static let increments = [0.01, 0.05, 0.1]

    static func feedrate(distance: Double, speed: Int) throws -> Int {
        guard distance.isFinite, distances.contains(abs(distance)), speeds.contains(speed) else {
            throw PrinterControlError.invalidRequest("Choose 10, 25, 50 or 100 mm and 1, 5 or 10 mm/s.")
        }
        return speed * 60
    }

    static func adjustedOffset(_ offset: Double, delta: Double) throws -> Double {
        guard ControlNumberInput.hasCoordinatePrecision(offset), increments.contains(abs(delta)) else {
            throw PrinterControlError.invalidRequest("Choose a 0.01, 0.05 or 0.1 mm adjustment.")
        }
        let value = (offset * 1000 + delta * 1000).rounded() / 1000
        guard (-5...5).contains(value) else {
            throw PrinterControlError.invalidRequest("Z-offset must stay within -5…5 mm.")
        }
        return value
    }
}

enum Heater: String, CaseIterable, Sendable {
    case hotend, bed

    var title: String { self == .hotend ? "Hotend" : "Bed" }
}

enum ControlNumberInput {
    static let heaterPrecisionMessage = "Use whole degrees Celsius. Fractional targets are not supported; no rounding is applied."
    static let coordinatePrecisionMessage = "Use at most 3 decimal places in millimetres. No rounding is applied."
    static let customFeedrateMessage = "Custom feedrates are unavailable without a verified maximum. Leave this field blank to use the established axis-specific rate."

    static func optional(_ text: String) throws -> Double? {
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return nil }
        guard let value = Double(trimmed), value.isFinite else {
            throw PrinterControlError.invalidRequest("Enter a finite number using a decimal point.")
        }
        return value
    }

    static func feedrate(_ text: String) throws -> Int? {
        guard text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            throw PrinterControlError.invalidRequest(customFeedrateMessage)
        }
        return nil
    }

    static func heaterTarget(_ text: String) throws -> Double? {
        guard let value = try optional(text) else { return nil }
        guard hasTextPrecision(text, places: 0), isWholeDegree(value) else {
            throw PrinterControlError.invalidRequest(heaterPrecisionMessage)
        }
        return value
    }

    static func coordinate(_ text: String) throws -> Double? {
        guard let value = try optional(text) else { return nil }
        guard hasTextPrecision(text, places: 3), hasCoordinatePrecision(value) else {
            throw PrinterControlError.invalidRequest(coordinatePrecisionMessage)
        }
        return value
    }

    static func isWholeDegree(_ value: Double) -> Bool {
        value.isFinite && value.rounded() == value
    }

    static func hasCoordinatePrecision(_ value: Double) -> Bool {
        guard value.isFinite else { return false }
        // The shared backend emits 0.### mm. Compare the decimal round-trip,
        // not value * 1000 (binary noise rejects valid values such as 1.001).
        // This only validates: the caller's original value is sent unchanged.
        return Double(String(format: "%.3f", locale: Locale(identifier: "en_US_POSIX"), value)) == value
    }

    private static func hasTextPrecision(_ text: String, places: Int) -> Bool {
        // Check the entered decimal before Double can erase tiny fractions or
        // underflow to zero. Trailing zeros and exact scientific notation are OK.
        let parts = text.trimmingCharacters(in: .whitespacesAndNewlines)
            .lowercased().split(separator: "e", omittingEmptySubsequences: false)
        guard parts.count <= 2,
              let exponent = Int(parts.count == 2 ? String(parts[1]) : "0"),
              parts[0].allSatisfy({ "0123456789.+-".contains($0) }) else { return false }
        let digits = parts[0].filter { $0.isNumber }
        if digits.allSatisfy({ $0 == "0" }) { return true }
        let fraction = parts[0].split(separator: ".", omittingEmptySubsequences: false)
        let decimalPlaces = fraction.count == 2 ? fraction[1].count : 0
        let trailingZeros = digits.reversed().prefix { $0 == "0" }.count
        return exponent >= decimalPlaces - trailingZeros - places
    }
}

struct ControlsError: Error, Equatable, Sendable {
    let command: ControlCommand
    let message: String
    let isRetryable: Bool
}

struct PrinterControlsComposition: Sendable {
    struct Identity: Hashable, Sendable {
        let serverID: UUID
        let generation: Int
        let revision: Int
    }

    let identity: Identity
    let printerService: any PrinterServiceProtocol
}

private struct PrinterControlsIdentity: Hashable {
    let serverID: UUID
    let printerID: UUID
}

/// Process-wide physical-request ownership, independent of view/service lifetime.
/// Only tokens are retained; neither command tasks nor view models live here.
@MainActor
private final class PrinterControlsLeases: ObservableObject {
    static let shared = PrinterControlsLeases()
    @Published private var owners: [PrinterControlsIdentity: UUID] = [:]

    func contains(_ identity: PrinterControlsIdentity) -> Bool {
        owners[identity] != nil
    }

    func acquire(_ identity: PrinterControlsIdentity, token: UUID) -> Bool {
        guard owners[identity] == nil else { return false }
        owners[identity] = token
        return true
    }

    func release(_ identity: PrinterControlsIdentity, token: UUID) {
        guard owners[identity] == token else { return }
        owners.removeValue(forKey: identity)
    }
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
    @Published private(set) var isLoadingHardware = false
    @Published private(set) var hardwareLoadError: String?
    @Published private(set) var commandNotice: String?
    @Published private(set) var isActive = true
    @Published private(set) var calibrationStep: ZOffsetCalibrationStep?
    @Published private(set) var calibrationOffset: Double?
    @Published private(set) var calibrationReview: PrinterDetails?
    @Published private(set) var calibrationMessage: String?
    @Published private(set) var isReviewingCalibration = false
    private var calibrationSession = UUID()
    private var calibrationCommandID: UUID?
    var registeredServerID: UUID? { composition?.identity.serverID }
    var compositionIdentity: PrinterControlsComposition.Identity? { composition?.identity }

    private let composition: PrinterControlsComposition?
    private var accessCheck: @MainActor () -> String? = { nil }
    private var hasConfiguredAccess = false
    private let commandLeases = PrinterControlsLeases.shared
    private var leaseObservation: AnyCancellable?
    private var commandTask: Task<Void, Error>?
    private var commandWasDispatched = false
    private var telemetryConfirmed = false
    private var commandStateInvalidated = false
    private var lifecycleGeneration = 0

    private(set) var printer: Printer

    private let printerService: any PrinterServiceProtocol
    private let clock: @Sendable () -> Date

    convenience init(
        printerService: any PrinterServiceProtocol,
        printer: Printer,
        clock: @escaping @Sendable () -> Date = Date.init
    ) {
        self.init(printerService: printerService, composition: nil, printer: printer, clock: clock)
    }

    convenience init(
        composition: PrinterControlsComposition,
        printer: Printer,
        clock: @escaping @Sendable () -> Date = Date.init
    ) {
        self.init(
            printerService: composition.printerService, composition: composition,
            printer: printer, clock: clock
        )
    }

    private init(
        printerService: any PrinterServiceProtocol,
        composition: PrinterControlsComposition?,
        printer: Printer,
        clock: @escaping @Sendable () -> Date
    ) {
        self.printerService = printerService
        self.composition = composition
        self.printer = printer
        self.clock = clock
        leaseObservation = commandLeases.objectWillChange.sink { [weak self] _ in
            self?.objectWillChange.send()
        }
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
            if hardware == nil || hardwareLoadError != nil { await loadHardware() }
        } catch {
            guard !Task.isCancelled, canPublishRead(generation) else { return }
            // Failed reads are not cached as proof of unsupported hardware.
            // Retrying this read never replays a physical command.
            capabilityLoadError = error.localizedDescription
        }
    }

    func loadHardware() async {
        guard isActive, accessCheck() == nil, !isLoadingHardware else { return }
        let generation = lifecycleGeneration
        isLoadingHardware = true
        hardwareLoadError = nil
        defer { isLoadingHardware = false }
        do {
            let details = try await printerService.getDetails(id: printer.id)
            try Task.checkCancellation()
            guard canPublishRead(generation) else { return }
            guard details.id == printer.id else {
                hardwareLoadError = "Heater limits belong to a different printer. Reopen this printer."
                return
            }
            hardware = details.capabilities
        } catch {
            guard !Task.isCancelled, canPublishRead(generation) else { return }
            hardwareLoadError = "Heater limits could not be read. Retry the limits check. \(error.localizedDescription)"
        }
    }

    private func canPublishRead(_ generation: Int) -> Bool {
        generation == lifecycleGeneration && isActive && accessCheck() == nil
    }

    func configureAccess(serverID: UUID?, _ check: @escaping @MainActor () -> String?) {
        guard let serverID, composition != nil, registeredServerID == serverID else {
            deactivate()
            return
        }
        if !hasConfiguredAccess {
            accessCheck = check
            hasConfiguredAccess = true
        }
        isActive = true
        refreshAccess()
    }

    func matchesComposition(_ current: PrinterControlsComposition?) -> Bool {
        guard let composition, let current else { return false }
        return composition.identity == current.identity
    }

    func refreshAccess() {
        if accessCheck() != nil {
            lifecycleGeneration += 1
            cancelCalibration()
            cancelPendingCommand()
        }
    }

    func deactivate() {
        isActive = false
        lifecycleGeneration += 1
        cancelCalibration()
        cancelPendingCommand()
    }

    func cancelPendingCommand() {
        guard let command = pendingCommand else { return }
        if calibrationCommandID == command.id {
            calibrationCommandID = nil
            calibrationMessage = "Calibration interrupted. Check the machine; no step was confirmed. Cancel and start again."
        }
        telemetryConfirmed = false
        commandStateInvalidated = true
        if commandTask != nil, commandWasDispatched {
            // Canceling an observer cannot recall a physical command. Keep
            // both the response and its single-flight owner until it settles.
            commandNotice = "Stopped waiting for telemetry. The request outcome is unresolved; routine controls remain locked. The printer may still execute the request."
            return
        }
        commandTask?.cancel()
        commandTask = nil
        pendingCommand = nil
        releaseLease(for: command)
        commandNotice = commandWasDispatched
            ? "Stopped waiting. The printer may still execute the request. Check the machine before another action."
            : "Request canceled before dispatch. No printer command was sent."
    }

    // MARK: - Commands

    var extrusionBlockedReason: String? {
        if let reason = blockedReason { return reason }
        guard capabilities?.supportsExtrusion == true else {
            return "Extrusion is unavailable without explicit backend support."
        }
        guard let measured = printer.hotendTemp, measured.isFinite else {
            return "Current measured hotend temperature is unavailable. A target or preheat preset cannot authorize extrusion."
        }
        // The shared Printer/Details contract has neither a sample timestamp
        // nor a material-safe minimum. Catalog maxima and assigned spools are
        // not substitutes. Keep this closed until shared evidence exists.
        return "Safe extrusion is unavailable: the server does not report a verified material-safe minimum or hotend sample freshness. Use the printer's guarded controls. Preheat alone does not establish readiness."
    }

    func extrude(distanceMm: Double, speedMmPerSecond: Int) async {
        let feedrate: Int
        do {
            feedrate = try MaterialControlInput.feedrate(distance: distanceMm, speed: speedMmPerSecond)
        } catch {
            commandNotice = error.localizedDescription
            return
        }
        let command = ControlCommand(kind: .extrusion(distanceMm: distanceMm, feedrateMmMin: feedrate), startedAt: clock())
        guard beginCommand(command) else { return }
        defer { endCommand(command) }
        if let reason = extrusionBlockedReason {
            setError(command: command, message: reason, isRetryable: false)
            return
        }
        await perform(command) { [self] in
            if let reason = extrusionBlockedReason { throw PrinterControlError.invalidRequest(reason) }
            let result = try await printerService.extrude(
                printerId: printer.id, distanceMm: distanceMm, feedrateMmPerMinute: feedrate
            )
            guard result.success else { throw PrinterControlError.rejected(result.message) }
        }
        finishAcceptedRequest(command, notice: "Extrusion request accepted. Travel and physical filament state are not reported; verify at the printer.")
    }

    func filamentBlockedReason(_ operation: PhysicalFilamentOperation) -> String? {
        if let reason = blockedReason { return reason }
        let supported: Bool
        switch operation {
        case .load: supported = capabilities?.supportsFilamentLoad == true
        case .unload: supported = capabilities?.supportsFilamentUnload == true
        case .change: supported = capabilities?.supportsFilamentChange == true
        }
        return supported ? nil : "\(operation.title) is unavailable: no verified per-operation backend/macro support."
    }

    func performFilament(_ operation: PhysicalFilamentOperation) async {
        let command = ControlCommand(kind: .filament(operation), startedAt: clock())
        guard beginCommand(command) else { return }
        defer { endCommand(command) }
        if let reason = filamentBlockedReason(operation) {
            setError(command: command, message: reason, isRetryable: false)
            return
        }
        var responseMessage: String?
        await perform(command) { [self] in
            if let reason = filamentBlockedReason(operation) { throw PrinterControlError.invalidRequest(reason) }
            switch operation {
            case .load:
                let result = try await printerService.loadFilament(printerId: printer.id)
                guard result.success else { throw PrinterControlError.rejected(result.message) }
                responseMessage = result.message
            case .unload:
                // Residual weight/spool ID describe inventory, not physical
                // completion. No binding, clearing or slot targeting occurs.
                let result = try await printerService.unloadFilament(printerId: printer.id, toolheadIndex: nil)
                guard result.success else { throw PrinterControlError.rejected(result.message) }
                responseMessage = result.message
            case .change:
                let result = try await printerService.changeFilament(printerId: printer.id)
                guard result.success else { throw PrinterControlError.rejected(result.message) }
                responseMessage = result.message
            }
        }
        let detail = responseMessage.map { " Server response: \($0)" } ?? ""
        finishAcceptedRequest(command, notice: "\(operation.title) request accepted. Follow the printer's prompts and verify physical completion. Spool assignment was not changed.\(detail)")
    }

    private func finishAcceptedRequest(_ command: ControlCommand, notice: String) {
        guard pendingCommand == command, lastError == nil, !commandStateInvalidated,
              !Task.isCancelled, canControl else { return }
        pendingCommand = nil
        commandNotice = notice
    }

    // MARK: - Interruptible calibration

    var calibrationBlockedReason: String? {
        if let reason = blockedReason { return reason }
        guard capabilities?.supportsZOffset == true,
              capabilities?.supportsZOffsetFirmwareSave == true else {
            return "Firmware Z-offset calibration is not verified for this printer. Database-only storage is not physical calibration. Use the printer's supported calibration procedure."
        }
        guard capabilities?.supportsHoming == true else { return "Verified All-axes homing is required." }
        guard capabilities?.supportsAbsoluteMovement == true else { return "Verified absolute positioning is required." }
        return nil
    }

    var calibrationPositionBlockedReason: String? {
        if let reason = calibrationBlockedReason { return reason }
        guard Self.homedAxesSatisfied(["X", "Y", "Z"], in: printer) else {
            return "All axes must be confirmed homed before positioning."
        }
        // Build volume is catalog information, not a coordinate envelope.
        // There is no safe native center/clearance calculation in this contract.
        return "Automatic positioning is unavailable: the server reports no verified bed origin, travel bounds or safe clearance. Build-volume dimensions cannot establish a safe center. Continue calibration on the printer; no guessed move will be sent."
    }

    func startCalibration() async {
        guard !isExecuting, canControl, !isReviewingCalibration, calibrationStep == nil else { return }
        calibrationSession = UUID()
        let session = calibrationSession
        let generation = lifecycleGeneration
        calibrationStep = .introduction
        calibrationOffset = nil
        calibrationReview = nil
        calibrationMessage = nil
        isReviewingCalibration = true
        defer { isReviewingCalibration = false }
        do {
            let details = try await printerService.getDetails(id: printer.id)
            try Task.checkCancellation()
            guard session == calibrationSession, canPublishRead(generation), details.id == printer.id else { return }
            guard let offset = details.zOffsetMm, offset.isFinite, (-5...5).contains(offset) else {
                calibrationMessage = "Existing Z-offset is unknown or out of range. No zero baseline is assumed. Use the printer's calibration procedure."
                return
            }
            calibrationOffset = offset
        } catch {
            guard session == calibrationSession, canPublishRead(generation) else { return }
            calibrationMessage = "Calibration details could not be read. \(error.localizedDescription)"
        }
    }

    func cancelCalibration() {
        calibrationSession = UUID()
        if calibrationCommandID != nil { cancelPendingCommand() }
        calibrationCommandID = nil
        calibrationStep = nil
        calibrationOffset = nil
        calibrationReview = nil
        calibrationMessage = nil
    }

    func beginCalibrationHome() {
        guard calibrationStep == .introduction, !isExecuting, !isReviewingCalibration,
              calibrationOffset != nil else { return }
        if let reason = calibrationBlockedReason {
            calibrationMessage = reason
            return
        }
        calibrationStep = .home
    }

    func homeForCalibration() async {
        guard calibrationStep == .home, !isExecuting else { return }
        if let reason = calibrationBlockedReason { calibrationMessage = reason; return }
        let command = ControlCommand(kind: .calibrationHome, startedAt: clock())
        guard beginCommand(command) else { return }
        calibrationCommandID = command.id
        defer { endCommand(command) }
        await perform(command) { [printerService, printer] in
            try await printerService.home(printerId: printer.id, axes: ["X", "Y", "Z"])
        }
    }

    func positionForCalibration() async {
        guard calibrationStep == .position, !isExecuting else { return }
        // Explicitly refuse the step rather than issue a guessed absolute move.
        calibrationMessage = calibrationPositionBlockedReason
    }

    func adjustCalibration(delta: Double) async {
        guard calibrationStep == .adjust, !isExecuting, let offset = calibrationOffset else { return }
        if let reason = calibrationPositionBlockedReason { calibrationMessage = reason; return }
        guard let z = printer.z, z.isFinite else {
            calibrationMessage = "Measured Z position is unavailable."
            return
        }
        do {
            _ = try MaterialControlInput.adjustedOffset(offset, delta: delta)
            let expectedZ = (z * 1000 + delta * 1000).rounded() / 1000
            let command = ControlCommand(kind: .calibrationAdjust(delta: delta, expectedZ: expectedZ), startedAt: clock())
            guard beginCommand(command) else { return }
            calibrationCommandID = command.id
            defer { endCommand(command) }
            await perform(command) { [printerService, printer] in
                let result = try await printerService.moveTo(
                    printerId: printer.id, x: nil, y: nil, z: expectedZ, feedrateMmMin: Self.zFeedrateMmMin
                )
                guard result.success else { throw PrinterControlError.rejected(result.message) }
            }
        } catch { calibrationMessage = error.localizedDescription }
    }

    func reviewCalibration() async {
        guard calibrationStep == .adjust, !isExecuting, !isReviewingCalibration else { return }
        let session = calibrationSession
        let generation = lifecycleGeneration
        isReviewingCalibration = true
        defer { isReviewingCalibration = false }
        do {
            let details = try await printerService.getDetails(id: printer.id)
            try Task.checkCancellation()
            guard session == calibrationSession, canPublishRead(generation), details.id == printer.id else { return }
            guard let revision = details.rowVersion, !revision.isEmpty else {
                calibrationMessage = "Printer revision unavailable. Refresh and review again."
                return
            }
            calibrationReview = details
            calibrationStep = .save
        } catch {
            guard session == calibrationSession, canPublishRead(generation) else { return }
            calibrationMessage = "Could not refresh the calibration review. \(error.localizedDescription)"
        }
    }

    func saveCalibration() async {
        guard calibrationStep == .save, !isExecuting, let review = calibrationReview,
              let revision = review.rowVersion, let offset = calibrationOffset else { return }
        if let reason = calibrationBlockedReason { calibrationMessage = reason; return }
        guard offset.isFinite, (-5...5).contains(offset) else {
            calibrationMessage = "Z-offset must stay within -5…5 mm."
            return
        }
        let command = ControlCommand(kind: .calibrationSave(offsetMm: offset), startedAt: clock())
        guard beginCommand(command) else { return }
        calibrationCommandID = command.id
        let session = calibrationSession
        defer { endCommand(command) }
        await perform(command) { [printerService, printer] in
            let result = try await printerService.saveZOffset(
                printerId: printer.id, offsetMm: offset, saveToFirmware: true, reviewedRowVersion: revision
            )
            guard result.success else { throw PrinterControlError.rejected(result.message) }
        }
        guard session == calibrationSession else { return }
        // A review is one-use, even for 412/428 or uncertain transport results.
        calibrationReview = nil
        guard pendingCommand == command, lastError == nil, !commandStateInvalidated, canControl, !Task.isCancelled else {
            calibrationMessage = "Save was not confirmed. Check the printer, then cancel and refresh/review. No automatic retry was sent."
            return
        }
        finishAcceptedRequest(command, notice: "Firmware save request accepted. The printer may restart or disconnect. Verify the offset at the machine before printing.")
        calibrationStep = .done
    }

    private func confirmCalibration(_ command: ControlCommand) {
        guard calibrationCommandID == command.id, !commandStateInvalidated, canControl else { return }
        switch command.kind {
        case .calibrationHome:
            calibrationStep = .position
        case .calibrationAdjust(let delta, _):
            if let offset = calibrationOffset {
                calibrationOffset = try? MaterialControlInput.adjustedOffset(offset, delta: delta)
            }
        default: break
        }
        calibrationCommandID = nil
    }

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
        guard ControlNumberInput.hasCoordinatePrecision(distanceMm) else {
            setError(command: command, message: ControlNumberInput.coordinatePrecisionMessage, isRetryable: false)
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
        guard !isLoadingHardware, hardwareLoadError == nil,
              let maximum = heater == .hotend ? hardware?.maxHotendTemp : hardware?.maxBedTemp,
              maximum > 0 else { return nil }
        return maximum
    }

    var needsHeaterLimits: Bool {
        Heater.allCases.contains { supports($0) && maximum(for: $0) == nil }
    }

    func heaterTargetError(_ heater: Heater, target: Double) -> String? {
        guard ControlNumberInput.isWholeDegree(target) else { return ControlNumberInput.heaterPrecisionMessage }
        guard target >= 0 else { return "\(heater.title) target must be nonnegative." }
        if target == 0 { return nil }
        guard let maximum = maximum(for: heater) else {
            return "A valid reported \(heater.title.lowercased()) maximum is required for heating. Only zero-off is available; retry the heater limits check."
        }
        guard target <= Double(maximum) else {
            return "\(heater.title) target must not exceed the reported maximum of \(maximum) °C."
        }
        return nil
    }

    func preheatBlockedReason(_ preset: PreheatPreset) -> String? {
        if let reason = heaterTargetError(.hotend, target: preset.hotend) { return reason }
        if supports(.bed) { return heaterTargetError(.bed, target: preset.bed) }
        return nil
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
        let automaticFeedrate = z == nil ? Self.xyFeedrateMmMin : Self.zFeedrateMmMin
        let command = ControlCommand(
            kind: .moveTo(x: x, y: y, z: z, feedrateMmMin: automaticFeedrate), startedAt: clock()
        )
        guard beginCommand(command) else { return }
        defer { endCommand(command) }
        guard feedrateMmMin == nil else {
            setError(command: command, message: ControlNumberInput.customFeedrateMessage, isRetryable: false)
            return
        }
        let coordinates = [("X", x), ("Y", y), ("Z", z)].filter { $0.1 != nil }
        guard capabilities?.supportsAbsoluteMovement == true, !coordinates.isEmpty,
              coordinates.allSatisfy({ capabilities?.supportedAxes.contains($0.0) == true && $0.1!.isFinite }) else {
            setError(command: command, message: "Provide finite coordinates on supported axes.", isRetryable: false)
            return
        }
        guard coordinates.allSatisfy({ ControlNumberInput.hasCoordinatePrecision($0.1!) }) else {
            setError(command: command, message: ControlNumberInput.coordinatePrecisionMessage, isRetryable: false)
            return
        }
        await perform(command) { [printerService, printer] in
            let result = try await printerService.moveTo(
                printerId: printer.id, x: x, y: y, z: z, feedrateMmMin: automaticFeedrate
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
            cancelCalibration()
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
                confirmCalibration(pending)
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
        case .disableMotors, .extrusion, .filament, .calibrationSave:
            return false
        case .calibrationHome:
            return previous.homedAxes != updated.homedAxes && homedAxesSatisfied(["X", "Y", "Z"], in: updated)
        case .calibrationAdjust(_, let expectedZ):
            return updated.z == expectedZ && previous.z != updated.z
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

    private var commandIdentity: PrinterControlsIdentity? {
        registeredServerID.map { PrinterControlsIdentity(serverID: $0, printerID: printer.id) }
    }

    var isExecuting: Bool {
        pendingCommand != nil || commandIdentity.map(commandLeases.contains) == true
    }

    var canControl: Bool {
        commandIdentity != nil && hasConfiguredAccess && isActive && accessCheck() == nil
            && printer.isOnline && !isPrintingOrPaused
    }

    var blockedReason: String? {
        if commandIdentity == nil { return "Controls require a registered server identity." }
        if !isActive { return "Controls are no longer active." }
        if let reason = accessCheck() { return reason }
        if !printer.isOnline { return "Printer is offline." }
        if isPrintingOrPaused { return "Controls are locked while a print is active." }
        if pendingCommand == nil, isExecuting {
            return "Another controls view is waiting for this printer's request outcome. Routine controls remain locked."
        }
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

    /// One pipeline acquires the registered-server/printer lease before any
    /// dispatch. Local pending state additionally preserves telemetry observation.
    private func beginCommand(_ command: ControlCommand) -> Bool {
        guard !Task.isCancelled else { return false }
        guard pendingCommand == nil else { return false }
        if let step = calibrationStep, step != .introduction, step != .done {
            switch command.kind {
            case .calibrationHome, .calibrationAdjust, .calibrationSave:
                break
            default:
                commandNotice = "Cancel calibration before another setup command. Emergency Stop remains independent."
                return false
            }
        }
        guard canControl else {
            commandNotice = nil
            lastError = ControlsError(
                command: command,
                message: blockedReason ?? "Controls are unavailable.",
                isRetryable: false
            )
            return false
        }
        guard let identity = commandIdentity,
              commandLeases.acquire(identity, token: command.id) else { return false }
        lastError = nil
        commandNotice = nil
        telemetryConfirmed = false
        commandStateInvalidated = false
        commandWasDispatched = false
        pendingCommand = command
        return true
    }

    private func validateTemperature(_ target: Double?, heater: Heater, command: ControlCommand) -> Bool {
        guard let target else { return true }
        if let message = heaterTargetError(heater, target: target) {
            setError(command: command, message: message, isRetryable: false)
            return false
        }
        return true
    }

    private func perform(_ command: ControlCommand, _ call: @escaping @MainActor () async throws -> Void) async {
        guard !Task.isCancelled else {
            cancelPendingCommand()
            return
        }
        let generation = lifecycleGeneration
        let task = Task { @MainActor in
            try Task.checkCancellation()
            guard self.pendingCommand == command, self.canControl else { throw CancellationError() }
            try Task.checkCancellation()
            self.commandWasDispatched = true
            try await call()
        }
        commandTask = task
        let result = await withTaskCancellationHandler {
            await task.result
        } onCancel: {
            Task { @MainActor in
                guard self.pendingCommand == command else { return }
                self.cancelPendingCommand()
            }
        }
        guard pendingCommand == command else { return }
        commandTask = nil
        guard generation == lifecycleGeneration else {
            pendingCommand = nil
            commandNotice = "Request observation ended after controls access changed. Physical outcome is unknown; check the original printer."
            return
        }
        switch result {
        case .success:
            if Task.isCancelled || !canControl || commandStateInvalidated {
                pendingCommand = nil
                commandNotice = "Request accepted, but waiting was interrupted or controls became unavailable. Physical outcome is unknown; check the machine before another action."
                return
            }
            commandNotice = "Request accepted; waiting for matching telemetry. This does not confirm physical completion."
        case .failure(let error):
            if error is CancellationError {
                pendingCommand = nil
                commandNotice = commandWasDispatched
                    ? "Request observation was canceled. Physical outcome is unknown; check the machine before another action."
                    : "Request canceled before dispatch. No printer command was sent."
            } else {
                setError(command: command, error: error)
            }
        }
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
    ///     further delta would hang forever, so we clear now. Only a fresh
    ///     post-dispatch snapshot permits telemetry wording; cached matches
    ///     report request acceptance without physical confirmation.
    /// Every exit releases only this invocation's shared lease. Post-response
    /// telemetry observation does not retain a transport lease.
    private func endCommand(_ command: ControlCommand) {
        defer { releaseLease(for: command) }
        guard pendingCommand == command else { return }
        if lastError?.command == command {
            pendingCommand = nil
            return
        }
        if telemetryConfirmed {
            confirmCalibration(command)
            pendingCommand = nil
            commandNotice = Self.confirmationNotice(for: command)
        } else if Self.transition(from: printer, to: printer, resolves: command) {
            pendingCommand = nil
            commandNotice = "Request accepted. Previously reported values already match; fresh physical completion is not confirmed. Check the machine before further setup."
        }
    }

    private func releaseLease(for command: ControlCommand) {
        guard let identity = commandIdentity else { return }
        commandLeases.release(identity, token: command.id)
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
