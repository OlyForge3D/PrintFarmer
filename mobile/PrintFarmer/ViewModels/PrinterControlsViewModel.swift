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

    enum Section: String, CaseIterable {
        case heat, motion, material
    }

    var section: Section {
        switch kind {
        case .preheat, .heater, .heaterTargets:
            return .heat
        case .home, .jog, .moveTo, .disableMotors,
             .calibrationHome, .calibrationPosition, .calibrationAdjust, .calibrationSave:
            return .motion
        case .extrusion, .filament:
            return .material
        }
    }

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
        case heaterTargets(hotend: Double?, bed: Double?)
        case moveTo(x: Double?, y: Double?, z: Double?, feedrateMmMin: Int?)
        case disableMotors
        case extrusion(distanceMm: Double, feedrateMmMin: Int)
        case filament(PhysicalFilamentOperation)
        case calibrationHome
        case calibrationPosition(target: SafetyVector3Dto, centered: Bool)
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
    static let absoluteCoordinatesMessage = "Enter destination coordinate in mm for X, Y, or Z. Unspecified axes remain unchanged."
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

    static func absolutePosition(x: Double?, y: Double?, z: Double?) throws -> (x: Double?, y: Double?, z: Double?) {
        guard x != nil || y != nil || z != nil else {
            throw PrinterControlError.invalidRequest(absoluteCoordinatesMessage)
        }
        if let x, (!x.isFinite || !hasCoordinatePrecision(x)) {
            throw PrinterControlError.invalidRequest(coordinatePrecisionMessage)
        }
        if let y, (!y.isFinite || !hasCoordinatePrecision(y)) {
            throw PrinterControlError.invalidRequest(coordinatePrecisionMessage)
        }
        if let z, (!z.isFinite || !hasCoordinatePrecision(z)) {
            throw PrinterControlError.invalidRequest(coordinatePrecisionMessage)
        }
        return (x: x, y: y, z: z)
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

private struct PrinterControlsIdentity: Hashable, Sendable {
    let serverID: UUID
    let printerID: UUID
}

/// Process-wide request/telemetry-wait ownership, independent of service lifetime.
/// Only tokens are retained; neither command tasks nor view models live here.
@MainActor
private final class PrinterControlsLeases: ObservableObject {
    static let shared = PrinterControlsLeases()
    @Published private var owners: [PrinterControlsIdentity: UUID] = [:]
    @Published private var workflows: [PrinterControlsIdentity: UUID] = [:]

    func contains(_ identity: PrinterControlsIdentity, excludingWorkflow: UUID? = nil) -> Bool {
        owners[identity] != nil || (workflows[identity] != nil && workflows[identity] != excludingWorkflow)
    }

    func acquire(_ identity: PrinterControlsIdentity, token: UUID, workflow: UUID? = nil) -> AnyCancellable? {
        guard !contains(identity, excludingWorkflow: workflow) else { return nil }
        owners[identity] = token
        return AnyCancellable {
            Task { @MainActor in self.release(identity, token: token) }
        }
    }

    func acquireWorkflow(_ identity: PrinterControlsIdentity, token: UUID) -> AnyCancellable? {
        guard !contains(identity) else { return nil }
        workflows[identity] = token
        return AnyCancellable {
            Task { @MainActor in self.releaseWorkflow(identity, token: token) }
        }
    }

    func releaseWorkflow(_ identity: PrinterControlsIdentity, token: UUID) {
        guard workflows[identity] == token else { return }
        workflows.removeValue(forKey: identity)
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
    @Published private(set) var pendingCommand: ControlCommand? {
        didSet {
            // Live confirmation and explicit observation cleanup share the
            // same token release, but neither may unlock an outstanding HTTP call.
            if let previous = oldValue, pendingCommand != previous, commandTask == nil {
                releaseLease(for: previous)
            }
        }
    }
    @Published private(set) var isLoadingCapabilities: Bool = false
    @Published private(set) var capabilityLoadError: String?
    @Published private(set) var hardware: PrinterHardwareCapabilities?
    @Published private(set) var isLoadingHardware = false
    @Published private(set) var hardwareLoadError: String?
    @Published private(set) var commandNotice: String?
    // Keep the originating card after pendingCommand is cleared by an outcome.
    @Published private(set) var feedbackSection: ControlCommand.Section?
    @Published private(set) var isActive = true
    @Published private(set) var calibrationStep: ZOffsetCalibrationStep?
    @Published private(set) var calibrationOffset: Double?
    @Published private(set) var calibrationReview: PrinterDetails?
    @Published private(set) var calibrationMessage: String?
    @Published private(set) var isReviewingCalibration = false
    @Published private(set) var safetyStatus: PrinterStatusDetail?
    @Published private(set) var isRefreshingSafety = false
    @Published private(set) var safetyReadError: String?
    @Published private(set) var safetyCheckedAt: Date?
    private var safetyReadID = UUID()
    private var calibrationFrame: SafetyVector3Dto?
    private var calibrationPosition: SafetyVector3Dto?
    private var calibrationBaseline: Double?
    private var calibrationInterrupted = false
    private var calibrationSession = UUID()
    private var calibrationLease: UUID?
    private var calibrationLeaseLifetime: AnyCancellable?
    private var calibrationCommandID: UUID?
    var registeredServerID: UUID? { composition?.identity.serverID }
    var compositionIdentity: PrinterControlsComposition.Identity? { composition?.identity }

    private let composition: PrinterControlsComposition?
    private var accessCheck: @MainActor () -> String? = { nil }
    private var hasConfiguredAccess = false
    private let commandLeases = PrinterControlsLeases.shared
    private var commandLease: (id: UUID, lifetime: AnyCancellable)?
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
            if safetyStatus == nil { await refreshSafetyEvidence(refreshDiscovery: false) }
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

    /// Read-only refresh; never retries a physical command or refreshes If-Match.
    func refreshSafetyEvidence(refreshDiscovery: Bool = true) async {
        guard isActive, accessCheck() == nil, !isRefreshingSafety else { return }
        let generation = lifecycleGeneration
        let readID = UUID()
        safetyReadID = readID
        isRefreshingSafety = true
        let startedAt = clock()
        defer {
            if safetyReadID == readID { isRefreshingSafety = false }
        }
        do {
            let loaded = refreshDiscovery
                ? try await printerService.getBackendCapabilities(printerId: printer.id) : capabilities
            try Task.checkCancellation()
            guard canPublishRead(generation), safetyReadID == readID else { return }
            if refreshDiscovery {
                if let old = capabilities?.verifiedSafety, calibrationStep != nil {
                    if let new = loaded?.verifiedSafety {
                        if !Self.sameSafetyConfiguration(old, new) {
                            interruptCalibration("Safety discovery changed. Cancel and review calibration again.")
                        }
                    } else {
                        interruptCalibration("Safety discovery is no longer available. Cancel and review again.")
                    }
                }
                capabilities = loaded
            }
            let status = try await printerService.getStatus(id: printer.id)
            try Task.checkCancellation()
            guard canPublishRead(generation), safetyReadID == readID else { return }
            guard status.id == printer.id else { throw NetworkError.invalidResponse }
            let previous = safetyStatus
            safetyStatus = status
            safetyCheckedAt = clock()
            safetyReadError = nil
            if !status.isOnline || ["printing", "paused", "starting"].contains(status.state?.lowercased() ?? "") {
                interruptCalibration("Printer readiness changed. Cancel and check the machine.")
            }
            if let frame = calibrationFrame, status.safetyTelemetry?.coordinateOriginOffsetMm.value != frame {
                interruptCalibration("Movement frame changed. Cancel, re-home and review again.")
            }
            confirmCalibrationObservation(previous: previous, status: status, readStartedAt: startedAt)
        } catch {
            guard canPublishRead(generation), safetyReadID == readID else { return }
            safetyStatus = nil
            safetyCheckedAt = nil
            safetyReadError = "Safety evidence could not be read. Refresh safety checks. \(error.localizedDescription)"
            interruptCalibration("Safety evidence could not be read. Check the machine, then cancel and review again.")
        }
    }

    private static func sameSafetyConfiguration(_ a: PrinterVerifiedSafetyDto, _ b: PrinterVerifiedSafetyDto) -> Bool {
        a.contractVersion == b.contractVersion &&
        a.discovery.sourceRevision == b.discovery.sourceRevision &&
        a.discovery.state == b.discovery.state &&
        a.positioning.coordinateOriginMm.state == b.positioning.coordinateOriginMm.state &&
        a.positioning.travelEnvelopeMm.state == b.positioning.travelEnvelopeMm.state &&
        a.positioning.minimumClearanceZMm.state == b.positioning.minimumClearanceZMm.state &&
        a.positioning.coordinateOriginMm.value == b.positioning.coordinateOriginMm.value &&
        a.positioning.travelEnvelopeMm.value == b.positioning.travelEnvelopeMm.value &&
        a.positioning.minimumClearanceZMm.value == b.positioning.minimumClearanceZMm.value &&
        a.operations.absoluteMovement.support == b.operations.absoluteMovement.support &&
        a.operations.firmwareZOffsetSave.support == b.operations.firmwareZOffsetSave.support
    }

    private func invalidateSafety() {
        safetyReadID = UUID()
        isRefreshingSafety = false
        safetyStatus = nil
        safetyCheckedAt = nil
        capabilities?.verifiedSafety = nil
    }

    func suspendSafetyObservation() {
        invalidateSafety()
        cancelCalibration()
    }

    private var safetyEvidenceBlockedReason: String? {
        guard let safety = capabilities?.verifiedSafety, safety.contractVersion == 1,
              safety.discovery.state != .unavailable,
              let revision = safety.discovery.sourceRevision, revision == String(printer.configurationRevision),
              let observed = safety.discovery.observedAtUtc, observed <= clock() else {
            return "Verified safety discovery is unavailable. Refresh safety checks or use the printer's supported procedure."
        }
        guard let checked = safetyCheckedAt, clock().timeIntervalSince(checked) >= 0,
              clock().timeIntervalSince(checked) <= 15, let status = safetyStatus else {
            return safetyReadError ?? "Refresh safety checks: current printer safety telemetry is unavailable."
        }
        guard status.isOnline,
              !["printing", "paused", "starting"].contains(status.state?.lowercased() ?? "") else {
            return "Safety status reports the printer offline or a print active."
        }
        return nil
    }

    private func hasProvenance(_ source: String?, _ observed: Date?) -> Bool {
        guard let source, !source.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty,
              let observed, let discovery = capabilities?.verifiedSafety?.discovery.observedAtUtc else { return false }
        return observed <= clock() && observed <= discovery
    }

    private func supportReason(_ operation: VerifiedSafetyOperationCapabilityDto?, title: String) -> String? {
        guard let operation else { return "\(title): verified support is unknown. Refresh safety checks." }
        if operation.support == .unsupported {
            return "\(title) is unsupported by the authoritative printer probe. Use the printer's supported procedure."
        }
        guard operation.support == .supported, hasProvenance(operation.source, operation.observedAtUtc) else {
            return "\(title): verified support or its provenance is unknown. Refresh safety checks."
        }
        return nil
    }

    private var materialTemperatureBlockedReason: String? {
        if let reason = safetyEvidenceBlockedReason { return reason }
        guard let minimum = capabilities?.verifiedSafety?.extrusion.minimumSafeMeasuredHotendTemperatureC,
              minimum.state == .verified, let value = minimum.value, value.isFinite,
              hasProvenance(minimum.source, minimum.observedAtUtc) else {
            return "A verified material-safe minimum is unavailable. Firmware cold-extrusion limits and assigned spools are not safety evidence."
        }
        guard let measured = safetyStatus?.safetyTelemetry?.measuredHotendTemperatureC,
              measured.isFresh(at: clock()), let temperature = measured.value, temperature.isFinite else {
            return "Fresh measured hotend temperature is unavailable. Refresh safety checks; a hot target cannot authorize extrusion."
        }
        return temperature >= value ? nil :
            "Measured hotend is below the verified minimum of \(value.formatted()) °C. Use Hotend preheat, then refresh safety checks."
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
            invalidateSafety()
            cancelCalibration()
            cancelPendingCommand()
        }
    }

    func deactivate() {
        isActive = false
        lifecycleGeneration += 1
        invalidateSafety()
        cancelCalibration()
        cancelPendingCommand()
    }

    func cancelPendingCommand() {
        guard let command = pendingCommand else { return }
        if calibrationCommandID == command.id {
            calibrationCommandID = nil
            calibrationInterrupted = true
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
        return materialTemperatureBlockedReason
    }

    func extrude(distanceMm: Double, speedMmPerSecond: Int) async {
        let feedrate: Int
        do {
            feedrate = try MaterialControlInput.feedrate(distance: distanceMm, speed: speedMmPerSecond)
        } catch {
            guard pendingCommand == nil else { return }
            feedbackSection = .material
            lastError = nil
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
        let evidence: VerifiedSafetyOperationCapabilityDto?
        switch operation {
        case .load:
            supported = capabilities?.supportsFilamentLoad == true
            evidence = capabilities?.verifiedSafety?.operations.filamentLoad
        case .unload:
            supported = capabilities?.supportsFilamentUnload == true
            evidence = capabilities?.verifiedSafety?.operations.filamentUnload
        case .change:
            supported = capabilities?.supportsFilamentChange == true
            evidence = capabilities?.verifiedSafety?.operations.filamentChange
        }
        guard supported else { return "\(operation.title) is unavailable: no verified per-operation backend/macro support." }
        return supportReason(evidence, title: operation.title) ?? materialTemperatureBlockedReason
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
        if calibrationInterrupted { return calibrationMessage ?? "Calibration interrupted. Cancel and start again." }
        guard capabilities?.supportsZOffset == true,
              capabilities?.supportsZOffsetFirmwareSave == true else {
            return "Firmware Z-offset calibration is not verified for this printer. Database-only storage is not physical calibration. Use the printer's supported calibration procedure."
        }
        guard capabilities?.supportsHoming == true else { return "Verified All-axes homing is required." }
        guard capabilities?.supportsAbsoluteMovement == true else { return "Verified absolute positioning is required." }
        return safetyEvidenceBlockedReason ??
            supportReason(capabilities?.verifiedSafety?.operations.firmwareZOffsetSave, title: "Firmware save") ??
            supportReason(capabilities?.verifiedSafety?.operations.absoluteMovement, title: "Absolute positioning")
    }

    var calibrationPositionBlockedReason: String? {
        if let reason = calibrationBlockedReason { return reason }
        return positioningEvidenceBlockedReason
    }

    private var positioningEvidenceBlockedReason: String? {
        if let reason = safetyEvidenceBlockedReason { return reason }
        guard let geometry = capabilities?.verifiedSafety?.positioning,
              geometry.coordinateOriginMm.state == .verified,
              let origin = geometry.coordinateOriginMm.value, origin.isFinite,
              hasProvenance(geometry.coordinateOriginMm.source, geometry.coordinateOriginMm.observedAtUtc),
              geometry.travelEnvelopeMm.state == .verified,
              let envelope = geometry.travelEnvelopeMm.value, envelope.isValid,
              hasProvenance(geometry.travelEnvelopeMm.source, geometry.travelEnvelopeMm.observedAtUtc),
              geometry.minimumClearanceZMm.state == .verified,
              let clearance = geometry.minimumClearanceZMm.value, clearance.isFinite,
              hasProvenance(geometry.minimumClearanceZMm.source, geometry.minimumClearanceZMm.observedAtUtc),
              (envelope.minimum.z...envelope.maximum.z).contains(clearance) else {
            return "Verified bed origin, travel bounds and safe clearance are required. Catalog build-volume dimensions cannot establish a safe move."
        }
        guard let telemetry = safetyStatus?.safetyTelemetry,
              telemetry.homedAxes.isFresh(at: clock()),
              Set(telemetry.homedAxes.value?.map { $0.uppercased() } ?? []).isSuperset(of: ["X", "Y", "Z"]) else {
            return "All axes must be freshly reported homed. Refresh safety checks or re-home."
        }
        guard telemetry.coordinateOriginOffsetMm.isFresh(at: clock()),
              let frame = telemetry.coordinateOriginOffsetMm.value, frame.isFinite else {
            return "Fresh coordinate-frame telemetry is required. Refresh safety checks."
        }
        guard frame == origin, calibrationFrame == nil || calibrationFrame == frame else {
            return "Movement frame differs from verified discovery. Cancel and refresh/re-home before moving."
        }
        return nil
    }

    private var reportedSafetyPosition: SafetyVector3Dto? {
        guard let status = safetyStatus, let x = status.x, let y = status.y, let z = status.z else { return nil }
        let position = SafetyVector3Dto(x: x, y: y, z: z)
        return position.isFinite ? position : nil
    }

    /// Quantize only internally derived positions, never entered or reported
    /// coordinates. Check the effective frame after rounding; a clearance lift
    /// must round upward and a center must stay inside the verified envelope.
    private static func derivedCalibrationCoordinate(
        _ desired: Double, frameOffset: Double, minimum: Double, maximum: Double
    ) -> Double? {
        guard desired.isFinite, frameOffset.isFinite, minimum.isFinite, maximum.isFinite,
              minimum <= maximum else { return nil }
        func quantized(_ value: Double) -> Double? {
            guard value.isFinite else { return nil }
            return Double(String(format: "%.3f", locale: Locale(identifier: "en_US_POSIX"), value))
        }
        guard var value = quantized(desired) else { return nil }
        // Comparing in the effective frame avoids treating .2 - .05's binary
        // tail as a real extra micron, while never rounding below clearance.
        if value + frameOffset < minimum {
            guard let next = quantized(value + 0.001) else { return nil }
            value = next
        } else if value + frameOffset > maximum {
            guard let next = quantized(value - 0.001) else { return nil }
            value = next
        }
        guard ControlNumberInput.hasCoordinatePrecision(value),
              (minimum...maximum).contains(value + frameOffset) else { return nil }
        return value
    }

    private func safeMoveReason(_ point: SafetyVector3Dto) -> String? {
        if let reason = positioningEvidenceBlockedReason { return reason }
        guard let frame = safetyStatus?.safetyTelemetry?.coordinateOriginOffsetMm.value,
              let envelope = capabilities?.verifiedSafety?.positioning.travelEnvelopeMm.value,
              let clearance = capabilities?.verifiedSafety?.positioning.minimumClearanceZMm.value else {
            return "Verified movement geometry is unavailable."
        }
        let effective = SafetyVector3Dto(x: point.x + frame.x, y: point.y + frame.y, z: point.z + frame.z)
        guard [point.x, point.y, point.z].allSatisfy(ControlNumberInput.hasCoordinatePrecision),
              envelope.contains(effective), effective.z >= clearance else {
            return "Move exceeds verified travel bounds, minimum clearance or supported coordinate precision. Use the printer's supported procedure."
        }
        return nil
    }

    private func interruptCalibration(_ message: String) {
        guard calibrationStep != nil, calibrationStep != .done else { return }
        if calibrationCommandID != nil { cancelPendingCommand() }
        calibrationInterrupted = true
        calibrationReview = nil
        calibrationMessage = message
    }

    func startCalibration() async {
        guard !isExecuting, canControl, !isReviewingCalibration, calibrationStep == nil else { return }
        calibrationSession = UUID()
        guard let identity = commandIdentity,
              let lifetime = commandLeases.acquireWorkflow(identity, token: calibrationSession) else { return }
        calibrationLease = calibrationSession
        calibrationLeaseLifetime = lifetime
        let session = calibrationSession
        let generation = lifecycleGeneration
        calibrationStep = .introduction
        calibrationOffset = nil
        calibrationReview = nil
        calibrationMessage = nil
        calibrationInterrupted = false
        calibrationFrame = nil
        calibrationPosition = nil
        calibrationBaseline = nil
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
            calibrationBaseline = offset
        } catch {
            guard session == calibrationSession, canPublishRead(generation) else { return }
            calibrationMessage = "Calibration details could not be read. \(error.localizedDescription)"
        }
    }

    func cancelCalibration() {
        releaseCalibrationLease()
        calibrationSession = UUID()
        if calibrationCommandID != nil { cancelPendingCommand() }
        calibrationCommandID = nil
        calibrationStep = nil
        calibrationOffset = nil
        calibrationReview = nil
        calibrationMessage = nil
        calibrationInterrupted = false
        calibrationFrame = nil
        calibrationPosition = nil
        calibrationBaseline = nil
    }

    private func releaseCalibrationLease() {
        if let token = calibrationLease, let identity = commandIdentity {
            commandLeases.releaseWorkflow(identity, token: token)
        }
        calibrationLease = nil
        calibrationLeaseLifetime = nil
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
        await perform(command) { [self] in
            if let reason = calibrationBlockedReason { throw PrinterControlError.invalidRequest(reason) }
            try await printerService.home(printerId: printer.id, axes: ["X", "Y", "Z"])
        }
    }

    func positionForCalibration() async {
        guard calibrationStep == .position, !isExecuting else { return }
        if let reason = calibrationPositionBlockedReason { calibrationMessage = reason; return }
        guard let current = reportedSafetyPosition,
              let geometry = capabilities?.verifiedSafety?.positioning,
              let envelope = geometry.travelEnvelopeMm.value,
              let frame = safetyStatus?.safetyTelemetry?.coordinateOriginOffsetMm.value,
              let clearance = geometry.minimumClearanceZMm.value else {
            calibrationMessage = "Reported X, Y and Z are required; no starting position is assumed."
            return
        }
        // Lift vertically before lateral motion. Never lower to an invented
        // paper-test height, or cross the verified minimum clearance.
        let needsLift = current.z + frame.z < clearance
        let target: SafetyVector3Dto
        if needsLift {
            guard let z = Self.derivedCalibrationCoordinate(
                clearance - frame.z, frameOffset: frame.z,
                minimum: clearance, maximum: envelope.maximum.z
            ) else {
                calibrationMessage = "Verified clearance cannot be reached within travel bounds at 0.001 mm transport precision. Use the printer's supported calibration procedure."
                return
            }
            target = .init(x: current.x, y: current.y, z: z)
        } else {
            guard let x = Self.derivedCalibrationCoordinate(
                envelope.minimum.x + (envelope.maximum.x - envelope.minimum.x) / 2 - frame.x,
                frameOffset: frame.x, minimum: envelope.minimum.x, maximum: envelope.maximum.x
            ), let y = Self.derivedCalibrationCoordinate(
                envelope.minimum.y + (envelope.maximum.y - envelope.minimum.y) / 2 - frame.y,
                frameOffset: frame.y, minimum: envelope.minimum.y, maximum: envelope.maximum.y
            ) else {
                calibrationMessage = "Verified travel bounds contain no center at 0.001 mm transport precision. Use the printer's supported calibration procedure."
                return
            }
            target = .init(x: x, y: y, z: current.z)
        }
        if let reason = safeMoveReason(target) { calibrationMessage = reason; return }
        if target == current {
            calibrationPosition = current
            calibrationStep = .adjust
            calibrationMessage = "Latest status already reports the verified center. No positioning command was needed."
            return
        }
        let command = ControlCommand(kind: .calibrationPosition(target: target, centered: !needsLift), startedAt: clock())
        guard beginCommand(command) else { return }
        calibrationCommandID = command.id
        defer { endCommand(command) }
        await perform(command) { [self] in
            if let reason = calibrationPositionBlockedReason ?? safeMoveReason(target) {
                throw PrinterControlError.invalidRequest(reason)
            }
            let result = try await printerService.moveTo(
                printerId: printer.id, x: target.x, y: target.y, z: target.z, feedrateMmMin: Self.zFeedrateMmMin
            )
            guard result.success else { throw PrinterControlError.rejected(result.message) }
        }
    }

    func calibrationAdjustmentBlockedReason(delta: Double) -> String? {
        if let reason = calibrationPositionBlockedReason { return reason }
        guard let offset = calibrationOffset, let current = reportedSafetyPosition, current == calibrationPosition else {
            return "Position changed or is unavailable. Cancel and re-position before adjusting."
        }
        do { _ = try MaterialControlInput.adjustedOffset(offset, delta: delta) }
        catch { return error.localizedDescription }
        let z = (current.z * 1000 + delta * 1000).rounded() / 1000
        return safeMoveReason(.init(x: current.x, y: current.y, z: z))
    }

    func adjustCalibration(delta: Double) async {
        guard calibrationStep == .adjust, !isExecuting, let offset = calibrationOffset else { return }
        if let reason = calibrationAdjustmentBlockedReason(delta: delta) { calibrationMessage = reason; return }
        guard let current = reportedSafetyPosition, current == calibrationPosition else {
            calibrationMessage = "Position changed or is unavailable. Cancel and re-position before adjusting."
            return
        }
        do {
            _ = try MaterialControlInput.adjustedOffset(offset, delta: delta)
            let expectedZ = (current.z * 1000 + delta * 1000).rounded() / 1000
            let target = SafetyVector3Dto(x: current.x, y: current.y, z: expectedZ)
            if let reason = safeMoveReason(target) { calibrationMessage = reason; return }
            let command = ControlCommand(kind: .calibrationAdjust(delta: delta, expectedZ: expectedZ), startedAt: clock())
            guard beginCommand(command) else { return }
            calibrationCommandID = command.id
            defer { endCommand(command) }
            await perform(command) { [self] in
                if let reason = calibrationPositionBlockedReason ?? safeMoveReason(target) {
                    throw PrinterControlError.invalidRequest(reason)
                }
                let result = try await printerService.moveTo(
                    printerId: printer.id, x: target.x, y: target.y, z: target.z, feedrateMmMin: Self.zFeedrateMmMin
                )
                guard result.success else { throw PrinterControlError.rejected(result.message) }
            }
        } catch { calibrationMessage = error.localizedDescription }
    }

    func reviewCalibration() async {
        guard calibrationStep == .adjust, !isExecuting, !isReviewingCalibration else { return }
        if let reason = calibrationPositionBlockedReason { calibrationMessage = reason; return }
        guard reportedSafetyPosition == calibrationPosition else {
            interruptCalibration("Position changed. Cancel and calibrate again before reviewing.")
            return
        }
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
            guard details.zOffsetMm == calibrationBaseline else {
                interruptCalibration("Stored Z-offset changed during calibration. Cancel and review the new baseline.")
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
        if let reason = calibrationPositionBlockedReason { calibrationMessage = reason; return }
        guard reportedSafetyPosition == calibrationPosition else {
            interruptCalibration("Position changed after review. Cancel and calibrate again.")
            return
        }
        guard offset.isFinite, (-5...5).contains(offset) else {
            calibrationMessage = "Z-offset must stay within -5…5 mm."
            return
        }
        let command = ControlCommand(kind: .calibrationSave(offsetMm: offset), startedAt: clock())
        guard beginCommand(command) else { return }
        calibrationCommandID = command.id
        let session = calibrationSession
        defer { endCommand(command) }
        await perform(command) { [self] in
            if let reason = calibrationPositionBlockedReason { throw PrinterControlError.invalidRequest(reason) }
            let result = try await printerService.saveZOffset(
                printerId: printer.id, offsetMm: offset, saveToFirmware: true, reviewedRowVersion: revision
            )
            guard result.success else { throw PrinterControlError.rejected(result.message) }
        }
        guard session == calibrationSession else { return }
        // A review is one-use, even for 412/428 or uncertain transport results.
        calibrationReview = nil
        guard pendingCommand == command, lastError == nil, !commandStateInvalidated, canControl, !Task.isCancelled else {
            calibrationInterrupted = true
            calibrationMessage = "Save was not confirmed. Check the printer, then cancel and refresh/review. No automatic retry was sent."
            return
        }
        finishAcceptedRequest(command, notice: "Firmware save request accepted. The printer may restart or disconnect. Verify the offset at the machine before printing.")
        calibrationStep = .done
        calibrationCommandID = nil
        releaseCalibrationLease()
    }

    private func confirmCalibration(_ command: ControlCommand) {
        guard calibrationCommandID == command.id, !commandStateInvalidated, canControl else { return }
        guard calibrationPositionBlockedReason == nil else {
            interruptCalibration("Safety evidence expired before command confirmation. Cancel and check the machine.")
            return
        }
        switch command.kind {
        case .calibrationHome:
            calibrationStep = .position
            calibrationFrame = safetyStatus?.safetyTelemetry?.coordinateOriginOffsetMm.value
        case .calibrationPosition(let target, let centered):
            calibrationPosition = target
            if centered { calibrationStep = .adjust }
            calibrationMessage = centered
                ? "Position reported at the verified center. Adjust only within the verified clearance."
                : "Clearance lift confirmed. Continue positioning to the verified center."
        case .calibrationAdjust(let delta, _):
            if let offset = calibrationOffset {
                calibrationOffset = try? MaterialControlInput.adjustedOffset(offset, delta: delta)
            }
            calibrationPosition = reportedSafetyPosition
        default: break
        }
        calibrationCommandID = nil
    }

    private func confirmCalibrationObservation(
        previous: PrinterStatusDetail?, status: PrinterStatusDetail, readStartedAt: Date
    ) {
        guard let command = pendingCommand, calibrationCommandID == command.id,
              commandWasDispatched, !commandStateInvalidated, !calibrationInterrupted,
              readStartedAt >= command.startedAt, calibrationPositionBlockedReason == nil else { return }
        let matches: Bool
        switch command.kind {
        case .calibrationHome:
            matches = (status.safetyTelemetry?.homedAxes.observedAtUtc ?? .distantPast) > command.startedAt
        case .calibrationPosition(let target, _):
            matches = reportedSafetyPosition == target &&
                (previous?.x != status.x || previous?.y != status.y || previous?.z != status.z)
        case .calibrationAdjust(_, let expectedZ):
            matches = status.z == expectedZ && previous?.z != status.z &&
                status.x == calibrationPosition?.x && status.y == calibrationPosition?.y
        default: return
        }
        if matches {
            telemetryConfirmed = true
            if commandTask == nil {
                confirmCalibration(command)
                pendingCommand = nil
                commandNotice = Self.confirmationNotice(for: command)
            }
        }
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
        guard supports(.hotend) else { return "Hotend temperature control is unavailable." }
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

    func setHeaterTargets(hotend: Double?, bed: Double?) async {
        let command = ControlCommand(kind: .heaterTargets(hotend: hotend, bed: bed), startedAt: clock())
        guard beginCommand(command) else { return }
        defer { endCommand(command) }
        guard hotend != nil || bed != nil else {
            setError(command: command, message: "Enter at least one target. Blank leaves a heater unchanged.", isRetryable: false)
            return
        }
        guard (hotend == nil || supports(.hotend)), (bed == nil || supports(.bed)) else {
            setError(command: command, message: "A requested heater is unavailable. Refresh heater support before setting targets.", isRetryable: false)
            return
        }
        guard validateTemperature(hotend, heater: .hotend, command: command),
              validateTemperature(bed, heater: .bed, command: command) else { return }
        await perform(command) { [printerService, printer] in
            try await printerService.setTemperatures(printerId: printer.id, hotend: hotend, bed: bed)
        }
    }

    func absoluteMoveBlockedReason(x: Double?, y: Double?, z: Double?, feedrateMmMin: Int? = nil) -> String? {
        if let reason = blockedReason { return reason }
        guard feedrateMmMin == nil else { return ControlNumberInput.customFeedrateMessage }
        let target: (x: Double?, y: Double?, z: Double?)
        do {
            target = try ControlNumberInput.absolutePosition(x: x, y: y, z: z)
        } catch { return error.localizedDescription }
        guard capabilities?.supportsAbsoluteMovement == true,
              Set(capabilities?.supportedAxes ?? []).isSuperset(of: ["X", "Y", "Z"]) else {
            return "Absolute movement requires confirmed support for all X, Y and Z axes."
        }
        guard let current = reportedSafetyPosition else {
            return positioningEvidenceBlockedReason ?? "Reported X, Y and Z positions are required to validate safe movement."
        }
        let fullPoint = SafetyVector3Dto(
            x: target.x ?? current.x,
            y: target.y ?? current.y,
            z: target.z ?? current.z
        )
        return supportReason(capabilities?.verifiedSafety?.operations.absoluteMovement, title: "Absolute positioning")
            ?? safeMoveReason(fullPoint)
    }

    func moveTo(x: Double?, y: Double?, z: Double?, feedrateMmMin: Int?) async {
        let automaticFeedrate = Self.zFeedrateMmMin
        let command = ControlCommand(
            kind: .moveTo(x: x, y: y, z: z, feedrateMmMin: automaticFeedrate), startedAt: clock()
        )
        guard beginCommand(command) else { return }
        defer { endCommand(command) }
        if let reason = absoluteMoveBlockedReason(x: x, y: y, z: z, feedrateMmMin: feedrateMmMin) {
            setError(command: command, message: reason, isRetryable: false)
            return
        }
        await perform(command) { [self] in
            if let reason = absoluteMoveBlockedReason(x: x, y: y, z: z, feedrateMmMin: feedrateMmMin) {
                throw PrinterControlError.invalidRequest(reason)
            }
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
        if previous.configurationRevision != updated.configurationRevision || previous.backend != updated.backend {
            lifecycleGeneration += 1
            invalidateSafety()
            interruptCalibration("Printer configuration changed. Cancel and refresh safety checks.")
        }
        if previous.isOnline && !updated.isOnline { invalidateSafety() }
        if let review = calibrationReview, updated.rowVersion != review.rowVersion {
            interruptCalibration("Printer revision changed after review. Cancel and refresh; no save was sent.")
        }
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
        case let .heaterTargets(hotend, bed):
            return (hotend != nil || bed != nil)
                && targetsSatisfied(hotendTarget: hotend, bedTarget: bed, in: updated)
        case let .moveTo(x, y, z, _):
            return (x != nil || y != nil || z != nil)
                && (x.map { updated.x == $0 } ?? true)
                && (y.map { updated.y == $0 } ?? true)
                && (z.map { updated.z == $0 } ?? true)
        case .disableMotors, .extrusion, .filament, .calibrationSave:
            return false
        case .calibrationHome, .calibrationPosition, .calibrationAdjust:
            // Legacy merged fields lack fact timestamps and frame provenance.
            // Calibration confirms only through the versioned status reader.
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

    private var commandIdentity: PrinterControlsIdentity? {
        registeredServerID.map { PrinterControlsIdentity(serverID: $0, printerID: printer.id) }
    }

    var isExecuting: Bool {
        pendingCommand != nil || commandIdentity.map {
            commandLeases.contains($0, excludingWorkflow: calibrationLease)
        } == true
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
    /// dispatch and retains it while this owner awaits matching telemetry.
    private func beginCommand(_ command: ControlCommand) -> Bool {
        guard !Task.isCancelled else { return false }
        guard pendingCommand == nil else { return false }
        if let step = calibrationStep, step != .introduction, step != .done {
            switch command.kind {
            case .calibrationHome, .calibrationPosition, .calibrationAdjust, .calibrationSave:
                break
            default:
                feedbackSection = command.section
                lastError = nil
                commandNotice = "Cancel calibration before another setup command. Emergency Stop remains independent."
                return false
            }
        }
        guard canControl else {
            feedbackSection = command.section
            commandNotice = nil
            lastError = ControlsError(
                command: command,
                message: blockedReason ?? "Controls are unavailable.",
                isRetryable: false
            )
            return false
        }
        guard let identity = commandIdentity,
              let lifetime = commandLeases.acquire(identity, token: command.id, workflow: calibrationLease) else { return false }
        commandLease = (command.id, lifetime)
        feedbackSection = command.section
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
    /// HTTP settlement alone cannot unlock another owner while this command
    /// still awaits telemetry. Pending-state cleanup releases the matching token.
    private func endCommand(_ command: ControlCommand) {
        defer {
            if pendingCommand != command { releaseLease(for: command) }
        }
        guard pendingCommand == command else { return }
        if lastError?.command == command {
            if calibrationCommandID == command.id {
                calibrationInterrupted = true
                calibrationReview = nil
                calibrationCommandID = nil
                calibrationMessage = "Calibration command failed or its outcome is uncertain. Check the printer, then cancel and start again. No automatic retry."
            }
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
        if commandLease?.id == command.id { commandLease = nil }
    }

    private static func confirmationNotice(for command: ControlCommand) -> String {
        switch command.kind {
        case .preheat, .heater, .heaterTargets:
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
