import SwiftUI

#if canImport(UIKit)
import WebKit

struct MJPEGStreamView: UIViewRepresentable {
    let url: URL
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

        webView.load(URLRequest(url: url))
        return webView
    }

    func updateUIView(_ webView: WKWebView, context: Context) {
        if context.coordinator.currentURL != url {
            context.coordinator.currentURL = url
            webView.load(URLRequest(url: url))
        }

        let rotationCSS = """
            document.body.style.margin = '0';
            document.body.style.padding = '0';
            document.body.style.overflow = 'hidden';
            document.body.style.background = 'transparent';
            var img = document.querySelector('img');
            if (img) {
                img.style.width = '100%';
                img.style.height = 'auto';
                img.style.display = 'block';
                img.style.transform = 'rotate(\(rotation)deg)';
                img.style.transformOrigin = 'center center';
            }
        """
        webView.evaluateJavaScript(rotationCSS, completionHandler: nil)
    }

    func makeCoordinator() -> Coordinator {
        Coordinator()
    }

    final class Coordinator: NSObject, WKNavigationDelegate {
        var currentURL: URL?
        var hasLoaded = false

        func webView(_ webView: WKWebView, didFinish navigation: WKNavigation!) {
            hasLoaded = true
            let setupCSS = """
                document.body.style.margin = '0';
                document.body.style.padding = '0';
                document.body.style.overflow = 'hidden';
                document.body.style.background = 'transparent';
                var img = document.querySelector('img');
                if (img) {
                    img.style.width = '100%';
                    img.style.height = 'auto';
                    img.style.display = 'block';
                }
            """
            webView.evaluateJavaScript(setupCSS, completionHandler: nil)
        }
    }
}

struct MJPEGStreamContainer: View {
    let url: URL
    let rotation: Int
    @State private var isLoading = true
    @State private var loadingTask: Task<Void, Never>?

    var body: some View {
        ZStack {
            MJPEGStreamView(url: url, rotation: rotation)
                .aspectRatio(16 / 9, contentMode: .fit)
                .clipShape(RoundedRectangle(cornerRadius: 12))
                .onAppear { scheduleLoadingDismiss() }

            if isLoading {
                RoundedRectangle(cornerRadius: 12)
                    .fill(Color.pfCard)
                    .aspectRatio(16 / 9, contentMode: .fit)
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
