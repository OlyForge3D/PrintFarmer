import Foundation
@testable import PrintFarmer

// MARK: - Controlled Filament Coverage Service (test helper, #778)
//
// A deterministic in-memory stand-in for `FilamentCoverageService`.
// Every request `await`s on a `CheckedContinuation` the test releases
// explicitly via `completeSuccess`, `completeError`, or
// `completeFeatureDisabled`. There is no fixed timeout, `Task.yield`,
// or elapsed-time observation anywhere in the flow — request
// interleavings are proven by barrier synchronization, not by sleeps.

actor ControlledFilamentCoverageService: FilamentCoverageServiceProtocol {

    enum PendingRequest {
        case forFleet(CheckedContinuation<FleetFilamentCoverage, Error>)
        case forPrinter(UUID, CheckedContinuation<PrinterFilamentCoverage, Error>)
    }

    private var pending: [PendingRequest] = []
    private var pendingWaiters: [(target: Int, cont: CheckedContinuation<Void, Never>)] = []

    // MARK: - Protocol

    func getForFleet() async throws -> FleetFilamentCoverage {
        try await withCheckedThrowingContinuation { cont in
            pending.append(.forFleet(cont))
            resumeMatchingWaiters()
        }
    }

    func getForPrinter(id: UUID) async throws -> PrinterFilamentCoverage {
        try await withCheckedThrowingContinuation { cont in
            pending.append(.forPrinter(id, cont))
            resumeMatchingWaiters()
        }
    }

    // MARK: - Test-side release API

    /// Suspends until at least `count` pending requests are captured.
    /// Deterministic: resumes on the next `getForFleet` / `getForPrinter`
    /// arrival that pushes the count to the target.
    func awaitPending(count: Int) async {
        if pending.count >= count { return }
        await withCheckedContinuation { (cont: CheckedContinuation<Void, Never>) in
            pendingWaiters.append((target: count, cont: cont))
        }
    }

    func completeSuccess(index: Int, fleet: FleetFilamentCoverage) {
        guard index < pending.count else { return }
        let req = pending.remove(at: index)
        if case .forFleet(let cont) = req {
            cont.resume(returning: fleet)
        } else {
            // The test used the wrong helper — fail loudly.
            fatalError("completeSuccess(fleet:) applied to a per-printer request at index \(index)")
        }
    }

    func completeSuccess(index: Int, printer: PrinterFilamentCoverage) {
        guard index < pending.count else { return }
        let req = pending.remove(at: index)
        if case .forPrinter(_, let cont) = req {
            cont.resume(returning: printer)
        } else {
            fatalError("completeSuccess(printer:) applied to a fleet request at index \(index)")
        }
    }

    func completeError(index: Int, error: Error) {
        guard index < pending.count else { return }
        let req = pending.remove(at: index)
        switch req {
        case .forFleet(let cont):
            cont.resume(throwing: error)
        case .forPrinter(_, let cont):
            cont.resume(throwing: error)
        }
    }

    func completeFeatureDisabled(index: Int) {
        let apiError = APIError(
            title: "Feature Disabled",
            status: 404,
            detail: "test",
            errors: nil,
            message: nil,
            code: "featureDisabled"
        )
        completeError(index: index, error: NetworkError.featureDisabled(apiError))
    }

    var pendingCount: Int { pending.count }

    // MARK: - Private

    private func resumeMatchingWaiters() {
        let currentCount = pending.count
        var remaining: [(target: Int, cont: CheckedContinuation<Void, Never>)] = []
        for waiter in pendingWaiters {
            if currentCount >= waiter.target {
                waiter.cont.resume()
            } else {
                remaining.append(waiter)
            }
        }
        pendingWaiters = remaining
    }
}
