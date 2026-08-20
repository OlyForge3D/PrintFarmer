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
    ILogger<QueueOutboxPublisherService> logger,
    IQueueSubscriptionMembershipNotifier? membershipNotifier = null) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RetryBackoffBase = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StaleLeaseAge = TimeSpan.FromMinutes(10);
    private const int MaxAttempts = 10;

    // #1731 follow-up (Bishop's PR #1741 review): "subscription resources" isn't only
    // printer/group/role authorization -- GetSubscriptionResourcesAsync also snapshots the
    // caller's CURRENT active jobIds/projectIds (PrintJobStatus Queued/Assigned/Starting/
    // Printing/Paused). An ordinary queue event can add or remove a job from that active
    // set without any authorization change, e.g. a brand-new queued job, or a job's terminal
    // completion/failure/cancellation. The client's "queueevent" SignalR groups are
    // job/printer/project-scoped, so a client not yet subscribed to a genuinely new job or
    // project would silently miss its events until some unrelated reconnect/membership
    // change happened -- that's a correctness regression, not just a latency tradeoff.
    // These are exactly the event types where a job enters or leaves that active set;
    // everything else (dispatch progress, pause/resume, bed-clear, physical actuation,
    // reconciliation-absent-returns-to-Assigned) is a transition WITHIN the active set and
    // does not change subscription-resources membership.
    private static readonly HashSet<string> MembershipChangingEventTypes = new(StringComparer.Ordinal)
    {
        QueueLifecycleEventWriter.EventTypeJobQueued,
        QueueLifecycleEventWriter.EventTypeCalibrationJobQueued,
        QueueLifecycleEventWriter.EventTypeJobCompleted,
        QueueLifecycleEventWriter.EventTypeJobFailed,
        QueueLifecycleEventWriter.EventTypeJobCancelled,
        QueueLifecycleEventWriter.EventTypeJobOrphanSynced,

        // NOTE: EventTypeJobAborted is deliberately excluded -- "abort" returns the job to
        // PrintJobStatus.Queued (see DispatchClaimService's abort handling), which is still
        // within the active set, so it is NOT a membership change.
        //
        // NOTE: EventTypeJobCopyCompleted is deliberately excluded -- a multi-copy job that
        // has not finished all copies also returns to PrintJobStatus.Queued (requeued for the
        // next copy), same as an abort. Only EventTypeJobCompleted (all copies done, job truly
        // exits the active set) is membership-changing; see PrintJobCompletionService and
        // PR #1741 review (Bishop).
    };

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

    internal async Task ProcessSingleEventAsync(
        QueueDispatchOutbox evt,
        CancellationToken ct)
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
                calibrationAttemptId: evt.CalibrationAttemptId,
                jobStatus: evt.JobStatus,
                jobKind: evt.JobKind,
                jobRevision: evt.AggregateRowVersion,
                dispatchStateRevision: evt.DispatchStateRowVersion,
                attemptId: evt.AttemptId,
                bedClearState: evt.BedClearState,
                failureCode: evt.FailureCode,
                payloadJson: evt.PayloadJson,
                jobLogicalRevision: evt.JobRevision,
                dispatchStateLogicalRevision: evt.DispatchStateRevision,
                attemptNumber: evt.AttemptNumber,
                attemptOutcome: evt.AttemptOutcome,
                bedClearCommandId: evt.BedClearCommandId,
                bedClearExpiresAtUtc: evt.BedClearExpiresAtUtc,
                failureRetryable: evt.FailureRetryable,
                failureRequiresReconciliation: evt.FailureRequiresReconciliation,
                schemaVersion: evt.SchemaVersion);

            List<Task> sends = new();

            // #1731: the "queueresourceschanged" discovery hint used to be sent here,
            // unconditionally, for every outbox event. Most event types flowing through this
            // outbox are dispatch-progress/bed-clear lifecycle events that cannot change
            // subscription-resources membership, so broadcasting the hint for every one of
            // them made every queue event trigger a client-side subscription reconciliation
            // (2 REST calls + resubscribe) for no reason.
            //
            // Authorization-driven membership changes (printer create/delete/reassignment,
            // printer-group membership, user role changes) are not outbox events at all --
            // those mutation points call IQueueSubscriptionMembershipNotifier directly (see
            // PrinterGroupService/PrintersService/UsersService/RoleManagementService), which
            // is both more precise and lower latency than waiting for this poller.
            //
            // #1731 PR #1741 review (Bishop): GetSubscriptionResourcesAsync's snapshot also
            // includes the caller's CURRENT active jobIds/projectIds (PrintJobStatus Queued/
            // Assigned/Starting/Printing/Paused), and ordinary queue lifecycle events CAN
            // change that without any authorization change -- a brand-new queued job, or a
            // job leaving the active set on completion/failure/cancellation. Those ARE outbox
            // events, so MembershipChangingEventTypes (above) narrowly re-fires the same hint
            // for exactly those transitions, below.
            if (membershipNotifier is not null &&
                MembershipChangingEventTypes.Contains(evt.EventType))
            {
                sends.Add(membershipNotifier.NotifyMembershipChangedAsync(ct));
            }

            // Job-scoped group: narrower delivery for clients watching this specific job.
            if (string.Equals(evt.AggregateType, nameof(PrintJob), StringComparison.Ordinal))
            {
                sends.Add(
                    hub.Clients.Group(AuthorizedHubGroups.QueueJob(evt.AggregateId))
                        .SendAsync("queueevent", envelope, ct));
            }

            // Printer-scoped group (when the event is associated with a printer).
            if (evt.PrinterId.HasValue)
            {
                sends.Add(
                    hub.Clients.Group(AuthorizedHubGroups.Printer(evt.PrinterId.Value))
                        .SendAsync("queueevent", envelope.RedactForPrinter(), ct));
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
