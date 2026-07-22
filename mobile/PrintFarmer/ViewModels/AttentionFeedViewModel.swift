import Foundation
import Observation

// MARK: - Public shape

/// Coarse view-model phase surfaced to the shell. Mutually exclusive.
///
/// * `idle`       — no request has ever run for the current lifecycle.
/// * `loading`    — the first canonical fetch is in flight and no page
///                  has been applied yet.
/// * `loaded`     — at least one canonical page has been applied. The
///                  view renders either items + healthy summary or an
///                  explicit empty surface derived from `items.isEmpty`
///                  and `healthyPrinterCount`.
/// * `error`      — the first fetch (no page applied yet) failed. The
///                  shell shows the retry surface; a later successful
///                  refresh transitions to `loaded`.
/// * `disabled`   — feature gate reports Attention off, or a canonical
///                  fetch returned `NetworkError.featureDisabled`. The
///                  shell shows the safe fallback surface.
enum AttentionFeedPhase: Equatable {
    case idle
    case loading
    case loaded
    case error
    case disabled
}

/// Snapshot of a single canonical fetch generation applied to the view
/// model. Everything the shell renders derives from this struct.
struct AttentionFeedSnapshot: Equatable, Sendable {
    var items: [AttentionItem]
    var nextCursor: String?
    var healthyPrinterCount: Int
}

/// Stable severity ordering used by the shell. Matches the backend's
/// canonical grouping (`critical` > `warning` > `info` > unknown). The
/// server already orders items within each severity — the shell must
/// not re-rank inside a group.
enum AttentionSeverityBucket: Int, CaseIterable, Sendable, Equatable {
    case critical = 0
    case warning = 1
    case info = 2
    case unknown = 3

    init(_ severity: AttentionSeverity) {
        switch severity {
        case .critical: self = .critical
        case .warning: self = .warning
        case .info: self = .info
        case .unknown: self = .unknown
        }
    }

    var accessibilityLabel: String {
        switch self {
        case .critical: "Critical"
        case .warning: "Warning"
        case .info: "Informational"
        case .unknown: "Other"
        }
    }
}

/// One severity group as rendered by the shell. Items keep server order.
struct AttentionSeverityGroup: Equatable, Sendable, Identifiable {
    let severity: AttentionSeverityBucket
    let items: [AttentionItem]

    var id: Int { severity.rawValue }
}

/// A latched load failure. `id` refreshes each time so SwiftUI treats
/// consecutive failures as distinct even when the message is identical.
struct AttentionLoadFailure: Equatable, Identifiable, Sendable {
    let id: UUID
    let message: String
}

/// A pagination-specific load failure bound to the cursor that failed.
/// The sentinel gates its auto-load on this being nil for the current
/// cursor — if the same cursor is showing and it already failed, the
/// operator must explicitly retry.
struct AttentionPaginationFailure: Equatable, Identifiable, Sendable {
    let id: UUID
    let cursor: String
    let message: String
}

struct AttentionActionFailure: Equatable, Identifiable, Sendable {
    let id: UUID
    let itemID: String
    let action: AttentionAction
    let snoozedUntilUtc: Date?
    let message: String
}

enum AttentionItemActionState: Equatable, Sendable {
    case idle
    case inProgress(AttentionActionKind)
    case failed(AttentionActionFailure)
}

enum AttentionMediaState: Equatable, Sendable {
    case idle
    case loading
    case available(Data)
    case unavailable(String)
}

struct AttentionMediaRequestID: Hashable, Sendable {
    let itemID: String
    let generation: UInt64
}

/// Opaque token issued by the view model to a caller that intends to
/// perform an async operation which must not fire if the view model was
/// deactivated between capture and completion. The view uses this to
/// guard `bootstrap` against a capability-refresh await that swallowed
/// cancellation and returned after `.onDisappear` already ran.
struct AttentionLifecycleToken: Equatable, Sendable {
    fileprivate let value: UInt64
}

/// Lock-protected monotonic event sequence issuer. The SignalR handler
/// issues a strictly-increasing sequence number for each event at
/// **delivery time** (on the signalR delivery queue). The MainActor
/// then uses that number, alongside a per-refresh "start-cover
/// snapshot" of the sequence, to decide whether a canonical refresh
/// has already covered the event or a follow-up is required.
///
/// This box replaces the previous completion-only fence, which could
/// not distinguish an event that preceded a refresh request from one
/// that arrived during it, and therefore either issued a redundant
/// second GET or dropped a necessary follow-up.
///
/// The class is `Sendable` so it can be captured by closures that
/// straddle the signalR delivery queue and MainActor. The lock is
/// sufficient — reads and writes are all short scalar copies.
final class EventSequenceBox: @unchecked Sendable {
    private let lock = NSLock()
    private var _value: UInt64 = 0

    /// Latest issued sequence, or 0 if none have been issued. Read
    /// on MainActor at refresh start to snapshot the "cover watermark".
    var currentValue: UInt64 {
        lock.lock()
        defer { lock.unlock() }
        return _value
    }

    /// Atomically increment and return the new sequence. Called on the
    /// signalR delivery queue when an `attentionchanged` event
    /// arrives, giving every event a unique strictly-increasing id.
    func issueNext() -> UInt64 {
        lock.lock()
        defer { lock.unlock() }
        _value &+= 1
        return _value
    }
}

// MARK: - View model

/// Canonical Attention feed state plus item-scoped action and failure-media
/// coordination. Feed replacement remains generation-authoritative; item
/// operations never mutate server truth locally.
@MainActor
@Observable
final class AttentionFeedViewModel {
    typealias CallbackEnqueuer = @Sendable (
        @escaping @MainActor @Sendable () async -> Void
    ) -> Void

    // MARK: Observed state

    /// Coarse shell phase. Callers switch UI on this, but also consult
    /// `snapshot`, `isRefreshing`, `isLoadingMore`, and `loadFailure` for
    /// the fully mutually-consistent surface documented in #779.
    private(set) var phase: AttentionFeedPhase = .idle

    /// Latest applied canonical snapshot. `nil` before the first
    /// successful fetch of the current lifecycle. After success this
    /// stays non-nil and refreshes atomically (see `applySnapshot`).
    private(set) var snapshot: AttentionFeedSnapshot?

    /// Grouped view of `snapshot?.items` in canonical severity order,
    /// preserving server order within each group. Recomputed only when
    /// items change so SwiftUI equality checks stay cheap.
    private(set) var groups: [AttentionSeverityGroup] = []

    /// The most recent load failure. `nil` when the latest completed
    /// fetch of the current lifecycle succeeded.
    private(set) var loadFailure: AttentionLoadFailure?

    /// The most recent pagination failure (from `loadMore`). Bound to
    /// the specific cursor that failed so we can distinguish "this page
    /// failed" from "the whole feed failed". Sentinel-driven auto-load
    /// is disabled while this is set for the current cursor —
    /// preventing the retry loop where `.onAppear` re-fires on every
    /// SwiftUI list rebuild.
    private(set) var paginationFailure: AttentionPaginationFailure?

    /// Local UI state for the "N printers running normally" summary row.
    /// Defaults to collapsed; the view supplies expansion state.
    var isHealthySummaryExpanded: Bool = false

    /// True while a canonical refresh (first page) is in flight after
    /// the very first success. Distinct from `phase == .loading`, which
    /// is only used before any page has been applied.
    private(set) var isRefreshing: Bool = false

    /// True while a `loadMore` page is in flight. The shell uses this to
    /// disable the load-more trigger and avoid duplicate requests.
    private(set) var isLoadingMore: Bool = false

    /// Item-scoped mutation state. A request for one item never disables
    /// actions or recovery on another item.
    private(set) var actionStates: [String: AttentionItemActionState] = [:]

    /// Failure snapshot state keyed by Attention item identity.
    private(set) var mediaStates: [String: AttentionMediaState] = [:]

    /// Canonical replacement/deactivation stamp used by row `.task(id:)`
    /// and stale media completion fences.
    private(set) var mediaGeneration: UInt64 = 0

    // MARK: Lifecycle plumbing

    @ObservationIgnored private let callbackEnqueuer: CallbackEnqueuer
    @ObservationIgnored private let now: @Sendable () -> Date
    @ObservationIgnored private var attentionService: (any AttentionServiceProtocol)?
    @ObservationIgnored private var printerService: (any PrinterServiceProtocol)?
    @ObservationIgnored private var signalRService: (any SignalRServiceProtocol)?
    @ObservationIgnored private var serviceIdentity: ObjectIdentifier?
    @ObservationIgnored private var printerServiceIdentity: ObjectIdentifier?
    @ObservationIgnored private var signalRIdentity: ObjectIdentifier?
    @ObservationIgnored private var actionGeneration: UInt64 = 0
    @ObservationIgnored private var actionOperationTokens: [String: UUID] = [:]
    @ObservationIgnored private var deferredMutationInvalidationSequences: [String: UInt64] = [:]
    @ObservationIgnored private var attentionEnabled = true
    @ObservationIgnored private var isActive = false
    /// Bumped whenever `configure` replaces the service/signalR pair.
    /// Every SignalR handler registered on the old authority captures
    /// its epoch and drops when the epoch has moved. A repeat configure
    /// with the same identities does NOT bump the epoch — the early
    /// return above short-circuits before we get here.
    @ObservationIgnored private var authorityEpoch: UInt64 = 0
    /// Bumped on every `deactivate` and `activate` (and every service
    /// replacement, transitively). Loads capture it at start and drop
    /// on completion if the epoch has moved — this closes the window
    /// where an activation-A load could apply into a fresh activation-B
    /// state after a deactivate/reactivate cycle.
    @ObservationIgnored private var activationEpoch: UInt64 = 0
    /// Monotonic lifecycle token issued to callers that plan to await
    /// external work (capability refresh, etc.) before calling
    /// `bootstrap`. Bumped on every `deactivate`. `bootstrap` compares
    /// the caller-supplied token and refuses to run if the token was
    /// issued in a superseded lifecycle. Prevents a swallowed-cancel
    /// capability await from reactivating an off-screen view.
    @ObservationIgnored private var lifecycleToken: UInt64 = 0
    /// Bumped on every fetch (`refresh`, `loadMore`). Each in-flight
    /// operation captures the stamp at start and drops its outcome if
    /// the stamp has moved by application time. This is what makes
    /// reverse-order completions safe even within a single authority.
    @ObservationIgnored private var loadStamp: UInt64 = 0
    /// The load stamp that currently owns the `isLoadingMore` flag. A
    /// completion clears the flag only when its captured stamp still
    /// matches this owner — protects the flag from being cleared by an
    /// older completion that fires after a newer request has taken over.
    @ObservationIgnored private var loadMoreOwnerStamp: UInt64 = 0
    @ObservationIgnored private var attentionSubscription: SignalRSubscription?
    /// Sendable, lock-protected monotonic event sequence issuer. Each
    /// SignalR `attentionchanged` event is stamped with a strictly
    /// increasing sequence at delivery time. Every refresh captures
    /// the current sequence at request-start as its "cover watermark"
    /// — the refresh covers exactly the events whose sequence is ≤
    /// that watermark.
    @ObservationIgnored private let eventSequenceBox = EventSequenceBox()
    /// The highest event sequence that has been covered by an APPLIED
    /// successful canonical refresh. Advanced only on `applySnapshot`
    /// success; failures and disabled outcomes do NOT advance it, so
    /// pending coverage requirements survive a failed refresh without
    /// creating a retry loop.
    @ObservationIgnored private var lastCoveredEventSequence: UInt64 = 0
    /// The highest event sequence that has been received (via SignalR
    /// drain) but has not yet been guaranteed covered by a completed
    /// successful refresh. Cleared when a subsequent refresh's cover
    /// watermark reaches it. Drives the follow-up decision at refresh
    /// completion and the drain decision at reactivation.
    @ObservationIgnored private var pendingCoverageEventSequence: UInt64?
    /// Authority-scoped ownership set of currently in-flight canonical
    /// refreshes. Every refresh generates a unique `UUID` token at
    /// start and registers it here; only same-authority completions
    /// remove their token. Old-authority completions (whose captured
    /// `authorityEpoch` no longer matches) neither insert nor remove
    /// from this set — so they cannot corrupt the current
    /// authority's ownership accounting.
    ///
    /// Cycle 6 replaces the previous global `activeRefreshCount: Int`
    /// (which could underflow when an old-authority completion
    /// decremented after `invalidateAuthority` had already reset it,
    /// masked by `max(0, ...)`). Set-membership with authority
    /// tokens makes cross-authority release literally impossible: an
    /// old completion has nothing to remove because its token was
    /// discarded when the authority changed.
    @ObservationIgnored private var activeRequestTokens: Set<UUID> = []

    /// Deduplicated ID set for `loadMore` appends. Rebuilt atomically on
    /// canonical refresh so refresh resets are all-or-nothing.
    @ObservationIgnored private var knownIDs: Set<String> = []

    /// True when a refresh was requested while the view was inactive.
    /// Drains to exactly one canonical refetch when `activate` is
    /// called with a matching service, matching #779's "exactly one
    /// queued refresh drains on re-entry" contract.
    ///
    /// Cycle 8: this flag is now CLAIMED (dedupe token below) rather
    /// than consumed at schedule time. A fenced closure only clears
    /// it when it actually fires. If a same-authority activation
    /// drift invalidates the closure, the flag persists and the
    /// next `activate` / `configure` re-entry queues a fresh closure
    /// under the current activation — preserving the reload intent
    /// instead of stranding it.
    @ObservationIgnored private var pendingReloadOnActivate = false
    /// The `activationEpoch` value for which we have already scheduled
    /// an owner-checked reload closure. Prevents queuing duplicate
    /// closures when `activate` runs multiple times within the same
    /// activation. Reset when the closure fires (successful claim),
    /// on service replacement (`invalidateAuthority`), or when the
    /// activation moves (checked at schedule time).
    @ObservationIgnored private var pendingReloadClaimedForActivation: UInt64?

    init(now: @escaping @Sendable () -> Date = { Date() }) {
        self.now = now
        self.callbackEnqueuer = { operation in
            Task { @MainActor in
                await operation()
            }
        }
    }

    #if DEBUG
    init(
        callbackEnqueuer: @escaping CallbackEnqueuer,
        now: @escaping @Sendable () -> Date = { Date() }
    ) {
        self.now = now
        self.callbackEnqueuer = callbackEnqueuer
    }
    #endif

    // MARK: - Configuration

    /// Bind the view model to a service + signalR pair. Called once when
    /// the shell appears and again whenever the active server switches.
    /// Repeated calls with the same identities and gate are a no-op, so
    /// SwiftUI `.task` re-runs do not stack `attentionchanged` handlers
    /// on the same service instance (#779 acceptance criterion).
    func configure(
        attentionService: any AttentionServiceProtocol,
        signalRService: any SignalRServiceProtocol,
        attentionEnabled: Bool
    ) {
        let newServiceIdentity = ObjectIdentifier(attentionService as AnyObject)
        let newSignalRIdentity = ObjectIdentifier(signalRService as AnyObject)

        // If the shell just re-appeared with the same authority
        // (`.task` running again after `.onDisappear` deactivated us),
        // re-use the existing subscription and drain a queued reload
        // instead of tearing everything down. Any refresh triggered
        // externally after this returns is subject to the normal load
        // stamp; the existing snapshot / phase are preserved.
        if serviceIdentity == newServiceIdentity,
           signalRIdentity == newSignalRIdentity,
           self.attentionEnabled == attentionEnabled,
           attentionSubscription != nil {
            if !isActive {
                isActive = true
                // Reactivation via configure is a new activation epoch
                // for the same reason `activate()` bumps: any in-flight
                // load from before the last deactivate must drop, not
                // apply into fresh reactivation state.
                activationEpoch &+= 1
                // Cycle 8: claim-based reload scheduling. Only queue
                // a closure when pending intent exists AND no
                // outstanding claim for the CURRENT activation. If
                // we drift again, the claim goes stale but pending
                // is preserved — the next activation re-queues.
                if pendingReloadOnActivate,
                   pendingReloadClaimedForActivation != activationEpoch {
                    pendingReloadClaimedForActivation = activationEpoch
                    enqueueOwnerCheckedReload(
                        capturedAuthority: authorityEpoch,
                        capturedActivation: activationEpoch
                    )
                }
            }
            return
        }

        // Service, signalR, or gate changed: drop authority and rebuild.
        invalidateAuthority(resetState: true)

        self.attentionService = attentionService
        self.signalRService = signalRService
        self.serviceIdentity = newServiceIdentity
        self.signalRIdentity = newSignalRIdentity
        self.attentionEnabled = attentionEnabled
        self.isActive = true

        if !attentionEnabled {
            phase = .disabled
        }

        let capturedEpoch = authorityEpoch
        let enqueue = callbackEnqueuer
        let sequenceBox = eventSequenceBox
        attentionSubscription = signalRService.onAttentionChanged { [weak self] event in
            // Issue the event's monotonic sequence at signalR delivery
            // time. This gives a strict ordering between events and
            // subsequent refresh-start "cover watermarks", regardless
            // of when the callback drains on MainActor.
            let eventSeq = sequenceBox.issueNext()
            enqueue { [weak self] in
                guard let self,
                      self.matchesAuthorityIgnoringActive(
                        epoch: capturedEpoch,
                        serviceIdentity: newServiceIdentity,
                        signalRIdentity: newSignalRIdentity
                      ) else {
                    return
                }
                await self.handleInvalidationDrain(
                    eventSequence: eventSeq,
                    itemID: event.itemId
                )
            }
        }
    }

    func configureSnapshotService(_ printerService: any PrinterServiceProtocol) {
        let identity = ObjectIdentifier(printerService as AnyObject)
        guard printerServiceIdentity != identity else { return }

        self.printerService = printerService
        printerServiceIdentity = identity
        invalidateMediaState(clearTerminalStates: true)
    }

    /// Central invalidation-drain handler with strict request/event
    /// ordering. Called on MainActor via `callbackEnqueuer`.
    ///
    /// The property we enforce:
    ///
    ///   > An event E is COVERED by canonical refresh R iff R started
    ///   > strictly after E was received; equivalently, R's start-cover
    ///   > watermark ≥ E's sequence.
    ///
    /// This lets us distinguish an event that preceded a request from
    /// one that arrived during it, and route them correctly:
    ///
    /// * Already-covered event → skip (no fetch).
    /// * Off-screen event → latch pending + queue reload on activate.
    /// * Uncovered event during an in-flight refresh → latch pending
    ///   without dispatching (the completion path launches a
    ///   follow-up if the in-flight didn't cover us).
    /// * Uncovered event with no refresh in flight → dispatch refresh.
    private func handleInvalidationDrain(
        eventSequence eventSeq: UInt64,
        itemID: String
    ) async {
        // Already covered by a completed successful refresh — no work.
        if eventSeq <= lastCoveredEventSequence { return }

        // Latch coverage requirement. This is authoritative: any refresh
        // completion path checks pendingCoverageEventSequence against
        // its own cover watermark. Setting it here is safe even if
        // we ultimately end up dispatching immediately (redundant with
        // the dispatched refresh's cover snapshot).
        pendingCoverageEventSequence = max(
            pendingCoverageEventSequence ?? 0,
            eventSeq
        )

        if actionOperationTokens[itemID] != nil {
            deferredMutationInvalidationSequences[itemID] = max(
                deferredMutationInvalidationSequences[itemID] ?? 0,
                eventSeq
            )
            return
        }

        if !isActive {
            // Off-screen: drain on activate.
            pendingReloadOnActivate = true
            return
        }

        if !activeRequestTokens.isEmpty {
            // A refresh is already in flight (under the current
            // authority). It may or may not cover this event
            // (depends on its start-cover watermark vs eventSeq).
            // The completion path will check
            // pendingCoverageEventSequence and launch a follow-up if
            // uncovered. Do NOT dispatch a concurrent refresh here —
            // that would violate the "no duplicate GET merely because
            // a callback drained" invariant.
            return
        }

        // Active and idle: dispatch a canonical refresh. Its cover
        // watermark will include this event (since it starts after
        // the event was received) and its success completion will
        // clear the pending coverage requirement.
        await refresh()
    }

    /// Called when the view leaves the screen. The next `configure` for
    /// a matching authority will drain a queued reload rather than
    /// starting from `.idle`. Also bumps `lifecycleToken` so any
    /// caller that captured a token before an async await sees the
    /// token become stale and refuses to `bootstrap`.
    func deactivate() {
        if !actionOperationTokens.isEmpty {
            pendingReloadOnActivate = true
        }
        invalidateItemOperationsForDeactivation()
        // Preserve `snapshot` so re-entering the view keeps the last
        // rendered content until the queued refresh completes; but drop
        // any in-flight authority so completions can't mutate state.
        isActive = false
        // Bumping the activation epoch fences off any in-flight refresh
        // or loadMore whose outcome would otherwise land into a fresh
        // reactivation. See `matchesActive`.
        activationEpoch &+= 1
        // Bumping the lifecycle token invalidates any caller that
        // captured a token before a pre-bootstrap async await (e.g.
        // capability refresh). See `bootstrap(lifecycleToken:)`.
        lifecycleToken &+= 1
        // Any load finishing while inactive will notice the mismatched
        // active state and clear loading without applying its result.
        // See applySnapshot / applyFailure / applyDisabled below.
    }

    /// Returns a lifecycle token the caller should re-supply to
    /// `bootstrap` after any pre-bootstrap `await` (typically the
    /// capability refresh in the view's `.task(id:)`). If the token
    /// value has moved by bootstrap time (because `deactivate` bumped
    /// it), the bootstrap becomes a no-op — no reactivation, no
    /// subscription, no fetch.
    func currentLifecycleToken() -> AttentionLifecycleToken {
        AttentionLifecycleToken(value: lifecycleToken)
    }

    /// Called when the view re-appears. If a `pendingReloadOnActivate`
    /// flag was set (typically by a signalR event that arrived while the
    /// tab was in the background), exactly one canonical refresh drains.
    ///
    /// Cycle 8: uses claim-based scheduling. The pending flag is NOT
    /// consumed here — it is only cleared when a matching-owner
    /// closure actually fires. Activation-drift closures that
    /// invalidate themselves leave the intent latched so a subsequent
    /// activation can re-queue.
    func activate() {
        guard attentionService != nil else { return }
        let wasActive = isActive
        isActive = true
        // Every reactivation is a new activation epoch so the queued
        // drain runs against a fresh fence, and any straggler from a
        // prior activation whose completion is still queued cannot
        // apply.
        if !wasActive {
            activationEpoch &+= 1
        }
        guard pendingReloadOnActivate else { return }
        // Cycle 8: claim-based scheduling. Skip if we already have an
        // outstanding closure for the CURRENT activation (dedupe).
        // Any closure claimed for a stale activation will no-op at
        // drain and leave pending intact for us to re-queue.
        guard pendingReloadClaimedForActivation != activationEpoch else { return }
        pendingReloadClaimedForActivation = activationEpoch
        // The pending flag is deliberately NOT cleared here — the
        // closure clears it (and the claim) on successful fire.
        enqueueOwnerCheckedReload(
            capturedAuthority: authorityEpoch,
            capturedActivation: activationEpoch
        )
    }

    /// Enqueue a canonical `refresh()` through `callbackEnqueuer` that
    /// no-ops at drain time unless the captured authority and
    /// activation still match the current owner. Shared by the two
    /// activation-reload sites (`activate()` and configure's
    /// re-entry) so both are structurally protected against
    /// old-owner drift between schedule and drain.
    ///
    /// Cycle 8: on successful drain, clears both
    /// `pendingReloadOnActivate` and `pendingReloadClaimedForActivation`
    /// atomically before awaiting `refresh()`. If the closure no-ops
    /// on drift, it leaves both flags untouched so the next
    /// activation re-queues the intent.
    private func enqueueOwnerCheckedReload(
        capturedAuthority: UInt64,
        capturedActivation: UInt64
    ) {
        let enqueue = callbackEnqueuer
        enqueue { [weak self] in
            guard let self else { return }
            // Owner-token drift: service replacement bumped authority.
            if capturedAuthority != self.authorityEpoch { return }
            // Owner-token drift: deactivate/reactivate (potentially
            // multiple times) bumped activation.
            if capturedActivation != self.activationEpoch { return }
            // VM must still be a valid owner right now.
            guard self.isActive, self.attentionEnabled,
                  self.attentionService != nil else { return }
            // Cycle 8: the reload intent may have been consumed
            // elsewhere since this closure was scheduled — for
            // example by `bootstrap()`, which owns its own follow-up
            // refresh. If the flag is no longer set, firing would
            // duplicate work. Skip.
            guard self.pendingReloadOnActivate else { return }
            // Claim the reload: clear both flags atomically before
            // awaiting refresh. This prevents a concurrent `activate`
            // from observing the flag still set and queuing another
            // closure for the same activation.
            self.pendingReloadOnActivate = false
            self.pendingReloadClaimedForActivation = nil
            await self.refresh()
        }
    }

    /// Bind + activate + issue exactly one canonical refresh in a way
    /// that coalesces any pending drain. Called from the view's
    /// `.task(id:)` so re-entry produces exactly one canonical GET,
    /// regardless of whether an invalidation arrived while off-screen.
    ///
    /// Contract:
    /// * `lifecycleToken` must be a token obtained from
    ///   `currentLifecycleToken()` BEFORE any pre-bootstrap `await`
    ///   (typically the capability refresh in the view's `.task(id:)`).
    ///   If `deactivate` has bumped the internal token between capture
    ///   and this call, bootstrap returns `false` without reactivating,
    ///   subscribing, or fetching — this closes the window where a
    ///   swallowed-cancellation capability await could reactivate an
    ///   off-screen view.
    /// * Bootstrap consumes any queued drain flag before configuring
    ///   so `configure`'s re-entry path does not enqueue a duplicate
    ///   fetch through the callback path.
    /// * Bootstrap then awaits a single `refresh()` — that is the one
    ///   canonical GET per bootstrap invocation.
    @discardableResult
    func bootstrap(
        attentionService: any AttentionServiceProtocol,
        signalRService: any SignalRServiceProtocol,
        attentionEnabled: Bool,
        lifecycleToken: AttentionLifecycleToken? = nil,
        printerService: (any PrinterServiceProtocol)? = nil
    ) async -> Bool {
        // #779 blocker 1: refuse to run when the lifecycle has been
        // superseded by a `deactivate`. Any state mutation here would
        // reactivate an off-screen view.
        if let lifecycleToken, lifecycleToken.value != self.lifecycleToken {
            return false
        }
        // Consume any queued drain before configure so its re-entry
        // path does not enqueue a duplicate refresh through the
        // callback enqueuer. Bootstrap owns the fetch below.
        //
        // Cycle 8: also clear the claim, so any already-queued
        // owner-checked reload closure that was scheduled for the
        // current activation will find `pendingReloadOnActivate ==
        // false` mid-flight... actually the closure clears both
        // itself. The important invariant: bootstrap's own refresh
        // subsumes any pending event coverage, so subsequent
        // closures firing would be duplicates. Clearing the claim
        // ensures the enqueued closure will not re-schedule on the
        // dedupe check.
        pendingReloadOnActivate = false
        pendingReloadClaimedForActivation = nil
        configure(
            attentionService: attentionService,
            signalRService: signalRService,
            attentionEnabled: attentionEnabled
        )
        if let printerService {
            configureSnapshotService(printerService)
        }
        return await refresh()
    }

    /// User-driven recovery from the feature-disabled surface. Clears
    /// the internal disabled gate and phase so the next canonical
    /// refresh actually hits the network again. The view calls this
    /// after re-resolving `SystemCapabilities` so `attentionEnabled`
    /// reflects the latest server truth.
    func retryDisabledRecovery(attentionEnabled newAttentionEnabled: Bool) {
        self.attentionEnabled = newAttentionEnabled
        // Bump the load stamp so any in-flight load (from before the
        // gate flip) drops on completion instead of re-applying
        // disabled state after the user asked to recover.
        _ = advanceLoadStamp()
        if newAttentionEnabled {
            if snapshot == nil {
                phase = .idle
            } else {
                phase = .loaded
            }
        } else {
            phase = .disabled
        }
    }

    // MARK: - Loading

    /// Perform (or coalesce onto) the canonical `GET /api/attention`
    /// fetch that resets the feed to the newest server truth.
    @discardableResult
    func refresh() async -> Bool {
        guard let service = attentionService, attentionEnabled else {
            // Feature-disabled or unconfigured: nothing to do.
            if !attentionEnabled { phase = .disabled }
            return false
        }
        guard isActive else {
            // #779: inactive-mid-load — queue exactly one refresh to
            // drain on next activate; never leave `isRefreshing` set.
            pendingReloadOnActivate = true
            isRefreshing = false
            if phase == .loading { phase = .idle }
            return false
        }

        // Capture BOTH the load stamp (prevents reverse-order applies
        // within a single activation) and the activation epoch (fences
        // off applies across a deactivate/reactivate boundary). An
        // older load whose activation is no longer current cannot
        // mutate visible state.
        let stampedLoad = advanceLoadStamp()
        let stampedActivation = activationEpoch
        // Capture the authority epoch so an old-authority completion
        // (this refresh was started under authority A, but the
        // service+signalR pair has been replaced with B by the time
        // we resume) can be detected and made a total no-op — no
        // token release, no coverage advance, no follow-up. See
        // cycle-6 blocker A.
        let stampedAuthority = authorityEpoch

        // Snapshot the event-sequence "cover watermark" NOW, before
        // awaiting the fetch. This request covers exactly the events
        // whose sequence is ≤ this watermark. Events issued after this
        // point are NOT covered — the completion path will schedule a
        // follow-up refresh for any such pending coverage.
        let startCoverSnapshot = eventSequenceBox.currentValue

        // Authority-scoped ownership: register this refresh in the
        // current authority's token set. Cross-authority completions
        // will neither insert nor remove, so the set can never
        // underflow or be corrupted by old-owner work.
        let requestToken = UUID()
        activeRequestTokens.insert(requestToken)

        if snapshot == nil {
            phase = .loading
        } else {
            isRefreshing = true
        }

        let outcome = await Self.fetchFirstPage(service: service)

        // Cross-authority completion: this refresh started under a
        // previous authority (invalidateAuthority has since run and
        // bumped authorityEpoch). Treat as a total no-op:
        //   * do NOT release a token from the current authority's set
        //     (our token was discarded when the set was reset),
        //   * do NOT touch coverage/pending under the current authority,
        //   * do NOT schedule follow-up work against the new authority.
        // The current authority owns its own ownership accounting.
        if stampedAuthority != authorityEpoch {
            return false
        }

        // Same-authority: release our token. Follow-up gate below
        // then observes an authority-scoped, precisely-accurate
        // "in-flight" view — the empty check is only true when THIS
        // authority's refreshes have all completed.
        activeRequestTokens.remove(requestToken)

        guard matchesActive(loadStamp: stampedLoad, activation: stampedActivation) else {
            // Stale generation OR view went inactive OR reactivated
            // under a fresh activation epoch: never overwrite.
            //
            // If we went inactive AND this call still owns the load
            // stamp (no newer refresh took over), clear the loading
            // flag so a subsequent reactivation doesn't observe a
            // permanently spinning shell, and queue exactly one
            // canonical refresh to drain on re-entry. The
            // activationEpoch check is intentionally omitted here: a
            // deactivate bumps activationEpoch, so requiring epoch
            // equality would prevent the stale-inactive path from
            // ever clearing — the very case #779 requires.
            if !isActive && loadStamp == stampedLoad {
                isRefreshing = false
                if phase == .loading { phase = .idle }
                pendingReloadOnActivate = true
            }
            // #779 cycle-5 reachable-stranding fix: even a
            // generation-stale completion (loadStamp advanced by a
            // newer concurrent refresh) evaluates the follow-up gate.
            // If we are the LAST active refresh AND our captured
            // activation is still current (i.e. this is only
            // generation-stale, not authority-invalidated), an
            // uncovered pending event must trigger exactly one
            // follow-up canonical GET. Coverage is NOT advanced here
            // — only valid applied successes may mark events covered.
            tryScheduleFollowupIfPending(capturedActivation: stampedActivation)
            return false
        }

        switch outcome {
        case .success(let feed):
            applySnapshot(feed, mode: .refresh)
            // Advance coverage. Any event with sequence ≤ startCover is
            // now proven covered by an applied successful refresh.
            lastCoveredEventSequence = max(lastCoveredEventSequence, startCoverSnapshot)
            if let pending = pendingCoverageEventSequence,
               pending <= lastCoveredEventSequence {
                pendingCoverageEventSequence = nil
            }
            pruneCoveredMutationInvalidations()
            tryScheduleFollowupIfPending(capturedActivation: stampedActivation)
            return true
        case .featureDisabled:
            applyDisabled()
            return false
        case .failure(let error):
            applyFailure(error)
            return false
        }
    }

    /// Centralized "last-refresh-wins" follow-up scheduler. Called from
    /// every applied-success completion AND from every generation-stale
    /// completion. Schedules exactly one canonical follow-up refresh
    /// through `callbackEnqueuer` iff:
    ///
    /// * this completion released the last active refresh
    ///   (`activeRequestTokens.isEmpty`),
    /// * the completion's captured activation is still current
    ///   (distinguishes generation-stale within a valid authority
    ///   from authority-invalidated old-owner work),
    /// * the authority is currently valid (`isActive`,
    ///   `attentionEnabled`, `attentionService != nil`), and
    /// * there is uncovered pending event coverage
    ///   (`pendingCoverageEventSequence > lastCoveredEventSequence`).
    ///
    /// Cycle 6: the queued closure ALSO captures the current
    /// authorityEpoch and activationEpoch, and re-checks them at
    /// drain time. This prevents an old-owner follow-up (scheduled
    /// under authority A, drained after A→B replacement) from
    /// executing a refresh against the wrong authority and stealing
    /// B's bootstrap slot.
    ///
    /// Deliberately not called from `.failure` / `.featureDisabled`
    /// completion paths to preserve the no-auto-loop invariant.
    private func tryScheduleFollowupIfPending(capturedActivation: UInt64) {
        // Only the last active refresh schedules a follow-up.
        guard activeRequestTokens.isEmpty else { return }
        // Authority validity: don't schedule for a deactivated view,
        // a disabled feature, or a service that has since been
        // replaced (nil after invalidateAuthority reset).
        guard isActive, attentionEnabled, attentionService != nil else { return }
        // Distinguish generation staleness (same activation, newer
        // loadStamp took over) from authority invalidity (activation
        // moved via deactivate / invalidateAuthority).
        guard capturedActivation == activationEpoch else { return }
        guard let pending = pendingCoverageEventSequence,
              pending > lastCoveredEventSequence else { return }
        guard !isPendingCoverageDeferredToMutation(pending) else { return }

        // Cycle 6 blocker B: capture BOTH the authority and activation
        // at schedule time. The queued closure re-checks them at drain
        // time — if either has moved (service replacement bumps
        // authority; deactivate/activate bumps activation), the
        // follow-up must no-op instead of firing an old-owner refresh
        // against a new lifecycle.
        let capturedAuthority = authorityEpoch
        let enqueue = callbackEnqueuer
        enqueue { [weak self] in
            guard let self else { return }
            // Authority still current at drain time? Old-owner
            // follow-up from a superseded authority must not fire
            // against the new one.
            if capturedAuthority != self.authorityEpoch { return }
            // Activation still current? Prevent old-activation
            // refresh reactivating an off-screen or freshly
            // reactivated view.
            if capturedActivation != self.activationEpoch { return }
            // Authority still valid?
            guard self.isActive, self.attentionEnabled,
                  self.attentionService != nil else { return }
            // Pending still uncovered under CURRENT authority?
            // (`invalidateAuthority` resets `pendingCoverageEventSequence`
            // so this is implicit after the authority-epoch check,
            // but explicit for defensive clarity.)
            guard let pending = self.pendingCoverageEventSequence,
                  pending > self.lastCoveredEventSequence else { return }
            guard !self.isPendingCoverageDeferredToMutation(pending) else {
                return
            }
            _ = pending
            await self.refresh()
        }
    }

    /// Retry after a load failure. Returns false when the failure IDs no
    /// longer match (stale retry taps ignored).
    @discardableResult
    func retryLoad(failureID: UUID) async -> Bool {
        guard loadFailure?.id == failureID else { return false }
        return await refresh()
    }

    /// Best-effort load of the next page. Bounded so the shell can call
    /// it aggressively from an `onAppear` on the sentinel row without
    /// dispatching redundant requests.
    ///
    /// Preconditions the caller does not need to enforce:
    /// * A `nextCursor` must be present.
    /// * No canonical refresh may be in flight (`isRefreshing == false`).
    /// * No prior `loadMore` may still be pending (`isLoadingMore == false`).
    /// * No unresolved pagination failure for the current cursor
    ///   (`paginationFailure?.cursor != snapshot.nextCursor`). This
    ///   prevents the `.onAppear`-driven auto-retry loop: after a
    ///   failed page, the sentinel is expected to render a Retry
    ///   button and stay quiescent until the operator calls
    ///   `retryLoadMore()`.
    ///
    /// The completion path uses an owner-token pattern: only the request
    /// that set `isLoadingMore` may clear it, and it does so on every
    /// terminal path (including when the view has since deactivated).
    /// This prevents an inactive-completion from permanently wedging
    /// `isLoadingMore = true` and killing further pagination on re-entry.
    @discardableResult
    func loadMore() async -> Bool {
        guard isActive, attentionEnabled, let service = attentionService else {
            return false
        }
        guard let cursor = snapshot?.nextCursor, !cursor.isEmpty else {
            return false
        }
        guard !isRefreshing, !isLoadingMore else { return false }
        // #779 blocker 3: refuse to auto-retry a cursor that already
        // failed. The `.onAppear`-driven sentinel is idempotent-safe
        // because SwiftUI may re-fire `.onAppear` on every list rebuild
        // — silently re-attempting the same failed request in a tight
        // loop would burn requests without operator intent. The retry
        // path is `retryLoadMore()`.
        if let paginationFailure, paginationFailure.cursor == cursor {
            return false
        }

        let stampedLoad = advanceLoadStamp()
        let stampedActivation = activationEpoch
        loadMoreOwnerStamp = stampedLoad
        isLoadingMore = true

        let outcome = await Self.fetchNextPage(service: service, cursor: cursor)

        // Release ownership of `isLoadingMore` regardless of whether the
        // outcome is applied — but only when *this* request still owns
        // it. An older completion that races a newer request must NOT
        // clear a flag it no longer owns.
        let stillOwnsLoadMore = (loadMoreOwnerStamp == stampedLoad)
        if stillOwnsLoadMore {
            isLoadingMore = false
        }

        guard matchesActive(loadStamp: stampedLoad, activation: stampedActivation) else {
            // Stale generation, view inactive, or activation epoch
            // moved: drop this page. `isLoadingMore` has already been
            // released above (if we still owned it) so pagination is
            // not wedged for the next activation.
            return false
        }

        switch outcome {
        case .success(let feed):
            applyAppendedPage(feed)
            return true
        case .featureDisabled:
            // A backend disable racing pagination collapses to the safe
            // fallback surface, matching the #779 mutually-consistent
            // state requirement.
            applyDisabled()
            return false
        case .failure(let error):
            // Latch the failure to this specific cursor so the sentinel
            // does not auto-retry. Operator explicitly retries via
            // `retryLoadMore()`.
            let message = (error as? LocalizedError)?.errorDescription
                ?? error.localizedDescription
            paginationFailure = AttentionPaginationFailure(
                id: UUID(),
                cursor: cursor,
                message: message
            )
            return false
        }
    }

    /// Explicit retry entry point for a failed `loadMore`. Clears the
    /// pagination failure latch for the current cursor and re-runs
    /// `loadMore`. Returns false if there is no matching latched
    /// failure to retry.
    @discardableResult
    func retryLoadMore(failureID: UUID) async -> Bool {
        guard let failure = paginationFailure, failure.id == failureID else {
            return false
        }
        paginationFailure = nil
        return await loadMore()
    }

    // MARK: - Item actions

    static let defaultSnoozeInterval: TimeInterval = 60 * 60

    static func supportedActions(in item: AttentionItem) -> [AttentionAction] {
        var seen: Set<String> = []
        return item.actions.filter { action in
            action.kind != .unknown
                && seen.insert(action.kind.rawValue).inserted
        }
    }

    func actionState(for itemID: String) -> AttentionItemActionState {
        actionStates[itemID] ?? .idle
    }

    @discardableResult
    func performAction(_ action: AttentionAction, for itemID: String) async -> Bool {
        guard let item = liveItem(id: itemID),
              let currentAction = Self.supportedActions(in: item)
                .first(where: { $0.kind == action.kind }) else {
            return false
        }

        let snoozedUntilUtc = currentAction.kind == .snooze
            ? now().addingTimeInterval(Self.defaultSnoozeInterval)
            : nil
        return await performActionRequest(
            currentAction,
            itemID: itemID,
            snoozedUntilUtc: snoozedUntilUtc
        )
    }

    @discardableResult
    func retryAction(failureID: UUID) async -> Bool {
        guard let failedState = actionStates.values.first(where: { state in
            guard case .failed(let candidate) = state else { return false }
            return candidate.id == failureID
        }), case .failed(let failure) = failedState else {
            return false
        }

        guard let item = liveItem(id: failure.itemID),
              Self.supportedActions(in: item).contains(where: {
                  $0.kind == failure.action.kind
              }) else {
            actionStates[failure.itemID] = nil
            return false
        }

        return await performActionRequest(
            failure.action,
            itemID: failure.itemID,
            snoozedUntilUtc: failure.snoozedUntilUtc
        )
    }

    @discardableResult
    private func performActionRequest(
        _ action: AttentionAction,
        itemID: String,
        snoozedUntilUtc: Date?
    ) async -> Bool {
        guard isActive, attentionEnabled, let service = attentionService else {
            return false
        }
        if case .inProgress = actionState(for: itemID) {
            return false
        }

        let token = UUID()
        let capturedActionGeneration = actionGeneration
        let capturedAuthority = authorityEpoch
        actionOperationTokens[itemID] = token
        actionStates[itemID] = .inProgress(action.kind)

        do {
            if action.kind == .snooze {
                guard let snoozedUntilUtc else {
                    preconditionFailure("Snooze actions require a deadline")
                }
                _ = try await service.snooze(
                    itemId: itemID,
                    snoozedUntilUtc: snoozedUntilUtc
                )
            } else {
                _ = try await service.executeAction(
                    itemId: itemID,
                    actionKind: action.kind
                )
            }

            guard matchesActionOperation(
                itemID: itemID,
                token: token,
                generation: capturedActionGeneration,
                authority: capturedAuthority
            ) else {
                return false
            }

            _ = await refresh()
            clearActionOperationIfCurrent(itemID: itemID, token: token)
            return true
        } catch {
            guard matchesActionOperation(
                itemID: itemID,
                token: token,
                generation: capturedActionGeneration,
                authority: capturedAuthority
            ) else {
                return false
            }

            actionOperationTokens[itemID] = nil
            if Self.isCancellation(error) {
                actionStates[itemID] = .idle
            } else {
                let message = (error as? LocalizedError)?.errorDescription
                    ?? error.localizedDescription
                actionStates[itemID] = .failed(
                    AttentionActionFailure(
                        id: UUID(),
                        itemID: itemID,
                        action: action,
                        snoozedUntilUtc: snoozedUntilUtc,
                        message: message
                    )
                )
            }
            if actionOperationTokens.isEmpty,
               !deferredMutationInvalidationSequences.isEmpty {
                deferredMutationInvalidationSequences.removeAll()
                _ = await refresh()
            }
            return false
        }
    }

    // MARK: - Failure media

    func mediaState(for itemID: String) -> AttentionMediaState {
        mediaStates[itemID] ?? .idle
    }

    func mediaRequestID(for itemID: String) -> AttentionMediaRequestID {
        AttentionMediaRequestID(itemID: itemID, generation: mediaGeneration)
    }

    @discardableResult
    func loadSnapshot(for itemID: String) async -> Bool {
        guard isActive,
              let service = printerService,
              let printerServiceIdentity,
              let item = liveItem(id: itemID),
              item.kind == .failure else {
            return false
        }
        guard mediaState(for: itemID) == .idle else { return false }

        let capturedGeneration = mediaGeneration
        mediaStates[itemID] = .loading

        do {
            let data = try await service.getSnapshot(id: item.printerId)
            guard matchesMediaOperation(
                itemID: itemID,
                generation: capturedGeneration,
                printerServiceIdentity: printerServiceIdentity
            ) else {
                return false
            }
            mediaStates[itemID] = .available(data)
            return true
        } catch {
            guard matchesMediaOperation(
                itemID: itemID,
                generation: capturedGeneration,
                printerServiceIdentity: printerServiceIdentity
            ) else {
                return false
            }

            if Self.isCancellation(error) {
                mediaStates[itemID] = .idle
            } else {
                let message = (error as? LocalizedError)?.errorDescription
                    ?? error.localizedDescription
                mediaStates[itemID] = .unavailable(message)
            }
            return false
        }
    }

    @discardableResult
    func retrySnapshot(for itemID: String) async -> Bool {
        switch mediaState(for: itemID) {
        case .idle:
            break
        case .unavailable:
            mediaStates[itemID] = .idle
        case .available:
            mediaStates[itemID] = .idle
        case .loading:
            return false
        }
        return await loadSnapshot(for: itemID)
    }

    // MARK: - Reads consumed by the view

    /// Convenience the shell checks to decide whether to attempt a
    /// paginated load. Bounded — returning false is the "stop" signal.
    ///
    /// Returns false when a `paginationFailure` is latched to the
    /// current cursor. In that state the view should show the failure
    /// affordance with a Retry button that calls `retryLoadMore`.
    var canLoadMore: Bool {
        guard isActive, attentionEnabled else { return false }
        guard let cursor = snapshot?.nextCursor, !cursor.isEmpty else {
            return false
        }
        if let paginationFailure, paginationFailure.cursor == cursor {
            return false
        }
        return !isRefreshing && !isLoadingMore
    }

    /// True when the view should show the "N printers running normally"
    /// row. Suppressed while loading the very first page so the shell
    /// never flashes an all-clear state before the server responds.
    var shouldShowHealthySummary: Bool {
        guard phase == .loaded, let snapshot else { return false }
        return snapshot.healthyPrinterCount > 0
    }

    /// True when the shell should render the empty state instead of the
    /// list. Requires an applied snapshot with no items *and* a zero
    /// healthy-printer count — a non-zero healthy count still needs to
    /// render the summary row.
    var shouldShowEmpty: Bool {
        guard phase == .loaded, let snapshot else { return false }
        return snapshot.items.isEmpty && snapshot.healthyPrinterCount == 0
    }

    // MARK: - Private application helpers

    private enum ApplyMode {
        case refresh
    }

    private enum FetchOutcome {
        case success(AttentionFeed)
        case featureDisabled
        case failure(Error)
    }

    private static func fetchFirstPage(service: any AttentionServiceProtocol) async -> FetchOutcome {
        do {
            let feed = try await service.getFeed(cursor: nil, limit: nil)
            return .success(feed)
        } catch NetworkError.featureDisabled {
            return .featureDisabled
        } catch is CancellationError {
            return .failure(CancellationError())
        } catch {
            return .failure(error)
        }
    }

    private static func fetchNextPage(
        service: any AttentionServiceProtocol,
        cursor: String
    ) async -> FetchOutcome {
        do {
            let feed = try await service.getFeed(cursor: cursor, limit: nil)
            return .success(feed)
        } catch NetworkError.featureDisabled {
            return .featureDisabled
        } catch is CancellationError {
            return .failure(CancellationError())
        } catch {
            return .failure(error)
        }
    }

    private func applySnapshot(_ feed: AttentionFeed, mode: ApplyMode) {
        // Deduplicate defensively — the server should never return
        // duplicates within a page, but a bad cursor would corrupt
        // pagination state if we trusted the payload blindly.
        var seen: Set<String> = []
        var ordered: [AttentionItem] = []
        ordered.reserveCapacity(feed.items.count)
        for item in feed.items where seen.insert(item.id).inserted {
            ordered.append(item)
        }
        let normalized = AttentionFeedSnapshot(
            items: ordered,
            nextCursor: feed.nextCursor,
            healthyPrinterCount: feed.healthyPrinterCount
        )
        reconcileItemScopedState(with: ordered)
        _ = mode // reserved for future modes; only .refresh today
        snapshot = normalized
        knownIDs = seen
        groups = Self.groupBySeverity(ordered)
        loadFailure = nil
        // A canonical refresh success clears any latched pagination
        // failure. The list has been rewritten atomically; the stale
        // failed cursor is no longer meaningful.
        paginationFailure = nil
        isRefreshing = false
        phase = .loaded
    }

    private func applyAppendedPage(_ feed: AttentionFeed) {
        var appended: [AttentionItem] = []
        var seen = knownIDs
        appended.reserveCapacity(feed.items.count)
        for item in feed.items where seen.insert(item.id).inserted {
            appended.append(item)
        }
        var existing = snapshot?.items ?? []
        existing.append(contentsOf: appended)
        let merged = AttentionFeedSnapshot(
            items: existing,
            nextCursor: feed.nextCursor,
            healthyPrinterCount: feed.healthyPrinterCount
        )
        snapshot = merged
        knownIDs = seen
        groups = Self.groupBySeverity(existing)
        loadFailure = nil
        // phase stays `.loaded`; pagination does not alter shell phase.
    }

    private func applyFailure(_ error: Error) {
        let message = (error as? LocalizedError)?.errorDescription
            ?? error.localizedDescription
        loadFailure = AttentionLoadFailure(id: UUID(), message: message)
        isRefreshing = false
        if snapshot == nil {
            phase = .error
        }
        // If we have a snapshot from a previous successful load, phase
        // stays `.loaded` and the shell renders the failure inline via
        // `loadFailure`. #779 requires refresh error to remain
        // pull-to-refresh accessible without wedging the list.
    }

    private func applyDisabled() {
        resetItemScopedState(clearTerminalMedia: true)
        attentionEnabled = false
        snapshot = nil
        groups = []
        knownIDs = []
        loadFailure = nil
        paginationFailure = nil
        isRefreshing = false
        isLoadingMore = false
        phase = .disabled
    }

    private static func groupBySeverity(_ items: [AttentionItem]) -> [AttentionSeverityGroup] {
        guard !items.isEmpty else { return [] }
        // Preserve server order inside each bucket. #779 forbids client
        // re-ranking; we only bucket.
        var buckets: [AttentionSeverityBucket: [AttentionItem]] = [:]
        for item in items {
            buckets[AttentionSeverityBucket(item.severity), default: []].append(item)
        }
        return AttentionSeverityBucket.allCases.compactMap { bucket in
            guard let bucketItems = buckets[bucket], !bucketItems.isEmpty else {
                return nil
            }
            return AttentionSeverityGroup(severity: bucket, items: bucketItems)
        }
    }

    // MARK: - Item-scoped helpers

    static func isCancellation(_ error: Error) -> Bool {
        if error is CancellationError {
            return true
        }
        if let urlError = error as? URLError {
            return urlError.code == .cancelled
        }
        if let networkError = error as? NetworkError,
           case .transportError(let urlError) = networkError {
            return urlError.code == .cancelled
        }
        return false
    }

    private func liveItem(id: String) -> AttentionItem? {
        snapshot?.items.first(where: { $0.id == id })
    }

    private func matchesActionOperation(
        itemID: String,
        token: UUID,
        generation: UInt64,
        authority: UInt64
    ) -> Bool {
        isActive
            && attentionEnabled
            && authorityEpoch == authority
            && actionGeneration == generation
            && actionOperationTokens[itemID] == token
    }

    private func clearActionOperationIfCurrent(itemID: String, token: UUID) {
        guard actionOperationTokens[itemID] == token else { return }
        actionOperationTokens[itemID] = nil
        deferredMutationInvalidationSequences[itemID] = nil
        actionStates[itemID] = liveItem(id: itemID) == nil ? nil : .idle
    }

    private func isPendingCoverageDeferredToMutation(_ pending: UInt64) -> Bool {
        let highestActiveDeferredSequence = deferredMutationInvalidationSequences
            .filter { actionOperationTokens[$0.key] != nil }
            .values
            .max()
        guard let highestActiveDeferredSequence else { return false }
        return pending <= highestActiveDeferredSequence
    }

    private func pruneCoveredMutationInvalidations() {
        deferredMutationInvalidationSequences = deferredMutationInvalidationSequences
            .filter { entry in
                actionOperationTokens[entry.key] != nil
                    && entry.value > lastCoveredEventSequence
            }
    }

    private func matchesMediaOperation(
        itemID: String,
        generation: UInt64,
        printerServiceIdentity: ObjectIdentifier
    ) -> Bool {
        isActive
            && mediaGeneration == generation
            && self.printerServiceIdentity == printerServiceIdentity
            && liveItem(id: itemID)?.kind == .failure
    }

    private func reconcileItemScopedState(with items: [AttentionItem]) {
        let liveIDs = Set(items.map(\.id))

        actionStates = actionStates.filter {
            liveIDs.contains($0.key)
                || actionOperationTokens[$0.key] != nil
        }

        mediaGeneration &+= 1
        mediaStates = mediaStates.reduce(into: [:]) { result, entry in
            guard liveIDs.contains(entry.key) else { return }
            result[entry.key] = entry.value == .loading ? .idle : entry.value
        }
    }

    private func invalidateMediaState(clearTerminalStates: Bool) {
        mediaGeneration &+= 1
        if clearTerminalStates {
            mediaStates = [:]
        } else {
            mediaStates = mediaStates.mapValues { state in
                state == .loading ? .idle : state
            }
        }
    }

    private func invalidateItemOperationsForDeactivation() {
        actionGeneration &+= 1
        actionOperationTokens = [:]
        actionStates = [:]
        deferredMutationInvalidationSequences.removeAll()
        invalidateMediaState(clearTerminalStates: false)
    }

    private func resetItemScopedState(clearTerminalMedia: Bool) {
        actionGeneration &+= 1
        actionOperationTokens = [:]
        actionStates = [:]
        deferredMutationInvalidationSequences.removeAll()
        invalidateMediaState(clearTerminalStates: clearTerminalMedia)
    }

    // MARK: - Authority helpers

    private func matchesActive(loadStamp stamped: UInt64, activation: UInt64) -> Bool {
        isActive
            && loadStamp == stamped
            && activationEpoch == activation
    }

    private func matchesAuthorityIgnoringActive(
        epoch: UInt64,
        serviceIdentity: ObjectIdentifier,
        signalRIdentity: ObjectIdentifier
    ) -> Bool {
        self.serviceIdentity == serviceIdentity
            && self.signalRIdentity == signalRIdentity
            && authorityEpoch == epoch
    }

    /// Advance and return the new load stamp. An older load that
    /// resolves after this bump captured a smaller stamp and drops.
    @discardableResult
    private func advanceLoadStamp() -> UInt64 {
        loadStamp &+= 1
        return loadStamp
    }

    private func invalidateAuthority(resetState: Bool) {
        resetItemScopedState(clearTerminalMedia: true)
        authorityEpoch &+= 1
        activationEpoch &+= 1
        lifecycleToken &+= 1
        loadStamp &+= 1
        loadMoreOwnerStamp = loadStamp
        attentionSubscription?.cancel()
        attentionSubscription = nil
        isRefreshing = false
        isLoadingMore = false
        pendingReloadOnActivate = false
        pendingReloadClaimedForActivation = nil
        // Coverage state is per-authority: any latched pending
        // coverage from the old authority is meaningless once the
        // service/signalR pair has been replaced.
        pendingCoverageEventSequence = nil
        lastCoveredEventSequence = 0
        activeRequestTokens = []

        guard resetState else { return }
        attentionService = nil
        signalRService = nil
        serviceIdentity = nil
        signalRIdentity = nil
        isActive = false
        snapshot = nil
        knownIDs = []
        groups = []
        loadFailure = nil
        paginationFailure = nil
        phase = .idle
    }
}

// MARK: - AttentionRecoveryOrchestrator

/// Testable, view-decoupled recovery orchestration used by
/// `AttentionView.performRecoveryRefresh`.
///
/// The orchestration is a pure sequence of owner-fence guards and
/// async awaits. Isolating it as a static function with closure
/// dependencies lets us prove the owner-fence properties (both
/// server-generation AND lifecycle-token) without a SwiftUI test
/// harness, and lets us regression-test the reachable stranding
/// scenarios Hicks flagged in cycles 6, 7, and 8.
///
/// Owner-fence semantics:
/// * `capturedServerGeneration`: bumped only on service swap.
///   Authority-crossing fence — survives the recovery's own
///   `configureVMWithNewGate` call because that call does not swap
///   services on the container.
/// * `capturedLifecycleToken`: bumped on deactivate AND on service
///   swap. Deactivate fence — checked before capability work AND
///   again immediately after the async capability await. NOT
///   re-checked past our own `configureVMWithNewGate` because a
///   gate change bumps the token as a side effect of our own action.
///
/// Same-owner recovery fires exactly one canonical GET via the
/// supplied `refresh` closure.
enum AttentionRecoveryOrchestrator {
    /// Perform a recovery cycle. See type doc for the fence semantics.
    ///
    /// The closure dependencies are:
    /// * `currentServerGeneration` / `currentLifecycleToken`: read
    ///   the current owner identities. Called after every await and
    ///   before every VM mutation.
    /// * `capabilityRefresh`: perform the async capability service
    ///   refresh. This is the awaitable that can suspend and allow
    ///   deactivate/reactivate to race with us.
    /// * `resolvedAttentionEnabled`: post-capability resolved gate.
    /// * `getAttentionEnabled` / `setAttentionEnabled`: read/write
    ///   the view's local mirror of the gate.
    /// * `configureVMWithNewGate`: called only when the gate flipped.
    /// * `resetDisabledLatch`: always called before the final refresh
    ///   to clear a latched featureDisabled state on the VM.
    /// * `refresh`: the canonical GET.
    @MainActor
    static func run(
        capturedServerGeneration: Int,
        capturedLifecycleToken: AttentionLifecycleToken,
        currentServerGeneration: @MainActor () -> Int,
        currentLifecycleToken: @MainActor () -> AttentionLifecycleToken,
        capabilityRefresh: () async -> Void,
        resolvedAttentionEnabled: @MainActor () -> Bool,
        getAttentionEnabled: @MainActor () -> Bool,
        setAttentionEnabled: @MainActor (Bool) -> Void,
        configureVMWithNewGate: @MainActor (Bool) -> Void,
        resetDisabledLatch: @MainActor (Bool) -> Void,
        refresh: () async -> Void
    ) async {
        // Pre-work owner check: bail before touching anything if the
        // authority or lifecycle has already moved.
        guard capturedServerGeneration == currentServerGeneration() else { return }
        guard capturedLifecycleToken == currentLifecycleToken() else { return }

        await capabilityRefresh()
        if Task.isCancelled { return }

        // Post-await owner re-check. BOTH identities re-validated:
        //   * server generation catches service swap during the
        //     capability await (blocker B from cycle 8),
        //   * lifecycle token catches deactivate during the capability
        //     await (cycle-8 delta blocker — this guard was dropped in
        //     cycle 8 with false reasoning; restored here).
        //
        // Task.isCancelled is an ADDITIONAL guard (SwiftUI cancels
        // the parent Task on view removal), NOT a substitute for
        // lifecycle-token equality — a swallowed cancellation or a
        // deactivate that does not cancel the task would still be
        // caught by the token re-check.
        guard capturedServerGeneration == currentServerGeneration() else { return }
        guard capturedLifecycleToken == currentLifecycleToken() else { return }

        let newEnabled = resolvedAttentionEnabled()
        let previouslyEnabled = getAttentionEnabled()
        setAttentionEnabled(newEnabled)

        // If the gate flipped (either direction) reconfigure so the
        // VM's own gate aligns with the fresh capability truth.
        // Lifecycle token deliberately NOT re-checked past this
        // point: our own `configureVMWithNewGate` bumps the token,
        // so a post-configure token check would false-positive on
        // our own action. The `capturedServerGeneration` guard
        // remains — it fences authority swaps only.
        if previouslyEnabled != newEnabled {
            guard capturedServerGeneration == currentServerGeneration() else { return }
            configureVMWithNewGate(newEnabled)
        }

        guard capturedServerGeneration == currentServerGeneration() else { return }
        // Independently clear any VM-side disabled latch — the gate may
        // still be true locally but a previous featureDisabled response
        // could have set the VM's internal flag. This is the fence that
        // makes the disabled-surface Refresh button recoverable in
        // place, without a tab round-trip.
        resetDisabledLatch(newEnabled)

        if newEnabled {
            guard capturedServerGeneration == currentServerGeneration() else { return }
            await refresh()
        }
    }
}
