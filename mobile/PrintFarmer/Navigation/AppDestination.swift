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
    /// Advanced printer controls (jog, preheat, z-offset, disable motors).
    /// F1 (#706) moves these off the printer detail scroll and gates them
    /// behind an "Advanced" navigation destination inside Printer Detail.
    case advancedPrinterControls(printerId: UUID)
}
