import Foundation
import XCTest
@testable import PrintFarmer

@MainActor
final class BackendReadinessDiagnosticsTests: XCTestCase {
    func testHTTPStatusFailureKindUsesCamelCaseDiagnosticValue() {
        XCTAssertEqual(BackendServiceFailureKind.httpStatus.rawValue, "httpStatus")
    }

    func testAPIHealthProbePreservesTransportFailure() async throws {
        let mockAPIClient = MockAPIClient()
        mockAPIClient.stubError(.cannotConnectToHost)
        let apiClient = mockAPIClient.apiClient
        let diagnostics = ReadinessDiagnosticRecorder()
        let plan = BackendReadinessPlan(
            capabilitiesService: DiagnosticCapabilitiesService(),
            probes: [
                BackendReadinessProbe(endpoint: .api) {
                    try await apiClient.checkReachability()
                },
            ]
        )

        let result = await BackendReadinessChecker(
            diagnosticRecorder: diagnostics.record
        ).check(plan: plan)

        let failure = try XCTUnwrap(result.failures.first)
        XCTAssertEqual(failure.endpoint, .api)
        XCTAssertEqual(failure.kind, .transport)
        XCTAssertTrue(failure.diagnosticDetail.contains("-1004"))
        let diagnostic = diagnostics.snapshot().first
        XCTAssertEqual(diagnostic?.failureKind, .transport)
        XCTAssertEqual(diagnostic?.detail, failure.diagnosticDetail)
    }

    func testAPIHealthProbePreservesHTTPFailure() async throws {
        let mockAPIClient = MockAPIClient()
        mockAPIClient.stubResponse(json: "{}", statusCode: 503)
        let apiClient = mockAPIClient.apiClient
        let diagnostics = ReadinessDiagnosticRecorder()
        let plan = BackendReadinessPlan(
            capabilitiesService: DiagnosticCapabilitiesService(),
            probes: [
                BackendReadinessProbe(endpoint: .api) {
                    try await apiClient.checkReachability()
                },
            ]
        )

        let result = await BackendReadinessChecker(
            diagnosticRecorder: diagnostics.record
        ).check(plan: plan)

        let failure = try XCTUnwrap(result.failures.first)
        XCTAssertEqual(failure.endpoint, .api)
        XCTAssertEqual(failure.kind, .httpStatus)
        XCTAssertEqual(failure.diagnosticDetail, "HTTP 503")
        let diagnostic = diagnostics.snapshot().first
        XCTAssertEqual(diagnostic?.failureKind, .httpStatus)
        XCTAssertEqual(diagnostic?.detail, "HTTP 503")
    }

    func testTransportFailurePreservesCodeWithoutLoggingEndpointData() async throws {
        let diagnostics = ReadinessDiagnosticRecorder()
        let sensitiveURL = URL(string: "https://private.example.invalid/api/internal/readiness")!
        let transportError = URLError(
            .cannotConnectToHost,
            userInfo: [NSURLErrorFailingURLErrorKey: sensitiveURL]
        )
        let plan = BackendReadinessPlan(
            capabilitiesService: DiagnosticCapabilitiesService(),
            probes: [
                BackendReadinessProbe(endpoint: .printers) {
                    throw NetworkError.transportError(transportError)
                },
            ]
        )

        let result = await BackendReadinessChecker(
            diagnosticRecorder: diagnostics.record
        ).check(plan: plan)

        let failure = try XCTUnwrap(result.failures.first)
        XCTAssertEqual(failure.kind, .transport)
        XCTAssertTrue(failure.diagnosticDetail.contains("-1004"))
        XCTAssertFalse(failure.diagnosticDetail.contains("private.example.invalid"))
        XCTAssertFalse(failure.diagnosticDetail.contains("/api/internal/readiness"))
        let diagnostic = diagnostics.snapshot().first { $0.endpoint == .printers }
        XCTAssertEqual(diagnostic?.failureKind, .transport)
        XCTAssertEqual(diagnostic?.detail, failure.diagnosticDetail)
    }

    func testHTTPFailureDropsAuthenticationDetail() async throws {
        let diagnostics = ReadinessDiagnosticRecorder()
        let plan = BackendReadinessPlan(
            capabilitiesService: DiagnosticCapabilitiesService(),
            probes: [
                BackendReadinessProbe(endpoint: .jobs) {
                    throw NetworkError.authFailed("Authorization details must not be logged")
                },
            ]
        )

        let result = await BackendReadinessChecker(
            diagnosticRecorder: diagnostics.record
        ).check(plan: plan)

        let failure = try XCTUnwrap(result.failures.first)
        XCTAssertEqual(failure.kind, .httpStatus)
        XCTAssertEqual(failure.diagnosticDetail, "HTTP 401")
        XCTAssertFalse(failure.diagnosticDetail.contains("Authorization"))
        let diagnostic = diagnostics.snapshot().first { $0.endpoint == .jobs }
        XCTAssertEqual(diagnostic?.failureKind, .httpStatus)
        XCTAssertEqual(diagnostic?.detail, "HTTP 401")
    }

    func testDecodeFailureDropsRawPayloadDetail() async throws {
        let diagnostics = ReadinessDiagnosticRecorder()
        let decodingError = DecodingError.typeMismatch(
            String.self,
            DecodingError.Context(
                codingPath: [],
                debugDescription: "raw payload detail must not be logged"
            )
        )
        let decodingFailure = ResponseDecodingFailure(
            error: decodingError,
            targetType: ReadinessDecodePayload.self
        )
        let plan = BackendReadinessPlan(
            capabilitiesService: DiagnosticCapabilitiesService(),
            probes: [
                BackendReadinessProbe(endpoint: .locations) {
                    throw NetworkError.decodingFailed(decodingFailure)
                },
            ]
        )

        let result = await BackendReadinessChecker(
            diagnosticRecorder: diagnostics.record
        ).check(plan: plan)

        let failure = try XCTUnwrap(result.failures.first)
        XCTAssertEqual(failure.kind, .decode)
        XCTAssertTrue(failure.diagnosticDetail.contains("ReadinessDecodePayload"))
        XCTAssertTrue(failure.diagnosticDetail.contains("typeMismatch"))
        XCTAssertFalse(failure.diagnosticDetail.contains("raw payload detail"))
        let diagnostic = diagnostics.snapshot().first { $0.endpoint == .locations }
        XCTAssertEqual(diagnostic?.failureKind, .decode)
        XCTAssertEqual(diagnostic?.detail, failure.diagnosticDetail)
    }

    func testCapabilitiesFailurePreservesDecodeDiagnostics() async throws {
        let classification = BackendReadinessFailureClassification(
            kind: .decode,
            diagnosticDetail: "SystemCapabilities: keyNotFound at featureFlags",
            userDetail: "The server returned data this app could not read."
        )
        let diagnostics = ReadinessDiagnosticRecorder()
        let plan = BackendReadinessPlan(
            capabilitiesService: DiagnosticCapabilitiesService(
                outcome: .failedWithDiagnostics(classification)
            ),
            probes: []
        )

        let result = await BackendReadinessChecker(
            diagnosticRecorder: diagnostics.record
        ).check(plan: plan)

        let failure = try XCTUnwrap(result.failures.first)
        XCTAssertEqual(failure.endpoint, .systemCapabilities)
        XCTAssertEqual(failure.kind, .decode)
        XCTAssertEqual(failure.diagnosticDetail, classification.diagnosticDetail)
        let diagnostic = diagnostics.snapshot().first
        XCTAssertEqual(diagnostic?.failureKind, .decode)
        XCTAssertEqual(diagnostic?.detail, classification.diagnosticDetail)
    }
}

@MainActor
private final class DiagnosticCapabilitiesService: SystemCapabilitiesServiceProtocol, @unchecked Sendable {
    private(set) var resolved = ResolvedSystemCapabilities.defaults
    private let outcome: SystemCapabilitiesRefreshOutcome

    init(outcome: SystemCapabilitiesRefreshOutcome = .loaded) {
        self.outcome = outcome
    }

    @discardableResult
    func refresh() async -> SystemCapabilitiesRefreshOutcome {
        outcome
    }
}

private final class ReadinessDiagnosticRecorder: @unchecked Sendable {
    private let lock = NSLock()
    private var diagnostics: [BackendReadinessProbeDiagnostic] = []

    func record(_ diagnostic: BackendReadinessProbeDiagnostic) {
        lock.withLock {
            diagnostics.append(diagnostic)
        }
    }

    func snapshot() -> [BackendReadinessProbeDiagnostic] {
        lock.withLock { diagnostics }
    }
}

private struct ReadinessDecodePayload: Decodable {}
