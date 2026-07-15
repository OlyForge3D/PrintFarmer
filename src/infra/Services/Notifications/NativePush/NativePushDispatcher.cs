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

    // Global-resolution item tombstone. When a Resolved with targetUserId=null
    // is observed, we record the resolved item's version even if no owner
    // currently holds a targeted lifecycle (temporarily tokenless recipients,
    // never-dispatched recipients). Any strictly-older subsequent dispatch —
    // targeted or global — for the same item is fenced here before it can
    // install a fresh per-user lifecycle at a stale version. This closes the
    // tokenless-recipient re-registration gap without globally serialising
    // unrelated (recipient, item) keys: the tombstone is item-scoped only,
    // authorization is unchanged, and per-recipient lifecycles still manage
    // their own version ordering for non-stale work.
    private readonly ConcurrentDictionary<string, AttentionDispatchVersion> _resolvedItemVersions
        = new(StringComparer.Ordinal);

    private static readonly TimeSpan AttentionSnapshotTtl = TimeSpan.FromDays(7);
    private static readonly TimeSpan InformationalAlertTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ActionableAlertTtl = TimeSpan.FromMinutes(30);

    private long _lastPruneAtTicks;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly INativePushSender _sender;
    private readonly IOptionsMonitor<NativePushSettings> _optionsMonitor;
    private readonly NativePushMetrics _metrics;
    private readonly ILogger<NativePushDispatcher> _logger;
    private readonly TimeProvider _timeProvider;

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
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
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

        // Global-resolution item tombstone check. If a strictly-newer global
        // Resolved has already been observed for this item, reject any older
        // subsequent dispatch here — even for recipients whose per-user
        // lifecycles have not yet been installed (temporarily tokenless
        // recipients that later re-register). This must happen BEFORE the
        // dispatch lane / lifecycle observers, otherwise a stale targeted
        // Created would install a brand-new per-user lifecycle at the stale
        // version and deliver the alert on the re-registered device.
        if (_resolvedItemVersions.TryGetValue(attentionItemId, out AttentionDispatchVersion resolvedVersion)
            && version.CompareTo(resolvedVersion) < 0)
        {
            return;
        }

        // Record the tombstone for a global Resolved observation BEFORE any
        // per-owner work. The tombstone is item-scoped, so recording it
        // early makes the version fence visible to concurrent stale
        // dispatches even while owner enumeration or targeted fan-out is
        // still in flight. Targeted resolutions install per-user lifecycle
        // tombstones and do not need to broadcast an item-wide fence.
        if (changeKind == AttentionChangeKind.Resolved && targetUserId is null)
        {
            _resolvedItemVersions.AddOrUpdate(
                attentionItemId,
                version,
                (_, existing) => version.CompareTo(existing) > 0 ? version : existing);
        }

        if (!TryObserveDispatch(dispatchKey, version, out AttentionDispatchLane lane))
        {
            return;
        }

        bool entered = false;
        try
        {
            await lane.Gate.WaitAsync(cancellationToken);
            entered = true;
            if (!lane.IsLatest(version))
            {
                return;
            }

            await DispatchCoreAsync(
                attentionItemId,
                changeKind,
                targetUserId,
                version,
                cancellationToken);
        }
        finally
        {
            if (entered)
            {
                lane.Gate.Release();
            }

            lane.Complete();
        }
    }

    private async Task DispatchCoreAsync(
        string attentionItemId,
        AttentionChangeKind changeKind,
        Guid? targetUserId,
        AttentionDispatchVersion version,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(attentionItemId))
        {
            return;
        }

        // Snapshot the startup-bound settings for a consistent fan-out. The
        // NativePush section is validated with ValidateOnStart; configuration
        // changes require a process restart rather than taking effect mid-flight.
        // Resolved events consume a per-user pre-resolution snapshot and emit a
        // silent dismissal even after the source removes the live item.
        NativePushSettings settings = _optionsMonitor.CurrentValue;
        if (settings.Mode == NativePushMode.Disabled)
        {
            return;
        }

        PruneCaches(UtcNow, settings);

        try
        {
            await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
            IServiceProvider sp = scope.ServiceProvider;
            IOperatorFeatureGate gate = sp.GetRequiredService<IOperatorFeatureGate>();
            if (!gate.IsEnabled(OperatorFeature.NativePush))
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
                if (changeKind == AttentionChangeKind.Resolved)
                {
                    // A global resolution must tombstone every recipient that has an
                    // active lifecycle for this attention item, not only recipients
                    // that currently hold device tokens. A temporarily tokenless
                    // recipient (device unregistered between an earlier targeted
                    // Created/Updated capture and this resolution) still owns an
                    // in-flight targeted lane; without this union, a later
                    // re-registration would let that stale targeted work resume and
                    // send after resolution. DispatchForOwnerAsync advances the
                    // lifecycle inside TryObserveLifecycle before the token check,
                    // so a tokenless recipient still receives the version tombstone.
                    List<Guid>? lifecycleOwners = GetOwnersWithLifecycleFor(attentionItemId);
                    if (lifecycleOwners is { Count: > 0 })
                    {
                        var union = new HashSet<Guid>(activeOwners);
                        foreach (Guid owner in lifecycleOwners)
                        {
                            union.Add(owner);
                        }

                        owners = union.ToArray();
                    }
                    else
                    {
                        owners = activeOwners;
                    }
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

            foreach (Guid userId in owners)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Re-check the gate on every recipient so a mid-flight flip stops sends.
                if (!gate.IsEnabled(OperatorFeature.NativePush))
                {
                    _metrics.SkippedFeatureDisabled.Add(1);
                    return;
                }

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
            _logger.LogWarning(ex, "[NativePush] Dispatch failed for attentionItemId={AttentionItemId}", attentionItemId);
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
        CancellationToken cancellationToken)
    {
        var snapshotKey = new AttentionSnapshotKey(userId, attentionItemId);
        if (!TryObserveLifecycle(
                snapshotKey,
                version,
                changeKind,
                out AttentionLifecycle lifecycle,
                out AttentionSnapshot? resolvedSnapshot))
        {
            return;
        }

        try
        {
            AttentionItemDto? item = await attention.FindItemAsync(
                userId,
                attentionItemId,
                cancellationToken);
            AttentionSnapshot? activeSnapshot = null;

            if (changeKind == AttentionChangeKind.Resolved)
            {
                if (resolvedSnapshot is null)
                {
                    return;
                }

                // #756 semantic on the lifecycle-owned architecture: this
                // recipient's alert generation exhausted every device without
                // a single successful delivery (all-transient outage, terminal
                // failures, or invalidations). The client never received the
                // alert this dismissal would clear, so treat the dismissal as
                // a benign no-op rather than send a silent push for an alert
                // that was never seen. The read is safe here because the
                // matching MarkDelivered call happens under the same lifecycle
                // sync as TryObserve's consumption of _snapshot: any success
                // that races with this Resolved is either fenced (its version
                // is no longer current) or was applied before TryObserve
                // returned this snapshot, and the Volatile.Read publishes it.
                // Partial success (at least one device delivered) still emits
                // the dismissal to every current device below, preserving
                // per-recipient behavior.
                if (!resolvedSnapshot.HasSuccessfulDelivery)
                {
                    _metrics.SkippedNeverDelivered.Add(1);
                    return;
                }

                item = null;
            }
            else if (item is null)
            {
                return;
            }

            AttentionKind kind = item?.Kind ?? resolvedSnapshot!.Kind;
            Guid printerId = item?.PrinterId ?? resolvedSnapshot!.PrinterId;
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

                if (!gate.IsEnabled(OperatorFeature.NativePush))
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
                        resolvedSnapshot,
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
            lifecycle.Complete();
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
        NativePushDispatchResult? result;
        try
        {
            result = await SendWithRetriesAsync(
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

            // Pre-transport synchronous sender failure. TryBeginSend caught
            // the sync throw before committing _snapshot/_consumedResolutionVersion
            // and rolled back dedupe/rate reservations. IsCurrent therefore
            // returns false (no snapshot commit at THIS version), but the
            // lifecycle has NOT advanced past our version. This is a
            // per-device pre-transport failure, not a supersession: the
            // outer fan-out must continue to sibling devices in the same
            // dispatch, and a subsequent exact-version DispatchAsync must
            // be able to retry every synchronously-failed device.
            //
            // Distinguish that state from the post-transport-start
            // fenced-current case, which routes through the sender_exception
            // transient result and follows normal token attribution.
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
            result = NativePushDispatchResult.Transient("sender_exception");
        }

        if (result is null)
        {
            return DeviceDispatchOutcome.DispatchStopped;
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
        out AttentionSnapshot? resolvedSnapshot)
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

        while (true)
        {
            lifecycle = _attentionLifecycles.GetOrAdd(
                key,
                static _ => new AttentionLifecycle());
            AttentionLifecycleObserveResult result = lifecycle.TryObserve(
                version,
                changeKind,
                UtcNow,
                onLifecycleAdvancedOrResolved,
                out resolvedSnapshot);
            if (result == AttentionLifecycleObserveResult.Accepted)
            {
                return true;
            }

            if (result == AttentionLifecycleObserveResult.Stale)
            {
                return false;
            }

            // A pruner retired this lifecycle after GetOrAdd returned it. Retry
            // against the replacement rather than updating orphaned state.
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

    private async Task<NativePushDispatchResult?> SendWithRetriesAsync(
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
        NativePushDispatchResult last = NativePushDispatchResult.Transient("no_attempt");
        bool attempted = false;
        for (int i = 0; i < attempts; i++)
        {
            // This persisted gate read is intentionally adjacent to the transport call.
            // A retry must never inherit the enabled decision made by an earlier attempt.
            if (!gate.IsEnabled(OperatorFeature.NativePush))
            {
                _metrics.SkippedFeatureDisabled.Add(1);
                return null;
            }

            bool transportStarted = false;
            try
            {
                // Track dedupe / rate reservations for rollback if startSend
                // throws synchronously. A synchronous throw means transport
                // never truly started; rolling back the reservations lets an
                // exact-version retry via a subsequent DispatchAsync proceed.
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
                        ((ICollection<KeyValuePair<string, DateTime>>)_dedupe)
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
                Func<Task<NativePushDispatchResult>> startSend = () =>
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

                    return _sender.SendAsync(envelope, cancellationToken);
                };
                LifecycleSendStart sendStart = lifecycle.TryBeginSend(
                    version,
                    lifecycleSnapshot,
                    changeKind == AttentionChangeKind.Resolved,
                    shouldEmit,
                    rollbackDedupe,
                    tryConsumeRate,
                    rollbackRate,
                    startSend);
                if (sendStart.BlockReason == LifecycleSendBlockReason.Dedupe)
                {
                    _metrics.SkippedDedupe.Add(1);
                    return null;
                }

                if (sendStart.BlockReason == LifecycleSendBlockReason.RateLimit)
                {
                    _metrics.SkippedRateLimit.Add(1);
                    return null;
                }

                Task<NativePushDispatchResult>? sendTask = sendStart.SendTask;
                if (sendTask is null)
                {
                    return null;
                }

                transportStarted = true;
                last = await sendTask;
            }
            finally
            {
                if (transportStarted && !attempted)
                {
                    _metrics.Attempted.Add(1);
                    attempted = true;
                }
            }

            // A sender may complete after an administrator has committed the
            // emergency disable or after a resolution/replacement consumed this
            // alert generation. Discard that provider result before retry or any
            // result attribution.
            cancellationToken.ThrowIfCancellationRequested();
            if (!gate.IsEnabled(OperatorFeature.NativePush))
            {
                _metrics.SkippedFeatureDisabled.Add(1);
                return null;
            }

            if (!lifecycle.IsCurrent(
                    version,
                    lifecycleSnapshot,
                    changeKind == AttentionChangeKind.Resolved))
            {
                return null;
            }

            if (last.Success || last.TokenInvalidated || !last.IsTransient)
            {
                return last;
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

        return last;
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
        if (!gate.IsEnabled(OperatorFeature.NativePush))
        {
            _metrics.SkippedFeatureDisabled.Add(1);
            return DeviceDispatchOutcome.DispatchStopped;
        }

        // Final attribution claim. On delivery success this must atomically
        // record "this generation has been delivered to at least one device"
        // under the same lifecycle lock that a racing Resolved uses to
        // consume the snapshot. Otherwise a Resolved could observe the same
        // snapshot instance between our IsCurrent check and MarkDelivered
        // and wrongly conclude the alert was never delivered (#756).
        if (!lifecycle.TryClaimAttribution(version, activeSnapshot, isResolution, result.Success))
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
        private AttentionDispatchVersion _latest;
        private DateTime _lastObservedAtUtc;
        private int _participants;
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
                if (versionOrder < 0 || (versionOrder == 0 && _participants != 0))
                {
                    return AttentionDispatchObserveResult.Stale;
                }

                if (versionOrder > 0)
                {
                    _latest = version;
                }

                _lastObservedAtUtc = observedAtUtc;
                _hasVersion = true;
                _participants++;
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

        public void Complete()
        {
            lock (_sync)
            {
                _participants--;
            }
        }

        public bool TryRetire(DateTime cutoffUtc)
        {
            lock (_sync)
            {
                if (_retired || _participants != 0 || _lastObservedAtUtc >= cutoffUtc)
                {
                    return false;
                }

                _retired = true;
                return true;
            }
        }
    }

    private readonly record struct AttentionSnapshotKey(Guid UserId, string AttentionItemId);

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

    private readonly record struct LifecycleSendStart(
        Task<NativePushDispatchResult>? SendTask,
        LifecycleSendBlockReason BlockReason);

    private sealed class AttentionLifecycle
    {
        private readonly object _sync = new();
        private AttentionDispatchVersion _latest;
        private AttentionDispatchVersion? _consumedResolutionVersion;
        private AttentionSnapshot? _snapshot;
        private DateTime _lastObservedAtUtc;
        private int _participants;
        private bool _hasVersion;
        private bool _latestCommitted;
        private bool _retired;

        public AttentionLifecycleObserveResult TryObserve(
            AttentionDispatchVersion version,
            AttentionChangeKind changeKind,
            DateTime observedAtUtc,
            Action? onLifecycleAdvancedOrResolved,
            out AttentionSnapshot? resolvedSnapshot)
        {
            lock (_sync)
            {
                resolvedSnapshot = null;
                if (_retired)
                {
                    return AttentionLifecycleObserveResult.Retired;
                }

                int versionOrder = _hasVersion ? version.CompareTo(_latest) : 1;
                if (versionOrder < 0
                    || (versionOrder == 0 && (_latestCommitted || _participants != 0)))
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
                _participants++;
                if (changeKind == AttentionChangeKind.Resolved)
                {
                    resolvedSnapshot = _snapshot;
                }

                // Fire under the sync lock so any concurrent newer occurrence's
                // TryBeginSend on this same lifecycle either observes an empty
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

        public LifecycleSendStart TryBeginSend(
            AttentionDispatchVersion version,
            AttentionSnapshot expectedSnapshot,
            bool isResolution,
            Func<bool> shouldEmit,
            Action rollbackDedupe,
            Func<bool>? tryConsumeRate,
            Action? rollbackRate,
            Func<Task<NativePushDispatchResult>> startSend)
        {
            lock (_sync)
            {
                if (_retired || !_hasVersion || version != _latest)
                {
                    return new LifecycleSendStart(null, LifecycleSendBlockReason.Stale);
                }

                bool alreadyStarted = isResolution
                    ? _consumedResolutionVersion == version
                    : ReferenceEquals(_snapshot, expectedSnapshot);
                bool reservedThisCall = false;
                if (!alreadyStarted)
                {
                    if ((isResolution && !ReferenceEquals(_snapshot, expectedSnapshot))
                        || (!isResolution && _latestCommitted))
                    {
                        return new LifecycleSendStart(null, LifecycleSendBlockReason.Stale);
                    }

                    if (!shouldEmit())
                    {
                        _latestCommitted = true;
                        return new LifecycleSendStart(null, LifecycleSendBlockReason.Dedupe);
                    }

                    if (tryConsumeRate is not null && !tryConsumeRate())
                    {
                        // The dedupe reservation is intentionally retained on the
                        // rate-blocked path: the rate block sets _latestCommitted,
                        // which fences a subsequent same-version dispatch through
                        // TryObserve, so the dedupe entry becomes unreachable and
                        // does not need to be rolled back.
                        _latestCommitted = true;
                        return new LifecycleSendStart(null, LifecycleSendBlockReason.RateLimit);
                    }

                    reservedThisCall = true;
                }

                Task<NativePushDispatchResult> sendTask;
                try
                {
                    sendTask = startSend();
                }
                catch (Exception ex)
                {
                    // Transport did not truly start. If this call reserved the
                    // dedupe / rate slots, roll them back and leave
                    // _latestCommitted false so an exact-version retry via a
                    // subsequent DispatchAsync can proceed. Stale provider
                    // results/retries remain fenced because no snapshot or
                    // resolution version was committed here.
                    if (reservedThisCall)
                    {
                        rollbackRate?.Invoke();
                        rollbackDedupe();
                    }

                    sendTask = Task.FromException<NativePushDispatchResult>(ex);
                    return new LifecycleSendStart(sendTask, LifecycleSendBlockReason.None);
                }

                if (!alreadyStarted)
                {
                    // Commit lifecycle ownership only after startSend returns a
                    // Task without throwing. From this point the send is
                    // considered to have handed off to transport; any later
                    // failure is an async result and follows the normal
                    // retry / result-attribution path.
                    _latestCommitted = true;
                    if (isResolution)
                    {
                        _snapshot = null;
                        _consumedResolutionVersion = version;
                    }
                    else
                    {
                        // #756 delivery inheritance: a later generation for the
                        // same recipient must not "forget" that an earlier
                        // Created/Updated already reached the client. If the
                        // next generation fails every retry, Resolved still
                        // owes that visible alert a dismissal push. Under
                        // _sync, atomically transfer the delivered bit from
                        // the displaced snapshot to the incoming one.
                        if (_snapshot is not null && _snapshot.HasSuccessfulDelivery)
                        {
                            expectedSnapshot.MarkDelivered();
                        }

                        _snapshot = expectedSnapshot;
                        _consumedResolutionVersion = null;
                    }
                }

                return new LifecycleSendStart(sendTask, LifecycleSendBlockReason.None);
            }
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
        /// Atomically validates the caller's (version, snapshot) is still the
        /// current attribution ownership AND, on delivery success, marks that
        /// snapshot as having achieved at least one successful device delivery.
        /// Both decisions must be one lock hold: a racing Resolved's
        /// TryObserve otherwise could consume the same snapshot instance
        /// between the ownership check and the mark, and its
        /// !HasSuccessfulDelivery gate would then wrongly suppress a
        /// dismissal for an alert the client actually received (#756).
        /// Returns true when the caller retains ownership; the caller then
        /// performs its persistence/attribution work. Returns false when a
        /// resolution/replacement has already fenced this attribution.
        /// </summary>
        public bool TryClaimAttribution(
            AttentionDispatchVersion version,
            AttentionSnapshot? expectedSnapshot,
            bool isResolution,
            bool wasSuccessfulDelivery)
        {
            lock (_sync)
            {
                if (!IsCurrentUnderLock(version, expectedSnapshot, isResolution))
                {
                    return false;
                }

                if (wasSuccessfulDelivery && !isResolution && expectedSnapshot is not null)
                {
                    expectedSnapshot.MarkDelivered();
                }

                return true;
            }
        }

        public void Complete()
        {
            lock (_sync)
            {
                _participants--;
            }
        }

        public bool TryRetire(DateTime cutoffUtc)
        {
            lock (_sync)
            {
                if (_retired
                    || _participants != 0
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

    // Mutable delivery bit protected by the enclosing AttentionLifecycle's
    // _sync lock. The lifecycle serializes every observe/begin-send/attribution
    // transition, so any mark and any read of HasSuccessfulDelivery that
    // matters for a Resolved's dismissal decision is happens-before-ordered
    // through that lock. Interlocked/Volatile is used only for defensive
    // publication of the flag itself. #756 semantics on the lifecycle-owned
    // architecture (issue #755 decision).
    private sealed class AttentionSnapshot
    {
        private int _hasSuccessfulDelivery;

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

        /// <summary>True once at least one device attempt for this alert generation succeeded.</summary>
        public bool HasSuccessfulDelivery => Volatile.Read(ref _hasSuccessfulDelivery) != 0;

        /// <summary>Marks this alert generation as having achieved at least one successful delivery.</summary>
        public void MarkDelivered() => Interlocked.Exchange(ref _hasSuccessfulDelivery, 1);
    }

    private List<Guid>? GetOwnersWithLifecycleFor(string attentionItemId)
    {
        // Enumerate users with a live AttentionLifecycle entry for this
        // attention item. Global resolution unions these with active token
        // owners so a temporarily tokenless recipient still receives the
        // lifecycle version tombstone. The dict is ConcurrentDictionary and
        // iterates a snapshot; retired entries the pruner has not yet removed
        // resolve themselves via TryObserve's Retired-retry path.
        List<Guid>? result = null;
        foreach (KeyValuePair<AttentionSnapshotKey, AttentionLifecycle> kv in _attentionLifecycles)
        {
            if (string.Equals(kv.Key.AttentionItemId, attentionItemId, StringComparison.Ordinal))
            {
                result ??= new List<Guid>();
                result.Add(kv.Key.UserId);
            }
        }

        return result;
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

    private void PruneCaches(DateTime nowUtc, NativePushSettings settings)
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
        // The eviction TTL now honours the configured RateLimitWindow rather
        // than a hard-coded 5 minutes — otherwise a longer configured window
        // (e.g., 30 minutes for slower environments) would evict buckets
        // while their timestamps were still relevant to rate decisions.
        TimeSpan evictAfter = settings.RateLimitWindow > TimeSpan.Zero
            ? settings.RateLimitWindow
            : TimeSpan.FromMinutes(5);
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

        // Keep lifecycle snapshots and tombstones long enough for real attention
        // lifetimes while bounding entries whose source never emits Resolved.
        DateTime snapshotCutoff = nowUtc - AttentionSnapshotTtl;
        foreach (KeyValuePair<AttentionSnapshotKey, AttentionLifecycle> kv in _attentionLifecycles)
        {
            if (kv.Value.TryRetire(snapshotCutoff))
            {
                _ = ((ICollection<KeyValuePair<AttentionSnapshotKey, AttentionLifecycle>>)_attentionLifecycles)
                    .Remove(kv);
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

        // Global-resolution item tombstones use the same seven-day retention
        // as snapshots/lanes so a stale targeted dispatch delayed by up to
        // that window is still rejected. Value-comparing Remove prevents a
        // race with a concurrent AddOrUpdate that bumped the tombstone to a
        // newer version between snapshot and remove.
        foreach (KeyValuePair<string, AttentionDispatchVersion> kv in _resolvedItemVersions)
        {
            if (kv.Value.OccurredAtUtc < snapshotCutoff)
            {
                _ = ((ICollection<KeyValuePair<string, AttentionDispatchVersion>>)_resolvedItemVersions)
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
