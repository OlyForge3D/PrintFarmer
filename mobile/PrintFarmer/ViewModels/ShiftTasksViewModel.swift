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
    @ObservationIgnored private var connectionStateSubscription: SignalRSubscription?
    @ObservationIgnored private var lastObservedConnectionState: SignalRConnectionState?

    @ObservationIgnored private var refreshOwnerToken: UUID?
    @ObservationIgnored private var refreshOwnerTask: Task<Void, Never>?
    @ObservationIgnored private var refreshRequested = false
    @ObservationIgnored private var refreshWaiters:
        [UUID: CheckedContinuation<Bool, Never>] = [:]

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
        let connectionRegistration = signalRService.onConnectionStateChanged { [weak self] state in
            enqueue { [weak self] in
                guard let self,
                      self.matchesAuthority(
                        epoch: epoch,
                        taskIdentity: newTaskIdentity,
                        signalRIdentity: newSignalRIdentity
                      ) else {
                    return
                }
                let previous = self.lastObservedConnectionState
                self.lastObservedConnectionState = state
                guard previous == .reconnecting, state == .connected else {
                    return
                }
                _ = await self.requestCanonicalRefresh(
                    epoch: epoch,
                    service: taskService,
                    shiftPlanEnabled: shiftPlanEnabled
                )
            }
        }
        lastObservedConnectionState = connectionRegistration.initial
        connectionStateSubscription = connectionRegistration.subscription
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

                refreshWaiters[waiterID] = continuation
                if refreshOwnerToken != nil {
                    refreshRequested = true
                    return
                }

                startRefreshOwner(
                    epoch: epoch,
                    service: service,
                    shiftPlanEnabled: shiftPlanEnabled
                )
            }
        } onCancel: {
            Task { @MainActor [weak self] in
                self?.cancelRefreshWaiter(id: waiterID, epoch: epoch)
            }
        }
    }

    private func startRefreshOwner(
        epoch: UInt64,
        service: any ShiftTaskServiceProtocol,
        shiftPlanEnabled: Bool
    ) {
        let token = UUID()
        refreshOwnerToken = token
        refreshRequested = false
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
        var lastPassSucceeded = false

        while matchesRefreshOwner(
            epoch: epoch,
            token: token,
            service: service
        ) {
            refreshRequested = false
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
                    lastPassSucceeded = false
                } else {
                    loadFailure = nil
                    lastPassSucceeded = true
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
                lastPassSucceeded = false
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
                lastPassSucceeded = false
            }

            guard !refreshRequested else { continue }
            finishRefreshOwner(token: token, succeeded: lastPassSucceeded)
            return
        }
    }

    private func finishRefreshOwner(token: UUID, succeeded: Bool) {
        guard refreshOwnerToken == token else { return }
        refreshOwnerTask = nil
        refreshOwnerToken = nil
        refreshRequested = false
        isRefreshing = false
        resumeRefreshWaiters(returning: succeeded)
    }

    private func cancelRefreshWaiter(id: UUID, epoch: UInt64) {
        guard lifecycleEpoch == epoch,
              let continuation = refreshWaiters.removeValue(forKey: id) else {
            return
        }
        continuation.resume(returning: false)
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
        connectionStateSubscription?.cancel()
        connectionStateSubscription = nil
        lastObservedConnectionState = nil

        refreshOwnerTask?.cancel()
        refreshOwnerTask = nil
        refreshOwnerToken = nil
        refreshRequested = false
        isRefreshing = false
        resumeRefreshWaiters(returning: false)

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
        refreshRequested = false
        isRefreshing = false
        if snapshot == nil, phase == .loading {
            phase = .idle
        }
    }

    private func resumeRefreshWaiters(returning value: Bool) {
        let continuations = Array(refreshWaiters.values)
        refreshWaiters.removeAll()
        continuations.forEach { $0.resume(returning: value) }
    }
}
