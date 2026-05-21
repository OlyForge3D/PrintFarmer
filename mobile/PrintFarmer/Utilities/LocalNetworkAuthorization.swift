import Foundation
import Network

/// Triggers the iOS Local Network permission dialog using a temporary NWBrowser.
///
/// iOS only shows the permission prompt when the app first attempts local network
/// access. By browsing for a Bonjour service we force the dialog to appear at a
/// controlled point in the UX — before the login screen — so the user can grant
/// access before a real API request is made.
@MainActor
final class LocalNetworkAuthorization {
    private var browser: NWBrowser?
    private var didResume = false

    /// Requests local network access by briefly browsing for a Bonjour service.
    ///
    /// The returned `Bool` is `true` when the browser received at least one
    /// state-change indicating the system processed the request (permission
    /// was either already granted or was just granted). It returns `false`
    /// after a timeout, which typically means the dialog was denied or
    /// dismissed.
    func requestAuthorization() async -> Bool {
        didResume = false
        return await withCheckedContinuation { continuation in
            let parameters = NWParameters()
            parameters.includePeerToPeer = true

            let browser = NWBrowser(
                for: .bonjour(type: "_printfarmer._tcp", domain: nil),
                using: parameters
            )
            self.browser = browser

            browser.stateUpdateHandler = { [weak self] state in
                DispatchQueue.main.async {
                    MainActor.assumeIsolated {
                        guard let self, !self.didResume else { return }
                        switch state {
                        case .ready:
                            self.didResume = true
                            self.stopBrowser()
                            continuation.resume(returning: true)
                        case .failed, .waiting:
                            self.didResume = true
                            self.stopBrowser()
                            continuation.resume(returning: false)
                        case .cancelled:
                            break
                        default:
                            break
                        }
                    }
                }
            }

            browser.start(queue: .main)

            // Timeout — if the dialog was already accepted in a previous launch
            // the callback fires almost immediately. Guard against hanging.
            DispatchQueue.main.asyncAfter(deadline: .now() + 3) { [weak self] in
                MainActor.assumeIsolated {
                    guard let self, !self.didResume else { return }
                    self.didResume = true
                    self.stopBrowser()
                    continuation.resume(returning: true)
                }
            }
        }
    }

    private func stopBrowser() {
        browser?.cancel()
        browser = nil
    }
}
