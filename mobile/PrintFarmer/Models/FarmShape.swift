import Foundation

/// Server-observed farm counts returned by `GET /api/system/farm-shape`.
struct FarmShape: Codable, Sendable, Equatable {
    let accountCount: Int
    let locationCount: Int
    let printerCount: Int
}
