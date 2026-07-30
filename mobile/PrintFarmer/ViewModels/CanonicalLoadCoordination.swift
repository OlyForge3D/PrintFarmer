import Foundation

struct CanonicalLoadWaiter: Sendable {
    private let registry: CanonicalLoadWaiterRegistry
    private let id: UUID
    private let onCancel: @Sendable () -> Void

    fileprivate init(
        registry: CanonicalLoadWaiterRegistry,
        id: UUID,
        onCancel: @escaping @Sendable () -> Void
    ) {
        self.registry = registry
        self.id = id
        self.onCancel = onCancel
    }

    func wait() async {
        await registry.wait(for: id, onCancel: onCancel)
    }
}

/// Cancellation-safe fan-out for callers awaiting one canonical owner. The
/// registry never retains its view model and resolves each continuation exactly
/// once across attachment, cancellation, invalidation, and deinitialization.
final class CanonicalLoadWaiterRegistry: @unchecked Sendable {
    private enum Slot {
        case unattached
        case attached(CheckedContinuation<Void, Never>)
    }

    private let lock = NSLock()
    private let beforeAttachment: (@Sendable () async -> Void)?
    private var slots: [UUID: Slot] = [:]
    private var completedBeforeAttachment: Set<UUID> = []
    private var countObservers: [
        (target: Int, continuation: CheckedContinuation<Void, Never>)
    ] = []

    init(beforeAttachment: (@Sendable () async -> Void)? = nil) {
        self.beforeAttachment = beforeAttachment
    }

    var activeCount: Int {
        lock.withLock { slots.count }
    }

    func registerWaiter(
        onCancel: @escaping @Sendable () -> Void
    ) -> CanonicalLoadWaiter {
        let id = UUID()
        register(id)
        return CanonicalLoadWaiter(
            registry: self,
            id: id,
            onCancel: onCancel
        )
    }

    func register(_ id: UUID) {
        let observers: [CheckedContinuation<Void, Never>] = lock.withLock {
            precondition(slots[id] == nil && !completedBeforeAttachment.contains(id))
            slots[id] = .unattached
            let ready = countObservers
                .filter { $0.target <= slots.count }
                .map(\.continuation)
            countObservers.removeAll { $0.target <= slots.count }
            return ready
        }
        observers.forEach { $0.resume() }
    }

    func waitForActiveCount(_ target: Int) async {
        await withCheckedContinuation { continuation in
            let resumeImmediately: Bool = lock.withLock {
                guard slots.count < target else { return true }
                countObservers.append((target, continuation))
                return false
            }
            if resumeImmediately {
                continuation.resume()
            }
        }
    }

    func wait(
        for id: UUID,
        onCancel: @escaping @Sendable () -> Void
    ) async {
        await withTaskCancellationHandler {
            if let beforeAttachment {
                await beforeAttachment()
            }
            await withCheckedContinuation { continuation in
                attach(continuation, to: id)
            }
        } onCancel: {
            self.complete(id)
            onCancel()
        }
    }

    func completeAll() {
        let continuations: [CheckedContinuation<Void, Never>] = lock.withLock {
            var ready: [CheckedContinuation<Void, Never>] = []
            for (id, slot) in slots {
                switch slot {
                case .unattached:
                    completedBeforeAttachment.insert(id)
                case .attached(let continuation):
                    ready.append(continuation)
                }
            }
            slots.removeAll(keepingCapacity: true)
            return ready
        }
        continuations.forEach { $0.resume() }
    }

    private func attach(
        _ continuation: CheckedContinuation<Void, Never>,
        to id: UUID
    ) {
        let resumeImmediately: Bool = lock.withLock {
            if completedBeforeAttachment.remove(id) != nil {
                return true
            }
            guard case .unattached = slots[id] else {
                return true
            }
            slots[id] = .attached(continuation)
            return false
        }
        if resumeImmediately {
            continuation.resume()
        }
    }

    func complete(_ id: UUID) {
        let continuation: CheckedContinuation<Void, Never>? = lock.withLock {
            guard let slot = slots.removeValue(forKey: id) else {
                return nil
            }
            switch slot {
            case .unattached:
                completedBeforeAttachment.insert(id)
                return nil
            case .attached(let continuation):
                return continuation
            }
        }
        continuation?.resume()
    }

    deinit {
        completeAll()
        let observers = lock.withLock {
            let ready = countObservers.map(\.continuation)
            countObservers.removeAll()
            return ready
        }
        observers.forEach { $0.resume() }
    }
}

/// Tracks only currently executing canonical tasks. Unlike the former retired
/// task arrays, completed tasks are never retained.
final class CanonicalLoadTaskTracker: @unchecked Sendable {
    private let lock = NSLock()
    private var activeTaskCount = 0
    private var idleWaiters: [CheckedContinuation<Void, Never>] = []

    func taskStarted() {
        lock.withLock {
            activeTaskCount += 1
        }
    }

    func taskFinished() {
        let waiters: [CheckedContinuation<Void, Never>] = lock.withLock {
            precondition(activeTaskCount > 0)
            activeTaskCount -= 1
            guard activeTaskCount == 0 else { return [] }
            let ready = idleWaiters
            idleWaiters.removeAll(keepingCapacity: true)
            return ready
        }
        waiters.forEach { $0.resume() }
    }

    func waitForIdle() async {
        await withCheckedContinuation { continuation in
            let resumeImmediately: Bool = lock.withLock {
                guard activeTaskCount > 0 else { return true }
                idleWaiters.append(continuation)
                return false
            }
            if resumeImmediately {
                continuation.resume()
            }
        }
    }
}
