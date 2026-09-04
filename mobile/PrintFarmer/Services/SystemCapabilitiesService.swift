import Foundation
import Observation
import OSLog

/// Service protocol for `/api/system/capabilities` used by the operator
/// feature gate (issue #725).
///
/// The protocol is `@MainActor`-isolated to match every concrete
/// conformance (`SystemCapabilitiesService` and
/// `StubSystemCapabilitiesService`) and every existing call site
/// (`AttentionView`, `ServiceContainer`). Under Swift 6 strict
/// concurrency, an unannotated protocol requirement forces conformances
/// to be nonisolated, which conflicts with both `@MainActor`
/// conformances and produces the compile break Bishop reproduced on
/// Swift 6.1.3 / 6.2.4. Marking the protocol requirements MainActor
/// keeps the caller and callee actor contexts aligned.
@MainActor
protocol SystemCapabilitiesServiceProtocol: AnyObject, Sendable {
    /// Latest resolved snapshot. Defaults to
    /// `ResolvedSystemCapabilities.defaults` before the first successful
    /// fetch or when the endpoint is unavailable.
    var resolved: ResolvedSystemCapabilities { get }

    /// Fetches `/api/system/capabilities` and updates ``resolved``.
    ///
    /// This method never throws. On failure (404, network error, decode
    /// error) the resolved snapshot is left at its default so callers can
    /// keep operating with the documented per-feature defaults used for
    /// older servers and missing flags.
    @discardableResult
    func refresh() async -> SystemCapabilitiesRefreshOutcome

    /// Performs the authenticated-startup refresh and retains its outcome for
    /// the immediately-following readiness gate.
    @discardableResult
    func prepareForReadiness() async -> SystemCapabilitiesRefreshOutcome

    /// Consumes a prepared startup outcome when available; otherwise performs
    /// the normal readiness refresh.
    @discardableResult
    func refreshForReadiness() async -> SystemCapabilitiesRefreshOutcome

    /// Drops a prepared result when readiness exits before reaching the
    /// capabilities probe so a later retry performs a fresh request.
    func discardPreparedReadiness()
}

extension SystemCapabilitiesServiceProtocol {
    func prepareForReadiness() async -> SystemCapabilitiesRefreshOutcome {
        await refresh()
    }

    func refreshForReadiness() async -> SystemCapabilitiesRefreshOutcome {
        await refresh()
    }

    func discardPreparedReadiness() {}
}

enum SystemCapabilitiesRefreshOutcome: Equatable, Sendable {
    case loaded
    case legacyDefaults
    case failed
    case failedWithDiagnostics(BackendReadinessFailureClassification)
}

/// Live implementation backed by the shared `APIClient`.
///
/// Views and navigation consult this shared gate rather than inventing
/// parallel booleans. Capability-disabled features are removed from the
/// operator shell; endpoint failures for enabled features remain visible to
/// their owning views.
@MainActor
@Observable
final class SystemCapabilitiesService: SystemCapabilitiesServiceProtocol, @unchecked Sendable {
    private static let logger = Logger(subsystem: "com.printfarmer.ios", category: "SystemCapabilities")

    @ObservationIgnored private let apiClient: APIClient
    @ObservationIgnored private var issuedRefreshGeneration: UInt64 = 0
    @ObservationIgnored private var appliedRefreshGeneration: UInt64 = 0
    @ObservationIgnored private var preparedReadinessOutcome: SystemCapabilitiesRefreshOutcome?
    @ObservationIgnored private var readinessPreparationGeneration: UInt64 = 0
    @ObservationIgnored private var readinessPreparation:
        (generation: UInt64, task: Task<SystemCapabilitiesRefreshOutcome, Never>)?
    private(set) var resolved: ResolvedSystemCapabilities = .defaults

    init(apiClient: APIClient) {
        self.apiClient = apiClient
    }

    @discardableResult
    func refresh() async -> SystemCapabilitiesRefreshOutcome {
        await performRefresh()
    }

    @discardableResult
    func prepareForReadiness() async -> SystemCapabilitiesRefreshOutcome {
        if let preparedReadinessOutcome {
            return preparedReadinessOutcome
        }
        if let readinessPreparation {
            let outcome = await readinessPreparation.task.value
            if self.readinessPreparation?.generation == readinessPreparation.generation {
                self.readinessPreparation = nil
                preparedReadinessOutcome = outcome
            }
            return outcome
        }

        readinessPreparationGeneration &+= 1
        let generation = readinessPreparationGeneration
        let task = Task { @MainActor [weak self] in
            guard let self else { return SystemCapabilitiesRefreshOutcome.failed }
            return await self.performRefresh()
        }
        readinessPreparation = (generation, task)
        let outcome = await task.value
        if readinessPreparation?.generation == generation {
            readinessPreparation = nil
            preparedReadinessOutcome = outcome
        }
        return outcome
    }

    @discardableResult
    func refreshForReadiness() async -> SystemCapabilitiesRefreshOutcome {
        if let preparedReadinessOutcome {
            self.preparedReadinessOutcome = nil
            return preparedReadinessOutcome
        }
        if let readinessPreparation {
            let outcome = await readinessPreparation.task.value
            if let preparedReadinessOutcome {
                self.preparedReadinessOutcome = nil
                return preparedReadinessOutcome
            }
            if self.readinessPreparation?.generation == readinessPreparation.generation {
                self.readinessPreparation = nil
                readinessPreparationGeneration &+= 1
            }
            return outcome
        }
        return await performRefresh()
    }

    func discardPreparedReadiness() {
        preparedReadinessOutcome = nil
        readinessPreparation?.task.cancel()
        readinessPreparation = nil
        readinessPreparationGeneration &+= 1
    }

    private func performRefresh() async -> SystemCapabilitiesRefreshOutcome {
        issuedRefreshGeneration &+= 1
        let generation = issuedRefreshGeneration

        do {
            let response: SystemCapabilities = try await apiClient.get("/api/system/capabilities")
            guard !Task.isCancelled, generation > appliedRefreshGeneration else { return .loaded }
            appliedRefreshGeneration = generation
            resolved = response.resolved
            return .loaded
        } catch NetworkError.notFound {
            // Server predates #725. Documented behavior: keep defaults.
            Self.logger.info("system/capabilities endpoint not present; using defaults")
            guard !Task.isCancelled, generation > appliedRefreshGeneration else { return .legacyDefaults }
            appliedRefreshGeneration = generation
            resolved = .defaults
            return .legacyDefaults
        } catch {
            let classification = BackendReadinessDiagnostics.classify(error)
            Self.logger.warning(
                """
                Failed to fetch capabilities \
                kind=\(classification.kind.rawValue, privacy: .public) \
                detail=\(classification.diagnosticDetail, privacy: .public)
                """
            )
            // Fail open — do not disable features because of a transient error.
            return .failedWithDiagnostics(classification)
        }
    }
}

/// Test/demo double that returns a caller-supplied snapshot.
@MainActor
final class StubSystemCapabilitiesService: SystemCapabilitiesServiceProtocol, @unchecked Sendable {
    private(set) var resolved: ResolvedSystemCapabilities

    init(resolved: ResolvedSystemCapabilities = .defaults) {
        self.resolved = resolved
    }

    func setResolved(_ new: ResolvedSystemCapabilities) {
        resolved = new
    }

    @discardableResult
    func refresh() async -> SystemCapabilitiesRefreshOutcome {
        .loaded
    }
}
