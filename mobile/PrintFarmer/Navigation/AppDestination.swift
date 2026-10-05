import Foundation

enum AppDestination: Hashable {
    case printerDetail(id: UUID)
    case jobDetail(id: UUID)
    case account
    case notifications
    case settings
    case jobQueue
    case offlineQueue
    case manageServers
    case advancedPrinterControls(printerId: UUID)
}

enum AppTab: String, Hashable, CaseIterable, Sendable {
    case farm
    case queue
    case filament

    var title: String {
        switch self {
        case .farm: "Farm"
        case .queue: "Queue"
        case .filament: "Filament"
        }
    }

    var systemImage: String {
        switch self {
        case .farm: "printer"
        case .queue: "list.bullet.rectangle"
        case .filament: "cylinder.fill"
        }
    }

    var tabAccessibilityIdentifier: String { "tab.\(rawValue)" }
    var sidebarAccessibilityIdentifier: String { "sidebar.\(rawValue)" }
}
