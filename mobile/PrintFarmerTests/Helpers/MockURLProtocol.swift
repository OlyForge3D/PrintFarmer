import Foundation
import XCTest

/// Intercepts requests for one mock URLSession without sharing handlers or
/// captured requests with concurrently running tests.
final class MockURLProtocol: URLProtocol {

    typealias RequestHandler = (URLRequest) throws -> (HTTPURLResponse, Data)

    final class Session: @unchecked Sendable {
        private let identifier = UUID().uuidString
        private let lock = NSLock()
        private var handler: RequestHandler?
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
            requests = []
            lock.unlock()
        }

        fileprivate func capture(_ request: URLRequest) -> RequestHandler? {
            lock.lock()
            defer { lock.unlock() }
            requests.append(request)
            return handler
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
              let session = Self.registeredSession(identifier: identifier),
              let handler = session.capture(request) else {
            client?.urlProtocol(self, didFailWithError: URLError(.unknown))
            return
        }

        do {
            let (response, data) = try handler(request)
            client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
            client?.urlProtocol(self, didLoad: data)
            client?.urlProtocolDidFinishLoading(self)
        } catch {
            client?.urlProtocol(self, didFailWithError: error)
        }
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
