import Foundation

/// Server-observed farm counts returned by `GET /api/system/farm-shape`.
///
/// This is an **authenticated** endpoint, deliberately separate from
/// `GET /api/system/capabilities` (which stays `[AllowAnonymous]`).
/// Response payload uses camelCase and `Cache-Control: no-store`. An absent
/// response (401 / 404 / timeout / older server) is treated as shape unknown;
/// an absent field on a present response is a decode failure, not "unknown".
///
/// See ``FarmShapeService`` for fetch/persistence and #2410 / #2411 for
/// the original endpoint design rationale. Counts no longer choose mobile navigation.
struct FarmShape: Codable, Sendable, Equatable {
    let accountCount: Int
    let locationCount: Int
    let printerCount: Int
}
