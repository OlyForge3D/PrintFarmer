import Foundation
import Observation

struct PrinterDetailSpoolLookupAuthority: Equatable, Sendable {
    let serverID: UUID?
    let userID: UUID?
    let generation: Int
    let printerID: UUID
    let spoolIDs: [Int]
}

struct SpoolWeightMeter: Equatable, Sendable {
    let remainingGrams: Double?
    let initialGrams: Double?

    var fraction: Double? {
        guard let remainingGrams, remainingGrams.isFinite, remainingGrams >= 0,
              let initialGrams, initialGrams.isFinite, initialGrams > 0,
              remainingGrams <= initialGrams else {
            return nil
        }
        return remainingGrams / initialGrams
    }

    var label: String {
        guard let remainingGrams, remainingGrams.isFinite, remainingGrams >= 0 else {
            return "Remaining weight unavailable"
        }
        guard let initialGrams, initialGrams.isFinite, initialGrams > 0 else {
            return "\(quantity(remainingGrams)) g remaining · starting weight unavailable"
        }
        guard remainingGrams <= initialGrams else {
            return "Spool weight data inconsistent: \(quantity(remainingGrams)) g remaining exceeds \(quantity(initialGrams)) g initial"
        }
        return "\(quantity(remainingGrams)) g of \(quantity(initialGrams)) g remaining"
    }

    private func quantity(_ value: Double) -> String {
        value.formatted(.number.precision(.fractionLength(0...1)))
    }
}

@MainActor
@Observable
final class PrinterDetailSpoolLookup {
    enum State {
        case idle
        case loading
        case loaded([Int: SpoolmanSpool], missingIDs: Set<Int>)
        case failed(String)
    }

    private(set) var state: State = .idle
    @ObservationIgnored private var requestGeneration: UInt64 = 0

    var spoolsByID: [Int: SpoolmanSpool] {
        guard case .loaded(let spools, _) = state else { return [:] }
        return spools
    }

    var statusMessage: String? {
        switch state {
        case .idle:
            return nil
        case .loading:
            return "Loading spool capacity…"
        case .loaded(_, let missingIDs):
            return missingIDs.isEmpty ? nil : "Some assigned spools were not found in inventory."
        case .failed(let message):
            return "Spool capacity unavailable: \(message)"
        }
    }

    func load(
        service: any SpoolServiceProtocol,
        authority: PrinterDetailSpoolLookupAuthority,
        isCurrent: @escaping @MainActor () -> Bool
    ) async {
        let spoolIDs = Set(authority.spoolIDs)
        guard !spoolIDs.isEmpty else {
            invalidate()
            return
        }
        requestGeneration &+= 1
        let generation = requestGeneration
        state = .loading
        do {
            let results = try await service.spools(ids: spoolIDs)
            try Task.checkCancellation()
            guard generation == requestGeneration else { return }
            guard isCurrent() else {
                state = .idle
                return
            }
            state = .loaded(results, missingIDs: spoolIDs.subtracting(Set(results.keys)))
        } catch is CancellationError {
            guard generation == requestGeneration else { return }
            state = .idle
        } catch {
            guard generation == requestGeneration else { return }
            state = isCurrent() ? .failed(error.localizedDescription) : .idle
        }
    }

    func invalidate() {
        requestGeneration &+= 1
        state = .idle
    }
}
