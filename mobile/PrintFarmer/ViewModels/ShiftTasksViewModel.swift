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

    /// Bounded, thread-safe coalescing mailbox for supported invalidations. The
    /// SignalR hub delivers events synchronously on its serial coordinator queue;
    /// a burst of N events schedules AT MOST ONE MainActor drain task via this
    /// mailbox instead of one hop task per event (issue #814, blocker 1). Recreated
    /// per lifecycle in `configure` so a stale drain can never touch fresh state.
    @ObservationIgnored private var invalidationMailbox: ShiftTaskInvalidationMailbox?

    @ObservationIgnored private var refreshOwnerToken: UUID?
    @ObservationIgnored private var refreshOwnerTask: Task<Void, Never>?

    /// One shared completion per live generation. Every explicit caller (refresh /
    /// retry / post-mutation load) and the single pass-initiating invalidation that
    /// target the same generation await the SAME object and receive the identical
    /// result exactly once. Adding the 2nd..Nth caller for a generation adds NO new
    /// entry here — the coalescer's per-generation state is O(live generations),
    /// never O(caller count N), and sustained storm invalidations park nothing at
    /// all (they coalesce into `coalescedDemand`). Bounded by concurrent user
    /// actions and live generations, both independent of invalidation volume.
    @ObservationIgnored private var generationCompletions:
        [RefreshGeneration: GenerationCompletion] = [:]

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

    /// Deterministic test seam awaited by the refresh owner immediately BEFORE it
    /// marks its pass begun and issues `loadSnapshot`. Production never assigns it
    /// (a single `?.` nil check). Tests use it to hold the owner in the
    /// reserved-but-not-begun window so real callers can register against the same
    /// first pass (issue #814, blocker 2/4).
    @ObservationIgnored
    var ownerPassWillBeginHook: (@MainActor @Sendable () async -> Void)?

    /// Deterministic test seam fired with the result of the single pass-initiating
    /// invalidation's awaited generation, so tests can count that terminal path
    /// exactly once (issue #814, blocker 4). Production never assigns it.
    @ObservationIgnored
    var invalidationCompletionObserver: (@MainActor @Sendable (Bool) -> Void)?

    /// Deterministic test seam awaited by an explicit refresh caller in the window
    /// AFTER it has reserved a covering generation but BEFORE it registers its
    /// waiter. Production never assigns it (a single `?.` nil check). Tests use it to
    /// park a caller mid-reservation so cancellation can land in the exact
    /// reserve→registration race the pre-cancellation contract must clean up (issue
    /// #814 authoritative pre-cancellation contract).
    @ObservationIgnored
    var preRegistrationHook: (@MainActor @Sendable () async -> Void)?

    /// Deterministic test seam fired SYNCHRONOUSLY on the MainActor in the window
    /// AFTER an explicit caller's early cancellation check but BEFORE it stores its
    /// waiter (`completion.addAwaiter`). Because it must not suspend, a test can
    /// cancel the caller's own task here so cancellation becomes visible EXACTLY at
    /// the waiter-storage linearization point — proving the sticky post-`addAwaiter`
    /// recheck resolves the caller inline (no queued cleanup that could lose
    /// executor order to the already-enqueued owner). Production never assigns it (a
    /// single `?.` nil check).
    @ObservationIgnored
    var preAddAwaiterHook: (@MainActor @Sendable () -> Void)?
    #endif

    /// Overflow-safe, totally-ordered refresh generation. Ordering is lexicographic
    /// on `(epoch, sequence)`. Allocation increments `sequence`; on `sequence`
    /// saturation it rolls into the next `epoch`; and if BOTH fields saturate, the
    /// coalescer renormalizes the bounded live-generation set back onto a compact
    /// low range (see `renormalizeGenerations`), so a later generation always
    /// compares strictly greater than an earlier one with no wrapping and no trap
    /// (issue #814, blocker 3).
    private struct RefreshGeneration: Comparable, Hashable {
        var epoch: UInt64
        var sequence: UInt64

        static func < (lhs: RefreshGeneration, rhs: RefreshGeneration) -> Bool {
            (lhs.epoch, lhs.sequence) < (rhs.epoch, rhs.sequence)
        }
    }

    /// One shared completion object per live generation. All callers targeting a
    /// generation await this single object and observe the identical result exactly
    /// once, including late joiners (via the cached `resolvedResult`). A per-caller
    /// continuation is retained ONLY to honor INDEPENDENT caller cancellation — a
    /// single caller abandoning must resolve to `false` immediately without
    /// disturbing the shared result its peers still await, a semantic the shipped
    /// `testP2Cancellation` itself requires. The shared RESULT is computed once and
    /// fanned out, so N callers never create N distinct results or N coalescer
    /// entries.
    @MainActor
    private final class GenerationCompletion {
        private var awaiters: [UUID: CheckedContinuation<Bool, Never>] = [:]
        private(set) var resolvedResult: Bool?

        var awaiterCount: Int { awaiters.count }
        var isResolved: Bool { resolvedResult != nil }

        /// Registers a caller. If the generation already resolved, resumes the
        /// caller immediately with the shared result and stores nothing.
        func addAwaiter(_ id: UUID, _ continuation: CheckedContinuation<Bool, Never>) {
            if let resolvedResult {
                continuation.resume(returning: resolvedResult)
                return
            }
            awaiters[id] = continuation
        }

        /// Publishes the shared result once, resuming every awaiter with the SAME
        /// value. Drains the map before resuming so re-entrancy cannot double-resume.
        func resolve(_ value: Bool) {
            guard resolvedResult == nil else { return }
            resolvedResult = value
            let pending = awaiters
            awaiters.removeAll()
            for continuation in pending.values {
                continuation.resume(returning: value)
            }
        }

        /// Removes a single cancelled caller, resuming ONLY it with `false`. Returns
        /// whether the caller was present so the coalescer can update its
        /// abandon-on-empty accounting.
        @discardableResult
        func cancelAwaiter(_ id: UUID) -> Bool {
            guard let continuation = awaiters.removeValue(forKey: id) else { return false }
            continuation.resume(returning: false)
            return true
        }

        /// Teardown / stale replacement: resume every awaiter with `false` exactly
        /// once WITHOUT marking the generation resolved, so no stale success is ever
        /// published.
        func resumeAllForTeardown() {
            let pending = awaiters
            awaiters.removeAll()
            for continuation in pending.values {
                continuation.resume(returning: false)
            }
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
        let mailbox = ShiftTaskInvalidationMailbox()
        self.invalidationMailbox = mailbox
        taskInvalidationSubscription = signalRService.onTaskInvalidated { [weak self] invalidation in
            // Runs on the SignalR hub's serial coordinator queue (off the
            // MainActor). Filter unsupported targets and coalesce into the bounded
            // mailbox HERE, so a synchronous burst of N events schedules AT MOST ONE
            // MainActor drain task — never one hop task per event (issue #814).
            guard ShiftTaskInvalidation.supportedTargets.contains(invalidation.target) else {
                return
            }
            guard mailbox.deposit() else { return }
            enqueue { [weak self] in
                await self?.drainInvalidationMailbox(
                    mailbox: mailbox,
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
        // Side-effect-free pre-cancellation (issue #814 authoritative contract): a
        // caller already cancelled before it can reserve demand returns `false`
        // having performed ZERO service loads and ZERO publication. Evaluating
        // cancellation BEFORE `reserveCoveringGeneration` guarantees the sole
        // pre-cancelled caller never starts an owner/pass at all.
        guard !Task.isCancelled,
              matchesAuthority(
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

        #if DEBUG
        // Deterministic race seam: hold the caller in the reserve→registration
        // window so a test can cancel it AFTER a reservation exists but BEFORE it
        // registers, exercising the demandless-reservation cleanup path. Production
        // leaves the hook nil.
        if let hook = preRegistrationHook {
            await hook()
        }
        #endif

        return await awaitGeneration(
            reservation.generation,
            didStartOwner: reservation.didStartOwner,
            epoch: epoch
        )
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

        let result = await awaitGeneration(
            reservation.generation,
            didStartOwner: reservation.didStartOwner,
            epoch: epoch
        )
        #if DEBUG
        invalidationCompletionObserver?(result)
        #endif
    }

    /// Drains the bounded invalidation mailbox on the MainActor. Exactly one drain
    /// task is ever scheduled per not-draining → draining transition (see
    /// `ShiftTaskInvalidationMailbox`); this loop then services all coalesced demand
    /// and atomically ends the drain when none remains, so demand raised during the
    /// loop is never lost yet no second task is spawned. Authority is fenced before
    /// each ingest so a stale drain (post-reconfigure / teardown) does no work.
    private func drainInvalidationMailbox(
        mailbox: ShiftTaskInvalidationMailbox,
        epoch: UInt64,
        service: any ShiftTaskServiceProtocol,
        shiftPlanEnabled: Bool
    ) async {
        while mailbox.takePending() {
            guard matchesAuthority(
                epoch: epoch,
                taskIdentity: ObjectIdentifier(service as AnyObject),
                signalRIdentity: signalRServiceIdentity
            ) else {
                mailbox.reset()
                return
            }
            await ingestSupportedInvalidation(
                epoch: epoch,
                service: service,
                shiftPlanEnabled: shiftPlanEnabled
            )
        }
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

    /// Hands out the next totally-ordered generation. Normally increments
    /// `sequence`; on `sequence` saturation it rolls into the next `epoch`; and if
    /// BOTH `epoch` and `sequence` are saturated it renormalizes the bounded set of
    /// live generations onto a compact low range first. The result is a strict total
    /// order that never wraps and never traps (issue #814, blocker 3).
    private func allocateGeneration() -> RefreshGeneration {
        if generationCursor.sequence == UInt64.max {
            if generationCursor.epoch == UInt64.max {
                // Both fields saturated — renormalize the (tiny) live set down to
                // `(0, 1...k)`, resetting the cursor low so allocation can resume.
                renormalizeGenerations()
            } else {
                generationCursor = RefreshGeneration(
                    epoch: generationCursor.epoch + 1,
                    sequence: 1
                )
                return generationCursor
            }
        }
        generationCursor.sequence += 1
        return generationCursor
    }

    /// Actor-isolated (MainActor) renormalization invoked only when the generation
    /// cursor fully saturates. Because at most a handful of generations are ever
    /// live (running, pending, last-completed, coalesced demand, and each parked
    /// shared completion), it remaps that bounded set onto a compact `(0, 1...k)`
    /// range that PRESERVES their exact relative order, and rewrites every
    /// reference — including the shared-completion dictionary keys — consistently.
    /// Parked callers are cancelled by waiter id (not by generation value), so key
    /// remapping never severs a waiter from its completion. Allocation then resumes
    /// from a low cursor without wrapping or trapping.
    private func renormalizeGenerations() {
        var live = Set<RefreshGeneration>()
        if let runningGeneration { live.insert(runningGeneration) }
        if let pendingGeneration { live.insert(pendingGeneration) }
        if let lastCompletedGeneration { live.insert(lastCompletedGeneration) }
        live.formUnion(coalescedDemand)
        live.formUnion(generationCompletions.keys)

        let ordered = live.sorted()
        var mapping: [RefreshGeneration: RefreshGeneration] = [:]
        for (index, generation) in ordered.enumerated() {
            mapping[generation] = RefreshGeneration(epoch: 0, sequence: UInt64(index + 1))
        }

        func remap(_ generation: RefreshGeneration?) -> RefreshGeneration? {
            guard let generation else { return nil }
            return mapping[generation] ?? generation
        }

        runningGeneration = remap(runningGeneration)
        pendingGeneration = remap(pendingGeneration)
        lastCompletedGeneration = remap(lastCompletedGeneration)
        coalescedDemand = Set(coalescedDemand.map { mapping[$0] ?? $0 })

        var remappedCompletions: [RefreshGeneration: GenerationCompletion] = [:]
        for (generation, completion) in generationCompletions {
            remappedCompletions[mapping[generation] ?? generation] = completion
        }
        generationCompletions = remappedCompletions

        generationCursor = ordered.isEmpty
            ? RefreshGeneration(epoch: 0, sequence: 0)
            : RefreshGeneration(epoch: 0, sequence: UInt64(ordered.count))
    }

    /// Awaits the SHARED completion for `generation`, resolving exactly once when a
    /// covering pass completes (or immediately if that generation already completed,
    /// or `false` on cancellation / stale authority). All callers of the same
    /// generation share one completion object; cancellation is per caller by id and
    /// resolves only that caller to `false`.
    private func awaitGeneration(
        _ generation: RefreshGeneration,
        didStartOwner: Bool,
        epoch: UInt64
    ) async -> Bool {
        let waiterID = UUID()
        return await withTaskCancellationHandler {
            await withCheckedContinuation { continuation in
                // Stale authority / torn-down lifecycle takes precedence over
                // cancellation: resume `false` and leave state to teardown.
                guard isActive, lifecycleEpoch == epoch else {
                    continuation.resume(returning: false)
                    return
                }
                // Cancellation landing after reservation but before registration:
                // resolve `false` and unwind any reservation this caller alone owns
                // so no demandless owner/pass can issue a load or publish. Peer
                // demand on the same generation is left untouched (issue #814
                // authoritative pre-cancellation contract).
                if Task.isCancelled {
                    unwindDemandlessReservation(
                        generation,
                        didStartOwner: didStartOwner,
                        epoch: epoch
                    )
                    continuation.resume(returning: false)
                    return
                }
                if let completed = lastCompletedGeneration, generation <= completed {
                    continuation.resume(returning: lastCompletedResult)
                    return
                }
                #if DEBUG
                preAddAwaiterHook?()
                #endif
                let completion = generationCompletions[generation] ?? {
                    let created = GenerationCompletion()
                    generationCompletions[generation] = created
                    return created
                }()
                completion.addAwaiter(waiterID, continuation)
                // LINEARIZATION POINT: waiter storage. A sticky re-check of
                // cancellation in the SAME synchronous MainActor continuation body
                // closes the race where cancellation lands after the early check but
                // at/before `addAwaiter`: the queued `onCancel` handler could lose
                // executor order to the already-enqueued owner and permit one load.
                // Cleaning up inline here (no yield) provably beats the owner, so a
                // pre-registration cancellation performs zero load and zero
                // publication. The `onCancel` handler stays correct/idempotent for
                // cancellation AFTER registration (below).
                if Task.isCancelled {
                    finalizeInlineCancellation(
                        waiterID: waiterID,
                        generation: generation,
                        didStartOwner: didStartOwner,
                        epoch: epoch
                    )
                    return
                }
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

    /// Inline (no-yield) cleanup for a caller cancelled exactly at the
    /// waiter-storage linearization point. Removes and resolves ONLY this caller's
    /// waiter to `false` exactly once, drops the now-empty completion it may have
    /// created, then retires any demandless reservation this caller alone owns —
    /// all synchronously, before the continuation body yields, so no enqueued owner
    /// can interleave a load. Idempotent with the queued `onCancel` handler: if this
    /// ran first, `cancelRefreshWaiter` finds no waiter and no-ops (exactly-once
    /// holds either order). Peer awaiter / coalesced / live-mailbox demand is left
    /// untouched (issue #814 authoritative pre-cancellation contract).
    private func finalizeInlineCancellation(
        waiterID: UUID,
        generation: RefreshGeneration,
        didStartOwner: Bool,
        epoch: UInt64
    ) {
        guard let completion = generationCompletions[generation],
              completion.cancelAwaiter(waiterID) else {
            return
        }
        if completion.awaiterCount == 0, !completion.isResolved {
            generationCompletions.removeValue(forKey: generation)
        }
        unwindDemandlessReservation(
            generation,
            didStartOwner: didStartOwner,
            epoch: epoch
        )
    }

    /// Retires a reservation whose sole owner cancelled in the reserve→registration
    /// window — but ONLY when no peer still depends on that generation. If any peer
    /// awaiter or coalesced invalidation targets the generation, everything is left
    /// intact so peer coverage and the single bounded owner continue unaffected
    /// (issue #814 authoritative pre-cancellation contract).
    private func unwindDemandlessReservation(
        _ generation: RefreshGeneration,
        didStartOwner: Bool,
        epoch: UInt64
    ) {
        guard isActive, lifecycleEpoch == epoch else { return }

        // Peer demand present (another parked awaiter or a coalesced invalidation):
        // never cancel, steal, or delay it.
        if let completion = generationCompletions[generation], completion.awaiterCount > 0 {
            return
        }
        if coalescedDemand.contains(generation) {
            return
        }

        // Drop an empty, unresolved completion the cancelled caller may have created.
        if let completion = generationCompletions[generation],
           completion.awaiterCount == 0,
           !completion.isResolved {
            generationCompletions.removeValue(forKey: generation)
        }

        // Live but not-yet-drained ingress-mailbox demand is peer demand too: an
        // off-MainActor supported invalidation may be deposited (pendingDemand) or a
        // drain may be mid-flight (draining) while these MainActor collections are
        // momentarily empty. A pending drain relies on the CURRENT owner/pending
        // state to cover that deposit, so retire nothing here — otherwise cancellation
        // would abandon/steal peer invalidation demand (issue #814). The mailbox lock
        // serializes this read against `deposit()`, so no deposit is lost: one landing
        // after this read schedules its own drain (covered, not duplicated).
        if invalidationMailbox?.hasLiveDemand == true {
            return
        }

        // A demandless queued pending pass: cancel just that reservation.
        if pendingGeneration == generation {
            pendingGeneration = nil
            return
        }

        // This caller itself started the running owner, the pass has not begun, and
        // nothing else needs it: retire the owner before it can issue a load. The
        // extra guards ensure `abandonRefreshOwner` (which clears ALL coalesced
        // demand) cannot strip peer work that lives on other generations.
        if didStartOwner,
           runningGeneration == generation,
           !runningPassStarted,
           pendingGeneration == nil,
           coalescedDemand.isEmpty,
           generationCompletions.isEmpty {
            abandonRefreshOwner()
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
            guard runningGeneration != nil else { return }
            #if DEBUG
            // Reserved-but-not-begun window: real callers can register against THIS
            // pass right up until it begins. Production leaves the hook nil.
            if let hook = ownerPassWillBeginHook {
                await hook()
                guard matchesRefreshOwner(
                    epoch: epoch,
                    token: token,
                    service: service
                ) else {
                    return
                }
            }
            #endif
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
    ///
    /// `runningGeneration` is read FRESH rather than captured, so if a concurrent
    /// allocation renormalized the generation set while `loadSnapshot` was awaiting,
    /// this still resolves the correct (remapped) generation (issue #814, blocker 3).
    private func completePass(
        token: UUID,
        succeeded: Bool
    ) {
        guard refreshOwnerToken == token else { return }
        guard let generation = runningGeneration else { return }

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

    /// Cancels a single caller by waiter id, searching across all live generation
    /// completions (the id, not the generation value, is the stable key — so a
    /// mid-flight renormalization that remapped generation values cannot sever the
    /// caller from its completion). Resolves only THIS caller to `false`; peers
    /// awaiting the same shared generation are untouched (issue #814, blocker 2).
    private func cancelRefreshWaiter(
        id: UUID,
        epoch: UInt64
    ) {
        guard lifecycleEpoch == epoch else { return }

        var didCancel = false
        for (generation, completion) in generationCompletions {
            if completion.cancelAwaiter(id) {
                didCancel = true
                if completion.awaiterCount == 0, !completion.isResolved {
                    generationCompletions.removeValue(forKey: generation)
                }
                break
            }
        }
        guard didCancel else { return }

        // Only abandon the owner once there is genuinely no outstanding demand:
        // no parked continuation, no coalesced invalidation still awaiting a
        // covering pass, and no live-but-undrained ingress-mailbox demand (an
        // off-MainActor deposit / in-flight drain that the running owner still
        // covers). Excluding the mailbox predicate would let caller cancellation
        // abandon an owner a supported invalidation still relies on (issue #814).
        if generationCompletions.isEmpty,
           coalescedDemand.isEmpty,
           invalidationMailbox?.hasLiveDemand != true {
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
        invalidationMailbox?.reset()
        invalidationMailbox = nil

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

    /// Resolves every shared completion whose target generation is covered by the
    /// just-completed `generation`, removing each from the map BEFORE resuming so no
    /// completion can be resolved twice even under re-entrant registration. Each
    /// completion fans its single shared result out to all its callers at once.
    private func resumeRefreshWaiters(
        coveringThrough generation: RefreshGeneration,
        returning value: Bool
    ) {
        let coveredKeys = generationCompletions.keys.filter { $0 <= generation }
        guard !coveredKeys.isEmpty else { return }
        var completions: [GenerationCompletion] = []
        for key in coveredKeys {
            if let completion = generationCompletions.removeValue(forKey: key) {
                completions.append(completion)
            }
        }
        for completion in completions {
            completion.resolve(value)
        }
    }

    /// Resolves ALL shared completions exactly once WITHOUT publishing success
    /// (teardown / server replacement). The map is drained before any resume so
    /// re-entrancy cannot observe a resumed completion still registered, and no
    /// stale success is ever published.
    private func resumeAllRefreshWaiters(returning value: Bool) {
        let completions = generationCompletions
        generationCompletions.removeAll()
        for completion in completions.values {
            if value {
                completion.resolve(true)
            } else {
                completion.resumeAllForTeardown()
            }
        }
    }

    #if DEBUG
    /// Number of parked continuations across all live generation completions.
    /// Bounded by concurrent explicit callers plus at most one pass-initiating
    /// invalidation — independent of invalidation volume.
    var pendingRefreshWaiterCountForTesting: Int {
        generationCompletions.values.reduce(0) { $0 + $1.awaiterCount }
    }

    /// Number of live shared-completion objects (one per live generation). This is
    /// the coalescer's per-generation state size: it stays O(live generations) and
    /// NEVER grows with the number of callers N targeting a generation (issue #814,
    /// blocker 2).
    var liveGenerationCompletionCountForTesting: Int { generationCompletions.count }

    var refreshWaiterTargetGenerationsForTesting: [UInt64] {
        generationCompletions.flatMap { generation, completion in
            Array(repeating: generation.sequence, count: completion.awaiterCount)
        }
        .sorted()
    }

    /// Number of MainActor drain tasks the invalidation mailbox has scheduled since
    /// the current lifecycle began. A burst of N supported invalidations delivered
    /// while the MainActor is occupied schedules AT MOST ONE additional drain, so
    /// this count is N-independent (issue #814, blocker 1).
    var invalidationMailboxScheduleCountForTesting: Int {
        invalidationMailbox?.scheduleCountForTesting ?? 0
    }

    /// Number of supported invalidations deposited into the mailbox this lifecycle.
    /// Grows with event count (proving events are observed) while the schedule count
    /// stays constant (proving drains are coalesced).
    var invalidationMailboxDepositCountForTesting: Int {
        invalidationMailbox?.depositCountForTesting ?? 0
    }

    /// Whether the ingress mailbox currently holds live (undrained) demand or an
    /// active drain cycle. Lets a proof assert a caller-cancellation cleanup did NOT
    /// abandon an owner a deposited-but-undrained supported invalidation relies on
    /// (issue #814).
    var invalidationMailboxHasLiveDemandForTesting: Bool {
        invalidationMailbox?.hasLiveDemand ?? false
    }

    var isRefreshOwnerActiveForTesting: Bool { refreshOwnerToken != nil }

    /// The live refresh-owner task handle, so a race proof can deterministically
    /// join a reserved-but-pre-begin (and possibly abandoned) owner after releasing
    /// its gate — no polling. Nil when no owner is active.
    var refreshOwnerTaskForTesting: Task<Void, Never>? { refreshOwnerTask }

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

    /// Full `(epoch, sequence)` tokens for the running / pending / last-completed
    /// generations, so a renormalization proof can assert the entire live set was
    /// remapped onto a compact low range while preserving relative order (issue
    /// #814, blocker 3).
    var runningGenerationTokenForTesting: (epoch: UInt64, sequence: UInt64)? {
        runningGeneration.map { ($0.epoch, $0.sequence) }
    }

    var pendingGenerationTokenForTesting: (epoch: UInt64, sequence: UInt64)? {
        pendingGeneration.map { ($0.epoch, $0.sequence) }
    }

    var liveGenerationCompletionTokensForTesting: [(epoch: UInt64, sequence: UInt64)] {
        generationCompletions.keys
            .map { ($0.epoch, $0.sequence) }
            .sorted { ($0.epoch, $0.sequence) < ($1.epoch, $1.sequence) }
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

/// Bounded, thread-safe coalescing mailbox that decouples the number of supported
/// SignalR invalidation events from the number of MainActor drain tasks scheduled.
///
/// The SignalR hub delivers events synchronously on its serial coordinator queue
/// (off the MainActor). Without coalescing, a burst of N events while the MainActor
/// is occupied would enqueue N hop tasks and, transitively, risk N parked waiters.
/// This mailbox guarantees that AT MOST ONE drain task is in flight at a time,
/// independent of event count, while never losing a deposit raised during a
/// drain/reschedule race:
///
/// - `deposit()` sets pending demand and returns `true` ONLY on the idle → draining
///   transition, so exactly one caller is told to schedule the single drain.
/// - `takePending()` clears-and-checks demand under the SAME lock; when it finds no
///   demand it atomically ends the draining state. Because deposit's set and
///   takePending's clear share one lock, a deposit that races the end-of-drain
///   either keeps the current drain alive (demand still set) or wins the next
///   idle → draining transition (schedules a fresh drain) — never both, never
///   neither.
///
/// `@unchecked Sendable` is sound: all mutable state is guarded by `lock`.
private final class ShiftTaskInvalidationMailbox: @unchecked Sendable {
    private let lock = NSLock()
    private var pendingDemand = false
    private var draining = false

    #if DEBUG
    private var depositCount = 0
    private var scheduleCount = 0
    var depositCountForTesting: Int { lock.withLock { depositCount } }
    var scheduleCountForTesting: Int { lock.withLock { scheduleCount } }
    #endif

    /// Records one unit of demand. Returns `true` exactly once per idle → draining
    /// transition; the sole `true` recipient must schedule exactly one drain.
    func deposit() -> Bool {
        lock.lock()
        defer { lock.unlock() }
        #if DEBUG
        depositCount += 1
        #endif
        pendingDemand = true
        if draining {
            return false
        }
        draining = true
        #if DEBUG
        scheduleCount += 1
        #endif
        return true
    }

    /// Read-only snapshot: `true` while unconsumed demand exists OR a drain cycle is
    /// active (deposit → scheduled-drain → ingest window). A caller-cancellation
    /// cleanup on the MainActor consults this before abandoning an owner so it never
    /// discards work a supported invalidation still relies on. Taken under the same
    /// lock as `deposit`/`takePending`, so it never observes a torn deposit and a
    /// deposit racing just after the read still schedules its own drain.
    var hasLiveDemand: Bool {
        lock.withLock { pendingDemand || draining }
    }

    /// Consumes pending demand for one drain iteration. Returns `true` if there was
    /// demand to service; otherwise atomically clears the draining state and returns
    /// `false`, ending the drain loop.
    func takePending() -> Bool {
        lock.lock()
        defer { lock.unlock() }
        if pendingDemand {
            pendingDemand = false
            return true
        }
        draining = false
        return false
    }

    /// Clears all state (authority change / teardown), so an outstanding drain
    /// observes no demand and exits without touching fresh lifecycle state.
    func reset() {
        lock.lock()
        defer { lock.unlock() }
        pendingDemand = false
        draining = false
    }
}
