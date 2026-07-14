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

    // Per-recipient pre-resolution snapshots. A user can consume only a snapshot
    // captured after that same user passed item visibility, role, preference,
    // token, dedupe, and rate gates. Keying by user prevents one owner's visible
    // item (especially admin-only maintenance) from authorizing another owner.
    // Resolved atomically consumes the entry before any send-side checks, making
    // replay safe even when a later opt-in changes.
    private readonly ConcurrentDictionary<AttentionSnapshotKey, AttentionSnapshot> _snapshots = new();

    // One versioned lane per item and delivery audience serializes an active lifecycle
    // transition, coalesces queued transitions to the newest authoritative timestamp,
    // and retains a tombstone so delayed Created work cannot follow Resolved.
    private readonly ConcurrentDictionary<AttentionDispatchKey, AttentionDispatchLane> _attentionDispatchLanes = new();
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

            await DispatchCoreAsync(attentionItemId, changeKind, targetUserId, cancellationToken);
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
        NativePushSettings settings,
        IOperatorFeatureGate gate,
        IDeviceTokenRepository tokens,
        IAttentionService attention,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var snapshotKey = new AttentionSnapshotKey(userId, attentionItemId);
        AttentionItemDto? item = await attention.FindItemAsync(userId, attentionItemId, cancellationToken);
        AttentionSnapshot? resolvedSnapshot = null;

        if (changeKind == AttentionChangeKind.Resolved)
        {
            // A dismissal is authorized only by this recipient's pre-resolution
            // snapshot, even when the source has not removed the live row yet.
            // This prevents a newly authorized owner from receiving a dismissal
            // for an alert that was never delivered to that owner.
            if (!_snapshots.TryRemove(snapshotKey, out resolvedSnapshot))
            {
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

        IReadOnlyList<DeviceToken> userTokens = await tokens.GetActiveByUserAsync(userId, cancellationToken);
        if (userTokens.Count == 0)
        {
            return;
        }

        string dedupeKey = string.Create(
            CultureInfo.InvariantCulture,
            $"{userId:D}|{attentionItemId}|{changeKind}");
        if (!ShouldEmit(dedupeKey, settings, UtcNow))
        {
            _metrics.SkippedDedupe.Add(1);
            return;
        }

        // Charge once per logical alert, never once per device. Silent resolved
        // dismissals are control messages and bypass the alert budget; otherwise
        // a just-delivered alert would consume the only slot and prevent its own
        // timely dismissal. Dedupe still makes the dismissal exactly-once.
        if (changeKind != AttentionChangeKind.Resolved)
        {
            var rateKey = new RateLimitKey(userId, printerId, kind);
            if (!TryConsumeRate(rateKey, settings, UtcNow))
            {
                _metrics.SkippedRateLimit.Add(1);
                return;
            }

            // Capture only the minimal routing shape, and only for this owner
            // after every authorization/preference guard above has passed.
            _snapshots[snapshotKey] = new AttentionSnapshot(
                Kind: item!.Kind,
                PrinterId: item.PrinterId,
                JobId: item.JobId,
                ToolheadIndex: item.ToolheadIndex,
                CapturedAtUtc: UtcNow);
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
                await SendAndApplyForDeviceAsync(
                    userId,
                    attentionItemId,
                    changeKind,
                    item,
                    resolvedSnapshot,
                    deviceToken,
                    settings,
                    gate,
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
        AttentionItemDto? item,
        AttentionSnapshot? resolvedSnapshot,
        DeviceToken deviceToken,
        NativePushSettings settings,
        IOperatorFeatureGate gate,
        CancellationToken cancellationToken)
    {
        // Rate limit consumption has moved to DispatchForOwnerAsync so it
        // runs after logical-event dedupe and is charged exactly once per
        // envelope regardless of how many devices this user has.
        NativePushEnvelope envelope = item is not null
            ? BuildEnvelope(item, changeKind, deviceToken)
            : BuildSilentEnvelopeFromSnapshot(attentionItemId, resolvedSnapshot!, deviceToken);
        NativePushDispatchResult? result;
        try
        {
            result = await SendWithRetriesAsync(envelope, settings, gate, cancellationToken);
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
            _logger.LogWarning(ex, "[NativePush] Sender threw for deviceTokenId={DeviceTokenId}.", deviceToken.Id);
            result = NativePushDispatchResult.Transient("sender_exception");
        }

        if (result is null)
        {
            return;
        }

        // Every mutating outcome opens its own DI scope/AppDbContext. A failed
        // SaveChanges can leave an EF tracker poisoned; isolating that tracker
        // prevents token A's persistence race from contaminating token B or a
        // later owner. Non-cancellation failures remain per-device concerns.
        try
        {
            await ApplyResultAsync(deviceToken, result, settings, cancellationToken);
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

    /// <inheritdoc />
    public void Dispose()
    {
        // No unmanaged resources; kept for future rate-limit timer.
    }

    private async Task<NativePushDispatchResult?> SendWithRetriesAsync(
        NativePushEnvelope envelope,
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

            try
            {
                last = await _sender.SendAsync(envelope, cancellationToken);
            }
            finally
            {
                if (!attempted)
                {
                    _metrics.Attempted.Add(1);
                    attempted = true;
                }
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

    private async Task ApplyResultAsync(
        DeviceToken deviceToken,
        NativePushDispatchResult result,
        NativePushSettings settings,
        CancellationToken cancellationToken)
    {
        DateTime nowUtc = UtcNow;
        if (result.Success)
        {
            _metrics.Delivered.Add(1, new KeyValuePair<string, object?>("mode", _sender.ModeName));
            await PersistOutcomeInFreshScopeAsync(
                (tokens, ct) => tokens.RecordSuccessAsync(
                    deviceToken.Id,
                    deviceToken.RegistrationVersion,
                    nowUtc,
                    ct),
                cancellationToken);
            return;
        }

        if (result.TokenInvalidated)
        {
            _metrics.TokensInvalidated.Add(1);
            await PersistOutcomeInFreshScopeAsync(
                async (tokens, ct) =>
                {
                    _ = await tokens.InvalidateAsync(
                        deviceToken.Id,
                        deviceToken.RegistrationVersion,
                        ct);
                },
                cancellationToken);
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
            return;
        }

        await PersistOutcomeInFreshScopeAsync(
            (tokens, ct) => tokens.RecordFailureAsync(
                deviceToken.Id,
                deviceToken.RegistrationVersion,
                nowUtc,
                settings.FailureDeactivationThreshold,
                ct),
            cancellationToken);
    }

    private async Task PersistOutcomeInFreshScopeAsync(
        Func<IDeviceTokenRepository, CancellationToken, Task> persist,
        CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
        IDeviceTokenRepository tokens = scope.ServiceProvider.GetRequiredService<IDeviceTokenRepository>();
        await persist(tokens, cancellationToken);
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

                if (_hasVersion && version.CompareTo(_latest) <= 0)
                {
                    return AttentionDispatchObserveResult.Stale;
                }

                _latest = version;
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

    private sealed record AttentionSnapshot(
        AttentionKind Kind,
        Guid PrinterId,
        Guid? JobId,
        int? ToolheadIndex,
        DateTime CapturedAtUtc);

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

        // Keep snapshots long enough for real attention lifetimes while bounding
        // entries whose source never emits Resolved. Value-comparing removal
        // preserves a concurrently refreshed occurrence.
        DateTime snapshotCutoff = nowUtc - AttentionSnapshotTtl;
        foreach (KeyValuePair<AttentionSnapshotKey, AttentionSnapshot> kv in _snapshots)
        {
            if (kv.Value.CapturedAtUtc < snapshotCutoff)
            {
                _ = ((ICollection<KeyValuePair<AttentionSnapshotKey, AttentionSnapshot>>)_snapshots).Remove(kv);
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
