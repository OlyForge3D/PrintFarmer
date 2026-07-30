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
        private let identifier: String
        private let lock = NSLock()
        private var handler: RequestHandler?
        private var asyncHandler: AsyncRequestHandler?
        private var requests: [URLRequest] = []
        private var stopLoadingObserver: (@Sendable () -> Void)?
        private var completionObserver: (@Sendable (Bool) -> Void)?

        /// I (issue #812): one serial, unspecified-QoS execution queue per
        /// session. Handler callbacks for a single session run FIFO and never
        /// overlap each other, while different sessions own different queues so
        /// cross-session requests still overlap concurrently. Unspecified QoS
        /// (no explicit `.userInitiated`) avoids the Thread Performance Checker
        /// priority-inversion advisories the old global-queue dispatch raised.
        fileprivate let executionQueue: DispatchQueue

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

        /// I (issue #812): test seam fired when `stopLoading()` fences an
        /// in-flight load for this session. Lets a regression prove the stop
        /// happened-before it releases a parked handler, so cancellation
        /// suppression is deterministic (no sleeps/polling).
        var onStopLoading: (@Sendable () -> Void)? {
            get {
                lock.lock()
                defer { lock.unlock() }
                return stopLoadingObserver
            }
            set {
                lock.lock()
                stopLoadingObserver = newValue
                lock.unlock()
            }
        }

        /// I (issue #812): test seam fired once per load when a terminal
        /// `URLProtocolClient` callback is reached. `delivered` is `true` when
        /// the completion was delivered and `false` when the lifecycle fence
        /// suppressed it (stopped or already completed).
        var onCompletion: (@Sendable (Bool) -> Void)? {
            get {
                lock.lock()
                defer { lock.unlock() }
                return completionObserver
            }
            set {
                lock.lock()
                completionObserver = newValue
                lock.unlock()
            }
        }

        fileprivate init() {
            let identifier = UUID().uuidString
            self.identifier = identifier
            self.executionQueue = DispatchQueue(label: "MockURLProtocol.Session.\(identifier)")
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
            stopLoadingObserver = nil
            completionObserver = nil
            lock.unlock()
        }

        fileprivate func capture(_ request: URLRequest) -> (sync: RequestHandler?, async: AsyncRequestHandler?) {
            lock.lock()
            defer { lock.unlock() }
            requests.append(request)
            return (handler, asyncHandler)
        }

        fileprivate func notifyStopLoading() {
            lock.lock()
            let observer = stopLoadingObserver
            lock.unlock()
            observer?()
        }

        fileprivate func notifyCompletion(delivered: Bool) {
            lock.lock()
            let observer = completionObserver
            lock.unlock()
            observer?(delivered)
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

    /// I (issue #812): per-load lifecycle fence. `stopLoading()` sets `stopped`
    /// so every later `URLProtocolClient` callback is suppressed, and `didComplete`
    /// guarantees a terminal callback is delivered at most once. Both flags are
    /// guarded by `lifecycleLock`, giving a synchronized stopped/completed fence.
    private let lifecycleLock = NSLock()
    private var stopped = false
    private var didComplete = false
    private weak var boundSession: Session?

    private func setBoundSession(_ session: Session) {
        lifecycleLock.lock()
        boundSession = session
        lifecycleLock.unlock()
    }

    private func currentBoundSession() -> Session? {
        lifecycleLock.lock()
        defer { lifecycleLock.unlock() }
        return boundSession
    }

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
        setBoundSession(session)
        let (syncHandler, asyncHandler) = session.capture(request)
        guard syncHandler != nil || asyncHandler != nil else {
            client?.urlProtocol(self, didFailWithError: URLError(.unknown))
            return
        }

        // Dispatch handler execution off the shared URLProtocol thread so per-session
        // handlers cannot serialize each other when one intentionally blocks (needed to
        // prove per-session isolation deterministically in APIClientTests overlap tests).
        let capturedRequest = request
        if let asyncHandler {
            // I (issue #816): async handler path — the test rendezvous happens
            // on continuation-based AsyncBarriers, no dispatch semaphores.
            // The lifecycle fence still gates delivery so a stopped load is
            // suppressed and completion happens at most once.
            let holder = ProtocolHolder(protocol: self)
            Task {
                do {
                    let (response, data) = try await asyncHandler(capturedRequest)
                    if let proto = holder.proto {
                        proto.emit { $0.client?.urlProtocol($0, didReceive: response, cacheStoragePolicy: .notAllowed) }
                        proto.emit { $0.client?.urlProtocol($0, didLoad: data) }
                        proto.finish { $0.client?.urlProtocolDidFinishLoading($0) }
                    }
                } catch {
                    if let proto = holder.proto {
                        proto.finish { $0.client?.urlProtocol($0, didFailWithError: error) }
                    }
                }
            }
            return
        }
        // I (issue #812): run the sync handler on this session's serial queue so
        // same-session handlers execute FIFO without overlap, while separate
        // sessions (separate queues) still overlap concurrently. The protocol and
        // the non-Sendable sync handler are passed through Sendable boxes so the
        // serial-queue closure introduces no Sendable-capture warnings.
        guard let syncHandler else { return }
        let holder = ProtocolHolder(protocol: self)
        let handlerBox = SyncHandlerBox(syncHandler)
        session.executionQueue.async {
            guard let proto = holder.proto else { return }
            do {
                let (response, data) = try handlerBox.handler(capturedRequest)
                proto.emit { $0.client?.urlProtocol($0, didReceive: response, cacheStoragePolicy: .notAllowed) }
                proto.emit { $0.client?.urlProtocol($0, didLoad: data) }
                proto.finish { $0.client?.urlProtocolDidFinishLoading($0) }
            } catch {
                proto.finish { $0.client?.urlProtocol($0, didFailWithError: error) }
            }
        }
    }

    /// I (issue #812): deliver a non-terminal client callback only while the
    /// load is still live (not stopped and not completed).
    private func emit(_ body: (MockURLProtocol) -> Void) {
        lifecycleLock.lock()
        let live = !stopped && !didComplete
        lifecycleLock.unlock()
        guard live else { return }
        body(self)
    }

    /// I (issue #812): deliver a terminal client callback at most once and only
    /// while the load has not been stopped. Notifies the bound session whether
    /// the terminal was delivered or suppressed so tests can assert the fence.
    private func finish(_ body: (MockURLProtocol) -> Void) {
        lifecycleLock.lock()
        let deliver = !stopped && !didComplete
        if deliver { didComplete = true }
        lifecycleLock.unlock()
        if deliver { body(self) }
        currentBoundSession()?.notifyCompletion(delivered: deliver)
    }

    /// Weak-holding, Sendable wrapper for MockURLProtocol so the async handler
    /// Task can capture a reference without violating Sendable checking on the
    /// non-Sendable URLProtocol.
    private final class ProtocolHolder: @unchecked Sendable {
        weak var proto: MockURLProtocol?
        init(protocol proto: MockURLProtocol) { self.proto = proto }
    }

    /// Sendable box for the non-Sendable sync `RequestHandler` so it can cross
    /// into the per-session serial-queue closure without a Sendable-capture
    /// warning. The handler is only ever invoked on that serial queue.
    private final class SyncHandlerBox: @unchecked Sendable {
        let handler: RequestHandler
        init(_ handler: @escaping RequestHandler) { self.handler = handler }
    }

    override func stopLoading() {
        lifecycleLock.lock()
        stopped = true
        lifecycleLock.unlock()
        currentBoundSession()?.notifyStopLoading()
    }

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
