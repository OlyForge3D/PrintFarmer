import Foundation

// MARK: - Demo Filament Coverage Service (F4-M / issue #778)
//
// The demo/preview app does not run a real coverage backend. Reporting
// the feature as disabled lets the coverage UI layer render its safe
// fallback (no covers / runout affordances) without inventing synthetic
// coverage numbers that could mislead an operator.

final class DemoFilamentCoverageService: FilamentCoverageServiceProtocol, @unchecked Sendable {
    func getForPrinter(id: UUID) async throws -> PrinterFilamentCoverage {
        throw NetworkError.featureDisabled(
            APIError(
                title: "Feature Disabled",
                status: 404,
                detail: "Filament coverage is not available in demo mode.",
                errors: nil,
                message: nil,
                code: "featureDisabled"
            )
        )
    }

    func getForFleet() async throws -> FleetFilamentCoverage {
        throw NetworkError.featureDisabled(
            APIError(
                title: "Feature Disabled",
                status: 404,
                detail: "Filament coverage is not available in demo mode.",
                errors: nil,
                message: nil,
                code: "featureDisabled"
            )
        )
    }
}
