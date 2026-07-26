using System.Text.Json;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Security;
using Farm.Infrastructure.Services.Interfaces;
using Farm.Infrastructure.Services.SignalR;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.Queue;

/// <summary>
/// Hosted background service that processes the QueueDispatchOutbox.
/// Reads Pending events, publishes them via SignalR to authorized groups,
/// and marks them Published. Idempotent: re-running after a crash will
/// re-process any events that were not marked Published before the crash.
///
/// <strong>BackendStartCommand handling:</strong> events with EventType
/// <c>PrintFarmer.Queue.BackendStartCommand.v1</c> are routed to
/// <see cref="IPrintJobManagementService.DispatchJobWithAckAsync"/> which acquires
/// the shared dispatch claim (validating the persisted bed-clear ack) and drives
/// the backend upload/start. The outbox event is marked Published once the command
/// is accepted for execution; actual success/failure is tracked via
/// <see cref="QueueDispatchAttempt"/>.
/// </summary>
public sealed class QueueOutboxPublisherService(
    IServiceScopeFactory scopeFactory,
    IHubContext<PrinterHub> hub,
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
            await ProcessSingleEventAsync(evt, ct);
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task ProcessSingleEventAsync(QueueDispatchOutbox evt, CancellationToken ct)
    {
        evt.Status = QueueOutboxEventStatus.Processing;
        evt.AttemptCount++;
        evt.LastAttemptedAtUtc = DateTime.UtcNow;

        try
        {
            // Handle durable backend-start commands separately: route through the shared
            // IDispatchClaimService to acquire the atomic claim, then drive the backend upload.
            if (evt.EventType == BedClearAcknowledgementService.BackendStartCommandEventType)
            {
                await HandleBackendStartCommandAsync(evt, ct);
                return;
            }

            // Build an authenticated versioned envelope and publish to authorized groups only.
            // Never use Clients.All — events are scoped to job, printer, and farm groups.
            var envelope = QueueEventEnvelope.Create(
                eventType: evt.EventType,
                jobId: evt.AggregateId,
                printerId: evt.PrinterId,
                payloadJson: evt.PayloadJson);

            List<Task> sends = new();

            // Farm-wide group: all authenticated farm users receive queue lifecycle events.
            sends.Add(hub.Clients.Group(AuthorizedHubGroups.Farm).SendAsync("queueevent", envelope, ct));

            // Job-scoped group: narrower delivery for clients watching this specific job.
            sends.Add(hub.Clients.Group(AuthorizedHubGroups.QueueJob(evt.AggregateId)).SendAsync("queueevent", envelope, ct));

            // Printer-scoped group (when the event is associated with a printer).
            if (evt.PrinterId.HasValue)
            {
                sends.Add(hub.Clients.Group(AuthorizedHubGroups.Printer(evt.PrinterId.Value)).SendAsync("queueevent", envelope, ct));
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
    }

    /// <summary>
    /// Handles a <c>BackendStartCommand.v1</c> outbox event by firing the shared dispatch
    /// claim and backend upload path. The command is marked Published immediately (before the
    /// background task completes) so the outbox publisher is not blocked during file upload.
    /// The actual claim outcome is tracked in <see cref="QueueDispatchAttempt"/>.
    /// </summary>
    private async Task HandleBackendStartCommandAsync(QueueDispatchOutbox evt, CancellationToken ct)
    {
        BackendStartPayload? payload = null;
        try
        {
            payload = JsonSerializer.Deserialize<BackendStartPayload>(evt.PayloadJson);
        }
        catch (JsonException jsonEx)
        {
            logger.LogError(
                jsonEx,
                "[OutboxPublisher] Cannot deserialize BackendStartCommand payload for event {EventId}",
                evt.Id);
            evt.Status = QueueOutboxEventStatus.DeadLettered;
            evt.LastError = $"Invalid payload JSON: {jsonEx.Message}"[..Math.Min(jsonEx.Message.Length + 24, 2047)];
            evt.CompletedAtUtc = DateTime.UtcNow;
            return;
        }

        if (payload is null || payload.JobId == Guid.Empty || string.IsNullOrWhiteSpace(payload.AcknowledgementKey))
        {
            evt.Status = QueueOutboxEventStatus.DeadLettered;
            evt.LastError = "BackendStartCommand payload is missing required fields (jobId, acknowledgementKey).";
            evt.CompletedAtUtc = DateTime.UtcNow;
            logger.LogError(
                "[OutboxPublisher] BackendStartCommand event {EventId} has incomplete payload — dead-lettered.",
                evt.Id);
            return;
        }

        // Mark as Published before firing the background task so the publisher loop
        // is not blocked for the duration of the file upload (which can take minutes).
        evt.Status = QueueOutboxEventStatus.Published;
        evt.CompletedAtUtc = DateTime.UtcNow;

        string jobId = payload.JobId.ToString();
        string actorSubject = payload.ActorSubject ?? "system";
        string ackKey = payload.AcknowledgementKey;
        Guid commandEventId = evt.Id;

        // Fire and manage the backend start in a background Task.
        // The scopeFactory creates an independent DI scope for the long-running operation.
        _ = Task.Run(
            async () =>
            {
                try
                {
                    await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
                    IPrintJobManagementService mgmt =
                        scope.ServiceProvider.GetRequiredService<IPrintJobManagementService>();

                    logger.LogInformation(
                        "[OutboxPublisher] Executing backend start for Job={JobId} Actor={Actor} CommandEvent={EventId}",
                        jobId,
                        actorSubject,
                        commandEventId);

                    await mgmt.DispatchJobWithAckAsync(jobId, actorSubject, ackKey, ct);

                    logger.LogInformation(
                        "[OutboxPublisher] Backend start completed for Job={JobId} CommandEvent={EventId}",
                        jobId,
                        commandEventId);
                }
                catch (Exception ex)
                {
                    // Errors are tracked in QueueDispatchAttempt by DispatchClaimService.
                    // Log here for observability only — the attempt record has the typed error.
                    logger.LogError(
                        ex,
                        "[OutboxPublisher] Backend start failed for Job={JobId} CommandEvent={EventId}",
                        jobId,
                        commandEventId);
                }
            },
            CancellationToken.None);
    }

    /// <summary>Payload shape for <c>BackendStartCommand.v1</c> outbox events.</summary>
    private sealed record BackendStartPayload(
        Guid JobId,
        Guid PrinterId,
        string? ActorSubject,
        string AcknowledgementKey);
}
