import Foundation
import os

/// SignalR real-time connection to /hubs/printers.
///
/// Uses URLSessionWebSocketTask with the SignalR JSON protocol (messages
/// delimited by ASCII Record Separator 0x1E). Handles negotiate → WebSocket
/// upgrade → handshake → message loop with auto-reconnect on disconnect.
@Observable
final class SignalRService: @unchecked Sendable, SignalRServiceProtocol {
    private(set) var connectionState: SignalRConnectionState = .disconnected

    private let serverURL: URL
    private let tokenProvider: @Sendable () async -> String?
    private let session: URLSession
    private let decoder: JSONDecoder
    private let logger = Logger(subsystem: "com.printfarmer.ios", category: "SignalR")

    /// 0x1E — ASCII Record Separator, SignalR message terminator
    private static let recordSeparator: UInt8 = 0x1E

    private var webSocketTask: URLSessionWebSocketTask?
    private var receiveTask: Task<Void, Never>?
    private var pingTask: Task<Void, Never>?
    private var reconnectAttempt = 0
    private var intentionalDisconnect = false

    private var printerUpdateHandlers: [@Sendable (PrinterStatusUpdate) -> Void] = []
    private var jobQueueUpdateHandlers: [@Sendable (JobQueueUpdate) -> Void] = []
    private let handlerLock = NSLock()

    init(serverURL: URL, session: URLSession = .shared, tokenProvider: @escaping @Sendable () async -> String?) {
        self.serverURL = serverURL
        self.tokenProvider = tokenProvider
        self.session = session

        self.decoder = JSONDecoder()
        // Match APIClient's dual-format date decoder — ASP.NET Core emits fractional seconds
        decoder.dateDecodingStrategy = .custom { decoder in
            let container = try decoder.singleValueContainer()
            let text = try container.decode(String.self)
            if let date = APIClient.iso8601WithFractional.date(from: text) { return date }
            if let date = APIClient.iso8601Plain.date(from: text) { return date }
            throw DecodingError.dataCorruptedError(
                in: container,
                debugDescription: "Cannot decode date string: \(text)"
            )
        }
    }

    // MARK: - Public API

    func connect() async throws {
        intentionalDisconnect = false
        connectionState = .connecting
        reconnectAttempt = 0
        try await performConnect()
    }

    func disconnect() async {
        intentionalDisconnect = true
        tearDown()
        connectionState = .disconnected
        logger.info("Disconnected from SignalR hub")
    }

    func onPrinterUpdated(_ handler: @escaping @Sendable (PrinterStatusUpdate) -> Void) {
        handlerLock.lock()
        printerUpdateHandlers.append(handler)
        handlerLock.unlock()
    }

    func onJobQueueUpdated(_ handler: @escaping @Sendable (JobQueueUpdate) -> Void) {
        handlerLock.lock()
        jobQueueUpdateHandlers.append(handler)
        handlerLock.unlock()
    }

    // MARK: - Connection Lifecycle

    private func performConnect() async throws {
        // Step 1: Negotiate to get a connection token
        let token = await tokenProvider()
        let negotiateResponse = try await negotiate(jwt: token)

        // Step 2: Open WebSocket with the connection token
        let connectionToken = negotiateResponse.connectionToken ?? negotiateResponse.connectionId ?? ""
        try await openWebSocket(connectionToken: connectionToken, jwt: token)

        // Step 3: Send SignalR handshake
        try await sendHandshake()

        connectionState = .connected
        reconnectAttempt = 0
        logger.info("Connected to SignalR hub at \(self.serverURL.absoluteString)")

        // Step 4: Start receive loop and keepalive ping
        startReceiveLoop()
        startPingLoop()
    }

    private func negotiate(jwt: String?) async throws -> SignalRNegotiateResponse {
        guard var components = URLComponents(url: serverURL, resolvingAgainstBaseURL: true) else {
            throw NetworkError.invalidURL(serverURL.absoluteString)
        }
        components.path = (components.path.hasSuffix("/") ? components.path : components.path + "/") + "hubs/printers/negotiate"
        components.queryItems = [URLQueryItem(name: "negotiateVersion", value: "1")]

        guard let url = components.url else {
            throw NetworkError.invalidURL("hubs/printers/negotiate")
        }

        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        if let jwt {
            request.setValue("Bearer \(jwt)", forHTTPHeaderField: "Authorization")
        }

        let (data, response) = try await session.data(for: request)
        guard let http = response as? HTTPURLResponse, (200...299).contains(http.statusCode) else {
            throw NetworkError.serverError((response as? HTTPURLResponse)?.statusCode ?? 0)
        }

        return try decoder.decode(SignalRNegotiateResponse.self, from: data)
    }

    private func openWebSocket(connectionToken: String, jwt: String?) async throws {
        guard var components = URLComponents(url: serverURL, resolvingAgainstBaseURL: true) else {
            throw NetworkError.invalidURL(serverURL.absoluteString)
        }
        let isSecure = components.scheme == "https"
        components.scheme = isSecure ? "wss" : "ws"
        components.path = (components.path.hasSuffix("/") ? components.path : components.path + "/") + "hubs/printers"

        var queryItems = [URLQueryItem(name: "id", value: connectionToken)]
        if let jwt {
            queryItems.append(URLQueryItem(name: "access_token", value: jwt))
        }
        components.queryItems = queryItems

        guard let wsURL = components.url else {
            throw NetworkError.invalidURL("hubs/printers (WebSocket)")
        }

        let task = session.webSocketTask(with: wsURL)
        task.resume()
        self.webSocketTask = task
    }

    private func sendHandshake() async throws {
        let handshake = SignalRHandshakeRequest(protocol: "json", version: 1)
        let data = try JSONEncoder().encode(handshake)
        var message = data
        message.append(Self.recordSeparator)
        try await webSocketTask?.send(.data(message))

        // Wait for handshake response
        guard let wsTask = webSocketTask else { throw NetworkError.invalidResponse }
        let result = try await wsTask.receive()

        switch result {
        case .data(let data):
            let trimmed = data.split(separator: Self.recordSeparator).first ?? data[...]
            if let json = try? JSONSerialization.jsonObject(with: Data(trimmed)) as? [String: Any],
               let error = json["error"] as? String {
                throw NetworkError.authFailed("SignalR handshake failed: \(error)")
            }
        case .string(let text):
            let cleaned = text.replacingOccurrences(of: "\u{1e}", with: "")
            if let data = cleaned.data(using: .utf8),
               let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
               let error = json["error"] as? String {
                throw NetworkError.authFailed("SignalR handshake failed: \(error)")
            }
        @unknown default:
            break
        }
    }

    // MARK: - Message Loop

    private func startReceiveLoop() {
        receiveTask = Task { [weak self] in
            guard let self else { return }
            while !Task.isCancelled {
                guard let wsTask = self.webSocketTask else { break }
                do {
                    let message = try await wsTask.receive()
                    self.handleMessage(message)
                } catch {
                    if !Task.isCancelled && !self.intentionalDisconnect {
                        self.logger.warning("WebSocket receive error: \(error.localizedDescription)")
                        await self.handleDisconnect()
                    }
                    break
                }
            }
        }
    }

    private func startPingLoop() {
        pingTask = Task { [weak self] in
            while !Task.isCancelled {
                try? await Task.sleep(for: .seconds(15))
                guard !Task.isCancelled, let self, let wsTask = self.webSocketTask else { break }
                // SignalR ping is type 6
                let ping = "{\"type\":6}"
                var data = Data(ping.utf8)
                data.append(Self.recordSeparator)
                try? await wsTask.send(.data(data))
            }
        }
    }

    private func handleMessage(_ message: URLSessionWebSocketTask.Message) {
        let rawData: Data
        switch message {
        case .data(let data):
            rawData = data
        case .string(let text):
            rawData = Data(text.utf8)
        @unknown default:
            return
        }

        // Split by record separator — a single WebSocket frame can contain multiple SignalR messages
        let frames = rawData.split(separator: Self.recordSeparator)
        for frame in frames {
            guard !frame.isEmpty else { continue }
            processFrame(Data(frame))
        }
    }

    private func processFrame(_ data: Data) {
        guard let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else { return }
        guard let type = json["type"] as? Int else { return }

        switch type {
        case 1: // Invocation
            handleInvocation(json, rawData: data)
        case 6: // Ping
            break
        case 7: // Close
            let error = json["error"] as? String
            logger.info("SignalR close frame received: \(error ?? "no error")")
            if !intentionalDisconnect {
                Task { await handleDisconnect() }
            }
        default:
            break
        }
    }

    private func handleInvocation(_ json: [String: Any], rawData: Data) {
        guard let target = (json["target"] as? String)?.lowercased(),
              let arguments = json["arguments"] as? [Any],
              let firstArg = arguments.first else { return }

        guard let argData = try? JSONSerialization.data(withJSONObject: firstArg) else { return }

        switch target {
        case "printerupdated":
            do {
                let update = try decoder.decode(PrinterStatusUpdate.self, from: argData)
                handlerLock.lock()
                let handlers = printerUpdateHandlers
                handlerLock.unlock()
                for handler in handlers {
                    handler(update)
                }
            } catch {
                logger.warning("Failed to decode printerupdated: \(error.localizedDescription)")
            }

        case "jobqueueupdate":
            do {
                let update = try decoder.decode(JobQueueUpdate.self, from: argData)
                handlerLock.lock()
                let handlers = jobQueueUpdateHandlers
                handlerLock.unlock()
                for handler in handlers {
                    handler(update)
                }
            } catch {
                logger.warning("Failed to decode jobqueueupdate: \(error.localizedDescription)")
            }

        case "toolheadupdate", "extruderupdate", "heaterbedupdate":
            break

        default:
            logger.debug("Unhandled SignalR event: \(target)")
        }
    }

    // MARK: - Reconnection

    private func handleDisconnect() async {
        tearDown()
        guard !intentionalDisconnect else { return }

        connectionState = .reconnecting
        reconnectAttempt += 1

        // Exponential backoff: 1s, 2s, 4s, 8s, 16s, max 30s
        let delay = min(pow(2.0, Double(reconnectAttempt - 1)), 30.0)
        logger.info("Reconnecting in \(delay)s (attempt \(self.reconnectAttempt))")

        try? await Task.sleep(for: .seconds(delay))

        guard !intentionalDisconnect, !Task.isCancelled else { return }

        do {
            try await performConnect()
        } catch {
            logger.warning("Reconnect attempt \(self.reconnectAttempt) failed: \(error.localizedDescription)")
            if reconnectAttempt < 10 {
                await handleDisconnect()
            } else {
                connectionState = .disconnected
                logger.error("Gave up reconnecting after \(self.reconnectAttempt) attempts")
            }
        }
    }

    private func tearDown() {
        receiveTask?.cancel()
        receiveTask = nil
        pingTask?.cancel()
        pingTask = nil
        webSocketTask?.cancel(with: .normalClosure, reason: nil)
        webSocketTask = nil
    }
}
