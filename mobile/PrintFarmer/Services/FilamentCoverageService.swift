import Foundation

// MARK: - Filament Coverage Service (F4-M / issue #778)
//
// Thin actor over the two canonical coverage endpoints. The service does
// NOT observe SignalR itself — invalidations (`filamentcoveragechanged`)
// and reconnect recovery are wired at the view-model layer so each screen
// can decide which endpoint to refetch. Payloads from the SignalR event
// are refetch *hints*, never a source of coverage truth.
//
// URL encoding: printer ids are UUIDs. UUIDs are pure hex + hyphens, so
// direct interpolation is byte-safe today; we still percent-encode the
// segment because the frozen contract requires it explicitly and to
// remain robust if the id shape ever widens (e.g. a future migration to
// slug-style ids). The encoder set removes RFC 3986 sub-delims that
// would confuse routing (`/`, `:`, `?`, `#`, `[`, `]`, `@`).

actor FilamentCoverageService: FilamentCoverageServiceProtocol {
    private let apiClient: APIClient

    init(apiClient: APIClient) {
        self.apiClient = apiClient
    }

    func getForPrinter(id: UUID) async throws -> PrinterFilamentCoverage {
        let segment = Self.encodePathSegment(id.uuidString)
        return try await apiClient.get("/api/printers/\(segment)/filament-coverage")
    }

    func getForFleet() async throws -> FleetFilamentCoverage {
        try await apiClient.get("/api/printers/filament-coverage")
    }

    /// Percent-encodes a printer id for use as a single path segment.
    /// Matches the same allowed-character set used by
    /// `AttentionService.encodePathSegment`.
    private static func encodePathSegment(_ segment: String) -> String {
        segment.addingPercentEncoding(withAllowedCharacters: .urlFilamentCoveragePathSegmentAllowed)
            ?? segment
    }
}

// MARK: - Path-segment character set

private extension CharacterSet {
    /// RFC 3986 `pchar` minus the sub-delims that would confuse routing.
    /// Private to this file so no other service picks it up by accident.
    static let urlFilamentCoveragePathSegmentAllowed: CharacterSet = {
        var set = CharacterSet.urlPathAllowed
        set.remove(charactersIn: ":/?#[]@")
        return set
    }()
}
