import Foundation

// MARK: - Stub Filament Coverage Service (F4-M / #778 UI tests)
//
// A pre-canned `FilamentCoverageServiceProtocol` implementation used by
// the UI-test bootstrap to seed deterministic coverage snapshots without
// hitting a backend or exercising real generation ordering.
//
// This lives inside the app target (rather than a test bundle) because
// `UITestBootstrap.makeBundle` needs to inject it into
// `ServiceContainer.demo()` before the SwiftUI hierarchy is built. Its
// only construction site is inside the bootstrap; production code never
// references it.

final class StubFilamentCoverageService: FilamentCoverageServiceProtocol, @unchecked Sendable {

    /// Immutable fleet snapshot returned by both endpoints. `printers`
    /// is keyed by printer id for `getForPrinter(id:)`.
    private let fleet: FleetFilamentCoverage
    private let byPrinter: [UUID: PrinterFilamentCoverage]

    init(fleet: FleetFilamentCoverage) {
        self.fleet = fleet
        self.byPrinter = Dictionary(
            uniqueKeysWithValues: fleet.printers.map { ($0.printerId, $0) }
        )
    }

    func getForPrinter(id: UUID) async throws -> PrinterFilamentCoverage {
        if let snapshot = byPrinter[id] {
            return snapshot
        }
        // A printer without a stubbed snapshot behaves as "genuinely
        // not found" (distinct from feature-disabled). UI tests target
        // printers that ARE in the fleet, so this branch is a safety
        // net rather than a normal path.
        throw NetworkError.notFound
    }

    func getForFleet() async throws -> FleetFilamentCoverage {
        fleet
    }
}
