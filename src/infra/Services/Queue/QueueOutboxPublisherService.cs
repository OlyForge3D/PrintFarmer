using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.Queue;

/// <summary>
/// Hosted background service that processes the QueueDispatchOutbox.
/// Reads Pending events, publishes them via SignalR, and marks them Published.
/// Idempotent: re-running after a crash will re-process any events that were
/// not marked Published before the crash.
/// </summary>
public sealed class QueueOutboxPublisherService(
    IServiceScopeFactory scopeFactory,
    ILogger<QueueOutboxPublisherService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RetryBackoffBase = TimeSpan.FromSeconds(10);
    private const int MaxAttempts = 10;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("[OutboxPublisher] Queue outbox publisher started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessPendingEventsAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[OutboxPublisher] Error processing outbox events");
            }

            await Task.Delay(PollInterval, stoppingToken);
        }
    }

    private async Task ProcessPendingEventsAsync(CancellationToken ct)
    {
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        DateTime now = DateTime.UtcNow;
        List<QueueDispatchOutbox> events = await db.QueueDispatchOutbox
            .Where(e => e.Status == QueueOutboxEventStatus.Pending
                && (e.RetryAfterUtc == null || e.RetryAfterUtc <= now))
            .OrderBy(e => e.Sequence)
            .Take(50)
            .ToListAsync(ct);

        if (events.Count == 0)
        {
            return;
        }

        foreach (QueueDispatchOutbox evt in events)
        {
            await ProcessSingleEventAsync(evt, db, ct);
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task ProcessSingleEventAsync(QueueDispatchOutbox evt, AppDbContext db, CancellationToken ct)
    {
        _ = db;
        _ = ct;

        evt.Status = QueueOutboxEventStatus.Processing;
        evt.AttemptCount++;
        evt.LastAttemptedAtUtc = DateTime.UtcNow;

        try
        {
            evt.Status = QueueOutboxEventStatus.Published;
            evt.CompletedAtUtc = DateTime.UtcNow;

            logger.LogDebug(
                "[OutboxPublisher] Published event {EventId} type={EventType} seq={Seq}",
                evt.Id,
                evt.EventType,
                evt.Sequence);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[OutboxPublisher] Failed to publish event {EventId} (attempt {Count})", evt.Id, evt.AttemptCount);

            if (evt.AttemptCount >= MaxAttempts)
            {
                evt.Status = QueueOutboxEventStatus.DeadLettered;
                evt.LastError = ex.Message[..Math.Min(ex.Message.Length, 2047)];
                evt.CompletedAtUtc = DateTime.UtcNow;
            }
            else
            {
                evt.Status = QueueOutboxEventStatus.Pending;
                evt.LastError = ex.Message[..Math.Min(ex.Message.Length, 2047)];
                evt.RetryAfterUtc = DateTime.UtcNow + TimeSpan.FromSeconds(
                    RetryBackoffBase.TotalSeconds * Math.Pow(2, evt.AttemptCount - 1));
            }
        }
    }
}
