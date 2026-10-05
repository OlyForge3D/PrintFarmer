import Foundation
import SwiftUI

/// One farm-floor shell with independent stacks on iPhone and iPad.
@MainActor @Observable
final class AppRouter {
    static let selectedTabDefaultsKey = "app.selectedTab"

    struct FilamentSwapDeepLink: Equatable {
        let printerId: UUID
        let toolheadIndex: Int
        let jobId: UUID?
    }

    var selectedTab: AppTab {
        didSet {
            userDefaults?.set(selectedTab.rawValue, forKey: Self.selectedTabDefaultsKey)
        }
    }
    var printersPath = NavigationPath()
    var jobsPath = NavigationPath()
    var inventoryPath = NavigationPath()
    var notificationBadgeCount = 0
    var pendingReadyCount = 0
    var sidebarVisibility: NavigationSplitViewVisibility = .automatic
    var pendingNFCReadyPrinterId: UUID?
    var pendingSpoolHighlightId: Int?
    var pendingNeedsAttentionFilter = false
    var pendingFilamentSwap: FilamentSwapDeepLink?
    var pendingExternalScanRequestID: UUID?
    var isScanFlowDismissing = false
    var notificationRoutingError: String?
    private var navigationEpoch = 0
    @ObservationIgnored private let userDefaults: UserDefaults?

    init(userDefaults: UserDefaults? = nil) {
        self.userDefaults = userDefaults
        selectedTab = Self.restoredTab(
            from: userDefaults?.string(forKey: Self.selectedTabDefaultsKey)
        )
    }

    static func restoredTab(from persistedRawValue: String?) -> AppTab {
        switch persistedRawValue {
        case "inventory", "scan": .filament
        case "jobs": .queue
        default: persistedRawValue.flatMap(AppTab.init(rawValue:)) ?? .farm
        }
    }

    func navigate(to destination: DeepLinkDestination, capabilities: ResolvedSystemCapabilities) {
        navigationEpoch &+= 1
        let capturedEpoch = navigationEpoch
        switch destination {
        case .scan:
            prepareExternalScan(capabilities: capabilities)
        case .farm:
            selectedTab = .farm
            printersPath = NavigationPath()
        case .attentionItem:
            selectedTab = .farm
            printersPath = NavigationPath()
            pendingNeedsAttentionFilter = true
        case .spoolDetail(let id):
            selectedTab = .filament
            inventoryPath = NavigationPath()
            pendingSpoolHighlightId = id
        case .printerDetail(let id), .printerReady(let id):
            selectedTab = .farm
            printersPath = NavigationPath()
            pendingNFCReadyPrinterId = destination == .printerReady(id: id) ? id : nil
            pushPrinter(id, epoch: capturedEpoch)
        case .filamentSwap(let printerId, let toolheadIndex, let jobId):
            selectedTab = .farm
            printersPath = NavigationPath()
            pendingFilamentSwap = capabilities.guidedSwapEnabled
                ? FilamentSwapDeepLink(printerId: printerId, toolheadIndex: toolheadIndex, jobId: jobId)
                : nil
            pushPrinter(printerId, epoch: capturedEpoch)
        }
    }

    private func pushPrinter(_ id: UUID, epoch: Int) {
        Task { @MainActor in
            try? await Task.sleep(for: .milliseconds(50))
            guard epoch == navigationEpoch else { return }
            printersPath.append(AppDestination.printerDetail(id: id))
        }
    }

    func invalidatePendingNavigation() {
        revokeAdvancedPrinterControlsAccess()
        isScanFlowDismissing = false
        pendingNFCReadyPrinterId = nil
        pendingSpoolHighlightId = nil
        pendingNeedsAttentionFilter = false
        pendingFilamentSwap = nil
        notificationRoutingError = nil
    }

    func revokeAdvancedPrinterControlsAccess() {
        navigationEpoch &+= 1
        printersPath = NavigationPath()
        jobsPath = NavigationPath()
        inventoryPath = NavigationPath()
    }

    func reconcileCapabilities(_ capabilities: ResolvedSystemCapabilities) {
        if !capabilities.guidedSwapEnabled { pendingFilamentSwap = nil }
    }

    func isAtRoot(_ tab: AppTab) -> Bool {
        switch tab {
        case .farm: printersPath.isEmpty
        case .queue: jobsPath.isEmpty
        case .filament: inventoryPath.isEmpty
        }
    }

    func resetAdaptiveShellSession() {
        invalidatePendingNavigation()
        selectedTab = .farm
    }

    func routeToFilamentSwap(printerID: UUID) {
        navigationEpoch &+= 1
        selectedTab = .farm
        printersPath = NavigationPath()
        printersPath.append(AppDestination.printerDetail(id: printerID))
    }

    func routeToJobQueue(capabilities: ResolvedSystemCapabilities) {
        selectedTab = .queue
        jobsPath = NavigationPath()
    }

    func resetToRoot(tab: AppTab) {
        switch tab {
        case .farm: printersPath = NavigationPath()
        case .queue: jobsPath = NavigationPath()
        case .filament: inventoryPath = NavigationPath()
        }
    }
}
