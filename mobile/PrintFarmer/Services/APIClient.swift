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

// MARK: - Active Server Generation

final class ActiveServerGeneration: @unchecked Sendable {
    private let lock = NSLock()
    private var value = 0

    var current: Int {
        lock.lock()
        defer { lock.unlock() }
        return value
    }

    @discardableResult
    func advance() -> Int {
        lock.lock()
        defer { lock.unlock() }
        value += 1
        return value
    }

    func isCurrent(_ generation: Int) -> Bool {
        lock.lock()
        defer { lock.unlock() }
        return value == generation
    }
}

// MARK: - API Client

actor APIClient {
    private let session: URLSession
    private let decoder: JSONDecoder
    private let encoder: JSONEncoder
    private var baseURL: URL
    private var accessToken: String?
    /// The exact auth-session/operation token this client's authenticated session was
    /// applied under (A). Carried on a 401 so the expiry handler can reject a stale
    /// event; nil for unauthenticated / login clients.
    private var authSessionToken: Int?
    private var tokenExpiryChecker: (@Sendable () async -> Bool)?
    private let serverGeneration: ActiveServerGeneration?
    private let generationAtCreation: Int?

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
        savedServerURLString(userDefaults: .standard)
    }

    static func savedServerURLString(userDefaults: UserDefaults) -> String? {
        guard let saved = userDefaults.string(forKey: serverURLKey),
              let normalized = normalizeServerURLString(saved, upgradeLegacyIPHTTP: true) else {
            return nil
        }

        if normalized != saved {
            userDefaults.set(normalized, forKey: serverURLKey)
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
        let effectiveScheme = components.scheme?.lowercased() ?? scheme
        components.scheme = effectiveScheme
        components.host = host.lowercased()
        if (effectiveScheme == "https" && components.port == 443)
            || (effectiveScheme == "http" && components.port == 80) {
            components.port = nil
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

    init(
        baseURL: URL,
        session: URLSession? = nil,
        serverGeneration: ActiveServerGeneration? = nil,
        accessToken: String? = nil,
        authSessionToken: Int? = nil
    ) {
        self.baseURL = baseURL
        self.session = session ?? Self.makePrivateNetworkSession()
        self.accessToken = accessToken
        self.serverGeneration = serverGeneration
        self.generationAtCreation = serverGeneration?.current
        // A2: bind the authenticated session's identity ATOMICALLY at construction.
        // Callers capture `authOperationEpoch.current` synchronously BEFORE handing
        // to the factory; the Task that used to publish identity later is gone, so
        // no orphaned reconstructed-client can read a newer epoch and clobber this
        // client's identity with a superseded operation's token. Unauthenticated
        // clients (nil accessToken) MUST have nil authSessionToken so they cannot
        // publish session-expiry.
        self.authSessionToken = accessToken == nil ? nil : authSessionToken

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

    /// Applies base URL + access token ATOMICALLY, but only if `epoch.isCurrent(token)`
    /// at the moment of application — a destination compare-and-set for the shared
    /// session (issue #816 H2). Because this runs inside the APIClient actor, the epoch
    /// check and the mutation cannot be separated by an await, so a superseded login /
    /// restore can never clobber a newer operation's session. Returns whether applied.
    @discardableResult
    func applySessionIfCurrent(baseURL: URL?, accessToken: String?, epoch: AuthOperationEpoch, token: Int) -> Bool {
        // B: perform the currency check AND the session mutation as ONE atomic
        // operation under the epoch lock, so the epoch cannot advance between them.
        let applied: Bool? = epoch.withCurrent(token) {
            if let baseURL {
                self.baseURL = baseURL
                UserDefaults.standard.set(baseURL.absoluteString, forKey: Self.serverURLKey)
            }
            self.accessToken = accessToken
            // A: capture the exact auth-session/operation identity this authenticated
            // session was applied under, so a later 401 carries it and the handler can
            // reject a stale event without borrowing the current token.
            self.authSessionToken = accessToken == nil ? nil : token
            return true
        }
        return applied ?? false
    }

    /// Post a session-expiry event carrying the originating auth-session identity
    /// `{serverGeneration, authSessionToken}` (A). SUPPRESSED for unauthenticated /
    /// login clients (nil generation or no captured auth session), which must never be
    /// able to log out an authenticated session. `authSessionToken` is the token the
    /// FAILING REQUEST was issued under (captured before the network await), never a
    /// later-applied session's token.
    private func postSessionExpired(authSessionToken: Int?) {
        guard let generationAtCreation, let authSessionToken else { return }
        NotificationCenter.default.post(
            name: .sessionExpired,
            object: nil,
            userInfo: ["generation": generationAtCreation, "authSessionToken": authSessionToken]
        )
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

    func unauthenticatedClient(baseURL: URL) -> APIClient {
        APIClient(baseURL: baseURL, session: session)
    }

    /// J: an immutable client snapshot carrying THIS client's exact current baseURL and
    /// bearer, so a request (e.g. `/logout`) is issued under the captured OLD session
    /// even if the shared client is later repointed to a newer session by a concurrent
    /// login. Carries no server generation, so it never emits its own session-expiry.
    func sessionSnapshotClient() -> APIClient {
        APIClient(baseURL: baseURL, session: session, serverGeneration: nil, accessToken: accessToken)
    }

    /// J (issue #816 reject): capture the session snapshot ATOMICALLY under an
    /// operation currency check, so the returned client's bearer/baseURL matches
    /// EXACTLY the operation the caller is running. Returns nil if the epoch has
    /// advanced past `token` — a stale logout thus cannot contact the server as a
    /// newer session. Because the currency check and the snapshot capture share
    /// this actor call (no await between them), the epoch cannot advance between
    /// them.
    func sessionSnapshotClientIfCurrent(epoch: AuthOperationEpoch, token: Int) -> APIClient? {
        let snapshot: APIClient? = epoch.withCurrent(token) {
            APIClient(baseURL: baseURL, session: session, serverGeneration: nil, accessToken: accessToken)
        }
        return snapshot
    }

    /// Restores a previously-saved server URL from UserDefaults.
    /// Upgrades legacy `http://` IP URLs to `https://` to match current behavior.
    static func savedBaseURL() -> URL? {
        guard let saved = savedServerURLString() else { return nil }
        return URL(string: saved)
    }

    // MARK: - HTTP Methods

    func get<T: Decodable & Sendable>(_ path: String) async throws -> T {
        // A1: capture the immutable session snapshot (bearer + generation +
        // authSessionToken) atomically at PUBLIC API ENTRY, before any await, and
        // thread it through request-build/checker/perform so no downstream step
        // reads mutable `self` after a suspension.
        let requestSession = captureRequestSession()
        let request = try buildRequest(session: requestSession, path: path, method: "GET")
        return try await execute(request, session: requestSession)
    }

    func post<T: Decodable & Sendable, B: Encodable & Sendable>(_ path: String, body: B) async throws -> T {
        let requestSession = captureRequestSession()
        var request = try buildRequest(session: requestSession, path: path, method: "POST")
        request.httpBody = try encoder.encode(body)
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        return try await execute(request, session: requestSession)
    }

    func getData(_ path: String) async throws -> Data {
        let requestSession = captureRequestSession() // A1: capture at entry, before any await
        try await checkTokenExpiry(session: requestSession)
        // A1: build from the captured snapshot's bearer, NEVER `self.accessToken`
        // which a concurrent applySessionIfCurrent could have advanced during the
        // checker's await — that would let this request go out with T2's bearer
        // while it is still labeled with T1's generation/authSessionToken.
        let request = try buildRequest(session: requestSession, path: path, method: "GET")
        let (data, response) = try await performRequest(request)
        try validateResponseGeneration(session: requestSession)
        try validateResponse(response, data: data, authSessionToken: requestSession.authSessionToken)
        return data
    }

    // MARK: - Reachability

    /// Lightweight, unauthenticated reachability probe for the connection indicator.
    /// Sends a short GET to `path` (default `/healthz`) using the configured
    /// private-network session so self-signed certs are still trusted. Returns
    /// `true` when the server answers with an HTTP status below 500 (server is up),
    /// `false` on transport failure or a 5xx response. Never throws.
    func isReachable(path: String = "/healthz", timeout: TimeInterval = 6) async -> Bool {
        guard let url = URL(string: path, relativeTo: baseURL) else { return false }
        var request = URLRequest(url: url)
        request.httpMethod = "GET"
        request.timeoutInterval = timeout
        request.cachePolicy = .reloadIgnoringLocalCacheData
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        do {
            let (_, response) = try await session.data(for: request)
            guard let http = response as? HTTPURLResponse else { return false }
            return http.statusCode < 500
        } catch {
            return false
        }
    }

    func post<T: Decodable & Sendable>(_ path: String) async throws -> T {
        let requestSession = captureRequestSession()
        let request = try buildRequest(session: requestSession, path: path, method: "POST")
        return try await execute(request, session: requestSession)
    }

    func postVoid(_ path: String) async throws {
        let requestSession = captureRequestSession()
        let request = try buildRequest(session: requestSession, path: path, method: "POST")
        try await executeVoid(request, session: requestSession)
    }

    func postVoid(_ path: String, headers: [String: String]) async throws {
        let requestSession = captureRequestSession()
        var request = try buildRequest(session: requestSession, path: path, method: "POST")
        for (name, value) in headers {
            request.setValue(value, forHTTPHeaderField: name)
        }
        try await executeVoid(request, session: requestSession)
    }

    func postVoid<B: Encodable & Sendable>(_ path: String, body: B) async throws {
        let requestSession = captureRequestSession()
        var request = try buildRequest(session: requestSession, path: path, method: "POST")
        request.httpBody = try encoder.encode(body)
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        try await executeVoid(request, session: requestSession)
    }

    func putVoid(_ path: String) async throws {
        let requestSession = captureRequestSession()
        let request = try buildRequest(session: requestSession, path: path, method: "PUT")
        try await executeVoid(request, session: requestSession)
    }

    func putVoid<B: Encodable & Sendable>(_ path: String, body: B) async throws {
        let requestSession = captureRequestSession()
        var request = try buildRequest(session: requestSession, path: path, method: "PUT")
        request.httpBody = try encoder.encode(body)
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        try await executeVoid(request, session: requestSession)
    }

    func put<T: Decodable & Sendable, B: Encodable & Sendable>(_ path: String, body: B) async throws -> T {
        let requestSession = captureRequestSession()
        var request = try buildRequest(session: requestSession, path: path, method: "PUT")
        request.httpBody = try encoder.encode(body)
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        return try await execute(request, session: requestSession)
    }

    func patch<T: Decodable & Sendable, B: Encodable & Sendable>(_ path: String, body: B) async throws -> T {
        let requestSession = captureRequestSession()
        var request = try buildRequest(session: requestSession, path: path, method: "PATCH")
        request.httpBody = try encoder.encode(body)
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        return try await execute(request, session: requestSession)
    }

    func delete(_ path: String) async throws {
        let requestSession = captureRequestSession()
        let request = try buildRequest(session: requestSession, path: path, method: "DELETE")
        try await executeVoid(request, session: requestSession)
    }

    // MARK: - Internal

    private func buildRequest(session requestSession: RequestSession, path: String, method: String) throws -> URLRequest {
        // A1: validate against the captured generation, not `self`, so a concurrent
        // session mutation cannot change our decision after we already committed to
        // building under this snapshot's identity.
        try validateRequestGeneration(session: requestSession)
        guard let url = URL(string: path, relativeTo: baseURL) else {
            throw NetworkError.invalidURL(path)
        }
        var request = URLRequest(url: url)
        request.httpMethod = method
        request.setValue("application/json", forHTTPHeaderField: "Accept")

        // A1: use the captured snapshot's bearer, NEVER `self.accessToken` after any
        // suspension — a concurrent applySessionIfCurrent could have advanced the
        // shared client to T2 by now; the snapshot still carries T1's bearer so this
        // request cannot be built with T2's bearer while it is still labeled with
        // T1's identity (or vice versa).
        if let token = requestSession.accessToken {
            request.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")
        }

        return request
    }

    /// Immutable snapshot of the authenticated request session, captured at API-call
    /// ENTRY — before request construction, the token-expiry checker, or ANY await (A1).
    /// Every downstream step — request building, expiry preflight, and the 401 post —
    /// uses THIS snapshot's identity (bearer + generation + authSessionToken) so a
    /// concurrent same-server re-login applied during an await can never make this
    /// request build with T2's bearer while it is still labeled with T1's identity,
    /// or vice versa. The snapshot is never re-read from `self` after a suspension.
    private struct RequestSession {
        let accessToken: String?
        let generationAtCreation: Int?
        let authSessionToken: Int?
    }

    /// Capture the immutable request-session identity synchronously (no await). Must be
    /// the FIRST thing every public request method does, before building the request or
    /// awaiting the checker.
    private func captureRequestSession() -> RequestSession {
        RequestSession(
            accessToken: accessToken,
            generationAtCreation: generationAtCreation,
            authSessionToken: authSessionToken
        )
    }

    private func execute<T: Decodable>(_ request: URLRequest, session requestSession: RequestSession) async throws -> T {
        // A1: the caller captured `requestSession` at PUBLIC API ENTRY before ANY
        // await; downstream steps thread it through so no path re-reads mutable
        // `self` after a suspension.
        try await checkTokenExpiry(session: requestSession)
        let (data, response) = try await performRequest(request)
        try validateResponseGeneration(session: requestSession)
        try validateResponse(response, data: data, authSessionToken: requestSession.authSessionToken)
        
        // Handle empty response body for Optional types (e.g., 204 No Content, 200 with empty body)
        if data.isEmpty {
            // Check if T is Optional by testing conformance to OptionalProtocol
            if let optionalType = T.self as? OptionalProtocol.Type {
                // T is Optional, return nil (wrapped as T)
                return optionalType.wrappedNone() as! T
            }
            // Non-optional type with empty body is an error
            throw NetworkError.decodingFailed(
                ResponseDecodingFailure(
                    error: DecodingError.dataCorrupted(
                        DecodingError.Context(
                            codingPath: [],
                            debugDescription: "Empty response body for non-optional type \(T.self)"
                        )
                    ),
                    targetType: T.self
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
            throw NetworkError.decodingFailed(ResponseDecodingFailure(error: error, targetType: T.self))
        }
    }

    private func executeVoid(_ request: URLRequest, session requestSession: RequestSession) async throws {
        try await checkTokenExpiry(session: requestSession)
        let (data, response) = try await performRequest(request)
        try validateResponseGeneration(session: requestSession)
        try validateResponse(response, data: data, authSessionToken: requestSession.authSessionToken)
    }

    private func checkTokenExpiry(session: RequestSession) async throws {
        try validateRequestGeneration(session: session)
        if let checker = tokenExpiryChecker, await checker() {
            // A1: post the captured request-session token, NEVER `self.authSessionToken`
            // read after the checker's await (which a concurrent re-login could change).
            postSessionExpired(authSessionToken: session.authSessionToken)
            throw NetworkError.unauthorized
        }
    }

    /// A1: validate the request-session generation against the shared server generation
    /// using the SNAPSHOT's `generationAtCreation`. Used at request build & response
    /// validation; never re-read from `self.generationAtCreation` after an await.
    private func validateRequestGeneration(session: RequestSession) throws {
        guard let serverGeneration, let generationAtCreation = session.generationAtCreation else { return }
        if !serverGeneration.isCurrent(generationAtCreation) {
            throw NetworkError.staleServerResponse
        }
    }

    private func validateResponseGeneration(session: RequestSession) throws {
        try validateRequestGeneration(session: session)
    }

    private func validateActiveServerGeneration() throws {
        guard let serverGeneration, let generationAtCreation else { return }
        if !serverGeneration.isCurrent(generationAtCreation) {
            throw NetworkError.staleServerResponse
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

    private func validateResponse(_ response: URLResponse, data: Data, authSessionToken: Int?) throws {
        guard let http = response as? HTTPURLResponse else {
            throw NetworkError.invalidResponse
        }
        switch http.statusCode {
        case 200...299:
            return
        case 401:
            postSessionExpired(authSessionToken: authSessionToken)
            throw NetworkError.unauthorized
        case 403:
            throw NetworkError.forbidden
        case 404:
            // #728: 404 bodies may carry RFC 7807 ProblemDetails with a
            // camelCase `code` extension. The operator feature gate
            // (#725) uses `code == "featureDisabled"` to indicate that a
            // gated endpoint (e.g. `/api/attention`) short-circuited.
            // Decode the body so callers can distinguish a gated 404
            // from a genuinely missing resource without parsing
            // localized text. Empty, malformed, or legacy 404 bodies
            // continue to map to `.notFound` for backward compatibility.
            if !data.isEmpty,
               let apiError = try? decoder.decode(APIError.self, from: data),
               apiError.code == "featureDisabled" {
                throw NetworkError.featureDisabled(apiError)
            }
            throw NetworkError.notFound
        case 405:
            throw NetworkError.methodNotAllowed
        case 409:
            // #714 (F9): the printed-parts harvest endpoints
            // (`POST /api/job-queue/{id}/harvest`) return a typed RFC 7807
            // ProblemDetails 409 with `code == "wrongBin"` or
            // `"partMappingRequired"` and extension fields the harvest UI
            // must read structurally (see PartsInventoryProblemDetails.cs).
            // Decode it when the `code` extension is recognised so callers
            // get typed detail; every other 409 (e.g. plain `{message}`
            // conflicts from job-state checks) keeps mapping to the
            // existing bare `.conflict` case so current callers
            // (PrinterControlsViewModel) are unaffected.
            if !data.isEmpty,
               let apiError = try? decoder.decode(APIError.self, from: data),
               apiError.code == PartsInventoryConflict.wrongBinCode
                || apiError.code == PartsInventoryConflict.partMappingRequiredCode,
               let conflict = try? decoder.decode(PartsInventoryConflict.self, from: data) {
                throw NetworkError.partsInventoryConflict(conflict)
            }
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

struct ResponseDecodingFailure: Error, Sendable {
    let targetType: String
    let kind: String
    let codingPath: String
    let expectedType: String
    let debugDescription: String

    init(error: Error, targetType: Any.Type) {
        self.targetType = String(describing: targetType)

        guard let decodingError = error as? DecodingError else {
            self.kind = "unknown"
            self.codingPath = "<root>"
            self.expectedType = String(describing: targetType)
            self.debugDescription = error.localizedDescription
            return
        }

        switch decodingError {
        case .keyNotFound(let key, let context):
            self.kind = "keyNotFound"
            self.codingPath = Self.formatPath(context.codingPath + [key])
            self.expectedType = "required key '\(key.stringValue)'"
            self.debugDescription = context.debugDescription
        case .typeMismatch(let type, let context):
            self.kind = "typeMismatch"
            self.codingPath = Self.formatPath(context.codingPath)
            self.expectedType = String(describing: type)
            self.debugDescription = context.debugDescription
        case .valueNotFound(let type, let context):
            self.kind = "valueNotFound"
            self.codingPath = Self.formatPath(context.codingPath)
            self.expectedType = String(describing: type)
            self.debugDescription = context.debugDescription
        case .dataCorrupted(let context):
            self.kind = "dataCorrupted"
            self.codingPath = Self.formatPath(context.codingPath)
            self.expectedType = String(describing: targetType)
            self.debugDescription = context.debugDescription
        @unknown default:
            self.kind = "unknown"
            self.codingPath = "<root>"
            self.expectedType = String(describing: targetType)
            self.debugDescription = error.localizedDescription
        }
    }

    var userMessage: String {
        "Failed to decode response for \(targetType): \(kind) at \(codingPath); expected \(expectedType); \(debugDescription).\nYour PrintFarmer server version may be incompatible; update the server."
    }

    private static func formatPath(_ codingPath: [CodingKey]) -> String {
        guard !codingPath.isEmpty else { return "<root>" }
        return codingPath.map(\.stringValue).joined(separator: ".")
    }
}

enum NetworkError: LocalizedError, Sendable {
    case invalidURL(String)
    case invalidResponse
    case unauthorized
    case forbidden
    case notFound
    /// A gated endpoint short-circuited with an RFC 7807 ProblemDetails
    /// 404 whose `code` extension identifies the gate (currently only
    /// `"featureDisabled"` from the operator feature gate, #725).
    /// Callers use this to branch to a safe fallback UI instead of
    /// treating the resource as genuinely missing. Introduced by #728.
    case featureDisabled(APIError)
    case methodNotAllowed
    case conflict
    /// A typed printed-parts harvest conflict (#714/#741): either a
    /// `wrongBin` mismatch (scanned destination bin does not match the
    /// expected bin for one or more SKUs) or `partMappingRequired`
    /// (the job has no resolvable output → SKU mapping). Callers use the
    /// decoded ``PartsInventoryConflict`` to render the exact guidance the
    /// backend adjudicated, rather than a generic conflict message.
    case partsInventoryConflict(PartsInventoryConflict)
    case noConnection
    case timeout
    case serverUnreachable
    case clientError(Int, APIError?)
    case serverError(Int)
    case unexpectedStatus(Int)
    case decodingFailed(ResponseDecodingFailure)
    case transportError(URLError)
    case authFailed(String)
    case staleServerResponse

    var errorDescription: String? {
        switch self {
        case .invalidURL(let path): return "Invalid URL: \(path)"
        case .invalidResponse: return "Invalid server response"
        case .unauthorized: return "Authentication required"
        case .forbidden: return "Access denied"
        case .notFound: return "Resource not found"
        case .featureDisabled(let apiError):
            return apiError.detail ?? apiError.title ?? "This feature is disabled on the server."
        case .methodNotAllowed:
            return "This action isn't supported by your PrintFarmer server (405). Update the server to the latest version."
        case .conflict: return "Conflict — resource was modified"
        case .partsInventoryConflict(let conflict): return conflict.detail ?? conflict.title ?? "Printed-parts conflict"
        case .noConnection: return "No internet connection"
        case .timeout: return "Request timed out"
        case .serverUnreachable: return "Server is unreachable"
        case .clientError(let code, let apiError):
            return apiError?.detail ?? apiError?.message ?? apiError?.title ?? "Client error (\(code))"
        case .serverError(let code): return "Server error (\(code))"
        case .unexpectedStatus(let code): return "Unexpected status (\(code))"
        case .decodingFailed(let failure): return failure.userMessage
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
        case .staleServerResponse: return "Ignored response from a previous server selection"
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
