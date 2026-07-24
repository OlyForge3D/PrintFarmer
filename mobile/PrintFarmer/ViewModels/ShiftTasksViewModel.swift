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

    /// Parked continuations awaiting a canonical pass, bucketed by the generation
    /// they target. Only explicit callers (refresh / retry / post-mutation load)
    /// and the single pass-initiating invalidation ever park here. Sustained storm
    /// invalidations coalesce WITHOUT parking a continuation, so the parked count
    /// is bounded by the number of concurrent explicit callers and is independent
    /// of invalidation volume (issue #814).
    @ObservationIgnored private var refreshWaiters:
        [RefreshGeneration: [UUID: CheckedContinuation<Bool, Never>]] = [:]

    /// Generations that have coalesced fire-and-forget demand from supported
    /// invalidations that did not park a continuation. Keyed by generation so the
    /// footprint is bounded by the number of live generations (at most two), never
    /// by invalidation volume. A generation stays here until a covering pass
    /// completes; it keeps the owner from being abandoned while demand is pending.
    @ObservationIgnored private var coalescedDemand: Set<RefreshGeneration> = []

    /// Total-ordered, overflow-safe generation token. `generationCursor` is the
    /// last value handed out; ordering is lexicographic on `(epoch, sequence)` so
    /// comparisons never depend on wrapping arithmetic or a zero sentinel.
    @ObservationIgnored private var generationCursor = RefreshGeneration(epoch: 0, sequence: 0)

    /// Generation of the pass the owner is currently servicing, plus whether that
    /// pass's `loadSnapshot` has begun. A caller registers against the running
    /// generation only while its load has NOT yet begun (it will still observe the
    /// caller's effect); once the load is in flight the caller must be covered by
    /// the next, pending pass.
    @ObservationIgnored private var runningGeneration: RefreshGeneration?
    @ObservationIgnored private var runningPassStarted = false

    /// At most one pass may be queued ahead of the running one. Every storm
    /// invalidation that arrives while a load is in flight collapses onto this
    /// single reserved generation, bounding scheduled passes to two.
    @ObservationIgnored private var pendingGeneration: RefreshGeneration?

    /// Last fully-completed generation and its published result, used to resolve a
    /// late registrant for an already-covered generation exactly once.
    @ObservationIgnored private var lastCompletedGeneration: RefreshGeneration?
    @ObservationIgnored private var lastCompletedResult = false

    #if DEBUG
    /// Deterministic test seam fired on the MainActor immediately after a
    /// refresh waiter is registered. Production never assigns it (zero cost
    /// beyond a nil check); tests use it as a barrier to observe waiter
    /// registration without sleeps or polling.
    @ObservationIgnored
    var refreshWaiterRegistrationObserver: (@MainActor @Sendable () -> Void)?
    #endif

    /// Overflow-safe, totally-ordered refresh generation. Ordering is lexicographic
    /// on `(epoch, sequence)`; when `sequence` saturates, the epoch increments and
    /// the sequence restarts, so a later generation always compares strictly
    /// greater than an earlier one without any wrapping arithmetic (issue #814).
    private struct RefreshGeneration: Comparable, Hashable {
        var epoch: UInt64
        var sequence: UInt64

        static func < (lhs: RefreshGeneration, rhs: RefreshGeneration) -> Bool {
            (lhs.epoch, lhs.sequence) < (rhs.epoch, rhs.sequence)
        }
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
                await self.ingestSupportedInvalidation(
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

    /// Explicit callers (pull-to-refresh, retry, post-mutation load) always need
    /// the pass result, so they park a continuation for the first generation that
    /// covers them and resolve as soon as THAT generation completes — never at
    /// global refresh quiescence.
    private func requestCanonicalRefresh(
        epoch: UInt64,
        service: any ShiftTaskServiceProtocol,
        shiftPlanEnabled: Bool
    ) async -> Bool {
        guard matchesAuthority(
            epoch: epoch,
            taskIdentity: ObjectIdentifier(service as AnyObject),
            signalRIdentity: signalRServiceIdentity
        ) else {
            return false
        }

        let reservation = reserveCoveringGeneration(
            epoch: epoch,
            service: service,
            shiftPlanEnabled: shiftPlanEnabled
        )
        return await awaitGeneration(reservation.generation, epoch: epoch)
    }

    /// Supported invalidations are pure signals: they only need to guarantee a
    /// canonical pass covers them, not the result. The pass-initiating invalidation
    /// (the one that finds the owner idle) awaits its generation so a single
    /// callback still drives one full load synchronously — the shipped #782
    /// behavior. Every subsequent storm invalidation coalesces onto the already
    /// scheduled work as fire-and-forget: it parks NO continuation and spawns NO
    /// suspended task, so N invalidations cannot grow waiters, tasks, or passes.
    private func ingestSupportedInvalidation(
        epoch: UInt64,
        service: any ShiftTaskServiceProtocol,
        shiftPlanEnabled: Bool
    ) async {
        guard matchesAuthority(
            epoch: epoch,
            taskIdentity: ObjectIdentifier(service as AnyObject),
            signalRIdentity: signalRServiceIdentity
        ) else {
            return
        }

        let reservation = reserveCoveringGeneration(
            epoch: epoch,
            service: service,
            shiftPlanEnabled: shiftPlanEnabled
        )

        guard reservation.didStartOwner else {
            // Coalesced onto an in-flight or already-scheduled pass. Record the
            // demand so the owner is not abandoned before it covers this event,
            // then return without parking a continuation.
            coalescedDemand.insert(reservation.generation)
            return
        }

        _ = await awaitGeneration(reservation.generation, epoch: epoch)
    }

    /// Reserves the generation of the first canonical pass that will begin AFTER
    /// the caller registers, starting the refresh owner if none is running.
    ///
    /// - If the owner is idle, a new generation is allocated and its pass is
    ///   started; the caller is covered by it (`didStartOwner == true`).
    /// - If the running pass has been reserved but its `loadSnapshot` has NOT begun
    ///   yet, the caller joins that running generation (it will still observe the
    ///   caller's effect).
    /// - If a load is already in flight, the caller is covered by the single queued
    ///   pending generation (allocated once, then shared by all later arrivals).
    ///
    /// This makes "first pass whose load begins after registration" the exact
    /// coverage contract, and bounds scheduled passes to two (issue #814).
    private func reserveCoveringGeneration(
        epoch: UInt64,
        service: any ShiftTaskServiceProtocol,
        shiftPlanEnabled: Bool
    ) -> (generation: RefreshGeneration, didStartOwner: Bool) {
        if refreshOwnerToken == nil {
            let generation = allocateGeneration()
            runningGeneration = generation
            runningPassStarted = false
            startRefreshOwner(
                epoch: epoch,
                service: service,
                shiftPlanEnabled: shiftPlanEnabled
            )
            return (generation, true)
        }

        if !runningPassStarted, let running = runningGeneration {
            return (running, false)
        }

        if pendingGeneration == nil {
            pendingGeneration = allocateGeneration()
        }
        // `pendingGeneration` is non-nil here by construction.
        return (pendingGeneration ?? allocateGeneration(), false)
    }

    /// Hands out the next totally-ordered generation. When the sequence saturates,
    /// the epoch is incremented (a checked add that traps rather than wrapping) and
    /// the sequence restarts at 1, so the new generation still compares strictly
    /// greater than every prior one. No wrapping arithmetic, no zero sentinel.
    private func allocateGeneration() -> RefreshGeneration {
        if generationCursor.sequence == UInt64.max {
            generationCursor = RefreshGeneration(
                epoch: generationCursor.epoch + 1,
                sequence: 1
            )
        } else {
            generationCursor.sequence += 1
        }
        return generationCursor
    }

    /// Parks a continuation for `generation`, resolving it exactly once when a
    /// covering pass completes (or immediately if that generation has already
    /// completed, or `false` on cancellation / stale authority).
    private func awaitGeneration(
        _ generation: RefreshGeneration,
        epoch: UInt64
    ) async -> Bool {
        let waiterID = UUID()
        return await withTaskCancellationHandler {
            await withCheckedContinuation { continuation in
                guard !Task.isCancelled,
                      isActive,
                      lifecycleEpoch == epoch else {
                    continuation.resume(returning: false)
                    return
                }
                if let completed = lastCompletedGeneration, generation <= completed {
                    continuation.resume(returning: lastCompletedResult)
                    return
                }
                refreshWaiters[generation, default: [:]][waiterID] = continuation
                #if DEBUG
                refreshWaiterRegistrationObserver?()
                #endif
            }
        } onCancel: {
            Task { @MainActor [weak self] in
                self?.cancelRefreshWaiter(
                    generation: generation,
                    id: waiterID,
                    epoch: epoch
                )
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
            guard let generation = runningGeneration else { return }
            // The load is about to read server state: from this point a newly
            // arriving caller can no longer be guaranteed coverage by THIS pass and
            // must be routed to the next (pending) generation.
            runningPassStarted = true
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
    /// whose target generation is now covered and clears that generation's
    /// coalesced fire-and-forget demand, then either promotes the queued
    /// `pendingGeneration` into the next running pass or retires the owner. This
    /// releases explicit mutation/load callers after the FIRST pass covering their
    /// generation instead of holding them until global quiescence.
    private func completePass(
        token: UUID,
        generation: RefreshGeneration,
        succeeded: Bool
    ) {
        guard refreshOwnerToken == token else { return }

        if lastCompletedGeneration == nil || generation > lastCompletedGeneration! {
            lastCompletedGeneration = generation
            lastCompletedResult = succeeded
        }
        resumeRefreshWaiters(coveringThrough: generation, returning: succeeded)
        coalescedDemand = coalescedDemand.filter { $0 > generation }

        if let pending = pendingGeneration {
            runningGeneration = pending
            pendingGeneration = nil
            runningPassStarted = false
            return
        }

        refreshOwnerTask = nil
        refreshOwnerToken = nil
        runningGeneration = nil
        runningPassStarted = false
        isRefreshing = false
    }

    private func cancelRefreshWaiter(
        generation: RefreshGeneration,
        id: UUID,
        epoch: UInt64
    ) {
        guard lifecycleEpoch == epoch,
              var bucket = refreshWaiters[generation],
              let continuation = bucket.removeValue(forKey: id) else {
            return
        }
        if bucket.isEmpty {
            refreshWaiters.removeValue(forKey: generation)
        } else {
            refreshWaiters[generation] = bucket
        }
        continuation.resume(returning: false)

        // Only abandon the owner once there is genuinely no outstanding demand:
        // no parked continuation and no coalesced invalidation still awaiting a
        // covering pass.
        if refreshWaiters.isEmpty, coalescedDemand.isEmpty {
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
        generationCursor = RefreshGeneration(epoch: 0, sequence: 0)
        runningGeneration = nil
        runningPassStarted = false
        pendingGeneration = nil
        lastCompletedGeneration = nil
        lastCompletedResult = false
        coalescedDemand.removeAll()
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
        runningGeneration = nil
        runningPassStarted = false
        pendingGeneration = nil
        coalescedDemand.removeAll()
        isRefreshing = false
        if snapshot == nil, phase == .loading {
            phase = .idle
        }
    }

    /// Resolves every parked continuation whose target generation is covered by the
    /// just-completed `generation`, removing each from its bucket BEFORE resuming so
    /// no continuation can be resumed twice even under re-entrant registration.
    private func resumeRefreshWaiters(
        coveringThrough generation: RefreshGeneration,
        returning value: Bool
    ) {
        let coveredKeys = refreshWaiters.keys.filter { $0 <= generation }
        guard !coveredKeys.isEmpty else { return }
        var continuations: [CheckedContinuation<Bool, Never>] = []
        for key in coveredKeys {
            if let bucket = refreshWaiters.removeValue(forKey: key) {
                continuations.append(contentsOf: bucket.values)
            }
        }
        for continuation in continuations {
            continuation.resume(returning: value)
        }
    }

    /// Resolves ALL parked continuations exactly once (teardown / server
    /// replacement). Buckets are drained before any resume so re-entrancy cannot
    /// observe a resumed continuation still registered.
    private func resumeAllRefreshWaiters(returning value: Bool) {
        let buckets = refreshWaiters
        refreshWaiters.removeAll()
        for bucket in buckets.values {
            for continuation in bucket.values {
                continuation.resume(returning: value)
            }
        }
    }

    #if DEBUG
    /// Number of parked continuations across all generations. Bounded by concurrent
    /// explicit callers plus at most one pass-initiating invalidation — independent
    /// of invalidation volume.
    var pendingRefreshWaiterCountForTesting: Int {
        refreshWaiters.values.reduce(0) { $0 + $1.count }
    }

    var refreshWaiterTargetGenerationsForTesting: [UInt64] {
        refreshWaiters.flatMap { generation, bucket in
            Array(repeating: generation.sequence, count: bucket.count)
        }
        .sorted()
    }

    var isRefreshOwnerActiveForTesting: Bool { refreshOwnerToken != nil }

    /// Count of canonical passes currently scheduled: the running pass (if any)
    /// plus the single queued pending pass (if any). Bounded to two regardless of
    /// invalidation volume.
    var scheduledPassCountForTesting: Int {
        (runningGeneration != nil ? 1 : 0) + (pendingGeneration != nil ? 1 : 0)
    }

    /// Number of coalesced fire-and-forget invalidation generations awaiting a
    /// covering pass. Bounded by live generations (≤ 2), never by storm size.
    var coalescedDemandCountForTesting: Int { coalescedDemand.count }

    var runningGenerationSequenceForTesting: UInt64? { runningGeneration?.sequence }

    var pendingGenerationSequenceForTesting: UInt64? { pendingGeneration?.sequence }

    /// Drives the REAL reservation path once (using the VM's current authority)
    /// and returns the reserved generation's `(epoch, sequence)` plus whether it
    /// started the owner, so a test can prove first-pass-after-registration
    /// semantics without reaching into internals.
    func debugReserveCoveringGeneration()
        -> (epoch: UInt64, sequence: UInt64, didStartOwner: Bool)? {
        guard isActive, let service = taskService else { return nil }
        let reservation = reserveCoveringGeneration(
            epoch: lifecycleEpoch,
            service: service,
            shiftPlanEnabled: shiftPlanEnabled
        )
        return (
            reservation.generation.epoch,
            reservation.generation.sequence,
            reservation.didStartOwner
        )
    }

    /// Positions the generation cursor for a deterministic rollover proof without
    /// brute-forcing 2^64 allocations.
    func debugSetGenerationCursor(epoch: UInt64, sequence: UInt64) {
        generationCursor = RefreshGeneration(epoch: epoch, sequence: sequence)
    }

    /// Allocates the next generation through the REAL overflow-safe path and
    /// returns its `(epoch, sequence)` so a test can assert total ordering across
    /// the sequence-saturation boundary.
    func debugAllocateGeneration() -> (epoch: UInt64, sequence: UInt64) {
        let generation = allocateGeneration()
        return (generation.epoch, generation.sequence)
    }

    /// Total-ordering comparison over the REAL generation type, used to prove that
    /// a post-rollover generation compares strictly greater than a pre-rollover one.
    func debugGenerationLess(
        lhsEpoch: UInt64,
        lhsSequence: UInt64,
        rhsEpoch: UInt64,
        rhsSequence: UInt64
    ) -> Bool {
        RefreshGeneration(epoch: lhsEpoch, sequence: lhsSequence)
            < RefreshGeneration(epoch: rhsEpoch, sequence: rhsSequence)
    }
    #endif
}
