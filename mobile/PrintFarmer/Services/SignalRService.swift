import Foundation
import os

// MARK: - Test seams for real-transport lifecycle proof (r9 blocker #4)

/// Abstraction over `URLSessionWebSocketTask`. Production uses the
/// stock URLSession task; tests provide a mock that gates `send` and
/// `receive` on continuations so real-transport lifecycle tests can
/// prove handshake/receive suspension behavior without wall-clock
/// pass criteria.
///
/// Only the subset of the WebSocket task API the SignalR service
/// actually calls is exposed. `URLSessionWebSocketTask` conforms via
/// the extension below.
protocol SignalRWebSocket: AnyObject, Sendable {
    func resume()
    func cancel(with closeCode: URLSessionWebSocketTask.CloseCode, reason: Data?)
    func send(_ message: URLSessionWebSocketTask.Message) async throws
    func receive() async throws -> URLSessionWebSocketTask.Message
}

extension URLSessionWebSocketTask: SignalRWebSocket {}

/// Async reconnect sleeper. Production uses `Task.sleep(for:)`.
/// Tests inject a controllable sleeper that resumes exactly when the
/// test releases each attempt so retries can be proven deterministically
/// without wall-clock waits (r9 blocker #4).
typealias SignalRReconnectSleeper = @Sendable (TimeInterval) async -> Void

/// Default production sleeper — wraps `Task.sleep(for:)` and returns
/// silently on cancellation so the reconnect flow can honor its
/// generation/intent recheck immediately after the sleep resumes.
@Sendable
func defaultSignalRReconnectSleeper(_ seconds: TimeInterval) async {
    try? await Task.sleep(for: .seconds(seconds))
}

/// SignalR real-time connection to /hubs/printers.
///
/// Uses URLSessionWebSocketTask with the SignalR JSON protocol (messages
/// delimited by ASCII Record Separator 0x1E). Handles negotiate → WebSocket
/// upgrade → handshake → message loop with auto-reconnect on disconnect.
final class SignalRService: @unchecked Sendable, SignalRServiceProtocol {
    /// Per-service coordinator: private serial queue + service-wide FIFO
    /// delivery guard shared by every hub belonging to this service. Two
    /// separate `SignalRService` instances (e.g. multi-server) each own
    /// their own coordinator and therefore never share hub state.
    private let coordinator = SignalRHubCoordinator(label: "com.printfarmer.signalr.hubs")
    /// One coherent serial-executor hub owns connection-state reads,
    /// mutation, subscription, initial-observation, and ordered transition
    /// delivery. See `SignalRConnectionStateHub` for the invariants.
    private let connectionStateHub: SignalRConnectionStateHub
    /// Event hubs — one per event type — deliver on their own serial
    /// executors so subscription/cancellation is race-free and every
    /// subscriber can be independently cancelled via its returned token.
    private let printerUpdateHub: SignalREventHub<PrinterStatusUpdate>
    private let jobQueueUpdateHub: SignalREventHub<JobQueueUpdate>
    private let attentionChangedHub: SignalREventHub<AttentionChangedEvent>

    /// Race-free connection-state read. Delegates to the hub's serial
    /// executor so any pending `setConnectionState` block has been applied
    /// before the read returns.
    var connectionState: SignalRConnectionState { connectionStateHub.snapshot() }

    private func setConnectionState(_ newValue: SignalRConnectionState) {
        connectionStateHub.setState(newValue)
    }

    private let serverURL: URL
    private let tokenProvider: @Sendable () async -> String?
    private let session: URLSession
    private let decoder: JSONDecoder
    private let logger = Logger(subsystem: "com.printfarmer.ios", category: "SignalR")
    /// Test seam: computes the reconnect back-off duration for a given
    /// attempt number. Production uses 1s, 2s, 4s, 8s, 16s, capped at
    /// 30s (`Self.defaultReconnectBackoff`). Tests inject a compressed
    /// version so real-transport lifecycle tests can prove multi-retry
    /// behavior in bounded wall time without gating on the production
    /// exponential curve.
    private let reconnectBackoff: @Sendable (Int) -> TimeInterval

    static let defaultReconnectBackoff: @Sendable (Int) -> TimeInterval = { attempt in
        min(pow(2.0, Double(max(attempt - 1, 0))), 30.0)
    }

    /// Async reconnect sleeper (r9 blocker #4). Production sleeps via
    /// `Task.sleep`; tests inject a controllable sleeper that resumes
    /// only when the test explicitly releases each attempt, so retry
    /// behavior can be proven without wall-clock timing.
    private let reconnectSleeper: SignalRReconnectSleeper

    /// WebSocket factory (r9 blocker #4). Production returns a
    /// `URLSessionWebSocketTask` from the configured session; tests
    /// return a mock `SignalRWebSocket` whose send/receive resume
    /// through continuations so handshake and receive-loop suspension
    /// can be gated deterministically.
    private let webSocketFactory: @Sendable (URL) -> SignalRWebSocket

    /// 0x1E — ASCII Record Separator, SignalR message terminator
    private static let recordSeparator: UInt8 = 0x1E

    /// Serial queue guarding every mutable lifecycle field below.
    /// `webSocketTask`, `receiveTask`, `pingTask`, `reconnectTask`,
    /// `reconnectAttempt`, `intentionalDisconnect`, and `generation`
    /// are ONLY read/written while executing on this queue. Every
    /// transition (connect / disconnect / receive-error / reconnect
    /// scheduling / socket install / state publish) routes through
    /// `lifecycleSync` so intent + generation are checked atomically
    /// alongside the socket handles they refer to (r7 blocker #2).
    private let lifecycleQueue = DispatchQueue(label: "com.printfarmer.signalr.lifecycle")
    /// Per-instance key used to detect that we're already executing
    /// on `lifecycleQueue`. When present, `lifecycleSync` runs the
    /// closure inline instead of calling `queue.sync` — this keeps
    /// nested lifecycle calls (e.g. `handleMessage` → `processFrame`
    /// type-7 → `scheduleReconnect`, r10 blocker #1) from deadlocking
    /// on the same serial queue while preserving full serial ordering
    /// (the outer caller still executes on the queue, so all state
    /// mutations and hub `coordinator.async` enqueues run in the same
    /// FIFO order as if the nested call had waited on `sync`).
    private let lifecycleSpecificKey = DispatchSpecificKey<UUID>()
    private let lifecycleSpecificValue = UUID()
    private var webSocketTask: SignalRWebSocket?
    private var receiveTask: Task<Void, Never>?
    private var pingTask: Task<Void, Never>?
    /// Coalesced reconnect owner. Set on entry to the reconnect flow and
    /// cleared on exit; a second reconnect trigger while one is already
    /// running is dropped (no accumulation). Paired with an identity
    /// token so ONLY the task that installed the slot may clear it
    /// (r8 blocker #2: prevents a stale reconnect from wiping a newer
    /// scheduled reconnect owner).
    private var reconnectTask: Task<Void, Never>?
    private var reconnectToken: UUID?
    private var reconnectAttempt = 0
    private var intentionalDisconnect = false
    /// Deferred successor request from an immediate receive-failure /
    /// type-7 that arrived while the reconnect slot was still held by
    /// the current retry owner. Keyed by the OWNING owner's token so
    /// the owner-scoped completion watcher (spawned alongside every
    /// retry owner) only schedules a successor after that specific
    /// owner's actual `Task.value` has completed AND the outgoing
    /// receive task's completion has itself been acknowledged (see
    /// `pendingSuccessorReceiveBarrier`). Cleared on `disconnect()`,
    /// `connect()` supersede, retry-terminal exit, and by the
    /// completion watcher on consumption. Never restored as an
    /// unowned global flag: reads require token equality so a stale
    /// pending from a superseded chain cannot spawn a successor for a
    /// different owner. Guarded by `lifecycleQueue`
    /// (`lifecycleSync`). (F4-L #777 externally-awaited successor
    /// ownership contract.)
    private var pendingSuccessorForOwner: UUID?
    /// Receive-completion barrier snapshotted at the moment the
    /// deferred successor request was recorded. This is the barrier
    /// Task for the specific receive-loop task that FAILED and
    /// triggered `scheduleReconnect`'s drop path (i.e. the receive
    /// task installed by the current owner's most recent successful
    /// `performConnect`). It is a Task whose body is
    /// `_ = await task.value; #if DEBUG postDebugLifecycleEvent(...)
    /// #endif; return` — so awaiting `barrier.value` is guaranteed
    /// to complete strictly AFTER the receive task's own body has
    /// exited AND (in DEBUG) after `receiveCompleted` has posted.
    /// The completion watcher awaits this barrier BEFORE posting
    /// `ownerCompleted` and dispatching the successor, which is the
    /// production happens-before edge required by the frozen
    /// `R1.completed < A.completed < B.created` strict order. In
    /// Release the barrier body is `await task.value; return` — the
    /// happens-before edge still holds. Guarded by `lifecycleQueue`.
    /// (F4-L #777 r21 R2 fix.)
    private var pendingSuccessorReceiveBarrier: Task<Void, Never>?
    /// Monotonically incremented every time `pendingSuccessorForOwner`
    /// + `pendingSuccessorReceiveBarrier` are recorded by
    /// `scheduleReconnect`'s drop path. The owner-scoped completion
    /// watcher tracks the LAST version it awaited; if the watcher
    /// re-enters `lifecycleSync` and observes `version >
    /// lastAwaited`, a NEW pending barrier was recorded during the
    /// TOCTOU race window between the watcher's initial snapshot and
    /// its atomic release/consume step (Rubber Duck R2 correction).
    /// The watcher then loops back to await the newly recorded
    /// barrier before dispatching any successor, so token release
    /// and successor dispatch are impossible while any matching
    /// owner-scoped barrier has not been awaited. Guarded by
    /// `lifecycleQueue`. (F4-L #777 r22 R2 fix.)
    private var pendingSuccessorVersion: UInt64 = 0
    /// The receive-completion barrier for the current receive-loop
    /// task, if any. Mirrored 1:1 with `receiveTask`: installed
    /// atomically in `performConnect` step 4 next to
    /// `setReceiveTaskLocked`, cleared in `tearDownLocked`. Used by
    /// `scheduleReconnect`'s drop path to snapshot the barrier that
    /// the completion watcher must await before dispatching the
    /// successor. (F4-L #777 r21 R2 fix.)
    private var receiveCompletionBarrier: Task<Void, Never>?
    /// Owner-scoped finalizing-authority marker. Set atomically by
    /// the owner-completion watcher IMMEDIATELY after `await
    /// reconnectTask.value` returns, BEFORE any release/consume
    /// step. While non-nil, `scheduleReconnect`'s Fix-B branch
    /// scopes recorded successor intent to THIS owner even if
    /// `reconnectToken` has been transiently cleared, so a
    /// same-generation receive task's late `scheduleReconnect` call
    /// (in the r22-CS-race window between its `stillCurrent` check
    /// and its `scheduleReconnect` entry) cannot take the
    /// fresh-reserve path and create a successor before its own
    /// `Task.value` resolves. Cleared inside the watcher's atomic
    /// `.done` step, at the same moment `reconnectToken` is
    /// released. Guarded by `lifecycleQueue`. (F4-L #777 r23 CS-race
    /// fix.)
    private var finalizingOwnerToken: UUID?
    /// Monotonic version bumped every time
    /// `receiveCompletionBarrier` is installed in `performConnect`
    /// step 4. The barrier's own body updates
    /// `receiveBarrierResolvedVersion` after `await task.value`
    /// completes (and, in DEBUG, after `receiveCompleted` has
    /// posted). `installed > resolved` therefore identifies a
    /// specific unresolved receive-barrier instance, distinguishing
    /// "live receive whose Task.value hasn't returned" from "stale
    /// barrier reference already fully awaited". Used by:
    ///   * `scheduleReconnect`'s Fix-B branch to detect a live
    ///     same-generation receive barrier and divert to record-
    ///     intent instead of fresh-reserve.
    ///   * The completion watcher's drain loop to await the current
    ///     receive barrier (in addition to any pending-successor
    ///     barrier) before releasing the slot / dispatching a
    ///     successor.
    /// Guarded by `lifecycleQueue`. (F4-L #777 r23 CS-race fix.)
    private var receiveBarrierInstalledVersion: UInt64 = 0
    /// Companion to `receiveBarrierInstalledVersion`; set from the
    /// barrier Task's own body after `await task.value` resolves.
    /// A barrier install captures `installedVersion` at install
    /// time; when its body finishes, it advances
    /// `receiveBarrierResolvedVersion` to that captured value. A
    /// concurrent scheduleReconnect / watcher observing `installed
    /// > resolved` under `lifecycleSync` therefore knows the
    /// current barrier's underlying receive Task has NOT yet
    /// returned. Guarded by `lifecycleQueue`. (F4-L #777 r23
    /// CS-race fix.)
    private var receiveBarrierResolvedVersion: UInt64 = 0
    /// Monotonic connection generation. Bumped on every explicit
    /// `disconnect()`, on every `tearDown()`, and on every reconnect
    /// scheduling. `performConnect()` snapshots this once at entry and
    /// checks it after every suspension point BEFORE publishing
    /// `.connected` or installing the receive/ping tasks; a stale
    /// resumption whose snapshot no longer matches is discarded so a
    /// slow negotiate/handshake from an earlier attempt cannot publish
    /// `.connected` after the user requested disconnect or a newer
    /// connect ran.
    private var generation: UInt64 = 0

    // Handler storage for #711 F6 `fallbackGroupsUpdated` follows the
    // simple handler-array pattern rather than a SignalREventHub because
    // fallback-group updates are refetch hints with no in-flight lifecycle
    // that would benefit from hub-managed unsubscribe semantics. Printer,
    // job-queue, and attention events were converted to hubs by #777 for
    // lifecycle isolation; #711 F6 intentionally keeps this simpler path.
    private var fallbackGroupsUpdatedHandlers: [@Sendable (FallbackGroupsUpdatedEvent) -> Void] = []
    private let handlerLock = NSLock()

    /// Per-instance lifecycle invariant counters (F4-L r12 blocker
    /// #1). Tests use `.snapshot()` and `waitFor*Zero()` to prove
    /// `maxActiveTransports <= 1`, `maxActiveReceiveLoops <= 1`, and
    /// `maxActiveReconnectOwners <= 1` for required interleavings,
    /// and to synchronize on tear-down completion without polling
    /// elapsed time. Non-vacuous because every increment/decrement
    /// is anchored to the actual mutation of `webSocketTask`,
    /// `receiveTask`, and `reconnectToken` inside `lifecycleSync`.
    let lifecycleInvariants = SignalRLifecycleInvariants()

    init(
        serverURL: URL,
        session: URLSession = .shared,
        tokenProvider: @escaping @Sendable () async -> String?,
        reconnectBackoff: @escaping @Sendable (Int) -> TimeInterval = SignalRService.defaultReconnectBackoff,
        reconnectSleeper: @escaping SignalRReconnectSleeper = defaultSignalRReconnectSleeper,
        webSocketFactory: (@Sendable (URL) -> SignalRWebSocket)? = nil
    ) {
        self.serverURL = serverURL
        self.tokenProvider = tokenProvider
        self.session = session
        self.reconnectBackoff = reconnectBackoff
        self.reconnectSleeper = reconnectSleeper
        // Capture `session` in a local Sendable so the closure escapes
        // safely into the actor-agnostic factory.
        let capturedSession = session
        self.webSocketFactory = webSocketFactory ?? { url in
            capturedSession.webSocketTask(with: url)
        }
        // All hubs share this service's coordinator so nested cross-hub
        // deliveries FIFO through the service-wide guard AND separate
        // services (multi-server) do not share hub state.
        self.connectionStateHub = SignalRConnectionStateHub(coordinator: coordinator)
        self.printerUpdateHub = SignalREventHub<PrinterStatusUpdate>(coordinator: coordinator)
        self.jobQueueUpdateHub = SignalREventHub<JobQueueUpdate>(coordinator: coordinator)
        self.attentionChangedHub = SignalREventHub<AttentionChangedEvent>(coordinator: coordinator)

        self.decoder = JSONDecoder()
        // r10 blocker #1: register a per-instance specific value on
        // the lifecycle queue so `lifecycleSync` can detect reentrant
        // calls and execute the closure inline. Two distinct
        // `SignalRService` instances have distinct values, so
        // reentrancy detection is instance-scoped even though the
        // queue label is shared.
        lifecycleQueue.setSpecific(key: lifecycleSpecificKey, value: lifecycleSpecificValue)
        // Match APIClient's dual-format date decoder — ASP.NET Core emits fractional seconds
        decoder.dateDecodingStrategy = .custom { decoder in
            let container = try decoder.singleValueContainer()
            let text = try container.decode(String.self)
            if let date = APIClient.iso8601WithFractional.date(from: text) { return date }
            if let date = APIClient.iso8601Plain.date(from: text) { return date }
            throw DecodingError.dataCorruptedError(
                in: container,
                debugDescription: "Cannot decode date string: \(text)"
            )
        }
    }

    // MARK: - Lifecycle helpers

    /// Snapshot lifecycle state under the queue. Returns a tuple of the
    /// current generation and intent flag so the caller can validate
    /// after each suspension without holding the queue across an await.
    ///
    /// **r10 blocker #1**: reentrancy-safe. When the caller is already
    /// executing on `lifecycleQueue` (detected via a per-instance
    /// `DispatchSpecificKey`), the closure is run inline — a nested
    /// `queue.sync` on the same serial queue would deadlock. Inline
    /// execution preserves the same serial ordering because the outer
    /// caller is still executing on the queue, so any hub
    /// `coordinator.async` enqueues emitted from the inner block
    /// happen strictly before the outer block returns.
    private func lifecycleSync<T>(_ work: () -> T) -> T {
        if DispatchQueue.getSpecific(key: lifecycleSpecificKey) == lifecycleSpecificValue {
            return work()
        }
        return lifecycleQueue.sync(execute: work)
    }

    // MARK: - Instrumented state setters (r12 blocker #1)
    //
    // Every mutation of `webSocketTask`, `receiveTask`, and
    // `reconnectToken` goes through one of these helpers so the
    // per-instance `lifecycleInvariants` counters see every
    // transition. Callers MUST already hold the lifecycle queue.
    // Counter mutations happen after the state variable is updated
    // so a stale snapshot reader can never see a decremented count
    // with the field still non-nil (or vice versa).

    /// Install `new` as the active WebSocket. Passing `nil` clears
    /// the slot.
    ///
    /// r15 (Hicks item 1): transport lifetime instrumentation. The
    /// live transport is the WebSocket owned by this slot from the
    /// moment it is installed here (post negotiate/upgrade,
    /// pre-handshake — so handshake-only/parked transports ARE
    /// observable) through authoritative teardown or replacement.
    /// Transitions:
    ///   nil → nonnil  = enter (new transport installed)
    ///   nonnil → nil  = exit  (teardown / cancel / disconnect)
    ///   nonnil → nonnil different = exit outgoing, enter incoming
    ///                   (supersede — observable as a brief
    ///                   `activeTransports == 2` only if the outgoing
    ///                   was NOT torn down first; every well-formed
    ///                   supersede path in this service calls
    ///                   `tearDownLocked` before installing the
    ///                   replacement so the counter stays at 1)
    ///   x → x (same) = no-op
    /// Transport lifetime is DISTINCT from receive-loop lifetime; the
    /// receive loop is tracked separately in `makeReceiveTask` and
    /// counts the running receive-Task body only.
    private func setWebSocketTaskLocked(_ new: SignalRWebSocket?) {
        let old = webSocketTask
        // Identity comparison — SignalRWebSocket is a class (AnyObject).
        if old === new { return }
        if old != nil { lifecycleInvariants.exitTransport() }
        if new != nil { lifecycleInvariants.enterTransport() }
        webSocketTask = new
    }

    private func setReceiveTaskLocked(_ new: Task<Void, Never>?) {
        receiveTask = new
    }

    private func setReconnectTokenLocked(_ new: UUID?) {
        reconnectToken = new
    }

    // MARK: - Public API

    func connect() async throws {
        // r9 blockers #1 + #2: reserve generation/intent AND publish
        // `.connecting` inside one lifecycleSync transition. If an
        // earlier reconnect flow still owns the slot, cancel and clear
        // it here so its failure-recursion cannot install a stale
        // replacement while we're mid-connect. Snapshot the cancelled
        // task after we exit the queue.
        //
        // r10 blocker #2b: also tear down any receive/ping/socket that
        // an in-flight reconnect may have installed (e.g. Step-4 of an
        // earlier `performConnect` already published `.connected`
        // before we were called). Bumping generation is not enough —
        // the socket must be closed and the receive/ping tasks
        // cancelled here, atomically with the generation bump, so the
        // superseded transport cannot deliver frames or schedule
        // reconnects under the new generation.
        let (myGen, alreadyLive, superseded): (UInt64, Bool, Task<Void, Never>?) = lifecycleSync {
            let state = connectionStateHub.snapshot()
            if state == .connected || state == .connecting {
                return (self.generation, true, nil)
            }
            let priorReconnect = self.reconnectTask
            self.reconnectTask = nil
            self.setReconnectTokenLocked(nil)
            self.pendingSuccessorForOwner = nil
            self.pendingSuccessorReceiveBarrier = nil
            // r23 (F4-L #777 CS-race fix): a supersede invalidates
            // any in-progress owner-watcher finalizing authority.
            // Clear the marker so `scheduleReconnect`'s Fix-B branch
            // does not record intent against the dead owner.
            self.finalizingOwnerToken = nil
            self.intentionalDisconnect = false
            self.generation &+= 1
            self.reconnectAttempt = 0
            // Cancel the leaked receive/ping/socket, if any (r10 #2b).
            // `tearDownLocked` runs inside the same lifecycleSync so
            // the transport is guaranteed torn down before the new
            // generation is exposed anywhere.
            self.tearDownLocked()
            // Enqueue `.connecting` INSIDE this transition so a
            // concurrent disconnect() that runs AFTER us publishes
            // `.disconnected` STRICTLY AFTER our `.connecting`, and one
            // that runs BEFORE us has already committed its
            // `.disconnected` — the hub coordinator preserves that
            // enqueue order because `setState` uses `coordinator.async`.
            self.connectionStateHub.setState(.connecting)
            return (self.generation, false, priorReconnect)
        }
        if alreadyLive { return }
        // Cancel a superseded reconnect AFTER releasing the lifecycle
        // queue — cancellation itself is safe outside the queue since
        // the token/task have already been cleared.
        superseded?.cancel()

        do {
            try await performConnect(myGen: myGen)
        } catch {
            // Only schedule a reconnect if this connect attempt is still
            // the current generation. Otherwise the user has already
            // disconnected or another connect superseded us — do not
            // enter the reconnect flow.
            let stillCurrent = lifecycleSync {
                self.generation == myGen && !self.intentionalDisconnect
            }
            if stillCurrent {
                scheduleReconnect(fromGen: myGen)
            }
            throw error
        }
    }

    func disconnect() async {
        // Bump generation, tear down, AND enqueue the .disconnected
        // publish all inside one lifecycleQueue block. Because
        // connectionStateHub.setState uses `coordinator.async`, the
        // publish is enqueued FIFO relative to any prior/pending
        // publishes emitted from other lifecycleSync blocks on this
        // service — so a stale performConnect that already exited
        // its own lifecycleSync WITHOUT enqueuing `.connected` cannot
        // publish `.connected` after this `.disconnected`, and one
        // that DID enqueue `.connected` while it was still current
        // will have that state observed before we override it here.
        // (r8 blocker #2: atomic state mutation + publication order.)
        let reconnect: Task<Void, Never>? = lifecycleSync {
            self.intentionalDisconnect = true
            self.generation &+= 1
            self.tearDownLocked()
            let r = self.reconnectTask
            self.reconnectTask = nil
            self.setReconnectTokenLocked(nil)
            self.pendingSuccessorForOwner = nil
            self.pendingSuccessorReceiveBarrier = nil
            // r23 (F4-L #777 CS-race fix): user disconnect
            // invalidates any in-progress owner-watcher finalizing
            // authority. See connect() supersede for full comment.
            self.finalizingOwnerToken = nil
            self.connectionStateHub.setState(.disconnected)
            return r
        }
        reconnect?.cancel()
        logger.info("Disconnected from SignalR hub")
    }

    @discardableResult
    func onConnectionStateChanged(
        _ handler: @escaping @Sendable (SignalRConnectionState) -> Void
    ) -> (initial: SignalRConnectionState, subscription: SignalRSubscription) {
        connectionStateHub.subscribe(handler)
    }

    @discardableResult
    func onPrinterUpdated(_ handler: @escaping @Sendable (PrinterStatusUpdate) -> Void) -> SignalRSubscription {
        printerUpdateHub.subscribe(handler)
    }

    @discardableResult
    func onJobQueueUpdated(_ handler: @escaping @Sendable (JobQueueUpdate) -> Void) -> SignalRSubscription {
        jobQueueUpdateHub.subscribe(handler)
    }

    @discardableResult
    func onAttentionChanged(_ handler: @escaping @Sendable (AttentionChangedEvent) -> Void) -> SignalRSubscription {
        attentionChangedHub.subscribe(handler)
    }

    func onFallbackGroupsUpdated(_ handler: @escaping @Sendable (FallbackGroupsUpdatedEvent) -> Void) {
        handlerLock.lock()
        fallbackGroupsUpdatedHandlers.append(handler)
        handlerLock.unlock()
    }

    // MARK: - Connection Lifecycle

    /// Perform a full negotiate/connect sequence stamped with `myGen`.
    /// After every suspension we validate the generation still matches
    /// the service's current one, so a stale performConnect that has
    /// been superseded by disconnect() or a newer connect() cannot
    /// publish `.connected`, install a socket, or start the receive
    /// loop under the newer intent.
    private func performConnect(myGen: UInt64) async throws {
        // Step 1: Negotiate.
        let token = await tokenProvider()
        try checkGenerationStillCurrent(myGen)

        let negotiateResponse = try await negotiate(jwt: token)
        try checkGenerationStillCurrent(myGen)

        // Step 2: Open WebSocket.
        let connectionToken = negotiateResponse.connectionToken ?? negotiateResponse.connectionId ?? ""
        let task = try makeWebSocketTask(connectionToken: connectionToken, jwt: token)
        // Install ONLY under the lifecycle queue with a generation
        // recheck; if generation moved on we release the task without
        // publishing it. `resume()` runs after the guard passes.
        let installed: Bool = lifecycleSync {
            guard self.generation == myGen, !self.intentionalDisconnect else {
                return false
            }
            self.setWebSocketTaskLocked(task)
            return true
        }
        if !installed {
            task.cancel(with: .normalClosure, reason: nil)
            throw NetworkError.invalidResponse
        }
        task.resume()

        // Step 3: Handshake. `sendHandshake(myGen:)` validates
        // generation both before the send and between send and
        // receive (r8 blocker #2: every suspension inside the
        // handshake path re-checks intent so disconnect() can
        // invalidate mid-handshake).
        try await sendHandshake(myGen: myGen)
        try checkGenerationStillCurrent(myGen)

        // Step 4: Publish `.connected` and start ancillary tasks — all
        // under one lifecycle-queue transition so a concurrent
        // disconnect() cannot squeeze between the state publish and
        // the task start. `.connected` is enqueued INSIDE the
        // lifecycleSync block so publication order across
        // performConnect vs disconnect is fully serialized by the
        // lifecycle queue (r8 blocker #2: atomic state mutation +
        // publication order — the hub's `coordinator.async` FIFO
        // observes lifecycleQueue ordering).
        //
        // r10 blocker #2a: release the reconnect ownership slot HERE,
        // atomically with `.connected` publication and receive-task
        // install. If we reached this point from a reconnect flow, its
        // token still owns `reconnectToken`; without releasing it, a
        // nested `scheduleReconnect(fromGen: myGen)` from an
        // immediately-failing receive would see the slot as still-owned
        // and drop, stranding the service in `.connected` with no live
        // receive loop and no scheduled retry. Clearing the slot here
        // is safe: the reconnect task's outer
        // `clearReconnectSlotIfOwned` becomes a no-op via its token
        // guard, and a normal `connect()` cleared the slot already so
        // this is a redundant no-op there.
        //
        // r20 (F4-L #777 successor-ownership fix): the r10 clear was
        // moved OUT of this step. Eagerly wiping `reconnectToken` /
        // `reconnectTask` before the owner's actual `Task.value`
        // completion allowed an immediate receive failure while the
        // owner was still parked at `handoffOpen` to reserve a fresh
        // slot for a SECOND concurrent owner (violating
        // `maxReconnectOwners <= 1` and the frozen strict order
        // `A.completed < B.created`). The slot is now released only
        // on the owner's own clean exit paths
        // (`clearReconnectSlotIfOwned` on retry-success return, and
        // the retry-exhausted terminal below), and the deferred
        // successor request is dispatched by the owner-scoped
        // completion watcher AFTER `await owner.value` returns. See
        // the `scheduleReconnect` drop path for the pending-request
        // side.
        let started: Bool = lifecycleSync {
            guard self.generation == myGen, !self.intentionalDisconnect else {
                return false
            }
            self.reconnectAttempt = 0
            // r23 (F4-L #777 CS-race fix): bump install version
            // BEFORE creating the barrier Task so the barrier body's
            // captured `barrierVersion` matches what
            // `scheduleReconnect` / watcher observe for this install.
            self.receiveBarrierInstalledVersion &+= 1
            let bv = self.receiveBarrierInstalledVersion
            let (receive, barrier) = self.makeReceiveTask(myGen: myGen, barrierVersion: bv)
            self.setReceiveTaskLocked(receive)
            // r21 (F4-L #777 R2 fix): install the receive-completion
            // barrier mirroring the receive task 1:1 so the drop path
            // in `scheduleReconnect` can snapshot it, and the owner
            // completion watcher can await it before dispatching a
            // successor. Cleared in `tearDownLocked` alongside the
            // receive task.
            self.receiveCompletionBarrier = barrier
            self.pingTask = self.makePingTask()
            self.connectionStateHub.setState(.connected)
            return true
        }
        if !started {
            throw NetworkError.invalidResponse
        }
        logger.info("Connected to SignalR hub at \(self.serverURL.absoluteString)")
    }

    private func checkGenerationStillCurrent(_ myGen: UInt64) throws {
        let stillCurrent = lifecycleSync {
            self.generation == myGen && !self.intentionalDisconnect
        }
        if !stillCurrent {
            throw NetworkError.invalidResponse
        }
    }

    private func negotiate(jwt: String?) async throws -> SignalRNegotiateResponse {
        guard var components = URLComponents(url: serverURL, resolvingAgainstBaseURL: true) else {
            throw NetworkError.invalidURL(serverURL.absoluteString)
        }
        components.path = (components.path.hasSuffix("/") ? components.path : components.path + "/") + "hubs/printers/negotiate"
        components.queryItems = [URLQueryItem(name: "negotiateVersion", value: "1")]

        guard let url = components.url else {
            throw NetworkError.invalidURL("hubs/printers/negotiate")
        }

        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        if let jwt {
            request.setValue("Bearer \(jwt)", forHTTPHeaderField: "Authorization")
        }

        let (data, response) = try await session.data(for: request)
        guard let http = response as? HTTPURLResponse, (200...299).contains(http.statusCode) else {
            throw NetworkError.serverError((response as? HTTPURLResponse)?.statusCode ?? 0)
        }

        return try decoder.decode(SignalRNegotiateResponse.self, from: data)
    }

    private func makeWebSocketTask(connectionToken: String, jwt: String?) throws -> SignalRWebSocket {
        guard var components = URLComponents(url: serverURL, resolvingAgainstBaseURL: true) else {
            throw NetworkError.invalidURL(serverURL.absoluteString)
        }
        let isSecure = components.scheme == "https"
        components.scheme = isSecure ? "wss" : "ws"
        components.path = (components.path.hasSuffix("/") ? components.path : components.path + "/") + "hubs/printers"

        var queryItems = [URLQueryItem(name: "id", value: connectionToken)]
        if let jwt {
            queryItems.append(URLQueryItem(name: "access_token", value: jwt))
        }
        components.queryItems = queryItems

        guard let wsURL = components.url else {
            throw NetworkError.invalidURL("hubs/printers (WebSocket)")
        }

        return webSocketFactory(wsURL)
    }

    private func sendHandshake(myGen: UInt64) async throws {
        try checkGenerationStillCurrent(myGen)
        let handshake = SignalRHandshakeRequest(protocol: "json", version: 1)
        let data = try JSONEncoder().encode(handshake)
        var message = data
        message.append(Self.recordSeparator)
        // Snapshot the current socket under the lifecycle queue so a
        // concurrent tearDown/disconnect that clears `webSocketTask`
        // cannot leave us sending onto a stale nil reference.
        guard let wsTask: SignalRWebSocket = lifecycleSync({ self.webSocketTask }) else {
            throw NetworkError.invalidResponse
        }
        try await wsTask.send(.data(message))

        // Between send and receive, re-check generation. A disconnect
        // arriving between these two awaits must be observed so a stale
        // handshake completion cannot advance the connection state.
        // (r8 blocker #2: generation/intent validation at every
        // suspension point inside the handshake.)
        try checkGenerationStillCurrent(myGen)

        // Wait for handshake response on the same snapshot.
        let result = try await wsTask.receive()

        // r9 blocker #3: gen-guard IMMEDIATELY after handshake receive,
        // BEFORE parsing the response. A disconnect racing the receive
        // await must invalidate this frame — otherwise a stale
        // handshake success could publish `.connected` via the
        // subsequent Step-4 lifecycleSync (the Step-4 guard would
        // otherwise be the only line of defense).
        try checkGenerationStillCurrent(myGen)

        switch result {
        case .data(let data):
            let trimmed = data.split(separator: Self.recordSeparator).first ?? data[...]
            if let json = try? JSONSerialization.jsonObject(with: Data(trimmed)) as? [String: Any],
               let error = json["error"] as? String {
                throw NetworkError.authFailed("SignalR handshake failed: \(error)")
            }
        case .string(let text):
            let cleaned = text.replacingOccurrences(of: "\u{1e}", with: "")
            if let data = cleaned.data(using: .utf8),
               let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
               let error = json["error"] as? String {
                throw NetworkError.authFailed("SignalR handshake failed: \(error)")
            }
        @unknown default:
            break
        }
    }

    // MARK: - Message Loop

    /// Build the receive task for connection generation `myGen`. On an
    /// error or a close-frame from the peer we schedule the reconnect
    /// flow on a **detached** Task so `tearDown()` cancelling the
    /// receive task cannot itself cancel the reconnect back-off sleep
    /// (r7 blocker #2). The reconnect flow gates on `myGen` so a stale
    /// receive task cannot trigger reconnect after the user has
    /// explicitly disconnected or a newer generation has taken over.
    private func makeReceiveTask(myGen: UInt64, barrierVersion: UInt64) -> (receive: Task<Void, Never>, barrier: Task<Void, Never>) {
        let invariants = self.lifecycleInvariants
        #if DEBUG
        let debugReceiveTaskID = UUID()
        #endif
        let task = Task { [weak self] in
            #if DEBUG
            if let svc = self {
                SignalRService.postDebugLifecycleEvent(
                    service: svc,
                    event: "receiveStarted",
                    taskID: debugReceiveTaskID
                )
            }
            #endif
            // r13 (Hicks item 1): task-lifetime instrumentation. Enter
            // at the top of the actual running task body; exit in
            // `defer` so the counter reflects real concurrent
            // execution, not slot occupancy. If a supersede fails to
            // fully tear down the outgoing receive task before this
            // body begins running, `maxReceiveLoops` will observe 2.
            //
            // r15 (Hicks item 1): transport lifetime is tracked
            // separately in `setWebSocketTaskLocked`, NOT here. The
            // receive-loop counter is now decoupled from the transport
            // counter so a handshake-only / parked-transport bug would
            // be caught (transport enter without matching receive-loop
            // enter, or vice versa).
            invariants.enterReceiveLoop()
            defer {
                invariants.exitReceiveLoop()
            }
            guard let self else { return }
            while !Task.isCancelled {
                let wsTask: SignalRWebSocket? = self.lifecycleSync { self.webSocketTask }
                guard let wsTask else { break }
                do {
                    let message = try await wsTask.receive()
                    // r9 blocker #3: serialize post-receive generation
                    // check with event dispatch. `handleMessage` fans
                    // out to `coordinator.async` hub enqueues, so
                    // performing that fan-out INSIDE `lifecycleSync`
                    // means a concurrent `disconnect()` (which bumps
                    // generation and enqueues `.disconnected` from its
                    // own lifecycleSync) is strictly ordered with
                    // respect to any stale-socket frame — either this
                    // block sees generation still current and enqueues
                    // events BEFORE disconnect's `.disconnected` is
                    // enqueued, or generation has already moved and no
                    // events are enqueued at all.
                    self.lifecycleSync {
                        guard self.generation == myGen, !self.intentionalDisconnect else {
                            return
                        }
                        self.handleMessage(message)
                    }
                } catch {
                    if !Task.isCancelled {
                        let stillCurrent = self.lifecycleSync {
                            self.generation == myGen && !self.intentionalDisconnect
                        }
                        if stillCurrent {
                            self.logger.warning("WebSocket receive error: \(error.localizedDescription)")
                            self.scheduleReconnect(fromGen: myGen)
                        }
                    }
                    break
                }
            }
        }
        // r21 (F4-L #777 R2 fix): receive-completion barrier.
        //
        // This Task exists in ALL build configurations. Its body
        // awaits the receive task's actual `Task.value` (which
        // resolves only after the receive body's `defer
        // { exitReceiveLoop() }` has run and the body has returned),
        // then — in DEBUG only — synchronously posts the
        // `receiveCompleted` lifecycle event, and returns.
        //
        // `barrier.value` therefore resolves strictly AFTER the
        // receive task's own `.value` has resolved and (in DEBUG)
        // after `receiveCompleted` has posted. `scheduleReconnect`'s
        // drop path snapshots this barrier alongside
        // `pendingSuccessorForOwner`, and the owning retry owner's
        // completion watcher awaits `barrier.value` BEFORE posting
        // `ownerCompleted` and dispatching the successor. This is the
        // production happens-before edge required by the frozen
        // strict order `R1.completed < A.completed < B.created`
        // (Rubber Duck R2 correction): the order holds even in
        // Release, where the DEBUG post is compiled out but the
        // barrier's await-of-`task.value` still provides the causal
        // synchronization gate between the outgoing receive's exit
        // and the successor owner's creation.
        let barrier = Task { [weak self, barrierVersion] in
            _ = await task.value
            #if DEBUG
            if let self {
                SignalRService.postDebugLifecycleEvent(
                    service: self,
                    event: "receiveCompleted",
                    taskID: debugReceiveTaskID
                )
            }
            #endif
            // r23 (F4-L #777 CS-race fix): mark this barrier's
            // install-generation as resolved so `scheduleReconnect`'s
            // Fix-B check and the watcher's drain loop can
            // distinguish a still-live receive barrier (installed >
            // resolved) from a stale reference already fully awaited.
            // Advance monotonically — a newer install's barrier body
            // may race with an older one; use max-write semantics.
            guard let self else { return }
            self.lifecycleSync {
                if self.receiveBarrierResolvedVersion < barrierVersion {
                    self.receiveBarrierResolvedVersion = barrierVersion
                }
            }
        }
        return (task, barrier)
    }

    private func makePingTask() -> Task<Void, Never> {
        Task { [weak self] in
            while !Task.isCancelled {
                try? await Task.sleep(for: .seconds(15))
                guard !Task.isCancelled, let self else { break }
                let wsTask: SignalRWebSocket? = self.lifecycleSync { self.webSocketTask }
                guard let wsTask else { break }
                // SignalR ping is type 6
                let ping = "{\"type\":6}"
                var data = Data(ping.utf8)
                data.append(Self.recordSeparator)
                try? await wsTask.send(.data(data))
            }
        }
    }

    private func handleMessage(_ message: URLSessionWebSocketTask.Message) {
        let rawData: Data
        switch message {
        case .data(let data):
            rawData = data
        case .string(let text):
            rawData = Data(text.utf8)
        @unknown default:
            return
        }

        // Split by record separator — a single WebSocket frame can contain multiple SignalR messages
        let frames = rawData.split(separator: Self.recordSeparator)
        for frame in frames {
            guard !frame.isEmpty else { continue }
            processFrame(Data(frame))
        }
    }

    private func processFrame(_ data: Data) {
        guard let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else { return }
        guard let type = json["type"] as? Int else { return }

        switch type {
        case 1: // Invocation
            handleInvocation(json, rawData: data)
        case 6: // Ping
            break
        case 7: // Close
            let error = json["error"] as? String
            logger.info("SignalR close frame received: \(error ?? "no error")")
            let (myGen, shouldReconnect) = lifecycleSync {
                // r22 (F4-L #777) type-7 liveness: the receive loop's
                // outer `guard let wsTask else { break }` depends on
                // `webSocketTask` becoming nil to exit. Without an
                // explicit cancel+clear here, the receive body relies
                // on a subsequent TCP FIN/error from the peer to
                // unstick `wsTask.receive()` — but a well-behaved
                // SignalR server may send type-7 and legitimately not
                // close the transport for some time. If the drop
                // path below records
                // `pendingSuccessorReceiveBarrier` = this receive's
                // barrier, that barrier's `await task.value` would
                // never resolve, and the owner-completion watcher
                // would await forever.
                //
                // Cancel the WebSocket and clear the slot so the
                // NEXT iteration of the receive loop sees
                // `wsTask == nil` and breaks cleanly. The current
                // handleMessage call is post-receive (we're between
                // `wsTask.receive()` calls), so this does not fight
                // an in-flight receive.
                //
                // Deliberately do NOT touch `receiveTask` or
                // `receiveCompletionBarrier` here: the barrier that
                // the drop path will snapshot below must remain
                // mirrored to the CURRENT receive task so awaiting
                // its `.value` gives us the causal edge to that same
                // receive body's exit.
                if let ws = self.webSocketTask {
                    ws.cancel(with: .normalClosure, reason: nil)
                    self.setWebSocketTaskLocked(nil)
                }
                return (self.generation, !self.intentionalDisconnect)
            }
            if shouldReconnect {
                scheduleReconnect(fromGen: myGen)
            }
        default:
            break
        }
    }

    private func handleInvocation(_ json: [String: Any], rawData: Data) {
        guard let target = (json["target"] as? String)?.lowercased(),
              let arguments = json["arguments"] as? [Any],
              let firstArg = arguments.first else { return }

        guard let argData = try? JSONSerialization.data(withJSONObject: firstArg) else { return }

        switch target {
        case "printerupdated":
            do {
                let update = try decoder.decode(PrinterStatusUpdate.self, from: argData)
                printerUpdateHub.deliver(update)
            } catch {
                logger.warning("Failed to decode printerupdated: \(error.localizedDescription)")
            }

        case "jobqueueupdate":
            do {
                let update = try decoder.decode(JobQueueUpdate.self, from: argData)
                jobQueueUpdateHub.deliver(update)
            } catch {
                logger.warning("Failed to decode jobqueueupdate: \(error.localizedDescription)")
            }

        // issue #707: `attentionchanged` is a lowercase invalidation event
        // on `/hubs/printers`. The payload is a small hint the client uses
        // to trigger a refetch of `GET /api/attention`; it is never the
        // authoritative item state. A malformed payload is logged and
        // dropped so a bad frame cannot poison the handler queue.
        case "attentionchanged":
            do {
                let event = try decoder.decode(AttentionChangedEvent.self, from: argData)
                attentionChangedHub.deliver(event)
            } catch {
                logger.warning("Failed to decode attentionchanged: \(error.localizedDescription)")
            }

        case "toolheadupdate", "extruderupdate", "heaterbedupdate":
            break

        // issue #711 F6: `fallbackgroupsupdated` is a lowercase invalidation
        // event on `/hubs/printers`. Payload is `{ printerId }`; handlers
        // refetch `GET /api/printers/{printerId}/fallback-groups` and never
        // persist any field of this payload as canonical state.
        case "fallbackgroupsupdated":
            do {
                let event = try decoder.decode(FallbackGroupsUpdatedEvent.self, from: argData)
                handlerLock.lock()
                let handlers = fallbackGroupsUpdatedHandlers
                handlerLock.unlock()
                for handler in handlers {
                    handler(event)
                }
            } catch {
                logger.warning("Failed to decode fallbackgroupsupdated: \(error.localizedDescription)")
            }

        default:
            logger.debug("Unhandled SignalR event: \(target)")
        }
    }

    // MARK: - Reconnection

    /// Enter the reconnect flow from generation `fromGen`. Coalesced:
    /// only one reconnect task runs at a time; subsequent calls while a
    /// reconnect is already in progress are dropped. The reconnect
    /// itself runs on a **detached** Task not tied to the receive task,
    /// so `tearDown()` cancelling the receive task cannot itself
    /// cancel the reconnect back-off sleep.
    ///
    /// Identity token (r8 blocker #2 + r9 blocker #2): each entry
    /// mints its own UUID and RESERVES the slot atomically before the
    /// detached task is created. A subsequent `scheduleReconnect` sees
    /// `reconnectToken != nil` and drops. The task itself installs
    /// only if the token still owns the slot when it runs. On exit
    /// the task compares its own token against `reconnectToken` and
    /// only clears the slot if they still match — a stale task whose
    /// slot has already been replaced by a newer scheduleReconnect
    /// (or zeroed by disconnect / superseded by connect) cannot wipe
    /// the newer owner's handle.
    ///
    /// r15 (Hicks item 2 + item 3): **single sequential retry-owner
    /// loop**. Previously a failed reconnect attempt recursively
    /// spawned a successor `Task.detached`, whose body's
    /// `enterReconnectOwner()` could execute before (or interleaved
    /// with) the outgoing Task's own body completion — even with the
    /// r14 LIFO-defer handoff, the outgoing Task itself had not
    /// externally completed. The r15 design uses a SINGLE detached
    /// retry-owner Task that iterates internally up to
    /// `Self.maxReconnectAttempts` attempts; there is no successor
    /// spawn, no `pendingReconnectFromGen` field, and no
    /// second-owner reservation. This makes `maxReconnectOwners <= 1`
    /// structural (one `enterReconnectOwner` / one
    /// `exitReconnectOwner` per retry chain). Non-vacuity is proved
    /// by `reconnectAttemptCount` (see
    /// `SignalRLifecycleInvariants.recordReconnectAttempt`) which is
    /// incremented at the top of every retry iteration, so a
    /// bounded-terminal test observes `reconnectAttemptCount == 10`
    /// with `reconnectOwnerEnterCount == 1`. Retry count (10),
    /// backoff schedule, terminal `.disconnected` publish,
    /// cancellation, and supersede semantics are unchanged.
    ///
    /// Immediate receive-failure on the success path (r15 Hicks item
    /// 3): a receive-error inside `makeReceiveTask` calls
    /// `scheduleReconnect(fromGen:)`, which reserves the slot IFF
    /// `reconnectToken == nil`. If a retry-owner is already running
    /// (e.g. an earlier type-7 already installed one), the second
    /// call drops. The reservation is owner-keyed by `taskToken` —
    /// no other owner or slot can consume this owner's state.
    private static let maxReconnectAttempts: Int = 10

    /// Result of the owner-completion watcher's atomic
    /// release/consume step. `.retry` yields a barrier the watcher
    /// must await before another atomic pass; the `isReceive` flag
    /// tells the loop whether the retry corresponds to the
    /// receive-completion barrier (updates
    /// `lastAwaitedReceiveInstalledVersion`) or to a pending-
    /// successor barrier (updates `lastAwaitedPendingVersion`).
    /// `.done` means the slot has been released (if owned) and,
    /// optionally, a successor generation captured for dispatch
    /// outside `lifecycleSync`. (F4-L #777 r22 R2 TOCTOU fix,
    /// r23 CS-race fix.)
    private enum DrainOutcome {
        case retry(Task<Void, Never>, UInt64, isReceive: Bool)
        case done(UInt64?)
        /// r24 (F4-L #777 cross-generation ownership fix): the
        /// watcher's atomic step observed that ownership was lost
        /// mid-drain (a `connect()`/`disconnect()`/terminal supersede
        /// cleared `reconnectToken` and/or bumped `generation` and/or
        /// cleared `finalizingOwnerToken` between snapshots). The
        /// supersede path is authoritative for slot release and any
        /// successor dispatch belonging to the replacement
        /// generation; the stale watcher must silently early-exit
        /// without touching `finalizingOwnerToken`, `pendingSuccessor*`,
        /// or the replacement generation's receive barrier.
        case lost
    }

    private func scheduleReconnect(fromGen: UInt64) {
        // r9 blocker #2: reserve `reconnectToken` under lifecycleQueue
        // BEFORE any detached Task exists. The token IS the ownership
        // proof; the task is installed later inside a second
        // lifecycleSync only if the token is still ours. Between
        // reservation and install, another scheduleReconnect sees the
        // reserved token and drops.
        let taskToken = UUID()
        let (myGen0, reserved): (UInt64, Bool) = lifecycleSync {
            guard !self.intentionalDisconnect, self.generation == fromGen else {
                return (self.generation, false)
            }
            // r23 (F4-L #777 CS-race fix): a same-generation receive
            // task whose barrier is live/unresolved must NEVER take
            // the fresh-reserve path, even if `reconnectToken` is
            // temporarily nil (r22 CS window between a caller's
            // `stillCurrent` check and this call, in which the owner
            // watcher woke, saw no pending, and released the token).
            // Record generation-scoped successor intent against
            // whichever ownership marker is still valid — an active
            // retry owner (`reconnectToken`) or a finalizing watcher
            // (`finalizingOwnerToken`). The watcher's drain loop
            // observes the version bump on its next iteration,
            // awaits this barrier, and dispatches the successor only
            // after the receive's `Task.value` (and, in DEBUG,
            // `receiveCompleted`) has resolved.
            //
            // If neither an owner nor a finalizing owner exists,
            // there is no completion watcher to observe intent —
            // this can only happen in the type-7-post-successful-
            // connect path where handleMessage runs inside the outer
            // receive-body `lifecycleSync` (so no watcher race is
            // possible) and no reconnectTask has been created yet.
            // Fall through to the normal fresh-reserve path in that
            // case so the reconnect actually happens.
            let receiveBarrierLive: Bool =
                self.receiveCompletionBarrier != nil
                    && self.receiveBarrierInstalledVersion > self.receiveBarrierResolvedVersion
            if receiveBarrierLive {
                let ownerScope: UUID? =
                    self.reconnectToken ?? self.finalizingOwnerToken
                if let owner = ownerScope {
                    self.pendingSuccessorForOwner = owner
                    self.pendingSuccessorReceiveBarrier =
                        self.receiveCompletionBarrier
                    self.pendingSuccessorVersion &+= 1
                    self.connectionStateHub.setState(.reconnecting)
                    return (self.generation, false)
                }
                // No owner/finalizing-owner to record against; fall
                // through so type-7-post-successful-connect can still
                // initiate a fresh reconnect. In this path the outer
                // receive-body `lifecycleSync` is held throughout,
                // so no owner-watcher race is possible.
            }
            if let existingToken = self.reconnectToken {
                // r20 (F4-L #777 successor-ownership fix): a retry
                // owner already holds the slot (e.g. this call came
                // from an immediate receive-failure while the owner
                // was parked at `handoffOpen` — the r20 change to
                // `performConnect` step 4 no longer eagerly clears
                // the token, so this branch is now the ordinary
                // "same-generation successor requested" path).
                // Instead of silently dropping, record a deferred
                // successor request keyed to the OWNING owner's
                // token. The owner-scoped completion watcher spawned
                // alongside `existingToken` will consume this request
                // AFTER `await owner.value` returns and schedule the
                // successor at that point — guaranteeing the frozen
                // `A.completed < B.created` strict order and
                // `maxReconnectOwners <= 1`.
                //
                // Publish `.reconnecting` here so subscribers observe
                // the transition out of `.connected` at the moment
                // the receive fails, without waiting for the current
                // owner to release. The state hub de-duplicates
                // consecutive same-state publishes, so if we are
                // already in `.reconnecting` this is a no-op.
                self.pendingSuccessorForOwner = existingToken
                // r21 (F4-L #777 R2 fix): snapshot the current
                // receive-completion barrier alongside the pending
                // token so the owning owner's completion watcher can
                // await this specific barrier before dispatching the
                // successor. This is the production happens-before
                // edge that guarantees the frozen strict order
                // `R1.completed < A.completed < B.created` even in
                // Release, without any DEBUG-only test barrier: the
                // barrier's `.value` resolves only after the current
                // receive task's own body (which just called us) has
                // fully exited.
                self.pendingSuccessorReceiveBarrier = self.receiveCompletionBarrier
                // r22 (F4-L #777 R2 TOCTOU fix): bump the pending
                // version so any concurrent completion watcher can
                // detect that a NEW barrier was recorded after its
                // last snapshot and loop back to await it before
                // releasing the slot / dispatching a successor.
                self.pendingSuccessorVersion &+= 1
                self.connectionStateHub.setState(.reconnecting)
                return (self.generation, false)
            }
            self.generation &+= 1
            self.tearDownLocked()
            self.reconnectAttempt = 0
            // Reserve the slot with just the token; the Task handle is
            // installed below once we've created it.
            self.setReconnectTokenLocked(taskToken)
            self.reconnectTask = nil
            // Defensive: a fresh reservation starts a new retry
            // chain, so any pending flag left over from a prior
            // owner's tail is stale by construction. Clear it so a
            // late completion watcher from that prior chain cannot
            // observe a stale token equality against the wrong
            // owner.
            self.pendingSuccessorForOwner = nil
            self.pendingSuccessorReceiveBarrier = nil
            self.connectionStateHub.setState(.reconnecting)
            return (self.generation, true)
        }
        if !reserved { return }

        #if DEBUG
        SignalRService.postDebugLifecycleEvent(
            service: self,
            event: "ownerCreated",
            taskID: taskToken
        )
        #endif

        let reconnectTask = Task.detached { [weak self] in
            guard let self else { return }
            #if DEBUG
            SignalRService.postDebugLifecycleEvent(
                service: self,
                event: "ownerStarted",
                taskID: taskToken
            )
            #endif
            let invariants = self.lifecycleInvariants
            // r13 (Hicks item 1): reconnect-owner lifetime = the
            // detached Task's actual body execution. Enter at top,
            // exit in defer.
            //
            // r15 (Hicks item 2): the retry LOOP is inside this
            // single Task body. There is no successor spawn, so this
            // enter/exit pair is called EXACTLY ONCE per retry chain
            // regardless of how many attempts iterate. Overlap is
            // structurally impossible.
            invariants.enterReconnectOwner()
            defer { invariants.exitReconnectOwner() }

            var currentGen = myGen0
            var attempt = 0
            while attempt < Self.maxReconnectAttempts {
                attempt += 1
                // r15 (Hicks item 4/8): record every retry attempt so
                // the retry-chain proof is non-vacuous even though
                // `reconnectOwnerEnterCount == 1`.
                invariants.recordReconnectAttempt()
                self.lifecycleSync { self.reconnectAttempt = attempt }

                #if DEBUG
                SignalRService.postDebugLifecycleEvent(
                    service: self,
                    event: "attemptStarted",
                    taskID: taskToken,
                    attempt: attempt,
                    ownerID: taskToken
                )
                #endif

                let delay = self.reconnectBackoff(attempt)
                self.logger.info("Reconnecting in \(delay)s (attempt \(attempt))")
                // r9 blocker #4: injected async sleeper. Production
                // wraps `Task.sleep`; tests provide a controllable
                // sleeper that suspends until the test releases each
                // attempt.
                await self.reconnectSleeper(delay)

                // After the back-off, revalidate under the lifecycle
                // queue. Cancelled / disconnected / superseded ⇒ exit.
                let stillOwn: Bool = self.lifecycleSync {
                    self.reconnectToken == taskToken &&
                        self.generation == currentGen &&
                        !self.intentionalDisconnect &&
                        !Task.isCancelled
                }
                if !stillOwn {
                    // r21 (F4-L #777 R1 fix): DO NOT clear the
                    // reconnect slot here. The owner-scoped
                    // completion watcher spawned below awaits this
                    // Task's actual `.value` and is the SOLE
                    // authority that releases the slot (Rubber Duck
                    // R1 correction: an in-body clear opens a
                    // token-release gap in which a same-generation
                    // receive failure can observe `reconnectToken
                    // == nil` and take the direct reserve path,
                    // spawning B before A's `Task.value` completes,
                    // violating the frozen strict order).
                    return
                }

                do {
                    try await self.performConnect(myGen: currentGen)
                    #if DEBUG
                    // Synchronous post: recorder may block here to
                    // hold the owner alive while B is triggered. Fires
                    // AFTER receive task install / .connected publish
                    // (r10 #2a). The completion watcher — not this
                    // in-body path — is the sole authority that
                    // releases the reconnect slot after the owner's
                    // `Task.value` completes (r21 R1 fix).
                    SignalRService.postDebugLifecycleEvent(
                        service: self,
                        event: "handoffOpen",
                        taskID: taskToken,
                        ownerID: taskToken
                    )
                    #endif
                    // r21 (F4-L #777 R1 fix): success path returns
                    // without releasing the slot. Slot release
                    // happens in the completion watcher AFTER `await
                    // reconnectTask.value` — this closes the
                    // token-release gap on the retry-loop success
                    // path (the Rubber Duck R1 defect). See the
                    // watcher below for the sole slot-release site.
                    return
                } catch {
                    self.logger.warning("Reconnect attempt \(attempt) failed: \(error.localizedDescription)")
                    // Per-attempt cleanup + generation bump. The failing
                    // `performConnect` may have installed a WebSocket
                    // before throwing (e.g. handshake failure). Tear it
                    // down here and bump generation so any stale
                    // in-flight receive/handshake from that attempt
                    // becomes inert. This mirrors the pre-r15 recursive
                    // design's per-attempt `tearDownLocked` +
                    // generation-bump semantics.
                    let (nextGen, shouldContinue): (UInt64, Bool) = self.lifecycleSync {
                        // A racing disconnect() / connect() may have
                        // yanked ownership; if so, stop.
                        guard self.reconnectToken == taskToken,
                              !self.intentionalDisconnect,
                              !Task.isCancelled else {
                            return (self.generation, false)
                        }
                        self.tearDownLocked()
                        self.generation &+= 1
                        return (self.generation, true)
                    }
                    if !shouldContinue {
                        // r21 (F4-L #777 R1 fix): no in-body slot
                        // release. Watcher clears the slot after
                        // `await reconnectTask.value`.
                        return
                    }
                    currentGen = nextGen
                    // Loop to next attempt.
                }
            }

            // Bounded terminal: `maxReconnectAttempts` exhausted. Only
            // publish `.disconnected` if we still own the slot AND the
            // user hasn't already intentionally disconnected. Under
            // lifecycleSync so publication order is preserved.
            self.lifecycleSync {
                if self.reconnectToken == taskToken {
                    self.reconnectTask = nil
                    self.setReconnectTokenLocked(nil)
                    // r20 (F4-L #777): a giving-up owner clears its
                    // own pending flag so a late completion watcher
                    // does not misinterpret a request that was tied
                    // to THIS owner as still valid after the
                    // terminal `.disconnected` publish.
                    if self.pendingSuccessorForOwner == taskToken {
                        self.pendingSuccessorForOwner = nil
                        self.pendingSuccessorReceiveBarrier = nil
                    }
                    if !self.intentionalDisconnect {
                        self.connectionStateHub.setState(.disconnected)
                    }
                }
            }
            self.logger.error("Gave up reconnecting after \(Self.maxReconnectAttempts) attempts")
        }
        // r21 (F4-L #777) owner-scoped completion watcher.
        //
        // This Task runs in ALL build configurations. It is the SOLE
        // authority that releases the reconnect slot for this owner
        // (R1 correction: no in-body `clearReconnectSlotIfOwned`
        // remains on the success or cancel-exit paths — see the
        // retry-loop above), and it enforces the receive-completion
        // happens-before edge required by the frozen strict order
        // `R1.completed < A.completed < B.created` (R2 correction).
        //
        // Sequence:
        //   1. `await reconnectTask.value` — owner A's body has
        //      returned (past its `defer exitReconnectOwner`).
        //   2. Snapshot `pendingSuccessorReceiveBarrier` under
        //      `lifecycleSync` (read-only): if a same-generation
        //      receive failure recorded a pending successor for this
        //      owner (drop path in `scheduleReconnect`), it also
        //      snapshotted the receive task's completion barrier at
        //      that moment. We do not clear it here yet — the second
        //      lifecycleSync below atomically consumes both the
        //      pending flag and the barrier alongside slot release.
        //   3. `await barrier.value` (if any). The barrier's body is
        //      `_ = await task.value; #if DEBUG post receiveCompleted
        //      #endif` — its `.value` therefore resolves only AFTER
        //      the receive task's `.value` (i.e. R1's body has
        //      returned) AND (in DEBUG) after `receiveCompleted` has
        //      been posted. This is the production happens-before
        //      edge required by the frozen `R1.completed <
        //      A.completed` proof — it holds in Release too, where
        //      the DEBUG post is compiled out but the barrier's
        //      await-of-`task.value` still provides the causal
        //      synchronization gate.
        //   4. `#if DEBUG ownerCompleted #endif` — now emitted
        //      strictly AFTER `receiveCompleted` (both due to (3)
        //      above and because it lives further down this same
        //      Task).
        //   5. Second `lifecycleSync`: release the slot iff still
        //      owned by this token (sole authority), consume pending
        //      + barrier tied to this owner, evaluate whether a
        //      successor should be dispatched. `!intentionalDisconnect
        //      && pendingSuccessorForOwner == taskToken` gates
        //      dispatch; otherwise (superseded/disconnected/no
        //      request/retry-exhausted terminal has already cleared
        //      pending) we return without dispatching. Note we no
        //      longer require `reconnectToken == nil` (the r20 gate)
        //      because THIS watcher is the site that clears the
        //      token; the retry-exhausted terminal is the only other
        //      in-body clear and it also clears pending, so pending
        //      equality against this owner suffices.
        //   6. Dispatch `scheduleReconnect(fromGen:)` OUTSIDE the
        //      lifecycleSync (that call takes the queue itself).
        //
        // Ordering guarantees (Rubber Duck R1+R2 corrections):
        //   * Slot release always happens AFTER `await
        //     reconnectTask.value` — no token-release gap.
        //   * `ownerCompleted(A)` (DEBUG) always follows
        //     `receiveCompleted(R1)` because the barrier's body
        //     posts receiveCompleted before its own `.value`
        //     resolves, and only then does the watcher continue to
        //     its ownerCompleted post.
        //   * Successor B's `ownerCreated` (emitted synchronously
        //     from the downstream `scheduleReconnect`) therefore
        //     follows both ownerCompleted(A) and receiveCompleted
        //     (R1), satisfying the frozen strict order in
        //     production, not merely under the DEBUG `handoffOpen`
        //     barrier.
        Task { [weak self, reconnectTask] in
            _ = await reconnectTask.value
            guard let self else { return }

            // r24 (F4-L #777 cross-generation ownership fix): claim
            // finalizing authority atomically ONLY if this watcher
            // still legitimately owns the chain at the moment its
            // owner task returned. A superseding call to
            // `connect()`/`disconnect()`/retry-exhausted terminal may
            // have run while this owner was still executing/awaited,
            // clearing our `reconnectToken`, bumping `generation`,
            // and clearing `finalizingOwnerToken`. In that case the
            // supersede path is authoritative — it has already
            // handled slot release, pending clearing, and any
            // successor logic for the replacement generation. A
            // stale watcher must NOT reassert `finalizingOwnerToken`
            // (it no longer owns it), must NOT touch pending state
            // (it belongs to another owner or is intentionally
            // cleared), and must NOT drain the replacement
            // generation's receive barrier (which now belongs to R2,
            // not to this watcher's owner). Silent early-exit.
            //
            // The claim predicate matches the reservation predicate
            // exactly: `reconnectToken == taskToken` (still the
            // current owner slot), `generation == myGen0` (no
            // generation bump has occurred), and
            // `!intentionalDisconnect` (user has not disconnected).
            // `myGen0` was captured as a `let` at reservation time
            // above (line ~1089) and is immutable for the lifetime
            // of this closure — a defining reference to the owner
            // generation this watcher belongs to.
            let claimed: Bool = self.lifecycleSync {
                guard self.reconnectToken == taskToken,
                      self.generation == myGen0,
                      !self.intentionalDisconnect else {
                    return false
                }
                self.finalizingOwnerToken = taskToken
                return true
            }
            if !claimed {
                // r24 (F4-L #777 cross-generation): superseded
                // before we could claim. Do NOT touch any state
                // (reconnectToken/finalizingOwnerToken/pending/
                // barrier/version fields all belong to a different
                // owner or the replacement generation). But we MUST
                // still emit `ownerCompleted` for this taskToken:
                // the frozen "every started lifetime completes"
                // contract (SignalRServiceBindingHarnessTests
                // .testRealService_connectSupersedesReconnect_
                // finishesEveryStartedLifetime) requires an
                // `ownerCompleted` for every `ownerStarted`, and
                // the supersede path itself does not emit it. In
                // production Release this branch is a no-op; the
                // DEBUG post is the only side-effect and it does
                // not mutate service state.
                #if DEBUG
                SignalRService.postDebugLifecycleEvent(
                    service: self,
                    event: "ownerCompleted",
                    taskID: taskToken
                )
                #endif
                return
            }

            // r22 (F4-L #777 R2 TOCTOU fix) + r23 (CS-race fix) +
            // r24 (cross-generation ownership fix): drain-loop with
            // two independent version tracks and ownership
            // revalidation at every lifecycleSync entry.
            //   * `lastAwaitedReceiveInstalled` tracks the version
            //     of the current receive-completion barrier we've
            //     already awaited. Bumped by `performConnect` step 4
            //     when a new barrier is installed; resolved by the
            //     barrier body itself after `await task.value`.
            //     A same-generation receive whose barrier is live
            //     (`installed > resolved AND installed > lastAwaited`)
            //     must be awaited BEFORE finalizing, so a late
            //     `scheduleReconnect` call from that receive's own
            //     catch (r22 CS window) has a chance to record intent
            //     via Fix B before we release the slot.
            //   * `lastAwaitedPending` tracks the pending-successor
            //     barrier version (r22 protocol) — bumped every time
            //     the drop path or Fix B records new pending +
            //     barrier.
            // r24: every `lifecycleSync` block that reads barrier
            // state or advances state re-checks ownership before
            // acting. If ownership is lost mid-drain (supersede),
            // the block returns `nil`/`.lost` and the loop exits
            // silently. This ensures a stale watcher can never park
            // on the replacement generation's receive barrier nor
            // intercept its reconnect intent via Fix B's
            // `reconnectToken ?? finalizingOwnerToken` fallback.
            var lastAwaitedPending: UInt64 = 0
            var lastAwaitedReceiveInstalled: UInt64 = 0
            var successorFromGen: UInt64? = nil
            drainLoop: while true {
                // Ownership-gated snapshot. Returns nil if we no
                // longer own the chain; otherwise the current pair
                // of barriers scoped to this owner.
                let snapshot: (
                    receive: Task<Void, Never>?, receiveV: UInt64,
                    pending: Task<Void, Never>?, pendingV: UInt64
                )? = self.lifecycleSync {
                    guard self.reconnectToken == taskToken,
                          self.generation == myGen0,
                          self.finalizingOwnerToken == taskToken,
                          !self.intentionalDisconnect else {
                        return nil
                    }
                    let p: Task<Void, Never>? =
                        (self.pendingSuccessorForOwner == taskToken)
                        ? self.pendingSuccessorReceiveBarrier : nil
                    let pv: UInt64 =
                        (self.pendingSuccessorForOwner == taskToken)
                        ? self.pendingSuccessorVersion : 0
                    // A receive barrier is "live" (needs awaiting)
                    // only while its install-version exceeds its
                    // resolved-version. A resolved-but-still-
                    // referenced barrier is safe to skip. Because
                    // ownership is confirmed above, the barrier we
                    // read here belongs to THIS generation — a
                    // supersede would have failed the guard.
                    let rLive: Bool =
                        self.receiveCompletionBarrier != nil
                            && self.receiveBarrierInstalledVersion
                                > self.receiveBarrierResolvedVersion
                    let r: Task<Void, Never>? =
                        rLive ? self.receiveCompletionBarrier : nil
                    let rv: UInt64 =
                        rLive ? self.receiveBarrierInstalledVersion : 0
                    return (r, rv, p, pv)
                }
                guard let snap = snapshot else {
                    // Ownership lost mid-drain. Supersede path
                    // owns slot release + successor dispatch for
                    // the replacement generation. Still emit
                    // `ownerCompleted` for the frozen "every
                    // started lifetime completes" contract; see
                    // the claim-failed branch above for full
                    // rationale.
                    #if DEBUG
                    SignalRService.postDebugLifecycleEvent(
                        service: self,
                        event: "ownerCompleted",
                        taskID: taskToken
                    )
                    #endif
                    return
                }
                // Await receive barrier first — it is the
                // production happens-before edge that R's late
                // scheduleReconnect (r22 CS window) depends on.
                if let rBarrier = snap.receive,
                   snap.receiveV > lastAwaitedReceiveInstalled {
                    _ = await rBarrier.value
                    lastAwaitedReceiveInstalled = snap.receiveV
                    continue drainLoop
                }
                if let pBarrier = snap.pending,
                   snap.pendingV > lastAwaitedPending {
                    _ = await pBarrier.value
                    lastAwaitedPending = snap.pendingV
                    continue drainLoop
                }
                // No unawaited barrier at the snapshot moment.
                // Attempt the atomic release/consume step, with a
                // final ownership revalidation. r24: if we've been
                // superseded between the snapshot and here, the
                // atomic step returns `.lost` and we early-exit
                // without touching any state — the supersede path
                // has already cleared everything on our behalf.
                let step: DrainOutcome = self.lifecycleSync {
                    guard self.reconnectToken == taskToken,
                          self.generation == myGen0,
                          self.finalizingOwnerToken == taskToken else {
                        return .lost
                    }
                    // Re-check receive-barrier track first.
                    if let rb = self.receiveCompletionBarrier,
                       self.receiveBarrierInstalledVersion
                           > lastAwaitedReceiveInstalled,
                       self.receiveBarrierInstalledVersion
                           > self.receiveBarrierResolvedVersion {
                        return .retry(
                            rb,
                            self.receiveBarrierInstalledVersion,
                            isReceive: true
                        )
                    }
                    // Then pending-successor track (r22 protocol).
                    if self.pendingSuccessorForOwner == taskToken,
                       self.pendingSuccessorVersion > lastAwaitedPending,
                       let pb = self.pendingSuccessorReceiveBarrier {
                        return .retry(
                            pb,
                            self.pendingSuccessorVersion,
                            isReceive: false
                        )
                    }
                    // Safe: no unawaited owner-scoped barriers.
                    // Release the slot iff still owned by this
                    // owner AND clear finalizing authority in the
                    // SAME atomic step so no observer sees
                    // `reconnectToken == nil` while
                    // `finalizingOwnerToken == taskToken` is still
                    // valid to record against.
                    if self.reconnectToken == taskToken {
                        self.reconnectTask = nil
                        self.setReconnectTokenLocked(nil)
                    }
                    if self.finalizingOwnerToken == taskToken {
                        self.finalizingOwnerToken = nil
                    }
                    guard !self.intentionalDisconnect,
                          self.pendingSuccessorForOwner == taskToken
                    else {
                        // Owner was superseded / disconnected, no
                        // successor requested, or retry-exhausted
                        // terminal already cleared pending.
                        // Defensively clear a stale pending tied
                        // specifically to this owner.
                        if self.pendingSuccessorForOwner == taskToken {
                            self.pendingSuccessorForOwner = nil
                            self.pendingSuccessorReceiveBarrier = nil
                        }
                        return .done(nil)
                    }
                    // Consume pending + capture generation.
                    self.pendingSuccessorForOwner = nil
                    self.pendingSuccessorReceiveBarrier = nil
                    return .done(self.generation)
                }
                switch step {
                case .lost:
                    // r24: ownership lost between snapshot and
                    // atomic step. Same rationale as above —
                    // early-exit and emit `ownerCompleted` for
                    // the frozen lifetime-pairing contract.
                    #if DEBUG
                    SignalRService.postDebugLifecycleEvent(
                        service: self,
                        event: "ownerCompleted",
                        taskID: taskToken
                    )
                    #endif
                    return
                case .retry(let b, let v, let isReceive):
                    _ = await b.value
                    if isReceive {
                        lastAwaitedReceiveInstalled = v
                    } else {
                        lastAwaitedPending = v
                    }
                    continue drainLoop
                case .done(let gen):
                    successorFromGen = gen
                    break drainLoop
                }
            }

            #if DEBUG
            // r22: `ownerCompleted` is posted after the drain loop
            // exits with `.done`, guaranteeing every pending barrier
            // (i.e. every `receiveCompleted` DEBUG post) has already
            // been awaited to completion, and before any successor
            // dispatch (which posts `ownerCreated`). Order:
            //     receiveCompleted (from awaited barrier's body)
            //         < ownerCompleted (here)
            //         < ownerCreated(B) (from scheduleReconnect below).
            // r24: only reached on a legitimate `.done` outcome —
            // a lost/superseded watcher returned early above and
            // does NOT post `ownerCompleted` (the supersede path
            // is responsible for the replacement generation's
            // lifecycle events).
            SignalRService.postDebugLifecycleEvent(
                service: self,
                event: "ownerCompleted",
                taskID: taskToken
            )
            #endif

            if let successorFromGen {
                self.scheduleReconnect(fromGen: successorFromGen)
            }
        }

        // Install the task handle only if we still own the token. A
        // disconnect() racing this call between the token reservation
        // above and now will have cleared `reconnectToken` — in that
        // case cancel the task we just created and drop.
        let installed: Bool = lifecycleSync {
            guard self.reconnectToken == taskToken else {
                return false
            }
            self.reconnectTask = reconnectTask
            return true
        }
        if !installed {
            reconnectTask.cancel()
        }
    }

    /// Clear the reconnect slot if and only if this task still owns
    /// it. Prevents a stale reconnect from wiping a newer owner.
    private func clearReconnectSlotIfOwned(token: UUID) {
        lifecycleSync {
            if self.reconnectToken == token {
                self.reconnectTask = nil
                self.setReconnectTokenLocked(nil)
            }
        }
    }

    /// MUST be invoked from within `lifecycleSync`. Cancels the current
    /// receive/ping/socket handles and clears them. Does NOT cancel the
    /// reconnect task — that owns its own lifecycle and cannot be
    /// interrupted by a receive-error tear-down.
    private func tearDownLocked() {
        receiveTask?.cancel()
        setReceiveTaskLocked(nil)
        // r21 (F4-L #777 R2 fix): the receive-completion barrier is
        // mirrored 1:1 with `receiveTask`. Clearing it here on
        // teardown so a subsequent `performConnect` step 4 installs a
        // fresh barrier. Note the CURRENT barrier Task itself is not
        // cancelled — it merely awaits the cancelled receive task's
        // `.value` and then posts (in DEBUG) / returns. Any pending
        // successor request that snapshotted this barrier still holds
        // its own strong reference and awaits it independently, which
        // is exactly the causal edge we want to preserve across
        // teardown.
        receiveCompletionBarrier = nil
        pingTask?.cancel()
        pingTask = nil
        webSocketTask?.cancel(with: .normalClosure, reason: nil)
        setWebSocketTaskLocked(nil)
    }
}

#if DEBUG
// MARK: - DEBUG-only lifecycle event wire (F4-L #777 hook-only checkpoint)
//
// Emits Notification-based test-observable lifecycle events. This
// entire block is compiled out of Release builds — no symbols,
// strings, or behavior leak into shipped binaries. Production logic
// (reconnect ownership, sequencing, cancellation, state transitions,
// retries, counters) is unchanged; the callers merely post a
// synchronous notification at defined points and, for owner/receive
// completion, an external unstructured observer awaits `Task.value`
// before posting.
//
// Contract (from approved harness `LifecycleEventRecorder`):
//   name:   "PrintFarmer.SignalRTaskLifecycle.test"
//   object: SignalRService instance (filter: `notification.object === service`)
//   userInfo:
//     "event":   String   (required)
//     "taskID":  UUID     (required)
//     "attempt": Int      (optional; attemptStarted only)
//     "ownerID": UUID     (optional; attemptStarted, handoffOpen)
extension SignalRService {
    fileprivate static let debugLifecycleEventNotification =
        Notification.Name("PrintFarmer.SignalRTaskLifecycle.test")

    fileprivate static func postDebugLifecycleEvent(
        service: SignalRService,
        event: String,
        taskID: UUID,
        attempt: Int? = nil,
        ownerID: UUID? = nil
    ) {
        var userInfo: [String: Any] = ["event": event, "taskID": taskID]
        if let attempt { userInfo["attempt"] = attempt }
        if let ownerID { userInfo["ownerID"] = ownerID }
        NotificationCenter.default.post(
            name: debugLifecycleEventNotification,
            object: service,
            userInfo: userInfo
        )
    }
}
#endif
