import Foundation
@testable import PrintFarmer

/// Captures API requests for assertion without hitting the network.
/// Uses MockURLProtocol under the hood for real APIClient testing.
final class MockAPIClient: @unchecked Sendable {

    private let transport: MockURLProtocol.Session
    let apiClient: APIClient

    init(baseURL: URL = TestData.testBaseURL) {
        let transport = MockURLProtocol.makeSession()
        self.transport = transport
        apiClient = APIClient(baseURL: baseURL, session: transport.urlSession)
    }

    var capturedRequests: [URLRequest] {
        transport.capturedRequests
    }

    var urlSession: URLSession {
        transport.urlSession
    }

    var requestHandler: MockURLProtocol.RequestHandler? {
        get { transport.requestHandler }
        set { transport.requestHandler = newValue }
    }

    func reset() {
        transport.reset()
    }

    func stubResponse(json: String, statusCode: Int = 200) {
        requestHandler = { request in
            let response = TestData.httpResponse(url: request.url, statusCode: statusCode)
            return (response, json.data(using: .utf8)!)
        }
    }

    /// Configures MockURLProtocol to return different responses per path.
    ///
    /// Keys are matched against `request.url.path` via substring containment.
    /// When multiple keys match a single path (e.g. `/api/printers/{id}` and
    /// `backend-capabilities` both matching `/api/printers/{id}/backend-capabilities`),
    /// the key whose match starts **latest** in the URL wins so a more specific
    /// suffix beats a generic prefix. Ties on start position resolve to the longer
    /// key. This makes dispatch deterministic regardless of `Dictionary` iteration
    /// order (which is not stable across processes).
    func stubResponses(_ responses: [String: (statusCode: Int, json: String)]) {
        requestHandler = { request in
            let path = request.url?.path ?? ""
            let matches: [(key: String, start: Int)] = responses.keys.compactMap { key in
                guard let range = path.range(of: key) else { return nil }
                return (key, path.distance(from: path.startIndex, to: range.lowerBound))
            }
            let best = matches.max { lhs, rhs in
                lhs.start != rhs.start
                    ? lhs.start < rhs.start
                    : lhs.key.count < rhs.key.count
            }
            if let match = best, let value = responses[match.key] {
                let response = TestData.httpResponse(url: request.url, statusCode: value.statusCode)
                return (response, Data(value.json.utf8))
            }
            let response = TestData.httpResponse(url: request.url, statusCode: 404)
            return (response, Data("{}".utf8))
        }
    }

    func stubError(_ error: URLError.Code) {
        requestHandler = { _ in
            throw URLError(error)
        }
    }

    func stubEmptySuccess() {
        requestHandler = { request in
            let response = TestData.httpResponse(url: request.url, statusCode: 200)
            return (response, Data())
        }
    }
}
