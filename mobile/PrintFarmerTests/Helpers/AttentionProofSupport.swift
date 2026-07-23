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

final class AttentionCountBarrier: @unchecked Sendable {
    private struct Waiter {
        let target: Int
        let continuation: CheckedContinuation<Bool, Never>
    }

    private let lock = NSLock()
    private var count = 0
    private var isClosed = false
    private var waiters: [UUID: Waiter] = [:]

    func advance(to newCount: Int) {
        lock.lock()
        count = newCount
        let ready = waiters.filter { count >= $0.value.target }
        ready.keys.forEach { waiters[$0] = nil }
        lock.unlock()
        ready.values.forEach { $0.continuation.resume(returning: true) }
    }

    @discardableResult
    func wait(for target: Int) async -> Bool {
        let waiterID = UUID()
        return await withTaskCancellationHandler {
            await withCheckedContinuation { continuation in
                lock.lock()
                if count >= target {
                    lock.unlock()
                    continuation.resume(returning: true)
                } else if isClosed || Task.isCancelled {
                    lock.unlock()
                    continuation.resume(returning: false)
                } else {
                    waiters[waiterID] = Waiter(
                        target: target,
                        continuation: continuation
                    )
                    lock.unlock()
                }
            }
        } onCancel: {
            self.cancel(waiterID: waiterID)
        }
    }

    func close() {
        lock.lock()
        guard !isClosed else {
            lock.unlock()
            return
        }
        isClosed = true
        let pending = Array(waiters.values)
        waiters = [:]
        lock.unlock()
        pending.forEach { $0.continuation.resume(returning: false) }
    }

    private func cancel(waiterID: UUID) {
        lock.lock()
        let waiter = waiters.removeValue(forKey: waiterID)
        lock.unlock()
        waiter?.continuation.resume(returning: false)
    }

    deinit {
        close()
    }
}

// MARK: - Result gate

/// Awaitable one-shot barrier that lets a test hold a service call
/// mid-flight and later resolve it as success or failure. Modelled on
/// `ShiftTaskResultGate` from the shift-task suite. Deterministic — no
/// polling or fixed sleeps.
final class AttentionResultGate<Value: Sendable>: @unchecked Sendable {
    private enum Resolution {
        case success(Value)
        case failure(AttentionProofError)
        case cancelled
    }

    private let lock = NSLock()
    private var terminalResolution: Resolution?
    private var continuations: [
        UUID: CheckedContinuation<Resolution, Never>
    ] = [:]

    func wait() async throws -> Value {
        let waiterID = UUID()
        let resolution: Resolution = await withTaskCancellationHandler {
            await withCheckedContinuation { continuation in
                lock.lock()
                if let terminalResolution {
                    lock.unlock()
                    continuation.resume(returning: terminalResolution)
                } else if Task.isCancelled {
                    lock.unlock()
                    continuation.resume(returning: .cancelled)
                } else {
                    continuations[waiterID] = continuation
                    lock.unlock()
                }
            }
        } onCancel: {
            self.cancelWaiter(waiterID)
        }

        switch resolution {
        case .success(let value):
            return value
        case .failure(let error):
            throw error
        case .cancelled:
            throw CancellationError()
        }
    }

    func succeed(_ value: Value) async {
        precondition(complete(.success(value)), "AttentionResultGate resolved twice")
    }

    func fail(_ error: AttentionProofError) async {
        precondition(complete(.failure(error)), "AttentionResultGate resolved twice")
    }

    func cancel() {
        _ = complete(.cancelled)
    }

    private func complete(_ resolution: Resolution) -> Bool {
        lock.lock()
        guard terminalResolution == nil else {
            lock.unlock()
            return false
        }
        terminalResolution = resolution
        let waiters = Array(continuations.values)
        continuations = [:]
        lock.unlock()
        waiters.forEach { $0.resume(returning: resolution) }
        return true
    }

    private func cancelWaiter(_ waiterID: UUID) {
        lock.lock()
        let continuation = continuations.removeValue(forKey: waiterID)
        lock.unlock()
        continuation?.resume(returning: .cancelled)
    }

    deinit {
        _ = complete(.cancelled)
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
    /// Hold the call open until the gate resolves, then throw
    /// `NetworkError.featureDisabled`. Used to prove that a stale
    /// gated-disabled completion cannot overwrite a newer success.
    case gatedFeatureDisabled(AttentionResultGate<Void>)
}

/// One recorded call for later assertion.
struct AttentionLoadCall: Equatable, Sendable {
    let cursor: String?
    let limit: Int?
}

enum AttentionActionStep: Sendable {
    case value(AttentionActionResult)
    case failure(AttentionProofError)
    case gated(AttentionResultGate<AttentionActionResult>)
}

enum AttentionSnoozeStep: Sendable {
    case value(SnoozeAttentionResponse)
    case failure(AttentionProofError)
    case gated(AttentionResultGate<SnoozeAttentionResponse>)
}

struct AttentionActionCall: Equatable, Sendable {
    let itemID: String
    let actionKind: AttentionActionKind
}

struct AttentionSnoozeCall: Equatable, Sendable {
    let itemID: String
    let snoozedUntilUtc: Date
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
    private var actionSteps: [AttentionActionStep]
    private var snoozeSteps: [AttentionSnoozeStep]
    private let defaultFeed: AttentionFeed

    private(set) var loadCalls: [AttentionLoadCall] = []
    private(set) var actionCalls: [AttentionActionCall] = []
    private(set) var snoozeCalls: [AttentionSnoozeCall] = []
    private let loadCountBarrier = AttentionCountBarrier()
    private let actionCountBarrier = AttentionCountBarrier()
    private let snoozeCountBarrier = AttentionCountBarrier()

    init(
        steps: [AttentionLoadStep] = [],
        actionSteps: [AttentionActionStep] = [],
        snoozeSteps: [AttentionSnoozeStep] = [],
        defaultFeed: AttentionFeed = AttentionFeed(
            items: [],
            nextCursor: nil,
            healthyPrinterCount: 0
        )
    ) {
        self.steps = steps
        self.actionSteps = actionSteps
        self.snoozeSteps = snoozeSteps
        self.defaultFeed = defaultFeed
    }

    var loadCallCount: Int { loadCalls.count }
    var actionCallCount: Int { actionCalls.count }
    var snoozeCallCount: Int { snoozeCalls.count }

    func getFeed(cursor: String?, limit: Int?) async throws -> AttentionFeed {
        loadCalls.append(AttentionLoadCall(cursor: cursor, limit: limit))
        loadCountBarrier.advance(to: loadCalls.count)

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
        case .gatedFeatureDisabled(let gate):
            _ = try await gate.wait()
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
        }
    }

    func snooze(itemId: String, snoozedUntilUtc: Date) async throws -> SnoozeAttentionResponse {
        snoozeCalls.append(
            AttentionSnoozeCall(
                itemID: itemId,
                snoozedUntilUtc: snoozedUntilUtc
            )
        )
        snoozeCountBarrier.advance(to: snoozeCalls.count)

        guard !snoozeSteps.isEmpty else {
            return SnoozeAttentionResponse(
                snoozedUntilUtc: snoozedUntilUtc,
                attentionItemAnchorAtUtc: nil
            )
        }
        let step = snoozeSteps.removeFirst()
        switch step {
        case .value(let response):
            return response
        case .failure(let error):
            throw error
        case .gated(let gate):
            return try await gate.wait()
        }
    }

    func clearSnooze(itemId: String) async throws {}

    func executeAction(
        itemId: String,
        actionKind: AttentionActionKind
    ) async throws -> AttentionActionResult {
        actionCalls.append(
            AttentionActionCall(itemID: itemId, actionKind: actionKind)
        )
        actionCountBarrier.advance(to: actionCalls.count)

        guard !actionSteps.isEmpty else {
            return AttentionActionResult(outcome: "Ok")
        }
        let step = actionSteps.removeFirst()
        switch step {
        case .value(let result):
            return result
        case .failure(let error):
            throw error
        case .gated(let gate):
            return try await gate.wait()
        }
    }

    func waitForLoadCount(_ target: Int) async {
        _ = await loadCountBarrier.wait(for: target)
    }

    func waitForActionCount(_ target: Int) async {
        _ = await actionCountBarrier.wait(for: target)
    }

    func waitForSnoozeCount(_ target: Int) async {
        _ = await snoozeCountBarrier.wait(for: target)
    }

    func closeWaiters() {
        loadCountBarrier.close()
        actionCountBarrier.close()
        snoozeCountBarrier.close()
    }
}

// MARK: - Snapshot script

enum AttentionSnapshotOutcome: Sendable {
    case value(Data)
    case nativeCancellation
    case urlCancellation
    case wrappedCancellation
    case wrappedTimeout
    case failure(AttentionProofError)
}

final class AttentionSnapshotGate: @unchecked Sendable {
    private let lock = NSLock()
    private var terminalOutcome: AttentionSnapshotOutcome?
    private var continuations: [
        UUID: CheckedContinuation<AttentionSnapshotOutcome, Never>
    ] = [:]

    func wait() async -> AttentionSnapshotOutcome {
        let waiterID = UUID()
        return await withTaskCancellationHandler {
            await withCheckedContinuation { continuation in
                lock.lock()
                if let terminalOutcome {
                    lock.unlock()
                    continuation.resume(returning: terminalOutcome)
                } else if Task.isCancelled {
                    lock.unlock()
                    continuation.resume(returning: .nativeCancellation)
                } else {
                    continuations[waiterID] = continuation
                    lock.unlock()
                }
            }
        } onCancel: {
            self.cancelWaiter(waiterID)
        }
    }

    func resolve(_ outcome: AttentionSnapshotOutcome) async {
        precondition(complete(outcome), "AttentionSnapshotGate resolved twice")
    }

    func cancel() {
        _ = complete(.nativeCancellation)
    }

    private func complete(_ outcome: AttentionSnapshotOutcome) -> Bool {
        lock.lock()
        guard terminalOutcome == nil else {
            lock.unlock()
            return false
        }
        terminalOutcome = outcome
        let waiters = Array(continuations.values)
        continuations = [:]
        lock.unlock()
        waiters.forEach { $0.resume(returning: outcome) }
        return true
    }

    private func cancelWaiter(_ waiterID: UUID) {
        lock.lock()
        let continuation = continuations.removeValue(forKey: waiterID)
        lock.unlock()
        continuation?.resume(returning: .nativeCancellation)
    }

    deinit {
        _ = complete(.nativeCancellation)
    }
}

enum AttentionSnapshotStep: Sendable {
    case outcome(AttentionSnapshotOutcome)
    case gated(AttentionSnapshotGate)
}

struct AttentionSnapshotCall: Equatable, Sendable {
    let printerID: UUID
}

actor ScriptedAttentionSnapshotSource {
    private var stepsByPrinterID: [UUID: [AttentionSnapshotStep]]
    private(set) var calls: [AttentionSnapshotCall] = []
    private let callCountBarrier = AttentionCountBarrier()

    init(stepsByPrinterID: [UUID: [AttentionSnapshotStep]]) {
        self.stepsByPrinterID = stepsByPrinterID
    }

    func load(printerID: UUID) async throws -> Data {
        calls.append(AttentionSnapshotCall(printerID: printerID))
        callCountBarrier.advance(to: calls.count)

        guard var steps = stepsByPrinterID[printerID], !steps.isEmpty else {
            throw AttentionProofError.forced(
                "No snapshot step for \(printerID.uuidString)"
            )
        }
        let step = steps.removeFirst()
        stepsByPrinterID[printerID] = steps

        switch step {
        case .outcome(let outcome):
            return try resolve(outcome)
        case .gated(let gate):
            return try resolve(await gate.wait())
        }
    }

    func callCount(for printerID: UUID) -> Int {
        calls.filter { $0.printerID == printerID }.count
    }

    func waitForCallCount(_ target: Int) async {
        _ = await callCountBarrier.wait(for: target)
    }

    private func resolve(_ outcome: AttentionSnapshotOutcome) throws -> Data {
        switch outcome {
        case .value(let data):
            return data
        case .nativeCancellation:
            throw CancellationError()
        case .urlCancellation:
            throw URLError(.cancelled)
        case .wrappedCancellation:
            throw NetworkError.transportError(URLError(.cancelled))
        case .wrappedTimeout:
            throw NetworkError.transportError(URLError(.timedOut))
        case .failure(let error):
            throw error
        }
    }

    func closeWaiters() {
        callCountBarrier.close()
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
    private let countBarrier = AttentionCountBarrier()

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
        _ = await countBarrier.wait(for: target)
    }

    @MainActor
    func runNext() async {
        let (operation, remainingCount): (Operation, Int) = {
            lock.lock()
            defer { lock.unlock() }
            precondition(!operations.isEmpty, "No queued attention callback")
            let operation = operations.removeFirst()
            return (operation, operations.count)
        }()
        countBarrier.advance(to: remainingCount)
        await operation()
    }

    private func append(_ operation: @escaping Operation) {
        lock.lock()
        operations.append(operation)
        let operationCount = operations.count
        lock.unlock()
        countBarrier.advance(to: operationCount)
    }

    func closeWaiters() {
        countBarrier.close()
    }
}

// MARK: - Fixtures

func makeAttentionItem(
    id: String = "failure:00000000-0000-0000-0000-000000000001",
    kind: AttentionKind = .failure,
    severity: AttentionSeverity = .critical,
    printerID: UUID = UUID(),
    printerName: String = "Printer 1",
    title: String = "Print failed",
    detail: String = "Layer shift detected",
    occurredAt: Date = Date(timeIntervalSince1970: 1_700_000_000),
    actions: [AttentionAction] = [],
    deadlineAt: Date? = nil,
    jobID: UUID? = nil
) -> AttentionItem {
    AttentionItem(
        id: id,
        kind: kind,
        severity: severity,
        printerId: printerID,
        printerName: printerName,
        title: title,
        detail: detail,
        occurredAt: occurredAt,
        actions: actions,
        toolheadIndex: nil,
        deadlineAt: deadlineAt,
        jobId: jobID,
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
