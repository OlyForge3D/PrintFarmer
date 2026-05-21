import Foundation

/// Errors for service-layer stubs that haven't been implemented yet.
enum ServiceError: LocalizedError {
    case notImplemented(String)

    var errorDescription: String? {
        switch self {
        case .notImplemented(let method):
            "'\(method)' is not yet implemented."
        }
    }
}
