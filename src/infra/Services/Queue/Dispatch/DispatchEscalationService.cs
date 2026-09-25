using System.Text.Json;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Farm.Infrastructure.Services.Queue.Dispatch;

/// <summary>
/// Raises durable, once-only escalation notifications for unresolved indeterminate pre-start
/// claims (issue #2859, refinement D). Each (attempt, policy revision, threshold) produces exactly
/// one <see cref="DispatchEscalationMarker"/> and, for job claims, one outbox event in the same
/// transaction; a unique index deduplicates across instances and restarts. Escalation never
/// releases, cancels, or otherwise mutates the claim.
/// </summary>
public sealed class DispatchEscalationService(
    IServiceScopeFactory scopeFactory,
    IOptions<DispatchEscalationOptions> options,
    TimeProvider timeProvider,
    ILogger<DispatchEscalationService> logger) : BackgroundService
{
    private const int ScanBatchSize = 200;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _ = await ScanOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "dispatch_escalation_scan_failed");
            }

            try
            {
                await Task.Delay(options.Value.ScanInterval, timeProvider, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Scans unresolved claims once and raises every due, absent threshold.</summary>
    /// <returns>The number of markers raised by this scan.</returns>
    public async Task<int> ScanOnceAsync(CancellationToken ct)
    {
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        IDbOutboxSequenceAllocator sequenceAllocator =
            scope.ServiceProvider.GetRequiredService<IDbOutboxSequenceAllocator>();
        DispatchEscalationOptions policy = options.Value;

        List<QueueDispatchAttempt> claims = await (
                from state in db.PrinterDispatchStates.AsNoTracking()
                join attempt in db.QueueDispatchAttempts.AsNoTracking()
                    on state.ActiveDispatchAttemptId equals (Guid?)attempt.Id
                where attempt.Outcome == DispatchAttemptOutcome.Unknown &&
                      attempt.RequiresReconciliation
                orderby attempt.ClaimedAtUtc
                select attempt)
            .Take(ScanBatchSize)
            .ToListAsync(ct);
        if (claims.Count == 0)
        {
            return 0;
        }

        List<Guid> attemptIds = claims.Select(attempt => attempt.Id).ToList();
        List<(Guid AttemptId, string Threshold)> existing = (await db.DispatchEscalationMarkers
                .AsNoTracking()
                .Where(marker =>
                    attemptIds.Contains(marker.DispatchAttemptId) &&
                    marker.PolicyRevision == policy.PolicyRevision)
                .Select(marker => new { marker.DispatchAttemptId, marker.Threshold })
                .ToListAsync(ct))
            .Select(marker => (marker.DispatchAttemptId, marker.Threshold))
            .ToList();
        HashSet<(Guid, string)> raised = [.. existing];

        DateTime now = timeProvider.GetUtcNow().UtcDateTime;
        int count = 0;
        foreach (QueueDispatchAttempt attempt in claims)
        {
            TimeSpan age = now - attempt.ClaimedAtUtc;
            if (age < TimeSpan.Zero)
            {
                age = TimeSpan.Zero;
            }

            foreach (DispatchEscalationLevel level in policy.DueLevels(age))
            {
                string threshold = level.ToString();
                if (raised.Contains((attempt.Id, threshold)))
                {
                    continue;
                }

                if (await TryRaiseAsync(db, sequenceAllocator, policy, attempt, level, age, now, ct))
                {
                    count++;
                }

                _ = raised.Add((attempt.Id, threshold));
            }
        }

        return count;
    }

    private async Task<bool> TryRaiseAsync(
        AppDbContext db,
        IDbOutboxSequenceAllocator sequenceAllocator,
        DispatchEscalationOptions policy,
        QueueDispatchAttempt attempt,
        DispatchEscalationLevel level,
        TimeSpan age,
        DateTime now,
        CancellationToken ct)
    {
        await using QueueOutboxTransactionScope transaction =
            await QueueOutboxTransactionScope.BeginAsync(db, ct);
        var marker = new DispatchEscalationMarker
        {
            Id = Guid.NewGuid(),
            DispatchAttemptId = attempt.Id,
            PrinterId = attempt.PrinterId,
            PrintJobId = attempt.PrintJobId,
            PolicyRevision = policy.PolicyRevision,
            Threshold = level.ToString(),
            ClaimAgeSeconds = (long)age.TotalSeconds,
            RaisedAtUtc = now,
        };
        db.DispatchEscalationMarkers.Add(marker);
        if (attempt.PrintJobId is Guid jobId)
        {
            await DispatchClaimService.AddLifecycleOutboxEventAsync(
                db,
                sequenceAllocator,
                DispatchClaimService.EventTypeDispatchIndeterminateEscalated,
                aggregateId: jobId,
                printerId: attempt.PrinterId,
                attemptId: attempt.Id,
                aggregateRowVersion: null,
                failureCode: "dispatch_indeterminate",
                payloadJson: JsonSerializer.Serialize(
                    new
                    {
                        jobId,
                        printerId = attempt.PrinterId,
                        attemptId = attempt.Id,
                        escalationLevel = marker.Threshold,
                        policyRevision = marker.PolicyRevision,
                        claimAgeSeconds = marker.ClaimAgeSeconds,
                        markerId = marker.Id,
                    },
                    DispatchRecoveryService.JsonOptions),
                ct,
                timeProvider: timeProvider);
        }

        try
        {
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            // Another instance raised the same (attempt, policy revision, threshold) first.
            db.ChangeTracker.Clear();
            logger.LogDebug(ex, "dispatch_escalation_marker_exists attempt={AttemptId} threshold={Threshold}", attempt.Id, marker.Threshold);
            return false;
        }

        db.ChangeTracker.Clear();
        logger.LogWarning(
            "dispatch_indeterminate_escalated printer={PrinterId} attempt={AttemptId} job={JobId} level={Level} policy={PolicyRevision} ageSeconds={AgeSeconds}",
            attempt.PrinterId,
            attempt.Id,
            attempt.PrintJobId,
            marker.Threshold,
            marker.PolicyRevision,
            marker.ClaimAgeSeconds);
        return true;
    }
}
