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

// MARK: - View model

/// Feed shell + state model for F2-U1 (issue #779). Owns the generation-
/// authoritative fetch pipeline against `AttentionServiceProtocol` and
/// the lowercase `attentionchanged` invalidation subscription.
///
/// This view model deliberately stays read-only over the shipped
/// AttentionService: action execution, snoozes, media capture, and
/// destination navigation are all owned by F2-U2 (#780). Reconnect-gap
/// refresh is owned by F2-R (#781). Neither is implemented here.
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

    // MARK: Lifecycle plumbing

    @ObservationIgnored private let callbackEnqueuer: CallbackEnqueuer
    @ObservationIgnored private var attentionService: (any AttentionServiceProtocol)?
    @ObservationIgnored private var signalRService: (any SignalRServiceProtocol)?
    @ObservationIgnored private var serviceIdentity: ObjectIdentifier?
    @ObservationIgnored private var signalRIdentity: ObjectIdentifier?
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

    /// Deduplicated ID set for `loadMore` appends. Rebuilt atomically on
    /// canonical refresh so refresh resets are all-or-nothing.
    @ObservationIgnored private var knownIDs: Set<String> = []

    /// True when a refresh was requested while the view was inactive.
    /// Drains to exactly one canonical refetch when `activate` is
    /// called with a matching service, matching #779's "exactly one
    /// queued refresh drains on re-entry" contract.
    @ObservationIgnored private var pendingReloadOnActivate = false

    init() {
        self.callbackEnqueuer = { operation in
            Task { @MainActor in
                await operation()
            }
        }
    }

    #if DEBUG
    init(callbackEnqueuer: @escaping CallbackEnqueuer) {
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
                if pendingReloadOnActivate {
                    pendingReloadOnActivate = false
                    let enqueue = callbackEnqueuer
                    enqueue { [weak self] in
                        await self?.refresh()
                    }
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
        attentionSubscription = signalRService.onAttentionChanged { [weak self] _ in
            enqueue { [weak self] in
                guard let self,
                      self.matchesAuthorityIgnoringActive(
                        epoch: capturedEpoch,
                        serviceIdentity: newServiceIdentity,
                        signalRIdentity: newSignalRIdentity
                      ) else {
                    return
                }
                // #779: `attentionchanged` is a diagnostic invalidation
                // signal. It never carries item truth — we refetch the
                // canonical page. When the view is inactive we queue
                // exactly one refresh to drain on re-entry so events
                // received off-screen don't leave stale content.
                if self.isActive {
                    await self.refresh()
                } else {
                    self.pendingReloadOnActivate = true
                }
            }
        }
    }

    /// Called when the view leaves the screen. The next `configure` for
    /// a matching authority will drain a queued reload rather than
    /// starting from `.idle`.
    func deactivate() {
        // Preserve `snapshot` so re-entering the view keeps the last
        // rendered content until the queued refresh completes; but drop
        // any in-flight authority so completions can't mutate state.
        isActive = false
        // Bumping the activation epoch fences off any in-flight refresh
        // or loadMore whose outcome would otherwise land into a fresh
        // reactivation. See `matchesActive`.
        activationEpoch &+= 1
        // Any load finishing while inactive will notice the mismatched
        // active state and clear loading without applying its result.
        // See applySnapshot / applyFailure / applyDisabled below.
    }

    /// Called when the view re-appears. If a `pendingReloadOnActivate`
    /// flag was set (typically by a signalR event that arrived while the
    /// tab was in the background), exactly one canonical refresh drains.
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
        pendingReloadOnActivate = false
        let enqueue = callbackEnqueuer
        enqueue { [weak self] in
            await self?.refresh()
        }
    }

    /// Bind + activate + issue exactly one canonical refresh in a way
    /// that coalesces any pending drain. Called from the view's
    /// `.task(id:)` so re-entry produces exactly one canonical GET,
    /// regardless of whether an invalidation arrived while off-screen.
    ///
    /// Contract:
    /// * Bootstrap consumes any queued drain flag before configuring
    ///   so `configure`'s re-entry path does not enqueue a duplicate
    ///   fetch through the callback path.
    /// * Bootstrap then awaits a single `refresh()` — that is the one
    ///   canonical GET per bootstrap invocation.
    ///
    /// The service, signalR, and gate arguments must match the shell's
    /// current authority; a mismatch triggers a full reconfigure inside
    /// `configure()`.
    @discardableResult
    func bootstrap(
        attentionService: any AttentionServiceProtocol,
        signalRService: any SignalRServiceProtocol,
        attentionEnabled: Bool
    ) async -> Bool {
        // Consume any queued drain before configure so its re-entry
        // path does not enqueue a duplicate refresh through the
        // callback enqueuer. Bootstrap owns the fetch below.
        pendingReloadOnActivate = false
        configure(
            attentionService: attentionService,
            signalRService: signalRService,
            attentionEnabled: attentionEnabled
        )
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

        if snapshot == nil {
            phase = .loading
        } else {
            isRefreshing = true
        }

        let outcome = await Self.fetchFirstPage(service: service)

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
            return false
        }

        switch outcome {
        case .success(let feed):
            applySnapshot(feed, mode: .refresh)
            return true
        case .featureDisabled:
            applyDisabled()
            return false
        case .failure(let error):
            applyFailure(error)
            return false
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
        case .failure:
            // Deliberately non-fatal: keep the currently displayed page.
            // The shell can offer another loadMore attempt on the same
            // sentinel; a canonical refresh will surface the error path.
            return false
        }
    }

    // MARK: - Reads consumed by the view

    /// Convenience the shell checks to decide whether to attempt a
    /// paginated load. Bounded — returning false is the "stop" signal.
    var canLoadMore: Bool {
        guard isActive, attentionEnabled else { return false }
        guard let cursor = snapshot?.nextCursor, !cursor.isEmpty else {
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
        _ = mode // reserved for future modes; only .refresh today
        snapshot = normalized
        knownIDs = seen
        groups = Self.groupBySeverity(ordered)
        loadFailure = nil
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
        attentionEnabled = false
        snapshot = nil
        groups = []
        knownIDs = []
        loadFailure = nil
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
        authorityEpoch &+= 1
        activationEpoch &+= 1
        loadStamp &+= 1
        loadMoreOwnerStamp = loadStamp
        attentionSubscription?.cancel()
        attentionSubscription = nil
        isRefreshing = false
        isLoadingMore = false
        pendingReloadOnActivate = false

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
        phase = .idle
    }
}
