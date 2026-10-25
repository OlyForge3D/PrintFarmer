import SwiftUI

/// Resolves the camera references returned by `GET /api/printers/{id}/camera/url`.
/// The server returns same-origin relative proxy routes (`/api/printers/{id}/camera/stream`),
/// which must be resolved against the active server and sent with the session bearer.
enum CameraURLResolver {
    /// Resolves `value` to an absolute http(s) URL. Absolute http(s) URLs pass through
    /// unchanged; relative paths need `baseURL` and resolve with the same semantics as
    /// `APIClient` request paths.
    static func resolve(_ value: String?, baseURL: URL?) -> URL? {
        guard let trimmed = value?.trimmingCharacters(in: .whitespacesAndNewlines),
              !trimmed.isEmpty,
              let parsed = URL(string: trimmed) else { return nil }
        if parsed.scheme != nil {
            return isHTTP(parsed) ? parsed : nil
        }
        guard let baseURL, isHTTP(baseURL),
              let resolved = URL(string: trimmed, relativeTo: baseURL)?.absoluteURL,
              isHTTP(resolved) else { return nil }
        return resolved
    }

    /// True when `value` names a snapshot the server can serve through the authenticated
    /// printer snapshot endpoint: a relative proxy path or an absolute http(s) URL.
    static func isSnapshotReference(_ value: String?) -> Bool {
        guard let trimmed = value?.trimmingCharacters(in: .whitespacesAndNewlines),
              !trimmed.isEmpty,
              let parsed = URL(string: trimmed) else { return false }
        if parsed.scheme == nil { return trimmed.hasPrefix("/") }
        return isHTTP(parsed)
    }

    /// Builds the stream request. The bearer is attached only when the stream is served by
    /// the authenticated server's origin, so it is never leaked to a third-party camera host.
    static func streamRequest(for url: URL, serverBaseURL: URL?, accessToken: String?) -> URLRequest {
        var request = URLRequest(url: url)
        if let accessToken, !accessToken.isEmpty,
           let serverBaseURL, isSameOrigin(url, serverBaseURL) {
            request.setValue("Bearer \(accessToken)", forHTTPHeaderField: "Authorization")
        }
        return request
    }

    private static func isHTTP(_ url: URL) -> Bool {
        let scheme = url.scheme?.lowercased()
        return (scheme == "http" || scheme == "https") && url.host?.isEmpty == false
    }

    private static func isSameOrigin(_ lhs: URL, _ rhs: URL) -> Bool {
        guard let lhsScheme = lhs.scheme?.lowercased(), lhsScheme == rhs.scheme?.lowercased(),
              let lhsHost = lhs.host?.lowercased(), lhsHost == rhs.host?.lowercased() else {
            return false
        }
        return effectivePort(lhs) == effectivePort(rhs)
    }

    private static func effectivePort(_ url: URL) -> Int? {
        if let port = url.port { return port }
        switch url.scheme?.lowercased() {
        case "http": return 80
        case "https": return 443
        default: return nil
        }
    }
}

#if canImport(UIKit)
import WebKit

struct MJPEGStreamView: UIViewRepresentable {
    let request: URLRequest
    let rotation: Int

    func makeUIView(context: Context) -> WKWebView {
        let config = WKWebViewConfiguration()
        config.allowsInlineMediaPlayback = true

        let webView = WKWebView(frame: .zero, configuration: config)
        webView.isOpaque = false
        webView.backgroundColor = .clear
        webView.scrollView.backgroundColor = .clear
        webView.scrollView.isScrollEnabled = false
        webView.scrollView.bounces = false
        webView.navigationDelegate = context.coordinator

        context.coordinator.currentRequest = request
        webView.load(request)
        return webView
    }

    func updateUIView(_ webView: WKWebView, context: Context) {
        if context.coordinator.currentRequest != request {
            context.coordinator.currentRequest = request
            webView.load(request)
        }

        context.coordinator.rotation = rotation
        webView.evaluateJavaScript(Self.layoutScript(rotation: rotation), completionHandler: nil)
    }

    /// Centres the frame and aspect-fits it; a quarter turn swaps the image box
    /// so the rotated frame fills the (already swapped) container (#3496).
    static func layoutScript(rotation: Int) -> String {
        let quarterTurn = PrinterDetailCameraLayout.isQuarterTurn(rotation)
        return """
            document.body.style.margin = '0';
            document.body.style.padding = '0';
            document.body.style.overflow = 'hidden';
            document.body.style.background = 'transparent';
            document.body.style.height = '100vh';
            var img = document.querySelector('img');
            if (img) {
                img.style.position = 'absolute';
                img.style.left = '50%';
                img.style.top = '50%';
                img.style.width = '\(quarterTurn ? "100vh" : "100vw")';
                img.style.height = '\(quarterTurn ? "100vw" : "100vh")';
                img.style.objectFit = 'contain';
                img.style.display = 'block';
                img.style.transform = 'translate(-50%, -50%) rotate(\(rotation)deg)';
                img.style.transformOrigin = 'center center';
            }
        """
    }

    func makeCoordinator() -> Coordinator {
        Coordinator()
    }

    final class Coordinator: NSObject, WKNavigationDelegate {
        var currentRequest: URLRequest?
        var hasLoaded = false
        var rotation = 0

        func webView(_ webView: WKWebView, didFinish navigation: WKNavigation!) {
            hasLoaded = true
            webView.evaluateJavaScript(MJPEGStreamView.layoutScript(rotation: rotation), completionHandler: nil)
        }
    }
}

struct MJPEGStreamContainer: View {
    let url: URL
    let rotation: Int
    /// Returns the authenticated server's base URL and bearer, read atomically.
    let credentials: @Sendable () async -> (baseURL: URL, accessToken: String?)?
    @State private var request: URLRequest?
    @State private var isLoading = true
    @State private var loadingTask: Task<Void, Never>?

    static func aspectRatio(rotation: Int) -> CGFloat {
        PrinterDetailCameraLayout.aspectRatio(
            imageSize: PrinterDetailCameraLayout.streamImageSize, rotation: rotation
        ) ?? 16 / 9
    }

    var body: some View {
        ZStack {
            if let request {
                MJPEGStreamView(request: request, rotation: rotation)
                    .aspectRatio(Self.aspectRatio(rotation: rotation), contentMode: .fit)
                    .clipShape(RoundedRectangle(cornerRadius: 12))
                    .onAppear { scheduleLoadingDismiss() }
            }

            if isLoading || request == nil {
                RoundedRectangle(cornerRadius: 12)
                    .fill(Color.pfCard)
                    .aspectRatio(Self.aspectRatio(rotation: rotation), contentMode: .fit)
                    .overlay {
                        VStack(spacing: 8) {
                            ProgressView()
                            Text("Connecting to stream…")
                                .font(.caption)
                                .foregroundStyle(Color.pfTextSecondary)
                        }
                    }
            }
        }
        .task(id: url) {
            let session = await credentials()
            guard !Task.isCancelled else { return }
            request = CameraURLResolver.streamRequest(
                for: url,
                serverBaseURL: session?.baseURL,
                accessToken: session?.accessToken
            )
        }
        .onDisappear { loadingTask?.cancel() }
    }

    private func scheduleLoadingDismiss() {
        loadingTask = Task {
            try? await Task.sleep(for: .seconds(2))
            withAnimation { isLoading = false }
        }
    }
}
#endif
