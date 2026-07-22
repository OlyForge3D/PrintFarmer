import Foundation

// MARK: - Filament Coverage Service Protocol (F4-M / issue #778)
//
// Typed repository over the coverage endpoints shipped by PR #732.
// Feature-gating is enforced server-side via a structured 404 with
// `code: "featureDisabled"` (mapped to `NetworkError.featureDisabled` by
// `APIClient`); callers use that case to render a coverage-hidden
// fallback without treating it as a hard error.

protocol FilamentCoverageServiceProtocol: AnyObject, Sendable {
    /// Coverage snapshot for a single printer.
    ///
    /// Throws:
    ///   - `NetworkError.featureDisabled(_:)` when the operator feature
    ///     gate for filament coverage is disabled server-side.
    ///   - `NetworkError.notFound` when the printer does not exist.
    ///   - Any other `NetworkError` for transport / server failures.
    func getForPrinter(id: UUID) async throws -> PrinterFilamentCoverage

    /// Fleet-wide coverage snapshot. Same feature-gate contract as
    /// `getForPrinter(id:)`.
    func getForFleet() async throws -> FleetFilamentCoverage
}
