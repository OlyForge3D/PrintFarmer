import Foundation

// MARK: - Durable Offline Write Queue — Coordinator (F10-Q1, #787)
//
// The single, actor-isolated owner of the outbox. Responsibilities:
//   * durable enqueue (persist BEFORE any attempt) of allowlisted intents;
//   * serialized, single-owner replay that preserves creation order per entity,
//     with exactly one in-flight attempt at a time;
//   * the 7-day idempotency-window boundary (≤ replays, > transitions to
//     `expiredNeedsReview` with ZERO network requests);
//   * strict identity isolation — only the active `(serverID, userID)` namespace
//     is ever replayed; a logout / server switch immediately abandons any
//     in-flight drain and never replays another namespace's writes;
//   * the operator feature gate (`offlineWriteReplayEnabled`): disabling pauses
//     (never discards) existing items and refuses new offline enqueue; enabling
//     resumes only non-expired items.
//
// Replay is driven by an injected `OfflineWriteReplayTransport`, so tests script
// exact per-attempt outcomes and drive reconnect deterministically — no sleeps,
// no wall clock.

struct OfflineWriteReplayIdentity: Equatable, Sendable {
    let serverID: UUID
    let userID: UUID
}

struct OfflineWriteReplayBinding: Equatable, Sendable {
    let identity: OfflineWriteReplayIdentity
    fileprivate let revision: UInt64
}

final class OfflineWriteReplayAuthority: @unchecked Sendable {
    private let lock = NSLock()
    private var revision: UInt64 = 0
    private var identity: OfflineWriteReplayIdentity?
    private var bindingAuthorized = false

    var currentIdentity: OfflineWriteReplayIdentity? {
        lock.lock()
        defer { lock.unlock() }
        return identity
    }

    @discardableResult
    func bind(serverID: UUID, userID: UUID) -> OfflineWriteReplayBinding {
        lock.lock()
        defer { lock.unlock() }
        revision &+= 1
        let nextIdentity = OfflineWriteReplayIdentity(serverID: serverID, userID: userID)
        identity = nextIdentity
        bindingAuthorized = true
        return OfflineWriteReplayBinding(identity: nextIdentity, revision: revision)
    }

    func bindIfCurrent(
        revision expectedRevision: UInt64,
        serverID: UUID,
        userID: UUID
    ) -> OfflineWriteReplayBinding? {
        lock.lock()
        defer { lock.unlock() }
        guard revision == expectedRevision, bindingAuthorized else { return nil }
        revision &+= 1
        let nextIdentity = OfflineWriteReplayIdentity(serverID: serverID, userID: userID)
        identity = nextIdentity
        return OfflineWriteReplayBinding(identity: nextIdentity, revision: revision)
    }

    @discardableResult
    func invalidate() -> UInt64 {
        lock.lock()
        revision &+= 1
        identity = nil
        bindingAuthorized = false
        let invalidationRevision = revision
        lock.unlock()
        return invalidationRevision
    }

    @discardableResult
    func authorizeBinding() -> UInt64 {
        lock.lock()
        defer { lock.unlock() }
        revision &+= 1
        identity = nil
        bindingAuthorized = true
        return revision
    }

    func captureRevision() -> UInt64 {
        lock.lock()
        defer { lock.unlock() }
        return revision
    }

    func isCurrent(revision expectedRevision: UInt64) -> Bool {
        lock.lock()
        defer { lock.unlock() }
        return revision == expectedRevision
    }

    func isCurrent(_ binding: OfflineWriteReplayBinding) -> Bool {
        lock.lock()
        defer { lock.unlock() }
        return revision == binding.revision && identity == binding.identity
    }
}

actor OfflineWriteQueue {
    private let store: OfflineWriteQueueStoring
    private let transport: OfflineWriteReplayTransport
    private let clock: OfflineQueueClock
    private let configuration: OfflineWriteQueueConfiguration
    private let replayAuthority: OfflineWriteReplayAuthority

    /// Authoritative in-memory mirror of the durable store, spanning every
    /// namespace. The coordinator filters by the active identity for replay.
    private var items: [OfflineWriteItem] = []
    private var didLoad = false

    private var activeServerID: UUID?
    private var activeUserID: UUID?
    private var activeBinding: OfflineWriteReplayBinding?
    private var replayEnabled = true

    /// Bumped on every identity/gate transition. A drain captures the value at
    /// start and abandons itself after any suspension if it changed — this is
    /// what guarantees a switch/logout/disable can never let an old owner apply
    /// a result under a new identity.
    private var generation = 0
    /// Single-replay-owner latch: a second `replayPending()` while a drain is
    /// active returns immediately, so duplicate reconnect signals / scenes yield
    /// exactly one owner.
    private var isDraining = false
    /// The item currently in flight (for the UI `isReplaying` badge).
    private var replayingItemID: UUID?

    /// Optional change hook (wired by the container to refresh the status
    /// surface). Never used by the coordinator's own logic.
    private var changeHandler: (@Sendable () -> Void)?

    init(
        store: OfflineWriteQueueStoring,
        transport: OfflineWriteReplayTransport,
        clock: OfflineQueueClock = SystemOfflineQueueClock(),
        configuration: OfflineWriteQueueConfiguration = .default,
        replayAuthority: OfflineWriteReplayAuthority = OfflineWriteReplayAuthority()
    ) {
        self.store = store
        self.transport = transport
        self.clock = clock
        self.configuration = configuration
        self.replayAuthority = replayAuthority
    }

    // MARK: Loading

    private func loadIfNeeded() async {
        guard !didLoad else { return }
        didLoad = true
        items = await store.loadAll()
    }

    private func persist() async {
        await store.saveAll(items)
        changeHandler?()
    }

    func setChangeHandler(_ handler: @escaping @Sendable () -> Void) {
        changeHandler = handler
    }

    // MARK: Identity lifecycle

    /// Binds the active namespace. A change from the previous identity bumps the
    /// generation, abandoning any in-flight drain owned by the old identity.
    func bind(serverID: UUID, userID: UUID) async {
        let binding = replayAuthority.bind(serverID: serverID, userID: userID)
        _ = await bind(binding: binding)
    }

    @discardableResult
    func bind(binding: OfflineWriteReplayBinding) async -> Bool {
        guard replayAuthority.isCurrent(binding) else { return false }
        await loadIfNeeded()
        guard replayAuthority.isCurrent(binding) else { return false }
        let serverID = binding.identity.serverID
        let userID = binding.identity.userID
        if activeServerID != serverID || activeUserID != userID || activeBinding != binding {
            generation += 1
            replayingItemID = nil
        }
        activeServerID = serverID
        activeUserID = userID
        activeBinding = binding
        return replayAuthority.isCurrent(binding)
    }

    /// Clears the active namespace (logout / no active server). Retained items
    /// stay on disk; any in-flight drain is abandoned. Nothing is replayed until
    /// a matching `bind` re-establishes the identity.
    func unbind() async {
        let invalidationRevision = replayAuthority.invalidate()
        _ = await unbind(authorityRevision: invalidationRevision)
    }

    @discardableResult
    func unbind(authorityRevision: UInt64) async -> Bool {
        guard replayAuthority.isCurrent(revision: authorityRevision) else { return false }
        await loadIfNeeded()
        guard replayAuthority.isCurrent(revision: authorityRevision) else { return false }
        generation += 1
        activeServerID = nil
        activeUserID = nil
        activeBinding = nil
        replayingItemID = nil
        return true
    }

    /// Applies the operator feature gate. Disabling pauses all active-namespace
    /// pending items (retained, never discarded) and abandons any drain; enabling
    /// resumes paused items, expiring any that have since aged out.
    func setReplayEnabled(_ enabled: Bool) async {
        await loadIfNeeded()
        guard enabled != replayEnabled else { return }
        replayEnabled = enabled
        generation += 1
        replayingItemID = nil

        guard let server = activeServerID, let user = activeUserID else { return }
        let now = clock.now()
        var changed = false
        for index in items.indices where items[index].serverID == server && items[index].userID == user {
            if !enabled, items[index].status.isPending {
                items[index].status = .paused
                changed = true
            } else if enabled, case .paused = items[index].status {
                if items[index].isExpired(at: now, retention: configuration.retention) {
                    items[index].status = .expiredNeedsReview
                } else {
                    items[index].status = .pending
                }
                changed = true
            }
        }
        if changed { await persist() }
    }

    // MARK: Enqueue

    /// Durably enqueues an intent. The item is persisted BEFORE any replay is
    /// attempted, so a crash between enqueue and the first attempt cannot lose
    /// it. Refuses when the gate is off (caller must use direct-online only),
    /// when there is no identity, when the outbox is full, or when the intent
    /// carries no idempotency key.
    func enqueue(_ operation: OfflineWriteOperation) async -> OfflineWriteEnqueueResult {
        await enqueue(operation, prerequisiteItemID: nil)
    }

    /// Durably enqueues an intent that MUST NOT replay until a prerequisite
    /// domain-write item has succeeded and left the queue (F10-Q2, #790). Used
    /// when a single logical action queues both a domain write and a linked
    /// task completion: the completion carries the domain write's queue id so
    /// the drain never applies the completion before its required mutation.
    func enqueue(
        _ operation: OfflineWriteOperation,
        prerequisiteItemID: UUID?
    ) async -> OfflineWriteEnqueueResult {
        await loadIfNeeded()
        guard replayEnabled else { return .replayDisabled }
        guard let server = activeServerID,
              let user = activeUserID,
              let activeBinding,
              replayAuthority.isCurrent(activeBinding) else {
            return .noIdentity
        }
        guard let key = operation.idempotencyKey, !key.isEmpty else { return .missingIdempotencyKey }

        let namespaceCount = items.reduce(0) { $0 + (($1.serverID == server && $1.userID == user) ? 1 : 0) }
        guard namespaceCount < configuration.maxItems else { return .queueFull }

        let item = OfflineWriteItem(
            serverID: server,
            userID: user,
            createdAt: clock.now(),
            idempotencyKey: key,
            operation: operation,
            status: .pending,
            prerequisiteItemID: prerequisiteItemID
        )
        items.append(item)
        await persist()
        return .enqueued(item)
    }

    // MARK: Replay

    /// Drives a single serialized replay pass over the active namespace. Safe to
    /// call from multiple reconnect signals: only the first proceeds; the rest
    /// return immediately (single owner). No-op when disabled or unbound.
    func replayPending() async {
        await loadIfNeeded()
        guard replayEnabled,
              activeServerID != nil,
              activeUserID != nil,
              let activeBinding,
              replayAuthority.isCurrent(activeBinding) else {
            return
        }
        guard !isDraining else { return }
        isDraining = true
        defer {
            isDraining = false
            replayingItemID = nil
        }
        await drain()
    }

    private func drain() async {
        guard let server = activeServerID,
              let user = activeUserID,
              let binding = activeBinding,
              replayAuthority.isCurrent(binding) else {
            return
        }
        let capturedGeneration = generation
        var blockedEntities: Set<String> = []

        while true {
            guard capturedGeneration == generation,
                  replayAuthority.isCurrent(binding) else {
                return
            }
            guard let item = oldestPending(server: server, user: user, blocking: blockedEntities) else { return }

            let now = clock.now()
            if item.isExpired(at: now, retention: configuration.retention) {
                // 7-day window exceeded: NO network request. Terminal review;
                // block this entity so younger same-entity items don't jump it.
                if let index = items.firstIndex(where: { $0.id == item.id }) {
                    items[index].status = .expiredNeedsReview
                }
                blockedEntities.insert(item.entityKey)
                await persist()
                continue
            }

            replayingItemID = item.id
            let outcome = await transport.replay(
                item.operation,
                expectedIdentity: binding.identity
            )
            replayingItemID = nil

            // Re-validate ownership after the suspension: a switch/logout/disable
            // may have advanced the generation while the request was in flight.
            guard capturedGeneration == generation,
                  replayAuthority.isCurrent(binding),
                  activeServerID == server, activeUserID == user else { return }
            // The item may have been disposed of concurrently.
            guard let index = items.firstIndex(where: { $0.id == item.id }) else { continue }

            switch outcome {
            case .success:
                // Canonical success / idempotent replay: remove EXACTLY this one.
                items.remove(at: index)
                await persist()
            case .retryable:
                // Network still failing: stop the whole pass so ordering holds;
                // reconnect will retry. No state change needed.
                return
            case .conflict(let conflict):
                items[index].status = .conflict(conflict)
                blockedEntities.insert(item.entityKey)
                await persist()
            case .identityChanged:
                // Session no longer valid: stop without mutating the item.
                return
            }
        }
    }

    /// Oldest (FIFO) pending, non-blocked item in the namespace. An item whose
    /// `prerequisiteItemID` still names an item present in the namespace (in any
    /// status) is held back, so a linked task completion never precedes its
    /// required domain mutation (F10-Q2, #790).
    private func oldestPending(server: UUID, user: UUID, blocking blockedEntities: Set<String>) -> OfflineWriteItem? {
        let namespaceIDs = Set(
            items.filter { $0.serverID == server && $0.userID == user }.map { $0.id }
        )
        return items
            .filter {
                $0.serverID == server && $0.userID == user
                    && $0.status.isPending
                    && !blockedEntities.contains($0.entityKey)
                    && !prerequisiteStillPending($0, in: namespaceIDs)
            }
            .min(by: { $0.createdAt < $1.createdAt })
    }

    /// Whether the item's prerequisite domain-write is still present in the
    /// namespace (i.e. has not yet succeeded and left the queue).
    private func prerequisiteStillPending(_ item: OfflineWriteItem, in namespaceIDs: Set<UUID>) -> Bool {
        guard let prerequisite = item.prerequisiteItemID else { return false }
        return namespaceIDs.contains(prerequisite)
    }

    // MARK: Operator dispositions

    /// Discards a single item (any status) from the active namespace.
    @discardableResult
    func discard(itemID: UUID) async -> Bool {
        await loadIfNeeded()
        guard let server = activeServerID,
              let user = activeUserID,
              let activeBinding,
              replayAuthority.isCurrent(activeBinding) else {
            return false
        }
        guard let index = items.firstIndex(where: {
            $0.id == itemID && $0.serverID == server && $0.userID == user
        }) else { return false }
        items.remove(at: index)
        await persist()
        return true
    }

    /// Review-and-retry-as-new: removes the reviewed item and enqueues a brand
    /// new intent (which the caller has already confirmed against current server
    /// state and stamped with a NEW idempotency key). Never a silent replay of
    /// the old body/key.
    @discardableResult
    func retryAsNew(replacing itemID: UUID, with operation: OfflineWriteOperation) async -> OfflineWriteEnqueueResult {
        await loadIfNeeded()
        _ = await discard(itemID: itemID)
        return await enqueue(operation)
    }

    // MARK: Snapshots (UI / tests)

    /// Entries for the active namespace, ordered by creation, with the transient
    /// `isReplaying` flag applied to the in-flight item.
    func activeEntries() async -> [OfflineWriteQueueEntry] {
        await loadIfNeeded()
        guard let server = activeServerID,
              let user = activeUserID,
              let activeBinding,
              replayAuthority.isCurrent(activeBinding) else {
            return []
        }
        let replaying = replayingItemID
        return items
            .filter { $0.serverID == server && $0.userID == user }
            .sorted { $0.createdAt < $1.createdAt }
            .map { OfflineWriteQueueEntry(item: $0, isReplaying: $0.id == replaying) }
    }

    /// All persisted items across every namespace (tests / diagnostics).
    func allItems() async -> [OfflineWriteItem] {
        await loadIfNeeded()
        return items
    }

    /// Items for a specific namespace regardless of the active binding (tests).
    func items(forServer server: UUID, user: UUID) async -> [OfflineWriteItem] {
        await loadIfNeeded()
        return items
            .filter { $0.serverID == server && $0.userID == user }
            .sorted { $0.createdAt < $1.createdAt }
    }

    var activeIdentity: OfflineWriteReplayIdentity? {
        guard let activeServerID,
              let activeUserID,
              let activeBinding,
              replayAuthority.isCurrent(activeBinding) else {
            return nil
        }
        return OfflineWriteReplayIdentity(serverID: activeServerID, userID: activeUserID)
    }

    var isReplayEnabled: Bool { replayEnabled }
}
