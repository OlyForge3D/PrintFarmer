import Foundation

enum DeepLinkDestination: Equatable {
    case printerDetail(id: UUID)
    case printerReady(id: UUID)
    case spoolDetail(id: Int)
}

struct DeepLinkHandler {
    /// Parses `printfarmer://` URLs into navigation destinations.
    ///
    /// Supported routes:
    /// - `printfarmer://printer/{UUID}` → printer detail
    /// - `printfarmer://printer/{UUID}/ready` → printer detail + mark ready
    /// - `printfarmer://spool/{id}` → spool detail (scroll-to in inventory)
    static func parse(url: URL) -> DeepLinkDestination? {
        guard url.scheme == "printfarmer" else { return nil }

        let pathComponents = url.pathComponents.filter { $0 != "/" }

        switch url.host {
        case "printer":
            guard let first = pathComponents.first,
                  let printerId = UUID(uuidString: first) else { return nil }

            if pathComponents.count > 1, pathComponents[1].lowercased() == "ready" {
                return .printerReady(id: printerId)
            }
            return .printerDetail(id: printerId)

        case "spool":
            guard let first = pathComponents.first,
                  let spoolId = Int(first) else { return nil }
            return .spoolDetail(id: spoolId)

        default:
            return nil
        }
    }
}
