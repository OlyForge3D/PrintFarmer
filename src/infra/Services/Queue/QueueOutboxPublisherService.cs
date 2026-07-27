using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Security;
using Farm.Infrastructure.Services.SignalR;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.Queue;

/// <summary>
/// Hosted background service that processes the QueueDispatchOutbox for SignalR hint delivery.
/// Reads <see cref="QueueOutboxEventStatus.Pending"/> events, publishes them via SignalR
/// to authorized groups, and marks them <see cref="QueueOutboxEventStatus.Published"/>.
/// Idempotent: re-running after a crash will re-process any events not yet Published.
///
/// <strong>Responsibility boundary:</strong> this service publishes SignalR hints ONLY.
/// <c>BackendStartCommand.v1</c> events are owned end-to-end by
/// <see cref="BackendStartCommandConsumerService"/> and are SKIPPED here.
/// </summary>
public sealed class QueueOutboxPublisherService(
    IServiceScopeFactory scopeFactory,
    IHubContext<PrinterHub> hub,
    ILogger<QueueOutboxPublisherService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RetryBackoffBase = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StaleLeaseAge = TimeSpan.FromMinutes(10);
    private const int MaxAttempts = 10;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("[OutboxPublisher] Queue outbox publisher (SignalR hints) started");

        await RecoverStaleLeasesAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RecoverStaleLeasesAsync(stoppingToken);
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

    internal async Task RecoverStaleLeasesAsync(CancellationToken ct)
    {
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        DateTime staleCutoff = DateTime.UtcNow - StaleLeaseAge;

        List<QueueDispatchOutbox> stale = await db.QueueDispatchOutbox
            .Where(evt =>
                evt.EventType != BedClearAcknowledgementService.BackendStartCommandEventType &&
                evt.EventType != BackendControlCommandConsumerService.EventType &&
                evt.Status == QueueOutboxEventStatus.Processing &&
                evt.LastAttemptedAtUtc < staleCutoff)
            .ToListAsync(ct);

        foreach (QueueDispatchOutbox evt in stale)
        {
            evt.Status = QueueOutboxEventStatus.Pending;
            evt.LastError = "Recovered after the publisher lease expired.";
            evt.RetryAfterUtc = DateTime.UtcNow;
        }

        if (stale.Count > 0)
        {
            await db.SaveChangesAsync(ct);
            logger.LogWarning(
                "[OutboxPublisher] Recovered {Count} stale publisher lease(s)",
                stale.Count);
        }
    }

    private async Task ProcessPendingEventsAsync(CancellationToken ct)
    {
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        DateTime now = DateTime.UtcNow;
        List<QueueDispatchOutbox> events = await db.QueueDispatchOutbox
            .Where(e =>
                e.Status == QueueOutboxEventStatus.Pending &&
                e.EventType != BedClearAcknowledgementService.BackendStartCommandEventType &&
                e.EventType != BackendControlCommandConsumerService.EventType &&
                (e.RetryAfterUtc == null || e.RetryAfterUtc <= now))
            .OrderBy(e => e.Sequence)
            .Take(50)
            .ToListAsync(ct);

        if (events.Count == 0)
        {
            return;
        }

        // =====================================================================
        // Atomic lease: mark every candidate row Processing and COMMIT before any
        // network I/O. The RowVersion concurrency token means a second publisher
        // racing on the same rows loses the save and re-reads, so no event can be
        // delivered twice by concurrent publishers.
        // =====================================================================
        foreach (QueueDispatchOutbox evt in events)
        {
            evt.Status = QueueOutboxEventStatus.Processing;
            evt.AttemptCount++;
            evt.LastAttemptedAtUtc = now;
        }

        try
        {
            _ = await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another publisher instance leased at least one of these rows first.
            // Abandon the whole batch — the winner owns delivery; we retry next poll.
            logger.LogDebug(
                "[OutboxPublisher] Lost lease race on a batch of {Count} event(s) — another publisher owns them",
                events.Count);
            return;
        }

        foreach (QueueDispatchOutbox evt in events)
        {
            await ProcessSingleEventAsync(evt, ct);
        }

        _ = await db.SaveChangesAsync(ct);
    }

    private async Task ProcessSingleEventAsync(QueueDispatchOutbox evt, CancellationToken ct)
    {
        try
        {
            // Build an authenticated versioned envelope and publish to authorized groups only.
            // Never use Clients.All — events are scoped to job, printer, and farm groups.
            // Identity/time/sequence come from the PERSISTED row so a redelivery is identical
            // and clients can de-duplicate on (eventId, sequence).
            QueueEventEnvelope envelope = QueueEventEnvelope.FromOutbox(
                eventId: evt.Id,
                sequence: evt.Sequence,
                occurredAtUtc: evt.CreatedAtUtc,
                eventType: evt.EventType,
                jobId: evt.AggregateId,
                printerId: evt.PrinterId,
                projectId: evt.ProjectId,
                jobStatus: evt.JobStatus,
                jobKind: evt.JobKind,
                jobRevision: evt.AggregateRowVersion,
                dispatchStateRevision: evt.DispatchStateRowVersion,
                attemptId: evt.AttemptId,
                bedClearState: evt.BedClearState,
                failureCode: evt.FailureCode,
                payloadJson: evt.PayloadJson,
                jobLogicalRevision: evt.JobRevision,
                dispatchStateLogicalRevision: evt.DispatchStateRevision);

            List<Task> sends = new();

            // Job-scoped group: narrower delivery for clients watching this specific job.
            sends.Add(hub.Clients.Group(AuthorizedHubGroups.QueueJob(evt.AggregateId)).SendAsync("queueevent", envelope, ct));

            // Printer-scoped group (when the event is associated with a printer).
            if (evt.PrinterId.HasValue)
            {
                sends.Add(hub.Clients.Group(AuthorizedHubGroups.Printer(evt.PrinterId.Value)).SendAsync("queueevent", envelope, ct));
            }

            if (evt.ProjectId.HasValue)
            {
                sends.Add(hub.Clients.Group(AuthorizedHubGroups.Project(evt.ProjectId.Value)).SendAsync("queueevent", envelope, ct));
            }

            await Task.WhenAll(sends);

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

        await Task.CompletedTask;
    }
}
