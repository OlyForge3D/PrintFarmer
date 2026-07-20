import Foundation
@testable import PrintFarmer

/// Captures API requests for assertion without hitting the network.
/// Uses MockURLProtocol under the hood for real APIClient testing.
final class MockAPIClient: @unchecked Sendable {

    /// Recorded request info for assertions.
    struct CapturedRequest: Sendable {
        let path: String
        let method: String
        let body: Data?
        let headers: [String: String]
    }

    private(set) var capturedRequests: [CapturedRequest] = []
    var responsesToReturn: [String: (Int, Data)] = [:]
    var errorToThrow: Error?

    /// Creates a real APIClient backed by MockURLProtocol.
    static func makeAPIClient(baseURL: URL = TestData.testBaseURL) -> APIClient {
        let session = MockURLProtocol.mockSession()
        return APIClient(baseURL: baseURL, session: session)
    }

    /// Configures MockURLProtocol to return a JSON response for any request.
    static func stubResponse(json: String, statusCode: Int = 200) {
        MockURLProtocol.requestHandler = { request in
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
    static func stubResponses(_ responses: [String: (statusCode: Int, json: String)]) {
        MockURLProtocol.requestHandler = { request in
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

    /// Configures MockURLProtocol to return an error.
    static func stubError(_ error: URLError.Code) {
        MockURLProtocol.requestHandler = { _ in
            throw URLError(error)
        }
    }

    /// Configures MockURLProtocol to return empty success.
    static func stubEmptySuccess() {
        MockURLProtocol.requestHandler = { request in
            let response = TestData.httpResponse(url: request.url, statusCode: 200)
            return (response, Data())
        }
    }
}
