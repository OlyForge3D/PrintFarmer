import Foundation

enum DeepLinkDestination: Equatable {
    case printerDetail(id: UUID)
    case printerReady(id: UUID)
    case spoolDetail(id: Int)
    case attentionItem(id: String)
    case filamentSwap(printerId: UUID, toolheadIndex: Int, jobId: UUID?)
}

struct DeepLinkHandler {
    /// Parses `printfarmer://` URLs into navigation destinations.
    ///
    /// Supported routes:
    /// - `printfarmer://printer/{UUID}` → printer detail
    /// - `printfarmer://printer/{UUID}/ready` → printer detail + mark ready
    /// - `printfarmer://printer/{UUID}/swap/{index}?jobId={UUID}` → guided swap
    /// - `printfarmer://spool/{id}` → spool detail (scroll-to in inventory);
    ///   `{id}` must be a positive integer — zero/negative values are
    ///   rejected (`nil`) rather than treated as a valid spool ID (#714
    ///   Item C).
    /// - `printfarmer://attention/{itemId}` → exact attention item
    static func parse(url: URL) -> DeepLinkDestination? {
        guard url.scheme == "printfarmer" else { return nil }

        let pathComponents = url.pathComponents.filter { $0 != "/" }

        switch url.host {
        case "printer":
            guard let first = pathComponents.first,
                  let printerId = UUID(uuidString: first) else { return nil }

            if pathComponents.count == 1 {
                return .printerDetail(id: printerId)
            }
            if pathComponents.count > 1, pathComponents[1].lowercased() == "ready" {
                return .printerReady(id: printerId)
            }
            if pathComponents.count == 3,
               pathComponents[1].lowercased() == "swap",
               let toolheadIndex = Int(pathComponents[2]),
               toolheadIndex >= 0 {
                let jobIdValue = URLComponents(url: url, resolvingAgainstBaseURL: false)?
                    .queryItems?
                    .first(where: { $0.name == "jobId" })?
                    .value
                guard let jobIdValue, !jobIdValue.isEmpty else {
                    return .filamentSwap(
                        printerId: printerId,
                        toolheadIndex: toolheadIndex,
                        jobId: nil
                    )
                }
                guard let jobId = UUID(uuidString: jobIdValue) else { return nil }
                return .filamentSwap(
                    printerId: printerId,
                    toolheadIndex: toolheadIndex,
                    jobId: jobId
                )
            }
            return nil

        case "spool":
            guard pathComponents.count == 1,
                  let first = pathComponents.first,
                  let spoolId = Int(first),
                  spoolId > 0 else { return nil }
            return .spoolDetail(id: spoolId)

        case "attention":
            guard pathComponents.count == 1,
                  let itemId = pathComponents.first,
                  !itemId.isEmpty else { return nil }
            return .attentionItem(id: itemId)

        default:
            return nil
        }
    }
}

enum NotificationDeepLinkRouting {
    enum Failure: Error, Equatable {
        case missingLink
        case invalidLink
        case unsupportedDestination

        var message: String {
            switch self {
            case .missingLink:
                return "This notification does not contain a destination for the selected server."
            case .invalidLink, .unsupportedDestination:
                return "This notification's destination is invalid for the selected server."
            }
        }
    }

    static func destination(
        from userInfo: [AnyHashable: Any]
    ) -> Result<DeepLinkDestination, Failure> {
        let urlString = (userInfo["deepLink"] as? String) ?? (userInfo["link"] as? String)
        guard let urlString, !urlString.isEmpty else {
            return .failure(.missingLink)
        }
        guard let url = URL(string: urlString) else {
            return .failure(.invalidLink)
        }
        guard let destination = DeepLinkHandler.parse(url: url) else {
            return .failure(.unsupportedDestination)
        }
        return .success(destination)
    }
}
