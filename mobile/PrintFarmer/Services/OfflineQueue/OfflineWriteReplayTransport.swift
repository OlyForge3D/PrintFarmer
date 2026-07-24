import Foundation

// MARK: - Durable Offline Write Queue — Replay Transport (F10-Q1, #787)
//
// The seam through which a queued item is actually re-sent to the server, and
// the pure classifier that maps a concrete result / `NetworkError` onto an
// `OfflineWriteReplayOutcome`. Keeping the transport behind a protocol lets the
// coordinator be driven by a scripted transport in deterministic tests (explicit
// per-attempt outcomes, no real network), while production replays through the
// same `PartsInventoryServiceProtocol` the #714 view models use — so replay
// reuses the frozen key+body against the identical route, never a parallel path.

protocol OfflineWriteReplayTransport: Sendable {
    /// Re-sends one frozen operation and classifies the result. Never throws —
    /// every terminal/transient condition maps onto an outcome.
    func replay(_ operation: OfflineWriteOperation) async -> OfflineWriteReplayOutcome
}

// MARK: - Classifier

/// Pure mapping from a thrown error to a replay disposition. A canonical
/// success (a returned response, including an idempotent `alreadyHarvested`
/// replay) is `.success` and is handled by the adapter directly; this handles
/// only the error paths.
enum OfflineWriteReplayClassifier {
    static func outcome(for error: Error) -> OfflineWriteReplayOutcome {
        guard let networkError = error as? NetworkError else {
            // Cancellation or an unknown error: treat as transient. Replay is
            // idempotent, so a later retry cannot double-apply.
            return .retryable
        }
        return outcome(for: networkError)
    }

    static func outcome(for error: NetworkError) -> OfflineWriteReplayOutcome {
        switch error {
        // Transient — offline / server-side hiccup. Idempotent replay is safe.
        case .noConnection, .timeout, .serverUnreachable, .transportError,
             .serverError, .invalidResponse, .invalidURL, .staleServerResponse,
             .decodingFailed:
            return .retryable

        // Session/identity lost — stop replaying rather than hammering; the
        // coordinator resumes after a fresh bind (re-auth) instead.
        case .unauthorized, .authFailed:
            return .identityChanged

        // Authorization business rejection — needs operator review.
        case .forbidden:
            return .conflict(OfflineWriteConflict(
                reason: .authorization,
                message: NetworkError.forbidden.errorDescription ?? "Access denied"
            ))

        // Typed harvest conflicts — surface the server's verbatim detail.
        case .partsInventoryConflict(let conflict):
            if conflict.isWrongBin {
                return .conflict(OfflineWriteConflict(
                    reason: .wrongBin,
                    message: conflict.detail ?? conflict.title ?? "Wrong destination bin",
                    partsInventoryConflict: conflict
                ))
            }
            if conflict.isPartMappingRequired {
                return .conflict(OfflineWriteConflict(
                    reason: .mappingRequired,
                    message: conflict.detail ?? conflict.title ?? "Part mapping required",
                    partsInventoryConflict: conflict
                ))
            }
            return .conflict(OfflineWriteConflict(
                reason: .businessConflict,
                message: conflict.detail ?? conflict.title ?? "Printed-parts conflict"
            ))

        // Bare 409 — a genuine business conflict (job-state, etc.).
        case .conflict:
            return .conflict(OfflineWriteConflict(
                reason: .businessConflict,
                message: "The server rejected this action as conflicting with the current state."
            ))

        case .methodNotAllowed:
            return .conflict(OfflineWriteConflict(
                reason: .businessConflict,
                message: NetworkError.methodNotAllowed.errorDescription ?? "Unsupported by server"
            ))

        case .notFound:
            return .conflict(OfflineWriteConflict(
                reason: .businessConflict,
                message: "The target no longer exists on the server."
            ))

        case .featureDisabled(let apiError):
            return .conflict(OfflineWriteConflict(
                reason: .businessConflict,
                message: apiError.detail ?? apiError.title ?? "This feature is disabled on the server."
            ))

        // Remaining 4xx — split validation from throttling from other business.
        case .clientError(let code, let apiError):
            switch code {
            case 408, 425, 429:
                return .retryable
            case 400, 422:
                return .conflict(OfflineWriteConflict(
                    reason: .validation,
                    message: Self.validationMessage(apiError, fallback: "The server rejected this request as invalid.")
                ))
            case 409:
                // A same-key/different-body idempotency conflict is reported
                // with a dedicated code; every other 409 is a plain business
                // conflict.
                if apiError?.code == "idempotencyKeyConflict" {
                    return .conflict(OfflineWriteConflict(
                        reason: .sameKeyDifferentBody,
                        message: apiError?.detail ?? "A different request already used this idempotency key."
                    ))
                }
                return .conflict(OfflineWriteConflict(
                    reason: .businessConflict,
                    message: apiError?.detail ?? apiError?.message ?? "Conflict with current state."
                ))
            default:
                return .conflict(OfflineWriteConflict(
                    reason: .businessConflict,
                    message: apiError?.detail ?? apiError?.message ?? apiError?.title ?? "Client error (\(code))"
                ))
            }

        case .unexpectedStatus(let code):
            return .conflict(OfflineWriteConflict(
                reason: .businessConflict,
                message: "Unexpected server status (\(code))."
            ))
        }
    }

    private static func validationMessage(_ apiError: APIError?, fallback: String) -> String {
        if let detail = apiError?.detail, !detail.isEmpty { return detail }
        if let message = apiError?.message, !message.isEmpty { return message }
        if let title = apiError?.title, !title.isEmpty { return title }
        return fallback
    }

    /// Whether a failure from a first, direct-online attempt is an offline-class
    /// (transient/transport/5xx) failure that a view model should durably
    /// enqueue for later idempotent replay — as opposed to a terminal conflict,
    /// validation, or identity failure that must be surfaced immediately and
    /// NOT queued. Reuses the single classification so the enqueue decision and
    /// the replay decision can never diverge.
    static func isEnqueueableOfflineFailure(_ error: Error) -> Bool {
        if case .retryable = outcome(for: error) { return true }
        return false
    }
}

// MARK: - Enqueue seam (view-model integration)

/// The minimal capability a #714 view model needs to durably hand off a frozen
/// intent (same key + body) to the outbox on an offline-class failure. Kept
/// deliberately narrow so the view models depend on an enqueue seam, not the
/// whole coordinator — and so tests can substitute a recording double.
protocol OfflineWriteEnqueuing: Sendable {
    func enqueue(_ operation: OfflineWriteOperation) async -> OfflineWriteEnqueueResult
}

extension OfflineWriteQueue: OfflineWriteEnqueuing {}

// MARK: - Production adapter

/// Replays through the same `PartsInventoryServiceProtocol` the online path
/// uses. A returned response (including an idempotent `alreadyHarvested` harvest
/// replay) is `.success`; any thrown error is classified.
struct PartsInventoryOfflineReplayTransport: OfflineWriteReplayTransport {
    let service: any PartsInventoryServiceProtocol

    init(service: any PartsInventoryServiceProtocol) {
        self.service = service
    }

    func replay(_ operation: OfflineWriteOperation) async -> OfflineWriteReplayOutcome {
        await OfflineWriteReplayExecutor.execute(operation, using: service)
    }
}

/// Production adapter that resolves the CURRENT parts-inventory service at
/// replay time (on the main actor). A server switch replaces the container's
/// service instance; resolving lazily means a replay always uses the client for
/// the currently-bound identity rather than a captured, possibly-stale one. If
/// the provider yields `nil` (container gone), the attempt is treated as
/// transient so nothing is falsely marked applied.
struct DynamicPartsInventoryReplayTransport: OfflineWriteReplayTransport {
    let provider: @Sendable @MainActor () -> (any PartsInventoryServiceProtocol)?

    func replay(_ operation: OfflineWriteOperation) async -> OfflineWriteReplayOutcome {
        guard let service = await provider() else { return .retryable }
        return await OfflineWriteReplayExecutor.execute(operation, using: service)
    }
}

/// Shared execution + classification so both production adapters behave
/// identically.
enum OfflineWriteReplayExecutor {
    static func execute(
        _ operation: OfflineWriteOperation,
        using service: any PartsInventoryServiceProtocol
    ) async -> OfflineWriteReplayOutcome {
        do {
            switch operation {
            case .partAdjustment(let sku, let request):
                _ = try await service.adjustPart(sku: sku, request: request)
            case .harvest(let jobId, let request):
                _ = try await service.harvestJob(jobId: jobId, request: request)
            }
            return .success
        } catch {
            return OfflineWriteReplayClassifier.outcome(for: error)
        }
    }
}
