import Foundation

// MARK: - Attention Service
//
// Thin repository over the unified `/api/attention` feed merged in PR #731
// (issue #707). Composition, ordering, snooze application, and typed
// action dispatch all live server-side; this actor just shapes the paged
// request/response and forwards typed actions.
//
// The `attentionchanged` SignalR event is deliberately handled elsewhere
// (`SignalRService.onAttentionChanged`) so realtime updates are treated as
// pure invalidation hints — a refetch through this service is always the
// authoritative source of item truth.

actor AttentionService: AttentionServiceProtocol {
    private let apiClient: APIClient

    /// Server-side upper bound (see `AttentionService.MaxLimit` in the
    /// backend). Kept in sync with the merged contract.
    static let maxLimit: Int = 250

    init(apiClient: APIClient) {
        self.apiClient = apiClient
    }

    // MARK: - Feed

    func getFeed(cursor: String? = nil, limit: Int? = nil) async throws -> AttentionFeed {
        var items: [URLQueryItem] = []
        if let cursor, !cursor.isEmpty {
            items.append(URLQueryItem(name: "cursor", value: cursor))
        }
        if let limit {
            items.append(URLQueryItem(name: "limit", value: String(limit)))
        }
        let query = Self.encodeQuery(items)
        return try await apiClient.get("/api/attention\(query)")
    }

    // MARK: - Snooze

    func snooze(
        itemId: String,
        snoozedUntilUtc: Date,
        attentionItemAnchorAtUtc: Date? = nil
    ) async throws -> SnoozeAttentionResponse {
        let request = SnoozeAttentionRequest(
            snoozedUntilUtc: snoozedUntilUtc,
            attentionItemAnchorAtUtc: attentionItemAnchorAtUtc
        )
        let path = "/api/attention/\(Self.encodePathSegment(itemId))/snooze"
        return try await apiClient.post(path, body: request)
    }

    func clearSnooze(itemId: String) async throws {
        let path = "/api/attention/\(Self.encodePathSegment(itemId))/snooze"
        try await apiClient.delete(path)
    }

    // MARK: - Typed actions

    func executeAction(
        itemId: String,
        actionKind: AttentionActionKind
    ) async throws -> AttentionActionResult {
        // Refuse to dispatch action kinds this client version does not
        // recognise — the server would accept them, but we cannot describe
        // the outcome to the user meaningfully.
        guard actionKind != .unknown else {
            throw NetworkError.clientError(
                400,
                APIError(
                    title: "Unknown attention action",
                    status: 400,
                    detail: "The client received an attention action kind it does not recognise.",
                    errors: nil,
                    message: nil,
                    code: "clientUnknownAction"
                )
            )
        }
        let path = "/api/attention/\(Self.encodePathSegment(itemId))/actions/\(actionKind.rawValue)"
        return try await apiClient.post(path)
    }

    // MARK: - Path & query encoding

    /// Percent-encodes an attention item id for use as a single path
    /// segment. Attention ids are of the form `"{kind}:{sourceId}"`; the
    /// `:` is a reserved subdelim in RFC 3986 path segments and MUST be
    /// escaped so the router matches the whole id, not just the prefix.
    private static func encodePathSegment(_ segment: String) -> String {
        segment.addingPercentEncoding(withAllowedCharacters: .urlPathSegmentAllowed)
            ?? segment
    }

    private static func encodeQuery(_ items: [URLQueryItem]) -> String {
        guard !items.isEmpty else { return "" }
        var components = URLComponents()
        components.queryItems = items
        // `URLComponents` returns `nil` percentEncodedQuery for an empty
        // query array; we've already guarded against that.
        return "?\(components.percentEncodedQuery ?? "")"
    }
}

// MARK: - Path-segment character set

private extension CharacterSet {
    /// RFC 3986 `pchar` minus the sub-delims that would confuse routing
    /// (`:`, `/`, `?`, `#`, `[`, `]`, `@`). Kept private to this file so
    /// other services keep using their existing encoders unchanged.
    static let urlPathSegmentAllowed: CharacterSet = {
        var set = CharacterSet.urlPathAllowed
        set.remove(charactersIn: ":/?#[]@")
        return set
    }()
}
