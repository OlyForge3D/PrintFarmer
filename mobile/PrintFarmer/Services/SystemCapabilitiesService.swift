import Foundation
import Observation
import OSLog

/// Service protocol for `/api/system/capabilities` used by the operator
/// feature gate (issue #725).
protocol SystemCapabilitiesServiceProtocol: AnyObject, Sendable {
    /// Latest resolved snapshot. Defaults to
    /// `ResolvedSystemCapabilities.defaults` before the first successful
    /// fetch or when the endpoint is unavailable.
    var resolved: ResolvedSystemCapabilities { get }

    /// Fetches `/api/system/capabilities` and updates ``resolved``.
    ///
    /// This method never throws. On failure (404, network error, decode
    /// error) the resolved snapshot is left at its default so callers can
    /// keep operating as if all features are enabled — matching the
    /// documented contract in #725 that older servers with missing flags
    /// fall back to defaults.
    func refresh() async
}

/// Live implementation backed by the shared `APIClient`.
///
/// F1 (#706): This lets `AttentionView` (and, later, F2's attention feed)
/// consult the shared gate contract from #725 without inventing a
/// parallel boolean. When `attentionEnabled` resolves to `false`, or
/// when `/api/attention` returns ProblemDetails with `code:
/// "featureDisabled"`, clients render the safe fallback that still
/// exposes the retained `DashboardView` / `NotificationsView` /
/// `MaintenanceView` source screens.
@MainActor
@Observable
final class SystemCapabilitiesService: SystemCapabilitiesServiceProtocol, @unchecked Sendable {
    private static let logger = Logger(subsystem: "com.printfarmer.ios", category: "SystemCapabilities")

    @ObservationIgnored private let apiClient: APIClient
    private(set) var resolved: ResolvedSystemCapabilities = .defaults

    init(apiClient: APIClient) {
        self.apiClient = apiClient
    }

    func refresh() async {
        do {
            let response: SystemCapabilities = try await apiClient.get("/api/system/capabilities")
            resolved = response.resolved
        } catch NetworkError.notFound {
            // Server predates #725. Documented behavior: keep defaults.
            Self.logger.info("system/capabilities endpoint not present; using defaults")
            resolved = .defaults
        } catch {
            Self.logger.warning("Failed to fetch capabilities: \(error.localizedDescription, privacy: .public)")
            // Fail open — do not disable features because of a transient error.
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

    func refresh() async {
        // no-op
    }
}
