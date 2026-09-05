#if canImport(UIKit)
import UIKit

/// SwiftUI's alert action precedes UIKit's dismissal completion. Keep the
/// result acknowledged only after the alert and presentation transitions end.
@MainActor
enum ScanPresentationReadiness {
    static func waitUntilReady() async -> Bool {
        let root = UIApplication.shared.connectedScenes
            .compactMap { $0 as? UIWindowScene }
            .filter { $0.activationState == .foregroundActive || $0.activationState == .foregroundInactive }
            .flatMap(\.windows)
            .first(where: \.isKeyWindow)?
            .rootViewController
        guard let root else { return false }
        return await waitUntilReady(from: root)
    }

    static func waitUntilReady(from root: UIViewController) async -> Bool {
        while !Task.isCancelled {
            guard root.viewIfLoaded?.window != nil else { return false }
            if !hasPendingPresentation(root) { return true }
            do {
                try await Task.sleep(for: .milliseconds(16))
            } catch {
                return false
            }
        }
        return false
    }

    private static func hasPendingPresentation(_ controller: UIViewController) -> Bool {
        if controller is UIAlertController || controller.isBeingPresented
            || controller.isBeingDismissed || controller.transitionCoordinator != nil {
            return true
        }
        if let presented = controller.presentedViewController, hasPendingPresentation(presented) {
            return true
        }
        return controller.children.contains(where: hasPendingPresentation)
    }
}
#endif
