import Foundation

enum AppConfig {
    /// Base URL for the Printfarmer API server.
    /// Override via PRINTFARMER_API_URL environment variable or update for your environment.
    static let baseURL: URL = {
        if let envURL = ProcessInfo.processInfo.environment["PRINTFARMER_API_URL"],
           let url = URL(string: envURL) {
            return url
        }
        // Default for local development
        return URL(string: "http://localhost:5000")!
    }()

    static let appVersion: String = {
        Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "1.0.0"
    }()

    static let buildNumber: String = {
        Bundle.main.infoDictionary?["CFBundleVersion"] as? String ?? "1"
    }()
}
