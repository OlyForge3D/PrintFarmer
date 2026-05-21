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
    static func stubResponses(_ responses: [String: (statusCode: Int, json: String)]) {
        MockURLProtocol.requestHandler = { request in
            let path = request.url?.path ?? ""
            if let match = responses.first(where: { path.contains($0.key) }) {
                let response = TestData.httpResponse(url: request.url, statusCode: match.value.statusCode)
                return (response, Data(match.value.json.utf8))
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
