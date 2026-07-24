import Foundation
import Observation

enum ShiftTasksPhase: Equatable {
    case idle
    case loading
    case content
    case disabled
    case failed
}

struct ShiftTaskLoadFailure: Equatable, Identifiable {
    let id: UUID
    let message: String
}

struct ShiftTaskMutationFailure: Equatable, Identifiable {
    let id: UUID
    let intent: ShiftTaskMutationIntent
    let message: String
}

struct ShiftTaskMutationActivity: Equatable {
    let token: UUID
    let intent: ShiftTaskMutationIntent
    var isInFlight: Bool
    var failure: ShiftTaskMutationFailure?
}

@MainActor
@Observable
final class ShiftTasksViewModel {
    typealias CallbackEnqueuer = @Sendable (
        @escaping @MainActor @Sendable () async -> Void
    ) -> Void

    private(set) var phase: ShiftTasksPhase = .idle
    private(set) var snapshot: ShiftTaskSnapshot?
    private(set) var loadFailure: ShiftTaskLoadFailure?
    private(set) var isRefreshing = false
    private(set) var mutationActivities: [String: ShiftTaskMutationActivity] = [:]

    @ObservationIgnored private let callbackEnqueuer: CallbackEnqueuer
    @ObservationIgnored private var taskService: (any ShiftTaskServiceProtocol)?
    @ObservationIgnored private var offlineQueue: (any OfflineWriteEnqueuing)?
    @ObservationIgnored private var signalRService: (any SignalRServiceProtocol)?
    @ObservationIgnored private var taskServiceIdentity: ObjectIdentifier?
    @ObservationIgnored private var signalRServiceIdentity: ObjectIdentifier?
    @ObservationIgnored private var shiftPlanEnabled = true
    @ObservationIgnored private var lifecycleEpoch: UInt64 = 0
    @ObservationIgnored private var isActive = false
    @ObservationIgnored private var taskInvalidationSubscription: SignalRSubscription?

    @ObservationIgnored private var refreshOwnerToken: UUID?
    @ObservationIgnored private var refreshOwnerTask: Task<Void, Never>?
    @ObservationIgnored private var refreshWaiters: [UUID: RefreshWaiter] = [:]

    /// Monotonic generation counter. Each canonical refresh pass is tagged with
    /// a generation, and each waiter records the generation of the first pass
    /// that will begin after it registers. Waiters resolve as soon as their
    /// generation (or any later one) completes rather than at global refresh
    /// quiescence, which bounds coalescing under sustained invalidations: parked
    /// waiters are released every pass instead of accumulating until a quiet
    /// pass occurs (issue #814).
    @ObservationIgnored private var passGenerationCounter: UInt64 = 0
    @ObservationIgnored private var runningGeneration: UInt64 = 0
    @ObservationIgnored private var pendingGeneration: UInt64 = 0
    @ObservationIgnored private var completedGeneration: UInt64 = 0

    #if DEBUG
    /// Deterministic test seam fired on the MainActor immediately after a
    /// refresh waiter is registered. Production never assigns it (zero cost
    /// beyond a nil check); tests use it as a barrier to observe waiter
    /// registration without sleeps or polling.
    @ObservationIgnored
    var refreshWaiterRegistrationObserver: (@MainActor @Sendable () -> Void)?
    #endif

    private struct RefreshWaiter {
        let continuation: CheckedContinuation<Bool, Never>
        let targetGeneration: UInt64
    }

    /// Production callers use the default enqueuer, which hops each
    /// callback onto the MainActor via an unstructured `Task`. Tests
    /// inject a deterministic enqueuer to control callback ordering.
    /// The default reproduces production behavior exactly, so this
    /// seam is safe to expose in all build configurations (including
    /// Release build-for-testing).
    init(callbackEnqueuer: @escaping CallbackEnqueuer = { operation in
        Task { @MainActor in
            await operation()
        }
    }) {
        self.callbackEnqueuer = callbackEnqueuer
    }

    func configure(
        taskService: any ShiftTaskServiceProtocol,
        signalRService: any SignalRServiceProtocol,
        shiftPlanEnabled: Bool,
        offlineQueue: (any OfflineWriteEnqueuing)? = nil
    ) {
        let newTaskIdentity = ObjectIdentifier(taskService as AnyObject)
        let newSignalRIdentity = ObjectIdentifier(signalRService as AnyObject)

        if isActive,
           taskServiceIdentity == newTaskIdentity,
           signalRServiceIdentity == newSignalRIdentity,
           self.shiftPlanEnabled == shiftPlanEnabled {
            return
        }

        invalidateLifecycle(resetState: true)
        self.taskService = taskService
        self.signalRService = signalRService
        self.offlineQueue = offlineQueue
        self.taskServiceIdentity = newTaskIdentity
        self.signalRServiceIdentity = newSignalRIdentity
        self.shiftPlanEnabled = shiftPlanEnabled
        self.isActive = true

        let epoch = lifecycleEpoch
        let enqueue = callbackEnqueuer
        taskInvalidationSubscription = signalRService.onTaskInvalidated { [weak self] invalidation in
            enqueue { [weak self] in
                guard let self,
                      self.matchesAuthority(
                        epoch: epoch,
                        taskIdentity: newTaskIdentity,
                        signalRIdentity: newSignalRIdentity
                      ),
                      ShiftTaskInvalidation.supportedTargets.contains(invalidation.target) else {
                    return
                }
                _ = await self.requestCanonicalRefresh(
                    epoch: epoch,
                    service: taskService,
                    shiftPlanEnabled: shiftPlanEnabled
                )
            }
        }
    }

    func deactivate() {
        invalidateLifecycle(resetState: true)
    }

    @discardableResult
    func refresh() async -> Bool {
        guard isActive, let taskService else { return false }
        return await requestCanonicalRefresh(
            epoch: lifecycleEpoch,
            service: taskService,
            shiftPlanEnabled: shiftPlanEnabled
        )
    }

    @discardableResult
    func retryLoad(failureID: UUID) async -> Bool {
        guard loadFailure?.id == failureID else { return false }
        return await refresh()
    }

    func perform(_ operation: ShiftTaskMutationOperation, taskID: String) async {
        guard isActive, let service = taskService else { return }
        guard mutationActivities[taskID]?.isInFlight != true else { return }

        let intent = ShiftTaskMutationIntent.make(taskID: taskID, operation: operation)
        await executeMutation(
            intent: intent,
            preservingFailure: nil,
            epoch: lifecycleEpoch,
            service: service,
            taskIdentity: ObjectIdentifier(service as AnyObject)
        )
    }

    @discardableResult
    func retryMutation(failureID: UUID) async -> Bool {
        guard isActive, let service = taskService else { return false }
        guard let activity = mutationActivities.values.first(where: {
            $0.failure?.id == failureID && !$0.isInFlight
        }), let failure = activity.failure else {
            return false
        }

        await executeMutation(
            intent: failure.intent,
            preservingFailure: failure,
            epoch: lifecycleEpoch,
            service: service,
            taskIdentity: ObjectIdentifier(service as AnyObject)
        )
        return true
    }

    @discardableResult
    func dismissMutationError(failureID: UUID) -> Bool {
        guard let (taskID, activity) = mutationActivities.first(where: {
            $0.value.failure?.id == failureID
        }) else {
            return false
        }

        if activity.isInFlight {
            var updated = activity
            updated.failure = nil
            mutationActivities[taskID] = updated
        } else {
            mutationActivities.removeValue(forKey: taskID)
        }
        return true
    }

    func mutationActivity(for taskID: String) -> ShiftTaskMutationActivity? {
        mutationActivities[taskID]
    }

    private func requestCanonicalRefresh(
        epoch: UInt64,
        service: any ShiftTaskServiceProtocol,
        shiftPlanEnabled: Bool
    ) async -> Bool {
        let waiterID = UUID()
        return await withTaskCancellationHandler {
            await withCheckedContinuation { continuation in
                guard !Task.isCancelled,
                      matchesAuthority(
                        epoch: epoch,
                        taskIdentity: ObjectIdentifier(service as AnyObject),
                        signalRIdentity: signalRServiceIdentity
                      ) else {
                    continuation.resume(returning: false)
                    return
                }

                let targetGeneration = ensureCoveringGeneration(
                    epoch: epoch,
                    service: service,
                    shiftPlanEnabled: shiftPlanEnabled
                )
                refreshWaiters[waiterID] = RefreshWaiter(
                    continuation: continuation,
                    targetGeneration: targetGeneration
                )
                #if DEBUG
                refreshWaiterRegistrationObserver?()
                #endif
            }
        } onCancel: {
            Task { @MainActor [weak self] in
                self?.cancelRefreshWaiter(id: waiterID, epoch: epoch)
            }
        }
    }

    /// Reserves the generation of the first canonical pass that will begin after
    /// the caller registers, starting the refresh owner if one is not already
    /// running. Coalescing is bounded: callers that arrive while a pass is in
    /// flight all share a single reserved `pendingGeneration` (at most one pass
    /// is ever queued ahead), so a sustained invalidation storm cannot grow the
    /// number of scheduled passes.
    private func ensureCoveringGeneration(
        epoch: UInt64,
        service: any ShiftTaskServiceProtocol,
        shiftPlanEnabled: Bool
    ) -> UInt64 {
        if runningGeneration == 0 {
            passGenerationCounter &+= 1
            runningGeneration = passGenerationCounter
            startRefreshOwner(
                epoch: epoch,
                service: service,
                shiftPlanEnabled: shiftPlanEnabled
            )
            return runningGeneration
        }

        if pendingGeneration == 0 {
            passGenerationCounter &+= 1
            pendingGeneration = passGenerationCounter
        }
        return pendingGeneration
    }

    private func startRefreshOwner(
        epoch: UInt64,
        service: any ShiftTaskServiceProtocol,
        shiftPlanEnabled: Bool
    ) {
        let token = UUID()
        refreshOwnerToken = token
        isRefreshing = true
        if snapshot == nil {
            phase = .loading
        }

        refreshOwnerTask = Task { @MainActor [weak self] in
            await self?.runRefreshLoop(
                epoch: epoch,
                token: token,
                service: service,
                shiftPlanEnabled: shiftPlanEnabled
            )
        }
    }

    private func runRefreshLoop(
        epoch: UInt64,
        token: UUID,
        service: any ShiftTaskServiceProtocol,
        shiftPlanEnabled: Bool
    ) async {
        while matchesRefreshOwner(
            epoch: epoch,
            token: token,
            service: service
        ) {
            let generation = runningGeneration
            var passSucceeded = false

            do {
                let loaded = try await service.loadSnapshot(
                    shiftPlanEnabled: shiftPlanEnabled
                )
                guard matchesRefreshOwner(
                    epoch: epoch,
                    token: token,
                    service: service
                ) else {
                    return
                }

                snapshot = loaded
                if let compatibilityError = loaded.compatibilityErrorMessage {
                    loadFailure = ShiftTaskLoadFailure(
                        id: UUID(),
                        message: compatibilityError
                    )
                    passSucceeded = false
                } else {
                    loadFailure = nil
                    passSucceeded = true
                }
                phase = loaded.mode == .featureDisabled ? .disabled : .content
            } catch is CancellationError {
                guard matchesRefreshOwner(
                    epoch: epoch,
                    token: token,
                    service: service
                ) else {
                    return
                }
                if snapshot == nil {
                    phase = .idle
                }
                passSucceeded = false
            } catch {
                guard matchesRefreshOwner(
                    epoch: epoch,
                    token: token,
                    service: service
                ) else {
                    return
                }

                loadFailure = ShiftTaskLoadFailure(
                    id: UUID(),
                    message: error.localizedDescription
                )
                if snapshot == nil {
                    phase = .failed
                }
                passSucceeded = false
            }

            completePass(
                token: token,
                generation: generation,
                succeeded: passSucceeded
            )
        }
    }

    /// Publishes the outcome of a single canonical pass: resolves every waiter
    /// whose target generation is now covered, then either promotes the queued
    /// `pendingGeneration` into the next running pass or retires the owner. This
    /// releases explicit mutation/load callers after the FIRST pass covering
    /// their generation instead of holding them until global quiescence.
    private func completePass(
        token: UUID,
        generation: UInt64,
        succeeded: Bool
    ) {
        guard refreshOwnerToken == token else { return }

        if generation > completedGeneration {
            completedGeneration = generation
        }
        resumeRefreshWaiters(coveringThrough: generation, returning: succeeded)

        if pendingGeneration != 0 {
            runningGeneration = pendingGeneration
            pendingGeneration = 0
            return
        }

        refreshOwnerTask = nil
        refreshOwnerToken = nil
        runningGeneration = 0
        isRefreshing = false
    }

    private func cancelRefreshWaiter(id: UUID, epoch: UInt64) {
        guard lifecycleEpoch == epoch,
              let waiter = refreshWaiters.removeValue(forKey: id) else {
            return
        }
        waiter.continuation.resume(returning: false)
        if refreshWaiters.isEmpty {
            abandonRefreshOwner()
        }
    }

    private func executeMutation(
        intent: ShiftTaskMutationIntent,
        preservingFailure: ShiftTaskMutationFailure?,
        epoch: UInt64,
        service: any ShiftTaskServiceProtocol,
        taskIdentity: ObjectIdentifier
    ) async {
        let token = UUID()
        mutationActivities[intent.taskID] = ShiftTaskMutationActivity(
            token: token,
            intent: intent,
            isInFlight: true,
            failure: preservingFailure
        )

        do {
            switch intent.operation {
            case .complete:
                guard let idempotencyKey = intent.idempotencyKey else {
                    throw NetworkError.invalidResponse
                }
                try await service.complete(
                    taskID: intent.taskID,
                    idempotencyKey: idempotencyKey
                )
            case .skip:
                try await service.skip(taskID: intent.taskID)
            case .dismiss:
                try await service.dismiss(taskID: intent.taskID)
            }

            guard matchesMutation(
                taskID: intent.taskID,
                token: token,
                epoch: epoch,
                taskIdentity: taskIdentity
            ) else {
                return
            }

            _ = await requestCanonicalRefresh(
                epoch: epoch,
                service: service,
                shiftPlanEnabled: shiftPlanEnabled
            )
            guard matchesMutation(
                taskID: intent.taskID,
                token: token,
                epoch: epoch,
                taskIdentity: taskIdentity
            ) else {
                return
            }

            mutationActivities.removeValue(forKey: intent.taskID)
        } catch is CancellationError {
            guard matchesMutation(
                taskID: intent.taskID,
                token: token,
                epoch: epoch,
                taskIdentity: taskIdentity
            ) else {
                return
            }
            if preservingFailure == nil {
                mutationActivities.removeValue(forKey: intent.taskID)
            } else {
                var activity = mutationActivities[intent.taskID]
                activity?.isInFlight = false
                mutationActivities[intent.taskID] = activity
            }
        } catch {
            guard matchesMutation(
                taskID: intent.taskID,
                token: token,
                epoch: epoch,
                taskIdentity: taskIdentity
            ) else {
                return
            }

            // Offline-class failure of a task completion: durably enqueue the
            // frozen intent (same idempotency key) so it replays exactly once
            // when connectivity returns (F10-Q2, #790). Skip/dismiss are NEVER
            // queued. Terminal conflicts / validation / identity failures are
            // surfaced immediately as before (never queued). Only the
            // completion op with an enqueuer wired takes this path; otherwise
            // the pre-existing surfaced-failure behavior is unchanged.
            if intent.operation == .complete,
               let offlineQueue,
               let idempotencyKey = intent.idempotencyKey,
               OfflineWriteReplayClassifier.isEnqueueableOfflineFailure(error) {
                let result = await offlineQueue.enqueue(
                    .taskComplete(taskID: intent.taskID, idempotencyKey: idempotencyKey)
                )
                guard matchesMutation(
                    taskID: intent.taskID,
                    token: token,
                    epoch: epoch,
                    taskIdentity: taskIdentity
                ) else {
                    return
                }
                if case .enqueued = result {
                    // Safely queued — clear the in-flight state without an error
                    // banner. The offline status surface tracks the pending item.
                    mutationActivities.removeValue(forKey: intent.taskID)
                    return
                }
            }

            mutationActivities[intent.taskID] = ShiftTaskMutationActivity(
                token: token,
                intent: intent,
                isInFlight: false,
                failure: ShiftTaskMutationFailure(
                    id: UUID(),
                    intent: intent,
                    message: error.localizedDescription
                )
            )
        }
    }

    private func matchesAuthority(
        epoch: UInt64,
        taskIdentity: ObjectIdentifier?,
        signalRIdentity: ObjectIdentifier?
    ) -> Bool {
        isActive
            && lifecycleEpoch == epoch
            && taskServiceIdentity == taskIdentity
            && signalRServiceIdentity == signalRIdentity
    }

    private func matchesRefreshOwner(
        epoch: UInt64,
        token: UUID,
        service: any ShiftTaskServiceProtocol
    ) -> Bool {
        matchesAuthority(
            epoch: epoch,
            taskIdentity: ObjectIdentifier(service as AnyObject),
            signalRIdentity: signalRServiceIdentity
        ) && refreshOwnerToken == token
    }

    private func matchesMutation(
        taskID: String,
        token: UUID,
        epoch: UInt64,
        taskIdentity: ObjectIdentifier
    ) -> Bool {
        matchesAuthority(
            epoch: epoch,
            taskIdentity: taskIdentity,
            signalRIdentity: signalRServiceIdentity
        ) && mutationActivities[taskID]?.token == token
    }

    private func invalidateLifecycle(resetState: Bool) {
        lifecycleEpoch &+= 1
        isActive = false

        taskInvalidationSubscription?.cancel()
        taskInvalidationSubscription = nil

        refreshOwnerTask?.cancel()
        refreshOwnerTask = nil
        refreshOwnerToken = nil
        passGenerationCounter = 0
        runningGeneration = 0
        pendingGeneration = 0
        completedGeneration = 0
        isRefreshing = false
        resumeAllRefreshWaiters(returning: false)

        mutationActivities.removeAll()
        taskService = nil
        signalRService = nil
        taskServiceIdentity = nil
        signalRServiceIdentity = nil

        guard resetState else { return }
        snapshot = nil
        loadFailure = nil
        phase = .idle
    }

    private func abandonRefreshOwner() {
        refreshOwnerTask?.cancel()
        refreshOwnerTask = nil
        refreshOwnerToken = nil
        runningGeneration = 0
        pendingGeneration = 0
        isRefreshing = false
        if snapshot == nil, phase == .loading {
            phase = .idle
        }
    }

    private func resumeRefreshWaiters(
        coveringThrough generation: UInt64,
        returning value: Bool
    ) {
        let matured = refreshWaiters.filter {
            $0.value.targetGeneration <= generation
        }
        guard !matured.isEmpty else { return }
        for id in matured.keys {
            refreshWaiters.removeValue(forKey: id)
        }
        for waiter in matured.values {
            waiter.continuation.resume(returning: value)
        }
    }

    private func resumeAllRefreshWaiters(returning value: Bool) {
        let waiters = refreshWaiters
        refreshWaiters.removeAll()
        for waiter in waiters.values {
            waiter.continuation.resume(returning: value)
        }
    }

    #if DEBUG
    var pendingRefreshWaiterCountForTesting: Int { refreshWaiters.count }

    var refreshWaiterTargetGenerationsForTesting: [UInt64] {
        refreshWaiters.values.map(\.targetGeneration).sorted()
    }

    var isRefreshOwnerActiveForTesting: Bool { refreshOwnerToken != nil }
    #endif
}
