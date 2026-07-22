import Foundation
@testable import PrintFarmer

// MARK: - Errors

enum AttentionProofError: LocalizedError, Sendable, Equatable {
    case forced(String)
    case network(String)

    var errorDescription: String? {
        switch self {
        case .forced(let message), .network(let message): message
        }
    }
}

// MARK: - Result gate

/// Awaitable one-shot barrier that lets a test hold a service call
/// mid-flight and later resolve it as success or failure. Modelled on
/// `ShiftTaskResultGate` from the shift-task suite. Deterministic — no
/// polling or fixed sleeps.
actor AttentionResultGate<Value: Sendable> {
    private var result: Result<Value, AttentionProofError>?
    private var continuation: CheckedContinuation<
        Result<Value, AttentionProofError>,
        Never
    >?

    func wait() async throws -> Value {
        let resolved: Result<Value, AttentionProofError> = await withCheckedContinuation { continuation in
            if let result {
                continuation.resume(returning: result)
            } else {
                self.continuation = continuation
            }
        }
        return try resolved.get()
    }

    func succeed(_ value: Value) {
        resolve(.success(value))
    }

    func fail(_ error: AttentionProofError) {
        resolve(.failure(error))
    }

    private func resolve(_ result: Result<Value, AttentionProofError>) {
        precondition(self.result == nil, "AttentionResultGate resolved twice")
        self.result = result
        continuation?.resume(returning: result)
        continuation = nil
    }
}

// MARK: - Load step

/// Explicit step a scripted `getFeed` call should take. The service
/// consumes one step per call. When steps are exhausted, further calls
/// return the configured `defaultFeed`.
enum AttentionLoadStep: Sendable {
    /// Return the given feed synchronously.
    case value(AttentionFeed)
    /// Throw the given error synchronously.
    case failure(AttentionProofError)
    /// Throw `NetworkError.featureDisabled` synchronously.
    case featureDisabled
    /// Hold the call open until the gate resolves.
    case gated(AttentionResultGate<AttentionFeed>)
}

/// One recorded call for later assertion.
struct AttentionLoadCall: Equatable, Sendable {
    let cursor: String?
    let limit: Int?
}

// MARK: - Scripted service

/// Deterministic `AttentionServiceProtocol` double. Each `getFeed` call
/// records its arguments and pops the next scripted step.
///
/// Instances are actor-isolated because tests call them from arbitrary
/// task contexts; the view model's own MainActor isolation keeps its
/// observed state safe.
actor ScriptedAttentionService: AttentionServiceProtocol {
    private var steps: [AttentionLoadStep]
    private let defaultFeed: AttentionFeed

    private(set) var loadCalls: [AttentionLoadCall] = []
    private var loadCountWaiters:
        [(target: Int, continuation: CheckedContinuation<Void, Never>)] = []

    init(
        steps: [AttentionLoadStep] = [],
        defaultFeed: AttentionFeed = AttentionFeed(
            items: [],
            nextCursor: nil,
            healthyPrinterCount: 0
        )
    ) {
        self.steps = steps
        self.defaultFeed = defaultFeed
    }

    var loadCallCount: Int { loadCalls.count }

    func getFeed(cursor: String?, limit: Int?) async throws -> AttentionFeed {
        loadCalls.append(AttentionLoadCall(cursor: cursor, limit: limit))
        resumeLoadCountWaiters()

        guard !steps.isEmpty else { return defaultFeed }
        let step = steps.removeFirst()
        switch step {
        case .value(let feed):
            return feed
        case .failure(let error):
            throw error
        case .featureDisabled:
            throw NetworkError.featureDisabled(
                APIError(
                    title: "Feature disabled",
                    status: 404,
                    detail: "Attention is disabled on this server.",
                    errors: nil,
                    message: nil,
                    code: "featureDisabled"
                )
            )
        case .gated(let gate):
            return try await gate.wait()
        }
    }

    func snooze(itemId: String, snoozedUntilUtc: Date) async throws -> SnoozeAttentionResponse {
        SnoozeAttentionResponse(
            snoozedUntilUtc: snoozedUntilUtc,
            attentionItemAnchorAtUtc: nil
        )
    }

    func clearSnooze(itemId: String) async throws {}

    func executeAction(
        itemId: String,
        actionKind: AttentionActionKind
    ) async throws -> AttentionActionResult {
        AttentionActionResult(outcome: "Ok")
    }

    func waitForLoadCount(_ target: Int) async {
        guard loadCalls.count < target else { return }
        await withCheckedContinuation { continuation in
            loadCountWaiters.append((target, continuation))
        }
    }

    private func resumeLoadCountWaiters() {
        let ready = loadCountWaiters.filter { loadCalls.count >= $0.target }
        loadCountWaiters.removeAll { loadCalls.count >= $0.target }
        ready.forEach { $0.continuation.resume() }
    }
}

// MARK: - Callback queue

/// Injectable callback enqueuer that lets tests control exactly when a
/// SignalR-invoked closure fires on the MainActor. Mirrors the
/// `ShiftTaskCallbackQueue` pattern.
final class AttentionCallbackQueue: @unchecked Sendable {
    private typealias Operation = @MainActor @Sendable () async -> Void

    private let lock = NSLock()
    private var operations: [Operation] = []
    private var countWaiters:
        [(target: Int, continuation: CheckedContinuation<Void, Never>)] = []

    var enqueuer: AttentionFeedViewModel.CallbackEnqueuer {
        { [weak self] operation in
            self?.append(operation)
        }
    }

    var count: Int {
        lock.lock()
        defer { lock.unlock() }
        return operations.count
    }

    func waitForCount(_ target: Int) async {
        await withCheckedContinuation { continuation in
            lock.lock()
            if operations.count >= target {
                lock.unlock()
                continuation.resume()
                return
            }
            countWaiters.append((target, continuation))
            lock.unlock()
        }
    }

    @MainActor
    func runNext() async {
        let operation: Operation = {
            lock.lock()
            defer { lock.unlock() }
            precondition(!operations.isEmpty, "No queued attention callback")
            return operations.removeFirst()
        }()
        await operation()
    }

    private func append(_ operation: @escaping Operation) {
        var ready: [CheckedContinuation<Void, Never>] = []
        lock.lock()
        operations.append(operation)
        ready = countWaiters
            .filter { operations.count >= $0.target }
            .map(\.continuation)
        countWaiters.removeAll { operations.count >= $0.target }
        lock.unlock()
        ready.forEach { $0.resume() }
    }
}

// MARK: - Fixtures

func makeAttentionItem(
    id: String = "failure:00000000-0000-0000-0000-000000000001",
    kind: AttentionKind = .failure,
    severity: AttentionSeverity = .critical,
    printerName: String = "Printer 1",
    title: String = "Print failed",
    detail: String = "Layer shift detected",
    occurredAt: Date = Date(timeIntervalSince1970: 1_700_000_000)
) -> AttentionItem {
    AttentionItem(
        id: id,
        kind: kind,
        severity: severity,
        printerId: UUID(),
        printerName: printerName,
        title: title,
        detail: detail,
        occurredAt: occurredAt,
        actions: [],
        toolheadIndex: nil,
        deadlineAt: nil,
        jobId: nil,
        allowFreshOccurrenceBypass: true
    )
}

func makeAttentionFeed(
    items: [AttentionItem] = [],
    nextCursor: String? = nil,
    healthyPrinterCount: Int = 0
) -> AttentionFeed {
    AttentionFeed(
        items: items,
        nextCursor: nextCursor,
        healthyPrinterCount: healthyPrinterCount
    )
}
