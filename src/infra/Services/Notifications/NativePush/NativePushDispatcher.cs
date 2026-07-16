using System.Collections.Concurrent;
using System.Globalization;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain.Notifications;
using Farm.Infrastructure.Dtos.Attention;
using Farm.Infrastructure.Repositories.Notifications;
using Farm.Infrastructure.Services.Attention;
using Farm.Infrastructure.Services.OperatorFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Farm.Infrastructure.Services.Notifications.NativePush;

/// <summary>
/// Native push delivery service. Fans out an attention change into per-user, per-device
/// envelopes and hands them to <see cref="INativePushSender"/>. Owns the double gate,
/// per-user category opt-out, dedupe LRU, and per-user rate limit. See
/// <c>docs/OPERATOR_NATIVE_PUSH.md</c>.
/// </summary>
public sealed class NativePushDispatcher : INativePushDispatcher, IDisposable
{
    // Dedupe / rate-limit state is process-local. Native push tolerates duplicates in
    // multi-node deployments better than false-negative suppression, so we keep the LRU
    // simple and un-distributed.
    private readonly ConcurrentDictionary<string, DateTime> _dedupe = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<RateLimitKey, RateLimitBucket> _rateLimits = new();

    // One lifecycle per recipient and attention item orders snapshot ownership across
    // targeted and global audience lanes without serializing unrelated recipients.
    private readonly ConcurrentDictionary<AttentionSnapshotKey, AttentionLifecycle> _attentionLifecycles = new();

    // One versioned lane per item and delivery audience serializes an active lifecycle
    // transition, coalesces queued transitions to the newest authoritative timestamp,
    // and retains a tombstone so delayed Created work cannot follow Resolved.
    private readonly ConcurrentDictionary<AttentionDispatchKey, AttentionDispatchLane> _attentionDispatchLanes = new();

    // Per-attention-item linearization point (see AttentionItemFence) between
    // a global Resolved's tombstone publication + owner enumeration and a
    // targeted dispatch's per-recipient lifecycle install. Both operations
    // acquire the SAME fence's lock, so whichever runs first is authoritative
    // for the other: a targeted install that wins is visible to the
    // resolution's owner enumeration (which runs after the install, under the
    // same lock); a resolution that wins publishes a version a subsequent
    // targeted admission re-checks atomically with its own lifecycle install.
    // This closes the P-A-D-R-S gap where a temporarily tokenless recipient's
    // lifecycle does not exist yet when a concurrent global resolution
    // enumerates owners, so a stale targeted dispatch that read the fence as
    // unpublished — then paused before installing its lifecycle — could
    // otherwise resume after a re-registration and deliver past the
    // resolution. Item-scoped only: authorization is unchanged, and
    // per-recipient lifecycles still manage their own version ordering for
    // non-stale work; unrelated attention items and recipients are never
    // serialised against each other.
    private readonly ConcurrentDictionary<string, AttentionItemFence> _attentionItemFences
        = new(StringComparer.Ordinal);

    private static readonly TimeSpan AttentionSnapshotTtl = TimeSpan.FromDays(7);
    private static readonly TimeSpan InformationalAlertTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ActionableAlertTtl = TimeSpan.FromMinutes(30);

    private long _lastPruneAtTicks;
    private long _lastValidRateLimitWindowTicks;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly INativePushTransportSender _sender;
    private readonly IOptionsMonitor<NativePushSettings> _optionsMonitor;
    private readonly NativePushMetrics _metrics;
    private readonly ILogger<NativePushDispatcher> _logger;
    private readonly TimeProvider _timeProvider;

    // Internal deterministic test seam. Production never assigns this; it
    // signals only after a resolution has captured a non-empty settlement set
    // and immediately before it awaits that set outside lifecycle locks.
    internal Action? OnResolutionSettlementWaitStartedForTests { get; set; }

    // -----------------------------------------------------------------------
    // Internal cache-count test seams (Hicks r2 blocker 1 leak-proofing).
    //
    // These accessors are visible to Farm.Web.Api.Tests only (via
    // InternalsVisibleTo in AssemblyInfo.TestsVisible.cs). They exist so
    // deterministic tests can prove that:
    //   * disabled-mode Created/Updated traffic creates NO dispatch state, and
    //   * disabled-mode Resolved traffic drives bounded retirement past the
    //     retention TTL rather than accumulating unbounded.
    // No production DI touches these members; they read the raw concurrent
    // dictionary Count with no locking to avoid any observable side effect.
    // -----------------------------------------------------------------------

    /// <summary>Test-only: live count of AttentionDispatchLane entries.</summary>
    internal int AttentionDispatchLaneCountForTests => _attentionDispatchLanes.Count;

    /// <summary>Test-only: live count of AttentionItemFence entries.</summary>
    internal int AttentionItemFenceCountForTests => _attentionItemFences.Count;

    /// <summary>Test-only: live count of AttentionLifecycle entries.</summary>
    internal int AttentionLifecycleCountForTests => _attentionLifecycles.Count;

    /// <summary>
    /// Test-only: drives <see cref="PruneCaches"/> with the current settings.
    /// The internal 30-second rate limit is respected; tests advance the
    /// injected <see cref="TimeProvider"/> to observe reclamation.
    /// </summary>
    internal void PruneCachesForTests()
    {
        NativePushSettings settings = _optionsMonitor.CurrentValue;
        RememberRateLimitWindow(settings);
        PruneCaches(UtcNow, EffectiveRateLimitWindow(settings));
    }

    /// <summary>Constructs the dispatcher.</summary>
    public NativePushDispatcher(
        IServiceScopeFactory scopeFactory,
        INativePushSender sender,
        IOptionsMonitor<NativePushSettings> optionsMonitor,
        NativePushMetrics metrics,
        ILogger<NativePushDispatcher> logger,
        TimeProvider? timeProvider = null)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _sender = sender as INativePushTransportSender
            ?? throw new ArgumentException(
                "Native-push dispatch requires a transport-aware sender.",
                nameof(sender));
        _optionsMonitor = optionsMonitor ?? throw new ArgumentNullException(nameof(optionsMonitor));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    private DateTime UtcNow => _timeProvider.GetUtcNow().UtcDateTime;

    /// <inheritdoc />
    public async Task DispatchAsync(
        string attentionItemId,
        AttentionChangeKind changeKind,
        Guid? targetUserId,
        DateTime? occurredAtUtc = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(attentionItemId))
        {
            return;
        }

        var dispatchKey = new AttentionDispatchKey(attentionItemId, targetUserId);
        var version = new AttentionDispatchVersion(
            NormalizeOccurredAt(occurredAtUtc ?? UtcNow),
            LifecycleOrder(changeKind));

        // Fast-path optimisation only — NOT the authoritative fence. A
        // torn/stale read of the item-wide resolved tombstone here can only
        // ever under-reject: the tombstone is monotonically non-decreasing,
        // so if this peek already justifies rejecting, the authoritative
        // check below (inside TryObserveLifecycle for targeted dispatches, or
        // PublishResolvedTombstoneAndFenceLifecycles for global Resolved)
        // would independently reject it too. This just avoids opening a DI
        // scope and querying the database for a dispatch that is definitely
        // stale.
        //
        // A one-time check here — as the sole fence — is NOT sufficient for
        // correctness: it goes stale the instant a concurrent global Resolved
        // publishes after this line but before this dispatch's per-owner
        // lifecycle is installed (the P-A-D-R-S interleaving). A temporarily
        // tokenless recipient with no lifecycle installed yet would not be
        // found by the resolution's owner enumeration, so a stale targeted
        // dispatch that already passed this check could resume later and
        // install a lifecycle unaware of the resolution. The authoritative
        // fence therefore lives at AttentionItemFence, which links the
        // per-user lifecycle install to the SAME lock a global resolution's
        // tombstone publish + owner enumeration uses, so the two can never
        // cross this gap. See AttentionItemFence for the full invariant.
        if (_attentionItemFences.TryGetValue(attentionItemId, out AttentionItemFence? fastPathFence)
            && fastPathFence.PeekResolvedVersion() is AttentionDispatchVersion peekedResolvedVersion
            && version.CompareTo(peekedResolvedVersion) < 0)
        {
            return;
        }

        // Hicks r2 blocker 1 (A): resolution fencing MUST run synchronously
        // before every fallible operation, so a Resolved change kind
        // deliberately skips the entry options snapshot. If
        // <see cref="IOptionsMonitor{T}.CurrentValue"/> throws during a
        // validated configuration reload here, the exception must not prevent
        // a Resolved from reaching the fence — otherwise an older prepared
        // targeted send (S1) that already passed transport-start preparation
        // could still resume and deliver unfenced after the reload recovers.
        // For non-Resolved change kinds the disabled fast-path DOES require
        // the current settings snapshot, and it is read BEFORE any persistent
        // state (lane, fence, lifecycle) is allocated: a throw here propagates
        // without leaking any dispatcher-owned state because
        // <see cref="TryObserveDispatch"/> runs strictly after this early
        // return. See the DispatchCoreAsync boundary for the resolution-time
        // fencing invariant.
        //
        // Hicks r2 blocker 1: a disabled-mode Created/Updated dispatch must
        // NOT observe a dispatch lane. The prior implementation always ran
        // TryObserveDispatch first and then early-returned at
        // DispatchCoreAsync's mode check, so every unique attention item
        // permanently created a fresh AttentionDispatchLane in the process
        // cache (retired only after the seven-day retention TTL). Because
        // Disabled is the default operator mode, unique attention items
        // accumulated lanes without bound. Skipping TryObserveDispatch
        // here is safe: no dispatcher-owned state (lane, lifecycle, or
        // item fence) exists that a non-Resolved disabled dispatch could
        // need to serialise against, and any subsequent Resolved on the
        // same item still creates or advances the required fence.
        //
        // Resolved changes (both global and targeted) fall through so this
        // method can publish their pre-exit fences before waiting on the lane,
        // even in disabled mode. DispatchCoreAsync then owns optional delivery
        // and bounded pruning; both finally blocks idempotently release the
        // fence participant leases on every exit.
        if (changeKind != AttentionChangeKind.Resolved)
        {
            NativePushSettings entrySettings = _optionsMonitor.CurrentValue;
            RememberRateLimitWindow(entrySettings);
            if (entrySettings.Mode == NativePushMode.Disabled)
            {
                return;
            }
        }

        if (!TryObserveDispatch(dispatchKey, version, out AttentionDispatchLane lane))
        {
            return;
        }

        GlobalResolvedFence? globalResolvedFence = null;
        GlobalResolvedFence? targetedResolvedFence = null;
        bool entered = false;
        try
        {
            // Publish resolution ownership before waiting for this audience lane.
            // An older same-lane Created may already be preparing a transport
            // while holding the lane. Publishing here advances its lifecycle
            // immediately, so its transport-boundary revalidation vetoes the
            // stale attempt instead of allowing lane ordering to hide the
            // resolution until after transport starts.
            globalResolvedFence = changeKind == AttentionChangeKind.Resolved
                && targetUserId is null
                ? PublishResolvedTombstoneAndFenceLifecycles(attentionItemId, version)
                : null;
            targetedResolvedFence = changeKind == AttentionChangeKind.Resolved
                && targetUserId is Guid targetForFence
                ? PublishTargetedResolvedFence(attentionItemId, targetForFence, version)
                : null;

            await lane.Gate.WaitAsync(cancellationToken);
            entered = true;
            if (!lane.IsLatest(version))
            {
                return;
            }

            // The lane protects only the latest-version decision. All feature
            // gate, database, sender preparation, and provider awaits run after
            // releasing it. Per-recipient lifecycle reservations perform the
            // authoritative atomic version check again at transport start.
            lane.Gate.Release();
            entered = false;

            await DispatchCoreAsync(
                attentionItemId,
                changeKind,
                targetUserId,
                version,
                globalResolvedFence,
                targetedResolvedFence,
                cancellationToken);
        }
        finally
        {
            if (entered)
            {
                lane.Gate.Release();
            }

            // DispatchCoreAsync normally releases these before pruning. These
            // idempotent completions cover cancellation while waiting for the
            // lane and any other pre-core exit.
            globalResolvedFence?.Complete();
            targetedResolvedFence?.Complete();
            lane.Complete(version);
        }
    }

    private async Task DispatchCoreAsync(
        string attentionItemId,
        AttentionChangeKind changeKind,
        Guid? targetUserId,
        AttentionDispatchVersion version,
        GlobalResolvedFence? globalResolvedFence,
        GlobalResolvedFence? targetedResolvedFence,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(attentionItemId))
        {
            return;
        }

        // Hicks r2 blocker 1 (B): every fallible operation past this point —
        // <see cref="IOptionsMonitor{T}.CurrentValue"/>, gate reads, scope
        // creation, DB queries, and delivery — MUST run inside the top-level
        // try/finally that owns fence + lifecycle lease release. A throw from
        // the validated-options reload here would otherwise leak the version-
        // specific fence participant leases acquired above (S1 exact-version
        // retry would remain stale) and skip the bounded PruneCaches that
        // retires past-retention state on the disabled/no-op path. Capturing
        // <c>settingsSnapshot</c> in an outer local lets the finally still
        // drive bounded reclamation even when the read itself failed.
        NativePushSettings? settingsSnapshot = null;
        try
        {
            // Snapshot the startup-bound settings for a consistent fan-out. The
            // NativePush section is validated with ValidateOnStart; configuration
            // changes require a process restart rather than taking effect mid-flight.
            // Resolved events consume a per-user pre-resolution snapshot and emit a
            // silent dismissal even after the source removes the live item.
            NativePushSettings settings = _optionsMonitor.CurrentValue;
            settingsSnapshot = settings;
            RememberRateLimitWindow(settings);
            if (settings.Mode == NativePushMode.Disabled)
            {
                return;
            }

            PruneCaches(UtcNow, EffectiveRateLimitWindow(settings));

            await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
            IServiceProvider sp = scope.ServiceProvider;
            IOperatorFeatureGate gate = sp.GetRequiredService<IOperatorFeatureGate>();
            if (!await gate.IsEnabledAsync(OperatorFeature.NativePush, cancellationToken).ConfigureAwait(false))
            {
                _metrics.SkippedFeatureDisabled.Add(1);
                return;
            }

            IDeviceTokenRepository tokens = sp.GetRequiredService<IDeviceTokenRepository>();
            IAttentionService attention = sp.GetRequiredService<IAttentionService>();
            AppDbContext db = sp.GetRequiredService<AppDbContext>();

            IReadOnlyList<Guid> owners;
            if (targetUserId is Guid explicitUser)
            {
                owners = new[] { explicitUser };
            }
            else
            {
                IReadOnlyList<Guid> activeOwners = await tokens.GetActiveTokenOwnersAsync(cancellationToken);
                if (globalResolvedFence is not null)
                {
                    // The global fence has already advanced every in-memory
                    // lifecycle synchronously. Preserve the existing active-owner
                    // lookup as a delivery dependency, then union its result with
                    // the captured lifecycle owners. Active-only owners retain
                    // the established no-snapshot lookup path; only captured
                    // owners can ultimately emit a dismissal.
                    var union = new HashSet<Guid>(activeOwners);
                    foreach (GlobalResolvedParticipant participant in globalResolvedFence.Participants)
                    {
                        _ = union.Add(participant.UserId);
                    }

                    owners = union.ToArray();
                }
                else
                {
                    owners = activeOwners;
                }
            }

            if (owners.Count == 0)
            {
                return;
            }

            // Global and targeted Resolved share the same preObservedResolution
            // plumbing: each participant carries the exact lifecycle and
            // captured lineage the pre-exit fence installed, so
            // DispatchForOwnerAsync consumes them uniformly. A resolution
            // scoped to a single user only enrolls that user's participant;
            // a global one enrolls every previously-tracked lifecycle. At
            // most one fence is non-null per DispatchCoreAsync call.
            GlobalResolvedFence? preObservedResolutionFence =
                globalResolvedFence ?? targetedResolvedFence;

            foreach (Guid userId in owners)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Re-check the gate on every recipient so a mid-flight flip stops sends.
                if (!await gate.IsEnabledAsync(OperatorFeature.NativePush, cancellationToken).ConfigureAwait(false))
                {
                    _metrics.SkippedFeatureDisabled.Add(1);
                    return;
                }

                GlobalResolvedParticipant? preObservedResolution =
                    preObservedResolutionFence?.Take(userId);

                // Vasquez v6 B1: isolate the entire per-owner resolution +
                // fan-out under a scope that never swallows cancellation but
                // continues to the next owner on any other exception. Without
                // this a transient DB read failure for one owner (attention
                // lookup, preferences read, token list) would abort every
                // remaining owner in the current dispatch.
                //
                // Cancellation is control flow and is never isolated as an
                // owner failure. Named-client HttpClient timeouts have already
                // been converted by the sender into typed transient results;
                // any OCE that reaches this boundary must stop fan-out.
                try
                {
                    await DispatchForOwnerAsync(
                        userId,
                        attentionItemId,
                        changeKind,
                        version,
                        settings,
                        gate,
                        tokens,
                        attention,
                        db,
                        preObservedResolution,
                        cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _metrics.IsolatedOwnerFailure.Add(1);
                    _logger.LogWarning(
                        ex,
                        "[NativePush] Isolated per-owner failure for userId={UserId} attentionItemId={AttentionItemId}; continuing with remaining owners.",
                        userId,
                        attentionItemId);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Caller/shutdown and unexpected linked cancellation propagate
            // out of DispatchAsync. HttpClient.Timeout is represented as a
            // transient result by the concrete sender and never reaches here.
            throw;
        }
        catch (Exception ex)
        {
            // Delivery failures must never break the attention broadcast path.
            // A validated-options reload that throws lands here (blocker 1 B):
            // the fence + lifecycle leases still release below and PruneCaches
            // still drives bounded reclamation without inventing rate settings.
            _logger.LogWarning(ex, "[NativePush] Dispatch failed for attentionItemId={AttentionItemId}", attentionItemId);
        }
        finally
        {
            // Release the version-specific participant leases FIRST so
            // PruneCaches below can reclaim any past-retention entry that
            // was still holding a lease before this dispatch. Complete()
            // is exactly-once and idempotent: a Take'd participant that
            // DispatchForOwnerAsync already Completed becomes a no-op.
            globalResolvedFence?.Complete();
            targetedResolvedFence?.Complete();

            // Hicks r2 blocker 1: bounded prune must actually run on every
            // relevant disabled/no-op early return. PruneCaches inside the
            // try (above) only runs when Mode != Disabled, so a disabled
            // Resolved would otherwise leave its own fence + lane in the
            // cache and never drive retirement of prior past-retention
            // entries. Running it here (after lease release) makes disabled
            // Resolved traffic drive the same bounded reclamation as normal
            // traffic; the internal 30-second rate limit prevents redundant
            // sweeps in bursty conditions.
            //
            // Settings-independent caches still need bounded reclamation after
            // an options failure, but rate buckets must never be pruned using a
            // fabricated shorter window. Use the most recent successfully-read
            // window; when none exists, skip rate-bucket pruning for this pass.
            if (settingsSnapshot is null || settingsSnapshot.Mode == NativePushMode.Disabled)
            {
                TimeSpan? rateLimitWindow = settingsSnapshot is not null
                    ? EffectiveRateLimitWindow(settingsSnapshot)
                    : TryGetLastValidRateLimitWindow();
                PruneCaches(UtcNow, rateLimitWindow);
            }
        }
    }

    private async Task DispatchForOwnerAsync(
        Guid userId,
        string attentionItemId,
        AttentionChangeKind changeKind,
        AttentionDispatchVersion version,
        NativePushSettings settings,
        IOperatorFeatureGate gate,
        IDeviceTokenRepository tokens,
        IAttentionService attention,
        AppDbContext db,
        GlobalResolvedParticipant? preObservedResolution,
        CancellationToken cancellationToken)
    {
        AttentionLifecycle lifecycle;
        ResolutionCapture? resolutionCapture;
        if (preObservedResolution is null)
        {
            var snapshotKey = new AttentionSnapshotKey(userId, attentionItemId);
            if (!TryObserveLifecycle(
                    snapshotKey,
                    version,
                    changeKind,
                    out lifecycle,
                    out resolutionCapture))
            {
                return;
            }
        }
        else
        {
            lifecycle = preObservedResolution.Lifecycle;
            resolutionCapture = preObservedResolution.Capture;
        }

        try
        {
            AttentionItemDto? item = null;
            AttentionSnapshot? activeSnapshot = null;

            if (changeKind == AttentionChangeKind.Resolved)
            {
                // Resolution captures and fences the exact alert generation
                // under the lifecycle lock, then waits outside every lock and
                // feature gate for already-started provider calls to settle.
                // A success that lands after this resolution begins therefore
                // remains attributed to the consumed snapshot and can still
                // trigger its required dismissal.
                if (resolutionCapture is not null)
                {
                    await resolutionCapture.WaitForPendingTransportsAsync(
                        OnResolutionSettlementWaitStartedForTests,
                        cancellationToken);
                }

                // Preserve the established resolution lookup boundary after
                // settlement. The live item is intentionally not used to
                // construct a dismissal, but the lookup maintains source
                // sequencing and authorization behavior for no-snapshot
                // resolutions.
                _ = await attention.FindItemAsync(
                    userId,
                    attentionItemId,
                    cancellationToken);
                if (resolutionCapture is null)
                {
                    return;
                }

                if (!resolutionCapture.HasSuccessfulDelivery)
                {
                    _metrics.SkippedNeverDelivered.Add(1);
                    return;
                }

                item = null;
            }
            else
            {
                item = await attention.FindItemAsync(
                    userId,
                    attentionItemId,
                    cancellationToken);
                if (item is null)
                {
                    return;
                }
            }

            AttentionKind kind = item?.Kind ?? resolutionCapture!.Snapshot.Kind;
            Guid printerId = item?.PrinterId ?? resolutionCapture!.Snapshot.PrinterId;
            if (AttentionPushCategories.CategoryFor(kind) is null)
            {
                return;
            }

            // Maintenance is admin-only. Re-check current role even for a snapshot
            // so revocation before resolution suppresses the dismissal.
            if (kind == AttentionKind.Maintenance
                && !await IsFarmAdminAsync(db, userId, cancellationToken))
            {
                return;
            }

            NotificationPreferences? prefs = await db.NotificationPreferences
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.UserId == userId, cancellationToken);
            AttentionPushCategoryPreferences catPrefs = AttentionPushCategoryPreferences.FromJson(
                prefs?.AttentionPushCategoryPreferencesJson);
            if (!catPrefs.IsEnabled(kind)
                || (prefs is not null && !prefs.EnablePushNotifications)
                || !IsPushEnabledForKind(prefs, kind))
            {
                _metrics.SkippedCategoryOptOut.Add(1);
                return;
            }

            IReadOnlyList<DeviceToken> userTokens = await tokens.GetActiveByUserAsync(
                userId,
                cancellationToken);
            if (userTokens.Count == 0)
            {
                return;
            }

            string dedupeKey = BuildDedupeKey(userId, attentionItemId, changeKind);
            RateLimitKey? rateKey = null;
            if (changeKind != AttentionChangeKind.Resolved)
            {
                rateKey = new RateLimitKey(userId, printerId, kind);
                activeSnapshot = new AttentionSnapshot(
                    kind: item!.Kind,
                    printerId: item.PrinterId,
                    jobId: item.JobId,
                    toolheadIndex: item.ToolheadIndex,
                    capturedAtUtc: UtcNow);
            }

            foreach (DeviceToken deviceToken in userTokens)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!await gate.IsEnabledAsync(OperatorFeature.NativePush, cancellationToken).ConfigureAwait(false))
                {
                    _metrics.SkippedFeatureDisabled.Add(1);
                    return;
                }

                try
                {
                    DeviceDispatchOutcome outcome = await SendAndApplyForDeviceAsync(
                        userId,
                        attentionItemId,
                        changeKind,
                        item,
                        resolutionCapture?.Snapshot,
                        lifecycle,
                        version,
                        activeSnapshot,
                        dedupeKey,
                        rateKey,
                        deviceToken,
                        settings,
                        gate,
                        cancellationToken);
                    if (outcome == DeviceDispatchOutcome.DispatchStopped)
                    {
                        return;
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _metrics.IsolatedDeviceFailure.Add(
                        1,
                        new KeyValuePair<string, object?>("stage", "device"));
                    _logger.LogWarning(
                        ex,
                        "[NativePush] Isolated per-device failure for deviceTokenId={DeviceTokenId} userId={UserId} attentionItemId={AttentionItemId}; continuing with remaining devices.",
                        deviceToken.Id,
                        userId,
                        attentionItemId);
                }
            }
        }
        finally
        {
            if (preObservedResolution is not null)
            {
                preObservedResolution.Complete();
            }
            else
            {
                lifecycle.Complete(version);
            }
        }
    }

    private async Task<DeviceDispatchOutcome> SendAndApplyForDeviceAsync(
        Guid userId,
        string attentionItemId,
        AttentionChangeKind changeKind,
        AttentionItemDto? item,
        AttentionSnapshot? resolvedSnapshot,
        AttentionLifecycle lifecycle,
        AttentionDispatchVersion version,
        AttentionSnapshot? activeSnapshot,
        string dedupeKey,
        RateLimitKey? rateKey,
        DeviceToken deviceToken,
        NativePushSettings settings,
        IOperatorFeatureGate gate,
        CancellationToken cancellationToken)
    {
        NativePushEnvelope envelope = item is not null
            ? BuildEnvelope(item, changeKind, deviceToken)
            : BuildSilentEnvelopeFromSnapshot(attentionItemId, resolvedSnapshot!, deviceToken);
        AttentionSnapshot lifecycleSnapshot = activeSnapshot
            ?? resolvedSnapshot
            ?? throw new InvalidOperationException("A lifecycle snapshot is required before native-push transport.");
        NativePushSendOutcome sendOutcome;
        NativePushDispatchResult? result;
        try
        {
            sendOutcome = await SendWithRetriesAsync(
                userId,
                envelope,
                lifecycle,
                version,
                lifecycleSnapshot,
                changeKind,
                dedupeKey,
                rateKey,
                settings,
                gate,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Caller/shutdown or unexpected linked cancellation stops the
            // pipeline. Concrete senders return HttpClient.Timeout as a typed
            // transient result, so it remains retryable and isolated.
            throw;
        }
        catch (Exception ex)
        {
            // A strictly-newer lifecycle observation supersedes this fan-out
            // entirely — stop the whole owner's dispatch so the newer
            // generation owns the send order. Only actual supersession
            // (a later version has taken over) qualifies here.
            if (lifecycle.IsSupersededBy(version))
            {
                return DeviceDispatchOutcome.DispatchStopped;
            }

            // A sender that faults before it signals the typed transport
            // boundary has not committed lifecycle ownership, dedupe, or rate
            // capacity. Its exact-version retry remains valid and siblings
            // continue independently.
            if (!lifecycle.IsCurrent(
                    version,
                    lifecycleSnapshot,
                    changeKind == AttentionChangeKind.Resolved))
            {
                _metrics.IsolatedDeviceFailure.Add(
                    1,
                    new KeyValuePair<string, object?>("stage", "pre_transport"));
                _logger.LogWarning(
                    ex,
                    "[NativePush] Isolated pre-transport sender failure for deviceTokenId={DeviceTokenId} userId={UserId} attentionItemId={AttentionItemId}; sibling devices continue and this device remains eligible for exact-version retry.",
                    deviceToken.Id,
                    userId,
                    attentionItemId);
                return DeviceDispatchOutcome.Completed;
            }

            _logger.LogWarning(ex, "[NativePush] Sender threw for deviceTokenId={DeviceTokenId}.", deviceToken.Id);
            sendOutcome = new NativePushSendOutcome(
                NativePushDispatchResult.Transient("sender_exception"),
                true);
        }

        result = sendOutcome.Result;
        if (result is null)
        {
            return sendOutcome.TransportStarted
                ? DeviceDispatchOutcome.DispatchStopped
                : DeviceDispatchOutcome.Completed;
        }

        // Every result opens its own DI scope/AppDbContext. Besides isolating a
        // failed token write from later devices, this gives the final result
        // boundary a fresh persisted kill-switch read immediately before any
        // delivery/failure attribution or registration mutation.
        try
        {
            return await ApplyResultAsync(
                lifecycle,
                version,
                lifecycleSnapshot,
                changeKind == AttentionChangeKind.Resolved,
                deviceToken,
                result,
                settings,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _metrics.IsolatedDeviceFailure.Add(
                1,
                new KeyValuePair<string, object?>("stage", "persist"));
            _logger.LogWarning(
                ex,
                "[NativePush] Failed to persist send result for deviceTokenId={DeviceTokenId} userId={UserId} attentionItemId={AttentionItemId}; continuing.",
                deviceToken.Id,
                userId,
                attentionItemId);
            return DeviceDispatchOutcome.Completed;
        }
    }

    private bool TryObserveDispatch(
        AttentionDispatchKey key,
        AttentionDispatchVersion version,
        out AttentionDispatchLane lane)
    {
        while (true)
        {
            lane = _attentionDispatchLanes.GetOrAdd(key, static _ => new AttentionDispatchLane());
            AttentionDispatchObserveResult result = lane.TryObserve(version, UtcNow);
            if (result == AttentionDispatchObserveResult.Accepted)
            {
                return true;
            }

            if (result == AttentionDispatchObserveResult.Stale)
            {
                return false;
            }

            // A pruner retired this lane after GetOrAdd returned it. Retry against
            // the replacement lane instead of executing against an orphaned tombstone.
        }
    }

    private bool TryObserveLifecycle(
        AttentionSnapshotKey key,
        AttentionDispatchVersion version,
        AttentionChangeKind changeKind,
        out AttentionLifecycle lifecycle,
        out ResolutionCapture? resolutionCapture)
    {
        // Any strictly-newer lifecycle observation (Created / Updated /
        // Resolved) must clear the prior generation's versionless
        // (user, item, kind) dedupe reservations atomically with the
        // version bump. A rate-limited or no-transport v_prev otherwise
        // retains a same-kind entry that would suppress a legitimate
        // strictly-newer v_next before its own reservation runs — the
        // deterministic Kane cycle-3 A/B interleavings.
        //
        // The reset is invoked under the lifecycle sync lock via
        // TryObserve, so it is atomic with the version bump and with any
        // concurrent Resolved observation's onResolvedObserved-derived
        // reset. Running it here (rather than lazily inside shouldEmit)
        // preserves the existing invariant that same-version repeats
        // are still de-duplicated.
        Action onLifecycleAdvancedOrResolved = () => ResetActiveLifecycleDedupe(
            key.UserId,
            key.AttentionItemId);

        LifecycleObservation observation = default;
        while (true)
        {
            AttentionItemFence fence = _attentionItemFences.GetOrAdd(
                key.AttentionItemId,
                static _ => new AttentionItemFence());

            // The item-wide resolved-tombstone check and the per-user
            // lifecycle GetOrAdd/TryObserve run as ONE atomic unit under the
            // fence's lock. This is the P-A-D-R-S linearization point: a
            // concurrent global resolution's tombstone publication + lifecycle
            // fencing (PublishResolvedTombstoneAndFenceLifecycles) uses
            // the SAME fence and lock, so the two operations can never cross
            // this gap for a given attention item. See AttentionItemFence.
            AttentionItemFenceResult fenceResult = fence.TryAdmitTargeted(
                version,
                UtcNow,
                () =>
                {
                    observation = ObserveLifecycleUnderFence(
                        key,
                        version,
                        changeKind,
                        onLifecycleAdvancedOrResolved);
                    if (observation.Lifecycle is AttentionLifecycle observedLifecycle)
                    {
                        fence.TrackLifecycle(key.UserId, observedLifecycle);
                    }
                });

            if (fenceResult == AttentionItemFenceResult.Retired)
            {
                // A pruner retired this fence after GetOrAdd returned it. Retry
                // against the replacement fence instead of executing against an
                // orphaned tombstone.
                continue;
            }

            break;
        }

        lifecycle = observation.Lifecycle!;
        resolutionCapture = observation.ResolutionCapture;
        return observation.Accepted;
    }

    /// <summary>
    /// Installs/advances the per-(recipient, item) lifecycle. Always invoked
    /// from inside <see cref="AttentionItemFence.TryAdmitTargeted"/>'s lock,
    /// so this never races a concurrent global resolution's synchronous
    /// lifecycle fencing for the same attention item.
    /// </summary>
    private LifecycleObservation ObserveLifecycleUnderFence(
        AttentionSnapshotKey key,
        AttentionDispatchVersion version,
        AttentionChangeKind changeKind,
        Action onLifecycleAdvancedOrResolved)
    {
        while (true)
        {
            AttentionLifecycle candidate = _attentionLifecycles.GetOrAdd(
                key,
                static _ => new AttentionLifecycle());
            AttentionLifecycleObserveResult result = candidate.TryObserve(
                version,
                changeKind,
                UtcNow,
                onLifecycleAdvancedOrResolved,
                out ResolutionCapture? resolution);
            if (result == AttentionLifecycleObserveResult.Accepted)
            {
                return new LifecycleObservation(true, candidate, resolution);
            }

            if (result == AttentionLifecycleObserveResult.Stale)
            {
                return new LifecycleObservation(false, null, null);
            }

            // A pruner retired this lifecycle after GetOrAdd returned it. Retry
            // against the replacement rather than updating orphaned state.
        }
    }

    /// <summary>
    /// Publishes the global resolved tombstone and advances every lifecycle already
    /// installed for this item while the same item fence remains held. This is the
    /// ordering-only half of a resolution and deliberately runs before optional
    /// configuration, scope, and database work.
    /// </summary>
    private GlobalResolvedFence PublishResolvedTombstoneAndFenceLifecycles(
        string attentionItemId,
        AttentionDispatchVersion version)
    {
        while (true)
        {
            AttentionItemFence fence = _attentionItemFences.GetOrAdd(
                attentionItemId,
                static _ => new AttentionItemFence());

            if (fence.TryPublishResolvedAndRun(
                    version,
                    UtcNow,
                    () => FenceTrackedLifecyclesForGlobalResolution(
                        fence,
                        attentionItemId,
                        version),
                    out List<GlobalResolvedParticipant>? participants))
            {
                return new GlobalResolvedFence(participants ?? []);
            }

            // A pruner retired this fence after GetOrAdd returned it. Retry
            // against the replacement fence instead of publishing to an
            // orphaned tombstone.
        }
    }

    private List<GlobalResolvedParticipant>? FenceTrackedLifecyclesForGlobalResolution(
        AttentionItemFence fence,
        string attentionItemId,
        AttentionDispatchVersion version)
    {
        List<GlobalResolvedParticipant>? participants = null;
        foreach (TrackedAttentionLifecycle tracked in fence.GetTrackedLifecycles())
        {
            AttentionLifecycleObserveResult result = tracked.Lifecycle.TryObserve(
                version,
                AttentionChangeKind.Resolved,
                UtcNow,
                () => ResetActiveLifecycleDedupe(tracked.UserId, attentionItemId),
                out ResolutionCapture? resolutionCapture);
            if (result == AttentionLifecycleObserveResult.Accepted)
            {
                participants ??= [];
                participants.Add(new GlobalResolvedParticipant(
                    tracked.UserId,
                    tracked.Lifecycle,
                    version,
                    resolutionCapture));
            }
            else if (result == AttentionLifecycleObserveResult.Retired)
            {
                fence.UntrackLifecycle(tracked.UserId, tracked.Lifecycle);
            }
        }

        return participants;
    }

    /// <summary>
    /// Publishes a targeted resolution's ordering fence: advances ONLY the
    /// target user's per-item lifecycle to Resolved v_target under the
    /// AttentionItemFence's lock, so a concurrent Created that already
    /// admitted the same lifecycle is superseded before this dispatch
    /// hits any optional/fallible early exit. Does NOT bump the item-wide
    /// <c>_resolvedVersion</c> tombstone (that is a global-only invariant).
    /// Returns a fence-shaped participant so the existing
    /// <see cref="GlobalResolvedFence.Take"/>/Complete plumbing releases
    /// the version-specific lease exactly once on every path (Hicks r2
    /// blocker 3).
    /// </summary>
    private GlobalResolvedFence PublishTargetedResolvedFence(
        string attentionItemId,
        Guid targetUserId,
        AttentionDispatchVersion version)
    {
        while (true)
        {
            AttentionItemFence fence = _attentionItemFences.GetOrAdd(
                attentionItemId,
                static _ => new AttentionItemFence());

            List<GlobalResolvedParticipant>? participants = null;
            AttentionItemFenceResult fenceResult = fence.TryFenceTargetedLifecycle(
                version,
                UtcNow,
                () => participants = FenceTargetLifecycleForTargetedResolution(
                    fence,
                    attentionItemId,
                    targetUserId,
                    version));

            if (fenceResult == AttentionItemFenceResult.Retired)
            {
                // A pruner retired this fence after GetOrAdd returned it.
                // Retry against the replacement fence instead of fencing
                // against an orphaned tombstone.
                continue;
            }

            return new GlobalResolvedFence(participants ?? []);
        }
    }

    private List<GlobalResolvedParticipant>? FenceTargetLifecycleForTargetedResolution(
        AttentionItemFence fence,
        string attentionItemId,
        Guid targetUserId,
        AttentionDispatchVersion version)
    {
        // Runs under the AttentionItemFence lock (via TryFenceTargetedLifecycle),
        // so this install-and-observe happens atomically with any global
        // resolution's tombstone publish + owner enumeration for the same
        // item. Lock order is fence -> lifecycle, matching every other
        // path so there is no deadlock potential.
        while (true)
        {
            var key = new AttentionSnapshotKey(targetUserId, attentionItemId);
            AttentionLifecycle lifecycle = _attentionLifecycles.GetOrAdd(
                key,
                static _ => new AttentionLifecycle());
            fence.TrackLifecycle(targetUserId, lifecycle);

            AttentionLifecycleObserveResult result = lifecycle.TryObserve(
                version,
                AttentionChangeKind.Resolved,
                UtcNow,
                () => ResetActiveLifecycleDedupe(targetUserId, attentionItemId),
                out ResolutionCapture? resolutionCapture);
            if (result == AttentionLifecycleObserveResult.Accepted)
            {
                return
                [
                    new GlobalResolvedParticipant(
                        targetUserId,
                        lifecycle,
                        version,
                        resolutionCapture),
                ];
            }

            if (result == AttentionLifecycleObserveResult.Retired)
            {
                // A pruner retired this lifecycle instance after GetOrAdd
                // returned it. Untrack the orphan and drop it from the
                // dictionary so the loop's next GetOrAdd installs a fresh
                // replacement, then retry the observation.
                fence.UntrackLifecycle(targetUserId, lifecycle);
                _ = ((ICollection<KeyValuePair<AttentionSnapshotKey, AttentionLifecycle>>)_attentionLifecycles)
                    .Remove(new KeyValuePair<AttentionSnapshotKey, AttentionLifecycle>(key, lifecycle));
                continue;
            }

            // Stale: a strictly-newer lifecycle observation already
            // superseded this generation. No participant to enrol; the
            // caller returns an empty fence and the eventual owner
            // enumeration will observe the same staleness.
            return null;
        }
    }

    private static DateTime NormalizeOccurredAt(DateTime occurredAt)
    {
        return occurredAt.Kind switch
        {
            DateTimeKind.Utc => occurredAt,
            DateTimeKind.Local => occurredAt.ToUniversalTime(),
            _ => DateTime.SpecifyKind(occurredAt, DateTimeKind.Utc),
        };
    }

    private static int LifecycleOrder(AttentionChangeKind changeKind)
    {
        return changeKind switch
        {
            AttentionChangeKind.Created => 0,
            AttentionChangeKind.Updated => 1,
            AttentionChangeKind.Resolved => 2,
            _ => int.MinValue,
        };
    }

    private static string BuildDedupeKey(
        Guid userId,
        string attentionItemId,
        AttentionChangeKind changeKind)
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{userId:D}|{attentionItemId}|{changeKind}");
    }

    private void ResetActiveLifecycleDedupe(Guid userId, string attentionItemId)
    {
        _dedupe.TryRemove(
            BuildDedupeKey(userId, attentionItemId, AttentionChangeKind.Created),
            out _);
        _dedupe.TryRemove(
            BuildDedupeKey(userId, attentionItemId, AttentionChangeKind.Updated),
            out _);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // No unmanaged resources; kept for future rate-limit timer.
    }

    private async Task<NativePushSendOutcome> SendWithRetriesAsync(
        Guid userId,
        NativePushEnvelope envelope,
        AttentionLifecycle lifecycle,
        AttentionDispatchVersion version,
        AttentionSnapshot lifecycleSnapshot,
        AttentionChangeKind changeKind,
        string dedupeKey,
        RateLimitKey? rateKey,
        NativePushSettings settings,
        IOperatorFeatureGate gate,
        CancellationToken cancellationToken)
    {
        int attempts = Math.Max(1, settings.MaxAttempts);
        NativePushDispatchResult? last = null;
        bool anyTransportStarted = false;
        for (int i = 0; i < attempts; i++)
        {
            // This persisted gate read is intentionally adjacent to the transport call.
            // A retry must never inherit the enabled decision made by an earlier attempt.
            // Awaited outside every lock — the dispatcher does not hold any in-memory
            // lock at this point, so a slow DB round-trip only delays this dispatch's
            // retry cadence, never unrelated concurrent transports.
            if (!await gate.IsEnabledAsync(OperatorFeature.NativePush, cancellationToken).ConfigureAwait(false))
            {
                _metrics.SkippedFeatureDisabled.Add(1);
                return new NativePushSendOutcome(null, anyTransportStarted);
            }

            // Reserve dedupe / rate capacity before preparation begins, but do
            // not commit lifecycle ownership until the sender explicitly
            // crosses its provider boundary. A JWT lock, payload build, or
            // cancellation before that signal must remain fully recoverable.
            DateTime? dedupeReservedAt = null;
            DateTime? rateReservedAt = null;

            Func<bool> shouldEmit = () =>
            {
                DateTime now = UtcNow;
                DateTime expiresAt = now.Add(settings.DedupeWindow);
                bool emitted = false;
                _ = _dedupe.AddOrUpdate(
                    dedupeKey,
                    _ =>
                    {
                        emitted = true;
                        return expiresAt;
                    },
                    (_, existing) =>
                    {
                        if (existing > now)
                        {
                            emitted = false;
                            return existing;
                        }

                        emitted = true;
                        return expiresAt;
                    });
                if (emitted)
                {
                    dedupeReservedAt = expiresAt;
                }

                return emitted;
            };
            Action rollbackDedupe = () =>
            {
                if (dedupeReservedAt is DateTime committed)
                {
                    _ = ((ICollection<KeyValuePair<string, DateTime>>)_dedupe)
                        .Remove(new KeyValuePair<string, DateTime>(dedupeKey, committed));
                    dedupeReservedAt = null;
                }
            };
            Func<bool>? tryConsumeRate = rateKey is RateLimitKey activeRateKey
                ? () =>
                {
                    DateTime now = UtcNow;
                    if (TryConsumeRate(activeRateKey, settings, now))
                    {
                        rateReservedAt = now;
                        return true;
                    }

                    return false;
                }
            : null;
            Action? rollbackRate = rateKey is RateLimitKey activeRateKeyForRollback
                ? () =>
                {
                    if (rateReservedAt is DateTime consumed)
                    {
                        RollbackRate(activeRateKeyForRollback, consumed);
                        rateReservedAt = null;
                    }
                }
            : null;

            LifecycleSendReservationResult reservationResult = lifecycle.TryReserveSend(
                version,
                lifecycleSnapshot,
                changeKind == AttentionChangeKind.Resolved,
                shouldEmit,
                rollbackDedupe,
                tryConsumeRate,
                rollbackRate);
            if (reservationResult.BlockReason == LifecycleSendBlockReason.Dedupe)
            {
                _metrics.SkippedDedupe.Add(1);
                return new NativePushSendOutcome(null, anyTransportStarted);
            }

            if (reservationResult.BlockReason == LifecycleSendBlockReason.RateLimit)
            {
                _metrics.SkippedRateLimit.Add(1);
                return new NativePushSendOutcome(null, anyTransportStarted);
            }

            if (reservationResult.Reservation is not LifecycleSendReservation reservation)
            {
                return new NativePushSendOutcome(null, anyTransportStarted);
            }

            var transportStart = new DispatcherTransportStart(
                _logger,
                envelope.AttentionItemId,
                lifecycle,
                reservation,
                () =>
                {
                    if (changeKind != AttentionChangeKind.Resolved)
                    {
                        _dedupe.TryRemove(
                            BuildDedupeKey(
                                userId,
                                envelope.AttentionItemId,
                                AttentionChangeKind.Resolved),
                            out _);
                    }

                    _metrics.Attempted.Add(1);
                },
                async ct => await gate.IsEnabledAsync(OperatorFeature.NativePush, ct).ConfigureAwait(false),
                () => _metrics.SkippedFeatureDisabled.Add(1),
                cancellationToken);

            NativePushDispatchResult? result = null;
            try
            {
                result = await SendThroughTransportBoundaryAsync(
                    envelope,
                    transportStart,
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (transportStart.WasStarted)
            {
                // The boundary already committed, so retain normal retry and
                // token-attribution semantics rather than rolling back a real
                // provider attempt. Cancellation is handled above and never
                // reaches this conversion.
                _logger.LogWarning(
                    ex,
                    "[NativePush] Sender threw after transport start for attentionItemId={AttentionItemId}.",
                    envelope.AttentionItemId);
                result = NativePushDispatchResult.Transient("sender_exception");
            }
            finally
            {
                if (transportStart.WasStarted)
                {
                    transportStart.Settle(result?.Success == true);
                    anyTransportStarted = true;
                }
                else
                {
                    transportStart.CompleteWithoutStart();
                }
            }

            if (!transportStart.WasStarted)
            {
                if (result?.Success == true)
                {
                    throw new InvalidOperationException(
                        "A native-push sender reported delivery without crossing the transport-start boundary.");
                }

                if (string.Equals(result?.Reason, "notConfigured", StringComparison.Ordinal))
                {
                    _metrics.SkippedNotConfigured.Add(1);
                }

                // A no-signal pre-transport result leaves the exact version
                // recoverable. If an earlier retry already crossed transport,
                // preserve that earlier provider result for normal attribution.
                return anyTransportStarted
                    ? new NativePushSendOutcome(last, true)
                    : new NativePushSendOutcome(null, false);
            }

            last = result!;

            // A sender may complete after an administrator has committed the
            // emergency disable or after a resolution/replacement consumed this
            // alert generation. Discard that provider result before retry or any
            // result attribution.
            cancellationToken.ThrowIfCancellationRequested();
            if (!await gate.IsEnabledAsync(OperatorFeature.NativePush, cancellationToken).ConfigureAwait(false))
            {
                _metrics.SkippedFeatureDisabled.Add(1);
                return new NativePushSendOutcome(null, true);
            }

            if (!lifecycle.IsCurrent(
                    version,
                    lifecycleSnapshot,
                    changeKind == AttentionChangeKind.Resolved))
            {
                return new NativePushSendOutcome(null, true);
            }

            if (last.Success || last.TokenInvalidated || !last.IsTransient)
            {
                return new NativePushSendOutcome(last, true);
            }

            if (i + 1 < attempts)
            {
                // Small linear backoff — the outbound HttpClient enforces the hard timeout.
                await Task.Delay(
                    TimeSpan.FromMilliseconds(200 * (i + 1)),
                    _timeProvider,
                    cancellationToken);
            }
        }

        return new NativePushSendOutcome(last, anyTransportStarted);
    }

    private Task<NativePushDispatchResult> SendThroughTransportBoundaryAsync(
        NativePushEnvelope envelope,
        DispatcherTransportStart transportStart,
        CancellationToken cancellationToken)
    {
        return _sender.SendAsync(envelope, transportStart, cancellationToken);
    }

    private sealed class DispatcherTransportStart(
        ILogger logger,
        string attentionItemId,
        AttentionLifecycle lifecycle,
        LifecycleSendReservation reservation,
        Action onStarted,
        Func<CancellationToken, Task<bool>> isFeatureEnabledAsync,
        Action onFeatureDisabledVeto,
        CancellationToken dispatchCancellationToken) : INativePushTransportStart
    {
        private readonly object _sync = new();
        private readonly PendingTransportAttempt _attempt = new();
        private TransportStartState _state;
        private bool _settled;
        private AttentionDeliveryLineage? _registeredLineage;

        public bool WasStarted
        {
            get
            {
                lock (_sync)
                {
                    return _state == TransportStartState.Started;
                }
            }
        }

        public async Task<NativePushTransportStartDecision> TryStartAsync(CancellationToken cancellationToken)
        {
            // Hicks r2 blocker 2: the persisted operator feature-gate read is the
            // authorization linearization point and it MUST run outside every
            // in-memory lock. The prior implementation invoked a synchronous
            // <c>isFeatureEnabled()</c> delegate that internally blocked on the
            // async repository read via <c>.GetAwaiter().GetResult()</c> — under
            // the sync lock below that pinned the caller's thread on an
            // unbounded EF/SQL round-trip, blocked pool workers, prevented
            // cancellation from reaching later checks, and let reservations
            // and lanes accumulate.
            //
            // Read the gate BEFORE acquiring the transport lock. Cancellation
            // propagates naturally through both the caller/dispatcher token
            // (captured at construction) and the sender-passed token. A
            // repository/DB throw is logged fail-closed at this exact site
            // and the reservation is rolled back so an exact-version retry
            // remains recoverable after DB recovery.
            //
            // A pre-cancelled token short-circuits without reading the gate at
            // all: pre-cancellation must never issue a DB query on the way to
            // committing a doomed attempt, and the veto+rollback below is the
            // same outcome the atomic revalidation section would produce.
            //
            // <paramref name="cancellationToken"/> is the sender-side token
            // required by <see cref="INativePushTransportStart.TryStartAsync"/>.
            // It is linked with the outer dispatcher/caller token captured at
            // construction time so either signal aborts the awaited gate read
            // and drives a veto+rollback without pinning a thread.
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
                dispatchCancellationToken,
                cancellationToken);
            CancellationToken linkedToken = linked.Token;
            if (linkedToken.IsCancellationRequested)
            {
                VetoAndRollback();
                return NativePushTransportStartDecision.Veto();
            }

            bool gateEnabled;
            try
            {
                gateEnabled = await isFeatureEnabledAsync(linkedToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Caller/dispatcher cancellation propagates unchanged. A pre-
                // transport rollback returns dedupe/rate and reverts the
                // reservation so an exact-version retry recovers after
                // cancellation clears.
                VetoAndRollback();
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                // Fail-closed on repository/DB/gate errors. Logged explicitly with
                // the delivery/attention-item context (the gate itself does not
                // know which envelope caused the read). A follow-up retry after
                // DB recovery re-enters this path and receives a fresh decision.
                logger.LogWarning(
                    ex,
                    "[NativePush] Feature-gate read failed at transport-start for attentionItemId={AttentionItemId}; failing closed and rolling back the reservation for exact-version retry.",
                    attentionItemId);
                VetoAndRollback();
                return NativePushTransportStartDecision.Veto();
            }

            if (!gateEnabled)
            {
                VetoAndRollback();
                onFeatureDisabledVeto();
                return NativePushTransportStartDecision.Veto();
            }

            // The gate has authorized this attempt. Now take the narrow
            // transport lock and atomically revalidate every dispatcher-owned
            // invariant that could have changed while we awaited the gate:
            //   * caller/dispatcher cancellation observed strictly after the
            //     authorization vetoes (rolls back);
            //   * a prior <c>TryStartAsync</c> may have already committed or
            //     vetoed;
            //   * a concurrent newer generation may have superseded this
            //     reservation's lifecycle version (rejected by
            //     <see cref="AttentionLifecycle.TryStartTransport"/>);
            //   * a Resolved capture may have consumed the snapshot pointer.
            // Commit is EXACTLY-ONCE: only one <c>TryStartAsync</c> per
            // reservation can transition <c>_state = Started</c> and emit
            // <c>Attempted</c>. Cancellation observed AFTER a permitted
            // decision is a genuine, already-committed attempt and is never
            // retroactively vetoed — the interface's documented contract.
            lock (_sync)
            {
                if (_state != TransportStartState.Pending)
                {
                    return NativePushTransportStartDecision.Veto();
                }

                if (linkedToken.IsCancellationRequested)
                {
                    _state = TransportStartState.Closed;
                    lifecycle.RollbackReservation(reservation);
                    return NativePushTransportStartDecision.Veto();
                }

                if (!lifecycle.TryStartTransport(reservation, _attempt, out AttentionDeliveryLineage? lineage))
                {
                    _state = TransportStartState.Closed;
                    lifecycle.RollbackReservation(reservation);
                    return NativePushTransportStartDecision.Veto();
                }

                _registeredLineage = lineage;
                _state = TransportStartState.Started;
                onStarted();
                return NativePushTransportStartDecision.Permit();
            }
        }

        private void VetoAndRollback()
        {
            lock (_sync)
            {
                if (_state != TransportStartState.Pending)
                {
                    return;
                }

                _state = TransportStartState.Closed;
                lifecycle.RollbackReservation(reservation);
            }
        }

        public void CompleteWithoutStart()
        {
            lock (_sync)
            {
                if (_state != TransportStartState.Pending)
                {
                    return;
                }

                _state = TransportStartState.Closed;
                lifecycle.RollbackReservation(reservation);
            }
        }

        public void Settle(bool wasSuccessful)
        {
            lock (_sync)
            {
                if (_state != TransportStartState.Started || _settled)
                {
                    return;
                }

                _settled = true;
            }

            if (!reservation.IsResolution)
            {
                _registeredLineage?.SettleStartedAttempt(_attempt, wasSuccessful);
            }
        }

        private enum TransportStartState
        {
            Pending,
            Started,
            Closed,
        }
    }

    private async Task<DeviceDispatchOutcome> ApplyResultAsync(
        AttentionLifecycle lifecycle,
        AttentionDispatchVersion version,
        AttentionSnapshot activeSnapshot,
        bool isResolution,
        DeviceToken deviceToken,
        NativePushDispatchResult result,
        NativePushSettings settings,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
        IOperatorFeatureGate gate = scope.ServiceProvider.GetRequiredService<IOperatorFeatureGate>();
        if (!await gate.IsEnabledAsync(OperatorFeature.NativePush, cancellationToken).ConfigureAwait(false))
        {
            _metrics.SkippedFeatureDisabled.Add(1);
            return DeviceDispatchOutcome.DispatchStopped;
        }

        // Final persisted-result attribution is fenced independently from the
        // transport settlement. A racing Resolved may reject this token write,
        // but it already captured and can await the same snapshot's successful
        // transport settlement before deciding whether to dismiss.
        if (!lifecycle.TryClaimAttribution(version, activeSnapshot, isResolution))
        {
            return DeviceDispatchOutcome.DispatchStopped;
        }

        IDeviceTokenRepository tokens = scope.ServiceProvider.GetRequiredService<IDeviceTokenRepository>();
        DateTime nowUtc = UtcNow;
        if (result.Success)
        {
            _metrics.Delivered.Add(1, new KeyValuePair<string, object?>("mode", _sender.ModeName));
            await tokens.RecordSuccessAsync(
                deviceToken.Id,
                deviceToken.RegistrationVersion,
                nowUtc,
                cancellationToken);
            return DeviceDispatchOutcome.Completed;
        }

        if (result.TokenInvalidated)
        {
            _metrics.TokensInvalidated.Add(1);
            _ = await tokens.InvalidateAsync(
                deviceToken.Id,
                deviceToken.RegistrationVersion,
                cancellationToken);
            return DeviceDispatchOutcome.Completed;
        }

        // NotConfigured is a config-typo skip, not a device fault. Log-and-drop with
        // NO failure counter mutation, so a misconfigured mode cannot deactivate the
        // entire token fleet on the first outage.
        if (string.Equals(result.Reason, "notConfigured", StringComparison.Ordinal))
        {
            _metrics.SkippedNotConfigured.Add(1);
            return DeviceDispatchOutcome.Completed;
        }

        if (result.IsTransient)
        {
            _metrics.TransientFailed.Add(
                1,
                new KeyValuePair<string, object?>("mode", _sender.ModeName),
                new KeyValuePair<string, object?>("reason", result.Reason ?? "unknown"));

            // Transient reasons cover provider-wide outages ("timeout", "network",
            // HTTP 429/5xx). These are NOT evidence the token is dead, so do not
            // increment the per-token failure counter — that would deactivate every
            // active token in five outages. Retry orchestration already retried this
            // send N times; the outage is process-wide.
            return DeviceDispatchOutcome.Completed;
        }

        _metrics.TerminalFailed.Add(
            1,
            new KeyValuePair<string, object?>("mode", _sender.ModeName),
            new KeyValuePair<string, object?>("reason", result.Reason ?? "unknown"));

        // Safe default: unknown, relay, configuration, JWT, topic, and payload
        // failures never poison registrations. Only a sender's explicit typed
        // attribution may increment/deactivate this token. Known APNs invalid
        // token responses use TokenInvalidated above and are removed directly.
        if (result.FailureAttribution != NativePushFailureAttribution.DeviceToken)
        {
            return DeviceDispatchOutcome.Completed;
        }

        await tokens.RecordFailureAsync(
            deviceToken.Id,
            deviceToken.RegistrationVersion,
            nowUtc,
            settings.FailureDeactivationThreshold,
            cancellationToken);
        return DeviceDispatchOutcome.Completed;
    }

    private static async Task<bool> IsFarmAdminAsync(AppDbContext db, Guid userId, CancellationToken cancellationToken)
    {
        return await db.Users
            .AsNoTracking()
            .AnyAsync(u => u.Id == userId && u.UserRoles.Any(ur => ur.Role.Name == "farm_admin" && ur.IsActive), cancellationToken);
    }

    private NativePushEnvelope BuildEnvelope(
        AttentionItemDto item,
        AttentionChangeKind changeKind,
        DeviceToken deviceToken)
    {
        bool isResolved = changeKind == AttentionChangeKind.Resolved;
        string category = AttentionPushCategories.CategoryFor(item.Kind) ?? "PRINTER_FAILURE";
        string threadId = AttentionPushCategories.ThreadIdFor(
            item.Kind,
            item.PrinterId,
            item.ToolheadIndex,
            item.Id);
        string deepLink = AttentionDeepLinks.For(
            item.Kind,
            item.PrinterId,
            item.Id,
            item.ToolheadIndex,
            item.JobId);

        DateTime nowUtc = UtcNow;
        TimeSpan alertTtl = item.Severity == AttentionSeverity.Info
            ? InformationalAlertTtl
            : ActionableAlertTtl;
        DateTime ttlExpiration = nowUtc.Add(alertTtl);
        DateTime expiresAt = isResolved
            ? nowUtc.Add(InformationalAlertTtl)
            : item.DeadlineAt is DateTime deadline && deadline < ttlExpiration
                ? deadline
                : ttlExpiration;
        IReadOnlyList<string> actionIds = isResolved
            ? Array.Empty<string>()
            : AttentionPushCategories.ActionsFor(item.Kind);

        return new NativePushEnvelope(
            DeviceTokenId: deviceToken.Id.ToString("D", CultureInfo.InvariantCulture),
            Token: deviceToken.Token,
            Platform: deviceToken.Platform,
            Environment: deviceToken.Environment,
            AppBundleId: deviceToken.AppBundleId,
            Category: category,
            ThreadId: threadId,
            Title: isResolved ? null : item.PrinterName,
            Subtitle: null,
            Body: isResolved ? null : item.Title,
            AttentionItemId: item.Id,
            AttentionKind: item.Kind,
            ChangeKind: changeKind,
            PrinterId: item.PrinterId,
            JobId: item.JobId,
            ToolheadIndex: item.ToolheadIndex,
            DeepLink: deepLink,
            Priority: isResolved ? NativePushPriority.Background : NativePushPriority.Alert,
            ExpiresAtUtc: expiresAt,
            ActionIds: actionIds);
    }

    private NativePushEnvelope BuildSilentEnvelopeFromSnapshot(
        string attentionItemId,
        AttentionSnapshot snapshot,
        DeviceToken deviceToken)
    {
        string category = AttentionPushCategories.CategoryFor(snapshot.Kind) ?? "PRINTER_FAILURE";
        string threadId = AttentionPushCategories.ThreadIdFor(
            snapshot.Kind,
            snapshot.PrinterId,
            snapshot.ToolheadIndex,
            attentionItemId);
        string deepLink = AttentionDeepLinks.For(
            snapshot.Kind,
            snapshot.PrinterId,
            attentionItemId,
            snapshot.ToolheadIndex,
            snapshot.JobId);

        return new NativePushEnvelope(
            DeviceTokenId: deviceToken.Id.ToString("D", CultureInfo.InvariantCulture),
            Token: deviceToken.Token,
            Platform: deviceToken.Platform,
            Environment: deviceToken.Environment,
            AppBundleId: deviceToken.AppBundleId,
            Category: category,
            ThreadId: threadId,
            Title: null,
            Subtitle: null,
            Body: null,
            AttentionItemId: attentionItemId,
            AttentionKind: snapshot.Kind,
            ChangeKind: AttentionChangeKind.Resolved,
            PrinterId: snapshot.PrinterId,
            JobId: snapshot.JobId,
            ToolheadIndex: snapshot.ToolheadIndex,
            DeepLink: deepLink,
            Priority: NativePushPriority.Background,
            ExpiresAtUtc: UtcNow.Add(InformationalAlertTtl),
            ActionIds: Array.Empty<string>());
    }

    private enum DeviceDispatchOutcome
    {
        Completed,
        DispatchStopped,
    }

    private readonly record struct AttentionDispatchKey(string AttentionItemId, Guid? TargetUserId);

    private readonly record struct AttentionDispatchVersion(DateTime OccurredAtUtc, int ChangeOrder)
        : IComparable<AttentionDispatchVersion>
    {
        public int CompareTo(AttentionDispatchVersion other)
        {
            int timestampOrder = OccurredAtUtc.CompareTo(other.OccurredAtUtc);
            return timestampOrder != 0 ? timestampOrder : ChangeOrder.CompareTo(other.ChangeOrder);
        }
    }

    private enum AttentionDispatchObserveResult
    {
        Accepted,
        Stale,
        Retired,
    }

    private sealed class AttentionDispatchLane
    {
        private readonly object _sync = new();

        // Active participants tracked per accepted version so a still-pending
        // older-version participant cannot block an equal-latest retry whose
        // own earlier participant already released (#755 Hicks). Retire and
        // Gate ordering continue to use the sum via _totalParticipants so a
        // lane cannot be pruned while any generation is still in flight.
        // A given (version) can be re-observed only after its participant
        // has completed (equal-version admission requires the current-version
        // slot to be empty), so each version key is either absent or exactly
        // one — a HashSet-style membership check is sufficient.
        private readonly HashSet<AttentionDispatchVersion> _activeVersions = [];
        private AttentionDispatchVersion _latest;
        private DateTime _lastObservedAtUtc;
        private int _totalParticipants;
        private bool _hasVersion;
        private bool _retired;

        public SemaphoreSlim Gate { get; } = new(1, 1);

        public AttentionDispatchObserveResult TryObserve(
            AttentionDispatchVersion version,
            DateTime observedAtUtc)
        {
            lock (_sync)
            {
                if (_retired)
                {
                    return AttentionDispatchObserveResult.Retired;
                }

                int versionOrder = _hasVersion ? version.CompareTo(_latest) : 1;
                if (versionOrder < 0
                    || (versionOrder == 0 && _activeVersions.Contains(_latest)))
                {
                    return AttentionDispatchObserveResult.Stale;
                }

                if (versionOrder > 0)
                {
                    _latest = version;
                }

                _lastObservedAtUtc = observedAtUtc;
                _hasVersion = true;
                _ = _activeVersions.Add(version);
                _totalParticipants++;
                return AttentionDispatchObserveResult.Accepted;
            }
        }

        public bool IsLatest(AttentionDispatchVersion version)
        {
            lock (_sync)
            {
                return version == _latest;
            }
        }

        public void Complete(AttentionDispatchVersion version)
        {
            lock (_sync)
            {
                if (_activeVersions.Remove(version))
                {
                    _totalParticipants--;
                }
            }
        }

        public bool TryRetire(DateTime cutoffUtc)
        {
            lock (_sync)
            {
                if (_retired || _totalParticipants != 0 || _lastObservedAtUtc >= cutoffUtc)
                {
                    return false;
                }

                _retired = true;
                return true;
            }
        }
    }

    private readonly record struct AttentionSnapshotKey(Guid UserId, string AttentionItemId);

    private enum AttentionItemFenceResult
    {
        Accepted,
        Stale,
        Retired,
    }

    private readonly record struct LifecycleObservation(
        bool Accepted,
        AttentionLifecycle? Lifecycle,
        ResolutionCapture? ResolutionCapture);

    private readonly record struct TrackedAttentionLifecycle(
        Guid UserId,
        AttentionLifecycle Lifecycle);

    private sealed class GlobalResolvedParticipant(
        Guid userId,
        AttentionLifecycle lifecycle,
        AttentionDispatchVersion version,
        ResolutionCapture? capture)
    {
        private int _completed;

        public Guid UserId { get; } = userId;

        public AttentionLifecycle Lifecycle { get; } = lifecycle;

        public ResolutionCapture? Capture { get; } = capture;

        public void Complete()
        {
            if (Interlocked.Exchange(ref _completed, 1) == 0)
            {
                // Release the version-specific lease that
                // FenceTrackedLifecyclesForGlobalResolution acquired via
                // TryObserve. Passing the resolution version keeps a still-
                // pending older-generation participant from blocking an
                // equal-latest retry after this resolution releases (#755).
                Lifecycle.Complete(version);
            }
        }
    }

    private sealed class GlobalResolvedFence(IReadOnlyList<GlobalResolvedParticipant> participants)
    {
        private readonly Dictionary<Guid, GlobalResolvedParticipant> _participantsByUser =
            participants.ToDictionary(participant => participant.UserId);

        public IReadOnlyList<GlobalResolvedParticipant> Participants { get; } = participants;

        public GlobalResolvedParticipant? Take(Guid userId)
        {
            return _participantsByUser.Remove(userId, out GlobalResolvedParticipant? participant)
                ? participant
                : null;
        }

        public void Complete()
        {
            foreach (GlobalResolvedParticipant participant in Participants)
            {
                participant.Complete();
            }
        }
    }

    /// <summary>
    /// Per-attention-item linearization point between a global resolution's
    /// tombstone publication + owner enumeration
    /// (<see cref="TryPublishResolvedAndRun"/>) and a targeted dispatch's
    /// per-recipient lifecycle install (<see cref="TryAdmitTargeted"/>).
    /// Both operations acquire this SAME lock, so for a given attention item
    /// exactly one of them happens-before the other:
    ///   - If a targeted admission runs first, the per-user lifecycle it
    ///     installs under this lock (via the <c>install</c> callback) is
    ///     already present in <c>_attentionLifecycles</c> by the time a
    ///     subsequent resolution's owner enumeration runs (also under this
    ///     lock), so that recipient is included and its lifecycle is
    ///     properly advanced/fenced by the resolution.
    ///   - If a resolution's publish-and-enumerate runs first, the published
    ///     version is visible to any subsequent targeted admission under
    ///     this same lock, so a stale targeted dispatch is rejected before
    ///     it can install a lifecycle unaware of the resolution.
    /// This closes the P-A-D-R-S interleaving: a stale targeted Created (P)
    /// reads this fence as unpublished, then pauses before installing its
    /// per-owner lifecycle; a concurrent global Resolved (A) publishes the
    /// tombstone; the resolution's owner enumeration (D) runs while the
    /// recipient is tokenless and before its lifecycle exists, so it finds
    /// no owner to fence and completes; the recipient re-registers a device
    /// token (R); P resumes (S) and re-enters this fence — with the
    /// published version now visible under the same lock, P is rejected
    /// before it can install a lifecycle or reach transport.
    /// Neither <see cref="TryAdmitTargeted"/> nor
    /// <see cref="TryPublishResolvedAndRun"/> ever holds this lock across an
    /// await — both only perform bounded, synchronous in-memory
    /// dictionary/version work while holding it — so unrelated attention
    /// items and recipients are never serialised, and there is no
    /// process-global lock.
    /// </summary>
    private sealed class AttentionItemFence
    {
        private readonly object _sync = new();
        private readonly Dictionary<Guid, AttentionLifecycle> _trackedLifecycles = [];
        private AttentionDispatchVersion? _resolvedVersion;
        private DateTime _lastTouchedAtUtc;
        private bool _retired;

        /// <summary>
        /// Atomically checks <paramref name="version"/> against the
        /// item-wide resolved tombstone and, only when not fenced, runs
        /// <paramref name="install"/> (the per-user lifecycle
        /// GetOrAdd/TryObserve) before releasing the lock.
        /// </summary>
        public AttentionItemFenceResult TryAdmitTargeted(
            AttentionDispatchVersion version,
            DateTime observedAtUtc,
            Action install)
        {
            lock (_sync)
            {
                if (_retired)
                {
                    return AttentionItemFenceResult.Retired;
                }

                _lastTouchedAtUtc = observedAtUtc;
                if (_resolvedVersion is AttentionDispatchVersion resolved
                    && version.CompareTo(resolved) < 0)
                {
                    return AttentionItemFenceResult.Stale;
                }

                install();
                return AttentionItemFenceResult.Accepted;
            }
        }

        /// <summary>
        /// Publishes (monotonically bumps) the item-wide resolved tombstone
        /// and, atomically with the publish under this same lock, runs
        /// <paramref name="fenceLifecycles"/>. Returns false only when this
        /// fence has already been retired by the pruner, so the caller can
        /// retry against a fresh replacement instead of publishing to an
        /// orphaned tombstone.
        /// </summary>
        public bool TryPublishResolvedAndRun(
            AttentionDispatchVersion version,
            DateTime observedAtUtc,
            Func<List<GlobalResolvedParticipant>?> fenceLifecycles,
            out List<GlobalResolvedParticipant>? participants)
        {
            lock (_sync)
            {
                if (_retired)
                {
                    participants = null;
                    return false;
                }

                _lastTouchedAtUtc = observedAtUtc;
                if (_resolvedVersion is null || version.CompareTo(_resolvedVersion.Value) > 0)
                {
                    _resolvedVersion = version;
                }

                participants = fenceLifecycles();
                return true;
            }
        }

        /// <summary>
        /// Runs <paramref name="fenceLifecycle"/> atomically under this
        /// fence's lock so a targeted Resolved advances the target user's
        /// lifecycle in the same P-A-D-R-S linearization point a global
        /// Resolved uses. Unlike <see cref="TryPublishResolvedAndRun"/>,
        /// this does NOT bump the item-wide <c>_resolvedVersion</c>
        /// tombstone (that is a global-only invariant), but it DOES bump
        /// <c>_lastTouchedAtUtc</c> so pruning honours the freshly-touched
        /// fence. Returns <see cref="AttentionItemFenceResult.Stale"/> when
        /// a strictly-newer global tombstone has already been published
        /// and this targeted Resolved is superseded before it can fence.
        /// Returns <see cref="AttentionItemFenceResult.Retired"/> when the
        /// pruner retired this fence so the caller can retry against a
        /// fresh replacement.
        /// </summary>
        public AttentionItemFenceResult TryFenceTargetedLifecycle(
            AttentionDispatchVersion version,
            DateTime observedAtUtc,
            Action fenceLifecycle)
        {
            lock (_sync)
            {
                if (_retired)
                {
                    return AttentionItemFenceResult.Retired;
                }

                _lastTouchedAtUtc = observedAtUtc;

                if (_resolvedVersion is AttentionDispatchVersion resolved
                    && version.CompareTo(resolved) < 0)
                {
                    return AttentionItemFenceResult.Stale;
                }

                fenceLifecycle();
                return AttentionItemFenceResult.Accepted;
            }
        }

        /// <summary>
        /// Records the lifecycle installed while this item fence is held. Global
        /// resolution uses this registry instead of weak concurrent-dictionary
        /// enumeration, so it fences every lifecycle that was admitted before
        /// its own fence acquisition.
        /// </summary>
        public void TrackLifecycle(Guid userId, AttentionLifecycle lifecycle)
        {
            lock (_sync)
            {
                if (!_retired)
                {
                    _trackedLifecycles[userId] = lifecycle;
                }
            }
        }

        public void UntrackLifecycle(Guid userId, AttentionLifecycle lifecycle)
        {
            lock (_sync)
            {
                if (_trackedLifecycles.TryGetValue(userId, out AttentionLifecycle? tracked)
                    && ReferenceEquals(tracked, lifecycle))
                {
                    _ = _trackedLifecycles.Remove(userId);
                }
            }
        }

        public TrackedAttentionLifecycle[] GetTrackedLifecycles()
        {
            lock (_sync)
            {
                return _trackedLifecycles
                    .Select(pair => new TrackedAttentionLifecycle(pair.Key, pair.Value))
                    .ToArray();
            }
        }

        /// <summary>
        /// Best-effort, lock-protected read of the currently published
        /// tombstone version. Used only by DispatchAsync's non-authoritative
        /// fast-path check; the authoritative fence is
        /// <see cref="TryAdmitTargeted"/>.
        /// </summary>
        public AttentionDispatchVersion? PeekResolvedVersion()
        {
            lock (_sync)
            {
                return _resolvedVersion;
            }
        }

        public bool TryRetire(DateTime cutoffUtc)
        {
            lock (_sync)
            {
                if (_retired
                    || _lastTouchedAtUtc >= cutoffUtc
                    || _trackedLifecycles.Count != 0)
                {
                    return false;
                }

                _retired = true;
                return true;
            }
        }
    }

    private enum AttentionLifecycleObserveResult
    {
        Accepted,
        Stale,
        Retired,
    }

    private enum LifecycleSendBlockReason
    {
        None,
        Stale,
        Dedupe,
        RateLimit,
    }

    private readonly record struct LifecycleSendReservationResult(
        LifecycleSendReservation? Reservation,
        LifecycleSendBlockReason BlockReason);

    private readonly record struct NativePushSendOutcome(
        NativePushDispatchResult? Result,
        bool TransportStarted);

    private sealed class LifecycleSendReservation(
        AttentionDispatchVersion version,
        AttentionSnapshot expectedSnapshot,
        bool isResolution,
        bool requiresLifecycleCommit,
        Action rollbackDedupe,
        Action? rollbackRate)
    {
        private int _state;

        public AttentionDispatchVersion Version { get; } = version;

        public AttentionSnapshot ExpectedSnapshot { get; } = expectedSnapshot;

        public bool IsResolution { get; } = isResolution;

        public bool RequiresLifecycleCommit { get; } = requiresLifecycleCommit;

        public bool IsPending => Volatile.Read(ref _state) == 0;

        public bool TryMarkStarted() => Interlocked.CompareExchange(ref _state, 1, 0) == 0;

        public bool TryBeginRollback() => Interlocked.CompareExchange(ref _state, 2, 0) == 0;

        public void RollbackExternalReservations()
        {
            rollbackRate?.Invoke();
            rollbackDedupe();
        }
    }

    private sealed class AttentionLifecycle
    {
        private readonly object _sync = new();
        private AttentionDispatchVersion _latest;
        private AttentionDispatchVersion? _consumedResolutionVersion;
        private AttentionSnapshot? _snapshot;

        // Shared pending-attempt/delivery state for the current unresolved
        // occurrence (see AttentionDeliveryLineage). Created lazily by the
        // first non-resolution generation and REUSED — never replaced — by
        // every later Created/Updated generation for the SAME occurrence, so
        // an older generation's still-in-flight attempt (or its eventual
        // late success) remains visible even after a newer generation
        // supersedes it. Detached (nulled) only once a resolution's own
        // reservation actually commits in TryStartTransport — the same
        // moment _snapshot is nulled there, NOT merely when a Resolved
        // change is observed/accepted in TryObserve. This lets an
        // exact-version retry of a resolution that was cancelled (or
        // otherwise never reached that commit) still find and re-capture
        // the SAME lineage. The next fresh occurrence lazily creates a
        // brand-new instance in TryStartTransport and never inherits a
        // truly-committed resolution's consumed one.
        private AttentionDeliveryLineage? _lineage;
        private DateTime _lastObservedAtUtc;

        // Active participants tracked per accepted version so a still-pending
        // older-version participant cannot block an equal-latest retry whose
        // own earlier participant already released (#755 Hicks). Retire and
        // pruning continue to use the sum via _totalParticipants so a
        // lifecycle cannot be pruned while any generation is still in flight.
        // A given (version) can be re-observed only after its participant
        // has completed (equal-version admission requires _latestCommitted to
        // be false AND the current-version slot to be empty), so each version
        // key is either absent or exactly one — a HashSet-style membership
        // check is sufficient.
        private readonly HashSet<AttentionDispatchVersion> _activeVersions = [];
        private int _totalParticipants;
        private bool _hasVersion;
        private bool _latestCommitted;
        private bool _retired;

        public AttentionLifecycleObserveResult TryObserve(
            AttentionDispatchVersion version,
            AttentionChangeKind changeKind,
            DateTime observedAtUtc,
            Action? onLifecycleAdvancedOrResolved,
            out ResolutionCapture? resolutionCapture)
        {
            lock (_sync)
            {
                resolutionCapture = null;
                if (_retired)
                {
                    return AttentionLifecycleObserveResult.Retired;
                }

                int versionOrder = _hasVersion ? version.CompareTo(_latest) : 1;
                if (versionOrder < 0
                    || (versionOrder == 0
                        && (_latestCommitted || _activeVersions.Contains(_latest))))
                {
                    return AttentionLifecycleObserveResult.Stale;
                }

                if (versionOrder > 0)
                {
                    _latest = version;
                    _latestCommitted = false;
                }

                _lastObservedAtUtc = observedAtUtc;
                _hasVersion = true;
                _ = _activeVersions.Add(version);
                _totalParticipants++;
                if (changeKind == AttentionChangeKind.Resolved && _lineage is AttentionDeliveryLineage activeLineage)
                {
                    // Take a stable, point-in-time read of the active
                    // lineage's currently-pending attempts (Hicks blocker 1).
                    // Any older-version non-resolution TryStartTransport that
                    // has not yet committed will fail its own
                    // "reservation.Version != _latest" check the instant this
                    // returns (that check runs under this same lock), so it
                    // can never register a NEW attempt on this lineage after
                    // this point — capturing now is race-free regardless of
                    // whether _lineage itself is detached yet. _snapshot is
                    // guaranteed non-null here because it is only ever set
                    // together with _lineage (see TryStartTransport).
                    //
                    // Deliberately does NOT null _lineage here. A resolution
                    // can be observed/accepted (this branch) without ever
                    // reaching TryStartTransport's commit — e.g. cancelled
                    // during the attention lookup, or skipped for other
                    // reasons — and an exact-version retry of that SAME
                    // resolution must still find the SAME lineage here and
                    // re-capture, not silently see nothing pending/delivered
                    // (see
                    // DispatchAsync_CancelledResolutionBeforeTransport_PreservesSnapshotForNewerResolution).
                    // _lineage is detached only once the resolution's own
                    // reservation actually commits in TryStartTransport — the
                    // exact same moment _snapshot is nulled there — so a
                    // later, strictly-newer Created (a fresh recurrence)
                    // still starts a lineage of its own instead of inheriting
                    // a truly-consumed one's pending/delivered state.
                    resolutionCapture = new ResolutionCapture(
                        _snapshot!,
                        activeLineage.CapturePendingSettlements(),
                        activeLineage);
                }

                // Fire under the sync lock so any concurrent newer occurrence's
                // TryReserveSend on this same lifecycle either observes an empty
                // dedupe window (legitimate recurrence emits) or is serialised
                // behind us. Two independent conditions invoke the reset:
                //   (1) versionOrder > 0 — a strictly-newer legitimate
                //       occurrence supersedes any prior generation's
                //       versionless (user, item, kind) dedupe reservation,
                //       including the rate-limited/no-transport branch that
                //       intentionally retains its entry. Without this, Kane
                //       cycle-3 A/B interleavings suppress the newer
                //       occurrence before Resolution can catch up.
                //   (2) changeKind == Resolved — a Resolved observation
                //       clears any surviving Created/Updated entries even
                //       when it lands at the same version (a peer left a
                //       reservation but never emitted). This eliminates
                //       the pre-fix race where a delayed reset erased
                //       newer dedupe state or suppressed a legitimate
                //       recurrence.
                if (versionOrder > 0 || changeKind == AttentionChangeKind.Resolved)
                {
                    onLifecycleAdvancedOrResolved?.Invoke();
                }

                return AttentionLifecycleObserveResult.Accepted;
            }
        }

        public LifecycleSendReservationResult TryReserveSend(
            AttentionDispatchVersion version,
            AttentionSnapshot expectedSnapshot,
            bool isResolution,
            Func<bool> shouldEmit,
            Action rollbackDedupe,
            Func<bool>? tryConsumeRate,
            Action? rollbackRate)
        {
            lock (_sync)
            {
                if (_retired || !_hasVersion || version != _latest)
                {
                    return new LifecycleSendReservationResult(
                        null,
                        LifecycleSendBlockReason.Stale);
                }

                bool alreadyStarted = isResolution
                    ? _consumedResolutionVersion == version
                    : ReferenceEquals(_snapshot, expectedSnapshot);
                if (!alreadyStarted)
                {
                    if ((isResolution && !ReferenceEquals(_snapshot, expectedSnapshot))
                        || (!isResolution && _latestCommitted))
                    {
                        return new LifecycleSendReservationResult(
                            null,
                            LifecycleSendBlockReason.Stale);
                    }

                    if (!shouldEmit())
                    {
                        _latestCommitted = true;
                        return new LifecycleSendReservationResult(
                            null,
                            LifecycleSendBlockReason.Dedupe);
                    }

                    if (tryConsumeRate is not null && !tryConsumeRate())
                    {
                        // The dedupe reservation is intentionally retained on the
                        // rate-blocked path: the rate block sets _latestCommitted,
                        // which fences a subsequent same-version dispatch through
                        // TryObserve, so the dedupe entry becomes unreachable and
                        // does not need to be rolled back.
                        _latestCommitted = true;
                        return new LifecycleSendReservationResult(
                            null,
                            LifecycleSendBlockReason.RateLimit);
                    }
                }

                return new LifecycleSendReservationResult(
                    new LifecycleSendReservation(
                        version,
                        expectedSnapshot,
                        isResolution,
                        requiresLifecycleCommit: !alreadyStarted,
                        rollbackDedupe,
                        rollbackRate),
                    LifecycleSendBlockReason.None);
            }
        }

        /// <summary>
        /// Atomically verifies and commits a real provider boundary after the
        /// sender has completed its preparation. No awaited work runs while
        /// this lock is held. On success for a non-resolution reservation,
        /// <paramref name="lineage"/> is the (lazily-created-or-reused)
        /// shared <see cref="AttentionDeliveryLineage"/> the caller's
        /// <see cref="PendingTransportAttempt"/> was registered against —
        /// the caller must settle against THIS instance later, not whatever
        /// <c>_lineage</c> may have become by then.
        /// </summary>
        public bool TryStartTransport(
            LifecycleSendReservation reservation,
            PendingTransportAttempt attempt,
            out AttentionDeliveryLineage? lineage)
        {
            lock (_sync)
            {
                lineage = null;
                if (!reservation.IsPending
                    || _retired
                    || !_hasVersion
                    || reservation.Version != _latest)
                {
                    return false;
                }

                if (reservation.RequiresLifecycleCommit)
                {
                    if ((reservation.IsResolution
                            && !ReferenceEquals(_snapshot, reservation.ExpectedSnapshot))
                        || (!reservation.IsResolution && _latestCommitted))
                    {
                        return false;
                    }
                }
                else if (!IsCurrentUnderLock(
                             reservation.Version,
                             reservation.ExpectedSnapshot,
                             reservation.IsResolution))
                {
                    return false;
                }

                if (!reservation.TryMarkStarted())
                {
                    return false;
                }

                if (reservation.RequiresLifecycleCommit)
                {
                    _latestCommitted = true;
                    if (reservation.IsResolution)
                    {
                        _snapshot = null;
                        _consumedResolutionVersion = reservation.Version;

                        // Detach the lineage here — the resolution's own
                        // reservation just won the race to actually commit
                        // (cross the transport-start boundary for the first
                        // device), the same moment _snapshot above is
                        // nulled. A later, strictly-newer Created (a fresh
                        // recurrence) will lazily create its own lineage
                        // below instead of inheriting this one's
                        // pending/delivered state. Until this commit
                        // happens, TryObserve's capture (above) keeps
                        // re-reading this SAME instance on every accepted
                        // Resolved observation — including an exact-version
                        // retry after an earlier attempt was cancelled or
                        // skipped before ever reaching this point.
                        _lineage = null;
                    }
                    else
                    {
                        _snapshot = reservation.ExpectedSnapshot;
                        _consumedResolutionVersion = null;

                        // Reuse the SAME lineage across a snapshot
                        // replacement (S1 -> S2) for this still-unresolved
                        // occurrence — created only once, on the first
                        // generation since the last consumed resolution (or
                        // since this lifecycle's inception). This is the
                        // Hicks blocker 1 fix: a still-pending older
                        // generation's attempt (and its eventual late
                        // success) stays reachable through this shared
                        // instance instead of being silently dropped when a
                        // newer generation becomes "current".
                        _lineage ??= new AttentionDeliveryLineage();
                    }
                }

                if (!reservation.IsResolution)
                {
                    lineage = _lineage;
                    lineage!.RegisterStartedAttempt(attempt);
                }

                return true;
            }
        }

        public void RollbackReservation(LifecycleSendReservation reservation)
        {
            if (!reservation.TryBeginRollback())
            {
                return;
            }

            lock (_sync)
            {
                if (reservation.RequiresLifecycleCommit
                    && !_retired
                    && _hasVersion
                    && reservation.Version == _latest)
                {
                    _latestCommitted = false;
                }
            }

            reservation.RollbackExternalReservations();
        }

        public bool IsCurrent(
            AttentionDispatchVersion version,
            AttentionSnapshot? expectedSnapshot,
            bool isResolution)
        {
            lock (_sync)
            {
                return IsCurrentUnderLock(version, expectedSnapshot, isResolution);
            }
        }

        /// <summary>
        /// Returns true if a strictly-newer version has been observed on this
        /// lifecycle since the caller's version. Used by the per-device sync
        /// sender-exception catch to distinguish "lifecycle superseded" (real
        /// supersession — the entire fan-out must stop) from "pre-transport
        /// failure" (no snapshot was ever committed, no supersession — the
        /// sibling devices in the same fan-out must still be attempted).
        /// A retired lifecycle is treated as not superseded so the caller
        /// falls through to its normal not-current handling.
        /// </summary>
        public bool IsSupersededBy(AttentionDispatchVersion version)
        {
            lock (_sync)
            {
                return !_retired
                    && _hasVersion
                    && version.CompareTo(_latest) < 0;
            }
        }

        /// <summary>
        /// Atomically validates the caller's (version, snapshot) still owns
        /// persisted result attribution. Provider settlement marks delivery on
        /// its snapshot before this method runs, allowing a concurrent
        /// resolution to observe a late success even if it fences persistence.
        /// </summary>
        public bool TryClaimAttribution(
            AttentionDispatchVersion version,
            AttentionSnapshot? expectedSnapshot,
            bool isResolution)
        {
            lock (_sync)
            {
                return IsCurrentUnderLock(version, expectedSnapshot, isResolution);
            }
        }

        public void Complete(AttentionDispatchVersion version)
        {
            lock (_sync)
            {
                if (_activeVersions.Remove(version))
                {
                    _totalParticipants--;
                }
            }
        }

        public bool TryRetire(DateTime cutoffUtc)
        {
            lock (_sync)
            {
                if (_retired
                    || _totalParticipants != 0
                    || _lastObservedAtUtc >= cutoffUtc
                    || (_snapshot is not null && _snapshot.CapturedAtUtc >= cutoffUtc))
                {
                    return false;
                }

                _retired = true;
                return true;
            }
        }

        private bool IsCurrentUnderLock(
            AttentionDispatchVersion version,
            AttentionSnapshot? expectedSnapshot,
            bool isResolution)
        {
            return !_retired
                && _hasVersion
                && version == _latest
                && (isResolution
                    ? _consumedResolutionVersion == version
                    : ReferenceEquals(_snapshot, expectedSnapshot));
        }
    }

    private sealed class PendingTransportAttempt
    {
        private readonly TaskCompletionSource _settled =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Settlement => _settled.Task;

        public void Complete() => _settled.TrySetResult();
    }

    private sealed class ResolutionCapture(
        AttentionSnapshot snapshot,
        Task[] pendingSettlements,
        AttentionDeliveryLineage lineage)
    {
        public AttentionSnapshot Snapshot { get; } = snapshot;

        /// <summary>
        /// True once ANY device attempt across every generation this
        /// lineage ever spanned (not just the most recently active
        /// snapshot) achieved a successful delivery. Must only be read
        /// AFTER <see cref="WaitForPendingTransportsAsync"/> completes, so a
        /// late success from an inherited, already-superseded generation is
        /// visible before the resolution decides whether to dismiss.
        /// </summary>
        public bool HasSuccessfulDelivery => lineage.HasSuccessfulDelivery;

        public Task WaitForPendingTransportsAsync(
            Action? onWaitStarted,
            CancellationToken cancellationToken)
        {
            if (pendingSettlements.Length == 0)
            {
                return Task.CompletedTask;
            }

            onWaitStarted?.Invoke();
            return Task.WhenAll(pendingSettlements).WaitAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Shared pending-attempt/delivery lineage for one unresolved attention
    /// occurrence — every Created/Updated generation between the occurrence's
    /// first dispatch and its next committed Resolved. <see
    /// cref="AttentionLifecycle"/> creates exactly one instance per
    /// occurrence (see <see cref="AttentionLifecycle.TryStartTransport"/>)
    /// and REUSES it — never replaces it — across every later snapshot
    /// generation for that same occurrence, so a still-pending older
    /// generation's provider attempt (and its eventual late success) remains
    /// reachable after a newer generation becomes "current" (Hicks blocker
    /// 1). <see cref="AttentionLifecycle"/> detaches its reference to this
    /// instance only once a resolution's own reservation actually commits in
    /// <see cref="AttentionLifecycle.TryStartTransport"/> — the same moment
    /// it nulls its snapshot pointer — so a resolution merely observed (and
    /// then cancelled or otherwise never reaching that commit) leaves this
    /// instance in place for an exact-version retry to find again, while a
    /// subsequent fresh occurrence still starts with a brand-new instance
    /// instead of inheriting stale delivered/pending state.
    /// </summary>
    private sealed class AttentionDeliveryLineage
    {
        private readonly object _sync = new();
        private readonly HashSet<PendingTransportAttempt> _pendingAttempts = [];
        private int _hasSuccessfulDelivery;

        /// <summary>True once at least one device attempt anywhere in this lineage succeeded.</summary>
        public bool HasSuccessfulDelivery => Volatile.Read(ref _hasSuccessfulDelivery) != 0;

        public void MarkDelivered() => Interlocked.Exchange(ref _hasSuccessfulDelivery, 1);

        public void RegisterStartedAttempt(PendingTransportAttempt attempt)
        {
            lock (_sync)
            {
                _ = _pendingAttempts.Add(attempt);
            }
        }

        public void SettleStartedAttempt(
            PendingTransportAttempt attempt,
            bool wasSuccessful)
        {
            if (wasSuccessful)
            {
                MarkDelivered();
            }

            lock (_sync)
            {
                _ = _pendingAttempts.Remove(attempt);
            }

            attempt.Complete();
        }

        /// <summary>
        /// Stable, point-in-time read of the settlement tasks for every
        /// attempt currently pending across every generation this lineage
        /// has ever spanned — not only the most recently active snapshot.
        /// Has no fencing side effect and is safe to call more than once:
        /// an exact-version retry of a resolution that was cancelled (or
        /// otherwise never reached <see
        /// cref="AttentionLifecycle.TryStartTransport"/>'s commit) before
        /// this lineage was detached calls this again and must see a live,
        /// still-accurate view.
        /// </summary>
        public Task[] CapturePendingSettlements()
        {
            lock (_sync)
            {
                return _pendingAttempts.Count == 0
                    ? Array.Empty<Task>()
                    : _pendingAttempts.Select(attempt => attempt.Settlement).ToArray();
            }
        }
    }

    // Immutable per-generation routing data (kind/printer/job/toolhead) plus
    // the timestamp used for TTL pruning. Pending-attempt tracking and the
    // cross-generation "has this occurrence ever delivered" flag live on
    // AttentionDeliveryLineage, not here — a snapshot's identity is still
    // used for per-device dedupe/staleness reference-equality checks
    // (AttentionLifecycle.TryReserveSend / IsCurrentUnderLock), but a
    // snapshot being superseded must never lose track of attempts that were
    // already started against it (Hicks blocker 1).
    private sealed class AttentionSnapshot
    {
        public AttentionSnapshot(
            AttentionKind kind,
            Guid printerId,
            Guid? jobId,
            int? toolheadIndex,
            DateTime capturedAtUtc)
        {
            Kind = kind;
            PrinterId = printerId;
            JobId = jobId;
            ToolheadIndex = toolheadIndex;
            CapturedAtUtc = capturedAtUtc;
        }

        public AttentionKind Kind { get; }

        public Guid PrinterId { get; }

        public Guid? JobId { get; }

        public int? ToolheadIndex { get; }

        public DateTime CapturedAtUtc { get; }
    }

    private bool TryConsumeRate(RateLimitKey key, NativePushSettings settings, DateTime nowUtc)
    {
        if (settings.RateLimitPerUser <= 0)
        {
            return true;
        }

        // Retry loop guards Hicks v3 blocker 3: if PruneCaches removed a
        // bucket concurrently we must NOT record a timestamp on the dead
        // instance (would be silently discarded and cost the user a slot).
        // Instead we spin — bounded by the pruner's per-call cadence.
        while (true)
        {
            RateLimitBucket bucket = _rateLimits.GetOrAdd(key, _ => new RateLimitBucket());
            lock (bucket)
            {
                if (bucket.IsDead)
                {
                    // Prune already removed us from the dict; loop and let
                    // GetOrAdd insert a fresh live bucket.
                    continue;
                }

                DateTime cutoff = nowUtc - settings.RateLimitWindow;
                bucket.Timestamps.RemoveAll(t => t < cutoff);
                if (bucket.Timestamps.Count >= settings.RateLimitPerUser)
                {
                    return false;
                }

                bucket.Timestamps.Add(nowUtc);
                return true;
            }
        }
    }

    private void RollbackRate(RateLimitKey key, DateTime consumedAtUtc)
    {
        // Roll back a rate reservation that was atomically consumed but whose
        // startSend threw synchronously. Removing the first matching timestamp
        // returns the user's slot; retries via a subsequent DispatchAsync do
        // not lose capacity to the failed pre-transport attempt.
        if (_rateLimits.TryGetValue(key, out RateLimitBucket? bucket))
        {
            lock (bucket)
            {
                if (!bucket.IsDead)
                {
                    bucket.Timestamps.Remove(consumedAtUtc);
                }
            }
        }
    }

    // The rate-bucket eviction TTL has always normalised a non-positive
    // configured window to five minutes (the historical PruneCaches default).
    // Centralised here so every prune call site — the normal path, the
    // disabled/no-op finally, and the retained last-valid snapshot — evicts on
    // identical semantics, and a misconfigured non-positive window can never
    // collapse the eviction cutoff onto "now".
    private static TimeSpan EffectiveRateLimitWindow(NativePushSettings settings)
    {
        return settings.RateLimitWindow > TimeSpan.Zero
            ? settings.RateLimitWindow
            : TimeSpan.FromMinutes(5);
    }

    private void RememberRateLimitWindow(NativePushSettings settings)
    {
        // Retain the authoritative last-valid eviction window so a later options
        // read failure can still prune rate buckets on the configured cadence
        // instead of a fabricated default (504dfb15 blocker 2). Interlocked
        // mirrors the sibling _lastPruneAtTicks so this 64-bit value is written
        // and read atomically on every runtime.
        _ = Interlocked.Exchange(
            ref _lastValidRateLimitWindowTicks,
            EffectiveRateLimitWindow(settings).Ticks);
    }

    private TimeSpan? TryGetLastValidRateLimitWindow()
    {
        long ticks = Interlocked.Read(ref _lastValidRateLimitWindowTicks);
        return ticks > 0 ? TimeSpan.FromTicks(ticks) : null;
    }

    private void PruneCaches(DateTime nowUtc, TimeSpan? rateLimitWindow)
    {
        // Rate-limit prune to at most once every 30s so an alert storm cannot force an
        // O(N) sweep on every dispatch (bucket count may stay > threshold while every
        // entry is still within its dedupe window).
        long lastTicks = Interlocked.Read(ref _lastPruneAtTicks);
        var lastPruneAt = new DateTime(lastTicks, DateTimeKind.Utc);
        if (nowUtc - lastPruneAt < TimeSpan.FromSeconds(30))
        {
            return;
        }

        // Only one caller wins the compare-exchange; concurrent callers skip.
        if (Interlocked.CompareExchange(ref _lastPruneAtTicks, nowUtc.Ticks, lastTicks) != lastTicks)
        {
            return;
        }

        foreach (KeyValuePair<string, DateTime> kv in _dedupe)
        {
            if (kv.Value <= nowUtc)
            {
                // Value-comparing overload: if a concurrent writer refreshed the
                // entry (later expiry) between snapshot and remove, do NOT drop
                // it. Prevents a race where prune wipes an in-window dedupe key.
                _ = ((ICollection<KeyValuePair<string, DateTime>>)_dedupe).Remove(kv);
            }
        }

        if (rateLimitWindow is TimeSpan evictAfter)
        {
            // Bucket-side prune: drop rate-limit entries that have gone stale so the
            // dictionary does not hold state for every user who ever received a push.
            //
            // Hicks v3 blocker 3: we must hold the bucket's own lock across BOTH
            // the emptiness check AND the dictionary Remove call. Otherwise a
            // concurrent TryConsumeRate could add a timestamp between our
            // decision and our Remove, and that timestamp would be silently
            // discarded when the bucket instance drops out of the dict. We also
            // set an IsDead flag so racing TryConsumeRate calls that already
            // fetched the doomed bucket via GetOrAdd can detect and retry.
            //
            // The eviction TTL honours the last successfully-read configured
            // RateLimitWindow. If options acquisition failed before any valid
            // snapshot existed, this entire settings-dependent section is skipped.
            foreach (KeyValuePair<RateLimitKey, RateLimitBucket> kv in _rateLimits)
            {
                lock (kv.Value)
                {
                    DateTime cutoff = nowUtc - evictAfter;
                    kv.Value.Timestamps.RemoveAll(t => t < cutoff);
                    bool empty = kv.Value.Timestamps.Count == 0;
                    if (!empty)
                    {
                        continue;
                    }

                    // Mark dead BEFORE removing from the dict so any thread that
                    // grabbed this instance via GetOrAdd sees IsDead=true after
                    // it acquires the lock and will retry with a fresh bucket.
                    kv.Value.IsDead = true;
                    _ = ((ICollection<KeyValuePair<RateLimitKey, RateLimitBucket>>)_rateLimits).Remove(kv);
                }
            }
        }

        // Keep lifecycle snapshots and tombstones long enough for real attention
        // lifetimes while bounding entries whose source never emits Resolved.
        DateTime snapshotCutoff = nowUtc - AttentionSnapshotTtl;
        foreach (KeyValuePair<AttentionSnapshotKey, AttentionLifecycle> kv in _attentionLifecycles)
        {
            if (kv.Value.TryRetire(snapshotCutoff))
            {
                _ = ((ICollection<KeyValuePair<AttentionSnapshotKey, AttentionLifecycle>>)_attentionLifecycles)
                    .Remove(kv);
                if (_attentionItemFences.TryGetValue(
                        kv.Key.AttentionItemId,
                        out AttentionItemFence? fence))
                {
                    fence.UntrackLifecycle(kv.Key.UserId, kv.Value);
                }
            }
        }

        // Lifecycle tombstones reject delayed work for the same seven-day window as
        // delivery snapshots. Retirement and dictionary removal are coordinated so
        // a racing observer retries against a live replacement lane.
        foreach (KeyValuePair<AttentionDispatchKey, AttentionDispatchLane> kv in _attentionDispatchLanes)
        {
            if (kv.Value.TryRetire(snapshotCutoff))
            {
                _ = ((ICollection<KeyValuePair<AttentionDispatchKey, AttentionDispatchLane>>)_attentionDispatchLanes)
                    .Remove(kv);
            }
        }

        // Item fences use the same seven-day retention as snapshots/lanes so
        // a stale targeted dispatch delayed by up to that window is still
        // rejected. TryRetire is decided under the fence's own lock, and a
        // racing publish/admission that touches the fence between the
        // enumeration snapshot and this removal is handled by the fence's
        // own retry-on-Retired loops (TryObserveLifecycle,
        // PublishResolvedTombstoneAndFenceLifecycles), which retry against a
        // fresh replacement fence rather than operate on the orphaned one.
        foreach (KeyValuePair<string, AttentionItemFence> kv in _attentionItemFences)
        {
            if (kv.Value.TryRetire(snapshotCutoff))
            {
                _ = ((ICollection<KeyValuePair<string, AttentionItemFence>>)_attentionItemFences)
                    .Remove(kv);
            }
        }
    }

    private sealed class RateLimitBucket
    {
        public List<DateTime> Timestamps { get; } = new(32);

        // Set under the bucket's own lock during pruning to signal
        // concurrent TryConsumeRate callers that this instance is no longer
        // registered in the dictionary and they must retry.
        public bool IsDead { get; set; }
    }

    // Hicks H2-v5-final rate-limit scope key. Rate-limiting per (user,
    // printer, kind) means a noisy printer/kind cannot silence unrelated
    // critical alerts for the same user, and multi-device users no longer
    // consume the bucket faster than single-device users.
    private readonly record struct RateLimitKey(Guid UserId, Guid PrinterId, AttentionKind Kind);

    // Maps the internal <see cref="AttentionKind"/> to the shared web
    // preference PushOn{Kind} bool exposed via #716's operator matrix. If
    // the user has no persisted row we fall through to CLR defaults on
    // <see cref="NotificationPreferences"/> which are `true` for push —
    // matching pre-#708 opt-in behaviour.
    private static bool IsPushEnabledForKind(NotificationPreferences? prefs, AttentionKind kind)
    {
        if (prefs is null)
        {
            return true;
        }

        return kind switch
        {
            AttentionKind.Failure => prefs.PushOnPrinterFailure,
            AttentionKind.Runout => prefs.PushOnFilamentRunout,
            AttentionKind.Harvest => prefs.PushOnHarvestReady,
            AttentionKind.Maintenance => prefs.PushOnMaintenanceDue,
            AttentionKind.Offline => prefs.PushOnPrinterOffline,
            _ => true,
        };
    }
}
