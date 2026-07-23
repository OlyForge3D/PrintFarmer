import Foundation
import XCTest

/// Intercepts requests for one mock URLSession without sharing handlers or
/// captured requests with concurrently running tests.
final class MockURLProtocol: URLProtocol {

    typealias RequestHandler = (URLRequest) throws -> (HTTPURLResponse, Data)
    /// I (issue #816): async variant of RequestHandler so tests can rendezvous
    /// on AsyncBarrier continuations instead of DispatchSemaphore timeouts. If
    /// set, takes precedence over the sync `requestHandler`.
    typealias AsyncRequestHandler = @Sendable (URLRequest) async throws -> (HTTPURLResponse, Data)

    final class Session: @unchecked Sendable {
        private let identifier = UUID().uuidString
        private let lock = NSLock()
        private var handler: RequestHandler?
        private var asyncHandler: AsyncRequestHandler?
        private var requests: [URLRequest] = []

        lazy var urlSession: URLSession = {
            let configuration = URLSessionConfiguration.ephemeral
            configuration.httpAdditionalHeaders = [MockURLProtocol.sessionHeader: identifier]
            configuration.protocolClasses = [MockURLProtocol.self]
            return URLSession(configuration: configuration)
        }()

        var requestHandler: RequestHandler? {
            get {
                lock.lock()
                defer { lock.unlock() }
                return handler
            }
            set {
                lock.lock()
                handler = newValue
                lock.unlock()
            }
        }

        /// I: an async handler that MockURLProtocol.startLoading awaits inside a
        /// Task. Preferred over the sync `requestHandler` when the test needs to
        /// rendezvous on an AsyncBarrier without a time-based timeout.
        var asyncRequestHandler: AsyncRequestHandler? {
            get {
                lock.lock()
                defer { lock.unlock() }
                return asyncHandler
            }
            set {
                lock.lock()
                asyncHandler = newValue
                lock.unlock()
            }
        }

        var capturedRequests: [URLRequest] {
            lock.lock()
            defer { lock.unlock() }
            return requests
        }

        fileprivate init() {
            MockURLProtocol.register(self, identifier: identifier)
        }

        deinit {
            MockURLProtocol.unregister(identifier: identifier)
        }

        func reset() {
            lock.lock()
            handler = nil
            asyncHandler = nil
            requests = []
            lock.unlock()
        }

        fileprivate func capture(_ request: URLRequest) -> (sync: RequestHandler?, async: AsyncRequestHandler?) {
            lock.lock()
            defer { lock.unlock() }
            requests.append(request)
            return (handler, asyncHandler)
        }
    }

    private final class WeakSession: @unchecked Sendable {
        weak var value: Session?

        init(_ value: Session) {
            self.value = value
        }
    }

    private static let sessionHeader = "X-PrintFarmer-Mock-Session"
    private static let registryLock = NSLock()
    nonisolated(unsafe) private static var sessions: [String: WeakSession] = [:]

    override static func canInit(with request: URLRequest) -> Bool {
        request.value(forHTTPHeaderField: sessionHeader) != nil
    }

    override static func canonicalRequest(for request: URLRequest) -> URLRequest { request }

    override func startLoading() {
        guard let identifier = request.value(forHTTPHeaderField: Self.sessionHeader),
              let session = Self.registeredSession(identifier: identifier) else {
            client?.urlProtocol(self, didFailWithError: URLError(.unknown))
            return
        }
        let (syncHandler, asyncHandler) = session.capture(request)
        guard syncHandler != nil || asyncHandler != nil else {
            client?.urlProtocol(self, didFailWithError: URLError(.unknown))
            return
        }

        // Dispatch handler execution off the shared URLProtocol thread so per-session
        // handlers cannot serialize each other when one intentionally blocks (needed to
        // prove per-session isolation deterministically in APIClientTests overlap tests).
        let capturedRequest = request
        let capturedClient = client
        if let asyncHandler {
            // I (issue #816): async handler path — the test rendezvous happens
            // on continuation-based AsyncBarriers, no dispatch semaphores.
            let holder = ProtocolHolder(protocol: self)
            Task {
                do {
                    let (response, data) = try await asyncHandler(capturedRequest)
                    if let proto = holder.proto {
                        capturedClient?.urlProtocol(proto, didReceive: response, cacheStoragePolicy: .notAllowed)
                        capturedClient?.urlProtocol(proto, didLoad: data)
                        capturedClient?.urlProtocolDidFinishLoading(proto)
                    }
                } catch {
                    if let proto = holder.proto {
                        capturedClient?.urlProtocol(proto, didFailWithError: error)
                    }
                }
            }
            return
        }
        DispatchQueue.global(qos: .userInitiated).async { [weak self] in
            guard let self, let handler = syncHandler else { return }
            do {
                let (response, data) = try handler(capturedRequest)
                self.client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
                self.client?.urlProtocol(self, didLoad: data)
                self.client?.urlProtocolDidFinishLoading(self)
            } catch {
                self.client?.urlProtocol(self, didFailWithError: error)
            }
        }
    }

    /// Weak-holding, Sendable wrapper for MockURLProtocol so the async handler
    /// Task can capture a reference without violating Sendable checking on the
    /// non-Sendable URLProtocol.
    private final class ProtocolHolder: @unchecked Sendable {
        weak var proto: MockURLProtocol?
        init(protocol proto: MockURLProtocol) { self.proto = proto }
    }

    override func stopLoading() {}

    static func makeSession() -> Session {
        Session()
    }

    private static func register(_ session: Session, identifier: String) {
        registryLock.lock()
        sessions[identifier] = WeakSession(session)
        registryLock.unlock()
    }

    private static func unregister(identifier: String) {
        registryLock.lock()
        sessions.removeValue(forKey: identifier)
        registryLock.unlock()
    }

    private static func registeredSession(identifier: String) -> Session? {
        registryLock.lock()
        defer { registryLock.unlock() }
        guard let session = sessions[identifier]?.value else {
            sessions.removeValue(forKey: identifier)
            return nil
        }
        return session
    }
}

extension URLRequest {
    func capturedHTTPBody() -> Data? {
        if let httpBody {
            return httpBody
        }

        guard let stream = httpBodyStream else { return nil }
        stream.open()
        defer { stream.close() }

        let bufferSize = 1024
        let buffer = UnsafeMutablePointer<UInt8>.allocate(capacity: bufferSize)
        defer { buffer.deallocate() }

        var data = Data()
        while stream.hasBytesAvailable {
            let readCount = stream.read(buffer, maxLength: bufferSize)
            if readCount < 0 {
                return data.isEmpty ? nil : data
            }
            if readCount == 0 {
                break
            }
            data.append(buffer, count: readCount)
        }

        return data
    }
}
