import Foundation
import Security

// MARK: - Optional Type Detection

/// Protocol to detect Optional types at runtime.
/// Any Optional<Wrapped> conforms to this, allowing us to check if a type is Optional.
private protocol OptionalProtocol {
    static func wrappedNone() -> Any
}

extension Optional: OptionalProtocol {
    static func wrappedNone() -> Any {
        return Self.none as Any
    }
}

// MARK: - TLS Diagnostics

private struct TLSDiagnosticSnapshot: Sendable {
    let requestHost: String?
    let challengeHost: String?
    let authenticationMethod: String?
    let disposition: String?
    let trustSource: String?
    let trustError: String?
    let certificateWarning: String?
    let timestamp: Date
}

enum TLSDiagnostics {
    private static let lock = NSLock()
    nonisolated(unsafe) private static var snapshot: TLSDiagnosticSnapshot?

    static func beginRequest(host: String?) {
        lock.lock()
        snapshot = TLSDiagnosticSnapshot(
            requestHost: host,
            challengeHost: nil,
            authenticationMethod: nil,
            disposition: nil,
            trustSource: nil,
            trustError: nil,
            certificateWarning: nil,
            timestamp: Date()
        )
        lock.unlock()
    }

    static func recordChallenge(
        host: String,
        authenticationMethod: String,
        disposition: String,
        trustSource: String? = nil,
        trustError: String? = nil,
        certificateWarning: String? = nil
    ) {
        lock.lock()
        let requestHost = snapshot?.requestHost
        snapshot = TLSDiagnosticSnapshot(
            requestHost: requestHost ?? host,
            challengeHost: host,
            authenticationMethod: authenticationMethod,
            disposition: disposition,
            trustSource: trustSource,
            trustError: trustError,
            certificateWarning: certificateWarning,
            timestamp: Date()
        )
        lock.unlock()
    }

    static func recentSummary(maxAge: TimeInterval = 60) -> String? {
        lock.lock()
        let current = snapshot
        lock.unlock()

        guard let current,
              Date().timeIntervalSince(current.timestamp) <= maxAge else {
            return nil
        }

        if let challengeHost = current.challengeHost {
            var parts = ["host=\(challengeHost)"]
            if let authenticationMethod = current.authenticationMethod {
                parts.append("method=\(authenticationMethod)")
            }
            if let disposition = current.disposition {
                parts.append("disposition=\(disposition)")
            }
            if let trustSource = current.trustSource {
                parts.append("source=\(trustSource)")
            }
            if let trustError = current.trustError, !trustError.isEmpty {
                parts.append("trust=\(trustError)")
            }
            if let certificateWarning = current.certificateWarning, !certificateWarning.isEmpty {
                parts.append("cert=\(certificateWarning)")
            }
            return parts.joined(separator: ", ")
        }

        if let requestHost = current.requestHost {
            return "no trust challenge observed for \(requestHost)"
        }

        return nil
    }

    static func clear() {
        lock.lock()
        snapshot = nil
        lock.unlock()
    }
}

enum TLSCertificateProfile {
    private static let serverAuthOID = Data([0x06, 0x08, 0x2b, 0x06, 0x01, 0x05, 0x05, 0x07, 0x03, 0x01])
    private static let basicConstraintsOID = Data([0x06, 0x03, 0x55, 0x1d, 0x13])
    private static let basicConstraintsCATrueValue = Data([0x30, 0x03, 0x01, 0x01, 0xff])

    static func warningSummary(for certificate: SecCertificate) -> String? {
        warningSummary(der: SecCertificateCopyData(certificate) as Data)
    }

    static func warningSummary(der: Data) -> String? {
        var warnings: [String] = []

        if containsCATrueBasicConstraints(in: der) {
            warnings.append("leaf cert has CA:TRUE")
        }

        if !der.contains(serverAuthOID) {
            warnings.append("leaf cert missing serverAuth EKU")
        }

        return warnings.isEmpty ? nil : warnings.joined(separator: "; ")
    }

    private static func containsCATrueBasicConstraints(in der: Data) -> Bool {
        guard let oidRange = der.range(of: basicConstraintsOID) else {
            return false
        }

        let searchRange = oidRange.upperBound..<der.endIndex
        guard let valueRange = der.range(of: basicConstraintsCATrueValue, options: [], in: searchRange) else {
            return false
        }

        return valueRange.lowerBound - oidRange.upperBound <= 8
    }
}

// MARK: - Self-Signed Certificate Trust

/// URLSession delegate that supports private-network HTTPS while still preferring
/// normal system trust for CA-signed certificates. Production hostnames use
/// standard certificate validation.
/// Implements both session-level and task-level challenge handlers to cover all
/// URLSession API surfaces (completion-handler and async/await).
final class PrivateNetworkSessionDelegate: NSObject, URLSessionDelegate, URLSessionTaskDelegate, @unchecked Sendable {
    private static let ipv4Pattern = #"^\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}$"#
    private enum TrustSource: String {
        case systemTrust
        case leafAnchor
    }
    private struct TrustEvaluationResult {
        let credential: URLCredential?
        let trustError: String?
        let trustSource: String?
        let certificateWarning: String?
    }

    // MARK: - Session-level challenge (covers completion-handler API)

    func urlSession(
        _ session: URLSession,
        didReceive challenge: URLAuthenticationChallenge,
        completionHandler: @escaping (URLSession.AuthChallengeDisposition, URLCredential?) -> Void
    ) {
        handleChallenge(challenge, completionHandler: completionHandler)
    }

    // MARK: - Task-level challenge (covers async/await API)

    func urlSession(
        _ session: URLSession,
        task: URLSessionTask,
        didReceive challenge: URLAuthenticationChallenge,
        completionHandler: @escaping (URLSession.AuthChallengeDisposition, URLCredential?) -> Void
    ) {
        handleChallenge(challenge, completionHandler: completionHandler)
    }

    private func handleChallenge(
        _ challenge: URLAuthenticationChallenge,
        completionHandler: @escaping (URLSession.AuthChallengeDisposition, URLCredential?) -> Void
    ) {
        guard challenge.protectionSpace.authenticationMethod == NSURLAuthenticationMethodServerTrust,
              let serverTrust = challenge.protectionSpace.serverTrust else {
            TLSDiagnostics.recordChallenge(
                host: challenge.protectionSpace.host,
                authenticationMethod: challenge.protectionSpace.authenticationMethod,
                disposition: "defaultHandling"
            )
            completionHandler(.performDefaultHandling, nil)
            return
        }

        let host = challenge.protectionSpace.host
        let isPrivate = Self.isPrivateHost(host)

        if isPrivate {
            let result = credentialForPrivateHost(serverTrust, host: host)
            if let credential = result.credential {
                TLSDiagnostics.recordChallenge(
                    host: host,
                    authenticationMethod: challenge.protectionSpace.authenticationMethod,
                    disposition: "useCredential",
                    trustSource: result.trustSource,
                    certificateWarning: result.certificateWarning
                )
                completionHandler(.useCredential, credential)
            } else {
                TLSDiagnostics.recordChallenge(
                    host: host,
                    authenticationMethod: challenge.protectionSpace.authenticationMethod,
                    disposition: "cancelAuthenticationChallenge",
                    trustSource: result.trustSource,
                    trustError: result.trustError,
                    certificateWarning: result.certificateWarning
                )
                completionHandler(.cancelAuthenticationChallenge, nil)
            }
        } else {
            TLSDiagnostics.recordChallenge(
                host: host,
                authenticationMethod: challenge.protectionSpace.authenticationMethod,
                disposition: "defaultHandling"
            )
            completionHandler(.performDefaultHandling, nil)
        }
    }

    static func isPrivateHost(_ host: String) -> Bool {
        isIPv4Address(host) || host.hasSuffix(".local") || host == "localhost"
    }

    static func isIPv4Address(_ host: String) -> Bool {
        host.range(of: ipv4Pattern, options: .regularExpression) != nil
    }

    static func shouldAttemptLeafAnchorFallback(certificateCount: Int) -> Bool {
        certificateCount <= 1
    }

    private func credentialForPrivateHost(_ serverTrust: SecTrust, host: String) -> TrustEvaluationResult {
        let policy = SecPolicyCreateSSL(true, host as CFString)
        SecTrustSetPolicies(serverTrust, policy)

        let certificateCount = SecTrustGetCertificateCount(serverTrust)
        let leafCertificate = (SecTrustCopyCertificateChain(serverTrust) as? [SecCertificate])?.first
        let certificateWarning = leafCertificate.flatMap(TLSCertificateProfile.warningSummary(for:))

        var systemTrustError: CFError?
        if SecTrustEvaluateWithError(serverTrust, &systemTrustError) {
            return TrustEvaluationResult(
                credential: URLCredential(trust: serverTrust),
                trustError: nil,
                trustSource: TrustSource.systemTrust.rawValue,
                certificateWarning: certificateWarning
            )
        }

        guard Self.shouldAttemptLeafAnchorFallback(certificateCount: certificateCount) else {
            return TrustEvaluationResult(
                credential: nil,
                trustError: systemTrustError?.localizedDescription ?? "trust evaluation failed",
                trustSource: nil,
                certificateWarning: certificateWarning
            )
        }

        guard let leafCertificate else {
            return TrustEvaluationResult(
                credential: nil,
                trustError: systemTrustError?.localizedDescription ?? "missing leaf certificate",
                trustSource: nil,
                certificateWarning: nil
            )
        }

        SecTrustSetPolicies(serverTrust, policy)
        SecTrustSetAnchorCertificates(serverTrust, [leafCertificate] as CFArray)
        SecTrustSetAnchorCertificatesOnly(serverTrust, true)

        var leafAnchorError: CFError?
        guard SecTrustEvaluateWithError(serverTrust, &leafAnchorError) else {
            #if DEBUG
            let systemMessage = systemTrustError?.localizedDescription ?? "unknown error"
            let leafMessage = leafAnchorError?.localizedDescription ?? "unknown error"
            print("TLS trust evaluation failed for \(host): system=\(systemMessage) leaf-anchor=\(leafMessage)")
            #endif
            let combinedError: String
            if let systemMessage = systemTrustError?.localizedDescription,
               let leafMessage = leafAnchorError?.localizedDescription,
               systemMessage != leafMessage {
                combinedError = "system trust failed: \(systemMessage); leaf-anchor trust failed: \(leafMessage)"
            } else {
                combinedError = leafAnchorError?.localizedDescription
                    ?? systemTrustError?.localizedDescription
                    ?? "trust evaluation failed"
            }
            return TrustEvaluationResult(
                credential: nil,
                trustError: combinedError,
                trustSource: nil,
                certificateWarning: certificateWarning
            )
        }

        return TrustEvaluationResult(
            credential: URLCredential(trust: serverTrust),
            trustError: nil,
            trustSource: TrustSource.leafAnchor.rawValue,
            certificateWarning: certificateWarning
        )
    }
}

// MARK: - API Client

actor APIClient {
    private let session: URLSession
    private let decoder: JSONDecoder
    private let encoder: JSONEncoder
    private var baseURL: URL
    private var accessToken: String?
    private var tokenExpiryChecker: (@Sendable () async -> Bool)?

    /// Shared delegate that trusts self-signed certs on private networks.
    private static let privateNetworkDelegate = PrivateNetworkSessionDelegate()

    /// Creates a URLSession configured to trust self-signed certs on private networks.
    static func makePrivateNetworkSession() -> URLSession {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.requestCachePolicy = .reloadIgnoringLocalCacheData
        configuration.urlCache = nil
        return URLSession(
            configuration: configuration,
            delegate: privateNetworkDelegate,
            delegateQueue: nil
        )
    }

    /// Key used to persist the server URL across launches.
    static let serverURLKey = "pf_server_url"

    /// Normalizes user-entered server URLs into a canonical string.
    /// Bare hosts/IPs default to `https://`; explicit `http://` is preserved.
    static func normalizedServerURLString(_ raw: String) -> String? {
        normalizeServerURLString(raw, upgradeLegacyIPHTTP: false)
    }

    /// Restores the saved server URL string, upgrading legacy `http://` IP URLs.
    static func savedServerURLString() -> String? {
        guard let saved = UserDefaults.standard.string(forKey: serverURLKey),
              let normalized = normalizeServerURLString(saved, upgradeLegacyIPHTTP: true) else {
            return nil
        }

        if normalized != saved {
            UserDefaults.standard.set(normalized, forKey: serverURLKey)
        }

        return normalized
    }

    private static func normalizeServerURLString(
        _ raw: String,
        upgradeLegacyIPHTTP: Bool
    ) -> String? {
        let trimmed = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return nil }

        let candidate = trimmed.contains("://") ? trimmed : "https://\(trimmed)"
        guard var components = URLComponents(string: candidate),
              let scheme = components.scheme?.lowercased(),
              scheme == "http" || scheme == "https",
              let host = components.host, !host.isEmpty else {
            return nil
        }

        if upgradeLegacyIPHTTP, scheme == "http", isIPv4Address(host) {
            components.scheme = "https"
        }

        guard let url = components.url else { return nil }
        let absoluteString = url.absoluteString
        return absoluteString.hasSuffix("/") ? String(absoluteString.dropLast()) : absoluteString
    }

    private static func isIPv4Address(_ host: String) -> Bool {
        host.range(
            of: #"^\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}$"#,
            options: .regularExpression
        ) != nil
    }

    /// ISO 8601 formatter with fractional seconds (matches ASP.NET Core output).
    nonisolated(unsafe) static let iso8601WithFractional: ISO8601DateFormatter = {
        let f = ISO8601DateFormatter()
        f.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return f
    }()

    /// ISO 8601 formatter without fractional seconds (fallback).
    nonisolated(unsafe) static let iso8601Plain: ISO8601DateFormatter = {
        let f = ISO8601DateFormatter()
        f.formatOptions = [.withInternetDateTime]
        return f
    }()

    init(baseURL: URL, session: URLSession? = nil) {
        self.baseURL = baseURL
        self.session = session ?? Self.makePrivateNetworkSession()

        self.decoder = JSONDecoder()
        // ASP.NET Core can emit fractional seconds; the built-in .iso8601 strategy
        // rejects them, so we use a custom strategy that tries both formats.
        decoder.dateDecodingStrategy = .custom { decoder in
            let container = try decoder.singleValueContainer()
            let text = try container.decode(String.self)
            if let date = Self.iso8601WithFractional.date(from: text) { return date }
            if let date = Self.iso8601Plain.date(from: text) { return date }
            throw DecodingError.dataCorruptedError(
                in: container,
                debugDescription: "Cannot decode date string: \(text)"
            )
        }

        self.encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
    }

    func setAccessToken(_ token: String?) {
        self.accessToken = token
    }

    /// Registers a closure that checks whether the current token is expired.
    /// Called before each API request to proactively reject expired tokens.
    func setTokenExpiryChecker(_ checker: @escaping @Sendable () async -> Bool) {
        self.tokenExpiryChecker = checker
    }

    func updateBaseURL(_ url: URL) {
        self.baseURL = url
        UserDefaults.standard.set(url.absoluteString, forKey: Self.serverURLKey)
    }

    func currentBaseURL() -> URL {
        baseURL
    }

    func currentAccessToken() -> String? {
        accessToken
    }

    /// Restores a previously-saved server URL from UserDefaults.
    /// Upgrades legacy `http://` IP URLs to `https://` to match current behavior.
    static func savedBaseURL() -> URL? {
        guard let saved = savedServerURLString() else { return nil }
        return URL(string: saved)
    }

    // MARK: - HTTP Methods

    func get<T: Decodable & Sendable>(_ path: String) async throws -> T {
        let request = try buildRequest(path: path, method: "GET")
        return try await execute(request)
    }

    func post<T: Decodable & Sendable, B: Encodable & Sendable>(_ path: String, body: B) async throws -> T {
        var request = try buildRequest(path: path, method: "POST")
        request.httpBody = try encoder.encode(body)
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        return try await execute(request)
    }

    func getData(_ path: String) async throws -> Data {
        try await checkTokenExpiry()
        let request = try buildRequest(path: path, method: "GET")
        let (data, response) = try await performRequest(request)
        try validateResponse(response, data: data)
        return data
    }

    func post<T: Decodable & Sendable>(_ path: String) async throws -> T {
        let request = try buildRequest(path: path, method: "POST")
        return try await execute(request)
    }

    func postVoid(_ path: String) async throws {
        let request = try buildRequest(path: path, method: "POST")
        try await executeVoid(request)
    }

    func postVoid<B: Encodable & Sendable>(_ path: String, body: B) async throws {
        var request = try buildRequest(path: path, method: "POST")
        request.httpBody = try encoder.encode(body)
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        try await executeVoid(request)
    }

    func putVoid(_ path: String) async throws {
        let request = try buildRequest(path: path, method: "PUT")
        try await executeVoid(request)
    }

    func putVoid<B: Encodable & Sendable>(_ path: String, body: B) async throws {
        var request = try buildRequest(path: path, method: "PUT")
        request.httpBody = try encoder.encode(body)
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        try await executeVoid(request)
    }

    func put<T: Decodable & Sendable, B: Encodable & Sendable>(_ path: String, body: B) async throws -> T {
        var request = try buildRequest(path: path, method: "PUT")
        request.httpBody = try encoder.encode(body)
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        return try await execute(request)
    }

    func patch<T: Decodable & Sendable, B: Encodable & Sendable>(_ path: String, body: B) async throws -> T {
        var request = try buildRequest(path: path, method: "PATCH")
        request.httpBody = try encoder.encode(body)
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        return try await execute(request)
    }

    func delete(_ path: String) async throws {
        let request = try buildRequest(path: path, method: "DELETE")
        try await executeVoid(request)
    }

    // MARK: - Internal

    private func buildRequest(path: String, method: String) throws -> URLRequest {
        // Pre-flight: reject if token is known to be expired
        // (check is sync-safe — the actual async check happens in execute/executeVoid wrappers)
        guard let url = URL(string: path, relativeTo: baseURL) else {
            throw NetworkError.invalidURL(path)
        }
        var request = URLRequest(url: url)
        request.httpMethod = method
        request.setValue("application/json", forHTTPHeaderField: "Accept")

        if let token = accessToken {
            request.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")
        }

        return request
    }

    private func execute<T: Decodable>(_ request: URLRequest) async throws -> T {
        try await checkTokenExpiry()
        let (data, response) = try await performRequest(request)
        try validateResponse(response, data: data)
        
        // Handle empty response body for Optional types (e.g., 204 No Content, 200 with empty body)
        if data.isEmpty {
            // Check if T is Optional by testing conformance to OptionalProtocol
            if let optionalType = T.self as? OptionalProtocol.Type {
                // T is Optional, return nil (wrapped as T)
                return optionalType.wrappedNone() as! T
            }
            // Non-optional type with empty body is an error
            throw NetworkError.decodingFailed(
                DecodingError.dataCorrupted(
                    DecodingError.Context(
                        codingPath: [],
                        debugDescription: "Empty response body for non-optional type \(T.self)"
                    )
                )
            )
        }
        
        do {
            return try decoder.decode(T.self, from: data)
        } catch {
            #if DEBUG
            let preview = String(data: data.prefix(2000), encoding: .utf8) ?? "<binary>"
            print("⚠️ [APIClient] Decode failed for \(T.self) at \(request.url?.path ?? "?"): \(error)")
            print("⚠️ [APIClient] Response body preview: \(preview)")
            #endif
            throw NetworkError.decodingFailed(error)
        }
    }

    private func executeVoid(_ request: URLRequest) async throws {
        try await checkTokenExpiry()
        let (data, response) = try await performRequest(request)
        try validateResponse(response, data: data)
    }

    private func checkTokenExpiry() async throws {
        if let checker = tokenExpiryChecker, await checker() {
            NotificationCenter.default.post(name: .sessionExpired, object: nil)
            throw NetworkError.unauthorized
        }
    }

    private func performRequest(_ request: URLRequest) async throws -> (Data, URLResponse) {
        TLSDiagnostics.beginRequest(host: request.url?.host)
        do {
            let result = try await session.data(for: request)
            TLSDiagnostics.clear()
            return result
        } catch let error as URLError {
            let isPrivateHTTPSRequest = request.url?.scheme?.lowercased() == "https"
                && (request.url?.host.map(PrivateNetworkSessionDelegate.isPrivateHost) ?? false)

            switch error.code {
            case .notConnectedToInternet, .networkConnectionLost, .dataNotAllowed:
                throw NetworkError.noConnection
            case .timedOut:
                throw NetworkError.timeout
            case .cannotFindHost, .cannotConnectToHost:
                if isPrivateHTTPSRequest {
                    throw NetworkError.transportError(error)
                }
                throw NetworkError.serverUnreachable
            case .appTransportSecurityRequiresSecureConnection:
                throw NetworkError.transportError(error)
            default:
                throw NetworkError.transportError(error)
            }
        }
    }

    private func validateResponse(_ response: URLResponse, data: Data) throws {
        guard let http = response as? HTTPURLResponse else {
            throw NetworkError.invalidResponse
        }
        switch http.statusCode {
        case 200...299:
            return
        case 401:
            NotificationCenter.default.post(name: .sessionExpired, object: nil)
            throw NetworkError.unauthorized
        case 403:
            throw NetworkError.forbidden
        case 404:
            throw NetworkError.notFound
        case 409:
            throw NetworkError.conflict
        case 400...499:
            let apiError = try? decoder.decode(APIError.self, from: data)
            throw NetworkError.clientError(http.statusCode, apiError)
        case 500...599:
            throw NetworkError.serverError(http.statusCode)
        default:
            throw NetworkError.unexpectedStatus(http.statusCode)
        }
    }
}

// MARK: - Errors

enum NetworkError: LocalizedError, Sendable {
    case invalidURL(String)
    case invalidResponse
    case unauthorized
    case forbidden
    case notFound
    case conflict
    case noConnection
    case timeout
    case serverUnreachable
    case clientError(Int, APIError?)
    case serverError(Int)
    case unexpectedStatus(Int)
    case decodingFailed(Error)
    case transportError(URLError)
    case authFailed(String)

    var errorDescription: String? {
        switch self {
        case .invalidURL(let path): return "Invalid URL: \(path)"
        case .invalidResponse: return "Invalid server response"
        case .unauthorized: return "Authentication required"
        case .forbidden: return "Access denied"
        case .notFound: return "Resource not found"
        case .conflict: return "Conflict — resource was modified"
        case .noConnection: return "No internet connection"
        case .timeout: return "Request timed out"
        case .serverUnreachable: return "Server is unreachable"
        case .clientError(let code, let apiError):
            return apiError?.detail ?? apiError?.message ?? apiError?.title ?? "Client error (\(code))"
        case .serverError(let code): return "Server error (\(code))"
        case .unexpectedStatus(let code): return "Unexpected status (\(code))"
        case .decodingFailed(let error): return "Failed to decode response: \(error.localizedDescription)"
        case .transportError(let error):
            let tlsSummary = TLSDiagnostics.recentSummary()
            let base: String
            if let streamComponent = Self.formattedStreamComponent(from: error) {
                base = "Network error (\(error.code.rawValue), \(streamComponent)): \(error.localizedDescription)"
            } else {
                base = "Network error (\(error.code.rawValue)): \(error.localizedDescription)"
            }

            var details: [String] = []
            if let transportHint = Self.transportHint(from: error, tlsSummary: tlsSummary) {
                details.append("transport: \(transportHint)")
            }
            if let trustHint = Self.trustHint(tlsSummary: tlsSummary) {
                details.append("trust-hint: \(trustHint)")
            }
            if let tlsSummary {
                details.append("tls: \(tlsSummary)")
            }

            guard !details.isEmpty else { return base }
            return "\(base) [\(details.joined(separator: "] ["))]"
        case .authFailed(let message): return message
        }
    }

    private static func formattedStreamComponent(from error: URLError) -> String? {
        guard let streamCode = error.userInfo["_kCFStreamErrorCodeKey"] as? Int else {
            return nil
        }

        if let streamReason = streamReason(for: streamCode) {
            return "stream \(streamCode): \(streamReason)"
        }

        return "stream \(streamCode)"
    }

    private static func transportHint(from error: URLError, tlsSummary: String?) -> String? {
        guard let tlsSummary,
              tlsSummary.hasPrefix("no trust challenge observed for "),
              let streamCode = error.userInfo["_kCFStreamErrorCodeKey"] as? Int,
              let posixCode = POSIXErrorCode(rawValue: Int32(streamCode)) else {
            return nil
        }

        switch posixCode {
        case .ECONNREFUSED:
            return "connection was refused before the TLS handshake started"
        case .ETIMEDOUT:
            return "connection timed out before the TLS handshake started"
        case .ECONNRESET:
            return "connection was reset before the TLS handshake completed"
        default:
            return nil
        }
    }

    private static func trustHint(tlsSummary: String?) -> String? {
        guard let tlsSummary else { return nil }

        var hints: [String] = []

        if tlsSummary.contains("leaf cert has CA:TRUE") {
            hints.append("server may be presenting a CA certificate instead of a TLS server leaf")
        }

        if tlsSummary.contains("certificate is not permitted for this usage")
            || tlsSummary.contains("leaf cert missing serverAuth EKU")
            || tlsSummary.contains("ExtendedKeyUsage") {
            hints.append("leaf certificate may be missing serverAuth")
        }

        if tlsSummary.contains("MissingIntermediate") {
            hints.append("TLS chain may be missing an intermediate certificate")
        }

        guard !hints.isEmpty else { return nil }
        return hints.joined(separator: "; ")
    }

    private static func streamReason(for streamCode: Int) -> String? {
        guard streamCode > 0,
              let posixCode = POSIXErrorCode(rawValue: Int32(streamCode)) else {
            return nil
        }

        switch posixCode {
        case .ECONNREFUSED:
            return "Connection refused"
        case .ETIMEDOUT:
            return "Connection timed out"
        case .ECONNRESET:
            return "Connection reset by peer"
        default:
            return nil
        }
    }
}

// MARK: - Session Expired Notification

extension Notification.Name {
    static let sessionExpired = Notification.Name("SessionExpired")
}
