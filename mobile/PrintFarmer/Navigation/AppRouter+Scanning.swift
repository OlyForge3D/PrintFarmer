import Foundation
import SwiftUI

extension AppRouter {
    func prepareExternalScan(capabilities: ResolvedSystemCapabilities) {
        guard makeTabVisibleIfPossible(.inventory, capabilities: capabilities) else { return }
        selectedTab = .inventory
        inventoryPath = NavigationPath()
        pendingExternalScanRequestID = UUID()
    }

    func consumeExternalScanRequest() -> Bool {
        guard !isScanFlowDismissing, pendingExternalScanRequestID != nil else { return false }
        pendingExternalScanRequestID = nil
        return true
    }

    /// Drops an in-flight external scan request at an identity boundary (#2480).
    ///
    /// Deliberately separate from `invalidatePendingNavigation()`: a plain
    /// navigation reset must still preserve the request (#2453), but a logout,
    /// an abandoned login, or a server/account switch must not let it replay
    /// into an unrelated session.
    func cancelPendingExternalScan() {
        pendingExternalScanRequestID = nil
        isScanFlowDismissing = false
    }

    func beginScanFlowDismissal(queuedExternalRequestID: UUID?) {
        isScanFlowDismissing = true
        pendingExternalScanRequestID = pendingExternalScanRequestID ?? queuedExternalRequestID
    }

    func completeScanFlowDismissal(capabilities: ResolvedSystemCapabilities) {
        isScanFlowDismissing = false
        guard pendingExternalScanRequestID != nil,
              makeTabVisibleIfPossible(.inventory, capabilities: capabilities) else { return }
        // A retry owns a new scan sheet, but must not invalidate the result's
        // delayed printer push, spool highlight, or navigation stacks.
        selectedTab = .inventory
    }
}
