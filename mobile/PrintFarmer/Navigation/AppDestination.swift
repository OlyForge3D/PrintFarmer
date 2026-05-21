import Foundation

enum AppDestination: Hashable {
    case printerDetail(id: UUID)
    case jobDetail(id: UUID)
    case locationDetail(id: UUID)
    case createJob
    case createPrinter
    case maintenanceAnalytics
    case uptimeReliability
    case predictiveInsights(printerId: UUID)
    case jobHistory
    case jobTimeline
    case dispatchDashboard
}
