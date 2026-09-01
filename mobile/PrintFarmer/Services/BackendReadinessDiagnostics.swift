import Foundation
import OSLog

enum BackendServiceFailureKind: String, Equatable, Sendable {
    case timeout
    case transport
    case httpStatus
    case decode
}

struct BackendServiceFailure: Identifiable, Equatable, Sendable {
    let endpoint: BackendServiceEndpoint
    let kind: BackendServiceFailureKind
    let elapsed: Duration
    let diagnosticDetail: String
    let userDetail: String

    var id: BackendServiceEndpoint { endpoint }
    var displayName: String { endpoint.displayName }
    var userDescription: String {
        String(
            localized: "\(displayName): \(userDetail)",
            comment: "Backend readiness alert line containing a capability name and its failure detail."
        )
    }
}

enum BackendReadinessProbeDiagnosticOutcome: String, Equatable, Sendable {
    case succeeded
    case unsupported
    case failed
    case cancelled
}

struct BackendReadinessProbeDiagnostic: Equatable, Sendable {
    let endpoint: BackendServiceEndpoint
    let elapsed: Duration
    let outcome: BackendReadinessProbeDiagnosticOutcome
    let failureKind: BackendServiceFailureKind?
    let detail: String?
}

struct BackendReadinessFailureClassification: Equatable, Sendable {
    let kind: BackendServiceFailureKind
    let diagnosticDetail: String
    let userDetail: String
}

enum BackendReadinessDiagnostics {
    private static let logger = Logger(
        subsystem: "com.printfarmer.ios",
        category: "BackendReadiness"
    )

    static func record(_ diagnostic: BackendReadinessProbeDiagnostic) {
        let capability = diagnostic.endpoint.displayName
        let elapsedMilliseconds = Self.elapsedMilliseconds(diagnostic.elapsed)

        if let failureKind = diagnostic.failureKind,
           let detail = diagnostic.detail {
            logger.warning(
                """
                Backend readiness capability=\(capability, privacy: .public) \
                elapsedMs=\(elapsedMilliseconds, privacy: .public) \
                outcome=\(diagnostic.outcome.rawValue, privacy: .public) \
                kind=\(failureKind.rawValue, privacy: .public) \
                detail=\(detail, privacy: .public)
                """
            )
            return
        }

        logger.info(
            """
            Backend readiness capability=\(capability, privacy: .public) \
            elapsedMs=\(elapsedMilliseconds, privacy: .public) \
            outcome=\(diagnostic.outcome.rawValue, privacy: .public)
            """
        )
    }

    static func makeFailure(
        endpoint: BackendServiceEndpoint,
        classification: BackendReadinessFailureClassification,
        elapsed: Duration
    ) -> BackendServiceFailure {
        BackendServiceFailure(
            endpoint: endpoint,
            kind: classification.kind,
            elapsed: elapsed,
            diagnosticDetail: classification.diagnosticDetail,
            userDetail: classification.userDetail
        )
    }

    static func timeoutClassification(limit: Duration) -> BackendReadinessFailureClassification {
        let diagnosticLimitDescription = diagnosticDurationDescription(limit)
        let localizedLimitDescription = localizedDurationDescription(limit)
        return BackendReadinessFailureClassification(
            kind: .timeout,
            diagnosticDetail: "readiness timeout budget \(diagnosticLimitDescription)",
            userDetail: String(
                localized: "Responding slowly; the readiness check exceeded \(localizedLimitDescription).",
                comment: "Backend readiness failure detail for a capability that exceeded its timeout."
            )
        )
    }

    static func classify(_ error: Error) -> BackendReadinessFailureClassification {
        if error is BackendReadinessProbeError {
            return transportClassification(detail: "reachability check failed")
        }

        if let networkError = error as? NetworkError {
            return classify(networkError)
        }
        if let urlError = error as? URLError {
            return classify(urlError)
        }
        if error is DecodingError {
            return decodeClassification(
                ResponseDecodingFailure(error: error, targetType: Any.self)
            )
        }
        return transportClassification(
            detail: "error type \(String(describing: type(of: error)))"
        )
    }

    private static func classify(
        _ error: NetworkError
    ) -> BackendReadinessFailureClassification {
        if let statusCode = httpStatusCode(for: error) {
            return httpClassification(statusCode: statusCode)
        }

        switch error {
        case .timeout:
            return timeoutErrorClassification()
        case .transportError(let urlError):
            return classify(urlError)
        case .decodingFailed(let failure):
            return decodeClassification(failure)
        case .invalidResponse:
            return decodeClassification(detail: "invalid server response")
        case .noConnection:
            return transportClassification(detail: "no internet connection")
        case .serverUnreachable:
            return transportClassification(detail: "server unreachable")
        case .invalidURL:
            return transportClassification(detail: "invalid URL configuration")
        case .staleServerResponse:
            return transportClassification(detail: "stale server response")
        case .insecureTransportBlocked:
            return transportClassification(detail: "insecure transport blocked")
        case .certificateChanged:
            return transportClassification(detail: "server certificate changed")
        case .certificateNotTrusted:
            return transportClassification(detail: "server certificate not trusted")
        case .unauthorized, .authFailed, .forbidden, .notFound, .featureDisabled,
             .methodNotAllowed, .conflict, .partsInventoryConflict,
             .preconditionFailed, .preconditionRequired, .clientError,
             .serverError, .unexpectedStatus:
            return transportClassification(detail: "unclassified HTTP failure")
        }
    }

    private static func httpStatusCode(for error: NetworkError) -> Int? {
        switch error {
        case .unauthorized, .authFailed:
            401
        case .forbidden:
            403
        case .notFound, .featureDisabled:
            404
        case .methodNotAllowed:
            405
        case .conflict, .partsInventoryConflict:
            409
        case .preconditionFailed:
            412
        case .preconditionRequired:
            428
        case .clientError(let statusCode, _),
             .serverError(let statusCode),
             .unexpectedStatus(let statusCode):
            statusCode
        case .invalidURL, .invalidResponse, .noConnection, .timeout,
             .serverUnreachable, .decodingFailed, .transportError,
             .staleServerResponse, .insecureTransportBlocked,
             .certificateChanged, .certificateNotTrusted:
            nil
        }
    }

    private static func classify(
        _ error: URLError
    ) -> BackendReadinessFailureClassification {
        if error.code == .timedOut {
            return timeoutErrorClassification()
        }
        return transportClassification(
            detail: "URL error \(error.code.rawValue) (\(String(describing: error.code)))"
        )
    }

    private static func timeoutErrorClassification() -> BackendReadinessFailureClassification {
        BackendReadinessFailureClassification(
            kind: .timeout,
            diagnosticDetail: "request timed out",
            userDetail: String(
                localized: "Responding slowly; the request timed out.",
                comment: "Backend readiness failure detail for a request-level timeout."
            )
        )
    }

    private static func transportClassification(
        detail: String
    ) -> BackendReadinessFailureClassification {
        BackendReadinessFailureClassification(
            kind: .transport,
            diagnosticDetail: detail,
            userDetail: String(
                localized: "Could not connect. Check the network and server.",
                comment: "Backend readiness failure detail for a transport connection failure."
            )
        )
    }

    private static func httpClassification(
        statusCode: Int
    ) -> BackendReadinessFailureClassification {
        BackendReadinessFailureClassification(
            kind: .httpStatus,
            diagnosticDetail: "HTTP \(statusCode)",
            userDetail: String(
                localized: "The server returned HTTP \(statusCode).",
                comment: "Backend readiness failure detail for a non-success HTTP response."
            )
        )
    }

    private static func decodeClassification(
        _ failure: ResponseDecodingFailure
    ) -> BackendReadinessFailureClassification {
        decodeClassification(
            detail: "\(failure.targetType): \(failure.kind) at \(failure.codingPath); expected \(failure.expectedType)"
        )
    }

    private static func decodeClassification(
        detail: String
    ) -> BackendReadinessFailureClassification {
        BackendReadinessFailureClassification(
            kind: .decode,
            diagnosticDetail: detail,
            userDetail: String(
                localized: "The server returned data this app could not read. Update the server if this continues.",
                comment: "Backend readiness failure detail for an incompatible response payload."
            )
        )
    }

    private static func diagnosticDurationDescription(_ duration: Duration) -> String {
        let components = duration.components
        let seconds = Double(components.seconds)
            + (Double(components.attoseconds) / 1_000_000_000_000_000_000)
        return String(format: "%.1f seconds", locale: Locale(identifier: "en_US_POSIX"), seconds)
    }

    private static func localizedDurationDescription(_ duration: Duration) -> String {
        duration.formatted(.units(allowed: [.seconds], width: .wide))
    }

    private static func elapsedMilliseconds(_ duration: Duration) -> Int64 {
        let components = duration.components
        let milliseconds = (Double(components.seconds) * 1_000)
            + (Double(components.attoseconds) / 1_000_000_000_000_000)
        return Int64(max(milliseconds.rounded(), 0))
    }
}
