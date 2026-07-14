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

    private long _lastPruneAtTicks;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly INativePushSender _sender;
    private readonly IOptionsMonitor<NativePushSettings> _optionsMonitor;
    private readonly NativePushMetrics _metrics;
    private readonly ILogger<NativePushDispatcher> _logger;

    /// <summary>Constructs the dispatcher.</summary>
    public NativePushDispatcher(
        IServiceScopeFactory scopeFactory,
        INativePushSender sender,
        IOptionsMonitor<NativePushSettings> optionsMonitor,
        NativePushMetrics metrics,
        ILogger<NativePushDispatcher> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        _optionsMonitor = optionsMonitor ?? throw new ArgumentNullException(nameof(optionsMonitor));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task DispatchAsync(
        string attentionItemId,
        AttentionChangeKind changeKind,
        Guid? targetUserId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(attentionItemId))
        {
            return;
        }

        // Snapshot the current attention state BEFORE the async gate/scope work runs.
        // A Resolved event fires after the source has already dropped the item, so a
        // later FindItemAsync returns null and no dismissal push would ever be built.
        // Capturing here also freezes the mode for the whole fan-out; a mid-flight
        // mode flip is picked up on the NEXT dispatch (real-time rollback is a
        // separate control-plane concern, not a per-envelope one).
        NativePushSettings settings = _optionsMonitor.CurrentValue;
        if (settings.Mode == NativePushMode.Disabled)
        {
            return;
        }

        PruneCaches(DateTime.UtcNow, settings);

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
                owners = await tokens.GetActiveTokenOwnersAsync(cancellationToken);
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
                // Hicks #1: rethrow ANY OperationCanceledException — not just
                // ones whose Token matches the caller's — because an inner
                // linked/timeout cancellation still means "stop this
                // pipeline". A guarded catch (when caller.IsCancellationRequested)
                // would let a linked-CTS OCE fall through into the generic
                // Exception isolator below, which would swallow it and keep
                // dispatching. Unconditional rethrow closes that gap.
                try
                {
                    await DispatchForOwnerAsync(
                        userId,
                        attentionItemId,
                        changeKind,
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
            // Hicks #1: unconditional rethrow. A cancellation surfaced from
            // any inner token — caller, linked, or per-attempt timeout — must
            // propagate out of DispatchAsync so the caller (broadcaster)
            // observes it and can shut down cleanly. Guarding on the caller
            // token alone would swallow legitimate internal cancellations.
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
        NativePushSettings settings,
        IOperatorFeatureGate gate,
        IDeviceTokenRepository tokens,
        IAttentionService attention,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        AttentionItemDto? item = await attention.FindItemAsync(userId, attentionItemId, cancellationToken);
        if (item is null)
        {
            // Resolved change: the source has already dropped the row so a
            // targeted find returns null. Skip — the SignalR event already
            // invalidated in-app; a native "resolved" push is best-effort.
            // (Snapshot-and-dispatch of resolved items is a future
            // enhancement; see docs/OPERATOR_NATIVE_PUSH.md rollback notes.)
            return;
        }

        string? category = AttentionPushCategories.CategoryFor(item.Kind);
        if (category is null)
        {
            return;
        }

        // Role gate — maintenance items are admin-only. FindItemAsync does not
        // filter this by role, so the dispatcher must.
        if (item.Kind == AttentionKind.Maintenance)
        {
            bool isAdmin = await IsFarmAdminAsync(db, userId, cancellationToken);
            if (!isAdmin)
            {
                return;
            }
        }

        // Per-user category opt-out.
        NotificationPreferences? prefs = await db.NotificationPreferences
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserId == userId, cancellationToken);
        AttentionPushCategoryPreferences catPrefs = AttentionPushCategoryPreferences.FromJson(prefs?.AttentionPushCategoryPreferencesJson);
        if (!catPrefs.IsEnabled(item.Kind))
        {
            _metrics.SkippedCategoryOptOut.Add(1);
            return;
        }

        // Hicks v5 H1 master gate: a persisted preferences row with
        // EnablePushNotifications=false is the shared "no push at all"
        // opt-out; every attention native push MUST honour it. Missing
        // row falls back to CLR default (true) so the pre-#708 opt-in
        // behaviour is preserved for users who never touched the
        // preference UI. This gate runs BEFORE the per-kind check so
        // preserved PushOn{Kind} values cannot leak past a global
        // opt-out.
        if (prefs is not null && !prefs.EnablePushNotifications)
        {
            _metrics.SkippedCategoryOptOut.Add(1);
            return;
        }

        // Hicks v4 blocker 3: the shared web preference matrix exposes
        // per-kind push toggles (PushOnPrinterFailure / FilamentRunout /
        // HarvestReady / MaintenanceDue / PrinterOffline) that #716 uses
        // to opt out of native push per event type. Without gating here
        // the dispatcher would deliver to users who explicitly disabled
        // that row in the operator matrix. Missing prefs row falls back
        // to CLR defaults on NotificationPreferences (push=true), which
        // preserves the historical opt-in behaviour.
        if (!IsPushEnabledForKind(prefs, item.Kind))
        {
            _metrics.SkippedCategoryOptOut.Add(1);
            return;
        }

        IReadOnlyList<DeviceToken> userTokens = await tokens.GetActiveByUserAsync(userId, cancellationToken);
        if (userTokens.Count == 0)
        {
            return;
        }

        // Hicks H2-v5-final: rate-limit ONCE per logical event before device
        // fan-out. Scope is (userId, printerId, kind) so:
        //   * a noisy printer/kind cannot suppress unrelated critical alerts
        //     for the same user (previous per-user scope failed here);
        //   * a multi-device user does not exhaust their bucket faster than a
        //     single-device user (previous per-device consumption failed
        //     here).
        // If the rate limit rejects the event, we skip ALL devices for this
        // envelope (a partial delivery would be worse than none — the user
        // would think their other devices missed the alert).
        var rateKey = new RateLimitKey(userId, item.PrinterId, item.Kind);
        if (!TryConsumeRate(rateKey, settings, DateTime.UtcNow))
        {
            _metrics.SkippedRateLimit.Add(1);
            return;
        }

        foreach (DeviceToken deviceToken in userTokens)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Third gate immediately before send: if the flag flipped since
            // the outer check, drop the rest of this owner's fan-out.
            if (!gate.IsEnabled(OperatorFeature.NativePush))
            {
                _metrics.SkippedFeatureDisabled.Add(1);
                return;
            }

            // Vasquez v6 B1: isolate the entire per-device send + persist
            // step so a downstream persistence throw for one token cannot
            // cost the remaining tokens their delivery attempt.
            //
            // Hicks #1: rethrow ANY OperationCanceledException so an internal
            // linked/timeout cancellation still bubbles out and stops the
            // dispatch — the caller-token guard swallowed those and let the
            // generic Exception isolator continue.
            try
            {
                await SendAndApplyForDeviceAsync(
                    userId,
                    attentionItemId,
                    changeKind,
                    item,
                    deviceToken,
                    settings,
                    tokens,
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

    private async Task SendAndApplyForDeviceAsync(
        Guid userId,
        string attentionItemId,
        AttentionChangeKind changeKind,
        AttentionItemDto item,
        DeviceToken deviceToken,
        NativePushSettings settings,
        IDeviceTokenRepository tokens,
        CancellationToken cancellationToken)
    {
        string dedupeKey = string.Create(
            CultureInfo.InvariantCulture,
            $"{userId:D}|{deviceToken.Id:D}|{attentionItemId}|{changeKind}");
        if (!ShouldEmit(dedupeKey, settings, DateTime.UtcNow))
        {
            _metrics.SkippedDedupe.Add(1);
            return;
        }

        // Rate limit consumption has moved to DispatchForOwnerAsync so it
        // scopes per (userId, printerId, kind) and is charged exactly once
        // per envelope regardless of how many devices this user has. See
        // Hicks H2-v5-final.
        NativePushEnvelope envelope = BuildEnvelope(item, changeKind, deviceToken);
        _metrics.Attempted.Add(1);

        NativePushDispatchResult result;
        try
        {
            result = await SendWithRetriesAsync(envelope, settings, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Hicks #1: any OCE — caller, linked, or internal timeout —
            // stops the pipeline. Never re-shape cancellation into a
            // "sender_exception" transient result.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[NativePush] Sender threw for deviceTokenId={DeviceTokenId}.", deviceToken.Id);
            result = NativePushDispatchResult.Transient("sender_exception");
        }

        // Vasquez v6 B1: persistence of the send outcome must not be able to
        // abort the outer fan-out. A transient DB error while recording
        // success/failure for one token is a per-token concern, not a
        // pipeline-wide one, so we scope it here with a cancellation-
        // preserving catch. Hicks #1: any OCE propagates unconditionally.
        try
        {
            await ApplyResultAsync(tokens, deviceToken, result, settings, cancellationToken);
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
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // No unmanaged resources; kept for future rate-limit timer.
    }

    private async Task<NativePushDispatchResult> SendWithRetriesAsync(
        NativePushEnvelope envelope,
        NativePushSettings settings,
        CancellationToken cancellationToken)
    {
        int attempts = Math.Max(1, settings.MaxAttempts);
        NativePushDispatchResult last = NativePushDispatchResult.Transient("no_attempt");
        for (int i = 0; i < attempts; i++)
        {
            last = await _sender.SendAsync(envelope, cancellationToken);
            if (last.Success || last.TokenInvalidated || !last.IsTransient)
            {
                return last;
            }

            if (i + 1 < attempts)
            {
                // Small linear backoff — the outbound HttpClient enforces the hard timeout.
                await Task.Delay(TimeSpan.FromMilliseconds(200 * (i + 1)), cancellationToken);
            }
        }

        return last;
    }

    private async Task ApplyResultAsync(
        IDeviceTokenRepository tokens,
        DeviceToken deviceToken,
        NativePushDispatchResult result,
        NativePushSettings settings,
        CancellationToken cancellationToken)
    {
        DateTime nowUtc = DateTime.UtcNow;
        if (result.Success)
        {
            _metrics.Delivered.Add(1, new KeyValuePair<string, object?>("mode", _sender.ModeName));
            await tokens.RecordSuccessAsync(deviceToken.Id, nowUtc, cancellationToken);
            return;
        }

        if (result.TokenInvalidated)
        {
            _metrics.TokensInvalidated.Add(1);
            _ = await tokens.InvalidateByTokenAsync(deviceToken.Token, cancellationToken);
            return;
        }

        // NotConfigured is a config-typo skip, not a device fault. Log-and-drop with
        // NO failure counter mutation, so a misconfigured mode cannot deactivate the
        // entire token fleet on the first outage.
        if (string.Equals(result.Reason, "notConfigured", StringComparison.Ordinal))
        {
            _metrics.SkippedNotConfigured.Add(1);
            return;
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
            return;
        }

        // Terminal (non-transient, non-invalidating, non-config): reason is
        // token-specific (e.g., APNs "PayloadTooLarge", "TopicDisallowed") or a
        // relay 4xx that isn't a rate limit. Count against this token only.
        _metrics.TerminalFailed.Add(
            1,
            new KeyValuePair<string, object?>("mode", _sender.ModeName),
            new KeyValuePair<string, object?>("reason", result.Reason ?? "unknown"));

        // Hicks H5-v5-final: config / payload-shape / topic-mismatch errors
        // are attributable to the deployment or per-envelope builder, NOT to
        // the device token. Ticking the failure counter for these would
        // deactivate every active token in five outages (e.g., wrong .p8,
        // wrong bundle id) — correcting the config would then require every
        // client to re-register before delivery resumes. Bail out with the
        // metric already recorded so operators see the terminal error surface
        // in dashboards without a token-fleet wipe.
        if (IsNotTokenAttributable(result.Reason))
        {
            return;
        }

        await tokens.RecordFailureAsync(deviceToken.Id, nowUtc, settings.FailureDeactivationThreshold, cancellationToken);
    }

    /// <summary>
    /// Reasons that indicate a deployment or per-envelope defect, not a bad
    /// device token. See Hicks H5-v5-final. Kept as an allow-list so any new
    /// terminal reason emitted by a sender defaults to the safe token-fault
    /// behavior; add here only after confirming the reason is genuinely
    /// deployment/payload-scoped.
    /// </summary>
    private static bool IsNotTokenAttributable(string? reason)
    {
        if (string.IsNullOrEmpty(reason))
        {
            return false;
        }

        return reason switch
        {
            // DirectApnsNativePushSender: JWT signing failed (bad .p8, wrong
            // KeyId/TeamId, malformed key material). Fully deployment-scoped.
            "jwt_sign_failed" => true,

            // APNs / relay: bundle id or apns-topic does not match the
            // registered app id. Deployment misconfiguration; the token is
            // valid for its actual topic.
            "TopicDisallowed" => true,
            "BadTopic" => true,

            // APNs: envelope encoding builder produced an oversized payload.
            // The token is fine; fix the payload constructor.
            "PayloadTooLarge" => true,
            "PayloadEmpty" => true,

            // APNs / relay: envelope failed structural validation. Same
            // rationale — sender-side defect, not the recipient.
            "BadMessageId" => true,
            "BadExpirationDate" => true,
            "BadPriority" => true,
            "BadCollapseId" => true,
            _ => false,
        };
    }

    private static async Task<bool> IsFarmAdminAsync(AppDbContext db, Guid userId, CancellationToken cancellationToken)
    {
        return await db.Users
            .AsNoTracking()
            .AnyAsync(u => u.Id == userId && u.UserRoles.Any(ur => ur.Role.Name == "farm_admin" && ur.IsActive), cancellationToken);
    }

    private static NativePushEnvelope BuildEnvelope(AttentionItemDto item, AttentionChangeKind changeKind, DeviceToken deviceToken)
    {
        string? category = AttentionPushCategories.CategoryFor(item.Kind);
        IReadOnlyList<string> actions = AttentionPushCategories.ActionsFor(item.Kind);
        string threadId = AttentionPushCategories.ThreadIdFor(item.Kind, item.PrinterId, item.ToolheadIndex, item.Id);
        string deepLink = AttentionDeepLinks.For(item.Kind, item.PrinterId, item.Id, item.ToolheadIndex, item.JobId);

        NativePushPriority priority = changeKind == AttentionChangeKind.Resolved
            ? NativePushPriority.Background
            : NativePushPriority.Alert;

        DateTime? expiresAt = item.DeadlineAt is DateTime deadline
            ? deadline
            : DateTime.UtcNow.AddMinutes(30);

        return new NativePushEnvelope(
            DeviceTokenId: deviceToken.Id.ToString("D", CultureInfo.InvariantCulture),
            Token: deviceToken.Token,
            Platform: deviceToken.Platform,
            Environment: deviceToken.Environment,
            AppBundleId: deviceToken.AppBundleId,
            Category: category ?? "PRINTER_FAILURE",
            ThreadId: threadId,
            Title: item.PrinterName,
            Subtitle: null,
            Body: item.Title,
            AttentionItemId: item.Id,
            AttentionKind: item.Kind,
            ChangeKind: changeKind,
            PrinterId: item.PrinterId,
            JobId: item.JobId,
            ToolheadIndex: item.ToolheadIndex,
            DeepLink: deepLink,
            Priority: priority,
            ExpiresAtUtc: expiresAt,
            ActionIds: actions);
    }

    private bool ShouldEmit(string key, NativePushSettings settings, DateTime nowUtc)
    {
        DateTime expiresAt = nowUtc.Add(settings.DedupeWindow);

        // AddOrUpdate is the only atomic option on ConcurrentDictionary that lets us
        // both observe the previous value and conditionally emit exactly once under
        // concurrent lookups.
        bool emit = false;
        _ = _dedupe.AddOrUpdate(
            key,
            _ =>
            {
                emit = true;
                return expiresAt;
            },
            (_, existing) =>
            {
                if (existing > nowUtc)
                {
                    emit = false;
                    return existing;
                }

                emit = true;
                return expiresAt;
            });
        return emit;
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
