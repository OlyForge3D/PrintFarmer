using System.Data.Common;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Farm.Infrastructure.Services.Queue;

/// <summary>
/// Background service that prunes <see cref="Domain.QueueDispatchOutbox"/>,
/// <see cref="Domain.QueueDispatchAttempt"/>, and <see cref="Domain.QueueOperationAudit"/>
/// rows past their independently configured retention windows
/// (<see cref="QueueRetentionSettings"/>).
///
/// <para>
/// Modeled on <see cref="Electricity.PowerReadingPruneService"/> but with three
/// improvements required by review of issue #1728:
/// </para>
/// <list type="bullet">
/// <item>Separate configurable retention windows per table — operation audits are a
/// compliance/forensic record and must not silently inherit the outbox window.</item>
/// <item>Bounded, batched deletes: each table is pruned in chunks of at most
/// <see cref="QueueRetentionSettings.DeleteBatchSize"/> rows, capped at
/// <see cref="QueueRetentionSettings.MaxDeletesPerTablePerPass"/> rows per tick, so a
/// first prune pass against an already-large table cannot hold a long-running lock or
/// block the connection pool.</item>
/// <item>A testable <see cref="RunOnceAsync"/> entry point (pattern from
/// <see cref="Idempotency.IdempotencyRecordCleanupService"/>), and only
/// <see cref="DbException"/> is swallowed so genuine programmer errors still surface.</item>
/// </list>
/// </summary>
public sealed class QueueRetentionPruneService(
    IServiceScopeFactory scopeFactory,
    IOptions<QueueRetentionSettings> options,
    ILogger<QueueRetentionPruneService> logger) : BackgroundService
{
    private readonly QueueRetentionSettings _settings = options.Value;

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await RunOnceAsync(stoppingToken);

            try
            {
                await Task.Delay(_settings.PruneInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Executes a single prune pass across all three tables under a fresh scope.
    /// Exposed for tests so they do not need to wait on the timer.
    /// </summary>
    internal async Task RunOnceAsync(CancellationToken ct)
    {
        try
        {
            await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            DateTime now = DateTime.UtcNow;

            int outboxDeleted = await PruneOutboxAsync(db, now, ct);
            int attemptsDeleted = await PruneDispatchAttemptsAsync(db, now, ct);
            int auditsDeleted = await PruneOperationAuditsAsync(db, now, ct);

            if (outboxDeleted > 0 || attemptsDeleted > 0 || auditsDeleted > 0)
            {
                logger.LogInformation(
                    "QueueRetentionPruneService: deleted {Outbox} outbox, {Attempts} dispatch " +
                    "attempts, {Audits} operation audits.",
                    outboxDeleted,
                    attemptsDeleted,
                    auditsDeleted);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Graceful shutdown; suppress.
        }
        catch (DbException ex)
        {
            // Transient database failures must not tear down the host — log and retry
            // on the next tick. Non-DbException failures deliberately propagate.
            logger.LogError(ex, "QueueRetentionPruneService: prune sweep failed; will retry on the next tick.");
        }
    }

    private Task<int> PruneOutboxAsync(AppDbContext db, DateTime now, CancellationToken ct)
    {
        DateTime cutoff = now.AddDays(-_settings.OutboxRetentionDays);
        return PruneBatchedAsync<Guid>(
            take => db.QueueDispatchOutbox
                .Where(e =>
                    (e.Status == Domain.QueueOutboxEventStatus.Published ||
                     e.Status == Domain.QueueOutboxEventStatus.DeadLettered) &&
                    e.CompletedAtUtc != null &&
                    e.CompletedAtUtc < cutoff)
                .OrderBy(e => e.Id)
                .Select(e => e.Id)
                .Take(take)
                .ToListAsync(ct),
            ids => db.QueueDispatchOutbox.Where(e => ids.Contains(e.Id)).ExecuteDeleteAsync(ct),
            ct);
    }

    private Task<int> PruneDispatchAttemptsAsync(AppDbContext db, DateTime now, CancellationToken ct)
    {
        DateTime cutoff = now.AddDays(-_settings.DispatchAttemptRetentionDays);
        return PruneBatchedAsync<Guid>(
            take => db.QueueDispatchAttempts
                .Where(a =>
                    !a.RequiresReconciliation &&
                    a.TerminalAtUtc != null &&
                    a.TerminalAtUtc < cutoff)
                .OrderBy(a => a.Id)
                .Select(a => a.Id)
                .Take(take)
                .ToListAsync(ct),
            ids => db.QueueDispatchAttempts.Where(a => ids.Contains(a.Id)).ExecuteDeleteAsync(ct),
            ct);
    }

    private Task<int> PruneOperationAuditsAsync(AppDbContext db, DateTime now, CancellationToken ct)
    {
        DateTime cutoff = now.AddDays(-_settings.OperationAuditRetentionDays);
        return PruneBatchedAsync<Guid>(
            take => db.QueueOperationAudits
                .Where(a => a.OccurredAtUtc < cutoff)
                .OrderBy(a => a.Id)
                .Select(a => a.Id)
                .Take(take)
                .ToListAsync(ct),
            ids => db.QueueOperationAudits.Where(a => ids.Contains(a.Id)).ExecuteDeleteAsync(ct),
            ct);
    }

    /// <summary>
    /// Deletes rows in bounded chunks of <see cref="QueueRetentionSettings.DeleteBatchSize"/>,
    /// stopping once a batch returns fewer candidate keys than requested (backlog drained)
    /// or the per-pass cap <see cref="QueueRetentionSettings.MaxDeletesPerTablePerPass"/> is
    /// reached (bounds one tick's total work and lock hold time).
    /// </summary>
    private async Task<int> PruneBatchedAsync<TKey>(
        Func<int, Task<List<TKey>>> fetchCandidateIds,
        Func<List<TKey>, Task<int>> deleteByIds,
        CancellationToken ct)
        where TKey : notnull
    {
        int totalDeleted = 0;
        while (totalDeleted < _settings.MaxDeletesPerTablePerPass)
        {
            int remaining = _settings.MaxDeletesPerTablePerPass - totalDeleted;
            int take = Math.Min(_settings.DeleteBatchSize, remaining);

            List<TKey> batch = await fetchCandidateIds(take);
            if (batch.Count == 0)
            {
                break;
            }

            int deleted = await deleteByIds(batch);
            totalDeleted += deleted;

            if (batch.Count < take)
            {
                // Backlog exhausted for this table on this pass.
                break;
            }

            ct.ThrowIfCancellationRequested();
        }

        return totalDeleted;
    }
}
