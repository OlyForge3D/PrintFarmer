import Foundation
@testable import PrintFarmer

final class StartupPrefetchTestClock: @unchecked Sendable {
    private let lock = NSLock()
    private var millis: Int64

    init(millis: Int64) {
        self.millis = millis
    }

    func set(millis: Int64) {
        lock.lock()
        self.millis = millis
        lock.unlock()
    }

    var now: @Sendable () -> Date {
        { [self] in
            lock.lock()
            let captured = millis
            lock.unlock()
            return Date(timeIntervalSince1970: Double(captured) / 1000.0)
        }
    }
}

enum ShiftTaskProofError: LocalizedError, Sendable {
    case forced(String)

    var errorDescription: String? {
        switch self {
        case .forced(let message): message
        }
    }
}

actor ShiftTaskResultGate<Value: Sendable> {
    private var result: Result<Value, ShiftTaskProofError>?
    private var continuation: CheckedContinuation<
        Result<Value, ShiftTaskProofError>,
        Never
    >?

    func wait() async throws -> Value {
        let result: Result<Value, ShiftTaskProofError> = await withCheckedContinuation { continuation in
            if let resolved = self.result {
                continuation.resume(returning: resolved)
            } else {
                self.continuation = continuation
            }
        }
        return try result.get()
    }

    func succeed(_ value: Value) {
        resolve(.success(value))
    }

    func fail(_ error: ShiftTaskProofError) {
        resolve(.failure(error))
    }

    private func resolve(_ result: Result<Value, ShiftTaskProofError>) {
        precondition(self.result == nil, "ShiftTaskResultGate resolved twice")
        self.result = result
        continuation?.resume(returning: result)
        continuation = nil
    }
}

enum CanonicalLoadStep<Value: Sendable>: Sendable {
    case value(Value)
    case failure(ShiftTaskProofError)
    case gated(ShiftTaskResultGate<Value>)
}

actor ScriptedCanonicalResult<Value: Sendable> {
    private var steps: [CanonicalLoadStep<Value>]
    private(set) var callCount = 0
    private var countWaiters:
        [(target: Int, continuation: CheckedContinuation<Void, Never>)] = []

    init(_ steps: [CanonicalLoadStep<Value>]) {
        self.steps = steps
    }

    func next() async throws -> Value {
        callCount += 1
        resumeCountWaiters()
        guard !steps.isEmpty else {
            throw ShiftTaskProofError.forced("Unexpected canonical load call \(callCount)")
        }
        switch steps.removeFirst() {
        case .value(let value):
            return value
        case .failure(let error):
            throw error
        case .gated(let gate):
            return try await gate.wait()
        }
    }

    func waitForCallCount(_ target: Int) async {
        guard callCount < target else { return }
        await withCheckedContinuation { continuation in
            countWaiters.append((target, continuation))
        }
    }

    private func resumeCountWaiters() {
        let ready = countWaiters.filter { callCount >= $0.target }
        countWaiters.removeAll { callCount >= $0.target }
        ready.forEach { $0.continuation.resume() }
    }
}

enum ShiftTaskLoadStep: Sendable {
    case value(ShiftTaskSnapshot)
    case failure(ShiftTaskProofError)
    case gated(ShiftTaskResultGate<ShiftTaskSnapshot>)
}

enum ShiftTaskMutationStep: Sendable {
    case success
    case failure(ShiftTaskProofError)
    case gated(ShiftTaskResultGate<Void>)
}

struct ShiftTaskMutationCall: Equatable, Sendable {
    let operation: ShiftTaskMutationOperation
    let taskID: String
    let idempotencyKey: String?
}

actor ScriptedShiftTaskService: ShiftTaskServiceProtocol {
    private var loadSteps: [ShiftTaskLoadStep]
    private var mutationSteps: [ShiftTaskMutationOperation: [ShiftTaskMutationStep]]
    private let defaultSnapshot: ShiftTaskSnapshot

    private(set) var loadCallCount = 0
    private(set) var mutationCalls: [ShiftTaskMutationCall] = []
    private var loadCountWaiters:
        [(target: Int, continuation: CheckedContinuation<Void, Never>)] = []
    private var mutationCountWaiters:
        [(target: Int, continuation: CheckedContinuation<Void, Never>)] = []

    init(
        loadSteps: [ShiftTaskLoadStep] = [],
        mutationSteps: [ShiftTaskMutationOperation: [ShiftTaskMutationStep]] = [:],
        defaultSnapshot: ShiftTaskSnapshot = makeShiftTaskSnapshot()
    ) {
        self.loadSteps = loadSteps
        self.mutationSteps = mutationSteps
        self.defaultSnapshot = defaultSnapshot
    }

    func loadSnapshot(shiftPlanEnabled: Bool) async throws -> ShiftTaskSnapshot {
        _ = shiftPlanEnabled
        loadCallCount += 1
        resumeLoadCountWaiters()

        guard !loadSteps.isEmpty else { return defaultSnapshot }
        let step = loadSteps.removeFirst()
        switch step {
        case .value(let snapshot):
            return snapshot
        case .failure(let error):
            throw error
        case .gated(let gate):
            return try await gate.wait()
        }
    }

    func complete(taskID: String, idempotencyKey: String) async throws {
        try await perform(
            .complete,
            taskID: taskID,
            idempotencyKey: idempotencyKey
        )
    }

    func skip(taskID: String) async throws {
        try await perform(.skip, taskID: taskID, idempotencyKey: nil)
    }

    func dismiss(taskID: String) async throws {
        try await perform(.dismiss, taskID: taskID, idempotencyKey: nil)
    }

    func waitForLoadCount(_ target: Int) async {
        guard loadCallCount < target else { return }
        await withCheckedContinuation { continuation in
            loadCountWaiters.append((target, continuation))
        }
    }

    func waitForMutationCount(_ target: Int) async {
        guard mutationCalls.count < target else { return }
        await withCheckedContinuation { continuation in
            mutationCountWaiters.append((target, continuation))
        }
    }

    private func perform(
        _ operation: ShiftTaskMutationOperation,
        taskID: String,
        idempotencyKey: String?
    ) async throws {
        mutationCalls.append(
            ShiftTaskMutationCall(
                operation: operation,
                taskID: taskID,
                idempotencyKey: idempotencyKey
            )
        )
        resumeMutationCountWaiters()

        var steps = mutationSteps[operation] ?? []
        guard !steps.isEmpty else { return }
        let step = steps.removeFirst()
        mutationSteps[operation] = steps

        switch step {
        case .success:
            return
        case .failure(let error):
            throw error
        case .gated(let gate):
            try await gate.wait()
        }
    }

    private func resumeLoadCountWaiters() {
        let ready = loadCountWaiters.filter { loadCallCount >= $0.target }
        loadCountWaiters.removeAll { loadCallCount >= $0.target }
        ready.forEach { $0.continuation.resume() }
    }

    private func resumeMutationCountWaiters() {
        let ready = mutationCountWaiters.filter { mutationCalls.count >= $0.target }
        mutationCountWaiters.removeAll { mutationCalls.count >= $0.target }
        ready.forEach { $0.continuation.resume() }
    }
}

final class ShiftTaskCallbackQueue: @unchecked Sendable {
    typealias Operation = @MainActor @Sendable () async -> Void

    private let lock = NSLock()
    private var operations: [Operation] = []
    private var countWaiters:
        [(target: Int, continuation: CheckedContinuation<Void, Never>)] = []

    var enqueuer: @Sendable (@escaping Operation) -> Void {
        { [weak self] operation in
            self?.append(operation)
        }
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
            precondition(!operations.isEmpty, "No queued SignalR callback")
            return operations.removeFirst()
        }()
        await operation()
    }

    var count: Int {
        lock.lock()
        defer { lock.unlock() }
        return operations.count
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

final class ShiftTaskEventRecorder: @unchecked Sendable {
    private let lock = NSLock()
    private var events: [ShiftTaskInvalidation] = []
    private var countWaiters:
        [(target: Int, continuation: CheckedContinuation<Void, Never>)] = []

    func record(_ event: ShiftTaskInvalidation) {
        var ready: [CheckedContinuation<Void, Never>] = []
        lock.lock()
        events.append(event)
        ready = countWaiters
            .filter { events.count >= $0.target }
            .map(\.continuation)
        countWaiters.removeAll { events.count >= $0.target }
        lock.unlock()
        ready.forEach { $0.resume() }
    }

    func waitForCount(_ target: Int) async {
        await withCheckedContinuation { continuation in
            lock.lock()
            if events.count >= target {
                lock.unlock()
                continuation.resume()
                return
            }
            countWaiters.append((target, continuation))
            lock.unlock()
        }
    }

    var snapshot: [ShiftTaskInvalidation] {
        lock.lock()
        defer { lock.unlock() }
        return events
    }
}

/// Deterministic MainActor barrier that records refresh-waiter registrations as
/// they happen and lets a test await a target count without sleeps or polling.
/// Wired to `ShiftTasksViewModel.refreshWaiterRegistrationObserver`.
@MainActor
final class RefreshWaiterRegistrationBarrier {
    private var registrations = 0
    private var waiters: [(target: Int, continuation: CheckedContinuation<Void, Never>)] = []

    var signal: @MainActor @Sendable () -> Void {
        { [weak self] in self?.record() }
    }

    func record() {
        registrations += 1
        let ready = waiters.filter { registrations >= $0.target }
        waiters.removeAll { registrations >= $0.target }
        ready.forEach { $0.continuation.resume() }
    }

    func waitForCount(_ target: Int) async {
        if registrations >= target { return }
        await withCheckedContinuation { continuation in
            waiters.append((target, continuation))
        }
    }
}

func makeShiftTask(
    id: String = "78200000-0000-0000-0000-000000000001",
    title: String = "Proof task",
    taskType: ShiftTaskType = .harvestReady,
    priority: ShiftTaskPriority = .high,
    anchorKind: ShiftTaskAnchorKind = .now,
    anchorAtUtc: Date? = nil,
    windowStartUtc: Date? = nil,
    windowEndUtc: Date? = nil,
    sourceKind: ShiftTaskSourceKind = .harvest,
    status: ShiftTaskStatus = .pending
) -> ShiftTask {
    ShiftTask(
        id: id,
        taskType: taskType,
        entityType: "Job",
        entityId: "30000000-0003-0000-0000-000000000001",
        title: title,
        description: "Proof detail",
        status: status,
        priority: priority,
        createdAt: Date(timeIntervalSince1970: 1_773_000_000),
        dueAt: nil,
        completedAt: nil,
        relatedEntityCount: 1,
        metadataJson: nil,
        anchorKind: anchorKind,
        anchorAtUtc: anchorAtUtc,
        windowStartUtc: windowStartUtc,
        windowEndUtc: windowEndUtc,
        sourceKind: sourceKind,
        sourceId: "proof:source"
    )
}

func makeShiftTaskSnapshot(
    title: String = "Proof task",
    taskID: String = "78200000-0000-0000-0000-000000000001"
) -> ShiftTaskSnapshot {
    ShiftTaskSnapshot(
        groups: [
            ShiftTaskGroup(
                anchorKind: .now,
                tasks: [makeShiftTask(id: taskID, title: title)]
            ),
        ],
        generatedAt: Date(timeIntervalSince1970: 1_773_000_100),
        mode: .grouped
    )
}

func makeEmptyShiftTaskSnapshot() -> ShiftTaskSnapshot {
    ShiftTaskSnapshot(
        groups: [],
        generatedAt: Date(timeIntervalSince1970: 1_773_000_200),
        mode: .grouped
    )
}
