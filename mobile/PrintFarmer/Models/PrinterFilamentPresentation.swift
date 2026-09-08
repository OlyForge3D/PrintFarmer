import Foundation

/// A value-only projection. The host supplies snapshots from one registered-server context.
struct PrinterFilamentPresentation: Equatable, Sendable {
    enum CoverageState: Equatable, Sendable {
        case loading
        case available
        case unavailable
        case disabled
        case failed(String)
    }

    struct Row: Identifiable, Equatable, Sendable {
        let id: String
        let toolheadID: UUID?
        let index: Int?
        let title: String
        let material: String?
        let spoolID: Int?
        let spoolName: String?
        let remainingGrams: Double?
        let coverage: ToolheadFilamentCoverage?
        let isLastConfirmed: Bool
        let isCoverageOnly: Bool
        /// Physical toolhead nozzle diameter in mm (issue #2522, Hicks
        /// review finding 21). Roster authority only — `nil` for
        /// coverage-only slots (no roster `Toolhead` match) and the
        /// printer-level spool row, exactly as the pre-#2522
        /// `toolheadSlotRow` never showed one for those either. Preserves
        /// the retired per-toolhead detail this section's rows replaced.
        let nozzleDiameter: Double?

        var notice: String? {
            guard let coverage else { return nil }
            let prefix = isLastConfirmed ? "Last confirmed: " : ""
            switch coverage.status {
            case .covers:
                return nil
            case .runout:
                return prefix + "Insufficient filament for active and assigned queued demand"
                    + (coverage.statusReason.map { ". \($0)" } ?? "")
            case .unknown:
                return prefix + "Coverage unknown"
                    + (coverage.statusReason.map { ": \($0)" } ?? "")
            }
        }
    }

    let printerID: UUID
    let rows: [Row]
    let coverageState: CoverageState
    let isStale: Bool
    let evaluatedAt: Date?
    let summary: String?
    let supportedActions: Set<PrinterFilamentAction.Kind>
    let integrityNotices: [String]

    var statusText: String? {
        switch coverageState {
        case .loading: return "Loading coverage"
        case .disabled: return "Coverage disabled"
        case .unavailable: return "Coverage unavailable"
        case .failed(let message): return "Coverage unavailable: \(message)"
        case .available: return evaluatedAt == nil ? "Coverage unavailable" : nil
        }
    }

    init(
        printer: Printer,
        toolheads: [Toolhead],
        spool: PrinterSpoolInfo?,
        coverage: PrinterFilamentCoverage?,
        coverageState: CoverageState,
        isStale: Bool,
        supportedActions: Set<PrinterFilamentAction.Kind>,
        reportIntegrityFailure: (String) -> Void = { assertionFailure($0) }
    ) {
        let ids = toolheads.map(\.id)
        let coverageIDs = (coverage?.toolheads ?? []).compactMap(\.toolheadId)
        let rosterIsValid = Set(ids).count == ids.count
        let coverageIdentityIsValid = Set(coverageIDs).count == coverageIDs.count
            && (coverage == nil || coverage?.printerId == printer.id)
        var notices: [String] = []
        if !rosterIsValid {
            notices.append("Toolhead roster unavailable: duplicate toolhead identities. Refresh printer data.")
        }
        if !coverageIdentityIsValid {
            notices.append("Coverage rejected: printer or toolhead identities do not match. Refresh printer data.")
        }
        integrityNotices = notices
        for notice in notices { reportIntegrityFailure(notice) }
        let roster = rosterIsValid ? toolheads : []
        printerID = printer.id
        self.coverageState = coverageIdentityIsValid ? coverageState : .unavailable
        // A retained snapshot during refresh/failure is historical even if the caller
        // has not yet updated its explicit stale flag.
        self.isStale = isStale || (coverage != nil && coverageState != .available && coverageState != .disabled)
        self.supportedActions = notices.isEmpty && !self.isStale ? supportedActions : []
        let snapshot = coverageState == .disabled || !coverageIdentityIsValid ? nil : coverage
        evaluatedAt = snapshot?.evaluatedAtUtc
        if let snapshot {
            let prefix = self.isStale ? "Last confirmed: " : ""
            switch snapshot.status {
            case .covers: summary = prefix + "Covers active and assigned queued demand"
            case .runout: summary = prefix + "Insufficient filament for active and assigned queued demand"
            case .unknown: summary = prefix + "Coverage unknown"
            }
        } else {
            summary = nil
        }

        let slots = snapshot?.toolheads ?? []
        var used = Set<Int>()
        var result: [Row] = []
        for toolhead in roster {
            let uuidMatch = slots.indices.first { slots[$0].toolheadId == toolhead.id }
            let sameIndex = slots.indices.filter { slots[$0].toolheadIndex == toolhead.index }
            let rosterIndexIsUnique = roster.filter { $0.index == toolhead.index }.count == 1
            let fallback = rosterIndexIsUnique && sameIndex.count == 1
                && slots[sameIndex[0]].toolheadId == nil ? sameIndex.first : nil
            let match = uuidMatch ?? fallback
            if let match { used.insert(match) }
            let slot = match.map { slots[$0] }
            result.append(Row(
                id: "\(printer.id)/id:\(toolhead.id)",
                toolheadID: toolhead.id, index: toolhead.index,
                title: toolhead.name ?? "Tool \(toolhead.index)",
                material: toolhead.currentMaterial,
                spoolID: toolhead.currentSpoolId,
                spoolName: nil,
                remainingGrams: nil,
                coverage: slot, isLastConfirmed: self.isStale, isCoverageOnly: false,
                nozzleDiameter: toolhead.nozzleDiameter
            ))
        }
        for offset in slots.indices where !used.contains(offset) {
            let slot = slots[offset]
            let ambiguous = slot.toolheadId == nil
                && slots.filter { $0.toolheadIndex == slot.toolheadIndex }.count > 1
            let identity = slot.toolheadId.map { "id:\($0)" }
                ?? "index:\(slot.toolheadIndex)" + (ambiguous ? "/unresolved:\(offset)" : "")
            result.append(Row(
                id: "\(printer.id)/coverage/\(identity)",
                toolheadID: slot.toolheadId, index: slot.toolheadIndex,
                title: slot.toolheadName,
                material: nil, spoolID: nil, spoolName: nil,
                remainingGrams: nil, coverage: slot, isLastConfirmed: self.isStale, isCoverageOnly: true,
                nozzleDiameter: nil
            ))
        }
        // Printer-level spool data has no slot authority. Keep it once, explicitly
        // unattributed, rather than copying its quantity into each physical slot.
        if let spool {
            result.append(Row(
                id: "\(printer.id)/printer-spool",
                toolheadID: nil, index: nil,
                title: "Printer-level spool (slot not specified)",
                material: spool.hasActiveSpool ? spool.material : nil,
                spoolID: spool.hasActiveSpool ? spool.activeSpoolId : nil,
                spoolName: spool.hasActiveSpool
                    ? (spool.filamentName ?? spool.spoolName ?? "Assigned spool")
                    : "No printer-level spool assigned",
                remainingGrams: spool.hasActiveSpool ? spool.remainingWeightG : nil,
                coverage: nil, isLastConfirmed: false, isCoverageOnly: false,
                nozzleDiameter: nil
            ))
        }
        rows = result
    }

    func disabledReason(for action: PrinterFilamentAction) -> String? {
        guard action.target.printerID == printerID else { return "Printer target changed" }
        if case .slot = action.target {
            // Current native Set/Change/Eject/NFC/swap flows are printer-level only.
            return "Slot-specific changes are not supported"
        }
        if let reason = action.disabledReason { return reason }
        if isStale { return "Refresh printer data before changing filament" }
        guard supportedActions.contains(action.kind) else { return "Action unavailable" }
        return nil
    }
}

struct PrinterFilamentAction: Identifiable, Equatable, Sendable {
    enum Kind: String, CaseIterable, Sendable {
        case set, change, clearAssignment, scanNFC, guidedSwap

        var title: String {
            switch self {
            case .set: return "Set spool"
            case .change: return "Change spool"
            case .clearAssignment: return "Clear spool assignment"
            case .scanNFC: return "Scan NFC"
            case .guidedSwap: return "Guided swap"
            }
        }
    }

    enum Target: Equatable, Sendable {
        case printer(UUID)
        case slot(printerID: UUID, toolheadID: UUID)

        var printerID: UUID {
            switch self {
            case .printer(let id): return id
            case .slot(let id, _): return id
            }
        }

        var identifier: String {
            switch self {
            case .printer(let id): return "\(id)/printer"
            case .slot(let id, let toolhead): return "\(id)/slot/\(toolhead)"
            }

        }

        var label: String {
            switch self {
            case .printer: return "printer-level"
            case .slot(_, let toolhead): return "slot \(toolhead)"
            }
        }
    }

    let kind: Kind
    let target: Target
    /// Nil means enabled by the host's permission/connection/pending gates.
    let disabledReason: String?

    var id: String { "\(target.identifier)/\(kind.rawValue)" }
}
