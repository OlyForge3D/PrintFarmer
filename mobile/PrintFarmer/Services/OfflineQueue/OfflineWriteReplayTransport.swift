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
    func replay(
        _ operation: OfflineWriteOperation,
        expectedBinding: OfflineWriteReplayBinding
    ) async -> OfflineWriteReplayOutcome
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
             .decodingFailed, .insecureTransportBlocked, .certificateChanged,
             .certificateNotTrusted:
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

        case .preconditionFailed(let apiError), .preconditionRequired(let apiError):
            return .conflict(OfflineWriteConflict(
                reason: .staleState,
                message: apiError?.detail
                    ?? error.errorDescription
                    ?? "The server requires a refreshed revision before this action can be retried."
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

/// The set of canonical services a replay may need, resolved at attempt time on
/// the main actor. Each is optional: a `nil` (container gone / not yet wired)
/// yields a transient `.retryable` so nothing is ever falsely marked applied.
/// Parts replays use `parts`; task-complete uses `tasks`; toolhead-bind uses
/// `printers`. One bundle keeps the four allowlisted kinds behind a single
/// replay owner (F10-Q2, #790) — no parallel queue/transport per kind.
struct OfflineReplayServices {
    let identity: OfflineWriteReplayIdentity?
    let parts: (any PartsInventoryServiceProtocol)?
    let tasks: (any ShiftTaskServiceProtocol)?
    let printers: (any PrinterServiceProtocol)?

    init(
        identity: OfflineWriteReplayIdentity? = nil,
        parts: (any PartsInventoryServiceProtocol)? = nil,
        tasks: (any ShiftTaskServiceProtocol)? = nil,
        printers: (any PrinterServiceProtocol)? = nil
    ) {
        self.identity = identity
        self.parts = parts
        self.tasks = tasks
        self.printers = printers
    }
}

/// Production adapter that resolves the CURRENT services at replay time (on the
/// main actor). A server switch replaces the container's service instances;
/// resolving lazily means a replay always uses the clients for the
/// currently-bound identity rather than captured, possibly-stale ones.
struct DynamicOfflineReplayTransport: OfflineWriteReplayTransport {
    let replayAuthority: OfflineWriteReplayAuthority
    let beforeProviderResolution: @Sendable () async -> Void
    let beforeServiceDispatch: @Sendable () async -> Void
    let provider: @Sendable @MainActor () -> OfflineReplayServices

    init(
        replayAuthority: OfflineWriteReplayAuthority,
        beforeProviderResolution: @escaping @Sendable () async -> Void = {},
        beforeServiceDispatch: @escaping @Sendable () async -> Void = {},
        provider: @escaping @Sendable @MainActor () -> OfflineReplayServices
    ) {
        self.replayAuthority = replayAuthority
        self.beforeProviderResolution = beforeProviderResolution
        self.beforeServiceDispatch = beforeServiceDispatch
        self.provider = provider
    }

    func replay(
        _ operation: OfflineWriteOperation,
        expectedBinding: OfflineWriteReplayBinding
    ) async -> OfflineWriteReplayOutcome {
        await beforeProviderResolution()
        let services = await provider()
        guard services.identity == expectedBinding.identity,
              replayAuthority.isCurrent(expectedBinding) else {
            return .retryable
        }
        let dispatchFence = OfflineReplayDispatchFence(
            authority: replayAuthority,
            binding: expectedBinding,
            beforeDispatch: beforeServiceDispatch
        )
        let outcome = await OfflineWriteReplayExecutor.execute(
            operation,
            using: services,
            dispatchFence: dispatchFence
        )
        let servicesAfterReplay = await provider()
        guard servicesAfterReplay.identity == expectedBinding.identity,
              replayAuthority.isCurrent(expectedBinding) else {
            return .retryable
        }
        return outcome
    }
}

fileprivate struct OfflineReplayDispatchFence: Sendable {
    let authority: OfflineWriteReplayAuthority
    let binding: OfflineWriteReplayBinding
    let beforeDispatch: @Sendable () async -> Void

    func acquirePermit() async -> OfflineWriteReplayDispatchPermit? {
        await beforeDispatch()
        return authority.beginDispatch(binding)
    }
}

/// Shared execution + classification. Each allowlisted kind re-sends the frozen
/// key+body against its canonical route. Task-complete and toolhead-bind first
/// obtain canonical state (#711/#710) so a replay after a gap applies exactly
/// once and NEVER overwrites a newer binding or completes an incompatible
/// terminal task — a changed state is surfaced for review with zero mutation.
enum OfflineWriteReplayExecutor {
    static func execute(
        _ operation: OfflineWriteOperation,
        using services: OfflineReplayServices
    ) async -> OfflineWriteReplayOutcome {
        await execute(operation, using: services, dispatchFence: nil)
    }

    fileprivate static func execute(
        _ operation: OfflineWriteOperation,
        using services: OfflineReplayServices,
        dispatchFence: OfflineReplayDispatchFence?
    ) async -> OfflineWriteReplayOutcome {
        switch operation {
        case .partAdjustment, .harvest:
            guard let parts = services.parts else { return .retryable }
            return await executeParts(
                operation,
                using: parts,
                dispatchFence: dispatchFence
            )
        case .taskComplete(let taskID, let idempotencyKey):
            guard let tasks = services.tasks else { return .retryable }
            return await executeTaskComplete(
                taskID: taskID,
                idempotencyKey: idempotencyKey,
                using: tasks,
                dispatchFence: dispatchFence
            )
        case .toolheadBind(let printerID, let toolheadIndex, let idempotencyKey, let request, let expectedPriorSpoolId):
            guard let printers = services.printers else { return .retryable }
            return await executeToolheadBind(
                printerID: printerID,
                toolheadIndex: toolheadIndex,
                idempotencyKey: idempotencyKey,
                request: request,
                expectedPriorSpoolId: expectedPriorSpoolId,
                using: printers,
                dispatchFence: dispatchFence
            )
        }
    }

    /// Parts-inventory replay (part adjustment / harvest, #787). A returned
    /// response (including an idempotent `alreadyHarvested` replay) is
    /// `.success`; any thrown error is classified.
    static func execute(
        _ operation: OfflineWriteOperation,
        using service: any PartsInventoryServiceProtocol
    ) async -> OfflineWriteReplayOutcome {
        await executeParts(operation, using: service)
    }

    private static func executeParts(
        _ operation: OfflineWriteOperation,
        using service: any PartsInventoryServiceProtocol,
        dispatchFence: OfflineReplayDispatchFence? = nil
    ) async -> OfflineWriteReplayOutcome {
        do {
            switch operation {
            case .partAdjustment(let sku, let request):
                guard await acquirePermit(dispatchFence) else { return .retryable }
                _ = try await service.adjustPart(sku: sku, request: request)
            case .harvest(let jobId, let request):
                guard await acquirePermit(dispatchFence) else { return .retryable }
                _ = try await service.harvestJob(jobId: jobId, request: request)
            case .taskComplete, .toolheadBind:
                // Not a parts operation — never routed here by the bundle
                // dispatcher. Treated as transient so it is never falsely
                // marked applied by the parts-only adapter.
                return .retryable
            }
            return .success
        } catch {
            return OfflineWriteReplayClassifier.outcome(for: error)
        }
    }

    /// Task-complete replay. Canonical task state decides the disposition:
    /// already-terminal `completed` = one completion (no re-POST); still
    /// applicable (`pending`/`inProgress`) replays the frozen key; an
    /// incompatible terminal state (`skipped`/`dismissed`/unrecognized) or a
    /// missing task is surfaced for review WITHOUT any write. Skip/dismiss are
    /// never encoded, so this only completes.
    private static func executeTaskComplete(
        taskID: String,
        idempotencyKey: String,
        using service: any ShiftTaskServiceProtocol,
        dispatchFence: OfflineReplayDispatchFence?
    ) async -> OfflineWriteReplayOutcome {
        do {
            guard await acquirePermit(dispatchFence) else { return .retryable }
            let snapshot = try await service.loadSnapshot(shiftPlanEnabled: true)
            let tasks = snapshot.groups.flatMap { $0.tasks }
            guard let task = tasks.first(where: { $0.id == taskID }) else {
                return .conflict(OfflineWriteConflict(
                    reason: .unavailable,
                    message: "This task no longer exists on the server."
                ))
            }
            switch task.status {
            case .completed:
                // Canonical idempotent success: exactly one completion.
                return .success
            case .pending, .inProgress:
                guard await acquirePermit(dispatchFence) else { return .retryable }
                try await service.complete(taskID: taskID, idempotencyKey: idempotencyKey)
                return .success
            case .skipped, .dismissed:
                return .conflict(OfflineWriteConflict(
                    reason: .staleState,
                    message: "This task was \(task.status.wireValue.lowercased()) since it was queued."
                ))
            case .unknown(let raw):
                return .conflict(OfflineWriteConflict(
                    reason: .staleState,
                    message: "This task is in an unrecognized state (\(raw)) and cannot be completed automatically."
                ))
            }
        } catch {
            return OfflineWriteReplayClassifier.outcome(for: error)
        }
    }

    /// Toolhead-bind replay. Canonical toolhead state (#711) decides: already
    /// bound to the target = idempotent `.success` with ZERO mutation; the
    /// expected prior binding still present replays the frozen body; a missing
    /// toolhead or a DIFFERENT (newer) binding is surfaced for review WITHOUT
    /// overwriting.
    private static func executeToolheadBind(
        printerID: UUID,
        toolheadIndex: Int,
        idempotencyKey: String,
        request: ToolheadSpoolBindRequest,
        expectedPriorSpoolId: Int?,
        using service: any PrinterServiceProtocol,
        dispatchFence: OfflineReplayDispatchFence?
    ) async -> OfflineWriteReplayOutcome {
        do {
            guard await acquirePermit(dispatchFence) else { return .retryable }
            let details = try await service.getDetails(id: printerID)
            guard let toolhead = details.toolheads.first(where: { $0.index == toolheadIndex }) else {
                return .conflict(OfflineWriteConflict(
                    reason: .unavailable,
                    message: "This toolhead no longer exists on the printer."
                ))
            }
            if toolhead.currentSpoolId == request.spoolId {
                // Already bound to the intended spool — idempotent, no write.
                return .success
            }
            guard toolhead.currentSpoolId == expectedPriorSpoolId else {
                // A newer/different binding is present. Never overwrite.
                return .conflict(OfflineWriteConflict(
                    reason: .staleState,
                    message: "This toolhead's spool changed since the bind was queued."
                ))
            }
            guard await acquirePermit(dispatchFence) else { return .retryable }
            _ = try await service.bindToolheadSpool(
                printerId: printerID,
                toolheadIndex: toolheadIndex,
                request: request,
                idempotencyKey: idempotencyKey
            )
            return .success
        } catch {
            return OfflineWriteReplayClassifier.outcome(for: error)
        }
    }

    private static func acquirePermit(
        _ dispatchFence: OfflineReplayDispatchFence?
    ) async -> Bool {
        guard let dispatchFence else { return true }
        return await dispatchFence.acquirePermit() != nil
    }
}
